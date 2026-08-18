using System;
using System.Collections.Generic;
using System.Linq;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Reorders each primitive's triangles and vertex records into the layout a GPU actually wants
    // to read them in. Nothing here changes what the model looks like - the same triangles are
    // drawn over the same vertex data - only the order the data sits in memory changes.
    //
    // Three meshoptimizer passes, in the order meshoptimizer's own documentation requires (each
    // one assumes the previous has already run):
    //
    //  1. Vertex cache. Reorders triangles so a vertex is reused while it is still in the GPU's
    //     post-transform cache, instead of being transformed again a few thousand triangles later.
    //     Measured as ACMR - average cache misses per triangle - which this reports before/after.
    //  2. Overdraw. Reorders those triangles again into rough front-to-back clusters, so a fragment
    //     that is going to be hidden is more often rejected by early-Z before its texture fetches
    //     and shading are paid for. This is the pass that most directly buys back texture bandwidth
    //     on a heavily textured model.
    //  3. Vertex fetch. Rewrites the vertex arrays so records are stored in the order the final
    //     index buffer reaches them, which turns scattered vertex reads into sequential ones. This
    //     is the only pass that touches vertex data, and it also drops any vertex no triangle
    //     references any more.
    //
    // Pass 2 reorders triangles, which is visible on BLEND materials, where draw order decides the
    // composite. Those primitives get passes 1 and 3 only - see the guard in OptimizePrimitive.
    public static class MeshLayoutOptimizer
    {
        public sealed class Options
        {
            /// <summary>
            /// How much vertex-cache efficiency the overdraw pass may trade away. 1.0 = none,
            /// 1.05 = up to 5% worse ACMR, which is meshoptimizer's suggested default.
            /// </summary>
            public float OverdrawThreshold { get; init; } = 1.05f;
        }

        public sealed class Report
        {
            public int PrimitivesOptimized { get; set; }
            public int PrimitivesSkipped { get; set; }
            public int TrianglesReordered { get; set; }
            public int VerticesReordered { get; set; }
            public int VerticesDropped { get; set; }
            public int PrimitivesKeptInDrawOrder { get; set; }

            /// <summary>
            /// Primitives where the overdraw reordering was computed, found not to reduce overdraw
            /// enough to justify the vertex-cache efficiency it costs, and discarded in favour of
            /// the cache ordering. High on convex, single-layered models, which is normal.
            /// </summary>
            public int OverdrawPassRejected { get; set; }

            // Triangle-weighted so a 200k-triangle primitive is not averaged against a 12-triangle
            // one - the whole-model number has to move with the geometry that dominates the frame.
            //
            // Three ACMR readings rather than two, because the two passes pull it in opposite
            // directions and a single before/after hides that: the cache pass lowers it, then the
            // overdraw pass is deliberately allowed to give some of that back (see
            // Options.OverdrawThreshold). Reporting only the endpoints makes a run that traded
            // correctly look like a run that regressed.
            public double AcmrBefore { get; set; }
            public double AcmrAfterCache { get; set; }
            public double AcmrAfter { get; set; }

            // The other half of that trade. 1.0 means every covered pixel is shaded exactly once;
            // higher means hidden fragments are being shaded and thrown away, texture fetches and all.
            public double OverdrawBefore { get; set; }
            public double OverdrawAfter { get; set; }

            public bool ChangedAnything => PrimitivesOptimized > 0;
        }

        public static Report Optimize(ModelRoot model, Options options)
        {
            var report = new Report();
            double weightedBefore = 0, weightedCache = 0, weightedAfter = 0;
            double weightedOverdrawBefore = 0, weightedOverdrawAfter = 0;
            long totalTriangles = 0;

            foreach (var mesh in model.LogicalMeshes)
            {
                foreach (var prim in mesh.Primitives)
                {
                    var result = OptimizePrimitive(prim, options);
                    if (result == null) { report.PrimitivesSkipped++; continue; }

                    report.PrimitivesOptimized++;
                    report.TrianglesReordered += result.TriangleCount;
                    report.VerticesReordered += result.VertexCount;
                    report.VerticesDropped += result.VerticesDropped;
                    if (result.KeptDrawOrder) report.PrimitivesKeptInDrawOrder++;
                    else if (!result.TookOverdrawOrder) report.OverdrawPassRejected++;

                    weightedBefore += result.AcmrBefore * result.TriangleCount;
                    weightedCache += result.AcmrAfterCache * result.TriangleCount;
                    weightedAfter += result.AcmrAfter * result.TriangleCount;
                    weightedOverdrawBefore += result.OverdrawBefore * result.TriangleCount;
                    weightedOverdrawAfter += result.OverdrawAfter * result.TriangleCount;
                    totalTriangles += result.TriangleCount;
                }
            }

            if (totalTriangles > 0)
            {
                report.AcmrBefore = weightedBefore / totalTriangles;
                report.AcmrAfterCache = weightedCache / totalTriangles;
                report.AcmrAfter = weightedAfter / totalTriangles;
                report.OverdrawBefore = weightedOverdrawBefore / totalTriangles;
                report.OverdrawAfter = weightedOverdrawAfter / totalTriangles;
            }

            // Vertex fetch replaces every vertex accessor, and SharpGLTF has no way to release the
            // ones it replaced - the same situation GeometryOptimizer.CompactUnusedVertices
            // documents. Packing here keeps the in-session model coherent; the dead views are
            // reclaimed by the cleanup MainForm already offers at save time.
            if (report.ChangedAnything) model.MergeBuffers();

            return report;
        }

        private sealed class PrimitiveResult
        {
            public int TriangleCount;
            public int VertexCount;
            public int VerticesDropped;
            public bool KeptDrawOrder;
            public bool TookOverdrawOrder;
            public double AcmrBefore;
            public double AcmrAfterCache;
            public double AcmrAfter;
            public double OverdrawBefore;
            public double OverdrawAfter;
        }

        private static PrimitiveResult? OptimizePrimitive(MeshPrimitive prim, Options options)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) return null;
            if (prim.MorphTargetsCount > 0) return null;   // morph deltas are indexed too; out of scope
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) return null;

            int vertexCount = posAcc.Count;
            var triangles = prim.GetTriangleIndices().ToArray();
            if (triangles.Length == 0 || vertexCount == 0) return null;

            var indices = new uint[triangles.Length * 3];
            for (int i = 0; i < triangles.Length; i++)
            {
                indices[i * 3 + 0] = (uint)triangles[i].A;
                indices[i * 3 + 1] = (uint)triangles[i].B;
                indices[i * 3 + 2] = (uint)triangles[i].C;
            }

            var result = new PrimitiveResult
            {
                TriangleCount = triangles.Length,
                VertexCount = vertexCount,
                AcmrBefore = MeasureAcmr(indices),
            };

            // Positions as a flat float array for the overdraw pass, which needs them to sort
            // clusters front-to-back.
            var positions = posAcc.AsVector3Array();
            var flatPositions = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                var p = positions[i];
                flatPositions[i * 3 + 0] = p.X;
                flatPositions[i * 3 + 1] = p.Y;
                flatPositions[i * 3 + 2] = p.Z;
            }

            // Blended materials composite in draw order, so reclustering their triangles would
            // change the rendered result. Cache and fetch order are invisible to the blender.
            bool blended = prim.Material?.Alpha == AlphaMode.BLEND;
            result.KeptDrawOrder = blended;

            var cacheOrder = new uint[indices.Length];
            var overdrawOrder = new uint[indices.Length];
            var remap = new uint[vertexCount];
            nuint survivors;
            double acmrAfterCache;
            bool tookOverdrawOrder = false;

            unsafe
            {
                fixed (uint* src = indices)
                fixed (uint* cache = cacheOrder)
                fixed (uint* over = overdrawOrder)
                fixed (uint* remapPtr = remap)
                fixed (float* pos = flatPositions)
                {
                    result.OverdrawBefore = MeshoptNative.AnalyzeOverdraw(
                        src, (nuint)indices.Length, pos, (nuint)vertexCount, sizeof(float) * 3).Overdraw;

                    MeshoptNative.OptimizeVertexCache(cache, src, (nuint)indices.Length, (nuint)vertexCount);
                    acmrAfterCache = MeasureAcmr(cacheOrder);

                    // The overdraw pass is run into its own buffer and then judged, rather than
                    // accepted blindly. It always costs vertex-cache efficiency (that is what
                    // OverdrawThreshold budgets for), but on a mesh that barely overdraws in the
                    // first place - anything convex and single-layered - there is nothing for it to
                    // win, so the cost buys nothing. Keeping the cache ordering in that case is
                    // strictly better, so the trade has to pay for itself to be taken.
                    uint* chosen = cache;
                    double overdrawChosen = MeshoptNative.AnalyzeOverdraw(
                        cache, (nuint)indices.Length, pos, (nuint)vertexCount, sizeof(float) * 3).Overdraw;

                    if (!blended)
                    {
                        MeshoptNative.OptimizeOverdraw(over, cache, (nuint)indices.Length,
                            pos, (nuint)vertexCount, sizeof(float) * 3, options.OverdrawThreshold);

                        double candidate = MeshoptNative.AnalyzeOverdraw(
                            over, (nuint)indices.Length, pos, (nuint)vertexCount, sizeof(float) * 3).Overdraw;

                        // A relative margin, not an absolute one: the analyzer rasterizes from a
                        // handful of fixed directions, so sub-percent differences are its own noise
                        // rather than a real win.
                        if (candidate <= overdrawChosen * (1.0 - MinOverdrawGain))
                        {
                            chosen = over;
                            overdrawChosen = candidate;
                            tookOverdrawOrder = true;
                        }
                    }

                    result.OverdrawAfter = overdrawChosen;

                    survivors = MeshoptNative.OptimizeVertexFetchRemap(
                        remapPtr, chosen, (nuint)indices.Length, (nuint)vertexCount);

                    MeshoptNative.RemapIndexBuffer(chosen, chosen, (nuint)indices.Length, remapPtr);
                }
            }

            var reordered = tookOverdrawOrder ? overdrawOrder : cacheOrder;
            result.TookOverdrawOrder = tookOverdrawOrder;

            int newVertexCount = (int)survivors;
            result.VerticesDropped = vertexCount - newVertexCount;
            result.AcmrAfterCache = acmrAfterCache;
            result.AcmrAfter = MeasureAcmr(reordered);

            // meshoptimizer hands back old -> new; permuting the attribute arrays needs the inverse.
            // Unreferenced vertices map to ~0u and simply never claim a slot.
            var newToOld = new int[newVertexCount];
            for (int old = 0; old < vertexCount; old++)
            {
                uint slot = remap[old];
                if (slot == uint.MaxValue) continue;
                if (slot >= (uint)newVertexCount) return null;   // not the table we assumed; leave this primitive alone
                newToOld[slot] = old;
            }

            // Everything is decoded up front: writing an accessor can rearrange the underlying
            // buffers, so reading one attribute after another has been rewritten is not safe.
            // (Same constraint as GeometryOptimizer.CompactPrimitive.)
            var rewrites = new List<Action>();
            foreach (var (name, accessor) in prim.VertexAccessors)
            {
                if (accessor.Count != vertexCount) return null;   // attribute does not line up with POSITION
                var rewrite = BuildPermutedRewrite(prim, name, accessor, newToOld);
                if (rewrite == null) return null;                 // unfamiliar encoding - leave this primitive alone
                rewrites.Add(rewrite);
            }

            foreach (var rewrite in rewrites) rewrite();

            var newIndices = new int[reordered.Length];
            for (int i = 0; i < reordered.Length; i++) newIndices[i] = (int)reordered[i];
            prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, newIndices);

            return result;
        }

        // Reads one attribute now and returns the deferred write. Float attributes go back through
        // the typed helper; integer ones (JOINTS_0 is UNSIGNED_BYTE or UNSIGNED_SHORT in every real
        // file) have to be re-encoded byte for byte, because writing them as floats would produce a
        // glTF that violates the spec and that runtimes reject.
        private static Action? BuildPermutedRewrite(MeshPrimitive prim, string name, Accessor accessor, int[] newToOld)
        {
            if (accessor.Encoding == EncodingType.FLOAT)
            {
                switch (accessor.Dimensions)
                {
                    case DimensionType.VEC2:
                        var vec2 = Permute(accessor.AsVector2Array(), newToOld);
                        return () => prim.WithVertexAccessor(name, vec2);
                    case DimensionType.VEC3:
                        var vec3 = Permute(accessor.AsVector3Array(), newToOld);
                        return () => prim.WithVertexAccessor(name, vec3);
                    case DimensionType.VEC4:
                        var vec4 = Permute(accessor.AsVector4Array(), newToOld);
                        return () => prim.WithVertexAccessor(name, vec4);
                    case DimensionType.SCALAR:
                        var scalars = Permute(accessor.AsScalarArray(), newToOld);
                        return () => prim.WithVertexAccessor(name, scalars);
                    default:
                        return null;
                }
            }

            if (accessor.Dimensions != DimensionType.VEC4) return null;

            int elementBytes = accessor.Encoding switch
            {
                EncodingType.UNSIGNED_BYTE => 1,
                EncodingType.UNSIGNED_SHORT => 2,
                EncodingType.UNSIGNED_INT => 4,
                _ => 0,
            };
            if (elementBytes == 0) return null;

            var values = Permute(accessor.AsVector4Array(), newToOld);
            var encoding = accessor.Encoding;
            bool normalized = accessor.Normalized;

            return () =>
            {
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
                    DimensionType.VEC4, encoding, normalized);
                prim.WithVertexAccessor(new SharpGLTF.Memory.MemoryAccessor(new ArraySegment<byte>(bytes), info));
            };
        }

        private static List<T> Permute<T>(IList<T> source, int[] newToOld)
        {
            var permuted = new List<T>(newToOld.Length);
            foreach (int old in newToOld) permuted.Add(source[old]);
            return permuted;
        }

        // ACMR against a plain 16-entry FIFO post-transform cache. That is not any real GPU - modern
        // hardware batches differently - but it is the model meshopt_optimizeVertexCache targets, so
        // it is the right yardstick for reporting what the pass bought, and simulating it here
        // avoids a struct-returning P/Invoke for the sake of one number.
        private const int CacheSize = 16;

        /// <summary>
        /// Relative overdraw reduction the overdraw ordering has to deliver before its vertex-cache
        /// cost is accepted. 1% clears the analyzer's own sampling noise without discarding real wins.
        /// </summary>
        private const double MinOverdrawGain = 0.01;

        private static double MeasureAcmr(uint[] indices)
        {
            if (indices.Length == 0) return 0;

            var cache = new uint[CacheSize];
            for (int i = 0; i < CacheSize; i++) cache[i] = uint.MaxValue;

            int next = 0, misses = 0;
            foreach (uint index in indices)
            {
                bool hit = false;
                for (int i = 0; i < CacheSize; i++)
                {
                    if (cache[i] == index) { hit = true; break; }
                }
                if (hit) continue;

                misses++;
                cache[next] = index;
                next = (next + 1) % CacheSize;
            }

            return misses / (double)(indices.Length / 3);
        }
    }
}
