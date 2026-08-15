using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Replaces dark outlines/cracks on albedo textures - see TextureLineThinner for the algorithm
    // and why the fill-color lookup runs in 3D against the mesh surface rather than in the raw 2D
    // pixel grid. Optionally restricted to a painted region of the mesh, painted right here in the
    // "Before" 3D pane using the same brush-and-triangle-bounding-sphere approach as
    // GeometryOptimizerEditor's paint tool (see that file's paintAllInBrush for the fuller
    // rationale - brush radius vs triangle bounding sphere, facing-the-camera-only, no
    // connectivity requirement so it reaches across gaps).
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window.
    public class TextureEditorEditor : UserControl
    {
        private readonly ModelRoot _model;

        private ComboBox _targetDropdown = null!;
        private NumericUpDown _numThreshold = null!, _numGutter = null!, _numFillMargin = null!;
        private Button _btnPreview = null!, _btnApply = null!, _btnApplyAll = null!, _btnRevert = null!;
        private CheckBox _chkDiagnostic = null!;
        private Label _lblStatus = null!, _lblBefore = null!, _lblAfter = null!;
        private PictureBox _picBefore = null!, _picAfter = null!;
        private WebView2 _webView = null!;

        private CheckBox _chkPaintMode = null!;
        private TrackBar _sliderBrush = null!;
        private Label _lblBrush = null!, _lblSelection = null!;
        private Button _btnClearSelection = null!;

        private CheckBox _chkFavorColor = null!;
        private Panel _favorSwatch = null!;
        private Button _btnEyedropFavorColor = null!, _btnChooseFavorColor = null!;
        private NumericUpDown _numFavorTolerance = null!;
        private Color _favorColor = Color.White;
        private bool _eyedropping;

        private List<TextureLineThinner.AlbedoTarget> _targets = new();

        // Original bytes for whichever images Apply has touched this session, so Revert can put
        // them back - the model is shared with every other editor mode, same pattern as
        // GeometryOptimizerEditor's _originalIndices.
        private readonly Dictionary<int, byte[]> _originalContent = new();

        private byte[]? _lastPreviewBytes;
        private int _lastPreviewImageIndex = -1;

        // The 3D quadrant shows the SAME committed model twice, diverging only by whichever
        // texture a Preview/Apply just touched - each temp GLB is versioned and the previous one
        // deleted on the next write, same lifecycle as GeometryOptimizerEditor's preview file.
        private int _glbVersion;
        private string? _beforeGlbPath;
        private string? _afterGlbPath;
        private string _currentBeforeFileName = "";
        private bool _viewerReady;

        public TextureEditorEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            BuildUi();
            RefreshTargets();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 340, AutoScroll = true };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };

            flow.Controls.Add(new Label
            {
                Text = "Replace Dark Lines (albedo only)",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            flow.Controls.Add(new Label { Text = "Texture:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
            _targetDropdown = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 8) };
            _targetDropdown.SelectedIndexChanged += (s, e) => ShowSelectedOriginal();
            flow.Controls.Add(_targetDropdown);

            flow.Controls.Add(HelpText(
                "Only images bound as a material's base color (albedo) show up here - normal, " +
                "metallic/roughness, occlusion and emissive maps encode surface properties rather " +
                "than paint and are left alone."));

            var thresholdRow = LabeledNumeric("Darkness threshold (0-255):", out _numThreshold, 0, 255, 40, 0);
            flow.Controls.Add(thresholdRow);
            flow.Controls.Add(HelpText("A pixel this bright or darker gets replaced outright."));

            var fillMarginRow = LabeledNumeric("Fill color confidence margin:", out _numFillMargin, 0, 255, 40, 0);
            flow.Controls.Add(fillMarginRow);
            flow.Controls.Add(HelpText(
                "Fill color is only ever pulled from a texel this much brighter than the darkness " +
                "threshold - not just anything one shade lighter. Without this, the nearest \"non-" +
                "line\" pixel is almost always the anti-aliased edge right next to the line, and " +
                "recoloring with that muddy transition shade reads as smudged rather than " +
                "genuinely replaced."));

            var gutterRow = LabeledNumeric("Gutter/seam padding (texels):", out _numGutter, 0, 32, 8, 0);
            flow.Controls.Add(gutterRow);
            flow.Controls.Add(HelpText(
                "Tightly packed or unpadded UV islands often leave a gap with no triangle mapped " +
                "to it at all, which reads as its own dark seam network between every facet, " +
                "separate from any painted line art. Raising this carries mapped color into those " +
                "gaps before the replace runs, so seams like that are reachable too."));

            flow.Controls.Add(new Label
            {
                Text = "Favor a color (optional)",
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 4),
            });

            _chkFavorColor = new CheckBox
            {
                Text = "Prefer this color as the fill source when a line sits between two colors",
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            flow.Controls.Add(_chkFavorColor);

            var favorRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            _favorSwatch = new Panel
            {
                Width = 28, Height = 24, BorderStyle = BorderStyle.FixedSingle,
                BackColor = _favorColor, Margin = new Padding(3, 3, 8, 3),
            };
            favorRow.Controls.Add(_favorSwatch);
            _btnEyedropFavorColor = MakeButton("Pick From Before Texture");
            _btnEyedropFavorColor.MinimumSize = new Size(180, 0);
            _btnEyedropFavorColor.Click += (s, e) => ToggleEyedropper();
            favorRow.Controls.Add(_btnEyedropFavorColor);
            flow.Controls.Add(favorRow);

            _btnChooseFavorColor = MakeButton("Choose Color...");
            _btnChooseFavorColor.Click += (s, e) => ChooseFavorColorManually();
            flow.Controls.Add(_btnChooseFavorColor);

            var favorToleranceRow = LabeledNumeric("Match tolerance (per channel):", out _numFavorTolerance, 0, 255, 40, 0);
            flow.Controls.Add(favorToleranceRow);
            flow.Controls.Add(HelpText(
                "Fill color is pulled from the nearest texel matching this color (within " +
                "tolerance) first - only falling back to the ordinary nearest-neighbor search " +
                "where nothing matching is reachable, so a favored color that doesn't run along a " +
                "particular stretch of line still gets replaced rather than skipped."));

            flow.Controls.Add(new Label
            {
                Text = "Paint a region (optional)",
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 4),
            });

            _chkPaintMode = new CheckBox
            {
                Text = "Paint mode (drag on the Before 3D view; Ctrl or right-click to erase)",
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkPaintMode.CheckedChanged += (s, e) => PushPaintMode();
            flow.Controls.Add(_chkPaintMode);

            _lblBrush = new Label { Text = "Brush size: 5%", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderBrush = new TrackBar
            {
                Width = 300, Height = 45, Minimum = 1, Maximum = 40, Value = 5,
                TickFrequency = 5, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderBrush.ValueChanged += (s, e) => { _lblBrush.Text = $"Brush size: {_sliderBrush.Value}%"; PushBrushRadius(); };
            flow.Controls.Add(_lblBrush);
            flow.Controls.Add(_sliderBrush);

            _btnClearSelection = MakeButton("Clear Selection");
            _btnClearSelection.Click += (s, e) => ClearSelection();
            flow.Controls.Add(_btnClearSelection);

            _lblSelection = new Label { Text = "0 triangles painted", AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
            flow.Controls.Add(_lblSelection);

            flow.Controls.Add(HelpText(
                "Restricts BOTH which pixels count as \"line\" and which pixels are eligible as a " +
                "fill source to the painted triangles - a texel can only pick up color from " +
                "somewhere inside the painted patch, never from an unrelated part of the texture " +
                "atlas. Leave nothing painted to process the whole texture instead."));

            _chkDiagnostic = new CheckBox
            {
                Text = "Diagnostic view (magenta = outside painted/mapped area, red = line but " +
                    "no fill source, blue = replaced via favored color, green = replaced)",
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 4, 3, 8),
            };
            flow.Controls.Add(_chkDiagnostic);

            _btnPreview = MakeButton("Preview");
            _btnPreview.Click += async (s, e) => await RunAsync(apply: false);
            flow.Controls.Add(_btnPreview);

            _btnApply = MakeButton("Apply to This Texture");
            _btnApply.Click += async (s, e) => await RunAsync(apply: true);
            flow.Controls.Add(_btnApply);

            _btnApplyAll = MakeButton("Apply to All Albedo Textures");
            _btnApplyAll.Click += async (s, e) => await RunAllAsync();
            flow.Controls.Add(_btnApplyAll);

            _btnRevert = MakeButton("Revert This Texture");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertSelected();
            flow.Controls.Add(_btnRevert);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 8, 3, 4), ForeColor = Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            // Quad chart: the flat 2D texture on top (what actually changed), the mesh in its
            // default bind pose underneath (what it looks like painted onto the surface, at
            // whatever angle a seam or a UV stretch would otherwise hide) - same before/after split
            // in both rows so a claim made by one is checkable against the other. Painting only
            // happens in the bottom-left (Before) pane; After is a plain, non-interactive viewer.
            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            _lblBefore = new Label { Text = "2D Texture - Before", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            _lblAfter = new Label { Text = "2D Texture - After", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            _picBefore = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
            _picBefore.MouseClick += (s, e) =>
            {
                if (_eyedropping) PickFavorColorFromBefore(e.Location);
            };
            _picAfter = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };

            previewPanel.Controls.Add(_lblBefore, 0, 0);
            previewPanel.Controls.Add(_lblAfter, 1, 0);
            previewPanel.Controls.Add(_picBefore, 0, 1);
            previewPanel.Controls.Add(_picAfter, 1, 1);

            var lbl3D = new Label
            {
                Text = "3D Model - Bind Pose (paint on Before)", AutoSize = true,
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            };
            previewPanel.Controls.Add(lbl3D, 0, 2);
            previewPanel.SetColumnSpan(lbl3D, 2);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            previewPanel.Controls.Add(_webView, 0, 3);
            previewPanel.SetColumnSpan(_webView, 2);

            Controls.Add(previewPanel);
            Controls.Add(controlPanel);
        }

        private static FlowLayoutPanel LabeledNumeric(string label, out NumericUpDown numeric,
            decimal min, decimal max, decimal value, int decimals)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 2),
            };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            var num = new NumericUpDown
            {
                Width = 90, DecimalPlaces = decimals, Minimum = min, Maximum = max, Value = value,
                Increment = decimals > 0 ? 0.5m : 1m,
                Margin = new Padding(0, 4, 3, 3),
            };
            row.Controls.Add(num);
            numeric = num;
            return row;
        }

        private static Label HelpText(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(3, 0, 3, 10),
            ForeColor = Color.Gray,
        };

        private static Button MakeButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(300, 0),
            Margin = new Padding(3, 3, 3, 3),
        };

        private void RefreshTargets()
        {
            _targets = TextureLineThinner.FindAlbedoImages(_model);

            _targetDropdown.Items.Clear();
            foreach (var t in _targets)
                _targetDropdown.Items.Add($"Image {t.ImageIndex} ({t.Label})");

            if (_targets.Count == 0)
            {
                _lblStatus.Text = "No albedo (base color) textures found on this model.";
                SetControlsEnabled(false);
                return;
            }

            SetControlsEnabled(true);
            _targetDropdown.SelectedIndex = 0;
        }

        private void SetControlsEnabled(bool enabled)
        {
            _targetDropdown.Enabled = enabled;
            _numThreshold.Enabled = enabled;
            _numGutter.Enabled = enabled;
            _numFillMargin.Enabled = enabled;
            _chkFavorColor.Enabled = enabled;
            _btnEyedropFavorColor.Enabled = enabled;
            _btnChooseFavorColor.Enabled = enabled;
            _numFavorTolerance.Enabled = enabled;
            _chkPaintMode.Enabled = enabled;
            _sliderBrush.Enabled = enabled;
            _btnClearSelection.Enabled = enabled;
            _btnPreview.Enabled = enabled;
            _btnApply.Enabled = enabled;
            _btnApplyAll.Enabled = enabled;
        }

        private TextureLineThinner.Options CurrentOptions(
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? selection) => new()
        {
            Threshold = (byte)_numThreshold.Value,
            FillConfidenceMargin = (byte)_numFillMargin.Value,
            GutterPaddingTexels = (int)_numGutter.Value,
            TriangleSelection = selection,
            FavorColor = _chkFavorColor.Checked ? (_favorColor.R, _favorColor.G, _favorColor.B) : null,
            FavorColorTolerance = (byte)_numFavorTolerance.Value,
        };

        private void SetFavorColor(Color color)
        {
            _favorColor = color;
            _favorSwatch.BackColor = color;
            _chkFavorColor.Checked = true;
        }

        private void ChooseFavorColorManually()
        {
            using var dlg = new ColorDialog { Color = _favorColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) SetFavorColor(dlg.Color);
        }

        private void ToggleEyedropper()
        {
            _eyedropping = !_eyedropping;
            _btnEyedropFavorColor.Text = _eyedropping ? "Click a color on the texture..." : "Pick From Before Texture";
            _picBefore.Cursor = _eyedropping ? Cursors.Cross : Cursors.Default;
        }

        // Maps a client-coordinate click on the Before PictureBox (SizeMode.Zoom, so the image is
        // letterboxed to fit while preserving aspect ratio) back to the underlying texture's pixel
        // coordinates, then samples that pixel as the favored color.
        private void PickFavorColorFromBefore(Point clientPoint)
        {
            if (_picBefore.Image == null) return;

            var imgRect = ZoomImageRect(_picBefore);
            if (!imgRect.Contains(clientPoint)) return;

            float relX = (clientPoint.X - imgRect.X) / (float)imgRect.Width;
            float relY = (clientPoint.Y - imgRect.Y) / (float)imgRect.Height;
            int px = Math.Clamp((int)(relX * _picBefore.Image.Width), 0, _picBefore.Image.Width - 1);
            int py = Math.Clamp((int)(relY * _picBefore.Image.Height), 0, _picBefore.Image.Height - 1);

            using var bmp = new Bitmap(_picBefore.Image);
            SetFavorColor(bmp.GetPixel(px, py));

            _eyedropping = false;
            _btnEyedropFavorColor.Text = "Pick From Before Texture";
            _picBefore.Cursor = Cursors.Default;
        }

        private static Rectangle ZoomImageRect(PictureBox pb)
        {
            if (pb.Image == null || pb.Width <= 0 || pb.Height <= 0) return Rectangle.Empty;

            float imgAspect = (float)pb.Image.Width / pb.Image.Height;
            float boxAspect = (float)pb.Width / pb.Height;

            int w, h, x, y;
            if (imgAspect > boxAspect)
            {
                w = pb.Width;
                h = Math.Max(1, (int)(pb.Width / imgAspect));
                x = 0;
                y = (pb.Height - h) / 2;
            }
            else
            {
                h = pb.Height;
                w = Math.Max(1, (int)(pb.Height * imgAspect));
                y = 0;
                x = (pb.Width - w) / 2;
            }
            return new Rectangle(x, y, w, h);
        }

        private int SelectedImageIndex =>
            _targetDropdown.SelectedIndex >= 0 ? _targets[_targetDropdown.SelectedIndex].ImageIndex : -1;

        private void ShowSelectedOriginal()
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0) return;

            _picBefore.Image?.Dispose();
            _picBefore.Image = LoadImageSafe(TextureLineThinner.SnapshotContent(_model, imageIndex));
            _picAfter.Image?.Dispose();
            _picAfter.Image = null;
            _lastPreviewBytes = null;
            _lastPreviewImageIndex = -1;
            ResetAfterViewerToBefore();

            _btnRevert.Enabled = _originalContent.ContainsKey(imageIndex);
        }

        private static System.Drawing.Image? LoadImageSafe(byte[] bytes)
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                return System.Drawing.Image.FromStream(ms);
            }
            catch
            {
                return null;
            }
        }

        private async Task RunAsync(bool apply)
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0) return;

            SetBusy(true, apply ? "Applying..." : "Processing preview...");

            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            var options = CurrentOptions(selection);

            TextureLineThinner.Report report;
            byte[] pngBytes, diagnosticPngBytes;
            try
            {
                (report, pngBytes, diagnosticPngBytes) = await Task.Run(() => TextureLineThinner.Process(_model, imageIndex, options));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Texture processing failed: {ex.Message}",
                    "Replace Dark Lines", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            _picAfter.Image?.Dispose();
            _picAfter.Image = LoadImageSafe(_chkDiagnostic.Checked ? diagnosticPngBytes : pngBytes);
            _lastPreviewBytes = pngBytes;
            _lastPreviewImageIndex = imageIndex;
            // The 3D pane always renders the real recolor, even with the 2D diagnostic overlay on
            // - the diagnostic is a 2D debugging aid, not something worth seeing painted on a mesh.
            UpdateAfterViewer(imageIndex, pngBytes);

            string scope = selection != null && selection.Count > 0 ? "painted region" : "whole texture";
            if (!apply)
            {
                SetBusy(false, $"{report.TexelsReplaced:N0} of {report.TexelsLine:N0} line texels replaced " +
                    $"({report.TexelsCovered:N0} texels eligible, {scope}). Preview only - nothing changed yet.");
                return;
            }

            if (!_originalContent.ContainsKey(imageIndex))
                _originalContent[imageIndex] = TextureLineThinner.SnapshotContent(_model, imageIndex);

            TextureLineThinner.Apply(_model, imageIndex, pngBytes);
            _btnRevert.Enabled = true;
            RefreshBeforeViewer();   // 'before' now IS the applied result - collapses the diff back to zero

            SetBusy(false, $"Applied: {report.TexelsReplaced:N0} of {report.TexelsLine:N0} line texels replaced " +
                $"({scope}). Included the next time you save the merge.");
        }

        private async Task RunAllAsync()
        {
            if (_targets.Count == 0) return;

            SetBusy(true, $"Applying to {_targets.Count} texture(s)...");

            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            var options = CurrentOptions(selection);
            int totalReplaced = 0;

            try
            {
                foreach (var target in _targets)
                {
                    var (report, pngBytes, _) = await Task.Run(() => TextureLineThinner.Process(_model, target.ImageIndex, options));
                    if (IsDisposed) return;

                    if (!_originalContent.ContainsKey(target.ImageIndex))
                        _originalContent[target.ImageIndex] = TextureLineThinner.SnapshotContent(_model, target.ImageIndex);

                    TextureLineThinner.Apply(_model, target.ImageIndex, pngBytes);
                    totalReplaced += report.TexelsReplaced;

                    if (target.ImageIndex == SelectedImageIndex)
                    {
                        _picAfter.Image?.Dispose();
                        _picAfter.Image = LoadImageSafe(pngBytes);
                        _lastPreviewBytes = pngBytes;
                        _lastPreviewImageIndex = target.ImageIndex;
                    }
                }
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Texture processing failed: {ex.Message}",
                    "Replace Dark Lines", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _btnRevert.Enabled = _originalContent.ContainsKey(SelectedImageIndex);
            RefreshBeforeViewer();
            SetBusy(false, $"Applied to {_targets.Count} texture(s): {totalReplaced:N0} texels replaced in total. " +
                "Included the next time you save the merge.");
        }

        private void RevertSelected()
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0 || !_originalContent.TryGetValue(imageIndex, out var original)) return;

            TextureLineThinner.Apply(_model, imageIndex, original);
            _originalContent.Remove(imageIndex);
            _btnRevert.Enabled = false;

            ShowSelectedOriginal();
            RefreshBeforeViewer();
            _lblStatus.Text = "Texture reverted to the original.";
        }

        private void SetBusy(bool busy, string? status)
        {
            _btnPreview.Enabled = !busy;
            _btnApply.Enabled = !busy;
            _btnApplyAll.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (status != null) _lblStatus.Text = status;
        }

        // --- 3D quadrant / paint tool ------------------------------------------------------------

        private async Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-forget
            // startup may still be mid-await - both the await itself and everything after it have
            // to tolerate that (same guard GeometryOptimizerEditor uses).
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _currentBeforeFileName = WriteTaggedGlbFile(_model, ref _beforeGlbPath);

            string html = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #wrap { display: flex; width: 100%; height: 100%; }
                    .pane { flex: 1; position: relative; }
                    .pane:first-child { border-right: 1px solid #444; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; position: absolute; top: 0; left: 0; }
                    #overlayCanvas { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; }
                    .paneLabel {
                        position: absolute; top: 6px; left: 8px; color: #ddd; font: 12px sans-serif;
                        background: rgba(0, 0, 0, 0.45); padding: 2px 6px; border-radius: 3px; pointer-events: none;
                        z-index: 2;
                    }
                </style>
            </head>
            <body>
                <div id='wrap'>
                    <div class='pane' id='panebefore'>
                        <div class='paneLabel'>Before (paint here)</div>
                        <model-viewer id='before' src='https://appassets.local/" + _currentBeforeFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                        <canvas id='overlayCanvas'></canvas>
                    </div>
                    <div class='pane'>
                        <div class='paneLabel'>After</div>
                        <model-viewer id='after' src='https://appassets.local/" + _currentBeforeFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                    </div>
                </div>
                <script>
                    // No 'autoplay' on either <model-viewer>, so both stay on the glTF's default
                    // bind pose - never advance into an animation clip's first frame.
                    window.setBeforeSrc = function (url) { document.getElementById('before').src = url; };
                    window.setAfterSrc = function (url) { document.getElementById('after').src = url; };

                    // Forcing OPAQUE is what stops the far side of the model showing through the
                    // near side when it's spun - the exact same fix GeometryOptimizerEditor's
                    // preview needed (see the 'load' handler there for the full explanation). Root
                    // cause: assets exported with alphaMode BLEND but no actual translucency
                    // (alpha 1.0 - common out of Meshy/Blender) land in the renderer's transparent
                    // pass, which draws with depth writes OFF, so nothing occludes anything and
                    // every surface behind the near one bleeds through as it rotates. This bit
                    // everyone twice already (once here, once there) purely because each preview's
                    // <model-viewer> setup was written independently - if a THIRD preview gets
                    // added anywhere in this app, it needs this same 'load' handler, unconditionally,
                    // not just when alpha looks like it's exactly 1.0 (the alpha may live in the
                    // base-colour texture, which isn't readable from here).
                    function fixMaterials(viewer) {
                        (viewer.model.materials || []).forEach(function (mat) {
                            mat.setAlphaMode('OPAQUE');
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });
                    }

                    var beforeViewer = document.getElementById('before');
                    var afterViewer = document.getElementById('after');
                    var panebefore = document.getElementById('panebefore');
                    afterViewer.addEventListener('load', function (e) { fixMaterials(e.target); });

                    // --- Paint-a-region tool (Before pane only) ---------------------------------
                    // Ported from GeometryOptimizerEditor's paint tool - see paintAllInBrush there
                    // for the fuller rationale on bounding-sphere overlap, facing-only, and no
                    // connectivity requirement. Trimmed down here: one selection set (no invert/
                    // restrict-vs-exclude split, no wireframe), since a painted region here is
                    // simply 'the area to operate on', full stop.
                    var paintableMeshes = [];      // { meshIndex, primIndex, object, centroids, radii, normals }
                    var selection = {};             // 'meshIndex_primIndex' -> Set<triangleIndex>
                    var paintMode = false;
                    var painting = false;
                    var brushFraction = 0.05;
                    var brushRadius = 0.05;
                    var modelMaxDim = 1;

                    function resolveBrushRadius() { brushRadius = brushFraction * (modelMaxDim || 1); }

                    var overlayCanvas = document.getElementById('overlayCanvas');
                    var overlayRenderer = new THREE.WebGLRenderer({ canvas: overlayCanvas, alpha: true, antialias: true });
                    overlayRenderer.setPixelRatio(window.devicePixelRatio);
                    var overlayScene = new THREE.Scene();
                    var overlayCamera = new THREE.PerspectiveCamera(45, 1, 0.01, 10000);
                    var overlayGroup = new THREE.Group();
                    overlayScene.add(overlayGroup);
                    var highlightMaterial = new THREE.MeshBasicMaterial({
                        color: 0xffee00, transparent: true, opacity: 0.5, depthTest: true,
                        side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
                        skinning: true,
                    });

                    function paneSize() {
                        return { w: panebefore.clientWidth || 1, h: panebefore.clientHeight || 1 };
                    }

                    // Building the highlight/mask/cursor as plain THREE.Mesh copies of the raw
                    // geometry - even with correct SELECTION math elsewhere - still renders the
                    // BIND pose: a plain Mesh has no skeleton to blend against, so 'skinning: true'
                    // on the material alone does nothing without this. Sharing the ORIGINAL mesh's
                    // skeleton (not cloning it) is what lets THREE's own renderer GPU-skin these the
                    // same way it skins the real, visible mesh - same bones, same current pose,
                    // automatically kept in sync with no per-frame work of our own.
                    function makeSkinnedCopy(src, geometry, material) {
                        var mesh;
                        if (src.isSkinnedMesh) {
                            mesh = new THREE.SkinnedMesh(geometry, material);
                            mesh.bind(src.skeleton, src.bindMatrix);
                        } else {
                            mesh = new THREE.Mesh(geometry, material);
                        }
                        mesh.matrixAutoUpdate = false;
                        mesh.matrix.copy(src.matrixWorld);
                        // A skinned mesh's bounding sphere (used for frustum culling) is computed
                        // from the RAW, bind-pose geometry and never updated for the posed result -
                        // on a model whose skeleton has been significantly re-posed (exactly the
                        // ModelAdjusterEditor case this whole fix is about), that stale bound can
                        // culled the mesh out of view even though it's plainly on-screen.
                        mesh.frustumCulled = false;
                        return mesh;
                    }

                    var depthMaskMaterial = new THREE.MeshBasicMaterial({
                        colorWrite: false, side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
                        skinning: true,
                    });
                    var depthMaskGroup = new THREE.Group();
                    overlayScene.add(depthMaskGroup);

                    function rebuildDepthMask() {
                        for (var i = depthMaskGroup.children.length - 1; i >= 0; i--) {
                            depthMaskGroup.remove(depthMaskGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var maskMesh = makeSkinnedCopy(meshInfo.object, meshInfo.object.geometry, depthMaskMaterial);
                            depthMaskGroup.add(maskMesh);
                        });
                    }

                    // A custom ShaderMaterial doesn't get skinning for free just from
                    // 'skinning: true' the way THREE's built-in materials do - that flag only turns
                    // on the uniform wiring (bindMatrix/bindMatrixInverse/boneTexture etc, supplied
                    // by the renderer each frame); the shader chunks that actually USE those
                    // uniforms to deform 'position' have to be included explicitly, which is what
                    // <skinning_pars_vertex>/<skinbase_vertex>/<skinning_vertex> below do.
                    var brushDecalMaterial = new THREE.ShaderMaterial({
                        uniforms: {
                            uCenter: { value: new THREE.Vector3() },
                            uRadius: { value: 1 },
                            uColor: { value: new THREE.Color(0xffee00) },
                        },
                        vertexShader: [
                            '#include <common>',
                            '#include <skinning_pars_vertex>',
                            'varying vec3 vWorld;',
                            'void main() {',
                            '    #include <begin_vertex>',
                            '    #include <beginnormal_vertex>',
                            '    #include <skinbase_vertex>',
                            '    #include <skinning_vertex>',
                            '    vec4 world = modelMatrix * vec4(transformed, 1.0);',
                            '    vWorld = world.xyz;',
                            '    gl_Position = projectionMatrix * viewMatrix * world;',
                            '}',
                        ].join('\n'),
                        fragmentShader: [
                            'uniform vec3 uCenter;',
                            'uniform float uRadius;',
                            'uniform vec3 uColor;',
                            'varying vec3 vWorld;',
                            'void main() {',
                            '    float d = distance(vWorld, uCenter);',
                            '    if (d > uRadius) discard;',
                            '    float rim = smoothstep(uRadius * 0.82, uRadius, d);',
                            '    gl_FragColor = vec4(uColor, mix(0.32, 0.85, rim));',
                            '}',
                        ].join('\n'),
                        transparent: true,
                        depthTest: true,
                        depthWrite: false,
                        side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4,
                        skinning: true,
                    });
                    var brushDecalGroup = new THREE.Group();
                    brushDecalGroup.visible = false;
                    overlayScene.add(brushDecalGroup);

                    function rebuildBrushDecal() {
                        for (var i = brushDecalGroup.children.length - 1; i >= 0; i--) {
                            brushDecalGroup.remove(brushDecalGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var decal = makeSkinnedCopy(meshInfo.object, meshInfo.object.geometry, brushDecalMaterial);
                            brushDecalGroup.add(decal);
                        });
                    }

                    function showBrushCursor(point) {
                        brushDecalMaterial.uniforms.uCenter.value.copy(point);
                        brushDecalMaterial.uniforms.uRadius.value = brushRadius;
                        brushDecalGroup.visible = true;
                    }

                    function hideBrushCursor() { brushDecalGroup.visible = false; }

                    function syncOverlayCamera() {
                        if (typeof beforeViewer.getCameraOrbit !== 'function') return;
                        var size = paneSize();
                        overlayRenderer.setSize(size.w, size.h);
                        var orbit = beforeViewer.getCameraOrbit();
                        var target = beforeViewer.getCameraTarget();
                        overlayCamera.fov = beforeViewer.getFieldOfView();
                        overlayCamera.aspect = size.w / size.h;
                        var span = modelMaxDim * 2;
                        overlayCamera.near = Math.max(orbit.radius - span, modelMaxDim / 1000);
                        overlayCamera.far = orbit.radius + span;
                        overlayCamera.updateProjectionMatrix();
                        var x = target.x + orbit.radius * Math.sin(orbit.phi) * Math.sin(orbit.theta);
                        var y = target.y + orbit.radius * Math.cos(orbit.phi);
                        var z = target.z + orbit.radius * Math.sin(orbit.phi) * Math.cos(orbit.theta);
                        overlayCamera.position.set(x, y, z);
                        overlayCamera.lookAt(target.x, target.y, target.z);
                    }

                    function renderOverlayLoop() {
                        requestAnimationFrame(renderOverlayLoop);
                        syncOverlayCamera();
                        overlayRenderer.render(overlayScene, overlayCamera);
                    }
                    renderOverlayLoop();

                    // Scratch objects for skinnedVertex, reused across every vertex/triangle rather
                    // than allocated per-call - this runs once per corner of every triangle in the
                    // model.
                    var _skinIndex = new THREE.Vector4();
                    var _skinWeight = new THREE.Vector4();
                    var _bindPos = new THREE.Vector3();
                    var _bindNrm = new THREE.Vector3();
                    var _boneMatrix = new THREE.Matrix4();
                    var _boneNormalMat = new THREE.Matrix3();
                    var _skinTmp = new THREE.Vector3();

                    // Resolves vertex `index`'s CURRENT posed position/normal (mesh-local space,
                    // same as a plain unskinned read - caller still applies mesh.matrixWorld
                    // afterward). Reimplements THREE.SkinnedMesh.boneTransform's position math by
                    // hand (and extends the same idea to normals, which that built-in doesn't
                    // cover) because the straightforward read - geometry.attributes.position
                    // fed straight through mesh.matrixWorld, no skinning applied at all - returns
                    // the BIND pose, not the current one. <model-viewer>'s real renderer runs full
                    // GPU skinning every frame and never shows this; this hand-rolled hit-test
                    // parse is never added to a renderer, so nothing skins it unless this does.
                    // Bind pose and current pose are the same file whenever nothing has re-posed
                    // the skeleton since export, which is why this went unnoticed until
                    // ModelAdjusterEditor's bone-length changes exposed it: that bakes new joint
                    // transforms into the node hierarchy without touching the skin's bind data, so
                    // the two poses diverge and the paint tool kept tracking the OLD, pre-adjustment
                    // shape.
                    function skinnedVertex(mesh, index, posAttr, nrmAttr, outPos, outNormal) {
                        _skinIndex.fromBufferAttribute(mesh.geometry.attributes.skinIndex, index);
                        _skinWeight.fromBufferAttribute(mesh.geometry.attributes.skinWeight, index);
                        _bindPos.fromBufferAttribute(posAttr, index).applyMatrix4(mesh.bindMatrix);
                        if (outNormal && nrmAttr) _bindNrm.fromBufferAttribute(nrmAttr, index).transformDirection(mesh.bindMatrix);

                        outPos.set(0, 0, 0);
                        if (outNormal) outNormal.set(0, 0, 0);

                        for (var i = 0; i < 4; i++) {
                            var weight = _skinWeight.getComponent(i);
                            if (weight === 0) continue;
                            var boneIndex = _skinIndex.getComponent(i);
                            _boneMatrix.multiplyMatrices(
                                mesh.skeleton.bones[boneIndex].matrixWorld,
                                mesh.skeleton.boneInverses[boneIndex]);
                            outPos.addScaledVector(_skinTmp.copy(_bindPos).applyMatrix4(_boneMatrix), weight);
                            if (outNormal && nrmAttr) {
                                _boneNormalMat.getNormalMatrix(_boneMatrix);
                                outNormal.addScaledVector(_skinTmp.copy(_bindNrm).applyMatrix3(_boneNormalMat), weight);
                            }
                        }
                        outPos.applyMatrix4(mesh.bindMatrixInverse);
                        if (outNormal && nrmAttr) { outNormal.transformDirection(mesh.bindMatrixInverse); }
                    }

                    function computeTriangleData(mesh) {
                        // Bones commonly live under a SEPARATE branch of the scene graph (an
                        // Armature sibling of the mesh, not a child of it), so mesh.updateMatrixWorld
                        // - which only walks ITS OWN descendants - can leave every bone's
                        // matrixWorld stale. loadHitTestGeometry updates the whole loaded scene once
                        // before this runs, which is what actually keeps this correct; the call here
                        // is just the mesh's own belt-and-braces (needed regardless, skinned or not).
                        mesh.updateMatrixWorld(true);
                        var index = mesh.geometry.index;
                        var pos = mesh.geometry.attributes.position;
                        var nrm = mesh.geometry.attributes.normal;
                        var isSkinned = !!(mesh.isSkinnedMesh && mesh.skeleton
                            && mesh.geometry.attributes.skinIndex && mesh.geometry.attributes.skinWeight);
                        var triCount = index.count / 3;
                        var centroids = new Array(triCount);
                        var radii = new Float32Array(triCount);
                        var normals = new Float32Array(triCount * 3);

                        var normalMatrix = new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld);
                        var a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3();
                        var na = new THREE.Vector3(), nb = new THREE.Vector3(), nc = new THREE.Vector3();
                        var n = new THREE.Vector3(), tmp = new THREE.Vector3(), edge = new THREE.Vector3();

                        for (var t = 0; t < triCount; t++) {
                            var ia = index.getX(t * 3), ib = index.getX(t * 3 + 1), ic = index.getX(t * 3 + 2);

                            if (isSkinned) {
                                skinnedVertex(mesh, ia, pos, nrm, a, nrm ? na : null);
                                skinnedVertex(mesh, ib, pos, nrm, b, nrm ? nb : null);
                                skinnedVertex(mesh, ic, pos, nrm, c, nrm ? nc : null);
                                a.applyMatrix4(mesh.matrixWorld);
                                b.applyMatrix4(mesh.matrixWorld);
                                c.applyMatrix4(mesh.matrixWorld);
                                if (nrm) {
                                    na.transformDirection(mesh.matrixWorld);
                                    nb.transformDirection(mesh.matrixWorld);
                                    nc.transformDirection(mesh.matrixWorld);
                                }
                            } else {
                                a.fromBufferAttribute(pos, ia).applyMatrix4(mesh.matrixWorld);
                                b.fromBufferAttribute(pos, ib).applyMatrix4(mesh.matrixWorld);
                                c.fromBufferAttribute(pos, ic).applyMatrix4(mesh.matrixWorld);
                            }

                            var centroid = new THREE.Vector3().add(a).add(b).add(c).multiplyScalar(1 / 3);
                            centroids[t] = centroid;
                            radii[t] = Math.sqrt(Math.max(
                                centroid.distanceToSquared(a),
                                centroid.distanceToSquared(b),
                                centroid.distanceToSquared(c)));

                            if (nrm && isSkinned) {
                                n.set(0, 0, 0).add(na).add(nb).add(nc);
                            } else if (nrm) {
                                n.set(0, 0, 0);
                                n.add(tmp.fromBufferAttribute(nrm, ia));
                                n.add(tmp.fromBufferAttribute(nrm, ib));
                                n.add(tmp.fromBufferAttribute(nrm, ic));
                                n.applyMatrix3(normalMatrix);
                            } else {
                                n.subVectors(b, a).cross(edge.subVectors(c, a));
                            }
                            if (n.lengthSq() > 1e-20) n.normalize();
                            normals[t * 3] = n.x; normals[t * 3 + 1] = n.y; normals[t * 3 + 2] = n.z;
                        }
                        return { centroids: centroids, radii: radii, normals: normals };
                    }

                    function selectionKey(meshInfo) { return meshInfo.meshIndex + '_' + meshInfo.primIndex; }

                    function paintAllInBrush(point, erase) {
                        var camera = overlayCamera.position;
                        var changed = false;

                        for (var m = 0; m < paintableMeshes.length; m++) {
                            var meshInfo = paintableMeshes[m];
                            var centroids = meshInfo.centroids;
                            var radii = meshInfo.radii;
                            var normals = meshInfo.normals;
                            var key = selectionKey(meshInfo);
                            var set = selection[key];

                            for (var t = 0; t < centroids.length; t++) {
                                var centroid = centroids[t];
                                var reach = brushRadius + radii[t];
                                if (centroid.distanceToSquared(point) > reach * reach) continue;

                                var dot = normals[t * 3] * (centroid.x - camera.x)
                                    + normals[t * 3 + 1] * (centroid.y - camera.y)
                                    + normals[t * 3 + 2] * (centroid.z - camera.z);
                                if (dot > 0) continue;

                                if (erase) {
                                    if (set && set.delete(t)) changed = true;
                                } else {
                                    if (!set) { set = selection[key] = new Set(); }
                                    if (!set.has(t)) { set.add(t); changed = true; }
                                }
                            }
                        }
                        return changed;
                    }

                    function rebuildOverlay() {
                        for (var i = overlayGroup.children.length - 1; i >= 0; i--) {
                            overlayGroup.remove(overlayGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var set = selection[selectionKey(meshInfo)];
                            if (!set || set.size === 0) return;

                            var src = meshInfo.object;
                            var srcGeom = src.geometry;
                            var srcIndex = srcGeom.index;
                            var newIndex = new Uint32Array(set.size * 3);
                            var i = 0;
                            set.forEach(function (tri) {
                                newIndex[i * 3] = srcIndex.getX(tri * 3);
                                newIndex[i * 3 + 1] = srcIndex.getX(tri * 3 + 1);
                                newIndex[i * 3 + 2] = srcIndex.getX(tri * 3 + 2);
                                i++;
                            });

                            var overlayGeom = new THREE.BufferGeometry();
                            overlayGeom.setAttribute('position', srcGeom.attributes.position);
                            // skinIndex/skinWeight are what the vertex shader actually reads to
                            // blend bone matrices - without them a SkinnedMesh built on this
                            // geometry has nothing to skin WITH, and silently renders bind pose
                            // again despite everything else here being correct.
                            if (src.isSkinnedMesh) {
                                overlayGeom.setAttribute('skinIndex', srcGeom.attributes.skinIndex);
                                overlayGeom.setAttribute('skinWeight', srcGeom.attributes.skinWeight);
                            }
                            overlayGeom.setIndex(new THREE.BufferAttribute(newIndex, 1));
                            var overlayMesh = makeSkinnedCopy(src, overlayGeom, highlightMaterial);
                            overlayGroup.add(overlayMesh);
                        });
                    }

                    function pushSelectionCount() {
                        var total = 0;
                        Object.keys(selection).forEach(function (k) { total += selection[k].size; });
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'selectionChanged', count: total }));
                        }
                    }

                    window.setPaintMode = function (enabled) {
                        paintMode = enabled;
                        if (!enabled) { painting = false; hideBrushCursor(); }
                    };

                    window.setBrushRadius = function (fraction) {
                        brushFraction = fraction;
                        resolveBrushRadius();
                    };

                    window.clearPaintSelection = function () {
                        selection = {};
                        rebuildOverlay();
                        pushSelectionCount();
                    };

                    window.getPaintSelection = function () {
                        var result = {};
                        Object.keys(selection).forEach(function (k) { result[k] = Array.from(selection[k]); });
                        return result;
                    };

                    var lastOverlayUpdate = 0;

                    function pickSurface(event) {
                        if (typeof beforeViewer.positionAndNormalFromPoint !== 'function') return null;
                        var rect = beforeViewer.getBoundingClientRect();
                        return beforeViewer.positionAndNormalFromPoint(event.clientX - rect.left, event.clientY - rect.top);
                    }

                    function pickAndPaint(event) {
                        var hit = pickSurface(event);
                        if (!hit) { hideBrushCursor(); return false; }

                        var point = new THREE.Vector3(hit.position.x, hit.position.y, hit.position.z);
                        showBrushCursor(point);

                        var erase = event.ctrlKey || (event.buttons & 2) !== 0;
                        if (paintAllInBrush(point, erase)) {
                            var now = performance.now();
                            if (now - lastOverlayUpdate > 50) {
                                rebuildOverlay();
                                pushSelectionCount();
                                lastOverlayUpdate = now;
                            }
                        }
                        return true;
                    }

                    // See GeometryOptimizerEditor's identical listener for why this has to be a
                    // CAPTURE-phase stopPropagation rather than toggling cameraControls - toggling
                    // it mid-drag corrupts <model-viewer>'s internal SmoothControls pointer state
                    // permanently.
                    beforeViewer.addEventListener('pointerdown', function (event) {
                        if (!paintMode || event.button === 1) return;
                        if (pickAndPaint(event)) {
                            painting = true;
                            event.stopPropagation();
                            event.preventDefault();
                        }
                    }, true);
                    beforeViewer.addEventListener('pointermove', function (event) {
                        if (!paintMode) return;
                        if (painting) { pickAndPaint(event); return; }
                        var hit = pickSurface(event);
                        if (!hit) { hideBrushCursor(); return; }
                        showBrushCursor(new THREE.Vector3(hit.position.x, hit.position.y, hit.position.z));
                    });
                    beforeViewer.addEventListener('pointerleave', function () {
                        if (!painting) hideBrushCursor();
                    });
                    window.addEventListener('pointerup', function () {
                        if (painting) {
                            painting = false;
                            rebuildOverlay();
                            pushSelectionCount();
                        }
                    });
                    beforeViewer.addEventListener('contextmenu', function (event) {
                        if (paintMode) event.preventDefault();
                    });

                    var hitTestLoader = new THREE.GLTFLoader();
                    function loadHitTestGeometry(url) {
                        hitTestLoader.load(url, function (gltf) {
                            // Bones typically live under an Armature node - a SIBLING of the mesh
                            // in the scene graph, not a descendant of it - so computeTriangleData's
                            // own mesh.updateMatrixWorld(true) can never reach them (it only walks
                            // its own descendants). This scene has never been added to a renderer
                            // (which would otherwise do this automatically every frame), so without
                            // an explicit whole-scene update here every bone's matrixWorld is stuck
                            // at its construction-time default and skinnedVertex would compute
                            // garbage. One-time, before any mesh in this scene is processed.
                            gltf.scene.updateMatrixWorld(true);
                            var meshes = [];
                            gltf.scene.traverse(function (obj) {
                                if (!obj.isMesh || !obj.userData || obj.userData.glbMergerMeshIndex === undefined) return;
                                var meshIndex = obj.userData.glbMergerMeshIndex;
                                var primIndex = 0;
                                var parent = obj.parent;
                                if (parent && parent.children.length > 1 && parent.children.every(function (c) {
                                    return c.userData && c.userData.glbMergerMeshIndex === meshIndex;
                                })) {
                                    primIndex = parent.children.indexOf(obj);
                                }
                                var triData = computeTriangleData(obj);
                                meshes.push({
                                    object: obj, meshIndex: meshIndex, primIndex: primIndex,
                                    centroids: triData.centroids,
                                    radii: triData.radii,
                                    normals: triData.normals,
                                });
                            });
                            paintableMeshes = meshes;
                            rebuildDepthMask();
                            rebuildBrushDecal();

                            var box = new THREE.Box3().setFromObject(gltf.scene);
                            var size = box.getSize(new THREE.Vector3());
                            modelMaxDim = Math.max(size.x, size.y, size.z) || 1;
                            resolveBrushRadius();
                        }, undefined, function (error) {
                            console.error('Failed to load paint hit-test geometry: ' + (error && error.message ? error.message : error));
                        });
                    }

                    beforeViewer.addEventListener('load', function (e) {
                        fixMaterials(e.target);
                        // Reloading (after Apply/Revert) invalidates whatever the paint selection
                        // was recorded against only in the sense that the underlying temp file
                        // changed - triangle indices themselves are stable across a texture-only
                        // edit, but the hit-test geometry still has to be re-parsed from the new
                        // file, so the selection is cleared here for the same reason
                        // GeometryOptimizerEditor's reloadModel does.
                        paintableMeshes = [];
                        selection = {};
                        rebuildOverlay();
                        rebuildDepthMask();
                        rebuildBrushDecal();
                        hideBrushCursor();
                        loadHitTestGeometry(beforeViewer.src);
                        window.chrome && window.chrome.webview &&
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                    });

                    window.addEventListener('resize', function () {
                        var size = paneSize();
                        overlayRenderer.setSize(size.w, size.h);
                    });
                    // --- end paint-a-region tool -------------------------------------------------
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(html);
        }

        // Writes model to a fresh temp GLB, deleting whichever one previously occupied that slot -
        // 'before' and 'after' are tracked as separate slots (separate ref params) so updating one
        // never disturbs whichever file the other <model-viewer> currently has loaded.
        private string WriteGlbFile(ModelRoot model, ref string? slotPath)
        {
            var previous = slotPath;
            string path = Path.Combine(Path.GetTempPath(), $"glbmerger_texedit_preview_{_glbVersion++}.glb");
            model.SaveGLB(path);
            slotPath = path;

            if (previous != null)
            {
                try { File.Delete(previous); }
                catch (IOException) { /* still held by the browser - harmless, it's a temp file */ }
                catch (UnauthorizedAccessException) { }
            }

            return Path.GetFileName(path);
        }

        // Tags every mesh with its own LogicalMeshes index via glTF extras, purely so the paint
        // tool's raycast hits (against the Before pane's Three.js objects) can be mapped back to a
        // (meshIndex, primIndex) pair - same tagging/lookup convention GeometryOptimizerEditor's
        // paint tool uses. Extras are cleared again immediately after saving: model is the same
        // instance the rest of the app saves, and this tag has no business surviving into the real
        // output file. Only 'before' ever needs this - 'after' is a plain, non-interactive viewer.
        private string WriteTaggedGlbFile(ModelRoot model, ref string? slotPath)
        {
            var previousExtras = new JsonNode?[model.LogicalMeshes.Count];
            for (int i = 0; i < model.LogicalMeshes.Count; i++)
            {
                previousExtras[i] = model.LogicalMeshes[i].Extras;
                model.LogicalMeshes[i].Extras = new JsonObject { ["glbMergerMeshIndex"] = i };
            }
            try
            {
                return WriteGlbFile(model, ref slotPath);
            }
            finally
            {
                for (int i = 0; i < model.LogicalMeshes.Count; i++)
                    model.LogicalMeshes[i].Extras = previousExtras[i]!;
            }
        }

        // Regenerates the 'before' pane (3D AND the 2D texture panel, which previously only ever
        // got set from ShowSelectedOriginal on a dropdown change - never refreshed after an Apply,
        // so it kept showing the pre-edit texture indefinitely) from the model's current committed
        // state. Called after anything that actually mutates _model (Apply/Apply All/Revert), so
        // the baseline always reflects reality rather than whatever it last happened to load.
        private void RefreshBeforeViewer()
        {
            _currentBeforeFileName = WriteTaggedGlbFile(_model, ref _beforeGlbPath);
            PushSrc("setBeforeSrc", _currentBeforeFileName);

            int imageIndex = SelectedImageIndex;
            if (imageIndex >= 0)
            {
                _picBefore.Image?.Dispose();
                _picBefore.Image = LoadImageSafe(TextureLineThinner.SnapshotContent(_model, imageIndex));
            }
        }

        // Renders the 'after' pane from a throwaway clone with just the previewed image swapped in
        // - never touches _model, so a Preview (as opposed to Apply) can show the result without
        // committing to it.
        private void UpdateAfterViewer(int imageIndex, byte[] pngBytes)
        {
            var clone = _model.DeepClone();
            clone.LogicalImages[imageIndex].Content = new SharpGLTF.Memory.MemoryImage(pngBytes);
            string fileName = WriteGlbFile(clone, ref _afterGlbPath);
            PushSrc("setAfterSrc", fileName);
        }

        // Collapses the 'after' pane back onto whatever 'before' currently shows - used when there
        // is no pending, uncommitted preview to display (a fresh texture selection, or right after
        // an Apply/Revert already made 'before' and 'after' identical).
        private void ResetAfterViewerToBefore() => PushSrc("setAfterSrc", _currentBeforeFileName);

        private void PushSrc(string jsFunction, string fileName)
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"{jsFunction}('https://appassets.local/{EscapeJs(fileName)}');");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ViewerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (message == null || IsDisposed) return;

            if (message.Action == "ready")
            {
                _viewerReady = true;
                PushPaintMode();
                PushBrushRadius();
                return;
            }

            if (message.Action == "selectionChanged")
            {
                int count = message.Count ?? 0;
                _lblSelection.Text = count == 1 ? "1 triangle painted" : $"{count:N0} triangles painted";
            }
        }

        private void PushPaintMode()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintMode({(_chkPaintMode.Checked ? "true" : "false")});");
        }

        private void PushBrushRadius()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            float fraction = _sliderBrush.Value / 100f;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setBrushRadius({fraction.ToString(CultureInfo.InvariantCulture)});");
        }

        private void ClearSelection()
        {
            _lblSelection.Text = "0 triangles painted";
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync("clearPaintSelection();");
        }

        // Reads the paint tool's current selection back from the JS side. ExecuteScriptAsync
        // JSON-encodes whatever the script evaluates to, so a script that itself returns
        // JSON.stringify(...) comes back as a JSON string *literal* - it has to be unwrapped once
        // before the selection object inside it can be parsed. Returns null (no restriction, whole
        // image) when the viewer isn't ready yet or nothing is painted.
        private async Task<Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>?> ReadSelectionAsync()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return null;

            string outer;
            try
            {
                outer = await _webView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(getPaintSelection())");
            }
            catch
            {
                return null;
            }
            if (IsDisposed) return null;

            string inner = JsonSerializer.Deserialize<string>(outer) ?? "{}";
            var raw = JsonSerializer.Deserialize<Dictionary<string, int[]>>(inner) ?? new Dictionary<string, int[]>();

            var result = new Dictionary<(int, int), HashSet<int>>();
            foreach (var (key, tris) in raw)
            {
                var parts = key.Split('_');
                if (parts.Length != 2) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshIdx)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int primIdx)) continue;
                if (tris.Length == 0) continue;
                result[(meshIdx, primIdx)] = new HashSet<int>(tris);
            }
            return result.Count > 0 ? result : null;
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public int? Count { get; set; }
        }
    }
}
