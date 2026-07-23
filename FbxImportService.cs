using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using SharpAssimp;

namespace GlbMerger
{
    public static class FbxImportService
    {
        // Only the animation clips are wanted out of an FBX (meshes/materials are discarded), so
        // this skips Assimp's mesh/skin triangulation and glTF export entirely - that combo is
        // what was hanging on real-world rigged/animated FBX exports. A plain import with no
        // post-processing is enough to read keyframe data straight off the Assimp scene.
        public static FbxAnimationSource ExtractAnimationClips(string fbxPath)
        {
            using var context = new AssimpContext();
            var scene = context.ImportFile(fbxPath, PostProcessSteps.None);

            var clips = new List<AnimationClipData>();

            for (int i = 0; i < scene.Animations.Count; i++)
            {
                var anim = scene.Animations[i];
                double ticksPerSecond = anim.TicksPerSecond != 0 ? anim.TicksPerSecond : 25.0;

                var clip = new AnimationClipData { Name = string.IsNullOrEmpty(anim.Name) ? $"Anim_{i}" : anim.Name };

                foreach (var ch in anim.NodeAnimationChannels)
                {
                    var nodeChannel = new NodeChannelData { NodeName = ch.NodeName };

                    // Built via an explicit loop with indexer assignment rather than ToDictionary:
                    // two distinct tick times can round to the exact same float once divided by
                    // ticksPerSecond (common with high frame-rate exports), and ToDictionary throws
                    // on a duplicate key instead of just keeping one - the values at that point are
                    // essentially identical anyway, so silently keeping the last one is correct.
                    if (ch.HasPositionKeys)
                    {
                        var translation = new Dictionary<float, Vector3>();
                        foreach (var k in ch.PositionKeys) translation[(float)(k.Time / ticksPerSecond)] = k.Value;
                        nodeChannel.Translation = translation;
                    }

                    if (ch.HasRotationKeys)
                    {
                        var rotation = new Dictionary<float, Quaternion>();
                        foreach (var k in ch.RotationKeys) rotation[(float)(k.Time / ticksPerSecond)] = Quaternion.Normalize(k.Value);
                        nodeChannel.Rotation = rotation;
                    }

                    if (ch.HasScalingKeys)
                    {
                        var scale = new Dictionary<float, Vector3>();
                        foreach (var k in ch.ScalingKeys) scale[(float)(k.Time / ticksPerSecond)] = k.Value;
                        nodeChannel.Scale = scale;
                    }

                    clip.NodeChannels.Add(nodeChannel);
                }

                clips.Add(clip);
            }

            // The root bone (Hips) is animated relative to its parent (typically an "Armature"
            // node), which frequently carries a fixed axis-convention rotation from the DCC tool
            // that authored the FBX (e.g. Z-up -> Y-up). Accumulate that ancestor chain's rotation
            // so root-motion translation and rotation can be corrected into the same convention
            // the target rig uses, instead of using raw values axis-for-axis.
            Quaternion? rootTranslationCorrection = null;
            var hipsNode = scene.RootNode?.FindNode("Hips");
            if (hipsNode?.Parent != null)
            {
                Matrix4x4.Decompose(GetWorldTransform(hipsNode.Parent), out _, out var parentRotation, out _);
                rootTranslationCorrection = Quaternion.Normalize(parentRotation);
            }

            return new FbxAnimationSource { Clips = clips, RootTranslationCorrection = rootTranslationCorrection };
        }

        private static Matrix4x4 GetWorldTransform(Node node) =>
            node.Parent == null ? node.Transform : node.Transform * GetWorldTransform(node.Parent);
    }
}
