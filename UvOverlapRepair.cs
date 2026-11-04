using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Separates places where a model's UVs map two different parts of the body onto the same
    // texels, so painting one no longer paints the other. Generated models (Meshy and friends)
    // ship with a few of these per model - Eli's back of the neck shares texels with his collar 11cm
    // below it - always an island folded back onto itself rather than two islands stacked.
    //
    // An overlap is a texel whose centre lies inside two triangles that are not neighbours (see
    // FoldRings: neighbours that close are a fold's flap lying over the surface next to it, which
    // TextureAtlasOptimizer tolerates for the same reason). The triangles on
    // each side of an overlap are grouped into
    // patches - triangles joined across edges whose UVs agree - and in each overlapping pair the
    // smaller patch moves: its triangles get their own copy of the block of texels they read,
    // placed in empty atlas space a whole number of texels away. A pure translation by whole
    // texels copies every channel exactly (no resampling, a normal map's directions unchanged),
    // and the block carries the texels round the patch too, so the model looks the same
    // afterwards, seams included; the patch's outline becomes a new UV seam - vertices split only
    // there.
    //
    // A patch whose two sides are joined through other overlapping triangles (a fold that runs
    // into itself) is first split between them, each triangle going to whichever side it is
    // nearer across the patch. Primitives with morph targets are skipped, same as the other
    // geometry tools here. Writes every vertex accessor (vertices are split), the UV set and the
    // images that set feeds.
    public static class UvOverlapRepair
    {
        public sealed class Options
        {
            /// <summary>Texels of surroundings copied with each moved patch (and kept clear round it), in the base colour image's texels.</summary>
            public int PaddingTexels { get; set; } = 4;
        }

        public sealed class PrimitiveResult
        {
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }
            public string MeshName { get; init; } = "";
            public int Triangles { get; init; }
            public int TexelsShared { get; set; }
            public int PatchesMoved { get; set; }
            public int PatchesUnplaced { get; set; }

            /// <summary>Triangles of the moved patches and of the parts they overlapped - what the preview highlights.</summary>
            public HashSet<int> Found { get; } = new();

            public string? SkippedReason { get; set; }
            internal string UvSet = "";
            internal int[]? NewToOld;
            internal Vector2[]? Uvs;
            internal int[]? Indices;
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();
            internal Dictionary<int, byte[]> Images { get; } = new();
            public int TexelsShared => Primitives.Sum(p => p.TexelsShared);
            public int PatchesMoved => Primitives.Sum(p => p.PatchesMoved);
            public int PatchesUnplaced => Primitives.Sum(p => p.PatchesUnplaced);
            public bool HasChanges => Primitives.Any(p => p.Indices != null);
        }

        // Overlapping triangles count as a fold, and are left alone, only when they are both this
        // few rings apart on the mesh (1: sharing a corner; 2: sharing a corner with a triangle
        // that shares one) and within FoldDistance of each other. Neither test is enough alone on a
        // coarse mesh: any distance in multiples of an edge reaches 11cm (Eli's neck to his
        // collar), and two rings can too - two of Mateo's sock triangles 5cm apart were let off as
        // a fold, and paint on the stripe showed up 5cm further down it.
        private const int FoldRings = 2;

        // As a fraction of the primitive's bounding diagonal - about 2cm on a player model.
        private const float FoldDistance = 0.01f;

        // Triangles round each triangle, ring by ring through shared (welded) corners.
        private sealed class MeshNeighbourhood
        {
            private readonly int[] _idx, _weld;
            private readonly List<int>[] _byCorner;
            private readonly Dictionary<int, HashSet<int>> _cache = new();

            public MeshNeighbourhood(int[] idx, int[] weld)
            {
                _idx = idx; _weld = weld;
                int welded = weld.Length == 0 ? 0 : weld.Max() + 1;
                _byCorner = new List<int>[welded];
                for (int i = 0; i < welded; i++) _byCorner[i] = new List<int>();
                for (int i = 0; i < idx.Length; i++) _byCorner[weld[idx[i]]].Add(i / 3);
            }

            public bool Within(int a, int b, int rings)
            {
                if (!_cache.TryGetValue(a, out var set))
                {
                    set = new HashSet<int> { a };
                    var frontier = new List<int> { a };
                    for (int r = 0; r < rings; r++)
                    {
                        var next = new List<int>();
                        foreach (int t in frontier)
                            for (int k = 0; k < 3; k++)
                                foreach (int n in _byCorner[_weld[_idx[t * 3 + k]]])
                                    if (set.Add(n)) next.Add(n);
                        frontier = next;
                    }
                    _cache[a] = set;
                }
                return set.Contains(b);
            }
        }

        private sealed class ImageState
        {
            public int Width, Height;
            public byte[] Pixels = Array.Empty<byte>();
            public bool[] Covered = Array.Empty<bool>();   // what every triangle reading it covers
            public bool Changed;
        }

        public static Report Analyze(ModelRoot model, Options options, CancellationToken cancellation = default)
        {
            var report = new Report();
            var images = new Dictionary<int, ImageState>();
            var occupied = new Dictionary<int, bool[]>();   // per reference image: where nothing new may go

            for (int m = 0; m < model.LogicalMeshes.Count; m++)
            {
                var mesh = model.LogicalMeshes[m];
                for (int p = 0; p < mesh.Primitives.Count; p++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var prim = mesh.Primitives[p];
                    var result = new PrimitiveResult
                    {
                        MeshIndex = m, PrimitiveIndex = p, MeshName = mesh.Name ?? $"mesh_{m}",
                        Triangles = prim.DrawPrimitiveType == PrimitiveType.TRIANGLES ? prim.GetTriangleIndices().Count() : 0,
                    };
                    report.Primitives.Add(result);
                    AnalyzePrimitive(model, prim, result, options, images, occupied);
                }
            }

            foreach (var (index, state) in images)
            {
                if (!state.Changed) continue;
                using var bmp = TextureAtlasUtil.WritePixels(state.Pixels, state.Width, state.Height);
                report.Images[index] = TextureAtlasUtil.EncodePng(bmp);
            }
            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var r in report.Primitives)
            {
                if (r.Indices == null) continue;
                var prim = model.LogicalMeshes[r.MeshIndex].Primitives[r.PrimitiveIndex];

                // Everything is read before anything is written - writing an accessor can
                // rearrange the buffers under the ones not yet read (see MeshLayoutOptimizer).
                var rewrites = new List<Action>();
                foreach (var (name, accessor) in prim.VertexAccessors)
                {
                    if (name == r.UvSet) continue;
                    rewrites.Add(MeshLayoutOptimizer.BuildPermutedRewrite(prim, name, accessor, r.NewToOld!)
                        ?? throw new InvalidOperationException($"Attribute {name} has an encoding this tool can't rewrite."));
                }
                foreach (var rewrite in rewrites) rewrite();
                prim.WithVertexAccessor(r.UvSet, r.Uvs!.ToList());
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, r.Indices);
            }
            if (report.HasChanges) model.MergeBuffers();
            foreach (var (index, png) in report.Images)
                TextureAtlasUtil.Apply(model, index, png);
        }

        // --- per primitive ------------------------------------------------------------------------

        private static void AnalyzePrimitive(ModelRoot model, MeshPrimitive prim, PrimitiveResult result, Options options,
            Dictionary<int, ImageState> images, Dictionary<int, bool[]> occupied)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) { result.SkippedReason = "not a triangle list"; return; }
            if (prim.MorphTargetsCount > 0) { result.SkippedReason = "has morph targets"; return; }
            var mat = prim.Material;
            var textured = mat?.Channels.Where(ch => ch.Texture?.PrimaryImage != null).ToList();
            if (textured == null || textured.Count == 0) { result.SkippedReason = "no texture"; return; }
            var baseCh = textured.Any(ch => ch.Key == "BaseColor") ? textured.First(ch => ch.Key == "BaseColor") : textured[0];
            int set = baseCh.TextureCoordinate;
            result.UvSet = $"TEXCOORD_{set}";
            var channels = textured.Where(ch => ch.TextureCoordinate == set).ToList();
            if (channels.Any(ch => ch.TextureTransform != null)) { result.SkippedReason = "uses a texture transform"; return; }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc) || !prim.VertexAccessors.TryGetValue(result.UvSet, out var uvAcc))
            { result.SkippedReason = $"no POSITION or {result.UvSet}"; return; }

            int refImage = baseCh.Texture!.PrimaryImage!.LogicalIndex;
            var imageIndices = channels.Select(ch => ch.Texture!.PrimaryImage!.LogicalIndex).Distinct().ToList();
            foreach (int img in imageIndices) LoadImage(model, img, images);
            var reference = images[refImage];
            int W = reference.Width, H = reference.Height;

            var pos = posAcc.AsVector3Array().ToArray();
            var uvs = uvAcc.AsVector2Array().ToArray();
            var idx = prim.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
            int triCount = idx.Length / 3;
            var weld = Weld(pos);

            // Texel-centre owners at the reference resolution, and the overlapping pairs found.
            var owner = new int[W * H];
            Array.Fill(owner, -1);
            var near = new MeshNeighbourhood(idx, weld);
            var centroid = new Vector3[triCount];
            for (int t = 0; t < triCount; t++)
                centroid[t] = (pos[idx[t * 3]] + pos[idx[t * 3 + 1]] + pos[idx[t * 3 + 2]]) / 3f;
            float foldDistance = (pos.Aggregate(Vector3.Max) - pos.Aggregate(Vector3.Min)).Length() * FoldDistance;
            var pairs = new Dictionary<(int, int), int>();
            var scale = new Vector2(W, H);
            for (int t = 0; t < triCount; t++)
            {
                ForEachTexel(uvs[idx[t * 3]] * scale, uvs[idx[t * 3 + 1]] * scale, uvs[idx[t * 3 + 2]] * scale, W, H, i =>
                {
                    int o = owner[i];
                    if (o < 0) { owner[i] = t; return; }
                    if (Vector3.Distance(centroid[t], centroid[o]) <= foldDistance && near.Within(t, o, FoldRings)) return;
                    var key = o < t ? (o, t) : (t, o);
                    pairs[key] = pairs.GetValueOrDefault(key) + 1;
                });
            }
            if (pairs.Count == 0) return;
            result.TexelsShared = pairs.Values.Sum();

            // Patches: the overlapping triangles, joined across edges whose UVs agree.
            var involved = new HashSet<int>(pairs.Keys.SelectMany(k => new[] { k.Item1, k.Item2 }));
            var adjacency = UvAdjacency(idx, weld, uvs, involved);
            var patch = Patches(involved, adjacency);
            SplitSelfOverlaps(pairs, patch, adjacency);

            var patchArea = new Dictionary<int, double>();
            foreach (int t in involved)
                patchArea[patch[t]] = patchArea.GetValueOrDefault(patch[t]) + UvArea(uvs, idx, t, scale);

            // In each overlapping pair of patches the smaller one moves, unless one of them already does.
            var conflicts = new Dictionary<(int, int), int>();
            foreach (var ((a, b), n) in pairs)
            {
                int pa = patch[a], pb = patch[b];
                if (pa == pb) continue;   // couldn't be split; left as it is
                var key = pa < pb ? (pa, pb) : (pb, pa);
                conflicts[key] = conflicts.GetValueOrDefault(key) + n;
            }
            var moving = new HashSet<int>();
            foreach (var ((pa, pb), _) in conflicts.OrderByDescending(kv => kv.Value))
            {
                if (moving.Contains(pa) || moving.Contains(pb)) continue;
                moving.Add(patchArea[pa] <= patchArea[pb] ? pa : pb);
            }
            foreach (var (a, b) in pairs.Keys) { result.Found.Add(a); result.Found.Add(b); }

            // Free space: nothing within the padding of anything already covered or placed.
            if (!occupied.TryGetValue(refImage, out var occ))
            {
                occ = Dilate(reference.Covered, W, H, options.PaddingTexels * 2);
                occupied[refImage] = occ;
            }

            var newUvs = new List<Vector2>(uvs);
            var newToOld = Enumerable.Range(0, uvs.Length).ToList();
            var newIdx = (int[])idx.Clone();
            var trisOf = moving.ToDictionary(pt => pt, pt => involved.Where(t => patch[t] == pt).ToList());
            var users = VertexUsers(idx, uvs.Length);

            foreach (int pt in moving.OrderByDescending(pt => patchArea[pt]))
            {
                var tris = trisOf[pt];
                // The block the patch's triangles reach, from their UV corners (a sliver narrower
                // than a texel covers no texel centre, but still samples the texels round it).
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (int t in tris)
                    for (int k = 0; k < 3; k++)
                    {
                        var q = uvs[idx[t * 3 + k]] * scale;
                        minX = MathF.Min(minX, q.X); maxX = MathF.Max(maxX, q.X);
                        minY = MathF.Min(minY, q.Y); maxY = MathF.Max(maxY, q.Y);
                    }
                int pad = options.PaddingTexels;
                int fx0 = Math.Max(0, (int)MathF.Floor(minX) - pad), fx1 = Math.Min(W - 1, (int)MathF.Ceiling(maxX) + pad);
                int fy0 = Math.Max(0, (int)MathF.Floor(minY) - pad), fy1 = Math.Min(H - 1, (int)MathF.Ceiling(maxY) + pad);
                if (fx1 < fx0 || fy1 < fy0) continue;
                if (!FindFree(occ, W, H, fx1 - fx0 + 1, fy1 - fy0 + 1, out int nx, out int ny)) { result.PatchesUnplaced++; continue; }
                int dx = nx - fx0, dy = ny - fy0;
                MarkRect(occ, W, H, nx - pad, ny - pad, nx + (fx1 - fx0) + pad, ny + (fy1 - fy0) + pad);

                // Every image the set feeds gets that whole block at the new place - not only the
                // texels whose centres the triangles cover. Bilinear filtering at a triangle's edge
                // reads the texels just outside it, which belong to its old neighbours, and a sliver
                // reads almost nothing but those: copied texel by texel, Mateo's sock seam slivers
                // came out as a white cut and a dark line. Copied as a block, the moved patch
                // samples exactly what it did before, edges included. (Whatever else lies in the
                // block is copied along unused; the destination is empty atlas space.)
                foreach (int img in imageIndices)
                {
                    var st = images[img];
                    float sx = st.Width / (float)W, sy = st.Height / (float)H;
                    int ddx = (int)MathF.Round(dx * sx), ddy = (int)MathF.Round(dy * sy);
                    int bx0 = (int)MathF.Floor(fx0 * sx), bx1 = Math.Min(st.Width - 1, (int)MathF.Ceiling((fx1 + 1) * sx) - 1);
                    int by0 = (int)MathF.Floor(fy0 * sy), by1 = Math.Min(st.Height - 1, (int)MathF.Ceiling((fy1 + 1) * sy) - 1);
                    for (int y = by0; y <= by1; y++)
                    {
                        int ty = y + ddy;
                        if (ty < 0 || ty >= st.Height) continue;
                        for (int x = bx0; x <= bx1; x++)
                        {
                            int tx = x + ddx;
                            if (tx < 0 || tx >= st.Width) continue;
                            System.Buffer.BlockCopy(st.Pixels, (y * st.Width + x) * 4, st.Pixels, (ty * st.Width + tx) * 4, 4);
                        }
                    }
                    st.Changed = true;
                }

                // Its triangles take the moved UVs; a corner shared with anything that stays is split.
                var offset = new Vector2(dx / (float)W, dy / (float)H);
                var trisSet = new HashSet<int>(tris);
                var remap = new Dictionary<int, int>();
                foreach (int t in tris)
                    for (int k = 0; k < 3; k++)
                    {
                        int v = idx[t * 3 + k];
                        if (!remap.TryGetValue(v, out int nv))
                        {
                            if (users[v].All(trisSet.Contains)) { nv = v; newUvs[v] = uvs[v] + offset; }
                            else { nv = newUvs.Count; newUvs.Add(uvs[v] + offset); newToOld.Add(v); }
                            remap[v] = nv;
                        }
                        newIdx[t * 3 + k] = nv;
                    }
                result.PatchesMoved++;
            }

            if (result.PatchesMoved == 0) return;
            result.NewToOld = newToOld.ToArray();
            result.Uvs = newUvs.ToArray();
            result.Indices = newIdx;
        }

        // --- patches ------------------------------------------------------------------------------

        private static int[] Weld(Vector3[] pos)
        {
            var min = pos.Aggregate(Vector3.Min); var max = pos.Aggregate(Vector3.Max);
            float cell = MathF.Max((max - min).Length() * 1e-6f, 1e-9f);
            var ids = new Dictionary<(long, long, long), int>();
            var weld = new int[pos.Length];
            for (int v = 0; v < pos.Length; v++)
            {
                var q = pos[v] / cell;
                var key = ((long)MathF.Round(q.X), (long)MathF.Round(q.Y), (long)MathF.Round(q.Z));
                if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
                weld[v] = id;
            }
            return weld;
        }

        // Neighbours among `set`: triangles sharing an edge (by welded position) whose UVs agree on it.
        private static Dictionary<int, List<int>> UvAdjacency(int[] idx, int[] weld, Vector2[] uvs, HashSet<int> set)
        {
            var byEdge = new Dictionary<(int, int), List<(int Tri, Vector2 A, Vector2 B)>>();
            foreach (int t in set)
                for (int k = 0; k < 3; k++)
                {
                    int va = idx[t * 3 + k], vb = idx[t * 3 + (k + 1) % 3];
                    int wa = weld[va], wb = weld[vb];
                    var key = wa < wb ? (wa, wb) : (wb, wa);
                    var (ua, ub) = wa < wb ? (uvs[va], uvs[vb]) : (uvs[vb], uvs[va]);
                    if (!byEdge.TryGetValue(key, out var list)) byEdge[key] = list = new();
                    list.Add((t, ua, ub));
                }
            var adj = set.ToDictionary(t => t, _ => new List<int>());
            foreach (var list in byEdge.Values)
                for (int i = 0; i < list.Count; i++)
                    for (int j = i + 1; j < list.Count; j++)
                        if (Vector2.DistanceSquared(list[i].A, list[j].A) < 1e-12f && Vector2.DistanceSquared(list[i].B, list[j].B) < 1e-12f)
                        {
                            adj[list[i].Tri].Add(list[j].Tri);
                            adj[list[j].Tri].Add(list[i].Tri);
                        }
            return adj;
        }

        private static Dictionary<int, int> Patches(HashSet<int> set, Dictionary<int, List<int>> adj)
        {
            var patch = new Dictionary<int, int>();
            int next = 0;
            foreach (int seed in set)
            {
                if (patch.ContainsKey(seed)) continue;
                var queue = new Queue<int>();
                queue.Enqueue(seed); patch[seed] = next;
                while (queue.Count > 0)
                    foreach (int n in adj[queue.Dequeue()])
                        if (!patch.ContainsKey(n)) { patch[n] = next; queue.Enqueue(n); }
                next++;
            }
            return patch;
        }

        // A pair inside one patch: split that patch between its two triangles, each triangle
        // joining whichever is nearer across the patch (breadth-first from both at once). Repeated
        // a few times, for patches holding more than one such pair.
        private static void SplitSelfOverlaps(Dictionary<(int, int), int> pairs, Dictionary<int, int> patch,
            Dictionary<int, List<int>> adj)
        {
            int next = patch.Values.DefaultIfEmpty(-1).Max() + 1;
            for (int round = 0; round < 8; round++)
            {
                var inside = pairs.Keys.FirstOrDefault(k => patch[k.Item1] == patch[k.Item2], (-1, -1));
                if (inside.Item1 < 0) return;
                int whole = patch[inside.Item1], other = next++;
                var side = new Dictionary<int, int> { [inside.Item1] = whole, [inside.Item2] = other };
                var queue = new Queue<int>(new[] { inside.Item1, inside.Item2 });
                while (queue.Count > 0)
                {
                    int t = queue.Dequeue();
                    foreach (int n in adj[t])
                        if (patch[n] == whole && !side.ContainsKey(n)) { side[n] = side[t]; queue.Enqueue(n); }
                }
                foreach (var (t, s) in side) patch[t] = s;
            }
        }

        private static List<int>[] VertexUsers(int[] idx, int vertexCount)
        {
            var users = new List<int>[vertexCount];
            for (int v = 0; v < vertexCount; v++) users[v] = new List<int>();
            for (int i = 0; i < idx.Length; i++) users[idx[i]].Add(i / 3);
            return users;
        }

        private static double UvArea(Vector2[] uvs, int[] idx, int t, Vector2 scale)
        {
            var a = uvs[idx[t * 3]] * scale; var b = uvs[idx[t * 3 + 1]] * scale; var c = uvs[idx[t * 3 + 2]] * scale;
            return Math.Abs(Cross2D(b - a, c - a)) * 0.5;
        }

        // --- texels -------------------------------------------------------------------------------

        private static void LoadImage(ModelRoot model, int index, Dictionary<int, ImageState> images)
        {
            if (images.ContainsKey(index)) return;
            using var bmp = TextureAtlasUtil.LoadBitmap(TextureAtlasUtil.SnapshotContent(model, index));
            var st = new ImageState { Width = bmp.Width, Height = bmp.Height, Pixels = TextureAtlasUtil.ReadPixels(bmp) };
            st.Covered = new bool[st.Width * st.Height];
            // What every primitive reading this image covers, through the set it reads it by.
            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES || prim.Material == null) continue;
                    foreach (var ch in prim.Material.Channels)
                    {
                        if (ch.Texture?.PrimaryImage?.LogicalIndex != index) continue;
                        if (!prim.VertexAccessors.TryGetValue($"TEXCOORD_{ch.TextureCoordinate}", out var acc)) continue;
                        var uv = acc.AsVector2Array();
                        var scale = new Vector2(st.Width, st.Height);
                        foreach (var (a, b, c) in prim.GetTriangleIndices())
                            ForEachTexel(uv[a] * scale, uv[b] * scale, uv[c] * scale, st.Width, st.Height, i => st.Covered[i] = true);
                    }
                }
            images[index] = st;
        }

        // Every texel whose centre lies inside the triangle - the coverage test UvIslandPadding uses.
        private static void ForEachTexel(Vector2 a, Vector2 b, Vector2 c, int width, int height, Action<int> visit)
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
                    if (inside) visit(py * width + px);
                }
        }

        private static bool[] Dilate(bool[] mask, int width, int height, int radius)
        {
            // Chessboard dilation as two separable passes of running maxima.
            var rows = new bool[mask.Length];
            for (int y = 0; y < height; y++)
            {
                int last = -radius - 1;
                for (int x = 0; x < width; x++) { if (mask[y * width + x]) last = x; rows[y * width + x] = x - last <= radius; }
                last = width + radius + 1;
                for (int x = width - 1; x >= 0; x--) { if (mask[y * width + x]) last = x; if (last - x <= radius) rows[y * width + x] = true; }
            }
            var result = new bool[mask.Length];
            for (int x = 0; x < width; x++)
            {
                int last = -radius - 1;
                for (int y = 0; y < height; y++) { if (rows[y * width + x]) last = y; result[y * width + x] = y - last <= radius; }
                last = height + radius + 1;
                for (int y = height - 1; y >= 0; y--) { if (rows[y * width + x]) last = y; if (last - y <= radius) result[y * width + x] = true; }
            }
            return result;
        }

        // The first free w x h rectangle, scanning rows top to bottom (summed-area table).
        private static bool FindFree(bool[] occ, int width, int height, int w, int h, out int x, out int y)
        {
            x = y = 0;
            if (w > width || h > height) return false;
            var sum = new int[(width + 1) * (height + 1)];
            for (int yy = 0; yy < height; yy++)
                for (int xx = 0; xx < width; xx++)
                    sum[(yy + 1) * (width + 1) + xx + 1] = (occ[yy * width + xx] ? 1 : 0)
                        + sum[yy * (width + 1) + xx + 1] + sum[(yy + 1) * (width + 1) + xx] - sum[yy * (width + 1) + xx];
            for (int yy = 0; yy + h <= height; yy++)
                for (int xx = 0; xx + w <= width; xx++)
                {
                    int s = sum[(yy + h) * (width + 1) + xx + w] - sum[yy * (width + 1) + xx + w]
                          - sum[(yy + h) * (width + 1) + xx] + sum[yy * (width + 1) + xx];
                    if (s == 0) { x = xx; y = yy; return true; }
                }
            return false;
        }

        private static void MarkRect(bool[] occ, int width, int height, int x0, int y0, int x1, int y1)
        {
            for (int y = Math.Max(0, y0); y <= Math.Min(height - 1, y1); y++)
                for (int x = Math.Max(0, x0); x <= Math.Min(width - 1, x1); x++)
                    occ[y * width + x] = true;
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    }
}
