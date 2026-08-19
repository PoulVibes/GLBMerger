using System.Collections.Generic;
using System.Numerics;

namespace GlbMerger
{
    // Source-agnostic animation clip: built either from a glTF ModelRoot or directly from an
    // Assimp-imported FBX scene, then applied onto the merged output's NodeBuilders the same way.
    public sealed class AnimationClipData
    {
        public required string Name { get; init; }
        public List<NodeChannelData> NodeChannels { get; } = new();
    }

    public sealed class NodeChannelData
    {
        public required string NodeName { get; init; }
        public Dictionary<float, Vector3>? Translation { get; set; }
        public Dictionary<float, Quaternion>? Rotation { get; set; }
        public Dictionary<float, Vector3>? Scale { get; set; }
    }

    // Bundles an FBX's raw (uncorrected) animation clips together with the root bone's
    // parent-frame axis correction (see RootTranslationCorrection).
    public sealed class FbxAnimationSource
    {
        public required List<AnimationClipData> Clips { get; init; }

        // The root bone (e.g. Hips) is authored in its parent (Armature) node's local space,
        // which often carries a fixed axis-convention rotation baked in by the DCC tool that
        // produced the FBX (e.g. Z-up -> Y-up, a 90 degree rotation about X). Without correcting
        // for this, the root bone's translation axes end up permuted/flipped and its rotation
        // ends up tilted relative to the target rig's convention.
        public Quaternion? RootTranslationCorrection { get; init; }
    }

    // Bundles a library GLB's raw (uncorrected) animation clips together with the bind-pose data
    // (per-bone bind translation, Hips bind rotation) needed to retarget them onto a different
    // structural model later - the same correction ApplyBoneLengthCorrection/
    // ApplyHipRotationCorrection already apply to slot 2's own dropped GLB, generalized to any
    // number of extra "animation library" GLBs a user adds via the dropdown.
    public sealed class GlbAnimationSource
    {
        public required List<AnimationClipData> Clips { get; init; }
        public required Dictionary<string, Vector3> BindTranslationsByName { get; init; }
        public Quaternion? HipsBindRotation { get; init; }

        // Whatever loop/pause setting (and seconds-offset to resume at) this library GLB's own
        // clips already carry in their glTF extras, keyed by each clip's *original* name (matching
        // Clips[i].Name before the caller disambiguates it against already-loaded clips) - lets the
        // "animation library" dropdown seed GlbInfoPanel's Loop checkbox/frame the same way a
        // directly-dropped GLB does, instead of always resetting to the row default.
        public Dictionary<string, (bool Loop, float LoopTime)>? LoopByClipName { get; init; }
    }
}
