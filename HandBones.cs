using System;
using System.Linq;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Resolves a rig's hand/wrist bone by name for the editors that hang
    // nodes off it (Ball Anchor, Stiff Arm Pose, Handoff viewer).
    //
    // Same alias lists ModelRenderer::FindHandJoints (ArmReach::FindHandNode)
    // uses on the C++ side, but a plain "first name containing any alias"
    // match breaks on rigs with finger bones: "LeftHandRing1" contains
    // "handr", so Eli's RIGHT hand resolved to a left finger. Finger bones
    // and names that also match the opposite side are skipped, and an exact
    // name match (ignoring a "mixamorig:"-style prefix) wins over a
    // substring one.
    public static class HandBones
    {
        private static readonly string[] RightNames = { "righthand", "hand_r", "r_hand", "hand.r",
            "wrist_r", "mixamorig:righthand", "handr", "rwrist" };
        private static readonly string[] LeftNames = { "lefthand", "hand_l", "l_hand", "hand.l",
            "wrist_l", "mixamorig:lefthand", "handl", "lwrist" };
        private static readonly string[] WeakNames = { "hand", "wrist", "palm" };
        private static readonly string[] FingerNames = { "thumb", "index", "middle", "ring", "pinky", "finger" };

        public static Node? Find(ModelRoot model, bool right)
        {
            var own = right ? RightNames : LeftNames;
            var other = right ? LeftNames : RightNames;

            bool Eligible(string lower) =>
                !FingerNames.Any(lower.Contains) && !other.Any(lower.Contains);

            Node? Search(string[] names, bool exact)
            {
                foreach (var n in model.LogicalNodes)
                {
                    if (string.IsNullOrEmpty(n.Name)) continue;
                    var lower = n.Name.ToLowerInvariant();
                    if (!Eligible(lower)) continue;
                    var bare = lower[(lower.LastIndexOf(':') + 1)..];
                    foreach (var key in names)
                        if (exact ? (bare == key || lower == key) : lower.Contains(key)) return n;
                }
                return null;
            }

            var found = Search(own, exact: true) ?? Search(own, exact: false);
            return right ? found ?? Search(WeakNames, exact: false) : found;
        }
    }
}
