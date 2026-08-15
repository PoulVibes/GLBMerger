using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Scenes;
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

            /// <summary>
            /// Restricts simplification to a painted subset of triangles, per primitive. A primitive
            /// with no entry here (or a null/empty Selection altogether) is simplified in full, same
            /// as before this existed. Triangles outside the selection are left byte-for-byte as they
            /// were, and every welded vertex they touch is locked so the selected region can never
            /// pull away from them and open a gap.
            /// </summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? Selection { get; set; }

            /// <summary>
            /// Flips <see cref="Selection"/> from the region to simplify into the region to protect:
            /// everything EXCEPT the painted triangles is simplified, and the painted ones are left
            /// byte-for-byte as they were. Same border locking either way, since it's the same
            /// border - only which side of it moves changes. Ignored when Selection is null.
            /// </summary>
            public bool InvertSelection { get; set; }
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
                    // When a selection is active at all, every primitive is restricted by it - one
                    // with no entry in the dictionary was never painted, so it gets an empty set
                    // (nothing eligible, left untouched) rather than null (which means "no
                    // restriction, optimize the whole primitive" and is only correct when Selection
                    // itself is absent, i.e. neither region checkbox is on).
                    //
                    // Inverted, an unpainted primitive means the opposite: nothing there to protect,
                    // so the whole thing is eligible - null, not an empty set. AnalyzePrimitive takes
                    // the complement of the painted ones, where the triangle count is already known.
                    HashSet<int>? selection = null;
                    if (options.Selection != null)
                    {
                        options.Selection.TryGetValue((meshIdx, primIdx), out var painted);
                        selection = options.InvertSelection
                            ? (painted != null && painted.Count > 0 ? painted : null)
                            : (painted ?? new HashSet<int>());
                    }

                    AnalyzePrimitive(mesh.Primitives[primIdx], options, selection, result);
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
        // On its own this makes the file BIGGER, not smaller: SharpGLTF has no way to release the
        // accessors it replaces - WithVertexAccessor creates new ones and the old buffer views stay
        // in LogicalAccessors - so MergeBuffers packs both copies. It is only half the job, and the
        // other half is BuildCleanedCopy below, which rebuilds the model to drop what this orphans.
        // Nothing should call this directly except that.
        public static int CompactUnusedVertices(ModelRoot model, bool mergeBuffers = true)
        {
            int removed = 0;
            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                    removed += CompactPrimitive(prim);

            if (removed > 0 && mergeBuffers) model.MergeBuffers();
            return removed;
        }

        // What a save would reclaim, measured without touching the model. Two separate kinds of
        // waste accumulate here and they are counted separately because they have different causes:
        //
        //  - Unused vertices. Simplification only ever rewrites indices, so every vertex the new
        //    index buffer stopped referencing is still sitting in the vertex buffer at full size.
        //  - Orphaned buffer views. Each Apply calls WithIndicesAccessor, which mints a new accessor
        //    and leaves the old one in LogicalAccessors with nothing pointing at it. Six passes over
        //    a mesh leave six dead index buffers, all of them written out on save.
        public sealed class CleanupEstimate
        {
            public int UnusedVertices { get; init; }
            public int TotalVertices { get; init; }
            public long UnusedVertexBytes { get; init; }
            public int OrphanedBufferViews { get; init; }
            public long OrphanedBytes { get; init; }

            public long ReclaimableBytes => UnusedVertexBytes + OrphanedBytes;
            public bool HasWaste => UnusedVertices > 0 || OrphanedBufferViews > 0;
        }

        public static CleanupEstimate EstimateCleanup(ModelRoot model)
        {
            // Which accessors something still points at. Only mesh data is collected: an accessor on
            // an untargeted buffer view (animation samplers, inverse bind matrices) is skipped
            // entirely below rather than judged against this set, so leaving those out is safe.
            var liveIndices = new HashSet<Accessor>();
            var liveVertices = new HashSet<Accessor>();

            int unused = 0, total = 0;
            long unusedBytes = 0;

            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (prim.IndexAccessor != null) liveIndices.Add(prim.IndexAccessor);
                foreach (var accessor in prim.VertexAccessors.Values) liveVertices.Add(accessor);
                for (int target = 0; target < prim.MorphTargetsCount; target++)
                    foreach (var accessor in prim.GetMorphTargetAccessors(target).Values)
                        liveVertices.Add(accessor);

                if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                total += posAcc.Count;

                // Mirrors CompactPrimitive's own guards exactly - a primitive it will refuse to
                // touch must not be counted here, or the dialog promises bytes back that the
                // cleanup then can't deliver.
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                if (prim.MorphTargetsCount > 0) continue;

                var used = new bool[posAcc.Count];
                foreach (var (a, b, c) in prim.GetTriangleIndices()) { used[a] = true; used[b] = true; used[c] = true; }

                int dead = 0;
                foreach (var u in used) if (!u) dead++;
                if (dead == 0) continue;

                long bytesPerVertex = prim.VertexAccessors.Values.Sum(a => (long)a.Format.ByteSizePadded);
                unused += dead;
                unusedBytes += dead * bytesPerVertex;
            }

            // A buffer view is dead when nothing live reads any accessor over it. Views are counted
            // rather than accessors because an interleaved view is shared by POSITION/NORMAL/UV and
            // its bytes must not be counted three times.
            int deadViews = 0;
            long deadBytes = 0;
            foreach (var view in model.LogicalBufferViews)
            {
                // Untargeted views hold image bytes, animation curves and inverse bind matrices.
                // None of those are reachable from the accessor sets above, so classifying them
                // here would report every one of them as garbage.
                if (!view.IsIndexBuffer && !view.IsVertexBuffer) continue;

                bool anyLive = false;
                foreach (var accessor in view.FindAccessors())
                    if (liveIndices.Contains(accessor) || liveVertices.Contains(accessor)) { anyLive = true; break; }

                if (anyLive) continue;
                deadViews++;
                deadBytes += view.Content.Count;
            }

            return new CleanupEstimate
            {
                UnusedVertices = unused,
                TotalVertices = total,
                UnusedVertexBytes = unusedBytes,
                OrphanedBufferViews = deadViews,
                OrphanedBytes = deadBytes,
            };
        }

        // Returns a cleaned copy, leaving the model it was given untouched - the live model is
        // shared by every editor mode and swapping that reference out is a much larger change than
        // this needs to be. The copy is what gets written to disk; the session carries on with the
        // original.
        //
        // Order matters. Compaction has to run first, and it orphans the vertex accessors it
        // replaces on the way, so the rebuild has to run second: SceneBuilder walks the scene and
        // re-emits only what it can actually reach, which is what finally drops both those
        // accessors and the dead index buffers left by earlier Apply passes.
        public static ModelRoot BuildCleanedCopy(ModelRoot model)
        {
            var working = model.DeepClone();
            CompactUnusedVertices(working, mergeBuffers: false);

            var cleaned = SceneBuilder.ToGltf2(SceneBuilder.CreateFrom(working), SceneBuilderSchema2Settings.Default);
            cleaned.MergeBuffers();
            return cleaned;
        }

        // The rebuild in BuildCleanedCopy re-emits the scene rather than editing it, so anything
        // SceneBuilder cannot reach is silently gone. The hand-authored BallAnchor/StiffArm markers
        // are the exposed case - they are empty named nodes parented to a joint, carrying nothing
        // but a local rotation - so the result is checked against the original before it is written
        // instead of trusted. Returns null when nothing was lost.
        public static string? DescribeLosses(ModelRoot before, ModelRoot after)
        {
            var losses = new List<string>();

            // Only named nodes are compared. Unnamed ones are structural, and reorganizing those is
            // exactly what the rebuild is entitled to do.
            var namesBefore = NamedNodes(before);
            var namesAfter = NamedNodes(after);
            var missingNodes = namesBefore.Except(namesAfter).OrderBy(n => n).ToList();
            if (missingNodes.Count > 0)
                losses.Add($"{missingNodes.Count} named node(s): {string.Join(", ", missingNodes.Take(8))}"
                    + (missingNodes.Count > 8 ? ", ..." : ""));

            var animsBefore = before.LogicalAnimations.ToDictionary(a => a.Name ?? $"Anim_{a.LogicalIndex}", a => a.Channels.Count);
            var animsAfter = after.LogicalAnimations.ToDictionary(a => a.Name ?? $"Anim_{a.LogicalIndex}", a => a.Channels.Count);
            foreach (var (name, channels) in animsBefore)
            {
                if (!animsAfter.TryGetValue(name, out var afterChannels))
                    losses.Add($"animation \"{name}\"");
                else if (afterChannels < channels)
                    losses.Add($"animation \"{name}\": {channels} channels -> {afterChannels}");
            }

            if (after.LogicalSkins.Count < before.LogicalSkins.Count)
                losses.Add($"skins: {before.LogicalSkins.Count} -> {after.LogicalSkins.Count}");
            if (after.LogicalMaterials.Count < before.LogicalMaterials.Count)
                losses.Add($"materials: {before.LogicalMaterials.Count} -> {after.LogicalMaterials.Count}");
            if (after.LogicalImages.Count < before.LogicalImages.Count)
                losses.Add($"textures: {before.LogicalImages.Count} -> {after.LogicalImages.Count}");

            int trisBefore = CountTriangles(before), trisAfter = CountTriangles(after);
            if (trisAfter != trisBefore)
                losses.Add($"triangles: {trisBefore:N0} -> {trisAfter:N0}");

            return losses.Count == 0 ? null : string.Join(Environment.NewLine, losses.Select(l => "  - " + l));
        }

        private static HashSet<string> NamedNodes(ModelRoot model) =>
            model.LogicalNodes
                .Select(n => n.Name)
                .Where(n => !string.IsNullOrEmpty(n))
                .ToHashSet()!;

        private static int CountTriangles(ModelRoot model) =>
            model.LogicalMeshes
                .SelectMany(m => m.Primitives)
                .Where(p => p.DrawPrimitiveType == PrimitiveType.TRIANGLES)
                .Sum(p => p.GetTriangleIndices().Count());

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

        private static void AnalyzePrimitive(MeshPrimitive prim, SimplifyOptions options, HashSet<int>? selection, PrimitiveResult result)
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

            // Inverted, what arrived is the region to PROTECT, so what's eligible is everything else.
            // Done here rather than in Analyze because it needs the triangle count, which costs a
            // second walk of the index buffer up there and nothing at all down here.
            if (selection != null && options.InvertSelection)
            {
                var eligible = new HashSet<int>();
                for (int t = 0; t < tris.Length; t++)
                    if (!selection.Contains(t)) eligible.Add(t);
                selection = eligible;
            }

            if (selection != null && selection.Count == 0) return;   // painted, but nothing in it - nothing to do

            var simplified = MeshoptSimplifier.Run(tris, positions, normals, uvs, joints, weights, options, selection, result);
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
                SimplifyOptions options, HashSet<int>? selection, PrimitiveResult result)
            {
                if (!MeshoptNative.IsAvailable)
                {
                    result.SkippedReason = "meshoptimizer native library unavailable";
                    return null;
                }

                // A painted selection restricts which triangles meshopt is even allowed to see -
                // everything else is carried through byte-for-byte below (see the concat at the
                // bottom) and none of its vertices are eligible to move (see BuildLockMask), so the
                // simplified region can never pull away from the untouched region and open a gap.
                Tri[] simplifyTris = tris;
                Tri[] passthroughTris = Array.Empty<Tri>();
                if (selection != null)
                {
                    var selected = new List<Tri>(selection.Count);
                    var unselected = new List<Tri>(tris.Length - selection.Count);
                    for (int i = 0; i < tris.Length; i++)
                        (selection.Contains(i) ? selected : unselected).Add(tris[i]);
                    simplifyTris = selected.ToArray();
                    passthroughTris = unselected.ToArray();
                }

                if (simplifyTris.Length < 4) return null;

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

                var weldedIndices = new uint[simplifyTris.Length * 3];
                for (int i = 0; i < simplifyTris.Length; i++)
                    for (int c = 0; c < 3; c++)
                        weldedIndices[i * 3 + c] = (uint)weld.Of[simplifyTris[i][c]];

                var attributeWeights = new[]
                {
                    options.NormalWeight, options.NormalWeight, options.NormalWeight,
                    options.UvWeight, options.UvWeight,
                };

                var locks = BuildLockMask(simplifyTris, weld, joints, weights, options, passthroughTris);

                int targetTriangles = Math.Max(1, (int)MathF.Round(simplifyTris.Length * Math.Clamp(options.TargetRatio, 0.01f, 1f)));
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

                var simplified = MapBackToOriginals(destination, producedIndices, weld, positions, normals);
                if (simplified == null) return null;

                if (options.OptimizeVertexOrder) OptimizeOrder(simplified, positions.Count);

                result.SimplifyError = resultError;

                if (passthroughTris.Length == 0) return simplified;

                // Untouched triangles ride along unchanged, exactly as they were before the
                // selection was carved out.
                var output = new int[simplified.Length + passthroughTris.Length * 3];
                Array.Copy(simplified, output, simplified.Length);
                for (int i = 0; i < passthroughTris.Length; i++)
                {
                    output[simplified.Length + i * 3 + 0] = passthroughTris[i].A;
                    output[simplified.Length + i * 3 + 1] = passthroughTris[i].B;
                    output[simplified.Length + i * 3 + 2] = passthroughTris[i].C;
                }
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
            // deformation meshopt is allowed to disturb. Also pins every welded vertex touched by a
            // passthrough (unselected) triangle - those triangles are never handed to meshopt, so
            // any vertex they reference must not move or they'd be left pointing at a position that
            // no longer lines up, opening a gap at the selection's boundary.
            private static byte[] BuildLockMask(Tri[] tris, Weld weld, IList<Vector4>? joints,
                IList<Vector4>? weights, SimplifyOptions options, Tri[] passthroughTris)
            {
                int weldedCount = weld.Duplicates.Count;

                byte[]? selectionLocks = null;
                if (passthroughTris.Length > 0)
                {
                    selectionLocks = new byte[weldedCount];
                    foreach (var t in passthroughTris)
                        for (int c = 0; c < 3; c++)
                            selectionLocks[weld.Of[t[c]]] = 1;
                }

                if (joints == null || weights == null || options.SkinTolerance >= 1f)
                    return selectionLocks ?? Array.Empty<byte>();

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

                if (selectionLocks != null)
                    for (int w = 0; w < weldedCount; w++)
                        if (selectionLocks[w] != 0) locks[w] = 1;

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
