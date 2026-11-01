using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // "Plant Feet": stops a retargeted clip's feet sliding while they're on the ground.
    //
    // Why a retargeted foot slides at all: the merge copies the donor's joint ROTATIONS onto the
    // target, and Fix Bone Length rebuilds the limbs at the target's own lengths, scaling the
    // pelvis' travel by the ratio of the two rigs' whole leg lengths. That keeps a foot planted
    // only when the two legs differ in overall size. Two independently authored rigs differ in
    // shape too - Billy -> Eli (measured 2026-10-31): Eli's thigh is 0.34 hip heights to Billy's
    // 0.40, the shin 0.38 to 0.34, the hip attachment 0.11 to 0.16 - so the same hip and knee
    // angles put Eli's ankle somewhere slightly different, and by a different amount on every
    // frame as the pelvis sways. Eli's grounded feet drifted ~7 cm/s in Billy's Idle against
    // Billy's own ~2. No constant correction can cancel an error that changes with the pose.
    //
    // The fix is the usual one: find when each foot is planted, pin it to one spot for that
    // window, and solve the leg's two-bone IK (thigh + shin) on every frame so the ankle lands on
    // that spot. Contacts are read off the DONOR'S own motion on the donor's own rig when it is
    // known - that is where the foot genuinely stood still - and off the target's motion
    // otherwise (FBX donors, a slot's own clips). Only UpLeg/Leg/Foot rotations change: Hips,
    // spine and arms keep exactly what the other corrections gave them. The foot's world
    // orientation is kept as the clip had it, so the sole stays as flat as it was.
    public static class FootPlanting
    {
        // The toe is tracked as well as the ankle: in Billy's Idle a foot spends about a fifth of
        // its ground time rolled onto the ball (heel up, toe planted), and pinning only the
        // ankle left those phases sliding.
        private static readonly (string UpLeg, string Leg, string Foot, string Toe)[] Legs =
        {
            ("LeftUpLeg", "LeftLeg", "LeftFoot", "LeftToeBase"),
            ("RightUpLeg", "RightLeg", "RightFoot", "RightToeBase"),
        };

        // A foot counts as planted while its ankle is within this share of the leg's length of
        // the clip's lowest ankle height...
        private const float ContactHeightShare = 0.06f;
        // ...and moving sideways slower than this share of the leg's length per second. A
        // stepping foot moves well over 50%/s. Loose enough to also catch a foot that already
        // slides (an earlier merge's clip re-processed on its own rig, ~10%/s on Eli); at 0.12
        // those contacts were missed and re-processing Eli's Idle barely helped.
        private const float ContactSpeedShare = 0.25f;
        // Gaps shorter than this inside a contact are noise, and contacts shorter than this are a
        // foot passing through, not standing.
        private const float MinGapSeconds = 0.1f;
        private const float MinContactSeconds = 0.12f;
        // How long the IK takes to fade in before a contact and out after it, so the foot doesn't
        // pop onto or off its pinned spot.
        private const float BlendSeconds = 0.2f;

        // The rest pose and hierarchy of one rig, by bone name - enough to run its legs' forward
        // kinematics from Hips down for any clip authored against it.
        public sealed class Rig
        {
            public required Dictionary<string, (Vector3 T, Quaternion R, Vector3 S, string? Parent)> Rest { get; init; }
            // Hips' parent's world transform (the Armature: unit scale and axis convention).
            public required Matrix4x4 HipsParentWorld { get; init; }

            public static Rig? From(IEnumerable<Node> nodes)
            {
                var byName = nodes.Where(n => n.Name != null).GroupBy(n => n.Name!).ToDictionary(g => g.Key, g => g.First());
                if (!byName.TryGetValue("Hips", out var hips)) return null;
                var rest = new Dictionary<string, (Vector3, Quaternion, Vector3, string?)>();
                foreach (var (name, node) in byName)
                {
                    Matrix4x4.Decompose(node.LocalMatrix, out var s, out var r, out var t);
                    rest[name] = (t, r, s, node.VisualParent?.Name);
                }
                return new Rig { Rest = rest, HipsParentWorld = hips.VisualParent?.WorldMatrix ?? Matrix4x4.Identity };
            }

            public static Rig? From(IReadOnlyDictionary<string, NodeBuilder> nodesByName)
            {
                if (!nodesByName.TryGetValue("Hips", out var hips)) return null;
                var rest = new Dictionary<string, (Vector3, Quaternion, Vector3, string?)>();
                foreach (var (name, node) in nodesByName)
                {
                    Matrix4x4.Decompose(node.LocalMatrix, out var s, out var r, out var t);
                    rest[name] = (t, r, s, node.Parent?.Name);
                }
                return new Rig { Rest = rest, HipsParentWorld = hips.Parent?.WorldMatrix ?? Matrix4x4.Identity };
            }

            // Hips, ..., foot: the bones whose transforms place this foot, or null if the rig
            // doesn't have this leg or it doesn't hang off Hips.
            internal List<string>? PathTo(string foot)
            {
                var path = new List<string>();
                for (string? b = foot; b != null; b = Rest.TryGetValue(b, out var r) ? r.Parent : null)
                {
                    if (!Rest.ContainsKey(b)) return null;
                    path.Add(b);
                    if (b == "Hips") { path.Reverse(); return path; }
                }
                return null;
            }
        }

        // Finds the windows in which each ankle and each toe stands still, from `clip` played on
        // `rig` (the rig the clip was authored for), and stores them on the clip for Apply, keyed
        // by the Foot / ToeBase bone's name.
        public static void DetectContacts(AnimationClipData clip, Rig rig)
        {
            var contacts = new Dictionary<string, List<(float Start, float End)>>();
            clip.FootContacts = contacts;

            // Ankles are compared with the lowest ankle, toes with the lowest toe.
            foreach (var joint in new Func<(string UpLeg, string Leg, string Foot, string Toe), string>[] { l => l.Foot, l => l.Toe })
            {
                var points = Legs.Select(l => (Name: joint(l), Path: rig.PathTo(joint(l)), Leg: rig.PathTo(l.Foot)))
                    .Where(l => l.Path != null && l.Leg != null).ToList();
                var times = SampleTimes(clip, points.SelectMany(l => l.Path!));
                if (points.Count == 0 || times.Length < 3) continue;

                var curves = new Curves(clip);
                var tracks = points.Select(l => times.Select(t => Pose(rig, l.Path!, curves, t)[^1].Pos).ToArray()).ToList();
                float floor = tracks.Min(a => a.Min(p => p.Y));

                for (int li = 0; li < points.Count; li++)
                {
                    float legLength = RestLegLength(rig, points[li].Leg!);
                    if (legLength <= 1e-6f) continue;
                    var p = tracks[li];

                    var planted = new bool[times.Length];
                    for (int i = 0; i < times.Length; i++)
                    {
                        int a = Math.Max(0, i - 1), b = Math.Min(times.Length - 1, i + 1);
                        float dt = times[b] - times[a];
                        float speed = dt > 0 ? Horizontal(p[b] - p[a]).Length() / dt : 0f;
                        planted[i] = p[i].Y - floor < ContactHeightShare * legLength && speed < ContactSpeedShare * legLength;
                    }

                    contacts[points[li].Name] = Runs(planted, times);
                }
            }
        }

        // Pins each foot through its contact windows by re-solving the leg's thigh and shin
        // rotations (see the class comment). `rig` is the rig the clip will play on. A clip with
        // no stored contacts gets them detected here, on this rig.
        public static void Apply(AnimationClipData clip, Rig rig)
        {
            if (clip.FootContacts == null) DetectContacts(clip, rig);
            var contacts = clip.FootContacts!;

            foreach (var (upLeg, leg, foot, toe) in Legs)
            {
                var ankleWindows = contacts.GetValueOrDefault(foot) ?? new();
                // The toe only counts when it hangs straight off the foot, so its offset from the
                // ankle is fixed by the foot's own orientation, which the IK keeps.
                var toePath = rig.PathTo(toe);
                var toeWindows = toePath != null && toePath.Count >= 2 && toePath[^2] == foot
                    ? contacts.GetValueOrDefault(toe) ?? new() : new();
                if (ankleWindows.Count == 0 && toeWindows.Count == 0) continue;

                var path = toeWindows.Count > 0 ? toePath! : rig.PathTo(foot);
                if (path == null) continue;
                int iUp = path.IndexOf(upLeg), iLeg = path.IndexOf(leg), iFoot = path.IndexOf(foot), iToe = path.Count - 1;
                if (iUp < 1 || iLeg <= iUp || iFoot <= iLeg) continue;

                var times = SampleTimes(clip, path);
                if (times.Length < 2) continue;
                var curves = new Curves(clip);
                var poses = times.Select(t => Pose(rig, path, curves, t)).ToArray();

                // Each window's pin: where the joint averaged over that window as the clip had it,
                // so the correction is as small as it can be on its frames.
                Vector3[] Pins(List<(float Start, float End)> windows, int joint) => windows.Select(w =>
                {
                    var inside = Enumerable.Range(0, times.Length).Where(i => times[i] >= w.Start && times[i] <= w.End).ToList();
                    if (inside.Count == 0) inside.Add(Nearest(times, (w.Start + w.End) / 2));
                    var sum = Vector3.Zero;
                    foreach (var i in inside) sum += poses[i][joint].Pos;
                    return sum / inside.Count;
                }).ToArray();
                var anklePins = Pins(ankleWindows, iFoot);
                var toePins = Pins(toeWindows, iToe);

                var newUp = new Dictionary<float, Quaternion>();
                var newLeg = new Dictionary<float, Quaternion>();
                var newFoot = new Dictionary<float, Quaternion>();
                for (int i = 0; i < times.Length; i++)
                {
                    float t = times[i];
                    var pose = poses[i];
                    newUp[t] = pose[iUp].Local;
                    newLeg[t] = pose[iLeg].Local;
                    newFoot[t] = pose[iFoot].Local;

                    // The ankle's own pin wins while the heel is down; with only the toe planted
                    // (heel lifting), the ankle goes wherever puts the toe on its pin. Blending
                    // one into the other by the ankle's weight keeps the hand-over smooth.
                    var (ankleWeight, anklePin) = Weight(ankleWindows, anklePins, t);
                    var (toeWeight, toePin) = Weight(toeWindows, toePins, t);
                    float weight = MathF.Max(ankleWeight, toeWeight);
                    if (weight <= 0f) continue;
                    var pin = toeWeight > 0f
                        ? Vector3.Lerp(toePin - (pose[iToe].Pos - pose[iFoot].Pos), anklePin, ankleWeight)
                        : anklePin;
                    var target = Vector3.Lerp(pose[iFoot].Pos, pin, weight);
                    if (!SolveTwoBone(pose[iUp].Pos, pose[iLeg].Pos, pose[iFoot].Pos, target, out var d1, out var d2)) continue;

                    // Everything below UpLeg turns by d1; everything below Leg by d2 on top. The
                    // foot keeps its own world orientation.
                    var upWorld = Quaternion.Normalize(Quaternion.Multiply(d1, pose[iUp].World));
                    newUp[t] = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(pose[iUp - 1].World), upWorld));

                    var legParentWorld = iLeg - 1 == iUp ? upWorld : Quaternion.Multiply(d1, pose[iLeg - 1].World);
                    var legWorld = Quaternion.Normalize(Quaternion.Multiply(d2, Quaternion.Multiply(d1, pose[iLeg].World)));
                    newLeg[t] = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(legParentWorld), legWorld));

                    var footParentWorld = iFoot - 1 == iLeg ? legWorld : Quaternion.Multiply(d2, Quaternion.Multiply(d1, pose[iFoot - 1].World));
                    newFoot[t] = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(footParentWorld), pose[iFoot].World));
                }

                Channel(clip, upLeg).Rotation = newUp;
                Channel(clip, leg).Rotation = newLeg;
                Channel(clip, foot).Rotation = newFoot;
            }
        }

        // The IK weight at time t and the pin it pulls toward: 1 inside a window, easing to 0
        // over BlendSeconds either side of it; where two windows' fades overlap, the nearer wins.
        private static (float Weight, Vector3 Pin) Weight(List<(float Start, float End)> windows, Vector3[] pins, float t)
        {
            float best = 0f;
            var pin = Vector3.Zero;
            for (int w = 0; w < windows.Count; w++)
            {
                float d = t < windows[w].Start ? windows[w].Start - t : t > windows[w].End ? t - windows[w].End : 0f;
                if (d >= BlendSeconds) continue;
                float x = 1f - d / BlendSeconds;
                float weight = x * x * (3f - 2f * x);
                if (weight > best) { best = weight; pin = pins[w]; }
            }
            return (best, pin);
        }

        // Rotations d1 (applied to the thigh, about the hip a) and d2 (then to the shin, about the
        // knee) that bring the ankle c onto `target`, keeping the knee bending the way it already
        // points. False when the pose is degenerate (a dead-straight leg has no knee direction).
        private static bool SolveTwoBone(Vector3 a, Vector3 b, Vector3 c, Vector3 target, out Quaternion d1, out Quaternion d2)
        {
            d1 = d2 = Quaternion.Identity;
            float upper = (b - a).Length(), lower = (c - b).Length();
            var toTarget = target - a;
            float reach = toTarget.Length();
            if (upper < 1e-6f || lower < 1e-6f || reach < 1e-6f) return false;

            var dir = toTarget / reach;
            reach = Math.Clamp(reach, MathF.Abs(upper - lower) + 1e-5f * upper, (upper + lower) * 0.9999f);

            var knee = b - a;
            var pole = knee - dir * Vector3.Dot(knee, dir);
            if (pole.Length() < 1e-4f * upper) return false;
            pole = Vector3.Normalize(pole);

            float along = (upper * upper - lower * lower + reach * reach) / (2f * reach);
            float across = MathF.Sqrt(MathF.Max(0f, upper * upper - along * along));
            var newKnee = a + dir * along + pole * across;
            var newAnkle = a + dir * reach;

            d1 = FromTo(b - a, newKnee - a);
            var ankleAfterThigh = a + Vector3.Transform(c - a, d1);
            d2 = FromTo(ankleAfterThigh - newKnee, newAnkle - newKnee);
            return true;
        }

        private static Quaternion FromTo(Vector3 from, Vector3 to)
        {
            from = Vector3.Normalize(from);
            to = Vector3.Normalize(to);
            float dot = Vector3.Dot(from, to);
            if (dot > 0.999999f) return Quaternion.Identity;
            if (dot < -0.999999f)
            {
                var axis = Vector3.Cross(from, Vector3.UnitX);
                if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(from, Vector3.UnitY);
                return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
            }
            var c = Vector3.Cross(from, to);
            return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + dot));
        }

        // Forward kinematics down `path` (Hips first) at time t: each bone's local rotation, world
        // rotation and world position. Bones the clip doesn't key hold their rest values.
        private static (Quaternion Local, Quaternion World, Vector3 Pos)[] Pose(Rig rig, List<string> path, Curves curves, float t)
        {
            var result = new (Quaternion, Quaternion, Vector3)[path.Count];
            var world = rig.HipsParentWorld;
            Matrix4x4.Decompose(world, out _, out var worldRot, out _);
            for (int i = 0; i < path.Count; i++)
            {
                var rest = rig.Rest[path[i]];
                var tr = curves.Translation(path[i], t) ?? rest.T;
                var r = curves.Rotation(path[i], t) ?? rest.R;
                var s = curves.Scale(path[i], t) ?? rest.S;
                world = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(tr) * world;
                worldRot = Quaternion.Normalize(Quaternion.Multiply(worldRot, r));
                result[i] = (r, worldRot, world.Translation);
            }
            return result;
        }

        private static float RestLegLength(Rig rig, List<string> path)
        {
            var world = rig.HipsParentWorld;
            var positions = new List<Vector3>();
            foreach (var bone in path)
            {
                var rest = rig.Rest[bone];
                world = Matrix4x4.CreateScale(rest.S) * Matrix4x4.CreateFromQuaternion(rest.R) * Matrix4x4.CreateTranslation(rest.T) * world;
                positions.Add(world.Translation);
            }
            // Hip joint to ankle along the bones, i.e. thigh + shin (+ anything between).
            int up = Math.Min(2, positions.Count - 1);
            for (int i = 0; i < path.Count; i++) if (path[i].EndsWith("UpLeg")) { up = i; break; }
            float length = 0f;
            for (int i = up + 1; i < positions.Count; i++) length += (positions[i] - positions[i - 1]).Length();
            return length;
        }

        // Every key time any of `bones` has, in order: the frames the IK is solved on.
        private static float[] SampleTimes(AnimationClipData clip, IEnumerable<string> bones)
        {
            var names = new HashSet<string>(bones);
            var times = new SortedSet<float>();
            foreach (var ch in clip.NodeChannels)
            {
                if (!names.Contains(ch.NodeName)) continue;
                if (ch.Translation != null) times.UnionWith(ch.Translation.Keys);
                if (ch.Rotation != null) times.UnionWith(ch.Rotation.Keys);
                if (ch.Scale != null) times.UnionWith(ch.Scale.Keys);
            }
            return times.ToArray();
        }

        // Runs of `true` as time windows, with short gaps filled and short runs dropped.
        private static List<(float Start, float End)> Runs(bool[] on, float[] times)
        {
            var runs = new List<(float Start, float End)>();
            for (int i = 0; i < on.Length; i++)
            {
                if (!on[i]) continue;
                int j = i;
                while (j + 1 < on.Length && on[j + 1]) j++;
                if (runs.Count > 0 && times[i] - runs[^1].End < MinGapSeconds) runs[^1] = (runs[^1].Start, times[j]);
                else runs.Add((times[i], times[j]));
                i = j;
            }
            runs.RemoveAll(r => r.End - r.Start < MinContactSeconds);
            return runs;
        }

        private static int Nearest(float[] times, float t)
        {
            int best = 0;
            for (int i = 1; i < times.Length; i++) if (MathF.Abs(times[i] - t) < MathF.Abs(times[best] - t)) best = i;
            return best;
        }

        private static Vector3 Horizontal(Vector3 v) => new(v.X, 0f, v.Z);

        private static NodeChannelData Channel(AnimationClipData clip, string bone)
        {
            var ch = clip.NodeChannels.FirstOrDefault(c => c.NodeName == bone);
            if (ch == null) { ch = new NodeChannelData { NodeName = bone }; clip.NodeChannels.Add(ch); }
            return ch;
        }

        // A clip's channels sampled at arbitrary times (linear / slerp between keys, held past
        // either end), the way glTF's LINEAR interpolation plays them.
        private sealed class Curves
        {
            private readonly Dictionary<string, (float[] T, Vector3[] V)> _translation = new();
            private readonly Dictionary<string, (float[] T, Quaternion[] V)> _rotation = new();
            private readonly Dictionary<string, (float[] T, Vector3[] V)> _scale = new();

            public Curves(AnimationClipData clip)
            {
                foreach (var ch in clip.NodeChannels)
                {
                    if (ch.Translation is { Count: > 0 }) _translation[ch.NodeName] = Sorted(ch.Translation);
                    if (ch.Rotation is { Count: > 0 }) _rotation[ch.NodeName] = Sorted(ch.Rotation);
                    if (ch.Scale is { Count: > 0 }) _scale[ch.NodeName] = Sorted(ch.Scale);
                }
            }

            public Vector3? Translation(string bone, float t) => _translation.TryGetValue(bone, out var c) ? Sample(c, t, Vector3.Lerp) : null;
            public Quaternion? Rotation(string bone, float t) => _rotation.TryGetValue(bone, out var c) ? Sample(c, t, Quaternion.Slerp) : null;
            public Vector3? Scale(string bone, float t) => _scale.TryGetValue(bone, out var c) ? Sample(c, t, Vector3.Lerp) : null;

            private static (float[], T[]) Sorted<T>(Dictionary<float, T> keys)
            {
                var ordered = keys.OrderBy(k => k.Key).ToArray();
                return (ordered.Select(k => k.Key).ToArray(), ordered.Select(k => k.Value).ToArray());
            }

            private static T Sample<T>((float[] T, T[] V) c, float t, Func<T, T, float, T> lerp)
            {
                int i = Array.BinarySearch(c.T, t);
                if (i >= 0) return c.V[i];
                i = ~i;
                if (i == 0) return c.V[0];
                if (i >= c.T.Length) return c.V[^1];
                float f = (t - c.T[i - 1]) / (c.T[i] - c.T[i - 1]);
                return lerp(c.V[i - 1], c.V[i], f);
            }
        }
    }
}
