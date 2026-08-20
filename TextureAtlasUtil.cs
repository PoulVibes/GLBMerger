using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Shared albedo-texture plumbing for the tools that edit a merged model's texture atlas
    // (UvIslandPadding, WatertightRepair, TextureEditorEditor): finding which images are actually
    // bound as base color, mapping each texel back to the 3D surface point that samples it, and
    // reading/writing the image bytes themselves.
    //
    // GatherTriangles/RasterizeTriangles are what give a texel a 3D position at all - a 2D
    // image-space view of an atlas can't tell a UV seam from open space, since two texels next to
    // each other in the atlas can belong to completely unrelated parts of the model. Rasterizing
    // every triangle's UV footprint and barycentric-interpolating its POSITION is what lets a
    // caller reason about texel adjacency on the surface instead.
    public static class TextureAtlasUtil
    {
        public sealed class AlbedoTarget
        {
            public int ImageIndex { get; init; }
            public string Label { get; init; } = "";
        }

        // Every image used as a BaseColor texture by at least one material, labelled with the
        // material name(s) that use it - a texture shared across materials shows up once.
        public static List<AlbedoTarget> FindAlbedoImages(ModelRoot model)
        {
            var byImage = new Dictionary<int, List<string>>();
            foreach (var mat in model.LogicalMaterials)
            {
                var ch = mat.FindChannel("BaseColor");
                if (!ch.HasValue) continue;
                var img = ch.Value.Texture?.PrimaryImage;
                if (img == null) continue;

                if (!byImage.TryGetValue(img.LogicalIndex, out var names))
                    byImage[img.LogicalIndex] = names = new List<string>();
                names.Add(mat.Name ?? $"material_{mat.LogicalIndex}");
            }

            return byImage
                .Select(kv => new AlbedoTarget { ImageIndex = kv.Key, Label = string.Join(", ", kv.Value) })
                .OrderBy(t => t.ImageIndex)
                .ToList();
        }

        // Writes a computed result onto the model.
        public static void Apply(ModelRoot model, int imageIndex, byte[] pngBytes) =>
            model.LogicalImages[imageIndex].Content = new SharpGLTF.Memory.MemoryImage(pngBytes);

        public static byte[] SnapshotContent(ModelRoot model, int imageIndex) =>
            model.LogicalImages[imageIndex].Content.Content.ToArray();

        // --- geometry gathering / rasterization -------------------------------------------------

        // Internal rather than private: WatertightRepair reuses this UV-footprint rasterization to
        // find unused space in the same atlas for texturing hole-cap triangles.
        internal readonly struct Tri3
        {
            public readonly Vector3 P0, P1, P2;
            public readonly Vector2 Uv0, Uv1, Uv2; // already in pixel space

            public Tri3(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2)
            {
                P0 = p0; P1 = p1; P2 = p2;
                Uv0 = uv0; Uv1 = uv1; Uv2 = uv2;
            }
        }

        // Every triangle, from every primitive whose material binds this image as BaseColor, in
        // that primitive's own local (unskinned bind-pose) space - texel adjacency is a property
        // of the surface itself, not of wherever a particular node instance happens to place it.
        internal static List<Tri3> GatherTriangles(ModelRoot model, int imageIndex, int width, int height)
        {
            var tris = new List<Tri3>();

            foreach (var mat in model.LogicalMaterials)
            {
                var ch = mat.FindChannel("BaseColor");
                if (!ch.HasValue) continue;
                var img = ch.Value.Texture?.PrimaryImage;
                if (img == null || img.LogicalIndex != imageIndex) continue;

                string uvName = $"TEXCOORD_{ch.Value.TextureCoordinate}";

                for (int meshIdx = 0; meshIdx < model.LogicalMeshes.Count; meshIdx++)
                {
                    var mesh = model.LogicalMeshes[meshIdx];
                    for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
                    {
                        var prim = mesh.Primitives[primIdx];
                        if (prim.Material?.LogicalIndex != mat.LogicalIndex) continue;
                        if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES) continue;
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue(uvName, out var uvAcc)) continue;

                        var positions = posAcc.AsVector3Array();
                        var uvs = uvAcc.AsVector2Array();

                        foreach (var (a, b, c) in prim.GetTriangleIndices())
                        {
                            tris.Add(new Tri3(
                                positions[a], positions[b], positions[c],
                                new Vector2(uvs[a].X * width, uvs[a].Y * height),
                                new Vector2(uvs[b].X * width, uvs[b].Y * height),
                                new Vector2(uvs[c].X * width, uvs[c].Y * height)));
                        }
                    }
                }
            }

            return tris;
        }

        // Fills positions/covered/texelSize for every texel inside some triangle's UV footprint.
        internal static void RasterizeTriangles(List<Tri3> tris, int width, int height,
            Vector3[] positions, bool[] covered, float[] texelSize)
        {
            foreach (var t in tris)
            {
                float minX = MathF.Min(t.Uv0.X, MathF.Min(t.Uv1.X, t.Uv2.X));
                float maxX = MathF.Max(t.Uv0.X, MathF.Max(t.Uv1.X, t.Uv2.X));
                float minY = MathF.Min(t.Uv0.Y, MathF.Min(t.Uv1.Y, t.Uv2.Y));
                float maxY = MathF.Max(t.Uv0.Y, MathF.Max(t.Uv1.Y, t.Uv2.Y));

                int x0 = Math.Max(0, (int)MathF.Floor(minX));
                int x1 = Math.Min(width - 1, (int)MathF.Ceiling(maxX));
                int y0 = Math.Max(0, (int)MathF.Floor(minY));
                int y1 = Math.Min(height - 1, (int)MathF.Ceiling(maxY));
                if (x1 < x0 || y1 < y0) continue;

                float pixelAreaX2 = Cross2D(t.Uv1 - t.Uv0, t.Uv2 - t.Uv0);
                if (MathF.Abs(pixelAreaX2) < 1e-6f) continue; // degenerate in UV space (zero footprint)

                float worldAreaX2 = Vector3.Cross(t.P1 - t.P0, t.P2 - t.P0).Length();
                float localTexelSize = worldAreaX2 <= 1e-12f ? 0f : MathF.Sqrt(worldAreaX2 / MathF.Abs(pixelAreaX2));

                for (int py = y0; py <= y1; py++)
                {
                    for (int px = x0; px <= x1; px++)
                    {
                        var p = new Vector2(px + 0.5f, py + 0.5f);

                        float w0 = Cross2D(t.Uv2 - t.Uv1, p - t.Uv1);
                        float w1 = Cross2D(t.Uv0 - t.Uv2, p - t.Uv2);
                        float w2 = Cross2D(t.Uv1 - t.Uv0, p - t.Uv0);

                        bool inside = pixelAreaX2 > 0
                            ? (w0 >= 0 && w1 >= 0 && w2 >= 0)
                            : (w0 <= 0 && w1 <= 0 && w2 <= 0);
                        if (!inside) continue;

                        float b0 = w0 / pixelAreaX2, b1 = w1 / pixelAreaX2, b2 = w2 / pixelAreaX2;
                        int idx = py * width + px;
                        positions[idx] = t.P0 * b0 + t.P1 * b1 + t.P2 * b2;
                        covered[idx] = true;
                        texelSize[idx] = localTexelSize;
                    }
                }
            }
        }

        private static float Cross2D(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

        // --- bitmap plumbing ---------------------------------------------------------------------


        internal static Bitmap LoadBitmap(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var raw = new Bitmap(ms);
            return raw.Clone(new Rectangle(0, 0, raw.Width, raw.Height), PixelFormat.Format32bppArgb);
        }

        // BGRA byte order per pixel, matching Format32bppArgb's in-memory layout.
        internal static byte[] ReadPixels(Bitmap bmp)
        {
            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[bmp.Width * bmp.Height * 4];
                int rowBytes = bmp.Width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                }
                else
                {
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * rowBytes, rowBytes);
                }
                return bytes;
            }
            finally { bmp.UnlockBits(data); }
        }

        internal static Bitmap WritePixels(byte[] pixels, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = width * 4;
                if (data.Stride == rowBytes)
                {
                    Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < height; y++)
                        Marshal.Copy(pixels, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
            }
            finally { bmp.UnlockBits(data); }
            return bmp;
        }

        // Always re-encoded as PNG regardless of the source format, so a re-run never compounds
        // JPEG block artifacts along the very edges this is trying to clean up.
        internal static byte[] EncodePng(Bitmap bmp)
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }
}
