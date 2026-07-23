using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;

using SchemaAlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace GlbMerger
{
    public static class GlbMergeService
    {
        private const string Tag1 = "GLB1";
        private const string Tag2 = "GLB2";

        // Builds and returns the merged model without saving it anywhere - callers decide when
        // and where to persist it (letting "process" and "save" be separate UI actions).
        public static ModelRoot MergeTargeted(
            string? path1, List<string> allowedMats1, List<string> allowedAnims1, List<string> inPlaceAnims1, FbxAnimationSource? fbxAnims1,
            string? path2, List<string> allowedMats2, List<string> allowedAnims2, List<string> inPlaceAnims2, List<FbxAnimationSource>? fbxAnims2,
            Dictionary<string, string>? animRenameMap1 = null, Dictionary<string, string>? animRenameMap2 = null,
            List<string>? groundFixAnims1 = null, List<string>? groundFixAnims2 = null,
            Dictionary<string, float>? yRotationAnims1 = null, Dictionary<string, float>? yRotationAnims2 = null,
            Dictionary<string, float>? yOffsetAnims1 = null, Dictionary<string, float>? yOffsetAnims2 = null,
            Dictionary<string, string>? matRenameMap1 = null, Dictionary<string, string>? matRenameMap2 = null,
            string? firstMatName1 = null, string? firstMatName2 = null,
            string? firstAnimName1 = null, string? firstAnimName2 = null)
        {
            if (path1 == null)
                throw new ArgumentException("Model 1 must be loaded - its geometry is always used as the merged output's structure.");

            // Model 1 always supplies geometry; model 2 (if present) only contributes materials
            // (matched onto model 1's parts by node name) and/or supplemental animation clips.
            var structuralModel = ModelRoot.Load(path1);
            var otherModel = path2 != null ? ModelRoot.Load(path2) : null;
            var model1 = structuralModel;
            var model2 = otherModel;

            var materialsByName1 = ToMaterialLookup(ExtractMaterials(model1, "G1").Where(m => allowedMats1.Contains(m.Name)));
            var materialsByName2 = model2 != null
                ? ToMaterialLookup(ExtractMaterials(model2, "G2").Where(m => allowedMats2.Contains(m.Name)))
                : new Dictionary<string, MaterialBuilder>();

            // Rename only the output MaterialBuilder's own name - the dictionary keys (used just
            // below and in EmitNodeGeometry to match a primitive's *original* source material name)
            // must stay untouched, or primitive-to-material matching breaks.
            ApplyMaterialRenames(materialsByName1, matRenameMap1);
            ApplyMaterialRenames(materialsByName2, matRenameMap2);

            var structuralMats = materialsByName1;
            var otherMats = materialsByName2;
            var structuralTag = Tag1;
            var otherTag = Tag2;

            bool anyMaterialSelected = structuralMats.Count > 0 || otherMats.Count > 0;

            // If no materials were selected, generate a default backup material to prevent scene crashing
            var fallbackMaterial = new MaterialBuilder("Default_Opaque");

            // The other model's parts are matched by node name to the structural model's parts,
            // since both files are expected to share identical geometry/topology under different
            // textures. When only one model is loaded, there is no "other" side to match.
            var otherNodesByName = otherModel != null
                ? otherModel.LogicalNodes
                    .Where(n => n.Name != null)
                    .GroupBy(n => n.Name!)
                    .ToDictionary(g => g.Key, g => g.First())
                : new Dictionary<string, Node>();

            var outScene = new SceneBuilder();

            // Rebuild the structural model's node hierarchy 1:1 instead of flattening it into
            // baked world-space meshes, so original node names/transforms (what animation
            // channels are authored against) survive the merge unchanged.
            var nodeMap = new Dictionary<Node, NodeBuilder>();
            foreach (var node in structuralModel.DefaultScene.VisualChildren)
                outScene.AddNode(BuildNodeTree(node, nodeMap));

            // The material the user marked "First" (in either panel) should end up at index 0 of
            // the output's material array, since some engines/tools default to that. SharpGLTF
            // assigns indices in first-encountered order while emitting primitives, so the nodes
            // that reference it are processed before the rest (a pure emission-order change - it
            // doesn't touch the already-built node hierarchy, so it's safe).
            var firstMaterialOriginalName = firstMatName1 ?? firstMatName2;
            var orderedNodePairs = firstMaterialOriginalName == null
                ? nodeMap.AsEnumerable()
                : nodeMap.OrderBy(pair => NodeReferencesMaterial(pair.Key, otherNodesByName, firstMaterialOriginalName) ? 0 : 1);

            foreach (var pair in orderedNodePairs)
                EmitNodeGeometry(
                    pair.Key, pair.Value,
                    structuralMats, otherMats, structuralTag, otherTag,
                    otherNodesByName, anyMaterialSelected, fallbackMaterial,
                    outScene, nodeMap, firstMaterialOriginalName);

            var nodeBuildersByName = nodeMap.Values
                .GroupBy(n => n.Name)
                .ToDictionary(g => g.Key, g => g.First());

            // Reference translation per bone, used to retarget FBX-sourced root motion. A bone's
            // root-motion position is a large vector (e.g. Hips sitting ~1m above the origin), so
            // rotating it directly by the correction would fling it sideways/underground. Instead
            // the *delta* from the FBX clip's own starting position gets re-anchored onto the
            // target's own natural resting position.
            // Unlike rotation (where bind/T-pose conventions can legitimately differ between
            // independently-authored rigs, justifying preferring an existing animation's pose as
            // reference instead), a bone's bind *position* is always the geometrically correct
            // resting point - there's no ambiguity to work around. Anchoring to "whichever
            // existing native animation happens to be first" was inconsistent (it could pick a
            // different animation each time clips were added/reordered across accumulated merges)
            // and caused a small but consistent vertical offset - most visible on stationary
            // clips like a planted throwing motion, where there's no walking-cycle bounce to mask it.
            var targetReferenceTranslationsByName = new Dictionary<string, Vector3>();
            foreach (var node in nodeMap.Keys)
            {
                if (node.Name == null) continue;
                Matrix4x4.Decompose(node.LocalMatrix, out _, out _, out var bindTranslation);
                targetReferenceTranslationsByName[node.Name] = bindTranslation;
            }

            // Port over selected animation tracks directly onto the matching structural
            // NodeBuilders, keyed only by name within this single rebuilt hierarchy - so a
            // node name that happens to also exist in the *other* source file can never
            // hijack the wrong target. A slot can contribute clips from its own glTF model
            // *and* any number of supplemental FBX animation-only donors (matched by bone/node
            // name, e.g. a Mixamo retarget onto a rig sharing the same joint names) at once - each
            // FBX keeps its own root axis-correction, since different FBX exports can use
            // different DCC-tool conventions.
            var clips1 = new List<AnimationClipData>();
            if (model1 != null) clips1.AddRange(ExtractGlbAnimationClips(model1));
            if (fbxAnims1 != null) clips1.AddRange(RetargetFbxClips(fbxAnims1, targetReferenceTranslationsByName));

            var clips2 = new List<AnimationClipData>();
            if (model2 != null) clips2.AddRange(ExtractGlbAnimationClips(model2));
            foreach (var fbxSource in fbxAnims2 ?? Enumerable.Empty<FbxAnimationSource>())
                clips2.AddRange(RetargetFbxClips(fbxSource, targetReferenceTranslationsByName));

            // The animation the user marked "First" should end up at index 0 of the output's
            // animation array. SharpGLTF assigns indices in first-registered order (the order
            // WithLocalTranslation/Rotation/Scale are first called for a given animation name), so
            // moving its clip to the front of its own slot's list, and processing that slot's
            // ApplyClipsToNodes call before the other slot's, achieves this without touching the
            // scene/node data itself.
            if (firstAnimName1 != null) MoveClipToFront(clips1, firstAnimName1);
            if (firstAnimName2 != null) MoveClipToFront(clips2, firstAnimName2);

            void ApplySlot1() { if (allowedAnims1.Count > 0) ApplyClipsToNodes(clips1, nodeBuildersByName, allowedAnims1, new HashSet<string>(inPlaceAnims1), animRenameMap1, new HashSet<string>(groundFixAnims1 ?? new List<string>()), yRotationAnims1, yOffsetAnims1); }
            void ApplySlot2() { if (allowedAnims2.Count > 0) ApplyClipsToNodes(clips2, nodeBuildersByName, allowedAnims2, new HashSet<string>(inPlaceAnims2), animRenameMap2, new HashSet<string>(groundFixAnims2 ?? new List<string>()), yRotationAnims2, yOffsetAnims2); }

            // Only swap the default (slot 1 then slot 2) order when slot 2 has a "First" pick and
            // slot 1 doesn't - otherwise slot 1's own animations would still register ahead of it.
            if (firstAnimName2 != null && firstAnimName1 == null) { ApplySlot2(); ApplySlot1(); }
            else { ApplySlot1(); ApplySlot2(); }

            return outScene.ToGltf2();
        }

        private static void MoveClipToFront(List<AnimationClipData> clips, string clipName)
        {
            var index = clips.FindIndex(c => c.Name == clipName);
            if (index <= 0) return; // not found, or already first

            var clip = clips[index];
            clips.RemoveAt(index);
            clips.Insert(0, clip);
        }

        private static Dictionary<string, MaterialBuilder> ToMaterialLookup(IEnumerable<MaterialBuilder> materials) =>
            materials.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());

        private static void ApplyMaterialRenames(Dictionary<string, MaterialBuilder> materialsByName, Dictionary<string, string>? renameMap)
        {
            if (renameMap == null) return;

            foreach (var (originalName, material) in materialsByName)
                if (renameMap.TryGetValue(originalName, out var renamed))
                    material.Name = renamed;
        }

        private static NodeBuilder BuildNodeTree(Node srcNode, Dictionary<Node, NodeBuilder> map)
        {
            var nb = new NodeBuilder(srcNode.Name ?? $"node_{srcNode.LogicalIndex}");
            nb.LocalMatrix = srcNode.LocalMatrix;
            map[srcNode] = nb;

            foreach (var child in srcNode.VisualChildren)
                nb.AddNode(BuildNodeTree(child, map));

            return nb;
        }

        // Whether this node (or its name-matched counterpart in the "other" source model) has any
        // primitive whose original source material name matches - used to decide emission order
        // for the "First" material feature.
        private static bool NodeReferencesMaterial(Node node, IReadOnlyDictionary<string, Node> otherNodesByName, string materialOriginalName)
        {
            if (node.Mesh != null && node.Mesh.Primitives.Any(p => p.Material?.Name == materialOriginalName))
                return true;

            if (node.Name != null && otherNodesByName.TryGetValue(node.Name, out var otherNode) && otherNode.Mesh != null
                && otherNode.Mesh.Primitives.Any(p => p.Material?.Name == materialOriginalName))
                return true;

            return false;
        }

        private static void EmitNodeGeometry(
            Node srcNode, NodeBuilder nodeBuilder,
            IReadOnlyDictionary<string, MaterialBuilder> structuralMats,
            IReadOnlyDictionary<string, MaterialBuilder> otherMats,
            string structuralTag, string otherTag,
            IReadOnlyDictionary<string, Node> otherNodesByName,
            bool anyMaterialSelected,
            MaterialBuilder fallbackMaterial,
            SceneBuilder outScene,
            Dictionary<Node, NodeBuilder> nodeMap,
            string? firstMaterialOriginalName)
        {
            if (srcNode.Mesh == null) return;

            Node? otherNode = null;
            if (srcNode.Name != null)
                otherNodesByName.TryGetValue(srcNode.Name, out otherNode);

            var joints = srcNode.Skin != null ? ResolveJoints(srcNode.Skin, nodeMap) : null;

            var baseName = srcNode.Mesh.Name ?? srcNode.Name ?? "mesh";
            bool multiPrim = srcNode.Mesh.Primitives.Count > 1;

            for (int primIdx = 0; primIdx < srcNode.Mesh.Primitives.Count; primIdx++)
            {
                var prim = srcNode.Mesh.Primitives[primIdx];
                var otherPrim = (otherNode?.Mesh != null && primIdx < otherNode.Mesh.Primitives.Count)
                    ? otherNode.Mesh.Primitives[primIdx] : null;

                // Pair each primitive with the correctly-corresponding material from each
                // selected source, instead of stamping every selected material (from either
                // model) onto every primitive.
                var variants = new List<(MaterialBuilder material, string tag, string? originalName)>();

                var structuralMatName = prim.Material?.Name;
                if (structuralMatName != null && structuralMats.TryGetValue(structuralMatName, out var structuralMb))
                    variants.Add((structuralMb, structuralTag, structuralMatName));

                var otherMatName = otherPrim?.Material?.Name;
                if (otherMatName != null && otherMats.TryGetValue(otherMatName, out var otherMb))
                    variants.Add((otherMb, otherTag, otherMatName));

                if (variants.Count == 0)
                {
                    if (!anyMaterialSelected)
                        variants.Add((fallbackMaterial, "", null));
                    else
                        continue; // neither source's texture for this part was selected
                }

                // Whichever material the user marked "First" gets its primitive built before the
                // others here, so SharpGLTF registers (and therefore indexes) that MaterialBuilder
                // first in the output's material array - the comparison uses each variant's
                // *original* source material name (matched by dictionary key), since by this point
                // the MaterialBuilder's own .Name may already have been overwritten by a rename.
                if (firstMaterialOriginalName != null)
                    variants = variants.OrderBy(v => v.originalName == firstMaterialOriginalName ? 0 : 1).ToList();

                foreach (var (material, tag, _) in variants)
                {
                    var variantName = baseName
                        + (multiPrim ? $"_p{primIdx}" : "")
                        + (tag.Length > 0 ? $"_{tag}" : "");

                    var childNode = new NodeBuilder(variantName);
                    nodeBuilder.AddNode(childNode);

                    if (joints != null)
                    {
                        var skinnedMesh = BuildSkinnedPrimitive(prim, material, variantName);
                        if (skinnedMesh != null)
                        {
                            outScene.AddSkinnedMesh(skinnedMesh, joints);
                            continue;
                        }
                        // Primitive had a skin on its node but no per-vertex joint data; fall
                        // through and merge it as a rigid (unskinned) piece instead.
                    }

                    var rigidMesh = BuildRigidPrimitive(prim, material, variantName);
                    if (rigidMesh != null)
                        outScene.AddRigidMesh(rigidMesh, childNode);
                }
            }
        }

        private static (NodeBuilder Joint, Matrix4x4 InverseBindMatrix)[]? ResolveJoints(Skin skin, Dictionary<Node, NodeBuilder> nodeMap)
        {
            var joints = new (NodeBuilder, Matrix4x4)[skin.JointsCount];
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var (jointNode, invBind) = skin.GetJoint(i);
                if (!nodeMap.TryGetValue(jointNode, out var jointBuilder))
                    return null; // joint lives outside the rebuilt hierarchy; can't skin safely
                joints[i] = (jointBuilder, invBind);
            }
            return joints;
        }

        private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>? BuildRigidPrimitive(
            MeshPrimitive prim, MaterialBuilder material, string name)
        {
            var verts = ReadBaseVertices(prim, out var indices);
            if (verts == null) return null;

            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(name);
            var outPrim = mesh.UsePrimitive(material);
            foreach (var (a, b, c) in Triangles(indices))
                outPrim.AddTriangle(
                    (verts[a].geo, verts[a].mat, default),
                    (verts[b].geo, verts[b].mat, default),
                    (verts[c].geo, verts[c].mat, default));

            return mesh;
        }

        private static MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>? BuildSkinnedPrimitive(
            MeshPrimitive prim, MaterialBuilder material, string name)
        {
            var verts = ReadBaseVertices(prim, out var indices);
            if (verts == null) return null;

            var skins = ReadSkinning(prim, verts.Length);
            if (skins == null) return null;

            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(name);
            var outPrim = mesh.UsePrimitive(material);
            foreach (var (a, b, c) in Triangles(indices))
                outPrim.AddTriangle(
                    (verts[a].geo, verts[a].mat, skins[a]),
                    (verts[b].geo, verts[b].mat, skins[b]),
                    (verts[c].geo, verts[c].mat, skins[c]));

            return mesh;
        }

        private static (VertexPositionNormal geo, VertexTexture1 mat)[]? ReadBaseVertices(MeshPrimitive prim, out IReadOnlyList<uint> indices)
        {
            indices = Array.Empty<uint>();

            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) return null;
            prim.VertexAccessors.TryGetValue("NORMAL", out var normAcc);
            prim.VertexAccessors.TryGetValue("TEXCOORD_0", out var uvAcc);

            var positions = posAcc.AsVector3Array();
            var normals = normAcc?.AsVector3Array();
            var uvs = uvAcc?.AsVector2Array();
            int count = positions.Count;

            var verts = new (VertexPositionNormal, VertexTexture1)[count];
            for (int i = 0; i < count; i++)
                verts[i] = (
                    new VertexPositionNormal(positions[i], normals != null ? normals[i] : Vector3.UnitY),
                    new VertexTexture1(uvs != null ? uvs[i] : Vector2.Zero));

            if (prim.IndexAccessor != null)
                indices = prim.IndexAccessor.AsIndicesArray();
            else
            {
                var seq = new List<uint>(count);
                for (uint i = 0; i < count; i++) seq.Add(i);
                indices = seq;
            }

            return verts;
        }

        private static VertexJoints4[]? ReadSkinning(MeshPrimitive prim, int vertexCount)
        {
            var jointsAcc = prim.GetVertexAccessor("JOINTS_0");
            var weightsAcc = prim.GetVertexAccessor("WEIGHTS_0");
            if (jointsAcc == null || weightsAcc == null) return null;

            var jointIdx = jointsAcc.AsVector4Array();
            var weights = weightsAcc.AsVector4Array();

            var result = new VertexJoints4[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                var j = jointIdx[i];
                var w = weights[i];
                result[i] = new VertexJoints4(new (int, float)[]
                {
                    ((int)j.X, w.X),
                    ((int)j.Y, w.Y),
                    ((int)j.Z, w.Z),
                    ((int)j.W, w.W),
                });
            }

            return result;
        }

        private static IEnumerable<(int a, int b, int c)> Triangles(IReadOnlyList<uint> indices)
        {
            for (int i = 0; i + 2 < indices.Count; i += 3)
                yield return ((int)indices[i], (int)indices[i + 1], (int)indices[i + 2]);
        }

        private static List<AnimationClipData> RetargetFbxClips(
            FbxAnimationSource source,
            IReadOnlyDictionary<string, Vector3> targetReferenceTranslationsByName)
        {
            var result = new List<AnimationClipData>();

            foreach (var clip in source.Clips)
            {
                var retargeted = new AnimationClipData { Name = clip.Name };

                foreach (var ch in clip.NodeChannels)
                {
                    var retargetedCh = new NodeChannelData { NodeName = ch.NodeName };

                    // Hips' rotation, like its translation, is authored relative to the Armature
                    // parent node's local frame - which carries a fixed axis-convention rotation
                    // (e.g. Z-up -> Y-up). Pre-multiplying by its inverse converts Hips' raw
                    // rotation into the same untilted convention the target rig uses - verified
                    // empirically to land within ~2 degrees of the target's own natural bind-pose
                    // orientation for an idle clip. Every other bone's rotation is authored
                    // relative to its own parent *bone* (not the Armature), so it's already in a
                    // consistent frame and passes through unmodified.
                    var correction = ch.NodeName == "Hips" && source.RootTranslationCorrection.HasValue
                        ? Quaternion.Normalize(Quaternion.Inverse(source.RootTranslationCorrection.Value))
                        : Quaternion.Identity;

                    if (ch.Translation != null)
                    {
                        // A bone's absolute position (e.g. Hips sitting ~1m above the origin) is
                        // too large a vector to just rotate directly - that flings it sideways by
                        // however far the correction rotates. Take the *delta* from this clip's own
                        // starting position and re-anchor it onto the target's own natural resting
                        // position, so genuine root motion (walking across the scene) is preserved
                        // without an offset baked in.
                        //
                        // The delta is NOT rotated by the bone's per-bone rotation correction: that
                        // correction describes how the bone's own local axes are oriented relative
                        // to its parent, which is a different thing from the world/parent-space
                        // axes root-motion translation is expressed in. Rotating the delta by it
                        // can spill horizontal motion (e.g. a weight shift) into the vertical axis,
                        // making the model appear to bob/float instead of sway side to side.
                        //
                        // It IS rotated by the root's fixed axis-convention correction (e.g. the
                        // FBX's Z-up -> Y-up conversion baked into the Armature node) - that's a
                        // single constant rotation shared by every frame, not a per-bone pose
                        // difference, so it doesn't have the same "flinging" problem: it just
                        // permutes which axis is "up" vs "forward" to match the target rig.
                        var fbxOrigin = ch.Translation.OrderBy(k => k.Key).First().Value;
                        var targetOrigin = targetReferenceTranslationsByName.TryGetValue(ch.NodeName, out var t) ? t : Vector3.Zero;
                        // Same axis-convention fix as Hips' rotation, and for the same reason it
                        // needs the *inverse* there (undoing a rotation baked into the parent
                        // frame rather than applying a new one): using the correction directly
                        // flips the sign of the height/depth swap, so a clip that genuinely dives
                        // downward (in the FBX's own axis convention) came out rising/floating
                        // instead. Verified against a tackle/dive clip whose raw Z decreases
                        // (correctly diving) - the direct correction inverted that into a rise;
                        // the inverse correctly preserves the descent.
                        var axisCorrection = ch.NodeName == "Hips" && source.RootTranslationCorrection.HasValue
                            ? Quaternion.Inverse(source.RootTranslationCorrection.Value)
                            : Quaternion.Identity;

                        retargetedCh.Translation = ch.Translation.ToDictionary(
                            k => k.Key,
                            k => targetOrigin + Vector3.Transform(k.Value - fbxOrigin, axisCorrection));
                    }

                    if (ch.Rotation != null)
                    {
                        // The axis-convention fix undoes a rotation baked into the *parent* frame,
                        // so it has to be pre-multiplied (correction * rawRotation) - verified
                        // empirically (pre-multiplied lands ~2 degrees from the target's bind pose
                        // for an idle clip; post-multiplied is 90 degrees off). For every other
                        // bone, correction is Identity, so multiplication order doesn't matter.
                        retargetedCh.Rotation = ch.Rotation.ToDictionary(k => k.Key, k => Quaternion.Normalize(Quaternion.Multiply(correction, k.Value)));
                    }

                    if (ch.Scale != null)
                        retargetedCh.Scale = ch.Scale;

                    retargeted.NodeChannels.Add(retargetedCh);
                }

                result.Add(retargeted);
            }

            return result;
        }

        private static List<AnimationClipData> ExtractGlbAnimationClips(ModelRoot source)
        {
            var clips = new List<AnimationClipData>();

            foreach (var srcAnim in source.LogicalAnimations)
            {
                var clip = new AnimationClipData { Name = srcAnim.Name ?? $"Anim_{srcAnim.LogicalIndex}" };

                // glTF stores translation/rotation/scale as separate channels even when they
                // target the same node, so a naive one-NodeChannelData-per-channel loop produces
                // several partial entries sharing the same NodeName (one with only Translation,
                // another with only Rotation, etc.) instead of one consolidated entry - silently
                // breaking anything that looks a node up by name expecting all three properties
                // together (e.g. the Y rotation/offset/ground-fix corrections, which only ever
                // saw whichever partial entry happened to come first).
                var channelsByNode = new Dictionary<string, NodeChannelData>();
                NodeChannelData GetOrAdd(string nodeName)
                {
                    if (!channelsByNode.TryGetValue(nodeName, out var nc))
                    {
                        nc = new NodeChannelData { NodeName = nodeName };
                        channelsByNode[nodeName] = nc;
                        clip.NodeChannels.Add(nc);
                    }
                    return nc;
                }

                foreach (var ch in srcAnim.Channels)
                {
                    if (ch.TargetNode?.Name == null) continue;

                    var nodeChannel = GetOrAdd(ch.TargetNode.Name);
                    var path = ch.TargetNodePath;

                    if (path == PropertyPath.translation)
                    {
                        var curve = ch.GetTranslationSampler().GetLinearKeys().ToDictionary(k => k.Key, v => v.Value);
                        if (curve.Count > 0) nodeChannel.Translation = curve;
                    }
                    else if (path == PropertyPath.rotation)
                    {
                        var curve = ch.GetRotationSampler().GetLinearKeys().ToDictionary(k => k.Key, v => v.Value);
                        if (curve.Count > 0) nodeChannel.Rotation = curve;
                    }
                    else if (path == PropertyPath.scale)
                    {
                        var curve = ch.GetScaleSampler().GetLinearKeys().ToDictionary(k => k.Key, v => v.Value);
                        if (curve.Count > 0) nodeChannel.Scale = curve;
                    }
                }

                clips.Add(clip);
            }

            return clips;
        }

        private static void ApplyClipsToNodes(
            List<AnimationClipData> clips, IReadOnlyDictionary<string, NodeBuilder> targetsByName,
            List<string> names, HashSet<string> inPlaceNames, IReadOnlyDictionary<string, string>? renameMap,
            HashSet<string>? groundFixNames = null,
            IReadOnlyDictionary<string, float>? yRotationByName = null, IReadOnlyDictionary<string, float>? yOffsetByName = null)
        {
            foreach (var clip in clips)
            {
                if (!names.Contains(clip.Name)) continue;

                // The animation track is written into the output under the user's chosen "Merged
                // As" name (falling back to the original name if it was never renamed) - matching
                // above against the *original* clip.Name so selection/lock-in-place keep working
                // regardless of what the clip ends up being called in the merged file.
                string outputName = renameMap != null && renameMap.TryGetValue(clip.Name, out var renamed) ? renamed : clip.Name;

                // "Lock in place" pins the ROOT bone's horizontal (X/Z) position so the clip
                // doesn't walk the model away from the origin, but keeps its vertical (Y) motion
                // playing - a jump/crouch should still move up and down even when locked in place.
                // Only the root (Hips) is affected: some exports bake a translation key onto every
                // joint every frame regardless of whether it actually moves, and indiscriminately
                // touching those too snaps non-root bones back to their static bind position while
                // their rotation keeps animating, producing a visible sway/wobble mismatch.
                bool lockInPlace = inPlaceNames.Contains(clip.Name);

                // "Fix Floating" corrects a uniform vertical offset (the whole clip floating a
                // constant amount above/below the ground) by finding this clip's own most
                // "grounded" pose and re-anchoring the whole clip's Hips height so that pose's
                // feet land exactly where the target rig's own resting feet are. Must run before
                // Lock In Place reads the Hips translation, since it shifts it.
                if (groundFixNames != null && groundFixNames.Contains(clip.Name))
                    ApplyGroundCorrection(clip, targetsByName);

                // Manual overrides layered on top of the automatic correction, for the cases it
                // doesn't quite nail: a fixed turn around the world Y axis (facing direction and
                // root motion path rotate together, pivoting on the clip's own starting position),
                // and a further additive Y nudge for residual floating/sinking.
                if (yRotationByName != null && yRotationByName.TryGetValue(clip.Name, out var yDegrees) && yDegrees != 0f)
                    ApplyYRotation(clip, yDegrees);

                if (yOffsetByName != null && yOffsetByName.TryGetValue(clip.Name, out var yOffset) && yOffset != 0f)
                    ApplyYOffset(clip, yOffset);

                foreach (var nodeChannel in clip.NodeChannels)
                {
                    if (!targetsByName.TryGetValue(nodeChannel.NodeName, out var dstNode)) continue;

                    if (nodeChannel.Translation != null)
                    {
                        if (lockInPlace && nodeChannel.NodeName == "Hips")
                        {
                            // Lock to the target node's actual rest/bind pose position (the
                            // un-animated geometry), not the clip's own first frame - the clip's
                            // first frame can itself be an arbitrary animated position (e.g. when
                            // retargeted, it's anchored onto an *existing* target animation's
                            // first frame, which is not necessarily the model's resting pose).
                            Matrix4x4.Decompose(dstNode.LocalMatrix, out _, out _, out var bindTranslation);
                            var lockedXZ = nodeChannel.Translation.ToDictionary(
                                k => k.Key,
                                k => new Vector3(bindTranslation.X, k.Value.Y, bindTranslation.Z));
                            dstNode.WithLocalTranslation(outputName, lockedXZ);
                        }
                        else
                        {
                            dstNode.WithLocalTranslation(outputName, nodeChannel.Translation);
                        }
                    }

                    if (nodeChannel.Rotation != null)
                        dstNode.WithLocalRotation(outputName, nodeChannel.Rotation);

                    if (nodeChannel.Scale != null)
                        dstNode.WithLocalScale(outputName, nodeChannel.Scale);
                }
            }
        }

        private static readonly (string UpLeg, string Leg, string Foot)[] LegChains =
        {
            ("LeftUpLeg", "LeftLeg", "LeftFoot"),
            ("RightUpLeg", "RightLeg", "RightFoot"),
        };

        // Corrects a uniform vertical float/sink: some retargeted clips leave their feet a
        // constant small distance off the ground even though the Hips translation anchor itself
        // is correct, because the clip's own natural knee-bend at rest differs slightly from the
        // pose the target's bind position implies. Finds the clip's own most "grounded" moment
        // (both feet as level with each other as possible, and as low as possible among the
        // roughly-level candidates - the actual planted-foot contact, not a level moment in
        // mid-air) via forward kinematics, then shifts the whole clip's Hips height by a constant
        // so that moment's feet land exactly where the target rig's own resting feet are.
        private static void ApplyGroundCorrection(AnimationClipData clip, IReadOnlyDictionary<string, NodeBuilder> targetsByName)
        {
            var hipsChannel = clip.NodeChannels.FirstOrDefault(c => c.NodeName == "Hips");
            if (hipsChannel?.Translation == null) return;

            Vector3 BindTranslation(string name) =>
                targetsByName.TryGetValue(name, out var node) && Matrix4x4.Decompose(node.LocalMatrix, out _, out _, out var t)
                    ? t : Vector3.Zero;

            // A bone's bind (rest) rotation is not Identity - it encodes how the segment naturally
            // points (e.g. "down the leg"), so it has to be the fallback whenever a frame has no
            // animated key, not Identity, or the reference/candidate heights come out nonsensical.
            Quaternion BindRotation(string name) =>
                targetsByName.TryGetValue(name, out var node) && Matrix4x4.Decompose(node.LocalMatrix, out _, out var r, out _)
                    ? r : Quaternion.Identity;

            Dictionary<float, Quaternion>? RotationOf(string name) =>
                clip.NodeChannels.FirstOrDefault(c => c.NodeName == name)?.Rotation;

            // World-space foot height via forward kinematics: Hips -> UpLeg -> Leg -> Foot. Only
            // Hips truly translates; the leg segments use their bind (rest) offsets, since that's
            // the actual bone length/attachment point regardless of how the joint is rotated.
            float FootHeight(
                Vector3 hipsPos, Quaternion hipsRot, float t,
                Dictionary<float, Quaternion>? upLegRot, Vector3 upLegBind, Quaternion upLegBindRot,
                Dictionary<float, Quaternion>? legRot, Vector3 legBind, Quaternion legBindRot,
                Vector3 footBind)
            {
                var hipsWorld = Matrix4x4.CreateFromQuaternion(hipsRot) * Matrix4x4.CreateTranslation(hipsPos);
                var upLegQ = upLegRot != null && upLegRot.TryGetValue(t, out var u) ? u : upLegBindRot;
                var upLegWorld = (Matrix4x4.CreateFromQuaternion(upLegQ) * Matrix4x4.CreateTranslation(upLegBind)) * hipsWorld;
                var legQ = legRot != null && legRot.TryGetValue(t, out var l) ? l : legBindRot;
                var legWorld = (Matrix4x4.CreateFromQuaternion(legQ) * Matrix4x4.CreateTranslation(legBind)) * upLegWorld;
                var footWorld = Matrix4x4.CreateTranslation(footBind) * legWorld;
                return footWorld.Translation.Y;
            }

            var legs = LegChains
                .Select(l => (
                    UpLegRot: RotationOf(l.UpLeg), UpLegBind: BindTranslation(l.UpLeg), UpLegBindRot: BindRotation(l.UpLeg),
                    LegRot: RotationOf(l.Leg), LegBind: BindTranslation(l.Leg), LegBindRot: BindRotation(l.Leg),
                    FootBind: BindTranslation(l.Foot)))
                .ToArray();
            if (legs.Any(l => l.UpLegBind == Vector3.Zero && l.LegBind == Vector3.Zero)) return; // rig has no legs to ground against

            // Target's own resting ground reference: foot height with Hips at its bind position
            // and legs at their bind rotation - i.e. where this rig's feet naturally sit at rest.
            var hipsBind = BindTranslation("Hips");
            var hipsBindRot = BindRotation("Hips");
            float groundReferenceY = legs.Average(l =>
                FootHeight(hipsBind, hipsBindRot, 0, null, l.UpLegBind, l.UpLegBindRot, null, l.LegBind, l.LegBindRot, l.FootBind));

            var hipsRotation = hipsChannel.Rotation;
            var candidates = hipsChannel.Translation.Keys.OrderBy(t => t).Select(t =>
            {
                var hipsPos = hipsChannel.Translation[t];
                var hipsRot = hipsRotation != null && hipsRotation.TryGetValue(t, out var hr) ? hr : hipsBindRot;
                var feetY = legs.Select(l => FootHeight(hipsPos, hipsRot, t, l.UpLegRot, l.UpLegBind, l.UpLegBindRot, l.LegRot, l.LegBind, l.LegBindRot, l.FootBind)).ToArray();
                return (AvgY: feetY.Average(), Levelness: feetY.Max() - feetY.Min());
            }).ToList();

            // Prefer frames where the feet are close to level (a planted stance, not mid-stride),
            // then pick the lowest of those - the actual ground-contact pose, rather than a
            // moment where the feet just happen to align in mid-air during a jump.
            float levelnessThreshold = candidates.Min(c => c.Levelness) * 3f + 0.01f;
            float groundedY = candidates.Where(c => c.Levelness <= levelnessThreshold).Min(c => c.AvgY);

            float deltaY = groundedY - groundReferenceY;
            hipsChannel.Translation = hipsChannel.Translation.ToDictionary(
                k => k.Key,
                k => new Vector3(k.Value.X, k.Value.Y - deltaY, k.Value.Z));
        }

        // Manually turns the whole clip around the world Y axis: the root's horizontal (X/Z)
        // path rotates around its own starting position (so the clip doesn't also drift away from
        // wherever it was correctly anchored), and its facing (rotation) turns by the same amount
        // so the character still faces the direction it's moving.
        private static void ApplyYRotation(AnimationClipData clip, float degrees)
        {
            var hipsChannel = clip.NodeChannels.FirstOrDefault(c => c.NodeName == "Hips");
            if (hipsChannel == null) return;

            var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, degrees * MathF.PI / 180f);

            if (hipsChannel.Translation != null)
            {
                var pivot = hipsChannel.Translation.OrderBy(k => k.Key).First().Value;
                hipsChannel.Translation = hipsChannel.Translation.ToDictionary(
                    k => k.Key,
                    k => pivot + Vector3.Transform(k.Value - pivot, turn));
            }

            if (hipsChannel.Rotation != null)
                // Multiply(turn, k.Value) - NOT Multiply(k.Value, turn). System.Numerics'
                // Quaternion.Multiply(a, b) combined with Vector3.Transform applies b first
                // (inner) then a (outer) - the opposite of this codebase's NodeBuilder matrix
                // convention. Putting turn second would apply it in Hips' own (possibly tilted)
                // local frame before Hips' rotation even happens, spinning around a tilted axis
                // instead of the fixed world Y axis. Verified empirically: this order leaves a
                // transformed reference vector's Y-component unchanged (a true world-Y spin only
                // changes facing, not how tipped-over the character is).
                hipsChannel.Rotation = hipsChannel.Rotation.ToDictionary(
                    k => k.Key,
                    k => Quaternion.Normalize(Quaternion.Multiply(turn, k.Value)));
        }

        // Manual additive vertical nudge on top of whatever automatic correction already ran, for
        // the residual cases where it doesn't quite land right.
        private static void ApplyYOffset(AnimationClipData clip, float offset)
        {
            var hipsChannel = clip.NodeChannels.FirstOrDefault(c => c.NodeName == "Hips");
            if (hipsChannel?.Translation == null) return;

            hipsChannel.Translation = hipsChannel.Translation.ToDictionary(
                k => k.Key,
                k => new Vector3(k.Value.X, k.Value.Y + offset, k.Value.Z));
        }

        private static List<MaterialBuilder> ExtractMaterials(ModelRoot model, string suffix)
        {
            var result = new List<MaterialBuilder>();

            foreach (var mat in model.LogicalMaterials)
            {
                // We keep the original clean material name so selection mapping works exactly
                var matName = mat.Name ?? $"Material_{suffix}_{mat.LogicalIndex}";
                var mb = new MaterialBuilder(matName);

                mb.WithAlpha(mat.Alpha switch
                {
                    SchemaAlphaMode.BLEND => SharpGLTF.Materials.AlphaMode.BLEND,
                    SchemaAlphaMode.MASK => SharpGLTF.Materials.AlphaMode.MASK,
                    _ => SharpGLTF.Materials.AlphaMode.OPAQUE
                }, mat.AlphaCutoff);

                mb.WithDoubleSide(mat.DoubleSided);

                ApplyChannel(mat, mb, "BaseColor", KnownChannel.BaseColor, KnownProperty.RGBA);
                ApplyChannel(mat, mb, "MetallicRoughness", KnownChannel.MetallicRoughness, KnownProperty.MetallicFactor, KnownProperty.RoughnessFactor);
                ApplyChannel(mat, mb, "Normal", KnownChannel.Normal, KnownProperty.NormalScale);
                ApplyChannel(mat, mb, "Occlusion", KnownChannel.Occlusion, KnownProperty.OcclusionStrength);
                ApplyChannel(mat, mb, "Emissive", KnownChannel.Emissive, KnownProperty.RGB);

                result.Add(mb);
            }

            return result;
        }

        private static void ApplyChannel(Material mat, MaterialBuilder mb, string channelName, KnownChannel known, params KnownProperty[] scalarProps)
        {
            var ch = mat.FindChannel(channelName);
            if (!ch.HasValue) return;

            bool hasTexture = false;
            var tex = ch.Value.Texture;
            if (tex?.PrimaryImage != null)
            {
                var content = tex.PrimaryImage.Content;
                var img = new SharpGLTF.Memory.MemoryImage(content.Content.ToArray());

                mb.UseChannel(known)
                  .UseTexture()
                  .WithPrimaryImage(img);
                hasTexture = true;
            }

            // NormalScale/OcclusionStrength only exist as properties of a texture reference in
            // glTF; writing them without an attached texture emits a dangling TextureInfo that
            // fails validation on save.
            bool factorRequiresTexture = known == KnownChannel.Normal || known == KnownChannel.Occlusion;
            if (factorRequiresTexture && !hasTexture) return;

            foreach (var prop in scalarProps)
            {
                try
                {
                    var v = ch.Value.GetFactor(prop.ToString());
                    mb.UseChannel(known).Parameters[prop] = v;
                }
                catch { /* property not present on this channel */ }
            }
        }
    }
}
