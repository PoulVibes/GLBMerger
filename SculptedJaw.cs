using System;
using System.Numerics;

namespace GlbMerger
{
    // The jaw for a model whose lips are sculpted into the mesh (FaceBonesEditor "Sculpted lips"): the chin
    // bone takes everything below the mouth's line, the lower lip included, so dropping it parts the real
    // lips -- the triangles bridging them stretch open, and the game draws the inside of the mouth there
    // (shaders/MouthOverlay.glsl, MouthSculpted). The upper lip stays with the head and the mouth bones.
    //
    // Along the mouth the split is clean: nothing just above the line, all of it just below. Toward the
    // corners it eases into a gentle ramp down from the line, so they stay joined and the cheeks stretch
    // rather than tear; past them the ramp carries on along the line held level. Across, the chin region's
    // width fades it out (Weight core); down, it holds to just past the region's bottom, then fades; and
    // through the depth slab round the lip line, so the back of the head is left alone.
    internal sealed class SculptedJaw
    {
        private const float SplitHalf = 0.0005f;   // the split's antialias, mesh units

        private readonly MouthCurve _curve;
        private readonly Vector3 _chinCentre, _chinRadii;
        private readonly float _front, _core;

        public SculptedJaw(MouthCurve curve, Vector3 chinCentre, Vector3 chinRadii, float chinFront, float core)
        {
            _curve = curve;
            _chinCentre = chinCentre;
            _chinRadii = chinRadii;
            _front = chinFront;
            _core = core;
        }

        public float Weight(Vector3 v)
        {
            var line = _curve.PointAt(v.X);
            float below = line.Y - v.Y;   // > 0 under the lip line
            float split = SmoothStep(-SplitHalf, SplitHalf, below);
            float ramp = SmoothStep(0f, _chinRadii.Y * 0.6f, below);
            float toCorner = SmoothStep(0.6f, 1f, MathF.Abs(_curve.AcrossAt(v.X)));
            float vertical = split + (ramp - split) * toCorner;

            float across = 1f - SmoothStep(_core, 1f, MathF.Abs(v.X - _chinCentre.X) / _chinRadii.X);
            float down = (_chinCentre.Y - v.Y) / _chinRadii.Y;   // +1 at the region's bottom edge
            float bottom = 1f - SmoothStep(1f, 1.4f, down);
            float dz = v.Z - line.Z;
            float depth = dz >= 0f ? dz / Math.Max(_front, 1e-6f) : -dz / Math.Max(_chinRadii.Z, 1e-6f);
            return vertical * across * bottom * (1f - SmoothStep(0.6f, 1f, depth));
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / Math.Max(b - a, 1e-6f), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
