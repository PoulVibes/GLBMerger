using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using static GlbMerger.FaceAnchors;

namespace GlbMerger
{
    // The model-side half of the game's shader-drawn jersey numbers (see JerseyNumberEditor):
    // the anchor convention, and the two one-off clean-ups that retire the baked numbers - painting
    // them out of the albedo and taking the rigid regions that held them flat back out of the skin.
    //
    // An anchor follows the face anchors' convention (FaceAnchors) with one difference: it is a
    // child of a spine joint, and the back anchor is turned 180 degrees about Y. In both, the
    // anchor's local frame is x = the reading direction as seen from outside, y = up, z = out of
    // the body, so the game reads digits the same way off either one. Scale is the half-extents in
    // mesh units: x = half the width the number may use, y = half the digit cell's height, z = the
    // slab depth INTO the body; the "front" extra is its reach out of the body, as a fraction of z.
    internal static class JerseyNumbers
    {
        public static readonly string[] AnchorNames = { "JerseyNumberFront", "JerseyNumberBack" };

        // The digit atlas both the preview and the game sample (JerseyNumberEditor.Viewer.cs builds
        // it; "Save Digit Atlas" writes it out): ten cells left to right, 0..9, each CellW x CellH,
        // holding a signed distance field in R - 0.5 on the glyph's edge, +-0.5 at SdfSpread pixels
        // inside / outside. Row 0 is the cell's top.
        public const int CellW = 160, CellH = 200, SdfSpread = 24;

        // ---- The game's fonts -----------------------------------------------------------------

        // The game's UI fonts (FootballRoguelite Graphics/UI/*.ttf - ImGuiLayer loads them), found by
        // walking up from the player-model library to the folder holding Graphics/UI. The digits
        // are drawn in one of these so they match the rest of the game's type.
        public static string? FindGameFontFolder(string? libraryDirectory)
        {
            for (var dir = string.IsNullOrEmpty(libraryDirectory) ? null : new System.IO.DirectoryInfo(libraryDirectory);
                 dir != null; dir = dir.Parent)
            {
                var ui = System.IO.Path.Combine(dir.FullName, "Graphics", "UI");
                if (System.IO.Directory.Exists(ui) && GameFonts(ui).Count > 0) return ui;
            }
            return null;
        }

        // The font the game's JerseyDigits.png was built from (Bob's numbers were tuned with it).
        public const string DefaultFont = "Champions-Thinner.ttf";

