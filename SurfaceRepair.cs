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
    //
    // Turning a fold only rewrites the index buffer - every vertex keeps its position, UV, normal and
    // skin weights - so GeometryHistory records the step for the price of the indices (plus the
    // images, when padding). Primitives with morph targets are skipped, same as the other geometry
    // tools here.
    public static class SurfaceRepair
    {
        public sealed class Options
        {
            /// <summary>Turn triangles whose winding disagrees with their stored normals.</summary>
            public bool FlipFolds { get; set; } = true;

            /// <summary>Texels to carry each island's edge colour into the gutter; 0 leaves the textures alone.</summary>
            public int PaddingTexels { get; set; } = 16;
        }

        public sealed class PrimitiveResult
        {
            public int MeshIndex { get; init; }
            public int PrimitiveIndex { get; init; }
            public string MeshName { get; init; } = "";
            public int Triangles { get; init; }

            /// <summary>The folds turned, by triangle index - what the preview highlights.</summary>
            public HashSet<int> Found { get; } = new();

            public int[]? NewIndices { get; set; }
            public string? SkippedReason { get; set; }
        }

        public sealed class ImageResult
        {
            public int ImageIndex { get; init; }
            public string Label { get; init; } = "";
            public int TexelsPadded { get; init; }
            internal byte[] Png { get; init; } = Array.Empty<byte>();
        }

        public sealed class Report
        {
            public List<PrimitiveResult> Primitives { get; } = new();
            public List<ImageResult> Images { get; } = new();
            public int Flipped => Primitives.Sum(p => p.Found.Count);
            public int TexelsPadded => Images.Sum(i => i.TexelsPadded);
            public bool TouchesGeometry => Primitives.Any(p => p.NewIndices != null);
            public bool TouchesImages => Images.Count > 0;
            public bool HasChanges => TouchesGeometry || TouchesImages;
        }

        public static Report Analyze(ModelRoot model, Options options, CancellationToken cancellation = default)
        {
            var report = new Report();
            if (options.FlipFolds)
            {
                for (int m = 0; m < model.LogicalMeshes.Count; m++)
                {
                    var mesh = model.LogicalMeshes[m];
                    for (int p = 0; p < mesh.Primitives.Count; p++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        report.Primitives.Add(AnalyzePrimitive(mesh, m, p));
                    }
                }
            }

            if (options.PaddingTexels > 0)
            {
                var padding = new UvIslandPadding.Options { PaddingTexels = options.PaddingTexels };
                foreach (var target in UvIslandPadding.FindImages(model))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var (result, png) = UvIslandPadding.Process(model, target.ImageIndex, padding);
                    if (result.TexelsPadded == 0) continue;
                    report.Images.Add(new ImageResult
                    {
                        ImageIndex = target.ImageIndex, Label = target.Label,
                        TexelsPadded = result.TexelsPadded, Png = png,
                    });
                }
            }
            return report;
        }

        public static void Apply(Report report, ModelRoot model)
        {
            foreach (var p in report.Primitives)
            {
                if (p.NewIndices == null) continue;
                model.LogicalMeshes[p.MeshIndex].Primitives[p.PrimitiveIndex]
                    .WithIndicesAccessor(PrimitiveType.TRIANGLES, p.NewIndices);
            }
            foreach (var image in report.Images)
                UvIslandPadding.Apply(model, image.ImageIndex, image.Png);
        }

        // A fold's face normal points against all three of its stored normals, which agree among
        // themselves - so it isn't merely a triangle across a sharp crease, where they wouldn't.
        private const float FoldDot = -0.3f, NormalsAgree = 0.5f;

        private static PrimitiveResult AnalyzePrimitive(Mesh mesh, int meshIndex, int primIndex)
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
            var normals = nAcc.AsVector3Array();
            var idx = tris.ToArray();
            for (int t = 0; t < idx.Length / 3; t++)
            {
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
                result.Found.Add(t);
            }

            if (result.Found.Count > 0) result.NewIndices = idx;
            return result;
        }
    }
}
