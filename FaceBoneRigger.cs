using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace GlbMerger
{
    // Gives a model brow, mouth and chin bones (FootballRoguelite FaceBones.h) and weights the face
    // round each painted brow, the mouth and the chin to them, so the game can move the actual painted
    // features.
    //
    // Bones, all under the head, each at a fixed spot in its feature's region:
    //   FaceBrowL / FaceBrowR  - at the brow's OUTER end, so a turn lifts or drops its inner end
    //   FaceMouth1..FaceMouth5  - on the five points picked along the painted mouth, corner to corner
    //                           in order of x (MouthCurve); a mouth with no points gets the five its
    //                           region implies
    //   FaceChin               - at the chin region's centre; drops as the mouth opens
    // Each carries its motion's scale in its extras ("face", "unit" = the region's half-height,
    // and per kind "reach"/"inner" or "across"/"halfWidth"). A bone's inverse bind is just the
    // inverse of its mesh-space position, so it skins the mesh to itself at rest.
    //
    // Weights: full inside `core` of the region (ellipse radius), fading to none at its edge, and
    // across the region's depth slab. The mouth's follow its curve instead (MouthCurve): shared between
    // neighbouring bones by where a vertex lies along it, fading with distance from it. The chin's fades across its width as the others do, but up and down it
    // ramps in from nothing at the region's top edge (the lips) -- or below the drawn mouth fully open,
    // when that reaches lower -- to full halfway to the bottom and stays full
    // to the bottom, so the chin moves and the skin above it stretches; where it overlaps the mouth's
    // region the two share the vertex. Re-running is safe: every face-bone influence is stripped (the vertex's
    // other weights renormalised, which restores them exactly) before the new ones go in, scaled
    // into room the others give up. At most four influences, smallest dropped.
    internal static class FaceBoneRigger
    {
        public sealed class Feature
        {
            public required string Name;   // "BrowL", "BrowR", "Mouth", "Chin"
            public AnchorRegion Region;
            public float Core = 0.4f;
            public float Drop;               // chin: x its half-height at a full open
            public Vector3[]? MouthPoints;   // mouth: the five picked along it, mesh units; null = from the region
            public float TiltDeg;            // brow: the region turned, degrees; positive raises its outer end
            public float? ChinStartY;        // chin: mesh y its pull starts below -- under the drawn mouth fully
                                             // open, so the opening rides the jaw rigidly; null = the region's top
        }

        private const float CornerAt = 0.9f;   // a brow bone's distance from the centre, x the half-width

        private sealed class BoneSpec
        {
            public required string Name;
            public Vector3 Position;            // mesh units
            public required JsonObject Extras;
            public Func<Vector3, float> Weight = _ => 0f;
        }

        private static readonly string[] MouthBoneNames = { "FaceMouth1", "FaceMouth2", "FaceMouth3", "FaceMouth4", "FaceMouth5" };

        // Every face bone this rigger has ever made: the current ones, and the three-bone mouth
        // (corners and middle) it made before the mouth was picked as points, which a re-save retires.
        public static readonly string[] AllBoneNames =
        {
            "FaceBrowL", "FaceBrowR", "FaceMouth1", "FaceMouth2", "FaceMouth3", "FaceMouth4", "FaceMouth5", "FaceChin",
            "FaceMouthL", "FaceMouthR", "FaceMouthMid",
        };

        // Returns a one-line status.
        public static string Apply(ModelRoot model, Node head, Matrix4x4 meshToHead, float modelHeight, IReadOnlyList<Feature> features)
        {
            var specs = BuildSpecs(features, modelHeight);
            var nodes = PlaceBones(model, head, meshToHead, specs);

            int skins = 0, verts = 0;
            foreach (var skin in model.LogicalSkins)
            {
                var faceJoints = Rebind(skin, nodes, specs);
                int headJoint = HeadJoint(skin, head);
                foreach (var meshNode in model.LogicalNodes.Where(n => n.Skin == skin && n.Mesh != null))
                    foreach (var prim in meshNode.Mesh.Primitives)
                        verts += Reweight(prim, faceJoints, headJoint, specs);
                skins++;
            }
            return $"{specs.Count} face bone(s); {verts} vertices weighted to them across {skins} skin(s).";
        }

        // ---- What bones, where -----------------------------------------------------------------

        private static List<BoneSpec> BuildSpecs(IReadOnlyList<Feature> features, float modelHeight)
        {
            var specs = new List<BoneSpec>();
            var browL = features.FirstOrDefault(f => f.Name == "BrowL" && f.Region.Set);
            var browR = features.FirstOrDefault(f => f.Name == "BrowR" && f.Region.Set);
            float midline = browL != null && browR != null ? 0.5f * (browL.Region.Centre.X + browR.Region.Centre.X) : 0f;

            foreach (var f in features.Where(f => f.Region.Set))
            {
                var c = f.Region.Centre;
                var r = f.Region.RadiiPct * (modelHeight / 100f);
                float front = f.Region.FrontPct * (modelHeight / 100f);

                if (f.Name == "Chin")
                {
                    specs.Add(new BoneSpec
                    {
                        Name = "FaceChin", Position = c,
                        Extras = new JsonObject { ["face"] = "chin", ["unit"] = r.Y, ["drop"] = f.Drop },
                        Weight = v => ChinFalloff(v - c, r, front, f.Core, ChinTop(c, r, f.ChinStartY)),
                    });
                }
                else if (f.Name == "Mouth")
                {
                    specs.AddRange(MouthBones(f, c, r, front));
                }
                else
                {
                    // The inner end faces the other brow (or the model's centre line). A tilted region turns
                    // about its centre, its outer end up for a positive tilt: the bone sits at that end, and
                    // the weights are measured along the turned axes.
                    float inner = c.X >= midline ? -1f : 1f;
                    float turn = -inner * f.TiltDeg * MathF.PI / 180f;
                    var across = new Vector3(MathF.Cos(turn), MathF.Sin(turn), 0f);
                    specs.Add(new BoneSpec
                    {
                        Name = "Face" + f.Name,
                        Position = c - inner * CornerAt * r.X * across,
                        Extras = new JsonObject
                        {
                            ["face"] = "brow", ["unit"] = r.Y, ["reach"] = 2f * CornerAt * r.X, ["inner"] = inner,
                        },
                        Weight = v => Falloff(Unturn(v - c, turn), r, front, f.Core),
                    });
                }
            }
            return specs;
        }

        // One bone on each of the mouth's five points; "across" (-1..1 corner to corner) is where along
        // the mouth it sits, which is what the game's motions are shaped by.
        private static IEnumerable<BoneSpec> MouthBones(Feature f, Vector3 c, Vector3 r, float front)
        {
            var curve = new MouthCurve(f.MouthPoints ?? MouthCurve.FromRegion(c, r));
            for (int k = 0; k < MouthCurve.PointCount; k++)
            {
                int bone = k;
                yield return new BoneSpec
                {
                    Name = MouthBoneNames[k], Position = curve[k],
                    Extras = new JsonObject
                    {
                        ["face"] = "mouth", ["unit"] = r.Y, ["across"] = Math.Round(curve.Across(k), 4),
                        ["halfWidth"] = curve.HalfSpan,
                    },
                    Weight = v => curve.Weight(v, bone, r.Y, r.Z, front, f.Core),
                };
            }
        }

        // `d` in a region turned `turn` radians about z: back into its own axes.
        private static Vector3 Unturn(Vector3 d, float turn)
        {
            float cs = MathF.Cos(turn), sn = MathF.Sin(turn);
            return new Vector3(d.X * cs + d.Y * sn, -d.X * sn + d.Y * cs, d.Z);
        }

        // Full inside `core` of the region's ellipse, none past its edge; likewise through the slab.
        private static float Falloff(Vector3 d, Vector3 r, float front, float core)
        {
            float radius = MathF.Sqrt((d.X / r.X) * (d.X / r.X) + (d.Y / r.Y) * (d.Y / r.Y));
            float depth = d.Z >= 0f ? d.Z / Math.Max(front, 1e-6f) : -d.Z / r.Z;
            return (1f - SmoothStep(core, 1f, radius)) * (1f - SmoothStep(0.6f, 1f, depth));
        }

        // Across and through the slab as Falloff; down from the top edge, nothing to full at the middle,
        // then full to just past the bottom (the jaw's underside goes with it).
        // Where (in the region's -1 top .. +1 bottom) the chin's pull starts: its top edge, or lower when
        // the drawn mouth reaches below it fully open -- a pull that varied across the drawn mouth would
        // bend it along the mesh's triangles (a zigzag lower lip) as the jaw drops. At most 0.6 down, so
        // the chin always has some ramp to full.
        private static float ChinTop(Vector3 c, Vector3 r, float? startY) =>
            startY is float y ? Math.Clamp((c.Y - y) / r.Y, -1f, 0.6f) : -1f;

        private static float ChinFalloff(Vector3 d, Vector3 r, float front, float core, float top)
        {
            float across = 1f - SmoothStep(core, 1f, MathF.Abs(d.X / r.X));
            float down = -d.Y / r.Y;   // -1 top edge .. +1 bottom edge
            float vertical = SmoothStep(top, 0.5f * (top + 1f), down) * (1f - SmoothStep(1f, 1.4f, down));
            float depth = d.Z >= 0f ? d.Z / Math.Max(front, 1e-6f) : -d.Z / r.Z;
            return across * vertical * (1f - SmoothStep(0.6f, 1f, depth));
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / Math.Max(b - a, 1e-6f), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        // ---- Nodes and skin ------------------------------------------------------------------------

        // Each wanted bone as a child of the head, at its mesh-space spot (through the head's inverse
        // bind, as the anchors are); every face bone NOT wanted any more loses its "face" extras so the
        // game leaves it alone.
        private static Dictionary<string, Node> PlaceBones(ModelRoot model, Node head, Matrix4x4 meshToHead, List<BoneSpec> specs)
        {
            var nodes = new Dictionary<string, Node>();
            foreach (var name in AllBoneNames)
            {
                var existing = model.LogicalNodes.FirstOrDefault(n => n.Name == name);
                var spec = specs.FirstOrDefault(s => s.Name == name);
                if (spec == null) { if (existing != null) existing.Extras = null; continue; }

                var node = existing ?? head.CreateNode(name);
                Matrix4x4.Decompose(Matrix4x4.CreateTranslation(spec.Position) * meshToHead, out var s, out var q, out var t);
                node.LocalTransform = new AffineTransform(s, q, t);
                node.Extras = spec.Extras;
                nodes[name] = node;
            }
            return nodes;
        }

        // The skin's joints with every face bone in it (existing joints keep their inverse binds);
        // returns the joint index of each wanted face bone.
        private static Dictionary<string, int> Rebind(Skin skin, Dictionary<string, Node> nodes, List<BoneSpec> specs)
        {
            var joints = new List<(Node, Matrix4x4)>();
            for (int i = 0; i < skin.JointsCount; i++) joints.Add(skin.GetJoint(i));

            var index = new Dictionary<string, int>();
            foreach (var spec in specs)
            {
                var node = nodes[spec.Name];
                var inverseBind = Matrix4x4.CreateTranslation(-spec.Position);   // mesh -> bone-local
                int at = joints.FindIndex(j => j.Item1 == node);
                if (at >= 0) joints[at] = (node, inverseBind);
                else { at = joints.Count; joints.Add((node, inverseBind)); }
                index[spec.Name] = at;
            }
            skin.BindJoints(joints);

            // Joints of retired face bones are still in the skin: their weights get stripped too.
            for (int i = 0; i < skin.JointsCount; i++)
            {
                var name = skin.GetJoint(i).Joint.Name;
                if (name != null && AllBoneNames.Contains(name) && !index.ContainsKey(name)) index["retired:" + name] = i;
            }
            return index;
        }

        // ---- Weights ---------------------------------------------------------------------------------

        // The head's joint in the skin, or -1.
        private static int HeadJoint(Skin skin, Node head)
        {
            for (int i = 0; i < skin.JointsCount; i++)
                if (skin.GetJoint(i).Joint == head) return i;
            return -1;
        }

        private static int Reweight(MeshPrimitive prim, Dictionary<string, int> faceJoints, int headJoint, List<BoneSpec> specs)
        {
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc) ||
                !prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc) ||
                !prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) return 0;

            var positions = posAcc.AsVector3Array();
            var joints = jAcc.AsVector4Array();
            var weights = wAcc.AsVector4Array();
            var faceSet = new HashSet<int>(faceJoints.Values);

            var newJoints = new Vector4[positions.Count];
            var newWeights = new Vector4[positions.Count];
            int touched = 0;
            for (int v = 0; v < positions.Count; v++)
            {
                var face = specs.Select(s => (Joint: faceJoints[s.Name], W: s.Weight(positions[v])))
                                .Where(x => x.W > 1e-4f).ToList();
                (newJoints[v], newWeights[v]) = Blend(joints[v], weights[v], faceSet, headJoint, face);
                if (face.Count > 0) touched++;
            }

            WriteJoints(prim, jAcc, newJoints, faceJoints.Values.DefaultIfEmpty(0).Max());
            WriteWeights(prim, wAcc, newWeights);
            return touched;
        }

        // The vertex's own influences minus any face bone's (renormalised), scaled to leave room for
        // `face`, the face ones added, the four biggest kept, summing to one. A vertex an earlier save
        // gave wholly to face bones has no influences of its own left: what the face bones don't take
        // now goes back to the head, rather than the vertex keeping its old face weights (a stale pull
        // there drags the paint when the bone moves -- the chin's zigzagging a lower lip).
        private static (Vector4, Vector4) Blend(Vector4 j, Vector4 w, HashSet<int> faceSet, int headJoint,
                                                List<(int Joint, float W)> face)
        {
            var own = new List<(int Joint, float W)>();
            for (int k = 0; k < 4; k++)
            {
                int joint = (int)MathF.Round(Get(j, k));
                float weight = Get(w, k);
                if (weight > 0f && !faceSet.Contains(joint)) own.Add((joint, weight));
            }
            if (own.Count == 0 && headJoint >= 0) own.Add((headJoint, 1f));
            float ownSum = own.Sum(x => x.W);
            float faceSum = Math.Min(face.Sum(x => x.W), 1f);
            float faceScale = face.Count > 0 ? faceSum / face.Sum(x => x.W) : 0f;

            var all = own.Select(x => (x.Joint, W: ownSum > 0f ? x.W / ownSum * (1f - faceSum) : 0f))
                         .Concat(face.Select(x => (x.Joint, W: x.W * faceScale)))
                         .Where(x => x.W > 0f).OrderByDescending(x => x.W).Take(4).ToList();
            float total = all.Sum(x => x.W);
            if (total <= 0f) return (j, w);   // nothing left to weight to: leave the vertex as it was

            var nj = Vector4.Zero; var nw = Vector4.Zero;
            for (int k = 0; k < all.Count; k++) { nj = Set(nj, k, all[k].Joint); nw = Set(nw, k, all[k].W / total); }
            return (nj, nw);
        }

        private static float Get(Vector4 v, int k) => k switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };
        private static Vector4 Set(Vector4 v, int k, float value)
        {
            switch (k) { case 0: v.X = value; break; case 1: v.Y = value; break; case 2: v.Z = value; break; default: v.W = value; break; }
            return v;
        }

        // ---- Writing the attributes back, in their own encodings ----------------------------------

        private static void WriteJoints(MeshPrimitive prim, Accessor old, Vector4[] values, int maxJoint)
        {
            // A byte can't index past 255 joints; widen if a new face bone needs it.
            var encoding = old.Encoding == EncodingType.UNSIGNED_BYTE && maxJoint > 255 ? EncodingType.UNSIGNED_SHORT : old.Encoding;
            WriteIntegers(prim, "JOINTS_0", values, encoding, normalized: false);
        }

        private static void WriteWeights(MeshPrimitive prim, Accessor old, Vector4[] values)
        {
            if (old.Encoding == EncodingType.FLOAT) { prim.WithVertexAccessor("WEIGHTS_0", values); return; }
            float max = old.Encoding == EncodingType.UNSIGNED_BYTE ? 255f : 65535f;
            var scaled = values.Select(v => v * max).ToArray();
            for (int i = 0; i < scaled.Length; i++)   // rounding must still sum to exactly max
            {
                var r = new Vector4(MathF.Round(scaled[i].X), MathF.Round(scaled[i].Y), MathF.Round(scaled[i].Z), MathF.Round(scaled[i].W));
                float error = max - (r.X + r.Y + r.Z + r.W);
                if (r.X + r.Y + r.Z + r.W > 0f) r.X += error;   // the largest is first (sorted)
                scaled[i] = r;
            }
            WriteIntegers(prim, "WEIGHTS_0", scaled, old.Encoding, normalized: true);
        }

        // As GeometryOptimizer: integer attributes are re-encoded byte for byte, since writing them as
        // floats would break the spec and runtimes reject it.
        private static void WriteIntegers(MeshPrimitive prim, string name, Vector4[] values, EncodingType encoding, bool normalized)
        {
            int size = encoding switch { EncodingType.UNSIGNED_BYTE => 1, EncodingType.UNSIGNED_SHORT => 2, _ => 4 };
            var bytes = new byte[values.Length * 4 * size];
            int offset = 0;
            foreach (var v in values)
                foreach (var component in new[] { v.X, v.Y, v.Z, v.W })
                {
                    uint raw = (uint)Math.Max(0f, MathF.Round(component));
                    for (int b = 0; b < size; b++) bytes[offset + b] = (byte)(raw >> (8 * b));
                    offset += size;
                }
            var info = new SharpGLTF.Memory.MemoryAccessInfo(name, 0, values.Length, 0, DimensionType.VEC4, encoding, normalized);
            prim.WithVertexAccessor(new SharpGLTF.Memory.MemoryAccessor(new ArraySegment<byte>(bytes), info));
        }
    }
}
