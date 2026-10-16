using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Re-lays the model's texture out as a proper atlas with fewer seams: neighbouring UV islands
    // are joined into larger charts, unwrapped again from the 3D surface, packed by their real
    // outlines, and every texture the material reads is rebaked into the new layout.
    //
    // The usual way of squeezing a texture - chop each existing UV island out and pack the pieces
    // tighter - can only ever keep the seams the source already has, and gives every one of them
    // its own gutter (and with it a colour bleed at mip levels). Generated models (Meshy and
    // friends) come with hundreds of small islands, so that's hundreds of visible seams. Instead:
    //
    //  1. Merge. Start from the source's own islands, which are known to flatten. Repeatedly take
    //     the pair of charts sharing the longest seam, weld them along it and unwrap the union by
    //     least-squares conformal mapping (LSCM). Keep the union only if it flattens cleanly - no
    //     triangle turned over, no overlap beyond a crease-sized fold, and no part of it scaled
    //     more than MaxStretch away from the texel density the source gave that part of the
    //     surface (so a face that had a lot of texture keeps a lot, and nothing smears). An island
    //     that never merges keeps its source flattening exactly. Seams can only go down.
    //  2. Pack. Charts are turned to their smallest bounding rectangle and scaled to the source's
    //     density, then dropped into the atlas Tetris-style by their actual outline (not their
    //     bounding box) in whichever of four rotations sits lowest, with one global scale
    //     binary-searched to the largest that fits.
    //  3. Rebake. Geometry is unchanged, so every new texel knows exactly which triangle and which
    //     source UV it shows - no surface search. Each texel is sampled from the source there (the
    //     source pre-padded on its own islands, so bilinear never pulls in the old gutter), a texel
    //     or two past each chart edge as well, then the gutter is padded.
    //
    // Every image the remapped UV set feeds is rebaked into the same layout - albedo, normal,
    // metallic/roughness, occlusion, emissive - since they all have to follow the new UVs.
    // Tangent-space normal maps also get each texel's XY rotated from the old UV frame to the new
    // one: a chart is free to lie at any angle in the atlas, and a normal map's "right" and "up"
    // are the UV directions. TANGENT, when present, is recomputed for the new UVs.
    //
    // A vertex keeps one record per chart it touches, so vertices only ever split where the
    // source already had a seam. Primitives with morph targets are skipped, same as the other
    // geometry tools here.
    public static class TextureAtlasOptimizer
    {
        public sealed class Options
        {
            /// <summary>How far (linear factor) any part of a merged chart may drift from the texel density the source gave it.</summary>
            public float MaxStretch { get; set; } = 1.5f;

            /// <summary>Gutter between charts, in texels of the output image.</summary>
            public int PaddingTexels { get; set; } = 8;

            /// <summary>Scale each chart by the texel density the source gave that part of the surface, rather than one uniform density.</summary>
            public bool PreserveDensity { get; set; } = true;

            /// <summary>Longest side of every rebuilt image; 0 keeps each image's own size.</summary>
            public int OutputSize { get; set; }
        }

        public sealed class GroupResult
        {
            public string Label { get; init; } = "";
            public List<int> ImageIndices { get; } = new();
            public int Primitives { get; set; }
            public int Triangles { get; set; }
            public int IslandsBefore { get; set; }
            public int ChartsAfter { get; set; }

            /// <summary>Mesh edges (welded by position) where the UVs on the two sides disagree.</summary>
            public int SeamEdgesBefore { get; set; }
            public int SeamEdgesAfter { get; set; }
            public int VerticesBefore { get; set; }
            public int VerticesAfter { get; set; }

            /// <summary>Fraction of the atlas the charts cover.</summary>
            public float Fill { get; set; }

            /// <summary>Texel density kept, relative to the source (1 = same texels per unit of surface).</summary>
            public float DetailScale { get; set; }

            public string? SkippedReason { get; set; }
        }

        public sealed class Report
        {
            public List<GroupResult> Groups { get; } = new();
            public bool HasChanges => Rewrites.Count > 0;

            /// <summary>Rebuilt PNG bytes per LogicalImages index.</summary>
            public IReadOnlyDictionary<int, byte[]> Images => NewImages;

            internal List<PrimitiveRewrite> Rewrites { get; } = new();
            internal Dictionary<int, byte[]> NewImages { get; } = new();
        }

        public readonly record struct Progress(int Done, int Total, string Stage);

        // Everything Apply needs for one primitive, computed up front so Apply itself can run on
        // the model or on a preview clone of it.
        internal sealed class PrimitiveRewrite
        {
            public int MeshIndex;
            public int PrimIndex;
            public string UvSet = "";
            public int[] NewToOld = Array.Empty<int>();
            public Vector2[] Uvs = Array.Empty<Vector2>();
            public Vector4[]? Tangents;
            public int[] Indices = Array.Empty<int>();
        }

        public static Report Analyze(ModelRoot model, Options options,
            IProgress<Progress>? progress = null, CancellationToken cancellation = default)
        {
            var report = new Report();
            var groups = CollectGroups(model, report);

            int total = groups.Sum(g => 2 + g.Images.Count), done = 0;
            foreach (var group in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                ProcessGroup(model, group, options, report, progress, ref done, total, cancellation);
            }

            progress?.Report(new Progress(total, total, "done"));
            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var rw in report.Rewrites)
            {
                var prim = model.LogicalMeshes[rw.MeshIndex].Primitives[rw.PrimIndex];

                // Everything is read before anything is written - writing an accessor can
                // rearrange the buffers under the ones not yet read (see MeshLayoutOptimizer).
                var rewrites = new List<Action>();
                foreach (var (name, accessor) in prim.VertexAccessors)
                {
                    if (name == rw.UvSet || (name == "TANGENT" && rw.Tangents != null)) continue;
                    var rewrite = MeshLayoutOptimizer.BuildPermutedRewrite(prim, name, accessor, rw.NewToOld)
                        ?? throw new InvalidOperationException($"Attribute {name} has an encoding this tool can't rewrite.");
                    rewrites.Add(rewrite);
                }
                foreach (var rewrite in rewrites) rewrite();

                prim.WithVertexAccessor(rw.UvSet, rw.Uvs);
                if (rw.Tangents != null) prim.WithVertexAccessor("TANGENT", rw.Tangents);
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, rw.Indices);
            }

            // Replaced accessors leave dead views behind - same as MeshLayoutOptimizer; the
            // cleanup MainForm offers at save time reclaims them.
            if (report.Rewrites.Count > 0) model.MergeBuffers();

            foreach (var (imageIndex, png) in report.NewImages)
                TextureAtlasUtil.Apply(model, imageIndex, png);
        }

        // --- grouping ------------------------------------------------------------------------------

        // One primitive and the images it reads through the UV set being remapped.
        private sealed class PrimEntry
        {
            public int MeshIndex;
            public int PrimIndex;
            public string UvSet = "";
            public string MaterialName = "";
            public List<int> Images = new();
            public string? Ineligible;
        }

        // Images that have to share one layout - any two read by the same primitive - and every
        // primitive drawing through them.
        private sealed class Group
        {
            public List<PrimEntry> Prims = new();
            public List<int> Images = new();
            public HashSet<int> NormalImages = new();
            public string? Blocked;
        }

        private static List<Group> CollectGroups(ModelRoot model, Report report)
        {
            var entries = new List<PrimEntry>();
            var normalImages = new HashSet<int>();
            var foreign = new HashSet<int>();     // read through some other UV set, which stays as it is
            var transformed = new HashSet<int>(); // read through KHR_texture_transform

            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var prim = mesh.Primitives[primIdx];
                    var mat = prim.Material;
                    if (mat == null) continue;

                    var textured = mat.Channels.Where(ch => ch.Texture?.PrimaryImage != null).ToList();
                    if (textured.Count == 0) continue;

                    // The set albedo reads is the one that defines the look; anything a channel
                    // reads through another set (a lightmap on TEXCOORD_1) is left alone.
                    int set = textured.Any(ch => ch.Key == "BaseColor")
                        ? textured.First(ch => ch.Key == "BaseColor").TextureCoordinate
                        : textured[0].TextureCoordinate;

                    var entry = new PrimEntry
                    {
                        MeshIndex = meshIdx, PrimIndex = primIdx,
                        UvSet = $"TEXCOORD_{set}",
                        MaterialName = mat.Name ?? $"material_{mat.LogicalIndex}",
                    };
                    foreach (var ch in textured)
                    {
                        int img = ch.Texture!.PrimaryImage!.LogicalIndex;
                        if (ch.TextureTransform != null) transformed.Add(img);
                        if (ch.TextureCoordinate != set) { foreign.Add(img); continue; }
                        if (!entry.Images.Contains(img)) entry.Images.Add(img);
                        if (ch.Key == "Normal") normalImages.Add(img);
                    }

                    entry.Ineligible = CheckEligible(prim, entry.UvSet);
                    if (entry.Images.Count > 0) entries.Add(entry);
                }
            }

            // Union-find over images: a primitive reading two images ties them to one layout.
            var parent = new Dictionary<int, int>();
            int Find(int x)
            {
                if (!parent.TryGetValue(x, out int p)) { parent[x] = x; return x; }
                if (p == x) return x;
                return parent[x] = Find(p);
            }
            foreach (var e in entries)
                for (int i = 1; i < e.Images.Count; i++)
                    parent[Find(e.Images[i])] = Find(e.Images[0]);

            var byRoot = new Dictionary<int, Group>();
            foreach (var e in entries)
            {
                int root = Find(e.Images[0]);
                if (!byRoot.TryGetValue(root, out var g)) byRoot[root] = g = new Group();
                g.Prims.Add(e);
                foreach (int img in e.Images)
                    if (!g.Images.Contains(img)) g.Images.Add(img);
            }

            var groups = new List<Group>();
            foreach (var g in byRoot.Values.OrderBy(g => g.Images.Min()))
            {
                g.Images.Sort();
                foreach (int img in g.Images)
                    if (normalImages.Contains(img)) g.NormalImages.Add(img);

                var bad = g.Prims.FirstOrDefault(p => p.Ineligible != null);
                if (bad != null) g.Blocked = $"{bad.MaterialName}: {bad.Ineligible}";
                else if (g.Images.Any(foreign.Contains)) g.Blocked = "an image is also read through a different UV set";
                else if (g.Images.Any(transformed.Contains)) g.Blocked = "uses a texture transform";

                if (g.Blocked != null)
                {
                    var skipped = new GroupResult { Label = GroupLabel(g), SkippedReason = g.Blocked };
                    skipped.ImageIndices.AddRange(g.Images);
                    report.Groups.Add(skipped);
                    continue;
                }
                groups.Add(g);
            }
            return groups;
        }

        private static string GroupLabel(Group g) => string.Join(", ", g.Prims.Select(p => p.MaterialName).Distinct());

        private static string? CheckEligible(MeshPrimitive prim, string uvSet)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) return "not a triangle list";
            if (prim.MorphTargetsCount > 0) return "has morph targets";
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var pos)) return "no POSITION";
            if (!prim.VertexAccessors.ContainsKey(uvSet)) return $"no {uvSet}";

            foreach (var (name, accessor) in prim.VertexAccessors)
            {
                if (accessor.Count != pos.Count) return $"{name} doesn't line up with POSITION";
                if (name == uvSet) continue;
                bool ok = accessor.Encoding == EncodingType.FLOAT
                    ? accessor.Dimensions is DimensionType.SCALAR or DimensionType.VEC2 or DimensionType.VEC3 or DimensionType.VEC4
                    : accessor.Dimensions == DimensionType.VEC4
                        && accessor.Encoding is EncodingType.UNSIGNED_BYTE or EncodingType.UNSIGNED_SHORT or EncodingType.UNSIGNED_INT;
                if (!ok) return $"{name} has an encoding this tool can't rewrite";
            }
            return null;
        }

        // --- per-primitive surface ---------------------------------------------------------------

        private sealed class PrimData
        {
            public required PrimEntry Entry;
            public required Vector3[] Pos;      // per original vertex
            public required Vector2[] Uv;       // source UVs, per original vertex
            public Vector3[]? Normals;
            public bool HasTangents;
            public required int[] Tris;         // original vertex indices, 3 per triangle
            public required int[] Weld;         // original vertex -> welded vertex
            public required Vector3[] WPos;     // per welded vertex
            public required Vector3[] FaceN;    // unit, or zero for a degenerate triangle
            public required float[] Area;
            public required float[] SourceTexArea;   // per triangle, in source texels
            public required int[] Adj;          // per triangle edge: the triangle across it, or -1
            public int TriCount => Tris.Length / 3;
            public int W(int t, int k) => Weld[Tris[t * 3 + k]];
        }

        private static PrimData LoadPrim(ModelRoot model, PrimEntry entry, int srcW, int srcH)
        {
            var prim = model.LogicalMeshes[entry.MeshIndex].Primitives[entry.PrimIndex];
            var pos = prim.VertexAccessors["POSITION"].AsVector3Array().ToArray();
            var uv = prim.VertexAccessors[entry.UvSet].AsVector2Array().ToArray();
            var normals = prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc) ? nAcc.AsVector3Array().ToArray() : null;

            var triList = new List<int>();
            foreach (var (a, b, c) in prim.GetTriangleIndices()) { triList.Add(a); triList.Add(b); triList.Add(c); }
            var tris = triList.ToArray();

            // Weld by position alone - the whole point is that a chart may run straight across
            // a seam the source UVs had. The tolerance is relative so it means the same thing
            // for a model built in metres or centimetres.
            var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
            foreach (var p in pos) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float quantum = MathF.Max(Vector3.Distance(min, max) * 1e-6f, 1e-9f);

            var weld = new int[pos.Length];
            var wpos = new List<Vector3>();
            var byKey = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < pos.Length; i++)
            {
                var key = ((long)MathF.Round(pos[i].X / quantum), (long)MathF.Round(pos[i].Y / quantum), (long)MathF.Round(pos[i].Z / quantum));
                if (!byKey.TryGetValue(key, out int w)) { byKey[key] = w = wpos.Count; wpos.Add(pos[i]); }
                weld[i] = w;
            }

            int triCount = tris.Length / 3;
            var faceN = new Vector3[triCount];
            var area = new float[triCount];
            var texArea = new float[triCount];
            var texScale = new Vector2(srcW, srcH);
            for (int t = 0; t < triCount; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                var cross = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                float len = cross.Length();
                area[t] = len * 0.5f;
                faceN[t] = len > 1e-20f ? cross / len : Vector3.Zero;
                texArea[t] = MathF.Abs(Cross2D((uv[b] - uv[a]) * texScale, (uv[c] - uv[a]) * texScale)) * 0.5f;
            }

            // Two triangles are neighbours across an edge only when exactly those two share it;
            // a non-manifold edge is always a chart boundary.
            int wCount = wpos.Count;
            var edges = new Dictionary<long, (int First, int Second, int Count)>();
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int a = weld[tris[t * 3 + k]], b = weld[tris[t * 3 + (k + 1) % 3]];
                    if (a == b) continue;
                    long key = (long)Math.Min(a, b) * wCount + Math.Max(a, b);
                    edges[key] = edges.TryGetValue(key, out var e)
                        ? (e.First, t * 3 + k, e.Count + 1)
                        : (t * 3 + k, -1, 1);
                }
            }
            var adj = new int[triCount * 3];
            Array.Fill(adj, -1);
            foreach (var e in edges.Values)
            {
                if (e.Count != 2) continue;
                adj[e.First] = e.Second / 3;
                adj[e.Second] = e.First / 3;
            }

            return new PrimData
            {
                Entry = entry, Pos = pos, Uv = uv, Normals = normals,
                HasTangents = prim.VertexAccessors.ContainsKey("TANGENT"),
                Tris = tris, Weld = weld, WPos = wpos.ToArray(), FaceN = faceN, Area = area,
                SourceTexArea = texArea, Adj = adj,
            };
        }

        // The SOURCE layout's islands (triangles joined across edges whose UVs agree on both
        // sides) and how many welded edges are seams in it.
        private static (int Seams, List<int[]> Islands) SourceIslands(PrimData p)
        {
            int triCount = p.TriCount;
            var parent = Enumerable.Range(0, triCount).ToArray();
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }

            int seams = 0;
            for (int t = 0; t < triCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int o = p.Adj[t * 3 + k];
                    if (o < t) continue;   // each shared edge once (and -1 skipped)
                    bool same = SameUvAt(p, t, o, p.W(t, k)) && SameUvAt(p, t, o, p.W(t, (k + 1) % 3));
                    if (same) parent[Find(t)] = Find(o);
                    else seams++;
                }
            }

            var islands = new Dictionary<int, List<int>>();
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(t);
                if (!islands.TryGetValue(root, out var list)) islands[root] = list = new List<int>();
                list.Add(t);
            }
            return (seams, islands.Values.Select(l => l.ToArray()).ToList());
        }

        private static bool SameUvAt(PrimData p, int t, int o, int welded)
        {
            Vector2? a = null, b = null;
            for (int k = 0; k < 3; k++)
            {
                if (p.W(t, k) == welded) a = p.Uv[p.Tris[t * 3 + k]];
                if (p.W(o, k) == welded) b = p.Uv[p.Tris[o * 3 + k]];
            }
            return a.HasValue && b.HasValue && Vector2.DistanceSquared(a.Value, b.Value) < 1e-12f;
        }

        private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
        {
            float len = v.Length();
            return len > 1e-20f ? v / len : (fallback == Vector3.Zero ? Vector3.UnitY : fallback);
        }

        // --- charts ------------------------------------------------------------------------------

        private sealed class Chart
        {
            public required PrimData Prim;
            public required int[] Tris;          // triangle indices within Prim
            public required int[] LocalTris;     // per corner of Tris: index into Pts
            public Vector2[] Pts = Array.Empty<Vector2>();   // flattened, then oriented + scaled
            public int Id;
            public float Area3D;
            public float SourceTexArea;
            public float Area2D;                 // of Pts once scaled to the source density
            public Vector2 Size;                 // bounding box of Pts at scale 1
            public int X, Y, Rotation;           // packed position, in packer cells; quarter turns
            public Vector2 RotatedMin;
            public Vector2[] FinalUv = Array.Empty<Vector2>();
        }

        // A source island as a chart, flattened exactly as the source had it. Corners are keyed by
        // welded position AND source UV: an island can meet itself along a seam (a sleeve cut open
        // down one side), and the two sides of that cut must stay two points.
        private static Chart SourceChart(PrimData p, int[] tris, Vector2 texScale)
        {
            var localOf = new Dictionary<(int, long, long), int>();
            var pts = new List<Vector2>();
            var localTris = new int[tris.Length * 3];
            for (int i = 0; i < tris.Length; i++)
                for (int k = 0; k < 3; k++)
                {
                    int ov = p.Tris[tris[i] * 3 + k];
                    var uv = p.Uv[ov];
                    var key = (p.Weld[ov], (long)MathF.Round(uv.X * 1e6f), (long)MathF.Round(uv.Y * 1e6f));
                    if (!localOf.TryGetValue(key, out int l)) { localOf[key] = l = pts.Count; pts.Add(uv * texScale); }
                    localTris[i * 3 + k] = l;
                }
            return new Chart { Prim = p, Tris = tris, LocalTris = localTris, Pts = pts.ToArray() };
        }

        // One corner per welded position - what a fresh unwrap works on.
        private static (int[] LocalTris, Vector3[] Pos) WeldedCorners(PrimData p, int[] tris)
        {
            var localOf = new Dictionary<int, int>();
            var pos = new List<Vector3>();
            var localTris = new int[tris.Length * 3];
            for (int i = 0; i < tris.Length; i++)
                for (int k = 0; k < 3; k++)
                {
                    int w = p.W(tris[i], k);
                    if (!localOf.TryGetValue(w, out int l)) { localOf[w] = l = pos.Count; pos.Add(p.WPos[w]); }
                    localTris[i * 3 + k] = l;
                }
            return (localTris, pos.ToArray());
        }

        // --- merging -----------------------------------------------------------------------------

        // Greedy seam removal. Every round looks at each pair of charts that share edges, longest
        // shared seam first, and tries to unwrap the pair as one chart; a union that flattens
        // cleanly (see Validate) replaces the two. Pairs are tried in parallel; a chart takes part
        // in at most one accepted merge per round, and a pair that failed is never tried again
        // (its charts would have to change first, which gives them new ids). Stops when no untried
        // pair is left.
        private static List<Chart> MergeCharts(List<PrimData> prims, List<Chart> charts, Options options,
            Action<int> onRound, CancellationToken cancellation)
        {
            var byId = new Dictionary<int, Chart>();
            var owner = prims.ToDictionary(p => p, p => new int[p.TriCount]);
            int nextId = 0;
            foreach (var c in charts)
            {
                c.Id = nextId++;
                byId[c.Id] = c;
                foreach (int t in c.Tris) owner[c.Prim][t] = c.Id;
            }

            var failed = new HashSet<(int, int)>();
            int extra = Math.Max(8, Environment.ProcessorCount * 2);

            while (true)
            {
                cancellation.ThrowIfCancellationRequested();

                var shared = new Dictionary<(int, int), int>();
                foreach (var p in prims)
                {
                    var own = owner[p];
                    for (int t = 0; t < p.TriCount; t++)
                        for (int k = 0; k < 3; k++)
                        {
                            int o = p.Adj[t * 3 + k];
                            if (o <= t || own[o] == own[t]) continue;
                            var key = own[o] < own[t] ? (own[o], own[t]) : (own[t], own[o]);
                            shared[key] = shared.GetValueOrDefault(key) + 1;
                        }
                }

                var candidates = shared.Where(kv => !failed.Contains(kv.Key))
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                    .Select(kv => kv.Key).ToList();
                if (candidates.Count == 0) break;

                // Everything that could merge this round without a conflict, plus the next few
                // best pairs (which may well touch the same big chart) to keep every core busy.
                var busy = new HashSet<int>();
                var tested = new List<(int, int)>();
                int extras = 0;
                foreach (var key in candidates)
                {
                    bool free = !busy.Contains(key.Item1) && !busy.Contains(key.Item2);
                    if (free) { busy.Add(key.Item1); busy.Add(key.Item2); }
                    else if (extras++ >= extra) continue;
                    tested.Add(key);
                }

                var unions = new Chart?[tested.Count];
                Parallel.For(0, tested.Count, new ParallelOptions { CancellationToken = cancellation },
                    i => unions[i] = TryUnion(byId[tested[i].Item1], byId[tested[i].Item2], options));

                var taken = new HashSet<int>();
                for (int i = 0; i < tested.Count; i++)
                {
                    var (a, b) = tested[i];
                    if (unions[i] == null) { failed.Add((a, b)); continue; }
                    if (taken.Contains(a) || taken.Contains(b)) continue;   // retried next round
                    taken.Add(a); taken.Add(b);

                    var u = unions[i]!;
                    u.Id = nextId++;
                    byId.Remove(a);
                    byId.Remove(b);
                    byId[u.Id] = u;
                    foreach (int t in u.Tris) owner[u.Prim][t] = u.Id;
                }
                onRound(byId.Count);
            }
            return byId.Values.ToList();
        }

        private static Chart? TryUnion(Chart a, Chart b, Options options)
        {
            var p = a.Prim;
            var tris = new int[a.Tris.Length + b.Tris.Length];
            a.Tris.CopyTo(tris, 0);
            b.Tris.CopyTo(tris, a.Tris.Length);

            // The area each triangle should end up with, relative to the rest: what the source
            // gave it when keeping density, else its share of the surface. A triangle the source
            // squashed to nothing gets the union's typical density.
            var target = new float[tris.Length];
            double tex = 0, area = 0;
            var sum = Vector3.Zero;
            foreach (int t in tris)
            {
                sum += p.FaceN[t] * p.Area[t];
                if (p.SourceTexArea[t] > 0) { tex += p.SourceTexArea[t]; area += p.Area[t]; }
            }
            float density2 = area > 0 ? (float)(tex / area) : 1f;
            for (int i = 0; i < tris.Length; i++)
            {
                int t = tris[i];
                target[i] = !options.PreserveDensity ? p.Area[t]
                    : p.SourceTexArea[t] > p.Area[t] * density2 * 1e-3f ? p.SourceTexArea[t] : p.Area[t] * density2;
            }

            var (localTris, pos) = WeldedCorners(p, tris);
            var projected = Project(pos, SafeNormalize(sum, Vector3.UnitY));
            var lscm = Lscm(localTris, pos, projected);
            if (lscm == null || !Validate(localTris, pos, lscm, target, options.MaxStretch)) return null;
            return new Chart { Prim = p, Tris = tris, LocalTris = localTris, Pts = lscm };
        }

        private static Vector2[] Project(Vector3[] pos, Vector3 n)
        {
            var u = Vector3.Cross(MathF.Abs(n.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, n);
            u = Vector3.Normalize(u);
            var v = Vector3.Cross(n, u);   // (u, v, n) right-handed: front faces come out counter-clockwise
            return pos.Select(q => new Vector2(Vector3.Dot(q, u), Vector3.Dot(q, v))).ToArray();
        }

        // Least-squares conformal map (Lévy et al. 2002). Each triangle contributes the two real
        // rows of sum_j W_j (u_j + i v_j) = 0, which any orientation-preserving similarity of the
        // triangle satisfies exactly; two pinned vertices fix the global similarity. Solved by
        // column-scaled CGLS started from the projection, which is already close.
        private static Vector2[]? Lscm(int[] localTris, Vector3[] pos, Vector2[] init)
        {
            int n = pos.Length;
            if (n <= 3) return init;

            var b0 = Bounds(init);
            bool alongX = b0.Max.X - b0.Min.X >= b0.Max.Y - b0.Min.Y;
            int pinA = 0, pinB = 0;
            for (int i = 1; i < n; i++)
            {
                float key = alongX ? init[i].X : init[i].Y;
                if (key < (alongX ? init[pinA].X : init[pinA].Y)) pinA = i;
                if (key > (alongX ? init[pinB].X : init[pinB].Y)) pinB = i;
            }
            if (pinA == pinB) return null;

            var free = new int[n];
            int nFree = 0;
            for (int i = 0; i < n; i++) free[i] = i == pinA || i == pinB ? -1 : nFree++;
            int cols = nFree * 2;

            int triCount = localTris.Length / 3;
            var rowCols = new int[triCount * 2 * 6];
            var rowVals = new double[triCount * 2 * 6];
            var rhs = new double[triCount * 2];
            int rows = 0;
            var wr = new double[3];
            var wi = new double[3];
            var vid = new int[3];

            for (int t = 0; t < triCount; t++)
            {
                int i0 = localTris[t * 3], i1 = localTris[t * 3 + 1], i2 = localTris[t * 3 + 2];
                var e1 = pos[i1] - pos[i0];
                var e2 = pos[i2] - pos[i0];
                var normal = Vector3.Cross(e1, e2);
                double area2 = normal.Length();
                float len1 = e1.Length();
                if (area2 < 1e-20 || len1 < 1e-12f) continue;

                // The triangle in its own plane: z0 = 0, z1 on the x axis.
                var ax = e1 / len1;
                var ay = Vector3.Cross(Vector3.Normalize(normal), ax);
                double x1 = len1, x2 = Vector3.Dot(e2, ax), y2 = Vector3.Dot(e2, ay);

                double w = 1.0 / Math.Sqrt(area2);
                // W0 = z2 - z1, W1 = z0 - z2, W2 = z1 - z0
                wr[0] = (x2 - x1) * w; wr[1] = -x2 * w; wr[2] = x1 * w;
                wi[0] = y2 * w; wi[1] = -y2 * w; wi[2] = 0;
                vid[0] = i0; vid[1] = i1; vid[2] = i2;

                for (int part = 0; part < 2; part++)
                {
                    int baseIdx = rows * 6;
                    double b = 0;
                    for (int j = 0; j < 3; j++)
                    {
                        // Real row: Wr u - Wi v; imaginary row: Wi u + Wr v.
                        double cu = part == 0 ? wr[j] : wi[j];
                        double cv = part == 0 ? -wi[j] : wr[j];
                        int f = free[vid[j]];
                        if (f < 0)
                        {
                            b -= cu * init[vid[j]].X + cv * init[vid[j]].Y;
                            rowCols[baseIdx + j * 2] = -1;
                            rowCols[baseIdx + j * 2 + 1] = -1;
                        }
                        else
                        {
                            rowCols[baseIdx + j * 2] = f * 2;
                            rowCols[baseIdx + j * 2 + 1] = f * 2 + 1;
                        }
                        rowVals[baseIdx + j * 2] = cu;
                        rowVals[baseIdx + j * 2 + 1] = cv;
                    }
                    rhs[rows++] = b;
                }
            }
            if (rows == 0) return null;

            var colScale = new double[cols];
            for (int r = 0; r < rows * 6; r++)
                if (rowCols[r] >= 0) colScale[rowCols[r]] += rowVals[r] * rowVals[r];
            for (int c = 0; c < cols; c++) colScale[c] = colScale[c] > 0 ? 1.0 / Math.Sqrt(colScale[c]) : 1.0;

            void MulA(double[] x, double[] y)   // y = A D x
            {
                for (int r = 0; r < rows; r++)
                {
                    double s = 0;
                    for (int j = 0; j < 6; j++)
                    {
                        int c = rowCols[r * 6 + j];
                        if (c >= 0) s += rowVals[r * 6 + j] * colScale[c] * x[c];
                    }
                    y[r] = s;
                }
            }
            void MulAt(double[] y, double[] x)  // x = D A^T y
            {
                Array.Clear(x);
                for (int r = 0; r < rows; r++)
                {
                    double yr = y[r];
                    if (yr == 0) continue;
                    for (int j = 0; j < 6; j++)
                    {
                        int c = rowCols[r * 6 + j];
                        if (c >= 0) x[c] += rowVals[r * 6 + j] * yr;
                    }
                }
                for (int c = 0; c < cols; c++) x[c] *= colScale[c];
            }

            var z = new double[cols];
            for (int i = 0; i < n; i++)
            {
                int f = free[i];
                if (f < 0) continue;
                z[f * 2] = init[i].X / colScale[f * 2];
                z[f * 2 + 1] = init[i].Y / colScale[f * 2 + 1];
            }

            var residual = new double[rows];
            var q = new double[rows];
            var s = new double[cols];
            var dir = new double[cols];

            MulA(z, q);
            for (int r = 0; r < rows; r++) residual[r] = rhs[r] - q[r];
            MulAt(residual, s);
            Array.Copy(s, dir, cols);
            double gamma = Dot(s, s), gamma0 = gamma;
            if (gamma0 == 0) return init;

            int maxIterations = Math.Min(2000, 100 + 40 * (int)Math.Sqrt(n));
            for (int it = 0; it < maxIterations; it++)
            {
                MulA(dir, q);
                double qq = Dot(q, q);
                if (qq <= 0) break;
                double alpha = gamma / qq;
                for (int c = 0; c < cols; c++) z[c] += alpha * dir[c];
                for (int r = 0; r < rows; r++) residual[r] -= alpha * q[r];
                MulAt(residual, s);
                double gammaNew = Dot(s, s);
                if (gammaNew <= 1e-14 * gamma0) break;
                double beta = gammaNew / gamma;
                for (int c = 0; c < cols; c++) dir[c] = s[c] + beta * dir[c];
                gamma = gammaNew;
            }

            var result = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                int f = free[i];
                result[i] = f < 0 ? init[i]
                    : new Vector2((float)(z[f * 2] * colScale[f * 2]), (float)(z[f * 2 + 1] * colScale[f * 2 + 1]));
                if (!float.IsFinite(result[i].X) || !float.IsFinite(result[i].Y)) return null;
            }
            return result;
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        // Generated meshes are full of little folds - a flap of surface lying back over its
        // neighbours - that no flattening can lay out without some overlap. A fold this small
        // (by area) is tolerated: the texels it shares show the neighbouring surface, deep in a
        // crease where nobody sees them, which beats keeping a whole seam open for it.
        private const float FoldTolerance = 0.005f;

        // Share of a chart (by target area) allowed outside MaxStretch - the odd squeezed sliver
        // shouldn't veto an otherwise good merge.
        private const float StretchTolerance = 0.05f;

        // A flattening is usable when (folds aside) no triangle is turned over, no two triangles
        // land on the same spot, and every part of it is within maxStretch of its target density
        // relative to the rest. Mirrors the whole chart first if it came out the wrong way round
        // (that alone isn't a fault). Fixes pts in place.
        private static bool Validate(int[] localTris, Vector3[] pos, Vector2[] pts, float[] target, float maxStretch)
        {
            int triCount = localTris.Length / 3;
            var a2 = new float[triCount];
            var a3 = new float[triCount];
            double pos2 = 0, neg2 = 0, total3 = 0, totalTarget = 0;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = localTris[t * 3], i1 = localTris[t * 3 + 1], i2 = localTris[t * 3 + 2];
                a2[t] = Cross2D(pts[i1] - pts[i0], pts[i2] - pts[i0]) * 0.5f;
                a3[t] = Vector3.Cross(pos[i1] - pos[i0], pos[i2] - pos[i0]).Length() * 0.5f;
                if (a2[t] > 0) pos2 += a2[t]; else neg2 -= a2[t];
                total3 += a3[t];
                totalTarget += target[t];
            }
            if (!(pos2 + neg2 > 0) || !(totalTarget > 0)) return false;

            if (neg2 > pos2)
            {
                for (int i = 0; i < pts.Length; i++) pts[i].X = -pts[i].X;
                for (int t = 0; t < triCount; t++) a2[t] = -a2[t];
                (pos2, neg2) = (neg2, pos2);
            }

            // Slivers flip and stretch harmlessly; they are ignored throughout.
            float minArea3 = (float)(total3 / triCount * 1e-3);
            double ratio = pos2 / totalTarget;
            double limit = maxStretch * maxStretch;
            double badStretch = 0;
            var faulty = new HashSet<int>();
            for (int t = 0; t < triCount; t++)
            {
                if (a3[t] < minArea3) continue;
                if (a2[t] <= 0) { faulty.Add(t); continue; }
                double r = a2[t] / target[t];
                if (r < ratio / limit || r > ratio * limit) badStretch += target[t];
            }
            if (badStretch > totalTarget * StretchTolerance) return false;

            double foldBudget = total3 * FoldTolerance;
            if (faulty.Sum(t => (double)a3[t]) > foldBudget) return false;
            FindOverlaps(localTris, pts, a2, a3, minArea3, faulty);
            return faulty.Sum(t => (double)a3[t]) <= foldBudget;
        }

        // Rasterizes the chart at a modest resolution: a sample strictly inside two triangles that
        // don't even share a corner means the chart lies on top of itself there. The smaller of
        // each such pair goes into `faulty`.
        private static void FindOverlaps(int[] localTris, Vector2[] pts, float[] a2, float[] a3, float minArea3, HashSet<int> faulty)
        {
            int triCount = localTris.Length / 3;
            if (triCount < 2) return;
            var b = Bounds(pts);
            float extent = MathF.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y);
            if (extent <= 0) return;

            int grid = Math.Clamp((int)(MathF.Sqrt(triCount) * 4), 32, 512);
            float cell = extent / grid;
            int gw = (int)MathF.Ceiling((b.Max.X - b.Min.X) / cell) + 1;
            int gh = (int)MathF.Ceiling((b.Max.Y - b.Min.Y) / cell) + 1;
            var owner = new int[gw * gh];
            Array.Fill(owner, -1);

            for (int t = 0; t < triCount; t++)
            {
                if (a2[t] <= 0 || a3[t] < minArea3) continue;
                var p0 = (pts[localTris[t * 3]] - b.Min) / cell;
                var p1 = (pts[localTris[t * 3 + 1]] - b.Min) / cell;
                var p2 = (pts[localTris[t * 3 + 2]] - b.Min) / cell;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X))));
                int x1 = Math.Min(gw - 1, (int)MathF.Ceiling(MathF.Max(p0.X, MathF.Max(p1.X, p2.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y))));
                int y1 = Math.Min(gh - 1, (int)MathF.Ceiling(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        var s = new Vector2(x + 0.5f, y + 0.5f);
                        if (Cross2D(p1 - p0, s - p0) <= 0 || Cross2D(p2 - p1, s - p1) <= 0 || Cross2D(p0 - p2, s - p2) <= 0) continue;
                        int idx = y * gw + x;
                        int o = owner[idx];
                        if (o < 0) { owner[idx] = t; continue; }
                        if (!SharesCorner(localTris, t, o)) faulty.Add(a3[t] <= a3[o] ? t : o);
                    }
            }
        }

        private static bool SharesCorner(int[] localTris, int a, int b)
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    if (localTris[a * 3 + i] == localTris[b * 3 + j]) return true;
            return false;
        }

        // --- orienting / packing -----------------------------------------------------------------

        // Turns the chart to its minimum-area bounding rectangle (rotating calipers over the hull)
        // and moves it to the origin.
        private static void Orient(Chart c)
        {
            var hull = ConvexHull(c.Pts);
            float bestArea = float.MaxValue, bestAngle = 0;
            for (int i = 0; i < hull.Count; i++)
            {
                var edge = hull[(i + 1) % hull.Count] - hull[i];
                if (edge.LengthSquared() < 1e-24f) continue;
                float angle = MathF.Atan2(edge.Y, edge.X);
                float cos = MathF.Cos(-angle), sin = MathF.Sin(-angle);
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                foreach (var h in hull)
                {
                    float x = h.X * cos - h.Y * sin, y = h.X * sin + h.Y * cos;
                    minX = MathF.Min(minX, x); maxX = MathF.Max(maxX, x);
                    minY = MathF.Min(minY, y); maxY = MathF.Max(maxY, y);
                }
                float area = (maxX - minX) * (maxY - minY);
                if (area < bestArea) { bestArea = area; bestAngle = angle; }
            }

            float c0 = MathF.Cos(-bestAngle), s0 = MathF.Sin(-bestAngle);
            for (int i = 0; i < c.Pts.Length; i++)
            {
                var q = c.Pts[i];
                c.Pts[i] = new Vector2(q.X * c0 - q.Y * s0, q.X * s0 + q.Y * c0);
            }
            var b = Bounds(c.Pts);
            for (int i = 0; i < c.Pts.Length; i++) c.Pts[i] -= b.Min;
            c.Size = b.Max - b.Min;
        }

        private static List<Vector2> ConvexHull(Vector2[] pts)
        {
            var sorted = pts.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
            if (sorted.Count < 3) return sorted;
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                foreach (var q in sorted)
                {
                    while (hull.Count >= start + 2 && Cross2D(hull[^1] - hull[^2], q - hull[^2]) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(q);
                }
                hull.RemoveAt(hull.Count - 1);
                sorted.Reverse();
            }
            return hull;
        }

        // Quarter turns, all proper rotations: a mirrored chart would flip its normal map.
        private static Vector2 Rotate(Vector2 q, int quarterTurns) => quarterTurns switch
        {
            1 => new Vector2(-q.Y, q.X),
            2 => new Vector2(-q.X, -q.Y),
            3 => new Vector2(q.Y, -q.X),
            _ => q,
        };

        // A chart's footprint on the packer's cell grid, in one rotation: per column, the lowest
        // and highest occupied cell (padding included). Anything between counts as taken - a
        // C-shaped chart doesn't get something packed into its mouth from above - but what lies
        // under its lower edge does get used, which is most of what bounding boxes waste.
        private sealed class Footprint
        {
            public int W, H;
            public int[] Bottom = Array.Empty<int>();   // int.MaxValue for an empty column
            public int[] Top = Array.Empty<int>();
            public Vector2 Min;                         // rotated chart's bounding-box minimum, texels
        }

        private static Footprint MakeFootprint(Chart c, float k, int rotation, float cell, int padCells)
        {
            var pts = new Vector2[c.Pts.Length];
            var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = Rotate(c.Pts[i] * k, rotation);
                min = Vector2.Min(min, pts[i]); max = Vector2.Max(max, pts[i]);
            }

            int w = (int)MathF.Ceiling((max.X - min.X) / cell) + 1 + 2 * padCells;
            int h = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 1 + 2 * padCells;
            var mask = new bool[w * h];
            var origin = min - new Vector2(padCells * cell);

            // Conservative: a cell is taken if the triangle touches it at all.
            for (int t = 0; t < c.LocalTris.Length / 3; t++)
            {
                var a = (pts[c.LocalTris[t * 3]] - origin) / cell;
                var b = (pts[c.LocalTris[t * 3 + 1]] - origin) / cell;
                var d = (pts[c.LocalTris[t * 3 + 2]] - origin) / cell;
                float area = Cross2D(b - a, d - a);
                float sign = area < 0 ? -1f : 1f;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, d.X))));
                int x1 = Math.Min(w - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, d.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, d.Y))));
                int y1 = Math.Min(h - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, d.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (area == 0 || (EdgeTouches(a, b, x, y, sign) && EdgeTouches(b, d, x, y, sign) && EdgeTouches(d, a, x, y, sign)))
                            mask[y * w + x] = true;
                    }
            }

            // Padding: grow the footprint by padCells all round.
            if (padCells > 0) mask = Dilate(mask, w, h, padCells);

            var fp = new Footprint { W = w, H = h, Bottom = new int[w], Top = new int[w], Min = min };
            for (int x = 0; x < w; x++)
            {
                fp.Bottom[x] = int.MaxValue;
                fp.Top[x] = -1;
                for (int y = 0; y < h; y++)
                {
                    if (!mask[y * w + x]) continue;
                    if (fp.Bottom[x] == int.MaxValue) fp.Bottom[x] = y;
                    fp.Top[x] = y;
                }
            }
            return fp;
        }

        // Whether the unit cell (x, y) reaches the inner side of edge a->b: true if its corner
        // furthest along the edge's inward normal is inside.
        private static bool EdgeTouches(Vector2 a, Vector2 b, int x, int y, float sign)
        {
            var e = b - a;
            float nx = -e.Y * sign, ny = e.X * sign;   // inward normal
            float cx = nx >= 0 ? x + 1 : x;
            float cy = ny >= 0 ? y + 1 : y;
            return Cross2D(e, new Vector2(cx, cy) - a) * sign >= 0;
        }

        private static bool[] Dilate(bool[] mask, int w, int h, int r)
        {
            var rows = new bool[w * h];
            for (int y = 0; y < h; y++)
            {
                int last = int.MinValue / 2;
                for (int x = 0; x < w; x++) { if (mask[y * w + x]) last = x; rows[y * w + x] = x - last <= r; }
                last = int.MaxValue / 2;
                for (int x = w - 1; x >= 0; x--) { if (mask[y * w + x]) last = x; if (last - x <= r) rows[y * w + x] = true; }
            }
            var result = new bool[w * h];
            for (int x = 0; x < w; x++)
            {
                int last = int.MinValue / 2;
                for (int y = 0; y < h; y++) { if (rows[y * w + x]) last = y; result[y * w + x] = y - last <= r; }
                last = int.MaxValue / 2;
                for (int y = h - 1; y >= 0; y--) { if (rows[y * w + x]) last = y; if (last - y <= r) result[y * w + x] = true; }
            }
            return result;
        }

        // Tetris-style: the skyline is, per column, the first free row; each chart drops straight
        // down at whichever column and rotation leaves its top lowest (then fills its own gap
        // best, then sits furthest left).
        private static bool TryPack(List<Chart> charts, float k, int gridW, int gridH, float cell, int padCells,
            CancellationToken cancellation)
        {
            var sky = new int[gridW];
            foreach (var c in charts)
            {
                cancellation.ThrowIfCancellationRequested();
                int bestTop = int.MaxValue, bestWaste = int.MaxValue, bestX = 0, bestY = 0, bestRot = 0;
                Footprint? best = null;
                for (int rot = 0; rot < 4; rot++)
                {
                    var fp = MakeFootprint(c, k, rot, cell, padCells);
                    if (fp.W > gridW || fp.H > gridH) continue;
                    for (int x = 0; x + fp.W <= gridW; x++)
                    {
                        int y = 0;
                        bool hopeless = false;
                        for (int i = 0; i < fp.W; i++)
                        {
                            if (fp.Bottom[i] == int.MaxValue) continue;
                            y = Math.Max(y, sky[x + i] - fp.Bottom[i]);
                            if (y + fp.H > bestTop) { hopeless = true; break; }
                        }
                        if (hopeless || y + fp.H > gridH) continue;

                        int top = y + fp.H, waste = 0;
                        for (int i = 0; i < fp.W; i++)
                            if (fp.Bottom[i] != int.MaxValue) waste += y + fp.Bottom[i] - sky[x + i];
                        if (top < bestTop || (top == bestTop && waste < bestWaste))
                        {
                            bestTop = top; bestWaste = waste; bestX = x; bestY = y; bestRot = rot; best = fp;
                        }
                    }
                }
                if (best == null) return false;

                for (int i = 0; i < best.W; i++)
                    if (best.Top[i] >= 0) sky[bestX + i] = Math.Max(sky[bestX + i], bestY + best.Top[i] + 1);
                c.X = bestX; c.Y = bestY; c.Rotation = bestRot; c.RotatedMin = best.Min;
            }
            return true;
        }

        // --- group pipeline ----------------------------------------------------------------------

        // The packer's grid is at most this many cells across; a 4K atlas packs in 4-texel cells.
        private const int PackGrid = 1024;

        private static void ProcessGroup(ModelRoot model, Group group, Options options, Report report,
            IProgress<Progress>? progress, ref int done, int total, CancellationToken cancellation)
        {
            string label = GroupLabel(group);
            var result = new GroupResult { Label = label };
            result.ImageIndices.AddRange(group.Images);
            report.Groups.Add(result);

            // Source sizes, and what each image is rebuilt at.
            var srcSize = new Dictionary<int, (int W, int H)>();
            var outSize = new Dictionary<int, (int W, int H)>();
            foreach (int img in group.Images)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream(TextureAtlasUtil.SnapshotContent(model, img));
                    using var bmp = System.Drawing.Image.FromStream(ms, false, false);
                    srcSize[img] = (bmp.Width, bmp.Height);
                }
                catch
                {
                    result.SkippedReason = $"image {img} could not be decoded";
                    done += 2 + group.Images.Count;
                    return;
                }
                var (sw, sh) = srcSize[img];
                if (options.OutputSize > 0)
                {
                    float scale = options.OutputSize / (float)Math.Max(sw, sh);
                    outSize[img] = (Math.Max(1, (int)MathF.Round(sw * scale)), Math.Max(1, (int)MathF.Round(sh * scale)));
                }
                else outSize[img] = (sw, sh);
            }
            int srcW = srcSize.Values.Max(s => s.W), srcH = srcSize.Values.Max(s => s.H);
            int refW = outSize.Values.Max(s => s.W), refH = outSize.Values.Max(s => s.H);
            var texScale = new Vector2(srcW, srcH);

            // 1. Start from the source's own islands - each already flattens - and merge across
            //    seams for as long as the unions still flatten.
            progress?.Report(new Progress(done, total, $"{label}: merging charts"));
            var prims = group.Prims.Select(e => LoadPrim(model, e, srcW, srcH)).ToList();
            var charts = new List<Chart>();
            foreach (var p in prims)
            {
                var (seams, islands) = SourceIslands(p);
                result.SeamEdgesBefore += seams;
                result.IslandsBefore += islands.Count;
                result.Triangles += p.TriCount;
                result.VerticesBefore += p.Pos.Length;
                foreach (var island in islands) charts.Add(SourceChart(p, island, texScale));
            }
            result.Primitives = prims.Count;

            int doneAtStart = done;
            charts = MergeCharts(prims, charts, options,
                count => progress?.Report(new Progress(doneAtStart, total, $"{label}: merging charts ({count:N0} left)")),
                cancellation);
            done++;

            // 2. Scale each chart to its texel density (in source texels), orient, pack.
            progress?.Report(new Progress(done, total, $"{label}: packing {charts.Count:N0} charts"));
            float totalArea3D = 0, totalSourceTex = 0;
            foreach (var c in charts)
            {
                foreach (int t in c.Tris)
                {
                    c.Area3D += c.Prim.Area[t];
                    c.SourceTexArea += c.Prim.SourceTexArea[t];
                }
                totalArea3D += c.Area3D;
                totalSourceTex += c.SourceTexArea;
            }

            // A chart whose source UVs were degenerate, or wildly off the model's typical
            // density, is pulled toward the median.
            float globalDensity = totalArea3D > 0 ? MathF.Sqrt(totalSourceTex / totalArea3D) : 1f;
            float medianDensity = WeightedMedian(charts.Where(c => c.Area3D > 0 && c.SourceTexArea > 0)
                .Select(c => (MathF.Sqrt(c.SourceTexArea / c.Area3D), c.Area3D)).ToList(), globalDensity);
            foreach (var c in charts)
            {
                float density = globalDensity;
                if (options.PreserveDensity)
                {
                    density = c.Area3D > 0 && c.SourceTexArea > 0 ? MathF.Sqrt(c.SourceTexArea / c.Area3D) : medianDensity;
                    density = Math.Clamp(density, medianDensity / 4f, medianDensity * 4f);
                }
                if (!(density > 0) || !float.IsFinite(density)) density = 1f;

                // Whatever units Pts came in (source texels for an untouched island, surface units
                // for a fresh unwrap), make its area the surface's, then density gives texels.
                double area2 = PtsArea(c);
                float scale = (area2 > 0 ? MathF.Sqrt(c.Area3D / (float)area2) : 1f) * density;
                for (int i = 0; i < c.Pts.Length; i++) c.Pts[i] *= scale;
                c.Area2D = (float)(area2 * scale * scale);
                Orient(c);
            }

            // Biggest first - the small ones fill in round them.
            charts = charts.OrderByDescending(c => Math.Max(c.Size.X, c.Size.Y)).ThenByDescending(c => c.Area2D).ToList();
            int pad = Math.Max(0, options.PaddingTexels);
            float cell = MathF.Max(1f, MathF.Ceiling(Math.Max(refW, refH) / (float)PackGrid));
            int gridW = (int)(refW / cell), gridH = (int)(refH / cell);
            int padCells = (int)MathF.Ceiling(pad / 2f / cell);

            double chartArea = charts.Sum(c => (double)c.Area2D);
            float lo = 0, hi = (float)Math.Sqrt(refW * (double)refH / Math.Max(chartArea, 1e-12));
            if (TryPack(charts, hi, gridW, gridH, cell, padCells, cancellation)) lo = hi;
            else
            {
                for (int it = 0; it < 14; it++)
                {
                    float mid = (lo + hi) / 2;
                    if (TryPack(charts, mid, gridW, gridH, cell, padCells, cancellation)) lo = mid; else hi = mid;
                }
                if (lo <= 0 || !TryPack(charts, lo, gridW, gridH, cell, padCells, cancellation))
                {
                    result.SkippedReason = $"{charts.Count:N0} charts don't fit with {pad} texels of padding - lower the padding";
                    done += 1 + group.Images.Count;
                    return;
                }
            }
            float k = lo;
            done++;

            // Final UVs, normalized to the atlas.
            double coveredTexels = 0;
            foreach (var c in charts)
            {
                c.FinalUv = new Vector2[c.Pts.Length];
                var offset = new Vector2((c.X + padCells) * cell, (c.Y + padCells) * cell) - c.RotatedMin;
                for (int i = 0; i < c.Pts.Length; i++)
                {
                    var q = Rotate(c.Pts[i] * k, c.Rotation) + offset;
                    c.FinalUv[i] = new Vector2(q.X / refW, q.Y / refH);
                }
                coveredTexels += c.Area2D * k * k;
            }

            result.ChartsAfter = charts.Count;
            result.Fill = (float)(coveredTexels / ((double)refW * refH));
            result.DetailScale = k;

            // Vertex split + index rewrite per primitive.
            var chartOf = prims.ToDictionary(p => p, p => new int[p.TriCount]);
            for (int i = 0; i < charts.Count; i++)
                foreach (int t in charts[i].Tris) chartOf[charts[i].Prim][t] = i;

            var rewrites = new Dictionary<PrimData, PrimitiveRewrite>();
            foreach (var p in prims)
            {
                var rw = BuildRewrite(p, charts.Where(c => c.Prim == p).ToList());
                rewrites[p] = rw;
                report.Rewrites.Add(rw);
                result.VerticesAfter += rw.NewToOld.Length;

                var cof = chartOf[p];
                for (int t = 0; t < p.TriCount; t++)
                    for (int e = 0; e < 3; e++)
                    {
                        int o = p.Adj[t * 3 + e];
                        if (o > t && cof[o] != cof[t]) result.SeamEdgesAfter++;
                    }
            }

            // 3. Rebake every image in the group into the new layout.
            foreach (int img in group.Images)
            {
                cancellation.ThrowIfCancellationRequested();
                progress?.Report(new Progress(done, total, $"{label}: baking image {img}"));
                var readers = prims.Where(p => p.Entry.Images.Contains(img)).ToList();
                report.NewImages[img] = Bake(model, img, outSize[img], readers, rewrites,
                    group.NormalImages.Contains(img), pad, cancellation);
                done++;
            }
        }

        private static double PtsArea(Chart c)
        {
            double a = 0;
            for (int t = 0; t < c.LocalTris.Length / 3; t++)
                a += Math.Abs(Cross2D(c.Pts[c.LocalTris[t * 3 + 1]] - c.Pts[c.LocalTris[t * 3]],
                                      c.Pts[c.LocalTris[t * 3 + 2]] - c.Pts[c.LocalTris[t * 3]])) * 0.5;
            return a;
        }

        private static float WeightedMedian(List<(float Value, float Weight)> items, float fallback)
        {
            if (items.Count == 0) return fallback;
            items.Sort((a, b) => a.Value.CompareTo(b.Value));
            float half = items.Sum(i => i.Weight) / 2, acc = 0;
            foreach (var (value, weight) in items)
            {
                acc += weight;
                if (acc >= half) return value;
            }
            return items[^1].Value;
        }

        // One new vertex per (source vertex, chart it appears in): a vertex inside a chart keeps
        // a single record, one on a chart boundary gets one per side.
        private static PrimitiveRewrite BuildRewrite(PrimData p, List<Chart> charts)
        {
            var slot = new Dictionary<long, int>();
            var newToOld = new List<int>();
            var uvs = new List<Vector2>();
            var indices = new int[p.Tris.Length];

            for (int ci = 0; ci < charts.Count; ci++)
            {
                var chart = charts[ci];
                for (int i = 0; i < chart.Tris.Length; i++)
                {
                    int t = chart.Tris[i];
                    for (int k = 0; k < 3; k++)
                    {
                        int ov = p.Tris[t * 3 + k];
                        long key = ((long)ov << 32) | (uint)ci;
                        if (!slot.TryGetValue(key, out int nv))
                        {
                            slot[key] = nv = newToOld.Count;
                            newToOld.Add(ov);
                            uvs.Add(chart.FinalUv[chart.LocalTris[i * 3 + k]]);
                        }
                        indices[t * 3 + k] = nv;
                    }
                }
            }

            var rw = new PrimitiveRewrite
            {
                MeshIndex = p.Entry.MeshIndex, PrimIndex = p.Entry.PrimIndex, UvSet = p.Entry.UvSet,
                NewToOld = newToOld.ToArray(), Uvs = uvs.ToArray(), Indices = indices,
            };
            if (p.HasTangents) rw.Tangents = ComputeTangents(p, rw);
            return rw;
        }

        // Per-vertex tangents for the new UVs: face tangents accumulated by area, made orthogonal
        // to the vertex normal, with w giving the handedness glTF's bitangent = cross(N, T) * w needs.
        private static Vector4[] ComputeTangents(PrimData p, PrimitiveRewrite rw)
        {
            int n = rw.NewToOld.Length;
            var tan = new Vector3[n];
            var bit = new Vector3[n];
            var faceSum = new Vector3[n];
            for (int t = 0; t < rw.Indices.Length / 3; t++)
            {
                int a = rw.Indices[t * 3], b = rw.Indices[t * 3 + 1], c = rw.Indices[t * 3 + 2];
                var p0 = p.Pos[rw.NewToOld[a]]; var p1 = p.Pos[rw.NewToOld[b]]; var p2 = p.Pos[rw.NewToOld[c]];
                if (!UvFrame(p0, p1, p2, rw.Uvs[a], rw.Uvs[b], rw.Uvs[c], out var dpdu, out var up)) continue;
                var cross = Vector3.Cross(p1 - p0, p2 - p0);
                float w = cross.Length();
                foreach (int v in new[] { a, b, c })
                {
                    tan[v] += Vector3.Normalize(dpdu) * w;
                    bit[v] += Vector3.Normalize(up) * w;
                    faceSum[v] += cross;
                }
            }

            var result = new Vector4[n];
            for (int v = 0; v < n; v++)
            {
                var normal = p.Normals != null ? p.Normals[rw.NewToOld[v]] : faceSum[v];
                normal = SafeNormalize(normal, Vector3.UnitY);
                var t = tan[v] - normal * Vector3.Dot(normal, tan[v]);
                if (t.LengthSquared() < 1e-20f)
                    t = Vector3.Cross(MathF.Abs(normal.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX, normal);
                t = Vector3.Normalize(t);
                float handed = Vector3.Dot(Vector3.Cross(normal, t), bit[v]) < 0 ? -1f : 1f;
                result[v] = new Vector4(t, handed);
            }
            return result;
        }

        // dP/du, and the surface direction a normal map's +Y points along: "up" in the image, which
        // is -v since glTF puts v = 0 at the top.
        private static bool UvFrame(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 t0, Vector2 t1, Vector2 t2,
            out Vector3 dpdu, out Vector3 up)
        {
            var e1 = p1 - p0; var e2 = p2 - p0;
            var d1 = t1 - t0; var d2 = t2 - t0;
            float det = d1.X * d2.Y - d2.X * d1.Y;
            dpdu = up = Vector3.Zero;
            if (MathF.Abs(det) < 1e-14f) return false;
            float r = 1f / det;
            dpdu = (e1 * d2.Y - e2 * d1.Y) * r;
            up = -(e2 * d1.X - e1 * d2.X) * r;
            return dpdu.LengthSquared() > 1e-30f && up.LengthSquared() > 1e-30f;
        }

        // The orthonormal tangent frame a renderer builds for one triangle: T along +u in the
        // surface plane, B = cross(N, T) flipped to whichever side "up" in the image is on.
        private static bool TangentFrame(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 t0, Vector2 t1, Vector2 t2,
            Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            tangent = bitangent = Vector3.Zero;
            if (normal == Vector3.Zero || !UvFrame(p0, p1, p2, t0, t1, t2, out var dpdu, out var up)) return false;
            var t = dpdu - normal * Vector3.Dot(normal, dpdu);
            if (t.LengthSquared() < 1e-20f) return false;
            tangent = Vector3.Normalize(t);
            bitangent = Vector3.Cross(normal, tangent);
            if (Vector3.Dot(bitangent, up) < 0) bitangent = -bitangent;
            return true;
        }

        // --- rebake ------------------------------------------------------------------------------

        private readonly struct BakeTri
        {
            public readonly Vector2 N0, N1, N2;      // new UVs, in output texels
            public readonly Vector2 O0, O1, O2;      // source UVs, normalized
            public readonly float M00, M01, M10, M11; // normal-map XY: source frame -> new frame

            public BakeTri(Vector2 n0, Vector2 n1, Vector2 n2, Vector2 o0, Vector2 o1, Vector2 o2,
                float m00, float m01, float m10, float m11)
            {
                N0 = n0; N1 = n1; N2 = n2; O0 = o0; O1 = o1; O2 = o2;
                M00 = m00; M01 = m01; M10 = m10; M11 = m11;
            }
        }

        private const int BandRows = 32;

        // Texels past a chart's edge that still get a real sample (from the nearest point on the
        // edge) rather than padding - bilinear filtering at the edge reads these.
        private const float EdgeReach = 1.5f;

        private static byte[] Bake(ModelRoot model, int imageIndex, (int W, int H) size, List<PrimData> readers,
            Dictionary<PrimData, PrimitiveRewrite> rewrites, bool isNormal, int padding, CancellationToken cancellation)
        {
            using var srcBitmap = TextureAtlasUtil.LoadBitmap(TextureAtlasUtil.SnapshotContent(model, imageIndex));
            int sw = srcBitmap.Width, sh = srcBitmap.Height;
            var source = TextureAtlasUtil.ReadPixels(srcBitmap);

            var tris = new List<BakeTri>();
            var sourceCovered = new bool[sw * sh];
            var srcScale = new Vector2(sw, sh);
            var outScale = new Vector2(size.W, size.H);
            foreach (var p in readers)
            {
                var rw = rewrites[p];
                for (int t = 0; t < p.TriCount; t++)
                {
                    int a = rw.Indices[t * 3], b = rw.Indices[t * 3 + 1], c = rw.Indices[t * 3 + 2];
                    var o0 = p.Uv[p.Tris[t * 3]]; var o1 = p.Uv[p.Tris[t * 3 + 1]]; var o2 = p.Uv[p.Tris[t * 3 + 2]];
                    var n0 = rw.Uvs[a]; var n1 = rw.Uvs[b]; var n2 = rw.Uvs[c];

                    float m00 = 1, m01 = 0, m10 = 0, m11 = 1;
                    if (isNormal)
                    {
                        var p0 = p.Pos[p.Tris[t * 3]]; var p1 = p.Pos[p.Tris[t * 3 + 1]]; var p2 = p.Pos[p.Tris[t * 3 + 2]];
                        var fn = p.FaceN[t];
                        if (TangentFrame(p0, p1, p2, o0, o1, o2, fn, out var to, out var bo)
                            && TangentFrame(p0, p1, p2, n0, n1, n2, fn, out var tn, out var bn))
                        {
                            m00 = Vector3.Dot(to, tn); m01 = Vector3.Dot(bo, tn);
                            m10 = Vector3.Dot(to, bn); m11 = Vector3.Dot(bo, bn);
                        }
                    }

                    tris.Add(new BakeTri(n0 * outScale, n1 * outScale, n2 * outScale, o0, o1, o2, m00, m01, m10, m11));
                    RasterizeCoverage(o0 * srcScale, o1 * srcScale, o2 * srcScale, sw, sh, sourceCovered);
                }
            }

            // Pad the source on its own islands first, so a bilinear sample at the very edge of a
            // source island reads more of that island instead of whatever the old gutter held.
            UvIslandPadding.DilateColor(sw, sh, source, sourceCovered, 4);

            int width = size.W, height = size.H;
            var output = new byte[width * height * 4];
            var dist = new float[width * height];
            Array.Fill(dist, float.MaxValue);

            int bands = (height + BandRows - 1) / BandRows;
            var perBand = new List<int>[bands];
            for (int i = 0; i < bands; i++) perBand[i] = new List<int>();
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                float minY = MathF.Min(t.N0.Y, MathF.Min(t.N1.Y, t.N2.Y)) - EdgeReach;
                float maxY = MathF.Max(t.N0.Y, MathF.Max(t.N1.Y, t.N2.Y)) + EdgeReach;
                int b0 = Math.Clamp((int)MathF.Floor(minY) / BandRows, 0, bands - 1);
                int b1 = Math.Clamp((int)MathF.Ceiling(maxY) / BandRows, 0, bands - 1);
                for (int b = b0; b <= b1; b++) perBand[b].Add(i);
            }

            Parallel.For(0, bands, new ParallelOptions { CancellationToken = cancellation }, band =>
            {
                int rowStart = band * BandRows;
                int rowEnd = Math.Min(height, rowStart + BandRows) - 1;
                foreach (int i in perBand[band])
                    BakeTriangle(tris[i], width, rowStart, rowEnd, source, sw, sh, output, dist, isNormal);
            });

            var covered = new bool[width * height];
            long count = 0; double sb = 0, sg = 0, sr = 0, sa = 0;
            for (int i = 0; i < covered.Length; i++)
            {
                if (dist[i] > EdgeReach) continue;
                covered[i] = true;
                count++;
                sb += output[i * 4]; sg += output[i * 4 + 1]; sr += output[i * 4 + 2]; sa += output[i * 4 + 3];
            }

            var reached = (bool[])covered.Clone();
            UvIslandPadding.DilateColor(width, height, output, covered, padding, reached);

            // Whatever the padding didn't reach takes the image's average, so the smallest mips
            // average towards the model's own colour rather than black.
            if (count > 0)
            {
                byte fb = (byte)(sb / count), fg = (byte)(sg / count), fr = (byte)(sr / count), fa = (byte)(sa / count);
                if (isNormal) { fb = 255; fg = 128; fr = 128; }
                for (int i = 0; i < reached.Length; i++)
                {
                    if (reached[i]) continue;
                    output[i * 4] = fb; output[i * 4 + 1] = fg; output[i * 4 + 2] = fr; output[i * 4 + 3] = fa;
                }
            }

            using var bmp = TextureAtlasUtil.WritePixels(output, width, height);
            return TextureAtlasUtil.EncodePng(bmp);
        }

        private static void BakeTriangle(in BakeTri t, int width, int rowStart, int rowEnd,
            byte[] source, int sw, int sh, byte[] output, float[] dist, bool isNormal)
        {
            float minX = MathF.Min(t.N0.X, MathF.Min(t.N1.X, t.N2.X)) - EdgeReach;
            float maxX = MathF.Max(t.N0.X, MathF.Max(t.N1.X, t.N2.X)) + EdgeReach;
            float minY = MathF.Min(t.N0.Y, MathF.Min(t.N1.Y, t.N2.Y)) - EdgeReach;
            float maxY = MathF.Max(t.N0.Y, MathF.Max(t.N1.Y, t.N2.Y)) + EdgeReach;
            int x0 = Math.Max(0, (int)MathF.Floor(minX));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(maxX));
            int y0 = Math.Max(rowStart, (int)MathF.Floor(minY));
            int y1 = Math.Min(rowEnd, (int)MathF.Ceiling(maxY));
            if (x1 < x0 || y1 < y0) return;

            float area = Cross2D(t.N1 - t.N0, t.N2 - t.N0);
            if (MathF.Abs(area) < 1e-9f) return;

            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    var s = new Vector2(px + 0.5f, py + 0.5f);
                    float w0 = Cross2D(t.N2 - t.N1, s - t.N1) / area;
                    float w1 = Cross2D(t.N0 - t.N2, s - t.N2) / area;
                    float w2 = 1f - w0 - w1;

                    float d = 0;
                    if (w0 < 0 || w1 < 0 || w2 < 0)
                    {
                        var closest = ClosestOnTriangle(s, t.N0, t.N1, t.N2, out w0, out w1, out w2);
                        d = Vector2.Distance(s, closest);
                        if (d > EdgeReach) continue;
                    }

                    int idx = py * width + px;
                    if (d >= dist[idx]) continue;
                    dist[idx] = d;

                    var uv = t.O0 * w0 + t.O1 * w1 + t.O2 * w2;
                    TextureRebaker.SampleBilinear(source, sw, sh, uv, output, idx * 4);

                    if (isNormal)
                    {
                        // BGRA: R and G hold X and Y.
                        float nx = output[idx * 4 + 2] / 255f * 2f - 1f;
                        float ny = output[idx * 4 + 1] / 255f * 2f - 1f;
                        float rx = t.M00 * nx + t.M01 * ny;
                        float ry = t.M10 * nx + t.M11 * ny;
                        output[idx * 4 + 2] = (byte)Math.Clamp(MathF.Round((rx + 1f) * 127.5f), 0, 255);
                        output[idx * 4 + 1] = (byte)Math.Clamp(MathF.Round((ry + 1f) * 127.5f), 0, 255);
                    }
                }
            }
        }

        // Nearest point of a triangle to s (which lies outside it), with its barycentrics.
        private static Vector2 ClosestOnTriangle(Vector2 s, Vector2 a, Vector2 b, Vector2 c,
            out float wa, out float wb, out float wc)
        {
            var best = a; float bestD = float.MaxValue;
            wa = 1; wb = 0; wc = 0;
            void Edge(Vector2 p, Vector2 q, int ip, int iq, ref float oa, ref float ob, ref float oc)
            {
                var e = q - p;
                float len2 = e.LengthSquared();
                float f = len2 > 0 ? Math.Clamp(Vector2.Dot(s - p, e) / len2, 0f, 1f) : 0f;
                var point = p + e * f;
                float d = Vector2.DistanceSquared(s, point);
                if (d >= bestD) return;
                bestD = d; best = point;
                oa = ob = oc = 0;
                SetW(ip, 1 - f, ref oa, ref ob, ref oc);
                SetW(iq, f, ref oa, ref ob, ref oc);
            }
            static void SetW(int i, float v, ref float oa, ref float ob, ref float oc)
            {
                if (i == 0) oa = v; else if (i == 1) ob = v; else oc = v;
            }
            Edge(a, b, 0, 1, ref wa, ref wb, ref wc);
            Edge(b, c, 1, 2, ref wa, ref wb, ref wc);
            Edge(c, a, 2, 0, ref wa, ref wb, ref wc);
            return best;
        }

        private static void RasterizeCoverage(Vector2 a, Vector2 b, Vector2 c, int width, int height, bool[] covered)
        {
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
            int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
            int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            float area = Cross2D(b - a, c - a);
            if (MathF.Abs(area) < 1e-6f) return;
            for (int py = y0; py <= y1; py++)
                for (int px = x0; px <= x1; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    float w0 = Cross2D(c - b, p - b), w1 = Cross2D(a - c, p - c), w2 = Cross2D(b - a, p - a);
                    bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                    if (inside) covered[py * width + px] = true;
                }
        }

        // --- helpers -----------------------------------------------------------------------------

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static (Vector2 Min, Vector2 Max) Bounds(Vector2[] pts)
        {
            var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
            foreach (var q in pts) { min = Vector2.Min(min, q); max = Vector2.Max(max, q); }
            return (min, max);
        }

        // UV wireframe of every triangle reading this image, drawn over a copy of it - shows the
        // layout (and every seam) at a glance in the editor's 2D panes.
        public static System.Drawing.Bitmap DrawLayout(ModelRoot model, int imageIndex, byte[] imageBytes)
        {
            var bmp = TextureAtlasUtil.LoadBitmap(imageBytes);
            int w = bmp.Width, h = bmp.Height;
            using var g = System.Drawing.Graphics.FromImage(bmp);
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(160, 0, 255, 255), Math.Max(1f, w / 1024f));

            var seen = new HashSet<(int, int, int)>();
            foreach (var mat in model.LogicalMaterials)
                foreach (var ch in mat.Channels)
                {
                    if (ch.Texture?.PrimaryImage?.LogicalIndex != imageIndex) continue;
                    string set = $"TEXCOORD_{ch.TextureCoordinate}";
                    for (int m = 0; m < model.LogicalMeshes.Count; m++)
                        for (int pi = 0; pi < model.LogicalMeshes[m].Primitives.Count; pi++)
                        {
                            var prim = model.LogicalMeshes[m].Primitives[pi];
                            if (prim.Material?.LogicalIndex != mat.LogicalIndex || prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                            if (!prim.VertexAccessors.TryGetValue(set, out var acc) || !seen.Add((m, pi, ch.TextureCoordinate))) continue;
                            var uv = acc.AsVector2Array();
                            foreach (var (a, b, c) in prim.GetTriangleIndices())
                            {
                                var pa = new System.Drawing.PointF(uv[a].X * w, uv[a].Y * h);
                                var pb = new System.Drawing.PointF(uv[b].X * w, uv[b].Y * h);
                                var pc = new System.Drawing.PointF(uv[c].X * w, uv[c].Y * h);
                                g.DrawPolygon(pen, new[] { pa, pb, pc });
                            }
                        }
                }
            return bmp;
        }
    }
}
