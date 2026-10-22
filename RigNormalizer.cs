using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Which joint frames a clip's rotation keys were authored against - decides how Normalize
    // converts it. Native: the target's own original frames (a clip the rig shipped with).
    // Reference: the reference rig's frames (a clip copied over raw, with Fix Bone Length).
    // ReferenceHipFixed: the same, but with the merge's Fix Hip Rotation also applied, which
    // leaves Hips in the target's original frame and its children in the reference's.
    public enum ClipConvention { Native, Reference, ReferenceHipFixed }

    // Rebakes a rig's joint frames so every joint shared (by name) with a reference rig has
    // exactly the reference's rest WORLD rotation, keeping every joint's world POSITION, so a
    // clip authored on the reference copies over with its local rotations untouched.
    //
    // Why: two rigs with matching joint names and the same T-pose can still disagree wildly on
    // each joint's local axes - Meshy's newer rigs differ from the older ones by 50-170 degrees
    // of roll around the arm bones and 137 degrees at Hips. A local rotation is only meaningful
    // in the frame it was authored in, so copying one between such rigs twists every limb by
    // that frame difference. A joint's frame orientation is arbitrary as far as skinning is
    // concerned (the inverse bind matrices absorb it), so changing it is free: the mesh at rest
    // is untouched, and only the meaning of a local rotation changes.
    //
    // The per-joint frame change is a constant C = inverse(oldWorld) * newWorld. A clip keeps
    // its exact world-space motion under it when every local key becomes
    //     local' = inverse(C_parent) * local * C_self
    // (world' = world * C, by induction down the chain) - that's how the rig's own (Native)
    // clips are carried across, and how the rest pose itself is rewritten. Translations are
    // offsets in the parent's frame, so they turn by inverse(C_parent).
    //
    // Two further cleanups ride along, both optional:
    //   - Extra joints (in the target's skin but not the reference's, e.g. Meshy's Bone_0xx
    //     hand helpers parented to the FOREARM, which no clip ever moves and so leave part of
    //     the hand behind whenever the wrist turns) have their skin weights folded into the
    //     nearest shared joint and are dropped from the skin. The nodes themselves stay as
    //     plain, unweighted nodes - SharpGLTF has no node-deletion API.
    //   - Scale keys on reference-convention clips are re-anchored from the reference's rest
    //     scale onto the target's (the older rigs carry a 0.97 rest scale on the shoulders,
    //     which copied raw shrinks the target's arms by 3%).
    public static class RigNormalizer
    {
        private const string HipsName = "Hips";

        public sealed class Analysis
        {
            public required List<string> SharedJoints { get; init; }
            // Extra joint name -> the shared joint its weights would fold into.
            public required Dictionary<string, string> FoldMap { get; init; }
            // Clip name -> best-guess convention, in model order.
            public required List<(string Clip, ClipConvention Detected)> Clips { get; init; }
            // Largest rest-rotation difference across the shared joints, in degrees - ~0 means
            // this rig has already been normalized against this reference.
            public required float MaxFrameDifferenceDegrees { get; init; }
            public bool AlreadyNormalized => MaxFrameDifferenceDegrees < 0.5f;
        }

        public static Analysis Analyze(ModelRoot target, ModelRoot reference)
        {
            var refSkin = reference.LogicalSkins.FirstOrDefault()
                ?? throw new InvalidOperationException("The reference model has no rigging.");
            var skin = target.LogicalSkins.FirstOrDefault()
                ?? throw new InvalidOperationException("This model has no rigging.");

            var refJoints = JointsByName(refSkin);
            var tgtJoints = JointsByName(skin);
            var shared = tgtJoints.Keys.Where(refJoints.ContainsKey).ToList();
            if (shared.Count == 0)
                throw new InvalidOperationException("The two rigs don't share any joint names.");

            float maxDiff = shared.Max(n => AngleDegrees(WorldRotation(tgtJoints[n]), WorldRotation(refJoints[n])));
            bool alreadyNormalized = maxDiff < 0.5f;

            var clips = new List<(string, ClipConvention)>();
            foreach (var anim in target.LogicalAnimations)
            {
                // A rig already in the reference's frames gives Native and Reference identical
                // scores; Native (convert nothing) is the only answer that can't double-apply.
                var detected = alreadyNormalized ? ClipConvention.Native : DetectConvention(anim, tgtJoints, refJoints);
                clips.Add((ClipName(anim), detected));
            }

            return new Analysis
            {
                SharedJoints = shared,
                FoldMap = BuildFoldMap(skin, refJoints),
                Clips = clips,
                MaxFrameDifferenceDegrees = maxDiff,
            };
        }

        public static string Apply(ModelRoot target, ModelRoot reference,
                                   IReadOnlyDictionary<string, ClipConvention> conventions, bool foldExtraJoints)
        {
            var refJoints = JointsByName(reference.LogicalSkins.First());
            var skins = target.LogicalSkins.ToList();
            var tgtJoints = JointsByName(skins.First());

            // ---- Snapshot everything the rewrite reads, before anything is written ----------------
            var nodes = target.LogicalNodes.ToList();
            var oldWorld = nodes.ToDictionary(n => n, n => n.WorldMatrix);
            var oldWorldRot = nodes.ToDictionary(n => n, n => WorldRotation(n));
            var oldWorldScale = nodes.ToDictionary(n => n, n => { Matrix4x4.Decompose(oldWorld[n], out var s, out _, out _); return s; });
            var oldLocal = nodes.ToDictionary(n => n, n => { Matrix4x4.Decompose(n.LocalMatrix, out var s, out var r, out var t); return (S: s, R: r, T: t); });
            var oldInverseBinds = skins.ToDictionary(s => s, s => Enumerable.Range(0, s.JointsCount).Select(s.GetJoint).ToList());

            // Frame change per node: non-identity only on shared joints.
            var frameChange = nodes.ToDictionary(n => n, _ => Quaternion.Identity);
            foreach (var (name, node) in tgtJoints)
                if (refJoints.TryGetValue(name, out var refNode))
                    frameChange[node] = Quaternion.Normalize(Quaternion.Inverse(oldWorldRot[node]) * WorldRotation(refNode));

            Quaternion ParentChange(Node n) => n.VisualParent != null ? frameChange[n.VisualParent] : Quaternion.Identity;
            Vector3 ParentScale(Node n) => n.VisualParent != null ? oldWorldScale[n.VisualParent] : Vector3.One;

            Quaternion ConvertRotation(Node n, Quaternion local) =>
                Quaternion.Normalize(Quaternion.Inverse(ParentChange(n)) * local * frameChange[n]);

            // An offset in the parent's frame, turned by inverse(C_parent) - with the parent's
            // world scale divided out and back in, so a cm-to-m armature scale doesn't skew it.
            Vector3 ConvertTranslation(Node n, Vector3 local)
            {
                var scale = ParentScale(n);
                var turned = Vector3.Transform(local * scale, Quaternion.Inverse(ParentChange(n)));
                return new Vector3(
                    MathF.Abs(scale.X) > 1e-8f ? turned.X / scale.X : turned.X,
                    MathF.Abs(scale.Y) > 1e-8f ? turned.Y / scale.Y : turned.Y,
                    MathF.Abs(scale.Z) > 1e-8f ? turned.Z / scale.Z : turned.Z);
            }

            // ---- Rest pose ---------------------------------------------------------------------------
            int rebakedJoints = frameChange.Count(kv => !IsIdentity(kv.Value));
            foreach (var n in nodes)
            {
                bool own = !IsIdentity(frameChange[n]);
                bool parent = !IsIdentity(ParentChange(n));
                if (!own && !parent) continue;
                var (_, r, t) = oldLocal[n];
                n.WithLocalRotation(ConvertRotation(n, r));
                n.WithLocalTranslation(ConvertTranslation(n, t));
            }

            // ---- Inverse binds: keep every joint's skinning matrix at rest exactly as it was --------
            // Row-vector convention: a vertex is skinned by IBM * World, so IBM' * World' must equal
            // IBM * World.
            foreach (var skin in skins)
            {
                var rebound = oldInverseBinds[skin].Select(j =>
                {
                    Matrix4x4.Invert(j.Joint.WorldMatrix, out var newWorldInverse);
                    return (j.Joint, j.InverseBindMatrix * oldWorld[j.Joint] * newWorldInverse);
                }).ToList();
                skin.BindJoints(rebound);
            }

            // ---- Clips ------------------------------------------------------------------------------
            Quaternion hipsDelta = Quaternion.Identity;
            if (tgtJoints.TryGetValue(HipsName, out var hips) && refJoints.TryGetValue(HipsName, out var refHips))
                hipsDelta = Quaternion.Normalize(Quaternion.Inverse(LocalRotation(refHips)) * oldLocal[hips].R);

            var refLocalScale = refJoints.ToDictionary(kv => kv.Key, kv => { Matrix4x4.Decompose(kv.Value.LocalMatrix, out var s, out _, out _); return s; });

            var clipCounts = new Dictionary<ClipConvention, int>();
            foreach (var anim in target.LogicalAnimations)
            {
                if (anim.Name == null) continue;   // With*Animation finds its channel by clip name
                var clip = ClipName(anim);
                var convention = conventions.TryGetValue(clip, out var c) ? c : ClipConvention.Native;
                clipCounts[convention] = clipCounts.GetValueOrDefault(convention) + 1;

                // The frame change THIS clip's rotation keys need: the rig's for Native; none for a
                // reference clip (it already speaks the frames the rig now has), except that the
                // hip fix put Hips back in the target's old frame, which undoing it takes out again.
                Quaternion ClipChange(Node? n)
                {
                    if (n == null) return Quaternion.Identity;
                    return convention switch
                    {
                        ClipConvention.Native => frameChange[n],
                        ClipConvention.ReferenceHipFixed when n == hips => Quaternion.Inverse(hipsDelta),
                        _ => Quaternion.Identity,
                    };
                }

                // Read every channel before writing any - each With*Animation replaces a channel.
                var channels = anim.Channels.Where(ch => ch.TargetNode != null).Select(ch => (
                    Node: ch.TargetNode!,
                    Path: ch.TargetNodePath,
                    Rot: ch.TargetNodePath == PropertyPath.rotation ? ch.GetRotationSampler().GetLinearKeys().ToArray() : null,
                    Vec: ch.TargetNodePath == PropertyPath.translation ? ch.GetTranslationSampler().GetLinearKeys().ToArray()
                       : ch.TargetNodePath == PropertyPath.scale ? ch.GetScaleSampler().GetLinearKeys().ToArray() : null)).ToList();

                foreach (var ch in channels)
                {
                    var node = ch.Node;
                    switch (ch.Path)
                    {
                        case PropertyPath.rotation:
                        {
                            var pc = ClipChange(node.VisualParent);
                            var own = ClipChange(node);
                            if (IsIdentity(pc) && IsIdentity(own)) break;
                            node.WithRotationAnimation(anim.Name, ch.Rot!.Select(k =>
                                (k.Key, Quaternion.Normalize(Quaternion.Inverse(pc) * k.Value * own))).ToArray());
                            break;
                        }
                        case PropertyPath.translation:
                            // Every convention's translation keys sit in the target's OLD frames: a
                            // native clip's naturally, a copied one's because Fix Bone Length
                            // re-anchored them onto the target's own bind offsets.
                            if (IsIdentity(ParentChange(node))) break;
                            node.WithTranslationAnimation(anim.Name, ch.Vec!.Select(k => (k.Key, ConvertTranslation(node, k.Value))).ToArray());
                            break;
                        case PropertyPath.scale:
                        {
                            if (convention == ClipConvention.Native) break;
                            if (node.Name == null || !refLocalScale.TryGetValue(node.Name, out var refScale)) break;
                            var ratio = SafeDivide(oldLocal[node].S, refScale);
                            if (Vector3.Distance(ratio, Vector3.One) < 1e-4f) break;
                            node.WithScaleAnimation(anim.Name, ch.Vec!.Select(k => (k.Key, k.Value * ratio)).ToArray());
                            break;
                        }
                    }
                }
            }

            // ---- Extra joints -----------------------------------------------------------------------
            int folded = 0;
            if (foldExtraJoints)
                foreach (var skin in skins)
                    folded += FoldExtraJoints(target, skin, refJoints);

            var parts = new List<string> { $"Re-oriented {rebakedJoints} joint(s) to the reference's frames." };
            if (clipCounts.Count > 0)
                parts.Add("Clips: " + string.Join(", ", clipCounts.Select(kv => $"{kv.Value} {Describe(kv.Key)}")) + ".");
            if (folded > 0) parts.Add($"Folded {folded} extra joint(s) into their nearest shared joint.");
            return string.Join(" ", parts);
        }

        public static string Describe(ClipConvention c) => c switch
        {
            ClipConvention.Native => "native",
            ClipConvention.Reference => "copied",
            _ => "copied + hip fix",
        };

        // ---- Detection -------------------------------------------------------------------------------

        // Scores each convention by how far the clip's rotation keys stray from the rest pose that
        // convention implies, and picks the closest. Under the right one a key differs from rest
        // only by the clip's own joint bends; under a wrong one every key also carries the frame
        // difference between the rigs (tens to 170 degrees on the arms), so the gap is wide.
        private static ClipConvention DetectConvention(Animation anim, Dictionary<string, Node> tgtJoints, Dictionary<string, Node> refJoints)
        {
            bool hasHips = tgtJoints.TryGetValue(HipsName, out var hips) && refJoints.ContainsKey(HipsName);
            var hipsDelta = hasHips
                ? Quaternion.Normalize(Quaternion.Inverse(LocalRotation(refJoints[HipsName])) * LocalRotation(hips!))
                : Quaternion.Identity;

            double native = 0, reference = 0, hipFixed = 0;
            int samples = 0;
            foreach (var ch in anim.Channels)
            {
                if (ch.TargetNodePath != PropertyPath.rotation || ch.TargetNode?.Name is not string name) continue;
                if (!tgtJoints.ContainsKey(name) || !refJoints.TryGetValue(name, out var refNode)) continue;

                var restNative = LocalRotation(ch.TargetNode);
                var restRef = LocalRotation(refNode);
                var restHipFixed = name == HipsName ? restRef * hipsDelta
                    : ch.TargetNode.VisualParent?.Name == HipsName ? Quaternion.Inverse(hipsDelta) * restRef
                    : restRef;

                var keys = ch.GetRotationSampler().GetLinearKeys().ToList();
                int step = Math.Max(1, keys.Count / 30);
                for (int i = 0; i < keys.Count; i += step)
                {
                    var q = keys[i].Value;
                    native += AngleDegrees(q, restNative);
                    reference += AngleDegrees(q, restRef);
                    hipFixed += AngleDegrees(q, restHipFixed);
                    samples++;
                }
            }
            if (samples == 0) return ClipConvention.Native;
            if (!hasHips) hipFixed = double.MaxValue;
            if (native <= reference && native <= hipFixed) return ClipConvention.Native;
            return reference <= hipFixed ? ClipConvention.Reference : ClipConvention.ReferenceHipFixed;
        }

        // ---- Folding extra joints ----------------------------------------------------------------

        // Each joint the reference doesn't have -> the nearest (by rest position) joint it does.
        // Nearest rather than parent on purpose: Meshy parents its hand helper chain to the
        // forearm even though it sits on the hand.
        private static Dictionary<string, string> BuildFoldMap(Skin skin, Dictionary<string, Node> refJoints)
        {
            var joints = Enumerable.Range(0, skin.JointsCount).Select(i => skin.GetJoint(i).Joint).ToList();
            var keep = joints.Where(j => j.Name != null && refJoints.ContainsKey(j.Name)).ToList();
            var map = new Dictionary<string, string>();
            if (keep.Count == 0) return map;
            // Face bones are this app's own (FaceBoneRigger), never Meshy's - a reference rigged
            // before they were added mustn't strip them back out.
            foreach (var extra in joints.Where(j => j.Name != null && !refJoints.ContainsKey(j.Name) && !FaceBoneRigger.AllBoneNames.Contains(j.Name)))
            {
                var pos = extra.WorldMatrix.Translation;
                var nearest = keep.OrderBy(k => Vector3.DistanceSquared(k.WorldMatrix.Translation, pos)).First();
                map[extra.Name!] = nearest.Name!;
            }
            return map;
        }

        private static int FoldExtraJoints(ModelRoot model, Skin skin, Dictionary<string, Node> refJoints)
        {
            var foldMap = BuildFoldMap(skin, refJoints);
            if (foldMap.Count == 0) return 0;

            var old = Enumerable.Range(0, skin.JointsCount).Select(skin.GetJoint).ToList();
            var kept = old.Where(j => j.Joint.Name == null || !foldMap.ContainsKey(j.Joint.Name)).ToList();
            var newIndex = kept.Select((j, i) => (j.Joint, i)).ToDictionary(t => t.Joint, t => t.i);
            var remap = old.Select(j => newIndex.TryGetValue(j.Joint, out var i) ? i
                : newIndex[kept.First(k => k.Joint.Name == foldMap[j.Joint.Name!]).Joint]).ToArray();

            foreach (var node in model.LogicalNodes.Where(n => n.Skin == skin && n.Mesh != null))
                foreach (var prim in node.Mesh.Primitives)
                    RemapWeights(prim, remap, kept.Count - 1);

            skin.BindJoints(kept);
            return foldMap.Count;
        }

        private static void RemapWeights(MeshPrimitive prim, int[] remap, int maxJoint)
        {
            if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc) ||
                !prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) return;
            if (prim.VertexAccessors.ContainsKey("JOINTS_1"))
                throw new InvalidOperationException("Meshes with more than 4 joint influences per vertex (JOINTS_1) aren't supported.");

            var joints = jAcc.AsVector4Array();
            var weights = wAcc.AsVector4Array();
            var newJoints = new Vector4[joints.Count];
            var newWeights = new Vector4[joints.Count];
            var merged = new Dictionary<int, float>();
            for (int v = 0; v < joints.Count; v++)
            {
                merged.Clear();
                var j = joints[v]; var w = weights[v];
                foreach (var (ji, wi) in new[] { (j.X, w.X), (j.Y, w.Y), (j.Z, w.Z), (j.W, w.W) })
                {
                    if (wi <= 0f) continue;
                    int idx = remap[(int)MathF.Round(ji)];
                    merged[idx] = merged.GetValueOrDefault(idx) + wi;
                }
                // Largest first: WriteWeights folds its rounding error into the first slot.
                var top = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float total = top.Sum(kv => kv.Value);
                if (total <= 0f) { newJoints[v] = Vector4.Zero; newWeights[v] = new Vector4(1, 0, 0, 0); continue; }
                float Wt(int k) => k < top.Count ? top[k].Value / total : 0f;
                float Jt(int k) => k < top.Count ? top[k].Key : 0f;
                newJoints[v] = new Vector4(Jt(0), Jt(1), Jt(2), Jt(3));
                newWeights[v] = new Vector4(Wt(0), Wt(1), Wt(2), Wt(3));
            }
            FaceBoneRigger.WriteJoints(prim, jAcc, newJoints, maxJoint);
            FaceBoneRigger.WriteWeights(prim, wAcc, newWeights);
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private static Dictionary<string, Node> JointsByName(Skin skin)
        {
            var map = new Dictionary<string, Node>();
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var node = skin.GetJoint(i).Joint;
                if (node.Name != null) map.TryAdd(node.Name, node);
            }
            return map;
        }

        private static string ClipName(Animation a) => a.Name ?? $"Anim_{a.LogicalIndex}";

        private static Quaternion WorldRotation(Node n)
        {
            Matrix4x4.Decompose(n.WorldMatrix, out _, out var r, out _);
            return Quaternion.Normalize(r);
        }

        private static Quaternion LocalRotation(Node n)
        {
            Matrix4x4.Decompose(n.LocalMatrix, out _, out var r, out _);
            return Quaternion.Normalize(r);
        }

        private static float AngleDegrees(Quaternion a, Quaternion b) =>
            2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0f, 1f)) * 180f / MathF.PI;

        private static bool IsIdentity(Quaternion q) => MathF.Abs(q.W) > 0.9999999f;

        private static Vector3 SafeDivide(Vector3 a, Vector3 b) => new(
            MathF.Abs(b.X) > 1e-8f ? a.X / b.X : 1f,
            MathF.Abs(b.Y) > 1e-8f ? a.Y / b.Y : 1f,
            MathF.Abs(b.Z) > 1e-8f ? a.Z / b.Z : 1f);
    }
}
