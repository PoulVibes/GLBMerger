using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Rewrites the texture so that it is pixel-accurate on the simplified mesh, instead of moving
    // the mesh's UVs toward the texture (UvWarpRepair) or putting triangles back (RegionRestorer).
    //
    // The premise is the same one those two rest on: simplification leaves every vertex's UV
    // alone, but a surviving triangle spans surface that several original triangles used to cover,
    // and the GPU stretches the texture straight across it where the original mapping bent. Three
    // UVs per triangle can only ever approximate that bend. Every texel, though, can be put
    // exactly right: for each texel a current triangle covers, work out the point on the current
    // surface that will display it, find the nearest point on the ORIGINAL surface, look up what
    // the original texture showed there, and write that into the texel. Afterwards the new
    // triangle shows, at every pixel, what the original surface showed at that spot on the
    // surface. What is left is only the geometry that is genuinely gone - a silhouette flattened,
    // a bump removed - which no texture can put back.
    //
    // Only texels whose content would actually move are rewritten. A texel whose original UV lands
    // on itself (within half a texel) is left as it was, so untouched parts of the atlas are never
    // resampled and never blur; and the current triangles the original mesh already had (matched
    // by position and UV, not by index) are skipped outright. The same two gates UvWarpRepair uses
    // apply per texel: a point too far off the original surface (a hole cap, a cut face) has
    // nothing to sample, and an original UV that jumps across the atlas has landed on the other
    // side of a UV seam, where sampling would pull one island's texels into another.
    //
    // Every image any material channel binds is rebuilt - normal and roughness maps are stretched
    // across a big triangle exactly as the albedo is - each against the UV set its channel reads.
    // Sampling always reads the ORIGINAL image (History entry 0's bytes), never the model's
    // current one, so a rebuild after a further pass is computed fresh rather than compounding a
    // previous rebuild's resampling.
    //
    // Cost is per covered texel - a 4K atlas over a heavily simplified model is millions of
    // closest-point queries - so the work is split into horizontal bands processed in parallel,
    // and progress is reported per band for the pane's progress bar. The queries depend only on
    // the geometry, the UV set and the atlas size, not on the image, so a model whose albedo,
    // normal and roughness maps are the same size over the same UVs (the usual case) runs them
    // once and samples three images from the one lookup.
    public static class TextureRebaker
    {
        public sealed class Options
        {
            /// <summary>Seam gate, in UV units: an original UV further than this from the texel's own is another island.</summary>
            public float MaxUvJump { get; set; } = 0.05f;

            /// <summary>Distance gate, as a fraction of the primitive's bounding diagonal.</summary>
            public float MaxSurfaceDistance { get; set; } = 0.02f;
        }

        public sealed class ImageResult
        {
            public int ImageIndex { get; init; }
            public string Label { get; init; } = "";
            public int Width { get; set; }
            public int Height { get; set; }
            public int TrianglesProcessed { get; set; }
            public long TexelsCovered { get; set; }
            public long TexelsRewritten { get; set; }

            /// <summary>Texels two triangles at different places on the surface both display (overlapping UVs) - left as they were, since only one could be right.</summary>
            public long TexelsConflicted { get; set; }

            public string? SkippedReason { get; set; }

            // Filled in by Analyze, consumed by Apply - null when the image is unchanged.
            internal byte[]? NewPng { get; set; }
        }

        public sealed class Report
        {
            public List<ImageResult> Images { get; } = new();
            public bool HasChanges => Images.Any(i => i.NewPng != null);
            public long TexelsRewritten => Images.Sum(i => i.TexelsRewritten);
            public long TexelsCovered => Images.Sum(i => i.TexelsCovered);
            public long TexelsConflicted => Images.Sum(i => i.TexelsConflicted);
            public int TrianglesProcessed => Images.Sum(i => i.TrianglesProcessed);
            public int ImagesRewritten => Images.Count(i => i.NewPng != null);
        }

        public readonly record struct Progress(int Done, int Total, string Stage);

        private const int BandRows = 32;

        // originalImages: encoded bytes per LogicalImages index to sample from, or null to sample
        // the model's current images (correct only while nothing has ever rewritten one).
        public static Report Analyze(ModelRoot model, UvWarpRepair.Reference reference,
            IReadOnlyList<byte[]>? originalImages, Options options,
            IProgress<Progress>? progress = null, CancellationToken cancellation = default)
        {
            var report = new Report();

            // Every (image, UV set) an actual primitive draws through: the same image can be read
            // through TEXCOORD_0 by one material and TEXCOORD_1 by another, and each is its own
            // rasterization, since it's a different mapping onto the surface.
            var jobs = CollectJobs(model);
            var byImage = jobs.GroupBy(j => j.ImageIndex).OrderBy(g => g.Key).ToList();

            // Progress is in bands over all images together, so the bar runs once end to end:
            // the lookup bands for each distinct (size, UV set, primitives) map, which is where
            // the time goes, plus a sampling pass per image.
            var sizes = new Dictionary<int, (int W, int H)>();
            var mapKeys = new HashSet<string>();
            int totalBands = 0;
            foreach (var group in byImage)
            {
                var bytes = originalImages != null && group.Key < originalImages.Count
                    ? originalImages[group.Key]
                    : model.LogicalImages[group.Key].Content.Content.ToArray();
                try
                {
                    using var ms = new System.IO.MemoryStream(bytes);
                    using var img = System.Drawing.Image.FromStream(ms, false, false);
                    sizes[group.Key] = (img.Width, img.Height);
                    int bands = (img.Height + BandRows - 1) / BandRows;
                    totalBands += bands;
                    if (mapKeys.Add(MapKey(img.Width, img.Height, group))) totalBands += bands;
                }
                catch
                {
                    sizes[group.Key] = (0, 0);
                }
            }

            var maps = new Dictionary<string, LookupMap>();
            int doneBands = 0;
            foreach (var group in byImage)
            {
                cancellation.ThrowIfCancellationRequested();
                int imageIndex = group.Key;
                var result = new ImageResult
                {
                    ImageIndex = imageIndex,
                    Label = string.Join(", ", group.Select(j => j.MaterialName).Distinct()),
                };
                report.Images.Add(result);

                var (width, height) = sizes[imageIndex];
                if (width == 0) { result.SkippedReason = "image could not be decoded"; continue; }

                string key = MapKey(width, height, group);
                if (!maps.TryGetValue(key, out var map))
                {
                    map = BuildLookupMap(model, reference, width, height, group.ToList(), options, result.Label,
                        progress, ref doneBands, totalBands, cancellation);
                    maps[key] = map;
                }

                var sourceBytes = originalImages != null && imageIndex < originalImages.Count
                    ? originalImages[imageIndex]
                    : model.LogicalImages[imageIndex].Content.Content.ToArray();

                SampleImage(sourceBytes, map, result, progress, ref doneBands, totalBands, cancellation);
            }

            progress?.Report(new Progress(totalBands, totalBands, "done"));
            return report;
        }

        private static string MapKey(int width, int height, IEnumerable<Job> jobs) =>
            $"{width}x{height}|" + string.Join(",", jobs.Select(j => $"{j.FlatIndex}:{j.UvSet}").OrderBy(k => k));

        // Per texel of one atlas size: the original UV that texel should show, or NaN where it is
        // uncovered, unchanged-triangle, off the original surface, or across a seam.
        private sealed class LookupMap
        {
            public required int Width { get; init; }
            public required int Height { get; init; }
            public required float[] U { get; init; }
            public required float[] V { get; init; }
            public int TrianglesProcessed { get; set; }
            public long TexelsCovered { get; set; }
            public long TexelsConflicted { get; set; }
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var image in report.Images)
                if (image.NewPng != null) TextureAtlasUtil.Apply(model, image.ImageIndex, image.NewPng);
        }

        // One primitive drawing through one image with one UV set.
        private sealed record Job(int ImageIndex, string MaterialName, int MeshIndex, int PrimitiveIndex, int FlatIndex, string UvSet);

        private static List<Job> CollectJobs(ModelRoot model)
        {
            var jobs = new List<Job>();
            var seen = new HashSet<(int, int, int, string)>();

            foreach (var mat in model.LogicalMaterials)
            {
                foreach (var ch in mat.Channels)
                {
                    var img = ch.Texture?.PrimaryImage;
                    if (img == null) continue;
                    string uvSet = $"TEXCOORD_{ch.TextureCoordinate}";
                    string matName = mat.Name ?? $"material_{mat.LogicalIndex}";

                    int flat = 0;
                    for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
                    {
                        var mesh = model.LogicalMeshes[meshIdx];
                        for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++, flat++)
                        {
                            var prim = mesh.Primitives[primIdx];
                            if (prim.Material?.LogicalIndex != mat.LogicalIndex) continue;
                            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                            if (!prim.VertexAccessors.ContainsKey("POSITION") || !prim.VertexAccessors.ContainsKey(uvSet)) continue;
                            if (!seen.Add((img.LogicalIndex, meshIdx, primIdx, uvSet))) continue;
                            jobs.Add(new Job(img.LogicalIndex, matName, meshIdx, primIdx, flat, uvSet));
                        }
                    }
                }
            }
            return jobs;
        }

        // A current triangle waiting to be rasterized: its corners in texel space, in 3D, and the
        // surface + UV set to look its texels up against.
        private readonly struct Work
        {
            public readonly Vector2 T0, T1, T2;
            public readonly Vector3 P0, P1, P2;
            public readonly UvWarpRepair.ReferenceSurface Surface;
            public readonly string UvSet;
            public readonly float MaxDistance;
            public readonly int Island;   // -1: unknown, fall back to the UV-distance gate

            public Work(Vector2 t0, Vector2 t1, Vector2 t2, Vector3 p0, Vector3 p1, Vector3 p2,
                UvWarpRepair.ReferenceSurface surface, string uvSet, float maxDistance, int island)
            {
                T0 = t0; T1 = t1; T2 = t2; P0 = p0; P1 = p1; P2 = p2;
                Surface = surface; UvSet = uvSet; MaxDistance = maxDistance; Island = island;
            }
        }

        private static LookupMap BuildLookupMap(ModelRoot model, UvWarpRepair.Reference reference, int width, int height,
            List<Job> jobs, Options options, string label, IProgress<Progress>? progress,
            ref int doneBands, int totalBands, CancellationToken cancellation)
        {
            var u = new float[width * height];
            var v = new float[width * height];
            Array.Fill(u, float.NaN);
            Array.Fill(v, float.NaN);
            var map = new LookupMap { Width = width, Height = height, U = u, V = v };

            // Gather the triangles that need looking up: current triangles of every job's
            // primitive that the original mesh did not already have.
            var work = new List<Work>();
            foreach (var job in jobs)
            {
                var surface = job.FlatIndex < reference.Surfaces.Count ? reference.Surfaces[job.FlatIndex] : null;
                if (surface == null || !surface.HasUvSet(job.UvSet)) continue;

                var prim = model.LogicalMeshes[job.MeshIndex].Primitives[job.PrimitiveIndex];
                var positions = prim.VertexAccessors["POSITION"].AsVector3Array();
                var uvs = prim.VertexAccessors[job.UvSet].AsVector2Array();
                float maxDistance = options.MaxSurfaceDistance * surface.Diagonal;

                var original = OriginalTriangleKeys(surface, job.UvSet);
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    if (original.Contains(TriangleKey.Of(positions[a], positions[b], positions[c], uvs[a], uvs[b], uvs[c]))) continue;
                    work.Add(new Work(
                        new Vector2(uvs[a].X * width, uvs[a].Y * height),
                        new Vector2(uvs[b].X * width, uvs[b].Y * height),
                        new Vector2(uvs[c].X * width, uvs[c].Y * height),
                        positions[a], positions[b], positions[c],
                        surface, job.UvSet, maxDistance,
                        surface.IslandOfTriangle(positions[a], positions[b], positions[c], uvs[a], uvs[b], uvs[c], job.UvSet)));
                }
            }
            map.TrianglesProcessed = work.Count;

            int bands = (height + BandRows - 1) / BandRows;
            if (work.Count == 0)
            {
                doneBands += bands;
                progress?.Report(new Progress(doneBands, totalBands, label));
                return map;
            }

            // Bucket by the rows each triangle's footprint spans, so a band only looks at what
            // can touch it, and bands never write the same row - which is what lets them run in
            // parallel over one output buffer without any locking.
            var perBand = new List<int>[bands];
            for (int i = 0; i < bands; i++) perBand[i] = new List<int>();
            for (int w = 0; w < work.Count; w++)
            {
                var t = work[w];
                float minY = MathF.Min(t.T0.Y, MathF.Min(t.T1.Y, t.T2.Y));
                float maxY = MathF.Max(t.T0.Y, MathF.Max(t.T1.Y, t.T2.Y));
                int b0 = Math.Clamp((int)MathF.Floor(minY) / BandRows, 0, bands - 1);
                int b1 = Math.Clamp((int)MathF.Ceiling(maxY) / BandRows, 0, bands - 1);
                for (int b = b0; b <= b1; b++) perBand[b].Add(w);
            }

            long covered = 0, conflicted = 0;
            float maxJumpSq = options.MaxUvJump * options.MaxUvJump;
            int done = doneBands;

            // Per texel: 0 untouched, 1 claimed (u/v hold its target, or NaN for "already right"),
            // 2 conflicted. A texel two triangles display from different places on the surface -
            // mirrored halves sharing one patch of atlas is the everyday case - has two different
            // right answers, and rewriting it for one side breaks it for the other; so it's left
            // exactly as it was. Two triangles that merely share an edge agree on the target to
            // within a texel and don't count.
            var state = new byte[width * height];

            Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancellation }, band =>
            {
                int rowStart = band * BandRows;
                int rowEnd = Math.Min(height, rowStart + BandRows) - 1;
                long localCovered = 0, localConflicted = 0;

                foreach (int w in perBand[band])
                {
                    var t = work[w];
                    RasterizeRows(t, width, rowStart, rowEnd, (px, py, bary) =>
                    {
                        localCovered++;
                        var p = t.P0 * bary.X + t.P1 * bary.Y + t.P2 * bary.Z;
                        if (!t.Surface.ClosestPoint(p, t.MaxDistance, out int tri, out var refBary, out _)) return;

                        var uvOriginal = t.Surface.Uv(t.UvSet, tri, refBary);
                        var uvTexel = new Vector2((px + 0.5f) / width, (py + 0.5f) / height);
                        var delta = uvOriginal - uvTexel;
                        if (delta.LengthSquared() > maxJumpSq) return;                       // far side of a seam
                        if (t.Island >= 0 && t.Surface.IslandOf(tri, t.UvSet) != t.Island) return;   // other island, however near

                        // Within half a texel of itself the content is already right; leaving it
                        // is what keeps untouched detail from being blurred by resampling.
                        bool unchanged = MathF.Abs(delta.X * width) < 0.5f && MathF.Abs(delta.Y * height) < 0.5f;
                        float targetU = unchanged ? float.NaN : uvOriginal.X;
                        float targetV = unchanged ? float.NaN : uvOriginal.Y;

                        int idx = py * width + px;
                        switch (state[idx])
                        {
                            case 0:
                                state[idx] = 1;
                                u[idx] = targetU;
                                v[idx] = targetV;
                                break;
                            case 1:
                                bool agree = float.IsNaN(u[idx])
                                    ? unchanged
                                    : !unchanged && MathF.Abs(u[idx] - targetU) * width < 1f && MathF.Abs(v[idx] - targetV) * height < 1f;
                                if (!agree)
                                {
                                    state[idx] = 2;
                                    u[idx] = float.NaN;
                                    v[idx] = float.NaN;
                                    localConflicted++;
                                }
                                break;
                        }
                    });
                }

                Interlocked.Add(ref covered, localCovered);
                Interlocked.Add(ref conflicted, localConflicted);
                int now = Interlocked.Increment(ref done);
                progress?.Report(new Progress(now, totalBands, label));
            });

            doneBands = done;
            map.TexelsCovered = covered;
            map.TexelsConflicted = conflicted;
            return map;
        }

        // Writes one image from a finished lookup: every texel with an entry is resampled from
        // the ORIGINAL image at that UV; everything else is copied through as it was.
        private static void SampleImage(byte[] sourceBytes, LookupMap map, ImageResult result,
            IProgress<Progress>? progress, ref int doneBands, int totalBands, CancellationToken cancellation)
        {
            using var srcBitmap = TextureAtlasUtil.LoadBitmap(sourceBytes);
            int width = srcBitmap.Width, height = srcBitmap.Height;
            result.Width = width;
            result.Height = height;
            result.TrianglesProcessed = map.TrianglesProcessed;
            result.TexelsCovered = map.TexelsCovered;
            result.TexelsConflicted = map.TexelsConflicted;

            int bands = (height + BandRows - 1) / BandRows;
            if (map.TrianglesProcessed == 0)
            {
                result.SkippedReason = "no triangle over it has changed";
                doneBands += bands;
                progress?.Report(new Progress(doneBands, totalBands, result.Label));
                return;
            }

            var source = TextureAtlasUtil.ReadPixels(srcBitmap);   // BGRA
            var output = (byte[])source.Clone();
            var u = map.U;
            var v = map.V;
            long rewritten = 0;
            int done = doneBands;
            string label = result.Label;

            Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancellation }, band =>
            {
                int rowStart = band * BandRows;
                int rowEnd = Math.Min(height, rowStart + BandRows);
                long local = 0;
                for (int py = rowStart; py < rowEnd; py++)
                {
                    int row = py * width;
                    for (int px = 0; px < width; px++)
                    {
                        float uu = u[row + px];
                        if (float.IsNaN(uu)) continue;
                        SampleBilinear(source, width, height, new Vector2(uu, v[row + px]), output, (row + px) * 4);
                        local++;
                    }
                }
                Interlocked.Add(ref rewritten, local);
                int now = Interlocked.Increment(ref done);
                progress?.Report(new Progress(now, totalBands, label));
            });

            doneBands = done;
            result.TexelsRewritten = rewritten;
            if (rewritten == 0) { result.SkippedReason = "every texel already matched"; return; }

            using var bmp = TextureAtlasUtil.WritePixels(output, width, height);
            result.NewPng = TextureAtlasUtil.EncodePng(bmp);
        }

        // Same edge-function rasterization as TextureAtlasUtil.RasterizeTriangles, restricted to a
        // row range and handing each covered texel's barycentrics to the caller.
        private static void RasterizeRows(in Work t, int width, int rowStart, int rowEnd, Action<int, int, Vector3> texel)
        {
            float minX = MathF.Min(t.T0.X, MathF.Min(t.T1.X, t.T2.X));
            float maxX = MathF.Max(t.T0.X, MathF.Max(t.T1.X, t.T2.X));
            float minY = MathF.Min(t.T0.Y, MathF.Min(t.T1.Y, t.T2.Y));
            float maxY = MathF.Max(t.T0.Y, MathF.Max(t.T1.Y, t.T2.Y));

            int x0 = Math.Max(0, (int)MathF.Floor(minX));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(maxX));
            int y0 = Math.Max(rowStart, (int)MathF.Floor(minY));
            int y1 = Math.Min(rowEnd, (int)MathF.Ceiling(maxY));
            if (x1 < x0 || y1 < y0) return;

            float areaX2 = Cross2D(t.T1 - t.T0, t.T2 - t.T0);
            if (MathF.Abs(areaX2) < 1e-6f) return;

            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    float w0 = Cross2D(t.T2 - t.T1, p - t.T1);
                    float w1 = Cross2D(t.T0 - t.T2, p - t.T2);
                    float w2 = Cross2D(t.T1 - t.T0, p - t.T0);

                    bool inside = areaX2 > 0
                        ? (w0 >= 0 && w1 >= 0 && w2 >= 0)
                        : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (!inside) continue;

                    texel(px, py, new Vector3(w0 / areaX2, w1 / areaX2, w2 / areaX2));
                }
            }
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        // Bilinear, wrapping (glTF's default sampler repeats), texel centres at half-integers -
        // so a UV that lands exactly on a texel centre reads that texel back unchanged.
        private static void SampleBilinear(byte[] src, int width, int height, Vector2 uv, byte[] dst, int dstOffset)
        {
            float fx = uv.X * width - 0.5f;
            float fy = uv.Y * height - 0.5f;
            int x0 = (int)MathF.Floor(fx), y0 = (int)MathF.Floor(fy);
            float tx = fx - x0, ty = fy - y0;

            int xa = Wrap(x0, width), xb = Wrap(x0 + 1, width);
            int ya = Wrap(y0, height), yb = Wrap(y0 + 1, height);
            int o00 = (ya * width + xa) * 4, o10 = (ya * width + xb) * 4;
            int o01 = (yb * width + xa) * 4, o11 = (yb * width + xb) * 4;

            for (int c = 0; c < 4; c++)
            {
                float top = src[o00 + c] * (1 - tx) + src[o10 + c] * tx;
                float bottom = src[o01 + c] * (1 - tx) + src[o11 + c] * tx;
                dst[dstOffset + c] = (byte)Math.Clamp(MathF.Round(top * (1 - ty) + bottom * ty), 0, 255);
            }
        }

        private static int Wrap(int i, int n) => ((i % n) + n) % n;

        // A triangle identified by where its corners are and what they map to, independent of
        // vertex numbering and corner order - two triangles that agree on all of that show the
        // same texture the same way, whatever indices they were built from.
        private readonly record struct TriangleKey(VertexKey A, VertexKey B, VertexKey C)
        {
            public static TriangleKey Of(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2)
            {
                var keys = new[] { VertexKey.Of(p0, uv0), VertexKey.Of(p1, uv1), VertexKey.Of(p2, uv2) };
                Array.Sort(keys);
                return new TriangleKey(keys[0], keys[1], keys[2]);
            }
        }

        private readonly record struct VertexKey(long X, long Y, long Z, long U, long V) : IComparable<VertexKey>
        {
            private const float PositionScale = 1e5f, UvScale = 1e5f;

            public static VertexKey Of(Vector3 p, Vector2 uv) => new(
                (long)MathF.Round(p.X * PositionScale), (long)MathF.Round(p.Y * PositionScale), (long)MathF.Round(p.Z * PositionScale),
                (long)MathF.Round(uv.X * UvScale), (long)MathF.Round(uv.Y * UvScale));

            public int CompareTo(VertexKey o)
            {
                int c = X.CompareTo(o.X); if (c != 0) return c;
                c = Y.CompareTo(o.Y); if (c != 0) return c;
                c = Z.CompareTo(o.Z); if (c != 0) return c;
                c = U.CompareTo(o.U); if (c != 0) return c;
                return V.CompareTo(o.V);
            }
        }

        private static HashSet<TriangleKey> OriginalTriangleKeys(UvWarpRepair.ReferenceSurface surface, string uvSet)
        {
            var keys = new HashSet<TriangleKey>(surface.TriangleCount);
            for (int t = 0; t < surface.TriangleCount; t++)
            {
                var (a, b, c, ua, ub, uc) = surface.Triangle(t, uvSet);
                keys.Add(TriangleKey.Of(a, b, c, ua, ub, uc));
            }
            return keys;
        }
    }
}
