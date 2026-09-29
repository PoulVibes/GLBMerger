using System;
using System.Collections.Generic;
using System.Linq;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Removes painted triangles from the merged model outright, and then the vertices they were the
    // last triangle to reference.
    //
    // The third operation in GeometryOptimizerEditor, alongside simplification and hole filling, and
    // the only one that takes geometry away rather than rearranging it: simplification collapses
    // triangles onto vertices that are already there and hole filling adds caps, but this leaves a
    // hole where the paint was. It is the tool for the parts that shouldn't be in the merge at all -
    // an interior surface no camera ever sees, a prop's stray backing plane, decals hidden under
    // other geometry - where reducing the triangle count is beside the point.
    //
    // Deletion happens in two halves, and the second is what the vertex numbers in the report are
    // about. Rewriting the index buffer without the painted triangles removes them from the model,
    // but every vertex they used stays in the vertex buffer at full size unless something goes and
    // renumbers what's left - so each touched primitive is handed to GeometryOptimizer's compaction
    // straight afterwards. That also sweeps up vertices stranded earlier by simplification passes
    // over the same primitive, which is why the vertices dropped can exceed what this deletion
    // alone stranded.
    //
    // A primitive whose triangles are ALL painted is left alone rather than emptied. glTF has no
    // representation for a primitive with no triangles (an accessor's count has to be at least one),
    // and removing the primitive outright would mean restructuring the mesh, which the whole editor
    // is built on not doing - every mode here, and GeometryHistory with it, addresses primitives by
    // position. Dropping a whole mesh part belongs at merge time, not here.
    public static class TriangleDeleter
    {
        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }

            public int TrianglesBefore { get; set; }
            public int TrianglesDeleted { get; set; }
            public int VerticesBefore { get; set; }

            /// <summary>Vertices compaction will drop afterwards - 0 when it can't run here.</summary>
            public int VerticesDropped { get; set; }

            /// <summary>Set when nothing is deleted from this primitive, and why.</summary>
            public string? SkippedReason { get; set; }

            /// <summary>Every triangle here was painted, so it was left alone - see the class comment.</summary>
            public bool FullyPainted { get; set; }

            /// <summary>Set when triangles go but their vertices have to stay, and why.</summary>
            public string? VerticesKeptReason { get; set; }

            public int TrianglesAfter => TrianglesBefore - TrianglesDeleted;

            // Filled in by Analyze, consumed by Apply - still in the primitive's CURRENT vertex
            // numbering, because compaction is what renumbers, and it runs after this is written.
            internal int[]? NewIndices { get; set; }
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();

            public int TrianglesBefore => Primitives.Sum(p => p.TrianglesBefore);
            public int TrianglesDeleted => Primitives.Sum(p => p.TrianglesDeleted);
            public int VerticesDropped => Primitives.Sum(p => p.VerticesDropped);
            public double PercentDeleted => TrianglesBefore == 0 ? 0 : 100.0 * TrianglesDeleted / TrianglesBefore;

            public bool HasChanges => Primitives.Any(p => p.NewIndices != null);

            /// <summary>Mesh parts left alone because every one of their triangles was painted.</summary>
            public int PrimitivesFullyPainted => Primitives.Count(p => p.FullyPainted);

            public int PrimitivesKeepingVertices => Primitives.Count(p => p.VerticesKeptReason != null);
        }

        // Dry run: works out what would go without touching the model, so the count can be read
        // before anything is committed. Selection is keyed the way the paint tool reports it -
        // triangle indices within a primitive, in GetTriangleIndices order.
        public static Report Analyze(ModelRoot model, Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection)
        {
            var report = new Report();

            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var mesh = model.LogicalMeshes[meshIdx];
                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var result = new PrimitiveResult
                    {
                        MeshName = mesh.Name ?? $"mesh_{meshIdx}",
                        MeshIndex = meshIdx,
                        PrimitiveIndex = primIdx,
                    };
                    report.Primitives.Add(result);

                    selection.TryGetValue((meshIdx, primIdx), out var painted);
                    AnalyzePrimitive(mesh.Primitives[primIdx], painted, result);
                }
            }

            return report;
        }

        private static void AnalyzePrimitive(MeshPrimitive prim, HashSet<int>? painted, PrimitiveResult result)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
            {
                result.SkippedReason = "not a triangle list";
                return;
            }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc))
            {
                result.SkippedReason = "no POSITION";
                return;
            }

            var triangles = prim.GetTriangleIndices().ToArray();
            result.TrianglesBefore = triangles.Length;
            result.VerticesBefore = posAcc.Count;

            if (painted == null || painted.Count == 0)
            {
                result.SkippedReason = "nothing painted here";
                return;
            }

            var kept = new List<int>(Math.Max(0, triangles.Length - painted.Count) * 3);
            int deleted = 0;
            for (int i = 0; i < triangles.Length; i++)
            {
                if (painted.Contains(i)) { deleted++; continue; }
                kept.Add(triangles[i].A);
                kept.Add(triangles[i].B);
                kept.Add(triangles[i].C);
            }

            if (deleted == 0)
            {
                result.SkippedReason = "nothing painted here";
                return;
            }

            if (kept.Count == 0)
            {
                // See the class comment: emptying a primitive would mean removing it, and nothing
                // in this editor restructures the model.
                result.SkippedReason = "every triangle painted - the whole mesh part would be gone";
                result.FullyPainted = true;
                return;
            }

            result.TrianglesDeleted = deleted;
            result.NewIndices = kept.ToArray();

            if (!GeometryOptimizer.CanCompactPrimitive(prim))
            {
                result.VerticesKeptReason = prim.MorphTargetsCount > 0
                    ? "has morph targets"
                    : "unfamiliar vertex encoding";
                return;
            }

            // What compaction will find once the painted triangles are gone: everything the
            // surviving triangles no longer reach, including whatever earlier passes over this
            // primitive already stranded.
            var used = new bool[posAcc.Count];
            foreach (int index in kept)
                if (index >= 0 && index < used.Length) used[index] = true;

            int survivors = 0;
            foreach (bool u in used) if (u) survivors++;
            result.VerticesDropped = posAcc.Count - survivors;
        }

        // Writes whatever Analyze computed. Order matters: the new index buffer has to land before
        // compaction runs, because compaction reads the triangles back off the primitive to decide
        // which vertices are still reachable and to renumber them.
        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.NewIndices == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];

                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, p.NewIndices);
                GeometryOptimizer.CompactPrimitive(prim);
            }
        }
    }
}
