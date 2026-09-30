using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Turns a static, high-detail model (the game's buildings) into a handful of flat billboards -
    // the "billboard clouds" technique (Decoret et al. 2003). Everything hangs off one number,
    // the tolerance epsilon: the furthest any point of the original surface may move when it is
    // projected onto the billboard that replaces it. A tiny epsilon flattens brick grooves into
    // the wall, a medium one flattens window wells, and one as deep as the model itself flattens
    // the whole thing into a card.
    //
    // The search is greedy. Candidate plane NORMALS come from clustering the surface's own
    // area-weighted normals (plus the building's dominant axes, which noisy photogrammetry
    // normals would otherwise only ever approximate); for each normal a 1D sweep over plane
    // offset finds the depth that holds the most projected area within +-epsilon. The best plane
    // takes its triangles, and repeats until nothing big enough is left.
    //
    // Two kinds of triangle belong to a plane:
    //  - aligned ones (facing within AlignAngle of the plane normal, and not much further from it
    //    than from their nearest candidate normal) are what a plane is scored on and claims, and
    //  - edge-on ones (groove walls, window reveals, the sides of a rail post) that sit within
    //    epsilon of an existing plane but face sideways. These are "absorbable": they score
    //    nothing for any new plane, so a strip of groove wall never becomes a billboard of its
    //    own, and at the end they are folded into whichever existing plane they fit best. The
    //    side wall of a building still gets its own plane - most of it is further than epsilon
    //    from the facade - and when it does it takes its corner strip with it, which is what
    //    keeps that strip from being flattened edge-on into the facade and leaving a gap.
    // Triangles facing away from a plane (dot below AbsorbDot) never join it, so the two faces
    // of a thin wall get back-to-back one-sided billboards instead of one mirrored card - the
    // game culls back faces.
    //
    // One plane can hold several unrelated pieces - every window pane of a facade sits at the
    // same depth, and inside a fire escape one slab of +-epsilon holds a rail AND the stair
    // behind it. So each plane's triangles are split by mesh connectivity and re-merged only
    // where pieces are close both across the plane and in depth. Each resulting group is one
    // isolated piece: its own billboard, drawn at its own pieces' depth, rendering only its
    // own triangles.
    //
    // See-through detail (fire escapes, railings) is then taken apart structure by structure
    // (RefineDetail): each stair flight, rail, floor and ladder becomes its own billboard(s),
    // flattened on its own, so nothing from one ends up in another's texture. A stair flight is
    // built entirely from treads facing up and stringers facing out, yet the flight as a whole
    // lies on a slanted plane no triangle faces, so flights are found separately: small up- (or
    // down-) facing patches whose centres line up on a slope (DetectStairPlanes).
    //
    // Finally, where two billboards on different planes meet at a corner, the edge that falls
    // short of their shared line is extended onto it (WeldCorners), so corners don't leak sky.
    //
    // This class only decides the geometry; BillboardBaker textures it.
    public static class ModelFlattener
    {
        // Triangles facing more than this far from a plane's normal don't score for it.
        private const float AlignAngleDegrees = 40f;
        // ...or are more than this much further from it than from the triangle's closest candidate.
        private const float AlignSlackDegrees = 10f;
        // ...and ones facing further away than this (about 105 degrees) can't join it at all.
        private const float AbsorbDot = -0.25f;
        // Cluster normals within this of a building axis are snapped exactly onto it.
        private const float SnapAngleDegrees = 10f;
        private const int OctBins = 40;
        private const int MaxPeaks = 96;
        private const int MaxRounds = 8;
        private const int MaxPlanes = 2000;
        private const int CoverageCells = 64;
        // Stair flights and other isolated structures (RefineDetail) only have to reach this share
        // of the minimum size.
        private const float FittedMinScale = 0.1f;
        // Triangles facing further off a billboard than this (about 78 degrees) are edge-on to it.
        private const float VisibleDot = 0.2f;
        // The side pass looks for a better billboard for anything facing further off its own
        // than this (45 degrees), not just the edge-on: on a shallow building both facades sit
        // within epsilon of most of the side wall, and a chamfered corner facing 60 degrees off
        // the back facade vanishes from the side if it's left there.
        private const float SideReleaseDot = 0.7f;
        // Tolerance of an upright plane turned off the building's axes, as a share of the
        // pass's own (see YawedEps).
        private const float YawedToleranceScale = 0.7f;

        public sealed class FlattenInput
        {
            // World space, three per triangle.
            public Vector3[] Corners = Array.Empty<Vector3>();
            // Unit geometric normal per triangle, oriented to agree with the vertex normals where
            // the mesh has them; zero for degenerate triangles.
            public Vector3[] Normals = Array.Empty<Vector3>();
            public float[] Areas = Array.Empty<float>();
            // World-space vertex normals, three per triangle; zero where the mesh has none.
            public Vector3[] VertexNormals = Array.Empty<Vector3>();
            // Position-welded vertex ids, three per triangle - connectivity across UV seams.
            public int[] WeldIds = Array.Empty<int>();
            // Where each triangle came from, for the texture bake.
            public (int Mesh, int Prim, int A, int B, int C)[] Sources = Array.Empty<(int, int, int, int, int)>();
            // Which mesh node drew it (index into NodeWorld) and which triangle of its primitive it is.
            public int[] SourceNode = Array.Empty<int>();
            public int[] SourceTriangle = Array.Empty<int>();
            public List<Matrix4x4> NodeWorld = new();
            // Triangles that stay real mesh whatever the flattening does (KeepCurvesAsMesh); empty
            // when none are.
            public bool[] Locked = Array.Empty<bool>();
            // For an input PrepareInput rebuilt: which triangle of the gathered input each one
            // is, or -1 for one that replaced others. Empty when it is the gathered input.
            public int[] GatheredIndex = Array.Empty<int>();

            public int TriangleCount => Areas.Length;
            public Vector3 BoundsMin, BoundsMax;
            public float Extent;      // largest bounding-box dimension
            public float TotalArea;

            // The building's dominant horizontal axes (glTF Y is up).
            public Vector3 FrameX = Vector3.UnitX, FrameZ = Vector3.UnitZ;
        }

        // Upright curved surfaces (CurvedRegions finds them):
        //  - Planes: flattened like everything else, onto a few flat planes - cheapest, but from
        //    below the bands and cornices round a curve break into chevrons and shards.
        //  - CurvedBillboards: one billboard bent round the curve, a strip of a few flat pieces -
        //    a few dozen triangles per curve, reads as round.
        //  - KeepAsMesh: the curve's own triangles, simplified, kept as real mesh - looks almost
        //    exactly like the original, at thousands of triangles per curve.
        public enum CurveHandling { Planes, CurvedBillboards, KeepAsMesh }

        // The input Compute should run on for these curve settings: `gathered` itself, or for
        // KeepAsMesh a copy with each curve's triangles replaced by simplified ones and locked
        // (see CurvedRegions.KeepAsMesh). Reads the model, so it belongs on the thread that owns
        // it; the bake source has to be extracted from the same input it returns.
        public static FlattenInput PrepareInput(ModelRoot model, FlattenInput gathered, CurveHandling curves) =>
            curves == CurveHandling.KeepAsMesh
                ? CurvedRegions.KeepAsMesh(model, gathered, gathered.Extent * CurveMeshError, false, out _)
                : gathered;

        // How far KeepAsMesh's simplification may move the surface, as a share of the model's size.
        public const float CurveMeshError = 0.005f;

        public sealed class FlattenSettings
        {
            public float Tolerance;          // epsilon, model units
            public float MinBillboardArea;   // projected area below which no billboard is made
            public bool KeepLeftoversAsMesh = true;
            // A low-resolution quad behind everything from each side the game sees a building
            // from, showing the whole building as seen from there (AddBackdrops).
            public bool Backdrops = true;
            // What becomes of upright curves - a round bay, a corner tower (see CurveHandling).
            // KeepAsMesh works on the input instead (PrepareInput); here it changes nothing.
            public CurveHandling Curves = CurveHandling.Planes;

            // Side views: after the main pass, the triangles that ended up edge-on to their
            // billboard (a balcony's side rails, a fire escape's stair stringers - flat from the
            // front, invisible from the side) get a second chance to form billboards of their
            // own, down to this smaller minimum size.
            public bool SideDetail;
            public float SideMinArea;

            // See-through structures (fire escapes, railings - anything whose billboard ends up
            // mostly empty) are re-flattened with this smaller tolerance, so their depth layers
            // - the wall-side and outer stringers of a stair flight - stay separate billboards
            // instead of collapsing into one line seen from the side. 0 or >= Tolerance = off.
            public float DetailTolerance;
            public float DetailMinArea;
        }

        // Billboards covering less than this share of their rectangle count as see-through detail.
        public const float DetailCoverage = 0.8f;
        private const float SoffitWeldCoverage = 0.3f;

        // A backdrop's cut (see CrossSection): per row up the plane (V0 + r * Step), the span

        // Which cells of a billboard's rectangle its triangles cover (see CoverageGrid).

        public sealed class CoverageMap

        {

            public Vector2 Min;

            public float Cell;

            public int Cols, Rows;

            public bool[] Covered = Array.Empty<bool>();


            // Covered at (u, v) or a cell next to it.

            public bool Near(float u, float v)

            {

                int cx = (int)MathF.Floor((u - Min.X) / Cell), cy = (int)MathF.Floor((v - Min.Y) / Cell);

                for (int y = Math.Max(0, cy - 1); y <= Math.Min(Rows - 1, cy + 1); y++)

                    for (int x = Math.Max(0, cx - 1); x <= Math.Min(Cols - 1, cx + 1); x++)

                        if (Covered[y * Cols + x]) return true;

                return false;

            }

        }


        // across (Lo..Hi, plus Margin) the model's cross-section covers there.

        public sealed class ClipRows

        {

            public float V0, Step, Margin;

            public float[] Lo = Array.Empty<float>(), Hi = Array.Empty<float>();


            // With clampV, points above or below the section are judged by its top or bottom row.

            public bool Contains(float u, float v, bool clampV)

            {

                int r = (int)MathF.Floor((v - V0) / Step);

                if (r < 0 || r >= Lo.Length)

                {

                    if (!clampV) return false;

                    r = Math.Clamp(r, 0, Lo.Length - 1);

                }

                return u >= Lo[r] - Margin && u <= Hi[r] + Margin;

            }

        }


        // A yes/no per square cell over a billboard's plane: cell (c, r) spans
        // U0 + c * Step .. + Step across and V0 + r * Step .. + Step up. Outside the grid is no.
        public sealed class CellMask
        {
            public float U0, V0, Step;
            public int Cols, Rows;
            public bool[] Cells = Array.Empty<bool>();

            public bool Contains(float u, float v)
            {
                int c = (int)MathF.Floor((u - U0) / Step), r = (int)MathF.Floor((v - V0) / Step);
                return c >= 0 && r >= 0 && c < Cols && r < Rows && Cells[r * Cols + c];
            }
        }

        public sealed class Billboard
        {
            public Vector3 Normal;
            public float Offset;              // plane: dot(p, Normal) == Offset
            public Vector3 AxisU, AxisV;      // AxisU x AxisV == Normal
            public Vector2 Min, Max;          // footprint rectangle in (U, V)
            // The rectangle the triangles themselves span, before corner welding moved Min/Max
            // onto a neighbouring billboard's plane. Where the weld extended the rectangle, the
            // bake stretches the content's edge texels out to fill it.
            public Vector2 ContentMin, ContentMax;
            public List<int> Triangles = new();
            public int PlaneIndex;
            public float Coverage;            // fraction of the rectangle the triangles cover
            public bool Side;                 // found by the side-detail pass
            // A floor or landing of see-through detail - a grating. Seen at a slant the real one
            // looks solid (each slat's sides close the gaps between them); flattened, it would
            // show every gap at every angle, so the bake fills its enclosed holes instead.
            public bool FillHoles;
            // Set back behind a hole in a solid surface (a door in its arch, a window in its
            // reveal): its rectangle reaches out behind that surface (see ExtendRecessed), and
            // if it's mostly solid itself it bakes fully opaque.
            public bool Recessed;
            // Where a Recessed billboard is really there: over its own surface, and behind the
            // solid surfaces in front of it. Its rectangle can take in sky around both (between
            // a stepped parapet's steps, above the notches beside a raised centre).
            public List<CoverageMap>? RecessedBehind;
            // Part of the see-through detail pass (a rail, a stair, a floor): its gaps are real.
            public bool Detail;
            // A fire-escape part or roof ornament (FireEscapeParts, AddRoofOrnaments): renders
            // everything in its box, edge-on or not.
            public bool Part;
            // A backdrop (AddBackdrops): behind everything, seen only through gaps between the
            // billboards in front, so it's baked at a fraction of the usual texture density.
            public bool Backdrop;
            public float DensityScale = 1f;
            // The rectangle before GrowForSweep, when it grew: edge texels are stretched across
            // weld extensions (out to here), never into the room left for the sweep fill.
            public Vector2? WeldedMin, WeldedMax;
            // The model's own cross-section at a backdrop's depth, which its baked texture is cut
            // to (null = no cut).
            public ClipRows? Clip;
            // Where a backdrop lies inside the building's skin (see SkinMask); cut away elsewhere.
            public CellMask? Inside;

            // A curved billboard (CurvedRegions): a strip round an upright cylinder instead of a
            // flat rectangle. Its (U, V) are (arc length round the axis, height), Offset is the
            // radius it's drawn at, and depth is distance from the axis.
            public CurveFrame? Curve;

            public Vector3 Corner(int i)
            {
                float u = i == 0 || i == 3 ? Min.X : Max.X;
                float v = i < 2 ? Min.Y : Max.Y;
                // A curved billboard's corners are its strip's ends (the outline is their chord).
                if (Curve != null) return Curve.At(u, v);
                return Normal * Offset + AxisU * u + AxisV * v;
            }
        }

        // The outline in plan a curved billboard is drawn along: a polyline - many short
        // pieces round a curve, a few straight runs round a canted bay. The billboard's U is
        // arc length along it, V is height, and depth is distance out from it.
        public sealed class CurveFrame
        {
            // (FrameX, FrameZ) coordinates, ordered so that out - the side the strip is seen
            // from - is on the right going along.
            public List<Vector2> Points = new();
            public Vector3 FrameX = Vector3.UnitX, FrameZ = Vector3.UnitZ;
            private float[]? _arc;

            public int Segments => Math.Max(1, Points.Count - 1);
            public float Length { get { Prepare(); return _arc![^1]; } }
            public float ArcAt(int point) { Prepare(); return _arc![point]; }

            private void Prepare()
            {
                if (_arc != null && _arc.Length == Points.Count) return;
                _arc = new float[Points.Count];
                for (int i = 1; i < Points.Count; i++) _arc[i] = _arc[i - 1] + (Points[i] - Points[i - 1]).Length();
            }

            private Vector2 Plan(Vector3 p) => new(Vector3.Dot(p, FrameX), Vector3.Dot(p, FrameZ));
            private static Vector2 Right(Vector2 d) => d.LengthSquared() > 1e-20f ? Vector2.Normalize(new Vector2(d.Y, -d.X)) : Vector2.UnitX;

            private (int Segment, float T) Nearest(Vector2 q)
            {
                int best = 0;
                float bestT = 0, bestD = float.MaxValue;
                for (int i = 0; i + 1 < Points.Count; i++)
                {
                    var a = Points[i];
                    var ab = Points[i + 1] - a;
                    float len2 = ab.LengthSquared();
                    float t = len2 > 1e-20f ? Math.Clamp(Vector2.Dot(q - a, ab) / len2, 0f, 1f) : 0f;
                    float d = (a + ab * t - q).LengthSquared();
                    if (d < bestD) { bestD = d; best = i; bestT = t; }
                }
                return (best, bestT);
            }

            // (arc length, height) of p's spot on the outline.
            public Vector2 Project(Vector3 p)
            {
                Prepare();
                var (i, t) = Nearest(Plan(p));
                return new(_arc![i] + t * (_arc[i + 1] - _arc[i]), p.Y);
            }

            // How far out from the outline p is (negative: behind it).
            public float Depth(Vector3 p)
            {
                var q = Plan(p);
                var (i, _) = Nearest(q);
                return Vector2.Dot(q - Points[i], Right(Points[i + 1] - Points[i]));
            }

            private int SegmentAt(float s)
            {
                Prepare();
                for (int i = 0; i + 2 < Points.Count; i++) if (s <= _arc![i + 1]) return i;
                return Points.Count - 2;
            }

            public Vector3 Outward(float s)
            {
                int i = SegmentAt(s);
                var n = Right(Points[i + 1] - Points[i]);
                return FrameX * n.X + FrameZ * n.Y;
            }

            public Vector3 At(float s, float y)
            {
                int i = SegmentAt(s);
                float len = _arc![i + 1] - _arc[i];
                float t = len > 1e-12f ? (s - _arc[i]) / len : 0f;
                var q = Points[i] + (Points[i + 1] - Points[i]) * t;
                return FrameX * q.X + FrameZ * q.Y + Vector3.UnitY * y;
            }

            // The same outline moved out by d (in by -d), corners mitred - except its two ends,
            // which stay where they are: that's where it meets the wall, and moved off it, the
            // slot between shows sky from the side.
            public CurveFrame Moved(float d)
            {
                var moved = new CurveFrame { FrameX = FrameX, FrameZ = FrameZ };
                for (int i = 0; i < Points.Count; i++)
                {
                    if (i == 0 || i == Points.Count - 1) { moved.Points.Add(Points[i]); continue; }
                    var before = i > 0 ? Right(Points[i] - Points[i - 1]) : Right(Points[1] - Points[0]);
                    var after = i + 1 < Points.Count ? Right(Points[i + 1] - Points[i]) : before;
                    var mitre = before + after;
                    mitre = mitre.LengthSquared() > 1e-12f ? Vector2.Normalize(mitre) : before;
                    moved.Points.Add(Points[i] + mitre * (d / MathF.Max(Vector2.Dot(mitre, before), 0.3f)));
                }
                return moved;
            }
        }

        public sealed class FlattenResult
        {
            public const int Kept = -1, Dropped = -2;

            public List<Billboard> Billboards = new();
            // Per source triangle: billboard index, or Kept / Dropped.
            public int[] Assignment = Array.Empty<int>();
            public int PlaneCount;
            public int KeptTriangles, DroppedTriangles;
            public float Tolerance;           // the epsilon it was flattened with
            // A curved billboard is two triangles per strip segment.
            public int OutputTriangles => Billboards.Sum(b => b.Curve != null ? 2 * b.Curve.Segments : 2) + KeptTriangles;
        }

        // Null when the model can be flattened; otherwise why not.
        public static string? WhyNotFlattenable(ModelRoot model)
        {
            if (model.LogicalSkins.Count > 0) return "This model is skinned. Flatten Model is for static models only.";
            if (model.LogicalAnimations.Count > 0) return "This model has animations. Flatten Model is for static models only.";
            return null;
        }

        // --- gathering -------------------------------------------------------------------------

        public static FlattenInput Gather(ModelRoot model)
        {
            var corners = new List<Vector3>();
            var vertexNormals = new List<Vector3>();
            var sources = new List<(int, int, int, int, int)>();
            var flipped = new List<bool>();
            var sourceNode = new List<int>();
            var sourceTriangle = new List<int>();
            var nodeWorld = new List<Matrix4x4>();

            foreach (var node in MeshNodes(model))
            {
                var mesh = node.Mesh;
                var world = node.WorldMatrix;
                int nodeIndex = nodeWorld.Count;
                nodeWorld.Add(world);
                bool mirrored = world.GetDeterminant() < 0;
                // Inverse-transpose, so normals stay perpendicular under non-uniform scale.
                var normalMatrix = Matrix4x4.Invert(world, out var inv) ? Matrix4x4.Transpose(inv) : world;

                for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                {
                    var prim = mesh.Primitives[primIdx];
                    if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                    if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;

                    var positions = posAcc.AsVector3Array();
                    var normals = prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc) ? nAcc.AsVector3Array() : null;

                    int triIndex = 0;
                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
                        sourceNode.Add(nodeIndex);
                        sourceTriangle.Add(triIndex++);
                        corners.Add(Vector3.Transform(positions[a], world));
                        corners.Add(Vector3.Transform(positions[b], world));
                        corners.Add(Vector3.Transform(positions[c], world));
                        foreach (int idx in new[] { a, b, c })
                        {
                            var vn = normals == null ? Vector3.Zero : Vector3.TransformNormal(normals[idx], normalMatrix);
                            vertexNormals.Add(vn.LengthSquared() > 1e-20f ? Vector3.Normalize(vn) : Vector3.Zero);
                        }
                        flipped.Add(mirrored);
                        sources.Add((mesh.LogicalIndex, primIdx, a, b, c));
                    }
                }
            }

            int n = sources.Count;
            var input = new FlattenInput
            {
                Corners = corners.ToArray(),
                Normals = new Vector3[n],
                Areas = new float[n],
                VertexNormals = vertexNormals.ToArray(),
                WeldIds = new int[n * 3],
                Sources = sources.ToArray(),
                SourceNode = sourceNode.ToArray(),
                SourceTriangle = sourceTriangle.ToArray(),
                NodeWorld = nodeWorld,
            };
            if (n == 0) return input;
            Finish(input, flipped);
            (input.FrameX, input.FrameZ) = FindBuildingAxes(input);
            return input;
        }

        // Normals, areas, bounds and the position weld, from the corners.
        internal static void Finish(FlattenInput input, IList<bool> flipped)
        {
            int n = input.TriangleCount;
            input.TotalArea = 0;

            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            foreach (var p in input.Corners) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            input.BoundsMin = min;
            input.BoundsMax = max;
            var size = max - min;
            input.Extent = MathF.Max(1e-6f, MathF.Max(size.X, MathF.Max(size.Y, size.Z)));

            for (int t = 0; t < n; t++)
            {
                var cross = Vector3.Cross(input.Corners[t * 3 + 1] - input.Corners[t * 3], input.Corners[t * 3 + 2] - input.Corners[t * 3]);
                float len = cross.Length();
                input.Areas[t] = len * 0.5f;
                input.TotalArea += len * 0.5f;
                if (len <= 1e-20f) continue;

                var normal = cross / len;
                // Vertex normals are the authoring tool's statement of which way is out; winding
                // on scanned or generated meshes (usually shipped doubleSided) is not reliable.
                var vn = input.VertexNormals[t * 3] + input.VertexNormals[t * 3 + 1] + input.VertexNormals[t * 3 + 2];
                if (vn.LengthSquared() > 1e-12f) { if (Vector3.Dot(vn, normal) < 0) normal = -normal; }
                else if (flipped[t]) normal = -normal;
                input.Normals[t] = normal;
            }

            // Weld on position so connectivity survives UV seams and split normals.
            float quantum = input.Extent * 1e-5f;
            var weld = new Dictionary<(long, long, long), int>();
            for (int i = 0; i < input.Corners.Length; i++)
            {
                var p = input.Corners[i];
                var key = ((long)MathF.Round(p.X / quantum), (long)MathF.Round(p.Y / quantum), (long)MathF.Round(p.Z / quantum));
                if (!weld.TryGetValue(key, out int id)) weld[key] = id = weld.Count;
                input.WeldIds[i] = id;
            }
        }

        // Every node in the default scene that draws a mesh - what Gather reads and what applying
        // a flatten replaces.
        public static List<Node> MeshNodes(ModelRoot model)
        {
            var result = new List<Node>();
            var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault();
            if (scene == null) return result;
            var stack = new Stack<Node>(scene.VisualChildren.Reverse());
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node.Mesh != null) result.Add(node);
                foreach (var child in node.VisualChildren.Reverse()) stack.Push(child);
            }
            return result;
        }

        // Buildings are boxes more often than not: the azimuth of their walls, taken modulo 90
        // degrees, piles up at one angle. Snapping planes to that frame keeps facades exactly
        // square to each other despite the noise in a scan's normals.
        private static (Vector3 X, Vector3 Z) FindBuildingAxes(FlattenInput input)
        {
            var hist = new double[90];
            for (int t = 0; t < input.TriangleCount; t++)
            {
                var nrm = input.Normals[t];
                float horizontal = 1f - MathF.Abs(nrm.Y);
                if (horizontal < 0.5f) continue;
                float deg = MathF.Atan2(nrm.Z, nrm.X) * 180f / MathF.PI;
                int bin = (int)MathF.Floor(((deg % 90f) + 90f) % 90f) % 90;
                hist[bin] += input.Areas[t] * horizontal;
            }

            int best = 0;
            double bestScore = -1;
            for (int b = 0; b < 90; b++)
            {
                double s = hist[(b + 89) % 90] + hist[b] * 2 + hist[(b + 1) % 90];
                if (s > bestScore) { bestScore = s; best = b; }
            }
            if (bestScore <= 0) return (Vector3.UnitX, Vector3.UnitZ);

            // Circular weighted mean over the peak's neighbourhood, for sub-degree accuracy.
            double sx = 0, sy = 0;
            for (int d = -2; d <= 2; d++)
            {
                int b = (best + d + 90) % 90;
                double ang = (b + 0.5) * 4 * Math.PI / 180; // x4 maps the 90-degree period to a full circle
                sx += hist[b] * Math.Cos(ang);
                sy += hist[b] * Math.Sin(ang);
            }
            float a = (float)(Math.Atan2(sy, sx) / 4);
            return (new Vector3(MathF.Cos(a), 0, MathF.Sin(a)), new Vector3(-MathF.Sin(a), 0, MathF.Cos(a)));
        }

        // --- solving ---------------------------------------------------------------------------

        public static FlattenResult Compute(FlattenInput input, FlattenSettings settings, CancellationToken ct) =>
            new Solver(input, settings, ct).Run();

        private sealed class Solver
        {
            private const int Unassigned = -3;
            // Kept as mesh from the start (FlattenInput.Locked): no plane ever claims it.
            private const int LockedMesh = -4;
            private bool[] _locked = Array.Empty<bool>();
            private bool IsLocked(int t) => _locked.Length > 0 && _locked[t];
            private List<CurvedRegions.Fold> _curves = new();
            private readonly Dictionary<CurvedRegions.Fold, List<int>> _curveTris = new();

            private readonly FlattenInput _in;
            private readonly FlattenSettings _s;
            private readonly CancellationToken _ct;
            private readonly float _degenerateArea, _alignAngle, _alignSlack;
            // Not readonly: the detail pass re-runs the search at a smaller tolerance.
            private float _eps;
            // Not readonly: the side-detail pass runs the same search against a smaller minimum.
            private float _minArea;
            private readonly int[] _plane;
            private readonly bool[] _absorbable;
            private readonly List<(Vector3 N, float Rho)> _planes = new();
            // The minimum size each plane was found under - side-detail planes are allowed to be
            // smaller than main ones, and BuildResult's stray-bit test has to know which is which.
            private readonly List<float> _planeMinArea = new();
            private bool _inSidePass, _inDetailPass;
            private readonly List<bool> _planeIsDetail = new();
            // Planes holding one isolated structure each (RefineDetail): already exactly one
            // thing, so BuildResult doesn't split them further by depth or proximity.
            private readonly HashSet<int> _planeIsStructure = new();
            // Fire-escape part planes: everything the part's billboard renders (FireEscapeParts);
            // a triangle can be in more than one, a corner post in both of its rails.
            private readonly Dictionary<int, List<int>> _partTris = new();
            private readonly HashSet<int> _floorParts = new();
            private readonly List<bool> _planeIsSide = new();
            // Stair planes are confined to their flight's box; null for every other plane.
            private readonly List<(Vector3 Min, Vector3 Max)?> _planeRegion = new();
            // Per plane: the depth its billboard is drawn at (see AcceptPlane).
            private readonly List<float> _planeDrawnAt = new();
            // Per plane: the tolerance it was found with. Every later fit test against the plane
            // uses this, not whatever tolerance the current pass runs at - otherwise a plane found
            // at the fine detail tolerance takes pieces up to the coarse one away from it, and a
            // rail is mixed with the stair behind it again.
            private readonly List<float> _planeEps = new();
            // Landings and balcony floors found while looking for stairs: too big to be treads,
            // and never a stair plane's to take, however close to the slope they sit.
            private readonly HashSet<int> _landings = new();
            private readonly Vector3[] _frame;

            private float[] _evKeys = Array.Empty<float>(), _evWeights = Array.Empty<float>();

            public Solver(FlattenInput input, FlattenSettings settings, CancellationToken ct)
            {
                _in = input;
                _s = settings;
                _ct = ct;
                _eps = MathF.Max(settings.Tolerance, input.Extent * 1e-6f);
                _minArea = MathF.Max(settings.MinBillboardArea, input.TotalArea * 1e-7f);
                _degenerateArea = input.Extent * input.Extent * 1e-12f;
                _alignAngle = AlignAngleDegrees * MathF.PI / 180f;
                _alignSlack = AlignSlackDegrees * MathF.PI / 180f;
                _plane = new int[input.TriangleCount];
                _absorbable = new bool[input.TriangleCount];
                _frame = new[] { input.FrameX, -input.FrameX, Vector3.UnitY, -Vector3.UnitY, input.FrameZ, -input.FrameZ };
            }

            public FlattenResult Run()
            {
                Array.Fill(_plane, Unassigned);
                _locked = _in.Locked.Length > 0 ? (bool[])_in.Locked.Clone() : new bool[_in.TriangleCount];
                if (_s.Curves == CurveHandling.CurvedBillboards)
                {
                    _curves = CurvedRegions.DetectFolds(_in);
                    foreach (var cyl in _curves)
                    {
                        _curveTris[cyl] = StripTriangles(cyl);
                        foreach (int t in _curveTris[cyl]) if (!SharedWithPlanes(cyl, t)) _locked[t] = true;
                    }
                }
                for (int t = 0; t < _locked.Length; t++)
                    if (_locked[t]) _plane[t] = LockedMesh;

                for (int round = 0; round < MaxRounds && _planes.Count < MaxPlanes; round++)
                {
                    var candidates = BuildCandidates(includeFrame: round == 0);
                    if (candidates.Count == 0) break;
                    if (!RunRound(candidates)) break;
                }

                AbsorbRemaining();
                if (_s.SideDetail) FindSidePlanes();
                var result = BuildResult();

                if (_s.DetailTolerance > 0 && _s.DetailTolerance < _eps && RefineDetail(result))
                    result = BuildResult();
                if (AddRoofOrnaments()) result = BuildResult();
                if (_s.Backdrops) AddBackdrops(result);
                AddCurvedBillboards(result);
                GrowForSweep(result);
                return result;
            }

            // See-through detail (fire escapes, railings) isn't flattened the way solid surfaces
            // are: a slab of +-epsilon either cuts one structure in two (half a round bar a few
            // centimetres behind the other half) or sweeps separate structures into one billboard
            // (a rail and the stair behind it, the left rail's end and the right rail). Instead a
            // fire escape is taken apart the way it's built (FireEscapeParts: a floor per balcony,
            // a rail per edge, per flight its treads and two sides), each part one plane
            // rendering everything in its box. What else the pass releases - loose trim, or
            // solid ornament that only looked see-through - goes onto a solid wall it fits, or
            // back to where it was.
            // Released: every see-through billboard whose triangles aren't already flat to within
            // the detail tolerance.
            private bool RefineDetail(FlattenResult result)
            {
                float detailEps = MathF.Max(_s.DetailTolerance, _in.Extent * 1e-6f);
                var released = new List<(int T, int Plane)>();
                foreach (var bb in result.Billboards)
                {
                    // Side billboards too: a see-through one (a rail seen from the side) needs
                    // the finer tolerance as much as anything; a solid side wall isn't released.
                    if (bb.Coverage >= DetailCoverage) continue;
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (int t in bb.Triangles)
                        for (int k = 0; k < 3; k++)
                        {
                            float d = Vector3.Dot(_in.Corners[t * 3 + k], bb.Normal);
                            lo = MathF.Min(lo, d);
                            hi = MathF.Max(hi, d);
                        }
                    if (hi - lo <= detailEps * 2) continue;

                    foreach (int t in bb.Triangles)
                    {
                        if (_plane[t] < 0) continue;
                        released.Add((t, _plane[t]));
                        _plane[t] = Unassigned;
                        _absorbable[t] = false;
                    }
                }
                if (released.Count == 0) return false;

                float mainEps = _eps, mainMin = _minArea;
                _eps = detailEps;
                _minArea = MathF.Max(_s.DetailMinArea, _in.TotalArea * 1e-7f);
                _inDetailPass = true;
                try
                {
                    var pool = new List<int>();
                    foreach (var (t, original) in released)
                        if (_in.Areas[t] > _degenerateArea) pool.Add(t);
                        else _plane[t] = original;

                    // Balconies and stairs are built from explicit parts first; what no part
                    // takes is clustered into structures below. The parts draw on every piece of
                    // see-through detail, not just what was released: a rail already flat enough
                    // to be left alone still belongs in its balcony's rail.
                    var stairs = DetectStairPlanes(pool);
                    var candidates = new HashSet<int>(pool);
                    foreach (var bb in result.Billboards)
                        if (bb.Coverage < DetailCoverage) candidates.UnionWith(bb.Triangles);
                    for (int t = 0; t < _in.TriangleCount; t++)
                        if (result.Assignment[t] == FlattenResult.Kept && !IsLocked(t)) candidates.Add(t);
                    candidates.RemoveWhere(t => _in.Areas[t] <= _degenerateArea);

                    var claimed = new HashSet<int>();
                    foreach (var (n, tris, floor) in FireEscapeParts(candidates, pool, stairs, detailEps))
                    {
                        AddPartPlane(n, tris, floor);
                        AddPartPlane(-n, tris, floor);
                        claimed.UnionWith(tris);
                    }
                    var rest = pool.Where(t => !claimed.Contains(t)).ToList();

                    // What isn't fire escape is mostly trim released with it - the curved coping
                    // of a gable, a window's moulding - and belongs on the solid wall plane it
                    // fits, within that plane's own tolerance: split off into little billboards of
                    // its own at its true depth, it no longer lines up with the rest of the wall's
                    // outline (from below, bits of coping float over the roofline).
                    var solidPlanes = result.Billboards.Where(b => b.Coverage >= DetailCoverage && !b.Detail)
                        .Select(b => b.PlaneIndex).Distinct().ToList();
                    float current = _eps;
                    _eps = mainEps;
                    try
                    {
                        var still = new List<int>();
                        foreach (int t in rest)
                        {
                            int best = -1;
                            float bestDot = 0.5f;
                            foreach (int p in solidPlanes)
                            {
                                float d = Vector3.Dot(_in.Normals[t], _planes[p].N);
                                if (d > bestDot && Fits(t, _planes[p].N, _planes[p].Rho)) { best = p; bestDot = d; }
                            }
                            if (best >= 0) _plane[t] = best; else still.Add(t);
                        }
                        rest = still;
                    }
                    finally { _eps = current; }

                    // Anything else goes back to the plane it had before it was released. Cut up
                    // into structures of its own, solid ornament that merely looked see-through
                    // (a corbel table's arches, a gable's curved coping) came apart into little
                    // cardboard flaps at odd depths, with sky between them.
                    var originalOf = released.ToDictionary(r => r.T, r => r.Plane);
                    foreach (int t in rest) _plane[t] = originalOf[t];
                }
                finally { _eps = mainEps; _minArea = mainMin; _inDetailPass = false; }
                return true;
            }

            // Every triangle facing well off the plane it was absorbed into (SideReleaseDot) is
            // released and searched again on its own, absorption off (they'd otherwise all be absorbable - that's how
            // they got here), under the side-detail minimum. Whatever no side plane claims goes
            // back where it was.
            private void FindSidePlanes()
            {
                var released = new List<(int T, int Plane)>();
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    int p = _plane[t];
                    if (p < 0 || _in.Areas[t] <= _degenerateArea) continue;
                    if (Vector3.Dot(_in.Normals[t], _planes[p].N) >= SideReleaseDot) continue;
                    released.Add((t, p));
                }
                if (released.Count == 0) return;

                // Two stages: pieces of see-through detail (a fire escape's rails and stairs) are
                // searched at the detail tolerance they were flattened with - at the full one a
                // single slab holds the right-hand rail and half a stair flight - and everything
                // else (a building's side wall) at the full tolerance.
                bool IsDetail(int plane) => _planeIsDetail[plane] || _planeRegion[plane] != null;
                float detailEps = _s.DetailTolerance > 0 && _s.DetailTolerance < _eps
                    ? MathF.Max(_s.DetailTolerance, _in.Extent * 1e-6f) : _eps;
                SidePass(released.Where(r => !IsDetail(r.Plane)).ToList(), _eps);
                SidePass(released.Where(r => IsDetail(r.Plane)).ToList(), detailEps);
            }

            private void SidePass(List<(int T, int Plane)> released, float eps)
            {
                if (released.Count == 0) return;
                foreach (var (t, _) in released)
                {
                    _plane[t] = Unassigned;
                    _absorbable[t] = false;
                }

                float mainMin = _minArea, mainEps = _eps;
                _minArea = MathF.Max(_s.SideMinArea, _in.TotalArea * 1e-7f);
                _eps = eps;
                _inSidePass = true;
                try
                {
                    for (int round = 0; round < MaxRounds && _planes.Count < MaxPlanes; round++)
                    {
                        var candidates = BuildCandidates(includeFrame: round == 0);
                        if (round == 0) AddMirroredSlants(candidates);
                        if (candidates.Count == 0 || !RunRound(candidates)) break;
                    }

                    Rehome(released);
                }
                finally { _minArea = mainMin; _eps = mainEps; _inSidePass = false; }
            }

            // A canted bay's two slanted sides are mirror images, but the main pass often gives
            // only one of them a plane - the other is swallowed by the facade and released here,
            // mixed with the jambs round its windows, whose normals pull the side pass's own
            // candidate ten degrees off (the window then lies on a plane cutting into the
            // facade). So the slants the main pass found, and their mirror images across the
            // building's axes, are candidates here too.
            private void AddMirroredSlants(List<Vector3> candidates)
            {
                float dedupeCos = MathF.Cos(5f * MathF.PI / 180f), nearCos = MathF.Cos(15f * MathF.PI / 180f);
                for (int p = 0; p < _planes.Count; p++)
                {
                    if (_planeIsSide[p] || _planeIsDetail[p] || _planeRegion[p] != null) continue;
                    var n = _planes[p].N;
                    if (MathF.Abs(n.Y) > 0.3f || _frame.Any(f => Vector3.Dot(f, n) > 0.9999f)) continue;
                    float x = Vector3.Dot(n, _in.FrameX), z = Vector3.Dot(n, _in.FrameZ);
                    foreach (var (sx, sz) in new[] { (1f, 1f), (-1f, 1f), (1f, -1f), (-1f, -1f) })
                    {
                        var m = Vector3.Normalize(_in.FrameX * (x * sx) + _in.FrameZ * (z * sz) + Vector3.UnitY * n.Y);
                        // It replaces the side pass's own near miss: that one scores higher (it
                        // collects the jambs too) and would claim the window first.
                        candidates.RemoveAll(c => Vector3.Dot(c, m) > nearCos && !_frame.Any(f => Vector3.Dot(f, c) > 0.9999f));
                        if (!candidates.Any(c => Vector3.Dot(c, m) > dedupeCos)) candidates.Add(m);
                    }
                }
            }

            // One face of a fire-escape part: a plane rendering all of `tris`, drawn at their
            // projected-area-weighted depth (seen from n's side). Triangles no part owns yet
            // become this one's.
            private void AddPartPlane(Vector3 n, List<int> tris, bool floor)
            {
                double weightSum = 0, depthSum = 0, plainSum = 0;
                foreach (int t in tris)
                {
                    double d = (Vector3.Dot(_in.Corners[t * 3], n) + Vector3.Dot(_in.Corners[t * 3 + 1], n) + Vector3.Dot(_in.Corners[t * 3 + 2], n)) / 3.0;
                    plainSum += d;
                    float w = _in.Areas[t] * MathF.Abs(Vector3.Dot(_in.Normals[t], n));
                    weightSum += w;
                    depthSum += w * d;
                }
                float rho = (float)(weightSum > 0 ? depthSum / weightSum : plainSum / tris.Count);
                int p = RegisterPlane(n, rho, rho, _minArea * FittedMinScale, null);
                _planeIsStructure.Add(p);
                _partTris[p] = tris;
                if (floor) _floorParts.Add(p);
                foreach (int t in tris)
                    if (!_partTris.ContainsKey(_plane[t])) _plane[t] = p;
            }

            // A fire escape, taken apart the way it's built: per balcony, the floor (a slab of
            // up-facing grating and the frame just under it) and a rail along each edge of the
            // floor that has one (a thin slab standing on that edge); per stair flight, the whole
            // flight - treads, stringers, handrail - in the box its treads span. Each part is one
            // plane, and its billboard renders every triangle in its box straight down the
            // plane's normal, whichever way the triangle faces and whether or not another part
            // has it too: a rail's posts are all there however thin, and the corner post shows
            // in both rails that meet at it. Only a flight leaves out what the balcony's floor
            // and rails already hold, the landing it arrives on.
            // Returns each part's facing (one side; the other is its negation) and triangles.
            private List<(Vector3 N, List<int> Tris, bool Floor)> FireEscapeParts(
                HashSet<int> candidates, List<int> pool, List<(Vector3 N, float Rho, (Vector3 Min, Vector3 Max) Region)> stairs, float detailEps)
            {
                var parts = new List<(Vector3 N, List<int> Tris, bool Floor)>();
                var ax = new[] { _in.FrameX, Vector3.UnitY, _in.FrameZ };
                float Coord(Vector3 p, int a) => Vector3.Dot(p, ax[a]);
                (float Lo, float Hi) Span(int t, int a)
                {
                    float c0 = Coord(_in.Corners[t * 3], a), c1 = Coord(_in.Corners[t * 3 + 1], a), c2 = Coord(_in.Corners[t * 3 + 2], a);
                    return (MathF.Min(c0, MathF.Min(c1, c2)), MathF.Max(c0, MathF.Max(c1, c2)));
                }
                float Mid(int t, int a) => (Coord(_in.Corners[t * 3], a) + Coord(_in.Corners[t * 3 + 1], a) + Coord(_in.Corners[t * 3 + 2], a)) / 3f;

                float gap = MathF.Max(2f * detailEps, _in.Extent * 0.01f);
                float layerGap = MathF.Max(0.5f * detailEps, _in.Extent * 0.002f);
                float minSide = _in.Extent * 0.02f;
                float below = MathF.Max(2f * detailEps, _in.Extent * 0.012f);   // floor frame under the grating
                float above = _in.Extent * 0.005f;                              // grating's own thickness above its top
                float railDepth = MathF.Max(detailEps, _in.Extent * 0.008f);    // rail slab, inward from the floor's edge
                float outside = _in.Extent * 0.005f;
                float minPart = _minArea * FittedMinScale;

                // Floors: up-facing detail piled up at one height (a histogram of up-facing area
                // over height - consecutive heights would chain a whole fire escape's bar tops
                // into one layer), spread wide in both directions, with a stair flight arriving
                // or leaving: that's what tells a fire-escape balcony from a cornice or a step.
                // Only within the ground the flights cover: a storey's stone band runs at the
                // same height a hand's width behind the balcony floor, and would join it.
                bool UnderFlights(int t)
                {
                    var c = (_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f;
                    return stairs.Any(st => c.X >= st.Region.Min.X && c.X <= st.Region.Max.X && c.Z >= st.Region.Min.Z && c.Z <= st.Region.Max.Z
                        && c.Y >= st.Region.Min.Y - _in.Extent * 0.05f && c.Y <= st.Region.Max.Y + _in.Extent * 0.05f);
                }
                var ups = candidates.Where(t => _in.Normals[t].Y > 0.9f && UnderFlights(t)).ToList();
                var floors = new List<(float X0, float X1, float Z0, float Z1, float Top)>();
                var bins = new Dictionary<int, float>();
                foreach (int t in ups)
                {
                    int b = (int)MathF.Floor(Mid(t, 1) / layerGap);
                    bins[b] = bins.GetValueOrDefault(b) + _in.Areas[t];
                }
                float binMin = 0.25f * minSide * minSide;
                var dense = bins.Where(kv => kv.Value >= binMin).Select(kv => kv.Key).OrderBy(b => b).ToList();
                var layers = new List<List<int>>();
                for (int i = 0; i < dense.Count;)
                {
                    int j = i + 1;
                    while (j < dense.Count && dense[j] - dense[j - 1] <= 2) j++;
                    int b0 = dense[i] - 1, b1 = dense[j - 1] + 1;
                    layers.Add(ups.Where(t => { int b = (int)MathF.Floor(Mid(t, 1) / layerGap); return b >= b0 && b <= b1; }).ToList());
                    i = j;
                }
                bool FlightTouches(float x0, float x1, float z0, float z1, float top) => stairs.Any(st =>
                {
                    var (lo, hi) = st.Region;
                    float rx0 = Coord(lo, 0), rx1 = Coord(hi, 0), rz0 = Coord(lo, 2), rz1 = Coord(hi, 2);
                    if (rx0 > rx1) (rx0, rx1) = (rx1, rx0);
                    if (rz0 > rz1) (rz0, rz1) = (rz1, rz0);
                    return rx0 <= x1 && x0 <= rx1 && rz0 <= z1 && z0 <= rz1 && top >= lo.Y && top <= hi.Y;
                });
                foreach (var layer in layers)
                {
                    // Patches within `gap` of each other, across the layer, are one floor.
                    layer.Sort((p, q) => Span(p, 0).Lo.CompareTo(Span(q, 0).Lo));
                    var uf = new UnionFind(layer.Count);
                    for (int a = 0; a < layer.Count; a++)
                    {
                        var (ax0, ax1) = Span(layer[a], 0);
                        var (az0, az1) = Span(layer[a], 2);
                        for (int b = a + 1; b < layer.Count && Span(layer[b], 0).Lo <= ax1 + gap; b++)
                        {
                            var (bz0, bz1) = Span(layer[b], 2);
                            if (bz0 <= az1 + gap && az0 <= bz1 + gap) uf.Union(a, b);
                        }
                    }
                    foreach (var group in Enumerable.Range(0, layer.Count).GroupBy(uf.Find))
                    {
                        var tris = group.Select(k => layer[k]).ToList();
                        float x0 = tris.Min(t => Span(t, 0).Lo), x1 = tris.Max(t => Span(t, 0).Hi);
                        float z0 = tris.Min(t => Span(t, 2).Lo), z1 = tris.Max(t => Span(t, 2).Hi);
                        // The floor's height is where most of its up-facing area is (a tread at
                        // the landing sits a little higher and mustn't lift it).
                        var byHeight = tris.OrderBy(t => Span(t, 1).Hi).ToList();
                        float half = byHeight.Sum(t => _in.Areas[t]) / 2f, acc = 0f, top = Span(byHeight[^1], 1).Hi;
                        foreach (int t in byHeight)
                            if ((acc += _in.Areas[t]) >= half) { top = Span(t, 1).Hi; break; }
                        if (x1 - x0 < minSide || z1 - z0 < minSide) continue;
                        if (tris.Sum(t => _in.Areas[t]) < 0.25f * (x1 - x0) * (z1 - z0)) continue;
                        if (!FlightTouches(x0, x1, z0, z1, top)) continue;
                        floors.Add((x0, x1, z0, z1, top));
                    }
                }

                var floorOrRail = new HashSet<int>();
                foreach (var f in floors)
                {
                    // A rail stands no higher than most of the way to the next floor above.
                    float railMax = _in.Extent * 0.06f;
                    foreach (var g in floors)
                        if (g.Top > f.Top && g.X0 < f.X1 && f.X0 < g.X1 && g.Z0 < f.Z1 && f.Z0 < g.Z1)
                            railMax = MathF.Min(railMax, 0.8f * (g.Top - f.Top));

                    var slab = candidates.Where(t =>
                    {
                        float x = Mid(t, 0), z = Mid(t, 2);
                        var y = Span(t, 1);
                        return x >= f.X0 - outside && x <= f.X1 + outside && z >= f.Z0 - outside && z <= f.Z1 + outside
                            && y.Lo >= f.Top - below && y.Hi <= f.Top + above;
                    }).ToList();
                    if (slab.Count > 0)
                    {
                        parts.Add((Vector3.UnitY, slab, true));
                        floorOrRail.UnionWith(slab);
                    }

                    // Rails: along each edge (axis a at its low or high end), across the edge's
                    // whole length, standing from the floor frame to the rail's top.
                    foreach (int a in new[] { 0, 2 })
                        foreach (bool high in new[] { false, true })
                        {
                            int o = 2 - a;
                            float edge = a == 0 ? (high ? f.X1 : f.X0) : (high ? f.Z1 : f.Z0);
                            float o0 = o == 0 ? f.X0 : f.Z0, o1 = o == 0 ? f.X1 : f.Z1;
                            float lo = high ? edge - railDepth : edge - outside, hi = high ? edge + outside : edge + railDepth;
                            var rail = candidates.Where(t =>
                            {
                                var s = Span(t, a);
                                var y = Span(t, 1);
                                float m = Mid(t, o);
                                return s.Lo >= lo && s.Hi <= hi && m >= o0 - railDepth && m <= o1 + railDepth
                                    && y.Lo >= f.Top - below && y.Hi <= f.Top + railMax;
                            }).ToList();
                            if (rail.Count == 0 || rail.Sum(t => _in.Areas[t]) < minPart) continue;
                            if (rail.Max(t => Span(t, 1).Hi) - f.Top < _in.Extent * 0.02f) continue;   // a lip, not a rail
                            parts.Add((high ? ax[a] : -ax[a], rail, false));
                            floorOrRail.UnionWith(rail);
                        }
                }

                // Flights: everything in the box the treads span, but not the balcony they land on.
                // A flight is its treads - one plane along its slope, seen from above and below,
                // reaching from under the stringers to just over the treads - and its two sides:
                // an upright plane at each edge holding that side's stringer, posts and handrail,
                // like a balcony rail. The handrail stands clear of the treads; drawn onto the
                // slope it would land on the stringer and vanish.
                float band = MathF.Max(2f * detailEps, _in.Extent * 0.01f);
                foreach (var st in stairs)
                {
                    // Only a flight between balconies: a stoop or a chance line of ornament with
                    // no floor at either end isn't a fire escape.
                    var (rlo, rhi) = st.Region;
                    float rx0 = MathF.Min(Coord(rlo, 0), Coord(rhi, 0)), rx1 = MathF.Max(Coord(rlo, 0), Coord(rhi, 0));
                    float rz0 = MathF.Min(Coord(rlo, 2), Coord(rhi, 2)), rz1 = MathF.Max(Coord(rlo, 2), Coord(rhi, 2));
                    if (!floors.Any(f => f.X0 <= rx1 && rx0 <= f.X1 && f.Z0 <= rz1 && rz0 <= f.Z1 && f.Top >= rlo.Y && f.Top <= rhi.Y))
                        continue;

                    var flight = candidates.Where(t => InRegion(st.Region, t) && !floorOrRail.Contains(t)).ToList();
                    if (flight.Count == 0 || flight.Sum(t => _in.Areas[t]) < minPart) continue;

                    bool OnSlope(int t)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float d = Vector3.Dot(_in.Corners[t * 3 + c], st.N) - st.Rho;
                            if (d > 0.5f * band || d < -2f * band) return false;
                        }
                        return true;
                    }
                    var treads = flight.Where(OnSlope).ToList();
                    if (treads.Count > 0 && treads.Sum(t => _in.Areas[t]) >= minPart) parts.Add((st.N, treads, false));

                    var run = Vector3.Normalize(new Vector3(st.N.X, 0, st.N.Z));
                    var across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, run));
                    float Across(Vector3 p) => Vector3.Dot(p, across);
                    if (treads.Count == 0) continue;
                    float e0 = treads.Min(t => MathF.Min(Across(_in.Corners[t * 3]), MathF.Min(Across(_in.Corners[t * 3 + 1]), Across(_in.Corners[t * 3 + 2]))));
                    float e1 = treads.Max(t => MathF.Max(Across(_in.Corners[t * 3]), MathF.Max(Across(_in.Corners[t * 3 + 1]), Across(_in.Corners[t * 3 + 2]))));
                    foreach (bool high in new[] { false, true })
                    {
                        float lo = high ? e1 - railDepth : e0 - outside, hi = high ? e1 + outside : e0 + railDepth;
                        var side = flight.Where(t =>
                        {
                            for (int c = 0; c < 3; c++)
                            {
                                float a = Across(_in.Corners[t * 3 + c]);
                                if (a < lo || a > hi) return false;
                            }
                            return true;
                        }).ToList();
                        if (side.Count > 0 && side.Sum(t => _in.Areas[t]) >= minPart) parts.Add((high ? across : -across, side, false));
                    }
                }

                // Strays: fire-escape pieces just outside every box (the posts of a ladder
                // between a balcony's back rail and the wall, a bit of floor frame behind the
                // grating) would otherwise become billboards of their own beside the part they
                // belong with. A connected piece joins the part whose plane it lies closest to,
                // if it's flat along that plane (all of it within reach of it - a bracket running
                // diagonally under a floor isn't) and lies over or near the part's outline.
                float reach = _in.Extent * 0.035f, grow = _in.Extent * 0.06f;
                var inParts = new HashSet<int>(parts.SelectMany(p => p.Tris));
                var planes = parts.Select(p =>
                {
                    var (u, v) = PlaneBasis(p.N, _in.FrameX);
                    float depth = p.Tris.Average(t => Vector3.Dot((_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f, p.N));
                    var pts = p.Tris.SelectMany(t => new[] { _in.Corners[t * 3], _in.Corners[t * 3 + 1], _in.Corners[t * 3 + 2] }).Select(q => Project(q, u, v)).ToList();
                    float d0 = p.Tris.Min(t => MathF.Min(Vector3.Dot(_in.Corners[t * 3], p.N), MathF.Min(Vector3.Dot(_in.Corners[t * 3 + 1], p.N), Vector3.Dot(_in.Corners[t * 3 + 2], p.N))));
                    float d1 = p.Tris.Max(t => MathF.Max(Vector3.Dot(_in.Corners[t * 3], p.N), MathF.Max(Vector3.Dot(_in.Corners[t * 3 + 1], p.N), Vector3.Dot(_in.Corners[t * 3 + 2], p.N))));
                    return (U: u, V: v, Depth: depth, D0: d0, D1: d1, Min: pts.Aggregate(Vector2.Min) - new Vector2(grow), Max: pts.Aggregate(Vector2.Max) + new Vector2(grow),
                            CoreMin: pts.Aggregate(Vector2.Min) - new Vector2(railDepth), CoreMax: pts.Aggregate(Vector2.Max) + new Vector2(railDepth));
                }).ToList();
                foreach (var piece in Components(pool.Where(t => !inParts.Contains(t)).ToList()))
                {
                    int best = -1;
                    float bestDist = float.MaxValue;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var pl = planes[i];
                        float far = 0f, near = float.MaxValue, back = float.MinValue;
                        var min = new Vector2(float.MaxValue);
                        var max = new Vector2(float.MinValue);
                        foreach (int t in piece)
                            for (int c = 0; c < 3; c++)
                            {
                                var q = _in.Corners[t * 3 + c];
                                float d = Vector3.Dot(q, parts[i].N);
                                far = MathF.Max(far, MathF.Abs(d - pl.Depth));
                                near = MathF.Min(near, d);
                                back = MathF.Max(back, d);
                                var uv = Project(q, pl.U, pl.V);
                                min = Vector2.Min(min, uv);
                                max = Vector2.Max(max, uv);
                            }
                        // Close to the part's own geometry in depth, not just its plane.
                        if (near > pl.D1 + railDepth || back < pl.D0 - railDepth) continue;
                        if (far > reach || min.X < pl.Min.X || min.Y < pl.Min.Y || max.X > pl.Max.X || max.Y > pl.Max.Y) continue;
                        // ...and be centred over the part itself: a window's frame just past the
                        // end of a balcony lies in the side rail's plane too, and isn't its.
                        var centre = (min + max) / 2f;
                        if (centre.X < pl.CoreMin.X || centre.Y < pl.CoreMin.Y || centre.X > pl.CoreMax.X || centre.Y > pl.CoreMax.Y) continue;
                        // It has to lie along the plane, not run through it: a handrail end
                        // sticking out past a side rail would draw as a stray line on it.
                        if (back - near > 0.5f * MathF.Max(max.X - min.X, max.Y - min.Y)) continue;
                        if (far < bestDist) { bestDist = far; best = i; }
                    }
                    if (best >= 0) parts[best].Tris.AddRange(piece);
                }
                return parts;
            }

            // Roof ornaments - a finial on a gable's corner, a chimney pot - are small and stand
            // free, so they're seen from every side. Flattened as part of the walls they'd show
            // twice, a front view on the facade and a side view on the side wall, a few
            // centimetres apart. Instead each one is two upright planes crossing at its own axis,
            // one along each of the building's axes, both rendering the whole ornament.
            // Found on a height map of the roof (the highest point over each little cell of
            // ground): a small patch rising well above everything a short way around it.
            private bool AddRoofOrnaments()
            {
                float cell = _in.Extent * 0.005f;
                const int Ring = 4;                       // cells out to "the surroundings"
                float rise = _in.Extent * 0.01f;
                var ax = _in.FrameX;
                var az = _in.FrameZ;
                (int X, int Z) Cell(Vector3 p) => ((int)MathF.Floor(Vector3.Dot(p, ax) / cell), (int)MathF.Floor(Vector3.Dot(p, az) / cell));

                var height = new Dictionary<(int X, int Z), float>();
                void Put(Vector3 p)
                {
                    var k = Cell(p);
                    if (!height.TryGetValue(k, out float h) || p.Y > h) height[k] = p.Y;
                }
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_in.Areas[t] <= _degenerateArea) continue;
                    Vector3 a = _in.Corners[t * 3], b = _in.Corners[t * 3 + 1], c = _in.Corners[t * 3 + 2];
                    Put(a); Put(b); Put(c);
                    Put((a + b) / 2); Put((b + c) / 2); Put((c + a) / 2); Put((a + b + c) / 3);
                }

                float Around((int X, int Z) k)
                {
                    float m = float.NegativeInfinity;
                    for (int d = -Ring; d <= Ring; d++)
                        foreach (var n in new[] { (k.X + d, k.Z - Ring), (k.X + d, k.Z + Ring), (k.X - Ring, k.Z + d), (k.X + Ring, k.Z + d) })
                            if (height.TryGetValue(n, out float h)) m = MathF.Max(m, h);
                    return m;
                }
                var peaks = height.Where(kv => kv.Value >= Around(kv.Key) + rise).Select(kv => kv.Key).ToHashSet();
                if (peaks.Count == 0) return false;

                // Patches of neighbouring peak cells, each small enough to stand inside its ring.
                var seen = new HashSet<(int X, int Z)>();
                var blobs = new List<List<(int X, int Z)>>();
                foreach (var start in peaks)
                {
                    if (!seen.Add(start)) continue;
                    var blob = new List<(int X, int Z)> { start };
                    for (int i = 0; i < blob.Count; i++)
                        for (int dx = -1; dx <= 1; dx++)
                            for (int dz = -1; dz <= 1; dz++)
                            {
                                var n = (blob[i].X + dx, blob[i].Z + dz);
                                if (peaks.Contains(n) && seen.Add(n)) blob.Add(n);
                            }
                    if (blob.Max(k => k.X) - blob.Min(k => k.X) >= 2 * Ring - 1 || blob.Max(k => k.Z) - blob.Min(k => k.Z) >= 2 * Ring - 1) continue;
                    blobs.Add(blob);
                }
                if (blobs.Count == 0) return false;

                var ornamentOf = new Dictionary<(int X, int Z), int>();
                var bases = new List<float>();
                for (int i = 0; i < blobs.Count; i++)
                {
                    // The whole pillar top down to the coping it stands on (the typical height
                    // around it, not the highest - a taller gable step alongside would cut the
                    // pedestal off and leave it split between the facade and the side wall).
                    var around = new List<float>();
                    foreach (var k in blobs[i])
                        for (int d = -Ring; d <= Ring; d++)
                            foreach (var n in new[] { (k.X + d, k.Z - Ring), (k.X + d, k.Z + Ring), (k.X - Ring, k.Z + d), (k.X + Ring, k.Z + d) })
                                if (height.TryGetValue(n, out float h)) around.Add(h);
                    around.Sort();
                    bases.Add(around.Count > 0 ? around[around.Count / 2] : blobs[i].Min(k => height[k]) - rise);
                    foreach (var k in blobs[i])
                        for (int dx = -2; dx <= 2; dx++)
                            for (int dz = -2; dz <= 2; dz++)
                                ornamentOf.TryAdd((k.X + dx, k.Z + dz), i);
                }
                var tris = blobs.Select(_ => new List<int>()).ToList();
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_in.Areas[t] <= _degenerateArea) continue;
                    var centre = (_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f;
                    if (!IsLocked(t) && ornamentOf.TryGetValue(Cell(centre), out int i) && centre.Y >= bases[i]) tris[i].Add(t);
                }

                bool any = false;
                _inDetailPass = true;
                try
                {
                    foreach (var ornament in tris)
                    {
                        if (ornament.Count == 0) continue;
                        // Standing up, not a lip along the roof's edge.
                        float top = ornament.Max(t => MathF.Max(_in.Corners[t * 3].Y, MathF.Max(_in.Corners[t * 3 + 1].Y, _in.Corners[t * 3 + 2].Y)));
                        float bottom = ornament.Min(t => MathF.Min(_in.Corners[t * 3].Y, MathF.Min(_in.Corners[t * 3 + 1].Y, _in.Corners[t * 3 + 2].Y)));
                        if (top - bottom < 2f * rise) continue;
                        // A finial or a chimney is small. Reaching most of the way down the
                        // building, its "surroundings" were the ground inside a roofless shell.
                        if (top - bottom > 0.2f * _in.Extent) continue;
                        foreach (var n in new[] { ax, -ax, az, -az }) AddPartPlane(n, ornament, false);
                        any = true;
                    }
                }
                finally { _inDetailPass = false; }
                return any;
            }

            // However carefully the billboards are placed, a real building's depth leaves gaps
            // between them at a slant: the wedges between the flat pieces of a curved bay window,
            // under a cornice's overhang, behind a balcony floor, round a corner ledge. And
            // there's nothing behind them - the models are shells, with no wall behind a bay and
            // no roof deck above a soffit - so every gap shows sky. Backdrops fill them in:
            // low-resolution quads (only ever seen through the gaps) showing the building as
            // seen from one side, set just behind what they show, so through a gap you see the
            // building's own colours a little out of place instead of sky.
            //  - Front and back: one each, behind nearly everything facing that way (the back
            //    5% by area - a recessed window's glass included). The main wall is in front of
            //    it everywhere but where the gaps are.
            //  - Sides and from below: there's no wall to hide behind - a plane deep inside the
            //    building would show a cornice's end or a bay's side far from where it really is,
            //    a ghost floating off the building. So these are layered: what faces that way is
            //    sliced by depth (1.2% of the model's size thick) and split into nearby patches,
            //    and each patch gets its own backdrop just behind itself - under a cornice, right
            //    above its soffit.
            private void AddBackdrops(FlattenResult result)
            {
                float behind = _in.Extent * 0.002f;
                float layer = _in.Extent * 0.012f;
                // A curve's strip covers what's on it; sideways backdrops of its pieces (a bay
                // window's jambs) only stand out as streaks up the curve. (Front and underneath,
                // they still fill the gaps round its edges.)
                var onCurve = new HashSet<int>(_curveTris.Values.SelectMany(v => v));
                float minArea = _in.TotalArea * 0.001f;
                foreach (var n in new[] { _in.FrameZ, -_in.FrameZ, _in.FrameX, -_in.FrameX, -Vector3.UnitY })
                {
                    // The layered sides and underside take only what faces them squarely: a curved
                    // corner tower's faces turn gradually from front to side, and those facing
                    // mostly forward are the front's to show - drawn flat on a side slice they
                    // widen the tower's outline.
                    bool sideways = MathF.Abs(n.X) > 0.9f;
                    float facing = MathF.Abs(n.Z) > 0.9f ? VisibleDot : 0.45f;
                    var tris = new List<int>();
                    for (int t = 0; t < _in.TriangleCount; t++)
                        if (_in.Areas[t] > _degenerateArea && result.Assignment[t] != FlattenResult.Dropped && Vector3.Dot(_in.Normals[t], n) > facing
                            && !OnOwnTiltedBillboard(result, t, n) && !(sideways && onCurve.Contains(t)))
                            tris.Add(t);
                    float area = tris.Sum(t => _in.Areas[t]);
                    if (area < minArea) continue;
                    float Depth(int t) => Vector3.Dot((_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f, n);
                    var (u, v) = PlaneBasis(n, _in.FrameX);
                    // Never outside the model: behind the outermost slice (a side wall's own
                    // skin) a backdrop would stand just clear of the building and widen its
                    // outline by a pixel or two all the way up.
                    float inside = float.MaxValue;
                    for (int c = 0; c < _in.Corners.Length; c++) inside = MathF.Min(inside, Vector3.Dot(_in.Corners[c], n));
                    inside += behind;

                    if (MathF.Abs(n.Z) > 0.9f)
                    {
                        var depths = tris.Select(t => (D: Depth(t), A: _in.Areas[t])).OrderBy(x => x.D).ToList();
                        float acc = 0f, depth = depths[0].D;
                        foreach (var (d, a) in depths)
                            if ((acc += a) >= 0.05f * area) { depth = d; break; }
                        // Only what's in front of it: the few things further back (a rear
                        // stair tower rising over the roof) drawn forward onto it would stand
                        // up out of the building's outline.
                        var inFront = tris.Where(t => Depth(t) >= depth - behind).ToList();
                        result.Billboards.Add(MakeBackdrop(n, u, v, MathF.Max(depth - behind, inside), inFront, keepInside: Vector3.Dot(n, _in.FrameZ) < 0));
                        // Mouldings turned down toward the street and standing well out in front
                        // (the cornice round a bay's foot) are layered as well: seen from below
                        // through a gap in them, the deep backdrop shows the building a long way
                        // back and so a long way off - brick in a cornice.
                        tris = tris.Where(t => Vector3.Dot(_in.Normals[t], n) > 0.45f && Depth(t) > depth + layer).ToList();
                    }

                    var sorted = tris.OrderBy(Depth).ToList();
                    for (int i = 0; i < sorted.Count;)
                    {
                        float start = Depth(sorted[i]);
                        int j = i;
                        while (j < sorted.Count && Depth(sorted[j]) - start <= layer) j++;
                        var slice = sorted.GetRange(i, j - i);
                        i = j;
                        foreach (var group in SplitLayer(slice, u, v, n, layer))
                        {
                            float groupArea = group.Sum(t => _in.Areas[t]);
                            if (groupArea < minArea) continue;
                            // A front's main wall needs none of its own: the deep one is right
                            // behind it. Nor does what doesn't turn down to the street (a bay's
                            // balcony and the band under it): only seen straight on, its gaps are
                            // the ones the real building has.
                            if (MathF.Abs(n.Z) > 0.9f && (groupArea > 0.2f * area
                                || group.Sum(t => _in.Normals[t].Y * _in.Areas[t]) > -0.1f * groupArea)) continue;
                            // Behind all but the last few percent of the patch (by area), not
                            // behind its single deepest corner: a couple of tiny faces at the far
                            // side of the slice lifted a whole row of window sills' undersides a
                            // layer out of place, and seen from below they cut a line across the
                            // glass. Not behind its bulk either - under a cornice that brought
                            // the soffit down across the tops of its brackets.
                            var byDepth = group.Select(t => (D: MathF.Min(Vector3.Dot(_in.Corners[t * 3], n), MathF.Min(Vector3.Dot(_in.Corners[t * 3 + 1], n), Vector3.Dot(_in.Corners[t * 3 + 2], n))), A: _in.Areas[t]))
                                .OrderBy(x => x.D).ToList();
                            float back = byDepth[0].D, skipped = 0f;
                            foreach (var (d, a) in byDepth)
                            {
                                back = d;
                                if ((skipped += a) > 0.05f * groupArea) break;
                            }
                            var backdrop = MakeBackdrop(n, u, v, MathF.Max(back - behind, inside), group);
                            // Not behind a railing: at a backdrop's low resolution its gaps
                            // close up into a solid panel.
                            if (MathF.Abs(n.Z) > 0.9f && backdrop.Coverage < 0.5f) continue;
                            result.Billboards.Add(backdrop);
                        }
                    }
                }
            }

            // The bake draws an upright surface's pieces again as seen from below (see
            // BillboardBaker's sweep fill): a coping standing out in front of the facade shows
            // above the facade's top edge. The rectangle grows up to hold that (not down: the
            // atlas room cost more than the little it filled); no edge texels are stretched
            // into it (WeldedMin/Max).
            // What a curve's strip draws: the upright and sloped faces of its region, and
            // everything upright just behind its outline (the wall behind a bay, deep reveals) -
            // drawn behind the surface, they show only through its gaps, where as planes of
            // their own they stood out as streaks up the curve. Flat faces (a curved cornice's
            // soffit, the tops of sills) are flat whatever shape they follow, and seen from the
            // outline they'd be edge-on and draw nothing - they stay with the planes. Near the
            // ends, only what faces out: a face turned back in there is a wall meeting the curve
            // (a tower's back meeting the back facade), and the strip there faces away from
            // anyone who sees it.
            private List<int> StripTriangles(CurvedRegions.Fold fold)
            {
                var region = new HashSet<int>(fold.Region);
                var result = new List<int>();
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_in.Areas[t] <= _degenerateArea || MathF.Abs(_in.Normals[t].Y) >= 0.8f) continue;
                    var c = Centroid(t);
                    if (c.Y < fold.YMin - _in.Extent * 0.01f || c.Y > fold.YMax + _in.Extent * 0.01f) continue;
                    bool nearEnd = NearCurveEnd(fold, t);
                    // Never what faces back in: that's seen from behind the curve (the back of
                    // the wall behind a bay), where the strip can't be seen at all.
                    float outward = Outwardness(fold, t);
                    if (outward < -0.3f) continue;
                    if (region.Contains(t) ? (outward > 0.2f || !nearEnd) : (!nearEnd && InsideCurve(fold, t))) result.Add(t);
                }
                return result;
            }

            private Vector3 Centroid(int t) => (_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f;

            // Just behind the outline, over its length: the wall behind a bay, deep reveals.
            // Only just behind it: a shallow bay's axis can lie behind the whole building, and
            // deeper in is the rest of it (the back facade).
            private bool InsideCurve(CurvedRegions.Fold fold, int t)
            {
                var c = Centroid(t);
                if (c.Y < fold.YMin || c.Y > fold.YMax) return false;
                float d = fold.Frame.Depth(c);
                if (d >= 0 || d < -_in.Extent * 0.06f) return false;
                float s = fold.Frame.Project(c).X;
                return s > 0 && s < fold.Frame.Length;
            }

            // How squarely a face turns out from the outline (its horizontal normal against the
            // outline's outward direction there): 1 straight out, 0 sideways, -1 back in.
            private float Outwardness(CurvedRegions.Fold fold, int t)
            {
                var c = Centroid(t);
                var n = _in.Normals[t] - Vector3.UnitY * _in.Normals[t].Y;
                if (n.LengthSquared() < 1e-12f) return 0f;
                return Vector3.Dot(Vector3.Normalize(n), fold.Frame.Outward(fold.Frame.Project(c).X));
            }

            // Where a curve ends it meets a flat wall (a tower's back meeting the back facade,
            // a bay's side meeting the facade). Taken off that wall's plane, the pieces there
            // leave the wall's billboard stopping short of the strip's end, and at a slant the
            // slot between them shows sky right up the building. So within a margin of each
            // end they stay with the planes too.
            // On the planes as well as the strip: near the ends (above), and on a canted bay what
            // faces well away from its run - a pilaster's side, seen from beside the bay where
            // the run itself is edge-on and shows nothing.
            private bool SharedWithPlanes(CurvedRegions.Fold fold, int t) =>
                NearCurveEnd(fold, t) || (fold.Shares && Outwardness(fold, t) < 0.5f);

            private bool NearCurveEnd(CurvedRegions.Fold fold, int t)
            {
                float s = fold.Frame.Project(Centroid(t)).X;
                return s < fold.EndMargin || s > fold.Frame.Length - fold.EndMargin;
            }

            // One billboard per curve found at the start (see Run), along its outline moved out
            // to the area-weighted depth of what faces out from it, holding its strip triangles.
            private void AddCurvedBillboards(FlattenResult result)
            {
                foreach (var fold in _curves)
                {
                    // Near its ends the curve's pieces are on the planes it meets as well (see
                    // NearCurveEnd); drawn on both, they overlap rather than part.
                    var tris = _curveTris[fold].Where(t => result.Assignment[t] == FlattenResult.Kept || SharedWithPlanes(fold, t)).ToList();
                    if (tris.Count == 0) continue;
                    // Drawn where what faces out from it mostly is.
                    double wsum = 0, dsum = 0;
                    foreach (int t in tris)
                    {
                        var c = Centroid(t);
                        float w = _in.Areas[t] * Vector3.Dot(_in.Normals[t], fold.Frame.Outward(fold.Frame.Project(c).X));
                        if (w <= 0) continue;
                        wsum += w;
                        dsum += w * fold.Frame.Depth(c);
                    }
                    var frame = fold.Frame.Moved(wsum > 0 ? (float)(dsum / wsum) : 0f);

                    var min = new Vector2(float.MaxValue);
                    var max = new Vector2(float.MinValue);
                    foreach (int t in tris)
                        for (int k = 0; k < 3; k++)
                        {
                            var q = frame.Project(_in.Corners[t * 3 + k]);
                            min = Vector2.Min(min, q);
                            max = Vector2.Max(max, q);
                        }
                    var mid = frame.Outward((min.X + max.X) / 2);
                    var bb = new Billboard
                    {
                        Normal = mid, Offset = 0f, AxisU = Vector3.Cross(Vector3.UnitY, mid), AxisV = Vector3.UnitY,
                        Triangles = tris, PlaneIndex = -1, Curve = frame, Coverage = 1f,
                    };
                    bb.Min = bb.ContentMin = min;
                    bb.Max = bb.ContentMax = max;
                    int index = result.Billboards.Count;
                    result.Billboards.Add(bb);
                    foreach (int t in tris)
                    {
                        if (result.Assignment[t] != FlattenResult.Kept) continue;
                        result.Assignment[t] = index;
                        result.KeptTriangles--;
                    }
                }
            }

            private void GrowForSweep(FlattenResult result)
            {
                float tan = BillboardBaker.MaxSweepTangent;
                foreach (var bb in result.Billboards)
                {
                    if (!BillboardBaker.Sweeps(bb)) continue;
                    float top = bb.Max.Y;
                    foreach (int t in bb.Triangles)
                        for (int k = 0; k < 3; k++)
                        {
                            var p = _in.Corners[t * 3 + k];
                            float d = bb.Curve != null ? bb.Curve.Depth(p) - bb.Offset : Vector3.Dot(p, bb.Normal) - bb.Offset;
                            float up = bb.Curve != null ? p.Y : Vector3.Dot(p, bb.AxisV);
                            top = MathF.Max(top, up + MathF.Max(d, 0f) * tan);
                        }
                    float tiny = _in.Extent * 1e-4f;
                    if (top <= bb.Max.Y + tiny) continue;
                    bb.WeldedMin = bb.Min;
                    bb.WeldedMax = bb.Max;
                    bb.Max.Y = MathF.Max(bb.Max.Y, top);
                }
            }

            // A sloped surface (an awning's underside) already drawn on a billboard tilted the
            // way it faces. A layered backdrop would show it from every side below, flat, where
            // the real one faces away and shows its other side: stripes across the wall above
            // the awning. Only surfaces squarely facing the backdrop stay on it.
            private bool OnOwnTiltedBillboard(FlattenResult result, int t, Vector3 n)
            {
                if (MathF.Abs(n.Z) > 0.9f || Vector3.Dot(_in.Normals[t], n) >= 0.9f) return false;
                int b = result.Assignment[t];
                return b >= 0 && Vector3.Dot(result.Billboards[b].Normal, _in.Normals[t]) > 0.95f;
            }

            private Billboard MakeBackdrop(Vector3 n, Vector3 u, Vector3 v, float offset, List<int> tris, bool keepInside = false)
            {
                var bb = new Billboard { Normal = n, Offset = offset, AxisU = u, AxisV = v, Triangles = tris, PlaneIndex = -1, Backdrop = true, DensityScale = 0.35f };
                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                foreach (int t in tris)
                    for (int k = 0; k < 3; k++)
                    {
                        var q = Project(_in.Corners[t * 3 + k], u, v);
                        min = Vector2.Min(min, q);
                        max = Vector2.Max(max, q);
                    }
                bb.Min = bb.ContentMin = min;
                bb.Max = bb.ContentMax = max;
                bb.Coverage = CoverageGrid(bb).Fraction;
                bb.Clip = CrossSection(n, u, v, offset);
                if (keepInside) bb.Inside = SkinMask(n, u, v, offset, bb.Min, bb.Max);
                return bb;
            }

            // Where a deep front or back backdrop stays inside the building: for each cell across
            // it, the building's outer skin on the far side (the way it faces away from) has to be
            // at least as far out as the backdrop is. The deep one facing back sits by the rearmost
            // faces turned that way, and some of those are near the front - it stood out in front of a storefront
            // recessed under the floors above, where it faced away from the street and so never
            // showed, but the game's shadow pass draws both sides and it threw shadows across the
            // shop fronts. Columns with nothing of the building in them are outside too. Only the
            // back one (the models face +Z): the front one has to reach up behind a front cornice
            // standing above a lower roof, where there's nothing of the building behind it. Not for
            // the layered ones: each is just behind its own patch already, and the gaps they fill
            // (between a cornice's brackets, with no roof deck above the soffit - the models are
            // shells) have nothing further out, so the test cut them away and the sky showed.
            private CellMask SkinMask(Vector3 n, Vector3 u, Vector3 v, float offset, Vector2 min, Vector2 max)
            {
                var skin = SkinFacingAway(n, u, v);
                float margin = _in.Extent * 0.005f, depth = -offset;
                int c0 = Math.Max(0, (int)MathF.Floor((min.X - skin.U0) / skin.Step) - 1);
                int r0 = Math.Max(0, (int)MathF.Floor((min.Y - skin.V0) / skin.Step) - 1);
                int c1 = Math.Min(skin.Cols - 1, (int)MathF.Floor((max.X - skin.U0) / skin.Step) + 1);
                int r1 = Math.Min(skin.Rows - 1, (int)MathF.Floor((max.Y - skin.V0) / skin.Step) + 1);
                var mask = new CellMask
                {
                    U0 = skin.U0 + c0 * skin.Step, V0 = skin.V0 + r0 * skin.Step, Step = skin.Step,
                    Cols = Math.Max(0, c1 - c0 + 1), Rows = Math.Max(0, r1 - r0 + 1),
                };
                mask.Cells = new bool[mask.Cols * mask.Rows];
                for (int r = 0; r < mask.Rows; r++)
                    for (int c = 0; c < mask.Cols; c++)
                        mask.Cells[r * mask.Cols + c] = skin.Depth[(r + r0) * skin.Cols + c + c0] >= depth - margin;
                return mask;
            }

            // The building's outer skin seen from the side n faces away from: per cell across the
            // plane (u, v), the furthest any of it reaches that way (-n), over the cell and its
            // neighbours (sampling can miss a cell a thin piece only grazes); float.MinValue where
            // there is none of it. Per direction, over the whole model, so the backdrops facing
            // one way share it.
            private readonly Dictionary<Vector3, (float U0, float V0, float Step, int Cols, int Rows, float[] Depth)> _skins = new();

            private (float U0, float V0, float Step, int Cols, int Rows, float[] Depth) SkinFacingAway(Vector3 n, Vector3 u, Vector3 v)
            {
                if (_skins.TryGetValue(n, out var cached)) return cached;
                float step = _in.Extent * 0.005f;
                var lo = new Vector2(float.MaxValue);
                var hi = new Vector2(float.MinValue);
                foreach (var p in _in.Corners)
                {
                    var q = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
                    lo = Vector2.Min(lo, q);
                    hi = Vector2.Max(hi, q);
                }
                float u0 = lo.X - step, v0 = lo.Y - step;
                int cols = (int)MathF.Ceiling((hi.X - lo.X) / step) + 3, rows = (int)MathF.Ceiling((hi.Y - lo.Y) / step) + 3;
                var raw = new float[cols * rows];
                Array.Fill(raw, float.MinValue);
                float spacing = step * 0.5f;
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_in.Areas[t] <= _degenerateArea) continue;
                    Vector3 a = _in.Corners[t * 3], b = _in.Corners[t * 3 + 1], c = _in.Corners[t * 3 + 2];
                    // A grid of barycentric samples fine enough to land in every cell it crosses.
                    int k = Math.Clamp((int)MathF.Ceiling(MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a))) / spacing), 1, 400);
                    for (int i = 0; i <= k; i++)
                        for (int j = 0; i + j <= k; j++)
                        {
                            var p = a + (b - a) * ((float)i / k) + (c - a) * ((float)j / k);
                            int cc = (int)MathF.Floor((Vector3.Dot(p, u) - u0) / step), rr = (int)MathF.Floor((Vector3.Dot(p, v) - v0) / step);
                            if (cc < 0 || rr < 0 || cc >= cols || rr >= rows) continue;
                            raw[rr * cols + cc] = MathF.Max(raw[rr * cols + cc], -Vector3.Dot(p, n));
                        }
                }
                var depthMap = new float[cols * rows];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                    {
                        float best = float.MinValue;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dc = -1; dc <= 1; dc++)
                            {
                                int rr = r + dr, cc = c + dc;
                                if (rr >= 0 && cc >= 0 && rr < rows && cc < cols) best = MathF.Max(best, raw[rr * cols + cc]);
                            }
                        depthMap[r * cols + c] = best;
                    }
                return _skins[n] = (u0, v0, step, cols, rows, depthMap);
            }

            // The model's cross-section by the plane dot(p, n) = offset, as the span it covers
            // across each thin row up the plane. What's drawn onto a backdrop keeps the outline
            // of the front it came from, and where the building is narrower at the backdrop's
            // depth (a rounded corner tower curving away between its wider cornice and base) the
            // backdrop would stick out past the walls. Rows, not a convex outline: the tower's
            // cornice and base would hold a hull out beyond its middle storeys.
            private ClipRows? CrossSection(Vector3 n, Vector3 u, Vector3 v, float offset)
            {
                var segments = new List<(Vector2 A, Vector2 B)>();
                Span<Vector2> hit = stackalloc Vector2[3];
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_in.Areas[t] <= _degenerateArea) continue;
                    int count = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        var a = _in.Corners[t * 3 + k];
                        var b = _in.Corners[t * 3 + (k + 1) % 3];
                        float da = Vector3.Dot(a, n) - offset, db = Vector3.Dot(b, n) - offset;
                        if ((da < 0) == (db < 0)) continue;
                        hit[count++] = Project(Vector3.Lerp(a, b, da / (da - db)), u, v);
                    }
                    if (count >= 2) segments.Add((hit[0], hit[1]));
                }
                if (segments.Count == 0) return null;

                float step = _in.Extent * 0.005f;
                float v0 = segments.Min(sg => MathF.Min(sg.A.Y, sg.B.Y));
                float v1 = segments.Max(sg => MathF.Max(sg.A.Y, sg.B.Y));
                int rows = Math.Max(1, (int)MathF.Ceiling((v1 - v0) / step));
                var lo = new float[rows];
                var hi = new float[rows];
                Array.Fill(lo, float.MaxValue);
                Array.Fill(hi, float.MinValue);
                foreach (var (a, b) in segments)
                {
                    int r0 = Math.Clamp((int)((MathF.Min(a.Y, b.Y) - v0) / step), 0, rows - 1);
                    int r1 = Math.Clamp((int)((MathF.Max(a.Y, b.Y) - v0) / step), 0, rows - 1);
                    for (int r = r0; r <= r1; r++)
                    {
                        // The part of the segment inside this row.
                        float ua = a.X, ub = b.X;
                        if (MathF.Abs(b.Y - a.Y) > 1e-9f)
                        {
                            float ta = Math.Clamp((v0 + r * step - a.Y) / (b.Y - a.Y), 0f, 1f);
                            float tb = Math.Clamp((v0 + (r + 1) * step - a.Y) / (b.Y - a.Y), 0f, 1f);
                            ua = a.X + (b.X - a.X) * ta;
                            ub = a.X + (b.X - a.X) * tb;
                        }
                        lo[r] = MathF.Min(lo[r], MathF.Min(ua, ub));
                        hi[r] = MathF.Max(hi[r], MathF.Max(ua, ub));
                    }
                }
                // A row's neighbours count too, and a little to either side: the cut shouldn't
                // bite into what overhangs the section by a hair (a cornice's return).
                var clip = new ClipRows { V0 = v0, Step = step, Lo = new float[rows], Hi = new float[rows], Margin = _in.Extent * 0.005f };
                for (int r = 0; r < rows; r++)
                {
                    clip.Lo[r] = float.MaxValue;
                    clip.Hi[r] = float.MinValue;
                    for (int d = Math.Max(0, r - 1); d <= Math.Min(rows - 1, r + 1); d++)
                    {
                        clip.Lo[r] = MathF.Min(clip.Lo[r], lo[d]);
                        clip.Hi[r] = MathF.Max(clip.Hi[r], hi[d]);
                    }
                }
                return clip;
            }

            // Connected components of the welded mesh among `tris`.
            private List<List<int>> Components(List<int> tris)
            {
                var uf = new UnionFind(tris.Count);
                var firstByWeld = new Dictionary<int, int>();
                for (int i = 0; i < tris.Count; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        int w = _in.WeldIds[tris[i] * 3 + c];
                        if (firstByWeld.TryGetValue(w, out int j)) uf.Union(i, j);
                        else firstByWeld[w] = i;
                    }
                var byRoot = new Dictionary<int, List<int>>();
                for (int i = 0; i < tris.Count; i++)
                {
                    int r = uf.Find(i);
                    if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new List<int>();
                    list.Add(tris[i]);
                }
                return byRoot.Values.ToList();
            }

            // What a pass released but no new plane claimed goes to whichever plane it fits that
            // faces it best - a side wall's chamfered corner fits the side wall's billboard as
            // well as the back facade it came from, and faces the side far more. Its original
            // plane is always one of the options, so nothing ends up facing worse than before
            // (and a high Strength still flattens everything, as it should).
            private void Rehome(List<(int T, int Plane)> released)
            {
                foreach (var (t, original) in released)
                {
                    if (_plane[t] != Unassigned) continue;
                    int best = original;
                    float bestDot = Vector3.Dot(_in.Normals[t], _planes[original].N);
                    for (int p = 0; p < _planes.Count; p++)
                    {
                        float d = Vector3.Dot(_in.Normals[t], _planes[p].N);
                        if (d > bestDot && FitsPlane(t, p) && InRegion(_planeRegion[p], t)) { best = p; bestDot = d; }
                    }
                    _plane[t] = best;
                }
            }

            // Stair detection: for the tops of treads (and their undersides), the small horizontal
            // patches whose centres lie on one sloped line are a flight. The flight's plane holds
            // that line and the horizontal direction across it (the treads' width); it faces the
            // same side as the treads. Platforms and landings are too big to count as treads.
            private List<(Vector3 N, float Rho, (Vector3 Min, Vector3 Max) Region)> DetectStairPlanes(List<int> pool)
            {
                var found = new List<(Vector3 N, float Rho, (Vector3 Min, Vector3 Max) Region, int Treads, bool Up)>();
                float margin = _in.Extent * 0.03f;
                float maxTreadArea = _in.Extent * _in.Extent * 0.002f;
                float lineTol = MathF.Max(_eps, _in.Extent * 0.003f);
                float reach = _in.Extent * 0.2f;
                float minSlope = MathF.Sin(20f * MathF.PI / 180f), maxSlope = MathF.Sin(80f * MathF.PI / 180f);

                foreach (var up in new[] { Vector3.UnitY, -Vector3.UnitY })
                {
                    var all = HorizontalPatches(pool, up);
                    foreach (var landing in all.Where(p => p.Area > maxTreadArea)) _landings.UnionWith(landing.Tris);
                    var patches = all.Where(p => p.Area <= maxTreadArea).ToList();
                    var used = new bool[patches.Count];
                    while (true)
                    {
                        _ct.ThrowIfCancellationRequested();
                        // Best line through two unused patches: the one most other unused patches sit on.
                        List<int>? best = null;
                        for (int i = 0; i < patches.Count; i++)
                        {
                            if (used[i]) continue;
                            for (int j = i + 1; j < patches.Count; j++)
                            {
                                if (used[j]) continue;
                                var d = patches[j].Centre - patches[i].Centre;
                                float len = d.Length();
                                if (len < 1e-6f || len > reach) continue;
                                var dir = d / len;
                                float slope = MathF.Abs(dir.Y);
                                if (slope < minSlope || slope > maxSlope) continue;

                                var inliers = new List<int>();
                                for (int k = 0; k < patches.Count; k++)
                                {
                                    if (used[k]) continue;
                                    var v = patches[k].Centre - patches[i].Centre;
                                    float along = Vector3.Dot(v, dir);
                                    if (MathF.Abs(along) > reach) continue;
                                    if ((v - dir * along).Length() <= lineTol) inliers.Add(k);
                                }
                                if (inliers.Count >= 4 && (best == null || inliers.Count > best.Count)) best = inliers;
                            }
                        }
                        if (best == null) break;
                        foreach (int k in best) used[k] = true;

                        // Refit the line's direction through the inliers' extremes along it.
                        best = best.OrderBy(k => patches[k].Centre.Y).ToList();
                        var pts = best.Select(k => patches[k].Centre).ToList();

                        // Treads come close together and evenly: a step's rise apart, a few
                        // missing at most. Window sills and heads that happen to line up across
                        // storeys are a whole storey apart.
                        // The flight is the longest run of treads spaced evenly (no gap over three
                        // times the typical step); a stray patch out at a landing isn't part of it.
                        var gaps = pts.Zip(pts.Skip(1), (a, b) => (b - a).Length()).ToList();
                        float medianStep = gaps.OrderBy(d => d).ElementAt(gaps.Count / 2);
                        int runStart = 0, runLength = 1;
                        for (int s = 0, start = 0; s <= gaps.Count; s++)
                            if (s == gaps.Count || gaps[s] > 3f * medianStep)
                            {
                                if (s + 1 - start > runLength) { runStart = start; runLength = s + 1 - start; }
                                start = s + 1;
                            }
                        pts = pts.GetRange(runStart, runLength);
                        best = best.GetRange(runStart, runLength);
                        if (pts.Count < 6 || medianStep > _in.Extent * 0.04f) continue;

                        var line = Vector3.Normalize(pts[^1] - pts[0]);
                        var horizontal = new Vector3(line.X, 0, line.Z);
                        if (horizontal.LengthSquared() < 1e-8f) continue;
                        // Flights run along the building's axes; noise in where each tread's
                        // centre sits otherwise leans the plane sideways a few degrees, and the
                        // top and underside of one flight stop agreeing.
                        // A line that doesn't run along one is bars' tops or bolt heads that
                        // happen to line up, not a flight.
                        horizontal = Vector3.Normalize(horizontal);
                        bool snapped = false;
                        foreach (var axis in new[] { _in.FrameX, -_in.FrameX, _in.FrameZ, -_in.FrameZ })
                            if (Vector3.Dot(horizontal, axis) > MathF.Cos(15f * MathF.PI / 180f))
                            {
                                float rise = line.Y / new Vector2(line.X, line.Z).Length();
                                line = Vector3.Normalize(axis + Vector3.UnitY * rise);
                                horizontal = axis;
                                snapped = true;
                                break;
                            }
                        if (!snapped) continue;
                        var across = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, horizontal));
                        // A tread is wide across the flight and shallow along it; a cornice's
                        // dentils or a row of bolt heads are small squares.
                        int wide = best.Count(k =>
                        {
                            var ts = patches[k].Tris;
                            float Extent(Vector3 d) =>
                                ts.SelectMany(t => new[] { _in.Corners[t * 3], _in.Corners[t * 3 + 1], _in.Corners[t * 3 + 2] }).Max(q => Vector3.Dot(q, d))
                                - ts.SelectMany(t => new[] { _in.Corners[t * 3], _in.Corners[t * 3 + 1], _in.Corners[t * 3 + 2] }).Min(q => Vector3.Dot(q, d));
                            return Extent(across) >= 2f * Extent(horizontal);
                        });
                        if (wide < 0.6f * best.Count) continue;
                        var n = Vector3.Normalize(Vector3.Cross(line, across));
                        if (Vector3.Dot(n, up) < 0) n = -n;
                        // The box reaches a little past the flight's ends and top and bottom, but
                        // across it only as wide as its treads: a flight usually runs alongside a
                        // balcony rail, a hand's width away.
                        var treadCorners = best.SelectMany(k => patches[k].Tris).SelectMany(t => new[] { _in.Corners[t * 3], _in.Corners[t * 3 + 1], _in.Corners[t * 3 + 2] }).ToList();
                        var lo = pts.Aggregate(Vector3.Min) - new Vector3(margin);
                        var hi = pts.Aggregate(Vector3.Max) + new Vector3(margin);
                        foreach (var ax in new[] { 0, 2 })
                        {
                            float w = ax == 0 ? MathF.Abs(across.X) : MathF.Abs(across.Z);
                            if (w < 0.9f) continue;
                            float tlo = treadCorners.Min(c => ax == 0 ? c.X : c.Z) - _in.Extent * 0.005f;
                            float thi = treadCorners.Max(c => ax == 0 ? c.X : c.Z) + _in.Extent * 0.005f;
                            if (ax == 0) { lo.X = tlo; hi.X = thi; } else { lo.Z = tlo; hi.Z = thi; }
                        }
                        float rho = pts.Average(q => Vector3.Dot(q, n));
                        found.Add((n, rho, (lo, hi), best.Count, up.Y > 0));   // one per flight: flights share normals but not boxes
                    }
                }

                // Real flights have many treads; the lines left once they're taken are chance
                // alignments of a few small patches. And a flight's undersides describe the same
                // flight as its tops (StructurePlanes gives every flight a plane facing back).
                if (found.Count == 0) return new();
                int most = found.Max(f => f.Treads);
                // Same flight: each one's box holds the middle of the other's (flights stacked
                // floor above floor overlap a little at the landings, not that much).
                static bool Holds((Vector3 Min, Vector3 Max) a, Vector3 c) =>
                    c.X >= a.Min.X && c.Y >= a.Min.Y && c.Z >= a.Min.Z && c.X <= a.Max.X && c.Y <= a.Max.Y && c.Z <= a.Max.Z;
                static bool Overlap((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
                    Holds(a, (b.Min + b.Max) / 2) && Holds(b, (a.Min + a.Max) / 2);
                var result = new List<(Vector3 N, float Rho, (Vector3 Min, Vector3 Max) Region)>();
                foreach (var f in found.Where(f => f.Treads * 2 >= most).OrderByDescending(f => f.Up))
                {
                    if (result.Any(r => MathF.Abs(Vector3.Dot(r.N, f.N)) > MathF.Cos(10f * MathF.PI / 180f) && Overlap(r.Region, f.Region))) continue;
                    result.Add(f.Up ? (f.N, f.Rho, f.Region) : (-f.N, -f.Rho, f.Region));
                }
                return result;
            }

            // Connected patches of the pool's triangles facing along `up` (within ~25 degrees).
            private List<(Vector3 Centre, float Area, List<int> Tris)> HorizontalPatches(List<int> pool, Vector3 up)
            {
                var facing = pool.Where(t => Vector3.Dot(_in.Normals[t], up) > 0.9f && _in.Areas[t] > _degenerateArea).ToList();
                var uf = new UnionFind(facing.Count);
                var firstByWeld = new Dictionary<int, int>();
                for (int i = 0; i < facing.Count; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _in.WeldIds[facing[i] * 3 + k];
                        if (firstByWeld.TryGetValue(w, out int j)) uf.Union(i, j);
                        else firstByWeld[w] = i;
                    }

                var sums = new Dictionary<int, (Vector3 Weighted, float Area, List<int> Tris)>();
                for (int i = 0; i < facing.Count; i++)
                {
                    int t = facing[i];
                    var c = (_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f;
                    int r = uf.Find(i);
                    if (!sums.TryGetValue(r, out var acc)) acc = (Vector3.Zero, 0f, new List<int>());
                    acc.Tris.Add(t);
                    sums[r] = (acc.Weighted + c * _in.Areas[t], acc.Area + _in.Areas[t], acc.Tris);
                }
                return sums.Values.Select(v => (v.Weighted / v.Area, v.Area, v.Tris)).ToList();
            }

            private bool InRegion((Vector3 Min, Vector3 Max)? region, int t)
            {
                if (region == null) return true;
                var c = (_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f;
                var (lo, hi) = region.Value;
                return c.X >= lo.X && c.Y >= lo.Y && c.Z >= lo.Z && c.X <= hi.X && c.Y <= hi.Y && c.Z <= hi.Z;
            }

            private bool FitsPlane(int t, int p)
            {
                // The finer of the plane's own tolerance and the current pass's: a fine plane never
                // reaches further than it was found with, and a fine pass never parks a piece on a
                // coarse plane further away than the fine tolerance either.
                float current = _eps;
                _eps = MathF.Min(_planeEps[p], current);
                try { return Fits(t, _planes[p].N, _planes[p].Rho); }
                finally { _eps = current; }
            }

            private bool Fits(int t, Vector3 n, float rho)
            {
                int i = t * 3;
                return MathF.Abs(Vector3.Dot(_in.Corners[i], n) - rho) <= _eps
                    && MathF.Abs(Vector3.Dot(_in.Corners[i + 1], n) - rho) <= _eps
                    && MathF.Abs(Vector3.Dot(_in.Corners[i + 2], n) - rho) <= _eps;
            }

            // Lazy greedy: a candidate's score can only fall as triangles are claimed, so a stale
            // score is an upper bound - re-evaluate the top one and take it if it still beats
            // the next best's (possibly stale, so optimistic) score.
            private bool RunRound(List<Vector3> candidates)
            {
                // A triangle only counts toward the candidates nearest its own normal. Without
                // this, a candidate tilted 20-odd degrees off the facade still "aligns" with every
                // leftover facade triangle, and its plane - slicing diagonally through the
                // building's depth - collects a strip from each depth layer it crosses.
                var aligned = new List<int>[candidates.Count];
                for (int c = 0; c < candidates.Count; c++) aligned[c] = new List<int>();
                var angles = new float[candidates.Count];
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_plane[t] != Unassigned) continue;
                    float best = float.MaxValue;
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        angles[c] = MathF.Acos(Math.Clamp(Vector3.Dot(_in.Normals[t], candidates[c]), -1f, 1f));
                        best = MathF.Min(best, angles[c]);
                    }
                    for (int c = 0; c < candidates.Count; c++)
                    {
                        if (angles[c] < _alignAngle && angles[c] <= best + _alignSlack) aligned[c].Add(t);
                    }
                }

                var queue = new PriorityQueue<int, float>();
                for (int c = 0; c < candidates.Count; c++)
                {
                    var (score, _) = WithEps(YawedEps(candidates[c]), () => Evaluate(candidates[c], aligned[c]));
                    if (score >= _minArea) queue.Enqueue(c, -score);
                }

                bool accepted = false;
                while (queue.TryDequeue(out int cand, out _))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (_planes.Count >= MaxPlanes) break;

                    float eps = YawedEps(candidates[cand]);
                    var (score, rho) = WithEps(eps, () => Evaluate(candidates[cand], aligned[cand]));
                    if (score < _minArea) continue;
                    if (queue.TryPeek(out _, out float nextNeg) && score < -nextNeg)
                    {
                        queue.Enqueue(cand, -score);
                        continue;
                    }

                    WithEps(eps, () => { AcceptPlane(candidates[cand], rho, aligned[cand], _minArea); return 0; });
                    accepted = true;
                    // The same normal may hold another plane at a different depth (a recessed
                    // pane behind the facade) - its score is recomputed when it next surfaces.
                    queue.Enqueue(cand, -score);
                }
                return accepted;
            }

            // An upright plane turned off the building's axes (a canted bay's side) is held to a
            // finer tolerance. Seen from the front, a piece drawn on it is out of place sideways
            // by up to its tolerance - on a wall facing the viewer the same error is only depth.
            // At the full tolerance such a plane gathered every 45-degree scrap up the facade
            // (a downspout's facets, the sides of brackets and window hoods) and drew them as a
            // loose pole and patches beside where they belong, leaving gaps where they were.
            // Not much finer, though: a round corner tower's faces beyond its curved strip then
            // fall to the axis planes and the sideways backdrops, and widen its outline.
            private float YawedEps(Vector3 n)
                => MathF.Abs(n.Y) < 0.3f && !_frame.Any(f => Vector3.Dot(f, n) > 0.9999f) ? _eps * YawedToleranceScale : _eps;

            private T WithEps<T>(float eps, Func<T> body)
            {
                float current = _eps;
                _eps = eps;
                try { return body(); }
                finally { _eps = current; }
            }

            // Best offset for normal n: each fitting triangle allows a range of offsets
            // [max depth - eps, min depth + eps], weighted by its projected area; the answer is
            // the offset covered by the most weight. Also drops claimed triangles from the list.
            private (float Score, float Rho) Evaluate(Vector3 n, List<int> list)
            {
                if (_evKeys.Length < list.Count * 2)
                {
                    _evKeys = new float[list.Count * 2];
                    _evWeights = new float[list.Count * 2];
                }

                // Ends sort just after starts at the same offset, so touching ranges overlap.
                float tie = _eps * 1e-3f;
                int count = 0, write = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    int t = list[i];
                    if (_plane[t] != Unassigned) continue;
                    list[write++] = t;
                    if (_absorbable[t]) continue;

                    float w = _in.Areas[t] * Vector3.Dot(_in.Normals[t], n);
                    if (w <= 0) continue;

                    int k = t * 3;
                    float d0 = Vector3.Dot(_in.Corners[k], n), d1 = Vector3.Dot(_in.Corners[k + 1], n), d2 = Vector3.Dot(_in.Corners[k + 2], n);
                    float lo = MathF.Max(d0, MathF.Max(d1, d2)) - _eps;
                    float hi = MathF.Min(d0, MathF.Min(d1, d2)) + _eps;
                    if (lo > hi) continue;

                    _evKeys[count] = lo; _evWeights[count++] = w;
                    _evKeys[count] = hi + tie; _evWeights[count++] = -w;
                }
                list.RemoveRange(write, list.Count - write);
                if (count == 0) return (0, 0);

                Array.Sort(_evKeys, _evWeights, 0, count);
                float sum = 0, best = 0, bestLo = 0, bestHi = 0;
                for (int i = 0; i < count; i++)
                {
                    sum += _evWeights[i];
                    if (sum > best)
                    {
                        best = sum;
                        bestLo = _evKeys[i];
                        bestHi = i + 1 < count ? _evKeys[i + 1] : _evKeys[i];
                    }
                }
                return (best, (bestLo + bestHi) * 0.5f);
            }

            // Adds a plane and everything tracked per plane; returns its index.
            private int RegisterPlane(Vector3 n, float claimRho, float drawnAt, float minArea, (Vector3 Min, Vector3 Max)? region)
            {
                _planes.Add((n, claimRho));
                _planeDrawnAt.Add(drawnAt);
                _planeEps.Add(_eps);
                _planeMinArea.Add(minArea);
                _planeRegion.Add(region);
                _planeIsSide.Add(_inSidePass);
                _planeIsDetail.Add(_inDetailPass);
                return _planes.Count - 1;
            }

            private void AcceptPlane(Vector3 n, float rho, List<int> aligned, float minArea)
            {
                // The sweep's offset is the middle of the range of depths that holds the most
                // surface; with a wide tolerance that range also takes in things beside the
                // main surface (a balcony floor plus the rail bars above it) and its middle
                // drifts off the floor. The billboard is placed at the projected-area-weighted
                // mean depth of what the plane scores instead - but it still claims the band the
                // sweep found, so nothing at the band's edge is orphaned by the move (those can
                // sit up to 2x epsilon from the billboard, in exchange for the billboard sitting
                // on the surface that dominates it).
                double weightSum = 0, depthSum = 0;
                foreach (int t in aligned)
                {
                    if (_plane[t] != Unassigned || _absorbable[t] || !Fits(t, n, rho)) continue;
                    float w = _in.Areas[t] * Vector3.Dot(_in.Normals[t], n);
                    if (w <= 0) continue;
                    float d = (Vector3.Dot(_in.Corners[t * 3], n) + Vector3.Dot(_in.Corners[t * 3 + 1], n) + Vector3.Dot(_in.Corners[t * 3 + 2], n)) / 3f;
                    weightSum += w;
                    depthSum += w * d;
                }
                float claimRho = rho;
                if (weightSum > 0) rho = (float)(depthSum / weightSum);

                // Every fit test from here on uses the band (claimRho); only the billboard itself
                // is drawn at the settled depth.
                int p = RegisterPlane(n, claimRho, rho, minArea, null);

                foreach (int t in aligned)
                    if (_plane[t] == Unassigned && Fits(t, n, claimRho)) _plane[t] = p;

                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_plane[t] != Unassigned || _absorbable[t]) continue;
                    if (Vector3.Dot(_in.Normals[t], n) > AbsorbDot && Fits(t, n, claimRho)) _absorbable[t] = true;
                }
            }

            private void AbsorbRemaining()
            {
                for (int t = 0; t < _in.TriangleCount; t++)
                {
                    if (_plane[t] != Unassigned) continue;
                    int best = -1;
                    float bestDot = AbsorbDot;
                    for (int p = 0; p < _planes.Count; p++)
                    {
                        float d = Vector3.Dot(_in.Normals[t], _planes[p].N);
                        if (d > bestDot && FitsPlane(t, p) && InRegion(_planeRegion[p], t)) { best = p; bestDot = d; }
                    }
                    if (best >= 0) _plane[t] = best;
                }
            }

            // Peaks of the area-weighted normal distribution among what's still unclaimed,
            // each refined by a couple of mean-shift steps, snapped to the building frame when
            // close, and de-duplicated.
            private List<Vector3> BuildCandidates(bool includeFrame)
            {
                var live = new List<int>();
                for (int t = 0; t < _in.TriangleCount; t++)
                    if (_plane[t] == Unassigned && !_absorbable[t] && _in.Areas[t] > _degenerateArea) live.Add(t);

                var result = new List<Vector3>();
                if (includeFrame) result.AddRange(_frame);
                if (live.Count == 0) return result;

                var bins = new float[OctBins * OctBins];
                foreach (int t in live) bins[OctBin(_in.Normals[t])] += _in.Areas[t];

                var peaks = new List<(int Bin, float Area)>();
                for (int y = 0; y < OctBins; y++)
                    for (int x = 0; x < OctBins; x++)
                    {
                        float v = bins[y * OctBins + x];
                        if (v <= 0) continue;
                        bool isPeak = true;
                        for (int dy = -1; dy <= 1 && isPeak; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = x + dx, ny = y + dy;
                                if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= OctBins || ny >= OctBins) continue;
                                if (bins[ny * OctBins + nx] > v) { isPeak = false; break; }
                            }
                        if (isPeak) peaks.Add((y * OctBins + x, v));
                    }

                float shiftCos = MathF.Cos(20f * MathF.PI / 180f);
                float snapCos = MathF.Cos(SnapAngleDegrees * MathF.PI / 180f);
                float snapSin = MathF.Sin(SnapAngleDegrees * MathF.PI / 180f);
                float dedupeCos = MathF.Cos(5f * MathF.PI / 180f);

                foreach (var (bin, _) in peaks.OrderByDescending(p => p.Area).Take(MaxPeaks))
                {
                    _ct.ThrowIfCancellationRequested();
                    var dir = OctDecode(new Vector2(
                        ((bin % OctBins) + 0.5f) / OctBins * 2 - 1,
                        ((bin / OctBins) + 0.5f) / OctBins * 2 - 1));

                    for (int iter = 0; iter < 2; iter++)
                    {
                        var sum = Vector3.Zero;
                        foreach (int t in live)
                            if (Vector3.Dot(_in.Normals[t], dir) > shiftCos) sum += _in.Normals[t] * _in.Areas[t];
                        if (sum.LengthSquared() < 1e-24f) break;
                        dir = Vector3.Normalize(sum);
                    }

                    foreach (var f in _frame)
                        if (Vector3.Dot(dir, f) > snapCos) { dir = f; break; }

                    // A tilted plane (a cornice's sloped underside) keeps level across the
                    // facade: a few degrees of stray yaw, averaged in from its ornament, tip one
                    // end of a building-wide billboard up out of the roofline.
                    foreach (var axis in new[] { _in.FrameX, Vector3.UnitY, _in.FrameZ })
                    {
                        float along = Vector3.Dot(dir, axis);
                        if (along != 0 && MathF.Abs(along) < snapSin) dir = Vector3.Normalize(dir - axis * along);
                    }

                    if (result.Any(r => Vector3.Dot(r, dir) > dedupeCos)) continue;
                    result.Add(dir);
                }
                return result;
            }

            // --- result ------------------------------------------------------------------------

            private FlattenResult BuildResult()
            {
                var result = new FlattenResult
                {
                    Assignment = new int[_in.TriangleCount],
                    PlaneCount = _planes.Count,
                    Tolerance = _eps,
                };

                var perPlane = new List<int>[_planes.Count];
                for (int p = 0; p < _planes.Count; p++) perPlane[p] = new List<int>();
                for (int t = 0; t < _in.TriangleCount; t++)
                    if (_plane[t] >= 0) perPlane[_plane[t]].Add(t);

                var leftovers = new List<int>();
                for (int t = 0; t < _in.TriangleCount; t++)
                    if (_plane[t] < 0) leftovers.Add(t);
                var edgeOnByPlane = new List<int>?[_planes.Count];

                for (int p = 0; p < _planes.Count; p++)
                {
                    _ct.ThrowIfCancellationRequested();
                    var n = _planes[p].N;
                    float rho = _planeDrawnAt[p];
                    var (u, v) = PlaneBasis(n, _in.FrameX);

                    // A fire-escape part (see FireEscapeParts): one billboard showing everything
                    // in the part's box, however it faces and whichever part owns it.
                    if (_partTris.TryGetValue(p, out var partTris))
                    {
                        var part = MakeBillboard(n, rho, u, v, p, partTris);
                        part.FillHoles = _floorParts.Contains(p);
                        part.Part = true;
                        result.Billboards.Add(part);
                        continue;
                    }

                    // Only triangles that actually face the billboard decide its shape. Edge-on
                    // ones (groove walls, reveals, the sides of a ledge) render to nothing in a
                    // view down the normal, but a stray sliver of them stretches a rectangle -
                    // and its atlas space - right across the building. They're attached
                    // afterwards to whichever billboard they sit on.
                    var visible = new List<int>();
                    var edgeOn = new List<int>();
                    foreach (int t in perPlane[p])
                        (Vector3.Dot(_in.Normals[t], n) > VisibleDot ? visible : edgeOn).Add(t);

                    var planeBillboards = new List<Billboard>();
                    var split = _planeIsStructure.Contains(p)
                        ? (visible.Count > 0 ? new List<List<int>> { visible } : new List<List<int>>())
                        : SplitSpatially(visible, u, v, n, _planeEps[p]);
                    var groups = split
                        .Select(g => (Tris: g, Projected: g.Sum(t => _in.Areas[t] * Vector3.Dot(_in.Normals[t], n))))
                        .ToList();
                    // Stair, side and detail planes run on small minimums, which lets their real
                    // pieces through but also the scraps around them - a triangle or two at an
                    // odd angle. Pieces much smaller than the plane's main one are strays too;
                    // they stay as real mesh (or are dropped) rather than become a billboard.
                    float strayBelow = _planeMinArea[p] * 0.25f;
                    if (groups.Count > 0 && !_planeIsStructure.Contains(p) && (_planeRegion[p] != null || _planeIsSide[p] || _planeIsDetail[p]))
                        strayBelow = MathF.Max(strayBelow, groups.Max(g => g.Projected) * (_planeRegion[p] != null ? 0.1f : 0.05f));
                    foreach (var (group, projected) in groups)
                    {
                        // Stray bits that happen to sit on a plane far from anything else on it.
                        if (projected < strayBelow) { leftovers.AddRange(group); continue; }

                        // Cutting a sparse billboard can leave slivers of a triangle or two;
                        // those stay as mesh rather than become billboards of their own.
                        var pieces = SplitSparse(MakeBillboard(n, rho, u, v, p, group), 0);
                        float largest = pieces.Max(b => ProjectedArea(b));
                        foreach (var piece in pieces)
                        {
                            if (pieces.Count > 1 && ProjectedArea(piece) < largest * 0.05f) leftovers.AddRange(piece.Triangles);
                            else
                            {
                                piece.FillHoles = _planeIsStructure.Contains(p) && MathF.Abs(n.Y) > 0.9f && piece.Coverage >= 0.5f;
                                planeBillboards.Add(piece);
                            }
                        }
                    }

                    edgeOnByPlane[p] = edgeOn;
                    result.Billboards.AddRange(planeBillboards);
                }

                // Edge-on triangles go to a billboard on their own plane whose rectangle they sit
                // in, failing that to any billboard they'd also fit (within tolerance, not facing
                // away) and sit in; only what's left over after both is a leftover.
                float margin = MathF.Max(_eps, _in.Extent * 0.005f);
                bool Inside(Billboard b, int t)
                {
                    var c = Project((_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2]) / 3f, b.AxisU, b.AxisV);
                    return c.X >= b.Min.X - margin && c.X <= b.Max.X + margin && c.Y >= b.Min.Y - margin && c.Y <= b.Max.Y + margin;
                }
                var additions = new List<(Billboard, int)>();
                for (int p = 0; p < _planes.Count; p++)
                {
                    foreach (int t in edgeOnByPlane[p] ?? new List<int>())
                    {
                        var host = (_planeIsStructure.Contains(p) ? FacingStructure(result.Billboards, t) : null)
                            ?? result.Billboards.FirstOrDefault(b => b.PlaneIndex == p && Inside(b, t))
                            ?? result.Billboards.FirstOrDefault(b => b.PlaneIndex != p
                                && Vector3.Dot(_in.Normals[t], b.Normal) > AbsorbDot && FitsPlane(t, b.PlaneIndex) && Inside(b, t)
                                && InRegion(_planeRegion[b.PlaneIndex], t));
                        if (host != null) additions.Add((host, t)); else leftovers.Add(t);
                    }
                }
                foreach (var (host, t) in additions)
                {
                    host.Triangles.Add(t);
                    // A structure's billboard grows to take in pieces rehomed onto it (a beam's
                    // underside along the edge of a balcony floor), so they're drawn whole.
                    if (!_planeIsStructure.Contains(host.PlaneIndex)) continue;
                    for (int k = 0; k < 3; k++)
                    {
                        var q = Project(_in.Corners[t * 3 + k], host.AxisU, host.AxisV);
                        host.Min = Vector2.Min(host.Min, q);
                        host.Max = Vector2.Max(host.Max, q);
                        host.ContentMin = Vector2.Min(host.ContentMin, q);
                        host.ContentMax = Vector2.Max(host.ContentMax, q);
                    }
                }

                WeldCorners(result.Billboards);
                ExtendRecessed(result.Billboards);

                for (int i = 0; i < result.Billboards.Count; i++)
                    foreach (int t in result.Billboards[i].Triangles) result.Assignment[t] = i;

                foreach (int t in leftovers)
                {
                    bool keep = IsLocked(t) || _s.KeepLeftoversAsMesh && _in.Areas[t] > _degenerateArea;
                    result.Assignment[t] = keep ? FlattenResult.Kept : FlattenResult.Dropped;
                    if (keep) result.KeptTriangles++; else result.DroppedTriangles++;
                }
                return result;
            }

            // A structure only draws what faces its own billboard; the rest of it is edge-on and
            // invisible. Where that rest faces another structure's billboard and lies on it -
            // the undersides of a balcony's edge beams and cross members, which belong to the
            // upright rails and frame, sit right on the floor's underside - it's drawn there
            // instead, or the floor seen from below is full of holes where the beams should be.
            private Billboard? FacingStructure(List<Billboard> billboards, int t)
            {
                Billboard? best = null;
                float bestDot = SideReleaseDot;
                foreach (var b in billboards)
                {
                    if (!_planeIsStructure.Contains(b.PlaneIndex)) continue;
                    float d = Vector3.Dot(_in.Normals[t], b.Normal);
                    if (d <= bestDot) continue;
                    float reach = 2f * _planeEps[b.PlaneIndex];
                    bool fits = true;
                    for (int k = 0; k < 3 && fits; k++)
                    {
                        var c = _in.Corners[t * 3 + k];
                        var q = Project(c, b.AxisU, b.AxisV);
                        fits = MathF.Abs(Vector3.Dot(c, b.Normal) - b.Offset) <= reach
                            && q.X >= b.Min.X - reach && q.X <= b.Max.X + reach && q.Y >= b.Min.Y - reach && q.Y <= b.Max.Y + reach;
                    }
                    if (fits) { best = b; bestDot = d; }
                }
                return best;
            }

            // A door set back in its arch, a window in its reveal: the facade billboard has a hole
            // there and the recessed piece has its own billboard behind it, exactly the hole's
            // size. Straight on that's right, but from below you look past the recessed piece's
            // top edge into the space behind the facade - where the real model has the arch's
            // underside, which faces edge-on to both billboards and was flattened away - and see
            // sky. So a billboard mostly inside a solid billboard's rectangle and parallel to it,
            // some depth d behind, is extended by d on every side (enough up to 45 degrees off
            // straight on), no further than the surface in front reaches; the facade hides the
            // extension everywhere but through the hole. The bake stretches its edge texels into
            // the extension, or, if it's mostly solid itself, fills it opaque (Recessed).
            private void ExtendRecessed(List<Billboard> billboards)
            {
                float minDepth = _in.Extent * 1e-3f, maxDepth = _in.Extent * 0.1f;
                foreach (var b in billboards)
                {
                    // Rails, copings and the like aren't windows set into a wall, and the wall's
                    // rectangle in front of them isn't solid everywhere (a gable's curved top).
                    if (b.Detail) continue;
                    var min = b.Min;
                    var max = b.Max;
                    var behind = new List<CoverageMap> { CoverageMap(b) };
                    foreach (var f in billboards)
                    {
                        if (f == b || f.Side || f.Coverage < DetailCoverage) continue;
                        if (Vector3.Dot(f.Normal, b.Normal) < 0.999f || Vector3.Dot(f.AxisU, b.AxisU) < 0.999f) continue;
                        float d = f.Offset - b.Offset;
                        if (d < minDepth || d > maxDepth) continue;
                        var lo = Vector2.Max(b.Min, f.Min);
                        var hi = Vector2.Min(b.Max, f.Max);
                        if (hi.X <= lo.X || hi.Y <= lo.Y) continue;
                        if ((hi.X - lo.X) * (hi.Y - lo.Y) < 0.8f * (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y)) continue;
                        min = Vector2.Min(min, Vector2.Max(b.Min - new Vector2(d), f.Min));
                        max = Vector2.Max(max, Vector2.Min(b.Max + new Vector2(d), f.Max));
                        behind.Add(CoverageMap(f));
                        b.Recessed = true;
                    }
                    if (b.Recessed) b.RecessedBehind = behind;
                    b.Min = min;
                    b.Max = max;
                }
            }

            // Where two billboards on different planes meet - a facade and the side wall at the
            // building's corner - each one's rectangle ends wherever its own triangles did, and
            // each plane sits up to epsilon from the true surface, so the two edges miss each
            // other by up to epsilon: a sliver of sky down the corner, or an overhang. Extending
            // an edge that falls short onto the line where the two planes intersect closes the
            // gap. Only edges parallel to that line and within a couple of epsilon of it count,
            // only when the two billboards share a stretch of that line, and edges only ever
            // move OUTWARD: an overhang is harmless, but pulling an edge in to a plane that
            // merely passes nearby (the inner face of a corner pilaster) cuts real facade off.
            private void WeldCorners(List<Billboard> billboards)
            {
                float tol = 2f * _eps + _in.Extent * 0.002f;
                for (int i = 0; i < billboards.Count; i++)
                    for (int j = i + 1; j < billboards.Count; j++)
                    {
                        var a = billboards[i];
                        var b = billboards[j];
                        // Stretching a see-through billboard's edge texels across a weld smears
                        // rails into streaks; only solid surfaces are welded (but see SoffitWeld).
                        if (a.Coverage < SoffitWeldCoverage || b.Coverage < SoffitWeldCoverage) continue;
                        // Nor bits of detail (a gable's coping, a finial): stretched up to the
                        // plane of the roof above them, they stick out of the building's outline.
                        if (a.Detail || b.Detail) continue;
                        float c = Vector3.Dot(a.Normal, b.Normal);
                        if (MathF.Abs(c) > 0.7f) continue;
                        bool solid = a.Coverage >= DetailCoverage && b.Coverage >= DetailCoverage;

                        var d = Vector3.Normalize(Vector3.Cross(a.Normal, b.Normal));
                        var onLine = ((a.Offset - b.Offset * c) * a.Normal + (b.Offset - a.Offset * c) * b.Normal) / (1 - c * c);
                        if (SoffitWeld(a, b, d, onLine, tol) || SoffitWeld(b, a, d, onLine, tol)) continue;
                        if (!solid) continue;
                        if (!NearestEdge(a, d, onLine, tol, out int edgeA, out float valueA)) continue;
                        if (!NearestEdge(b, d, onLine, tol, out int edgeB, out float valueB)) continue;

                        var (a0, a1) = ExtentAlong(a, d);
                        var (b0, b1) = ExtentAlong(b, d);
                        float overlap = MathF.Min(a1, b1) - MathF.Max(a0, b0);
                        if (overlap < 0.3f * MathF.Min(a1 - a0, b1 - b0)) continue;

                        ExtendEdge(a, edgeA, valueA);
                        ExtendEdge(b, edgeB, valueB);
                    }
            }

            // A wall whose top stops a little short of the soffit drawn above it (the soffit's
            // billboard sits at the mean height of everything under the cornice, the wall stops
            // where the real soffit is): from below, the slot between them shows sky all along
            // the cornice. The soffit's billboard often runs on past the wall (the whole depth of
            // the building, front and back cornice alike), so this isn't an edge-to-edge weld:
            // only the wall's top edge moves, up to the soffit's plane. What it adds is under the
            // soffit, seen only through the slot it closes. Walls full of windows and soffits
            // broken up by brackets count too (SoffitWeldCoverage).
            private static bool SoffitWeld(Billboard wall, Billboard soffit, Vector3 d, Vector3 onLine, float tol)
            {
                if (soffit.Normal.Y > -0.9f || MathF.Abs(wall.Normal.Y) > 0.1f) return false;
                if (!NearestEdge(wall, d, onLine, tol, out int edge, out float value) || edge != 3 || value <= wall.Max.Y) return false;
                // The line runs under the soffit's own rectangle, not off past its edge.
                float across = Vector3.Dot(onLine, soffit.AxisU), along = Vector3.Dot(onLine, soffit.AxisV);
                bool inside = MathF.Abs(Vector3.Dot(d, soffit.AxisU)) < 0.05f
                    ? across >= soffit.Min.X && across <= soffit.Max.X
                    : along >= soffit.Min.Y && along <= soffit.Max.Y;
                if (!inside) return false;
                var (w0, w1) = ExtentAlong(wall, d);
                var (s0, s1) = ExtentAlong(soffit, d);
                if (MathF.Min(w1, s1) - MathF.Max(w0, s0) < 0.3f * (w1 - w0)) return false;
                ExtendEdge(wall, edge, value);
                return true;
            }

            // Edges: 0 = Min.X, 1 = Max.X, 2 = Min.Y, 3 = Max.Y. The line must run along one of
            // the billboard's axes; the edge across from it closest to the line, within tol, wins.
            private static bool NearestEdge(Billboard bb, Vector3 lineDir, Vector3 onLine, float tol, out int edge, out float value)
            {
                edge = -1;
                value = 0;
                const float parallel = 0.05f;
                int lo;
                float at;
                if (MathF.Abs(Vector3.Dot(lineDir, bb.AxisU)) < parallel) { lo = 0; at = Vector3.Dot(onLine, bb.AxisU); }
                else if (MathF.Abs(Vector3.Dot(lineDir, bb.AxisV)) < parallel) { lo = 2; at = Vector3.Dot(onLine, bb.AxisV); }
                else return false;

                float min = lo == 0 ? bb.Min.X : bb.Min.Y, max = lo == 0 ? bb.Max.X : bb.Max.Y;
                float dMin = MathF.Abs(at - min), dMax = MathF.Abs(at - max);
                if (MathF.Min(dMin, dMax) > tol) return false;
                edge = dMin <= dMax ? lo : lo + 1;
                value = at;
                return true;
            }

            private static void ExtendEdge(Billboard bb, int edge, float value)
            {
                switch (edge)
                {
                    case 0: bb.Min.X = MathF.Min(bb.Min.X, value); break;
                    case 1: bb.Max.X = MathF.Max(bb.Max.X, value); break;
                    case 2: bb.Min.Y = MathF.Min(bb.Min.Y, value); break;
                    default: bb.Max.Y = MathF.Max(bb.Max.Y, value); break;
                }
            }

            private static (float Lo, float Hi) ExtentAlong(Billboard bb, Vector3 dir)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    float x = Vector3.Dot(bb.Corner(k), dir);
                    lo = MathF.Min(lo, x);
                    hi = MathF.Max(hi, x);
                }
                return (lo, hi);
            }

            // Connected components on the welded mesh, then components merged while their 2D
            // footprints overlap or sit within the gap of each other AND their depth ranges along
            // the normal do too - a rail and the stair a few centimetres behind it overlap
            // perfectly in the plane's 2D view, and are still two separate things.
            private List<List<int>> SplitSpatially(List<int> tris, Vector3 u, Vector3 v, Vector3 n, float eps)
            {
                // Depth layers first: wherever nothing on the plane sits in a gap of more than
                // depthGap along the normal, what's in front and what's behind are separate
                // things, however the mesh happens to connect them (a fire escape is usually one
                // connected mesh, rails and stairs alike).
                float layerGap = MathF.Max(eps * 0.5f, _in.Extent * 0.002f);
                var byDepth = tris
                    .Select(t => (T: t,
                                  Lo: MathF.Min(Vector3.Dot(_in.Corners[t * 3], n), MathF.Min(Vector3.Dot(_in.Corners[t * 3 + 1], n), Vector3.Dot(_in.Corners[t * 3 + 2], n))),
                                  Hi: MathF.Max(Vector3.Dot(_in.Corners[t * 3], n), MathF.Max(Vector3.Dot(_in.Corners[t * 3 + 1], n), Vector3.Dot(_in.Corners[t * 3 + 2], n)))))
                    .OrderBy(x => x.Lo)
                    .ToList();
                var result = new List<List<int>>();
                var layer = new List<int>();
                float layerHi = float.MinValue;
                foreach (var (t, lo, hi) in byDepth)
                {
                    if (layer.Count > 0 && lo > layerHi + layerGap)
                    {
                        result.AddRange(SplitLayer(layer, u, v, n, eps));
                        layer = new List<int>();
                        layerHi = float.MinValue;
                    }
                    layer.Add(t);
                    layerHi = MathF.Max(layerHi, hi);
                }
                if (layer.Count > 0) result.AddRange(SplitLayer(layer, u, v, n, eps));
                return result;
            }

            private List<List<int>> SplitLayer(List<int> tris, Vector3 u, Vector3 v, Vector3 n, float eps)
            {
                var groups = new List<List<int>>();
                if (tris.Count == 0) return groups;

                var uf = new UnionFind(tris.Count);
                var firstByWeld = new Dictionary<int, int>();
                for (int i = 0; i < tris.Count; i++)
                    for (int k = 0; k < 3; k++)
                    {
                        int w = _in.WeldIds[tris[i] * 3 + k];
                        if (firstByWeld.TryGetValue(w, out int j)) uf.Union(i, j);
                        else firstByWeld[w] = i;
                    }

                var byRoot = new Dictionary<int, List<int>>();
                for (int i = 0; i < tris.Count; i++)
                {
                    int r = uf.Find(i);
                    if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new List<int>();
                    list.Add(tris[i]);
                }

                var comps = byRoot.Values.Select(list =>
                {
                    var min = new Vector2(float.MaxValue);
                    var max = new Vector2(float.MinValue);
                    float dMin = float.MaxValue, dMax = float.MinValue;
                    foreach (int t in list)
                        for (int k = 0; k < 3; k++)
                        {
                            var p = _in.Corners[t * 3 + k];
                            var q = Project(p, u, v);
                            min = Vector2.Min(min, q);
                            max = Vector2.Max(max, q);
                            float d = Vector3.Dot(p, n);
                            dMin = MathF.Min(dMin, d);
                            dMax = MathF.Max(dMax, d);
                        }
                    return (Min: min, Max: max, DMin: dMin, DMax: dMax, Tris: list);
                }).ToList();

                float gap = MathF.Max(eps, _in.Extent * 0.02f);
                float depthGap = MathF.Max(eps * 0.5f, _in.Extent * 0.002f);
                bool merged = true;
                while (merged && comps.Count > 1)
                {
                    _ct.ThrowIfCancellationRequested();
                    merged = false;
                    comps.Sort((a, b) => a.Min.X.CompareTo(b.Min.X));
                    var cuf = new UnionFind(comps.Count);
                    for (int i = 0; i < comps.Count; i++)
                        for (int j = i + 1; j < comps.Count && comps[j].Min.X <= comps[i].Max.X + gap; j++)
                        {
                            if (comps[j].Min.Y > comps[i].Max.Y + gap || comps[i].Min.Y > comps[j].Max.Y + gap) continue;
                            if (comps[j].DMin > comps[i].DMax + depthGap || comps[i].DMin > comps[j].DMax + depthGap) continue;
                            if (cuf.Union(i, j)) merged = true;
                        }
                    if (!merged) break;

                    var next = new Dictionary<int, (Vector2 Min, Vector2 Max, float DMin, float DMax, List<int> Tris)>();
                    for (int i = 0; i < comps.Count; i++)
                    {
                        int r = cuf.Find(i);
                        var c = comps[i];
                        if (next.TryGetValue(r, out var acc))
                        {
                            acc.Tris.AddRange(c.Tris);
                            next[r] = (Vector2.Min(acc.Min, c.Min), Vector2.Max(acc.Max, c.Max),
                                       MathF.Min(acc.DMin, c.DMin), MathF.Max(acc.DMax, c.DMax), acc.Tris);
                        }
                        else next[r] = (c.Min, c.Max, c.DMin, c.DMax, new List<int>(c.Tris));
                    }
                    comps = next.Values.Select(g => (g.Min, g.Max, g.DMin, g.DMax, g.Tris)).ToList();
                }

                foreach (var c in comps) groups.Add(c.Tris);
                return groups;
            }

            private Billboard MakeBillboard(Vector3 n, float rho, Vector3 u, Vector3 v, int plane, List<int> tris, bool ownDepth = true)
            {
                // Drawn at the projected-area-weighted depth of its own triangles: a group split
                // off its plane (a rail in front of the stair it shared a slab with) sits where
                // it actually is, not at the depth of everything the plane held.
                double weightSum = 0, depthSum = 0;
                foreach (int t in tris)
                {
                    float w = _in.Areas[t] * Vector3.Dot(_in.Normals[t], n);
                    if (w <= 0) continue;
                    weightSum += w;
                    depthSum += w * (Vector3.Dot(_in.Corners[t * 3], n) + Vector3.Dot(_in.Corners[t * 3 + 1], n) + Vector3.Dot(_in.Corners[t * 3 + 2], n)) / 3.0;
                }
                if (ownDepth && weightSum > 0) rho = (float)(depthSum / weightSum);

                var bb = new Billboard { Normal = n, Offset = rho, AxisU = u, AxisV = v, Triangles = new List<int>(tris), PlaneIndex = plane, Side = _planeIsSide[plane], Detail = _planeIsDetail[plane] };
                var min = new Vector2(float.MaxValue);
                var max = new Vector2(float.MinValue);
                foreach (int t in tris)
                    for (int k = 0; k < 3; k++)
                    {
                        var q = Project(_in.Corners[t * 3 + k], u, v);
                        min = Vector2.Min(min, q);
                        max = Vector2.Max(max, q);
                    }
                bb.Min = bb.ContentMin = min;
                bb.Max = bb.ContentMax = max;
                bb.Coverage = CoverageGrid(bb).Fraction;
                return bb;
            }

            // A mostly-empty billboard (a row of pilasters joined only by a cornice, a door recess
            // and a strip of parapet at the same depth) costs little in triangles but a lot of
            // atlas space and overdraw. It is cut in two where that shrinks the combined
            // rectangles the most - swept like a BVH split: triangles sorted by centroid along
            // each axis, every cut between neighbours scored by the two trimmed halves' total
            // area - and each half is tried again. Two extra triangles per cut, so a cut has to
            // save a real share of the rectangle to be worth it.
            private List<Billboard> SplitSparse(Billboard bb, int depth)
            {
                var single = new List<Billboard> { bb };
                if (depth >= 6 || bb.Coverage > 0.6f || bb.Triangles.Count < 2) return single;

                float bestArea = RectArea(bb) * 0.7f;
                List<int>? bestA = null, bestB = null;
                foreach (var axis in new[] { bb.AxisU, bb.AxisV })
                {
                    var sorted = bb.Triangles
                        .Select(t => (T: t, Key: Vector3.Dot(_in.Corners[t * 3] + _in.Corners[t * 3 + 1] + _in.Corners[t * 3 + 2], axis)))
                        .OrderBy(x => x.Key)
                        .Select(x => x.T)
                        .ToList();
                    int n = sorted.Count;

                    // Prefix and suffix footprints.
                    var preMin = new Vector2[n]; var preMax = new Vector2[n];
                    var sufMin = new Vector2[n]; var sufMax = new Vector2[n];
                    var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
                    for (int i = 0; i < n; i++)
                    {
                        var (lo, hi) = Footprint(sorted[i], bb.AxisU, bb.AxisV);
                        min = Vector2.Min(min, lo); max = Vector2.Max(max, hi);
                        preMin[i] = min; preMax[i] = max;
                    }
                    min = new Vector2(float.MaxValue); max = new Vector2(float.MinValue);
                    for (int i = n - 1; i >= 0; i--)
                    {
                        var (lo, hi) = Footprint(sorted[i], bb.AxisU, bb.AxisV);
                        min = Vector2.Min(min, lo); max = Vector2.Max(max, hi);
                        sufMin[i] = min; sufMax[i] = max;
                    }

                    int bestCut = -1;
                    for (int i = 0; i + 1 < n; i++)
                    {
                        var a = preMax[i] - preMin[i];
                        var b = sufMax[i + 1] - sufMin[i + 1];
                        float area = a.X * a.Y + b.X * b.Y;
                        if (area < bestArea) { bestArea = area; bestCut = i; }
                    }
                    if (bestCut >= 0)
                    {
                        bestA = sorted.GetRange(0, bestCut + 1);
                        bestB = sorted.GetRange(bestCut + 1, n - bestCut - 1);
                    }
                }
                if (bestA == null || bestB == null) return single;

                // Both halves are the same structure, so they keep its depth - averaging each on its
                // own puts a visible step in a rail's top bar where the cut falls.
                var result = SplitSparse(MakeBillboard(bb.Normal, bb.Offset, bb.AxisU, bb.AxisV, bb.PlaneIndex, bestA, ownDepth: false), depth + 1);
                result.AddRange(SplitSparse(MakeBillboard(bb.Normal, bb.Offset, bb.AxisU, bb.AxisV, bb.PlaneIndex, bestB, ownDepth: false), depth + 1));
                return result;
            }

            private (Vector2 Min, Vector2 Max) Footprint(int t, Vector3 u, Vector3 v)
            {
                var a = Project(_in.Corners[t * 3], u, v);
                var b = Project(_in.Corners[t * 3 + 1], u, v);
                var c = Project(_in.Corners[t * 3 + 2], u, v);
                return (Vector2.Min(a, Vector2.Min(b, c)), Vector2.Max(a, Vector2.Max(b, c)));
            }

            private float ProjectedArea(Billboard bb) =>
                bb.Triangles.Sum(t => _in.Areas[t] * MathF.Max(0f, Vector3.Dot(_in.Normals[t], bb.Normal)));

            private static float RectArea(Billboard bb) => (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);

            // Coarse raster of the billboard's triangles into a grid over its rectangle: the
            // uncovered fraction is what will need to be transparent once it's textured.
            private CoverageMap CoverageMap(Billboard bb)
            {
                var grid = CoverageGrid(bb);
                return new CoverageMap { Min = bb.Min, Cell = MathF.Max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y) / CoverageCells, Cols = grid.Cols, Rows = grid.Rows, Covered = grid.Covered };
            }

            private (bool[] Covered, int Cols, int Rows, float Fraction) CoverageGrid(Billboard bb)
            {
                var size = bb.Max - bb.Min;
                if (size.X <= 0 || size.Y <= 0) return (new[] { true }, 1, 1, 1f);
                float cell = MathF.Max(size.X, size.Y) / CoverageCells;
                int cols = Math.Max(1, (int)MathF.Ceiling(size.X / cell));
                int rows = Math.Max(1, (int)MathF.Ceiling(size.Y / cell));
                var covered = new bool[cols * rows];

                foreach (int t in bb.Triangles)
                {
                    var a = (Project(_in.Corners[t * 3], bb.AxisU, bb.AxisV) - bb.Min) / cell;
                    var b = (Project(_in.Corners[t * 3 + 1], bb.AxisU, bb.AxisV) - bb.Min) / cell;
                    var c = (Project(_in.Corners[t * 3 + 2], bb.AxisU, bb.AxisV) - bb.Min) / cell;

                    // Triangles smaller than a cell still mark the cell they sit in.
                    var centroid = (a + b + c) / 3f;
                    covered[Math.Clamp((int)centroid.Y, 0, rows - 1) * cols + Math.Clamp((int)centroid.X, 0, cols - 1)] = true;

                    float area = Cross2(b - a, c - a);
                    if (MathF.Abs(area) < 1e-12f) continue;
                    int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
                    int x1 = Math.Min(cols - 1, (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                    int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
                    int y1 = Math.Min(rows - 1, (int)MathF.Floor(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                    for (int y = y0; y <= y1; y++)
                        for (int x = x0; x <= x1; x++)
                        {
                            var p = new Vector2(x + 0.5f, y + 0.5f);
                            float w0 = Cross2(c - b, p - b), w1 = Cross2(a - c, p - c), w2 = Cross2(b - a, p - a);
                            bool inside = area > 0 ? (w0 >= 0 && w1 >= 0 && w2 >= 0) : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                            if (inside) covered[y * cols + x] = true;
                        }
                }

                int count = 0;
                foreach (bool b in covered) if (b) count++;
                return (covered, cols, rows, (float)count / covered.Length);
            }
        }

        // --- helpers ---------------------------------------------------------------------------

        // Upright for walls (V follows world up, so texture rows stay level); for roofs and
        // floors U follows the building's own X axis. AxisU x AxisV == n, so a quad wound
        // (min,min) -> (max,min) -> (max,max) -> (min,max) is counter-clockwise seen from the front.
        public static (Vector3 U, Vector3 V) PlaneBasis(Vector3 n, Vector3 frameX)
        {
            if (MathF.Abs(n.Y) < 0.95f)
            {
                var v = Vector3.Normalize(Vector3.UnitY - n * n.Y);
                return (Vector3.Cross(v, n), v);
            }
            var u = frameX - n * Vector3.Dot(frameX, n);
            u = u.LengthSquared() < 1e-8f ? Vector3.Normalize(Vector3.Cross(Vector3.UnitZ, n)) : Vector3.Normalize(u);
            return (u, Vector3.Cross(n, u));
        }

        private static Vector2 Project(Vector3 p, Vector3 u, Vector3 v) => new(Vector3.Dot(p, u), Vector3.Dot(p, v));

        private static float Cross2(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        private static int OctBin(Vector3 n)
        {
            var e = OctEncode(n);
            int x = Math.Clamp((int)((e.X * 0.5f + 0.5f) * OctBins), 0, OctBins - 1);
            int y = Math.Clamp((int)((e.Y * 0.5f + 0.5f) * OctBins), 0, OctBins - 1);
            return y * OctBins + x;
        }

        private static Vector2 OctEncode(Vector3 n)
        {
            float s = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
            if (s <= 0) return Vector2.Zero;
            float x = n.X / s, y = n.Y / s;
            if (n.Z < 0)
            {
                float ox = (1 - MathF.Abs(y)) * (x >= 0 ? 1 : -1);
                float oy = (1 - MathF.Abs(x)) * (y >= 0 ? 1 : -1);
                x = ox; y = oy;
            }
            return new Vector2(x, y);
        }

        private static Vector3 OctDecode(Vector2 e)
        {
            var v = new Vector3(e.X, e.Y, 1 - MathF.Abs(e.X) - MathF.Abs(e.Y));
            if (v.Z < 0)
            {
                float ox = (1 - MathF.Abs(e.Y)) * (e.X >= 0 ? 1 : -1);
                float oy = (1 - MathF.Abs(e.X)) * (e.Y >= 0 ? 1 : -1);
                v.X = ox; v.Y = oy;
            }
            return Vector3.Normalize(v);
        }

        private sealed class UnionFind
        {
            private readonly int[] _parent;
            public UnionFind(int n) { _parent = new int[n]; for (int i = 0; i < n; i++) _parent[i] = i; }
            public int Find(int i)
            {
                while (_parent[i] != i) { _parent[i] = _parent[_parent[i]]; i = _parent[i]; }
                return i;
            }
            public bool Union(int a, int b)
            {
                a = Find(a); b = Find(b);
                if (a == b) return false;
                _parent[a] = b;
                return true;
            }
        }
    }
}
