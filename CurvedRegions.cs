using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Upright curved surfaces - a round bay, a rounded corner tower - found by Hough voting.
    //
    // Billboards approximate a curve with a few flat planes, and from below every one of them
    // shows it: the bands and cornices round a bay turn into chevrons and crosses, the gaps between
    // facets show sky. Found here, such a region is kept as (simplified) real mesh instead.
    //
    // Every upright face of a vertical cylinder of radius r around axis c satisfies
    // c = p - r * n (p its centre, n its horizontal normal), so each face votes, for every radius,
    // for the axis position that would explain it. A real curve's faces pile their votes on one
    // cell; flat walls spread theirs along a line, and ornament scatters. Works in the building's
    // own frame (FrameX, FrameZ); only convex curves (normals pointing away from the axis).
    public static class CurvedRegions
    {
        public sealed class Cylinder
        {
            public Vector2 Centre;          // (FrameX, FrameZ) coordinates
            public float Radius;
            public float AngleMin, AngleMax;  // radians, atan2(z, x) of the support round the centre
            public float YMin, YMax;
            public float SupportArea;
            public List<int> Support = new();
            // Everything in the curve's wedge: the surface, and what's on it or set into it.
            public List<int> Region = new();
        }

        // Something a strip following an outline in plan draws better than flat planes do: a
        // round curve (a cylinder, above) or a canted bay (DetectBays).
        public sealed class Fold
        {
            public ModelFlattener.CurveFrame Frame = new();   // at the surface
            public float YMin, YMax;
            public List<int> Region = new();
            // Arc length at each end that stays with the planes too (see ModelFlattener).
            public float EndMargin;
            // A canted bay: what faces well away from its run stays with the planes as well.
            public bool Shares;
        }

        public static List<Fold> DetectFolds(ModelFlattener.FlattenInput input)
        {
            var folds = new List<Fold>();
            float ext = input.Extent;
            foreach (var cyl in Detect(input))
            {
                // The arc, a little past the support at each end, in pieces short enough that
                // their chords stay within a fraction of a percent of it.
                float pad = ext * 0.01f / MathF.Max(cyl.Radius, 1e-6f);
                float a0 = cyl.AngleMin - pad, a1 = cyl.AngleMax + pad;
                float chord = ext * 0.002f;
                float step = MathF.Min(2f * MathF.Acos(MathF.Max(1f - chord / cyl.Radius, -1f)), 15f * MathF.PI / 180f);
                int pieces = Math.Max(1, (int)MathF.Ceiling((a1 - a0) / step));
                var frame = new ModelFlattener.CurveFrame { FrameX = input.FrameX, FrameZ = input.FrameZ };
                // Increasing angle keeps out (away from the axis) on the right.
                for (int i = 0; i <= pieces; i++)
                {
                    float a = a0 + (a1 - a0) * i / pieces;
                    frame.Points.Add(cyl.Centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * cyl.Radius);
                }
                folds.Add(new Fold
                {
                    Frame = frame, YMin = cyl.YMin, YMax = cyl.YMax, Region = cyl.Region,
                    EndMargin = MathF.Min(ext * 0.04f, cyl.Radius * 0.35f),
                });
            }
            var taken = new HashSet<int>(folds.SelectMany(f => f.Region));
            folds.AddRange(DetectBays(input, taken));
            return folds;
        }

        // Canted bays: stretches of a facade standing out in front of its main wall with
        // slanted sides. Found from their slanted faces (sides turned 20-70 degrees from the
        // facade), grouped where they share heights and lie within a bay's width of each
        // other; the outline is the stretch's profile in plan (the median depth of what faces
        // out, across it), simplified to a few straight runs, from the wall out and back.
        private static List<Fold> DetectBays(ModelFlattener.FlattenInput input, HashSet<int> taken)
        {
            var folds = new List<Fold>();
            float ext = input.Extent, cell = ext * 0.01f, stand = ext * 0.02f;
            Vector3 Centroid(int t) => (input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f;
            float cos20 = MathF.Cos(20f * MathF.PI / 180f), cos70 = MathF.Cos(70f * MathF.PI / 180f);
            foreach (var (d, l) in new[] { (input.FrameZ, input.FrameX), (-input.FrameZ, input.FrameX), (input.FrameX, input.FrameZ), (-input.FrameX, input.FrameZ) })
            {
                // The main wall: where most of what faces this way is.
                float bin = ext * 0.005f;
                var hist = new Dictionary<int, float>();
                for (int t = 0; t < input.TriangleCount; t++)
                    if (input.Areas[t] > 0 && Vector3.Dot(input.Normals[t], d) > 0.9f)
                    {
                        int k = (int)MathF.Floor(Vector3.Dot(Centroid(t), d) / bin);
                        hist[k] = hist.GetValueOrDefault(k) + input.Areas[t];
                    }
                if (hist.Count == 0) continue;
                float wall = (hist.MaxBy(kv => kv.Value).Key + 0.5f) * bin;

                // Slanted faces standing out in front of it, gridded by (across, height).
                var slanted = new Dictionary<(int U, int Y), List<int>>();
                for (int t = 0; t < input.TriangleCount; t++)
                {
                    if (input.Areas[t] <= 0 || taken.Contains(t) || MathF.Abs(input.Normals[t].Y) > 0.3f) continue;
                    var c = Centroid(t);
                    if (Vector3.Dot(c, d) < wall + stand * 0.5f) continue;
                    var h = input.Normals[t] - Vector3.UnitY * input.Normals[t].Y;
                    h = Vector3.Normalize(h);
                    float facing = Vector3.Dot(h, d);
                    if (facing > cos20 || facing < cos70) continue;
                    var key = ((int)MathF.Floor(Vector3.Dot(c, l) / cell), (int)MathF.Floor(c.Y / cell));
                    if (!slanted.TryGetValue(key, out var list)) slanted[key] = list = new List<int>();
                    list.Add(t);
                }
                // Grouped: a bay's two sides are a bay's width apart, at the same heights.
                int reach = (int)MathF.Ceiling(ext * 0.2f / cell), rise = 2;
                var seen = new HashSet<(int, int)>();
                foreach (var start in slanted.Keys)
                {
                    if (!seen.Add(start)) continue;
                    var group = new List<(int U, int Y)> { start };
                    for (int i = 0; i < group.Count; i++)
                        foreach (var other in slanted.Keys)
                            if (!seen.Contains(other) && Math.Abs(other.U - group[i].U) <= reach && Math.Abs(other.Y - group[i].Y) <= rise)
                            {
                                seen.Add(other);
                                group.Add(other);
                            }
                    var sides = group.SelectMany(k => slanted[k]).ToList();
                    if (sides.Sum(t => input.Areas[t]) < input.TotalArea * 0.002f) continue;
                    // Both sides, each a flat facet: slanted faces turned one way (a railing's
                    // round bars, a fire escape) point every which way.
                    bool Facet(IEnumerable<int> faces)
                    {
                        var list = faces.ToList();
                        float area = list.Sum(t => input.Areas[t]);
                        if (area < input.TotalArea * 0.001f) return false;
                        var sum = Vector3.Zero;
                        foreach (int t in list) sum += (input.Normals[t] - Vector3.UnitY * input.Normals[t].Y) * input.Areas[t];
                        return sum.Length() >= 0.9f * area;
                    }
                    if (!Facet(sides.Where(t => Vector3.Dot(input.Normals[t], l) > 0)) || !Facet(sides.Where(t => Vector3.Dot(input.Normals[t], l) < 0))) continue;
                    float u0 = sides.Min(t => Vector3.Dot(Centroid(t), l)), u1 = sides.Max(t => Vector3.Dot(Centroid(t), l));
                    float y0 = sides.Min(t => Centroid(t).Y), y1 = sides.Max(t => Centroid(t).Y);
                    if (u1 - u0 < ext * 0.06f || u1 - u0 > ext * 0.35f || y1 - y0 < ext * 0.08f) continue;
                    // A little past the sides, but never past the building's own edges.
                    float lMin = float.MaxValue, lMax = float.MinValue;
                    foreach (var corner in input.Corners) { float u = Vector3.Dot(corner, l); lMin = MathF.Min(lMin, u); lMax = MathF.Max(lMax, u); }
                    u0 = MathF.Max(u0 - cell, lMin);
                    u1 = MathF.Min(u1 + cell, lMax);

                    // Everything in front of the wall over the bay's stretch and heights.
                    var region = new List<int>();
                    for (int t = 0; t < input.TriangleCount; t++)
                    {
                        if (input.Areas[t] <= 0 || taken.Contains(t)) continue;
                        var c = Centroid(t);
                        float u = Vector3.Dot(c, l);
                        if (u < u0 - cell || u > u1 + cell || c.Y < y0 - cell || c.Y > y1 + cell) continue;
                        if (Vector3.Dot(c, d) < wall - cell) continue;
                        region.Add(t);
                    }

                    // Solid: seen straight on, what stands out covers most of the stretch - a fire
                    // escape's rails and stairs leave it mostly open.
                    float covered = region.Where(t => Vector3.Dot(Centroid(t), d) > wall + stand * 0.5f)
                        .Sum(t => input.Areas[t] * MathF.Max(0f, Vector3.Dot(input.Normals[t], d)));
                    if (covered < 0.5f * (u1 - u0) * (y1 - y0)) continue;

                    // Profile: per strip across, the area-weighted median depth of what faces out.
                    var profile = new List<Vector2> { new(u0, wall) };
                    int columns = Math.Max(1, (int)MathF.Ceiling((u1 - u0) / cell));
                    for (int ci = 0; ci < columns; ci++)
                    {
                        float ua = u0 + ci * cell, ub = ua + cell;
                        var faces = region.Where(t =>
                        {
                            float u = Vector3.Dot(Centroid(t), l);
                            return u >= ua && u < ub && MathF.Abs(input.Normals[t].Y) < 0.5f && Vector3.Dot(input.Normals[t], d) > 0.2f
                                && Vector3.Dot(Centroid(t), d) > wall + stand * 0.5f;
                        }).Select(t => (D: Vector3.Dot(Centroid(t), d), W: input.Areas[t])).OrderBy(x => x.D).ToList();
                        if (faces.Count == 0) continue;
                        float half = faces.Sum(x => x.W) / 2, acc = 0, median = faces[^1].D;
                        foreach (var (dep, w) in faces) if ((acc += w) >= half) { median = dep; break; }
                        profile.Add(new Vector2((ua + ub) / 2, median));
                    }
                    profile.Add(new Vector2(u1, wall));
                    profile = Simplify(profile, ext * 0.01f);
                    // A canted bay, not just a slab standing out: some run has to slant.
                    bool slants = false;
                    for (int i = 0; i + 1 < profile.Count; i++)
                    {
                        var run = profile[i + 1] - profile[i];
                        float along = MathF.Abs(run.X) / MathF.Max(run.Length(), 1e-9f);
                        if (run.Length() >= ext * 0.03f && along < cos20 && along > cos70) slants = true;
                    }
                    // One bay standing clear of the wall, not a zigzag along a whole facade.
                    if (!slants || profile.Count < 3 || profile.Count > 8 || profile.Max(p => p.Y) - wall < ext * 0.025f) continue;
                    // One bump: out to its front and back again, no dips (a fire escape's
                    // balconies and stairs zigzag in and out).
                    int top = profile.IndexOf(profile.MaxBy(p => p.Y));
                    bool bump = true;
                    for (int i = 1; i <= top; i++) if (profile[i].Y < profile[i - 1].Y - cell) bump = false;
                    for (int i = top + 1; i < profile.Count; i++) if (profile[i].Y > profile[i - 1].Y + cell) bump = false;
                    if (!bump) continue;

                    // The bay ends where its slanted sides meet the wall. Whatever the profile
                    // runs along past them is something else standing out (a corner pilaster,
                    // a string course), and drawn on the strip it bent the strip's end out past
                    // the building's corner. So each end is cut back to its outermost slanted
                    // run, carried on to the wall.
                    profile = TrimToSlants(profile, wall, ext * 0.03f, cos20, cos70);
                    float t0 = profile[0].X, t1 = profile[^1].X;
                    region = region.Where(t => { float u = Vector3.Dot(Centroid(t), l); return u >= t0 - cell && u <= t1 + cell; }).ToList();

                    // (across, depth) to plan, and out on the right going along.
                    var frame = new ModelFlattener.CurveFrame { FrameX = input.FrameX, FrameZ = input.FrameZ };
                    foreach (var p in profile)
                    {
                        var world = l * p.X + d * p.Y;
                        frame.Points.Add(new Vector2(Vector3.Dot(world, input.FrameX), Vector3.Dot(world, input.FrameZ)));
                    }
                    var dPlan = new Vector2(Vector3.Dot(d, input.FrameX), Vector3.Dot(d, input.FrameZ));
                    var first = frame.Points[^1] - frame.Points[0];
                    if (Vector2.Dot(new Vector2(first.Y, -first.X), dPlan) < 0) frame.Points.Reverse();
                    folds.Add(new Fold { Frame = frame, YMin = y0, YMax = y1, Region = region, EndMargin = ext * 0.01f, Shares = true });
                    foreach (int t in region) taken.Add(t);
                }
            }
            return folds;
        }

        // Cuts a bay's profile (across, depth; wall at both ends) back at each end to its
        // outermost slanted run, whose line then carries on down to the wall - but never
        // further out than the profile's own end.
        private static List<Vector2> TrimToSlants(List<Vector2> profile, float wall, float minRun, float cos20, float cos70)
        {
            bool Slanted(Vector2 a, Vector2 b)
            {
                var run = b - a;
                float along = MathF.Abs(run.X) / MathF.Max(run.Length(), 1e-9f);
                return run.Length() >= minRun && along < cos20 && along > cos70;
            }
            List<Vector2> TrimStart(List<Vector2> p)
            {
                int i = 0;
                while (i + 1 < p.Count && !Slanted(p[i], p[i + 1])) i++;
                if (i == 0 || i + 1 >= p.Count) return p;
                Vector2 outer = p[i], inner = p[i + 1];
                float onLine = outer.X + (inner.X - outer.X) * (wall - outer.Y) / (inner.Y - outer.Y);
                // Between the profile's own end and the run's outer point.
                float u = Math.Clamp(onLine, MathF.Min(p[0].X, outer.X), MathF.Max(p[0].X, outer.X));
                var result = new List<Vector2> { new(u, wall) };
                // Where the run's line reaches the wall, the outer point lies on the way in.
                if (MathF.Abs(u - onLine) > 1e-6f) result.Add(outer);
                result.AddRange(p.Skip(i + 1));
                return result;
            }
            var trimmed = TrimStart(profile);
            trimmed.Reverse();
            trimmed = TrimStart(trimmed);
            trimmed.Reverse();
            return trimmed;
        }

        // Douglas-Peucker.
        private static List<Vector2> Simplify(List<Vector2> points, float tolerance)
        {
            if (points.Count < 3) return points;
            var keep = new bool[points.Count];
            keep[0] = keep[^1] = true;
            var stack = new Stack<(int, int)>();
            stack.Push((0, points.Count - 1));
            while (stack.Count > 0)
            {
                var (a, b) = stack.Pop();
                var ab = points[b] - points[a];
                float len = ab.Length();
                int worst = -1;
                float worstD = tolerance;
                for (int i = a + 1; i < b; i++)
                {
                    var ap = points[i] - points[a];
                    float dist = len > 1e-9f ? MathF.Abs(ab.X * ap.Y - ab.Y * ap.X) / len : ap.Length();
                    if (dist > worstD) { worstD = dist; worst = i; }
                }
                if (worst < 0) continue;
                keep[worst] = true;
                stack.Push((a, worst));
                stack.Push((worst, b));
            }
            return points.Where((p, i) => keep[i]).ToList();
        }

        public static List<Cylinder> Detect(ModelFlattener.FlattenInput input, int maxCount = 6)
        {
            var found = new List<Cylinder>();
            int n = input.TriangleCount;
            if (n == 0) return found;
            float ext = input.Extent;
            float cell = ext * 0.01f;
            float rMin = ext * 0.03f, rMax = ext * 0.8f;
            int rBins = (int)MathF.Ceiling((rMax - rMin) / cell);

            Vector2 Flat(Vector3 v) => new(Vector3.Dot(v, input.FrameX), Vector3.Dot(v, input.FrameZ));
            var centre = new Vector2[n];
            var dir = new Vector2[n];
            var weight = new float[n];
            var candidates = new List<int>();
            for (int t = 0; t < n; t++)
            {
                var nrm = input.Normals[t];
                if (input.Areas[t] <= 0 || MathF.Abs(nrm.Y) > 0.3f) continue;
                var h = Flat(nrm);
                if (h.LengthSquared() < 1e-6f) continue;
                dir[t] = Vector2.Normalize(h);
                centre[t] = Flat((input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f);
                weight[t] = input.Areas[t] * (1f - MathF.Abs(nrm.Y));
                candidates.Add(t);
            }

            // Votes come from points spread over the surface, not one per triangle: a wall built
            // of two huge triangles would otherwise cast two huge votes on one cell.
            var samples = new List<(int T, Vector2 P, float W)>();
            var rng = new Random(12345);
            foreach (int t in candidates)
            {
                int k = Math.Clamp((int)MathF.Round(input.Areas[t] / (cell * cell)), 1, 2000);
                var a = input.Corners[t * 3]; var b = input.Corners[t * 3 + 1]; var c = input.Corners[t * 3 + 2];
                for (int i = 0; i < k; i++)
                {
                    float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                    if (u + v > 1) { u = 1 - u; v = 1 - v; }
                    samples.Add((t, Flat(a + (b - a) * u + (c - a) * v), weight[t] / k));
                }
            }

            var taken = new bool[n];
            for (int round = 0; round < maxCount; round++)
            {
                var votes = new Dictionary<long, float>();
                static long Key(int x, int z, int r) => ((long)(x + 1_000_000) << 40) | ((long)(z + 1_000_000) << 20) | (long)r;
                foreach (var (t, p, w) in samples)
                {
                    if (taken[t]) continue;
                    for (int r = 0; r < rBins; r++)
                    {
                        float radius = rMin + (r + 0.5f) * cell;
                        var c = p - dir[t] * radius;
                        long k = Key((int)MathF.Floor(c.X / cell), (int)MathF.Floor(c.Y / cell), r);
                        votes[k] = votes.GetValueOrDefault(k) + w;
                    }
                }
                if (votes.Count == 0) break;
                var best = votes.MaxBy(kv => kv.Value);
                int bx = (int)((best.Key >> 40) - 1_000_000), bz = (int)(((best.Key >> 20) & 0xFFFFF) - 1_000_000), br = (int)(best.Key & 0xFFFFF);
                var cyl = new Cylinder
                {
                    Centre = new Vector2((bx + 0.5f) * cell, (bz + 0.5f) * cell),
                    Radius = rMin + (br + 0.5f) * cell,
                };

                // Support: faces on the circle, facing straight out from the axis.
                float band = 2f * cell;
                float cosTol = MathF.Cos(12f * MathF.PI / 180f);
                foreach (int t in candidates)
                {
                    if (taken[t]) continue;
                    var off = centre[t] - cyl.Centre;
                    float d = off.Length();
                    if (d < 1e-6f || MathF.Abs(d - cyl.Radius) > band) continue;
                    if (Vector2.Dot(off / d, dir[t]) < cosTol) continue;
                    cyl.Support.Add(t);
                }
                if (cyl.Support.Count == 0) break;
                foreach (int t in cyl.Support) taken[t] = true;
                cyl.SupportArea = cyl.Support.Sum(t => input.Areas[t]);

                // A curve turns: its support has to face a good range of directions, spread
                // reasonably evenly (a wall happening to cross the circle faces one way).
                var angles = cyl.Support.Select(t => MathF.Atan2(dir[t].Y, dir[t].X)).ToList();
                float mid = MathF.Atan2(cyl.Support.Sum(t => dir[t].Y * weight[t]), cyl.Support.Sum(t => dir[t].X * weight[t]));
                float Rel(float a) { float x = a - mid; while (x > MathF.PI) x -= 2 * MathF.PI; while (x < -MathF.PI) x += 2 * MathF.PI; return x; }
                var rel = cyl.Support.Select(t => (A: Rel(MathF.Atan2(dir[t].Y, dir[t].X)), W: weight[t])).OrderBy(x => x.A).ToList();
                float total = rel.Sum(x => x.W), acc = 0, lo = rel[0].A, hi = rel[^1].A;
                foreach (var (a, w) in rel) { acc += w; if (acc >= 0.05f * total) { lo = a; break; } }
                acc = 0;
                for (int i = rel.Count - 1; i >= 0; i--) { acc += rel[i].W; if (acc >= 0.05f * total) { hi = rel[i].A; break; } }
                cyl.AngleMin = mid + lo;
                cyl.AngleMax = mid + hi;
                cyl.YMin = cyl.Support.Min(t => MathF.Min(input.Corners[t * 3].Y, MathF.Min(input.Corners[t * 3 + 1].Y, input.Corners[t * 3 + 2].Y)));
                cyl.YMax = cyl.Support.Max(t => MathF.Max(input.Corners[t * 3].Y, MathF.Max(input.Corners[t * 3 + 1].Y, input.Corners[t * 3 + 2].Y)));

                bool turns = hi - lo >= 40f * MathF.PI / 180f;
                // Tight rounded corners (a few centimetres) lose nothing to two flat planes.
                bool big = cyl.SupportArea >= input.TotalArea * 0.005f;
                bool wide = cyl.Radius >= ext * 0.04f;
                // Even: a curve's support is spread along it, not bunched at a few angles.
                int bins = 8, filled = 0;
                var hist = new float[bins];
                foreach (var (a, w) in rel)
                    if (a >= lo && a <= hi) hist[Math.Clamp((int)((a - lo) / MathF.Max(hi - lo, 1e-6f) * bins), 0, bins - 1)] += w;
                foreach (float h in hist) if (h > 0.03f * total) filled++;
                bool even = filled >= bins - 2;
                if (!big) break;
                if (turns && even && wide) found.Add(cyl);
            }

            foreach (var cyl in found) cyl.Region = RegionOf(input, cyl);
            return found;
        }

        public sealed class Summary
        {
            public int Curves, RegionTriangles, SimplifiedTriangles;
            public override string ToString() => Curves == 0 ? "no curves"
                : $"{Curves} curve(s): {RegionTriangles:N0} -> {SimplifiedTriangles:N0} triangles kept as mesh";
        }

        // Finds the curves and returns a copy of `input` in which each one's triangles are
        // replaced by a simplified version of themselves (meshoptimizer, to within `tolerance`
        // model units, per primitive, so every new triangle still indexes its primitive's own
        // vertices and bakes from its own UVs) and locked: the flattening keeps them as mesh.
        public static ModelFlattener.FlattenInput KeepAsMesh(ModelRoot model, ModelFlattener.FlattenInput input,
            float tolerance, bool lockBorders, out Summary summary)
        {
            summary = new Summary();
            var folds = DetectFolds(input);
            var region = new HashSet<int>(folds.SelectMany(c => c.Region));
            summary.Curves = folds.Count;
            summary.RegionTriangles = region.Count;
            if (region.Count == 0) return input;

            var removed = new HashSet<int>();
            var added = new List<(int Node, int Mesh, int Prim, int A, int B, int C)>();
            foreach (var group in region.GroupBy(t => (input.SourceNode[t], input.Sources[t].Mesh, input.Sources[t].Prim)))
            {
                var (node, meshIdx, primIdx) = group.Key;
                var prim = model.LogicalMeshes[meshIdx].Primitives[primIdx];
                var positions = prim.VertexAccessors["POSITION"].AsVector3Array().ToArray();
                var normals = prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc) ? nAcc.AsVector3Array().ToArray() : null;
                var colour = prim.Material?.FindChannel("BaseColor");
                var uvs = prim.VertexAccessors.TryGetValue($"TEXCOORD_{(colour.HasValue ? colour.Value.TextureCoordinate : 0)}", out var uvAcc)
                    ? uvAcc.AsVector2Array().ToArray() : null;
                var triangles = prim.GetTriangleIndices().ToList();
                var selection = group.Select(t => input.SourceTriangle[t]).ToHashSet();

                // The tolerance in the primitive's own units, relative to meshopt's scale for it.
                var world = input.NodeWorld[node];
                float nodeScale = MathF.Cbrt(MathF.Abs(world.GetDeterminant()));
                float meshScale;
                unsafe
                {
                    fixed (Vector3* pos = positions)
                        meshScale = MeshoptNative.SimplifyScale((float*)pos, (nuint)positions.Length, (nuint)sizeof(Vector3));
                }
                var options = new GeometryOptimizer.SimplifyOptions
                {
                    TargetRatio = 0.01f,
                    TargetError = tolerance / MathF.Max(nodeScale, 1e-9f) / MathF.Max(meshScale, 1e-9f),
                    LockBorders = lockBorders,
                    OptimizeVertexOrder = false,
                };
                var simplified = GeometryOptimizer.SimplifySubset(positions, normals, uvs, triangles, selection, options, out _);
                if (simplified == null) continue;
                removed.UnionWith(group);
                for (int i = 0; i + 2 < simplified.Length; i += 3)
                    added.Add((node, meshIdx, primIdx, simplified[i], simplified[i + 1], simplified[i + 2]));
            }

            // The copy: everything not replaced, then the replacements.
            var corners = new List<Vector3>();
            var vertexNormals = new List<Vector3>();
            var sources = new List<(int, int, int, int, int)>();
            var sourceNode = new List<int>();
            var sourceTriangle = new List<int>();
            var flipped = new List<bool>();
            var locked = new List<bool>();
            var gatheredIndex = new List<int>();
            for (int t = 0; t < input.TriangleCount; t++)
            {
                if (removed.Contains(t)) continue;
                gatheredIndex.Add(t);
                for (int k = 0; k < 3; k++)
                {
                    corners.Add(input.Corners[t * 3 + k]);
                    vertexNormals.Add(input.VertexNormals[t * 3 + k]);
                }
                sources.Add(input.Sources[t]);
                sourceNode.Add(input.SourceNode[t]);
                sourceTriangle.Add(input.SourceTriangle[t]);
                flipped.Add(input.NodeWorld[input.SourceNode[t]].GetDeterminant() < 0);
                locked.Add(region.Contains(t));
            }
            var cache = new Dictionary<(int, int), (Vector3[] P, Vector3[]? N)>();
            foreach (var (node, meshIdx, primIdx, a, b, c) in added)
            {
                if (!cache.TryGetValue((meshIdx, primIdx), out var data))
                {
                    var prim = model.LogicalMeshes[meshIdx].Primitives[primIdx];
                    data = (prim.VertexAccessors["POSITION"].AsVector3Array().ToArray(),
                        prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc) ? nAcc.AsVector3Array().ToArray() : null);
                    cache[(meshIdx, primIdx)] = data;
                }
                var world = input.NodeWorld[node];
                var normalMatrix = Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : world;
                foreach (int v in new[] { a, b, c })
                {
                    corners.Add(Vector3.Transform(data.P[v], world));
                    var vn = data.N == null ? Vector3.Zero : Vector3.TransformNormal(data.N[v], normalMatrix);
                    vertexNormals.Add(vn.LengthSquared() > 1e-20f ? Vector3.Normalize(vn) : Vector3.Zero);
                }
                sources.Add((meshIdx, primIdx, a, b, c));
                sourceNode.Add(node);
                sourceTriangle.Add(-1);
                flipped.Add(world.GetDeterminant() < 0);
                locked.Add(true);
                gatheredIndex.Add(-1);
            }
            summary.SimplifiedTriangles = added.Count + region.Count(t => !removed.Contains(t));

            int n = sources.Count;
            var output = new ModelFlattener.FlattenInput
            {
                Corners = corners.ToArray(),
                Normals = new Vector3[n],
                Areas = new float[n],
                VertexNormals = vertexNormals.ToArray(),
                WeldIds = new int[n * 3],
                Sources = sources.ToArray(),
                SourceNode = sourceNode.ToArray(),
                SourceTriangle = sourceTriangle.ToArray(),
                NodeWorld = input.NodeWorld,
                Locked = locked.ToArray(),
                GatheredIndex = gatheredIndex.ToArray(),
                FrameX = input.FrameX,
                FrameZ = input.FrameZ,
            };
            ModelFlattener.Finish(output, flipped);
            return output;
        }

        // Everything inside the curve's wedge: within its angles (and a little past them), from
        // a little inside the surface (glass set into it) to what stands out from it (cornices,
        // sills), over its height.
        private static List<int> RegionOf(ModelFlattener.FlattenInput input, Cylinder cyl)
        {
            float ext = input.Extent;
            // No deeper than half the radius: round a small rounded corner, a fixed band would
            // take in the ends of both walls meeting there.
            float inside = MathF.Min(ext * 0.04f, cyl.Radius * 0.5f), outside = MathF.Min(ext * 0.04f, cyl.Radius * 0.5f), pad = ext * 0.01f;
            float angPad = pad / MathF.Max(cyl.Radius, 1e-6f);
            float mid = (cyl.AngleMin + cyl.AngleMax) / 2, half = (cyl.AngleMax - cyl.AngleMin) / 2 + angPad;
            var region = new List<int>();
            for (int t = 0; t < input.TriangleCount; t++)
            {
                var p = (input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f;
                if (p.Y < cyl.YMin - pad || p.Y > cyl.YMax + pad) continue;
                var off = new Vector2(Vector3.Dot(p, input.FrameX), Vector3.Dot(p, input.FrameZ)) - cyl.Centre;
                float d = off.Length();
                if (d < cyl.Radius - inside || d > cyl.Radius + outside) continue;
                float a = MathF.Atan2(off.Y, off.X) - mid;
                while (a > MathF.PI) a -= 2 * MathF.PI;
                while (a < -MathF.PI) a += 2 * MathF.PI;
                if (MathF.Abs(a) > half) continue;
                region.Add(t);
            }
            return region;
        }
    }
}
