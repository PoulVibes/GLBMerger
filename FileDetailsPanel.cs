using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // What Save would write right now: the file's size, its vertex and triangle totals, and the
    // resolution of every texture in it. Everything is measured from the file as it would be
    // written - every mesh and image the model still carries, drawn or not - so a model whose
    // unused geometry wasn't cleaned up shows the bytes that cleanup would have reclaimed.
    internal sealed class FileDetailsPanel : GroupBox
    {
        private readonly Label _size = new(), _vertices = new(), _triangles = new(), _textures = new();
        private readonly ToolTip _tip = new() { AutoPopDelay = 30000, ShowAlways = true };

        public FileDetailsPanel()
        {
            Text = "File details (on Save)";
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(6, 2, 6, 4);
            Margin = new Padding(8, 0, 0, 0);

            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (var (name, value) in new[] { ("File size:", _size), ("Vertices:", _vertices), ("Triangles:", _triangles), ("Textures:", _textures) })
            {
                grid.Controls.Add(new Label { Text = name, AutoSize = true, Margin = new Padding(0, 1, 6, 1) });
                value.AutoSize = true;
                value.Margin = new Padding(0, 1, 0, 1);
                // A model with many textures would otherwise widen the toolbar off the screen.
                value.MaximumSize = new System.Drawing.Size(280, 0);
                grid.Controls.Add(value);
            }
            Controls.Add(grid);

            ShowModel(null);
            Disposed += (s, e) => _tip.Dispose();
        }

        // null clears the panel, with `why` in place of the size (nothing merged yet, or the
        // merge is out of date and Save is off until Process Merge runs again).
        public void ShowModel(ModelRoot? model, string why = "nothing to save yet")
        {
            _tip.SetToolTip(_textures, null);
            if (model == null)
            {
                _size.Text = why;
                _vertices.Text = _triangles.Text = _textures.Text = "-";
                return;
            }

            var details = Measure(model);
            _size.Text = details.Bytes is long bytes ? FormatBytes(bytes) : "can't be saved: " + details.Error;
            _vertices.Text = details.Vertices.ToString("N0");
            _triangles.Text = details.Triangles.ToString("N0");

            if (details.Textures.Count == 0)
            {
                _textures.Text = "none";
                return;
            }
            // Same-sized textures are counted together; the tooltip names each one.
            var groups = details.Textures
                .GroupBy(t => t.Resolution)
                .OrderByDescending(g => g.First().Pixels)
                .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key);
            _textures.Text = $"{details.Textures.Count}: {string.Join(", ", groups)}";
            _tip.SetToolTip(_textures, string.Join(Environment.NewLine,
                details.Textures.Select(t => $"{t.Name}: {t.Resolution} {t.Format}, {FormatBytes(t.Bytes)}")));
        }

        private sealed record TextureInfo(string Name, string Resolution, long Pixels, string Format, long Bytes);

        private sealed record Details(long? Bytes, string? Error, int Vertices, int Triangles, List<TextureInfo> Textures);

        private static Details Measure(ModelRoot model)
        {
            // The size is what SaveGLB would write, so it's measured by writing it - padding, JSON
            // and embedded images included - into a stream that only counts.
            long? bytes = null;
            string? error = null;
            try
            {
                using var counter = new CountingStream();
                model.WriteGLB(counter);
                bytes = counter.Position;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            // Primitives can share one vertex buffer, so each POSITION accessor is counted once.
            var positions = new HashSet<Accessor>();
            int triangles = 0;
            foreach (var prim in model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (prim.VertexAccessors.TryGetValue("POSITION", out var pos)) positions.Add(pos);
                if (prim.DrawPrimitiveType is PrimitiveType.TRIANGLES or PrimitiveType.TRIANGLE_STRIP or PrimitiveType.TRIANGLE_FAN)
                    triangles += prim.GetTriangleIndices().Count();
            }

            var textures = model.LogicalImages.Select(img =>
            {
                var content = img.Content;
                var size = ReadSize(content.Content.Span);
                string name = !string.IsNullOrEmpty(img.Name) ? img.Name : $"image {img.LogicalIndex}";
                string format = content.IsValid ? content.FileExtension.TrimStart('.').ToUpperInvariant() : "?";
                return size is var (w, h)
                    ? new TextureInfo(name, $"{w}x{h}", (long)w * h, format, content.Content.Length)
                    : new TextureInfo(name, "unknown size", 0, format, content.Content.Length);
            }).ToList();

            return new Details(bytes, error, positions.Sum(a => a.Count), triangles, textures);
        }

        // Width and height straight from the image header, for the formats glTF embeds - decoding
        // whole textures just to measure them would be far slower than the rest of this put together.
        private static (int W, int H)? ReadSize(ReadOnlySpan<byte> b)
        {
            static int Be32(ReadOnlySpan<byte> s, int i) => (s[i] << 24) | (s[i + 1] << 16) | (s[i + 2] << 8) | s[i + 3];
            static int Be16(ReadOnlySpan<byte> s, int i) => (s[i] << 8) | s[i + 1];
            static int Le32(ReadOnlySpan<byte> s, int i) => s[i] | (s[i + 1] << 8) | (s[i + 2] << 16) | (s[i + 3] << 24);
            static int Le24(ReadOnlySpan<byte> s, int i) => s[i] | (s[i + 1] << 8) | (s[i + 2] << 16);

            // PNG: the IHDR chunk always comes first.
            if (b.Length >= 24 && b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
                return (Be32(b, 16), Be32(b, 20));

            // JPEG: walk the markers to the first start-of-frame (C0-CF, less DHT/JPG/DAC).
            if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    if (marker == 0xFF) { i++; continue; }
                    if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                        return (Be16(b, i + 7), Be16(b, i + 5));
                    if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                    i += 2 + Be16(b, i + 2);
                }
                return null;
            }

            // WebP: lossy (VP8), lossless (VP8L) and extended (VP8X) headers each store it differently.
            if (b.Length >= 30 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P')
            {
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8')
                {
                    if (b[15] == ' ') return (Le32(b, 26) & 0x3FFF, (Le32(b, 26) >> 16) & 0x3FFF);
                    if (b[15] == 'L') { int v = Le32(b, 21); return ((v & 0x3FFF) + 1, ((v >> 14) & 0x3FFF) + 1); }
                    if (b[15] == 'X') return (Le24(b, 24) + 1, Le24(b, 27) + 1);
                }
                return null;
            }

            // KTX2: fixed header after the 12-byte identifier.
            if (b.Length >= 28 && b[0] == 0xAB && b[1] == 'K' && b[2] == 'T' && b[3] == 'X' && b[5] == '2')
                return (Le32(b, 20), Le32(b, 24));

            // DDS: "DDS " then a header with height before width.
            if (b.Length >= 20 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S' && b[3] == ' ')
                return (Le32(b, 16), Le32(b, 12));

            return null;
        }

        internal static string FormatBytes(long bytes) => bytes switch
        {
            >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
            _ => $"{bytes:N0} bytes",
        };

        // Discards what is written and keeps only the count.
        private sealed class CountingStream : Stream
        {
            private long _length;
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => _length;
            public override long Position { get => _length; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) => _length += count;
            public override void Write(ReadOnlySpan<byte> buffer) => _length += buffer.Length;
            public override void WriteByte(byte value) => _length++;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
