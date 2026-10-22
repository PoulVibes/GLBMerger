using System;
using System.Collections.Generic;
using System.Linq;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Copies whole animation tracks from an older export of the same model back onto the current
    // one - for undoing a merge that cropped clips by mistake (GlbInfoPanel's grid once carried a
    // stale Last Frame across a rebuild, see RebuildAnimationGrid) without redoing every later edit
    // to the file (face bones, eye rig, textures). Nodes are matched by name, not index, so a
    // target that has since gained nodes still lines up. Each restored channel replaces the
    // target's channel outright via Node.WithXAnimation - the same replace-in-place path
    // TrimAnimation uses - so the animation object, and the loop extras on it, are kept.
    public static class AnimationRestore
    {
        public sealed record Result(string Clip, float OldDuration, float NewDuration, int Channels, List<string> Unmatched);

        public static List<Result> Restore(ModelRoot target, ModelRoot source, IEnumerable<string> clipNames)
        {
            var targetNodes = target.LogicalNodes
                .Where(n => n.Name != null)
                .GroupBy(n => n.Name!)
                .ToDictionary(g => g.Key, g => g.First());

            var results = new List<Result>();
            foreach (var clip in clipNames)
                results.Add(RestoreClip(target, source, clip, targetNodes));
            return results;
        }

        private static Result RestoreClip(ModelRoot target, ModelRoot source, string clip,
                                          IReadOnlyDictionary<string, Node> targetNodes)
        {
            var srcAnim = FindAnimation(source, clip)
                ?? throw new InvalidOperationException($"Source has no animation '{clip}'.");
            var dstAnim = FindAnimation(target, clip)
                ?? throw new InvalidOperationException($"Target has no animation '{clip}'.");

            float oldDuration = dstAnim.Duration;
            int copied = 0;
            var unmatched = new List<string>();

            foreach (var ch in srcAnim.Channels)
            {
                var name = ch.TargetNode?.Name;
                if (name == null || !targetNodes.TryGetValue(name, out var node))
                {
                    unmatched.Add(name ?? "(unnamed)");
                    continue;
                }

                switch (ch.TargetNodePath)
                {
                    case PropertyPath.translation:
                        node.WithTranslationAnimation(clip, Keys(ch.GetTranslationSampler().GetLinearKeys()));
                        break;
                    case PropertyPath.rotation:
                        node.WithRotationAnimation(clip, Keys(ch.GetRotationSampler().GetLinearKeys()));
                        break;
                    case PropertyPath.scale:
                        // Relative to each side's rest scale, so a branch Size set since that
                        // export survives the restore (see ScaleKeySync).
                        node.WithScaleAnimation(clip, ScaleKeySync.ToTargetRest(
                            Keys(ch.GetScaleSampler().GetLinearKeys()), RestScale(ch.TargetNode!), RestScale(node)));
                        break;
                    default:
                        continue;   // morph weights: no player clip animates them
                }
                copied++;
            }

            return new Result(clip, oldDuration, FindAnimation(target, clip)!.Duration, copied, unmatched);
        }

        private static Animation? FindAnimation(ModelRoot model, string name) =>
            model.LogicalAnimations.FirstOrDefault(a => (a.Name ?? $"Anim_{a.LogicalIndex}") == name);

        private static System.Numerics.Vector3 RestScale(Node node)
        {
            System.Numerics.Matrix4x4.Decompose(node.LocalMatrix, out var scale, out _, out _);
            return scale;
        }

        private static (float, T)[] Keys<T>(IEnumerable<(float Key, T Value)> keys) =>
            keys.OrderBy(k => k.Key).Select(k => (k.Key, k.Value)).ToArray();
    }
}
