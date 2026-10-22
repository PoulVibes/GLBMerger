using System;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Converts between a node's raw local translation units and the
    // model-independent "display" units the editors' X/Y/Z fields show.
    //
    // Rigs don't agree on units: the older models' bones are authored in
    // centimetres under an Armature scaled ~0.01, while newer Meshy exports
    // (e.g. Mateo) put bones in metres with no Armature scale at all - so the
    // same raw "+1" on a bone-local offset moved the ball ~1 cm on one model
    // and ~1 m on the other. Every bone-space position field instead shows
    // world-space centimetres (raw value x the parent node's accumulated
    // world scale x 100), which lands at ~1:1 with the old models' raw
    // numbers and makes the same entry move every model by the same amount.
    // Stored/saved values stay in raw local units; only the fields convert.
    public static class ModelUnits
    {
        private const float CentimetresPerMetre = 100f;

        // Display units per raw local unit for a translation expressed in
        // 'parent's local space (i.e. a child of 'parent', or 'parent's own
        // children's offsets). A null parent means a root-level node, whose
        // translation is already in world metres.
        public static float DisplayPerLocal(Node? parent)
        {
            if (parent == null) return CentimetresPerMetre;
            var m = parent.WorldMatrix;
            // Uniform-scale rigs are the norm; average the three axes so a
            // slightly non-uniform rig still gets a sensible single factor.
            float sx = new Vector3(m.M11, m.M12, m.M13).Length();
            float sy = new Vector3(m.M21, m.M22, m.M23).Length();
            float sz = new Vector3(m.M31, m.M32, m.M33).Length();
            float s = (sx + sy + sz) / 3f;
            return s > 1e-9f && float.IsFinite(s) ? s * CentimetresPerMetre : CentimetresPerMetre;
        }
    }
}
