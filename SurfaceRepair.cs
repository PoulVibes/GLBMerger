using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Cleans up the surface faults generated models (Meshy, Tripo and friends) ship with, which show
    // in a game as dark specks that flicker as the camera moves:
    //
    //  1. Folds. A small flap of surface turned back over its neighbours, so its winding says it
    //     faces into the body while its stored normals say out. A double-sided material draws its
    //     back face with the normals flipped - dark - and since it lies on top of its neighbours it
    //     fights them for the depth test, so the dark comes and goes. Each fold is turned the right
    //     way round (two indices swapped): it then shades as the surface round it does, and whichever
    //     wins the depth test looks the same.
    //  2. Gutter padding. Each texture island's edge colour carried out into the empty atlas around
    //     it (UvIslandPadding), so bilinear filtering and mip levels at an island's edge blend in more
    //     of the island instead of the black around it. The atlas optimizer and generators both leave
    //     this thin; it's what makes the specks read as black.
    //  3. Dark island rims. Generators bake a thin dark rim round each texture island, which reads
    //     as short dark needles on the surface and survives painting over in a texture painter.
    //     Cleaned before padding, so the padding carries clean colour out instead of the rim (see
    //     UvIslandPadding.CleanRims). Analyze paints the triangles it touched, along with the folds;
    //     a real dark outline on an island's edge (round an eye, say) looks the same, so a caller
    //     can limit cleaning to the triangles left painted.
    //  4. Bad normals. Generators often store normals that disagree with the surface, worst where a
    //     UV seam splits a vertex in two and each copy got a different normal: the shading then
    //     jumps along the seam, and under a highlight a whole triangle reads as a bright or dark
    //     facet - lighting, so no texture fix touches it. Each normal is rebuilt from the faces
    //     round its position by the same rule every merge smooths with (ModelSmoother.RebuildNormals:
    //     creases sharper than its default angle stay hard, so the side walls of raised lettering
    //     stay crisp). Every normal is rebuilt, not just the badly
    //     wrong ones: the two copies at a seam each being a few degrees off, in different
    //     directions, still shows as an edge, and fixing only the far-off ones left those facets
    //     in. Only normals more than ReportDegrees off count as fixed and get highlighted. Runs
    //     after the folds are turned, so they count the right way round.
    //  5. Pleats. A seam left standing as a wall a fraction of a millimetre high but centimetres
    //     long (Niten's lower back). Every face of it is a needle, yet long enough to have as much
    //     area as the real faces beside it, so area weighting alone can't stop it turning their
    //     corners' normals to face along the wall: those corners then shade near black from every
    //     direction, and the wall opens as the mesh is posed, so the dark patch grows with it.
    //     Nothing in a texture painter reaches it, since it isn't in the texture. The normal rebuild
    //     picks these faces out by shape and leaves them out of its averages (see
    //     ModelSmoother.RebuildNormals), which is enough to make the wall shade as the surface it
    //     sits on; they're reported and highlighted as well, so a real thin ridge that was meant to
    //     be there can be seen for what it is.
    //  6. Faces drawn twice. The same face in the same place, the same way round, more than once -
    //     older merges and older runs of Make Watertight both left these behind. The two fight for
    //     the depth test, so which one is seen changes with the camera, and the fold turner and the
    //     normal rebuild below both read a surface that isn't really there. The extras are dropped
    //     (the first is kept, since a cap is appended after the surface it copies and so carries
    //     the wrong UV) before anything else looks at the geometry. The same place wound BOTH ways
    //     is left alone: that's the two sides of a face with no thickness, which hair and cloth
    //     need, and it's only counted.
    //
    // Turning a fold only rewrites the index buffer - every vertex keeps its position, UV, normal and
    // skin weights - so GeometryHistory records it for the price of the indices (plus the images,
    // when padding). Fixing normals rewrites the NORMAL accessor, which makes the step a
    // vertex-touching one. Primitives with morph targets are skipped, same as the other geometry
    // tools here.
    public static class SurfaceRepair
    {
        public sealed class Options
        {
            /// <summary>Turn triangles whose winding disagrees with their stored normals.</summary>
            public bool FlipFolds { get; set; } = true;

            /// <summary>Drop faces that stand in the same place, the same way round, as one already kept.</summary>
            public bool DropDuplicateFaces { get; set; } = true;

            /// <summary>Rebuild stored normals that disagree with the surface round them.</summary>
            public bool RepairNormals { get; set; } = true;

            /// <summary>Texels to carry each island's edge colour into the gutter; 0 leaves the textures alone.</summary>
            public int PaddingTexels { get; set; } = 16;

            /// <summary>Replace the dark rims generators bake round texture islands (BaseColor textures only).</summary>
            public bool CleanDarkRims { get; set; } = true;

            /// <summary>When set, dark rims are only cleaned inside these triangles - see UvIslandPadding.Options.CleanOnlyTriangles.</summary>
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? CleanOnlyTriangles { get; set; }
        }

        public sealed class PrimitiveResult
        {
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }
            public string MeshName { get; init; } = "";
            public int Triangles { get; init; }

            /// <summary>The folds turned, by triangle index - what the preview highlights.</summary>
            public HashSet<int> Found { get; } = new();

            /// <summary>Triangles with a corner whose normal was far off - also highlighted.</summary>
            public HashSet<int> NormalTriangles { get; } = new();

            /// <summary>The pleat faces the normal rebuild left out of its averages - also highlighted.</summary>
            public HashSet<int> PleatTriangles { get; } = new();

            /// <summary>Faces dropped as duplicates, by their index before the drop - also highlighted.</summary>
            public HashSet<int> DuplicateTriangles { get; } = new();

            /// <summary>Back-to-back pairs found and left alone (see DropDuplicateFaces) - also highlighted.</summary>
            public HashSet<int> OpposedTriangles { get; } = new();

            /// <summary>Normals that were far off (ReportDegrees); every normal is rebuilt regardless.</summary>
            public int NormalsFixed { get; set; }

            public int[]? NewIndices { get; set; }
            public Vector3[]? NewNormals { get; set; }
            public string? SkippedReason { get; set; }
        }

        public sealed class ImageResult
        {
            public int ImageIndex { get; init; }
            public string Label { get; init; } = "";
            public int TexelsPadded { get; init; }
            public int TexelsCleaned { get; init; }
            public Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> CleanedTriangles { get; init; } = new();
            internal byte[] Png { get; init; } = Array.Empty<byte>();
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();
            public List<ImageResult> Images { get; } = new();
            public int Flipped => Primitives.Sum(p => p.Found.Count);
            public int NormalsFixed => Primitives.Sum(p => p.NormalsFixed);
            public int Pleats => Primitives.Sum(p => p.PleatTriangles.Count);
            public int DuplicatesDropped => Primitives.Sum(p => p.DuplicateTriangles.Count);
            public int OpposedFaces => Primitives.Sum(p => p.OpposedTriangles.Count);
            public int TexelsPadded => Images.Sum(i => i.TexelsPadded);
            public int TexelsCleaned => Images.Sum(i => i.TexelsCleaned);
            public bool TouchesGeometry => Primitives.Any(p => p.NewIndices != null || p.NewNormals != null);
            public bool TouchesVertexData => Primitives.Any(p => p.NewNormals != null);
            public bool TouchesImages => Images.Count > 0;
            public bool HasChanges => TouchesGeometry || TouchesImages;
        }

        public static Report Analyze(ModelRoot model, Options options, CancellationToken cancellation = default)
        {
            var report = new Report();
            if (options.FlipFolds || options.RepairNormals || options.DropDuplicateFaces)
            {
                for (int m = 0; m < model.LogicalMeshes.Count; m++)
                {
                    var mesh = model.LogicalMeshes[m];
                    for (int p = 0; p < mesh.Primitives.Count; p++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        report.Primitives.Add(AnalyzePrimitive(mesh, m, p, options));
                    }
                }
            }

            if (options.PaddingTexels > 0 || options.CleanDarkRims)
            {
                var padding = new UvIslandPadding.Options
                {
                    PaddingTexels = options.PaddingTexels, CleanDarkRims = options.CleanDarkRims,
                    CleanOnlyTriangles = options.CleanOnlyTriangles,
                };
                foreach (var target in UvIslandPadding.FindImages(model))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (result, png) = UvIslandPadding.Process(model, target.ImageIndex, padding);
                    if (result.TexelsPadded == 0 && result.TexelsCleaned == 0) continue;
                    report.Images.Add(new ImageResult
                    {
                        ImageIndex = target.ImageIndex, Label = target.Label,
                        TexelsPadded = result.TexelsPadded, TexelsCleaned = result.TexelsCleaned,
                        CleanedTriangles = result.CleanedTriangles, Png = png,
                    });
                }
            }
            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                var prim = model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex];
                if (p.NewIndices != null) prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, p.NewIndices);
                if (p.NewNormals != null) WriteNormals(prim, p.NewNormals);
            }
            foreach (var image in report.Images)
                UvIslandPadding.Apply(model, image.ImageIndex, image.Png);
        }

        // Written over the existing accessor where it is plain floats, the same way ModelSmoother
        // writes a smoothed set: WithVertexAccessor mints a new one instead, and because NORMAL is
        // usually interleaved with POSITION and the UVs, the view it came from stays live for those
        // and the old normals can never be reclaimed - every repair would add a full normal array
        // to the file. Anything else (a normalized or sparse accessor) still goes the long way.
        private static void WriteNormals(MeshPrimitive prim, Vector3[] normals)
        {
            var accessor = prim.GetVertexAccessor("NORMAL");
            if (accessor != null && accessor.Encoding == EncodingType.FLOAT && !accessor.Normalized && !accessor.IsSparse)
            {
                var target = accessor.AsVector3Array();
                if (target.Count == normals.Length)
                {
                    for (int v = 0; v < normals.Length; v++) target[v] = normals[v];
                    accessor.UpdateBounds();
                    return;
                }
            }
            // FLOAT VEC3 is always a valid NORMAL encoding, whatever the source was.
            prim.WithVertexAccessor("NORMAL", normals.ToList());
        }

        // A fold's face normal points against all three of its stored normals, which agree among
        // themselves - so it isn't merely a triangle across a sharp crease, where they wouldn't.
        private const float FoldDot = -0.3f, NormalsAgree = 0.5f;

        private static PrimitiveResult AnalyzePrimitive(Mesh mesh, int meshIndex, int primIndex, Options options)
        {
            var prim = mesh.Primitives[primIndex];
            var tris = new List<int>();
            if (prim.DrawPrimitiveType == PrimitiveType.TRIANGLES)
                foreach (var (a, b, c) in prim.GetTriangleIndices()) { tris.Add(a); tris.Add(b); tris.Add(c); }

            var result = new PrimitiveResult
            {
                MeshIndex = meshIndex, PrimitiveIndex = primIndex,
                MeshName = mesh.Name ?? $"mesh_{meshIndex}", Triangles = tris.Count / 3,
            };
            if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) { result.SkippedReason = "not a triangle list"; return result; }
            if (prim.MorphTargetsCount > 0) { result.SkippedReason = "has morph targets"; return result; }
            if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) { result.SkippedReason = "no POSITION"; return result; }
            if (!prim.VertexAccessors.TryGetValue("NORMAL", out var nAcc)) { result.SkippedReason = "no NORMAL to tell a fold by"; return result; }

            var pos = posAcc.AsVector3Array();
            var stored = nAcc.AsVector3Array();
            var idx = tris.ToArray();

            // Before anything else: a face drawn twice is one face too many, and leaving it in would
            // have the fold turner and the normal rebuild both reading a surface that isn't there.
            int[]? kept = null;
            if (options.DropDuplicateFaces) idx = DropDuplicateFaces(pos, idx, result, out kept);

            if (options.FlipFolds) TurnFolds(pos, stored, idx, result.Found);

            if (options.RepairNormals)
            {
                var rebuilt = RebuildNormals(pos, stored, idx, result.PleatTriangles);
                // A fold whose stored normals were as wrong as its winding agreed with them, so the
                // first look passed it; against the rebuilt normals it shows. Turning it changes the
                // faces round it, so the normals are rebuilt once more after.
                if (options.FlipFolds && TurnFolds(pos, rebuilt, idx, result.Found) > 0)
                {
                    // Turning a fold only swaps two of its corners, so a triangle keeps its index and
                    // the pleats found the first time round are still the same triangles.
                    result.PleatTriangles.Clear();
                    rebuilt = RebuildNormals(pos, rebuilt, idx, result.PleatTriangles);
                }
                RecordNormalChanges(stored, rebuilt, idx, result);
            }

            // Everything above counted triangles in the buffer it was handed, which is short of the
            // duplicates by then; the preview paints the model as it stands, so they go back to the
            // numbering it knows.
            if (kept != null)
            {
                Renumber(result.Found, kept);
                Renumber(result.NormalTriangles, kept);
                Renumber(result.PleatTriangles, kept);
            }

            if (result.Found.Count > 0 || result.DuplicateTriangles.Count > 0) result.NewIndices = idx;
            return result;
        }

        // Turns triangle numbers in the compacted buffer back into the numbers they had before the
        // duplicates were dropped.
        private static void Renumber(HashSet<int> triangles, int[] kept)
        {
            if (triangles.Count == 0) return;
            var original = triangles.Select(t => kept[t]).ToList();
            triangles.Clear();
            foreach (var t in original) triangles.Add(t);
        }

        // Two faces standing in the same place and wound the same way are one face drawn twice.
        // They fight for the depth test, so which one is seen changes with the camera - the
        // flickering dark speck again - and nothing downstream can tell them apart. The first is
        // kept: a cap is appended after the surface it duplicates, so the first is the original,
        // with the UV the surface around it really uses.
        //
        // A pair in the same place wound opposite ways is a different thing: the two sides of a
        // face with no thickness, which generated models use for hair, leaves and cloth and which a
        // single-sided material needs both of. Those are counted and left alone - drop a side and
        // it vanishes from that direction - even though a cap over a loose triangle looks exactly
        // the same from here. Make Watertight no longer adds those (WatertightRepair's
        // LoopsAlreadyFaced), so what is left is as likely to be meant as not.
        //
        // Returns the compacted index buffer; `kept` gives each surviving triangle's number in the
        // buffer that came in.
        private static int[] DropDuplicateFaces(IList<Vector3> pos, int[] idx, PrimitiveResult result, out int[] kept)
        {
            int triCount = idx.Length / 3;

            // Welded on the same grid the normal rebuild uses, so split copies of one point (a UV
            // seam) count as the same place.
            var min = pos.Count > 0 ? pos.Aggregate(Vector3.Min) : Vector3.Zero;
            var max = pos.Count > 0 ? pos.Aggregate(Vector3.Max) : Vector3.Zero;
            float cell = MathF.Max((max - min).Length() * 1e-6f, 1e-9f);
            var weld = new int[pos.Count];
            var weldIds = new Dictionary<(long, long, long), int>();
            for (int v = 0; v < pos.Count; v++)
            {
                var p = pos[v] / cell;
                var key = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z));
                if (!weldIds.TryGetValue(key, out int id)) weldIds[key] = id = weldIds.Count;
                weld[v] = id;
            }

            // A face's three welded points rotated to start at the smallest: equal for two faces in
            // the same place wound the same way, different when one is wound the other way.
            (int, int, int) Wound(int t)
            {
                int a = weld[idx[t * 3]], b = weld[idx[t * 3 + 1]], c = weld[idx[t * 3 + 2]];
                return a <= b && a <= c ? (a, b, c) : b <= c ? (b, c, a) : (c, a, b);
            }
            static (int, int, int) Place((int A, int B, int C) w)
            {
                int lo = Math.Min(w.A, Math.Min(w.B, w.C)), hi = Math.Max(w.A, Math.Max(w.B, w.C));
                return (lo, w.A + w.B + w.C - lo - hi, hi);
            }

            var firstWound = new Dictionary<(int, int, int), int>();
            var survivors = new List<int>(triCount);
            foreach (var t in Enumerable.Range(0, triCount))
            {
                var wound = Wound(t);
                // A face with two corners in one place has no front or back to duplicate.
                if (wound.Item1 == wound.Item2 || wound.Item2 == wound.Item3 || wound.Item1 == wound.Item3)
                {
                    survivors.Add(t);
                    continue;
                }
                if (firstWound.ContainsKey(wound)) { result.DuplicateTriangles.Add(t); continue; }
                firstWound[wound] = t;
                survivors.Add(t);
            }

            // Of what is left, the places that hold more than one way round.
            var byPlace = new Dictionary<(int, int, int), List<int>>();
            foreach (var t in survivors)
            {
                var wound = Wound(t);
                if (wound.Item1 == wound.Item2 || wound.Item2 == wound.Item3 || wound.Item1 == wound.Item3) continue;
                var place = Place(wound);
                if (!byPlace.TryGetValue(place, out var list)) byPlace[place] = list = new List<int>();
                list.Add(t);
            }
            foreach (var list in byPlace.Values)
                if (list.Count > 1)
                    foreach (var t in list) result.OpposedTriangles.Add(t);

            kept = survivors.ToArray();
            if (result.DuplicateTriangles.Count == 0) return idx;

            var compacted = new int[survivors.Count * 3];
            for (int i = 0; i < survivors.Count; i++)
            {
                int t = survivors[i];
                compacted[i * 3] = idx[t * 3];
                compacted[i * 3 + 1] = idx[t * 3 + 1];
                compacted[i * 3 + 2] = idx[t * 3 + 2];
            }
            return compacted;
        }

        // Turns, in place, every triangle whose winding is against normals that agree among
        // themselves, skipping ones already turned. Returns how many it turned.
        private static int TurnFolds(IList<Vector3> pos, IList<Vector3> normals, int[] idx, HashSet<int> found)
        {
            int turned = 0;
            for (int t = 0; t < idx.Length / 3; t++)
            {
                if (found.Contains(t)) continue;
                int a = idx[t * 3], b = idx[t * 3 + 1], c = idx[t * 3 + 2];
                var face = Vector3.Cross(pos[b] - pos[a], pos[c] - pos[a]);
                float len = face.Length();
                if (len < 1e-20f) continue;
                var avg = normals[a] + normals[b] + normals[c];
                if (avg.LengthSquared() < 1e-12f) continue;
                avg = Vector3.Normalize(avg);
                if (Vector3.Dot(normals[a], avg) < NormalsAgree || Vector3.Dot(normals[b], avg) < NormalsAgree
                    || Vector3.Dot(normals[c], avg) < NormalsAgree) continue;
                if (Vector3.Dot(face / len, avg) >= FoldDot) continue;

                (idx[t * 3 + 1], idx[t * 3 + 2]) = (c, b);
                found.Add(t);
                turned++;
            }
            return turned;
        }

        // A normal more than this far off its rebuilt value is what the report counts and the
        // preview shows; the rest are rebuilt all the same.
        private const float ReportDegrees = 15f;

        // The same rule every merge smooths with (ModelSmoother.RebuildNormals), so a repaired
        // file still agrees with itself after being merged again.
        private static Vector3[] RebuildNormals(IList<Vector3> pos, IList<Vector3> normals, int[] idx,
            HashSet<int>? pleats = null) =>
            ModelSmoother.RebuildNormals(pos.ToArray(), normals.ToArray(), idx, ModelSmoother.DefaultCreaseAngle, pleats);

        // Fills in what changed between the stored normals and the rebuilt ones: the normals more
        // than ReportDegrees off, the triangles they touch, and NewNormals if anything moved.
        private static void RecordNormalChanges(IList<Vector3> stored, Vector3[] rebuilt, int[] idx, PrimitiveResult result)
        {
            float reportCos = MathF.Cos(ReportDegrees * MathF.PI / 180f);
            var farOff = new bool[stored.Count];
            bool anyChanged = false;
            for (int v = 0; v < stored.Count; v++)
            {
                if (Agreement(stored[v], rebuilt[v]) >= ModelSmoother.UnchangedDot) continue;
                anyChanged = true;
                if (Agreement(stored[v], rebuilt[v]) < reportCos) { farOff[v] = true; result.NormalsFixed++; }
            }
            if (!anyChanged) return;

            for (int t = 0; t < idx.Length / 3; t++)
                if (farOff[idx[t * 3]] || farOff[idx[t * 3 + 1]] || farOff[idx[t * 3 + 2]])
                    result.NormalTriangles.Add(t);
            result.NewNormals = rebuilt;
        }

        // Cosine between a stored normal (possibly unnormalised or zero) and a unit one.
        private static float Agreement(Vector3 stored, Vector3 unit)
        {
            float len = stored.Length();
            return len > 1e-6f ? Vector3.Dot(stored / len, unit) : -1f;
        }
    }
}
