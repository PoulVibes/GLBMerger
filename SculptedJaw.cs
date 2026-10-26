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
    //
    // Lips modelled apart (a lower lip picked too): the jaw starts at the lower lip's edge instead, and the
    // band between the two lines (the teeth painted there) ramps evenly from none at the upper line to all
    // at the lower one, so raising the lower lip by the gap squashes the band flat into the upper line.
    // Toward the corners the ramp is never sharper than the one above, so they still stretch, not tear.
    internal sealed class SculptedJaw
    {
        private const float SplitHalf = 0.0005f;   // the split's antialias, mesh units

        private readonly MouthCurve _curve;
        private readonly MouthCurve? _lower;
        private readonly Vector3 _chinCentre, _chinRadii;
        private readonly float _front, _core;

        public SculptedJaw(MouthCurve curve, MouthCurve? lower, Vector3 chinCentre, Vector3 chinRadii, float chinFront, float core)
        {
            _curve = curve;
            _lower = lower;
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
            if (_lower != null)
            {
                // Over exactly the distance the lower lip's bones close at x, so the band ends on the upper line.
                float band = Math.Max(CloseAt(v.X), toCorner * _chinRadii.Y * 0.6f);
                vertical = Math.Clamp(below / Math.Max(band, 1e-6f), 0f, 1f);
            }

            float across = 1f - SmoothStep(_core, 1f, MathF.Abs(v.X - _chinCentre.X) / _chinRadii.X);
            // The band between the lips is the mouth's, out to its corners, however narrow the chin.
            if (_lower != null) across = Math.Max(across, LowerLipHold(v, _chinRadii.Y * 0.6f) * (1f - SmoothStep(1f, 1.15f, MathF.Abs(_curve.AcrossAt(v.X)))));
            float down = (_chinCentre.Y - v.Y) / _chinRadii.Y;   // +1 at the region's bottom edge
            float bottom = 1f - SmoothStep(1f, 1.4f, down);
            float dz = v.Z - line.Z;
            float depth = dz >= 0f ? dz / Math.Max(_front, 1e-6f) : -dz / Math.Max(_chinRadii.Z, 1e-6f);
            return vertical * across * bottom * (1f - SmoothStep(0.6f, 1f, depth));
        }

        // How far lower-lip bone k goes up to shut the mouth: from its point straight up to the upper lip,
        // mesh units (0 without a lower lip).
        public float Close(int k) =>
            _lower == null ? 0f : Math.Max(_curve.PointAt(_lower[k].X).Y - _lower[k].Y, 0f);

        // What the lower lip's bones close at x, blended as they're weighted (MouthCurve.ShareAt).
        public float CloseAt(float x)
        {
            if (_lower == null) return 0f;
            float c = 0f;
            for (int k = 0; k < MouthCurve.PointCount; k++) c += _lower.ShareAt(x, k) * Close(k);
            return c;
        }

        // Bone k of the lower lip's share at vertex v.
        public float LowerShare(Vector3 v, int k) => _lower == null ? 0f : _lower.ShareAt(v.X, k);

        // Below the band by this much or more, the lower lip's bones hand vertex v on to the chin as usual;
        // in the band and just under it, all of the jaw's share is theirs, so it closes.
        public float LowerLipHold(Vector3 v, float fade)
        {
            if (_lower == null) return 0f;
            float underEdge = _curve.PointAt(v.X).Y - CloseAt(v.X) - v.Y;
            return 1f - SmoothStep(0f, fade, underEdge);
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / Math.Max(b - a, 1e-6f), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
