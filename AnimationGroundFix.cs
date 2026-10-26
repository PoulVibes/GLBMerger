using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Keeps a clip's skinned mesh out of the floor, frame by frame: at every keyframe, finds how
    // far the lowest vertex dips under the floor and raises the Hips translation key by exactly
    // that much - and nothing on a frame that isn't under. A permanent edit on the shared in-memory
    // merge result (like TrimAnimation), so it lands in the file on the next Save and the engine
    // plays the corrected clip as authored.
    //
    // Why per frame, from the mesh: the merge's "Fix Floating" shifts a whole clip by one constant
    // worked out from the feet, and the per-clip Y offset is one constant too. Neither can fix a
    // get-up authored lying 5-12 in into the ground but standing correctly at the end (the game's
    // StandUp / StandUpBelly on every player rig, measured 2026-10-25). The lowest point is read
    // off the skinned mesh rather than the bones, because what sinks is skin: a hip, tucked toes,
    // the hands planted for the push-up.
    //
    // The floor is where this model's feet stand in its Idle clip (the median of Idle's lowest
    // vertex), falling back to the rest pose's soles without one. Standing feet sit an inch or so
    // under the rest-pose soles on these rigs (toes flexed, the foot flattened), so the rest pose
    // as the floor counted every standing frame as under, raised whole clips and left the end of a
    // get-up standing an inch above Idle's feet.
    //
    // Unsmoothed on purpose: the lowest vertex moves continuously with the pose, so raising by
    // exactly its depth can't pop. It is the body resting on the ground - whichever part is lowest
    // sits on the floor - and any smoothing on top leaves frames raised that weren't under.
    public static class AnimationGroundFix
    {
        // Every VertexStride-th vertex is skinned: what touches the ground is a patch of skin, not
        // one vertex, so a third of them finds it at a third of the cost.
        private const int VertexStride = 3;

        // Raises under this fraction of the height (~0.07 in) are noise, not a correction.
        private const float MinLiftShare = 0.001f;

        public sealed record Result(
            float Height,            // rest pose, toe to crown, model units
            string FloorFrom,        // what the floor was taken from: "Idle" or "rest pose"
            float DeepestBefore,     // how far the lowest vertex went under the floor, model units
            float DeepestAt,         // ...at this clip time, seconds
            float DeepestAfter,      // the same after Apply (Measure: equal to DeepestBefore)
            float LargestRaise,      // the biggest Hips raise Apply wrote (Measure: what it would)
            int FramesRaised,        // keyframes under the floor, so raised
            float EndRaise,          // the raise on the last frame (a get-up's standing end: Idle's feet vs its own)
            int Keys)                // keyframes measured
        {
            public static float Inches(float units, float height) => height > 0f ? units / height * 72f : 0f;
        }

        // Reports how far `animationName` goes under the floor and what Apply would raise, without
        // touching the model.
        public static Result Measure(ModelRoot model, string animationName)
        {
            var clip = Sampled.Read(model, animationName);
            return ResultOf(clip, clip.Deepest);
        }

        // Raises `animationName`'s Hips so its mesh never goes under the floor (see the class
        // comment). Returns what it found and what it did; a clip that never goes under is left
        // untouched.
        public static Result Apply(ModelRoot model, string animationName)
        {
            var clip = Sampled.Read(model, animationName);
            if (clip.Deepest < MinLiftShare * clip.Height) return ResultOf(clip, clip.Deepest);

            WriteHipsRaise(clip);
            return ResultOf(clip, Sampled.Read(model, animationName).Deepest);
        }

        // A clip's frame numbering: every distinct keyframe time across its channels, as the
        // Animation Trim editor's sliders and GlbMergeService.TrimAnimation count frames.
        public static float[] FrameTimes(Animation anim) => Sampled.FrameTimes(anim);

        private static Result ResultOf(Sampled clip, float deepestAfter) =>
            new(clip.Height, clip.FloorFrom, clip.Deepest, clip.DeepestAt, deepestAfter, clip.Deepest,
                clip.Need.Count(n => n >= MinLiftShare * clip.Height), clip.Need.LastOrDefault(), clip.Times.Length);

        // ---- The raise ---------------------------------------------------------------------------

        // A key on every frame time: the Hips' own local translation there plus that frame's depth
        // under the floor, carried into the Hips' parent's frame (which may be scaled or turned) so
        // it lands straight up in the scene. Linear keys at the union of every channel's times
        // reproduce the original curve exactly on every frame that isn't under.
        private static void WriteHipsRaise(Sampled clip)
        {
            var keys = new (float Time, Vector3 Value)[clip.Times.Length];
            for (int i = 0; i < keys.Length; i++)
            {
                var up = Vector3.TransformNormal(new Vector3(0f, clip.Need[i], 0f), clip.HipsParentInverse[i]);
                keys[i] = (clip.Times[i], clip.HipsLocal[i] + up);
            }
            clip.Hips.WithTranslationAnimation(clip.AnimationName, keys);
        }

        // ---- Sampling ----------------------------------------------------------------------------

        // One clip read off the model: per keyframe, how far the mesh needs raising, where the Hips
        // are, and what the Hips' local translation and parent frame are (for writing the raise).
        private sealed class Sampled
        {
            public required string AnimationName { get; init; }
            public required Node Hips { get; init; }
            public required float Height { get; init; }
            public required float[] Times { get; init; }
            public required string FloorFrom { get; init; }
            public required float[] Need { get; init; }
            public required Vector3[] HipsLocal { get; init; }
            public required Matrix4x4[] HipsParentInverse { get; init; }
            public float Deepest { get; init; }
            public float DeepestAt { get; init; }

            public static Sampled Read(ModelRoot model, string animationName)
            {
                var anim = model.LogicalAnimations.FirstOrDefault(a => (a.Name ?? $"Anim_{a.LogicalIndex}") == animationName)
                    ?? throw new InvalidOperationException($"Animation '{animationName}' not found.");
                var skins = SkinnedVertices.Read(model);
                if (skins.Count == 0) throw new InvalidOperationException("The model has no skinned mesh to keep above ground.");
                var hips = FindHips(model) ?? throw new InvalidOperationException("No Hips bone (or root joint) found.");

                var rest = WorldMatrices(model, null, 0f);
                var (sole, top) = Extent(skins, rest);
                float height = top - sole;
                var (floor, floorFrom) = StandingFloor(model, skins, sole);

                var times = FrameTimes(anim);
                var need = new float[times.Length];
                var hipsLocal = new Vector3[times.Length];
                var parentInverse = new Matrix4x4[times.Length];
                float deepest = 0f, deepestAt = 0f;
                for (int i = 0; i < times.Length; i++)
                {
                    var world = WorldMatrices(model, anim, times[i]);
                    float lowest = Extent(skins, world).Lowest;
                    need[i] = Math.Max(0f, floor - lowest);
                    if (need[i] > deepest) { deepest = need[i]; deepestAt = times[i]; }

                    hipsLocal[i] = hips.GetLocalTransform(anim, times[i]).Translation;
                    var parentWorld = hips.VisualParent != null ? world[hips.VisualParent.LogicalIndex] : Matrix4x4.Identity;
                    Matrix4x4.Invert(parentWorld, out parentInverse[i]);
                }

                return new Sampled
                {
                    AnimationName = animationName, Hips = hips, Height = height, FloorFrom = floorFrom,
                    Times = times, Need = need, HipsLocal = hipsLocal, HipsParentInverse = parentInverse,
                    Deepest = deepest, DeepestAt = deepestAt,
                };
            }

            // Where the feet stand: the median of the Idle clip's lowest vertex over its keys (a
            // shifting weight or a heel lift doesn't move it), else the rest pose's soles.
            private static (float Floor, string From) StandingFloor(ModelRoot model, List<SkinnedVertices> skins, float restSole)
            {
                var idle = model.LogicalAnimations.FirstOrDefault(a => string.Equals(a.Name, "Idle", StringComparison.OrdinalIgnoreCase));
                if (idle == null) return (restSole, "rest pose");
                var lows = FrameTimes(idle).Select(t => Extent(skins, WorldMatrices(model, idle, t)).Lowest).OrderBy(y => y).ToList();
                return lows.Count == 0 ? (restSole, "rest pose") : (lows[lows.Count / 2], "Idle");
            }

            // The merge names the root bone "Hips"; otherwise the skin's top-most joint.
            private static Node? FindHips(ModelRoot model)
            {
                var named = model.LogicalNodes.FirstOrDefault(n => n.Name == "Hips");
                if (named != null) return named;
                var skin = model.LogicalSkins.FirstOrDefault();
                if (skin == null) return null;
                var joints = Enumerable.Range(0, skin.JointsCount).Select(i => skin.GetJoint(i).Joint).ToHashSet();
                return joints.FirstOrDefault(j => j.VisualParent == null || !joints.Contains(j.VisualParent));
            }

            // Every keyframe time across the clip's channels: the same frame numbering the trim
            // sliders use (AnimationGroundFix.FrameTimes shares it with `--trim-clip`).
            internal static float[] FrameTimes(Animation anim)
            {
                var times = new SortedSet<float>();
                foreach (var ch in anim.Channels)
                {
                    if (ch.TargetNodePath == PropertyPath.translation)
                        foreach (var k in ch.GetTranslationSampler().GetLinearKeys()) times.Add(k.Key);
                    else if (ch.TargetNodePath == PropertyPath.rotation)
                        foreach (var k in ch.GetRotationSampler().GetLinearKeys()) times.Add(k.Key);
                    else if (ch.TargetNodePath == PropertyPath.scale)
                        foreach (var k in ch.GetScaleSampler().GetLinearKeys()) times.Add(k.Key);
                }
                return times.ToArray();
            }

            // Every node's world matrix at `time` (`anim` null = the rest pose), parents first, by
            // LogicalIndex.
            private static Matrix4x4[] WorldMatrices(ModelRoot model, Animation? anim, float time)
            {
                var world = new Matrix4x4[model.LogicalNodes.Count];
                void Walk(Node node, Matrix4x4 parent)
                {
                    var local = anim != null ? node.GetLocalTransform(anim, time).Matrix : node.LocalMatrix;
                    world[node.LogicalIndex] = local * parent;
                    foreach (var child in node.VisualChildren) Walk(child, world[node.LogicalIndex]);
                }
                foreach (var root in model.LogicalNodes.Where(n => n.VisualParent == null)) Walk(root, Matrix4x4.Identity);
                return world;
            }

            private static (float Lowest, float Highest) Extent(List<SkinnedVertices> skins, Matrix4x4[] world)
            {
                float lowest = float.MaxValue, highest = float.MinValue;
                foreach (var s in skins)
                {
                    var jointMatrices = s.JointMatrices(world);
                    foreach (float y in s.SkinnedHeights(jointMatrices))
                    {
                        lowest = Math.Min(lowest, y);
                        highest = Math.Max(highest, y);
                    }
                }
                return (lowest, highest);
            }
        }

        // A subsample of one skinned primitive's vertices with their joints and weights, and the
        // skin they are bound to.
        private sealed class SkinnedVertices
        {
            private readonly Skin _skin;
            private readonly Vector3[] _positions;
            private readonly Vector4[] _joints;
            private readonly Vector4[] _weights;

            private SkinnedVertices(Skin skin, Vector3[] positions, Vector4[] joints, Vector4[] weights)
            {
                _skin = skin; _positions = positions; _joints = joints; _weights = weights;
            }

            public static List<SkinnedVertices> Read(ModelRoot model)
            {
                var result = new List<SkinnedVertices>();
                foreach (var node in model.LogicalNodes.Where(n => n.Skin != null && n.Mesh != null))
                {
                    foreach (var prim in node.Mesh.Primitives)
                    {
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var pAcc) ||
                            !prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc) ||
                            !prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;
                        var p = pAcc.AsVector3Array();
                        var j = jAcc.AsVector4Array();
                        var w = wAcc.AsVector4Array();
                        int count = (p.Count + VertexStride - 1) / VertexStride;
                        var ps = new Vector3[count]; var js = new Vector4[count]; var ws = new Vector4[count];
                        for (int v = 0, k = 0; v < p.Count; v += VertexStride, k++) { ps[k] = p[v]; js[k] = j[v]; ws[k] = w[v]; }
                        result.Add(new SkinnedVertices(node.Skin, ps, js, ws));
                    }
                }
                return result;
            }

            // inverseBind * jointWorld per joint: mesh space -> scene, this frame.
            public Matrix4x4[] JointMatrices(Matrix4x4[] world)
            {
                var m = new Matrix4x4[_skin.JointsCount];
                for (int i = 0; i < m.Length; i++)
                {
                    var (joint, inverseBind) = _skin.GetJoint(i);
                    m[i] = inverseBind * world[joint.LogicalIndex];
                }
                return m;
            }

            public IEnumerable<float> SkinnedHeights(Matrix4x4[] joints)
            {
                for (int v = 0; v < _positions.Length; v++)
                {
                    var w = _weights[v];
                    var j = _joints[v];
                    float y = 0f;
                    if (w.X > 0f) y += w.X * Vector3.Transform(_positions[v], joints[(int)j.X]).Y;
                    if (w.Y > 0f) y += w.Y * Vector3.Transform(_positions[v], joints[(int)j.Y]).Y;
                    if (w.Z > 0f) y += w.Z * Vector3.Transform(_positions[v], joints[(int)j.Z]).Y;
                    if (w.W > 0f) y += w.W * Vector3.Transform(_positions[v], joints[(int)j.W]).Y;
                    yield return y;
                }
            }
        }
    }
}
