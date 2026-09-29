using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Puts the original triangles back under a painted region of a simplified model, so the
    // texture there is exactly what it was, and re-simplifies everything else to about the density
    // it already had.
    //
    // This is the tool for what UvWarpRepair can't reach. A window frame, a logo, lettering - any
    // texture detail finer than the triangles simplification left under it - can't be put right by
    // moving three UVs per triangle; the mapping the original had across that patch simply doesn't
    // exist on the coarse mesh any more. The only real fix is to have the original triangles back
    // there, and spend the polygon budget elsewhere.
    //
    // Doing that by splicing original triangles into the current mesh is the obvious route and it
    // doesn't work: the edge of the fine patch is a zig-zag of original vertices, the coarse
    // triangles around it have straight edges between survivors, and the two don't meet. Zipping
    // that seam by hand is exactly the kind of mesh surgery that goes wrong at every T-junction. So
    // instead the whole primitive is re-simplified from the ORIGINAL triangles (History entry 0),
    // with the ones under the paint protected the same way the pane's own EXCEPT mode protects a
    // painted region - passed through untouched, their vertices locked - and with the target set to
    // the number of triangles the unpainted part has right now. meshopt is what stitches the
    // transition, edge collapse by edge collapse, and it does that correctly by construction.
    //
    // Protected alongside the paint is every original triangle that is still present, verbatim, in
    // the current mesh outside it. Re-simplifying from the original would otherwise re-collapse
    // whatever an earlier restore brought back (and whatever a previous EXCEPT pass protected, and
    // whatever meshopt simply chose to leave), so a second restore would undo the first. With
    // those held, the only thing that changes is the painted area and the transition around it;
    // the target for the rest is the number of coarse triangles it has now, so it lands at the
    // same density it already had.
    //
    // The paint is on the current mesh and has to be read against the original one. Each original
    // triangle's centroid is projected onto the painted current triangles (UvWarpRepair's
    // ReferenceSurface, built over just those); one that lands within tolerance is under the paint.
    //
    // "Original" here means the oldest History state whose vertices are the ones the model holds
    // now. A simplify pass and the texture-warp fix rewrite indices and UVs only, so after any
    // number of them the true original (entry 0) still applies; a delete, cut or fill renumbers
    // the vertices, and from then on the furthest back a triangle can be fetched from is the state
    // that operation left - its triangles are the finest ones that still address these vertices.
    // GeometryHistory.OldestEntrySharingCurrentVertices picks that entry, and the report names it
    // so the pane can say what the detail was restored from.
    public static class RegionRestorer
    {
        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }

            public int TrianglesBefore { get; set; }
            public int TrianglesAfter { get; set; }

            /// <summary>Current triangles under the paint - what gets replaced.</summary>
            public int PaintedCurrent { get; set; }

            /// <summary>Original triangles under the paint - what they get replaced with.</summary>
            public int PaintedOriginal { get; set; }

            /// <summary>Original triangles already present outside the paint (earlier restores, unsimplified areas) - protected, not re-simplified.</summary>
            public int OriginalKept { get; set; }

            public string? SkippedReason { get; set; }

            public int TrianglesAdded => TrianglesAfter - TrianglesBefore;
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();

            /// <summary>History entry the triangles came from - the oldest whose vertices are the model's current ones.</summary>
            public int ReferenceIndex { get; init; }
            public string ReferenceLabel { get; init; } = "";

            /// <summary>True when that entry is the current one - nothing has been simplified since the vertices last changed.</summary>
            public bool NothingSimplifiedSince { get; init; }

            /// <summary>
            /// The restored triangles' indices in the NEW index buffer, per primitive that changed -
            /// what the paint should become after Apply, so the region stays selected and the next
            /// operation can be run on it without painting it again.
            /// </summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> NewSelection { get; } = new();

            /// <summary>Original triangles outside the paint that were already present and are held as they are.</summary>
            public int OriginalKept => Primitives.Sum(p => p.OriginalKept);

            // Analyze fills this; Apply writes it. Only primitives with paint are in play.
            internal GeometryOptimizer.Report? Simplify { get; set; }

            public bool HasChanges => Simplify != null && Simplify.HasChanges;
            public int TrianglesBefore => Primitives.Sum(p => p.TrianglesBefore);
            public int TrianglesAfter => Primitives.Sum(p => p.TrianglesAfter);
            public int TrianglesAdded => TrianglesAfter - TrianglesBefore;
            public int PaintedCurrent => Primitives.Sum(p => p.PaintedCurrent);
            public int PaintedOriginal => Primitives.Sum(p => p.PaintedOriginal);
            public float WorstError => Simplify?.WorstError ?? 0;
        }

        // How far an original triangle's centroid may sit off the painted current triangles and
        // still count as under them, as a fraction of the primitive's bounding diagonal. The gap
        // is the simplification error itself, which the pane keeps small.
        private const float PaintTolerance = 0.02f;

        public static Report Analyze(ModelRoot model, GeometryHistory history,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            GeometryOptimizer.SimplifyOptions baseOptions)
        {
            int referenceIndex = history.OldestEntrySharingCurrentVertices();
            var report = new Report
            {
                ReferenceIndex = referenceIndex,
                ReferenceLabel = history.Entries[referenceIndex].Label,
                NothingSimplifiedSince = referenceIndex == history.CurrentIndex,
            };
            if (report.NothingSimplifiedSince) return report;

            var original = history.GetReferenceGeometry(referenceIndex);

            var sources = new Dictionary<(int, int), int[]>();
            var protectedTris = new Dictionary<(int, int), HashSet<int>>();
            var targets = new Dictionary<(int, int), int>();
            var underPaintByPrim = new Dictionary<(int, int), HashSet<int>>();

            int flat = 0;
            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var prim = mesh.Primitives[primIdx];
                    var reference = flat < original.Count ? original[flat] : null;
                    flat++;

                    if (!selection.TryGetValue((meshIdx, primIdx), out var painted) || painted.Count == 0) continue;

                    var result = new PrimitiveResult
                    {
                        MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                        MeshIndex = meshIdx,
                        PrimitiveIndex = primIdx,
                    };
                    report.Primitives.Add(result);

                    if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) { result.SkippedReason = "not a triangle list"; continue; }
                    if (reference == null) { result.SkippedReason = "no earlier geometry to restore from"; continue; }
                    if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) { result.SkippedReason = "no POSITION"; continue; }

                    // Can't happen given how the reference entry is chosen; kept as a guard, since
                    // re-simplifying against mismatched vertices would produce garbage silently.
                    var positions = posAcc.AsVector3Array();
                    if (!SamePositions(positions, reference.Positions))
                    {
                        result.SkippedReason = $"vertices differ from \"{report.ReferenceLabel}\"";
                        continue;
                    }

                    var current = prim.GetTriangleIndices().ToArray();
                    result.TrianglesBefore = current.Length;
                    result.TrianglesAfter = current.Length;

                    var paintedCurrent = painted.Where(t => t >= 0 && t < current.Length).ToList();
                    result.PaintedCurrent = paintedCurrent.Count;
                    if (paintedCurrent.Count == 0) { result.SkippedReason = "paint doesn't land on this part"; continue; }

                    var underPaint = OriginalTrianglesUnderPaint(current, paintedCurrent, positions, reference);
                    result.PaintedOriginal = underPaint.Count;
                    if (underPaint.Count == 0) { result.SkippedReason = "no original triangles found under the paint"; continue; }

                    // Nothing to restore when the paint already covers only original detail - every
                    // painted current triangle is an original one. (Matched on the triangle, not its
                    // index, since the two buffers are ordered differently.)
                    var originalSet = new HashSet<(int, int, int)>();
                    for (int i = 0; i < reference.Indices.Length; i += 3)
                        originalSet.Add((reference.Indices[i], reference.Indices[i + 1], reference.Indices[i + 2]));
                    if (underPaint.Count == paintedCurrent.Count && paintedCurrent.All(t => originalSet.Contains(current[t])))
                    {
                        result.SkippedReason = "painted area is already at original detail";
                        continue;
                    }

                    // Original triangles still present outside the paint are held exactly as they
                    // are (see the class comment) - which is what keeps an earlier restore intact.
                    var paintedSet = new HashSet<int>(paintedCurrent);
                    var unpaintedCurrent = new HashSet<(int, int, int)>();
                    for (int t = 0; t < current.Length; t++)
                        if (!paintedSet.Contains(t)) unpaintedCurrent.Add(current[t]);

                    var protectedOriginals = new HashSet<int>(underPaint);
                    int kept = 0;
                    for (int i = 0, t = 0; i < reference.Indices.Length; i += 3, t++)
                    {
                        if (!unpaintedCurrent.Contains((reference.Indices[i], reference.Indices[i + 1], reference.Indices[i + 2]))) continue;
                        if (protectedOriginals.Add(t)) kept++;
                    }
                    result.OriginalKept = kept;

                    sources[(meshIdx, primIdx)] = reference.Indices;
                    protectedTris[(meshIdx, primIdx)] = protectedOriginals;
                    underPaintByPrim[(meshIdx, primIdx)] = underPaint;
                    // The unpainted part keeps its present originals as they are, so what meshopt is
                    // handed should come out at the number of coarse triangles that part has now.
                    targets[(meshIdx, primIdx)] = Math.Max(1, current.Length - paintedCurrent.Count - kept);
                }
            }

            if (sources.Count == 0) return report;

            var options = new GeometryOptimizer.SimplifyOptions
            {
                TargetRatio = baseOptions.TargetRatio,
                TargetError = baseOptions.TargetError,
                LockBorders = baseOptions.LockBorders,
                SkinTolerance = baseOptions.SkinTolerance,
                OptimizeVertexOrder = baseOptions.OptimizeVertexOrder,
                WeldTolerance = baseOptions.WeldTolerance,
                UvTolerance = baseOptions.UvTolerance,
                NormalWeight = baseOptions.NormalWeight,
                UvWeight = baseOptions.UvWeight,
                SourceIndices = sources,
                Selection = protectedTris,
                InvertSelection = true,
                TargetTriangles = targets,
            };

            var simplify = GeometryOptimizer.Analyze(model, options);
            report.Simplify = simplify;

            foreach (var result in report.Primitives)
            {
                if (result.SkippedReason != null) continue;
                var s = simplify.Primitives.First(p => p.MeshIndex == result.MeshIndex && p.PrimitiveIndex == result.PrimitiveIndex);
                if (s.NewIndices == null)
                {
                    result.SkippedReason = s.SkippedReason ?? "simplifier produced no result";
                    continue;
                }
                result.TrianglesAfter = s.TrianglesAfter;

                // The protected originals rode through as the passthrough block; the ones under
                // the paint are the restored region, at whatever index they landed on.
                var underPaint = underPaintByPrim[(result.MeshIndex, result.PrimitiveIndex)];
                var restored = new HashSet<int>();
                var passthrough = s.PassthroughSourceIndices;
                for (int k = 0; k < passthrough.Length; k++)
                    if (underPaint.Contains(passthrough[k])) restored.Add(s.PassthroughStart + k);
                report.NewSelection[(result.MeshIndex, result.PrimitiveIndex)] = restored;
            }

            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            if (report.Simplify != null) GeometryOptimizer.Apply(report.Simplify, model);
        }

        private static bool SamePositions(IList<Vector3> current, IReadOnlyList<Vector3> original)
        {
            if (current.Count != original.Count) return false;
            for (int i = 0; i < current.Count; i++)
                if (current[i] != original[i]) return false;
            return true;
        }

        // Which original triangles sit under the painted current ones: each original centroid is
        // projected onto a surface built from ONLY the painted current triangles, and counts if it
        // lands within tolerance. The painted current triangles themselves are original vertices
        // over the original surface, so an original triangle they cover projects onto them at
        // roughly the simplification error, and one outside them lands on nothing nearer than its
        // own distance to the paint's edge.
        private static HashSet<int> OriginalTrianglesUnderPaint((int A, int B, int C)[] current, List<int> paintedCurrent,
            IList<Vector3> positions, GeometryHistory.ReferencePrimitive reference)
        {
            var flat = new int[paintedCurrent.Count * 3];
            for (int i = 0; i < paintedCurrent.Count; i++)
            {
                var (a, b, c) = current[paintedCurrent[i]];
                flat[i * 3] = a; flat[i * 3 + 1] = b; flat[i * 3 + 2] = c;
            }

            var surface = new UvWarpRepair.ReferenceSurface(new GeometryHistory.ReferencePrimitive
            {
                Indices = flat,
                Positions = positions.ToList(),
                UvSets = new Dictionary<string, IReadOnlyList<Vector2>>(),
            });

            // Tolerance against the whole primitive's size, not the painted patch's - a small
            // patch on a large mesh would otherwise get a tolerance smaller than the collapse error.
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float tolerance = PaintTolerance * (max - min).Length();

            var under = new HashSet<int>();
            var idx = reference.Indices;
            var refPos = reference.Positions;
            for (int t = 0; t < idx.Length / 3; t++)
            {
                var centroid = (refPos[idx[t * 3]] + refPos[idx[t * 3 + 1]] + refPos[idx[t * 3 + 2]]) / 3f;
                if (surface.ClosestPoint(centroid, tolerance, out _, out _, out _)) under.Add(t);
            }
            return under;
        }
    }
}
