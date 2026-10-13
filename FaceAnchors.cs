using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace GlbMerger
{
    // One face-feature anchor as the editors hold it: where it sits and how big, in the units the
    // panels show. Mesh units for the centre; percent of the model's height for the extents.
    internal struct AnchorRegion
    {
        public bool Set;
        public Vector3 Centre;     // mesh units
        public Vector3 RadiiPct;   // half-extents; Z = depth behind the centre
        public float FrontPct;     // depth in front of the centre, <= RadiiPct.Z
    }

    // What the Eye Anchors and Brows & Mouth editors share: the game's face-feature anchor
    // convention (FootballRoguelite AnchorFrame.h). An anchor is a node under the head bone whose
    // position is the feature's centre and whose scale is its half-extents in MESH units along the
    // mesh's own axes - x across, y up, z the slab depth behind the centre. The game maps it back
    // into mesh space with the head joint's inverse-bind matrix, so it is written through that same
    // matrix, which keeps it right on rigs whose skeleton and mesh use different units. The slab's
    // reach in front of the centre is the "front" extra, a fraction of the depth behind.
    internal static class FaceAnchors
    {
        // SharpGLTF can't detach a node: a cleared anchor is renamed out of the game's sight
        // instead, and reused if that feature is placed again.
        public const string UnusedSuffix = "_unused";

        // ---- Rig lookups ---------------------------------------------------------------------

        // The game's convention (BoneNames::Normalise): lowercase alphanumerics, minus a
        // "mixamorig" prefix, compared exactly - "mixamorig:Head" matches, "HeadTop_End" doesn't.
        public static string Normalise(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var s = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return s.StartsWith("mixamorig", StringComparison.Ordinal) ? s.Substring("mixamorig".Length) : s;
        }

        public static Node? FindHead(ModelRoot model) =>
            model.LogicalNodes.FirstOrDefault(n => Normalise(n.Name) == "head");

        // The node's inverse-bind matrix when it is a skin joint - the same mapping the game uses
        // (AnchorFrame::BindToMesh) - else the inverse of its rest world matrix. Row-vector.
        public static Matrix4x4 MeshToNode(ModelRoot model, Node? node)
        {
            if (node == null) return Matrix4x4.Identity;
            foreach (var skin in model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (joint, inverseBind) = skin.GetJoint(i);
                    if (joint == node) return inverseBind;
                }
            return Matrix4x4.Invert(node.WorldMatrix, out var inv) ? inv : Matrix4x4.Identity;
        }

        public static float MeasureModelHeight(ModelRoot model)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (positions == null) continue;
                    foreach (var p in positions) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
                }
            return maxY > minY ? maxY - minY : 1f;
        }

        public static Node? FindAnchorNode(ModelRoot model, string name, bool includeUnused) =>
            model.LogicalNodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? (includeUnused
                ? model.LogicalNodes.FirstOrDefault(n => string.Equals(n.Name, name + UnusedSuffix, StringComparison.OrdinalIgnoreCase))
                : null);

        // ---- Anchor transform ----------------------------------------------------------------

        public static AnchorRegion ReadRegion(ModelRoot model, Node node, float modelHeight)
        {
            // Row-vector: local -> parent-local -> mesh.
            if (!Matrix4x4.Invert(MeshToNode(model, node.VisualParent), out var parentToMesh)) return default;
            var m = node.LocalTransform.Matrix * parentToMesh;

            var radii = new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(),
                                    new Vector3(m.M21, m.M22, m.M23).Length(),
                                    new Vector3(m.M31, m.M32, m.M33).Length());
            var radiiPct = radii / modelHeight * 100f;
            float front = 1f;
            if (node.Extras is JsonObject o) ReadUnit(o, "front", ref front);
            return new AnchorRegion { Set = true, Centre = m.Translation, RadiiPct = radiiPct, FrontPct = front * radiiPct.Z };
        }

        // Writes `region` as the anchor `name` under `head` (reusing `existing`, retired or not) and
        // returns it, or null if its transform can't be expressed. The anchor in mesh space - scale
        // then place - carried into head-local by the head's inverse bind: the exact inverse of how
        // the game reads it back.
        public static Node? WriteRegion(Node head, Matrix4x4 meshToHead, Node? existing, string name,
                                        AnchorRegion region, float modelHeight)
        {
            var radii = region.RadiiPct * (modelHeight / 100f);
            var meshAnchor = Matrix4x4.CreateScale(radii) * Matrix4x4.CreateTranslation(region.Centre);
            if (!Matrix4x4.Decompose(meshAnchor * meshToHead, out var scale, out var rotation, out var translation))
                return null;

            var node = existing ?? head.CreateNode(name);
            node.Name = name;
            node.LocalTransform = new AffineTransform(scale, rotation, translation);
            return node;
        }

        public static void Retire(Node? existing, string name)
        {
            if (existing != null) existing.Name = name + UnusedSuffix;
        }

        public static double FrontRatio(AnchorRegion r) =>
            Math.Round(r.RadiiPct.Z > 0 ? Math.Clamp(r.FrontPct / r.RadiiPct.Z, 0f, 1f) : 1f, 3);

        // ---- Extras ---------------------------------------------------------------------------

        public static void ReadClamped(JsonObject o, string key, ref float field, float min, float max)
        {
            try
            {
                if (o[key] is JsonValue v && v.TryGetValue(out double value)) field = Math.Clamp((float)value, min, max);
            }
            catch (InvalidOperationException) { }
        }

        public static void ReadUnit(JsonObject o, string key, ref float field) => ReadClamped(o, key, ref field, 0f, 1f);

        public static Color? ReadColor(JsonObject o, string key)
        {
            try
            {
                if (o[key] is JsonArray c && c.Count == 3)
                    return Color.FromArgb(ToByte(c[0]!.GetValue<double>()), ToByte(c[1]!.GetValue<double>()),
                                          ToByte(c[2]!.GetValue<double>()));
            }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
            return null;
        }

        public static JsonArray ColorJson(Color c) => new(Unit(c.R), Unit(c.G), Unit(c.B));

        public static int ToByte(double unit) => (int)Math.Round(Math.Clamp(unit, 0, 1) * 255);
        private static double Unit(int channel) => Math.Round(channel / 255.0, 4);

        // ---- Panels and the preview -----------------------------------------------------------

        public static string Inv(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        // [r,g,b] in 0..1 for the preview's JS.
        public static string ColorJs(Color c) => $"[{Inv(c.R / 255f)},{Inv(c.G / 255f)},{Inv(c.B / 255f)}]";

        public static void SetClamped(NumericUpDown n, float v) =>
            n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);
    }
}
