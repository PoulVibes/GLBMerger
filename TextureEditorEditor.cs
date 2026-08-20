using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Texture-side tools for the merged model: UV island padding (see UvIslandPadding) on the
    // selected albedo texture, and whole-model asset layout optimization (see MeshLayoutOptimizer).
    // Both are shown against the same before/after quad chart - the flat 2D texture on top, the
    // mesh in its bind pose underneath.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window.
    public class TextureEditorEditor : UserControl
    {
        private readonly ModelRoot _model;

        private ComboBox _targetDropdown = null!;
        private Button _btnRevert = null!;
        private Label _lblStatus = null!, _lblBefore = null!, _lblAfter = null!;
        private PictureBox _picBefore = null!, _picAfter = null!;
        private WebView2 _webView = null!;

        private List<TextureAtlasUtil.AlbedoTarget> _targets = new();

        private NumericUpDown _numIslandPadding = null!;
        private Button _btnPreviewPad = null!, _btnApplyPad = null!, _btnApplyPadAll = null!;

        private NumericUpDown _numOverdrawThreshold = null!;
        private Button _btnOptimizeLayout = null!;

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
                Text = "Texture Tools (albedo only)",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            flow.Controls.Add(new Label { Text = "Texture:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
            _targetDropdown = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 8) };
            _targetDropdown.SelectedIndexChanged += (s, e) => ShowSelectedOriginal();
            flow.Controls.Add(_targetDropdown);

            flow.Controls.Add(HelpText(
                "Only images bound as a material's base color (albedo) are listed - an image used " +
                "solely as a normal, metallic/roughness, occlusion or emissive map never shows up " +
                "as a target here."));

            flow.Controls.Add(new Label
            {
                Text = "Pad UV Islands",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 4),
            });

            flow.Controls.Add(HelpText(
                "Extends each island's own edge color into the unused gutter around it. Up close " +
                "this changes nothing visible - but from a distance, mip-mapped sampling blends a " +
                "wider patch of texels, and an unpadded gutter lets that blend pull in an unrelated " +
                "island's color across the seam, which is what reads as a faint color fringe at " +
                "range. Applies to every material channel bound to this image, not just base color."));

            var paddingRow = LabeledNumeric("Padding (texels):", out _numIslandPadding, 0, 64, 16, 0);
            flow.Controls.Add(paddingRow);

            _btnPreviewPad = MakeButton("Preview Padding");
            _btnPreviewPad.Click += async (s, e) => await RunPaddingAsync(apply: false);
            flow.Controls.Add(_btnPreviewPad);

            _btnApplyPad = MakeButton("Apply Padding to This Texture");
            _btnApplyPad.Click += async (s, e) => await RunPaddingAsync(apply: true);
            flow.Controls.Add(_btnApplyPad);

            _btnApplyPadAll = MakeButton("Apply Padding to All Textures");
            _btnApplyPadAll.Click += async (s, e) => await RunPaddingAllAsync();
            flow.Controls.Add(_btnApplyPadAll);

            _btnRevert = MakeButton("Revert This Texture");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertSelected();
            flow.Controls.Add(_btnRevert);

            flow.Controls.Add(new Label
            {
                Text = "Optimize Asset Layout (meshoptimizer)",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 4),
            });

            flow.Controls.Add(HelpText(
                "Reorders triangles and vertex records into the order the GPU reads them, without " +
                "changing a single pixel or vertex position. Two of the three passes pay off in " +
                "texturing specifically: front-to-back triangle clustering lets early-Z throw away " +
                "hidden fragments before their texture fetches are paid for, and reordering vertex " +
                "records puts each triangle's UVs next to the ones sampled beside it instead of " +
                "scattered across the buffer. Unlike the two tools above, this works on the whole " +
                "model at once rather than the selected texture."));

            var overdrawRow = LabeledNumeric("Overdraw threshold:", out _numOverdrawThreshold, 1.0m, 3.0m, 1.05m, 2);
            _numOverdrawThreshold.Increment = 0.01m;   // LabeledNumeric's 0.5 step overshoots the useful 1.00-1.20 range
            flow.Controls.Add(overdrawRow);
            flow.Controls.Add(HelpText(
                "How much vertex-cache efficiency the front-to-back pass may trade away to get " +
                "there. 1.00 forbids the trade entirely; 1.05 allows 5% and is the usual choice. " +
                "The trade is only taken where it pays: overdraw is measured before and after, and " +
                "a mesh with little overdraw to begin with - anything convex and single-layered - " +
                "keeps its cache ordering instead."));

            _btnOptimizeLayout = MakeButton("Optimize Layout for Whole Model");
            _btnOptimizeLayout.Click += async (s, e) => await RunLayoutOptimizeAsync();
            flow.Controls.Add(_btnOptimizeLayout);

            flow.Controls.Add(HelpText(
                "There is no Revert for this one - it rewrites geometry for every mesh in the " +
                "model, so re-merge if you want it undone. It is a pure reordering, so the render " +
                "is unchanged either way."));

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
            // in both rows so a claim made by one is checkable against the other.
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
            _picAfter = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };

            previewPanel.Controls.Add(_lblBefore, 0, 0);
            previewPanel.Controls.Add(_lblAfter, 1, 0);
            previewPanel.Controls.Add(_picBefore, 0, 1);
            previewPanel.Controls.Add(_picAfter, 1, 1);

            var lbl3D = new Label
            {
                Text = "3D Model - Bind Pose", AutoSize = true,
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
            _targets = TextureAtlasUtil.FindAlbedoImages(_model);

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
            _numIslandPadding.Enabled = enabled;
            _btnPreviewPad.Enabled = enabled;
            _btnApplyPad.Enabled = enabled;
            _btnApplyPadAll.Enabled = enabled;

            // Deliberately not gated on `enabled`: that flag tracks whether the model has any
            // albedo texture to edit, and layout optimization works on geometry regardless.
            _numOverdrawThreshold.Enabled = MeshoptNative.IsAvailable;
            _btnOptimizeLayout.Enabled = MeshoptNative.IsAvailable;
        }

        private int SelectedImageIndex =>
            _targetDropdown.SelectedIndex >= 0 ? _targets[_targetDropdown.SelectedIndex].ImageIndex : -1;

        private void ShowSelectedOriginal()
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0) return;

            _picBefore.Image?.Dispose();
            _picBefore.Image = LoadImageSafe(TextureAtlasUtil.SnapshotContent(_model, imageIndex));
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

        private void RevertSelected()
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0 || !_originalContent.TryGetValue(imageIndex, out var original)) return;

            UvIslandPadding.Apply(_model, imageIndex, original);
            _originalContent.Remove(imageIndex);
            _btnRevert.Enabled = false;

            ShowSelectedOriginal();
            RefreshBeforeViewer();
            _lblStatus.Text = "Texture reverted to the original.";
        }

        private void SetBusy(bool busy, string? status)
        {
            _btnPreviewPad.Enabled = !busy;
            _btnApplyPad.Enabled = !busy;
            _btnApplyPadAll.Enabled = !busy;
            _btnOptimizeLayout.Enabled = !busy && MeshoptNative.IsAvailable;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (status != null) _lblStatus.Text = status;
        }

        private async Task RunPaddingAsync(bool apply)
        {
            int imageIndex = SelectedImageIndex;
            if (imageIndex < 0) return;

            SetBusy(true, apply ? "Applying padding..." : "Processing padding preview...");

            var options = new UvIslandPadding.Options { PaddingTexels = (int)_numIslandPadding.Value };

            UvIslandPadding.Report report;
            byte[] pngBytes;
            try
            {
                (report, pngBytes) = await Task.Run(() => UvIslandPadding.Process(_model, imageIndex, options));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"UV island padding failed: {ex.Message}",
                    "Pad UV Islands", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            _picAfter.Image?.Dispose();
            _picAfter.Image = LoadImageSafe(pngBytes);
            _lastPreviewBytes = pngBytes;
            _lastPreviewImageIndex = imageIndex;
            UpdateAfterViewer(imageIndex, pngBytes);

            if (!apply)
            {
                SetBusy(false, $"{report.TexelsPadded:N0} gutter texel(s) padded around " +
                    $"{report.TexelsCovered:N0} mapped texels. Preview only - nothing changed yet.");
                return;
            }

            if (!_originalContent.ContainsKey(imageIndex))
                _originalContent[imageIndex] = UvIslandPadding.SnapshotContent(_model, imageIndex);

            UvIslandPadding.Apply(_model, imageIndex, pngBytes);
            _btnRevert.Enabled = true;
            RefreshBeforeViewer();

            SetBusy(false, $"Applied: {report.TexelsPadded:N0} gutter texel(s) padded. " +
                "Included the next time you save the merge.");
        }

        private async Task RunPaddingAllAsync()
        {
            if (_targets.Count == 0) return;

            SetBusy(true, $"Applying padding to {_targets.Count} texture(s)...");

            var options = new UvIslandPadding.Options { PaddingTexels = (int)_numIslandPadding.Value };
            int totalPadded = 0;

            try
            {
                foreach (var target in _targets)
                {
                    var (report, pngBytes) = await Task.Run(() => UvIslandPadding.Process(_model, target.ImageIndex, options));
                    if (IsDisposed) return;

                    if (!_originalContent.ContainsKey(target.ImageIndex))
                        _originalContent[target.ImageIndex] = UvIslandPadding.SnapshotContent(_model, target.ImageIndex);

                    UvIslandPadding.Apply(_model, target.ImageIndex, pngBytes);
                    totalPadded += report.TexelsPadded;

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
                MessageBox.Show(this, $"UV island padding failed: {ex.Message}",
                    "Pad UV Islands", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _btnRevert.Enabled = _originalContent.ContainsKey(SelectedImageIndex);
            RefreshBeforeViewer();
            SetBusy(false, $"Applied to {_targets.Count} texture(s): {totalPadded:N0} gutter texels padded in total. " +
                "Included the next time you save the merge.");
        }

        // Whole-model, one shot, no undo - see MeshLayoutOptimizer for what the three passes do
        // and why BLEND materials only get two of them.
        private async Task RunLayoutOptimizeAsync()
        {
            if (!MeshoptNative.IsAvailable)
            {
                MessageBox.Show(this,
                    "The meshoptimizer native library could not be loaded, so layout optimization " +
                    "is unavailable.",
                    "Optimize Asset Layout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirm = MessageBox.Show(this,
                "Reorder triangles and vertex records across every mesh in the model?" +
                Environment.NewLine + Environment.NewLine +
                "This does not change how the model looks, but there is no Revert for it - the " +
                "other tools in this editor keep a per-texture original, and this one rewrites " +
                "geometry model-wide.",
                "Optimize Asset Layout", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (confirm != DialogResult.OK) return;

            SetBusy(true, "Optimizing asset layout...");

            var options = new MeshLayoutOptimizer.Options
            {
                OverdrawThreshold = (float)_numOverdrawThreshold.Value,
            };

            MeshLayoutOptimizer.Report report;
            try
            {
                report = await Task.Run(() => MeshLayoutOptimizer.Optimize(_model, options));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Layout optimization failed: {ex.Message}",
                    "Optimize Asset Layout", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            if (!report.ChangedAnything)
            {
                SetBusy(false, "Nothing to optimize - no triangle primitive in this model qualified.");
                return;
            }

            // Both panes show the same committed model again: the geometry under every texture just
            // moved, and no uncommitted texture preview survives that.
            _lastPreviewBytes = null;
            _lastPreviewImageIndex = -1;
            RefreshBeforeViewer();
            ResetAfterViewerToBefore();

            // Both metrics, always: the two passes pull ACMR in opposite directions, so a bare
            // before/after on it alone makes a correct trade read as a regression.
            var summary = $"Optimized {report.PrimitivesOptimized:N0} primitive(s): " +
                $"{report.TrianglesReordered:N0} triangles, {report.VerticesReordered:N0} vertices reordered." +
                Environment.NewLine +
                $"Vertex cache (ACMR, lower is better): {report.AcmrBefore:0.000} -> {report.AcmrAfter:0.000}" +
                Environment.NewLine +
                $"Overdraw (lower is better): {report.OverdrawBefore:0.000} -> {report.OverdrawAfter:0.000}";

            if (report.AcmrAfter > report.AcmrAfterCache + 0.0005)
                summary += Environment.NewLine +
                    $"Cache alone reached {report.AcmrAfterCache:0.000}; the front-to-back pass gave " +
                    $"{report.AcmrAfter - report.AcmrAfterCache:0.000} of that back to cut overdraw.";

            if (report.OverdrawPassRejected > 0)
                summary += Environment.NewLine +
                    $"{report.OverdrawPassRejected:N0} primitive(s) had too little overdraw for the " +
                    "front-to-back pass to be worth its cache cost, so the cache ordering was kept.";

            if (report.VerticesDropped > 0)
                summary += $" {report.VerticesDropped:N0} unreferenced vertex/vertices dropped.";
            if (report.PrimitivesKeptInDrawOrder > 0)
                summary += $" {report.PrimitivesKeptInDrawOrder:N0} blended primitive(s) kept in draw order.";
            if (report.PrimitivesSkipped > 0)
                summary += $" {report.PrimitivesSkipped:N0} primitive(s) skipped.";

            SetBusy(false, summary + " Included the next time you save the merge.");
        }

        // --- 3D quadrant --------------------------------------------------------------------------

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
                    <div class='pane' id='panebefore'>
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
                    afterViewer.addEventListener('load', function (e) { fixMaterials(e.target); });

                    beforeViewer.addEventListener('load', function (e) {
                        fixMaterials(e.target);
                        window.chrome && window.chrome.webview &&
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                    });
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

        // Regenerates the 'before' pane (3D AND the 2D texture panel, which previously only ever
        // got set from ShowSelectedOriginal on a dropdown change - never refreshed after an Apply,
        // so it kept showing the pre-edit texture indefinitely) from the model's current committed
        // state. Called after anything that actually mutates _model (Apply Padding/Revert), so
        // the baseline always reflects reality rather than whatever it last happened to load.
        private void RefreshBeforeViewer()
        {
            _currentBeforeFileName = WriteGlbFile(_model, ref _beforeGlbPath);
            PushSrc("setBeforeSrc", _currentBeforeFileName);

            int imageIndex = SelectedImageIndex;
            if (imageIndex >= 0)
            {
                _picBefore.Image?.Dispose();
                _picBefore.Image = LoadImageSafe(TextureAtlasUtil.SnapshotContent(_model, imageIndex));
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

            if (message.Action == "ready") _viewerReady = true;
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
        }
    }
}
