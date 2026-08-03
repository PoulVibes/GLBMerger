using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Reduces the merged model's triangle count, via GeometryOptimizer's meshoptimizer-backed
    // simplifier. Two controls decide the result: how many triangles to keep, and how much error to
    // allow - whichever is reached first stops the collapse. A high keep ratio with a tight error
    // budget is the near-lossless setting.
    //
    // Analyze is a dry run: it reports exactly what would change, so the settings can be dialled
    // against real numbers before anything is committed. Apply writes the result onto the shared
    // merge result in place (like every other editor mode), and Revert puts the original index
    // buffers back - worth having here specifically because this is the one mode that can silently
    // alter geometry across the whole model at once.
    //
    // The preview is raw Three.js rather than <model-viewer> so it can offer a wireframe toggle;
    // seeing triangle density before and after is the entire point of this tool.
    //
    // One of the five modes hosted by ModelEditorForm (see EditorMode there).
    public class GeometryOptimizerEditor : UserControl
    {
        private readonly ModelRoot _model;

        private WebView2 _webView = null!;
        private TrackBar _sliderSkin = null!, _sliderRatio = null!;
        private Label _lblSkin = null!, _lblRatio = null!, _lblStatus = null!, _lblTotals = null!;
        private NumericUpDown _numError = null!;
        private CheckBox _chkWireframe = null!, _chkLockBorders = null!, _chkVertexOrder = null!;
        private Button _btnAnalyze = null!, _btnApply = null!, _btnRevert = null!;
        private DataGridView _grid = null!;

        private bool _viewerReady;
        private int _previewVersion;
        private string? _previewPath;

        // Taken once, immediately before the first Apply of the session - the model is shared with
        // every other editor mode and nothing else keeps a copy of the original geometry.
        private List<int[]>? _originalIndices;

        public GeometryOptimizerEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            BuildUi();
            ShowBaseline();
            UpdateSimplifyEnabled();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, AutoScroll = true };

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
                Text = "Simplify (meshoptimizer)",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            _lblRatio = new Label { Text = "Keep 50% of triangles", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderRatio = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 5, Maximum = 100, Value = 50,
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderRatio.ValueChanged += (s, e) => _lblRatio.Text = $"Keep {_sliderRatio.Value}% of triangles";
            flow.Controls.Add(_lblRatio);
            flow.Controls.Add(_sliderRatio);

            var errorRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            errorRow.Controls.Add(new Label { Text = "Error budget:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _numError = new NumericUpDown
            {
                Width = 90, DecimalPlaces = 4, Increment = 0.005m, Minimum = 0.0001m, Maximum = 1m, Value = 0.01m,
                Margin = new Padding(0, 4, 3, 3),
            };
            errorRow.Controls.Add(_numError);
            flow.Controls.Add(errorRow);

            flow.Controls.Add(HelpText(
                "Whichever comes first wins: simplification stops at the keep ratio, or earlier if " +
                "it would exceed the error budget. For a near-lossless pass, use a high keep ratio " +
                "with a small budget."));

            _chkLockBorders = new CheckBox { Text = "Preserve mesh outlines", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 0) };
            flow.Controls.Add(_chkLockBorders);

            _chkVertexOrder = new CheckBox { Text = "Optimize vertex cache order", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 8) };
            flow.Controls.Add(_chkVertexOrder);

            _lblSkin = new Label { Text = SkinLabel(0), AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderSkin = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 0, Maximum = 100, Value = 0,
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderSkin.ValueChanged += (s, e) => _lblSkin.Text = SkinLabel(_sliderSkin.Value);
            flow.Controls.Add(_lblSkin);
            flow.Controls.Add(_sliderSkin);

            flow.Controls.Add(HelpText(
                "Collapsing an edge re-interpolates its skin weights. At 0% only vertices whose " +
                "neighbours share identical weights can move, so deformation cannot change."));

            _btnAnalyze = MakeButton("Analyze (dry run)");
            _btnAnalyze.Click += async (s, e) => await RunAnalyzeAsync(apply: false);
            flow.Controls.Add(_btnAnalyze);

            _btnApply = MakeButton("Apply to Merge");
            _btnApply.Click += async (s, e) => await RunAnalyzeAsync(apply: true);
            flow.Controls.Add(_btnApply);

            _btnRevert = MakeButton("Revert Geometry");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertGeometry();
            flow.Controls.Add(_btnRevert);

            flow.Controls.Add(HelpText(
                "Optimizing rewrites triangles only, so vertices the result no longer references " +
                "still sit in the file. Dropping them needs a full model rebuild - see " +
                "GeometryOptimizer.CompactUnusedVertices."));

            _chkWireframe = new CheckBox { Text = "Wireframe preview", AutoSize = true, Margin = new Padding(3, 8, 3, 8) };
            _chkWireframe.CheckedChanged += (s, e) => PushWireframe();
            flow.Controls.Add(_chkWireframe);

            // Status sits above the grid rather than below it: the grid is tall enough to push
            // anything under it off the bottom of the panel, and the outcome line is the first
            // thing to read after a run.
            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 4, 3, 6), ForeColor = System.Drawing.Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            _lblTotals = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            flow.Controls.Add(_lblTotals);

            _grid = new DataGridView
            {
                Width = 340,
                Height = 220,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Margin = new Padding(3, 0, 3, 8),
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mesh", FillWeight = 40 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Before", FillWeight = 20 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "After", FillWeight = 20 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Saved", FillWeight = 20 });
            flow.Controls.Add(_grid);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private static Label HelpText(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new System.Drawing.Size(340, 0),
            Margin = new Padding(3, 0, 3, 12),
            ForeColor = System.Drawing.Color.Gray,
        };

        private static Button MakeButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new System.Drawing.Size(330, 0),
            Margin = new Padding(3, 3, 3, 3),
        };

        private static string SkinLabel(int percent) => percent switch
        {
            0 => "Skin tolerance: 0% (identical weights only)",
            100 => "Skin tolerance: 100% (ignore skinning)",
            _ => $"Skin tolerance: {percent}%",
        };

        private GeometryOptimizer.SimplifyOptions CurrentOptions() => new()
        {
            TargetRatio = _sliderRatio.Value / 100f,
            TargetError = (float)_numError.Value,
            LockBorders = _chkLockBorders.Checked,
            OptimizeVertexOrder = _chkVertexOrder.Checked,
            SkinTolerance = _sliderSkin.Value / 100f,
        };

        // Everything here needs the native meshoptimizer library; without it the controls would
        // look available but silently do nothing, so the whole pane is switched off and says why.
        private void UpdateSimplifyEnabled()
        {
            bool available = MeshoptNative.IsAvailable;
            if (available) return;

            _sliderRatio.Enabled = false;
            _lblRatio.Enabled = false;
            _numError.Enabled = false;
            _chkLockBorders.Enabled = false;
            _chkVertexOrder.Enabled = false;
            _sliderSkin.Enabled = false;
            _btnAnalyze.Enabled = false;
            _btnApply.Enabled = false;
            _lblStatus.Text = "meshoptimizer native library unavailable - optimization is disabled.";
        }

        // Fills the grid with the model's current triangle counts before anything has been run, so
        // the pane isn't just an empty table when it opens.
        private void ShowBaseline()
        {
            _grid.Rows.Clear();
            int total = 0;
            foreach (var mesh in _model.LogicalMeshes)
            {
                int tris = mesh.Primitives.Sum(p => p.GetTriangleIndices().Count());
                total += tris;
                _grid.Rows.Add(mesh.Name ?? "(unnamed)", tris.ToString("N0"), "-", "-");
            }
            _lblTotals.Text = $"Current: {total:N0} triangles";
        }

        private async Task RunAnalyzeAsync(bool apply)
        {
            SetBusy(true, apply ? "Optimizing..." : "Analyzing...");

            var options = CurrentOptions();
            GeometryOptimizer.Report report;
            try
            {
                // A big mesh takes a noticeable moment; off the UI thread so the window keeps
                // painting. The analysis only reads the model, so it's safe to run there - the
                // write-back below happens back on the UI thread.
                report = await Task.Run(() => GeometryOptimizer.Analyze(_model, options));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Geometry analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            FillGrid(report);

            if (!apply)
            {
                SetBusy(false, report.HasChanges
                    ? $"Would remove {report.TrianglesSaved:N0} triangles ({report.PercentSaved:0.00}%). Nothing changed yet."
                    : "Nothing to simplify at these settings - lower the keep ratio or raise the error budget.");
                return;
            }

            if (!report.HasChanges)
            {
                SetBusy(false, "Nothing to simplify at these settings - model left unchanged.");
                return;
            }

            _originalIndices ??= GeometryOptimizer.SnapshotIndices(_model);
            GeometryOptimizer.Apply(report, _model);
            _btnRevert.Enabled = true;

            SetBusy(false, $"Removed {report.TrianglesSaved:N0} triangles ({report.PercentSaved:0.00}%). " +
                "Included the next time you save the merge.");
            ReloadPreview();
        }

        private void RevertGeometry()
        {
            if (_originalIndices == null) return;

            GeometryOptimizer.RestoreIndices(_model, _originalIndices);
            _originalIndices = null;
            _btnRevert.Enabled = false;

            ShowBaseline();
            _lblStatus.Text = "Geometry reverted to the original merge result.";
            ReloadPreview();
        }

        private void FillGrid(GeometryOptimizer.Report report)
        {
            _grid.Rows.Clear();
            foreach (var group in report.Primitives.GroupBy(p => p.MeshName))
            {
                int before = group.Sum(p => p.TrianglesBefore);
                int after = group.Sum(p => p.TrianglesAfter);
                int saved = before - after;
                _grid.Rows.Add(
                    group.Key,
                    before.ToString("N0"),
                    after.ToString("N0"),
                    saved == 0 ? "-" : $"{saved:N0} ({100.0 * saved / Math.Max(before, 1):0.0}%)");
            }

            var skipped = report.Primitives.Where(p => p.SkippedReason != null).ToList();
            _lblTotals.Text =
                $"{report.TrianglesBefore:N0} -> {report.TrianglesAfter:N0} triangles ({report.PercentSaved:0.00}% saved)\n" +
                $"worst error {report.WorstError:0.0000} (relative to mesh size)" +
                (skipped.Count > 0 ? $"\n{skipped.Count} primitive(s) skipped: {skipped[0].SkippedReason}" : "");
        }

        private void SetBusy(bool busy, string? status)
        {
            _btnAnalyze.Enabled = !busy;
            _btnApply.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (status != null) _lblStatus.Text = status;
        }

        private async Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-forget
            // startup may still be mid-await, so both the await itself and everything after it have
            // to tolerate that.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string previewFileName = WritePreviewFile();

            // Raw Three.js (rather than <model-viewer>) purely for the wireframe toggle. No
            // animation mixer here: this mode is about triangle density, and the bind pose is the
            // clearest thing to inspect it in.
            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewport { width: 100%; height: 100%; display: block; }
                    #error-overlay {
                        position: absolute; top: 10px; left: 10px; right: 10px;
                        background: rgba(139, 0, 0, 0.92); color: #fff; padding: 12px;
                        border-radius: 6px; font-family: monospace; font-size: 12px;
                        white-space: pre-wrap; display: none; max-height: 40%; overflow: auto;
                    }
                </style>
            </head>
            <body>
                <canvas id='viewport'></canvas>
                <div id='error-overlay'></div>
                <script>
                    function showError(msg) {
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };

                    var canvas = document.querySelector('#viewport');
                    var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
                    renderer.setPixelRatio(window.devicePixelRatio);
                    renderer.setSize(window.innerWidth, window.innerHeight);
                    renderer.outputEncoding = THREE.sRGBEncoding;

                    var scene = new THREE.Scene();
                    scene.background = new THREE.Color(0x1a1c1e);

                    var camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.01, 10000);
                    var controls = new THREE.OrbitControls(camera, renderer.domElement);
                    controls.enableDamping = true;

                    scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
                    scene.add(new THREE.AmbientLight(0xffffff, 0.6));
                    var dirLight = new THREE.DirectionalLight(0xffffff, 1.4);
                    dirLight.position.set(5, 10, 7.5);
                    scene.add(dirLight);

                    var root = null;
                    var materials = [];
                    var wireframe = false;
                    var framed = false;

                    function applyWireframe() {
                        materials.forEach(function (mat) { mat.wireframe = wireframe; });
                    }

                    window.setWireframe = function (value) {
                        wireframe = value;
                        applyWireframe();
                    };

                    // Re-reads the model after an Apply or Revert. The camera is only framed on the
                    // very first load, so re-optimizing doesn't yank the view back to a default
                    // angle every time the user changes a setting.
                    window.reloadModel = function (url) {
                        var loader = new THREE.GLTFLoader();
                        loader.load(url, function (gltf) {
                            try {
                                if (root) scene.remove(root);
                                root = gltf.scene;
                                materials = [];
                                root.traverse(function (obj) {
                                    if (obj.isMesh && obj.material) {
                                        var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                                        mats.forEach(function (mat) {
                                            mat.side = THREE.DoubleSide;
                                            if (typeof mat.metalness === 'number') mat.metalness = Math.min(mat.metalness, 0.15);
                                            if (typeof mat.roughness === 'number') mat.roughness = Math.max(mat.roughness, 0.7);
                                            if (materials.indexOf(mat) === -1) materials.push(mat);
                                        });
                                    }
                                });
                                scene.add(root);
                                applyWireframe();

                                if (!framed) {
                                    framed = true;
                                    var box = new THREE.Box3().setFromObject(root);
                                    var size = box.getSize(new THREE.Vector3());
                                    var center = box.getCenter(new THREE.Vector3());
                                    var maxDim = (Math.max(size.x, size.y, size.z) || 1) * 2.0;
                                    controls.target.copy(center);
                                    camera.position.copy(center).add(new THREE.Vector3(maxDim * 0.6, maxDim * 0.4, maxDim));
                                    camera.near = maxDim / 1000;
                                    camera.far = maxDim * 100;
                                    camera.updateProjectionMatrix();
                                    controls.update();
                                }

                                window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                            } catch (innerErr) {
                                showError('Error setting up model: ' + innerErr.message);
                            }
                        }, undefined, function (error) {
                            showError('Failed to load preview: ' + (error && error.message ? error.message : error));
                        });
                    };

                    window.addEventListener('resize', function () {
                        camera.aspect = window.innerWidth / window.innerHeight;
                        camera.updateProjectionMatrix();
                        renderer.setSize(window.innerWidth, window.innerHeight);
                    });

                    function animate() {
                        requestAnimationFrame(animate);
                        controls.update();
                        renderer.render(scene, camera);
                    }
                    animate();

                    window.reloadModel('https://appassets.local/" + previewFileName + @"');
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(htmlContent);
        }

        // Dumps the current in-memory model to a fresh temp file for the preview to load. Each
        // reload writes a NEW file rather than overwriting the one the browser already read, which
        // would race its cache and could show pre-optimization geometry.
        private string WritePreviewFile()
        {
            var previous = _previewPath;
            _previewPath = Path.Combine(Path.GetTempPath(), $"glbmerger_optimize_preview_{_previewVersion++}.glb");
            _model.SaveGLB(_previewPath);

            if (previous != null)
            {
                try { File.Delete(previous); }
                catch (IOException) { /* still held by the browser - harmless, it's a temp file */ }
                catch (UnauthorizedAccessException) { }
            }

            return Path.GetFileName(_previewPath);
        }

        private void ReloadPreview()
        {
            if (_webView.CoreWebView2 == null) return;

            _viewerReady = false;
            string fileName = WritePreviewFile();
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"reloadModel('https://appassets.local/{EscapeJs(fileName)}');");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? action;
            try
            {
                action = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Action;
            }
            catch
            {
                return;
            }

            if (action != "ready" || IsDisposed) return;

            _viewerReady = true;
            PushWireframe();
        }

        private void PushWireframe()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setWireframe({(_chkWireframe.Checked ? "true" : "false")});");
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
        }
    }
}
