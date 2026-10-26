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
        // lights as visible facets. Each vertex gets the angle-weighted average of the faces around
        // its position - welded on position only, so a UV seam doesn't leave a lighting seam -
        // except faces bent further than creaseAngleDegrees from its own face, which keeps
        // genuinely hard edges (and back-to-back shells sharing positions) sharp. Positions, UVs,
        // indices and skinning are never touched. Returns how many vertices changed noticeably.
        // Used wherever smooth shading runs without someone choosing the angle: every processed
        // merge, and the --smooth-normals command line.
        public const float DefaultCreaseAngle = 60f;

        public static int SmoothNormals(ModelRoot model, float creaseAngleDegrees)
        {
            float cosCrease = MathF.Cos(creaseAngleDegrees * MathF.PI / 180f);
            int changed = 0;

            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc)) continue;

                var positions = posAcc.AsVector3Array().ToArray();
                var oldNormals = nAcc.AsVector3Array().ToArray();
                var tris = prim.GetTriangleIndices().ToArray();
                if (positions.Length == 0 || tris.Length == 0) continue;

                // Weld on a grid far finer than any real edge, so split copies of one point
                // (a UV seam) land together without bit-exact float equality.
                var min = positions.Aggregate(Vector3.Min);
                var max = positions.Aggregate(Vector3.Max);
                float cell = MathF.Max((max - min).Length() * 1e-6f, 1e-9f);
                var weld = new int[positions.Length];
                var weldIds = new Dictionary<(long, long, long), int>();
                for (int v = 0; v < positions.Length; v++)
                {
                    var p = positions[v] / cell;
                    var key = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z));
                    if (!weldIds.TryGetValue(key, out int id)) weldIds[key] = id = weldIds.Count;
                    weld[v] = id;
                }

                // Per welded point: each incident face's unit normal and its angle-weighted share.
                var faceNormal = new Vector3[tris.Length];
                var incident = new List<(int Tri, Vector3 Weighted)>[weldIds.Count];
                var vertexTris = new List<int>[positions.Length];
                for (int t = 0; t < tris.Length; t++)
                {
                    var (ia, ib, ic) = tris[t];
                    Vector3 a = positions[ia], b = positions[ib], c = positions[ic];
                    var cross = Vector3.Cross(b - a, c - a);
                    if (cross.LengthSquared() < 1e-30f) continue;
                    var n = Vector3.Normalize(cross);
                    faceNormal[t] = n;

                    AddCorner(ia, a, b, c);
                    AddCorner(ib, b, c, a);
                    AddCorner(ic, c, a, b);

                    void AddCorner(int v, Vector3 at, Vector3 e1, Vector3 e2)
                    {
                        var d1 = e1 - at; var d2 = e2 - at;
                        float len = MathF.Sqrt(d1.LengthSquared() * d2.LengthSquared());
                        float angle = len > 0 ? MathF.Acos(Math.Clamp(Vector3.Dot(d1, d2) / len, -1f, 1f)) : 0f;
                        (incident[weld[v]] ??= new()).Add((t, n * angle));
                        (vertexTris[v] ??= new()).Add(t);
                    }
                }

                var newNormals = new Vector3[positions.Length];
                for (int v = 0; v < positions.Length; v++)
                {
                    var sum = Vector3.Zero;
                    if (vertexTris[v] != null)
                    {
                        // A vertex shared by several of its own faces averages what each of them
                        // would see, so it can't pick up a face only one of them is near.
                        foreach (int own in vertexTris[v])
                            foreach (var (other, weighted) in incident[weld[v]])
                                if (Vector3.Dot(faceNormal[own], faceNormal[other]) >= cosCrease)
                                    sum += weighted;
                    }
                    newNormals[v] = sum.LengthSquared() > 1e-20f ? Vector3.Normalize(sum) : oldNormals[v];
                    if (Vector3.Dot(newNormals[v], oldNormals[v]) < 0.9995f) changed++;
                }

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
    }
}
