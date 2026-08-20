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
    // filling a hole adds vertices, so the snapshot/restore pair here has to carry full vertex
    // accessor contents for every primitive touched, not just its indices.
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
            public bool HasChanges => Primitives.Any(p => p.Patch != null);
        }

        // Snapshot of everything Apply is about to touch, captured up front so Restore can put it
        // back exactly as it was.
        public sealed class Snapshot
        {
            public sealed class PrimitiveState
            {
                public required int MeshIndex { get; init; }
                public required int PrimitiveIndex { get; init; }
                public required Dictionary<string, Accessor> OriginalAccessorSources { get; init; }
                public required Dictionary<string, IReadOnlyList<Vector4>> OriginalAttributes { get; init; }
                public required int[] OriginalIndices { get; init; }
            }

            public List<PrimitiveState> Primitives { get; } = new();
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

        public static Report Analyze(ModelRoot model)
        {
            var report = new Report();
            float posScale = 1f / WeldTolerance;

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

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var data = primDatas[primIdx];
                    perPrimBoundaries[primIdx] = new List<(Edge, EdgeKey)>();
                    if (data == null) continue;

                    var boundary = FindBoundaryEdges(data, results[primIdx]);
                    foreach (var edge in boundary)
                    {
                        var key = new EdgeKey(
                            new PosKey(data.Positions[edge.A], posScale),
                            new PosKey(data.Positions[edge.B], posScale));
                        perPrimBoundaries[primIdx].Add((edge, key));
                        meshEdgeOwners[key] = meshEdgeOwners.GetValueOrDefault(key) + 1;
                    }
                }

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var data = primDatas[primIdx];
                    if (data == null) continue;

                    // Only edges owned by exactly this one primitive are real holes; edges shared
                    // with a sibling primitive (owner count 2) are seams, and edges owned by 3+
                    // primitives are cross-primitive non-manifold - both are left alone.
                    var realHoleEdges = perPrimBoundaries[primIdx]
                        .Where(b => meshEdgeOwners[b.Key] == 1)
                        .Select(b => b.Edge)
                        .ToList();
                    int seams = perPrimBoundaries[primIdx].Count(b => meshEdgeOwners[b.Key] == 2);
                    results[primIdx].UnresolvedEdges += perPrimBoundaries[primIdx].Count(b => meshEdgeOwners[b.Key] >= 3);

                    if (realHoleEdges.Count == 0) continue;

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

                    var loops = ChainLoops(weldedHoleEdges, results[primIdx]);
                    results[primIdx].HolesFound = loops.Count;
                    if (loops.Count == 0) continue;

                    var target = FindBaseColorTarget(mesh.Primitives[primIdx]);
                    Vector2? patchUv = target != null ? GetPatchUv(target.Value.ImageIndex) : null;
                    results[primIdx].UsedTexturePatch = patchUv.HasValue;

                    BuildFillPatch(data, loops, results[primIdx], target?.UvAttribute, patchUv);
                    _ = seams; // seams are intentionally not reported per-primitive; see Report.HolesFound
                }
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

        public static Snapshot TakeSnapshot(Report report, ModelRoot model)
        {
            var snapshot = new Snapshot();
            foreach (var p in report.Primitives)
            {
                if (p.Patch == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];

                var sources = new Dictionary<string, Accessor>();
                var values = new Dictionary<string, IReadOnlyList<Vector4>>();
                foreach (var (name, accessor) in prim.VertexAccessors)
                {
                    sources[name] = accessor;
                    values[name] = (IReadOnlyList<Vector4>)ReadAsVector4(accessor);
                }

                snapshot.Primitives.Add(new Snapshot.PrimitiveState
                {
                    MeshIndex = p.MeshIndex,
                    PrimitiveIndex = p.PrimitiveIndex,
                    OriginalAccessorSources = sources,
                    OriginalAttributes = values,
                    OriginalIndices = prim.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray(),
                });
            }
            return snapshot;
        }

        public static void Restore(ModelRoot model, Snapshot snapshot)
        {
            foreach (var state in snapshot.Primitives)
            {
                var prim = model.LogicalMeshes[state.MeshIndex].Primitives[state.PrimitiveIndex];
                foreach (var (name, values) in state.OriginalAttributes)
                    WriteAttribute(prim, name, state.OriginalAccessorSources[name], values);
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, state.OriginalIndices);
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
        private static List<Edge> FindBoundaryEdges(PrimitiveData data, PrimitiveResult result)
        {
            var counts = new Dictionary<Edge, int>();
            foreach (var (a, b, c) in data.Triangles)
            {
                CountEdge(counts, a, b);
                CountEdge(counts, b, c);
                CountEdge(counts, c, a);
            }

            var boundary = new List<Edge>();
            foreach (var (edge, count) in counts)
            {
                // Each undirected edge was recorded from exactly one winding direction per
                // triangle that uses it, so a manifold interior edge appears as a (forward, count)
                // and its reverse never gets its own entry - looking it up the other way returns 0.
                int total = count + counts.GetValueOrDefault(edge.Reversed);
                if (total == 1) boundary.Add(edge);
                else if (total >= 3) result.UnresolvedEdges++;
            }
            return boundary;
        }

        private static void CountEdge(Dictionary<Edge, int> counts, int a, int b)
        {
            var edge = new Edge(a, b);
            counts[edge] = counts.GetValueOrDefault(edge) + 1;
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
                    consumed.Add(current);
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
        private static void BuildFillPatch(PrimitiveData data, List<List<int>> loops, PrimitiveResult result,
            string? patchUvAttribute, Vector2? patchUv)
        {
            bool usePatch = patchUv.HasValue && patchUvAttribute != null;
            int baseVertexCount = data.Positions.Count;
            var appended = data.Attributes.Keys.ToDictionary(name => name, _ => new List<Vector4>());
            var newTriangles = new List<(int A, int B, int C)>();

            int AppendVertex(Func<string, Vector4> valueFor)
            {
                int newIndex = baseVertexCount + appended.Values.First().Count;
                foreach (var (name, values) in appended) values.Add(valueFor(name));
                return newIndex;
            }

            Vector4 CentroidValue(string name, List<int> loop)
            {
                if (usePatch && name == patchUvAttribute) return new Vector4(patchUv!.Value, 0, 0);

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
                if (loop.Count == 3 && !usePatch)
                {
                    // A triangular hole needs no new vertex - the loop direction is the boundary's
                    // own winding, which already points the cap the right way.
                    newTriangles.Add((loop[0], loop[1], loop[2]));
                    result.TrianglesAdded++;
                    result.HolesFilled++;
                    continue;
                }

                var loopVerts = usePatch
                    ? loop.Select(v => AppendVertex(name =>
                        name == patchUvAttribute ? new Vector4(patchUv!.Value, 0, 0) : data.Attributes[name][v])).ToList()
                    : loop;
                if (usePatch) result.VerticesAdded += loop.Count;

                if (loop.Count == 3)
                {
                    newTriangles.Add((loopVerts[0], loopVerts[1], loopVerts[2]));
                    result.TrianglesAdded++;
                    result.HolesFilled++;
                    continue;
                }

                int centroidIndex = AppendVertex(name => CentroidValue(name, loop));
                result.VerticesAdded++;

                for (int i = 0; i < loopVerts.Count; i++)
                {
                    int a = loopVerts[i];
                    int b = loopVerts[(i + 1) % loopVerts.Count];
                    // Boundary edge (a -> b) already runs the direction the missing face's winding
                    // must continue in, so the fan triangle is (a, b, centroid) - not the reverse.
                    newTriangles.Add((a, b, centroidIndex));
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

        private static void WritePatch(MeshPrimitive prim, VertexPatch patch)
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
        private static IList<Vector4> ReadAsVector4(Accessor accessor)
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
        private static void WriteAttribute(MeshPrimitive prim, string name, Accessor source, IReadOnlyList<Vector4> values)
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
