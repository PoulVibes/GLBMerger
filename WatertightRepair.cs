using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Finds open holes in the merged model and caps them, so the result is a closed
    // ("watertight") surface - useful for 3D printing or physics/collision generation,
    // neither of which tolerates a gap in the shell.
    //
    // A hole is a boundary loop: a chain of edges each used by exactly one triangle. But glTF
    // routinely splits one continuous surface into several MeshPrimitives (one per material or
    // UV island), so a boundary edge in one primitive is very often just a seam matched by a
    // boundary edge in a sibling primitive of the same mesh - not an actual hole. Those are told
    // apart by position: an edge whose endpoints match a boundary edge in exactly one other
    // primitive of the same mesh is a seam and is left alone; one that matches nowhere else is a
    // real hole; one that matches two or more is non-manifold and is reported rather than guessed
    // at.
    //
    // Genuine holes are capped with a centroid fan: one new vertex at the loop's averaged
    // position (and averaged normal/UV/color/skinning), triangulated to every edge of the loop.
    // That's simple and robust for arbitrary, non-planar loops - unlike ear-clipping, which needs
    // a stable projection plane a hole is not guaranteed to have - at the cost of one extra vertex
    // per hole, which is an easy trade for a repair tool.
    //
    // Texturing the cap by averaging the loop's own UVs (the obvious first move) breaks down
    // whenever the loop's vertices sit in different UV islands of the same atlas - common right at
    // a seam - because the average then samples a meaningless blend of two unrelated patches. So
    // instead, for any primitive whose material has a BaseColor texture, this looks for a patch of
    // that atlas nothing already maps to (reusing TextureAtlasUtil's own UV-footprint
    // rasterizer to find it) and, if a sufficiently blank one exists, points every cap vertex at
    // one fixed spot inside it - which also means the cap's vertices can no longer be the primitive's
    // original boundary vertices (those still need their real UV for the surface beside the hole),
    // so they are duplicated first. Where no textured material or no free patch exists, it falls
    // back to plain UV averaging over the original vertices, unchanged from before.
    //
    // Unlike GeometryOptimizer's simplification (which only ever rewrites the index buffer),
    // filling a hole adds vertices, so GeometryHistory has to capture full vertex accessor
    // contents before a fill runs, not just the indices - see its BeginChange.
    //
    // Besides finding holes itself (Analyze), the capping half is offered on its own to a caller
    // that already knows its rings (CapLoops) - PlanarCutter closes the flat edge a cut leaves
    // that way, so a cut cap and a fill cap are textured and built by the same code.
    //
    // A cut left uncapped and then saved is the case the per-primitive loop search can't handle:
    // the ring runs through every mesh part the plane crossed, so each part holds an open chain
    // that never closes on its own, and every one of them is reported as unresolved. Sealing
    // planar gaps (an option on Analyze) handles exactly that: every boundary edge lying on an
    // axis-aligned plane, taken across ALL parts of the mesh at once and joined by position, is
    // chained into rings on that plane, and each ring is closed with a flat polygon
    // triangulation (PolygonTriangulator) - n - 2 triangles over the ring's own vertices, no
    // centroid - hosted in whichever part contributes most of the ring. Rings nested inside
    // another on the same plane are treated as holes in its cap.
    public static class WatertightRepair
    {
        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }
            public int HolesFound { get; set; }
            public int HolesFilled { get; set; }
            public int TrianglesAdded { get; set; }
            public int VerticesAdded { get; set; }
            public int UnresolvedEdges { get; set; }
            public string? SkippedReason { get; set; }

            /// <summary>True when caps here sample a found blank patch of the material's texture rather than an averaged UV.</summary>
            public bool UsedTexturePatch { get; set; }

            /// <summary>Rings on an axis-aligned plane sealed with a flat polygon (see Analyze's sealPlanarGaps), hosted in this part.</summary>
            public int PlanarHolesSealed { get; set; }

            /// <summary>Planar chains that didn't close on their own and were closed with a straight edge across the plane.</summary>
            public int PlanarChainsClosed { get; set; }

            /// <summary>Ring vertices that lay on a straight run between two corners and so weren't needed by the cap.</summary>
            public int PlanarCornersDropped { get; set; }

            // Filled in by Analyze, consumed by Apply - null when this primitive is unchanged.
            public VertexPatch? Patch { get; set; }
        }

        // The new data a fill needs to write into one primitive: appended vertex attribute
        // values (parallel arrays, one entry per new vertex, in the same order as the source
        // primitive's own VertexAccessors) and the full replacement index buffer.
        public sealed class VertexPatch
        {
            public required Dictionary<string, IReadOnlyList<Vector4>> AppendedAttributes { get; init; }
            public required int[] NewIndices { get; init; }
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();
            public int HolesFound => Primitives.Sum(p => p.HolesFound);
            public int HolesFilled => Primitives.Sum(p => p.HolesFilled);
            public int TrianglesAdded => Primitives.Sum(p => p.TrianglesAdded);
            public int VerticesAdded => Primitives.Sum(p => p.VerticesAdded);
            public int UnresolvedEdges => Primitives.Sum(p => p.UnresolvedEdges);
            public int PrimitivesUsingTexturePatch => Primitives.Count(p => p.UsedTexturePatch);
            public int PlanarHolesSealed => Primitives.Sum(p => p.PlanarHolesSealed);
            public int PlanarChainsClosed => Primitives.Sum(p => p.PlanarChainsClosed);
            public int PlanarCornersDropped => Primitives.Sum(p => p.PlanarCornersDropped);
            public bool HasChanges => Primitives.Any(p => p.Patch != null);
        }

        private const float WeldTolerance = 1e-4f;

        private readonly struct Edge : IEquatable<Edge>
        {
            public readonly int A, B;
            public Edge(int a, int b) { A = a; B = b; }
            public Edge Reversed => new(B, A);
            public bool Equals(Edge other) => A == other.A && B == other.B;
            public override bool Equals(object? obj) => obj is Edge e && Equals(e);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }

        private readonly struct PosKey : IEquatable<PosKey>
        {
            public readonly long X, Y, Z;
            public PosKey(Vector3 p, float scale)
            {
                X = (long)MathF.Round(p.X * scale);
                Y = (long)MathF.Round(p.Y * scale);
                Z = (long)MathF.Round(p.Z * scale);
            }
            public bool Equals(PosKey other) => X == other.X && Y == other.Y && Z == other.Z;
            public override bool Equals(object? obj) => obj is PosKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(X, Y, Z);
        }

        private readonly struct EdgeKey : IEquatable<EdgeKey>
        {
            public readonly PosKey A, B;
            public EdgeKey(PosKey a, PosKey b)
            {
                // Order-independent so the same physical edge hashes the same regardless of which
                // primitive's winding it was read from.
                if (Compare(a, b) <= 0) { A = a; B = b; }
                else { A = b; B = a; }
            }
            private static int Compare(PosKey x, PosKey y)
            {
                int c = x.X.CompareTo(y.X); if (c != 0) return c;
                c = x.Y.CompareTo(y.Y); if (c != 0) return c;
                return x.Z.CompareTo(y.Z);
            }
            public bool Equals(EdgeKey other) => A.Equals(other.A) && B.Equals(other.B);
            public override bool Equals(object? obj) => obj is EdgeKey k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(A, B);
        }

        // Everything Analyze needs from one primitive, decoded once up front.
        private sealed class PrimitiveData
        {
            public required MeshPrimitive Prim { get; init; }
            public required IList<Vector3> Positions { get; init; }
            public required Dictionary<string, IList<Vector4>> Attributes { get; init; }
            public required (int A, int B, int C)[] Triangles { get; init; }
        }

        public static Report Analyze(ModelRoot model, bool sealPlanarGaps = false)
        {
            var report = new Report();
            float posScale = 1f / WeldTolerance;

            // Planes are looked for in world space - the space a planar cut was made in. Looked for
            // even when sealing is off: a plane already sealed on an earlier run has to be
            // recognized and left alone either way, or the ordinary search would fan-cap the
            // ring's small edges over the polygon that is already there.
            var worldPositions = new PlanarCutter.WorldPositionSource(model);

            // One patch lookup per image, however many primitives/holes end up using it - rasterizing
            // an atlas's UV coverage isn't cheap, and every hole on the same textured surface can
            // safely share the same blank spot since it's meant to read as a flat, unremarkable fill.
            var patchUvByImage = new Dictionary<int, Vector2?>();
            Vector2? GetPatchUv(int imageIndex)
            {
                if (!patchUvByImage.TryGetValue(imageIndex, out var uv))
                    patchUvByImage[imageIndex] = uv = FindTexturePatchUv(model, imageIndex);
                return uv;
            }

            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                var primDatas = new PrimitiveData?[mesh.Primitives.Count];
                var results = new PrimitiveResult[mesh.Primitives.Count];

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var result = new PrimitiveResult
                    {
                        MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                        MeshIndex = meshIdx,
                        PrimitiveIndex = primIdx,
                    };
                    results[primIdx] = result;
                    report.Primitives.Add(result);
                    primDatas[primIdx] = LoadPrimitive(mesh.Primitives[primIdx], result);
                }

                // Boundary edges (in position-quantized form) across every primitive of this mesh,
                // so a seam can be told apart from a real hole. Value is the raw count of boundary
                // EDGE INSTANCES at that position, deliberately not deduplicated per primitive: a
                // UV/normal seam that duplicates vertices is very often split across two vertex-index
                // pairs INSIDE THE SAME PRIMITIVE (one per side of the seam), each independently
                // boundary by index, and both have to be counted or the seam looks exactly like a
                // hole - which is what a model with everything baked into one primitive (no
                // material split at all) actually looks like without this.
                var meshEdgeOwners = new Dictionary<EdgeKey, int>();
                var perPrimBoundaries = new List<(Edge Edge, EdgeKey Key)>[mesh.Primitives.Count];
                var instancesByKey = new Dictionary<EdgeKey, List<(int Prim, Edge Edge, int Opposite)>>();

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var data = primDatas[primIdx];
                    perPrimBoundaries[primIdx] = new List<(Edge, EdgeKey)>();
                    if (data == null) continue;

                    var boundary = FindBoundaryEdges(data, results[primIdx]);
                    foreach (var (edge, opposite) in boundary)
                    {
                        var key = new EdgeKey(
                            new PosKey(data.Positions[edge.A], posScale),
                            new PosKey(data.Positions[edge.B], posScale));
                        perPrimBoundaries[primIdx].Add((edge, key));
                        if (!instancesByKey.TryGetValue(key, out var instances)) instancesByKey[key] = instances = new();
                        instances.Add((primIdx, edge, opposite));
                    }
                }

                // Two boundary edges at the same place are a seam only if the surface continues
                // across them - their triangles on opposite sides of the edge. Two triangles on
                // the SAME side are coincident layers (a double-walled garment, a shell modelled
                // inside and out), and each of those edges is a real open edge of its own layer;
                // counting them as a seam is what left every ring on such a model unresolved.
                foreach (var (key, instances) in instancesByKey)
                {
                    int owners = instances.Count;
                    if (owners == 2 && SameSide(primDatas, instances[0], instances[1])) owners = 1;
                    meshEdgeOwners[key] = owners;
                }

                // Planar rings first: the edges they seal are taken out of the per-primitive
                // search below, which could only ever have found the parts of them that happen
                // to close inside one primitive.
                var sealedKeys = new HashSet<EdgeKey>();
                var planarAdditions = new Dictionary<int, PlanarAdditions>();
                SealPlanarGaps(mesh, primDatas, perPrimBoundaries, meshEdgeOwners, results,
                    worldPositions.For(meshIdx), GetPatchUv, sealedKeys, planarAdditions, sealPlanarGaps);

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var data = primDatas[primIdx];
                    if (data == null) continue;

                    // Only edges owned by exactly this one primitive are real holes; edges shared
                    // with a sibling primitive (owner count 2) are seams, and edges owned by 3+
                    // primitives are cross-primitive non-manifold - both are left alone.
                    var realHoleEdges = perPrimBoundaries[primIdx]
                        .Where(b => meshEdgeOwners[b.Key] == 1 && !sealedKeys.Contains(b.Key))
                        .Select(b => b.Edge)
                        .ToList();
                    int seams = perPrimBoundaries[primIdx].Count(b => meshEdgeOwners[b.Key] == 2);
                    results[primIdx].UnresolvedEdges += perPrimBoundaries[primIdx].Count(b => meshEdgeOwners[b.Key] >= 3);

                    planarAdditions.TryGetValue(primIdx, out var planar);
                    if (realHoleEdges.Count == 0 && planar == null) continue;

                    // ChainLoops below links edges purely by shared VERTEX INDEX, but a mesh that
                    // gives every triangle its own unshared vertices (no common normal/UV across
                    // faces - typical of some AI generators, and true of the primitive this was
                    // debugged against) can hand two boundary edges around the very same hole
                    // different indices at what is actually the same corner - they'd never link up.
                    // Collapsing every vertex a real-hole edge touches down to one representative
                    // index per quantized position - the same idea as BuildWeld elsewhere in this
                    // file - is what makes edges that are positionally adjacent actually chain.
                    var representative = new Dictionary<PosKey, int>();
                    int RepresentativeOf(int vertex)
                    {
                        var key = new PosKey(data.Positions[vertex], posScale);
                        if (!representative.TryGetValue(key, out var rep))
                            representative[key] = rep = vertex;
                        return rep;
                    }
                    var weldedHoleEdges = realHoleEdges.Select(e => new Edge(RepresentativeOf(e.A), RepresentativeOf(e.B))).ToList();

                    var loops = realHoleEdges.Count > 0 ? ChainLoops(weldedHoleEdges, results[primIdx]) : new List<List<int>>();
                    results[primIdx].HolesFound += loops.Count;
                    if (loops.Count == 0 && planar == null) continue;

                    var target = FindBaseColorTarget(mesh.Primitives[primIdx]);
                    Vector2? patchUv = target != null ? GetPatchUv(target.Value.ImageIndex) : null;
                    results[primIdx].UsedTexturePatch = patchUv.HasValue;

                    BuildFillPatch(data, loops, results[primIdx], target?.UvAttribute, patchUv, flatCaps: false, planar);
                    _ = seams; // seams are intentionally not reported per-primitive; see Report.HolesFound
                }
            }

            return report;
        }

        // Caps loops a caller has already found, instead of searching the model for holes. Each
        // loop is a closed ring of vertex indices into its primitive, in the direction the
        // surrounding triangles traverse those edges - the same convention Analyze's own loops
        // follow. PlanarCutter uses this for the rings its cut leaves: it knows exactly which
        // vertices they are, and finding them again by searching would be both slower and less
        // certain (it would have to tell them apart from any opening the model already had).
        //
        // flatCaps makes every cap vertex a fresh duplicate carrying the loop's own plane normal,
        // computed from its winding, rather than sharing the boundary vertices and their normals.
        // Right for a ring that really is planar - a cut - and wrong for the arbitrary, non-planar
        // holes Analyze finds, which keep the averaged normals they always had.
        public static Report CapLoops(ModelRoot model,
            IReadOnlyDictionary<(int MeshIndex, int PrimitiveIndex), List<List<int>>> loopsByPrimitive, bool flatCaps)
        {
            var report = new Report();
            var patchUvByImage = new Dictionary<int, Vector2?>();

            foreach (var ((meshIdx, primIdx), loops) in loopsByPrimitive)
            {
                if (loops.Count == 0) continue;
                var mesh = model.LogicalMeshes[meshIdx];
                var result = new PrimitiveResult
                {
                    MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                    MeshIndex = meshIdx,
                    PrimitiveIndex = primIdx,
                };
                report.Primitives.Add(result);

                var data = LoadPrimitive(mesh.Primitives[primIdx], result);
                if (data == null) continue;
                result.HolesFound = loops.Count;

                var target = FindBaseColorTarget(mesh.Primitives[primIdx]);
                Vector2? patchUv = null;
                if (target != null)
                {
                    if (!patchUvByImage.TryGetValue(target.Value.ImageIndex, out patchUv))
                        patchUvByImage[target.Value.ImageIndex] = patchUv = FindTexturePatchUv(model, target.Value.ImageIndex);
                }
                result.UsedTexturePatch = patchUv.HasValue;

                BuildFillPatch(data, loops, result, target?.UvAttribute, patchUv, flatCaps);
            }

            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.Patch == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];
                WritePatch(prim, p.Patch);
            }
        }

        private static PrimitiveData? LoadPrimitive(MeshPrimitive prim, PrimitiveResult result)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
            {
                result.SkippedReason = "not a triangle list";
                return null;
            }
            if (prim.MorphTargetsCount > 0)
            {
                result.SkippedReason = "has morph targets";
                return null;
            }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc))
            {
                result.SkippedReason = "no POSITION";
                return null;
            }

            var attributes = new Dictionary<string, IList<Vector4>>();
            foreach (var (name, accessor) in prim.VertexAccessors)
                attributes[name] = ReadAsVector4(accessor);

            var triangles = prim.GetTriangleIndices().ToArray();
            return new PrimitiveData
            {
                Prim = prim,
                Positions = posAcc.AsVector3Array(),
                Attributes = attributes,
                Triangles = triangles,
            };
        }

        // Boundary edges of one primitive, by vertex index (not yet position-quantized - that
        // happens one level up, once every primitive of the mesh has been scanned). An edge used
        // 3+ times within a single primitive is already non-manifold on its own; it's reported and
        // excluded rather than treated as a boundary.
        private static List<(Edge Edge, int Opposite)> FindBoundaryEdges(PrimitiveData data, PrimitiveResult result)
        {
            var counts = new Dictionary<Edge, (int Count, int Opposite)>();
            foreach (var (a, b, c) in data.Triangles)
            {
                CountEdge(counts, a, b, c);
                CountEdge(counts, b, c, a);
                CountEdge(counts, c, a, b);
            }

            var boundary = new List<(Edge, int)>();
            foreach (var (edge, (count, opposite)) in counts)
            {
                // Each undirected edge was recorded from exactly one winding direction per
                // triangle that uses it, so a manifold interior edge appears as a (forward, count)
                // and its reverse never gets its own entry - looking it up the other way returns 0.
                int total = count + (counts.TryGetValue(edge.Reversed, out var rev) ? rev.Count : 0);
                if (total == 1) boundary.Add((edge, opposite));
                else if (total >= 3) result.UnresolvedEdges++;
            }
            return boundary;
        }

        // Counts the edge and remembers the third corner of the (first) triangle that uses it -
        // which for a boundary edge is the only one, and is what tells a seam from a coincident
        // layer (see SameSide).
        private static void CountEdge(Dictionary<Edge, (int Count, int Opposite)> counts, int a, int b, int opposite)
        {
            var edge = new Edge(a, b);
            counts[edge] = counts.TryGetValue(edge, out var e) ? (e.Count + 1, e.Opposite) : (1, opposite);
        }

        // Whether two boundary edges at the same position have their triangles on the same side
        // of the edge: the direction from the edge to each triangle's far corner, with the
        // along-edge component removed, points the same way. Both primitives are parts of one
        // mesh, so their local positions share a space.
        private static bool SameSide(PrimitiveData?[] primDatas, (int Prim, Edge Edge, int Opposite) x, (int Prim, Edge Edge, int Opposite) y)
        {
            var dx = primDatas[x.Prim]!;
            var dy = primDatas[y.Prim]!;
            var a = dx.Positions[x.Edge.A];
            var dir = dx.Positions[x.Edge.B] - a;
            if (dir.LengthSquared() < 1e-20f) return false;
            dir = Vector3.Normalize(dir);

            Vector3 Across(Vector3 corner)
            {
                var v = corner - a;
                v -= dir * Vector3.Dot(v, dir);
                return v.LengthSquared() < 1e-20f ? Vector3.Zero : Vector3.Normalize(v);
            }

            var ax = Across(dx.Positions[x.Opposite]);
            var ay = Across(dy.Positions[y.Opposite]);
            if (ax == Vector3.Zero || ay == Vector3.Zero) return false;
            return Vector3.Dot(ax, ay) > 0.94f;   // within ~20 degrees: the same side, not a crease
        }

        // Chains boundary edges (already known to belong to exactly one hole ring across the
        // whole mesh) into closed loops by matching each edge's end vertex to the next edge's
        // start vertex. A chain that never comes back to its own start is left unresolved - forcing
        // it closed would fabricate connectivity that was not actually implied by the mesh.
        private static List<List<int>> ChainLoops(List<Edge> edges, PrimitiveResult result)
        {
            var byStart = new Dictionary<int, Edge>();
            foreach (var e in edges)
            {
                if (byStart.ContainsKey(e.A)) { result.UnresolvedEdges += 2; continue; } // branching boundary - not a simple loop
                byStart[e.A] = e;
            }

            var loops = new List<List<int>>();
            var consumed = new HashSet<int>();

            foreach (var start in byStart.Keys)
            {
                if (consumed.Contains(start)) continue;

                var loop = new List<int> { start };
                int current = start;
                bool closed = false;
                var visited = new HashSet<int> { start };

                while (byStart.TryGetValue(current, out var next))
                {
                    // Already walked from an earlier start: this chain's tail was counted then.
                    if (!consumed.Add(current)) break;
                    current = next.B;
                    if (current == start) { closed = true; break; }
                    if (!visited.Add(current)) break; // revisited a non-start vertex - malformed, bail out
                    loop.Add(current);
                }

                if (closed && loop.Count >= 3)
                {
                    loops.Add(loop);
                    consumed.Add(start);
                }
                else
                {
                    result.UnresolvedEdges += loop.Count;
                }
            }

            return loops;
        }

        // Computes the centroid-fan triangles (and, for loops of more than 3 vertices, the new
        // centroid vertex's attributes) for every loop found in one primitive, and packages the
        // whole primitive's new index buffer plus appended vertex attributes into a VertexPatch.
        //
        // patchUv, when present, is a single fixed UV coordinate inside a found blank spot of the
        // primitive's material texture (see FindTexturePatchUv) - every cap vertex's patchUvAttribute
        // component is set to exactly that value rather than derived from the loop at all, so the
        // whole cap reads as one flat, unremarkable fill regardless of what the hole's own UVs look
        // like. That only works if the cap's vertices are never also real surface vertices (they'd
        // keep their real UV too), so this path duplicates every loop vertex it uses instead of
        // reusing the originals - the one behavioral difference from the no-patch fallback below,
        // where reuse is exactly the point (nothing to duplicate for, so don't).
        //
        // flatCaps (see CapLoops) changes two things: every loop vertex is duplicated whether or
        // not there's a texture patch - the duplicates are what carry the cap's own normal, and the
        // originals keep theirs for the surface beside the cut - and that normal is the loop's
        // plane normal rather than an average of the boundary's.
        private static void BuildFillPatch(PrimitiveData data, List<List<int>> loops, PrimitiveResult result,
            string? patchUvAttribute, Vector2? patchUv, bool flatCaps = false, PlanarAdditions? planar = null)
        {
            bool usePatch = patchUv.HasValue && patchUvAttribute != null;
            bool duplicateLoop = usePatch || flatCaps;
            int baseVertexCount = data.Positions.Count;
            var appended = data.Attributes.Keys.ToDictionary(name => name, _ => new List<Vector4>());
            var newTriangles = new List<(int A, int B, int C)>();

            // Planar caps were built first and their triangles already index the vertices they
            // appended, counted from baseVertexCount - so they go in ahead of anything the loops
            // below add.
            if (planar != null)
            {
                foreach (var (name, values) in planar.Appended) appended[name].AddRange(values);
                newTriangles.AddRange(planar.Triangles);
                result.TrianglesAdded += planar.Triangles.Count;
                result.VerticesAdded += planar.Appended.Values.First().Count;
                result.HolesFilled += planar.Holes;
            }

            int AppendVertex(Func<string, Vector4> valueFor)
            {
                int newIndex = baseVertexCount + appended.Values.First().Count;
                foreach (var (name, values) in appended) values.Add(valueFor(name));
                return newIndex;
            }

            // What a duplicated loop vertex carries: its own attributes, except the UV when a
            // texture patch is in use, and the normal when the cap is flat.
            Vector4 LoopVertexValue(string name, int v, Vector3? flatNormal)
            {
                if (usePatch && name == patchUvAttribute) return new Vector4(patchUv!.Value, 0, 0);
                if (flatNormal.HasValue && name == "NORMAL") return new Vector4(flatNormal.Value, 0);
                return data.Attributes[name][v];
            }

            Vector4 CentroidValue(string name, List<int> loop, Vector3? flatNormal)
            {
                if (usePatch && name == patchUvAttribute) return new Vector4(patchUv!.Value, 0, 0);
                if (flatNormal.HasValue && name == "NORMAL") return new Vector4(flatNormal.Value, 0);

                // Joint indices identify which bones influence a vertex - they are labels, not
                // magnitudes, so averaging them across the loop would invent a bone that isn't in
                // the skin. The nearest loop vertex's joints are copied instead, paired with the
                // loop's averaged WEIGHTS_0 so the blend still sums sensibly.
                if (name == "JOINTS_0") return data.Attributes[name][loop[0]];

                var accum = Vector4.Zero;
                foreach (var v in loop) accum += data.Attributes[name][v];
                var avg = accum / loop.Count;
                if (name == "NORMAL" || name == "TANGENT")
                {
                    var n = new Vector3(avg.X, avg.Y, avg.Z);
                    avg = n.LengthSquared() > 1e-20f
                        ? new Vector4(Vector3.Normalize(n), avg.W)
                        : new Vector4(Vector3.UnitY, avg.W);
                }
                return avg;
            }

            foreach (var loop in loops)
            {
                // The loop's own plane normal, oriented the way the fan below faces - the fan runs
                // the loop backwards (see the winding note there), so the normal is negated.
                Vector3? flatNormal = flatCaps ? -LoopNormal(data.Positions, loop) : null;

                if (loop.Count == 3 && !duplicateLoop)
                {
                    // A triangular hole needs no new vertex. Reversed for the reason given at the
                    // fan below: the loop runs the way the surrounding triangles traverse it.
                    newTriangles.Add((loop[2], loop[1], loop[0]));
                    result.TrianglesAdded++;
                    result.HolesFilled++;
                    continue;
                }

                var loopVerts = duplicateLoop
                    ? loop.Select(v => AppendVertex(name => LoopVertexValue(name, v, flatNormal))).ToList()
                    : loop;
                if (duplicateLoop) result.VerticesAdded += loop.Count;

                if (loop.Count == 3)
                {
                    newTriangles.Add((loopVerts[2], loopVerts[1], loopVerts[0]));
                    result.TrianglesAdded++;
                    result.HolesFilled++;
                    continue;
                }

                int centroidIndex = AppendVertex(name => CentroidValue(name, loop, flatNormal));
                result.VerticesAdded++;

                for (int i = 0; i < loopVerts.Count; i++)
                {
                    int a = loopVerts[i];
                    int b = loopVerts[(i + 1) % loopVerts.Count];
                    // Each boundary edge (a -> b) is recorded in the direction the ONE triangle
                    // that still uses it traverses it. Two triangles sharing an edge in a
                    // consistently wound mesh traverse it in opposite directions, so the missing
                    // face - which is what the cap stands in for - has to run it b -> a. Hence
                    // (b, a, centroid): the cap faces the same way as the surface around it. The
                    // earlier (a, b, centroid) faced inward, which a double-sided material hid.
                    newTriangles.Add((b, a, centroidIndex));
                }
                result.TrianglesAdded += loopVerts.Count;
                result.HolesFilled++;
            }

            if (newTriangles.Count == 0) return;

            var newIndices = new int[(data.Triangles.Length + newTriangles.Count) * 3];
            int w = 0;
            foreach (var (a, b, c) in data.Triangles) { newIndices[w++] = a; newIndices[w++] = b; newIndices[w++] = c; }
            foreach (var (a, b, c) in newTriangles) { newIndices[w++] = a; newIndices[w++] = b; newIndices[w++] = c; }

            result.Patch = new VertexPatch
            {
                AppendedAttributes = appended.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Vector4>)kv.Value),
                NewIndices = newIndices,
            };
        }

        // What sealing planar gaps adds to one primitive: appended vertex attributes (same keys
        // as the primitive's own) and triangles over them, indexed from the primitive's current
        // vertex count.
        private sealed class PlanarAdditions
        {
            public required Dictionary<string, List<Vector4>> Appended { get; init; }
            public List<(int A, int B, int C)> Triangles { get; } = new();
            public int Holes { get; set; }
        }

        // Which plane a boundary edge lies on, and which of the mesh's parts it came from.
        private readonly record struct PlaneEdge(int Prim, Edge Edge, EdgeKey Key, Vector3 WorldA, Vector3 WorldB);

        // See the class comment. Works on one mesh: every count-1 boundary edge of every part whose
        // two ends share an X, Y or Z coordinate (in world space, within a tolerance scaled to the
        // mesh) is grouped with the others on that plane; the group is welded by position and
        // chained into rings; rings are nested into outer-with-holes sets and each set is
        // triangulated flat. The edges consumed go into sealedKeys so the ordinary per-part search
        // leaves them alone; the caps land in additions, keyed by host primitive.
        private static void SealPlanarGaps(Mesh mesh, PrimitiveData?[] primDatas,
            List<(Edge Edge, EdgeKey Key)>[] perPrimBoundaries, Dictionary<EdgeKey, int> owners,
            PrimitiveResult[] results, Func<MeshPrimitive, Vector3[]> toWorld, Func<int, Vector2?> getPatchUv,
            HashSet<EdgeKey> sealedKeys, Dictionary<int, PlanarAdditions> additions, bool seal)
        {
            var world = new Vector3[primDatas.Length][];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (int p = 0; p < primDatas.Length; p++)
            {
                if (primDatas[p] == null) continue;
                world[p] = toWorld(mesh.Primitives[p]);
                foreach (var v in world[p]) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
            }
            float diagonal = (max - min).Length();
            if (!(diagonal > 0)) return;
            float eps = MathF.Max(diagonal * 1e-4f, 1e-6f);

            // Every candidate edge, and for each axis the plane coordinate it would sit on.
            var candidates = new List<PlaneEdge>();
            for (int p = 0; p < primDatas.Length; p++)
            {
                if (primDatas[p] == null) continue;
                foreach (var (edge, key) in perPrimBoundaries[p])
                {
                    if (owners[key] != 1) continue;
                    candidates.Add(new PlaneEdge(p, edge, key, world[p][edge.A], world[p][edge.B]));
                }
            }
            if (candidates.Count < 3) return;

            // Cluster per axis by plane coordinate. An edge along an axis-aligned line sits on two
            // planes at once; it goes to whichever cluster has more company.
            var clusters = new List<(int Axis, List<int> Members)>();
            for (int axis = 0; axis < 3; axis++)
            {
                var onAxis = new List<(float C, int Index)>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    float a = Component(candidates[i].WorldA, axis), b = Component(candidates[i].WorldB, axis);
                    if (MathF.Abs(a - b) <= eps) onAxis.Add(((a + b) * 0.5f, i));
                }
                onAxis.Sort((x, y) => x.C.CompareTo(y.C));
                List<int>? current = null;
                float last = float.NaN;
                foreach (var (c, index) in onAxis)
                {
                    if (current == null || c - last > eps)
                    {
                        current = new List<int>();
                        clusters.Add((axis, current));
                    }
                    current.Add(index);
                    last = c;
                }
            }

            var bestCluster = new int[candidates.Count];
            Array.Fill(bestCluster, -1);
            for (int k = 0; k < clusters.Count; k++)
                foreach (int i in clusters[k].Members)
                    if (bestCluster[i] < 0 || clusters[bestCluster[i]].Members.Count < clusters[k].Members.Count)
                        bestCluster[i] = k;

            for (int k = 0; k < clusters.Count; k++)
            {
                var members = clusters[k].Members.Where(i => bestCluster[i] == k).ToList();
                if (members.Count < 3) continue;
                SealPlane(mesh, primDatas, candidates, members, clusters[k].Axis, results, getPatchUv, sealedKeys, additions, seal);
            }
        }

        private static float Component(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

        private static void SealPlane(Mesh mesh, PrimitiveData?[] primDatas, List<PlaneEdge> candidates, List<int> members, int axis,
            PrimitiveResult[] results, Func<int, Vector2?> getPatchUv, HashSet<EdgeKey> sealedKeys, Dictionary<int, PlanarAdditions> additions, bool seal)
        {
            float posScale = 1f / WeldTolerance;

            // Weld the ring's corners across parts by world position.
            var nodeOf = new Dictionary<PosKey, int>();
            var nodeSource = new List<(int Prim, int Vertex)>();
            var nodeWorld = new List<Vector3>();
            int NodeFor(int prim, int vertex, Vector3 w)
            {
                var key = new PosKey(w, posScale);
                if (!nodeOf.TryGetValue(key, out int id))
                {
                    id = nodeSource.Count;
                    nodeOf[key] = id;
                    nodeSource.Add((prim, vertex));
                    nodeWorld.Add(w);
                }
                return id;
            }

            // Chained UNDIRECTED, then oriented by the majority. The ring's edges each run the way
            // the one triangle still using them traverses them, which on a consistently wound
            // mesh is one way round the ring - but a mirrored part with flipped winding (a
            // character's other arm, typically) contributes edges running the other way, and a
            // directed walk simply stops at the first of those. Walking by adjacency instead gets
            // round the ring whatever each edge's direction, and the direction most edges agree
            // on is then the mesh's winding, which is what the cap has to face against.
            var adjacency = new Dictionary<int, List<(int To, int Candidate, bool Forward)>>();
            void Link(int from, int to, int candidate, bool forward)
            {
                if (!adjacency.TryGetValue(from, out var list)) adjacency[from] = list = new();
                list.Add((to, candidate, forward));
            }
            // Coincident layers put two edges between the same two corners; one ring is walked
            // and one cap built, and both edges count as sealed by it.
            var seenPairs = new Dictionary<(int, int), int>();
            var twins = new Dictionary<int, List<int>>();
            foreach (int i in members)
            {
                var e = candidates[i];
                int a = NodeFor(e.Prim, e.Edge.A, e.WorldA);
                int b = NodeFor(e.Prim, e.Edge.B, e.WorldB);
                if (a == b) continue;
                var pair = a < b ? (a, b) : (b, a);
                if (seenPairs.TryGetValue(pair, out int first))
                {
                    if (!twins.TryGetValue(first, out var list)) twins[first] = list = new();
                    list.Add(i);
                    continue;
                }
                seenPairs[pair] = i;
                Link(a, b, i, true);
                Link(b, a, i, false);
            }

            // A cap built here leaves the ring's dropped vertices as T-junctions along its long
            // edges, so on a later run those small mesh edges look open again. The cap edges
            // give it away - no mesh edge has other ring corners lying along it - and a plane
            // that already has them is left alone, its edges counted as sealed.
            {
                var nodeList = Enumerable.Range(0, nodeWorld.Count).ToList();
                float along = MathF.Max(WeldTolerance, Diagonal(nodeWorld) * 1e-4f);
                bool capped = false;
                foreach (int i in members)
                {
                    var e = candidates[i];
                    var a = e.WorldA; var b = e.WorldB;
                    var ab = b - a;
                    float lenSq = ab.LengthSquared();
                    if (lenSq <= along * along * 4) continue;
                    foreach (int n in nodeList)
                    {
                        var p = nodeWorld[n];
                        float t = Vector3.Dot(p - a, ab) / lenSq;
                        if (t <= 0.05f || t >= 0.95f) continue;
                        if (Vector3.Distance(p, a + ab * t) <= along) { capped = true; break; }
                    }
                    if (capped) break;
                }
                if (capped)
                {
                    foreach (int i in members) sealedKeys.Add(candidates[i].Key);
                    return;
                }
            }
            if (!seal) return;   // only here to recognize an existing cap; the ordinary search takes the rest

            // Walk chains. Open ones (a corner with a single edge is an end) are walked from an
            // end first, so each comes out whole rather than in the fragments an arbitrary start
            // would leave on either side of it; what's left after that is closed rings.
            var chains = new List<(List<int> Nodes, bool Closed, List<int> Walked, int Forward, int Backward)>();
            var usedEdges = new HashSet<int>();
            var starts = adjacency.Keys.Where(n => adjacency[n].Count == 1).Concat(adjacency.Keys).ToList();
            foreach (int start in starts)
            {
                if (adjacency[start].All(l => usedEdges.Contains(l.Candidate))) continue;

                var ring = new List<int> { start };
                var walked = new List<int>();
                int forward = 0, backward = 0;
                int current = start;
                bool closed = false;
                while (true)
                {
                    // Next unused edge out of here. A corner with several (a pinch, or two rings
                    // touching) is walked through on the first one found; anything it leaves
                    // behind starts its own chain later.
                    var links = adjacency[current];
                    int pick = -1;
                    for (int k = 0; k < links.Count; k++)
                        if (!usedEdges.Contains(links[k].Candidate)) { pick = k; break; }
                    if (pick < 0) break;

                    var link = links[pick];
                    usedEdges.Add(link.Candidate);
                    walked.Add(link.Candidate);
                    if (link.Forward) forward++; else backward++;
                    current = link.To;
                    if (current == start) { closed = true; break; }
                    if (ring.Contains(current)) break;
                    ring.Add(current);
                }
                if (ring.Count < 2) continue;
                chains.Add((ring, closed, walked, forward, backward));
            }

            // Join open chains whose ends nearly meet - a corner the position weld's rounding
            // grid split in two, or a hairline gap in the ring - before resorting to a chord.
            float gapTolerance = MathF.Max(WeldTolerance * 8f, (nodeWorld.Count > 0 ? Diagonal(nodeWorld) : 0f) * 1e-3f);
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < chains.Count && !merged; i++)
                {
                    if (chains[i].Closed) continue;
                    var ci = chains[i];
                    var tail = nodeWorld[ci.Nodes[^1]];
                    for (int j = 0; j < chains.Count; j++)
                    {
                        if (chains[j].Closed) continue;
                        var cj = chains[j];
                        if (i == j)
                        {
                            if (ci.Nodes.Count >= 3 && Vector3.Distance(tail, nodeWorld[ci.Nodes[0]]) <= gapTolerance)
                            {
                                chains[i] = (ci.Nodes, true, ci.Walked, ci.Forward, ci.Backward);
                                merged = true;
                                break;
                            }
                            continue;
                        }
                        bool toHead = Vector3.Distance(tail, nodeWorld[cj.Nodes[0]]) <= gapTolerance;
                        bool toTail = !toHead && Vector3.Distance(tail, nodeWorld[cj.Nodes[^1]]) <= gapTolerance;
                        if (!toHead && !toTail) continue;

                        var joined = new List<int>(ci.Nodes);
                        joined.AddRange(toHead ? cj.Nodes : Enumerable.Reverse(cj.Nodes));
                        // The other chain's direction flips with it when reversed.
                        int fwd = ci.Forward + (toHead ? cj.Forward : cj.Backward);
                        int back = ci.Backward + (toHead ? cj.Backward : cj.Forward);
                        chains[i] = (joined, false, ci.Walked.Concat(cj.Walked).ToList(), fwd, back);
                        chains.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }

            var rings = new List<List<int>>();
            int chainsClosed = 0;
            foreach (var (ring, closed, walked, forward, backward) in chains)
            {
                if (ring.Count < 3) continue;
                // An open chain on a plane is closed with the straight edge back to its start - it
                // lies in the plane too, and a slightly-off seal beats an open face.
                if (!closed) chainsClosed++;
                if (backward > forward) ring.Reverse();
                rings.Add(ring);
                foreach (int c in walked)
                {
                    sealedKeys.Add(candidates[c].Key);
                    if (twins.TryGetValue(c, out var same))
                        foreach (int t in same) sealedKeys.Add(candidates[t].Key);
                }
            }
            if (rings.Count == 0) return;

            // Only the ring's actual corners shape the cap. A cut through a building leaves the
            // ring subdivided wherever the walls' own triangles met the plane - thousands of
            // vertices along what are a handful of straight edges - and a triangulation over all
            // of them spends a triangle per vertex to draw a rectangle. Straight runs are reduced
            // to their end points (Ramer-Douglas-Peucker, at the same tolerance the plane itself
            // is matched with, so a curved ring keeps essentially all of its vertices); the mesh
            // vertices dropped still lie on the cap's edge, so nothing opens.
            float straightness = MathF.Max(WeldTolerance, Diagonal(nodeWorld) * 1e-4f);
            int cornersBefore = rings.Sum(r => r.Count);
            for (int i = 0; i < rings.Count; i++) rings[i] = SimplifyRing(rings[i], nodeWorld, straightness);
            rings.RemoveAll(r => r.Count < 3);
            if (rings.Count == 0) return;
            results[Array.FindIndex(primDatas, d => d != null)].PlanarCornersDropped += cornersBefore - rings.Sum(r => r.Count);

            // Project onto the plane for the 2D work: the two axes that aren't the plane's.
            int ua = axis == 0 ? 1 : 0, va = axis == 2 ? 1 : 2;
            Vector2 Flat(int node) => new(Component(nodeWorld[node], ua), Component(nodeWorld[node], va));
            var flat = rings.Select(r => (IReadOnlyList<Vector2>)r.Select(Flat).ToList()).ToList();

            // Nesting depth: a ring inside an odd number of others is a hole in the innermost of
            // them; inside an even number, it's an outer ring of its own.
            var depth = new int[rings.Count];
            var parent = new int[rings.Count];
            Array.Fill(parent, -1);
            for (int i = 0; i < rings.Count; i++)
            {
                int best = -1;
                for (int j = 0; j < rings.Count; j++)
                {
                    if (i == j || !PointInPolygon(flat[j], flat[i][0])) continue;
                    depth[i]++;
                    if (best < 0 || Area(flat[j]) < Area(flat[best])) best = j;   // innermost container
                }
                parent[i] = best;
            }

            for (int outer = 0; outer < rings.Count; outer++)
            {
                if (depth[outer] % 2 != 0) continue;
                var holes = Enumerable.Range(0, rings.Count).Where(h => parent[h] == outer && depth[h] % 2 == 1).ToList();

                var triangles = PolygonTriangulator.Triangulate(flat[outer], holes.Select(h => flat[h]).ToList());
                if (triangles == null || triangles.Count == 0) continue;

                // All the ring vertices this cap uses, in the order the triangulator indexes them.
                var nodes = new List<int>(rings[outer]);
                foreach (int h in holes) nodes.AddRange(rings[h]);

                // Host: the part contributing most of the ring. Corners from other parts get their
                // non-positional attributes (skinning above all) from the nearest host corner.
                int host = nodes.GroupBy(n => nodeSource[n].Prim).OrderByDescending(g => g.Count()).First().Key;
                var hostData = primDatas[host]!;
                var hostCorners = nodes.Where(n => nodeSource[n].Prim == host).ToList();
                int DonorFor(int node)
                {
                    if (nodeSource[node].Prim == host) return nodeSource[node].Vertex;
                    int nearest = hostCorners[0];
                    float bestSq = float.MaxValue;
                    foreach (int c in hostCorners)
                    {
                        float d = Vector3.DistanceSquared(nodeWorld[c], nodeWorld[node]);
                        if (d < bestSq) { bestSq = d; nearest = c; }
                    }
                    return nodeSource[nearest].Vertex;
                }
                Vector3 Local(int node) => primDatas[nodeSource[node].Prim]!.Positions[nodeSource[node].Vertex];

                // The cap faces the way the missing surface would: the ring runs the way the
                // remaining triangles traverse it, so the cap runs it the other way - hence the
                // negated Newell normal, exactly as the flat cut caps do.
                var capNormal = -LoopNormal(rings[outer].Select(Local).ToList(), Enumerable.Range(0, rings[outer].Count).ToList());

                var target = FindBaseColorTarget(mesh.Primitives[host]);
                Vector2? patchUv = target != null ? getPatchUv(target.Value.ImageIndex) : null;
                results[host].UsedTexturePatch |= patchUv.HasValue;

                if (!additions.TryGetValue(host, out var add))
                    additions[host] = add = new PlanarAdditions { Appended = hostData.Attributes.Keys.ToDictionary(n => n, _ => new List<Vector4>()) };

                int baseIndex = hostData.Positions.Count + add.Appended.Values.First().Count;
                foreach (int node in nodes)
                {
                    int donor = DonorFor(node);
                    foreach (var (name, values) in add.Appended)
                    {
                        Vector4 value;
                        if (name == "POSITION") value = new Vector4(Local(node), 0);
                        else if (name == "NORMAL") value = new Vector4(capNormal, 0);
                        else if (patchUv.HasValue && name == target!.Value.UvAttribute) value = new Vector4(patchUv.Value, 0, 0);
                        else value = hostData.Attributes[name][donor];
                        values.Add(value);
                    }
                }

                foreach (var (a, b, c) in triangles)
                {
                    int ia = baseIndex + a, ib = baseIndex + b, ic = baseIndex + c;
                    var pa = Local(nodes[a]); var pb = Local(nodes[b]); var pc = Local(nodes[c]);
                    bool flip = Vector3.Dot(Vector3.Cross(pb - pa, pc - pa), capNormal) < 0;
                    add.Triangles.Add(flip ? (ia, ic, ib) : (ia, ib, ic));
                }

                add.Holes += 1 + holes.Count;
                results[host].HolesFound += 1 + holes.Count;
                results[host].PlanarHolesSealed += 1 + holes.Count;
            }

            // Attributed to whichever part hosted the most rings on this plane; it's a count for
            // the summary line, not something per-part anything acts on.
            if (chainsClosed > 0 && additions.Count > 0)
                results[additions.Keys.First()].PlanarChainsClosed += chainsClosed;
        }

        // Ramer-Douglas-Peucker on a closed ring: split at the vertex farthest from the first,
        // simplify both halves as open polylines, join. Keeps the ring's end points and every
        // vertex that deviates from the straight line between kept neighbours by more than tol.
        private static List<int> SimplifyRing(List<int> ring, List<Vector3> world, float tol)
        {
            if (ring.Count <= 3) return ring;

            int far = 0;
            float farSq = -1;
            for (int i = 1; i < ring.Count; i++)
            {
                float d = Vector3.DistanceSquared(world[ring[0]], world[ring[i]]);
                if (d > farSq) { farSq = d; far = i; }
            }

            var kept = new List<int>();
            SimplifyOpen(ring, 0, far, world, tol, kept);
            SimplifyOpen(ring, far, ring.Count, world, tol, kept);   // wraps back to ring[0]

            // The two split anchors were kept unconditionally; if either sits on a straight run
            // it goes now, and so does anything that becomes collinear once a neighbour has gone.
            bool changed = true;
            while (changed && kept.Count > 3)
            {
                changed = false;
                for (int i = 0; i < kept.Count && kept.Count > 3; i++)
                {
                    var a = world[kept[(i + kept.Count - 1) % kept.Count]];
                    var b = world[kept[(i + 1) % kept.Count]];
                    var p = world[kept[i]];
                    var ab = b - a;
                    float lenSq = ab.LengthSquared();
                    float t = lenSq > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f) : 0f;
                    if (Vector3.Distance(p, a + ab * t) <= tol) { kept.RemoveAt(i); changed = true; i--; }
                }
            }
            return kept;
        }

        // Appends the simplified vertices of ring[from..to] (to == ring.Count meaning ring[0]),
        // excluding the end vertex, which the next segment's call appends as its start.
        private static void SimplifyOpen(List<int> ring, int from, int to, List<Vector3> world, float tol, List<int> kept)
        {
            var a = world[ring[from]];
            var b = world[ring[to % ring.Count]];
            var ab = b - a;
            float abLenSq = ab.LengthSquared();

            int worst = -1;
            float worstDist = tol;
            for (int i = from + 1; i < to; i++)
            {
                var p = world[ring[i]];
                float t = abLenSq > 0 ? Math.Clamp(Vector3.Dot(p - a, ab) / abLenSq, 0f, 1f) : 0f;
                float d = Vector3.Distance(p, a + ab * t);
                if (d > worstDist) { worstDist = d; worst = i; }
            }

            if (worst < 0) { kept.Add(ring[from]); return; }
            SimplifyOpen(ring, from, worst, world, tol, kept);
            SimplifyOpen(ring, worst, to, world, tol, kept);
        }

        private static float Diagonal(IList<Vector3> points)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (max - min).Length();
        }

        private static float Area(IReadOnlyList<Vector2> ring)
        {
            float area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                area += a.X * b.Y - b.X * a.Y;
            }
            return MathF.Abs(area) * 0.5f;
        }

        private static bool PointInPolygon(IReadOnlyList<Vector2> ring, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i]; var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        // Newell's method: the normal of a polygon from its vertices alone, robust to the odd
        // non-convex or slightly non-planar loop, with the sign following the loop's winding by
        // the right-hand rule - the same rule the fan triangles (a, b, centroid) obey.
        private static Vector3 LoopNormal(IList<Vector3> positions, List<int> loop)
        {
            var n = Vector3.Zero;
            for (int i = 0; i < loop.Count; i++)
            {
                var a = positions[loop[i]];
                var b = positions[loop[(i + 1) % loop.Count]];
                n.X += (a.Y - b.Y) * (a.Z + b.Z);
                n.Y += (a.Z - b.Z) * (a.X + b.X);
                n.Z += (a.X - b.X) * (a.Y + b.Y);
            }
            return n.LengthSquared() > 1e-20f ? Vector3.Normalize(n) : Vector3.UnitY;
        }

        // The image and TEXCOORD_n accessor a primitive's cap should be textured from, if its
        // material binds a BaseColor texture on a UV set the primitive actually carries.
        private static (int ImageIndex, string UvAttribute)? FindBaseColorTarget(MeshPrimitive prim)
        {
            var ch = prim.Material?.FindChannel("BaseColor");
            if (ch is not { } channel) return null;
            var image = channel.Texture?.PrimaryImage;
            if (image == null) return null;

            string uvAttribute = $"TEXCOORD_{channel.TextureCoordinate}";
            if (!prim.VertexAccessors.ContainsKey(uvAttribute)) return null;

            return (image.LogicalIndex, uvAttribute);
        }

        // A fixed, low resolution for the free-space search regardless of the texture's real size -
        // finding a patch a few dozen texels across doesn't need per-texel precision, and capping
        // the grid this way keeps the integral-image tables below small and fast even for a 4K atlas.
        private const int PatchSearchResolution = 256;
        private const int PatchBlockSize = 8;
        private const int PatchMargin = 2;

        private sealed class ImageCoverage
        {
            public required int Width { get; init; }
            public required int Height { get; init; }

            // (Width+1)*(Height+1) summed-area tables - CoveredSum counts UV-mapped texels;
            // R/G/B(Sq)Sum let ComputeVariance read a block's color mean/variance in O(1).
            public required long[] CoveredSum { get; init; }
            public required long[] RSum { get; init; }
            public required long[] GSum { get; init; }
            public required long[] BSum { get; init; }
            public required long[] RSqSum { get; init; }
            public required long[] GSqSum { get; init; }
            public required long[] BSqSum { get; init; }
        }

        // Finds one blank-ish spot in an image's UV atlas and returns it as a UV coordinate, or null
        // if the image can't be read or nothing suitable was found (fully packed, or too small).
        // Reuses TextureAtlasUtil's own UV-footprint rasterizer, so "unused" here means exactly
        // what it means there: no triangle in the whole model, not just this primitive, maps to it -
        // which is what keeps a cap from being pointed at a patch some other, unrelated part of the
        // model is already relying on.
        private static Vector2? FindTexturePatchUv(ModelRoot model, int imageIndex)
        {
            var coverage = BuildImageCoverage(model, imageIndex);
            if (coverage == null) return null;

            var patch = FindFreePatch(coverage);
            if (patch == null) return null;

            return new Vector2(
                (patch.Value.X + PatchBlockSize / 2f) / coverage.Width,
                (patch.Value.Y + PatchBlockSize / 2f) / coverage.Height);
        }

        private static ImageCoverage? BuildImageCoverage(ModelRoot model, int imageIndex)
        {
            byte[] bytes;
            try { bytes = model.LogicalImages[imageIndex].Content.Content.ToArray(); }
            catch { return null; }

            Bitmap fullBitmap;
            try { fullBitmap = TextureAtlasUtil.LoadBitmap(bytes); }
            catch { return null; }

            using (fullBitmap)
            {
                int required = PatchBlockSize + PatchMargin * 2;
                int width = Math.Min(PatchSearchResolution, fullBitmap.Width);
                int height = Math.Min(PatchSearchResolution, fullBitmap.Height);
                if (width < required || height < required) return null;

                var tris = TextureAtlasUtil.GatherTriangles(model, imageIndex, width, height);
                var positions = new Vector3[width * height];
                var covered = new bool[width * height];
                var texelSize = new float[width * height];
                TextureAtlasUtil.RasterizeTriangles(tris, width, height, positions, covered, texelSize);

                byte[] pixels;
                using (var resized = new Bitmap(width, height))
                {
                    using var g = Graphics.FromImage(resized);
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(fullBitmap, new Rectangle(0, 0, width, height));
                    pixels = TextureAtlasUtil.ReadPixels(resized);
                }

                int stride = width + 1;
                var coveredSum = new long[stride * (height + 1)];
                var rSum = new long[stride * (height + 1)];
                var gSum = new long[stride * (height + 1)];
                var bSum = new long[stride * (height + 1)];
                var rSqSum = new long[stride * (height + 1)];
                var gSqSum = new long[stride * (height + 1)];
                var bSqSum = new long[stride * (height + 1)];

                for (int y = 0; y < height; y++)
                {
                    long rowCov = 0, rowR = 0, rowG = 0, rowB = 0, rowRSq = 0, rowGSq = 0, rowBSq = 0;
                    for (int x = 0; x < width; x++)
                    {
                        int i = y * width + x;
                        byte b = pixels[i * 4 + 0], g = pixels[i * 4 + 1], r = pixels[i * 4 + 2];
                        rowCov += covered[i] ? 1 : 0;
                        rowR += r; rowG += g; rowB += b;
                        rowRSq += (long)r * r; rowGSq += (long)g * g; rowBSq += (long)b * b;

                        int rowIdx = (y + 1) * stride + (x + 1);
                        int aboveIdx = y * stride + (x + 1);
                        coveredSum[rowIdx] = coveredSum[aboveIdx] + rowCov;
                        rSum[rowIdx] = rSum[aboveIdx] + rowR;
                        gSum[rowIdx] = gSum[aboveIdx] + rowG;
                        bSum[rowIdx] = bSum[aboveIdx] + rowB;
                        rSqSum[rowIdx] = rSqSum[aboveIdx] + rowRSq;
                        gSqSum[rowIdx] = gSqSum[aboveIdx] + rowGSq;
                        bSqSum[rowIdx] = bSqSum[aboveIdx] + rowBSq;
                    }
                }

                return new ImageCoverage
                {
                    Width = width, Height = height,
                    CoveredSum = coveredSum, RSum = rSum, GSum = gSum, BSum = bSum,
                    RSqSum = rSqSum, GSqSum = gSqSum, BSqSum = bSqSum,
                };
            }
        }

        private static long RectSum(long[] table, int stride, int x0, int y0, int x1, int y1) =>
            table[y1 * stride + x1] - table[y0 * stride + x1] - table[y1 * stride + x0] + table[y0 * stride + x0];

        // Scans every possible block position for one with zero UV coverage across a margin-padded
        // area (so bilinear/mip sampling at render time can't pull in real surface color from just
        // outside the block) and picks whichever is closest to a flat color - the surest sign
        // nothing important was ever baked there. Full scan rather than a coarse stride: at
        // PatchSearchResolution this is at most 256*256 candidates, each an O(1) table lookup.
        private static (int X, int Y)? FindFreePatch(ImageCoverage cov)
        {
            int checkSize = PatchBlockSize + PatchMargin * 2;
            int stride = cov.Width + 1;
            if (cov.Width < checkSize || cov.Height < checkSize) return null;

            (int X, int Y)? best = null;
            double bestVariance = double.MaxValue;

            for (int y = 0; y + checkSize <= cov.Height; y++)
            {
                for (int x = 0; x + checkSize <= cov.Width; x++)
                {
                    if (RectSum(cov.CoveredSum, stride, x, y, x + checkSize, y + checkSize) != 0) continue;

                    int innerX = x + PatchMargin, innerY = y + PatchMargin;
                    double variance = ComputeVariance(cov, innerX, innerY);
                    if (variance >= bestVariance) continue;

                    bestVariance = variance;
                    best = (innerX, innerY);
                    if (bestVariance < 4.0) return best; // near-flat colour - good enough, stop searching
                }
            }

            return best;
        }

        private static double ComputeVariance(ImageCoverage cov, int x0, int y0)
        {
            int stride = cov.Width + 1;
            int x1 = x0 + PatchBlockSize, y1 = y0 + PatchBlockSize;
            long n = (long)PatchBlockSize * PatchBlockSize;

            double meanR = RectSum(cov.RSum, stride, x0, y0, x1, y1) / (double)n;
            double meanG = RectSum(cov.GSum, stride, x0, y0, x1, y1) / (double)n;
            double meanB = RectSum(cov.BSum, stride, x0, y0, x1, y1) / (double)n;
            double varR = RectSum(cov.RSqSum, stride, x0, y0, x1, y1) / (double)n - meanR * meanR;
            double varG = RectSum(cov.GSqSum, stride, x0, y0, x1, y1) / (double)n - meanG * meanG;
            double varB = RectSum(cov.BSqSum, stride, x0, y0, x1, y1) / (double)n - meanB * meanB;
            return varR + varG + varB;
        }

        // Internal so PlanarCutter can write its own VertexPatch (appended plane vertices plus the
        // clipped index buffer) through exactly the same path a fill uses.
        internal static void WritePatch(MeshPrimitive prim, VertexPatch patch)
        {
            foreach (var (name, accessor) in prim.VertexAccessors)
            {
                var existing = ReadAsVector4(accessor);
                var combined = new List<Vector4>(existing.Count + patch.AppendedAttributes[name].Count);
                combined.AddRange(existing);
                combined.AddRange(patch.AppendedAttributes[name]);
                WriteAttribute(prim, name, accessor, combined);
            }

            prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, patch.NewIndices);
        }

        // Reads any vertex accessor into a uniform Vector4 form regardless of its actual
        // dimensionality, so hole-filling's averaging/appending logic doesn't need a case per
        // attribute type. The lost dimensions are always zero and are never read back for them
        // (WriteAttribute is told the original accessor's shape and only reads the components it
        // originally had).
        //
        // Internal rather than private because GeometryHistory captures and restores vertex data in
        // exactly this form - a fill is the one operation in the optimizer that changes it, so the
        // encoding round-trip it needs is the one already written here.
        internal static IList<Vector4> ReadAsVector4(Accessor accessor)
        {
            switch (accessor.Dimensions)
            {
                case DimensionType.VEC2:
                    return accessor.AsVector2Array().Select(v => new Vector4(v, 0, 0)).ToList();
                case DimensionType.VEC3:
                    return accessor.AsVector3Array().Select(v => new Vector4(v, 0)).ToList();
                case DimensionType.VEC4:
                    return accessor.AsVector4Array();
                case DimensionType.SCALAR:
                    return accessor.AsScalarArray().Select(v => new Vector4(v, 0, 0, 0)).ToList();
                default:
                    throw new NotSupportedException($"Unsupported accessor dimension {accessor.Dimensions}");
            }
        }

        // Writes values back in the original accessor's dimensionality and encoding. Integer
        // encodings (JOINTS_0 is UNSIGNED_BYTE or UNSIGNED_SHORT in every real file) are re-encoded
        // byte for byte rather than through the float helpers, because writing them as floats
        // would produce a glTF that violates the spec and that runtimes reject - same reasoning as
        // GeometryOptimizer.BuildCompactedRewrite.
        internal static void WriteAttribute(MeshPrimitive prim, string name, Accessor source, IReadOnlyList<Vector4> values)
        {
            if (source.Encoding == EncodingType.FLOAT)
            {
                switch (source.Dimensions)
                {
                    case DimensionType.VEC2:
                        prim.WithVertexAccessor(name, values.Select(v => new Vector2(v.X, v.Y)).ToList());
                        return;
                    case DimensionType.VEC3:
                        prim.WithVertexAccessor(name, values.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList());
                        return;
                    case DimensionType.VEC4:
                        prim.WithVertexAccessor(name, values.ToList());
                        return;
                    case DimensionType.SCALAR:
                        prim.WithVertexAccessor(name, values.Select(v => v.X).ToList());
                        return;
                    default:
                        throw new NotSupportedException($"Unsupported accessor dimension {source.Dimensions}");
                }
            }

            if (source.Dimensions != DimensionType.VEC4)
                throw new NotSupportedException($"Unsupported integer-encoded dimension {source.Dimensions} for {name}");

            int elementBytes = source.Encoding switch
            {
                EncodingType.UNSIGNED_BYTE => 1,
                EncodingType.UNSIGNED_SHORT => 2,
                EncodingType.UNSIGNED_INT => 4,
                _ => throw new NotSupportedException($"Unsupported integer encoding {source.Encoding} for {name}"),
            };

            var bytes = new byte[values.Count * 4 * elementBytes];
            int offset = 0;
            foreach (var v in values)
            {
                foreach (var component in new[] { v.X, v.Y, v.Z, v.W })
                {
                    uint raw = (uint)MathF.Round(component);
                    for (int b = 0; b < elementBytes; b++)
                        bytes[offset + b] = (byte)(raw >> (8 * b));   // glTF buffers are little-endian
                    offset += elementBytes;
                }
            }

            var info = new SharpGLTF.Memory.MemoryAccessInfo(name, 0, values.Count, 0,
                DimensionType.VEC4, source.Encoding, source.Normalized);
            prim.WithVertexAccessor(new SharpGLTF.Memory.MemoryAccessor(new ArraySegment<byte>(bytes), info));
        }
    }
}
