using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Textures the billboards ModelFlattener chose, and packs everything the flattened model
    // needs into ONE atlas - the game reads a single base-colour image per model (see
    // FootballRoguelite's ModelLoader ExtractBaseColor), so there is no second texture to fall
    // back on for anything.
    //
    // Each billboard is an orthographic render of ITS OWN triangles, looking straight down its
    // normal - only its own, so a rail billboard doesn't bake in the wall behind it (the wall's
    // billboard has that) and the wall doesn't bake in the rail. A depth test keeps whatever is
    // nearest the viewer where several layers project onto the same texel (the reveal walls of a
    // window well flattened into the facade land edge-on, so the glass behind them shows).
    // Rendered with 3x3 supersampling: the covered fraction of each texel is its alpha, so gaps
    // between rail posts come out transparent and silhouettes anti-aliased.
    //
    // Then, per billboard:
    //  - cracks one texel wide are closed and tiny enclosed holes filled - scans are full of
    //    pinholes that would otherwise sparkle through under an alpha test;
    //  - a billboard that ends up fully covered is marked opaque outright;
    //  - colour is pushed out into every transparent texel and the padding around the
    //    rectangle, so neither bilinear filtering nor mipmaps pull black into the edges.
    //
    // A hole enclosed by a billboard's own surface (where a balcony bracket or a window sill met
    // the wall - the wall mesh stops there, and the bracket's underside went to a billboard that
    // is edge-on from the front) is painted with whatever geometry sits in front of it, so the
    // background never shows through. Openings that reach the rectangle's edge - the silhouette,
    // a doorway at ground level - are real and stay open, and nothing BEHIND the plane is ever
    // painted in, so a recessed window's billboard still shows through its hole in the facade.
    //
    // Recessed surface (the glass at the back of a flattened window well, the grooves between
    // bricks) loses the shading its depth gave it once it's flat, so each texel is darkened by how
    // far it sits behind the nearest surface around it (RecessDarkening) - a cheap baked AO.
    //
    // A billboard's quad is trimmed to an octagon where its corners are empty (a stepped
    // roofline, a diagonal stair flight): one extra triangle per cut corner, in exchange for
    // not alpha-testing that empty space every frame.
    //
    // A second atlas, same layout, holds the full model's shading normal at every texel - the
    // interpolated vertex normals, perturbed by the source's own normal map where it has one -
    // expressed in the billboard's frame (+X right, +Y up, +Z out: glTF's convention, with the
    // billboard's AxisU as tangent). The grooves, reveals and ornament that flattening removed
    // still catch the light. Kept-mesh charts copy the source normal texels as they are, since
    // their UVs are only moved and scaled. A third atlas carries metallic/roughness the same way
    // (glTF packing: roughness in G, metallic in B, source factors baked in). Both extra atlases
    // go through the same stretch/backfill/bleed passes as colour - see Layer.
    //
    // Triangles ModelFlattener kept as real mesh keep their original UVs' texels: each UV chart's
    // footprint is copied (rescaled to the atlas's texel density) into its own atlas rectangle.
    //
    // Texel density: one base density for the big surfaces, and DetailBoost times that for the
    // small, mostly see-through front-facing billboards (rails, fire escapes), where a texel more
    // or less decides whether a post survives. Nothing ever goes above its own source
    // texture's density - upsampling adds nothing. The base is the highest that fits the 2048
    // cap, then the atlas shrinks to the smallest power-of-two size that still holds it all.
    public static class BillboardBaker
    {
        public const int MaxAtlasSize = 2048;
        private const int Pad = 4;              // texels of bleed around every rectangle
        private const int SS = 3;               // supersampling per axis
        private const int BandRows = 64;        // texel rows rasterized at a time, to bound memory
        private const int SmallHoleTexels = 24; // enclosed holes up to this size are filled
        private const float MaskCutoff = 0.5f;
        // Viewing angles from below the sweep fill covers (see RenderBillboard).
        private static readonly float[] SweepTangents = new[] { 10f, 20f, 30f }.Select(a => MathF.Tan(a * MathF.PI / 180f)).ToArray();
        public static float MaxSweepTangent => SweepTangents.Max();
        public static bool Sweeps(ModelFlattener.Billboard bb) => !bb.Detail && !bb.Part && !bb.Backdrop && MathF.Abs(bb.Normal.Y) < 0.3f;
        private const int MinTrimTexels = 64 * 64;     // smallest corner cut worth a triangle

        public sealed class SourceTexture
        {
            public int Width, Height;
            public byte[] Bgra = Array.Empty<byte>();
        }

        public sealed class SourceMaterial
        {
            public int Texture = -1;            // index into BakeSource.Textures
            public Vector4 Factor = Vector4.One;
            public int NormalTexture = -1;      // index into BakeSource.Textures, raw (not colour) data
            public float NormalScale = 1f;
            // glTF defaults when a material says nothing: fully metallic, fully rough.
            public int MrTexture = -1;          // index into BakeSource.Textures, raw data
            public float Metallic = 1f, Roughness = 1f;
        }

        // Everything the bake needs from the model, pulled out once (on the UI thread) so the
        // bake itself can run on a worker without touching the shared ModelRoot.
        public sealed class BakeSource
        {
            public Vector2[] Uvs = Array.Empty<Vector2>();      // three per triangle
            public Vector2[] NormalUvs = Array.Empty<Vector2>(); // three per triangle, the normal map's UV set
            public Vector2[] MrUvs = Array.Empty<Vector2>();     // three per triangle, the metallic/roughness UV set
            // Per triangle, world space: the source normal map's tangent (direction of increasing
            // u) and the sign that makes cross(normal, tangent) * sign its +Y (up in the image).
            public Vector3[] Tangents = Array.Empty<Vector3>();
            public float[] TangentSigns = Array.Empty<float>();
            public int[] Material = Array.Empty<int>();         // per triangle, index into Materials
            public List<SourceMaterial> Materials = new();
            public List<SourceTexture> Textures = new();
        }

        public sealed class BakeSettings
        {
            // Texel density multiplier for see-through front billboards (see class comment).
            public float DetailBoost = 2f;
            // 0..1: how much a texel at the back of its billboard's depth range is darkened.
            public float RecessDarkening = 0.3f;
            // Cut empty corners off billboards (up to four extra triangles each).
            public bool TrimCorners = true;
            // Bake the full model's normals into a second atlas (the material's normalTexture).
            public bool BakeNormals = true;
            // Carry the source's metallic/roughness texture over as a third atlas. Only happens
            // when some source material has one; otherwise its factors are copied as constants.
            public bool BakeMetallicRoughness = true;
        }

        public sealed class BakeResult
        {
            public byte[] AtlasPng = Array.Empty<byte>();
            public byte[] AtlasBgra = Array.Empty<byte>();
            // Same layout as the colour atlas; empty when normals weren't baked.
            public byte[] NormalPng = Array.Empty<byte>();
            public byte[] NormalBgra = Array.Empty<byte>();
            public bool HasNormals => NormalBgra.Length > 0;
            // Same layout again; empty when there was no source texture to carry over, in which
            // case the material uses these factors as constants.
            public byte[] MetallicRoughnessPng = Array.Empty<byte>();
            public byte[] MetallicRoughnessBgra = Array.Empty<byte>();
            public bool HasMetallicRoughness => MetallicRoughnessBgra.Length > 0;
            public float MetallicFactor = 1f, RoughnessFactor = 1f;
            public int Width, Height;
            public float TexelsPerUnit, DetailTexelsPerUnit;
            public bool UsesAlpha;
            public int MaskedBillboards;
            public int Billboards, KeptCharts;

            // World-space triangle list.
            public List<Vector3> Positions = new();
            public List<Vector3> Normals = new();
            public List<Vector4> Tangents = new();   // xyz + handedness, glTF TANGENT
            public List<Vector2> Uvs = new();
            // Per output triangle: the billboard it belongs to, or -1 for kept mesh.
            public List<int> TriangleBillboard = new();

            public int TriangleCount => Positions.Count / 3;
        }

        // --- source extraction -------------------------------------------------------------------

        public static BakeSource ExtractSource(ModelRoot model, ModelFlattener.FlattenInput input)
        {
            var src = new BakeSource
            {
                Uvs = new Vector2[input.TriangleCount * 3],
                NormalUvs = new Vector2[input.TriangleCount * 3],
                MrUvs = new Vector2[input.TriangleCount * 3],
                Tangents = new Vector3[input.TriangleCount],
                TangentSigns = new float[input.TriangleCount],
                Material = new int[input.TriangleCount],
            };

            var materialIndex = new Dictionary<int, int>();   // glTF material -> SourceMaterial
            var textureIndex = new Dictionary<int, int>();    // glTF image -> SourceTexture
            var uvCache = new Dictionary<(int, int, int), Vector2[]?>();

            int Decode(SharpGLTF.Schema2.Image img)
            {
                if (textureIndex.TryGetValue(img.LogicalIndex, out int ti)) return ti;
                using var bmp = TextureAtlasUtil.LoadBitmap(img.Content.Content.ToArray());
                src.Textures.Add(new SourceTexture { Width = bmp.Width, Height = bmp.Height, Bgra = TextureAtlasUtil.ReadPixels(bmp) });
                return textureIndex[img.LogicalIndex] = src.Textures.Count - 1;
            }

            Vector2[]? UvSet(MeshPrimitive prim, int meshIdx, int primIdx, int set)
            {
                if (!uvCache.TryGetValue((meshIdx, primIdx, set), out var uvs))
                {
                    uvs = prim.VertexAccessors.TryGetValue($"TEXCOORD_{set}", out var acc) ? acc.AsVector2Array().ToArray() : null;
                    uvCache[(meshIdx, primIdx, set)] = uvs;
                }
                return uvs;
            }

            for (int t = 0; t < input.TriangleCount; t++)
            {
                var (meshIdx, primIdx, a, b, c) = input.Sources[t];
                var prim = model.LogicalMeshes[meshIdx].Primitives[primIdx];
                var mat = prim.Material;

                int key = mat?.LogicalIndex ?? -1;
                if (!materialIndex.TryGetValue(key, out int mi))
                {
                    var sm = new SourceMaterial();
                    var ch = mat?.FindChannel("BaseColor");
                    if (ch.HasValue)
                    {
                        sm.Factor = ch.Value.Color;
                        var img = ch.Value.Texture?.PrimaryImage;
                        if (img != null) sm.Texture = Decode(img);
                    }
                    var mrch = mat?.FindChannel("MetallicRoughness");
                    if (mrch.HasValue)
                    {
                        sm.Metallic = mrch.Value.Parameter.X;
                        sm.Roughness = mrch.Value.Parameter.Y;
                        var mrimg = mrch.Value.Texture?.PrimaryImage;
                        if (mrimg != null) sm.MrTexture = Decode(mrimg);
                    }
                    var nch = mat?.FindChannel("Normal");
                    var nimg = nch?.Texture?.PrimaryImage;
                    if (nimg != null)
                    {
                        sm.NormalTexture = Decode(nimg);
                        sm.NormalScale = nch!.Value.Parameter.X is > 0 and var scale ? scale : 1f;
                    }
                    src.Materials.Add(sm);
                    materialIndex[key] = mi = src.Materials.Count - 1;
                }
                src.Material[t] = mi;

                var colorCh = mat?.FindChannel("BaseColor");
                var uvs = UvSet(prim, meshIdx, primIdx, colorCh.HasValue ? colorCh.Value.TextureCoordinate : 0);
                if (uvs != null)
                {
                    src.Uvs[t * 3] = uvs[a];
                    src.Uvs[t * 3 + 1] = uvs[b];
                    src.Uvs[t * 3 + 2] = uvs[c];
                }

                var mrCh = mat?.FindChannel("MetallicRoughness");
                var mruvs = mrCh.HasValue ? UvSet(prim, meshIdx, primIdx, mrCh.Value.TextureCoordinate) : uvs;
                if (mruvs != null)
                {
                    src.MrUvs[t * 3] = mruvs[a];
                    src.MrUvs[t * 3 + 1] = mruvs[b];
                    src.MrUvs[t * 3 + 2] = mruvs[c];
                }

                var normalCh = mat?.FindChannel("Normal");
                var nuvs = normalCh.HasValue ? UvSet(prim, meshIdx, primIdx, normalCh.Value.TextureCoordinate) : uvs;
                if (nuvs != null)
                {
                    src.NormalUvs[t * 3] = nuvs[a];
                    src.NormalUvs[t * 3 + 1] = nuvs[b];
                    src.NormalUvs[t * 3 + 2] = nuvs[c];
                }
                (src.Tangents[t], src.TangentSigns[t]) = UvTangent(
                    input.Corners[t * 3], input.Corners[t * 3 + 1], input.Corners[t * 3 + 2],
                    src.NormalUvs[t * 3], src.NormalUvs[t * 3 + 1], src.NormalUvs[t * 3 + 2], input.Normals[t]);
            }
            return src;
        }

        // A triangle's tangent frame from its UVs: the tangent is the direction of increasing u,
        // and the sign makes cross(normal, tangent) * sign point up the image (decreasing v) -
        // the glTF convention, same as MikkTSpace's handedness for an unmirrored layout.
        internal static (Vector3 Tangent, float Sign) UvTangent(Vector3 p0, Vector3 p1, Vector3 p2,
            Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 normal)
        {
            var e1 = p1 - p0; var e2 = p2 - p0;
            var d1 = uv1 - uv0; var d2 = uv2 - uv0;
            float r = d1.X * d2.Y - d2.X * d1.Y;
            if (MathF.Abs(r) < 1e-12f) return (Vector3.Zero, 1f);
            var dPdu = (e1 * d2.Y - e2 * d1.Y) / r;
            var dPdv = (e2 * d1.X - e1 * d2.X) / r;
            return (dPdu, Vector3.Dot(Vector3.Cross(normal, dPdu), -dPdv) < 0 ? -1f : 1f);
        }

        // --- bake --------------------------------------------------------------------------------

        private sealed class Item
        {
            public int Billboard = -1;          // or a kept chart:
            public List<int>? Chart;
            public int ChartTexture = -1;
            public Vector2 ChartMinPx, ChartMaxPx;
            public float ChartDensity;          // source texels per world unit
            public int ChartMaterial;

            public int W, H;                    // including padding, at the chosen density
            public int X, Y;                    // placement
            public float ChartScale;            // atlas texels per source texel

            public bool Detail;                 // gets DetailBoost x the base density
            public float Scale = 1f;            // and this on top (a backdrop's fraction)
            public float SourceDensity;         // ceiling: its own source texels per world unit
        }



        public static BakeResult Bake(ModelFlattener.FlattenInput input, ModelFlattener.FlattenResult flat,
            BakeSource src, BakeSettings settings, CancellationToken ct)
        {
            float detailBoost = settings.DetailBoost;
            float globalDensity = SourceDensity(input, src, Enumerable.Range(0, input.TriangleCount));
            var items = new List<Item>();
            for (int i = 0; i < flat.Billboards.Count; i++)
            {
                var bb = flat.Billboards[i];
                float own = SourceDensity(input, src, bb.Triangles.Where(t => Vector3.Dot(input.Normals[t], bb.Normal) > 0.2f));
                // Side billboards are seen at a slant, so they don't get the boost either.
                // A curved billboard gets the boost too: rails and grilles in front of its
                // windows are drawn on it, and at the base density they wash out into the glass.
                items.Add(new Item { Billboard = i, Detail = !bb.Side && !bb.Backdrop && (bb.Coverage < ModelFlattener.DetailCoverage || bb.Curve != null), Scale = bb.DensityScale, SourceDensity = own > 0 ? own : globalDensity });
            }
            items.AddRange(BuildCharts(input, flat, src));

            detailBoost = Math.Max(1f, detailBoost);
            var (density, width, height) = ChooseLayout(flat, items, detailBoost);
            ct.ThrowIfCancellationRequested();

            var result = new BakeResult
            {
                Width = width,
                Height = height,
                TexelsPerUnit = density,
                DetailTexelsPerUnit = density * detailBoost,
                AtlasBgra = new byte[width * height * 4],
                NormalBgra = settings.BakeNormals ? FilledAtlas(width, height, FlatNormal) : Array.Empty<byte>(),
                MetallicRoughnessBgra = settings.BakeMetallicRoughness && src.Materials.Any(m => m.MrTexture >= 0)
                    ? FilledAtlas(width, height, MrTexel(DominantMaterial(input, src)))
                    : Array.Empty<byte>(),
                Billboards = flat.Billboards.Count,
                KeptCharts = items.Count - flat.Billboards.Count,
            };

            // Every item owns a disjoint rectangle of the atlas, so they can all be written in parallel.
            var masked = new bool[items.Count];
            Parallel.For(0, items.Count, new ParallelOptions { CancellationToken = ct }, i =>
            {
                var item = items[i];
                if (item.Billboard >= 0) masked[i] = RenderBillboard(input, flat, item.Billboard, src, item, settings, result);
                else CopyChart(src, item, result);
            });
            ct.ThrowIfCancellationRequested();

            if (!result.HasMetallicRoughness)
            {
                // A factor is only half the story when a texture multiplies it (a factor of 1
                // over a texture that's ~0 metallic is a matte wall), so skipping the texture
                // bakes its average into the constant.
                var dominant = DominantMaterial(input, src);
                result.MetallicFactor = dominant.Metallic;
                result.RoughnessFactor = dominant.Roughness;
                if (dominant.MrTexture >= 0)
                {
                    var tex = src.Textures[dominant.MrTexture];
                    double metallic = 0, roughness = 0;
                    int texels = tex.Width * tex.Height;
                    for (int i = 0; i < texels; i++) { metallic += tex.Bgra[i * 4]; roughness += tex.Bgra[i * 4 + 1]; }
                    result.MetallicFactor *= (float)(metallic / texels / 255);
                    result.RoughnessFactor *= (float)(roughness / texels / 255);
                }
            }

            result.MaskedBillboards = masked.Count(m => m);
            result.UsesAlpha = result.MaskedBillboards > 0;

            EmitGeometry(input, flat, items, src, settings, result);

            using (var bmp = TextureAtlasUtil.WritePixels(result.AtlasBgra, width, height))
                result.AtlasPng = TextureAtlasUtil.EncodePng(bmp);
            if (result.HasNormals)
                using (var bmp = TextureAtlasUtil.WritePixels(result.NormalBgra, width, height))
                    result.NormalPng = TextureAtlasUtil.EncodePng(bmp);
            if (result.HasMetallicRoughness)
                using (var bmp = TextureAtlasUtil.WritePixels(result.MetallicRoughnessBgra, width, height))
                    result.MetallicRoughnessPng = TextureAtlasUtil.EncodePng(bmp);
            return result;
        }

        // BGRA texel values: the flat normal (0, 0, 1), and a material's metallic (B) /
        // roughness (G) with R and A unused.
        private static readonly byte[] FlatNormal = { 255, 128, 128, 255 };

        private static byte[] MrTexel(SourceMaterial m) =>
            new[] { ToByte(m.Metallic * 255f), ToByte(m.Roughness * 255f), (byte)255, (byte)255 };

        private static byte ToByte(float v) => (byte)Math.Clamp(v + 0.5f, 0f, 255f);

        // Unused atlas space holds a neutral value, so filtering at a rectangle's edge never pulls
        // in garbage.
        private static byte[] FilledAtlas(int width, int height, byte[] texel)
        {
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++) System.Buffer.BlockCopy(texel, 0, bgra, i * 4, 4);
            return bgra;
        }

        // The material covering the most surface - its factors stand in for the whole model where
        // one value has to (the constant fallback, the atlas background).
        private static SourceMaterial DominantMaterial(ModelFlattener.FlattenInput input, BakeSource src)
        {
            var area = new double[src.Materials.Count];
            for (int t = 0; t < input.TriangleCount; t++) area[src.Material[t]] += input.Areas[t];
            int best = 0;
            for (int i = 1; i < area.Length; i++) if (area[i] > area[best]) best = i;
            return src.Materials.Count > 0 ? src.Materials[best] : new SourceMaterial();
        }

        // Source texels per world unit over the given triangles (0 if none are textured).
        private static float SourceDensity(ModelFlattener.FlattenInput input, BakeSource src, IEnumerable<int> tris)
        {
            double uvArea = 0, worldArea = 0;
            foreach (int t in tris)
            {
                int tex = src.Materials[src.Material[t]].Texture;
                if (tex < 0) continue;
                uvArea += UvPixelArea(src, t, src.Textures[tex]);
                worldArea += input.Areas[t];
            }
            return uvArea > 0 && worldArea > 0 ? (float)Math.Sqrt(uvArea / worldArea) : 0f;
        }

        private static float UvPixelArea(BakeSource src, int t, SourceTexture tex)
        {
            var scale = new Vector2(tex.Width, tex.Height);
            var a = src.Uvs[t * 3] * scale;
            var b = src.Uvs[t * 3 + 1] * scale;
            var c = src.Uvs[t * 3 + 2] * scale;
            return MathF.Abs(Cross2(b - a, c - a)) * 0.5f;
        }

        // Kept triangles grouped into their UV charts (triangles sharing a source vertex).
        private static List<Item> BuildCharts(ModelFlattener.FlattenInput input, ModelFlattener.FlattenResult flat, BakeSource src)
        {
            var kept = new List<int>();
            for (int t = 0; t < input.TriangleCount; t++)
                if (flat.Assignment[t] == ModelFlattener.FlattenResult.Kept) kept.Add(t);
            if (kept.Count == 0) return new List<Item>();

            var parent = Enumerable.Range(0, kept.Count).ToArray();
            int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
            var firstByVertex = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < kept.Count; i++)
            {
                var (m, p, a, b, c) = input.Sources[kept[i]];
                foreach (int v in new[] { a, b, c })
                {
                    if (firstByVertex.TryGetValue((m, p, v), out int j)) parent[Find(i)] = Find(j);
                    else firstByVertex[(m, p, v)] = i;
                }
            }

            var charts = new Dictionary<int, List<int>>();
            for (int i = 0; i < kept.Count; i++)
            {
                int r = Find(i);
                if (!charts.TryGetValue(r, out var list)) charts[r] = list = new List<int>();
                list.Add(kept[i]);
            }

            var items = new List<Item>();
            foreach (var tris in charts.Values)
            {
                int material = src.Material[tris[0]];
                int texIdx = src.Materials[material].Texture;
                // Not boosted: kept mesh keeps its real shape, and its many fragmented charts
                // would otherwise eat most of the atlas.
                var item = new Item { Chart = tris, ChartTexture = texIdx, ChartMaterial = material };
                if (texIdx >= 0)
                {
                    var tex = src.Textures[texIdx];
                    var scale = new Vector2(tex.Width, tex.Height);
                    var min = new Vector2(float.MaxValue);
                    var max = new Vector2(float.MinValue);
                    float uvArea = 0, worldArea = 0;
                    foreach (int t in tris)
                    {
                        for (int k = 0; k < 3; k++)
                        {
                            var px = src.Uvs[t * 3 + k] * scale;
                            min = Vector2.Min(min, px);
                            max = Vector2.Max(max, px);
                        }
                        uvArea += UvPixelArea(src, t, tex);
                        worldArea += input.Areas[t];
                    }
                    item.ChartMinPx = min;
                    item.ChartMaxPx = max;
                    item.ChartDensity = uvArea > 0 && worldArea > 0 ? MathF.Sqrt(uvArea / worldArea) : 0;
                    item.SourceDensity = item.ChartDensity;
                }
                items.Add(item);
            }
            return items;
        }

        private static float ItemDensity(Item item, float baseDensity, float detailBoost)
        {
            float d = baseDensity * (item.Detail ? detailBoost : 1f) * item.Scale;
            return item.SourceDensity > 0 ? MathF.Min(d, item.SourceDensity) : d;
        }

        private static void SizeItems(ModelFlattener.FlattenResult flat, List<Item> items, float baseDensity, float detailBoost)
        {
            foreach (var item in items)
            {
                float density = ItemDensity(item, baseDensity, detailBoost);
                if (item.Billboard >= 0)
                {
                    var bb = flat.Billboards[item.Billboard];
                    item.W = Math.Max(1, (int)MathF.Ceiling((bb.Max.X - bb.Min.X) * density)) + Pad * 2;
                    item.H = Math.Max(1, (int)MathF.Ceiling((bb.Max.Y - bb.Min.Y) * density)) + Pad * 2;
                }
                else if (item.ChartTexture < 0 || item.ChartDensity <= 0)
                {
                    item.ChartScale = 0;
                    item.W = item.H = 1 + Pad * 2;
                }
                else
                {
                    // Never upsampled, and capped so one badly-unwrapped chart with a huge UV
                    // footprint can't claim most of the atlas.
                    var span = item.ChartMaxPx - item.ChartMinPx;
                    float s = MathF.Min(1f, density / item.ChartDensity);
                    s = MathF.Min(s, (MaxAtlasSize / 4f) / MathF.Max(1f, MathF.Max(span.X, span.Y)));
                    item.ChartScale = s;
                    item.W = Math.Max(1, (int)MathF.Ceiling(span.X * s)) + Pad * 2;
                    item.H = Math.Max(1, (int)MathF.Ceiling(span.Y * s)) + Pad * 2;
                }
            }
        }

        private static (float Density, int Width, int Height) ChooseLayout(
            ModelFlattener.FlattenResult flat, List<Item> items, float detailBoost)
        {
            bool Fits(float d, int size)
            {
                SizeItems(flat, items, d, detailBoost);
                return Pack(items, size) >= 0;
            }

            // Past the highest source density nothing grows any more.
            float density = items.Count == 0 ? 1f : items.Max(i => i.SourceDensity);
            if (density <= 0) density = MaxAtlasSize;
            if (!Fits(density, MaxAtlasSize))
            {
                float lo = 0, hi = density;
                for (int i = 0; i < 16; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (Fits(mid, MaxAtlasSize)) lo = mid; else hi = mid;
                }
                density = lo;
            }

            int size = MaxAtlasSize;
            while (size > 64 && Fits(density, size / 2)) size /= 2;

            SizeItems(flat, items, density, detailBoost);
            int usedHeight = Pack(items, size);
            if (usedHeight < 0) throw new InvalidOperationException("Atlas packing failed.");
            int height = size;
            while (height > 64 && height / 2 >= usedHeight) height /= 2;
            return (density, size, height);
        }

        // Skyline bottom-left packing into a width x width square. Returns the height used, or
        // -1 if something didn't fit. Places items in place (X/Y).
        private static int Pack(List<Item> items, int width)
        {
            var order = items.OrderByDescending(i => i.H).ThenByDescending(i => i.W).ToList();
            var sky = new List<(int X, int Y, int W)> { (0, 0, width) };
            int used = 0;

            foreach (var item in order)
            {
                if (item.W > width) return -1;
                int bestY = int.MaxValue, bestX = 0, bestIndex = -1;
                for (int i = 0; i < sky.Count; i++)
                {
                    int x = sky[i].X;
                    if (x + item.W > width) break;
                    int y = 0, remaining = item.W;
                    for (int j = i; j < sky.Count && remaining > 0; j++)
                    {
                        y = Math.Max(y, sky[j].Y);
                        remaining -= sky[j].W;
                    }
                    if (y + item.H > width) continue;
                    if (y < bestY) { bestY = y; bestX = x; bestIndex = i; }
                }
                if (bestIndex < 0) return -1;

                item.X = bestX;
                item.Y = bestY;
                used = Math.Max(used, bestY + item.H);

                // Replace the covered span of the skyline with the new top edge.
                int end = bestX + item.W;
                var next = new List<(int X, int Y, int W)> { (bestX, bestY + item.H, item.W) };
                foreach (var seg in sky)
                {
                    int segEnd = seg.X + seg.W;
                    if (segEnd <= bestX || seg.X >= end) { next.Add(seg); continue; }
                    if (seg.X < bestX) next.Add((seg.X, seg.Y, bestX - seg.X));
                    if (segEnd > end) next.Add((end, seg.Y, segEnd - end));
                }
                next.Sort((a, b) => a.X.CompareTo(b.X));

                // Merge neighbours at the same height.
                sky.Clear();
                foreach (var seg in next)
                {
                    if (sky.Count > 0 && sky[^1].Y == seg.Y && sky[^1].X + sky[^1].W == seg.X)
                        sky[^1] = (sky[^1].X, sky[^1].Y, sky[^1].W + seg.W);
                    else sky.Add(seg);
                }
            }
            return used;
        }

        // --- billboard rendering -----------------------------------------------------------------

        private readonly struct RasterTri
        {
            public readonly Vector2 A, B, C;        // sample space
            public readonly float Za, Zb, Zc;       // depth toward the viewer
            public readonly float Area2;
            public readonly int Tri;

            public RasterTri(Vector2 a, Vector2 b, Vector2 c, float za, float zb, float zc, int tri)
            {
                A = a; B = b; C = c; Za = za; Zb = zb; Zc = zc; Tri = tri;
                Area2 = Cross2(b - a, c - a);
            }

            public bool Barycentric(Vector2 p, out float w0, out float w1, out float w2)
            {
                w0 = Cross2(C - B, p - B) / Area2;
                w1 = Cross2(A - C, p - C) / Area2;
                w2 = Cross2(B - A, p - A) / Area2;
                const float tol = -1e-5f;
                return w0 >= tol && w1 >= tol && w2 >= tol;
            }
        }

        // Returns true if the billboard needs alpha.
        private static bool RenderBillboard(ModelFlattener.FlattenInput input, ModelFlattener.FlattenResult flat, int index,
            BakeSource src, Item item, BakeSettings settings, BakeResult atlas)
        {
            var bb = flat.Billboards[index];
            int w = item.W - Pad * 2, h = item.H - Pad * 2;
            float sizeU = MathF.Max(bb.Max.X - bb.Min.X, 1e-9f), sizeV = MathF.Max(bb.Max.Y - bb.Min.Y, 1e-9f);
            float sx = w / sizeU * SS, sy = h / sizeV * SS;

            RasterTri Project(int t)
            {
                var curve = bb.Curve;
                Vector2 P(int k)
                {
                    var p = input.Corners[t * 3 + k];
                    var q = curve != null ? curve.Project(p) : new Vector2(Vector3.Dot(p, bb.AxisU), Vector3.Dot(p, bb.AxisV));
                    return new Vector2((q.X - bb.Min.X) * sx, (bb.Max.Y - q.Y) * sy);
                }
                float Z(int k) => curve != null ? curve.Depth(input.Corners[t * 3 + k]) : Vector3.Dot(input.Corners[t * 3 + k], bb.Normal);
                return new RasterTri(P(0), P(1), P(2), Z(0), Z(1), Z(2), t);
            }

            var own = new List<RasterTri>(bb.Triangles.Count);
            foreach (int t in bb.Triangles)
            {
                // A piece of detail shows only what faces it: what it holds edge-on (the top of a
                // coping on a strip of trim) draws as a thin line a little in front of where the
                // real edge is, and seen from below that line floats above the roofline. Fire-
                // escape parts draw everything in their box - the tread edges on a stair's side.
                if (bb.Detail && !bb.Part && Vector3.Dot(input.Normals[t], bb.Normal) < 0.5f) continue;
                var rt = Project(t);
                if (MathF.Abs(rt.Area2) > 1e-9f) own.Add(rt);
            }

            // Backfill candidates: everything else not dropped, facing this way, and overlapping
            // the rectangle, from a little in front of the plane (what's attached to the surface -
            // a bracket, a sill - not a pilaster across the fire escape) to well behind it (the
            // window or door set back in a hole: painted into the hole, it can't be looked past
            // at a slant the way a separate billboard behind can - the sky leaks round its edge).
            // The nearest wins, so what's in front takes precedence. Only for surfaces, not
            // see-through detail or side billboards - their gaps are real, not holes to paint over.
            var fill = new List<RasterTri>();
            float behind = -input.Extent * 0.1f;
            float reach = flat.Tolerance * 2f;
            bool backfill = !bb.Side && !bb.Detail && !bb.Backdrop && bb.Curve == null && bb.Coverage >= 0.5f;
            for (int t = 0; backfill && t < input.TriangleCount; t++)
            {
                int a = flat.Assignment[t];
                if (a == index || a == ModelFlattener.FlattenResult.Dropped) continue;
                if (Vector3.Dot(input.Normals[t], bb.Normal) <= 0.2f) continue;
                float front = MathF.Max(Vector3.Dot(input.Corners[t * 3], bb.Normal),
                    MathF.Max(Vector3.Dot(input.Corners[t * 3 + 1], bb.Normal), Vector3.Dot(input.Corners[t * 3 + 2], bb.Normal)));
                if (front < bb.Offset + behind || front > bb.Offset + reach) continue;
                var rt = Project(t);
                if (MathF.Abs(rt.Area2) <= 1e-9f) continue;
                float minX = MathF.Min(rt.A.X, MathF.Min(rt.B.X, rt.C.X)), maxX = MathF.Max(rt.A.X, MathF.Max(rt.B.X, rt.C.X));
                float minY = MathF.Min(rt.A.Y, MathF.Min(rt.B.Y, rt.C.Y)), maxY = MathF.Max(rt.A.Y, MathF.Max(rt.B.Y, rt.C.Y));
                if (maxX < 0 || maxY < 0 || minX > w * SS || minY > h * SS) continue;
                fill.Add(rt);
            }

            // Seen from below, what stands a little in front of the plane shows higher up than
            // where it's drawn and what's a little behind it lower down (a camera looking up at
            // a cornice sees its crown above the roofline drawn on the facade, and the moulding
            // under it through the slot between two layers of the front). So an upright
            // surface's own triangles are drawn again, shifted up by their depth in front of the
            // plane times the tangent of a few viewing angles from below (down, if behind it),
            // and those copies fill whatever the surface itself leaves empty. Not see-through
            // detail - its gaps are real.
            var sweep = new List<RasterTri>();
            if (Sweeps(bb))
            {
                float tiny = input.Extent * 1e-3f;
                foreach (var rt in own)
                {
                    float da = rt.Za - bb.Offset, db = rt.Zb - bb.Offset, dc = rt.Zc - bb.Offset;
                    if (MathF.Max(MathF.Abs(da), MathF.Max(MathF.Abs(db), MathF.Abs(dc))) < tiny) continue;
                    foreach (float tan in SweepTangents)
                        sweep.Add(new RasterTri(rt.A - new Vector2(0, da * tan * sy), rt.B - new Vector2(0, db * tan * sy),
                            rt.C - new Vector2(0, dc * tan * sy), rt.Za, rt.Zb, rt.Zc, rt.Tri));
                }
            }

            // Per texel: own colour/sample count, and backfill colour/count from samples no own
            // triangle reached.
            var color = new byte[w * h * 4];
            var hits = new byte[w * h];
            var fillColor = new byte[w * h * 4];
            var fillHits = new byte[w * h];
            var sweepColor = new byte[w * h * 4];
            var sweepHits = new byte[w * h];
            var sweepL = ExtraLayers(input, src, bb, atlas, w * h);
            var depth = new float[w * h];   // mean depth of the own samples; NaN where none
            var layers = ExtraLayers(input, src, bb, atlas, w * h);

            int sw = w * SS;
            int bands = (h + BandRows - 1) / BandRows;
            var ownBuckets = Bucket(own, bands);
            var fillBuckets = Bucket(fill, bands);
            var sweepBuckets = Bucket(sweep, bands);

            // Bands write disjoint rows, so they run in parallel - a card-sized billboard is one
            // item but hundreds of bands.
            Parallel.For(0, bands, band =>
            {
                int row0 = band * BandRows;
                int rows = Math.Min(BandRows, h - row0);
                int sRow0 = row0 * SS, sRows = rows * SS;
                var ownTri = RasterBand(own, ownBuckets[band], sw, sRow0, sRows);
                var fillTri = fill.Count > 0 ? RasterBand(fill, fillBuckets[band], sw, sRow0, sRows) : null;
                var sweepTri = sweep.Count > 0 ? RasterBand(sweep, sweepBuckets[band], sw, sRow0, sRows) : null;
                var sumSweepL = new Vector4[layers.Count];
                var sumOwnL = new Vector4[layers.Count];
                var sumFillL = new Vector4[layers.Count];

                // Resolve: shade each winning sample once, average per texel.
                for (int ty = 0; ty < rows; ty++)
                    for (int tx = 0; tx < w; tx++)
                    {
                        var sumOwn = Vector4.Zero;
                        var sumFill = Vector4.Zero;
                        var sumSweep = Vector4.Zero;
                        Array.Clear(sumOwnL);
                        Array.Clear(sumFillL);
                        Array.Clear(sumSweepL);
                        float sumDepth = 0;
                        int nOwn = 0, nFill = 0, nSweep = 0;
                        for (int j = 0; j < SS; j++)
                            for (int k = 0; k < SS; k++)
                            {
                                int sxI = tx * SS + k, syI = ty * SS + j;
                                var p = new Vector2(sxI + 0.5f, sRow0 + syI + 0.5f);
                                int i = ownTri[syI * sw + sxI];
                                if (i >= 0)
                                {
                                    sumOwn += ShadeSample(src, own[i], p);
                                    sumDepth += DepthAt(own[i], p);
                                    for (int l = 0; l < layers.Count; l++) sumOwnL[l] += layers[l].Sample(own[i], p);
                                    nOwn++;
                                    continue;
                                }
                                int sweepI = sweepTri == null ? -1 : sweepTri[syI * sw + sxI];
                                if (sweepI >= 0)
                                {
                                    sumSweep += ShadeSample(src, sweep[sweepI], p);
                                    for (int l = 0; l < layers.Count; l++) sumSweepL[l] += layers[l].Sample(sweep[sweepI], p);
                                    nSweep++;
                                }
                                int f = fillTri == null ? -1 : fillTri[syI * sw + sxI];
                                if (f >= 0)
                                {
                                    sumFill += ShadeSample(src, fill[f], p);
                                    for (int l = 0; l < layers.Count; l++) sumFillL[l] += layers[l].Sample(fill[f], p);
                                    nFill++;
                                }
                            }
                        int o = (row0 + ty) * w + tx;
                        depth[o] = nOwn > 0 ? sumDepth / nOwn : float.NaN;
                        if (nOwn > 0) { Store(color, o, sumOwn / nOwn); hits[o] = (byte)nOwn; }
                        if (nFill > 0) { Store(fillColor, o, sumFill / nFill); fillHits[o] = (byte)nFill; }
                        if (nSweep > 0) { Store(sweepColor, o, sumSweep / nSweep); sweepHits[o] = (byte)nSweep; }
                        for (int l = 0; l < layers.Count; l++)
                        {
                            layers[l].Store(layers[l].Own, o, sumOwnL[l], nOwn);
                            layers[l].Store(layers[l].Fill, o, sumFillL[l], nFill);
                            sweepL[l].Store(sweepL[l].Own, o, sumSweepL[l], nSweep);
                        }
                    }
            });

            StretchIntoWeld(bb, w, h, hits, fillHits, depth,
                layers.SelectMany(l => new[] { l.Own, l.Fill }).Append(color).Append(fillColor).ToList());

            // Alpha = coverage (times the source's own alpha).
            var alpha = new byte[w * h];
            var solid = new bool[w * h];
            for (int i = 0; i < w * h; i++)
            {
                alpha[i] = (byte)(hits[i] * color[i * 4 + 3] / (SS * SS));
                solid[i] = alpha[i] >= MaskCutoff * 255;
            }

            // Holes inside the surface take the backfill.
            if (fill.Count > 0)
            {
                var enclosed = BoundedHoles(solid, w, h);
                for (int i = 0; i < w * h; i++)
                {
                    if (!enclosed[i] || fillHits[i] == 0) continue;
                    int n = hits[i] + fillHits[i];
                    for (int c = 0; c < 4; c++)
                    {
                        color[i * 4 + c] = (byte)((color[i * 4 + c] * hits[i] + fillColor[i * 4 + c] * fillHits[i]) / n);
                        foreach (var layer in layers)
                            layer.Own[i * 4 + c] = (byte)((layer.Own[i * 4 + c] * hits[i] + layer.Fill[i * 4 + c] * fillHits[i]) / n);
                    }
                    hits[i] = (byte)n;
                    alpha[i] = (byte)(n * color[i * 4 + 3] / (SS * SS));
                    solid[i] = alpha[i] >= MaskCutoff * 255;
                }
            }

            // What the surface leaves empty takes the copies seen from below.
            for (int i = 0; i < w * h && sweep.Count > 0; i++)
            {
                if (solid[i] || sweepHits[i] == 0) continue;
                int n = hits[i] + sweepHits[i];
                for (int c = 0; c < 4; c++)
                {
                    color[i * 4 + c] = (byte)((color[i * 4 + c] * hits[i] + sweepColor[i * 4 + c] * sweepHits[i]) / n);
                    for (int l = 0; l < layers.Count; l++)
                        layers[l].Own[i * 4 + c] = (byte)((layers[l].Own[i * 4 + c] * hits[i] + sweepL[l].Own[i * 4 + c] * sweepHits[i]) / n);
                }
                hits[i] = (byte)Math.Min(n, SS * SS);
                alpha[i] = (byte)(hits[i] * color[i * 4 + 3] / (SS * SS));
                solid[i] = alpha[i] >= MaskCutoff * 255;
            }

            FillCracksAndPinholes(solid, alpha, w, h);

            // A backdrop is cut to the model's cross-section at its depth (Billboard.Clip). An
            // upright one only across: the front's cornice and parapet stand higher than the
            // building further back, and the backdrop has to reach up behind them.
            if (bb.Clip != null)
            {
                bool upright = MathF.Abs(bb.Normal.Y) < 0.5f;
                for (int y = 0; y < h; y++)
                {
                    float pv = bb.Max.Y - (y + 0.5f) / h * sizeV;
                    for (int x = 0; x < w; x++)
                        if (!bb.Clip.Contains(bb.Min.X + (x + 0.5f) / w * sizeU, pv, upright))
                        {
                            alpha[y * w + x] = 0;
                            solid[y * w + x] = false;
                        }
                }
            }
            // ...and to where it lies inside the building's skin (Billboard.Inside).
            if (bb.Inside != null)
            {
                for (int y = 0; y < h; y++)
                {
                    float pv = bb.Max.Y - (y + 0.5f) / h * sizeV;
                    for (int x = 0; x < w; x++)
                        if (!bb.Inside.Contains(bb.Min.X + (x + 0.5f) / w * sizeU, pv))
                        {
                            alpha[y * w + x] = 0;
                            solid[y * w + x] = false;
                        }
                }
            }

            // A grating floor is baked solid inside its outline (see Billboard.FillHoles); the
            // holes take the colour bled in from the slats around them.
            if (bb.FillHoles)
            {
                var enclosed = BoundedHoles(solid, w, h);
                for (int i = 0; i < w * h; i++)
                    if (enclosed[i]) { alpha[i] = 255; solid[i] = true; }
            }

            // A mostly solid piece set back behind a hole (see ExtendRecessed) is baked opaque:
            // whatever of it isn't its own surface - the corners around an arched door, the
            // extension behind the facade - is only ever seen past the hole's edge, where the
            // real model has the reveal, not sky.
            if (bb.Recessed && bb.Coverage >= 0.5f)
            {
                for (int y = 0; y < h; y++)
                {
                    float pv = bb.Max.Y - (y + 0.5f) / h * sizeV;
                    for (int x = 0; x < w; x++)
                    {
                        float pu = bb.Min.X + (x + 0.5f) / w * sizeU;
                        if (bb.RecessedBehind == null || bb.RecessedBehind.Any(f => f.Near(pu, pv)))
                        {
                            alpha[y * w + x] = 255;
                            solid[y * w + x] = true;
                        }
                    }
                }
            }

            if (settings.RecessDarkening > 0)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var rt in own)
                {
                    lo = MathF.Min(lo, MathF.Min(rt.Za, MathF.Min(rt.Zb, rt.Zc)));
                    hi = MathF.Max(hi, MathF.Max(rt.Za, MathF.Max(rt.Zb, rt.Zc)));
                }
                int radius = Math.Clamp((int)MathF.Round(input.Extent * 0.01f * w / sizeU), 2, 24);
                DarkenRecesses(color, depth, w, h, radius, MathF.Max((hi - lo) * 0.5f, 1e-6f), settings.RecessDarkening);
            }

            int solidCount = solid.Count(x => x);
            bool opaque = solidCount >= w * h * 0.999f;
            if (opaque) Array.Fill(alpha, (byte)255);

            // Push colour (and the extra layers) into every texel no sample reached, nearest-first.
            foreach (var layer in layers) BleedColor(layer.Own, hits, w, h);
            BleedColor(color, hits, w, h);

            // Write inner texels, then clamp-to-edge the padding.
            for (int y = -Pad; y < h + Pad; y++)
                for (int x = -Pad; x < w + Pad; x++)
                {
                    int cx = Math.Clamp(x, 0, w - 1), cy = Math.Clamp(y, 0, h - 1);
                    int s = cy * w + cx;
                    int d = ((item.Y + Pad + y) * atlas.Width + item.X + Pad + x) * 4;
                    atlas.AtlasBgra[d] = color[s * 4];
                    atlas.AtlasBgra[d + 1] = color[s * 4 + 1];
                    atlas.AtlasBgra[d + 2] = color[s * 4 + 2];
                    atlas.AtlasBgra[d + 3] = alpha[s];
                    foreach (var layer in layers)
                    {
                        layer.Atlas[d] = layer.Own[s * 4];
                        layer.Atlas[d + 1] = layer.Own[s * 4 + 1];
                        layer.Atlas[d + 2] = layer.Own[s * 4 + 2];
                    }
                }

            return !opaque;
        }

        // A per-texel channel baked alongside colour into its own atlas (normals, metallic/
        // roughness). Own and Fill are BGRA buffers over the billboard's texels, handled exactly
        // like the colour pair: own samples first, backfill samples where no own sample landed.
        private sealed class Layer
        {
            public byte[] Atlas = Array.Empty<byte>();
            public byte[] Own = Array.Empty<byte>(), Fill = Array.Empty<byte>();
            public Func<RasterTri, Vector2, Vector4> Sample = (_, _) => Vector4.Zero;
            // (buffer, texel, sum of Sample over the texel's samples, sample count)
            public Action<byte[], int, Vector4, int> Store = (_, _, _, _) => { };
        }

        private static List<Layer> ExtraLayers(ModelFlattener.FlattenInput input, BakeSource src,
            ModelFlattener.Billboard bb, BakeResult atlas, int texels)
        {
            var layers = new List<Layer>();
            if (atlas.HasNormals)
                layers.Add(new Layer
                {
                    Atlas = atlas.NormalBgra,
                    Sample = (rt, p) => new Vector4(NormalSample(input, src, rt, p), 0f),
                    Store = (buf, o, sum, n) => StoreNormal(buf, o, new Vector3(sum.X, sum.Y, sum.Z), bb),
                });
            if (atlas.HasMetallicRoughness)
            {
                var background = MrTexel(DominantMaterial(input, src));
                layers.Add(new Layer
                {
                    Atlas = atlas.MetallicRoughnessBgra,
                    Sample = (rt, p) => MetallicRoughnessSample(src, rt, p),
                    Store = (buf, o, sum, n) =>
                    {
                        if (n > 0) Store(buf, o, sum / n);
                        else System.Buffer.BlockCopy(background, 0, buf, o * 4, 4);
                    },
                });
            }
            foreach (var layer in layers)
            {
                layer.Own = new byte[texels * 4];
                layer.Fill = new byte[texels * 4];
            }
            return layers;
        }

        // Metallic (B) and roughness (G) at a sample, 0..255, source factors applied.
        private static Vector4 MetallicRoughnessSample(BakeSource src, RasterTri rt, Vector2 p)
        {
            var mat = src.Materials[src.Material[rt.Tri]];
            float metallic = mat.Metallic, roughness = mat.Roughness;
            if (mat.MrTexture >= 0)
            {
                rt.Barycentric(p, out float w0, out float w1, out float w2);
                int t = rt.Tri;
                var uv = src.MrUvs[t * 3] * w0 + src.MrUvs[t * 3 + 1] * w1 + src.MrUvs[t * 3 + 2] * w2;
                var texel = SampleBilinear(src.Textures[mat.MrTexture], uv);   // BGRA
                metallic *= texel.X / 255f;
                roughness *= texel.Y / 255f;
            }
            return new Vector4(metallic * 255f, roughness * 255f, 255f, 255f);
        }

        // The full model's shading normal at a sample, in world space: the interpolated vertex
        // normal, perturbed by the source normal map when the triangle's material has one.
        private static Vector3 NormalSample(ModelFlattener.FlattenInput input, BakeSource src, RasterTri rt, Vector2 p)
        {
            rt.Barycentric(p, out float w0, out float w1, out float w2);
            int t = rt.Tri;
            var n = input.VertexNormals[t * 3] * w0 + input.VertexNormals[t * 3 + 1] * w1 + input.VertexNormals[t * 3 + 2] * w2;
            n = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : input.Normals[t];

            var mat = src.Materials[src.Material[t]];
            if (mat.NormalTexture < 0 || src.Tangents[t].LengthSquared() < 1e-20f) return n;

            var uv = src.NormalUvs[t * 3] * w0 + src.NormalUvs[t * 3 + 1] * w1 + src.NormalUvs[t * 3 + 2] * w2;
            var texel = SampleBilinear(src.Textures[mat.NormalTexture], uv);   // BGRA 0..255
            float x = (texel.Z / 255f * 2f - 1f) * mat.NormalScale;
            float y = (texel.Y / 255f * 2f - 1f) * mat.NormalScale;
            float z = texel.X / 255f * 2f - 1f;

            var tangent = src.Tangents[t] - n * Vector3.Dot(src.Tangents[t], n);
            if (tangent.LengthSquared() < 1e-20f) return n;
            tangent = Vector3.Normalize(tangent);
            var bitangent = Vector3.Cross(n, tangent) * src.TangentSigns[t];
            var perturbed = tangent * x + bitangent * y + n * z;
            return perturbed.LengthSquared() > 1e-12f ? Vector3.Normalize(perturbed) : n;
        }

        // Averages a texel's world normals and stores them in the billboard's frame (+X = AxisU,
        // +Y = AxisV, +Z = Normal), encoded BGRA = z, y, x.
        private static void StoreNormal(byte[] bgra, int o, Vector3 sum, ModelFlattener.Billboard bb)
        {
            if (sum.LengthSquared() < 1e-12f) { bgra[o * 4] = 255; bgra[o * 4 + 1] = 128; bgra[o * 4 + 2] = 128; bgra[o * 4 + 3] = 255; return; }
            var n = Vector3.Normalize(sum);
            float x = Vector3.Dot(n, bb.AxisU), y = Vector3.Dot(n, bb.AxisV), z = Vector3.Dot(n, bb.Normal);
            // A normal facing away from the billboard (an edge-on or back-facing surface that
            // still won a sample) is folded to the horizon rather than flipped inside out.
            if (z < 0.05f)
            {
                var flat = new Vector2(x, y);
                flat = flat.LengthSquared() > 1e-12f ? Vector2.Normalize(flat) * MathF.Sqrt(1f - 0.05f * 0.05f) : Vector2.Zero;
                x = flat.X; y = flat.Y; z = 0.05f;
            }
            bgra[o * 4] = (byte)Math.Clamp((z * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
            bgra[o * 4 + 1] = (byte)Math.Clamp((y * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
            bgra[o * 4 + 2] = (byte)Math.Clamp((x * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
            bgra[o * 4 + 3] = 255;
        }

        private static float DepthAt(RasterTri rt, Vector2 p)
        {
            rt.Barycentric(p, out float w0, out float w1, out float w2);
            return rt.Za * w0 + rt.Zb * w1 + rt.Zc * w2;
        }

        // Where corner welding pushed the rectangle past the triangles (ContentMin/Max), the
        // extension has no geometry of its own; the content's edge texels are stretched across it
        // so the welded seam is closed by surface, not by a transparent strip.
        private static void StretchIntoWeld(ModelFlattener.Billboard bb, int w, int h,
            byte[] hits, byte[] fillHits, float[] depth, List<byte[]> bgraBuffers)
        {
            float sizeU = MathF.Max(bb.Max.X - bb.Min.X, 1e-9f), sizeV = MathF.Max(bb.Max.Y - bb.Min.Y, 1e-9f);
            int x0 = Math.Clamp((int)MathF.Floor((bb.ContentMin.X - bb.Min.X) * w / sizeU), 0, w - 1);
            int x1 = Math.Clamp((int)MathF.Ceiling((bb.ContentMax.X - bb.Min.X) * w / sizeU) - 1, x0, w - 1);
            int y0 = Math.Clamp((int)MathF.Floor((bb.Max.Y - bb.ContentMax.Y) * h / sizeV), 0, h - 1);
            int y1 = Math.Clamp((int)MathF.Ceiling((bb.Max.Y - bb.ContentMin.Y) * h / sizeV) - 1, y0, h - 1);
            // Only out to the welded rectangle - past it is room for the sweep fill.
            int wx0 = 0, wx1 = w - 1, wy0 = 0, wy1 = h - 1;
            if (bb.WeldedMin is Vector2 wmin && bb.WeldedMax is Vector2 wmax)
            {
                wx0 = Math.Clamp((int)MathF.Floor((wmin.X - bb.Min.X) * w / sizeU), 0, w - 1);
                wx1 = Math.Clamp((int)MathF.Ceiling((wmax.X - bb.Min.X) * w / sizeU) - 1, wx0, w - 1);
                wy0 = Math.Clamp((int)MathF.Floor((bb.Max.Y - wmax.Y) * h / sizeV), 0, h - 1);
                wy1 = Math.Clamp((int)MathF.Ceiling((bb.Max.Y - wmin.Y) * h / sizeV) - 1, wy0, h - 1);
            }
            if (x0 <= wx0 && y0 <= wy0 && x1 >= wx1 && y1 >= wy1) return;

            for (int y = wy0; y <= wy1; y++)
                for (int x = wx0; x <= wx1; x++)
                {
                    if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
                    int d = y * w + x, s = Math.Clamp(y, y0, y1) * w + Math.Clamp(x, x0, x1);
                    foreach (var buffer in bgraBuffers)
                        for (int c = 0; c < 4; c++) buffer[d * 4 + c] = buffer[s * 4 + c];
                    hits[d] = hits[s];
                    fillHits[d] = fillHits[s];
                    depth[d] = depth[s];
                }
        }

        // Each texel is darkened by how far it sits behind the nearest surface within `radius`
        // texels, relative to `scale` (half the billboard's own depth range), by up to `strength`.
        private static void DarkenRecesses(byte[] color, float[] depth, int w, int h, int radius, float scale, float strength)
        {
            // Separable sliding max, ignoring texels with no depth.
            var rowMax = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float m = float.NaN;
                    for (int k = Math.Max(0, x - radius); k <= Math.Min(w - 1, x + radius); k++)
                    {
                        float d = depth[y * w + k];
                        if (!float.IsNaN(d) && !(d <= m)) m = d;
                    }
                    rowMax[y * w + x] = m;
                }
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                {
                    int i = y * w + x;
                    if (float.IsNaN(depth[i])) continue;
                    float m = float.NaN;
                    for (int k = Math.Max(0, y - radius); k <= Math.Min(h - 1, y + radius); k++)
                    {
                        float d = rowMax[k * w + x];
                        if (!float.IsNaN(d) && !(d <= m)) m = d;
                    }
                    float recess = Math.Clamp((m - depth[i]) / scale, 0f, 1f);
                    float f = 1f - strength * recess;
                    for (int c = 0; c < 3; c++) color[i * 4 + c] = (byte)(color[i * 4 + c] * f + 0.5f);
                }
        }

        private static List<int>[] Bucket(List<RasterTri> tris, int bands)
        {
            var buckets = new List<int>[bands];
            for (int i = 0; i < bands; i++) buckets[i] = new List<int>();
            for (int i = 0; i < tris.Count; i++)
            {
                var rt = tris[i];
                float minY = MathF.Min(rt.A.Y, MathF.Min(rt.B.Y, rt.C.Y)) / SS;
                float maxY = MathF.Max(rt.A.Y, MathF.Max(rt.B.Y, rt.C.Y)) / SS;
                if (maxY < 0 || minY >= bands * BandRows) continue;
                int b0 = Math.Clamp((int)minY / BandRows, 0, bands - 1);
                int b1 = Math.Clamp((int)maxY / BandRows, 0, bands - 1);
                for (int b = b0; b <= b1; b++) buckets[b].Add(i);
            }
            return buckets;
        }

        // Depth-tested raster of one band of samples: for each sample, the index of the nearest
        // triangle covering it (nearest = furthest along the billboard normal), or -1.
        private static int[] RasterBand(List<RasterTri> tris, List<int> bucket, int sw, int sRow0, int sRows)
        {
            var winner = new int[sw * sRows];
            var depth = new float[sw * sRows];
            Array.Fill(winner, -1);
            Array.Fill(depth, float.NegativeInfinity);

            foreach (int i in bucket)
            {
                var rt = tris[i];
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(rt.A.X, MathF.Min(rt.B.X, rt.C.X))));
                int x1 = Math.Min(sw - 1, (int)MathF.Ceiling(MathF.Max(rt.A.X, MathF.Max(rt.B.X, rt.C.X))));
                int y0 = Math.Max(sRow0, (int)MathF.Floor(MathF.Min(rt.A.Y, MathF.Min(rt.B.Y, rt.C.Y))));
                int y1 = Math.Min(sRow0 + sRows - 1, (int)MathF.Ceiling(MathF.Max(rt.A.Y, MathF.Max(rt.B.Y, rt.C.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!rt.Barycentric(new Vector2(x + 0.5f, y + 0.5f), out float w0, out float w1, out float w2)) continue;
                        float z = rt.Za * w0 + rt.Zb * w1 + rt.Zc * w2;
                        int s = (y - sRow0) * sw + x;
                        if (z > depth[s]) { depth[s] = z; winner[s] = i; }
                    }
            }
            return winner;
        }

        private static Vector4 ShadeSample(BakeSource src, RasterTri rt, Vector2 p)
        {
            rt.Barycentric(p, out float w0, out float w1, out float w2);
            int t = rt.Tri;
            var uv = src.Uvs[t * 3] * w0 + src.Uvs[t * 3 + 1] * w1 + src.Uvs[t * 3 + 2] * w2;
            return Shade(src, src.Material[t], uv);
        }

        private static void Store(byte[] bgra, int o, Vector4 c)
        {
            bgra[o * 4] = (byte)(c.X + 0.5f);
            bgra[o * 4 + 1] = (byte)(c.Y + 0.5f);
            bgra[o * 4 + 2] = (byte)(c.Z + 0.5f);
            bgra[o * 4 + 3] = (byte)(c.W + 0.5f);
        }

        // Non-solid texels with own surface on both sides along their row or their column. Looser
        // than fully enclosed on purpose: the gap behind a corner pilaster runs out to the
        // rectangle's edge but is still inside the facade, while the sky beyond the silhouette
        // and a doorway at ground level have surface on at most one side of each line.
        private static bool[] BoundedHoles(bool[] solid, int w, int h)
        {
            var rowFirst = new int[h]; var rowLast = new int[h];
            var colFirst = new int[w]; var colLast = new int[w];
            Array.Fill(rowFirst, int.MaxValue); Array.Fill(rowLast, -1);
            Array.Fill(colFirst, int.MaxValue); Array.Fill(colLast, -1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!solid[y * w + x]) continue;
                    rowFirst[y] = Math.Min(rowFirst[y], x); rowLast[y] = Math.Max(rowLast[y], x);
                    colFirst[x] = Math.Min(colFirst[x], y); colLast[x] = Math.Max(colLast[x], y);
                }

            var result = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (solid[y * w + x]) continue;
                    result[y * w + x] = (x > rowFirst[y] && x < rowLast[y]) || (y > colFirst[x] && y < colLast[x]);
                }
            return result;
        }

        // Non-solid texels in components that don't reach the rectangle's border, up to maxSize
        // texels per component.
        private static bool[] EnclosedHoles(bool[] solid, int w, int h, int maxSize)
        {
            var result = new bool[w * h];
            var seen = new bool[w * h];
            var stack = new Stack<int>();
            var component = new List<int>();
            for (int start = 0; start < w * h; start++)
            {
                if (solid[start] || seen[start]) continue;
                component.Clear();
                bool touchesBorder = false;
                stack.Push(start);
                seen[start] = true;
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    component.Add(i);
                    int x = i % w, y = i / w;
                    if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touchesBorder = true;
                    if (x > 0 && !solid[i - 1] && !seen[i - 1]) { seen[i - 1] = true; stack.Push(i - 1); }
                    if (x < w - 1 && !solid[i + 1] && !seen[i + 1]) { seen[i + 1] = true; stack.Push(i + 1); }
                    if (y > 0 && !solid[i - w] && !seen[i - w]) { seen[i - w] = true; stack.Push(i - w); }
                    if (y < h - 1 && !solid[i + w] && !seen[i + w]) { seen[i + w] = true; stack.Push(i + w); }
                }
                if (touchesBorder || component.Count > maxSize) continue;
                foreach (int i in component) result[i] = true;
            }
            return result;
        }

        // Closing (dilate, then erode) seals cracks a texel or two wide; then any enclosed hole
        // no bigger than SmallHoleTexels is filled. Both kinds are scan artefacts - a real gap
        // (between rail posts, under an arch) is bigger than that at any useful texel density.
        private static void FillCracksAndPinholes(bool[] solid, byte[] alpha, int w, int h)
        {
            bool At(bool[] m, int x, int y) => x >= 0 && y >= 0 && x < w && y < h && m[y * w + x];

            var dilated = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool any = false;
                    for (int dy = -1; dy <= 1 && !any; dy++)
                        for (int dx = -1; dx <= 1 && !any; dx++) any = At(solid, x + dx, y + dy);
                    dilated[y * w + x] = any;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (solid[i] || !dilated[i]) continue;
                    // Eroding: stays set only if its whole neighbourhood is set (outside counts as
                    // set, so the rectangle's border doesn't erode inward).
                    bool all = true;
                    for (int dy = -1; dy <= 1 && all; dy++)
                        for (int dx = -1; dx <= 1 && all; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            all = dilated[ny * w + nx];
                        }
                    if (all) { solid[i] = true; alpha[i] = 255; }
                }

            // Small enclosed holes.
            var holes = EnclosedHoles(solid, w, h, SmallHoleTexels);
            for (int i = 0; i < w * h; i++)
                if (holes[i]) { solid[i] = true; alpha[i] = 255; }
        }

        // Breadth-first from every texel that has a colour: each empty texel takes the average
        // of its already-coloured neighbours, one ring at a time.
        private static void BleedColor(byte[] color, byte[] hits, int w, int h)
        {
            var known = new bool[w * h];
            var frontier = new List<int>();
            for (int i = 0; i < w * h; i++)
            {
                known[i] = hits[i] > 0;
                if (known[i]) continue;
                int x = i % w, y = i / w;
                if ((x > 0 && hits[i - 1] > 0) || (x < w - 1 && hits[i + 1] > 0) || (y > 0 && hits[i - w] > 0) || (y < h - 1 && hits[i + w] > 0))
                    frontier.Add(i);
            }
            if (!known.Any(k => k))
            {
                Array.Fill(color, (byte)128);
                return;
            }

            var queued = new bool[w * h];
            foreach (int i in frontier) queued[i] = true;
            while (frontier.Count > 0)
            {
                var next = new List<int>();
                var fills = new List<(int I, byte B, byte G, byte R)>();
                foreach (int i in frontier)
                {
                    int x = i % w, y = i / w, sb = 0, sg = 0, sr = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            int j = ny * w + nx;
                            if (!known[j]) { if (!queued[j]) { queued[j] = true; next.Add(j); } continue; }
                            sb += color[j * 4]; sg += color[j * 4 + 1]; sr += color[j * 4 + 2]; n++;
                        }
                    if (n > 0) fills.Add((i, (byte)(sb / n), (byte)(sg / n), (byte)(sr / n)));
                }
                foreach (var f in fills)
                {
                    color[f.I * 4] = f.B; color[f.I * 4 + 1] = f.G; color[f.I * 4 + 2] = f.R;
                    known[f.I] = true;
                }
                frontier = next;
            }
        }

        // --- kept mesh charts --------------------------------------------------------------------

        private static void CopyChart(BakeSource src, Item item, BakeResult atlas)
        {
            var mat = src.Materials[item.ChartMaterial];
            int w = item.W - Pad * 2, h = item.H - Pad * 2;

            // How many source samples per atlas texel per axis when shrinking, so the copy is
            // box-filtered rather than aliased.
            int taps = item.ChartScale > 0 ? Math.Clamp((int)MathF.Ceiling(1f / item.ChartScale), 1, 4) : 1;

            for (int y = -Pad; y < h + Pad; y++)
                for (int x = -Pad; x < w + Pad; x++)
                {
                    Vector4 c;
                    if (item.ChartScale <= 0 || item.ChartTexture < 0)
                    {
                        c = Shade(src, item.ChartMaterial, Vector2.Zero);
                    }
                    else
                    {
                        var tex = src.Textures[item.ChartTexture];
                        c = Vector4.Zero;
                        for (int j = 0; j < taps; j++)
                            for (int k = 0; k < taps; k++)
                            {
                                var px = item.ChartMinPx + new Vector2(x + (k + 0.5f) / taps, y + (j + 0.5f) / taps) / item.ChartScale;
                                c += Shade(src, item.ChartMaterial, new Vector2(px.X / tex.Width, px.Y / tex.Height));
                            }
                        c /= taps * taps;
                    }
                    int d = ((item.Y + Pad + y) * atlas.Width + item.X + Pad + x) * 4;
                    atlas.AtlasBgra[d] = (byte)(c.X + 0.5f);
                    atlas.AtlasBgra[d + 1] = (byte)(c.Y + 0.5f);
                    atlas.AtlasBgra[d + 2] = (byte)(c.Z + 0.5f);
                    atlas.AtlasBgra[d + 3] = 255;

                    // The chart's UVs are only moved and uniformly scaled, so its tangent frame -
                    // and so its normal texels - carry over unchanged. Same box filter as colour.
                    // Metallic/roughness: copied the same way, with the source factors baked in; a
                    // material with no texture of its own is just its factors.
                    if (atlas.HasMetallicRoughness)
                    {
                        var mr = new Vector4(255f, 255f, 255f, 255f);
                        if (mat.MrTexture >= 0 && item.ChartScale > 0)
                        {
                            var mtex = src.Textures[mat.MrTexture];
                            var colorTex = src.Textures[item.ChartTexture];
                            mr = Vector4.Zero;
                            for (int j = 0; j < taps; j++)
                                for (int k = 0; k < taps; k++)
                                {
                                    var px = item.ChartMinPx + new Vector2(x + (k + 0.5f) / taps, y + (j + 0.5f) / taps) / item.ChartScale;
                                    mr += SampleBilinear(mtex, new Vector2(px.X / colorTex.Width, px.Y / colorTex.Height));
                                }
                            mr /= taps * taps;
                        }
                        atlas.MetallicRoughnessBgra[d] = ToByte(mr.X * mat.Metallic);
                        atlas.MetallicRoughnessBgra[d + 1] = ToByte(mr.Y * mat.Roughness);
                    }

                    if (atlas.HasNormals && mat.NormalTexture >= 0 && item.ChartScale > 0)
                    {
                        var ntex = src.Textures[mat.NormalTexture];
                        var cn = Vector4.Zero;
                        for (int j = 0; j < taps; j++)
                            for (int k = 0; k < taps; k++)
                            {
                                var px = item.ChartMinPx + new Vector2(x + (k + 0.5f) / taps, y + (j + 0.5f) / taps) / item.ChartScale;
                                var colorTex = src.Textures[item.ChartTexture];
                                cn += SampleBilinear(ntex, new Vector2(px.X / colorTex.Width, px.Y / colorTex.Height));
                            }
                        cn /= taps * taps;
                        atlas.NormalBgra[d] = (byte)(cn.X + 0.5f);
                        atlas.NormalBgra[d + 1] = (byte)(cn.Y + 0.5f);
                        atlas.NormalBgra[d + 2] = (byte)(cn.Z + 0.5f);
                    }
                }
        }

        // --- shading -----------------------------------------------------------------------------

        // BGRA, 0..255. The base colour factor multiplies the sRGB texel directly - an
        // approximation, but factors on these models are 1 in practice. (COLOR_0 isn't used.)
        private static Vector4 Shade(BakeSource src, int material, Vector2 uv)
        {
            var mat = src.Materials[material];
            var f = new Vector4(mat.Factor.Z, mat.Factor.Y, mat.Factor.X, mat.Factor.W); // RGBA -> BGRA
            if (mat.Texture < 0) return f * 255f;
            return SampleBilinear(src.Textures[mat.Texture], uv) * f;
        }

        private static Vector4 SampleBilinear(SourceTexture tex, Vector2 uv)
        {
            float fx = uv.X * tex.Width - 0.5f, fy = uv.Y * tex.Height - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float ax = fx - x0, ay = fy - y0;
            int X(int x) => ((x % tex.Width) + tex.Width) % tex.Width;   // REPEAT wrap
            int Y(int y) => ((y % tex.Height) + tex.Height) % tex.Height;
            Vector4 Px(int x, int y)
            {
                int i = (Y(y) * tex.Width + X(x)) * 4;
                return new Vector4(tex.Bgra[i], tex.Bgra[i + 1], tex.Bgra[i + 2], tex.Bgra[i + 3]);
            }
            var top = Vector4.Lerp(Px(x0, y0), Px(x0 + 1, y0), ax);
            var bottom = Vector4.Lerp(Px(x0, y0 + 1), Px(x0 + 1, y0 + 1), ax);
            return Vector4.Lerp(top, bottom, ay);
        }

        // --- geometry ----------------------------------------------------------------------------

        private static void EmitGeometry(ModelFlattener.FlattenInput input, ModelFlattener.FlattenResult flat,
            List<Item> items, BakeSource src, BakeSettings settings, BakeResult result)
        {
            var atlasSize = new Vector2(result.Width, result.Height);

            foreach (var item in items)
            {
                if (item.Billboard >= 0)
                {
                    var bb = flat.Billboards[item.Billboard];
                    int w = item.W - Pad * 2, h = item.H - Pad * 2;
                    float left = item.X + Pad, top = item.Y + Pad;
                    float sizeU = bb.Max.X - bb.Min.X, sizeV = bb.Max.Y - bb.Min.Y;

                    // A curved billboard is a strip of flat pieces round its cylinder, the image
                    // laid along it by arc length.
                    if (bb.Curve is ModelFlattener.CurveFrame curve)
                    {
                        Vector3 At(Vector2 t) => curve.At(bb.Min.X + t.X / w * sizeU, bb.Max.Y - t.Y / h * sizeV);
                        void Emit(Vector2 a, Vector2 b, Vector2 c)
                        {
                            var pa = At(a);
                            var outward = curve.Outward(bb.Min.X + (a.X + b.X + c.X) / 3f / w * sizeU);
                            var face = Vector3.Cross(At(b) - pa, At(c) - pa);
                            var corners = Vector3.Dot(face, outward) >= 0 ? new[] { a, b, c } : new[] { a, c, b };
                            foreach (var t in corners)
                            {
                                float arc = bb.Min.X + t.X / w * sizeU;
                                result.Positions.Add(At(t));
                                result.Normals.Add(curve.Outward(arc));
                                result.Tangents.Add(new Vector4(Vector3.Cross(Vector3.UnitY, curve.Outward(arc)), 1f));
                                result.Uvs.Add(new Vector2(left + t.X, top + t.Y) / atlasSize);
                            }
                            result.TriangleBillboard.Add(item.Billboard);
                        }
                        // Pieces break at the outline's corners inside the billboard's span.
                        var breaks = new List<float> { 0f };
                        for (int k = 1; k + 1 < curve.Points.Count; k++)
                        {
                            float x = (curve.ArcAt(k) - bb.Min.X) / sizeU * w;
                            if (x > 0.5f && x < w - 0.5f) breaks.Add(x);
                        }
                        breaks.Add(w);
                        for (int i = 0; i + 1 < breaks.Count; i++)
                        {
                            float x0 = breaks[i], x1 = breaks[i + 1];
                            Emit(new Vector2(x0, h), new Vector2(x1, h), new Vector2(x1, 0));
                            Emit(new Vector2(x0, h), new Vector2(x1, 0), new Vector2(x0, 0));
                        }
                        continue;
                    }

                    // Outline in texel coordinates (y down), counter-clockwise seen from the
                    // front: bottom-left, bottom-right, top-right, top-left - V points up the
                    // billboard, image rows point down.
                    var outline = settings.TrimCorners
                        ? TrimmedOutline(result, item.X + Pad, item.Y + Pad, w, h)
                        : new List<Vector2> { new(0, h), new(w, h), new(w, 0), new(0, 0) };

                    Vector3 Pos(Vector2 t) => bb.Normal * bb.Offset
                        + bb.AxisU * (bb.Min.X + t.X / w * sizeU) + bb.AxisV * (bb.Max.Y - t.Y / h * sizeV);
                    // Tangent = AxisU, and cross(Normal, AxisU) = AxisV is already up the image.
                    for (int k = 1; k + 1 < outline.Count; k++, result.TriangleBillboard.Add(item.Billboard))
                        foreach (var t in new[] { outline[0], outline[k], outline[k + 1] })
                        {
                            result.Positions.Add(Pos(t));
                            result.Normals.Add(bb.Normal);
                            result.Tangents.Add(new Vector4(bb.AxisU, 1f));
                            result.Uvs.Add(new Vector2(left + t.X, top + t.Y) / atlasSize);
                        }
                    continue;
                }

                foreach (int t in item.Chart!)
                {
                    // Winding on scanned meshes is unreliable (they ship doubleSided); the flattened
                    // material is single-sided, so each kept triangle is wound to face the way its
                    // vertex normals say is out.
                    var p0 = input.Corners[t * 3];
                    var geometric = Vector3.Cross(input.Corners[t * 3 + 1] - p0, input.Corners[t * 3 + 2] - p0);
                    int[] order = Vector3.Dot(geometric, input.Normals[t]) < 0 ? new[] { 0, 2, 1 } : new[] { 0, 1, 2 };

                    // Unchanged by the chart's move-and-scale, so the source frame is still right.
                    var (tangent, sign) = (src.Tangents[t], src.TangentSigns[t]);
                    result.TriangleBillboard.Add(-1);

                    foreach (int k in order)
                    {
                        result.Positions.Add(input.Corners[t * 3 + k]);
                        var vn = input.VertexNormals[t * 3 + k];
                        vn = vn.LengthSquared() > 0 ? vn : input.Normals[t];
                        result.Normals.Add(vn);
                        var tn = tangent - vn * Vector3.Dot(tangent, vn);
                        result.Tangents.Add(tn.LengthSquared() > 1e-20f ? new Vector4(Vector3.Normalize(tn), sign) : new Vector4(AnyPerpendicular(vn), 1f));

                        Vector2 atlasPx;
                        if (item.ChartScale <= 0) atlasPx = new Vector2(item.X + Pad + 0.5f, item.Y + Pad + 0.5f);
                        else
                        {
                            var tex = src.Textures[item.ChartTexture];
                            var uvPx = src.Uvs[t * 3 + k] * new Vector2(tex.Width, tex.Height);
                            atlasPx = new Vector2(item.X + Pad, item.Y + Pad) + (uvPx - item.ChartMinPx) * item.ChartScale;
                        }
                        result.Uvs.Add(atlasPx / atlasSize);
                    }
                }
            }
        }

        // The billboard's rectangle with each empty corner cut off diagonally, as big a cut as
        // leaves every visible texel (plus a margin) inside. Conservative about "visible": the
        // game scales alpha up with mip level (MaskAlpha), so faint texels matter at a distance.
        // Each cut is limited to half of both edges it touches, which keeps the result convex
        // and simple, and is only taken if it removes a worthwhile share of the rectangle.
        private static List<Vector2> TrimmedOutline(BakeResult atlas, int x0, int y0, int w, int h)
        {
            const int margin = 2;
            const byte visibleAlpha = 32;
            var solid = new bool[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (atlas.AtlasBgra[((y0 + y) * atlas.Width + x0 + x) * 4 + 3] < visibleAlpha) continue;
                    for (int dy = -margin; dy <= margin; dy++)
                        for (int dx = -margin; dx <= margin; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx >= 0 && ny >= 0 && nx < w && ny < h) solid[ny * w + nx] = true;
                        }
                }

            // Corner-local frames: (flipX, flipY) maps local (x from corner, y from corner).
            (float A, float B) Cut(bool flipX, bool flipY)
            {
                int maxA = w / 2, maxB = h / 2;
                if (maxA < 2 || maxB < 2) return (0, 0);
                var colMin = new int[maxA];
                for (int lx = 0; lx < maxA; lx++)
                {
                    colMin[lx] = int.MaxValue;
                    int x = flipX ? w - 1 - lx : lx;
                    for (int ly = 0; ly < maxB; ly++)
                        if (solid[(flipY ? h - 1 - ly : ly) * w + x]) { colMin[lx] = ly; break; }
                }

                float bestA = 0, bestB = 0, bestArea = 0;
                int step = Math.Max(1, maxA / 128);
                for (int a = 2; a <= maxA; a += step)
                {
                    // A solid texel's nearest point to the corner is (x, y); it stays outside
                    // the cut x/a + y/b < 1 iff b <= y / (1 - x/a).
                    float b = maxB;
                    for (int x = 0; x < a && b > 0; x++)
                        if (colMin[x] != int.MaxValue) b = MathF.Min(b, colMin[x] / (1f - (float)x / a));
                    if (a * b > bestArea) { bestArea = a * b; bestA = a; bestB = b; }
                }
                // Worth a triangle only if it removes a real share of the rectangle AND a real
                // number of texels - on a small billboard the overdraw saved is negligible.
                float removed = bestArea * 0.5f;
                return removed >= 0.04f * w * h && removed >= MinTrimTexels && bestB >= 2 ? (bestA, bestB) : (0, 0);
            }

            var bl = Cut(false, true);
            var br = Cut(true, true);
            var tr = Cut(true, false);
            var tl = Cut(false, false);

            var outline = new List<Vector2>();
            void Corner((float A, float B) cut, Vector2 onVertical, Vector2 corner, Vector2 onHorizontal, bool verticalFirst)
            {
                if (cut.A <= 0) { outline.Add(corner); return; }
                if (verticalFirst) { outline.Add(onVertical); outline.Add(onHorizontal); }
                else { outline.Add(onHorizontal); outline.Add(onVertical); }
            }
            Corner(bl, new(0, h - bl.B), new(0, h), new(bl.A, h), verticalFirst: true);
            Corner(br, new(w, h - br.B), new(w, h), new(w - br.A, h), verticalFirst: false);
            Corner(tr, new(w, tr.B), new(w, 0), new(w - tr.A, 0), verticalFirst: true);
            Corner(tl, new(0, tl.B), new(0, 0), new(tl.A, 0), verticalFirst: false);
            return outline;
        }

        private static Vector3 AnyPerpendicular(Vector3 n) =>
            Vector3.Normalize(Vector3.Cross(n, MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));

        // --- output ------------------------------------------------------------------------------

        public const string FlattenedMarker = "glbMergerFlattened";

        // Positions/normals come out of the bake in world space; hostWorld is the world transform
        // of the node that will hold the mesh (identity for a standalone preview).
        public static MeshBuilder<VertexPositionNormalTangent, VertexTexture1> BuildMesh(BakeResult bake, Matrix4x4 hostWorld, string name)
        {
            Matrix4x4.Invert(hostWorld, out var toLocal);
            var normalMatrix = Matrix4x4.Transpose(hostWorld);

            var material = new MaterialBuilder(name)
                .WithMetallicRoughnessShader()
                .WithDoubleSide(false)
                .WithBaseColor(ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(bake.AtlasPng), name + "_atlas"));
            if (bake.UsesAlpha) material = material.WithAlpha(SharpGLTF.Materials.AlphaMode.MASK, MaskCutoff);
            if (bake.HasNormals) material = material.WithNormal(ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(bake.NormalPng), name + "_normal"));
            material = bake.HasMetallicRoughness
                ? material.WithMetallicRoughness(ImageBuilder.From(new SharpGLTF.Memory.MemoryImage(bake.MetallicRoughnessPng), name + "_metallicRoughness"), 1f, 1f)
                : material.WithMetallicRoughness(bake.MetallicFactor, bake.RoughnessFactor);

            var mesh = new MeshBuilder<VertexPositionNormalTangent, VertexTexture1>(name);
            mesh.Extras = new JsonObject { [FlattenedMarker] = true };
            var prim = mesh.UsePrimitive(material);

            (VertexPositionNormalTangent, VertexTexture1) V(int i)
            {
                var n = Vector3.TransformNormal(bake.Normals[i], normalMatrix);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : Vector3.UnitY;
                var t4 = bake.Tangents[i];
                var t = Vector3.TransformNormal(new Vector3(t4.X, t4.Y, t4.Z), toLocal);
                t -= n * Vector3.Dot(t, n);
                t = t.LengthSquared() > 1e-20f ? Vector3.Normalize(t) : AnyPerpendicular(n);
                return (new VertexPositionNormalTangent(Vector3.Transform(bake.Positions[i], toLocal), n, new Vector4(t, t4.W)),
                        new VertexTexture1(bake.Uvs[i]));
            }

            for (int i = 0; i + 2 < bake.Positions.Count; i += 3)
                prim.AddTriangle(V(i), V(i + 1), V(i + 2));
            return mesh;
        }

        // A standalone GLB of just the flattened result, for the editor's textured preview.
        public static void SavePreview(BakeResult bake, string path)
        {
            var scene = new SceneBuilder();
            scene.AddRigidMesh(BuildMesh(bake, Matrix4x4.Identity, "Flattened"), Matrix4x4.Identity);
            scene.ToGltf2().SaveGLB(path);
        }

        private static float Cross2(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    }
}
