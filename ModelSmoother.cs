using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Backs ModelSmootherEditor's live brush. The smoothing itself - the Laplacian relaxation, the
    // position weld that keeps seams from tearing, the per-frame halo normal recompute - all runs
    // in the browser, against the mesh actually on screen, because that's what "smooths in real
    // time as you brush" requires: there's no round trip to .NET fast enough to do it per frame.
    //
    // What's left here is the one moment a stroke's result does need to land in the shared
    // ModelRoot every other editor mode reads and writes: a snapshot/restore pair for Revert, and a
    // thin applier that writes one primitive's finished POSITION/NORMAL back once a stroke ends.
    public static class ModelSmoother
    {
        public sealed class PositionSnapshot
        {
            public List<Vector3> Positions { get; init; } = new();
            public List<Vector3>? Normals { get; init; }
        }

        // Captures every primitive's current POSITION/NORMAL so an in-session "Revert" can put the
        // surface back - the model is shared with every other editor mode and there's no other copy
        // of it anywhere.
        public static List<PositionSnapshot> SnapshotPositions(ModelRoot model) =>
            model.LogicalMeshes.SelectMany(m => m.Primitives).Select(prim =>
            {
                var positions = prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)
                    ? posAcc.AsVector3Array().ToList()
                    : new List<Vector3>();
                var normals = prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc)
                    ? nAcc.AsVector3Array().ToList()
                    : null;
                return new PositionSnapshot { Positions = positions, Normals = normals };
            }).ToList();

        public static void RestorePositions(ModelRoot model, List<PositionSnapshot> snapshot)
        {
            int i = 0;
            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (i >= snapshot.Count) break;
                var snap = snapshot[i++];
                if (snap.Positions.Count == 0) continue;
                prim.WithVertexAccessor("POSITION", snap.Positions);
                if (snap.Normals != null) prim.WithVertexAccessor("NORMAL", snap.Normals);
            }
        }

        // Writes one brush stroke's result - already computed live in the browser - onto the shared
        // model. Only POSITION and (where the primitive has one) NORMAL are touched; triangle
        // count, UVs and skinning are never involved.
        public static void ApplyStroke(ModelRoot model, int meshIndex, int primitiveIndex,
            List<Vector3> positions, List<Vector3>? normals)
        {
            var prim = model.LogicalMeshes[meshIndex].Primitives[primitiveIndex];
            prim.WithVertexAccessor("POSITION", positions);
            if (normals != null) prim.WithVertexAccessor("NORMAL", normals);
        }

        // Rebuilds every primitive's NORMALs as smooth shading. Meshy exports (Jamal, for one) ship
        // with a large share of per-face normals, so a dense, smooth surface like a nose still
        // lights as visible facets. See RebuildNormals for the rule. Positions, UVs, indices and
        // skinning are never touched. Returns how many vertices changed noticeably. Used wherever
        // smooth shading runs without someone choosing the angle: every processed merge, the
        // --smooth-normals command line and SurfaceRepair - all through RebuildNormals, so a merge
        // and a repair agree, and a repaired file doesn't read as needing repair once re-merged.
        public const float DefaultCreaseAngle = 60f;

        /// <summary>A rebuilt normal within this cosine of the old one doesn't count as changed.</summary>
        internal const float UnchangedDot = 0.9995f;

        /// <summary>
        /// How thin a face has to be - height over longest edge, as a fraction of that edge - before
        /// it can count as part of a pleat. Measured on every player model to hand, a tenth of the
        /// thinnest honest faces sit above this and every pleat face well below it.
        /// </summary>
        internal const float PleatAspect = 0.05f;

        /// <summary>How far a thin face has to lean off the surface round it to count as a pleat.</summary>
        internal const float PleatAngleDegrees = 60f;

        /// <summary>How far a surface direction spreads into the points that have none of their own.</summary>
        private const int SurfaceSpreadPasses = 3;

        public static int SmoothNormals(ModelRoot model, float creaseAngleDegrees)
        {
            int changed = 0;

            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc)) continue;

                var positions = posAcc.AsVector3Array().ToArray();
                var oldNormals = nAcc.AsVector3Array().ToArray();
                var idx = prim.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                if (positions.Length == 0 || idx.Length == 0) continue;

                var newNormals = RebuildNormals(positions, oldNormals, idx, creaseAngleDegrees);
                for (int v = 0; v < positions.Length; v++)
                    if (Vector3.Dot(newNormals[v], oldNormals[v]) < UnchangedDot) changed++;

                // Written over the existing accessor where it's plain floats: replacing it
                // would leave the old normals orphaned in the buffer, and saved files would grow
                // by a full normal array.
                if (nAcc.Encoding == EncodingType.FLOAT && !nAcc.Normalized && nAcc.IsSparse == false)
                {
                    var target = nAcc.AsVector3Array();
                    for (int v = 0; v < newNormals.Length; v++) target[v] = newNormals[v];
                    nAcc.UpdateBounds();
                }
                else prim.WithVertexAccessor("NORMAL", newNormals.ToList());
            }

            return changed;
        }

        // Each vertex gets the area-weighted average of the faces round its position - welded on
        // position only, so a UV seam doesn't leave a lighting seam - except faces bent further than
        // creaseAngleDegrees from the vertex's own side of the point: the area-weighted average of
        // the faces that vertex itself belongs to. That keeps genuinely hard edges (and back-to-back
        // shells sharing positions) sharp.
        //
        // Testing against the averaged side rather than against each own face matters at a fold:
        // a thin flap turned back over the surface is one of its corner's own faces, and tested
        // face by face it pulls in its own half of the fold and drags the normal round with it,
        // leaving a dark wedge. Against the average it's simply the outlier, and left out. Area
        // weighting likewise keeps a sliver from moving anything much.
        //
        // Area weighting is not enough on its own against a pleat: a seam left standing as a wall a
        // fraction of a millimetre high, running along the surface for centimetres. Each of its
        // faces is a needle - no thickness worth speaking of - but long, so its area is a match for
        // the real faces beside it and it drags their corners' normals round to face along the wall.
        // Those corners then shade near black whichever way the light comes from, which no amount of
        // painting reaches, and the wall opens up as the mesh is posed, so the dark grows with it.
        // Pleat faces are picked out by shape rather than size - a face thinner than PleatAspect that
        // also leans further than PleatAngleDegrees off the surface round it - and left out of every
        // average here. A corner with nothing but pleat faces takes the surface round its point, so
        // the wall shades as the surface it sits on and stops showing.
        //
        // A vertex with no face at all (unused) keeps its old normal.
        internal static Vector3[] RebuildNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> oldNormals,
            int[] idx, float creaseAngleDegrees) =>
            RebuildNormals(positions, oldNormals, idx, creaseAngleDegrees, null);

        /// <param name="pleatFaces">When given, filled with the triangles left out as pleats.</param>
        internal static Vector3[] RebuildNormals(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> oldNormals,
            int[] idx, float creaseAngleDegrees, HashSet<int>? pleatFaces)
        {
            float cosCrease = MathF.Cos(creaseAngleDegrees * MathF.PI / 180f);
            int vertexCount = positions.Count, triCount = idx.Length / 3;

            // Weld on a grid far finer than any real edge, so split copies of one point
            // (a UV seam) land together without bit-exact float equality.
            var min = positions.Aggregate(Vector3.Min);
            var max = positions.Aggregate(Vector3.Max);
            float cell = MathF.Max((max - min).Length() * 1e-6f, 1e-9f);
            var weld = new int[vertexCount];
            var weldIds = new Dictionary<(long, long, long), int>();
            for (int v = 0; v < vertexCount; v++)
            {
                var p = positions[v] / cell;
                var key = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z));
                if (!weldIds.TryGetValue(key, out int id)) weldIds[key] = id = weldIds.Count;
                weld[v] = id;
            }

            // Area-weighted face normals (the unnormalised cross product), their unit direction, and
            // how thin each face is: its height over its longest edge, as a fraction of that edge.
            // A line is 0 and an equilateral triangle about 0.87, so it says nothing about how big
            // the face is - which is the point, since a pleat's faces are long.
            var face = new Vector3[triCount];
            var unit = new Vector3[triCount];
            var thin = new bool[triCount];
            for (int t = 0; t < triCount; t++)
            {
                var a = positions[idx[t * 3]];
                var b = positions[idx[t * 3 + 1]];
                var c = positions[idx[t * 3 + 2]];
                face[t] = Vector3.Cross(b - a, c - a);
                float len = face[t].Length();
                unit[t] = len > 1e-20f ? face[t] / len : Vector3.Zero;
                float longest = MathF.Max((b - a).Length(), MathF.Max((c - b).Length(), (a - c).Length()));
                thin[t] = longest > 1e-20f && len / (longest * longest) < PleatAspect;
            }

            // Faces round each welded point (CSR).
            var start = new int[weldIds.Count + 1];
            for (int i = 0; i < idx.Length; i++) start[weld[idx[i]] + 1]++;
            for (int w = 0; w < weldIds.Count; w++) start[w + 1] += start[w];
            var faces = new int[idx.Length];
            var fill = (int[])start.Clone();
            for (int i = 0; i < idx.Length; i++) faces[fill[weld[idx[i]]]++] = i / 3;

            // Where the surface runs at each welded point, from the faces that aren't thin. A point
            // with none of its own borrows the direction of the points it shares a face with, a ring
            // at a time, so a pleat a few faces wide still has a surface to be measured against.
            var surface = new Vector3[weldIds.Count];
            for (int t = 0; t < triCount; t++)
            {
                if (thin[t]) continue;
                for (int k = 0; k < 3; k++) surface[weld[idx[t * 3 + k]]] += face[t];
            }
            for (int pass = 0; pass < SurfaceSpreadPasses; pass++)
            {
                var spread = new List<(int Point, Vector3 Normal)>();
                for (int w = 0; w < weldIds.Count; w++)
                {
                    if (surface[w].LengthSquared() > 1e-30f) continue;
                    var sum = Vector3.Zero;
                    for (int k = start[w]; k < start[w + 1]; k++)
                        for (int c = 0; c < 3; c++)
                        {
                            var near = surface[weld[idx[faces[k] * 3 + c]]];
                            if (near.LengthSquared() > 1e-30f) sum += Vector3.Normalize(near);
                        }
                    if (sum.LengthSquared() > 1e-30f) spread.Add((w, sum));
                }
                if (spread.Count == 0) break;
                foreach (var (point, normal) in spread) surface[point] = normal;
            }

            // A pleat face: thin, and leaning off the surface at every corner that knows where the
            // surface is. Thin on its own is ordinary - generated meshes are full of thin faces that
            // lie flat - and leaning on its own is just a hard edge; it takes both.
            float cosPleat = MathF.Cos(PleatAngleDegrees * MathF.PI / 180f);
            var pleat = new bool[triCount];
            for (int t = 0; t < triCount; t++)
            {
                if (!thin[t] || unit[t] == Vector3.Zero) continue;
                float flattest = -1f;
                for (int k = 0; k < 3; k++)
                {
                    var s = surface[weld[idx[t * 3 + k]]];
                    if (s.LengthSquared() < 1e-30f) continue;
                    flattest = MathF.Max(flattest, MathF.Abs(Vector3.Dot(unit[t], Vector3.Normalize(s))));
                }
                if (flattest < 0f || flattest >= cosPleat) continue;
                pleat[t] = true;
                pleatFaces?.Add(t);
            }

            // The summed normal of each vertex's own faces, pleats left out.
            var own = new Vector3[vertexCount];
            var anyFace = new bool[vertexCount];
            for (int i = 0; i < idx.Length; i++)
            {
                anyFace[idx[i]] = true;
                if (!pleat[i / 3]) own[idx[i]] += face[i / 3];
            }

            var result = oldNormals.ToArray();
            for (int v = 0; v < vertexCount; v++)
            {
                if (!anyFace[v]) continue;
                int w = weld[v];
                if (own[v].LengthSquared() < 1e-30f)
                {
                    // Nothing but pleat faces (or nothing but degenerate ones): no side of its own to
                    // measure a crease against, so it takes the surface its point sits on.
                    if (surface[w].LengthSquared() > 1e-30f) result[v] = Vector3.Normalize(surface[w]);
                    continue;
                }
                var side = Vector3.Normalize(own[v]);

                var sum = Vector3.Zero;
                // A face reaches a point through two corners only if it's degenerate; counting it
                // twice then costs nothing, since its area is ~0.
                for (int k = start[w]; k < start[w + 1]; k++)
                {
                    int t = faces[k];
                    if (pleat[t]) continue;
                    if (Vector3.Dot(unit[t], side) >= cosCrease) sum += face[t];
                }
                if (sum.LengthSquared() > 1e-30f) result[v] = Vector3.Normalize(sum);
            }
            return result;
        }
    }
}
