using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // The inverse of simplification: puts triangles back into a painted region of an over-pruned
    // mesh, and bends them out to the curve the surface's normals say it should have, so a faceted
    // cylinder or shoulder rounds out again.
    //
    // Neither library the optimizer stands on can do this. meshoptimizer only ever removes
    // triangles - its one tessellation entry point (meshopt_generateTessellationIndexBuffer) builds
    // a 12-index patch buffer for a GPU hull shader and makes no vertices - and Assimp's
    // Catmull-Clark subdivider is internal to its AC3D loader, not reachable through the C API
    // SharpAssimp binds. So the refinement is done here.
    //
    // Each painted triangle is treated as a PN triangle (Vlachos et al., "Curved PN Triangles"):
    // a cubic Bezier patch through its three corners whose tangent plane at each corner is the
    // plane its normal defines. That's exactly the information a simplified mesh still carries -
    // meshopt keeps the surviving vertices' original normals, which were averaged over the dense
    // surface that's gone, so they still point the way the real curve did. The patch is sampled on
    // an N x N barycentric grid, every new vertex's other attributes (UV, colour, tangent, skin)
    // interpolated linearly from the corners so the texture mapping is the one the flat triangle
    // already had.
    //
    // Cracks are the problem with PN triangles, and they're avoided by construction rather than
    // patched afterwards:
    //  - A patch edge depends only on its two end positions and their normals, but at a UV seam or
    //    a hard edge the two triangles either side address different vertex indices that may carry
    //    different normals. So every point ON an edge is computed once per WELDED edge (vertices
    //    merged by position) and reused by every triangle that meets there, whichever indices it
    //    uses. Only the non-positional attributes are per index edge.
    //  - An edge whose two faces meet at more than the crease angle is a deliberate hard edge (a
    //    box corner, a roof ridge) and stays straight: its control points are put on the line, and
    //    the patch interior follows.
    //  - The edge of the paint is shared with triangles that aren't being refined. Each of those
    //    gets the split edge's new vertices too, fanned from its opposite corner, so the surface
    //    stays watertight. One that would be split on two or three sides is simply refined with
    //    the rest - that only happens in a notch of the paint, where it's what was meant anyway.
    public static class RegionRefiner
    {
        public sealed class Options
        {
            /// <summary>Segments each painted edge is cut into: the triangle count grows by its square.</summary>
            public int Segments { get; set; } = 2;

            /// <summary>0 = flat subdivision, 1 = the full PN curve, above 1 exaggerates it.</summary>
            public float Roundness { get; set; } = 1f;

            /// <summary>Faces meeting at more than this (degrees) keep a sharp, straight edge.</summary>
            public float CreaseAngleDegrees { get; set; } = 60f;

            /// <summary>Curve to the stored NORMALs (the original surface's) rather than ones rebuilt from the faces.</summary>
            public bool UseStoredNormals { get; set; } = true;
        }

        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }

            public int TrianglesBefore { get; set; }
            public int TrianglesAfter { get; set; }
            public int VerticesAdded { get; set; }

            /// <summary>Painted triangles that were refined.</summary>
            public int Refined { get; set; }
            /// <summary>Unpainted triangles refined anyway because two or more of their sides were being split.</summary>
            public int Promoted { get; set; }
            /// <summary>Unpainted neighbours fanned to meet one split edge.</summary>
            public int Stitched { get; set; }
            /// <summary>Hard edges kept straight inside or around the paint.</summary>
            public int CreaseEdges { get; set; }
            /// <summary>Furthest any new vertex moved off the flat triangle, in model units.</summary>
            public float MaxBulge { get; set; }

            public string? SkippedReason { get; set; }

            internal WatertightRepair.VertexPatch? Patch { get; set; }
            internal HashSet<int> NewSelection { get; } = new();
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();

            public int TrianglesBefore => Primitives.Where(p => p.Patch != null).Sum(p => p.TrianglesBefore);
            public int TrianglesAfter => Primitives.Where(p => p.Patch != null).Sum(p => p.TrianglesAfter);
            public int TrianglesAdded => TrianglesAfter - TrianglesBefore;
            public int VerticesAdded => Primitives.Sum(p => p.VerticesAdded);
            public int Refined => Primitives.Sum(p => p.Refined);
            public int Promoted => Primitives.Sum(p => p.Promoted);
            public int Stitched => Primitives.Sum(p => p.Stitched);
            public int CreaseEdges => Primitives.Sum(p => p.CreaseEdges);
            public float MaxBulge => Primitives.Count == 0 ? 0f : Primitives.Max(p => p.MaxBulge);
            public bool HasChanges => Primitives.Any(p => p.Patch != null);

            /// <summary>The refined region's triangles, in the new numbering - so the paint follows the result.</summary>
            public Dictionary<(int, int), HashSet<int>> NewSelection =>
                Primitives.Where(p => p.Patch != null)
                    .ToDictionary(p => (p.MeshIndex, p.PrimitiveIndex), p => p.NewSelection);
        }

        // Positions closer than this share of the primitive's extent are the same point - the
        // twins either side of a UV seam or a hard edge.
        private const float WeldFraction = 1e-6f;

        // A stored normal this far off its own face (degrees) is broken or belongs to a very
        // different surface; the patch would fold over trying to honour it, so the face-derived
        // normal stands in for that corner instead.
        private const float MaxNormalDeviation = 80f;

        public static Report Analyze(ModelRoot model,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection, Options options)
        {
            var report = new Report();
            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    if (!selection.TryGetValue((meshIdx, primIdx), out var painted) || painted.Count == 0) continue;

                    var result = new PrimitiveResult
                    {
                        MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                        MeshIndex = meshIdx,
                        PrimitiveIndex = primIdx,
                    };
                    report.Primitives.Add(result);
                    AnalyzePrimitive(mesh.Primitives[primIdx], painted, options, result);
                }
            }
            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.Patch == null) continue;
                WatertightRepair.WritePatch(model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex], p.Patch);
            }
        }

        private static void AnalyzePrimitive(MeshPrimitive prim, HashSet<int> painted, Options options, PrimitiveResult result)
        {
            // Appending vertices means re-encoding every attribute, which is what compaction needs too.
            if (!GeometryOptimizer.CanCompactPrimitive(prim))
            {
                result.SkippedReason = prim.MorphTargetsCount > 0 ? "has morph targets" : "unfamiliar vertex encoding";
                return;
            }

            int n = Math.Clamp(options.Segments, 2, 16);
            var tris = prim.GetTriangleIndices().ToArray();
            result.TrianglesBefore = tris.Length;

            var attributes = new Dictionary<string, IList<Vector4>>();
            foreach (var (name, accessor) in prim.VertexAccessors)
                attributes[name] = WatertightRepair.ReadAsVector4(accessor);
            var positions = attributes["POSITION"].Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
            attributes.TryGetValue("NORMAL", out var storedNormals);
            int baseCount = positions.Length;

            // ---- Weld by position --------------------------------------------------------------
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float cell = MathF.Max((max - min).Length() * WeldFraction, 1e-12f);
            var weld = new int[baseCount];
            var weldIds = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < baseCount; i++)
            {
                var p = positions[i];
                var key = ((long)MathF.Round(p.X / cell), (long)MathF.Round(p.Y / cell), (long)MathF.Round(p.Z / cell));
                if (!weldIds.TryGetValue(key, out int id)) weldIds[key] = id = weldIds.Count;
                weld[i] = id;
            }

            // ---- Faces, welded edges, creases --------------------------------------------------
            var faceNormal = new Vector3[tris.Length];
            var faceArea = new float[tris.Length];
            var degenerate = new bool[tris.Length];
            var facesOfWeldedEdge = new Dictionary<(int, int), List<int>>();
            var facesOfWeld = new Dictionary<int, List<int>>();
            for (int f = 0; f < tris.Length; f++)
            {
                var (a, b, c) = tris[f];
                int ga = weld[a], gb = weld[b], gc = weld[c];
                var cross = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                float len = cross.Length();
                degenerate[f] = ga == gb || gb == gc || gc == ga || len < 1e-20f;
                if (degenerate[f]) continue;
                faceNormal[f] = cross / len;
                faceArea[f] = len * 0.5f;
                foreach (var e in new[] { WKey(ga, gb), WKey(gb, gc), WKey(gc, ga) })
                {
                    if (!facesOfWeldedEdge.TryGetValue(e, out var list)) facesOfWeldedEdge[e] = list = new List<int>(2);
                    list.Add(f);
                }
                foreach (int g in new[] { ga, gb, gc })
                {
                    if (!facesOfWeld.TryGetValue(g, out var list)) facesOfWeld[g] = list = new List<int>(6);
                    list.Add(f);
                }
            }

            float creaseCos = MathF.Cos(options.CreaseAngleDegrees * MathF.PI / 180f);
            var creaseCache = new Dictionary<(int, int), bool>();
            bool IsCrease((int, int) weldedEdge)
            {
                if (creaseCache.TryGetValue(weldedEdge, out bool known)) return known;
                bool crease = false;
                if (facesOfWeldedEdge.TryGetValue(weldedEdge, out var faces))
                    for (int i = 0; i < faces.Count && !crease; i++)
                        for (int j = i + 1; j < faces.Count && !crease; j++)
                            crease = Vector3.Dot(faceNormal[faces[i]], faceNormal[faces[j]]) < creaseCos;
                creaseCache[weldedEdge] = crease;
                return crease;
            }

            // ---- Which triangles get refined ---------------------------------------------------
            var refine = new bool[tris.Length];
            foreach (int f in painted)
                if (f >= 0 && f < tris.Length && !degenerate[f]) refine[f] = true;
            if (!refine.Any(r => r))
            {
                result.SkippedReason = "the painted triangles are all degenerate";
                return;
            }

            // Split edges are WELDED edges: the neighbour across a UV seam addresses different
            // vertices but has to be split at the same points all the same. A neighbour split on
            // two or more sides is promoted into the refined set, which can in turn split more of
            // its neighbours, so this runs to a fixed point.
            var splitEdges = new HashSet<(int, int)>();
            bool IsSplit(int a, int b) => splitEdges.Contains(WKey(weld[a], weld[b]));
            void MarkSplit(int f)
            {
                var (a, b, c) = tris[f];
                splitEdges.Add(WKey(weld[a], weld[b])); splitEdges.Add(WKey(weld[b], weld[c])); splitEdges.Add(WKey(weld[c], weld[a]));
            }
            for (int f = 0; f < tris.Length; f++) if (refine[f]) MarkSplit(f);
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int f = 0; f < tris.Length; f++)
                {
                    if (refine[f] || degenerate[f]) continue;
                    var (a, b, c) = tris[f];
                    int sides = (IsSplit(a, b) ? 1 : 0) + (IsSplit(b, c) ? 1 : 0) + (IsSplit(c, a) ? 1 : 0);
                    if (sides < 2) continue;
                    refine[f] = true;
                    result.Promoted++;
                    MarkSplit(f);
                    changed = true;
                }
            }

            // ---- Corner normals the curve is built from ----------------------------------------
            // Rebuilt from the faces round the corner that are on this face's side of any crease,
            // angle-weighted so a fan of slivers doesn't outvote one big face.
            Vector3 ComputedNormal(int f, int corner)
            {
                int g = weld[corner];
                var sum = Vector3.Zero;
                foreach (int o in facesOfWeld[g])
                {
                    if (Vector3.Dot(faceNormal[o], faceNormal[f]) < creaseCos) continue;
                    sum += faceNormal[o] * CornerAngle(o, g);
                }
                return sum.LengthSquared() > 1e-20f ? Vector3.Normalize(sum) : faceNormal[f];
            }
            float CornerAngle(int f, int g)
            {
                var (a, b, c) = tris[f];
                int at = weld[a] == g ? a : weld[b] == g ? b : c;
                int p = at == a ? b : at == b ? c : a;
                int q = at == a ? c : at == b ? a : b;
                var u = positions[p] - positions[at];
                var v = positions[q] - positions[at];
                float d = u.Length() * v.Length();
                return d < 1e-20f ? 0f : MathF.Acos(Math.Clamp(Vector3.Dot(u, v) / d, -1f, 1f));
            }
            float maxDeviationCos = MathF.Cos(MaxNormalDeviation * MathF.PI / 180f);
            Vector3 CornerNormal(int f, int corner)
            {
                if (options.UseStoredNormals && storedNormals != null)
                {
                    var s = storedNormals[corner];
                    var nrm = new Vector3(s.X, s.Y, s.Z);
                    if (nrm.LengthSquared() > 1e-20f)
                    {
                        nrm = Vector3.Normalize(nrm);
                        if (Vector3.Dot(nrm, faceNormal[f]) >= maxDeviationCos) return nrm;
                    }
                }
                return ComputedNormal(f, corner);
            }

            // ---- New vertices ------------------------------------------------------------------
            var appended = attributes.Keys.ToDictionary(name => name, _ => new List<Vector4>());
            int AppendedCount() => appended["POSITION"].Count;

            // Points on a welded edge, ga < gb, indexed by step from ga - shared by every triangle
            // meeting there, so the two sides of a seam land on exactly the same positions.
            var weldedEdgePoints = new Dictionary<(int, int), Vector3[]>();
            // Vertices on an index edge, a < b, indexed by step from a.
            var indexEdgeVertices = new Dictionary<(int, int), int[]>();
            var countedCreases = new HashSet<(int, int)>();

            Vector3[] WeldedEdgePoints(int f, int a, int b)
            {
                int ga = weld[a], gb = weld[b];
                var key = WKey(ga, gb);
                if (weldedEdgePoints.TryGetValue(key, out var pts)) return pts;

                // Built in the canonical direction so every caller reads it the same way round.
                int lo = ga == key.Item1 ? a : b, hi = lo == a ? b : a;
                var p0 = positions[lo];
                var p1 = positions[hi];
                bool crease = IsCrease(key);
                if (crease && countedCreases.Add(key)) result.CreaseEdges++;
                var (c0, c1) = crease
                    ? ((2 * p0 + p1) / 3f, (2 * p1 + p0) / 3f)
                    : EdgeControls(p0, CornerNormal(f, lo), p1, CornerNormal(f, hi));

                pts = new Vector3[n + 1];
                for (int s = 0; s <= n; s++)
                {
                    float t = s / (float)n;
                    var flat = Vector3.Lerp(p0, p1, t);
                    var curved = CubicBezier(p0, c0, c1, p1, t);
                    pts[s] = Vector3.Lerp(flat, curved, options.Roundness);
                    result.MaxBulge = MathF.Max(result.MaxBulge, Vector3.Distance(flat, pts[s]));
                }
                weldedEdgePoints[key] = pts;
                return pts;
            }

            // The vertex `step` segments along index edge a->b (0 < step < n).
            int EdgeVertex(int f, int a, int b, int step)
            {
                var key = IKey(a, b);
                int fromLo = a == key.Item1 ? step : n - step;
                if (!indexEdgeVertices.TryGetValue(key, out var verts))
                {
                    int lo = key.Item1, hi = key.Item2;
                    var pts = WeldedEdgePoints(f, lo, hi);
                    bool reversed = weld[lo] != WKey(weld[lo], weld[hi]).Item1;
                    verts = new int[n + 1];
                    verts[0] = lo;
                    verts[n] = hi;
                    for (int s = 1; s < n; s++)
                    {
                        float t = s / (float)n;
                        verts[s] = baseCount + AppendedCount();
                        foreach (var (name, values) in appended)
                            values.Add(Interpolate(name, attributes[name], new[] { (lo, 1f - t), (hi, t) }));
                        MergeSkin(attributes, appended, new[] { (lo, 1f - t), (hi, t) });
                        var p = pts[reversed ? n - s : s];
                        appended["POSITION"][^1] = new Vector4(p, 0);
                    }
                    indexEdgeVertices[key] = verts;
                }
                return verts[fromLo];
            }

            // Every split edge belongs to at least one refined triangle; building the curves from
            // those first means a stitched neighbour never decides the shape with its own normals.
            for (int f = 0; f < tris.Length; f++)
            {
                if (!refine[f]) continue;
                var (a, b, c) = tris[f];
                WeldedEdgePoints(f, a, b); WeldedEdgePoints(f, b, c); WeldedEdgePoints(f, c, a);
            }

            var newTriangles = new List<(int A, int B, int C)>(tris.Length + painted.Count * n * n);
            var newSelection = result.NewSelection;
            var grid = new int[(n + 1) * (n + 1)];

            for (int f = 0; f < tris.Length; f++)
            {
                var (a, b, c) = tris[f];

                if (refine[f])
                {
                    result.Refined++;
                    var pa = positions[a]; var pb = positions[b]; var pc = positions[c];
                    var na = CornerNormal(f, a); var nb = CornerNormal(f, b); var nc = CornerNormal(f, c);

                    // Edge control points: the shared welded-edge curve where the edge is curved,
                    // straight where it's a crease - the same choice the edge points made.
                    var (ab0, ab1) = Controls(pa, na, pb, nb, IsCrease(WKey(weld[a], weld[b])));
                    var (bc0, bc1) = Controls(pb, nb, pc, nc, IsCrease(WKey(weld[b], weld[c])));
                    var (ca0, ca1) = Controls(pc, nc, pa, na, IsCrease(WKey(weld[c], weld[a])));
                    var e = (ab0 + ab1 + bc0 + bc1 + ca0 + ca1) / 6f;
                    var v = (pa + pb + pc) / 3f;
                    var centre = e + (e - v) / 2f;

                    // Grid point (i, j): weight i/n on B, j/n on C, the rest on A.
                    for (int i = 0; i <= n; i++)
                        for (int j = 0; i + j <= n; j++)
                        {
                            int k = n - i - j;
                            int idx;
                            if (i == 0 && j == 0) idx = a;
                            else if (i == n) idx = b;
                            else if (j == n) idx = c;
                            else if (j == 0) idx = EdgeVertex(f, a, b, i);
                            else if (k == 0) idx = EdgeVertex(f, b, c, j);
                            else if (i == 0) idx = EdgeVertex(f, c, a, n - j);
                            else
                            {
                                float wa = k / (float)n, wb = i / (float)n, wc = j / (float)n;
                                idx = baseCount + AppendedCount();
                                var weights = new[] { (a, wa), (b, wb), (c, wc) };
                                foreach (var (name, values) in appended)
                                    values.Add(Interpolate(name, attributes[name], weights));
                                MergeSkin(attributes, appended, weights);

                                var flat = pa * wa + pb * wb + pc * wc;
                                var curved =
                                    pa * (wa * wa * wa) + pb * (wb * wb * wb) + pc * (wc * wc * wc)
                                    + 3f * (ab0 * (wa * wa * wb) + ab1 * (wa * wb * wb)
                                          + bc0 * (wb * wb * wc) + bc1 * (wb * wc * wc)
                                          + ca0 * (wc * wc * wa) + ca1 * (wc * wa * wa))
                                    + 6f * centre * (wa * wb * wc);
                                var p = Vector3.Lerp(flat, curved, options.Roundness);
                                result.MaxBulge = MathF.Max(result.MaxBulge, Vector3.Distance(flat, p));
                                appended["POSITION"][^1] = new Vector4(p, 0);
                            }
                            grid[i * (n + 1) + j] = idx;
                        }

                    int G(int i, int j) => grid[i * (n + 1) + j];
                    for (int i = 0; i < n; i++)
                        for (int j = 0; i + j < n; j++)
                        {
                            newSelection.Add(newTriangles.Count);
                            newTriangles.Add((G(i, j), G(i + 1, j), G(i, j + 1)));
                            if (i + j < n - 1)
                            {
                                newSelection.Add(newTriangles.Count);
                                newTriangles.Add((G(i + 1, j), G(i + 1, j + 1), G(i, j + 1)));
                            }
                        }
                    continue;
                }

                // Not refined: split on at most one side (two or more would have promoted it - a
                // degenerate triangle is never promoted, but it has no area to leave a gap in).
                // Rotate so that side is a->b, then fan from c, which keeps the winding.
                if (IsSplit(b, c)) (a, b, c) = (b, c, a);
                else if (IsSplit(c, a)) (a, b, c) = (c, a, b);
                else if (!IsSplit(a, b)) { newTriangles.Add(tris[f]); continue; }

                result.Stitched++;
                int prev = a;
                for (int s = 1; s <= n; s++)
                {
                    int next = s == n ? b : EdgeVertex(f, a, b, s);
                    newTriangles.Add((prev, next, c));
                    prev = next;
                }
            }

            result.TrianglesAfter = newTriangles.Count;
            result.VerticesAdded = AppendedCount();

            var indices = new int[newTriangles.Count * 3];
            int w = 0;
            foreach (var (x, y, z) in newTriangles) { indices[w++] = x; indices[w++] = y; indices[w++] = z; }
            result.Patch = new WatertightRepair.VertexPatch
            {
                AppendedAttributes = appended.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Vector4>)kv.Value),
                NewIndices = indices,
            };
        }

        private static (int, int) WKey(int a, int b) => a < b ? (a, b) : (b, a);
        private static (int, int) IKey(int a, int b) => a < b ? (a, b) : (b, a);

        // The two inner control points of a PN edge from p0 to p1: each end's third-of-the-way
        // point, projected onto that end's tangent plane.
        private static (Vector3, Vector3) EdgeControls(Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1)
        {
            float w01 = Vector3.Dot(p1 - p0, n0);
            float w10 = Vector3.Dot(p0 - p1, n1);
            return ((2 * p0 + p1 - w01 * n0) / 3f, (2 * p1 + p0 - w10 * n1) / 3f);
        }

        private static (Vector3, Vector3) Controls(Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1, bool crease) =>
            crease ? ((2 * p0 + p1) / 3f, (2 * p1 + p0) / 3f) : EdgeControls(p0, n0, p1, n1);

        private static Vector3 CubicBezier(Vector3 p0, Vector3 c0, Vector3 c1, Vector3 p1, float t)
        {
            float u = 1 - t;
            return p0 * (u * u * u) + c0 * (3 * u * u * t) + c1 * (3 * u * t * t) + p1 * (t * t * t);
        }

        // Linear blend of the corners for everything that's a magnitude; directions renormalized,
        // a tangent's handedness taken from the heaviest corner. JOINTS/WEIGHTS get a placeholder
        // here that MergeSkin overwrites.
        private static Vector4 Interpolate(string name, IList<Vector4> values, (int Vertex, float Weight)[] corners)
        {
            var v = Vector4.Zero;
            foreach (var (vertex, weight) in corners) v += values[vertex] * weight;
            if (name == "NORMAL" || name == "TANGENT")
            {
                var heaviest = corners.MaxBy(c => c.Weight).Vertex;
                var d = new Vector3(v.X, v.Y, v.Z);
                d = d.LengthSquared() > 1e-20f ? Vector3.Normalize(d)
                    : new Vector3(values[heaviest].X, values[heaviest].Y, values[heaviest].Z);
                v = new Vector4(d, name == "TANGENT" ? values[heaviest].W : 0f);
            }
            return v;
        }

        // Joint indices name bones, so they can't be blended; the new vertex's binding is the
        // union of the corners' influences, weights blended, cut to the strongest four and
        // renormalized - the same rule PlanarCutter uses for a cut vertex.
        private static void MergeSkin(Dictionary<string, IList<Vector4>> attributes,
            Dictionary<string, List<Vector4>> appended, (int Vertex, float Weight)[] corners)
        {
            for (int set = 0; ; set++)
            {
                string jointsName = $"JOINTS_{set}", weightsName = $"WEIGHTS_{set}";
                if (!attributes.TryGetValue(jointsName, out var joints) || !attributes.TryGetValue(weightsName, out var weights))
                    break;

                var blended = new Dictionary<int, float>();
                foreach (var (vertex, scale) in corners)
                {
                    if (scale <= 0f) continue;
                    var j = joints[vertex];
                    var wt = weights[vertex];
                    foreach (var (joint, weight) in new[] { (j.X, wt.X), (j.Y, wt.Y), (j.Z, wt.Z), (j.W, wt.W) })
                    {
                        if (weight <= 0f) continue;
                        int key = (int)MathF.Round(joint);
                        blended[key] = blended.GetValueOrDefault(key) + weight * scale;
                    }
                }

                var top = blended.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float total = top.Sum(kv => kv.Value);
                if (total <= 0f) top = new List<KeyValuePair<int, float>> { new(0, 1f) };
                else top = top.Select(kv => new KeyValuePair<int, float>(kv.Key, kv.Value / total)).ToList();
                while (top.Count < 4) top.Add(new KeyValuePair<int, float>(0, 0f));

                appended[jointsName][^1] = new Vector4(top[0].Key, top[1].Key, top[2].Key, top[3].Key);
                appended[weightsName][^1] = new Vector4(top[0].Value, top[1].Value, top[2].Value, top[3].Value);
            }
        }
    }
}
