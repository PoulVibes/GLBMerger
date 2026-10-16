using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Re-atlases the model's textures (see TextureAtlasOptimizer): new UV charts unwrapped from
    // the surface, packed, and every texture rebaked into them. Same before/after quad chart as
    // the Texture Editor - the flat 2D image on top (optionally with its UV wireframe, which is
    // where the seam count shows), the mesh in its bind pose underneath.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window.
    public class TextureAtlasOptimizerEditor : UserControl, IUnappliedChanges
    {
        private readonly HelpTips _help;
        private readonly ModelRoot _model;

        private NumericUpDown _numStretch = null!, _numPadding = null!;
        private ComboBox _cmbOutputSize = null!, _cmbImage = null!;
        private CheckBox _chkPreserveDensity = null!, _chkWireframe = null!;
        private Button _btnPreview = null!, _btnApply = null!, _btnCancel = null!;
        private ProgressBar _progress = null!;
        private Label _lblStatus = null!;
        private PictureBox _picBefore = null!, _picAfter = null!;
        private WebView2 _webView = null!;

        private static readonly (string Label, int Size)[] OutputSizes =
        {
            ("Keep each image's size", 0), ("4096", 4096), ("2048", 2048), ("1024", 1024), ("512", 512),
        };

        private TextureAtlasOptimizer.Report? _report;
        private ModelRoot? _previewModel;
        private CancellationTokenSource? _cts;

        private int _glbVersion;
        private string? _beforeGlbPath, _afterGlbPath;
        private string _currentBeforeFileName = "";
        private bool _viewerReady;

        public TextureAtlasOptimizerEditor(ModelRoot model, bool darkMode = false)
        {
            _help = new HelpTips(this);
            _model = model;
            Dock = DockStyle.Fill;

            BuildUi();
            RefreshImageList();

            ThemeManager.Apply(this, darkMode);
            _ = InitializeViewerAsync();
        }

        public bool HasUnappliedChanges => _report != null && _report.HasChanges;
        public string UnappliedChangesDescription => "a re-atlas preview not yet applied";

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 340, AutoScroll = true };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
            };

            flow.Controls.Add(new Label { Text = "Texture Atlas Optimizer", AutoSize = true, Margin = new Padding(3, 0, 3, 8) });
            _help.Add(flow,
                "Joins neighbouring UV islands into larger charts, unwraps them again from the " +
                "surface, packs them by their real outlines and rebakes every texture into the new " +
                "layout - instead of chopping out the existing islands and packing those tighter, " +
                "which keeps (and adds gutters to) every seam the source had. Albedo, normal, " +
                "metallic/roughness, occlusion and emissive all follow the same new layout; normal " +
                "maps are also rotated to their charts' new orientation.");

            flow.Controls.Add(LabeledNumeric("Max stretch:", out _numStretch, 1.1m, 4m, 1.5m, 2));
            _numStretch.Increment = 0.1m;
            _help.Add(flow,
                "Islands are only joined when the joined chart flattens with every part of it within " +
                "this factor of the texel density the source gave it (1.5 = up to 50% more or fewer " +
                "texels per unit of surface than its neighbours). Higher joins more and removes " +
                "more seams, at the cost of some parts getting softer; lower keeps detail exact.");

            flow.Controls.Add(LabeledNumeric("Padding (texels):", out _numPadding, 0, 64, 8, 0));
            _help.Add(flow,
                "Gutter between charts in the rebuilt image, filled with each chart's own edge " +
                "colour so mip-mapping at a distance never blends one chart into the next.");

            flow.Controls.Add(new Label { Text = "Output size:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
            _cmbOutputSize = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 6) };
            foreach (var (label, _) in OutputSizes) _cmbOutputSize.Items.Add(label);
            _cmbOutputSize.SelectedIndex = 0;
            flow.Controls.Add(_cmbOutputSize);
            _help.Add(flow,
                "Longest side of every rebuilt image. A tighter layout often fits the same detail " +
                "into a smaller image - the status line reports how much of the source's texel " +
                "density survived.");

            _chkPreserveDensity = new CheckBox { Text = "Keep source texel density per region", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 4) };
            flow.Controls.Add(_chkPreserveDensity);
            _help.Add(flow,
                "Scales each chart by how much texture the source gave that part of the surface - " +
                "a face that had a lot of the old texture keeps a lot of the new one. Off spreads " +
                "texels evenly over the whole surface.");

            _btnPreview = MakeButton("Preview Re-Atlas");
            _btnPreview.Click += async (s, e) => await RunPreviewAsync();
            flow.Controls.Add(_btnPreview);

            _btnCancel = MakeButton("Cancel");
            _btnCancel.Enabled = false;
            _btnCancel.Click += (s, e) => _cts?.Cancel();
            flow.Controls.Add(_btnCancel);

            _btnApply = MakeButton("Apply to Model");
            _btnApply.Enabled = false;
            _btnApply.Click += (s, e) => ApplyPreview();
            flow.Controls.Add(_btnApply);
            _help.Add(flow,
                "There is no Revert - Apply rewrites the UVs (and splits vertices along the new " +
                "seams) on every affected mesh, so re-merge if you want it undone.");

            _progress = new ProgressBar { Width = 300, Minimum = 0, Maximum = 1000, Visible = false, Margin = new Padding(3, 6, 3, 3) };
            flow.Controls.Add(_progress);

            flow.Controls.Add(new Label { Text = "Show image:", AutoSize = true, Margin = new Padding(3, 10, 3, 2) });
            _cmbImage = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _cmbImage.SelectedIndexChanged += (s, e) => ShowImages();
            flow.Controls.Add(_cmbImage);

            _chkWireframe = new CheckBox { Text = "Show UV layout", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 4) };
            _chkWireframe.CheckedChanged += (s, e) => ShowImages();
            flow.Controls.Add(_chkWireframe);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 8, 3, 4), ForeColor = Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            previewPanel.Controls.Add(new Label { Text = "2D Texture - Before", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 0, 0);
            previewPanel.Controls.Add(new Label { Text = "2D Texture - After", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, 1, 0);
            _picBefore = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
            _picAfter = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
            previewPanel.Controls.Add(_picBefore, 0, 1);
            previewPanel.Controls.Add(_picAfter, 1, 1);

            var lbl3D = new Label { Text = "3D Model - Bind Pose", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
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
            numeric = new NumericUpDown
            {
                Width = 80, DecimalPlaces = decimals, Minimum = min, Maximum = max, Value = value,
                Margin = new Padding(0, 4, 3, 3),
            };
            row.Controls.Add(numeric);
            return row;
        }

        private static Button MakeButton(string text) => new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(300, 0), Margin = new Padding(3, 3, 3, 3),
        };

        // Every image any material channel binds - the re-atlas rebuilds all of them, not just albedo.
        private List<UvIslandPadding.ImageTarget> _images = new();

        private void RefreshImageList()
        {
            _images = UvIslandPadding.FindImages(_model);
            _cmbImage.Items.Clear();
            foreach (var t in _images) _cmbImage.Items.Add($"Image {t.ImageIndex} ({t.Label})");
            if (_images.Count == 0)
            {
                _lblStatus.Text = "This model has no textures to re-atlas.";
                _btnPreview.Enabled = false;
                return;
            }
            _cmbImage.SelectedIndex = 0;
        }

        private int SelectedImageIndex => _cmbImage.SelectedIndex >= 0 ? _images[_cmbImage.SelectedIndex].ImageIndex : -1;

        private void ShowImages()
        {
            int img = SelectedImageIndex;
            if (img < 0) return;

            _picBefore.Image?.Dispose();
            _picBefore.Image = Render(_model, img, TextureAtlasUtil.SnapshotContent(_model, img));

            _picAfter.Image?.Dispose();
            _picAfter.Image = null;
            if (_previewModel != null && _report != null && _report.Images.TryGetValue(img, out var bytes))
                _picAfter.Image = Render(_previewModel, img, bytes);
        }

        private System.Drawing.Image? Render(ModelRoot model, int img, byte[] bytes)
        {
            try
            {
                if (_chkWireframe.Checked) return TextureAtlasOptimizer.DrawLayout(model, img, bytes);
                using var ms = new MemoryStream(bytes);
                return System.Drawing.Image.FromStream(ms);
            }
            catch
            {
                return null;
            }
        }

        private void SetBusy(bool busy)
        {
            _btnPreview.Enabled = !busy && _images.Count > 0;
            _btnApply.Enabled = !busy && _report != null && _report.HasChanges;
            _btnCancel.Enabled = busy;
            _numStretch.Enabled = _numPadding.Enabled = _cmbOutputSize.Enabled = _chkPreserveDensity.Enabled = !busy;
            _progress.Visible = busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private async Task RunPreviewAsync()
        {
            var options = new TextureAtlasOptimizer.Options
            {
                MaxStretch = (float)_numStretch.Value,
                PaddingTexels = (int)_numPadding.Value,
                PreserveDensity = _chkPreserveDensity.Checked,
                OutputSize = OutputSizes[Math.Max(0, _cmbOutputSize.SelectedIndex)].Size,
            };

            _report = null;
            _previewModel = null;
            _cts = new CancellationTokenSource();
            SetBusy(true);
            _progress.Value = 0;
            _lblStatus.Text = "Unwrapping...";

            var progress = new Progress<TextureAtlasOptimizer.Progress>(p =>
            {
                if (IsDisposed) return;
                _progress.Value = p.Total > 0 ? Math.Clamp(p.Done * 1000 / p.Total, 0, 1000) : 0;
                _lblStatus.Text = p.Stage;
            });

            TextureAtlasOptimizer.Report report;
            ModelRoot preview;
            try
            {
                var token = _cts.Token;
                (report, preview) = await Task.Run(() =>
                {
                    var r = TextureAtlasOptimizer.Analyze(_model, options, progress, token);
                    var clone = _model.DeepClone();
                    TextureAtlasOptimizer.Apply(r, clone);
                    return (r, clone);
                });
            }
            catch (OperationCanceledException)
            {
                if (IsDisposed) return;
                SetBusy(false);
                _lblStatus.Text = "Cancelled.";
                ShowImages();
                return;
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                SetBusy(false);
                MessageBox.Show(this, $"Re-atlas failed: {ex.Message}", "Texture Atlas Optimizer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (IsDisposed) return;

            _report = report;
            _previewModel = preview;
            SetBusy(false);
            ShowImages();
            UpdateAfterViewer(preview);
            _lblStatus.Text = Summarize(report) + Environment.NewLine +
                (report.HasChanges ? "Preview only - nothing changed yet." : "Nothing to change.");
        }

        private static string Summarize(TextureAtlasOptimizer.Report report)
        {
            var lines = new List<string>();
            foreach (var g in report.Groups)
            {
                string images = string.Join(", ", g.ImageIndices);
                if (g.SkippedReason != null)
                {
                    lines.Add($"{g.Label} (images {images}): skipped - {g.SkippedReason}.");
                    continue;
                }
                lines.Add($"{g.Label} (images {images}): islands {g.IslandsBefore:N0} -> charts {g.ChartsAfter:N0}, " +
                    $"seam edges {g.SeamEdgesBefore:N0} -> {g.SeamEdgesAfter:N0}, " +
                    $"vertices {g.VerticesBefore:N0} -> {g.VerticesAfter:N0}. " +
                    $"Atlas {g.Fill:P0} full, detail kept {g.DetailScale:P0} of the source's texel density.");
            }
            return string.Join(Environment.NewLine, lines);
        }

        private void ApplyPreview()
        {
            if (_report == null || !_report.HasChanges) return;

            var confirm = MessageBox.Show(this,
                "Rewrite the UVs and textures on the model with this layout?" + Environment.NewLine + Environment.NewLine +
                "There is no Revert - re-merge to undo it.",
                "Texture Atlas Optimizer", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK) return;

            try
            {
                TextureAtlasOptimizer.Apply(_report, _model);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Applying the re-atlas failed: {ex.Message}", "Texture Atlas Optimizer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _report = null;
            _previewModel = null;
            SetBusy(false);
            RefreshImageList();
            ShowImages();
            RefreshBeforeViewer();
            ResetAfterViewerToBefore();
            _lblStatus.Text = "Applied. Included the next time you save the merge.";
        }

        // --- 3D quadrant (same lifecycle as TextureEditorEditor's) -------------------------------

        private async Task InitializeViewerAsync()
        {
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _currentBeforeFileName = WriteGlbFile(_model, ref _beforeGlbPath);

            string html = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #wrap { display: flex; width: 100%; height: 100%; }
                    .pane { flex: 1; position: relative; }
                    .pane:first-child { border-right: 1px solid #444; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; position: absolute; top: 0; left: 0; }
                    .paneLabel {
                        position: absolute; top: 6px; left: 8px; color: #ddd; font: 12px sans-serif;
                        background: rgba(0, 0, 0, 0.45); padding: 2px 6px; border-radius: 3px; pointer-events: none;
                        z-index: 2;
                    }
                </style>
            </head>
            <body>
                <div id='wrap'>
                    <div class='pane'>
                        <div class='paneLabel'>Before</div>
                        <model-viewer id='before' src='https://appassets.local/" + _currentBeforeFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                    </div>
                    <div class='pane'>
                        <div class='paneLabel'>After</div>
                        <model-viewer id='after' src='https://appassets.local/" + _currentBeforeFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                    </div>
                </div>
                <script>
                    window.setBeforeSrc = function (url) { document.getElementById('before').src = url; };
                    window.setAfterSrc = function (url) { document.getElementById('after').src = url; };

                    // BLEND with no real translucency draws without depth writes, so the far side
                    // shows through - draw it alpha-tested instead (see TextureEditorEditor).
                    function keepCutouts(mat) {
                        if (mat.getAlphaMode() !== 'BLEND') return;
                        var factor = mat.pbrMetallicRoughness && mat.pbrMetallicRoughness.baseColorFactor;
                        if (factor && factor[3] < 0.5) return;
                        mat.setAlphaMode('MASK');
                        mat.setAlphaCutoff(0.5);
                    }
                    function fixMaterials(viewer) {
                        (viewer.model.materials || []).forEach(function (mat) {
                            keepCutouts(mat);
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });
                    }

                    document.getElementById('after').addEventListener('load', function (e) { fixMaterials(e.target); });
                    document.getElementById('before').addEventListener('load', function (e) {
                        fixMaterials(e.target);
                        window.chrome && window.chrome.webview &&
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                    });
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(html);
        }

        private string WriteGlbFile(ModelRoot model, ref string? slotPath)
        {
            var previous = slotPath;
            string path = Path.Combine(Path.GetTempPath(), $"glbmerger_reatlas_preview_{_glbVersion++}.glb");
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

        private void RefreshBeforeViewer()
        {
            _currentBeforeFileName = WriteGlbFile(_model, ref _beforeGlbPath);
            PushSrc("setBeforeSrc", _currentBeforeFileName);
        }

        private void UpdateAfterViewer(ModelRoot preview) => PushSrc("setAfterSrc", WriteGlbFile(preview, ref _afterGlbPath));

        private void ResetAfterViewerToBefore() => PushSrc("setAfterSrc", _currentBeforeFileName);

        private void PushSrc(string jsFunction, string fileName)
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"{jsFunction}('https://appassets.local/{fileName.Replace("\\", "\\\\").Replace("'", "\\'")}');");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (doc.RootElement.TryGetProperty("action", out var action) && action.GetString() == "ready")
                    _viewerReady = true;
            }
            catch (JsonException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts?.Cancel();
            base.Dispose(disposing);
        }
    }
}
