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
        // The single source of truth for naming an unnamed material, used everywhere a material
        // is identified by name: the UI's Include-selection list (GlbInfoPanel), material
        // extraction, and primitive-to-material matching below. glTF material names are optional,
        // and AI-exported models (e.g. Meshy AI) frequently omit them entirely - if any of these
        // three sites ever generates a different fallback string than the others, the material
        // silently fails to match and every primitive using it falls back to plain white.
        public static string GetEffectiveMaterialName(Material mat) =>
            mat.Name ?? $"Material_{mat.LogicalIndex}";

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
            // Which of a material's channels (BaseColor/MetallicRoughness/Normal/Occlusion/
            // Emissive) to actually pull into the output, keyed by that material's *original*
            // source name - a material absent from the dictionary, or entirely, contributes all
            // of its channels (same "absent means default" convention as the other per-item
            // dictionaries above). See KnownMaterialChannelNames.
            Dictionary<string, HashSet<string>>? matChannels1 = null, Dictionary<string, HashSet<string>>? matChannels2 = null,
            string? firstMatName1 = null, string? firstMatName2 = null,
            string? firstAnimName1 = null, string? firstAnimName2 = null,
            // Frame range to keep for a given clip name, as [Start, End] keyframe indices into
            // that clip's own distinct keyframe times (not a fixed sample rate - glTF/FBX clips
            // don't store one) - a clip absent from the dictionary, or entirely, is left
            // untouched. Applied to the source-agnostic AnimationClipData right after
            // extraction/retargeting, before it's baked onto the output's NodeBuilders, so both a
            // slot's native GLB clips and its supplemental FBX clips trim the same way.
            Dictionary<string, (int Start, int End)>? frameTrimByName1 = null,
            Dictionary<string, (int Start, int End)>? frameTrimByName2 = null,
            // Re-anchors a clip's translation channels (usually just a bone's static length,
            // repeated every frame) from the source rig's own bone lengths onto the target rig's,
            // so a differently-proportioned donor rig doesn't stretch/squish the target's limbs.
            // Purely geometric - doesn't touch rotation at all, unlike the rotation-retargeting
            // this app tried and backed out of. A no-op for slot 1 (its own clips are already in
            // the structural model's own bone lengths) - kept as a parameter anyway to stay
            // parallel with every other per-animation option, which all come in matched pairs.
            List<string>? fixBoneLengthAnims1 = null,
            List<string>? fixBoneLengthAnims2 = null,
            // Corrects just the Hips joint's rotation, using a fixed delta computed from each
            // rig's own Hips bind pose - deliberately narrower than the full per-joint/chain
            // rotation retargeting this app tried and backed out of (that made limb rotations
            // worse). Hips is a special case: unlike a limb, its bind rotation difference between
            // two independently-authored rigs is primarily a coordinate-convention/lean mismatch
            // rather than a "different stance" one, so a plain bind-pose delta suits it without
            // the over-rotation problems seen on legs. A no-op for slot 1, kept as a parameter
            // anyway to stay parallel with every other per-animation option.
            List<string>? fixHipRotationAnims1 = null,
            List<string>? fixHipRotationAnims2 = null,
            // Model 2 never contributes its own geometry (model 1's is always used), so only
            // model 1's mesh/node names need a user-controlled rename - keyed by the node's
            // original baseName (Mesh.Name, falling back to Node.Name), same as GlbInfoPanel's
            // geometry grid rows. Output naming used to auto-suffix "_GLB1"/"_GLB2", but that
            // compounded every time an already-merged file was reloaded and merged again (each
            // pass stamped another suffix on top) - the user now names the output explicitly
            // instead.
            Dictionary<string, string>? geomRenameMap1 = null)
        {
            if (path1 == null)
                throw new ArgumentException("Model 1 must be loaded - its geometry is always used as the merged output's structure.");

            // Model 1 always supplies geometry; model 2 (if present) only contributes materials
            // (matched onto model 1's parts by node name) and/or supplemental animation clips.
            var structuralModel = ModelRoot.Load(path1);
            var otherModel = path2 != null ? ModelRoot.Load(path2) : null;
            var model1 = structuralModel;
            var model2 = otherModel;

            // Raw, still-unbuilt selected materials, keyed by their *original* source name (the
            // key EmitNodeGeometry matches primitives against) - kept as raw SharpGLTF Materials
            // rather than MaterialBuilders here so a same-output-name collision between the two
            // models (below) can pull channels from both sources into one combined builder.
            var rawMats1 = model1.LogicalMaterials
                .Where(m => allowedMats1.Contains(MaterialKey(m)))
                .GroupBy(MaterialKey).ToDictionary(g => g.Key, g => g.First());
            var rawMats2 = model2 != null
                ? model2.LogicalMaterials
                    .Where(m => allowedMats2.Contains(MaterialKey(m)))
                    .GroupBy(MaterialKey).ToDictionary(g => g.Key, g => g.First())
                : new Dictionary<string, Material>();

            string FinalName(Dictionary<string, string>? renameMap, string original) =>
                renameMap != null && renameMap.TryGetValue(original, out var renamed) && !string.IsNullOrWhiteSpace(renamed)
                    ? renamed : original;

            HashSet<string> SelectedChannels(Dictionary<string, HashSet<string>>? channelMap, string original) =>
                channelMap != null && channelMap.TryGetValue(original, out var chs)
                    ? chs : new HashSet<string>(KnownMaterialChannelNames);

            var materialsByName1 = new Dictionary<string, MaterialBuilder>();
            var materialsByName2 = new Dictionary<string, MaterialBuilder>();

            // Materials sharing the same *output* name across the two models (e.g. renamed to the
            // same "Merged As" name) get combined into a single MaterialBuilder that carries the
            // union of whichever channels were selected on each side, instead of the two models'
            // materials being emitted as separate stacked geometry variants. Model 1 takes
            // priority on a channel selected by both sides.
            foreach (var (origName1, mat1) in rawMats1)
            {
                var finalName = FinalName(matRenameMap1, origName1);
                var collidingOrigName2 = rawMats2.Keys.FirstOrDefault(o2 => FinalName(matRenameMap2, o2) == finalName);

                if (collidingOrigName2 != null)
                {
                    var combined = BuildCombinedMaterial(finalName,
                        (mat1, SelectedChannels(matChannels1, origName1)),
                        (rawMats2[collidingOrigName2], SelectedChannels(matChannels2, collidingOrigName2)));
                    materialsByName1[origName1] = combined;
                    materialsByName2[collidingOrigName2] = combined;
                }
                else
                {
                    materialsByName1[origName1] = BuildMaterial(finalName, mat1, SelectedChannels(matChannels1, origName1));
                }
            }

            foreach (var (origName2, mat2) in rawMats2)
            {
                if (materialsByName2.ContainsKey(origName2)) continue; // already combined above
                materialsByName2[origName2] = BuildMaterial(FinalName(matRenameMap2, origName2), mat2, SelectedChannels(matChannels2, origName2));
            }

            var structuralMats = materialsByName1;
            var otherMats = materialsByName2;

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

            // Fallback for when name-based matching can't apply at all - e.g. a source file
            // whose mesh node was never named (some exporters, including glTF/GLB round-trips
            // through other tools, omit it entirely), or the two sides just happen to use
            // different naming conventions. If each model has exactly one mesh-bearing node,
            // there's no ambiguity about which one's material belongs to which - pair them
            // positionally instead of leaving the match to fail silently. Left null (no fallback)
            // whenever either side has more than one mesh node, since guessing pairing order
            // there risks applying the wrong texture to the wrong part.
            Node? singleMeshFallbackNode = null;
            if (otherModel != null)
            {
                var structuralMeshNodes = structuralModel.LogicalNodes.Where(n => n.Mesh != null).ToList();
                var otherMeshNodes = otherModel.LogicalNodes.Where(n => n.Mesh != null).ToList();
                if (structuralMeshNodes.Count == 1 && otherMeshNodes.Count == 1)
                    singleMeshFallbackNode = otherMeshNodes[0];
            }

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
                    structuralMats, otherMats,
                    otherNodesByName, anyMaterialSelected, fallbackMaterial,
                    outScene, nodeMap, firstMaterialOriginalName, geomRenameMap1,
                    singleMeshFallbackNode);

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

            // Per-joint bind translation of Model 2's own hierarchy - see ApplyBoneLengthCorrection
            // for why this is needed alongside targetReferenceTranslationsByName above.
            var sourceBindTranslationsByName = new Dictionary<string, Vector3>();
            if (model2 != null)
            {
                foreach (var node in otherNodesByName.Values)
                {
                    if (node.Name == null) continue;
                    Matrix4x4.Decompose(node.LocalMatrix, out _, out _, out var srcBindTranslation);
                    sourceBindTranslationsByName[node.Name] = srcBindTranslation;
                }
            }

            // Hips' own bind rotation on each side - see ApplyHipRotationCorrection for why only
            // this one joint gets a rotation correction.
            Quaternion? targetHipsBindRotation = null;
            var targetHipsNode = nodeMap.Keys.FirstOrDefault(n => n.Name == "Hips");
            if (targetHipsNode != null)
            {
                Matrix4x4.Decompose(targetHipsNode.LocalMatrix, out _, out var tgtHipsRot, out _);
                targetHipsBindRotation = tgtHipsRot;
            }

            // Read off the rig rather than hard-coded (it used to be a fixed
            // Spine02/LeftUpLeg/RightUpLeg list) - a rig naming its spine root anything else
            // silently skipped the cancellation ApplyHipRotationCorrection depends on.
            var targetHipsChildNames = targetHipsNode?.VisualChildren
                .Select(c => c.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray() ?? Array.Empty<string>();

            Quaternion? sourceHipsBindRotation = null;
            if (model2 != null && otherNodesByName.TryGetValue("Hips", out var srcHipsNode))
            {
                Matrix4x4.Decompose(srcHipsNode.LocalMatrix, out _, out var srcHipsRot, out _);
                sourceHipsBindRotation = srcHipsRot;
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
            if (model2 != null)
            {
                var nativeClips2 = ExtractGlbAnimationClips(model2);
                ApplyBoneLengthCorrection(
                    nativeClips2,
                    sourceBindTranslationsByName, targetReferenceTranslationsByName,
                    new HashSet<string>(fixBoneLengthAnims2 ?? new List<string>()));
                if (sourceHipsBindRotation.HasValue && targetHipsBindRotation.HasValue)
                    ApplyHipRotationCorrection(
                        nativeClips2,
                        sourceHipsBindRotation.Value, targetHipsBindRotation.Value,
                        targetHipsChildNames,
                        new HashSet<string>(fixHipRotationAnims2 ?? new List<string>()));
                clips2.AddRange(nativeClips2);
            }
            foreach (var fbxSource in fbxAnims2 ?? Enumerable.Empty<FbxAnimationSource>())
                clips2.AddRange(RetargetFbxClips(fbxSource, targetReferenceTranslationsByName));

            ApplyFrameTrim(clips1, frameTrimByName1);
            ApplyFrameTrim(clips2, frameTrimByName2);

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

        private static void ApplyFrameTrim(List<AnimationClipData> clips, Dictionary<string, (int Start, int End)>? trimByName)
        {
            if (trimByName == null || trimByName.Count == 0) return;

            foreach (var clip in clips)
                if (trimByName.TryGetValue(clip.Name, out var range))
                    TrimClipToFrameRange(clip, range.Start, range.End);
        }

        // Total count of distinct keyframe times across every channel of this clip - the same
        // "frame" numbering GlbInfoPanel's trim columns show and edit, computed the same way here
        // so the two always agree on what frame index N means for a given clip.
        public static int ComputeAnimationFrameCount(AnimationClipData clip) => CollectFrameTimes(clip).Count;

        // Crops `clip` in place down to just [startFrame, endFrame] (inclusive, by index into its
        // own distinct keyframe times), re-basing so the trimmed clip starts at time 0 - mirrors
        // TrimAnimation's logic below, but operates on the source-agnostic AnimationClipData
        // instead of an already-baked ModelRoot animation, so it works identically whether the
        // clip came from a GLB or an FBX retarget.
        public static void TrimClipToFrameRange(AnimationClipData clip, int startFrame, int endFrame)
        {
            var times = CollectFrameTimes(clip);
            if (times.Count < 2) return;

            startFrame = Math.Clamp(startFrame, 0, times.Count - 1);
            endFrame = Math.Clamp(endFrame, startFrame, times.Count - 1);
            float startTime = times[startFrame];
            float endTime = times[endFrame];

            foreach (var ch in clip.NodeChannels)
            {
                if (ch.Translation != null) ch.Translation = TrimKeyDict(ch.Translation, startTime, endTime);
                if (ch.Rotation != null) ch.Rotation = TrimKeyDict(ch.Rotation, startTime, endTime);
                if (ch.Scale != null) ch.Scale = TrimKeyDict(ch.Scale, startTime, endTime);
            }
        }

        private static List<float> CollectFrameTimes(AnimationClipData clip)
        {
            var times = new SortedSet<float>();
            foreach (var ch in clip.NodeChannels)
            {
                if (ch.Translation != null) foreach (var t in ch.Translation.Keys) times.Add(t);
                if (ch.Rotation != null) foreach (var t in ch.Rotation.Keys) times.Add(t);
                if (ch.Scale != null) foreach (var t in ch.Scale.Keys) times.Add(t);
            }
            return times.ToList();
        }

        private static Dictionary<float, T> TrimKeyDict<T>(Dictionary<float, T> keys, float startTime, float endTime)
        {
            var trimmed = keys.Where(k => k.Key >= startTime && k.Key <= endTime)
                .ToDictionary(k => k.Key - startTime, k => k.Value);

            if (trimmed.Count == 0)
            {
                var nearest = keys.OrderBy(k => Math.Abs(k.Key - startTime)).First();
                trimmed = new Dictionary<float, T> { [0f] = nearest.Value };
            }

            return trimmed;
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
            if (node.Mesh != null && node.Mesh.Primitives.Any(p => MaterialKeyOrNull(p.Material) == materialOriginalName))
                return true;

            if (node.Name != null && otherNodesByName.TryGetValue(node.Name, out var otherNode) && otherNode.Mesh != null
                && otherNode.Mesh.Primitives.Any(p => MaterialKeyOrNull(p.Material) == materialOriginalName))
                return true;

            return false;
        }

        private static void EmitNodeGeometry(
            Node srcNode, NodeBuilder nodeBuilder,
            IReadOnlyDictionary<string, MaterialBuilder> structuralMats,
            IReadOnlyDictionary<string, MaterialBuilder> otherMats,
            IReadOnlyDictionary<string, Node> otherNodesByName,
            bool anyMaterialSelected,
            MaterialBuilder fallbackMaterial,
            SceneBuilder outScene,
            Dictionary<Node, NodeBuilder> nodeMap,
            string? firstMaterialOriginalName,
            IReadOnlyDictionary<string, string>? geomRenameMap,
            Node? singleMeshFallbackNode = null)
        {
            if (srcNode.Mesh == null) return;

            Node? otherNode = null;
            if (srcNode.Name != null)
                otherNodesByName.TryGetValue(srcNode.Name, out otherNode);
            // Name-based lookup found nothing (either this node has no name, or the two files
            // just name it differently) - if there's no ambiguity about the pairing (exactly one
            // mesh node per side), use that instead of leaving this node without its "other"
            // material entirely.
            otherNode ??= singleMeshFallbackNode;

            var joints = srcNode.Skin != null ? ResolveJoints(srcNode.Skin, nodeMap) : null;

            var originalBaseName = srcNode.Mesh.Name ?? srcNode.Name ?? "mesh";
            var baseName = geomRenameMap != null && geomRenameMap.TryGetValue(originalBaseName, out var renamedBase) && !string.IsNullOrWhiteSpace(renamedBase)
                ? renamedBase
                : originalBaseName;
            bool multiPrim = srcNode.Mesh.Primitives.Count > 1;

            for (int primIdx = 0; primIdx < srcNode.Mesh.Primitives.Count; primIdx++)
            {
                var prim = srcNode.Mesh.Primitives[primIdx];
                var otherPrim = (otherNode?.Mesh != null && primIdx < otherNode.Mesh.Primitives.Count)
                    ? otherNode.Mesh.Primitives[primIdx] : null;

                // Pair each primitive with the correctly-corresponding material from each
                // selected source, instead of stamping every selected material (from either
                // model) onto every primitive.
                var variants = new List<(MaterialBuilder material, string? originalName)>();

                var structuralMatName = MaterialKeyOrNull(prim.Material);
                if (structuralMatName != null && structuralMats.TryGetValue(structuralMatName, out var structuralMb))
                    variants.Add((structuralMb, structuralMatName));

                var otherMatName = MaterialKeyOrNull(otherPrim?.Material);
                if (otherMatName != null && otherMats.TryGetValue(otherMatName, out var otherMb))
                    variants.Add((otherMb, otherMatName));

                if (variants.Count == 0)
                {
                    if (!anyMaterialSelected)
                        variants.Add((fallbackMaterial, null));
                    else
                        continue; // neither source's texture for this part was selected
                }

                // Both slots can point at the very same MaterialBuilder instance when their
                // materials were combined above (same output name after renaming) - collapse back
                // to one variant so that case doesn't emit the same geometry twice.
                variants = variants.GroupBy(v => v.material).Select(g => g.First()).ToList();

                // Whichever material the user marked "First" gets its primitive built before the
                // others here, so SharpGLTF registers (and therefore indexes) that MaterialBuilder
                // first in the output's material array - the comparison uses each variant's
                // *original* source material name (matched by dictionary key), since by this point
                // the MaterialBuilder's own .Name may already have been overwritten by a rename.
                if (firstMaterialOriginalName != null)
                    variants = variants.OrderBy(v => v.originalName == firstMaterialOriginalName ? 0 : 1).ToList();

                foreach (var (material, _) in variants)
                {
                    var variantName = baseName + (multiPrim ? $"_p{primIdx}" : "");

                    if (joints != null)
                    {
                        var skinnedMesh = BuildSkinnedPrimitive(prim, material, variantName);
                        if (skinnedMesh != null)
                        {
                            // AddSkinnedMesh positions vertices entirely from the joints array -
                            // it takes no target-node argument, so a skinned variant needs no
                            // extra NodeBuilder of its own. Earlier code created one anyway (as a
                            // dead, meshless placeholder purely named after the mesh) which did
                            // nothing but clutter the node list - it showed up alongside real
                            // bones in the "Fix Joint Orientation" dropdown, and since
                            // BuildNodeTree copies the *whole* hierarchy verbatim, re-merging an
                            // already-merged file copied those placeholders forward and let them
                            // pile up deeper on every pass.
                            outScene.AddSkinnedMesh(skinnedMesh, joints);
                            continue;
                        }
                        // Primitive had a skin on its node but no per-vertex joint data; fall
                        // through and merge it as a rigid (unskinned) piece instead.
                    }

                    // Only the rigid path needs its own NodeBuilder - AddRigidMesh anchors the
                    // mesh to whatever node it's given.
                    var childNode = new NodeBuilder(variantName);
                    nodeBuilder.AddNode(childNode);
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

        // Re-anchors a clip's translation channels from the source rig's own bone lengths onto
        // the target rig's - a bone's translation channel is usually just its (static,
        // every-frame-identical) bone length, not real motion, so copied verbatim onto a
        // differently-proportioned target rig it overwrites the target's own bone lengths,
        // visibly squishing/stretching limbs. Takes the delta from this clip's own bind
        // translation (usually zero, since most non-root channels are just a static bone length
        // repeated every frame) and re-anchors it onto the target's own bone length/resting
        // position, the same technique RetargetFbxClips already uses for Hips' root motion.
        // Deliberately rotation-only-untouched: this only ever writes Translation, never Rotation.
        //
        // The re-anchored delta is also SCALED by the ratio between the two rigs' leg lengths.
        // Hips is the one bone whose translation channel is real motion rather than a static bone
        // length, and copying that motion over at 1:1 while every limb below it gets rebuilt at
        // the target's proportions is self-inconsistent: on a rig with legs 86% as long, the
        // pelvis still travelled the source's full distance, so the legs could no longer reach
        // far enough to cancel it and the planted foot slid across the ground by the leftover
        // 14%. Scaling the pelvis' travel by the same factor the limbs were scaled by keeps the
        // clip internally consistent - a shorter character shifts its weight a proportionally
        // shorter distance, and the foot it is standing on stays put.
        private static void ApplyBoneLengthCorrection(
            List<AnimationClipData> clips,
            IReadOnlyDictionary<string, Vector3> sourceBindTranslationsByName,
            IReadOnlyDictionary<string, Vector3> targetBindTranslationsByName,
            HashSet<string> enabledClipNames)
        {
            var motionScale = RigScaleRatio(sourceBindTranslationsByName, targetBindTranslationsByName);

            foreach (var clip in clips)
            {
                if (!enabledClipNames.Contains(clip.Name)) continue;

                foreach (var ch in clip.NodeChannels)
                {
                    if (ch.Translation == null) continue;
                    if (!sourceBindTranslationsByName.TryGetValue(ch.NodeName, out var srcBindPos)) continue;
                    if (!targetBindTranslationsByName.TryGetValue(ch.NodeName, out var tgtBindPos)) continue;

                    ch.Translation = ch.Translation.ToDictionary(
                        k => k.Key,
                        k => tgtBindPos + motionScale * (k.Value - srcBindPos));
                }
            }
        }

        // How much smaller/larger the target rig is than the source, measured down the leg chain
        // (hip attachment + thigh + shin) - the chain that actually determines how far a foot can
        // stay planted while the pelvis moves, which is what this ratio is used to keep honest.
        // Averaged over both legs, and falling back to standing hip height, then to 1 (no
        // rescale) if a rig doesn't use these joint names at all.
        private static float RigScaleRatio(
            IReadOnlyDictionary<string, Vector3> sourceBindTranslationsByName,
            IReadOnlyDictionary<string, Vector3> targetBindTranslationsByName)
        {
            float ChainLength(IReadOnlyDictionary<string, Vector3> binds)
            {
                float total = 0f;
                int counted = 0;
                foreach (var (upLeg, leg, foot) in LegChains)
                {
                    if (!binds.TryGetValue(upLeg, out var u) || !binds.TryGetValue(leg, out var l) || !binds.TryGetValue(foot, out var f))
                        continue;
                    total += u.Length() + l.Length() + f.Length();
                    counted++;
                }
                if (counted > 0) return total / counted;
                return binds.TryGetValue("Hips", out var hips) ? MathF.Abs(hips.Y) : 0f;
            }

            var source = ChainLength(sourceBindTranslationsByName);
            var target = ChainLength(targetBindTranslationsByName);
            return source > 1e-4f && target > 1e-4f ? target / source : 1f;
        }

        // Corrects the Hips joint's rotation so the pelvis deforms on the target rig exactly the
        // way it does on the source rig. Deliberately narrow: this app previously tried applying
        // an equivalent correction to every joint (and later a more sophisticated world-space
        // chain solve), and both made limb rotations worse, not better - copied raw, legs and
        // arms already land close to the target rig's own convention. Hips is the exception: as
        // the root, its own bind-rotation mismatch between two independently authored rigs (a
        // coordinate-convention/lean difference, not a "different stance" the way a leg's can be)
        // propagates into how the whole spine/leg attachment reads.
        //
        // The correction is POST-multiplied: hipsRaw * (inverse(sourceBind) * targetBind). That
        // reads as "strip the source rig's bind pose back out of the animated rotation, leaving
        // the clip's own delta-from-rest, then re-apply that delta on top of the target rig's
        // bind pose" - which is what retargeting one rig's pose onto another actually means, and
        // it makes the pelvis's skinning transform match the source's identically on every frame.
        //
        // It used to be PRE-multiplied instead (targetBind * inverse(sourceBind) * hipsRaw). Both
        // forms agree exactly at the bind pose, which is why that was easy to miss, but off the
        // bind pose the pre-multiplied form applies the bind difference in the wrong frame and
        // the error grows with how far Hips has rotated: small on an idle, ~130 degrees at the
        // extremes of a tackle.
        //
        // Correcting Hips alone drags its whole subtree along with it: a child's world transform
        // is parentWorld * childLocal, so every direct child of Hips (the spine root and both
        // UpLegs) inherits the correction on top of its own raw local rotation, which was
        // authored against Hips' *uncorrected* rotation. Each of those children therefore gets
        // the correction's exact inverse folded into its own local rotation, so the whole body
        // below Hips keeps the world orientation an uncorrected copy would have given it. Because
        // the correction is a constant applied on Hips' right, its inverse is the same constant
        // applied on the children's left, and the two cancel exactly on every frame. (The old
        // pre-multiplied form had no such exact cancellation available: the constant
        // inverse(delta) it used left a residual that scaled with Hips' rotation, so the legs and
        // torso swung *with* the hips - the feet slid along under the pelvis and the model
        // appeared to pivot around its hips instead of staying planted on the ground.)
        //
        // Only the children's ROTATION is cancelled, never their translation, even though that
        // translation is a bone offset expressed in Hips' local frame and so does get swung
        // around by the correction. That swing is wanted: Fix Bone Length has already replaced
        // those offsets with the *target* rig's own bind offsets, which are authored in the
        // target's Hips frame - the very frame the corrected Hips now supplies. Left uncorrected,
        // Hips holds the source rig's frame and the target's hip/spine attachment offsets get
        // read in the wrong basis, which planted the two legs at different heights and floated
        // the model off the ground. The two corrections are only geometrically consistent with
        // each other when both are on.
        //
        // This used to additionally pin Hips' YAW to the target's resting facing direction every
        // frame (a swing-twist split around world Y), to mop up the facing drift the correction
        // appeared to introduce. That drift was really the leaked residual described above, and
        // the pin was a bigger cause of the pivot-around-the-hips problem in its own right:
        // replacing the clip's own per-frame yaw with a constant turns the whole body under a
        // pelvis held at a fixed facing, so planted feet get swept around the hip joint. With the
        // cancellation exact, no yaw reaches the body from this correction and there is nothing
        // left for a yaw lock to mop up. Lock In Place (which pins translation, not rotation) and
        // the manual Y Rotation control are unaffected.
        private static void ApplyHipRotationCorrection(
            List<AnimationClipData> clips,
            Quaternion sourceHipsBindRotation,
            Quaternion targetHipsBindRotation,
            IReadOnlyCollection<string> hipsChildBoneNames,
            HashSet<string> enabledClipNames)
        {
            var bindDelta = Quaternion.Normalize(Quaternion.Multiply(
                Quaternion.Inverse(sourceHipsBindRotation), targetHipsBindRotation));
            var inverseBindDelta = Quaternion.Normalize(Quaternion.Inverse(bindDelta));

            foreach (var clip in clips)
            {
                if (!enabledClipNames.Contains(clip.Name)) continue;

                var hipsChannel = clip.NodeChannels.FirstOrDefault(c => c.NodeName == "Hips");
                if (hipsChannel?.Rotation == null) continue;

                hipsChannel.Rotation = hipsChannel.Rotation.ToDictionary(
                    k => k.Key,
                    k => Quaternion.Normalize(Quaternion.Multiply(k.Value, bindDelta)));

                foreach (var childBoneName in hipsChildBoneNames)
                {
                    var childChannel = clip.NodeChannels.FirstOrDefault(c => c.NodeName == childBoneName);
                    if (childChannel?.Rotation == null) continue;

                    childChannel.Rotation = childChannel.Rotation.ToDictionary(
                        k => k.Key,
                        k => Quaternion.Normalize(Quaternion.Multiply(inverseBindDelta, k.Value)));
                }
            }
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

        // Crops an existing animation in-place (on the already-loaded model) down to just the
        // frame range [startFrame, endFrame], re-basing so the trimmed clip starts at time 0 -
        // matching the correction pattern used by JointOrientationEditor (Node.WithXAnimation
        // replaces a node's channel data for a given animation outright, rather than merging).
        // "Frame" here means one of the distinct keyframe times actually present across the
        // clip's channels, not a fixed sample rate - that's the only frame numbering the caller
        // (the Animation Trim editor's frame sliders) can offer without guessing an FPS the source file
        // never stored.
        public static void TrimAnimation(ModelRoot model, string animationName, int startFrame, int endFrame)
        {
            var anim = model.LogicalAnimations.FirstOrDefault(a => (a.Name ?? $"Anim_{a.LogicalIndex}") == animationName)
                ?? throw new InvalidOperationException($"Animation '{animationName}' not found.");

            var channels = anim.Channels.ToList();

            var frameTimes = new SortedSet<float>();
            foreach (var ch in channels)
            {
                if (ch.TargetNodePath == PropertyPath.translation)
                    foreach (var k in ch.GetTranslationSampler().GetLinearKeys()) frameTimes.Add(k.Key);
                else if (ch.TargetNodePath == PropertyPath.rotation)
                    foreach (var k in ch.GetRotationSampler().GetLinearKeys()) frameTimes.Add(k.Key);
                else if (ch.TargetNodePath == PropertyPath.scale)
                    foreach (var k in ch.GetScaleSampler().GetLinearKeys()) frameTimes.Add(k.Key);
            }
            var times = frameTimes.ToList();
            if (times.Count < 2) return;

            // Clamped to leave room for endFrame > startFrame below without the two bounds ever
            // crossing (which Math.Clamp throws on) - e.g. a caller passing startFrame ==
            // endFrame == times.Count - 1.
            startFrame = Math.Clamp(startFrame, 0, times.Count - 2);
            endFrame = Math.Clamp(endFrame, startFrame + 1, times.Count - 1);
            float startTime = times[startFrame];
            float endTime = times[endFrame];

            foreach (var ch in channels)
            {
                var node = ch.TargetNode;
                if (node == null) continue;

                if (ch.TargetNodePath == PropertyPath.translation)
                    node.WithTranslationAnimation(animationName, TrimKeys(ch.GetTranslationSampler().GetLinearKeys(), startTime, endTime));
                else if (ch.TargetNodePath == PropertyPath.rotation)
                    node.WithRotationAnimation(animationName, TrimKeys(ch.GetRotationSampler().GetLinearKeys(), startTime, endTime));
                else if (ch.TargetNodePath == PropertyPath.scale)
                    node.WithScaleAnimation(animationName, TrimKeys(ch.GetScaleSampler().GetLinearKeys(), startTime, endTime));
            }
        }

        // Keeps only the keys within [startTime, endTime] and shifts them so the earliest kept
        // key lands at time 0. Falls back to a single flat key (whatever was closest to
        // startTime) if the window happens to fall between two keys with none inside it, so the
        // channel is never left completely empty.
        private static (float Time, T Value)[] TrimKeys<T>(IEnumerable<(float Key, T Value)> keys, float startTime, float endTime)
        {
            var all = keys.ToList();
            var trimmed = all.Where(k => k.Key >= startTime && k.Key <= endTime)
                .OrderBy(k => k.Key)
                .Select(k => (k.Key - startTime, k.Value))
                .ToArray();

            if (trimmed.Length == 0)
            {
                var nearest = all.OrderBy(k => Math.Abs(k.Key - startTime)).First();
                trimmed = new[] { (0f, nearest.Value) };
            }

            return trimmed;
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

        // The single source of truth for "what do we call this material". glTF material names are
        // optional (Meshy/pygltflib exports leave them off entirely), so anything that identifies a
        // material by name has to agree on the same synthesized fallback - the info panel that
        // lists materials for the user to tick, the extraction that keys the lookup dictionary, and
        // the primitive-to-material matching during emission. When these disagreed, an unnamed
        // material could never match the user's selection, every primitive fell through to
        // Default_Opaque, and the merged model silently lost all of its textures.
        public static string MaterialKey(Material mat) => mat.Name ?? $"Material_{mat.LogicalIndex}";

        // Null-tolerant overload: a primitive with no material at all has no key to match on.
        private static string? MaterialKeyOrNull(Material? mat) => mat == null ? null : MaterialKey(mat);

        // The channel names shown/checked in the UI's material grid (GlbInfoPanel) and matched
        // against here - a single source of truth so the two never drift apart the way
        // MaterialKey's fallback naming used to.
        public static readonly string[] KnownMaterialChannelNames =
            { "BaseColor", "MetallicRoughness", "Normal", "Occlusion", "Emissive" };

        private static readonly Dictionary<string, (KnownChannel Known, KnownProperty[] ScalarProps)> ChannelInfo = new()
        {
            ["BaseColor"] = (KnownChannel.BaseColor, new[] { KnownProperty.RGBA }),
            ["MetallicRoughness"] = (KnownChannel.MetallicRoughness, new[] { KnownProperty.MetallicFactor, KnownProperty.RoughnessFactor }),
            ["Normal"] = (KnownChannel.Normal, new[] { KnownProperty.NormalScale }),
            ["Occlusion"] = (KnownChannel.Occlusion, new[] { KnownProperty.OcclusionStrength }),
            ["Emissive"] = (KnownChannel.Emissive, new[] { KnownProperty.RGB }),
        };

        // Builds an output material from a single source, carrying over only the channels the
        // user selected for it.
        private static MaterialBuilder BuildMaterial(string outputName, Material src, HashSet<string> selectedChannels)
        {
            var mb = new MaterialBuilder(outputName);
            ApplyBaseProperties(src, mb);

            foreach (var channelName in KnownMaterialChannelNames)
                if (selectedChannels.Contains(channelName))
                    ApplyChannel(src, mb, channelName, ChannelInfo[channelName].Known, ChannelInfo[channelName].ScalarProps);

            return mb;
        }

        // Builds one output material out of several sources that ended up sharing the same output
        // name (e.g. renamed to the same "Merged As" name in both models) - each source
        // contributes whichever of its own selected channels haven't already been claimed by an
        // earlier (higher-priority) source, so a channel picked on both sides is taken from
        // whichever source comes first in sourcesInPriorityOrder rather than being blended.
        private static MaterialBuilder BuildCombinedMaterial(string outputName, params (Material Source, HashSet<string> SelectedChannels)[] sourcesInPriorityOrder)
        {
            var mb = new MaterialBuilder(outputName);
            ApplyBaseProperties(sourcesInPriorityOrder[0].Source, mb);

            var applied = new HashSet<string>();
            foreach (var (source, selected) in sourcesInPriorityOrder)
                foreach (var channelName in KnownMaterialChannelNames)
                {
                    if (applied.Contains(channelName) || !selected.Contains(channelName)) continue;
                    if (ApplyChannel(source, mb, channelName, ChannelInfo[channelName].Known, ChannelInfo[channelName].ScalarProps))
                        applied.Add(channelName);
                }

            return mb;
        }

        private static void ApplyBaseProperties(Material src, MaterialBuilder mb)
        {
            mb.WithAlpha(src.Alpha switch
            {
                SchemaAlphaMode.BLEND => SharpGLTF.Materials.AlphaMode.BLEND,
                SchemaAlphaMode.MASK => SharpGLTF.Materials.AlphaMode.MASK,
                _ => SharpGLTF.Materials.AlphaMode.OPAQUE
            }, src.AlphaCutoff);

            mb.WithDoubleSide(src.DoubleSided);
        }

        // Returns whether it actually wrote anything to mb, so BuildCombinedMaterial can tell a
        // channel that's selected-but-genuinely-absent-on-this-source apart from one it applied,
        // and let a lower-priority source fill it in instead.
        private static bool ApplyChannel(Material mat, MaterialBuilder mb, string channelName, KnownChannel known, params KnownProperty[] scalarProps)
        {
            var ch = mat.FindChannel(channelName);
            if (!ch.HasValue) return false;

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
            if (factorRequiresTexture && !hasTexture) return false;

            bool appliedAny = hasTexture;
            foreach (var prop in scalarProps)
            {
                try
                {
                    var v = ch.Value.GetFactor(prop.ToString());
                    mb.UseChannel(known).Parameters[prop] = v;
                    appliedAny = true;
                }
                catch { /* property not present on this channel */ }
            }

            return appliedAny;
        }
    }
}
