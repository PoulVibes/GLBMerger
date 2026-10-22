using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Keeps animation scale tracks in step with the rest-pose node scale, which is where the
    // Model Adjuster's branch Size lives (ModelAdjusterEditor.ApplyGroupSizes). glTF lets a clip's
    // scale track override the node's own scale outright, so a clip whose track still carries
    // another rig's (or an older export's) value plays that limb at the wrong size: the game, like
    // any glTF player, honours the track - only this app's preview forces the rest size back on.
    //
    // Two uses:
    //   - ToTargetRest: clips coming from another file (merge slot 2, library GLBs, FBX donors,
    //     AnimationRestore) are re-expressed relative to the target's rest scale, so the size set
    //     on the target survives the merge while any real scale animation keeps its shape.
    //   - Sync: repairs a saved model whose tracks already disagree with its rest scale.
    public static class ScaleKeySync
    {
        private const float Eps = 1e-3f;

        public static Dictionary<string, Vector3> RestScales(IEnumerable<Node> nodes)
        {
            var result = new Dictionary<string, Vector3>(StringComparer.Ordinal);
            foreach (var node in nodes)
            {
                if (node.Name == null || result.ContainsKey(node.Name)) continue;
                Matrix4x4.Decompose(node.LocalMatrix, out var scale, out _, out _);
                result[node.Name] = scale;
            }
            return result;
        }

        // Rescales every scale track by targetRest / sourceRest for its node. With no known
        // source rest scale (an FBX donor), the track's own first key stands in for it - a
        // constant track then lands exactly on the target's rest scale.
        public static void ToTargetRest(IEnumerable<AnimationClipData> clips,
                                        IReadOnlyDictionary<string, Vector3>? sourceRest,
                                        IReadOnlyDictionary<string, Vector3> targetRest)
        {
            foreach (var ch in clips.SelectMany(c => c.NodeChannels))
            {
                if (ch.Scale == null || ch.Scale.Count == 0) continue;
                if (!targetRest.TryGetValue(ch.NodeName, out var target)) continue;
                var source = sourceRest != null && sourceRest.TryGetValue(ch.NodeName, out var s)
                    ? s : ch.Scale.OrderBy(k => k.Key).First().Value;
                var ratio = SafeDivide(target, source);
                if (Vector3.Distance(ratio, Vector3.One) < 1e-6f) continue;
                ch.Scale = ch.Scale.ToDictionary(k => k.Key, k => k.Value * ratio);
            }
        }

        // Same rescale for raw keys about to be written onto `node` (AnimationRestore).
        public static (float, Vector3)[] ToTargetRest((float, Vector3)[] keys, Vector3 sourceRest, Vector3 targetRest)
        {
            var ratio = SafeDivide(targetRest, sourceRest);
            return keys.Select(k => (k.Item1, k.Item2 * ratio)).ToArray();
        }

        // Sync's rule for clips already relative to the target (merge slot 1's own clips): a
        // constant track off a uniform rest scale is set to it.
        public static void SyncToRest(IEnumerable<AnimationClipData> clips, IReadOnlyDictionary<string, Vector3> targetRest)
        {
            foreach (var ch in clips.SelectMany(c => c.NodeChannels))
            {
                if (ch.Scale == null || ch.Scale.Count == 0) continue;
                if (!targetRest.TryGetValue(ch.NodeName, out var rest) || !IsUniform(rest)) continue;
                var first = ch.Scale.Values.First();
                if (Near(first, rest) || ch.Scale.Values.Any(v => !Near(v, first))) continue;
                ch.Scale = ch.Scale.ToDictionary(k => k.Key, _ => rest);
            }
        }

        public sealed record Fix(string Clip, string Node, Vector3 From, Vector3 To);

        // Rewrites every CONSTANT scale track that disagrees with its node's rest scale to that
        // rest scale (key times kept). Only uniform rest scales qualify - the Size tool only ever
        // writes uniform ones, and a non-uniform rest (a rig normaliser's residue) is not a size
        // anyone set. A track that genuinely animates scale is left alone and reported via
        // `skipped`, since its intended shape can't be recovered from the rest value alone.
        public static List<Fix> Sync(ModelRoot model, List<string>? skipped = null)
        {
            var fixes = new List<Fix>();
            foreach (var anim in model.LogicalAnimations)
            {
                var clip = anim.Name ?? $"Anim_{anim.LogicalIndex}";
                foreach (var ch in anim.Channels.Where(c => c.TargetNodePath == PropertyPath.scale).ToList())
                {
                    var node = ch.TargetNode;
                    if (node?.Name == null) continue;
                    Matrix4x4.Decompose(node.LocalMatrix, out var rest, out _, out _);
                    if (!IsUniform(rest)) continue;

                    var keys = ch.GetScaleSampler().GetLinearKeys().OrderBy(k => k.Key).ToArray();
                    if (keys.Length == 0) continue;
                    var first = keys[0].Value;
                    if (Near(first, rest)) continue;
                    if (keys.Any(k => !Near(k.Value, first)))
                    {
                        skipped?.Add($"{clip}/{node.Name}");
                        continue;
                    }

                    node.WithScaleAnimation(clip, keys.Select(k => (k.Key, rest)).ToArray());
                    fixes.Add(new Fix(clip, node.Name, first, rest));
                }
            }
            return fixes;
        }

        private static bool IsUniform(Vector3 v) =>
            MathF.Abs(v.X - v.Y) < Eps && MathF.Abs(v.Y - v.Z) < Eps;

        private static bool Near(Vector3 a, Vector3 b) =>
            MathF.Abs(a.X - b.X) < Eps && MathF.Abs(a.Y - b.Y) < Eps && MathF.Abs(a.Z - b.Z) < Eps;

        private static Vector3 SafeDivide(Vector3 a, Vector3 b) => new(
            MathF.Abs(b.X) > 1e-8f ? a.X / b.X : 1f,
            MathF.Abs(b.Y) > 1e-8f ? a.Y / b.Y : 1f,
            MathF.Abs(b.Z) > 1e-8f ? a.Z / b.Z : 1f);
    }
}
