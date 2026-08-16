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
    }
}
