using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GlbMerger
{
    // Ear-clipping triangulation of a planar polygon with holes: an outer ring and any number of
    // inner rings become n - 2 triangles over the rings' own vertices, with no vertex added.
    //
    // This is what seals a planar gap (WatertightRepair's "seal planar gaps") with as few
    // triangles as possible, where the centroid fan the ordinary hole fill uses would add a vertex
    // and spend n triangles on a ring that is flat anyway. Holes are joined to the outer ring
    // through a bridge - a doubled-up edge from a hole vertex to a mutually visible outer vertex -
    // which turns the whole thing into one simple polygon that ear clipping handles as usual.
    //
    // Input is 2D: the caller projects the ring onto its plane. Winding of the input doesn't
    // matter (rings are normalized here); the triangles come back with a consistent 2D winding,
    // and the caller orients them in 3D however its surface needs.
    internal static class PolygonTriangulator
    {
        // Returns triangles as index triples into the concatenation of outer followed by each
        // hole in order. Null when the outer ring is degenerate.
        public static List<(int A, int B, int C)>? Triangulate(IReadOnlyList<Vector2> outer, IReadOnlyList<IReadOnlyList<Vector2>> holes)
        {
            if (outer.Count < 3) return null;

            var points = new List<Vector2>(outer);
            var polygon = Enumerable.Range(0, outer.Count).ToList();
            if (SignedArea(points, polygon) < 0) polygon.Reverse();
            if (MathF.Abs(SignedArea(points, polygon)) < 1e-12f) return null;

            // Holes, clockwise, largest-x first - the standard order, so a bridge from one hole
            // never has to cross another hole that hasn't been spliced in yet.
            var pendingHoles = new List<List<int>>();
            foreach (var hole in holes)
            {
                if (hole.Count < 3) continue;
                int offset = points.Count;
                points.AddRange(hole);
                var ring = Enumerable.Range(offset, hole.Count).ToList();
                if (SignedArea(points, ring) > 0) ring.Reverse();
                pendingHoles.Add(ring);
            }
            pendingHoles.Sort((a, b) => b.Max(i => points[i].X).CompareTo(a.Max(i => points[i].X)));

            while (pendingHoles.Count > 0)
            {
                var hole = pendingHoles[0];
                pendingHoles.RemoveAt(0);
                if (!Bridge(points, polygon, hole, pendingHoles)) continue;   // unreachable hole: left unfilled inside the cap
            }

            return ClipEars(points, polygon);
        }

        private static float SignedArea(List<Vector2> points, List<int> ring)
        {
            float area = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = points[ring[i]];
                var b = points[ring[(i + 1) % ring.Count]];
                area += a.X * b.Y - b.X * a.Y;
            }
            return area * 0.5f;
        }

        // Splices a hole into the polygon through the closest pair of (hole vertex, polygon vertex)
        // whose connecting segment crosses no edge of the polygon, the hole, or any hole still
        // waiting. The pair is duplicated so the bridge is walked in both directions.
        private static bool Bridge(List<Vector2> points, List<int> polygon, List<int> hole, List<List<int>> otherHoles)
        {
            var candidates = new List<(float DistSq, int HolePos, int PolyPos)>();
            for (int h = 0; h < hole.Count; h++)
                for (int p = 0; p < polygon.Count; p++)
                    candidates.Add((Vector2.DistanceSquared(points[hole[h]], points[polygon[p]]), h, p));
            candidates.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

            foreach (var (_, h, p) in candidates)
            {
                var from = points[hole[h]];
                var to = points[polygon[p]];
                if (CrossesAny(points, polygon, from, to, hole[h], polygon[p])) continue;
                if (CrossesAny(points, hole, from, to, hole[h], polygon[p])) continue;
                bool blocked = false;
                foreach (var other in otherHoles)
                    if (CrossesAny(points, other, from, to, hole[h], polygon[p])) { blocked = true; break; }
                if (blocked) continue;

                // ... polygon[p], hole[h], hole[h+1], ..., hole[h-1], hole[h], polygon[p], polygon[p+1] ...
                var spliced = new List<int>(polygon.Count + hole.Count + 2);
                spliced.AddRange(polygon.Take(p + 1));
                for (int k = 0; k <= hole.Count; k++) spliced.Add(hole[(h + k) % hole.Count]);
                spliced.Add(polygon[p]);
                spliced.AddRange(polygon.Skip(p + 1));
                polygon.Clear();
                polygon.AddRange(spliced);
                return true;
            }
            return false;
        }

        // Whether segment from-to properly crosses any edge of the ring, ignoring edges that end at
        // either of the segment's own endpoints (by index, and by position, since bridges duplicate).
        private static bool CrossesAny(List<Vector2> points, List<int> ring, Vector2 from, Vector2 to, int fromIndex, int toIndex)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                int ia = ring[i], ib = ring[(i + 1) % ring.Count];
                if (ia == fromIndex || ib == fromIndex || ia == toIndex || ib == toIndex) continue;
                var a = points[ia];
                var b = points[ib];
                if (a == from || a == to || b == from || b == to) continue;
                if (SegmentsIntersect(from, to, a, b)) return true;
            }
            return false;
        }

        private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        // Standard ear clipping over a counter-clockwise simple polygon (bridges included). When
        // numerical trouble leaves no clean ear, the flattest corner is clipped anyway rather than
        // stopping short - a slightly wrong sliver beats an unfilled gap.
        private static List<(int A, int B, int C)> ClipEars(List<Vector2> points, List<int> polygon)
        {
            var triangles = new List<(int, int, int)>();
            var ring = new List<int>(polygon);

            while (ring.Count > 3)
            {
                int n = ring.Count;
                int best = -1;
                float flattest = float.MaxValue;
                bool found = false;

                for (int i = 0; i < n && !found; i++)
                {
                    int ip = ring[(i + n - 1) % n], ic = ring[i], inx = ring[(i + 1) % n];
                    var a = points[ip]; var b = points[ic]; var c = points[inx];
                    float cross = Cross(b - a, c - b);
                    if (MathF.Abs(cross) < flattest) { flattest = MathF.Abs(cross); best = i; }
                    if (cross <= 0) continue;   // reflex corner: not an ear

                    bool empty = true;
                    for (int j = 0; j < n; j++)
                    {
                        int io = ring[j];
                        if (io == ip || io == ic || io == inx) continue;
                        var o = points[io];
                        if (o == a || o == b || o == c) continue;   // a bridge duplicate of a corner
                        if (PointInTriangle(o, a, b, c)) { empty = false; break; }
                    }
                    if (!empty) continue;

                    triangles.Add((ip, ic, inx));
                    ring.RemoveAt(i);
                    found = true;
                }

                if (!found)
                {
                    int i = best < 0 ? 0 : best;
                    int n2 = ring.Count;
                    var tri = (ring[(i + n2 - 1) % n2], ring[i], ring[(i + 1) % n2]);
                    if (MathF.Abs(Cross(points[tri.Item2] - points[tri.Item1], points[tri.Item3] - points[tri.Item2])) > 1e-12f)
                        triangles.Add(tri);
                    ring.RemoveAt(i);
                }
            }

            if (ring.Count == 3) triangles.Add((ring[0], ring[1], ring[2]));
            return triangles;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a);
            float d2 = Cross(c - b, p - b);
            float d3 = Cross(a - c, p - c);
            bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
            bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNeg && hasPos);
        }
    }
}