        // DefaultFont first, so it is what the editor picks; the rest by name.
        public static List<string> GameFonts(string folder) => System.IO.Directory
            .EnumerateFiles(folder, "*.*")
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetFileName(f)!)
            .OrderBy(f => string.Equals(f, DefaultFont, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ---- Rig lookups ---------------------------------------------------------------------

        private static readonly Regex SpinePattern = new(@"^spine\d*$");

        // Every skin joint, spine chain first (base of the spine first), then the rest in skin order.
        public static List<Node> JointsSpineFirst(ModelRoot model)
        {
            var joints = new List<Node>();
            foreach (var skin in model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var j = skin.GetJoint(i).Joint;
                    if (!joints.Contains(j)) joints.Add(j);
                }
            var spine = joints.Where(IsSpine).OrderBy(Depth).ToList();
            return spine.Concat(joints.Where(j => !IsSpine(j))).ToList();
        }

        public static bool IsSpine(Node n) => SpinePattern.IsMatch(Normalise(n.Name));

        // The joint at the middle of the spine chain: on a Spine/Spine1/Spine2 rig, Spine1.
        public static Node? DefaultSpine(ModelRoot model)
        {
            var spine = JointsSpineFirst(model).Where(IsSpine).ToList();
            return spine.Count == 0 ? null : spine[spine.Count / 2];
        }

        private static int Depth(Node n)
        {
            int d = 0;
            for (var p = n.VisualParent; p != null; p = p.VisualParent) d++;
            return d;
        }

        // ---- Anchor transform ----------------------------------------------------------------

        // The anchor in mesh space - scale, turn (back only), place.
        private static Matrix4x4 MeshAnchor(int side, AnchorRegion region, float modelHeight)
        {
            var radii = region.RadiiPct * (modelHeight / 100f);
            var turn = side == 1 ? Matrix4x4.CreateRotationY(MathF.PI) : Matrix4x4.Identity;
            return Matrix4x4.CreateScale(radii) * turn * Matrix4x4.CreateTranslation(region.Centre);
        }

        // Writes `region` as anchor `side` under `spine` and returns it, or null if its transform
        // can't be expressed. SharpGLTF can't move a node to another parent, so an existing anchor
        // under some other joint is retired and a fresh one made.
        public static Node? WriteAnchor(ModelRoot model, Node spine, int side, AnchorRegion region, float modelHeight)
        {
            string name = AnchorNames[side];
            var existing = FindAnchorNode(model, name, includeUnused: true);
            if (existing != null && existing.VisualParent != spine)
            {
                Retire(existing, name);
                existing = null;
            }

            var local = MeshAnchor(side, region, modelHeight) * MeshToNode(model, spine);
            if (!Matrix4x4.Decompose(local, out var scale, out var rotation, out var translation)) return null;

            var node = existing ?? spine.CreateNode(name);
            node.Name = name;
            node.LocalTransform = new AffineTransform(scale, rotation, translation);
            return node;
        }

        // ---- The slab ------------------------------------------------------------------------

        // An anchor resolved to mesh units, for testing points against it.
        public readonly struct Slab
        {
            public readonly int Side;
            public readonly Vector3 Centre, Radii;
            public readonly float Out;   // reach out of the body

            public Slab(int side, AnchorRegion r, float modelHeight)
            {
                Side = side;
                Centre = r.Centre;
                Radii = r.RadiiPct * (modelHeight / 100f);
                Out = r.FrontPct * (modelHeight / 100f);
            }

            // Mesh direction out of the body.
            public Vector3 Outward => Side == 1 ? -Vector3.UnitZ : Vector3.UnitZ;

            // `grow` widens it across and up (not in depth).
            public bool Contains(Vector3 p, float grow = 1f)
            {
                var d = p - Centre;
                float x = Side == 1 ? -d.X : d.X, z = Side == 1 ? -d.Z : d.Z;
                return MathF.Abs(x) <= Radii.X * grow && MathF.Abs(d.Y) <= Radii.Y * grow &&
                       z >= -Radii.Z && z <= Out;
            }
        }

        // Faces pointing at least this far out of the slab's face count as on it - the back of the
        // shirt never takes the front's number through the body.
        private const float MinFacing = 0.2f;

        private static bool FacesOut(Slab s, Vector3 p0, Vector3 p1, Vector3 p2)
        {
            var n = Vector3.Cross(p1 - p0, p2 - p0);
            float len = n.Length();
            return len > 1e-12f && Vector3.Dot(n / len, s.Outward) >= MinFacing;
        }

        // ---- Erasing the baked number ----------------------------------------------------------

        public sealed class EraseResult
        {
            public int ImageIndex { get; init; }
            public int Texels { get; set; }
            public byte[]? Png { get; set; }
            // The same texels in the material's other maps (roughness, occlusion, ...) that share
            // the albedo's size and UVs, flattened to their value at the fill texel.
            public List<(int ImageIndex, byte[] Png)> Others { get; } = new();
            public (byte R, byte G, byte B)? Fill { get; set; }
            public string? Problem { get; set; }
        }

        // How far past a triangle's UV edge (texels) the fill bleeds, into atlas space no triangle
        // uses - so mip levels never pull the old number's colour back in round the island's edge.
        private const float Bleed = 2.5f;

        // Paints every texel showing a point inside either slab, on a face pointing out of it, in
        // one flat jersey green: the colour of the jersey round the number (the median-brightness
        // texel that reads fully as jersey to the game's recolour mask), so the game recolours the
        // whole patch exactly as it does the rest of the shirt.
        public static List<EraseResult> EraseBakedNumbers(ModelRoot model, IReadOnlyList<Slab> slabs)
        {
            var results = new List<EraseResult>();
            foreach (var target in TextureAtlasUtil.FindAlbedoImages(model))
            {
                var result = EraseInImage(model, target.ImageIndex, slabs);
                if (result != null) results.Add(result);
            }
            return results;
        }

        private static EraseResult? EraseInImage(ModelRoot model, int imageIndex, IReadOnlyList<Slab> slabs)
        {
            using var bmp = TextureAtlasUtil.LoadBitmap(TextureAtlasUtil.SnapshotContent(model, imageIndex));
            int w = bmp.Width, h = bmp.Height;
            var tris = TextureAtlasUtil.GatherTriangles(model, imageIndex, w, h);

            // Triangles on the torso that reach into a slab and face out of it.
            var candidates = new List<(TextureAtlasUtil.Tri3 Tri, Slab Slab)>();
            foreach (var t in TorsoTriangles(model, imageIndex, w, h))
                foreach (var s in slabs)
                    if ((s.Contains(t.P0, 1.1f) || s.Contains(t.P1, 1.1f) || s.Contains(t.P2, 1.1f) ||
                         s.Contains((t.P0 + t.P1 + t.P2) / 3f, 1.1f)) && FacesOut(s, t.P0, t.P1, t.P2))
                    {
                        candidates.Add((t, s));
                        break;
                    }
            if (candidates.Count == 0) return null;

            // What every triangle covers, so the bleed past an edge only lands on unused atlas space.
            var positions = new Vector3[w * h];
            var covered = new bool[w * h];
            TextureAtlasUtil.RasterizeTriangles(tris, w, h, positions, covered, new float[w * h]);

            var pixels = TextureAtlasUtil.ReadPixels(bmp);   // BGRA
            var paint = new bool[w * h];
            var jersey = new List<int>();
            foreach (var (t, s) in candidates)
                Rasterize(t, w, h, (idx, inside, p) =>
                {
                    if (!s.Contains(p)) return;
                    if (inside)
                    {
                        paint[idx] = true;
                        if (MaskOf(pixels, idx) >= 0.99f) jersey.Add(idx);
                    }
                    else if (!covered[idx]) paint[idx] = true;
                });

            var result = new EraseResult { ImageIndex = imageIndex };
            if (jersey.Count == 0)
            {
                result.Problem = "no jersey green inside the number boxes to take the colour from";
                return result;
            }

            jersey.Sort((a, b) => Luma(pixels, a).CompareTo(Luma(pixels, b)));
            int pick = jersey[jersey.Count / 2];
            byte fb = pixels[pick * 4], fg = pixels[pick * 4 + 1], fr = pixels[pick * 4 + 2];

            for (int i = 0; i < paint.Length; i++)
            {
                if (!paint[i]) continue;
                pixels[i * 4] = fb; pixels[i * 4 + 1] = fg; pixels[i * 4 + 2] = fr;
                result.Texels++;
            }

            using var outBmp = TextureAtlasUtil.WritePixels(pixels, w, h);
            result.Png = TextureAtlasUtil.EncodePng(outBmp);
            result.Fill = (fr, fg, fb);

            foreach (int other in CompanionImages(model, imageIndex))
            {
                using var otherBmp = TextureAtlasUtil.LoadBitmap(TextureAtlasUtil.SnapshotContent(model, other));
                if (otherBmp.Width != w || otherBmp.Height != h) continue;
                var op = TextureAtlasUtil.ReadPixels(otherBmp);
                for (int i = 0; i < paint.Length; i++)
                    if (paint[i]) System.Buffer.BlockCopy(op, pick * 4, op, i * 4, 4);
                using var otherOut = TextureAtlasUtil.WritePixels(op, w, h);
                result.Others.Add((other, TextureAtlasUtil.EncodePng(otherOut)));
            }
            return result;
        }

        // Every other image a material using `imageIndex` as its albedo binds, on the same UV set.
        private static IEnumerable<int> CompanionImages(ModelRoot model, int imageIndex)
        {
            var found = new HashSet<int>();
            foreach (var mat in model.LogicalMaterials)
            {
                var albedo = mat.FindChannel("BaseColor");
                if (albedo?.Texture?.PrimaryImage?.LogicalIndex != imageIndex) continue;
                foreach (var ch in mat.Channels)
                {
                    var img = ch.Texture?.PrimaryImage;
                    if (img == null || img.LogicalIndex == imageIndex) continue;
                    if (ch.TextureCoordinate != albedo.Value.TextureCoordinate) continue;
                    found.Add(img.LogicalIndex);
                }
            }
            return found;
        }

        // The triangles TextureAtlasUtil.GatherTriangles would give, minus any whose skin mostly
        // follows a joint off the spine chain and hips: a box drawn a little generously round the
        // front number reaches the chin or the inside of the mouth, which must not turn green.
        private static List<TextureAtlasUtil.Tri3> TorsoTriangles(ModelRoot model, int imageIndex, int width, int height)
        {
            var tris = new List<TextureAtlasUtil.Tri3>();
            foreach (var mat in model.LogicalMaterials)
            {
                var ch = mat.FindChannel("BaseColor");
                if (ch?.Texture?.PrimaryImage?.LogicalIndex != imageIndex) continue;
                string uvName = $"TEXCOORD_{ch.Value.TextureCoordinate}";

                foreach (var mesh in model.LogicalMeshes)
                {
                    var skin = model.LogicalNodes.FirstOrDefault(n => n.Mesh == mesh && n.Skin != null)?.Skin;
                    if (skin == null) continue;
                    var torso = new bool[skin.JointsCount];
                    for (int j = 0; j < skin.JointsCount; j++)
                    {
                        var joint = skin.GetJoint(j).Joint;
                        torso[j] = IsSpine(joint) || Normalise(joint.Name) == "hips";
                    }

                    foreach (var prim in mesh.Primitives)
                    {
                        if (prim.Material?.LogicalIndex != mat.LogicalIndex) continue;
                        if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var pAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue(uvName, out var uvAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;
                        var pos = pAcc.AsVector3Array();
                        var uvs = uvAcc.AsVector2Array();
                        var jts = jAcc.AsVector4Array();
                        var wts = wAcc.AsVector4Array();

                        float TorsoShare(int v)
                        {
                            float share = 0f;
                            void One(float j, float wt) { int ji = (int)j; if (ji >= 0 && ji < torso.Length && torso[ji]) share += wt; }
                            One(jts[v].X, wts[v].X); One(jts[v].Y, wts[v].Y); One(jts[v].Z, wts[v].Z); One(jts[v].W, wts[v].W);
                            return share;
                        }

                        foreach (var (a, b, c) in prim.GetTriangleIndices())
                        {
                            if (TorsoShare(a) + TorsoShare(b) + TorsoShare(c) < 1.5f) continue;
                            Vector2 Px(int v) => new(uvs[v].X * width, uvs[v].Y * height);
                            tris.Add(new TextureAtlasUtil.Tri3(pos[a], pos[b], pos[c], Px(a), Px(b), Px(c)));
                        }
                    }
                }
            }
            return tris;
        }

        // Calls `visit` for each texel within Bleed of the triangle's UV footprint, with whether
        // its centre is inside the triangle and the surface point there (extrapolated past the edges).
        private static void Rasterize(TextureAtlasUtil.Tri3 t, int w, int h, Action<int, bool, Vector3> visit)
        {
            float area = Cross(t.Uv1 - t.Uv0, t.Uv2 - t.Uv0);
            if (MathF.Abs(area) < 1e-6f) return;

            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.Uv0.X, MathF.Min(t.Uv1.X, t.Uv2.X)) - Bleed));
            int x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(t.Uv0.X, MathF.Max(t.Uv1.X, t.Uv2.X)) + Bleed));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.Uv0.Y, MathF.Min(t.Uv1.Y, t.Uv2.Y)) - Bleed));
            int y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(t.Uv0.Y, MathF.Max(t.Uv1.Y, t.Uv2.Y)) + Bleed));

            // Signed distance (texels) from each edge, positive inside whichever way the triangle winds.
            float sign = MathF.Sign(area);
            float l0 = (t.Uv2 - t.Uv1).Length(), l1 = (t.Uv0 - t.Uv2).Length(), l2 = (t.Uv1 - t.Uv0).Length();
            if (l0 < 1e-6f || l1 < 1e-6f || l2 < 1e-6f) return;

            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    float e0 = Cross(t.Uv2 - t.Uv1, p - t.Uv1), e1 = Cross(t.Uv0 - t.Uv2, p - t.Uv2), e2 = Cross(t.Uv1 - t.Uv0, p - t.Uv0);
                    float d0 = sign * e0 / l0, d1 = sign * e1 / l1, d2 = sign * e2 / l2;
                    if (d0 < -Bleed || d1 < -Bleed || d2 < -Bleed) continue;
                    bool inside = d0 >= 0 && d1 >= 0 && d2 >= 0;
                    var point = t.P0 * (e0 / area) + t.P1 * (e1 / area) + t.P2 * (e2 / area);
                    visit(py * w + px, inside, point);
                }
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static float Luma(byte[] bgra, int i) => 0.0722f * bgra[i * 4] + 0.7152f * bgra[i * 4 + 1] + 0.2126f * bgra[i * 4 + 2];

        private static float MaskOf(byte[] bgra, int i) => MaskOf(new Vector3(
            SrgbToLinear(bgra[i * 4 + 2] / 255f), SrgbToLinear(bgra[i * 4 + 1] / 255f), SrgbToLinear(bgra[i * 4] / 255f)));

        // FootballRoguelite JerseyMask::MaskOf: how strongly a LINEAR colour reads as jersey green
        // to the game's team recolour. Keep in step.
        public static float MaskOf(Vector3 c)
        {
            var hsv = RgbToHsv(c);
            float hueDeg = hsv.X * 360f;
            float hueLo = SmoothStep(70f, 85f, hueDeg);
            float hueHi = 1f - SmoothStep(155f, 170f, hueDeg);
            float satOk = SmoothStep(0.12f, 0.25f, hsv.Y);
            float valOk = SmoothStep(0.05f, 0.15f, hsv.Z);
            return hueLo * hueHi * satOk * valOk;
        }

        private static Vector3 RgbToHsv(Vector3 c)
        {
            var k = new Vector4(0f, -1f / 3f, 2f / 3f, -1f);
            var p = Vector4.Lerp(new Vector4(c.Z, c.Y, k.W, k.Z), new Vector4(c.Y, c.Z, k.X, k.Y), c.Z <= c.Y ? 1f : 0f);
            var q = Vector4.Lerp(new Vector4(p.X, p.Y, p.W, c.X), new Vector4(c.X, p.Y, p.Z, p.X), p.X <= c.X ? 1f : 0f);
            float d = q.X - MathF.Min(q.W, q.Y);
            const float e = 1e-10f;
            return new Vector3(MathF.Abs(q.Z + (q.W - q.Y) / (6f * d + e)), d / (q.X + e), q.X);
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
            return t * t * (3f - 2f * t);
        }

        public static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

        // ---- Taking the rigid backs out --------------------------------------------------------

        // Skin bound entirely to one joint - what a rigid region leaves (and what the middle of a
        // naturally weighted torso can be too).
        public const float RigidWeight = 0.999f;

        // The triangles to rebuild: on a face pointing out of a slab grown by `reach` across and
        // up, with at least one corner bound to a single joint. Keyed (mesh, primitive) -> triangle
        // indices, as SkinRegionWeights expects.
        public static Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> RigidTrianglesIn(
            ModelRoot model, IReadOnlyList<Slab> slabs, float reach)
        {
            var selection = new Dictionary<(int, int), HashSet<int>>();
            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var prim = mesh.Primitives[primIdx];
                    if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                    if (!prim.VertexAccessors.TryGetValue("POSITION", out var pAcc)) continue;
                    if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;
                    var pos = pAcc.AsVector3Array();
                    var wts = wAcc.AsVector4Array();
                    bool Rigid(int v) => MathF.Max(MathF.Max(wts[v].X, wts[v].Y), MathF.Max(wts[v].Z, wts[v].W)) >= RigidWeight;

                    int t = 0;
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        if ((Rigid(a) || Rigid(b) || Rigid(c)) &&
                            slabs.Any(s => s.Contains(pos[a], reach) && s.Contains(pos[b], reach) &&
                                           s.Contains(pos[c], reach) && FacesOut(s, pos[a], pos[b], pos[c])))
                        {
                            if (!selection.TryGetValue((meshIdx, primIdx), out var set))
                                selection[(meshIdx, primIdx)] = set = new HashSet<int>();
                            set.Add(t);
                        }
                        t++;
                    }
                }
            }
            return selection;
        }
    }
}
