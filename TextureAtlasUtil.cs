using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Replaces baked-in dark outlines/cracks on albedo textures outright, recoloring every dark
    // texel with the nearest non-dark color rather than partially fading it - a full swap, not an
    // erosion. Optionally restricted to a painted region of the mesh (see TriangleSelection),
    // which is what keeps the fill-color search from ever crossing into an unrelated part of a
    // busy, multi-island texture atlas: both what counts as "line" and what's eligible as a fill
    // source are drawn only from the painted triangles, so a texel can only ever pick up color
    // from somewhere the user actually indicated was the same intended patch.
    //
    // The "nearby color" lookup works in 3D, on the mesh surface, not in the texture's raw 2D
    // pixel grid. A 2D image-space search can't tell a UV seam from open space: two texels that
    // sit right next to each other in the atlas can belong to completely unrelated parts of the
    // model, and pulling color across that boundary bleeds the wrong material into the line. Every
    // texel that has geometry mapped to it gets a 3D position (by rasterizing each triangle's UV
    // footprint and barycentric-interpolating its POSITION), and the fill color for a dark texel
    // is sourced from whichever non-dark texel is closest to it ON THE SURFACE, which is what
    // actually respects seams.
    //
    // Only albedo (baseColorTexture) images are ever touched - normal maps, ORM, emissive etc.
    // encode surface properties, not paint, and replacing their "dark" pixels would corrupt them.
    public static class TextureLineThinner
    {
        public sealed class Options
        {
            /// <summary>A pixel with luminance at or below this (0-255) counts as "line".</summary>
            public byte Threshold { get; set; } = 40;

            /// <summary>
            /// How much brighter than Threshold a texel must be to count as a fill SOURCE, as
            /// opposed to merely "not dark enough to erode". Without this, the nearest non-line
            /// neighbor to a line texel is almost always the anti-aliased transition pixel right at
            /// the edge of the line - only barely above Threshold - and recoloring with that muddy
            /// border color reads as smudged rather than genuinely replaced. Raising this forces
            /// the search to skip past that transition band and pull from texels that are
            /// unambiguously the surrounding color.
            /// </summary>
            public byte FillConfidenceMargin { get; set; } = 40;

            /// <summary>
            /// How far, in texels, mapped surface data is carried into texels no triangle's UV
            /// footprint actually reaches - the gutter between tightly packed (or unpadded) UV
            /// islands. Those gaps commonly render as a dark seam network of their own, indistinct
            /// from painted line art, and without this they're invisible to the rest of this class:
            /// a texel with no triangle over it has no 3D position and gets skipped outright.
            /// </summary>
            public int GutterPaddingTexels { get; set; } = 8;

            /// <summary>
            /// Restricts both which texels are eligible to be treated as "line" and which are
            /// eligible as a fill source to a painted subset of triangles, per primitive - the same
            /// (MeshIndex, PrimitiveIndex) -> triangle-index shape GeometryOptimizer's own painted
            /// selection uses. Null, or every set empty, falls back to the whole image (every
            /// triangle that binds this texture as albedo), same as before painting existed.
            /// </summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? TriangleSelection { get; set; }

            /// <summary>
            /// When a line sits between two different colors, plain nearest-3D-neighbor has no
            /// concept of which side is "correct" - it just takes whichever fill source happens to
            /// be geometrically closest, which can flip unpredictably along a single line. Setting
            /// this biases the search toward fill sources close (within FavorColorTolerance) to
            /// this color first, only falling back to the unrestricted nearest neighbor where
            /// nothing matching is reachable - so a line between yellow and white can be told to
            /// pull from the white side specifically, rather than picking whichever is closer.
            /// </summary>
            public (byte R, byte G, byte B)? FavorColor { get; set; }

            /// <summary>Maximum per-channel difference from FavorColor still considered a match.</summary>
            public byte FavorColorTolerance { get; set; } = 40;
        }

        public sealed class AlbedoTarget
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
            public int TexelsLine { get; init; }
            public int TexelsReplaced { get; init; }
        }

        // Every image used as a BaseColor texture by at least one material, labelled with the
        // material name(s) that use it - a texture shared across materials shows up once.
        public static List<AlbedoTarget> FindAlbedoImages(ModelRoot model)
        {
            var byImage = new Dictionary<int, List<string>>();
            foreach (var mat in model.LogicalMaterials)
            {
                var ch = mat.FindChannel("BaseColor");
                if (!ch.HasValue) continue;
                var img = ch.Value.Texture?.PrimaryImage;
                if (img == null) continue;

                if (!byImage.TryGetValue(img.LogicalIndex, out var names))
                    byImage[img.LogicalIndex] = names = new List<string>();
                names.Add(mat.Name ?? $"material_{mat.LogicalIndex}");
            }

            return byImage
                .Select(kv => new AlbedoTarget { ImageIndex = kv.Key, Label = string.Join(", ", kv.Value) })
                .OrderBy(t => t.ImageIndex)
                .ToList();
        }

        // Computes the replaced result without touching the model - lets a preview be shown before
        // committing, same split as GeometryOptimizer's Analyze/Apply.
        //
        // DiagnosticPngBytes is a debug view of the SAME run, color-coding what each texel was
        // classified/resolved as instead of showing the actual recolor: magenta where the texel is
        // outside the painted region (or, with nothing painted, where no triangle's UV footprint
        // reaches at all, even after gutter dilation); red where a texel was classified as line but
        // no valid fill source was found anywhere in the eligible set; green where a texel was
        // actually replaced.
        public static (Report Report, byte[] PngBytes, byte[] DiagnosticPngBytes) Process(
            ModelRoot model, int imageIndex, Options options)
        {
            var image = model.LogicalImages[imageIndex];
            byte[] originalBytes = image.Content.Content.ToArray();

            using var srcBitmap = LoadBitmap(originalBytes);
            int width = srcBitmap.Width, height = srcBitmap.Height;

            var triangles = GatherTriangles(model, imageIndex, width, height, options.TriangleSelection);

            var positions = new Vector3[width * height];
            var covered = new bool[width * height];
            var texelSize = new float[width * height];
            RasterizeTriangles(triangles, width, height, positions, covered, texelSize);
            DilateCoverage(width, height, positions, covered, texelSize, options.GutterPaddingTexels);

            var pixels = ReadPixels(srcBitmap);

            var isLine = new bool[width * height];
            var isFillSource = new bool[width * height];
            int coveredCount = 0, lineCount = 0;
            int fillThreshold = options.Threshold + options.FillConfidenceMargin;
            float maxTexelSize = 0f;
            for (int i = 0; i < width * height; i++)
            {
                if (!covered[i]) continue;
                coveredCount++;
                if (texelSize[i] > maxTexelSize) maxTexelSize = texelSize[i];

                byte b = pixels[i * 4 + 0], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
                int luminance = (int)(0.299f * r + 0.587f * g + 0.114f * b);
                if (luminance <= options.Threshold) { isLine[i] = true; lineCount++; }
                // Deliberately NOT just "!isLine": a texel sitting in the anti-aliased transition
                // band between the two - not dark enough to erode, but not confidently bright
                // either - is left alone rather than treated as line OR offered up as a fill color,
                // which is what stops recoloring from ever pulling a muddy in-between shade.
                else if (luminance >= fillThreshold) isFillSource[i] = true;
            }

            // A generous, single upper bound on the search radius rather than a per-texel budget:
            // this is a full replace, not a graduated erosion, so a line texel should find its
            // fill source wherever it is within the eligible set (the whole image, or - with a
            // paint selection active - just the painted patch, which is small and local by
            // construction). Derived from the bounding box of every covered texel's position so it
            // scales with the model's actual size instead of an arbitrary constant.
            float maxSearchDistance = BoundingDiagonal(positions, covered);
            if (maxSearchDistance <= 0f) maxSearchDistance = MathF.Max(maxTexelSize, 1f);

            float cellSize = MathF.Max(maxTexelSize, 1e-6f);
            var grid = new SpatialGrid(cellSize);
            SpatialGrid? favoredGrid = options.FavorColor.HasValue ? new SpatialGrid(cellSize) : null;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!isFillSource[i]) continue;
                grid.Add(i, positions[i]);
                if (favoredGrid != null && ColorMatches(pixels, i, options.FavorColor!.Value, options.FavorColorTolerance))
                    favoredGrid.Add(i, positions[i]);
            }

            bool hasSelection = options.TriangleSelection != null && options.TriangleSelection.Values.Any(s => s.Count > 0);

            var newPixels = (byte[])pixels.Clone();
            var diagPixels = (byte[])pixels.Clone();
            int replacedCount = 0;

            for (int i = 0; i < width * height; i++)
            {
                if (!covered[i])
                {
                    // With a paint selection active, "not covered" mostly just means "outside the
                    // painted area" - true of most of the texture, on purpose. Painting magenta over
                    // all of that would bury whatever an EARLIER run already changed there under a
                    // solid color, which reads as "my previous edit got reverted" even though the
                    // pixels underneath are untouched. Reserve magenta for the one case it's actually
                    // diagnosing - genuinely unmapped geometry - which only means anything when
                    // nothing is scoping "covered" down in the first place.
                    if (!hasSelection) SetPixel(diagPixels, i, 255, 0, 255);
                    continue;
                }
                if (!isLine[i]) continue;                                              // leave as-is

                // Try the favored-color pool first (a much smaller, pre-filtered subset of the
                // same fill sources) - only fall back to the unrestricted nearest neighbor where
                // nothing matching the favored color is reachable within range at all, so a
                // favored color that doesn't happen to run along this particular stretch of line
                // still gets SOMETHING replaced rather than being left untouched.
                bool usedFavored = false;
                int nearest = -1;
                if (favoredGrid != null)
                {
                    nearest = FindNearestFillSource(favoredGrid, positions, positions[i], maxSearchDistance);
                    usedFavored = nearest >= 0;
                }
                if (nearest < 0)
                    nearest = FindNearestFillSource(grid, positions, positions[i], maxSearchDistance);

                if (nearest < 0)
                {
                    SetPixel(diagPixels, i, 255, 0, 0);   // red: classified as line, no fill source found
                    continue;
                }

                // Full replace - no feathering. A partial blend is what previously made a thinned
                // line read as merely softened instead of gone; a full swap is what "replace" means.
                newPixels[i * 4 + 0] = pixels[nearest * 4 + 0];
                newPixels[i * 4 + 1] = pixels[nearest * 4 + 1];
                newPixels[i * 4 + 2] = pixels[nearest * 4 + 2];
                // Alpha is left alone deliberately - this reshapes paint, not silhouette.
                // Blue marks a favored-color match specifically, distinct from green (an ordinary
                // or fallen-back-to replace), so favor-color coverage is checkable at a glance.
                if (usedFavored) SetPixel(diagPixels, i, 0, 120, 255);
                else SetPixel(diagPixels, i, 0, 255, 0);
                replacedCount++;
            }

            using var resultBitmap = WritePixels(newPixels, width, height);
            byte[] pngBytes = EncodePng(resultBitmap);

            using var diagBitmap = WritePixels(diagPixels, width, height);
            byte[] diagnosticPngBytes = EncodePng(diagBitmap);

            var report = new Report
            {
                ImageIndex = imageIndex,
                Width = width,
                Height = height,
                TexelsCovered = coveredCount,
                TexelsLine = lineCount,
                TexelsReplaced = replacedCount,
            };
            return (report, pngBytes, diagnosticPngBytes);
        }

        private static void SetPixel(byte[] pixels, int index, byte r, byte g, byte b)
        {
            pixels[index * 4 + 0] = b;
            pixels[index * 4 + 1] = g;
            pixels[index * 4 + 2] = r;
        }

        // Max-per-channel rather than Euclidean distance - simpler to reason about when setting
        // the tolerance ("each channel within N of the target"), and avoids a single wildly-off
        // channel being masked by two close ones the way a combined Euclidean threshold could.
        private static bool ColorMatches(byte[] pixels, int index, (byte R, byte G, byte B) target, byte tolerance)
        {
            byte b = pixels[index * 4 + 0], g = pixels[index * 4 + 1], r = pixels[index * 4 + 2];
            return Math.Abs(r - target.R) <= tolerance
                && Math.Abs(g - target.G) <= tolerance
                && Math.Abs(b - target.B) <= tolerance;
        }

        // Writes a previously computed result onto the model.
        public static void Apply(ModelRoot model, int imageIndex, byte[] pngBytes) =>
            model.LogicalImages[imageIndex].Content = new SharpGLTF.Memory.MemoryImage(pngBytes);

        public static byte[] SnapshotContent(ModelRoot model, int imageIndex) =>
            model.LogicalImages[imageIndex].Content.Content.ToArray();

        // --- geometry gathering / rasterization -------------------------------------------------

        // Internal rather than private: WatertightRepair reuses this UV-footprint rasterization to
        // find unused space in the same atlas for texturing hole-cap triangles.
        internal readonly struct Tri3
        {
            public readonly Vector3 P0, P1, P2;
            public readonly Vector2 Uv0, Uv1, Uv2; // already in pixel space

            public Tri3(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2)
            {
                P0 = p0; P1 = p1; P2 = p2;
                Uv0 = uv0; Uv1 = uv1; Uv2 = uv2;
            }
        }

        // Every triangle, from every primitive whose material binds this image as BaseColor, in
        // that primitive's own local (unskinned bind-pose) space - texel adjacency is a property
        // of the surface itself, not of wherever a particular node instance happens to place it.
        //
        // When selection is non-null and has at least one painted triangle, only THOSE triangles
        // are gathered - unpainted geometry is invisible to the rest of the pipeline entirely, not
        // merely excluded from being "line", which is what keeps a fill source from ever being
        // pulled from outside the painted patch.
        internal static List<Tri3> GatherTriangles(ModelRoot model, int imageIndex, int width, int height,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? selection)
        {
            bool hasSelection = selection != null && selection.Values.Any(s => s.Count > 0);
            var tris = new List<Tri3>();

            foreach (var mat in model.LogicalMaterials)
            {
                var ch = mat.FindChannel("BaseColor");
                if (!ch.HasValue) continue;
                var img = ch.Value.Texture?.PrimaryImage;
                if (img == null || img.LogicalIndex != imageIndex) continue;

                string uvName = $"TEXCOORD_{ch.Value.TextureCoordinate}";

                for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
                {
                    var mesh = model.LogicalMeshes[meshIdx];
                    for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                    {
                        var prim = mesh.Primitives[primIdx];
                        if (prim.Material?.LogicalIndex != mat.LogicalIndex) continue;
                        if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue(uvName, out var uvAcc)) continue;

                        HashSet<int>? paintedTris = null;
                        if (hasSelection)
                        {
                            if (!selection!.TryGetValue((meshIdx, primIdx), out paintedTris) || paintedTris.Count == 0)
                                continue;   // this primitive has nothing painted - skip it entirely
                        }

                        var positions = posAcc.AsVector3Array();
                        var uvs = uvAcc.AsVector2Array();

                        int triIndex = 0;
                        foreach (var (a, b, c) in prim.GetTriangleIndices())
                        {
                            if (paintedTris == null || paintedTris.Contains(triIndex))
                            {
                                tris.Add(new Tri3(
                                    positions[a], positions[b], positions[c],
                                    new Vector2(uvs[a].X * width, uvs[a].Y * height),
                                    new Vector2(uvs[b].X * width, uvs[b].Y * height),
                                    new Vector2(uvs[c].X * width, uvs[c].Y * height)));
                            }
                            triIndex++;
                        }
                    }
                }
            }

            return tris;
        }

        // Fills positions/covered/texelSize for every texel inside some triangle's UV footprint.
        internal static void RasterizeTriangles(List<Tri3> tris, int width, int height,
            Vector3[] positions, bool[] covered, float[] texelSize)
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

                float pixelAreaX2 = Cross2D(t.Uv1 - t.Uv0, t.Uv2 - t.Uv0);
                if (MathF.Abs(pixelAreaX2) < 1e-6f) continue; // degenerate in UV space (zero footprint)

                float worldAreaX2 = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0).Length();
                float localTexelSize = worldAreaX2 <= 1e-12f ? 0f : MathF.Sqrt(worldAreaX2 / MathF.Abs(pixelAreaX2));

                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        var p = new Vector2(px + 0.5f, py + 0.5f);

                        float w0 = Cross2D(t.Uv2 - t.Uv1, p - t.Uv1);
                        float w1 = Cross2D(t.Uv0 - t.Uv2, p - t.Uv2);
                        float w2 = Cross2D(t.Uv1 - t.Uv0, p - t.Uv0);

                        bool inside = pixelAreaX2 > 0
                            ? (w0 >= 0 && w1 >= 0 && w2 >= 0)
                            : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                        if (!inside) continue;

                        float b0 = w0 / pixelAreaX2, b1 = w1 / pixelAreaX2, b2 = w2 / pixelAreaX2;
                        int idx = py * width + px;
                        positions[idx] = t.P0 * b0 + t.P1 * b1 + t.P2 * b2;
                        covered[idx] = true;
                        texelSize[idx] = localTexelSize;
                    }
                }
            }
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static readonly int[] Dx8 = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] Dy8 = { -1, -1, -1, 0, 0, 1, 1, 1 };

        // Multi-source BFS, seeded from every texel a triangle actually rasterized into, flooding
        // outward up to maxRadius texels and stamping each newly-reached gutter texel with the
        // position/texelSize of whichever mapped texel it was reached from. 8-connected BFS
        // distance is only an approximation of true Euclidean/geodesic distance, but the gaps this
        // is meant to close are a handful of texels wide at most, where that approximation and the
        // real distance are indistinguishable.
        private static void DilateCoverage(int width, int height, Vector3[] positions, bool[] covered,
            float[] texelSize, int maxRadius)
        {
            if (maxRadius <= 0) return;

            int count = width * height;
            var dist = new int[count];
            var queue = new Queue<int>();
            for (int i = 0; i < count; i++)
            {
                dist[i] = covered[i] ? 0 : -1;
                if (covered[i]) queue.Enqueue(i);
            }

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
                    positions[nIdx] = positions[idx];
                    texelSize[nIdx] = texelSize[idx];
                    covered[nIdx] = true;
                    queue.Enqueue(nIdx);
                }
            }
        }

        // Bounding-box diagonal of every covered texel's 3D position - a one-time, generous upper
        // bound for the fill-source search radius, so it scales with the actual size of whatever
        // is eligible (the whole model, or just a painted patch) instead of a fixed constant that
        // would either be too tight on a large model or wastefully loose on a small one.
        private static float BoundingDiagonal(Vector3[] positions, bool[] covered)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            bool any = false;
            for (int i = 0; i < positions.Length; i++)
            {
                if (!covered[i]) continue;
                any = true;
                min = Vector3.Min(min, positions[i]);
                max = Vector3.Max(max, positions[i]);
            }
            return any ? Vector3.Distance(min, max) : 0f;
        }

        // --- 3D nearest-neighbor spatial index --------------------------------------------------

        // A uniform grid rather than a formal k-d tree: texel positions lie on a bounded surface
        // with fairly even local density (each cell sized off the largest local texel size seen),
        // so a hashed grid with an expanding-ring search gets the same near-O(1) query behavior
        // with far less code, and no tree-balancing cost to pay up front.
        private sealed class SpatialGrid
        {
            private readonly Dictionary<(int, int, int), List<int>> _cells = new();
            public readonly float CellSize;

            public SpatialGrid(float cellSize) => CellSize = cellSize;

            public void Add(int index, Vector3 pos)
            {
                var key = KeyOf(pos);
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                list.Add(index);
            }

            public (int, int, int) KeyOf(Vector3 pos) => (
                (int)MathF.Floor(pos.X / CellSize),
                (int)MathF.Floor(pos.Y / CellSize),
                (int)MathF.Floor(pos.Z / CellSize));

            public bool TryGetCell((int, int, int) key, out List<int> list) => _cells.TryGetValue(key, out list!);
        }

        // Purely 3D nearest-neighbor now - no image-space cap. That safety net existed only to stop
        // a whole-image search from reaching clear across an unrelated part of a compact, busy
        // atlas; with the fill-source pool restricted to a painted region (or, unpainted, the
        // caller's own choice to process the whole image), an unbounded 3D search is exactly what
        // resolves a UV seam correctly - and a paint selection is local by construction, so it has
        // nothing distant left to wrongly latch onto.
        private static int FindNearestFillSource(SpatialGrid grid, Vector3[] positions, Vector3 query, float maxDistance)
        {
            var (cx, cy, cz) = grid.KeyOf(query);
            int maxRing = (int)MathF.Ceiling(maxDistance / grid.CellSize) + 1;

            int bestIdx = -1;
            float bestDistSq = float.MaxValue;

            for (int ring = 0; ring <= maxRing; ring++)
            {
                // Once a candidate is found, any cell in a ring further out than this is
                // guaranteed to be no closer than (ring - 1) cell-widths away, so once that floor
                // exceeds the best distance found so far, nothing further out can win.
                if (bestIdx >= 0)
                {
                    float minPossible = (ring - 1) * grid.CellSize;
                    if (minPossible > 0 && minPossible * minPossible > bestDistSq) break;
                }

                foreach (var (dx, dy, dz) in RingOffsets(ring))
                {
                    if (!grid.TryGetCell((cx + dx, cy + dy, cz + dz), out var list)) continue;
                    foreach (var idx in list)
                    {
                        float d = Vector3.DistanceSquared(positions[idx], query);
                        if (d < bestDistSq) { bestDistSq = d; bestIdx = idx; }
                    }
                }
            }

            if (bestIdx < 0 || bestDistSq > maxDistance * maxDistance) return -1;
            return bestIdx;
        }

        private static IEnumerable<(int, int, int)> RingOffsets(int ring)
        {
            if (ring == 0) { yield return (0, 0, 0); yield break; }

            for (int dx = -ring; dx <= ring; dx++)
                for (int dy = -ring; dy <= ring; dy++)
                    for (int dz = -ring; dz <= ring; dz++)
                        if (Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz))) == ring)
                            yield return (dx, dy, dz);
        }

        // --- bitmap plumbing ---------------------------------------------------------------------

        internal static Bitmap LoadBitmap(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var raw = new Bitmap(ms);
            return raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), PixelFormat.Format32bppArgb);
        }

        // BGRA byte order per pixel, matching Format32bppArgb's in-memory layout.
        internal static byte[] ReadPixels(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[bmp.Width * bmp.Height * 4];
                int rowBytes = bmp.Width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                }
                else
                {
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * rowBytes, rowBytes);
                }
                return bytes;
            }
            finally { bmp.UnlockBits(data); }
        }

        internal static Bitmap WritePixels(byte[] pixels, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        // Always re-encoded as PNG regardless of the source format, so a re-run never compounds
        // JPEG block artifacts along the very edges this is trying to clean up.
        internal static byte[] EncodePng(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
