using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Straightens texture that has drifted after simplification, by moving the surviving vertices'
    // UVs back toward where the original mesh put the texture on that part of the surface.
    //
    // Why the texture drifts at all, given that simplification never touches a vertex: it only
    // rewrites indices (see GeometryOptimizer), so every surviving vertex keeps its exact UV. But a
    // surviving triangle now covers surface that several original triangles used to cover, and the
    // GPU interpolates UV linearly across it - while the original mapping over that same patch was
    // piecewise-linear across several triangles, which on any curved or unevenly unwrapped surface
    // is not the same thing. The texture inside the big triangle lands somewhere slightly different
    // from where it did before, and reads as a warp. There is nothing to compare vertex by vertex,
    // then; the comparison has to be made on the surface itself.
    //
    // Which is what this does. Every current triangle is sampled at a lattice of interior points;
    // each sample is projected to the nearest point on the ORIGINAL surface (History entry 0, the
    // untouched merge result - see GeometryHistory.GetOriginalGeometry) and the original UV there
    // is what that sample should be showing. The gap between that and the UV the current triangle
    // interpolates is the warp, measured. Closing it is a least-squares fit: the surviving vertices'
    // UVs are the unknowns, each sample is one equation in the three UVs of its triangle, and a
    // Gauss-Seidel sweep settles them. Triangles the pass never changed contribute samples whose
    // target equals their current value, which is what anchors the fit and keeps it local.
    //
    // Two gates keep bad correspondences out of the fit. A sample whose nearest original point is
    // far off the surface sits on geometry that didn't exist originally (a hole cap, a cut face)
    // and has nothing to match. A sample whose original UV is far from its current one in the atlas
    // has landed on the other side of a UV seam - the nearest original point belongs to a different
    // island - and pulling toward it would drag one island's texels across into another. Both are
    // simply dropped. What survives is a genuine "just a bit" of drift, which is all this is for.
    //
    // What it can't do: a triangle has three UVs to move, so a warp INSIDE one large triangle - the
    // original mapping bending across the patch it replaced - is reduced to the best affine fit,
    // not removed. What's left after this needs the texture itself rewritten (re-baked against the
    // same original surface), which is why ReferenceSurface below is kept separate and reusable.
    //
    // Writes TEXCOORD accessors only, so GeometryHistory records it as a vertex-touching step and
    // it can be clicked back out of like any other pass.
    public static class UvWarpRepair
    {
        public sealed class Options
        {
            /// <summary>Lattice density per triangle. 4 gives 6 interior samples; corners are skipped since they sit on a vertex and can't disagree with it.</summary>
            public int Subdivisions { get; set; } = 4;

            /// <summary>Seam gate, in UV units: a sample whose original UV is further than this from its current one is on another island, not warped.</summary>
            public float MaxUvJump { get; set; } = 0.05f;

            /// <summary>Distance gate, as a fraction of the primitive's bounding diagonal: samples further than this from the original surface aren't on it.</summary>
            public float MaxSurfaceDistance { get; set; } = 0.02f;

            /// <summary>How hard each vertex is pulled back to its current UV, relative to the weight of the samples that touch it.</summary>
            public float AnchorWeight { get; set; } = 0.1f;

            public int Iterations { get; set; } = 40;
        }

        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }

            public int SamplesUsed { get; set; }
            public int SamplesRejected { get; set; }
            public int VerticesAdjusted { get; set; }

            /// <summary>Area-weighted RMS distance between current and original UV over the surface, in UV units.</summary>
            public float RmsBefore { get; set; }
            public float RmsAfter { get; set; }
            public float MaxBefore { get; set; }
            public float MaxAfter { get; set; }

            /// <summary>Surface area the samples cover, so meshes can be combined area-weighted. Zero when nothing was measured.</summary>
            public float SampledArea { get; set; }

            /// <summary>Base color texture width in texels, so the UV numbers above can be read in pixels. 0 when the material has none.</summary>
            public int TexelsPerUv { get; set; }

            public string? SkippedReason { get; set; }

            // Filled in by Analyze, consumed by Apply - null when this primitive is unchanged.
            internal Dictionary<string, Vector2[]>? NewUvs { get; set; }
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();

            public bool HasChanges => Primitives.Any(p => p.NewUvs != null);
            public int SamplesUsed => Primitives.Sum(p => p.SamplesUsed);
            public int SamplesRejected => Primitives.Sum(p => p.SamplesRejected);
            public int VerticesAdjusted => Primitives.Sum(p => p.VerticesAdjusted);
            public int PrimitivesSkipped => Primitives.Count(p => p.SkippedReason != null);

            public float RmsBefore => CombinedRms(p => p.RmsBefore);
            public float RmsAfter => CombinedRms(p => p.RmsAfter);
            public float MaxBefore => Primitives.Count == 0 ? 0 : Primitives.Max(p => p.MaxBefore);
            public float MaxAfter => Primitives.Count == 0 ? 0 : Primitives.Max(p => p.MaxAfter);

            // Every measured primitive's RMS is over its own area, so the model-wide figure is the
            // area-weighted combination of the squares, not an average of the RMS values.
            private float CombinedRms(Func<PrimitiveResult, float> rms)
            {
                double area = 0, sum = 0;
                foreach (var p in Primitives)
                {
                    if (p.SampledArea <= 0) continue;
                    area += p.SampledArea;
                    sum += (double)rms(p) * rms(p) * p.SampledArea;
                }
                return area <= 0 ? 0 : (float)Math.Sqrt(sum / area);
            }
        }

        // The original model, ready to be queried. Built once from GeometryHistory and held by the
        // editor for the whole session: entry 0 never changes, and the spatial grids inside are the
        // expensive part of a run.
        public sealed class Reference
        {
            internal List<ReferenceSurface?> Surfaces { get; }
            internal Reference(List<ReferenceSurface?> surfaces) { Surfaces = surfaces; }
        }

        public static Reference BuildReference(GeometryHistory history)
        {
            var surfaces = history.GetOriginalGeometry()
                .Select(p => p == null ? null : new ReferenceSurface(p))
                .ToList();
            return new Reference(surfaces);
        }

        // Dry run: measures every primitive and solves for its corrected UVs without touching the
        // model, so the numbers can be looked at before anything is committed.
        public static Report Analyze(ModelRoot model, Reference reference, Options options)
        {
            var report = new Report();

            // Decoding an image just for its width is not free on a 4K atlas, so once per image.
            var texelsByImage = new Dictionary<int, int>();

            int flat = 0;
            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var prim = mesh.Primitives[primIdx];
                    var surface = flat < reference.Surfaces.Count ? reference.Surfaces[flat] : null;
                    flat++;

                    var result = new PrimitiveResult
                    {
                        MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                        MeshIndex = meshIdx,
                        PrimitiveIndex = primIdx,
                        TexelsPerUv = TexelsPerUv(prim, texelsByImage),
                    };
                    report.Primitives.Add(result);

                    AnalyzePrimitive(prim, surface, options, result);
                }
            }

            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.NewUvs == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];
                // FLOAT VEC2 is always a valid TEXCOORD encoding, whatever the source was, so this
                // doesn't need WatertightRepair.WriteAttribute's integer re-encoding path.
                foreach (var (name, uvs) in p.NewUvs)
                    prim.WithVertexAccessor(name, uvs.ToList());
            }
        }

        private static int TexelsPerUv(MeshPrimitive prim, Dictionary<int, int> cache)
        {
            var image = prim.Material?.FindChannel("BaseColor")?.Texture?.PrimaryImage;
            if (image == null) return 0;
            if (cache.TryGetValue(image.LogicalIndex, out int width)) return width;

            try
            {
                // Header only - validateImageData: false stops GDI+ decoding the pixels, which on
                // a 4K atlas is most of a second for a number that's only used in the report.
                using var ms = new System.IO.MemoryStream(image.Content.Content.ToArray());
                using var img = System.Drawing.Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);
                width = img.Width;
            }
            catch
            {
                width = 0;   // undecodable image - the numbers just stay in UV units
            }
            cache[image.LogicalIndex] = width;
            return width;
        }

        private static void AnalyzePrimitive(MeshPrimitive prim, ReferenceSurface? surface, Options options, PrimitiveResult result)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) { result.SkippedReason = "not a triangle list"; return; }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) { result.SkippedReason = "no POSITION"; return; }
            if (surface == null) { result.SkippedReason = "no original geometry to compare against"; return; }

            var uvNames = prim.VertexAccessors.Keys
                .Where(n => n.StartsWith("TEXCOORD_", StringComparison.Ordinal) && surface.HasUvSet(n))
                .ToList();
            if (uvNames.Count == 0) { result.SkippedReason = "no texture coordinates"; return; }

            var positions = posAcc.AsVector3Array();
            var tris = prim.GetTriangleIndices().ToArray();
            if (tris.Length == 0) { result.SkippedReason = "no triangles"; return; }

            // Same triangles over the same vertices as the original means the same mapping - there
            // is nothing to measure, and no reason to spend the closest-point queries finding out.
            if (surface.IsSameTopology(tris, positions.Count))
            {
                result.SkippedReason = "unchanged since original";
                return;
            }

            float maxDistance = options.MaxSurfaceDistance * surface.Diagonal;
            var lattice = Lattice(Math.Max(2, options.Subdivisions));

            // The correspondence (which original point each sample lands on) is a property of the
            // geometry alone, so it's computed once and shared by every UV set.
            var samples = new List<Sample>(tris.Length * lattice.Count);
            int rejectedDistance = 0;
            double sampledArea = 0;
            for (int t = 0; t < tris.Length; t++)
            {
                var (a, b, c) = tris[t];
                Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
                float area = 0.5f * Vector3.Cross(pb - pa, pc - pa).Length();
                if (area <= 1e-12f) continue;
                float weight = area / lattice.Count;

                foreach (var bary in lattice)
                {
                    var p = pa * bary.X + pb * bary.Y + pc * bary.Z;
                    if (!surface.ClosestPoint(p, maxDistance, out int refTri, out var refBary, out float distance))
                    {
                        rejectedDistance++;
                        continue;
                    }
                    samples.Add(new Sample(a, b, c, bary, refTri, refBary, weight));
                    sampledArea += weight;
                }
            }

            if (samples.Count == 0)
            {
                result.SamplesRejected = rejectedDistance;
                result.SkippedReason = "no part of it lies on the original surface";
                return;
            }

            var newUvs = new Dictionary<string, Vector2[]>();
            int rejectedSeam = 0;
            double rmsBeforeSq = 0, rmsAfterSq = 0, areaBefore = 0;
            float maxBefore = 0, maxAfter = 0;
            int adjusted = 0;

            foreach (var name in uvNames)
            {
                var current = prim.VertexAccessors[name].AsVector2Array();
                var fit = FitUvSet(name, current, positions, samples, surface, options, out var stats);
                rejectedSeam += stats.RejectedSeam;
                rmsBeforeSq += stats.SumSqBefore;
                rmsAfterSq += stats.SumSqAfter;
                areaBefore += stats.Area;
                maxBefore = MathF.Max(maxBefore, stats.MaxBefore);
                maxAfter = MathF.Max(maxAfter, stats.MaxAfter);

                if (fit == null) continue;
                adjusted += stats.Adjusted;
                newUvs[name] = fit;
            }

            result.SamplesUsed = samples.Count * uvNames.Count - rejectedSeam;
            result.SamplesRejected = rejectedDistance * uvNames.Count + rejectedSeam;
            result.SampledArea = (float)areaBefore;
            result.RmsBefore = areaBefore <= 0 ? 0 : (float)Math.Sqrt(rmsBeforeSq / areaBefore);
            result.RmsAfter = areaBefore <= 0 ? 0 : (float)Math.Sqrt(rmsAfterSq / areaBefore);
            result.MaxBefore = maxBefore;
            result.MaxAfter = maxAfter;
            result.VerticesAdjusted = adjusted;

            if (newUvs.Count > 0) result.NewUvs = newUvs;
            else result.SkippedReason = result.RmsBefore <= 1e-7f ? "texture already matches the original" : "no improvement found";
        }

        // A sample point on the current surface: which current triangle it sits in and where
        // (Bary), and the original triangle it projects onto and where (RefBary). Weight is the
        // surface area it stands for.
        private readonly record struct Sample(int A, int B, int C, Vector3 Bary, int RefTri, Vector3 RefBary, float Weight);

        private struct FitStats
        {
            public int RejectedSeam, Adjusted;
            public double SumSqBefore, SumSqAfter, Area;
            public float MaxBefore, MaxAfter;
        }

        // Solves one UV set. Returns the corrected per-vertex UVs, or null when the fit didn't
        // improve on what's there (in which case nothing should be written).
        private static Vector2[]? FitUvSet(string name, IList<Vector2> current, IList<Vector3> positions, List<Sample> samples,
            ReferenceSurface surface, Options options, out FitStats stats)
        {
            stats = default;
            int vertexCount = current.Count;

            // Targets, gated on the seam. Where the current triangle's corners are still original
            // corners (always, after simplify passes alone) the gate is exact: the hit has to be
            // in the same UV island. Otherwise it falls back to the distance in UV space.
            // Everything below runs over `used` only.
            var used = new List<(Sample S, Vector2 Target)>(samples.Count);
            float maxJumpSq = options.MaxUvJump * options.MaxUvJump;
            foreach (var s in samples)
            {
                var target = surface.Uv(name, s.RefTri, s.RefBary);
                var now = current[s.A] * s.Bary.X + current[s.B] * s.Bary.Y + current[s.C] * s.Bary.Z;
                float errSq = (target - now).LengthSquared();
                if (errSq > maxJumpSq) { stats.RejectedSeam++; continue; }
                int island = surface.IslandOfTriangle(positions[s.A], positions[s.B], positions[s.C],
                    current[s.A], current[s.B], current[s.C], name);
                if (island >= 0 && surface.IslandOf(s.RefTri, name) != island) { stats.RejectedSeam++; continue; }

                used.Add((s, target));
                stats.SumSqBefore += errSq * s.Weight;
                stats.Area += s.Weight;
                stats.MaxBefore = MathF.Max(stats.MaxBefore, MathF.Sqrt(errSq));
            }
            if (used.Count == 0) return null;

            // Per vertex: the samples that touch it and with what barycentric weight. Vertices no
            // sample reaches (unreferenced ones, and those only in rejected triangles) never move.
            var touching = new List<(int Sample, float B)>[vertexCount];
            for (int i = 0; i < used.Count; i++)
            {
                var s = used[i].S;
                (touching[s.A] ??= new()).Add((i, s.Bary.X));
                (touching[s.B] ??= new()).Add((i, s.Bary.Y));
                (touching[s.C] ??= new()).Add((i, s.Bary.Z));
            }

            var uv = current.ToArray();
            var diag = new float[vertexCount];      // sum of w*b^2 over touching samples
            var anchor = new float[vertexCount];    // AnchorWeight * diag: pull toward current
            for (int v = 0; v < vertexCount; v++)
            {
                if (touching[v] == null) continue;
                float d = 0;
                foreach (var (i, b) in touching[v]) d += used[i].S.Weight * b * b;
                diag[v] = d;
                anchor[v] = options.AnchorWeight * d;
            }

            // Gauss-Seidel: each vertex takes the value that minimizes its own samples' residuals
            // given where its neighbours currently are, sweeping until it settles. The system is
            // diagonally dominant (barycentric weights are non-negative and sum to one), so this
            // converges without any damping.
            for (int iter = 0; iter < options.Iterations; iter++)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    var list = touching[v];
                    if (list == null) continue;

                    var numerator = anchor[v] * current[v];
                    foreach (var (i, b) in list)
                    {
                        var (s, target) = used[i];
                        // The residual with this vertex's own contribution taken out.
                        var others = target - (uv[s.A] * s.Bary.X + uv[s.B] * s.Bary.Y + uv[s.C] * s.Bary.Z) + uv[v] * b;
                        numerator += s.Weight * b * others;
                    }
                    uv[v] = numerator / (diag[v] + anchor[v]);
                }
            }

            foreach (var (s, target) in used)
            {
                var now = uv[s.A] * s.Bary.X + uv[s.B] * s.Bary.Y + uv[s.C] * s.Bary.Z;
                float errSq = (target - now).LengthSquared();
                stats.SumSqAfter += errSq * s.Weight;
                stats.MaxAfter = MathF.Max(stats.MaxAfter, MathF.Sqrt(errSq));
            }

            if (stats.SumSqAfter >= stats.SumSqBefore)
            {
                // Nothing gained - report the surface as it stands rather than a fit that isn't used.
                stats.SumSqAfter = stats.SumSqBefore;
                stats.MaxAfter = stats.MaxBefore;
                return null;
            }

            for (int v = 0; v < vertexCount; v++)
                if ((uv[v] - current[v]).LengthSquared() > 1e-12f) stats.Adjusted++;

            return uv;
        }

        // Interior barycentric lattice points of a triangle at the given subdivision: every
        // (i, j, k) with i + j + k = n and all three positive. Corners and edges are left out - a
        // corner IS a vertex and can't disagree with itself, and an edge sample would be shared
        // between two triangles and counted twice.
        private static List<Vector3> Lattice(int n)
        {
            var points = new List<Vector3>();
            for (int i = 1; i < n; i++)
                for (int j = 1; i + j < n; j++)
                {
                    int k = n - i - j;
                    if (k < 1) continue;
                    points.Add(new Vector3(i, j, k) / n);
                }
            // n = 2 has no strictly-interior point; fall back to the centroid so every triangle
            // still gets measured.
            if (points.Count == 0) points.Add(new Vector3(1f / 3f));
            return points;
        }

        // One primitive of the original model, ready for "where on this surface is the point
        // nearest p, and what was the UV there" queries. Deliberately knows nothing about the fit
        // above: a texture re-bake needs exactly this same question answered per texel.
        internal sealed class ReferenceSurface
        {
            private readonly IReadOnlyList<Vector3> _positions;
            private readonly Dictionary<string, IReadOnlyList<Vector2>> _uvSets;
            private readonly int[] _indices;
            private readonly int _triangleCount;

            // Sparse uniform grid: each occupied cell lists the triangles whose bounding box
            // overlaps it. Keyed rather than a dense array because the mesh is a surface - almost
            // every cell of the volume it spans is empty, and the cell has to be small (see the
            // constructor) for the lists to stay short.
            private readonly Vector3 _gridMin;
            private readonly float _cellSize;
            private readonly int _nx, _ny, _nz;
            private readonly Dictionary<long, List<int>> _cells = new();

            public float Diagonal { get; }

            public ReferenceSurface(GeometryHistory.ReferencePrimitive source)
            {
                _positions = source.Positions;
                _uvSets = source.UvSets;
                _indices = source.Indices;
                _triangleCount = _indices.Length / 3;

                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var p in _positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                if (_positions.Count == 0) { min = max = Vector3.Zero; }

                var extent = max - min;
                Diagonal = extent.Length();

                // Cell size comes from the triangles themselves, not from how many there are per
                // unit of volume: the mesh is a surface, so sizing cells by cbrt(count) (as one
                // would for a point cloud filling the box) leaves hundreds of triangles per cell
                // on a dense character and the query scanning most of them. A cell about two
                // edges across holds a handful, and a query touching 27 of them stays cheap. The
                // floor keeps the axis counts bounded on a mesh of tiny triangles.
                double edgeSum = 0;
                for (int t = 0; t < _triangleCount; t++)
                {
                    Vector3 a = _positions[_indices[t * 3]], b = _positions[_indices[t * 3 + 1]], c = _positions[_indices[t * 3 + 2]];
                    edgeSum += (b - a).Length() + (c - b).Length() + (a - c).Length();
                }
                float meanEdge = _triangleCount > 0 ? (float)(edgeSum / (3.0 * _triangleCount)) : Diagonal;
                _cellSize = MathF.Max(MathF.Max(2f * meanEdge, Diagonal / 1024f), 1e-6f);
                _gridMin = min - new Vector3(_cellSize * 0.5f);
                _nx = (int)(extent.X / _cellSize) + 2;
                _ny = (int)(extent.Y / _cellSize) + 2;
                _nz = (int)(extent.Z / _cellSize) + 2;

                for (int t = 0; t < _triangleCount; t++)
                {
                    Vector3 a = _positions[_indices[t * 3]], b = _positions[_indices[t * 3 + 1]], c = _positions[_indices[t * 3 + 2]];
                    var (lo, hi) = (CellOf(Vector3.Min(a, Vector3.Min(b, c))), CellOf(Vector3.Max(a, Vector3.Max(b, c))));
                    for (int z = lo.Z; z <= hi.Z; z++)
                        for (int y = lo.Y; y <= hi.Y; y++)
                            for (int x = lo.X; x <= hi.X; x++)
                            {
                                long key = Index(x, y, z);
                                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>();
                                list.Add(t);
                            }
                }
            }

            public bool HasUvSet(string name) => _uvSets.ContainsKey(name);

            public int TriangleCount => _triangleCount;

            // UV islands, per UV set: the connected components of the original triangles when two
            // triangles count as joined only where they share a corner at the same position AND
            // the same UV. A seam is exactly where that fails - same position, different UV - so
            // the two sides of a seam land in different islands, and a nearest-point hit on the
            // far side is told apart from a genuine neighbour by island alone, with no distance
            // threshold to tune. Built on first use per UV set.
            private readonly Dictionary<string, (int[] IslandOfTriangle, Dictionary<CornerKey, int> IslandOfCorner)> _islands = new();

            private readonly record struct CornerKey(long X, long Y, long Z, long U, long V)
            {
                public static CornerKey Of(Vector3 p, Vector2 uv) => new(
                    (long)MathF.Round(p.X * 1e5f), (long)MathF.Round(p.Y * 1e5f), (long)MathF.Round(p.Z * 1e5f),
                    (long)MathF.Round(uv.X * 1e5f), (long)MathF.Round(uv.Y * 1e5f));
            }

            private (int[] IslandOfTriangle, Dictionary<CornerKey, int> IslandOfCorner) Islands(string uvSet)
            {
                lock (_islands)
                {
                    if (_islands.TryGetValue(uvSet, out var cached)) return cached;

                    var uvs = _uvSets[uvSet];
                    var cornerId = new Dictionary<CornerKey, int>();
                    var parent = new List<int>();
                    int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                    void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
                    int IdOf(int vertex)
                    {
                        var key = CornerKey.Of(_positions[vertex], uvs[vertex]);
                        if (!cornerId.TryGetValue(key, out int id)) { id = parent.Count; parent.Add(id); cornerId[key] = id; }
                        return id;
                    }

                    var triCorner = new int[_triangleCount];
                    for (int t = 0; t < _triangleCount; t++)
                    {
                        int a = IdOf(_indices[t * 3]), b = IdOf(_indices[t * 3 + 1]), c = IdOf(_indices[t * 3 + 2]);
                        Union(a, b); Union(b, c);
                        triCorner[t] = a;
                    }

                    var islandOfTriangle = new int[_triangleCount];
                    for (int t = 0; t < _triangleCount; t++) islandOfTriangle[t] = Find(triCorner[t]);
                    var islandOfCorner = new Dictionary<CornerKey, int>(cornerId.Count);
                    foreach (var (key, id) in cornerId) islandOfCorner[key] = Find(id);

                    var built = (islandOfTriangle, islandOfCorner);
                    _islands[uvSet] = built;
                    return built;
                }
            }

            public int IslandOf(int tri, string uvSet) => Islands(uvSet).IslandOfTriangle[tri];

            // The island a current triangle belongs to, from its corners' position+UV - which are
            // original corners after any number of simplify passes. -1 when a corner isn't one the
            // original had (a fill or cut added it) or the corners disagree, in which case the
            // caller has to fall back to its distance-based gate.
            public int IslandOfTriangle(Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, string uvSet)
            {
                var corners = Islands(uvSet).IslandOfCorner;
                if (!corners.TryGetValue(CornerKey.Of(a, ua), out int ia)) return -1;
                if (!corners.TryGetValue(CornerKey.Of(b, ub), out int ib) || ib != ia) return -1;
                if (!corners.TryGetValue(CornerKey.Of(c, uc), out int ic) || ic != ia) return -1;
                return ia;
            }

            // One original triangle's corners, in position and in the named UV set - what
            // TextureRebaker keys its "is this current triangle one the original already had"
            // lookup on, so the texels under an untouched triangle are never resampled.
            public (Vector3 A, Vector3 B, Vector3 C, Vector2 UvA, Vector2 UvB, Vector2 UvC) Triangle(int tri, string uvSet)
            {
                var uvs = _uvSets[uvSet];
                int a = _indices[tri * 3], b = _indices[tri * 3 + 1], c = _indices[tri * 3 + 2];
                return (_positions[a], _positions[b], _positions[c], uvs[a], uvs[b], uvs[c]);
            }

            // True when the current index buffer is exactly the original one over the same number
            // of vertices - the mapping can't have changed.
            public bool IsSameTopology((int A, int B, int C)[] tris, int vertexCount)
            {
                if (tris.Length != _triangleCount || vertexCount != _positions.Count) return false;
                for (int t = 0; t < tris.Length; t++)
                    if (tris[t].A != _indices[t * 3] || tris[t].B != _indices[t * 3 + 1] || tris[t].C != _indices[t * 3 + 2])
                        return false;
                return true;
            }

            public Vector2 Uv(string set, int tri, Vector3 bary)
            {
                var uvs = _uvSets[set];
                return uvs[_indices[tri * 3]] * bary.X + uvs[_indices[tri * 3 + 1]] * bary.Y + uvs[_indices[tri * 3 + 2]] * bary.Z;
            }

            // Nearest point on the surface to p, if there is one within maxDistance. Rings of
            // cells are searched outward from p's own cell; once the best distance found is no
            // further than the ring boundary, no cell further out can hold anything closer - and
            // once the ring boundary passes maxDistance, nothing further out is wanted anyway,
            // which is what keeps a sample sitting nowhere near the surface from scanning the
            // whole grid to find that out.
            public bool ClosestPoint(Vector3 p, float maxDistance, out int tri, out Vector3 bary, out float distance)
            {
                tri = -1; bary = default; distance = float.MaxValue;
                if (_triangleCount == 0) return false;

                var (cx, cy, cz) = CellOf(p);
                float bestSq = float.MaxValue;
                int maxRing = Math.Max(_nx, Math.Max(_ny, _nz));

                for (int ring = 0; ring <= maxRing; ring++)
                {
                    // Everything inside the previous ring has been seen; anything in this ring or
                    // beyond is at least (ring - 1) cells away, however p sits within its own cell.
                    float reach = (ring - 1) * _cellSize;
                    if (ring > 0 && (bestSq <= reach * reach || reach > maxDistance)) break;

                    for (int z = cz - ring; z <= cz + ring; z++)
                    {
                        if (z < 0 || z >= _nz) continue;
                        for (int y = cy - ring; y <= cy + ring; y++)
                        {
                            if (y < 0 || y >= _ny) continue;
                            bool faceZY = Math.Abs(z - cz) == ring || Math.Abs(y - cy) == ring;
                            for (int x = cx - ring; x <= cx + ring; x++)
                            {
                                if (x < 0 || x >= _nx) continue;
                                // Only the shell of this ring; the interior was the previous ring.
                                if (!faceZY && Math.Abs(x - cx) != ring) continue;

                                if (!_cells.TryGetValue(Index(x, y, z), out var list)) continue;
                                foreach (int t in list)
                                {
                                    var q = ClosestPointOnTriangle(p, t, out var b);
                                    float dSq = (q - p).LengthSquared();
                                    if (dSq < bestSq) { bestSq = dSq; tri = t; bary = b; }
                                }
                            }
                        }
                    }
                }

                if (tri < 0) return false;
                distance = MathF.Sqrt(bestSq);
                return distance <= maxDistance;
            }

            private (int X, int Y, int Z) CellOf(Vector3 p)
            {
                var rel = (p - _gridMin) / _cellSize;
                return (
                    Math.Clamp((int)MathF.Floor(rel.X), 0, _nx - 1),
                    Math.Clamp((int)MathF.Floor(rel.Y), 0, _ny - 1),
                    Math.Clamp((int)MathF.Floor(rel.Z), 0, _nz - 1));
            }

            private long Index(int x, int y, int z) => ((long)z * _ny + y) * _nx + x;

            // Ericson, Real-Time Collision Detection 5.1.5: classifies p against the triangle's
            // Voronoi regions (vertex, edge, face) and returns the nearest point with barycentrics.
            private Vector3 ClosestPointOnTriangle(Vector3 p, int tri, out Vector3 bary)
            {
                Vector3 a = _positions[_indices[tri * 3]], b = _positions[_indices[tri * 3 + 1]], c = _positions[_indices[tri * 3 + 2]];

                var ab = b - a; var ac = c - a; var ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0 && d2 <= 0) { bary = new Vector3(1, 0, 0); return a; }

                var bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0 && d4 <= d3) { bary = new Vector3(0, 1, 0); return b; }

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0 && d1 >= 0 && d3 <= 0)
                {
                    float v = d1 / (d1 - d3);
                    bary = new Vector3(1 - v, v, 0);
                    return a + ab * v;
                }

                var cp = p - c;
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0 && d5 <= d6) { bary = new Vector3(0, 0, 1); return c; }

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0 && d2 >= 0 && d6 <= 0)
                {
                    float w = d2 / (d2 - d6);
                    bary = new Vector3(1 - w, 0, w);
                    return a + ac * w;
                }

                float va = d3 * d6 - d5 * d4;
                if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                {
                    float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                    bary = new Vector3(0, 1 - w, w);
                    return b + (c - b) * w;
                }

                float denom = va + vb + vc;
                if (MathF.Abs(denom) < 1e-30f) { bary = new Vector3(1, 0, 0); return a; }   // degenerate sliver
                float bv = vb / denom, bw = vc / denom;
                bary = new Vector3(1 - bv - bw, bv, bw);
                return a + ab * bv + ac * bw;
            }
        }
    }
}
