using System;
using System.Linq;
using System.Numerics;

namespace GlbMerger
{
    // The painted mouth as the five points picked on it (FaceBonesEditor.MouthPoints.cs), corner to corner
    // in order of x, and what the face rig makes of them (FaceBoneRigger): one bone on each point, and each
    // vertex near the mouth weighted to them by where it lies along the curve and how far it is from it.
    //
    // Along: the vertex's nearest point on the polyline through the five, as a position s from 0 (first
    // corner) to 4 (last); bone k's share is a smooth hat round k, so neighbours always sum to one and the
    // line between two points moves as a blend of the two. Off the curve: full within `core` of `reach`
    // (mouth units: the region's half-height), fading to none at `reach`, and through the region's depth
    // slab round the curve point. Past a corner the nearest point is the corner itself, so the falloff
    // rounds off there.
    internal sealed class MouthCurve
    {
        public const int PointCount = 5;

        private readonly Vector3[] _p;

        public MouthCurve(Vector3[] points)
        {
            if (points.Length != PointCount) throw new ArgumentException("A mouth curve takes exactly five points.");
            _p = points.OrderBy(p => p.X).ToArray();
        }

        public Vector3 this[int k] => _p[k];
        public float MidX => 0.5f * (_p[0].X + _p[PointCount - 1].X);
        public float HalfSpan => Math.Max(0.5f * (_p[PointCount - 1].X - _p[0].X), 1e-6f);

        // Where point k sits across the mouth: -1 at the -x corner, +1 at the +x one.
        public float Across(int k) => (_p[k].X - MidX) / HalfSpan;

        // Where x falls across the mouth: -1 .. 1 corner to corner, beyond past them.
        public float AcrossAt(float x) => (x - MidX) / HalfSpan;

        // The lip line straight up or down from x: the polyline's point there, held level past the corners.
        public Vector3 PointAt(float x)
        {
            if (x <= _p[0].X) return _p[0];
            for (int i = 0; i < PointCount - 1; i++)
                if (x <= _p[i + 1].X)
                    return Vector3.Lerp(_p[i], _p[i + 1], (x - _p[i].X) / Math.Max(_p[i + 1].X - _p[i].X, 1e-6f));
            return _p[PointCount - 1];
        }

        // The five a region alone implies, for a mouth no one has picked points on: corners at 90% of its
        // half-width, the others halfway in, all level with its centre.
        public static Vector3[] FromRegion(Vector3 centre, Vector3 radii)
        {
            float[] across = { -0.9f, -0.45f, 0f, 0.45f, 0.9f };
            return across.Select(a => centre + new Vector3(a * radii.X, 0f, 0f)).ToArray();
        }

        // Bone k's weight at vertex v. `back`/`front`: the region's depth behind / in front of the curve.
        public float Weight(Vector3 v, int k, float reach, float back, float front, float core)
        {
            var (s, curvePoint) = Project(v);
            float hat = 1f - SmoothStep(0f, 1f, Math.Abs(s - k));
            if (hat <= 0f) return 0f;

            var offset = v - curvePoint;
            float off = MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y) / Math.Max(reach, 1e-6f);
            float depth = offset.Z >= 0f ? offset.Z / Math.Max(front, 1e-6f) : -offset.Z / Math.Max(back, 1e-6f);
            return hat * (1f - SmoothStep(core, 1f, off)) * (1f - SmoothStep(0.6f, 1f, depth));
        }

        // The nearest point on the polyline (in x/y) and its position along it, 0..4.
        private (float S, Vector3 Point) Project(Vector3 v)
        {
            float bestD = float.MaxValue, bestS = 0f;
            Vector3 bestP = _p[0];
            for (int i = 0; i < PointCount - 1; i++)
            {
                var a = _p[i]; var ab = _p[i + 1] - a;
                float len2 = ab.X * ab.X + ab.Y * ab.Y;
                float t = len2 > 0f ? Math.Clamp(((v.X - a.X) * ab.X + (v.Y - a.Y) * ab.Y) / len2, 0f, 1f) : 0f;
                var p = a + ab * t;
                float d = (v.X - p.X) * (v.X - p.X) + (v.Y - p.Y) * (v.Y - p.Y);
                if (d < bestD) { bestD = d; bestS = i + t; bestP = p; }
            }
            return (bestS, bestP);
        }

        private static float SmoothStep(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / Math.Max(b - a, 1e-6f), 0f, 1f);
            return t * t * (3f - 2f * t);
        }
    }
}
