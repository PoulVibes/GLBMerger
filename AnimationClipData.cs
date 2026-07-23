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
}
