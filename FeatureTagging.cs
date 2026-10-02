using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace GlbMerger
{
    // A part of a model tagged by hand in the Flatten Model editor: these triangles are this
    // group, whatever the flattener's own detection would make of them (ModelFlattener applies
    // them through FlattenSettings.Tags).
    public sealed class FeatureTag
    {
        public FeatureGroup Group;
        public int[] Triangles = Array.Empty<int>();
    }

    // A box aligned with the building's own axes (FlattenInput.FrameX, up, FrameZ): Center in
    // model coordinates, Size along those three axes.
    public readonly record struct FeatureBox(Vector3 Center, Vector3 Size)
    {
        public bool Contains(ModelFlattener.FlattenInput input, Vector3 p, float slack = 0f)
        {
            var d = p - Center;
            return MathF.Abs(Vector3.Dot(d, input.FrameX)) <= Size.X * 0.5f + slack
                && MathF.Abs(d.Y) <= Size.Y * 0.5f + slack
                && MathF.Abs(Vector3.Dot(d, input.FrameZ)) <= Size.Z * 0.5f + slack;
        }
    }

    public static class FeatureTagging
    {
        // The groups a part can be tagged as. Bays and curved walls need their own search
        // (CurvedRegions) and come later; backdrops and window glass are made, not found.
        public static readonly FeatureGroup[] Taggable =
        {
            FeatureGroup.Front, FeatureGroup.Side, FeatureGroup.Back, FeatureGroup.Roof, FeatureGroup.Underside,
            FeatureGroup.Corner, FeatureGroup.Column, FeatureGroup.Overhang, FeatureGroup.Pediment, FeatureGroup.RoofOrnament, FeatureGroup.FireEscape,
            FeatureGroup.Leftover, FeatureGroup.Dropped,
        };

        // Every triangle lying in the box (all three corners, with a little slack).
        public static List<int> InBox(ModelFlattener.FlattenInput input, FeatureBox box)
        {
            float slack = input.Extent * 0.002f;
            var result = new List<int>();
            for (int t = 0; t < input.TriangleCount; t++)
            {
                if (input.Areas[t] <= 0) continue;
                if (box.Contains(input, input.Corners[t * 3], slack) && box.Contains(input, input.Corners[t * 3 + 1], slack)
                    && box.Contains(input, input.Corners[t * 3 + 2], slack))
                    result.Add(t);
            }
            return result;
        }

        // What in the box belongs to `group`, by that group's own rules but more willingly than
        // the flattener's detection - the user has already said the feature is there.
        public static List<int> FindInBox(ModelFlattener.FlattenInput input, FeatureGroup group, FeatureBox box)
        {
            var inside = InBox(input, box);
            if (inside.Count == 0) return inside;
            Vector3 Centroid(int t) => (input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f;
            float cell = input.Extent * 0.005f;

            switch (group)
            {
                case FeatureGroup.Front: return inside.Where(t => Vector3.Dot(input.Normals[t], input.FrameZ) > 0.5f).ToList();
                case FeatureGroup.Back: return inside.Where(t => Vector3.Dot(input.Normals[t], input.FrameZ) < -0.5f).ToList();
                case FeatureGroup.Side: return inside.Where(t => MathF.Abs(Vector3.Dot(input.Normals[t], input.FrameX)) > 0.5f).ToList();
                case FeatureGroup.Roof: return inside.Where(t => input.Normals[t].Y > 0.5f).ToList();
                case FeatureGroup.Underside: return inside.Where(t => input.Normals[t].Y < -0.5f).ToList();
            }

            // The wall the feature stands on: the way most of the box's area faces, and the
            // depth most of what faces that way sits at.
            var (d, wall) = WallIn(input, inside);
            switch (group)
            {
                case FeatureGroup.Pediment:
                    // All but what faces back in (its back, seen only from the roof).
                    return inside.Where(t => Vector3.Dot(input.Normals[t], d) >= -0.3f).ToList();
                case FeatureGroup.Overhang:
                    // As DetectOverhangs takes its band: not what faces back in, not what's
                    // turned up (nobody below sees it), not the plain wall behind.
                    return inside.Where(t =>
                    {
                        float facing = Vector3.Dot(input.Normals[t], d);
                        if (facing < -0.5f || input.Normals[t].Y > 0.2f) return false;
                        return !(facing > 0.9f && Vector3.Dot(Centroid(t), d) < wall + cell);
                    }).ToList();
                case FeatureGroup.Column:
                case FeatureGroup.Corner:
                    // What stands out from the wall, and its flanks.
                    return inside.Where(t => Vector3.Dot(Centroid(t), d) >= wall + cell).ToList();
                case FeatureGroup.FireEscape:
                    // Everything but the solid wall behind it.
                    return inside.Where(t => !(Vector3.Dot(input.Normals[t], d) > 0.9f && MathF.Abs(Vector3.Dot(Centroid(t), d) - wall) < cell)).ToList();
                default:
                    // Ornaments, mesh, dropped: all of it.
                    return inside;
            }
        }

        // The frame direction most of these triangles' area faces (FrameZ, -FrameZ, FrameX or
        // -FrameX), and the area-modal depth along it of the faces that face it.
        public static (Vector3 Dir, float Wall) WallIn(ModelFlattener.FlattenInput input, IReadOnlyList<int> tris)
        {
            var dirs = new[] { input.FrameZ, -input.FrameZ, input.FrameX, -input.FrameX };
            var area = new float[4];
            foreach (int t in tris)
                for (int i = 0; i < 4; i++)
                    area[i] += input.Areas[t] * MathF.Max(0f, Vector3.Dot(input.Normals[t], dirs[i]));
            int best = Array.IndexOf(area, area.Max());
            var d = dirs[best];

            float bin = input.Extent * 0.0025f;
            var hist = new Dictionary<int, float>();
            foreach (int t in tris)
            {
                if (Vector3.Dot(input.Normals[t], d) < 0.9f) continue;
                var c = (input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f;
                int k = (int)MathF.Floor(Vector3.Dot(c, d) / bin);
                hist[k] = hist.GetValueOrDefault(k) + input.Areas[t];
            }
            float wall = hist.Count == 0
                ? tris.Min(t => Vector3.Dot(input.Corners[t * 3], d))
                : (hist.MaxBy(kv => kv.Value).Key + 0.5f) * bin;
            return (d, wall);
        }

        // Grows a painted selection to the whole feature it's part of: across shared edges of the
        // mesh and onto loose pieces lying right up against it, but never into a large flat face
        // (the wall it's on) or anything in `blocked` (another tag), and not far beyond where the
        // painting was.
        public static List<int> GrowFeature(ModelFlattener.FlattenInput input, IEnumerable<int> seed, ISet<int> blocked)
        {
            var result = new HashSet<int>(seed.Where(t => t >= 0 && t < input.TriangleCount));
            if (result.Count == 0) return result.ToList();
            Vector3 Centroid(int t) => (input.Corners[t * 3] + input.Corners[t * 3 + 1] + input.Corners[t * 3 + 2]) / 3f;

            // Not far: the painting's own extent again on every side, at least 5% of the model.
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            foreach (int t in result)
                for (int k = 0; k < 3; k++) { lo = Vector3.Min(lo, input.Corners[t * 3 + k]); hi = Vector3.Max(hi, input.Corners[t * 3 + k]); }
            var pad = Vector3.Max(hi - lo, new Vector3(input.Extent * 0.05f));
            lo -= pad; hi += pad;
            bool Near(int t)
            {
                var c = Centroid(t);
                return c.X >= lo.X && c.Y >= lo.Y && c.Z >= lo.Z && c.X <= hi.X && c.Y <= hi.Y && c.Z <= hi.Z;
            }

            // Triangles around each welded vertex.
            var byVertex = new Dictionary<int, List<int>>();
            for (int t = 0; t < input.TriangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int w = input.WeldIds.Length > 0 ? input.WeldIds[t * 3 + k] : t * 3 + k;
                    if (!byVertex.TryGetValue(w, out var list)) byVertex[w] = list = new List<int>(6);
                    list.Add(t);
                }
            IEnumerable<int> Neighbours(int t)
            {
                for (int k = 0; k < 3; k++)
                {
                    int w = input.WeldIds.Length > 0 ? input.WeldIds[t * 3 + k] : t * 3 + k;
                    foreach (int n in byVertex[w]) if (n != t) yield return n;
                }
            }

            // A large flat face: the triangle with every neighbour lying in its plane, over more
            // than half a percent of the model's surface.
            var flatSize = new Dictionary<int, float>();
            float bigFlat = input.TotalArea * 0.005f;
            bool IsWall(int t)
            {
                if (flatSize.TryGetValue(t, out float size)) return size > bigFlat;
                var region = new List<int> { t };
                var inRegion = new HashSet<int> { t };
                var n0 = input.Normals[t];
                float d0 = Vector3.Dot(input.Corners[t * 3], n0), tol = input.Extent * 0.002f;
                float area = 0f;
                for (int i = 0; i < region.Count && area <= bigFlat; i++)
                {
                    area += input.Areas[region[i]];
                    foreach (int n in Neighbours(region[i]))
                        if (!inRegion.Contains(n) && Vector3.Dot(input.Normals[n], n0) > 0.99f
                            && MathF.Abs(Vector3.Dot(Centroid(n), n0) - d0) < tol && inRegion.Add(n))
                            region.Add(n);
                }
                foreach (int r in region) flatSize[r] = area;
                return area > bigFlat;
            }

            bool Takes(int t) => !result.Contains(t) && !blocked.Contains(t) && input.Areas[t] > 0 && Near(t) && !IsWall(t);

            // Loose pieces against it: anything whose centre is within this of a grown triangle's.
            float touch = input.Extent * 0.005f;
            var frontier = new List<int>(result);
            for (int round = 0; round < 4 && frontier.Count > 0; round++)
            {
                // Across the mesh.
                for (int i = 0; i < frontier.Count; i++)
                    foreach (int n in Neighbours(frontier[i]))
                        if (Takes(n)) { result.Add(n); frontier.Add(n); }

                // Onto pieces just touching it.
                var grid = new Dictionary<(int, int, int), List<int>>();
                (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X / touch), (int)MathF.Floor(p.Y / touch), (int)MathF.Floor(p.Z / touch));
                foreach (int t in result)
                {
                    var key = Cell(Centroid(t));
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(t);
                }
                var next = new List<int>();
                for (int t = 0; t < input.TriangleCount; t++)
                {
                    if (!Takes(t)) continue;
                    var (cx, cy, cz) = Cell(Centroid(t));
                    bool touching = false;
                    for (int dx = -1; dx <= 1 && !touching; dx++)
                        for (int dy = -1; dy <= 1 && !touching; dy++)
                            for (int dz = -1; dz <= 1 && !touching; dz++)
                                if (grid.ContainsKey((cx + dx, cy + dy, cz + dz))) touching = true;
                    if (touching) next.Add(t);
                }
                foreach (int t in next) result.Add(t);
                frontier = next;
            }
            return result.ToList();
        }
    }
}
