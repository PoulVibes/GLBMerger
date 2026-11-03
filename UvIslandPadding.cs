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
    //
    // That flood trusts the island's own edge, and generated textures (Meshy, Tripo and friends)
    // often can't be trusted there: the bake leaves a thin dark rim round each island, a texel or
    // two deep, where the black background bled in. On an atlas cut into hundreds of tiny islands
    // those rims are all over the surface, and filtering at an island's edge reads them as short
    // dark needles. Padding alone carries the dark rim outward into the gutter, and painting over
    // it in a texture painter doesn't help either: a painter only writes the texels inside a
    // triangle's footprint, so the dark gutter texel just outside still gets blended into the edge.
    // CleanDarkRims fixes the rim first (see CleanRims), so the padding then carries out clean
    // colour. It's a brightness test, so it only runs on images a BaseColor channel binds.
    public static class UvIslandPadding
    {
        public sealed class Options
        {
            /// <summary>How far past an island's own edge to extend its color, in texels.</summary>
            public int PaddingTexels { get; set; } = 16;

            /// <summary>Replace dark rim texels at island edges with the island's own colour before padding (BaseColor images only).</summary>
            public bool CleanDarkRims { get; set; }

            /// <summary>When set, rims are only cleaned inside these triangles' footprints, by (mesh, primitive) - how a caller keeps a real dark outline.</summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? CleanOnlyTriangles { get; set; }
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
            public int TexelsCleaned { get; init; }

            /// <summary>Triangles whose footprint holds a cleaned texel, by (mesh, primitive) - what a preview highlights.</summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> CleanedTriangles { get; } = new();
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

            bool[]? cleaned = null;
            int cleanedCount = 0;
            if (options.CleanDarkRims && IsBaseColorImage(model, imageIndex))
            {
                bool[]? allowed = null;
                if (options.CleanOnlyTriangles != null)
                {
                    allowed = new bool[width * height];
                    RasterizeCoverage(tris.Where(t =>
                        options.CleanOnlyTriangles.TryGetValue((t.MeshIndex, t.PrimitiveIndex), out var set)
                        && set.Contains(t.TriangleIndex)).ToList(), width, height, allowed);
                }
                cleaned = new bool[width * height];
                cleanedCount = CleanRims(width, height, pixels, covered, cleaned, allowed);
            }

            int paddedCount = DilateColor(width, height, pixels, covered, options.PaddingTexels);

            using var resultBitmap = TextureAtlasUtil.WritePixels(pixels, width, height);
            byte[] pngBytes = TextureAtlasUtil.EncodePng(resultBitmap);

            var report = new Report
            {
                ImageIndex = imageIndex, Width = width, Height = height,
                TexelsCovered = coveredCount, TexelsPadded = paddedCount, TexelsCleaned = cleanedCount,
            };
            if (cleanedCount > 0)
            {
                foreach (var t in tris)
                {
                    if (!TouchesAny(t, width, height, cleaned!)) continue;
                    var key = (t.MeshIndex, t.PrimitiveIndex);
                    if (!report.CleanedTriangles.TryGetValue(key, out var set))
                        report.CleanedTriangles[key] = set = new HashSet<int>();
                    set.Add(t.TriangleIndex);
                }
            }
            return (report, pngBytes);
        }

        private static bool IsBaseColorImage(ModelRoot model, int imageIndex) =>
            model.LogicalMaterials.Any(mat => mat.Channels.Any(ch =>
                ch.Key == "BaseColor" && ch.Texture?.PrimaryImage?.LogicalIndex == imageIndex));

        // Writes a previously computed result onto the model.
        public static void Apply(ModelRoot model, int imageIndex, byte[] pngBytes) =>
            model.LogicalImages[imageIndex].Content = new SharpGLTF.Memory.MemoryImage(pngBytes);

        public static byte[] SnapshotContent(ModelRoot model, int imageIndex) =>
            model.LogicalImages[imageIndex].Content.Content.ToArray();

        // --- geometry gathering / rasterization -------------------------------------------------

        private readonly struct Tri2
        {
            public readonly Vector2 Uv0, Uv1, Uv2; // already in pixel space
            public readonly int MeshIndex, PrimitiveIndex, TriangleIndex;
            public Tri2(Vector2 uv0, Vector2 uv1, Vector2 uv2, int meshIndex, int primitiveIndex, int triangleIndex)
            {
                Uv0 = uv0; Uv1 = uv1; Uv2 = uv2;
                MeshIndex = meshIndex; PrimitiveIndex = primitiveIndex; TriangleIndex = triangleIndex;
            }
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
                            int triIdx = 0;
                            foreach (var (a, b, c) in prim.GetTriangleIndices())
                            {
                                tris.Add(new Tri2(
                                    new Vector2(uvs[a].X * width, uvs[a].Y * height),
                                    new Vector2(uvs[b].X * width, uvs[b].Y * height),
                                    new Vector2(uvs[c].X * width, uvs[c].Y * height),
                                    meshIdx, primIdx, triIdx++));
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
        // were filled. When given, `filled` is set for every texel the flood reached (covered ones
        // included).
        internal static int DilateColor(int width, int height, byte[] pixels, bool[] covered, int maxRadius, bool[]? filled = null)
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
                    if (filled != null) filled[nIdx] = true;
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

        // --- dark rim cleanup --------------------------------------------------------------------

        // A rim texel is one within RimDepth texels of its island's edge (8-neighbour steps); the
        // rest of the island is its core. A rim texel darker than DarkRatio times the core texel
        // nearest it is bake bleed, not detail.
        private const int RimDepth = 2;
        private const float DarkRatio = 0.65f;

        // Replaces each island's dark rim texels with the colour of the nearest core texel of the
        // same island, so padding afterwards carries clean colour out. Islands are the 8-connected
        // groups of covered texels; the floods below only step through covered texels, so they
        // never leave the island they started in and one island's colour can't reach another.
        //
        // An island too small to have a core RimDepth deep uses whatever is more than one texel in;
        // one with no interior at all is left alone, since there's nothing to compare its rim to.
        // A dark island (hair, a shadowed fold) has a dark core too, so its rim isn't dark by
        // comparison and stays - only a rim darker than what it surrounds is replaced. A deliberate
        // dark outline that runs along an island's edge looks the same as bleed and is lightened,
        // which is why callers preview the triangles this touched and can pass `allowed` - only
        // texels set in it are cleaned (they're still compared against the whole island).
        internal static int CleanRims(int width, int height, byte[] pixels, bool[] covered, bool[] cleaned, bool[]? allowed = null)
        {
            int count = width * height;

            // Depth into the island: 1 for a texel touching the gutter, growing inward.
            var depth = new int[count];
            var queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                if (!covered[i]) continue;
                int x = i % width, y = i / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + Dx8[k], ny = y + Dy8[k];
                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || !covered[ny * width + nx])
                    {
                        depth[i] = 1;
                        queue.Enqueue(i);
                        break;
                    }
                }
            }
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                ForEachCoveredNeighbour(idx, width, height, covered, n =>
                {
                    if (depth[n] != 0) return;
                    depth[n] = depth[idx] + 1;
                    queue.Enqueue(n);
                });
            }

            // Label islands, and note the deepest texel each reaches to pick its core depth.
            var island = new int[count];
            var deepest = new List<int> { 0 };
            for (int i = 0; i < count; i++)
            {
                if (!covered[i] || island[i] != 0) continue;
                int label = deepest.Count, maxDepth = 0;
                deepest.Add(0);
                island[i] = label;
                queue.Enqueue(i);
                while (queue.Count > 0)
                {
                    int idx = queue.Dequeue();
                    maxDepth = Math.Max(maxDepth, depth[idx]);
                    ForEachCoveredNeighbour(idx, width, height, covered, n =>
                    {
                        if (island[n] != 0) return;
                        island[n] = label;
                        queue.Enqueue(n);
                    });
                }
                deepest[label] = maxDepth;
            }

            int CoreDepth(int idx)
            {
                int d = deepest[island[idx]];
                return d > RimDepth ? RimDepth + 1 : d > 1 ? 2 : int.MaxValue;
            }

            // Nearest core texel to every texel of its island, by flooding out from the cores.
            var source = new int[count];
            Array.Fill(source, -1);
            for (int i = 0; i < count; i++)
            {
                if (covered[i] && depth[i] >= CoreDepth(i)) { source[i] = i; queue.Enqueue(i); }
            }
            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                ForEachCoveredNeighbour(idx, width, height, covered, n =>
                {
                    if (source[n] != -1) return;
                    source[n] = source[idx];
                    queue.Enqueue(n);
                });
            }

            int cleanedCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (source[i] == -1 || source[i] == i) continue;
                if (allowed != null && !allowed[i]) continue;
                if (Luminance(pixels, i) >= DarkRatio * Luminance(pixels, source[i])) continue;
                cleaned[i] = true;
                cleanedCount++;
            }
            if (cleanedCount == 0) return 0;

            // Each dark rim texel takes the colour of the core texel it was judged against. Not the
            // nearest acceptable texel: that can be a rim texel only just above DarkRatio of its
            // own core, which is still too dark for a brighter core next door - and every later
            // run would then find, and "clean", the same texels again.
            for (int i = 0; i < count; i++)
            {
                if (!cleaned[i]) continue;
                int s = source[i];
                pixels[i * 4 + 0] = pixels[s * 4 + 0];
                pixels[i * 4 + 1] = pixels[s * 4 + 1];
                pixels[i * 4 + 2] = pixels[s * 4 + 2];
            }
            return cleanedCount;
        }

        private static void ForEachCoveredNeighbour(int idx, int width, int height, bool[] covered, Action<int> visit)
        {
            int x = idx % width, y = idx / width;
            for (int k = 0; k < 8; k++)
            {
                int nx = x + Dx8[k], ny = y + Dy8[k];
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue;
                int n = ny * width + nx;
                if (covered[n]) visit(n);
            }
        }

        // Pixels are BGRA (TextureAtlasUtil.ReadPixels).
        private static float Luminance(byte[] pixels, int idx) =>
            0.114f * pixels[idx * 4] + 0.587f * pixels[idx * 4 + 1] + 0.299f * pixels[idx * 4 + 2];

        // Whether any texel centre inside the triangle's footprint is set in `mask` - the same
        // inside test RasterizeCoverage uses.
        private static bool TouchesAny(Tri2 t, int width, int height, bool[] mask)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.Uv0.X, MathF.Min(t.Uv1.X, t.Uv2.X))));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(t.Uv0.X, MathF.Max(t.Uv1.X, t.Uv2.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(t.Uv0.Y, MathF.Min(t.Uv1.Y, t.Uv2.Y))));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(t.Uv0.Y, MathF.Max(t.Uv1.Y, t.Uv2.Y))));
            float areaX2 = Cross2D(t.Uv1 - t.Uv0, t.Uv2 - t.Uv0);
            if (MathF.Abs(areaX2) < 1e-6f) return false;

            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    if (!mask[py * width + px]) continue;
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    float w0 = Cross2D(t.Uv2 - t.Uv1, p - t.Uv1);
                    float w1 = Cross2D(t.Uv0 - t.Uv2, p - t.Uv2);
                    float w2 = Cross2D(t.Uv1 - t.Uv0, p - t.Uv0);
                    bool inside = areaX2 > 0
                        ? (w0 >= 0 && w1 >= 0 && w2 >= 0)
                        : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (inside) return true;
                }
            }
            return false;
        }
    }
}
