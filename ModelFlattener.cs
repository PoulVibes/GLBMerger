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

            public int TriangleCount => Areas.Length;
            public Vector3 BoundsMin, BoundsMax;
            public float Extent;      // largest bounding-box dimension
            public float TotalArea;

            // The building's dominant horizontal axes (glTF Y is up).
            public Vector3 FrameX = Vector3.UnitX, FrameZ = Vector3.UnitZ;
        }

        public sealed class FlattenSettings
        {
            public float Tolerance;          // epsilon, model units
            public float MinBillboardArea;   // projected area below which no billboard is made
            public bool KeepLeftoversAsMesh = true;

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
            // Part of the see-through detail pass (a rail, a stair, a floor): its gaps are real.
            public bool Detail;

            public Vector3 Corner(int i)
            {
                float u = i == 0 || i == 3 ? Min.X : Max.X;
                float v = i < 2 ? Min.Y : Max.Y;
                return Normal * Offset + AxisU * u + AxisV * v;
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
            public int OutputTriangles => Billboards.Count * 2 + KeptTriangles;
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

            foreach (var node in MeshNodes(model))
            {
                var mesh = node.Mesh;
                var world = node.WorldMatrix;
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

                    foreach (var (a, b, c) in prim.GetTriangleIndices())
                    {
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
            };
            if (n == 0) return input;

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

            (input.FrameX, input.FrameZ) = FindBuildingAxes(input);
            return input;
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
                return result;
            }

            // See-through detail (fire escapes, railings) isn't searched for planes the way solid
            // surfaces are: a slab of +-epsilon either cuts one structure in two (half a round
            // bar a few centimetres behind the other half) or sweeps separate structures into
            // one billboard (a rail and the stair behind it, the left rail's end and the right
            // rail). Neither does classifying triangles one at a time by the way they face - a
            // round bar's faces point every way, and it falls apart into slivers.
            // Instead each structure is isolated first and flattened on its own:
            //  - stair flights (DetectStairPlanes) take their treads' tops and undersides;
            //  - everything else is split into connected pieces, and pieces are merged while
            //    what they make stays flat along one direction (ClusterStructures): a rail's bars
            //    and top bar become one flat rail, the stair behind it doesn't join;
            //  - each structure gets one billboard facing its flat direction (and one facing
            //    back for its rear), drawn at its own depth, rendering only its own triangles.
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

                    // A flight takes what lies on it - treads, nosings, stringers - not everything
                    // in its box facing its way: the box also holds the balcony it lands on, and
                    // the rail bars there face the flight's slope as much as its treads do.
                    // The band reaches well below the treads (stringers, the flight's underside)
                    // but only a little above them: the handrail runs just above, and split
                    // between the flight's plane and its own it would come out in broken pieces.
                    var stairs = DetectStairPlanes(pool);
                    float stairBand = MathF.Max(2f * _eps, _in.Extent * 0.01f);
                    bool OnFlight(int t, Vector3 n, float rho)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            float d = Vector3.Dot(_in.Corners[t * 3 + c], n) - rho;
                            if (d > stairBand * 0.5f || d < -stairBand) return false;
                        }
                        return true;
                    }
                    var stairTris = new List<int>?[stairs.Count * 2];
                    var rest = new List<int>();
                    foreach (int t in pool)
                    {
                        int best = -1;
                        float bestDot = VisibleDot;
                        if (!_landings.Contains(t))
                            for (int i = 0; i < stairs.Count; i++)
                            {
                                if (!InRegion(stairs[i].Region, t) || !OnFlight(t, stairs[i].N, stairs[i].Rho)) continue;
                                float d = Vector3.Dot(_in.Normals[t], stairs[i].N);
                                if (MathF.Abs(d) > bestDot) { bestDot = MathF.Abs(d); best = i * 2 + (d > 0 ? 0 : 1); }
                            }
                        if (best < 0) rest.Add(t);
                        else (stairTris[best] ??= new List<int>()).Add(t);
                    }
                    for (int i = 0; i < stairTris.Length; i++)
                    {
                        var st = stairs[i / 2];
                        if (stairTris[i] != null) AddStructurePlane(i % 2 == 0 ? st.N : -st.N, stairTris[i]!, _minArea * FittedMinScale, st.Region);
                    }

                    var axes = new[] { _in.FrameX, Vector3.UnitY, _in.FrameZ }.Concat(stairs.Select(st => st.N)).ToArray();
                    foreach (var (tris, n) in ClusterStructures(rest, axes, detailEps))
                    {
                        // A structure's billboard shows only what faces it. A slender member lying
                        // in it - a stair's handrail in the side structure with its stringer, a
                        // floor's front edge - is edge-on to it and would vanish from straight
                        // on, where the real round bar is plain to see. Edge-on parts big enough
                        // to matter get a strip billboard of their own, facing the way they do.
                        // Each connected edge-on piece (all the way round a bar) faces the one axis
                        // it shows most to, front and back, like a structure of its own.
                        var edgeOn = tris.Where(t => MathF.Abs(Vector3.Dot(_in.Normals[t], n)) <= VisibleDot).ToHashSet();
                        foreach (var strip in Components(edgeOn.ToList()))
                        {
                            var a = axes.Where(x => MathF.Abs(Vector3.Dot(x, n)) < 0.9f)
                                .MaxBy(x => strip.Sum(t => _in.Areas[t] * MathF.Abs(Vector3.Dot(_in.Normals[t], x))));
                            float shown = a == default ? 0f : strip.Sum(t => _in.Areas[t] * MathF.Abs(Vector3.Dot(_in.Normals[t], a))) / 2f;
                            if (shown < _minArea * FittedMinScale) { edgeOn.ExceptWith(strip); continue; }
                            var stripFront = strip.Where(t => Vector3.Dot(_in.Normals[t], a) >= 0).ToList();
                            var stripBack = strip.Where(t => Vector3.Dot(_in.Normals[t], a) < 0).ToList();
                            if (stripFront.Count > 0) AddStructurePlane(a, stripFront, _minArea * FittedMinScale, null);
                            if (stripBack.Count > 0) AddStructurePlane(-a, stripBack, _minArea * FittedMinScale, null);
                        }

                        var front = new List<int>();
                        var back = new List<int>();
                        foreach (int t in tris)
                            if (!edgeOn.Contains(t)) (Vector3.Dot(_in.Normals[t], n) >= 0 ? front : back).Add(t);
                        if (front.Count > 0) AddStructurePlane(n, front, _minArea * FittedMinScale, null);
                        if (back.Count > 0) AddStructurePlane(-n, back, _minArea * FittedMinScale, null);
                    }
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
                        if (candidates.Count == 0 || !RunRound(candidates)) break;
                    }

                    Rehome(released);
                }
                finally { _minArea = mainMin; _eps = mainEps; _inSidePass = false; }
            }

            // One plane holding exactly these triangles, drawn at their projected-area-weighted
            // depth; BuildResult turns it into one billboard (or a few, if it's sparse).
            private void AddStructurePlane(Vector3 n, List<int> tris, float minArea, (Vector3 Min, Vector3 Max)? region)
            {
                double weightSum = 0, depthSum = 0, plainSum = 0;
                foreach (int t in tris)
                {
                    double d = (Vector3.Dot(_in.Corners[t * 3], n) + Vector3.Dot(_in.Corners[t * 3 + 1], n) + Vector3.Dot(_in.Corners[t * 3 + 2], n)) / 3.0;
                    plainSum += d;
                    float w = _in.Areas[t] * Vector3.Dot(_in.Normals[t], n);
                    if (w <= 0) continue;
                    weightSum += w;
                    depthSum += w * d;
                }
                float rho = (float)(weightSum > 0 ? depthSum / weightSum : plainSum / tris.Count);
                int p = RegisterPlane(n, rho, rho, minArea, region);
                _planeIsStructure.Add(p);
                foreach (int t in tris) _plane[t] = p;
            }

            // Splits triangles into structures - pieces that are each flat along one of `axes`
            // (thickness within tol) - and picks each one's facing.
            //  1. Triangles are grown into pieces along the welded mesh, smoothest joins first,
            //     for as long as each piece stays flat. A fire escape is often welded into one
            //     mesh; this is what cuts it apart where a rail meets the floor or a stair.
            //  2. Nearby pieces are merged, closest first, while the merged piece stays flat.
            //  Both merge first only along horizontal axes (a rail's bars join its top bar as an
            //  upright rail, rather than the top bars of the front and side rails joining as a
            //  flat ring seen from above), then along any.
            //  3. Each structure faces the flat axis it shows the most surface to.
            //  4. Structures below the minimum size join a big neighbour they're nearly flat
            //     with (within twice the tolerance along its facing) - a bracket joins its rail -
            //     otherwise they stay on their own.
            private List<(List<int> Tris, Vector3 N)> ClusterStructures(List<int> tris, Vector3[] axes, float tol)
            {
                int k = axes.Length;
                float gapTol = MathF.Max(2f * tol, _in.Extent * 0.01f);

                // 1. pieces along the mesh
                var tri = new FlatClusters(tris.Select(t => new List<int> { t }).ToList(), axes, _in, tol);
                var joins = new List<(int A, int B, float Order)>();
                var byWeld = new Dictionary<int, List<int>>();
                for (int i = 0; i < tris.Count; i++)
                    for (int c = 0; c < 3; c++)
                    {
                        int w = _in.WeldIds[tris[i] * 3 + c];
                        if (!byWeld.TryGetValue(w, out var list)) byWeld[w] = list = new List<int>();
                        if (!list.Contains(i)) list.Add(i);
                    }
                foreach (var list in byWeld.Values)
                    for (int a = 0; a < list.Count; a++)
                        for (int b = a + 1; b < list.Count; b++)
                            joins.Add((list[a], list[b], -Vector3.Dot(_in.Normals[tris[list[a]]], _in.Normals[tris[list[b]]])));
                joins.Sort((x, y) => x.Order.CompareTo(y.Order));
                tri.MergeAll(joins, _ct);
                var pieces = tri.Groups().Select(g => g.SelectMany(i => tri.Items[i]).ToList()).ToList();

                // 2. pieces by proximity: candidate pairs by box gap along the frame axes (0..2)
                var fc = new FlatClusters(pieces, axes, _in, tol);
                int m = pieces.Count;
                var order = Enumerable.Range(0, m).OrderBy(i => fc.Lo[i][0]).ToArray();
                var pairs = new List<(int A, int B, float Gap)>();
                for (int oi = 0; oi < m; oi++)
                {
                    _ct.ThrowIfCancellationRequested();
                    int i = order[oi];
                    for (int oj = oi + 1; oj < m; oj++)
                    {
                        int j = order[oj];
                        if (fc.Lo[j][0] > fc.Hi[i][0] + gapTol) break;
                        float gap = 0;
                        for (int a = 0; a < 3; a++)
                            gap = MathF.Max(gap, MathF.Max(fc.Lo[j][a] - fc.Hi[i][a], fc.Lo[i][a] - fc.Hi[j][a]));
                        if (gap <= gapTol) pairs.Add((i, j, gap));
                    }
                }
                pairs.Sort((x, y) => x.Gap.CompareTo(y.Gap));
                fc.MergeAll(pairs, _ct);

                // 3. facing
                var members = fc.Groups().ToDictionary(g => fc.Find(g[0]), g => g);
                var facing = new Dictionary<int, int>();
                foreach (var (r, list) in members)
                {
                    int best = -1;
                    float bestScore = float.MinValue;
                    bool anyFlat = Enumerable.Range(0, k).Any(a => fc.Thickness(r, a) <= tol);
                    for (int a = 0; a < k; a++)
                    {
                        if (anyFlat && fc.Thickness(r, a) > tol) continue;
                        float score = 0;
                        foreach (int i in list)
                            foreach (int t in pieces[i]) score += _in.Areas[t] * MathF.Abs(Vector3.Dot(_in.Normals[t], axes[a]));
                        if (a == 1 || a > 2) score *= 0.9f;   // near-ties go to upright billboards
                        if (score > bestScore) { bestScore = score; best = a; }
                    }
                    facing[r] = best;
                }

                // 4. small structures join a big neighbour they're nearly flat with
                var area = members.ToDictionary(kv => kv.Key, kv => kv.Value.Sum(i => pieces[i].Sum(t => _in.Areas[t])));
                var target = members.Keys.ToDictionary(r => r, r => r);
                bool Joins(int small, int big)
                {
                    int a = facing[big];
                    return MathF.Max(fc.Hi[small][a], fc.Hi[big][a]) - MathF.Min(fc.Lo[small][a], fc.Lo[big][a]) <= 2f * tol;
                }
                foreach (var (a, b, _) in pairs)
                {
                    int ra = fc.Find(a), rb = fc.Find(b);
                    if (ra == rb) continue;
                    bool smallA = area[ra] < _minArea, smallB = area[rb] < _minArea;
                    if (smallA && !smallB && target[ra] == ra && Joins(ra, rb)) target[ra] = rb;
                    else if (smallB && !smallA && target[rb] == rb && Joins(rb, ra)) target[rb] = ra;
                }

                var result = new Dictionary<int, List<int>>();
                foreach (var (r, list) in members)
                {
                    int to = target[r];
                    if (!result.TryGetValue(to, out var acc)) result[to] = acc = new List<int>();
                    foreach (int i in list) acc.AddRange(pieces[i]);
                }
                return result.Select(kv => (kv.Value, axes[facing[kv.Key]])).ToList();
            }

            // Union-find over groups of triangles that also tracks each set's extent along every
            // axis, and merges two sets only while the result stays flat (within tol) along one.
            private sealed class FlatClusters
            {
                public readonly List<List<int>> Items;
                public readonly float[][] Lo, Hi;
                private readonly UnionFind _uf;
                private readonly float _tol;
                private readonly int _k;

                public FlatClusters(List<List<int>> items, Vector3[] axes, FlattenInput input, float tol)
                {
                    Items = items;
                    _tol = tol;
                    _k = axes.Length;
                    _uf = new UnionFind(items.Count);
                    Lo = new float[items.Count][];
                    Hi = new float[items.Count][];
                    for (int i = 0; i < items.Count; i++)
                    {
                        var lo = Enumerable.Repeat(float.MaxValue, _k).ToArray();
                        var hi = Enumerable.Repeat(float.MinValue, _k).ToArray();
                        foreach (int t in items[i])
                            for (int c = 0; c < 3; c++)
                            {
                                var q = input.Corners[t * 3 + c];
                                for (int a = 0; a < _k; a++)
                                {
                                    float d = Vector3.Dot(q, axes[a]);
                                    lo[a] = MathF.Min(lo[a], d);
                                    hi[a] = MathF.Max(hi[a], d);
                                }
                            }
                        Lo[i] = lo;
                        Hi[i] = hi;
                    }
                }

                public int Find(int i) => _uf.Find(i);
                public float Thickness(int root, int axis) => Hi[root][axis] - Lo[root][axis];

                // Pairs in order; horizontal axes (0 and 2) only first, then any.
                public void MergeAll(List<(int A, int B, float Order)> pairs, CancellationToken ct)
                {
                    foreach (bool horizontalOnly in new[] { true, false })
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var (a, b, _) in pairs)
                        {
                            int ra = _uf.Find(a), rb = _uf.Find(b);
                            if (ra == rb || !FlatTogether(ra, rb, horizontalOnly)) continue;
                            _uf.Union(ra, rb);
                            int r = _uf.Find(ra), o = r == ra ? rb : ra;
                            for (int x = 0; x < _k; x++)
                            {
                                Lo[r][x] = MathF.Min(Lo[r][x], Lo[o][x]);
                                Hi[r][x] = MathF.Max(Hi[r][x], Hi[o][x]);
                            }
                        }
                    }
                }

                private bool FlatTogether(int ra, int rb, bool horizontalOnly)
                {
                    for (int a = 0; a < _k; a++)
                    {
                        if (horizontalOnly && a != 0 && a != 2) continue;
                        if (MathF.Max(Hi[ra][a], Hi[rb][a]) - MathF.Min(Lo[ra][a], Lo[rb][a]) <= _tol) return true;
                    }
                    return false;
                }

                public List<List<int>> Groups()
                {
                    var byRoot = new Dictionary<int, List<int>>();
                    for (int i = 0; i < Items.Count; i++)
                    {
                        int r = _uf.Find(i);
                        if (!byRoot.TryGetValue(r, out var list)) byRoot[r] = list = new List<int>();
                        list.Add(i);
                    }
                    return byRoot.Values.ToList();
                }
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
                        var pts = best.Select(k => patches[k].Centre).OrderBy(c => c.Y).ToList();
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
                    var (score, _) = Evaluate(candidates[c], aligned[c]);
                    if (score >= _minArea) queue.Enqueue(c, -score);
                }

                bool accepted = false;
                while (queue.TryDequeue(out int cand, out _))
                {
                    _ct.ThrowIfCancellationRequested();
                    if (_planes.Count >= MaxPlanes) break;

                    var (score, rho) = Evaluate(candidates[cand], aligned[cand]);
                    if (score < _minArea) continue;
                    if (queue.TryPeek(out _, out float nextNeg) && score < -nextNeg)
                    {
                        queue.Enqueue(cand, -score);
                        continue;
                    }

                    AcceptPlane(candidates[cand], rho, aligned[cand], _minArea);
                    accepted = true;
                    // The same normal may hold another plane at a different depth (a recessed
                    // pane behind the facade) - its score is recomputed when it next surfaces.
                    queue.Enqueue(cand, -score);
                }
                return accepted;
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
                    bool keep = _s.KeepLeftoversAsMesh && _in.Areas[t] > _degenerateArea;
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
                    var min = b.Min;
                    var max = b.Max;
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
                        b.Recessed = true;
                    }
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
                        // rails into streaks; only solid surfaces are welded.
                        if (a.Coverage < DetailCoverage || b.Coverage < DetailCoverage) continue;
                        float c = Vector3.Dot(a.Normal, b.Normal);
                        if (MathF.Abs(c) > 0.7f) continue;

                        var d = Vector3.Normalize(Vector3.Cross(a.Normal, b.Normal));
                        var onLine = ((a.Offset - b.Offset * c) * a.Normal + (b.Offset - a.Offset * c) * b.Normal) / (1 - c * c);
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
