using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Polygon-count reduction that runs on the already-merged model, in place (same pattern as
    // every other post-merge edit in this app - whatever it changes is what Save writes out).
    //
    // Reduction is done entirely by meshoptimizer's quadric edge-collapse simplifier. An earlier
    // version of this file also carried a hand-written "planar merge" pass that combined coplanar
    // triangles under an angle tolerance; it was removed because meshopt does the same job far
    // better. Planar merging can only reclaim genuinely flat, affinely-unwrapped patches whose rim
    // nothing else references, which on real assets is very little - it managed 7.8% on a fence
    // prop and 3.3% on a character, where meshopt reaches whatever ratio it is asked for. Nothing
    // is lost by dropping it: a near-lossless pass here is simply a high keep ratio with a small
    // target error.
    //
    // Simplification only ever produces a new INDEX buffer over the existing vertices - meshopt
    // collapses edges onto vertices that are already there and never invents one - so every
    // surviving vertex keeps its exact original position, UV, normal and skin binding. That is what
    // makes this safe to apply to a skinned character and cheap to undo (see SnapshotIndices).
    public static class GeometryOptimizer
    {
        public sealed class SimplifyOptions
        {
            /// <summary>Fraction of the current triangle count to aim for (0.5 = half).</summary>
            public float TargetRatio { get; set; } = 0.5f;

            /// <summary>Error budget, relative to the mesh's own scale. Hit before the ratio, simplification stops early.</summary>
            public float TargetError { get; set; } = 0.01f;

            /// <summary>Keeps open edges / mesh outlines pinned where they are.</summary>
            public bool LockBorders { get; set; } = true;

            /// <summary>0 pins every vertex whose skin weights differ from its neighbours, so deformation cannot change; 1 ignores skinning.</summary>
            public float SkinTolerance { get; set; }

            /// <summary>Reorders the result for post-transform vertex cache locality. Changes no triangle counts.</summary>
            public bool OptimizeVertexOrder { get; set; } = true;

            public float WeldTolerance { get; set; } = 1e-6f;
            public float UvTolerance { get; set; } = 1e-4f;

            /// <summary>How hard meshopt tries to preserve normals and UVs relative to position error.</summary>
            public float NormalWeight { get; set; } = 0.2f;
            public float UvWeight { get; set; } = 1.0f;
        }

        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }
            public int TrianglesBefore { get; set; }
            public int TrianglesAfter { get; set; }
            public string? SkippedReason { get; set; }

            /// <summary>Error meshopt actually landed on, relative to the mesh scale.</summary>
            public float SimplifyError { get; set; }

            // Filled in by Analyze, consumed by Apply - null when this primitive is unchanged.
            public int[]? NewIndices { get; set; }

            public int TrianglesSaved => TrianglesBefore - TrianglesAfter;
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();
            public int TrianglesBefore => Primitives.Sum(p => p.TrianglesBefore);
            public int TrianglesAfter => Primitives.Sum(p => p.TrianglesAfter);
            public int TrianglesSaved => TrianglesBefore - TrianglesAfter;
            public double PercentSaved => TrianglesBefore == 0 ? 0 : 100.0 * TrianglesSaved / TrianglesBefore;
            public float WorstError => Primitives.Count == 0 ? 0 : Primitives.Max(p => p.SimplifyError);
            public bool HasChanges => Primitives.Any(p => p.NewIndices != null);
        }

        // Dry run: computes the new index buffer for every primitive without touching the model, so
        // the UI can show before/after counts and let the settings be dialled against real numbers
        // before anything is committed.
        public static Report Analyze(ModelRoot model, SimplifyOptions options)
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
                    AnalyzePrimitive(mesh.Primitives[primIdx], options, result);
                }
            }

            return report;
        }

        // Writes whatever Analyze computed. Only the index accessor is rewritten - vertex data is
        // left exactly as it was, which is what makes this safe for skinned meshes and cheap to
        // undo (see SnapshotIndices).
        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.NewIndices == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, p.NewIndices);
            }
        }

        // Captures every primitive's current index buffer so an in-session "Revert" can put the
        // geometry back - the model is shared with the other editor modes and there's no other
        // copy of it anywhere.
        public static List<int[]> SnapshotIndices(ModelRoot model) =>
            model.LogicalMeshes
                .SelectMany(m => m.Primitives)
                .Select(p => p.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray())
                .ToList();

        public static void RestoreIndices(ModelRoot model, List<int[]> snapshot)
        {
            int i = 0;
            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (i >= snapshot.Count) break;
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, snapshot[i++]);
            }
        }

        // Drops vertices that no longer appear in any triangle and rewrites every vertex accessor
        // without them, preserving each attribute's original encoding (JOINTS_0 in particular has
        // to stay UNSIGNED_BYTE/SHORT; writing it as float produces a glTF runtimes reject).
        // Returns how many vertices were removed.
        //
        // NOT currently exposed in the editor, and deliberately so. It does compact the mesh
        // correctly - verified at 53,117 -> 24,433 vertices with joint encodings and weights intact
        // - but SharpGLTF has no way to release the accessors it replaces: WithVertexAccessor
        // creates new ones and the old buffer views stay in LogicalAccessors, so MergeBuffers packs
        // both copies and the saved GLB comes out LARGER than it went in. Reclaiming them needs the
        // whole model rebuilt (SceneBuilder.CreateFrom(scene).ToGltf2() does drop them, ~48.4MB vs
        // 50.0MB on the test character), which produces a new ModelRoot rather than editing this
        // one in place - so it needs the shared model reference swapped across every editor mode,
        // plus proof that the hand-authored BallAnchor/StiffArm marker nodes survive the rebuild.
        // That belongs in its own change; this is left here as the piece it will build on.
        public static int CompactUnusedVertices(ModelRoot model)
        {
            int removed = 0;
            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                    removed += CompactPrimitive(prim);

            if (removed > 0) model.MergeBuffers();
            return removed;
        }

        private static int CompactPrimitive(MeshPrimitive prim)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) return 0;
            if (prim.MorphTargetsCount > 0) return 0;   // morph deltas are indexed too; out of scope
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) return 0;

            int vertexCount = posAcc.Count;
            var triangles = prim.GetTriangleIndices().ToArray();

            var used = new bool[vertexCount];
            foreach (var (a, b, c) in triangles) { used[a] = true; used[b] = true; used[c] = true; }

            var remap = new int[vertexCount];
            int survivors = 0;
            for (int i = 0; i < vertexCount; i++)
                remap[i] = used[i] ? survivors++ : -1;

            if (survivors == vertexCount) return 0;

            // Everything is decoded up front: writing an accessor can rearrange the underlying
            // buffers, so reading one attribute after another has been rewritten is not safe.
            var rewrites = new List<Action>();
            foreach (var (name, accessor) in prim.VertexAccessors)
            {
                var rewrite = BuildCompactedRewrite(prim, name, accessor, used, survivors);
                if (rewrite == null) return 0;   // unfamiliar encoding - leave this primitive alone
                rewrites.Add(rewrite);
            }

            foreach (var rewrite in rewrites) rewrite();

            var newIndices = new int[triangles.Length * 3];
            for (int i = 0; i < triangles.Length; i++)
            {
                newIndices[i * 3 + 0] = remap[triangles[i].A];
                newIndices[i * 3 + 1] = remap[triangles[i].B];
                newIndices[i * 3 + 2] = remap[triangles[i].C];
            }
            prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, newIndices);

            return vertexCount - survivors;
        }

        // Reads one attribute now and returns the deferred write. Float attributes go back through
        // the typed helper; integer ones (JOINTS_0 is UNSIGNED_BYTE or UNSIGNED_SHORT in every real
        // file) have to be re-encoded byte for byte, because writing them as floats would produce a
        // glTF that violates the spec and that runtimes reject.
        private static Action? BuildCompactedRewrite(MeshPrimitive prim, string name, Accessor accessor,
            bool[] used, int survivors)
        {
            if (accessor.Encoding == EncodingType.FLOAT)
            {
                switch (accessor.Dimensions)
                {
                    case DimensionType.VEC2:
                        var vec2 = Keep(accessor.AsVector2Array(), used, survivors);
                        return () => prim.WithVertexAccessor(name, vec2);
                    case DimensionType.VEC3:
                        var vec3 = Keep(accessor.AsVector3Array(), used, survivors);
                        return () => prim.WithVertexAccessor(name, vec3);
                    case DimensionType.VEC4:
                        var vec4 = Keep(accessor.AsVector4Array(), used, survivors);
                        return () => prim.WithVertexAccessor(name, vec4);
                    case DimensionType.SCALAR:
                        var scalars = Keep(accessor.AsScalarArray(), used, survivors);
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

            var values = Keep(accessor.AsVector4Array(), used, survivors);
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

        private static List<T> Keep<T>(IList<T> source, bool[] used, int survivors)
        {
            var kept = new List<T>(survivors);
            for (int i = 0; i < source.Count; i++)
                if (used[i]) kept.Add(source[i]);
            return kept;
        }

        private static void AnalyzePrimitive(MeshPrimitive prim, SimplifyOptions options, PrimitiveResult result)
        {
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
            {
                result.SkippedReason = "not a triangle list";
                return;
            }
            if (prim.MorphTargetsCount > 0)
            {
                // Morph targets carry their own per-vertex deltas; collapsing edges under them is
                // out of scope for this pass.
                result.SkippedReason = "has morph targets";
                return;
            }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc))
            {
                result.SkippedReason = "no POSITION";
                return;
            }

            var positions = posAcc.AsVector3Array();
            var normals = prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc) ? nAcc.AsVector3Array() : null;
            var uvs = prim.VertexAccessors.TryGetValue("TEXCOORD_0", out var uvAcc) ? uvAcc.AsVector2Array() : null;
            var joints = prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc) ? jAcc.AsVector4Array() : null;
            var weights = prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc) ? wAcc.AsVector4Array() : null;

            var tris = prim.GetTriangleIndices().Select(t => new Tri(t.A, t.B, t.C)).ToArray();
            result.TrianglesBefore = tris.Length;
            result.TrianglesAfter = tris.Length;
            if (tris.Length < 3) return;

            var simplified = MeshoptSimplifier.Run(tris, positions, normals, uvs, joints, weights, options, result);
            if (simplified == null) return;

            result.TrianglesAfter = simplified.Length / 3;
            result.NewIndices = simplified;
        }

        private readonly struct Tri
        {
            public readonly int A, B, C;
            public Tri(int a, int b, int c) { A = a; B = b; C = c; }
            public int this[int corner] => corner == 0 ? A : corner == 1 ? B : C;
        }

        // Drives meshopt_simplifyWithAttributes.
        //
        // meshoptimizer needs a position-welded, indexed mesh: it collapses edges, and an edge that
        // isn't shared between two triangles is a border it won't touch. Exported glTF is the
        // opposite of that - the character this was built against splits 13,665 real positions into
        // 53,117 vertices, leaving only 26% of edges shared - so feeding the raw buffers straight to
        // meshopt simplifies almost nothing. Everything here is about handing meshopt a properly
        // welded mesh and then mapping its answer back onto the original vertices.
        //
        // Vertices are welded on position + UV + skinning, deliberately NOT on normal. Welding on
        // UV keeps texture islands intact (a seam vertex stays two vertices, so a collapse can never
        // drag one island's texels across into another) and welding on skinning keeps deformation
        // intact, while ignoring normals is what actually restores the topology, since normals are
        // what the exporter split most of these vertices over in the first place. The normal is then
        // recovered per output triangle - see PickDuplicate.
        private static class MeshoptSimplifier
        {
            public static int[]? Run(Tri[] tris, IList<Vector3> positions, IList<Vector3>? normals,
                IList<Vector2>? uvs, IList<Vector4>? joints, IList<Vector4>? weights,
                SimplifyOptions options, PrimitiveResult result)
            {
                if (!MeshoptNative.IsAvailable)
                {
                    result.SkippedReason = "meshoptimizer native library unavailable";
                    return null;
                }
                if (tris.Length < 4) return null;

                var weld = BuildWeld(positions, uvs, joints, weights, options);
                int weldedCount = weld.Duplicates.Count;
                if (weldedCount < 4) return null;

                var weldedPositions = new float[weldedCount * 3];
                var weldedAttributes = new float[weldedCount * 5];
                for (int w = 0; w < weldedCount; w++)
                {
                    int rep = weld.Duplicates[w][0];
                    var p = positions[rep];
                    weldedPositions[w * 3 + 0] = p.X;
                    weldedPositions[w * 3 + 1] = p.Y;
                    weldedPositions[w * 3 + 2] = p.Z;

                    var n = normals != null ? normals[rep] : Vector3.UnitY;
                    weldedAttributes[w * 5 + 0] = n.X;
                    weldedAttributes[w * 5 + 1] = n.Y;
                    weldedAttributes[w * 5 + 2] = n.Z;

                    var uv = uvs != null ? uvs[rep] : Vector2.Zero;
                    weldedAttributes[w * 5 + 3] = uv.X;
                    weldedAttributes[w * 5 + 4] = uv.Y;
                }

                var weldedIndices = new uint[tris.Length * 3];
                for (int i = 0; i < tris.Length; i++)
                    for (int c = 0; c < 3; c++)
                        weldedIndices[i * 3 + c] = (uint)weld.Of[tris[i][c]];

                var attributeWeights = new[]
                {
                    options.NormalWeight, options.NormalWeight, options.NormalWeight,
                    options.UvWeight, options.UvWeight,
                };

                var locks = BuildLockMask(tris, weld, joints, weights, options);

                int targetTriangles = Math.Max(1, (int)MathF.Round(tris.Length * Math.Clamp(options.TargetRatio, 0.01f, 1f)));
                var destination = new uint[weldedIndices.Length];
                float resultError;
                nuint produced;

                unsafe
                {
                    fixed (uint* dst = destination)
                    fixed (uint* idx = weldedIndices)
                    fixed (float* pos = weldedPositions)
                    fixed (float* attr = weldedAttributes)
                    fixed (float* attrW = attributeWeights)
                    fixed (byte* lockMask = locks)
                    {
                        produced = MeshoptNative.SimplifyWithAttributes(
                            dst, idx, (nuint)weldedIndices.Length,
                            pos, (nuint)weldedCount, sizeof(float) * 3,
                            attr, sizeof(float) * 5,
                            attrW, 5,
                            locks.Length > 0 ? lockMask : null,
                            (nuint)(targetTriangles * 3), options.TargetError,
                            (uint)(options.LockBorders ? MeshoptNative.SimplifyOptions.LockBorder : MeshoptNative.SimplifyOptions.None),
                            &resultError);
                    }
                }

                int producedIndices = (int)produced;
                if (producedIndices < 3 || producedIndices >= weldedIndices.Length) return null;

                var output = MapBackToOriginals(destination, producedIndices, weld, positions, normals);
                if (output == null) return null;

                if (options.OptimizeVertexOrder) OptimizeOrder(output, positions.Count);

                result.SimplifyError = resultError;
                return output;
            }

            private sealed class Weld
            {
                public int[] Of = Array.Empty<int>();               // original vertex -> welded id
                public List<List<int>> Duplicates = new();          // welded id -> original vertices at it
            }

            private static Weld BuildWeld(IList<Vector3> positions, IList<Vector2>? uvs,
                IList<Vector4>? joints, IList<Vector4>? weights, SimplifyOptions options)
            {
                float posScale = options.WeldTolerance > 0 ? 1f / options.WeldTolerance : 1e6f;
                float uvScale = options.UvTolerance > 0 ? 1f / options.UvTolerance : 1e4f;

                var lookup = new Dictionary<(long, long, long, long, long, int, int), int>(positions.Count);
                var weld = new Weld { Of = new int[positions.Count] };

                for (int i = 0; i < positions.Count; i++)
                {
                    var p = positions[i];
                    var uv = uvs != null ? uvs[i] : Vector2.Zero;
                    var key = (
                        (long)MathF.Round(p.X * posScale), (long)MathF.Round(p.Y * posScale), (long)MathF.Round(p.Z * posScale),
                        (long)MathF.Round(uv.X * uvScale), (long)MathF.Round(uv.Y * uvScale),
                        joints != null ? joints[i].GetHashCode() : 0,
                        weights != null ? weights[i].GetHashCode() : 0);

                    if (!lookup.TryGetValue(key, out int id))
                    {
                        id = weld.Duplicates.Count;
                        lookup[key] = id;
                        weld.Duplicates.Add(new List<int>());
                    }
                    weld.Of[i] = id;
                    weld.Duplicates[id].Add(i);
                }

                return weld;
            }

            // Pins the welded vertices whose skin weights differ from their neighbourhood by more
            // than the tolerance, so the same slider that governs the planar pass governs how much
            // deformation meshopt is allowed to disturb.
            private static byte[] BuildLockMask(Tri[] tris, Weld weld, IList<Vector4>? joints,
                IList<Vector4>? weights, SimplifyOptions options)
            {
                if (joints == null || weights == null || options.SkinTolerance >= 1f) return Array.Empty<byte>();

                int weldedCount = weld.Duplicates.Count;
                var neighbours = new List<int>[weldedCount];
                for (int i = 0; i < weldedCount; i++) neighbours[i] = new List<int>();

                foreach (var t in tris)
                {
                    for (int e = 0; e < 3; e++)
                    {
                        int a = weld.Of[t[e]], b = weld.Of[t[(e + 1) % 3]];
                        if (a == b) continue;
                        neighbours[a].Add(b);
                        neighbours[b].Add(a);
                    }
                }

                float tolerance = MathF.Max(options.SkinTolerance, 1e-6f);
                var locks = new byte[weldedCount];

                for (int w = 0; w < weldedCount; w++)
                {
                    if (neighbours[w].Count == 0) { locks[w] = 1; continue; }

                    var average = new Dictionary<int, float>();
                    foreach (var n in neighbours[w])
                        foreach (var (joint, weight) in Influences(joints, weights, weld.Duplicates[n][0]))
                            average[joint] = average.TryGetValue(joint, out var acc) ? acc + weight : weight;

                    var own = new Dictionary<int, float>();
                    foreach (var (joint, weight) in Influences(joints, weights, weld.Duplicates[w][0]))
                        own[joint] = own.TryGetValue(joint, out var acc) ? acc + weight : weight;

                    foreach (var joint in average.Keys.Union(own.Keys))
                    {
                        float mine = own.TryGetValue(joint, out var m) ? m : 0f;
                        float theirs = (average.TryGetValue(joint, out var a) ? a : 0f) / neighbours[w].Count;
                        if (MathF.Abs(mine - theirs) > tolerance) { locks[w] = 1; break; }
                    }
                }

                return locks;
            }

            private static IEnumerable<(int Joint, float Weight)> Influences(IList<Vector4> joints, IList<Vector4> weights, int vertex)
            {
                var j = joints[vertex];
                var w = weights[vertex];
                if (w.X > 0) yield return ((int)j.X, w.X);
                if (w.Y > 0) yield return ((int)j.Y, w.Y);
                if (w.Z > 0) yield return ((int)j.Z, w.Z);
                if (w.W > 0) yield return ((int)j.W, w.W);
            }

            private static int[]? MapBackToOriginals(uint[] destination, int producedIndices, Weld weld,
                IList<Vector3> positions, IList<Vector3>? normals)
            {
                var output = new int[producedIndices];

                for (int i = 0; i < producedIndices; i += 3)
                {
                    int w0 = (int)destination[i], w1 = (int)destination[i + 1], w2 = (int)destination[i + 2];
                    if (w0 == w1 || w1 == w2 || w0 == w2) return null;   // meshopt should never emit these

                    var a = positions[weld.Duplicates[w0][0]];
                    var b = positions[weld.Duplicates[w1][0]];
                    var c = positions[weld.Duplicates[w2][0]];
                    var faceNormal = Vector3.Cross(b - a, c - a);
                    if (faceNormal.LengthSquared() > 1e-20f) faceNormal = Vector3.Normalize(faceNormal);

                    output[i] = PickDuplicate(weld.Duplicates[w0], faceNormal, normals);
                    output[i + 1] = PickDuplicate(weld.Duplicates[w1], faceNormal, normals);
                    output[i + 2] = PickDuplicate(weld.Duplicates[w2], faceNormal, normals);
                }

                return output;
            }

            // All the originals behind one welded vertex share a position, a UV and a skin binding -
            // they differ only in their normal. Choosing the one whose normal best matches the
            // triangle being emitted is what preserves hard edges: on a flat-shaded box, a triangle
            // on the top face picks the duplicates that point up, instead of an arbitrary one that
            // would smear the shading around the corner.
            private static int PickDuplicate(List<int> duplicates, Vector3 faceNormal, IList<Vector3>? normals)
            {
                if (duplicates.Count == 1 || normals == null) return duplicates[0];

                int best = duplicates[0];
                float bestDot = float.MinValue;
                foreach (var candidate in duplicates)
                {
                    float dot = Vector3.Dot(normals[candidate], faceNormal);
                    if (dot > bestDot) { bestDot = dot; best = candidate; }
                }
                return best;
            }

            private static void OptimizeOrder(int[] indices, int vertexCount)
            {
                var source = new uint[indices.Length];
                for (int i = 0; i < indices.Length; i++) source[i] = (uint)indices[i];

                var destination = new uint[indices.Length];
                unsafe
                {
                    fixed (uint* dst = destination)
                    fixed (uint* src = source)
                    {
                        MeshoptNative.OptimizeVertexCache(dst, src, (nuint)source.Length, (nuint)vertexCount);
                    }
                }

                for (int i = 0; i < indices.Length; i++) indices[i] = (int)destination[i];
            }
        }
    }
}
