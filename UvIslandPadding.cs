using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Extends each UV island's own edge color outward into the unused gutter around it, so
    // mip-mapped (or otherwise minified) sampling from a distance blends with more of the same
    // island instead of whatever unrelated content happens to be packed next to it in the atlas.
    //
    // Up close this is invisible - a full-resolution sample lands squarely inside its own island -
    // but from a distance, a lower mip level averages a wider patch of texels, and an unpadded
    // gutter means that average pulls in a neighboring island's baked color across the seam,
    // reading as a faint color fringe along the seam.
    //
    // Purely a 2D image-space fill: padding doesn't care what the gutter "should" look like - any
    // island's own edge color is a better fallback there than whatever happens to already occupy
    // that unused space, since nothing samples deep into the gutter, only its first few texels at
    // minification. So this is a straight multi-source flood of RGBA color, seeded from every texel
    // a triangle's UV footprint actually reaches - across every material channel that binds this
    // image, not just BaseColor, since padding helps a normal or ORM map exactly as much as albedo.
    public static class UvIslandPadding
    {
        public sealed class Options
        {
            /// <summary>How far past an island's own edge to extend its color, in texels.</summary>
            public int PaddingTexels { get; set; } = 16;
        }

        public sealed class ImageTarget
        {
            public int ImageIndex { get; init; }
            public string Label { get; init; } = "";
        }

        public sealed class Report
        {
            public int ImageIndex { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public int TexelsCovered { get; init; }
            public int TexelsPadded { get; init; }
        }

        // Every image bound by at least one material channel, labelled with the material name(s)
        // that use it - a texture shared across materials/channels shows up once.
        public static List<ImageTarget> FindImages(ModelRoot model)
        {
            var byImage = new Dictionary<int, List<string>>();
            foreach (var mat in model.LogicalMaterials)
            {
                foreach (var ch in mat.Channels)
                {
                    var img = ch.Texture?.PrimaryImage;
                    if (img == null) continue;
                    if (!byImage.TryGetValue(img.LogicalIndex, out var names))
                        byImage[img.LogicalIndex] = names = new List<string>();
                    names.Add(mat.Name ?? $"material_{mat.LogicalIndex}");
                }
            }

            return byImage
                .Select(kv => new ImageTarget { ImageIndex = kv.Key, Label = string.Join(", ", kv.Value.Distinct()) })
                .OrderBy(t => t.ImageIndex)
                .ToList();
        }

        // Computes the padded result without touching the model - lets a preview be shown before
        // committing, same Analyze/Apply split the other editors use.
        public static (Report Report, byte[] PngBytes) Process(ModelRoot model, int imageIndex, Options options)
        {
            byte[] originalBytes = model.LogicalImages[imageIndex].Content.Content.ToArray();

            using var srcBitmap = TextureAtlasUtil.LoadBitmap(originalBytes);
            int width = srcBitmap.Width, height = srcBitmap.Height;

            var tris = GatherTriangles(model, imageIndex, width, height);
            var covered = new bool[width * height];
            RasterizeCoverage(tris, width, height, covered);
            int coveredCount = covered.Count(c => c);

            var pixels = TextureAtlasUtil.ReadPixels(srcBitmap);
            int paddedCount = DilateColor(width, height, pixels, covered, options.PaddingTexels);

            using var resultBitmap = TextureAtlasUtil.WritePixels(pixels, width, height);
            byte[] pngBytes = TextureAtlasUtil.EncodePng(resultBitmap);

            var report = new Report
            {
                ImageIndex = imageIndex, Width = width, Height = height,
                TexelsCovered = coveredCount, TexelsPadded = paddedCount,
            };
            return (report, pngBytes);
        }

        // Writes a previously computed result onto the model.
        public static void Apply(ModelRoot model, int imageIndex, byte[] pngBytes) =>
            model.LogicalImages[imageIndex].Content = new SharpGLTF.Memory.MemoryImage(pngBytes);

        public static byte[] SnapshotContent(ModelRoot model, int imageIndex) =>
            model.LogicalImages[imageIndex].Content.Content.ToArray();

        // --- geometry gathering / rasterization -------------------------------------------------

        private readonly struct Tri2
        {
            public readonly Vector2 Uv0, Uv1, Uv2; // already in pixel space
            public Tri2(Vector2 uv0, Vector2 uv1, Vector2 uv2) { Uv0 = uv0; Uv1 = uv1; Uv2 = uv2; }
        }

        // Every triangle from every primitive whose material binds this image on ANY channel - the
        // one deliberate difference from TextureAtlasUtil.GatherTriangles, which only follows
        // BaseColor because its callers edit paint. A UV footprint is a UV footprint regardless of which
        // channel put it there, and padding a normal or ORM map matters exactly as much as albedo.
        private static List<Tri2> GatherTriangles(ModelRoot model, int imageIndex, int width, int height)
        {
            var tris = new List<Tri2>();

            // The same (primitive, TEXCOORD set) pair can be reached by more than one channel (e.g.
            // BaseColor and Emissive both sampling TEXCOORD_0) - gather it once, since its UV
            // footprint is identical either way.
            var seen = new HashSet<(int MeshIndex, int PrimIndex, int TexCoord)>();

            foreach (var mat in model.LogicalMaterials)
            {
                foreach (var ch in mat.Channels)
                {
                    var img = ch.Texture?.PrimaryImage;
                    if (img == null || img.LogicalIndex != imageIndex) continue;

                    string uvName = $"TEXCOORD_{ch.TextureCoordinate}";

                    for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
                    {
                        var mesh = model.LogicalMeshes[meshIdx];
                        for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                        {
                            var prim = mesh.Primitives[primIdx];
                            if (prim.Material?.LogicalIndex != mat.LogicalIndex) continue;
                            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                            if (!prim.VertexAccessors.TryGetValue(uvName, out var uvAcc)) continue;
                            if (!seen.Add((meshIdx, primIdx, ch.TextureCoordinate))) continue;

                            var uvs = uvAcc.AsVector2Array();
                            foreach (var (a, b, c) in prim.GetTriangleIndices())
                            {
                                tris.Add(new Tri2(
                                    new Vector2(uvs[a].X * width, uvs[a].Y * height),
                                    new Vector2(uvs[b].X * width, uvs[b].Y * height),
                                    new Vector2(uvs[c].X * width, uvs[c].Y * height)));
                            }
                        }
                    }
                }
            }

            return tris;
        }

        private static void RasterizeCoverage(List<Tri2> tris, int width, int height, bool[] covered)
        {
            foreach (var t in tris)
            {
                float minX = MathF.Min(t.Uv0.X, MathF.Min(t.Uv1.X, t.Uv2.X));
                float maxX = MathF.Max(t.Uv0.X, MathF.Max(t.Uv1.X, t.Uv2.X));
                float minY = MathF.Min(t.Uv0.Y, MathF.Min(t.Uv1.Y, t.Uv2.Y));
                float maxY = MathF.Max(t.Uv0.Y, MathF.Max(t.Uv1.Y, t.Uv2.Y));

                int x0 = Math.Max(0, (int)MathF.Floor(minX));
                int x1 = Math.Min(width - 1, (int)MathF.Ceiling(maxX));
                int y0 = Math.Max(0, (int)MathF.Floor(minY));
                int y1 = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
                if (x1 < x0 || y1 < y0) continue;

                float areaX2 = Cross2D(t.Uv1 - t.Uv0, t.Uv2 - t.Uv0);
                if (MathF.Abs(areaX2) < 1e-6f) continue; // degenerate in UV space (zero footprint)

                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        var p = new Vector2(px + 0.5f, py + 0.5f);

                        float w0 = Cross2D(t.Uv2 - t.Uv1, p - t.Uv1);
                        float w1 = Cross2D(t.Uv0 - t.Uv2, p - t.Uv2);
                        float w2 = Cross2D(t.Uv1 - t.Uv0, p - t.Uv0);

                        bool inside = areaX2 > 0
                            ? (w0 >= 0 && w1 >= 0 && w2 >= 0)
                            : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                        if (inside) covered[py * width + px] = true;
                    }
                }
            }
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static readonly int[] Dx8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] Dy8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        // Multi-source BFS from every covered texel, flooding outward up to maxRadius texels and
        // stamping each newly-reached texel with the color of whichever covered texel it was
        // reached from, carrying color outward one texel-ring at a time. Returns how many texels
        // were filled.
        private static int DilateColor(int width, int height, byte[] pixels, bool[] covered, int maxRadius)
        {
            if (maxRadius <= 0) return 0;

            int count = width * height;
            var dist = new int[count];
            var queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                dist[i] = covered[i] ? 0 : -1;
                if (covered[i]) queue.Enqueue(i);
            }

            int paddedCount = 0;
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int d = dist[idx];
                if (d >= maxRadius) continue;

                int x = idx % width, y = idx / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + Dx8[k], ny = y + Dy8[k];
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;

                    int nIdx = ny * width + nx;
                    if (dist[nIdx] != -1) continue;

                    dist[nIdx] = d + 1;
                    pixels[nIdx * 4 + 0] = pixels[idx * 4 + 0];
                    pixels[nIdx * 4 + 1] = pixels[idx * 4 + 1];
                    pixels[nIdx * 4 + 2] = pixels[idx * 4 + 2];
                    // Alpha is forced fully opaque in the gutter rather than copied - a padded texel
                    // exists specifically to be sampled, and inheriting a low source alpha would
                    // make the fix invisible in exactly the case it's meant to help.
                    pixels[nIdx * 4 + 3] = 255;
                    paddedCount++;
                    queue.Enqueue(nIdx);
                }
            }
            return paddedCount;
        }
    }
}
