using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Slices the merged model with a plane: everything on one side is deleted, and every triangle
    // the plane passes through is clipped so the model ends in a clean, flat edge exactly on the
    // plane rather than a jagged one. Optionally that edge is then capped with a flat surface.
    //
    // The plane is defined in WORLD space - the space the preview shows - and every vertex is
    // measured against it in world space too, which for a skinned mesh means through its skin
    // (bind position blended through inverseBind x jointWorld per influence, the same way
    // MainForm.ComputeModelHeightMeters measures height) and for a rigid mesh through its node's
    // WorldMatrix. So the cut lands where the preview says it will, whatever transforms sit
    // between the vertex buffer and the screen. The new vertices themselves are written in the
    // primitive's own space, interpolated along the edge they sit on, which is exact for a rigid
    // mesh and exact for a skinned one wherever the edge's two ends share skin weights - which is
    // everywhere except right at a joint, where the deviation is well below anything visible.
    //
    // Clipping is Sutherland-Hodgman per triangle against the keep half-space: a crossing triangle
    // comes back as a triangle or a quad (split into two), with a new vertex at each point an edge
    // meets the plane. Every attribute of such a vertex is interpolated along the edge - except
    // skin influences, which are labels rather than magnitudes and are merged instead (union of
    // both ends' joints, weights blended, strongest four kept). Each edge is cut once however many
    // triangles share it, so the two sides of the cut always meet at the same vertex, and the cut
    // edge is a closed loop the capper can find.
    //
    // Capping is delegated to WatertightRepair rather than duplicated: after the clip, the cut edge
    // IS a hole, and the hole filler already knows how to close one with a centroid fan and texture
    // it from a blank patch of the material's atlas. But the rings are found HERE, not by asking it
    // to search: the clip knows every vertex it put on the plane, so chaining those into rings is
    // exact, never confuses the cut with an opening the model already had, and can weld the twin
    // vertices a UV seam leaves at the same position with a tolerance sized to the model - the
    // filler's own weld is an absolute 1e-4, which on a small prop is coarse enough to merge two
    // genuinely different cut points and break the ring. The rings go to CapLoops with flat caps
    // asked for, so every cap vertex gets the ring's own plane normal instead of an average of the
    // side surface's normals (which, for a planar cut, would point sideways).
    //
    // The vertices left on the deleted side are dropped afterwards by the same per-primitive
    // compaction TriangleDeleter uses, so a cut shrinks the vertex buffer as well as the index
    // buffer. Whole steps in GeometryHistory terms: one BeginChange/Record pair with vertex data.
    public static class PlanarCutter
    {
        // The half-space to delete is the positive side: dot(Normal, p) > Distance.
        public readonly struct Plane
        {
            public Vector3 Normal { get; }
            public float Distance { get; }

            public Plane(Vector3 normal, float distance)
            {
                Normal = Vector3.Normalize(normal);
                Distance = distance;
            }

            // An axis-aligned plane at a given coordinate. deletePositive picks which side goes.
            public static Plane AxisAligned(int axis, float position, bool deletePositive)
            {
                var n = axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
                return deletePositive ? new Plane(n, position) : new Plane(-n, -position);
            }

            public float SignedDistance(Vector3 worldPoint) => Vector3.Dot(Normal, worldPoint) - Distance;
        }

        public sealed class PrimitiveResult
        {
            public string MeshName { get; init; } = "";
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }

            public int TrianglesBefore { get; set; }
            /// <summary>Wholly on the deleted side.</summary>
            public int TrianglesDeleted { get; set; }
            /// <summary>Crossed the plane and were trimmed back to it.</summary>
            public int TrianglesClipped { get; set; }
            /// <summary>Triangles in the primitive after the clip, before any cap.</summary>
            public int TrianglesAfter { get; set; }

            public int VerticesBefore { get; set; }
            /// <summary>New vertices placed on the plane where edges crossed it.</summary>
            public int VerticesAdded { get; set; }
            /// <summary>Original vertices left on the deleted side, dropped by compaction afterwards.</summary>
            public int VerticesDropped { get; set; }

            /// <summary>Closed rings the cut edge forms here - each one is what a cap would close.</summary>
            public int CutLoops { get; set; }
            /// <summary>Cut edges that don't close into a ring (the cut ran into an existing opening).</summary>
            public int OpenCutEdges { get; set; }

            public string? SkippedReason { get; set; }

            /// <summary>The plane would take every triangle here, so it was left alone - see TriangleDeleter.</summary>
            public bool FullyDeleted { get; set; }

            // Filled in by Analyze, consumed by Apply - null when the plane misses this primitive
            // entirely or it can't be touched.
            internal WatertightRepair.VertexPatch? Patch { get; set; }

            // Every vertex on the plane after the clip: the new ones, plus any original vertex that
            // already sat on the plane and survived. The cut edge runs between these.
            internal HashSet<int> CutBoundaryVertices { get; } = new();

            // The closed rings the cut edge forms, as vertex indices in the patched (pre-compaction)
            // numbering, each in the direction the clipped triangles traverse it - which is the
            // convention WatertightRepair.CapLoops expects.
            internal List<List<int>> CutRings { get; } = new();
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();

            public int TrianglesBefore => Primitives.Sum(p => p.TrianglesBefore);
            public int TrianglesDeleted => Primitives.Sum(p => p.TrianglesDeleted);
            public int TrianglesClipped => Primitives.Sum(p => p.TrianglesClipped);
            public int TrianglesAfter => Primitives.Sum(p => p.Patch != null ? p.TrianglesAfter : p.TrianglesBefore);
            public int VerticesAdded => Primitives.Sum(p => p.VerticesAdded);
            public int VerticesDropped => Primitives.Sum(p => p.VerticesDropped);
            public int CutLoops => Primitives.Sum(p => p.CutLoops);
            public int OpenCutEdges => Primitives.Sum(p => p.OpenCutEdges);

            public bool HasChanges => Primitives.Any(p => p.Patch != null);

            /// <summary>Mesh parts the plane would remove in their entirety - left alone, see TriangleDeleter.</summary>
            public int PrimitivesFullyDeleted => Primitives.Count(p => p.FullyDeleted);
        }

        // Vertices within this distance of the plane (world units) count as ON it: they're kept,
        // no new vertex is made for an edge ending at one, and they join the cut edge. Without a
        // band like this an edge that touches the plane at one end would spawn a second vertex a
        // hair away from the first.
        private const float OnPlaneTolerance = 1e-5f;

        private enum Side { Keep, OnPlane, Delete }

        // Everything the clip needs from one primitive, read once.
        private sealed class PrimitiveData
        {
            public required MeshPrimitive Prim { get; init; }
            public required Dictionary<string, IList<Vector4>> Attributes { get; init; }
            public required (int A, int B, int C)[] Triangles { get; init; }
            public required float[] SignedDistance { get; init; }
            public required Side[] Sides { get; init; }
            public int VertexCount => SignedDistance.Length;
        }

        // Dry run: computes the clip for every primitive without touching the model. The cap can't
        // be previewed the same way - the hole filler reads the clipped mesh, which only exists
        // after Apply - so the report counts the closed cut loops instead, each of which is what a
        // cap would close.
        public static Report Analyze(ModelRoot model, Plane plane)
        {
            var report = new Report();
            var worldPositions = new WorldPositionSource(model);

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

                    var data = LoadPrimitive(mesh.Primitives[primIdx], worldPositions.For(meshIdx), plane, result);
                    if (data != null) ClipPrimitive(data, result);
                }
            }

            return report;
        }

        // Writes whatever Analyze computed. Three phases, in an order that matters:
        //
        //  1. Each cut primitive gets its clipped index buffer and the new plane vertices appended.
        //  2. The cap, if wanted, goes through WatertightRepair.CapLoops with the rings found by
        //     Analyze - which is why it can't come first (it reads the clipped triangles back off
        //     the primitive) and why it has to come before compaction: the rings are vertex
        //     indices in the patched numbering, and nothing has renumbered them yet.
        //  3. Only then are the deleted side's vertices compacted away, renumbering everything.
        //
        // Returns the cap's own report (null when no cap was asked for) so the caller can say how
        // many loops were actually closed.
        public static WatertightRepair.Report? Apply(Report report, ModelRoot model, bool cap)
        {
            var touched = new Dictionary<(int, int), PrimitiveResult>();
            foreach (var p in report.Primitives)
            {
                if (p.Patch == null) continue;
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];
                WatertightRepair.WritePatch(prim, p.Patch);
                touched[(p.MeshIndex, p.PrimitiveIndex)] = p;
            }

            WatertightRepair.Report? capReport = null;
            if (cap && touched.Count > 0)
            {
                var rings = touched.Where(kv => kv.Value.CutRings.Count > 0)
                    .ToDictionary(kv => kv.Key, kv => kv.Value.CutRings);
                capReport = WatertightRepair.CapLoops(model, rings, flatCaps: true);
                WatertightRepair.Apply(capReport, model);
            }

            foreach (var p in touched.Values)
                GeometryOptimizer.CompactPrimitive(model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex]);

            return capReport;
        }

        // World-space extent of the whole model, measured the same way the cut measures vertices,
        // so a UI can range its plane controls over exactly the space the cut operates in.
        public static (Vector3 Min, Vector3 Max) WorldBounds(ModelRoot model)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var source = new WorldPositionSource(model);

            for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
            {
                var toWorld = source.For(meshIdx);
                foreach (var prim in model.LogicalMeshes[meshIdx].Primitives)
                {
                    foreach (var p in toWorld(prim))
                    {
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                    }
                }
            }

            return min.X <= max.X ? (min, max) : (Vector3.Zero, Vector3.Zero);
        }

        // Resolves a primitive's POSITION into world space through whichever node draws its mesh.
        // A mesh drawn by several nodes is measured through the first; one no node draws at all is
        // taken as already in world space.
        private sealed class WorldPositionSource
        {
            private readonly Dictionary<int, Node> _nodeByMesh = new();

            public WorldPositionSource(ModelRoot model)
            {
                foreach (var node in model.LogicalNodes)
                    if (node.Mesh != null) _nodeByMesh.TryAdd(node.Mesh.LogicalIndex, node);
            }

            public Func<MeshPrimitive, Vector3[]> For(int meshIndex)
            {
                _nodeByMesh.TryGetValue(meshIndex, out var node);

                if (node?.Skin != null)
                {
                    var skin = node.Skin;
                    var skinMatrices = new Matrix4x4[skin.JointsCount];
                    for (int i = 0; i < skin.JointsCount; i++)
                    {
                        var (jointNode, invBind) = skin.GetJoint(i);
                        skinMatrices[i] = invBind * jointNode.WorldMatrix;
                    }
                    var rigid = node.WorldMatrix;

                    return prim =>
                    {
                        var positions = prim.VertexAccessors["POSITION"].AsVector3Array();
                        var result = new Vector3[positions.Count];

                        // A primitive on a skinned node that carries no skin binding of its own
                        // can only be placed by the node, same as an unskinned one.
                        if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc)
                            || !prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc))
                        {
                            for (int i = 0; i < result.Length; i++) result[i] = Vector3.Transform(positions[i], rigid);
                            return result;
                        }

                        var joints = jAcc.AsVector4Array();
                        var weights = wAcc.AsVector4Array();
                        for (int i = 0; i < result.Length; i++)
                        {
                            var p = positions[i];
                            var j = joints[i];
                            var w = weights[i];
                            result[i] =
                                Vector3.Transform(p, Joint(skinMatrices, j.X)) * w.X +
                                Vector3.Transform(p, Joint(skinMatrices, j.Y)) * w.Y +
                                Vector3.Transform(p, Joint(skinMatrices, j.Z)) * w.Z +
                                Vector3.Transform(p, Joint(skinMatrices, j.W)) * w.W;
                        }
                        return result;
                    };
                }

                var world = node?.WorldMatrix ?? Matrix4x4.Identity;
                return prim =>
                {
                    var positions = prim.VertexAccessors["POSITION"].AsVector3Array();
                    var result = new Vector3[positions.Count];
                    for (int i = 0; i < result.Length; i++) result[i] = Vector3.Transform(positions[i], world);
                    return result;
                };
            }

            private static Matrix4x4 Joint(Matrix4x4[] skinMatrices, float index)
            {
                int i = (int)index;
                return i >= 0 && i < skinMatrices.Length ? skinMatrices[i] : Matrix4x4.Identity;
            }
        }

        private static PrimitiveData? LoadPrimitive(MeshPrimitive prim, Func<MeshPrimitive, Vector3[]> toWorld,
            Plane plane, PrimitiveResult result)
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
            if (!prim.VertexAccessors.ContainsKey("POSITION"))
            {
                result.SkippedReason = "no POSITION";
                return null;
            }
            // New vertices have to be written back in every attribute's own encoding, which is the
            // same requirement compaction has - so the same test decides both.
            if (!GeometryOptimizer.CanCompactPrimitive(prim))
            {
                result.SkippedReason = "unfamiliar vertex encoding";
                return null;
            }

            var worldPositions = toWorld(prim);
            var distances = new float[worldPositions.Length];
            var sides = new Side[worldPositions.Length];
            for (int i = 0; i < worldPositions.Length; i++)
            {
                float s = plane.SignedDistance(worldPositions[i]);
                distances[i] = s;
                sides[i] = s > OnPlaneTolerance ? Side.Delete : s < -OnPlaneTolerance ? Side.Keep : Side.OnPlane;
            }

            var attributes = new Dictionary<string, IList<Vector4>>();
            foreach (var (name, accessor) in prim.VertexAccessors)
                attributes[name] = WatertightRepair.ReadAsVector4(accessor);

            return new PrimitiveData
            {
                Prim = prim,
                Attributes = attributes,
                Triangles = prim.GetTriangleIndices().ToArray(),
                SignedDistance = distances,
                Sides = sides,
            };
        }

        private static void ClipPrimitive(PrimitiveData data, PrimitiveResult result)
        {
            result.TrianglesBefore = data.Triangles.Length;
            result.VerticesBefore = data.VertexCount;

            bool anyDelete = data.Sides.Any(s => s == Side.Delete);
            if (!anyDelete)
            {
                result.SkippedReason = "entirely on the kept side";
                return;
            }

            int baseCount = data.VertexCount;
            var appended = data.Attributes.Keys.ToDictionary(name => name, _ => new List<Vector4>());
            var cutVertexByEdge = new Dictionary<(int, int), int>();
            var newTriangles = new List<(int A, int B, int C)>();

            // The vertex where edge a-b meets the plane, made once per edge whichever direction and
            // whichever triangle asks for it.
            int CutVertex(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (cutVertexByEdge.TryGetValue(key, out int existing)) return existing;

                (int lo, int hi) = key;
                float t = data.SignedDistance[lo] / (data.SignedDistance[lo] - data.SignedDistance[hi]);
                t = Math.Clamp(t, 0f, 1f);

                int index = baseCount + appended.Values.First().Count;
                foreach (var (name, values) in appended)
                    values.Add(Interpolate(name, data.Attributes[name][lo], data.Attributes[name][hi], t));
                MergeSkinInfluences(data.Attributes, appended, lo, hi, t);

                cutVertexByEdge[key] = index;
                result.CutBoundaryVertices.Add(index);
                return index;
            }

            var polygon = new List<int>(4);
            foreach (var tri in data.Triangles)
            {
                int deleteCount = 0, keepCount = 0;
                foreach (int v in new[] { tri.A, tri.B, tri.C })
                {
                    if (data.Sides[v] == Side.Delete) deleteCount++;
                    else if (data.Sides[v] == Side.Keep) keepCount++;
                }

                if (deleteCount == 0)
                {
                    newTriangles.Add(tri);
                    foreach (int v in new[] { tri.A, tri.B, tri.C })
                        if (data.Sides[v] == Side.OnPlane) result.CutBoundaryVertices.Add(v);
                    continue;
                }
                if (keepCount == 0)
                {
                    // Nothing strictly on the kept side: gone, including a triangle that only
                    // touches the plane with a vertex or an edge.
                    result.TrianglesDeleted++;
                    continue;
                }

                // Sutherland-Hodgman against the keep half-space, walking the triangle's edges in
                // winding order so the polygon that comes out keeps that winding.
                polygon.Clear();
                var corners = new[] { tri.A, tri.B, tri.C };
                for (int i = 0; i < 3; i++)
                {
                    int s = corners[i];
                    int e = corners[(i + 1) % 3];
                    bool sInside = data.Sides[s] != Side.Delete;
                    bool eInside = data.Sides[e] != Side.Delete;

                    // The crossing only needs a new vertex when the inside end is strictly inside;
                    // an end already on the plane IS the crossing, and is emitted as itself.
                    if (eInside)
                    {
                        if (!sInside && data.Sides[e] == Side.Keep) polygon.Add(CutVertex(s, e));
                        polygon.Add(e);
                        if (data.Sides[e] == Side.OnPlane) result.CutBoundaryVertices.Add(e);
                    }
                    else if (sInside && data.Sides[s] == Side.Keep)
                    {
                        polygon.Add(CutVertex(s, e));
                    }
                }

                if (polygon.Count < 3)
                {
                    result.TrianglesDeleted++;
                    continue;
                }

                result.TrianglesClipped++;
                newTriangles.Add((polygon[0], polygon[1], polygon[2]));
                if (polygon.Count == 4) newTriangles.Add((polygon[0], polygon[2], polygon[3]));
            }

            if (result.TrianglesDeleted == 0 && result.TrianglesClipped == 0)
            {
                result.SkippedReason = "entirely on the kept side";
                return;
            }

            if (newTriangles.Count == 0)
            {
                // See TriangleDeleter: a primitive can be thinned but not emptied.
                result.SkippedReason = "entirely on the deleted side - the whole mesh part would be gone";
                result.FullyDeleted = true;
                result.CutBoundaryVertices.Clear();
                return;
            }

            result.TrianglesAfter = newTriangles.Count;
            result.VerticesAdded = appended.Values.First().Count;

            var newIndices = new int[newTriangles.Count * 3];
            int w = 0;
            foreach (var (a, b, c) in newTriangles) { newIndices[w++] = a; newIndices[w++] = b; newIndices[w++] = c; }

            result.Patch = new WatertightRepair.VertexPatch
            {
                AppendedAttributes = appended.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Vector4>)kv.Value),
                NewIndices = newIndices,
            };

            // What compaction will find: every original vertex no surviving triangle reaches. The
            // cap adds only vertices its own triangles use, so this is the final figure too.
            var used = new bool[baseCount];
            foreach (int index in newIndices)
                if (index < baseCount) used[index] = true;
            int survivors = 0;
            foreach (bool u in used) if (u) survivors++;
            result.VerticesDropped = baseCount - survivors;

            FindCutRings(data, appended["POSITION"], newTriangles, result);
        }

        // Straight interpolation for everything that is a magnitude. Direction attributes are
        // renormalized so a cut vertex between two differently-lit corners doesn't end up dim.
        private static Vector4 Interpolate(string name, Vector4 a, Vector4 b, float t)
        {
            var v = Vector4.Lerp(a, b, t);
            if (name == "NORMAL" || name == "TANGENT")
            {
                var n = new Vector3(v.X, v.Y, v.Z);
                if (n.LengthSquared() > 1e-20f) n = Vector3.Normalize(n);
                else n = new Vector3(a.X, a.Y, a.Z);
                // A tangent's W is its handedness sign, which has to stay exactly +-1.
                v = new Vector4(n, name == "TANGENT" ? (t < 0.5f ? a.W : b.W) : v.W);
            }
            return v;
        }

        // Joint indices identify bones, so a lerp of two JOINTS_n vectors would invent bones that
        // influence neither end. A cut vertex's skin binding is instead the union of both ends'
        // influences with their weights blended by position along the edge, cut back to the
        // strongest four and renormalized - the standard way to interpolate a skin binding.
        // Overwrites the naive lerp Interpolate already appended for each JOINTS_n/WEIGHTS_n pair.
        private static void MergeSkinInfluences(Dictionary<string, IList<Vector4>> attributes,
            Dictionary<string, List<Vector4>> appended, int lo, int hi, float t)
        {
            for (int set = 0; ; set++)
            {
                string jointsName = $"JOINTS_{set}", weightsName = $"WEIGHTS_{set}";
                if (!attributes.TryGetValue(jointsName, out var joints) || !attributes.TryGetValue(weightsName, out var weights))
                    break;

                var blended = new Dictionary<int, float>();
                void Accumulate(int vertex, float scale)
                {
                    var j = joints[vertex];
                    var w = weights[vertex];
                    foreach (var (joint, weight) in new[] { (j.X, w.X), (j.Y, w.Y), (j.Z, w.Z), (j.W, w.W) })
                    {
                        if (weight <= 0f) continue;
                        int key = (int)MathF.Round(joint);
                        blended[key] = blended.GetValueOrDefault(key) + weight * scale;
                    }
                }
                Accumulate(lo, 1f - t);
                Accumulate(hi, t);

                var top = blended.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float total = top.Sum(kv => kv.Value);
                if (total <= 0f) top = new List<KeyValuePair<int, float>> { new(0, 1f) };
                else top = top.Select(kv => new KeyValuePair<int, float>(kv.Key, kv.Value / total)).ToList();
                while (top.Count < 4) top.Add(new KeyValuePair<int, float>(0, 0f));

                int last = appended[jointsName].Count - 1;
                appended[jointsName][last] = new Vector4(top[0].Key, top[1].Key, top[2].Key, top[3].Key);
                appended[weightsName][last] = new Vector4(top[0].Value, top[1].Value, top[2].Value, top[3].Value);
            }
        }

        // Chains the cut edge into closed rings. The cut edge is every edge of a surviving triangle
        // whose two ends are both on the plane and that no second triangle uses; each is taken in
        // the direction that one triangle traverses it, which is the direction the rings are kept
        // in and the direction CapLoops expects.
        //
        // Chaining purely by index breaks at every UV seam: the seam's twin vertices (same
        // position, different index, different UV) each get their own cut vertex, so the ring
        // arrives at one twin and leaves from the other. Endpoints are therefore welded by
        // position first, to a tolerance a millionth of the primitive's own extent - tight enough
        // that two genuinely different cut points a hair apart stay apart, loose enough to absorb
        // the rounding difference between twins cut from opposite ends of the same edge.
        private static void FindCutRings(PrimitiveData data, List<Vector4> appendedPositions,
            List<(int A, int B, int C)> triangles, PrimitiveResult result)
        {
            var onPlane = result.CutBoundaryVertices;
            int baseCount = data.VertexCount;
            var basePositions = data.Attributes["POSITION"];

            Vector3 PositionOf(int v)
            {
                var p = v < baseCount ? basePositions[v] : appendedPositions[v - baseCount];
                return new Vector3(p.X, p.Y, p.Z);
            }

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (int v in onPlane) { var p = PositionOf(v); min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            float extent = MathF.Max((max - min).Length(), 1e-6f);
            float scale = 1f / (extent * 1e-6f);

            var representative = new Dictionary<(long, long, long), int>();
            int Rep(int v)
            {
                var p = PositionOf(v);
                var key = ((long)MathF.Round(p.X * scale), (long)MathF.Round(p.Y * scale), (long)MathF.Round(p.Z * scale));
                if (!representative.TryGetValue(key, out int rep)) representative[key] = rep = v;
                return rep;
            }

            // Undirected use count per on-plane edge, and the direction it was used in.
            var edgeUse = new Dictionary<(int, int), int>();
            var directed = new Dictionary<(int, int), (int, int)>();
            void Count(int a, int b)
            {
                if (!onPlane.Contains(a) || !onPlane.Contains(b)) return;
                var key = a < b ? (a, b) : (b, a);
                edgeUse[key] = edgeUse.GetValueOrDefault(key) + 1;
                directed[key] = (a, b);
            }
            foreach (var (a, b, c) in triangles) { Count(a, b); Count(b, c); Count(c, a); }

            var next = new Dictionary<int, int>();
            int branching = 0;
            foreach (var (key, uses) in edgeUse)
            {
                if (uses != 1) continue;
                var (a, b) = directed[key];
                int ra = Rep(a), rb = Rep(b);
                if (ra == rb) continue;
                if (!next.TryAdd(ra, rb)) branching++;
            }

            var consumed = new HashSet<int>();
            int open = 0;
            foreach (int start in next.Keys)
            {
                if (consumed.Contains(start)) continue;
                var ring = new List<int>();
                int current = start;
                bool closed = false;
                while (next.TryGetValue(current, out int following) && consumed.Add(current))
                {
                    ring.Add(current);
                    current = following;
                    if (current == start) { closed = true; break; }
                }
                if (closed && ring.Count >= 3) result.CutRings.Add(ring);
                else open += ring.Count;
            }

            result.CutLoops = result.CutRings.Count;
            result.OpenCutEdges = open + branching;
        }
    }
}
