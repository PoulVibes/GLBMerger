using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    // The preview uses <model-viewer> for the visible model, for the same PBR rendering,
    // environment lighting and camera framing as AnimationTrimEditor. Note that the choice of
    // renderer was NOT what fixed the "far side of the model shows through the near side" bug -
    // that was the model's own alphaMode: BLEND materials landing in the transparent (no depth
    // write) pass, which every renderer does; see the setAlphaMode('OPAQUE') call in the 'load'
    // handler below.
    //
    // <model-viewer> exposes neither a wireframe mode nor a triangle-level pick, so a second,
    // invisible THREE.GLTFLoader parse of the same file supplies real geometry for both, and a
    // transparent overlay canvas - camera-synced to <model-viewer> every frame - draws everything
    // this tool adds on top of it: a depth-only pass of that geometry (so the overlay knows what
    // the model occludes), then the wireframe, the paint highlight and the brush cursor.
    //
    // One of the five modes hosted by ModelEditorForm (see EditorMode there).
    public class GeometryOptimizerEditor : UserControl
    {
        private readonly ModelRoot _model;

        private WebView2 _webView = null!;
        private TrackBar _sliderSkin = null!, _sliderRatio = null!;
        private Label _lblSkin = null!, _lblRatio = null!, _lblStatus = null!, _lblTotals = null!;
        private NumericUpDown _numError = null!;
        private CheckBox _chkLockBorders = null!, _chkVertexOrder = null!, _chkWireframe = null!;
        private Button _btnAnalyze = null!, _btnApply = null!, _btnRevert = null!;
        private DataGridView _grid = null!;

        private CheckBox _chkPaintMode = null!, _chkRestrictSelection = null!, _chkExcludeSelection = null!;

        // Guards the two region checkboxes clearing each other: unticking one from the other's
        // handler raises CheckedChanged again, which would tick the first straight back on.
        private bool _syncingSelectionMode;
        private TrackBar _sliderBrush = null!;
        private Label _lblBrush = null!, _lblSelection = null!;
        private Button _btnClearSelection = null!;

        private bool _viewerReady;
        private int _previewVersion;
        private string? _previewPath;

        // Taken once, immediately before the first Apply of the session - the model is shared with
        // every other editor mode and nothing else keeps a copy of the original geometry.
        private List<int[]>? _originalIndices;

        private Button _btnAnalyzeWatertight = null!, _btnApplyWatertight = null!, _btnRevertWatertight = null!;

        // Kept separate from _originalIndices: filling a hole appends vertices, which a plain
        // index-buffer snapshot can't undo, so this carries full vertex accessor contents for
        // whatever primitives a fill actually touched. Each button pair here manages its own
        // single-level revert, same as every other editor mode.
        private WatertightRepair.Snapshot? _watertightSnapshot;

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

            flow.Controls.Add(new Label
            {
                Text = "Paint a region",
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 4),
            });

            _chkPaintMode = new CheckBox
            {
                Text = "Paint mode (drag to select, Ctrl or right-click to erase)",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkPaintMode.CheckedChanged += (s, e) => PushPaintMode();
            flow.Controls.Add(_chkPaintMode);

            _lblBrush = new Label { Text = "Brush size: 5%", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderBrush = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 1, Maximum = 40, Value = 5,
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

            _chkRestrictSelection = new CheckBox
            {
                Text = "Optimize ONLY the painted region",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Enabled = false,
                Margin = new Padding(3, 0, 3, 2),
            };
            _chkRestrictSelection.CheckedChanged += (s, e) =>
                SyncSelectionMode(_chkRestrictSelection, _chkExcludeSelection);
            flow.Controls.Add(_chkRestrictSelection);

            _chkExcludeSelection = new CheckBox
            {
                Text = "Optimize everything EXCEPT the painted region",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Enabled = false,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkExcludeSelection.CheckedChanged += (s, e) =>
                SyncSelectionMode(_chkExcludeSelection, _chkRestrictSelection);
            flow.Controls.Add(_chkExcludeSelection);

            flow.Controls.Add(HelpText(
                "Two ways to use a painted region, and only one applies at a time - ticking either " +
                "clears the other. ONLY makes the paint the region to simplify; EXCEPT makes it the " +
                "region to protect and simplifies all the rest, which is the one to reach for on " +
                "faces, hands and logos. Either way the vertices right at the edge of the paint are " +
                "locked, so the side that moves can't pull away from the side that doesn't and open " +
                "a gap. Leave both off to optimize the whole model."));

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
                "Optimizing rewrites triangles only, so vertices the result no longer references - " +
                "and the index buffer each pass replaces - stay in the model, and it can grow rather " +
                "than shrink. Saving offers to rebuild and drop both."));

            flow.Controls.Add(new Label
            {
                Text = "Make Watertight",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 4),
            });

            flow.Controls.Add(HelpText(
                "Finds open holes - boundary edges with no triangle on the other side - and caps " +
                "each one with a new triangle fan. Boundaries shared between two parts of this " +
                "mesh (a material or UV seam) are recognized and left alone, not capped. A hole " +
                "that's meant to stay open, like a mouth interior, will get capped too since there's " +
                "no way to tell intent from geometry alone - revert if that happens."));

            flow.Controls.Add(HelpText(
                "When the material has a texture, caps look for an unused, blank-looking patch of " +
                "it and sample the fill from there, so the cap reads as a plain, unremarkable " +
                "surface instead of a smear across unrelated texture. Where no such patch exists, " +
                "the cap falls back to blending the hole's own edge colors."));

            _btnAnalyzeWatertight = MakeButton("Analyze Holes (dry run)");
            _btnAnalyzeWatertight.Click += async (s, e) => await RunWatertightAsync(apply: false);
            flow.Controls.Add(_btnAnalyzeWatertight);

            _btnApplyWatertight = MakeButton("Fill Holes");
            _btnApplyWatertight.Click += async (s, e) => await RunWatertightAsync(apply: true);
            flow.Controls.Add(_btnApplyWatertight);

            _btnRevertWatertight = MakeButton("Revert Watertight Fill");
            _btnRevertWatertight.Enabled = false;
            _btnRevertWatertight.Click += (s, e) => RevertWatertight();
            flow.Controls.Add(_btnRevertWatertight);

            _chkWireframe = new CheckBox { Text = "Wireframe preview", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
            _chkWireframe.CheckedChanged += (s, e) => PushWireframe();
            flow.Controls.Add(_chkWireframe);

            flow.Controls.Add(HelpText(
                "Edges are drawn over the shaded model rather than replacing it, and are hidden " +
                "where the model itself hides them - so this reads as triangle density on the " +
                "surface you are looking at, not an x-ray of the whole mesh."));

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
            bool exclude = _chkExcludeSelection.Checked;
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? selection = null;
            if (_chkRestrictSelection.Checked || exclude)
            {
                selection = await ReadSelectionAsync();
                if (IsDisposed) return;
                if (selection == null || selection.Values.All(s => s.Count == 0))
                {
                    _lblStatus.Text = exclude
                        ? "EXCEPT is checked, but nothing is painted yet - paint a region first, or uncheck it."
                        : "ONLY is checked, but nothing is painted yet - paint a region first, or uncheck it.";
                    return;
                }
            }

            SetBusy(true, apply ? "Optimizing..." : "Analyzing...");

            var options = CurrentOptions();
            options.Selection = selection;
            options.InvertSelection = exclude;
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

        private async Task RunWatertightAsync(bool apply)
        {
            SetBusy(true, apply ? "Filling holes..." : "Analyzing holes...");

            WatertightRepair.Report report;
            try
            {
                report = await Task.Run(() => WatertightRepair.Analyze(_model));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Hole analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            if (!report.HasChanges)
            {
                SetBusy(false, report.HolesFound == 0 && report.UnresolvedEdges == 0
                    ? "No open holes found - this model is already watertight."
                    : $"Found {report.UnresolvedEdges} boundary edge(s) that couldn't be resolved into simple loops - nothing else to fill.");
                return;
            }

            string summary = $"Found {report.HolesFound:N0} hole(s): {report.TrianglesAdded:N0} triangle(s) and " +
                $"{report.VerticesAdded:N0} vertex/vertices would be added." +
                (report.PrimitivesUsingTexturePatch > 0
                    ? $" {report.PrimitivesUsingTexturePatch} mesh part(s) will texture their cap from a found blank spot in the atlas."
                    : "") +
                (report.UnresolvedEdges > 0 ? $" {report.UnresolvedEdges} boundary edge(s) left unresolved." : "");

            if (!apply)
            {
                SetBusy(false, summary + " Nothing changed yet.");
                return;
            }

            _watertightSnapshot ??= WatertightRepair.TakeSnapshot(report, _model);
            WatertightRepair.Apply(report, _model);
            _btnRevertWatertight.Enabled = true;

            SetBusy(false, summary + " Included the next time you save the merge.");
            ShowBaseline();
            ReloadPreview();
        }

        private void RevertWatertight()
        {
            if (_watertightSnapshot == null) return;

            WatertightRepair.Restore(_model, _watertightSnapshot);
            _watertightSnapshot = null;
            _btnRevertWatertight.Enabled = false;

            ShowBaseline();
            _lblStatus.Text = "Watertight fill reverted to the original merge result.";
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
            _btnAnalyzeWatertight.Enabled = !busy;
            _btnApplyWatertight.Enabled = !busy;
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

            // <model-viewer> renders the visible model (see the class comment for why - it gets
            // correct backface rendering for free, which a hand-rolled Three.js scene here
            // previously did not). Painting still needs real triangle geometry to hit-test
            // against, which <model-viewer> doesn't expose (only a position+normal pick via
            // positionAndNormalFromPoint) - so a second, invisible THREE.GLTFLoader parse of the
            // SAME file supplies that, purely as a data source that's never added to any renderer.
            // A small transparent <canvas> overlay, camera-synced to <model-viewer> every frame,
            // draws the paint highlight on top of it.
            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewerStack { position: relative; width: 100%; height: 100%; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; position: absolute; top: 0; left: 0; }
                    #overlayCanvas { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; }
                    #error-overlay {
                        position: absolute; top: 10px; left: 10px; right: 10px;
                        background: rgba(139, 0, 0, 0.92); color: #fff; padding: 12px;
                        border-radius: 6px; font-family: monospace; font-size: 12px;
                        white-space: pre-wrap; display: none; max-height: 40%; overflow: auto;
                    }
                </style>
            </head>
            <body>
                <div id='viewerStack'>
                    <model-viewer id='viewer' src='https://appassets.local/" + previewFileName + @"' camera-controls
                        shadow-intensity='1' environment-image='neutral' exposure='1'>
                    </model-viewer>
                    <canvas id='overlayCanvas'></canvas>
                </div>
                <div id='error-overlay'></div>
                <script>
                    function showError(msg) {
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };

                    var viewer = document.querySelector('#viewer');
                    var loaded = false;
                    var currentUrl = viewer.getAttribute('src');

                    viewer.addEventListener('load', function () {
                        (viewer.model.materials || []).forEach(function (mat) {
                            // Forcing OPAQUE is what stops the far side of the model showing
                            // through the near side. Assets exported with alphaMode BLEND but no
                            // actual translucency (alpha 1.0 - common out of Meshy/Blender) land in
                            // the renderer's transparent pass, which draws with depth writes OFF,
                            // so nothing occludes anything: sleeve openings, the jersey hem and the
                            // far foot all paint over the torso as soon as the model is rotated.
                            // Unconditional rather than only-when-alpha-is-1 on purpose: the alpha
                            // may live in the base-colour texture, which isn't readable from here,
                            // and for a tool about triangle density a solid model is the right
                            // preview anyway. AnimationTrimEditor already gets this for free from
                            // isolateMaterial(-1); it just isn't obvious that's what's doing it.
                            mat.setAlphaMode('OPAQUE');

                            // Same preview-only matte-plastic tweak AnimationTrimEditor applies -
                            // never touches the saved file.
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });
                        loaded = true;
                        window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));

                        // Deliberately started only once <model-viewer>'s own fetch of this exact
                        // URL has finished, not in parallel with it - two concurrent requests for
                        // the same freshly-written temp file raced the WebView2 virtual host's
                        // caching and was the actual cause of the partial/edge-transparency
                        // glitches seen here (never in AnimationTrimEditor, which only ever fetches
                        // the file once). Painting simply becomes available a moment after the
                        // model appears instead of at the same instant.
                        loadHitTestGeometry(currentUrl);
                    });

                    // --- Paint-a-region tool ---------------------------------------------------
                    // paintableMeshes comes from a SEPARATE, invisible GLTFLoader parse of the
                    // same file (loadHitTestGeometry below) - it exists purely so a pick can be
                    // matched to a triangle index, never to render anything. Each entry is tagged
                    // with the (meshIndex, primIndex) it corresponds to in GeometryOptimizer's own
                    // primitive list (see the mesh-level glTF extras set in WritePreviewFile),
                    // exactly the same tagging/lookup convention the old raw-Three.js viewer used.
                    var paintableMeshes = [];      // { meshIndex, primIndex, object, centroids, radii, normals }
                    var selection = {};             // 'meshIndex_primIndex' -> Set<triangleIndex>
                    var paintMode = false;
                    var painting = false;
                    var wireframe = false;
                    // The brush is sized as a FRACTION of the model, so the world-space radius can
                    // only be resolved once the model's dimensions are known. Keeping the fraction
                    // (rather than only the resolved radius) is what makes that possible: .NET
                    // pushes the slider value on 'ready', which fires when <model-viewer> finishes
                    // loading - well before the hit-test parse that measures the model - so the
                    // radius has to be recomputed when modelMaxDim lands. Without that it stayed at
                    // 0.05 WORLD UNITS on a model tens of units tall, i.e. smaller than a single
                    // triangle, which is why a click painted one face or none at all.
                    var brushFraction = 0.05;
                    var brushRadius = 0.05;
                    var modelMaxDim = 1;

                    function resolveBrushRadius() { brushRadius = brushFraction * (modelMaxDim || 1); }

                    var overlayCanvas = document.querySelector('#overlayCanvas');
                    var overlayRenderer = new THREE.WebGLRenderer({ canvas: overlayCanvas, alpha: true, antialias: true });
                    overlayRenderer.setPixelRatio(window.devicePixelRatio);
                    overlayRenderer.setSize(window.innerWidth, window.innerHeight);
                    var overlayScene = new THREE.Scene();
                    var overlayCamera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.01, 10000);
                    var overlayGroup = new THREE.Group();
                    overlayScene.add(overlayGroup);
                    var highlightMaterial = new THREE.MeshBasicMaterial({
                        color: 0xffee00, transparent: true, opacity: 0.5, depthTest: true,
                        side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
                    });

                    // depthTest on the highlight above only tests against THIS canvas's depth
                    // buffer, and <model-viewer> renders the model into a completely separate
                    // canvas underneath - so on its own the highlight composites over the model
                    // whether or not the model is in front of it, and paint on the far side bleeds
                    // through the near side as soon as you rotate. The fix is to put the model into
                    // this canvas's depth buffer too: the hit-test geometry (already parsed, and
                    // already the thing the highlight indexes into) is drawn first as a depth-only
                    // pass - colorWrite false, so it stays invisible and the <model-viewer> image
                    // shows through untouched, but it writes the depth that then occludes the
                    // highlight correctly. Opaque, so three.js draws this whole group before the
                    // transparent highlight pass regardless of scene order.
                    // The polygon offset pushes the mask slightly AWAY from the camera. Everything
                    // else drawn here (wireframe lines, the brush ring, the highlight) sits exactly
                    // ON this surface, so without the nudge they z-fight with it and break up into
                    // dashes.
                    var depthMaskMaterial = new THREE.MeshBasicMaterial({
                        colorWrite: false, side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
                    });
                    var depthMaskGroup = new THREE.Group();
                    overlayScene.add(depthMaskGroup);

                    function rebuildDepthMask() {
                        for (var i = depthMaskGroup.children.length - 1; i >= 0; i--) {
                            depthMaskGroup.remove(depthMaskGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var maskMesh = new THREE.Mesh(meshInfo.object.geometry, depthMaskMaterial);
                            maskMesh.matrixAutoUpdate = false;
                            maskMesh.matrix.copy(meshInfo.object.matrixWorld);
                            depthMaskGroup.add(maskMesh);
                        });
                    }

                    // --- Wireframe -----------------------------------------------------------
                    // <model-viewer> has no wireframe API, so this draws the edges itself, into the
                    // overlay canvas, from the same hit-test geometry everything else here uses.
                    // The depth mask above is what makes it read as a wireframe ON the model rather
                    // than an x-ray of it: edges on the far side are occluded exactly as the shaded
                    // surface underneath occludes them.
                    //
                    // Built only while it's switched on, and thrown away again when it's switched
                    // off - WireframeGeometry expands to six vertices per triangle, which is a lot
                    // to keep resident for a mode that's mostly off, on the one model in the app
                    // most likely to be dense enough to care.
                    var wireframeMaterial = new THREE.LineBasicMaterial({
                        color: 0x8ab4f8, transparent: true, opacity: 0.55, depthTest: true,
                    });
                    var wireframeGroup = new THREE.Group();
                    overlayScene.add(wireframeGroup);

                    function rebuildWireframe() {
                        for (var i = wireframeGroup.children.length - 1; i >= 0; i--) {
                            var old = wireframeGroup.children[i];
                            wireframeGroup.remove(old);
                            old.geometry.dispose();
                        }
                        if (!wireframe) return;
                        paintableMeshes.forEach(function (meshInfo) {
                            var lines = new THREE.LineSegments(
                                new THREE.WireframeGeometry(meshInfo.object.geometry), wireframeMaterial);
                            lines.matrixAutoUpdate = false;
                            lines.matrix.copy(meshInfo.object.matrixWorld);
                            wireframeGroup.add(lines);
                        });
                    }

                    window.setWireframe = function (enabled) {
                        wireframe = enabled;
                        rebuildWireframe();
                    };

                    // --- Brush cursor --------------------------------------------------------
                    // The cursor is drawn by re-rendering the model geometry itself with a shader
                    // that keeps only the fragments within the brush radius of the cursor point and
                    // discards the rest. That makes it a real projection: the disc wraps over
                    // curvature, wraps around a limb, stops at the edge of a sleeve, and splits
                    // across a gap - because it IS the surface, clipped to the brush. A flat disc
                    // placed at the pick point can't do any of that; on anything but a flat panel it
                    // sinks into the geometry it's meant to be lying on.
                    //
                    // It also matches what a click paints, since painting is the same ball-vs-
                    // surface test (see paintAllInBrush) - so the cursor is a preview of the result
                    // rather than an approximation of it.
                    //
                    // Geometry is SHARED with the depth mask, not copied, so this costs one extra
                    // draw per frame and no extra memory. It's only in the scene while paint mode is
                    // on and the pointer is over the model.
                    var brushDecalMaterial = new THREE.ShaderMaterial({
                        uniforms: {
                            uCenter: { value: new THREE.Vector3() },
                            uRadius: { value: 1 },
                            uColor: { value: new THREE.Color(0xffee00) },
                        },
                        vertexShader: [
                            'varying vec3 vWorld;',
                            'void main() {',
                            '    vec4 world = modelMatrix * vec4(position, 1.0);',
                            '    vWorld = world.xyz;',
                            '    gl_Position = projectionMatrix * viewMatrix * world;',
                            '}',
                        ].join('\n'),
                        // The rim is just the outer slice of the same disc, brightened - it reads as
                        // an outline without needing separate geometry to draw one.
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
                    });
                    var brushDecalGroup = new THREE.Group();
                    brushDecalGroup.visible = false;
                    overlayScene.add(brushDecalGroup);

                    function rebuildBrushDecal() {
                        for (var i = brushDecalGroup.children.length - 1; i >= 0; i--) {
                            brushDecalGroup.remove(brushDecalGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var decal = new THREE.Mesh(meshInfo.object.geometry, brushDecalMaterial);
                            decal.matrixAutoUpdate = false;
                            decal.matrix.copy(meshInfo.object.matrixWorld);
                            // The shader discards nearly all of it, so three.js's own frustum test
                            // (which only sees the full model bounds) can't help and its result
                            // would be misleading if the cursor sits off-screen. Cheaper to keep it
                            // on and let the discard do the work.
                            decal.frustumCulled = false;
                            brushDecalGroup.add(decal);
                        });
                    }

                    function showBrushCursor(point) {
                        brushDecalMaterial.uniforms.uCenter.value.copy(point);
                        brushDecalMaterial.uniforms.uRadius.value = brushRadius;
                        brushDecalGroup.visible = true;
                    }

                    function hideBrushCursor() { brushDecalGroup.visible = false; }

                    // Mirrors <model-viewer>'s own orbit camera every frame (theta/phi/radius
                    // around cameraTarget, its documented public API) so the overlay canvas lines
                    // up with whatever <model-viewer> is currently showing, including mid-drag and
                    // during its own damped settle after a release.
                    function syncOverlayCamera() {
                        // <model-viewer> loads via <script type='module'>, which is deferred - this
                        // inline script (and its render loop, kicked off a few lines down) starts
                        // running before the custom element has upgraded, so its camera API isn't
                        // there yet on the first several frames. Once it exists it stays there, so
                        // this only ever bails out briefly at startup.
                        if (typeof viewer.getCameraOrbit !== 'function') return;
                        var orbit = viewer.getCameraOrbit();
                        var target = viewer.getCameraTarget();
                        overlayCamera.fov = viewer.getFieldOfView();
                        overlayCamera.aspect = window.innerWidth / window.innerHeight;
                        // Tracked to the current orbit distance rather than left at a fixed
                        // 0.01..10000. That default spans six orders of magnitude, which leaves so
                        // little depth precision that the highlight and the depth mask it is tested
                        // against z-fight and the paint speckles through the surface it sits on.
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

                    // Per-triangle world-space data, computed once at load, so no paint event has to
                    // re-touch the raw attribute buffers:
                    //
                    //   centroid - the triangle's centre.
                    //   radius   - centroid to its furthest vertex. Turns the centroid into a
                    //              bounding sphere, which is what makes 'any face PARTIALLY inside
                    //              the brush' testable: the brush ball and the triangle overlap when
                    //              their centres are closer than the sum of their radii. A centroid-
                    //              only test misses a large triangle the brush lands in the middle
                    //              of, and clips small ones sticking into the edge of the circle.
                    //   normal   - for the facing test in paintAllInBrush. Taken from the NORMAL
                    //              attribute where the asset has one (it's what the shading the user
                    //              is actually looking at comes from) and derived from winding
                    //              otherwise.
                    function computeTriangleData(mesh) {
                        mesh.updateMatrixWorld(true);
                        var index = mesh.geometry.index;
                        var pos = mesh.geometry.attributes.position;
                        var nrm = mesh.geometry.attributes.normal;
                        var triCount = index.count / 3;
                        var centroids = new Array(triCount);
                        var radii = new Float32Array(triCount);
                        var normals = new Float32Array(triCount * 3);

                        // Normals transform by the inverse-transpose, not the model matrix - a
                        // non-uniform scale anywhere in the node chain skews them otherwise.
                        var normalMatrix = new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld);
                        var a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3();
                        var n = new THREE.Vector3(), tmp = new THREE.Vector3(), edge = new THREE.Vector3();

                        for (var t = 0; t < triCount; t++) {
                            var ia = index.getX(t * 3), ib = index.getX(t * 3 + 1), ic = index.getX(t * 3 + 2);
                            a.fromBufferAttribute(pos, ia).applyMatrix4(mesh.matrixWorld);
                            b.fromBufferAttribute(pos, ib).applyMatrix4(mesh.matrixWorld);
                            c.fromBufferAttribute(pos, ic).applyMatrix4(mesh.matrixWorld);

                            var centroid = new THREE.Vector3().add(a).add(b).add(c).multiplyScalar(1 / 3);
                            centroids[t] = centroid;
                            radii[t] = Math.sqrt(Math.max(
                                centroid.distanceToSquared(a),
                                centroid.distanceToSquared(b),
                                centroid.distanceToSquared(c)));

                            if (nrm) {
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

                    // Every triangle that the brush ball touches AT ALL, in every primitive, facing
                    // the camera. Three things that matters for, in the order they bit:
                    //
                    //   Partial overlap counts. The test is bounding-sphere against the brush ball
                    //   (centroid distance minus the triangle's own radius), so a face only clipped
                    //   by the edge of the circle is in, and a face big enough to swallow the whole
                    //   circle is in. That's what 'even partially inside' means, and it also makes
                    //   the face under the cursor land in the selection for free - the pick point is
                    //   on it, so it can't fail the test.
                    //
                    //   No connectivity requirement. This walks the triangles directly instead of
                    //   flood-filling across shared edges, so the brush covers everything under the
                    //   circle even where the surface is split - across the gap between an arm and
                    //   the torso, or between a shoe and the leg above it.
                    //
                    //   Facing the camera only. Without it a brush wider than a limb is thick paints
                    //   the far side too, and reaching inside a sleeve or under a hem paints the
                    //   inner surface - none of which is visible to click on, all of which lands in
                    //   the selection and then gets simplified.
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

                                // Positive dot means the face turns away from the eye.
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

                            var srcGeom = meshInfo.object.geometry;
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
                            overlayGeom.setIndex(new THREE.BufferAttribute(newIndex, 1));
                            var overlayMesh = new THREE.Mesh(overlayGeom, highlightMaterial);
                            overlayMesh.matrixAutoUpdate = false;
                            overlayMesh.matrix.copy(meshInfo.object.matrixWorld);
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

                    // <model-viewer>'s positionAndNormalFromPoint is the only pick API it exposes.
                    // It returns a point on the surface, not a triangle index - which is all the
                    // brush needs, since both the cursor and paintAllInBrush work from a world-space
                    // ball centred on that point rather than from a picked face.
                    function pickSurface(event) {
                        if (typeof viewer.positionAndNormalFromPoint !== 'function') return null;
                        var rect = viewer.getBoundingClientRect();
                        return viewer.positionAndNormalFromPoint(event.clientX - rect.left, event.clientY - rect.top);
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

                    // Suppressing the orbit during a stroke is done by keeping the event away from
                    // <model-viewer> entirely, NOT by toggling its cameraControls property.
                    //
                    // Toggling it corrupts its internal state, permanently. Its SmoothControls
                    // tracks live pointers in an array: onPointerDown pushes, onPointerUp splices.
                    // Setting cameraControls = false mid-gesture makes it tear off its own
                    // pointerup listener before that pointer is released, so the entry is never
                    // removed - and a drag is only ever STARTED when that array is empty. One
                    // stroke and orbiting is dead for the rest of the session (three strokes and
                    // its onPointerDown bails immediately on pointers.length > 2), which is only
                    // noticed on leaving paint mode because that's the first time you try to orbit.
                    //
                    // CAPTURE phase, and it has to be: SmoothControls binds its pointerdown to an
                    // inner <div> inside <model-viewer>'s shadow root, not to the element itself,
                    // so that listener is deeper in the path than anything registered on the host.
                    // A normal (bubble) listener here runs AFTER it and stopping propagation at
                    // that point is already too late - the pointer has been pushed. Capture runs
                    // root-to-target, so this fires while the event is still on its way down and
                    // stopPropagation keeps it from ever arriving.
                    //
                    // Only stopped when the pick actually lands on the model, so dragging the
                    // background still orbits normally while in paint mode.
                    viewer.addEventListener('pointerdown', function (event) {
                        if (!paintMode || event.button === 1) return;
                        if (pickAndPaint(event)) {
                            painting = true;
                            event.stopPropagation();
                            event.preventDefault();
                        }
                    }, true);
                    viewer.addEventListener('pointermove', function (event) {
                        if (!paintMode) return;
                        if (painting) { pickAndPaint(event); return; }

                        // Not dragging: track the disc across the surface so the brush footprint is
                        // visible before committing to a stroke.
                        var hit = pickSurface(event);
                        if (!hit) { hideBrushCursor(); return; }
                        showBrushCursor(new THREE.Vector3(hit.position.x, hit.position.y, hit.position.z));
                    });
                    viewer.addEventListener('pointerleave', function () {
                        if (!painting) hideBrushCursor();
                    });
                    window.addEventListener('pointerup', function () {
                        if (painting) {
                            painting = false;
                            rebuildOverlay();
                            pushSelectionCount();
                        }
                    });
                    viewer.addEventListener('contextmenu', function (event) {
                        if (paintMode) event.preventDefault();
                    });

                    // Invisible parse of the same file, purely for hit-testing data - never added
                    // to a scene, never rendered. Runs independently of <model-viewer>'s own load;
                    // painting simply isn't possible until this resolves, same as it wasn't
                    // possible before <model-viewer> finished loading either.
                    var _skinIndex = new THREE.Vector4();
                    var _skinWeight = new THREE.Vector4();
                    var _bindPos = new THREE.Vector3();
                    var _bindNrm = new THREE.Vector3();
                    var _boneMatrix = new THREE.Matrix4();
                    var _boneNormalMat = new THREE.Matrix3();
                    var _skinTmp = new THREE.Vector3();
                    var _skinTmpN = new THREE.Vector3();

                    // Bakes the mesh's current pose into a plain (unskinned) BufferGeometry, in the
                    // mesh's own local frame - i.e. the position a static mesh would need so that,
                    // once mesh.matrixWorld is applied on top (same as every other geometry here),
                    // it lands exactly where <model-viewer>'s GPU skinning renders the posed
                    // surface. Everything downstream (wireframe, depth mask, brush decal, paint
                    // overlay, triangle data) reads geometry.attributes.position directly and
                    // applies only matrixWorld - none of it runs the skinning shader, so without
                    // this the bind pose (often a T-pose) is what gets drawn and hit-tested instead
                    // of the actual pose. That's most visible on whatever moves furthest from bind
                    // pose, typically the arms.
                    function bakeSkinnedGeometry(mesh) {
                        var srcGeom = mesh.geometry;
                        if (!mesh.isSkinnedMesh || !mesh.skeleton
                            || !srcGeom.attributes.skinIndex || !srcGeom.attributes.skinWeight) {
                            return srcGeom;
                        }
                        var pos = srcGeom.attributes.position;
                        var nrm = srcGeom.attributes.normal;
                        var skinIndexAttr = srcGeom.attributes.skinIndex;
                        var skinWeightAttr = srcGeom.attributes.skinWeight;
                        var outPos = new Float32Array(pos.count * 3);
                        var outNrm = nrm ? new Float32Array(nrm.count * 3) : null;
                        var bones = mesh.skeleton.bones;
                        var boneInverses = mesh.skeleton.boneInverses;

                        for (var i = 0; i < pos.count; i++) {
                            _skinIndex.fromBufferAttribute(skinIndexAttr, i);
                            _skinWeight.fromBufferAttribute(skinWeightAttr, i);
                            _bindPos.fromBufferAttribute(pos, i).applyMatrix4(mesh.bindMatrix);
                            if (outNrm) _bindNrm.fromBufferAttribute(nrm, i).transformDirection(mesh.bindMatrix);

                            var vx = 0, vy = 0, vz = 0, nx = 0, ny = 0, nz = 0;
                            for (var w = 0; w < 4; w++) {
                                var weight = _skinWeight.getComponent(w);
                                if (weight === 0) continue;
                                var boneIndex = _skinIndex.getComponent(w);
                                _boneMatrix.multiplyMatrices(bones[boneIndex].matrixWorld, boneInverses[boneIndex]);
                                _skinTmp.copy(_bindPos).applyMatrix4(_boneMatrix);
                                vx += _skinTmp.x * weight; vy += _skinTmp.y * weight; vz += _skinTmp.z * weight;
                                if (outNrm) {
                                    _boneNormalMat.getNormalMatrix(_boneMatrix);
                                    _skinTmpN.copy(_bindNrm).applyMatrix3(_boneNormalMat);
                                    nx += _skinTmpN.x * weight; ny += _skinTmpN.y * weight; nz += _skinTmpN.z * weight;
                                }
                            }
                            _skinTmp.set(vx, vy, vz).applyMatrix4(mesh.bindMatrixInverse);
                            outPos[i * 3] = _skinTmp.x; outPos[i * 3 + 1] = _skinTmp.y; outPos[i * 3 + 2] = _skinTmp.z;
                            if (outNrm) {
                                _skinTmpN.set(nx, ny, nz).transformDirection(mesh.bindMatrixInverse);
                                outNrm[i * 3] = _skinTmpN.x; outNrm[i * 3 + 1] = _skinTmpN.y; outNrm[i * 3 + 2] = _skinTmpN.z;
                            }
                        }

                        var baked = new THREE.BufferGeometry();
                        baked.setAttribute('position', new THREE.BufferAttribute(outPos, 3));
                        if (outNrm) baked.setAttribute('normal', new THREE.BufferAttribute(outNrm, 3));
                        if (srcGeom.index) baked.setIndex(srcGeom.index);
                        return baked;
                    }

                    var hitTestLoader = new THREE.GLTFLoader();
                    function loadHitTestGeometry(url) {
                        hitTestLoader.load(url, function (gltf) {
                            gltf.scene.updateMatrixWorld(true);
                            var meshes = [];
                            gltf.scene.traverse(function (obj) {
                                if (!obj.isMesh || !obj.userData || obj.userData.glbMergerMeshIndex === undefined) return;
                                var meshIndex = obj.userData.glbMergerMeshIndex;
                                var primIndex = 0;
                                var parent = obj.parent;
                                // A mesh with more than one primitive comes back from GLTFLoader as
                                // a Group whose children are one Mesh per primitive, in primitive
                                // order - every child in that group carries the SAME mesh-level
                                // extras tag, so that's how a plain single-primitive mesh
                                // (primIndex always 0) is told apart from one of several siblings
                                // sharing a mesh (primIndex = its position among those siblings).
                                if (parent && parent.children.length > 1 && parent.children.every(function (c) {
                                    return c.userData && c.userData.glbMergerMeshIndex === meshIndex;
                                })) {
                                    primIndex = parent.children.indexOf(obj);
                                }
                                obj.geometry = bakeSkinnedGeometry(obj);
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

                            // Only now is the brush fraction resolvable into a world-space radius,
                            // and only now is there geometry to draw a wireframe from - .NET pushed
                            // both of these settings back when <model-viewer> finished loading,
                            // which is earlier than this.
                            resolveBrushRadius();
                            rebuildWireframe();
                        }, undefined, function (error) {
                            showError('Failed to load paint hit-test geometry: ' + (error && error.message ? error.message : error));
                        });
                    }
                    // --- end paint-a-region tool -------------------------------------------------

                    // Points <model-viewer> at a freshly written GLB after an Apply/Revert. 'load'
                    // fires again afterward, which is what re-announces readiness so .NET can
                    // re-push state (see AnimationTrimEditor's identical reloadModel/'load' split).
                    window.reloadModel = function (url) {
                        loaded = false;
                        currentUrl = url;
                        // The reloaded geometry's triangle indices don't correspond to whatever a
                        // previous paint selection was recorded against (Apply just rewrote them),
                        // so painting starts over on every reload. loadHitTestGeometry itself is
                        // deferred to the 'load' handler above (see the comment there for why).
                        paintableMeshes = [];
                        selection = {};
                        rebuildOverlay();
                        rebuildDepthMask();
                        rebuildWireframe();
                        rebuildBrushDecal();
                        hideBrushCursor();
                        viewer.src = url;
                    };

                    window.addEventListener('resize', function () {
                        overlayRenderer.setSize(window.innerWidth, window.innerHeight);
                        overlayCamera.aspect = window.innerWidth / window.innerHeight;
                        overlayCamera.updateProjectionMatrix();
                    });

                    // No initial loadHitTestGeometry call here - <model-viewer>'s own 'load' event
                    // (which fires once for the src set on the element above) triggers it, same as
                    // every subsequent reload.
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

            // Tags every mesh with its own LogicalMeshes index via glTF extras, purely so the paint
            // tool's raycast hits (against the preview's Three.js objects) can be mapped back to a
            // GeometryOptimizer primitive - see the paint-tool JS below for how primitive index is
            // then recovered from sibling order. Extras are cleared again immediately after saving:
            // _model is the same instance the rest of the app saves, and this tag has no business
            // surviving into the real output file.
            var previousExtras = new JsonNode?[_model.LogicalMeshes.Count];
            for (int i = 0; i < _model.LogicalMeshes.Count; i++)
            {
                previousExtras[i] = _model.LogicalMeshes[i].Extras;
                _model.LogicalMeshes[i].Extras = new JsonObject { ["glbMergerMeshIndex"] = i };
            }
            try
            {
                _model.SaveGLB(_previewPath);
            }
            finally
            {
                for (int i = 0; i < _model.LogicalMeshes.Count; i++)
                    _model.LogicalMeshes[i].Extras = previousExtras[i]!;
            }

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

            // The reloaded model may have different triangle indices than whatever the paint
            // selection was recorded against (that's the whole point of Apply), so the JS side
            // throws its selection away on every reload - this just keeps the WinForms side in sync
            // with that.
            _lblSelection.Text = "0 triangles painted";
            SetSelectionModeAvailable(false);

            _viewerReady = false;
            string fileName = WritePreviewFile();
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"reloadModel('https://appassets.local/{EscapeJs(fileName)}');");
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
                PushWireframe();
                return;
            }

            if (message.Action == "selectionChanged")
            {
                int count = message.Count ?? 0;
                _lblSelection.Text = count == 1 ? "1 triangle painted" : $"{count:N0} triangles painted";
                SetSelectionModeAvailable(count > 0);
            }
        }

        private void PushPaintMode()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintMode({(_chkPaintMode.Checked ? "true" : "false")});");
        }

        // The two region modes are mutually exclusive, but checkboxes rather than radio buttons
        // because "neither" is a real, and the default, state: optimize the whole model. A radio
        // pair can't express that without a third "off" button.
        // Both region modes need something painted to mean anything, so they enable and clear
        // together. The guard is held across both so clearing one can't tick the other back on.
        private void SetSelectionModeAvailable(bool available)
        {
            _syncingSelectionMode = true;
            try
            {
                _chkRestrictSelection.Enabled = available;
                _chkExcludeSelection.Enabled = available;
                if (!available)
                {
                    _chkRestrictSelection.Checked = false;
                    _chkExcludeSelection.Checked = false;
                }
            }
            finally { _syncingSelectionMode = false; }
        }

        private void SyncSelectionMode(CheckBox changed, CheckBox other)
        {
            if (_syncingSelectionMode) return;
            if (!changed.Checked) return;

            _syncingSelectionMode = true;
            try { other.Checked = false; }
            finally { _syncingSelectionMode = false; }
        }

        private void PushWireframe()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setWireframe({(_chkWireframe.Checked ? "true" : "false")});");
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
            SetSelectionModeAvailable(false);
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync("clearPaintSelection();");
        }

        // Reads the paint tool's current selection back from the JS side. ExecuteScriptAsync
        // JSON-encodes whatever the script evaluates to, so a script that itself returns
        // JSON.stringify(...) comes back as a JSON string *literal* - it has to be unwrapped once
        // before the selection object inside it can be parsed.
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
                result[(meshIdx, primIdx)] = new HashSet<int>(tris);
            }
            return result;
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public int? Count { get; set; }
        }
    }
}
