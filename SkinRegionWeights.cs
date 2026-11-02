using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Skin-weight surgery over a painted patch of triangles, shared by the Rigid Region and Jersey
    // Numbers editors: resolving the patch (and its feather rings) across UV seams, and rebuilding
    // its weights from the unpinned skin around it.
    internal static class SkinRegionWeights
    {
        // A primitive's JOINTS_0 accessor -> its WEIGHTS_0 and both as they were before the first edit.
        internal sealed class SkinSnapshots : Dictionary<Accessor, (Accessor WeightsAcc, Vector4[] OrigJoints, Vector4[] OrigWeights)> { }

        // 1.0 at the painted core, linearly down to just above 0 at the outermost feather ring,
        // 0 (untouched) past that - so the boundary blends rather than creasing.
        internal static float FeatherFactor(int level, int maxRings)
        {
            if (level < 0 || level > maxRings) return 0f;
            if (level == 0) return 1f;
            return 1f - (float)level / (maxRings + 1);
        }

        // Blends a vertex's CURRENT skin weights back toward the originals it had before any
        // Apply touched it, by factor t (1 = fully original, 0 = leave the pinning as it is).
        // Joint INDICES can't be interpolated, so the two sets are merged in weight space
        // instead: each side contributes its share, entries naming the same joint are summed,
        // and the four heaviest survive - the same drop-the-smallest-and-renormalize rule
        // ComputeRigidBlend uses to fit back into glTF's four slots.
        internal static (Vector4 Joints, Vector4 Weights) BlendTowardOriginal(
            Vector4 curJoints, Vector4 curWeights, Vector4 origJoints, Vector4 origWeights, float t)
        {
            var merged = new Dictionary<int, float>();
            void Add(Vector4 j, Vector4 w, float scale)
            {
                void One(float joint, float weight)
                {
                    if (weight <= 0f) return;
                    int idx = (int)joint;
                    merged[idx] = merged.TryGetValue(idx, out var existing) ? existing + weight * scale : weight * scale;
                }
                One(j.X, w.X); One(j.Y, w.Y); One(j.Z, w.Z); One(j.W, w.W);
            }
            Add(origJoints, origWeights, t);
            Add(curJoints, curWeights, 1f - t);

            var top = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
            float sum = top.Sum(kv => kv.Value);
            if (top.Count == 0 || sum < 1e-6f) return (origJoints, origWeights);

            var j2 = new float[4];
            var w2 = new float[4];
            for (int i = 0; i < top.Count; i++)
            {
                j2[i] = top[i].Key;
                w2[i] = top[i].Value / sum;
            }
            return (new Vector4(j2[0], j2[1], j2[2], j2[3]), new Vector4(w2[0], w2[1], w2[2], w2[3]));
        }

        // A jersey-number-style paint almost always sits on its own UV island, which means the
        // mesh is topologically SPLIT right at the edge of the painted patch - duplicate,
        // position-coincident vertices so the texture atlas can give the number its own space.
        // Feathering by raw vertex-INDEX adjacency alone stops dead at that seam (the far side is
        // a different vertex index even though it's the same point in space), which pins one side
        // of the seam while its duplicate stays completely untouched - exactly the mismatch that
        // opens a visible hole as the two coincident-but-differently-weighted copies diverge
        // under animation. Unioning every position-coincident vertex (across every primitive of
        // this mesh, since a UV split commonly also splits a primitive in two) into one "logical
        // vertex" before running the ring BFS is what lets the feather actually cross the seam -
        // both copies of a seam vertex always land in the same ring, get the same blend factor,
        // and so stay coincident as the bone moves instead of tearing apart.
        internal static PaintedRegion? BuildPaintedRegion(
            SharpGLTF.Schema2.Mesh mesh, int meshIdx,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            int maxRings)
        {
            var prims = new List<(int PrimIndex, Accessor Joints, Accessor Weights,
                IList<Vector3> Positions, List<(int A, int B, int C)> Triangles)>();
            for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
            {
                var prim = mesh.Primitives[primIdx];
                if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("POSITION", out var pAcc)) continue;
                prims.Add((primIdx, jAcc, wAcc, pAcc.AsVector3Array(), prim.GetTriangleIndices().ToList()));
            }
            if (prims.Count == 0) return null;

            var offsets = new int[prims.Count];
            int total = 0;
            for (int i = 0; i < prims.Count; i++) { offsets[i] = total; total += prims[i].Positions.Count; }
            if (total == 0) return null;

            var dsu = new DisjointSet(total);

            // DSU is used ONLY to identify seam duplicates (below) - NOT ordinary triangle
            // topology. A whole connected mesh's vertices are transitively joined by shared
            // triangle edges, so unioning on every triangle edge here would collapse the entire
            // mesh into one component and make every vertex look like part of the painted core.
            // Real triangle-edge adjacency is tracked separately in rootAdjacency below, which is
            // what the ring BFS actually walks.

            // Seam bridge: any vertex (in any primitive of this mesh - they all share the same
            // node, hence the same local coordinate space) sitting at the same bind-pose position
            // as another is the same physical point, regardless of index or which primitive it's
            // in. 4 decimal places (~0.1mm at meter scale) is tight enough to never merge
            // genuinely distinct nearby vertices while still catching true duplicates, which are
            // normally bit-for-bit identical anyway.
            var byPosition = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < prims.Count; i++)
            {
                var positions = prims[i].Positions;
                for (int v = 0; v < positions.Count; v++)
                {
                    var p = positions[v];
                    var key = ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
                    int gid = offsets[i] + v;
                    if (byPosition.TryGetValue(key, out var existing)) dsu.Union(existing, gid);
                    else byPosition[key] = gid;
                }
            }

            // Adjacency between logical vertices (DSU roots) - built from the same triangles as
            // above, but keyed by root rather than raw index, so two triangles on opposite sides
            // of a seam that was just bridged become directly linked in this graph even though
            // they never shared a raw vertex index.
            var rootAdjacency = new Dictionary<int, HashSet<int>>();
            void Link(int ra, int rb)
            {
                if (ra == rb) return;
                if (!rootAdjacency.TryGetValue(ra, out var set)) rootAdjacency[ra] = set = new HashSet<int>();
                set.Add(rb);
                if (!rootAdjacency.TryGetValue(rb, out var set2)) rootAdjacency[rb] = set2 = new HashSet<int>();
                set2.Add(ra);
            }
            for (int i = 0; i < prims.Count; i++)
                foreach (var (a, b, c) in prims[i].Triangles)
                {
                    int ra = dsu.Find(offsets[i] + a), rb = dsu.Find(offsets[i] + b), rc = dsu.Find(offsets[i] + c);
                    Link(ra, rb); Link(rb, rc); Link(ra, rc);
                }

            // Seed the BFS at every painted (core) vertex's logical root.
            var level = new Dictionary<int, int>();
            var frontier = new List<int>();
            for (int i = 0; i < prims.Count; i++)
            {
                if (!selection.TryGetValue((meshIdx, prims[i].PrimIndex), out var triSet)) continue;
                var triangles = prims[i].Triangles;
                foreach (var t in triSet)
                {
                    if (t < 0 || t >= triangles.Count) continue;
                    var (a, b, c) = triangles[t];
                    foreach (var v in new[] { a, b, c })
                    {
                        int root = dsu.Find(offsets[i] + v);
                        if (level.ContainsKey(root)) continue;
                        level[root] = 0;
                        frontier.Add(root);
                    }
                }
            }

            for (int ring = 1; ring <= maxRings && frontier.Count > 0; ring++)
            {
                var next = new List<int>();
                foreach (var root in frontier)
                {
                    if (!rootAdjacency.TryGetValue(root, out var neighbors)) continue;
                    foreach (var n in neighbors)
                    {
                        if (level.ContainsKey(n)) continue;
                        level[n] = ring;
                        next.Add(n);
                    }
                }
                frontier = next;
            }

            return new PaintedRegion(prims, offsets, dsu, level, rootAdjacency);
        }

        // The painted patch resolved against one mesh: the primitives that carry skin data, the
        // running offsets that let (primitive, vertex) be addressed as one flat index space, the
        // union-find that bridged the UV seams, and the ring level every logical vertex the paint
        // reached ended up on. Both Apply and Remove walk exactly this - they differ only in what
        // they write into the weights once a vertex's feather factor is in hand.
        internal sealed record PaintedRegion(
            List<(int PrimIndex, Accessor Joints, Accessor Weights,
                IList<Vector3> Positions, List<(int A, int B, int C)> Triangles)> Prims,
            int[] Offsets,
            DisjointSet Dsu,
            Dictionary<int, int> Level,
            Dictionary<int, HashSet<int>> RootAdjacency)
        {
            // 1 at the painted core, tapering across the feather rings, 0 for anything the paint
            // never reached (which callers skip entirely).
            public float FeatherAt(int primSlot, int vertex, int maxRings)
            {
                int root = Dsu.Find(Offsets[primSlot] + vertex);
                return Level.TryGetValue(root, out int lvl) ? FeatherFactor(lvl, maxRings) : 0f;
            }
        }

        // For regions pinned in an EARLIER session. Their original weights were saved over and no
        // snapshot exists, so there is nothing exact to put back - instead the painted vertices'
        // weights are rebuilt from the unpinned skin around them. The vertices just outside the
        // paint (and its feather rings) keep their weights and act as the boundary; everything
        // inside is solved as the smooth (harmonic) blend of that boundary, which is the shape a
        // hand-smoothed weight map takes anyway: a shoulder patch pinned 100% to Spine comes back
        // shading from the arm's weights on one side to the neck's on the other. Feather rings
        // then ease from the rebuilt weights into what's already there, as Apply's own rings do.
        // An island the paint covers entirely has no boundary to rebuild from and is left alone.
        // `snapshots` gets each primitive's pre-edit skin the first time it is written, for the
        // caller's Revert.
        internal static int RebuildWeightsInMesh(
            SharpGLTF.Schema2.Mesh mesh, int meshIdx,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            int maxRings, SkinSnapshots snapshots, ref int skippedPrims)
        {
            var region = BuildPaintedRegion(mesh, meshIdx, selection, maxRings);
            if (region == null) { skippedPrims++; return 0; }

            var joints = new IList<Vector4>[region.Prims.Count];
            var weights = new IList<Vector4>[region.Prims.Count];
            for (int i = 0; i < region.Prims.Count; i++)
            {
                joints[i] = region.Prims[i].Joints.AsVector4Array();
                weights[i] = region.Prims[i].Weights.AsVector4Array();
            }

            // One weight map per logical vertex; seam duplicates share skin data, so the first
            // copy speaks for all of them.
            var current = new Dictionary<int, Dictionary<int, float>>();
            for (int i = 0; i < region.Prims.Count; i++)
                for (int v = 0; v < region.Prims[i].Positions.Count; v++)
                {
                    int root = region.Dsu.Find(region.Offsets[i] + v);
                    if (current.ContainsKey(root)) continue;
                    var map = new Dictionary<int, float>();
                    void One(float j, float w) { if (w > 0f) map[(int)j] = map.TryGetValue((int)j, out var e) ? e + w : w; }
                    One(joints[i][v].X, weights[i][v].X); One(joints[i][v].Y, weights[i][v].Y);
                    One(joints[i][v].Z, weights[i][v].Z); One(joints[i][v].W, weights[i][v].W);
                    current[root] = map;
                }

            // Seed from the boundary inward, breadth first, so every unknown starts from the
            // average of whatever is already settled next to it. That alone is a reasonable fill;
            // the relaxation below then smooths away the ring-by-ring banding it leaves.
            var solved = new Dictionary<int, Dictionary<int, float>>();
            var order = new List<int>();
            var frontier = region.Level.Keys
                .Where(r => region.RootAdjacency.TryGetValue(r, out var n) && n.Any(x => !region.Level.ContainsKey(x)))
                .ToList();
            var queued = new HashSet<int>(frontier);
            while (frontier.Count > 0)
            {
                var next = new List<int>();
                foreach (int root in frontier)
                {
                    solved[root] = AverageOfNeighbors(root);
                    order.Add(root);
                    foreach (int n in region.RootAdjacency[root])
                        if (region.Level.ContainsKey(n) && queued.Add(n)) next.Add(n);
                }
                frontier = next;
            }

            Dictionary<int, float> AverageOfNeighbors(int root)
            {
                var sum = new Dictionary<int, float>();
                int count = 0;
                foreach (int n in region.RootAdjacency[root])
                {
                    var map = region.Level.ContainsKey(n)
                        ? (solved.TryGetValue(n, out var s) ? s : null)
                        : current.GetValueOrDefault(n);
                    if (map == null) continue;
                    foreach (var (j, w) in map) sum[j] = sum.GetValueOrDefault(j) + w;
                    count++;
                }
                if (count > 0)
                    foreach (int j in sum.Keys.ToList()) sum[j] /= count;
                return sum;
            }

            // Gauss-Seidel relaxation toward the harmonic solution. Tiny weights are pruned each
            // pass so the maps don't fill up with every joint the diffusion ever brushed past.
            for (int iter = 0; iter < 300; iter++)
            {
                float maxDelta = 0f;
                foreach (int root in order)
                {
                    var avg = AverageOfNeighbors(root);
                    foreach (var j in avg.Where(kv => kv.Value < 1e-4f).Select(kv => kv.Key).ToList()) avg.Remove(j);
                    var old = solved[root];
                    foreach (var j in avg.Keys.Union(old.Keys))
                        maxDelta = MathF.Max(maxDelta, MathF.Abs(avg.GetValueOrDefault(j) - old.GetValueOrDefault(j)));
                    solved[root] = avg;
                }
                if (maxDelta < 1e-4f) break;
            }

            int touched = 0;
            for (int i = 0; i < region.Prims.Count; i++)
            {
                var (_, jointsAcc, weightsAcc, positions, _) = region.Prims[i];
                bool written = false;

                for (int v = 0; v < positions.Count; v++)
                {
                    float f = region.FeatherAt(i, v, maxRings);
                    if (f <= 0f) continue;
                    int root = region.Dsu.Find(region.Offsets[i] + v);
                    if (!solved.TryGetValue(root, out var map) || map.Count == 0) continue;

                    if (!written)
                    {
                        if (!snapshots.ContainsKey(jointsAcc))
                            snapshots[jointsAcc] = (weightsAcc, joints[i].ToArray(), weights[i].ToArray());
                        written = true;
                    }

                    var top = map.OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float total = top.Sum(kv => kv.Value);
                    var tj = new float[4];
                    var tw = new float[4];
                    for (int k = 0; k < top.Count; k++) { tj[k] = top[k].Key; tw[k] = top[k].Value / total; }

                    var (nj, nw) = BlendTowardOriginal(joints[i][v], weights[i][v],
                        new Vector4(tj[0], tj[1], tj[2], tj[3]), new Vector4(tw[0], tw[1], tw[2], tw[3]), f);
                    joints[i][v] = nj;
                    weights[i][v] = nw;
                    touched++;
                }
            }
            return touched;
        }

        // Minimal union-find with path compression and union-by-rank, used to collapse
        // position-coincident (seam-duplicate) vertices into single logical nodes.
        internal sealed class DisjointSet
        {
            private readonly int[] _parent;
            private readonly int[] _rank;

            public DisjointSet(int size)
            {
                _parent = new int[size];
                _rank = new int[size];
                for (int i = 0; i < size; i++) _parent[i] = i;
            }

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
                _parent[rb] = ra;
                if (_rank[ra] == _rank[rb]) _rank[ra]++;
            }
        }
    }
}
