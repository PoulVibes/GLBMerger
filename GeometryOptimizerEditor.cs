using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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
    // merge result in place, like every other editor mode.
    //
    // Unlike every other editor mode, though, this one keeps a full History rather than a single
    // "revert to how it arrived" step: it is the one mode that can silently alter geometry across
    // the whole model at once, and reaching a good result means stacking several passes at
    // different ratios and regions, so the state worth going back to is rarely the original. Every
    // pass - simplification, deletion, a planar cut, hole filling and the texture-warp fix alike -
    // records into GeometryHistory, and any of them can be clicked back to without destroying the
    // ones after it.
    //
    // Painting feeds three of those. For simplification it marks the region to reduce or the
    // region to protect (the ONLY/EXCEPT pair); for TriangleDeleter it marks the triangles to throw
    // away outright; for RegionRestorer it marks where the original triangles should come back.
    // PlanarCutter takes geometry off the model too, but by a plane rather than by paint - it's
    // positioned with its own controls and previewed as a translucent slab over the side that
    // goes, drawn into the same overlay canvas as the paint.
    //
    // The operations are picked from a dropdown at the top of the pane and only the chosen one's
    // controls are shown; the paint controls, the wireframe toggle, the status line, the results
    // grid and the History list are shared and stay put.
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
        private CheckBox _chkLockBorders = null!, _chkVertexOrder = null!, _chkWireframe = null!, _chkRebakeTexture = null!;

        // Bottom status bar: a progress bar for the texture rebuild, which is the one thing in
        // this pane slow enough to need one - a 4K atlas is millions of closest-point queries.
        private StatusStrip _statusStrip = null!;
        private ToolStripStatusLabel _progressLabel = null!;
        private ToolStripProgressBar _progressBar = null!;
        private Button _btnAnalyze = null!, _btnApply = null!;
        private DataGridView _grid = null!;

        private CheckBox _chkPaintMode = null!, _chkHidePaint = null!, _chkRestrictSelection = null!, _chkExcludeSelection = null!;

        // The highlight colour the viewer draws painted triangles in. Swatch button opens the
        // standard colour dialog; the choice lives for the session.
        private Button _btnPaintColor = null!;
        private System.Drawing.Color _paintColor = System.Drawing.Color.FromArgb(255, 238, 0);

        // Guards the two region checkboxes clearing each other: unticking one from the other's
        // handler raises CheckedChanged again, which would tick the first straight back on.
        private bool _syncingSelectionMode;
        private TrackBar _sliderBrush = null!;
        private Label _lblBrush = null!, _lblSelection = null!;
        private Button _btnClearSelection = null!;

        private bool _viewerReady;
        private int _previewVersion;
        private string? _previewPath;

        // A painted region to put back once the preview has reloaded after an Apply. The viewer
        // drops its selection on every reload (the triangle indices it was recorded against have
        // just been rewritten), so an operation that wants the paint to survive works out where
        // its triangles ended up and leaves the answer here for the 'ready' handler to push.
        private Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? _pendingSelection;

        private Button _btnAnalyzeDelete = null!, _btnDelete = null!;

        private ComboBox _cmbCutAxis = null!, _cmbCutSide = null!;
        private TrackBar _sliderCut = null!;
        private NumericUpDown _numCut = null!;
        private Label _lblCut = null!;
        private CheckBox _chkCapCut = null!, _chkShowCutPlane = null!;
        private Button _btnAnalyzeCut = null!, _btnApplyCut = null!;

        // World-space extent the cut plane's controls range over. Measured once, when the pane
        // opens, and deliberately not again after each pass: a cut that shrinks the model would
        // otherwise move the slider's endpoints out from under a position that was just dialled in.
        private (Vector3 Min, Vector3 Max) _worldBounds;

        // The plane's position along the chosen axis, in world units. Held separately from the
        // slider so that a value typed into the numeric box is used as typed - the slider only
        // has a thousand steps, which on a two-metre model is a couple of millimetres.
        private float _cutPosition;

        // The slider and the numeric box mirror each other; each one's ValueChanged writes the
        // other, which would raise ValueChanged straight back without this.
        private bool _syncingCut;
        private Button _btnAnalyzeWatertight = null!, _btnApplyWatertight = null!;
        private CheckBox _chkSealPlanar = null!;

        private NumericUpDown _numUvJump = null!;
        private Button _btnAnalyzeUv = null!, _btnApplyUv = null!;
        private Button _btnAnalyzeRestore = null!, _btnRestore = null!;

        private enum Operation { Simplify, Delete, Cut, Watertight, TextureWarp, Restore }

        // Dropdown order and labels, indexed by Operation.
        private static readonly (Operation Op, string Label)[] Operations =
        {
            (Operation.Simplify,    "Simplify (meshoptimizer)"),
            (Operation.Delete,      "Delete Painted Triangles"),
            (Operation.Cut,         "Planar Cut"),
            (Operation.Watertight,  "Make Watertight"),
            (Operation.TextureWarp, "Fix Texture Warp"),
            (Operation.Restore,     "Restore Painted Detail"),
        };

        private ComboBox _cmbOperation = null!;
        private FlowLayoutPanel _secSimplifySettings = null!, _secPaint = null!, _secSimplifyActions = null!,
            _secDelete = null!, _secCut = null!, _secWatertight = null!, _secUv = null!, _secRestore = null!;

        private Operation CurrentOperation => Operations[Math.Max(0, _cmbOperation.SelectedIndex)].Op;

        // The original merge result, indexed for closest-point queries (see UvWarpRepair). Built
        // the first time the texture-warp fix runs and kept for the session: History entry 0 never
        // changes, and building the spatial grids is the slow half of a run.
        private UvWarpRepair.Reference? _uvReference;

        // Every state the geometry has been in this session, including the untouched merge result
        // it started from - the model is shared with every other editor mode and nothing else keeps
        // a copy of it. Both kinds of operation here (simplify, fill holes) record into the one
        // list, so backing out of a fill and then out of the simplification that preceded it is
        // just two clicks down the History grid.
        private GeometryHistory _history = null!;
        private DataGridView _historyGrid = null!;

        // Restoring a state selects its row, which raises SelectionChanged again - without this the
        // handler would re-enter and restore the same state a second time.
        private bool _syncingHistory;

        public GeometryOptimizerEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;
            _history = new GeometryHistory(model);
            _worldBounds = PlanarCutter.WorldBounds(model);

            Dock = DockStyle.Fill;

            BuildUi();
            RefreshHistoryGrid();
            ShowBaseline();
            UpdateSimplifyEnabled();

            ThemeManager.Apply(this, darkMode);

            // After the theme, which paints every label the same foreground: the outcome line is
            // the one thing in the pane that is output rather than a control, and it's coloured
            // to say so. Dark green as asked; lifted a little on the dark theme so it still reads
            // against the near-black panel.
            _lblStatus.ForeColor = darkMode
                ? System.Drawing.Color.FromArgb(70, 170, 80)
                : System.Drawing.Color.DarkGreen;

            // The swatch shows the paint colour, not the theme's button colour.
            _btnPaintColor.BackColor = _paintColor;

            // ToolStrips aren't in ThemeManager's cases; painted to match the panel by hand.
            _statusStrip.BackColor = darkMode ? ThemeManager.DarkPanel : System.Drawing.SystemColors.Control;
            _statusStrip.ForeColor = darkMode ? ThemeManager.DarkFore : System.Drawing.SystemColors.ControlText;
            _progressLabel.ForeColor = _statusStrip.ForeColor;

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

            // One operation on screen at a time - the pane had grown to six sections of controls
            // stacked end to end, most of them irrelevant to whatever is being done at the moment.
            // The paint controls are shared by the three operations that take a painted region and
            // sit between the simplifier's settings and its buttons, where they always were.
            flow.Controls.Add(new Label { Text = "Operation:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
            _cmbOperation = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 330,
                Margin = new Padding(3, 0, 3, 10),
            };
            foreach (var (_, label) in Operations) _cmbOperation.Items.Add(label);
            flow.Controls.Add(_cmbOperation);

            var simplifySettings = _secSimplifySettings = NewSection();
            var paint = _secPaint = NewSection();
            var simplifyActions = _secSimplifyActions = NewSection();
            var delete = _secDelete = NewSection();
            var cut = _secCut = NewSection();
            var watertight = _secWatertight = NewSection();
            var uv = _secUv = NewSection();
            var restore = _secRestore = NewSection();

            _lblRatio = new Label { Text = "Keep 50% of triangles", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderRatio = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 5, Maximum = 100, Value = 50,
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderRatio.ValueChanged += (s, e) => _lblRatio.Text = $"Keep {_sliderRatio.Value}% of triangles";
            simplifySettings.Controls.Add(_lblRatio);
            simplifySettings.Controls.Add(_sliderRatio);

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
            simplifySettings.Controls.Add(errorRow);

            simplifySettings.Controls.Add(HelpText(
                "Whichever comes first wins: simplification stops at the keep ratio, or earlier if " +
                "it would exceed the error budget. For a near-lossless pass, use a high keep ratio " +
                "with a small budget."));

            _chkLockBorders = new CheckBox { Text = "Preserve mesh outlines", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 0) };
            simplifySettings.Controls.Add(_chkLockBorders);

            _chkVertexOrder = new CheckBox { Text = "Optimize vertex cache order", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 8) };
            simplifySettings.Controls.Add(_chkVertexOrder);

            _chkRebakeTexture = new CheckBox
            {
                Text = "Rebuild texture after optimization",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 0, 3, 2),
            };
            simplifySettings.Controls.Add(_chkRebakeTexture);

            simplifySettings.Controls.Add(HelpText(
                "After the pass, every texel under a triangle that changed is looked up on the " +
                "original surface and rewritten with what the original texture showed there, so " +
                "the texture is pixel-accurate on the new triangles instead of stretched across " +
                "them. Nothing is added to the geometry; what's genuinely lost (a flattened " +
                "silhouette) stays lost. Texels that already match are left untouched, so nothing " +
                "blurs. Every texture the model's materials use is rebuilt. Slow on a big atlas - " +
                "watch the bar at the bottom of the window. Undone together with the pass in History."));

            _lblSkin = new Label { Text = SkinLabel(0), AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderSkin = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 0, Maximum = 100, Value = 0,
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderSkin.ValueChanged += (s, e) => _lblSkin.Text = SkinLabel(_sliderSkin.Value);
            simplifySettings.Controls.Add(_lblSkin);
            simplifySettings.Controls.Add(_sliderSkin);

            simplifySettings.Controls.Add(HelpText(
                "Collapsing an edge re-interpolates its skin weights. At 0% only vertices whose " +
                "neighbours share identical weights can move, so deformation cannot change."));

            paint.Controls.Add(new Label
            {
                Text = "Paint a region",
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 4),
            });

            // Paint mode and Hide paint side by side. Hiding only stops the highlight being drawn -
            // the selection itself is untouched, so a painted region can be looked at without its
            // overlay and still be operated on.
            var paintRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            _chkPaintMode = new CheckBox
            {
                Text = "Paint mode (drag; Ctrl or right-click erases)",
                AutoSize = true,
                Margin = new Padding(3, 0, 8, 4),
            };
            _chkPaintMode.CheckedChanged += (s, e) => PushPaintMode();
            paintRow.Controls.Add(_chkPaintMode);

            _chkHidePaint = new CheckBox { Text = "Hide paint", AutoSize = true, Margin = new Padding(0, 0, 3, 4) };
            _chkHidePaint.CheckedChanged += (s, e) => PushPaintVisible();
            paintRow.Controls.Add(_chkHidePaint);
            paint.Controls.Add(paintRow);

            // In tenths of a percent of the model's size: a window frame on a building or a
            // fingernail on a character wants a brush well under 1%.
            _lblBrush = new Label { Text = "Brush size: 5.0%", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderBrush = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 2, Maximum = 400, Value = 50,
                TickFrequency = 50, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderBrush.ValueChanged += (s, e) => { _lblBrush.Text = $"Brush size: {_sliderBrush.Value / 10.0:0.0}%"; PushBrushRadius(); };
            paint.Controls.Add(_lblBrush);
            paint.Controls.Add(_sliderBrush);

            var colorRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            colorRow.Controls.Add(new Label { Text = "Paint color:", AutoSize = true, Margin = new Padding(3, 6, 6, 4) });
            _btnPaintColor = new Button
            {
                Width = 60, Height = 24, Margin = new Padding(0, 2, 3, 4),
                BackColor = _paintColor, FlatStyle = FlatStyle.Flat, Text = "",
            };
            _btnPaintColor.FlatAppearance.BorderColor = System.Drawing.Color.Gray;
            _btnPaintColor.Click += (s, e) =>
            {
                using var dlg = new ColorDialog { Color = _paintColor, FullOpen = true };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                _paintColor = dlg.Color;
                _btnPaintColor.BackColor = _paintColor;
                PushPaintColor();
            };
            colorRow.Controls.Add(_btnPaintColor);
            paint.Controls.Add(colorRow);

            _btnClearSelection = MakeButton("Clear Selection");
            _btnClearSelection.Click += (s, e) => ClearSelection();
            paint.Controls.Add(_btnClearSelection);

            _lblSelection = new Label { Text = "0 triangles painted", AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
            paint.Controls.Add(_lblSelection);

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
            simplifyActions.Controls.Add(_chkRestrictSelection);

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
            simplifyActions.Controls.Add(_chkExcludeSelection);

            simplifyActions.Controls.Add(HelpText(
                "Two ways to use a painted region, and only one applies at a time - ticking either " +
                "clears the other. ONLY makes the paint the region to simplify; EXCEPT makes it the " +
                "region to protect and simplifies all the rest, which is the one to reach for on " +
                "faces, hands and logos. Either way the vertices right at the edge of the paint are " +
                "locked, so the side that moves can't pull away from the side that doesn't and open " +
                "a gap. Leave both off to optimize the whole model."));

            _btnAnalyze = MakeButton("Analyze (dry run)");
            _btnAnalyze.Click += async (s, e) => await RunAnalyzeAsync(apply: false);
            simplifyActions.Controls.Add(_btnAnalyze);

            _btnApply = MakeButton("Apply to Merge");
            _btnApply.Click += async (s, e) => await RunAnalyzeAsync(apply: true);
            simplifyActions.Controls.Add(_btnApply);

            simplifyActions.Controls.Add(HelpText(
                "Optimizing rewrites triangles only, so vertices the result no longer references - " +
                "and the index buffer each pass replaces - stay in the model, and it can grow rather " +
                "than shrink. Saving offers to rebuild and drop both."));

            delete.Controls.Add(HelpText(
                "Throws the painted triangles away outright, then drops every vertex no triangle " +
                "references any more - including ones earlier simplify passes over the same mesh " +
                "part left behind. For geometry that shouldn't be in the merge at all: interior " +
                "surfaces no camera sees, a prop's backing plane, decals buried under other parts."));

            delete.Controls.Add(HelpText(
                "This leaves a hole - nothing is closed up behind it, so paint with the far side " +
                "of the model in mind. The ONLY/EXCEPT checkboxes above belong to simplification; " +
                "deletion always takes exactly what is painted, and a mesh part with every triangle " +
                "painted is left alone rather than emptied."));

            _btnAnalyzeDelete = MakeButton("Analyze Deletion (dry run)");
            _btnAnalyzeDelete.Click += async (s, e) => await RunDeleteAsync(apply: false);
            delete.Controls.Add(_btnAnalyzeDelete);

            _btnDelete = MakeButton("Delete Painted Triangles");
            _btnDelete.Click += async (s, e) => await RunDeleteAsync(apply: true);
            delete.Controls.Add(_btnDelete);

            cut.Controls.Add(HelpText(
                "Slices the whole model with a flat plane and deletes everything on one side. " +
                "Triangles the plane passes through are trimmed back to it - new vertices go in " +
                "exactly where each edge crosses - so the model ends in a clean, flat edge on the " +
                "plane rather than a jagged one. Positions are measured as the preview shows them, " +
                "so the cut lands where the plane is drawn."));

            var cutAxisRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 2),
            };
            cutAxisRow.Controls.Add(new Label { Text = "Axis:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _cmbCutAxis = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, Margin = new Padding(0, 4, 12, 3) };
            _cmbCutAxis.Items.AddRange(new object[] { "X", "Y", "Z" });
            _cmbCutAxis.SelectedIndex = 1;
            cutAxisRow.Controls.Add(_cmbCutAxis);
            _cmbCutSide = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200, Margin = new Padding(0, 4, 3, 3) };
            cutAxisRow.Controls.Add(_cmbCutSide);
            cut.Controls.Add(cutAxisRow);

            _lblCut = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderCut = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 0, Maximum = 1000, Value = 500,
                TickFrequency = 100, Margin = new Padding(3, 0, 3, 0),
            };
            cut.Controls.Add(_lblCut);
            cut.Controls.Add(_sliderCut);

            var cutPosRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            cutPosRow.Controls.Add(new Label { Text = "Exact position:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _numCut = new NumericUpDown { Width = 110, DecimalPlaces = 4, Margin = new Padding(0, 4, 3, 3) };
            cutPosRow.Controls.Add(_numCut);
            cut.Controls.Add(cutPosRow);

            _chkCapCut = new CheckBox
            {
                Text = "Cap the cut with a flat surface", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 0),
            };
            cut.Controls.Add(_chkCapCut);

            _chkShowCutPlane = new CheckBox
            {
                Text = "Show the cut plane in the preview", AutoSize = true, Margin = new Padding(3, 0, 3, 4),
            };
            _chkShowCutPlane.CheckedChanged += (s, e) => PushCutPlane();
            cut.Controls.Add(_chkShowCutPlane);

            cut.Controls.Add(HelpText(
                "The cap closes each ring the cut edge forms with a flat fan on the plane, textured " +
                "from a blank patch of the material where one exists - the same fill Make Watertight " +
                "uses, restricted to the cut. Where the plane runs into an opening the model already " +
                "had, that ring isn't closed (there's nothing flat to close), and where it meets a " +
                "seam between two mesh parts, each part's half of the ring is capped on its own. " +
                "A part lying entirely on the deleted side is left alone rather than emptied."));

            _btnAnalyzeCut = MakeButton("Analyze Cut (dry run)");
            _btnAnalyzeCut.Click += async (s, e) => await RunCutAsync(apply: false);
            cut.Controls.Add(_btnAnalyzeCut);

            _btnApplyCut = MakeButton("Apply Cut");
            _btnApplyCut.Click += async (s, e) => await RunCutAsync(apply: true);
            cut.Controls.Add(_btnApplyCut);

            // Wired after every control exists: the axis handler reaches all of them.
            _cmbCutAxis.SelectedIndexChanged += (s, e) => OnCutAxisChanged();
            _cmbCutSide.SelectedIndexChanged += (s, e) => OnCutControlTouched();
            _sliderCut.ValueChanged += (s, e) => OnCutSliderChanged();
            _numCut.ValueChanged += (s, e) => OnCutNumericChanged();
            OnCutAxisChanged();

            watertight.Controls.Add(HelpText(
                "Finds open holes - boundary edges with no triangle on the other side - and caps " +
                "each one with a new triangle fan. Boundaries shared between two parts of this " +
                "mesh (a material or UV seam) are recognized and left alone, not capped. A hole " +
                "that's meant to stay open, like a mouth interior, will get capped too since there's " +
                "no way to tell intent from geometry alone - step back in History if that happens."));

            watertight.Controls.Add(HelpText(
                "When the material has a texture, caps look for an unused, blank-looking patch of " +
                "it and sample the fill from there, so the cap reads as a plain, unremarkable " +
                "surface instead of a smear across unrelated texture. Where no such patch exists, " +
                "the cap falls back to blending the hole's own edge colors."));

            _chkSealPlanar = new CheckBox
            {
                Text = "Seal planar gaps (uncapped cut faces)",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Checked = true,
                Margin = new Padding(3, 0, 3, 2),
            };
            watertight.Controls.Add(_chkSealPlanar);

            watertight.Controls.Add(HelpText(
                "A cut that was left open and saved leaves a ring that runs through every mesh " +
                "part the plane crossed, which the hole search above can't close part by part. " +
                "This gathers every open edge lying on an X, Y or Z plane across the whole mesh, " +
                "joins them into rings, and closes each with one flat polygon of as few triangles " +
                "as possible - no centre vertex. Rings inside another on the same plane become " +
                "holes in its cap."));

            _btnAnalyzeWatertight = MakeButton("Analyze Holes (dry run)");
            _btnAnalyzeWatertight.Click += async (s, e) => await RunWatertightAsync(apply: false);
            watertight.Controls.Add(_btnAnalyzeWatertight);

            _btnApplyWatertight = MakeButton("Fill Holes");
            _btnApplyWatertight.Click += async (s, e) => await RunWatertightAsync(apply: true);
            watertight.Controls.Add(_btnApplyWatertight);

            uv.Controls.Add(HelpText(
                "Simplifying never moves a vertex or its texture coordinate, but a surviving " +
                "triangle now spans surface that several triangles used to cover, and the texture " +
                "is stretched straight across it where the original bent it - so it can read as " +
                "slightly warped. This measures the current surface against the Original merge " +
                "result, point by point, and nudges the surviving vertices' texture coordinates so " +
                "the texture lands back where it was."));

            uv.Controls.Add(HelpText(
                "A triangle only has three coordinates to move, so a warp inside one large triangle " +
                "is reduced to its best fit, not removed outright. Max correction is how far a point " +
                "is allowed to be off before it's taken for the other side of a texture seam and " +
                "left alone; raise it only if a warp is being skipped. Records a History step."));

            uv.Controls.Add(new Label { Text = "Max correction (% of texture):", AutoSize = true, Margin = new Padding(3, 4, 3, 0) });
            _numUvJump = new NumericUpDown
            {
                Minimum = 0.5m, Maximum = 50m, DecimalPlaces = 1, Increment = 0.5m, Value = 5m,
                Width = 110, Margin = new Padding(3, 0, 3, 6),
            };
            uv.Controls.Add(_numUvJump);

            _btnAnalyzeUv = MakeButton("Analyze Texture Warp (dry run)");
            _btnAnalyzeUv.Click += async (s, e) => await RunUvRepairAsync(apply: false);
            uv.Controls.Add(_btnAnalyzeUv);

            _btnApplyUv = MakeButton("Fix Texture Warp");
            _btnApplyUv.Click += async (s, e) => await RunUvRepairAsync(apply: true);
            uv.Controls.Add(_btnApplyUv);

            restore.Controls.Add(HelpText(
                "For texture Fix Texture Warp can't straighten - window frames, lettering, anything " +
                "finer than the triangles left under it. Paint the area and this puts the original " +
                "triangles back there, exactly as they were, then re-simplifies the rest of that " +
                "mesh part from the original to about the triangle count it has now, using the " +
                "Simplify operation's error budget, outline and skin settings. The painted area costs whatever " +
                "it cost originally; the count goes up by that much."));

            restore.Controls.Add(HelpText(
                "Anything already at original detail outside the paint - an earlier restore, a " +
                "region a previous pass protected - is held exactly as it is, so restores stack. " +
                "\"Original\" is the oldest History step whose vertices are the model's current " +
                "ones: the merge result, or after a delete, cut or fill, the state that step " +
                "left - the status line says which. Records a History step."));

            _btnAnalyzeRestore = MakeButton("Analyze Restore (dry run)");
            _btnAnalyzeRestore.Click += async (s, e) => await RunRestoreAsync(apply: false);
            restore.Controls.Add(_btnAnalyzeRestore);

            _btnRestore = MakeButton("Restore Painted Detail");
            _btnRestore.Click += async (s, e) => await RunRestoreAsync(apply: true);
            restore.Controls.Add(_btnRestore);

            foreach (var section in new[] { simplifySettings, paint, simplifyActions, delete, cut, watertight, uv, restore })
                flow.Controls.Add(section);

            // Wired after every section exists, since the handler shows and hides all of them.
            _cmbOperation.SelectedIndexChanged += (s, e) => OnOperationChanged();
            _cmbOperation.SelectedIndex = 0;

            _chkWireframe = new CheckBox { Text = "Wireframe preview", AutoSize = true, Margin = new Padding(3, 8, 3, 0) };
            _chkWireframe.CheckedChanged += (s, e) => PushWireframe();
            flow.Controls.Add(_chkWireframe);

            flow.Controls.Add(HelpText(
                "Edges are drawn over the shaded model rather than replacing it, and are hidden " +
                "where the model itself hides them - so this reads as triangle density on the " +
                "surface you are looking at, not an x-ray of the whole mesh."));

            // Everything a run reports - the outcome line, the totals and the per-mesh table - is
            // boxed and labelled as output, so it reads as the result of the last click rather
            // than as more controls. Status sits above the grid rather than below it: the grid is
            // tall enough to push anything under it off the bottom of the panel, and the outcome
            // line is the first thing to read after a run.
            var output = new GroupBox
            {
                Text = "Output",
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Width = 352,
                Padding = new Padding(6, 4, 6, 6),
                Margin = new Padding(3, 8, 3, 8),
            };
            var outputFlow = NewSection();
            outputFlow.Location = new System.Drawing.Point(output.Padding.Left, output.DisplayRectangle.Top);
            output.Controls.Add(outputFlow);
            flow.Controls.Add(output);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(328, 0),
                Margin = new Padding(3, 4, 3, 6),
                Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
            };
            outputFlow.Controls.Add(_lblStatus);

            _lblTotals = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(328, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            outputFlow.Controls.Add(_lblTotals);

            _grid = new DataGridView
            {
                Width = 328,
                Height = 220,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Margin = new Padding(3, 0, 3, 8),
            };
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Mesh", FillWeight = 32 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Before", FillWeight = 20 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "After", FillWeight = 20 });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Saved", FillWeight = 28 });
            outputFlow.Controls.Add(_grid);

            flow.Controls.Add(new Label
            {
                Text = "History",
                AutoSize = true,
                Margin = new Padding(3, 8, 3, 4),
            });

            _historyGrid = new DataGridView
            {
                Width = 340,
                Height = 180,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Margin = new Padding(3, 0, 3, 4),
            };
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Step", FillWeight = 52 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Vertices", FillWeight = 24 });
            _historyGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Triangles", FillWeight = 24 });
            _historyGrid.SelectionChanged += (s, e) => OnHistorySelectionChanged();
            flow.Controls.Add(_historyGrid);

            flow.Controls.Add(HelpText(
                "Every pass lands here. Click a row to put the model back into that state - the " +
                "rows below it stay, so stepping back and forward again costs nothing and loses " +
                "nothing. Applying a new pass while standing on an older row is what drops the " +
                "ones under it, since they were built on the geometry that pass just replaced."));

            flow.Controls.Add(HelpText(
                "Vertices counts only the ones a triangle still uses - the number a save keeps. " +
                "Simplifying rewrites triangles alone, so the ones it stops referencing stay in " +
                "the file at full size until then, and this column is where that shows up."));

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);

            _progressLabel = new ToolStripStatusLabel { Text = "", Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            _progressBar = new ToolStripProgressBar { Width = 260, Visible = false, Minimum = 0, Maximum = 100, Style = ProgressBarStyle.Continuous };
            _statusStrip = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false };
            _statusStrip.Items.Add(_progressLabel);
            _statusStrip.Items.Add(_progressBar);
            // Added after the two panes so it is docked before them and runs the full width.
            Controls.Add(_statusStrip);
        }

        // The status bar shows nothing while idle; during a rebuild it carries the bar and a line
        // saying which texture is being worked on.
        private void ShowProgress(TextureRebaker.Progress p)
        {
            if (IsDisposed) return;
            _progressBar.Visible = true;
            _progressBar.Maximum = Math.Max(1, p.Total);
            _progressBar.Value = Math.Clamp(p.Done, 0, _progressBar.Maximum);
            _progressLabel.Text = p.Stage == "done"
                ? "Texture rebuild finished."
                : $"Rebuilding texture ({p.Stage})... {100.0 * p.Done / Math.Max(1, p.Total):0}%";
        }

        private void HideProgress()
        {
            if (IsDisposed) return;
            _progressBar.Visible = false;
            _progressLabel.Text = "";
        }

        // One operation's worth of controls, laid out exactly as the parent flow lays out its own
        // children, so hiding a section is the same as the controls never having been added.
        private static FlowLayoutPanel NewSection() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };

        private void OnOperationChanged()
        {
            var op = CurrentOperation;
            _secSimplifySettings.Visible = op == Operation.Simplify;
            _secPaint.Visible = op is Operation.Simplify or Operation.Delete or Operation.Restore;
            _secSimplifyActions.Visible = op == Operation.Simplify;
            _secDelete.Visible = op == Operation.Delete;
            _secCut.Visible = op == Operation.Cut;
            _secWatertight.Visible = op == Operation.Watertight;
            _secUv.Visible = op == Operation.TextureWarp;
            _secRestore.Visible = op == Operation.Restore;

            // The cut plane overlay only makes sense while the cut is what's being set up.
            PushCutPlane();
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
            _chkRebakeTexture.Enabled = false;
            _sliderSkin.Enabled = false;
            _btnAnalyze.Enabled = false;
            _btnApply.Enabled = false;
            _btnAnalyzeRestore.Enabled = false;
            _btnRestore.Enabled = false;
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
            _lblTotals.Text = $"Current: {total:N0} triangles, {_history.Current.LiveVertices:N0} vertices";
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
            else if (apply)
            {
                // Paint with neither box ticked is almost always a forgotten tick, not a decision
                // to optimize the whole model - and the whole-model pass throws the paint away on
                // reload, so the mistake would cost the region as well as the pass.
                var painted = await ReadSelectionAsync();
                if (IsDisposed) return;
                int count = painted?.Values.Sum(v => v.Count) ?? 0;
                if (count > 0)
                {
                    var answer = MessageBox.Show(this,
                        $"{count:N0} triangle(s) are painted, but neither \"Optimize ONLY the painted region\" nor " +
                        "\"Optimize everything EXCEPT the painted region\" is checked.\n\n" +
                        "Apply will simplify the WHOLE model, ignore the paint, and clear it.\n\n" +
                        "Continue anyway?",
                        "Optimize Geometry", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes)
                    {
                        _lblStatus.Text = "Cancelled - tick ONLY or EXCEPT to use the painted region, or clear the paint.";
                        return;
                    }
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

            // Simplification rewrites index buffers only, never vertex data - hence false here,
            // which is what lets the history keep this step for the price of the indices alone.
            // A texture rebuild on top of it rewrites images, and those have to be captured
            // before Apply the same way vertices are.
            bool rebake = _chkRebakeTexture.Checked;
            _history.BeginChange(touchesVertexData: false, touchesImages: rebake);
            GeometryOptimizer.Apply(report, _model);

            string outcome = $"Removed {report.TrianglesSaved:N0} triangles ({report.PercentSaved:0.00}%).";
            string label = $"Simplify {_sliderRatio.Value}%{SelectionSuffix()}";

            if (rebake)
            {
                var rebakeReport = await RunRebakeAsync();
                if (IsDisposed) return;
                if (rebakeReport != null)
                {
                    TextureRebaker.Apply(rebakeReport, _model);
                    outcome += rebakeReport.HasChanges
                        ? $" Texture rebuilt: {rebakeReport.TexelsRewritten:N0} texel(s) in {rebakeReport.ImagesRewritten} image(s) " +
                          $"under {rebakeReport.TrianglesProcessed:N0} changed triangle(s)."
                        : " Texture already matched everywhere - no texels rewritten.";
                    if (rebakeReport.HasChanges) label += " + texture rebuild";
                }
                else
                {
                    outcome += " Texture rebuild failed - geometry kept, texture unchanged.";
                }
            }

            SetBusy(false, outcome + " Included the next time you save the merge.");
            RecordHistory(label, touchedVertexData: false, touchedImages: rebake);
            ReloadPreview(selection == null ? null : RemapSimplifiedSelection(report, selection, exclude));
        }

        // Runs the texture rebuild against the model's current geometry, with the status bar
        // tracking it. Null when it failed (the user has been told); the caller decides what to
        // do with the geometry it was meant to accompany.
        private async Task<TextureRebaker.Report?> RunRebakeAsync()
        {
            var progress = new Progress<TextureRebaker.Progress>(ShowProgress);
            ShowProgress(new TextureRebaker.Progress(0, 1, "preparing"));
            _lblStatus.Text = "Rebuilding texture...";
            try
            {
                return await Task.Run(() =>
                {
                    _uvReference ??= UvWarpRepair.BuildReference(_history);
                    return TextureRebaker.Analyze(_model, _uvReference, _history.OriginalImages,
                        new TextureRebaker.Options { MaxUvJump = (float)_numUvJump.Value / 100f }, progress);
                });
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    MessageBox.Show(this, $"Texture rebuild failed: {ex.Message}",
                        "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }
            finally
            {
                HideProgress();
            }
        }

        // Where the painted region is after a simplify pass over (ONLY) or around (EXCEPT) it. The
        // pass writes the simplified block first and the untouched triangles after it, so with
        // ONLY the paint becomes the simplified block, and with EXCEPT it IS the untouched block.
        // A primitive the pass left alone keeps its paint as it was.
        private static Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> RemapSimplifiedSelection(
            GeometryOptimizer.Report report, Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> before, bool exclude)
        {
            var after = new Dictionary<(int, int), HashSet<int>>(before);
            foreach (var p in report.Primitives)
            {
                if (p.NewIndices == null) continue;
                var key = (p.MeshIndex, p.PrimitiveIndex);
                if (!before.TryGetValue(key, out var painted) || painted.Count == 0) continue;

                int total = p.NewIndices.Length / 3;
                after[key] = exclude
                    ? new HashSet<int>(Enumerable.Range(p.PassthroughStart, total - p.PassthroughStart))
                    : new HashSet<int>(Enumerable.Range(0, p.PassthroughStart));
            }
            return after;
        }

        // What a Simplify step was scoped to, so two passes at the same ratio are still tellable
        // apart in the History list.
        private string SelectionSuffix() =>
            _chkRestrictSelection.Checked ? " (painted only)"
            : _chkExcludeSelection.Checked ? " (except painted)"
            : "";

        private async Task RunDeleteAsync(bool apply)
        {
            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            if (selection == null || selection.Values.All(s => s.Count == 0))
            {
                _lblStatus.Text = "Nothing is painted - turn on paint mode and drag over the triangles to delete.";
                return;
            }

            SetBusy(true, apply ? "Deleting..." : "Analyzing...");

            TriangleDeleter.Report report;
            try
            {
                // Read-only, like the other two analyses, so it can go off the UI thread; the
                // write-back below happens back on it.
                report = await Task.Run(() => TriangleDeleter.Analyze(_model, selection));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Deletion analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            if (!report.HasChanges)
            {
                SetBusy(false, report.PrimitivesFullyPainted > 0
                    ? $"Every triangle of {report.PrimitivesFullyPainted} mesh part(s) is painted - a part can't be " +
                      "emptied here, only thinned. Leave some of it unpainted, or drop the whole part at merge time."
                    : "Nothing to delete - the painted triangles are all in mesh parts this can't touch.");
                return;
            }

            FillDeleteGrid(report);

            string summary =
                $"{report.TrianglesDeleted:N0} triangle(s) ({report.PercentDeleted:0.00}% of the model) and " +
                $"{report.VerticesDropped:N0} vertex/vertices" +
                (apply ? " deleted." : " would be deleted.") +
                (report.PrimitivesFullyPainted > 0
                    ? $" {report.PrimitivesFullyPainted} fully-painted mesh part(s) left alone - a part can't be emptied here."
                    : "") +
                (report.PrimitivesKeepingVertices > 0
                    ? $" {report.PrimitivesKeepingVertices} mesh part(s) keep their stranded vertices " +
                      $"({report.Primitives.First(p => p.VerticesKeptReason != null).VerticesKeptReason})."
                    : "");

            if (!apply)
            {
                SetBusy(false, summary + " Nothing changed yet.");
                return;
            }

            // Compaction renumbers the surviving vertices, so this rewrites vertex accessors as
            // well as indices - the history has to capture the full pre-deletion vertex state.
            _history.BeginChange(touchesVertexData: true);
            TriangleDeleter.Apply(report, _model);

            SetBusy(false, summary + " Included the next time you save the merge.");
            RecordHistory($"Delete {report.TrianglesDeleted:N0} triangle(s)", touchedVertexData: true);
            ShowBaseline();
            ReloadPreview();
        }

        private void FillDeleteGrid(TriangleDeleter.Report report)
        {
            _grid.Rows.Clear();
            foreach (var group in report.Primitives.Where(p => p.TrianglesDeleted > 0).GroupBy(p => p.MeshName))
            {
                int before = group.Sum(p => p.TrianglesBefore);
                int after = group.Sum(p => p.TrianglesAfter);
                int deleted = before - after;
                _grid.Rows.Add(
                    group.Key,
                    before.ToString("N0"),
                    after.ToString("N0"),
                    $"{deleted:N0} ({100.0 * deleted / Math.Max(before, 1):0.0}%)");
            }

            _lblTotals.Text =
                $"{report.TrianglesBefore:N0} -> {report.TrianglesBefore - report.TrianglesDeleted:N0} triangles " +
                $"({report.PercentDeleted:0.00}% deleted)\n" +
                $"{report.VerticesDropped:N0} vertex/vertices left with no triangle, dropped with them";
        }

        // --- Planar cut controls ------------------------------------------------------------

        private int CutAxis => Math.Max(0, _cmbCutAxis.SelectedIndex);
        private bool CutDeletesPositive => _cmbCutSide.SelectedIndex == 0;

        private (float Min, float Max) CutRange()
        {
            float min = Component(_worldBounds.Min, CutAxis);
            float max = Component(_worldBounds.Max, CutAxis);
            // A hair beyond the model at either end, so the extremes read as "delete nothing" and
            // "delete everything" rather than a sliver.
            float pad = Math.Max((max - min) * 0.01f, 1e-4f);
            return (min - pad, max + pad);
        }

        private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };

        private float CutPosition() => _cutPosition;

        private float SliderPosition()
        {
            var (min, max) = CutRange();
            return min + (max - min) * (_sliderCut.Value / 1000f);
        }

        private PlanarCutter.Plane CurrentCutPlane() =>
            PlanarCutter.Plane.AxisAligned(CutAxis, CutPosition(), CutDeletesPositive);

        private void OnCutAxisChanged()
        {
            string axis = "XYZ"[CutAxis].ToString();
            int side = Math.Max(0, _cmbCutSide.SelectedIndex);

            _syncingCut = true;
            try
            {
                _cmbCutSide.Items.Clear();
                if (CutAxis == 1)
                    _cmbCutSide.Items.AddRange(new object[] { "Delete above the plane (+Y)", "Delete below the plane (-Y)" });
                else
                    _cmbCutSide.Items.AddRange(new object[] { $"Delete the +{axis} side", $"Delete the -{axis} side" });
                _cmbCutSide.SelectedIndex = side;

                // The slider keeps its fraction across an axis change; the numeric box is re-ranged
                // to the new axis and set from that fraction.
                var (min, max) = CutRange();
                _cutPosition = SliderPosition();
                _numCut.Minimum = (decimal)min;
                _numCut.Maximum = (decimal)max;
                _numCut.Increment = Math.Max((decimal)((max - min) / 200f), 0.0001m);
                _numCut.Value = Math.Clamp((decimal)_cutPosition, _numCut.Minimum, _numCut.Maximum);
            }
            finally
            {
                _syncingCut = false;
            }

            UpdateCutLabel();
            OnCutControlTouched();
        }

        private void OnCutSliderChanged()
        {
            if (_syncingCut) return;
            _cutPosition = SliderPosition();
            _syncingCut = true;
            try { _numCut.Value = Math.Clamp((decimal)_cutPosition, _numCut.Minimum, _numCut.Maximum); }
            finally { _syncingCut = false; }
            UpdateCutLabel();
            OnCutControlTouched();
        }

        private void OnCutNumericChanged()
        {
            if (_syncingCut) return;
            _cutPosition = (float)_numCut.Value;
            var (min, max) = CutRange();
            float fraction = max > min ? (_cutPosition - min) / (max - min) : 0.5f;
            _syncingCut = true;
            try { _sliderCut.Value = Math.Clamp((int)MathF.Round(fraction * 1000f), 0, 1000); }
            finally { _syncingCut = false; }
            UpdateCutLabel();
            OnCutControlTouched();
        }

        private void UpdateCutLabel() =>
            _lblCut.Text = $"Cut at {"XYZ"[CutAxis]} = {CutPosition().ToString("0.####", CultureInfo.InvariantCulture)}";

        // Adjusting any cut control switches the plane preview on - there's no dialling a cut in
        // blind - after which the checkbox is the user's to turn off again.
        private void OnCutControlTouched()
        {
            if (_syncingCut) return;
            if (_chkShowCutPlane != null && !_chkShowCutPlane.Checked && _viewerReady) _chkShowCutPlane.Checked = true;
            else PushCutPlane();
        }

        private void PushCutPlane()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null || _chkShowCutPlane == null) return;
            string position = CutPosition().ToString("R", CultureInfo.InvariantCulture);
            bool show = _chkShowCutPlane.Checked && CurrentOperation == Operation.Cut;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setCutPlane({CutAxis}, {position}, {(CutDeletesPositive ? "true" : "false")}, {(show ? "true" : "false")});");
        }

        private string CutStepLabel()
        {
            string position = CutPosition().ToString("0.###", CultureInfo.InvariantCulture);
            string axis = "XYZ"[CutAxis].ToString();
            if (CutAxis == 1) return CutDeletesPositive ? $"Cut off above Y = {position}" : $"Cut off below Y = {position}";
            return $"Cut off {(CutDeletesPositive ? "+" : "-")}{axis} side at {position}";
        }

        private async Task RunCutAsync(bool apply)
        {
            var plane = CurrentCutPlane();
            SetBusy(true, apply ? "Cutting..." : "Analyzing...");

            PlanarCutter.Report report;
            try
            {
                report = await Task.Run(() => PlanarCutter.Analyze(_model, plane));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Cut analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            if (!report.HasChanges)
            {
                SetBusy(false, report.PrimitivesFullyDeleted > 0
                    ? $"The plane leaves nothing of {report.PrimitivesFullyDeleted} mesh part(s), and nothing else " +
                      "crosses it - a part can't be emptied here, only trimmed. Move the plane, or drop the part at merge time."
                    : "Nothing on the deleted side of the plane - the model is untouched at this position.");
                return;
            }

            FillCutGrid(report);

            string summary =
                $"{report.TrianglesDeleted:N0} triangle(s) removed and {report.TrianglesClipped:N0} trimmed to the plane, " +
                $"placing {report.VerticesAdded:N0} new vertex/vertices on it and dropping {report.VerticesDropped:N0}." +
                (report.CutLoops > 0 ? $" The cut edge forms {report.CutLoops} ring(s)." : "") +
                (report.OpenCutEdges > 0 ? $" {report.OpenCutEdges} cut edge(s) run into an existing opening and don't close." : "") +
                (report.PrimitivesFullyDeleted > 0
                    ? $" {report.PrimitivesFullyDeleted} mesh part(s) entirely on the deleted side left alone."
                    : "");

            if (!apply)
            {
                SetBusy(false, summary + (_chkCapCut.Checked && report.CutLoops > 0
                    ? " Each ring would be capped. Nothing changed yet."
                    : " Nothing changed yet."));
                return;
            }

            // New vertices on the plane and compaction of the deleted side both rewrite vertex
            // accessors - a full vertex capture, like a fill or a delete.
            _history.BeginChange(touchesVertexData: true);
            var capReport = PlanarCutter.Apply(report, _model, cap: _chkCapCut.Checked);

            string capSummary = capReport == null ? ""
                : capReport.HolesFilled > 0
                    ? $" Capped {capReport.HolesFilled} ring(s) with {capReport.TrianglesAdded:N0} triangle(s)" +
                      (capReport.PrimitivesUsingTexturePatch > 0 ? ", textured from a blank patch of the atlas." : ".")
                    : " No closed ring to cap.";

            SetBusy(false, summary + capSummary + " Included the next time you save the merge.");
            RecordHistory(CutStepLabel(), touchedVertexData: true);
            ShowBaseline();
            ReloadPreview();
        }

        private void FillCutGrid(PlanarCutter.Report report)
        {
            _grid.Rows.Clear();
            foreach (var group in report.Primitives.Where(p => p.Patch != null).GroupBy(p => p.MeshName))
            {
                int before = group.Sum(p => p.TrianglesBefore);
                int after = group.Sum(p => p.TrianglesAfter);
                int removed = before - after;
                _grid.Rows.Add(
                    group.Key,
                    before.ToString("N0"),
                    after.ToString("N0"),
                    removed <= 0 ? "-" : $"{removed:N0} ({100.0 * removed / Math.Max(before, 1):0.0}%)");
            }

            _lblTotals.Text =
                $"{report.TrianglesBefore:N0} -> {report.TrianglesAfter:N0} triangles before any cap\n" +
                $"{report.VerticesAdded:N0} vertex/vertices placed on the plane, {report.VerticesDropped:N0} dropped";
        }

        private async Task RunWatertightAsync(bool apply)
        {
            // Caps are appended after the existing triangles, so painted indices still hold.
            var selection = apply ? await ReadSelectionAsync() : null;
            if (IsDisposed) return;

            SetBusy(true, apply ? "Filling holes..." : "Analyzing holes...");

            WatertightRepair.Report report;
            try
            {
                bool sealPlanar = _chkSealPlanar.Checked;
                report = await Task.Run(() => WatertightRepair.Analyze(_model, sealPlanar));
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
                (report.PlanarHolesSealed > 0
                    ? $" {report.PlanarHolesSealed} of them lie on a flat plane and get a flat polygon cap" +
                      (report.PlanarCornersDropped > 0 ? $" over the ring's corners only ({report.PlanarCornersDropped:N0} vertices on straight runs skipped)" : "") +
                      (report.PlanarChainsClosed > 0 ? $" ({report.PlanarChainsClosed} open chain(s) closed across the plane)" : "") + "."
                    : "") +
                (report.PrimitivesUsingTexturePatch > 0
                    ? $" {report.PrimitivesUsingTexturePatch} mesh part(s) will texture their cap from a found blank spot in the atlas."
                    : "") +
                (report.UnresolvedEdges > 0 ? $" {report.UnresolvedEdges} boundary edge(s) left unresolved." : "");

            if (!apply)
            {
                SetBusy(false, summary + " Nothing changed yet.");
                return;
            }

            // A fill appends vertices and rewrites vertex accessors, so the history has to take a
            // full vertex capture of the pre-fill state before Apply overwrites it.
            _history.BeginChange(touchesVertexData: true);
            WatertightRepair.Apply(report, _model);

            SetBusy(false, summary + " Included the next time you save the merge.");
            RecordHistory($"Fill {report.HolesFound:N0} hole(s)", touchedVertexData: true);
            ShowBaseline();
            ReloadPreview(selection);
        }

        private async Task RunUvRepairAsync(bool apply)
        {
            // Read before anything runs: the fix rewrites UVs only, so whatever is painted is
            // still the same triangles afterwards and goes straight back on.
            var selection = apply ? await ReadSelectionAsync() : null;
            if (IsDisposed) return;

            SetBusy(true, apply ? "Fixing texture warp..." : "Measuring texture warp...");

            var options = new UvWarpRepair.Options { MaxUvJump = (float)_numUvJump.Value / 100f };
            UvWarpRepair.Report report;
            try
            {
                // Both halves only read the model - the reference is built from History entry 0's
                // capture (or the live accessors, which are still the originals when there is
                // none) and the analysis reads the current accessors - so off the UI thread like
                // the other passes. The write-back below is on it.
                report = await Task.Run(() =>
                {
                    _uvReference ??= UvWarpRepair.BuildReference(_history);
                    return UvWarpRepair.Analyze(_model, _uvReference, options);
                });
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Texture warp analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            FillUvGrid(report);

            if (!report.HasChanges)
            {
                bool untouched = report.Primitives.All(p => p.SkippedReason
                    is "unchanged since original" or "texture already matches the original" or "no texture coordinates");
                SetBusy(false, untouched
                    ? "Texture matches the original everywhere - nothing has moved it yet."
                    : $"Measured {FormatUv(report.RmsBefore, report)} average drift, but no correction improved on it - model left unchanged.");
                return;
            }

            string summary =
                $"Texture drift {FormatUv(report.RmsBefore, report)} -> {FormatUv(report.RmsAfter, report)} average, " +
                $"{FormatUv(report.MaxBefore, report)} -> {FormatUv(report.MaxAfter, report)} worst; " +
                $"{report.VerticesAdjusted:N0} vertex/vertices " + (apply ? "adjusted." : "would be adjusted.");

            if (!apply)
            {
                SetBusy(false, summary + " Nothing changed yet.");
                return;
            }

            // Rewrites TEXCOORD accessors, so the history has to capture the pre-fix vertex state.
            _history.BeginChange(touchesVertexData: true);
            UvWarpRepair.Apply(report, _model);

            SetBusy(false, summary + " Included the next time you save the merge.");
            RecordHistory("Fix texture warp", touchedVertexData: true);
            ReloadPreview(selection);
        }

        private async Task RunRestoreAsync(bool apply)
        {
            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            if (selection == null || selection.Values.All(s => s.Count == 0))
            {
                _lblStatus.Text = "Nothing is painted - turn on paint mode and drag over the area whose detail should come back.";
                return;
            }

            SetBusy(true, apply ? "Restoring..." : "Analyzing...");

            var options = CurrentOptions();
            RegionRestorer.Report report;
            try
            {
                // Read-only like the other analyses (the original geometry comes out of History,
                // the rest off the current accessors), so off the UI thread; the write-back below
                // happens back on it.
                report = await Task.Run(() => RegionRestorer.Analyze(_model, _history, selection, options));
            }
            catch (Exception ex)
            {
                SetBusy(false, null);
                MessageBox.Show(this, $"Restore analysis failed: {ex.Message}",
                    "Optimize Geometry", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (IsDisposed) return;

            FillRestoreGrid(report);

            if (report.NothingSimplifiedSince)
            {
                SetBusy(false, $"Nothing to restore: nothing has been simplified since \"{report.ReferenceLabel}\", " +
                    "and that step changed the vertices, so there is no finer detail to fetch for them.");
                return;
            }

            if (!report.HasChanges)
            {
                var reason = report.Primitives.FirstOrDefault(p => p.SkippedReason != null)?.SkippedReason;
                SetBusy(false, reason != null
                    ? $"Nothing to restore: {reason}."
                    : "Nothing to restore in the painted area.");
                return;
            }

            // Which state the detail came from matters once a delete, cut or fill is in the
            // lineage: from then on it's that step's triangles, not the merge result's.
            string source = report.ReferenceIndex == 0
                ? ""
                : $" Restored from step {report.ReferenceIndex + 1} \"{report.ReferenceLabel}\" - the oldest state with the model's current vertices.";

            string summary =
                $"{report.PaintedCurrent:N0} painted triangle(s) " + (apply ? "replaced" : "would be replaced") +
                $" by the {report.PaintedOriginal:N0} original ones under them; " +
                $"{report.TrianglesBefore:N0} -> {report.TrianglesAfter:N0} triangles " +
                $"({(report.TrianglesAdded >= 0 ? "+" : "")}{report.TrianglesAdded:N0})." + source;

            if (!apply)
            {
                SetBusy(false, summary + " Nothing changed yet.");
                return;
            }

            // Index buffers only, same as a simplify pass - the original vertices are already there.
            _history.BeginChange(touchesVertexData: false);
            RegionRestorer.Apply(report, _model);

            // The restored triangles take over the paint; a part the restore skipped keeps its own.
            var keep = new Dictionary<(int, int), HashSet<int>>(selection);
            foreach (var (key, restored) in report.NewSelection) keep[key] = restored;

            SetBusy(false, summary + " Included the next time you save the merge.");
            RecordHistory($"Restore painted detail ({(report.TrianglesAdded >= 0 ? "+" : "")}{report.TrianglesAdded:N0}"
                + (report.ReferenceIndex == 0 ? "" : $", from step {report.ReferenceIndex + 1}") + ")", touchedVertexData: false);
            ReloadPreview(keep);
        }

        private void FillRestoreGrid(RegionRestorer.Report report)
        {
            _grid.Rows.Clear();
            foreach (var group in report.Primitives.Where(p => p.SkippedReason == null).GroupBy(p => p.MeshName))
            {
                int before = group.Sum(p => p.TrianglesBefore);
                int after = group.Sum(p => p.TrianglesAfter);
                int restored = group.Sum(p => p.PaintedOriginal);
                _grid.Rows.Add(
                    group.Key,
                    before.ToString("N0"),
                    after.ToString("N0"),
                    $"{restored:N0} restored");
            }

            var skipped = report.Primitives.Where(p => p.SkippedReason != null).ToList();
            _lblTotals.Text =
                $"{report.TrianglesBefore:N0} -> {report.TrianglesAfter:N0} triangles in the painted mesh part(s)\n" +
                $"worst re-simplification error {report.WorstError:0.0000} (relative to mesh size)" +
                (skipped.Count > 0 ? $"\n{skipped.Count} mesh part(s) skipped: {skipped[0].SkippedReason}" : "");
        }

        // Drift is measured in UV units, which nobody thinks in; shown in texels when the model's
        // textures give a scale, as a share of the texture otherwise. One scale for the whole
        // report - the largest texture bound - so figures across meshes stay comparable.
        private static string FormatUv(float uv, UvWarpRepair.Report report)
        {
            int texels = report.Primitives.Count == 0 ? 0 : report.Primitives.Max(p => p.TexelsPerUv);
            return texels > 0 ? $"{uv * texels:0.0} px" : $"{uv * 100:0.00}% of texture";
        }

        private static string FormatUv(float uv, int texels) =>
            texels > 0 ? $"{uv * texels:0.0} px" : $"{uv * 100:0.00}%";

        private void FillUvGrid(UvWarpRepair.Report report)
        {
            _grid.Rows.Clear();
            foreach (var group in report.Primitives.Where(p => p.SampledArea > 0).GroupBy(p => p.MeshName))
            {
                // Same area-weighted combination Report itself uses, per mesh.
                double area = group.Sum(p => (double)p.SampledArea);
                float before = (float)Math.Sqrt(group.Sum(p => (double)p.RmsBefore * p.RmsBefore * p.SampledArea) / area);
                float after = (float)Math.Sqrt(group.Sum(p => (double)p.RmsAfter * p.RmsAfter * p.SampledArea) / area);
                int texels = group.Max(p => p.TexelsPerUv);
                _grid.Rows.Add(
                    group.Key,
                    FormatUv(before, texels),
                    FormatUv(after, texels),
                    before <= 1e-7f ? "-" : $"{100.0 * (before - after) / before:0.0}%");
            }

            var skipped = report.Primitives.Where(p => p.SkippedReason != null).ToList();
            _lblTotals.Text =
                $"{report.SamplesUsed:N0} surface points measured, {report.SamplesRejected:N0} skipped (off the original surface or across a seam)\n" +
                $"{report.VerticesAdjusted:N0} vertex/vertices moved" +
                (skipped.Count > 0 ? $"\n{skipped.Count} primitive(s) skipped: {skipped[0].SkippedReason}" : "");
        }

        private void RecordHistory(string label, bool touchedVertexData, bool touchedImages = false)
        {
            _history.Record(label, touchedVertexData, touchedImages);
            RefreshHistoryGrid();
        }

        // Rebuilt wholesale rather than appended to: a Record on top of a restored older state
        // drops the rows that followed it, so the row count can shrink as well as grow.
        private void RefreshHistoryGrid()
        {
            _syncingHistory = true;
            try
            {
                _historyGrid.Rows.Clear();
                var entries = _history.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    _historyGrid.Rows.Add(
                        entry.Label, entry.LiveVertices.ToString("N0"), entry.Triangles.ToString("N0"));
                }
                UpdateHistoryStyling();

                if (entries.Count > 0)
                {
                    // Setting CurrentCell (rather than Selected) is what also moves the grid's
                    // notion of the current row, which is what OnHistorySelectionChanged reads
                    // back, and scrolls the row into view for free.
                    _historyGrid.CurrentCell = _historyGrid.Rows[_history.CurrentIndex].Cells[0];
                }
            }
            finally
            {
                _syncingHistory = false;
            }
        }

        // Steps past the one currently applied are still reachable - the point of the list is that
        // stepping back doesn't destroy them - but they aren't what the model looks like right now,
        // so they read as greyed-out.
        private void UpdateHistoryStyling()
        {
            for (int i = 0; i < _historyGrid.Rows.Count; i++)
                // Empty rather than the grid's own colour for the rest, so they keep inheriting
                // whatever ThemeManager last painted the grid.
                _historyGrid.Rows[i].DefaultCellStyle.ForeColor = i > _history.CurrentIndex
                    ? System.Drawing.Color.Gray
                    : System.Drawing.Color.Empty;
        }

        private void OnHistorySelectionChanged()
        {
            if (_syncingHistory || IsDisposed) return;
            if (_historyGrid.SelectedRows.Count != 1) return;

            int index = _historyGrid.SelectedRows[0].Index;
            if (index < 0 || index >= _history.Entries.Count || index == _history.CurrentIndex) return;

            var entry = _history.Entries[index];
            _history.RestoreTo(index);

            // Recolouring only - rebuilding the rows from inside the grid's own SelectionChanged
            // would re-enter it while it is still settling the new current cell, which DataGridView
            // refuses outright. The rows themselves haven't changed here anyway: a restore keeps
            // every step exactly as it was and only moves which one is current.
            UpdateHistoryStyling();

            ShowBaseline();
            _lblStatus.Text = $"Restored to \"{entry.Label}\" - {entry.LiveVertices:N0} vertices, " +
                $"{entry.Triangles:N0} triangles. Applying a new pass from here drops the steps below it.";
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
            _btnAnalyzeDelete.Enabled = !busy;
            _btnDelete.Enabled = !busy;
            _btnAnalyzeCut.Enabled = !busy;
            _btnApplyCut.Enabled = !busy;
            _btnAnalyzeWatertight.Enabled = !busy;
            _btnApplyWatertight.Enabled = !busy;
            _btnAnalyzeUv.Enabled = !busy;
            _btnApplyUv.Enabled = !busy;
            _btnAnalyzeRestore.Enabled = !busy;
            _btnRestore.Enabled = !busy;
            // Restoring mid-run would have the pass write its result over the state just restored.
            _historyGrid.Enabled = !busy;
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
                    var paintableMeshes = [];      // { meshIndex, primIndex, object, centroids, radii, normals, corners }
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

                    // --- Cut plane preview -----------------------------------------------------
                    // Two translucent shapes drawn into the overlay: the plane itself as a quad
                    // sized to the model's bounds, and a box over the whole deleted half of those
                    // bounds. Both are depth-tested against the model's depth mask, so the box
                    // tints exactly the surface that is about to go and nothing behind it, and
                    // the quad disappears where the model is in front of it - which is what makes
                    // it read as a plane THROUGH the model rather than a card in front of it.
                    // Positioned from the same world-space bounds the hit-test load measures, so
                    // it can only be built once that load has finished (see modelBox below).
                    var cutPlane = { axis: 1, position: 0, deletePositive: true, visible: false };
                    var modelBox = null;
                    var cutPlaneGroup = new THREE.Group();
                    overlayScene.add(cutPlaneGroup);
                    var cutPlaneMaterial = new THREE.MeshBasicMaterial({
                        color: 0xff6a3d, transparent: true, opacity: 0.35, depthTest: true, depthWrite: false,
                        side: THREE.DoubleSide,
                    });
                    var cutSideMaterial = new THREE.MeshBasicMaterial({
                        color: 0xff6a3d, transparent: true, opacity: 0.12, depthTest: true, depthWrite: false,
                        side: THREE.DoubleSide,
                    });

                    function rebuildCutPlane() {
                        for (var i = cutPlaneGroup.children.length - 1; i >= 0; i--) {
                            var old = cutPlaneGroup.children[i];
                            cutPlaneGroup.remove(old);
                            old.geometry.dispose();
                        }
                        if (!cutPlane.visible || !modelBox) return;

                        var pad = 1.08;
                        var size = modelBox.getSize(new THREE.Vector3()).multiplyScalar(pad);
                        var center = modelBox.getCenter(new THREE.Vector3());
                        var axis = cutPlane.axis;

                        // PlaneGeometry lies in XY facing +Z; turn it to face the cut axis.
                        var quad;
                        if (axis === 0) {
                            quad = new THREE.Mesh(new THREE.PlaneGeometry(size.z, size.y), cutPlaneMaterial);
                            quad.rotation.y = Math.PI / 2;
                        } else if (axis === 1) {
                            quad = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.z), cutPlaneMaterial);
                            quad.rotation.x = -Math.PI / 2;
                        } else {
                            quad = new THREE.Mesh(new THREE.PlaneGeometry(size.x, size.y), cutPlaneMaterial);
                        }
                        var quadPos = center.clone();
                        quadPos.setComponent(axis, cutPlane.position);
                        quad.position.copy(quadPos);
                        cutPlaneGroup.add(quad);

                        var boxMin = center.clone().sub(size.clone().multiplyScalar(0.5));
                        var boxMax = center.clone().add(size.clone().multiplyScalar(0.5));
                        if (cutPlane.deletePositive) boxMin.setComponent(axis, cutPlane.position);
                        else boxMax.setComponent(axis, cutPlane.position);
                        var extent = boxMax.clone().sub(boxMin);
                        if (extent.x > 0 && extent.y > 0 && extent.z > 0) {
                            var box = new THREE.Mesh(new THREE.BoxGeometry(extent.x, extent.y, extent.z), cutSideMaterial);
                            box.position.copy(boxMin.clone().add(extent.clone().multiplyScalar(0.5)));
                            cutPlaneGroup.add(box);
                        }
                    }

                    window.setCutPlane = function (axis, position, deletePositive, visible) {
                        cutPlane.axis = axis;
                        cutPlane.position = position;
                        cutPlane.deletePositive = deletePositive;
                        cutPlane.visible = visible;
                        rebuildCutPlane();
                    };

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

                    // While the brush disc is on the surface the pointer becomes a small dot, so
                    // the disc reads as the brush and the arrow doesn't sit on top of it. The
                    // element under the pointer is a canvas inside <model-viewer>'s shadow root
                    // with its own 'grab' cursor, so a cursor on the host is not enough - a rule
                    // is added inside the shadow root instead, switched by a class on the host.
                    var dotCursorStyled = false;
                    function ensureDotCursorStyle() {
                        // Lazily, on first use: the shadow root only exists once the custom
                        // element has upgraded, which need not be before this script ran.
                        if (dotCursorStyled || !viewer.shadowRoot) return;
                        var dot = ""url(\""data:image/svg+xml;utf8,%3Csvg xmlns='http://www.w3.org/2000/svg' width='17' height='17'%3E%3Ccircle cx='8.5' cy='8.5' r='3' fill='white' stroke='black' stroke-width='1.5'/%3E%3C/svg%3E\"") 8 8, crosshair"";
                        var style = document.createElement('style');
                        style.textContent = ':host(.paint-dot), :host(.paint-dot) * { cursor: ' + dot + ' !important; }';
                        viewer.shadowRoot.appendChild(style);
                        dotCursorStyled = true;
                    }

                    function showBrushCursor(point) {
                        brushDecalMaterial.uniforms.uCenter.value.copy(point);
                        brushDecalMaterial.uniforms.uRadius.value = brushRadius;
                        brushDecalGroup.visible = true;
                        ensureDotCursorStyle();
                        viewer.classList.add('paint-dot');
                    }

                    function hideBrushCursor() {
                        brushDecalGroup.visible = false;
                        viewer.classList.remove('paint-dot');
                    }

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
                    //   radius   - centroid to its furthest vertex, i.e. a bounding sphere. This is
                    //              only the cheap REJECT in front of the real test: a triangle whose
                    //              sphere doesn't reach the brush ball can't reach it either, so one
                    //              distance compare throws out almost the whole mesh per event.
                    //   corners  - the three world-space vertices, so the exact point-to-triangle
                    //              test in triangleTouchesBrush has something to measure against.
                    //              36 bytes a face, computed once here rather than re-transformed
                    //              out of the attribute buffers on every pointer move.
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
                        var corners = new Float32Array(triCount * 9);

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

                            var o = t * 9;
                            corners[o] = a.x; corners[o + 1] = a.y; corners[o + 2] = a.z;
                            corners[o + 3] = b.x; corners[o + 4] = b.y; corners[o + 5] = b.z;
                            corners[o + 6] = c.x; corners[o + 7] = c.y; corners[o + 8] = c.z;

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
                        return { centroids: centroids, radii: radii, normals: normals, corners: corners };
                    }

                    function selectionKey(meshInfo) { return meshInfo.meshIndex + '_' + meshInfo.primIndex; }

                    // Does the triangle actually intersect the brush ball? Distance from the brush
                    // centre to the closest point ON the triangle - the face is in when that lands
                    // within the brush radius, which is the same condition, on the same surface,
                    // that the cursor shader discards fragments by. Only ever reached by triangles
                    // the bounding-sphere reject already let through.
                    var _brushTri = new THREE.Triangle();
                    var _brushClosest = new THREE.Vector3();

                    function triangleTouchesBrush(corners, t, point) {
                        var o = t * 9;
                        _brushTri.a.set(corners[o], corners[o + 1], corners[o + 2]);
                        _brushTri.b.set(corners[o + 3], corners[o + 4], corners[o + 5]);
                        _brushTri.c.set(corners[o + 6], corners[o + 7], corners[o + 8]);
                        _brushTri.closestPointToPoint(point, _brushClosest);
                        return _brushClosest.distanceToSquared(point) <= brushRadius * brushRadius;
                    }

                    // Every triangle that the brush ball touches AT ALL, in every primitive, facing
                    // the camera. Three things that matters for, in the order they bit:
                    //
                    //   Partial overlap counts - but only REAL overlap. A face clipped by the edge
                    //   of the circle is in, a face big enough to swallow the whole circle is in,
                    //   and the face under the cursor is in for free (the pick point lies on it, so
                    //   its distance is zero). What decides that is triangleTouchesBrush, measuring
                    //   to the closest point on the triangle itself. The bounding-sphere compare in
                    //   front of it is a reject, not the test: on its own it admits every triangle
                    //   whose sphere overlaps the ball, and a sphere is a poor stand-in for a
                    //   triangle - a long thin face, or a big flat panel, has a sphere radius far
                    //   larger than anything the face reaches in most directions, so faces nowhere
                    //   near the circle came back inside it. Using it as the test is what made the
                    //   brush overselect, and what made the selection disagree with a cursor drawn
                    //   from exactly the same radius.
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
                            var corners = meshInfo.corners;
                            var key = selectionKey(meshInfo);
                            var set = selection[key];

                            for (var t = 0; t < centroids.length; t++) {
                                var centroid = centroids[t];
                                var reach = brushRadius + radii[t];
                                if (centroid.distanceToSquared(point) > reach * reach) continue;
                                if (!triangleTouchesBrush(corners, t, point)) continue;

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

                    // Hide paint: the overlay group is simply not drawn; the selection behind it
                    // is untouched, and getPaintSelection still reports it.
                    window.setPaintVisible = function (visible) {
                        overlayGroup.visible = visible;
                    };

                    window.setPaintColor = function (hex) {
                        highlightMaterial.color.setHex(hex);
                    };

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

                    // The inverse: .NET handing a selection back after a reload, in the reloaded
                    // model's triangle indices. Only called once 'load' has rebuilt paintableMeshes,
                    // and clamped to each mesh's actual triangle count so a stale index can't put
                    // garbage into the overlay.
                    // 'ready' (which is when .NET calls this) fires when <model-viewer> has the
                    // model, but paintableMeshes is built by the separate hit-test parse that only
                    // starts then - so the data is parked and applied by whichever comes second.
                    var pendingSelectionData = null;
                    window.setPaintSelection = function (data) {
                        pendingSelectionData = data;
                        if (paintableMeshes.length > 0) applyPendingSelection();
                    };

                    function applyPendingSelection() {
                        var data = pendingSelectionData;
                        if (!data) return;
                        pendingSelectionData = null;
                        selection = {};
                        paintableMeshes.forEach(function (meshInfo) {
                            var key = selectionKey(meshInfo);
                            var tris = data[key];
                            if (!tris || tris.length === 0) return;
                            var index = meshInfo.object.geometry.index;
                            var count = index ? index.count / 3 : meshInfo.object.geometry.attributes.position.count / 3;
                            var set = new Set();
                            for (var i = 0; i < tris.length; i++) {
                                if (tris[i] >= 0 && tris[i] < count) set.add(tris[i]);
                            }
                            if (set.size > 0) selection[key] = set;
                        });
                        rebuildOverlay();
                        pushSelectionCount();
                    }

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
                                    corners: triData.corners,
                                });
                            });
                            paintableMeshes = meshes;
                            rebuildDepthMask();
                            rebuildBrushDecal();

                            var box = new THREE.Box3().setFromObject(gltf.scene);
                            var size = box.getSize(new THREE.Vector3());
                            modelMaxDim = Math.max(size.x, size.y, size.z) || 1;
                            modelBox = box;
                            rebuildCutPlane();

                            // Only now is the brush fraction resolvable into a world-space radius,
                            // and only now is there geometry to draw a wireframe from - .NET pushed
                            // both of these settings back when <model-viewer> finished loading,
                            // which is earlier than this.
                            resolveBrushRadius();
                            rebuildWireframe();
                            applyPendingSelection();
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
                        pendingSelectionData = null;
                        modelBox = null;
                        rebuildOverlay();
                        rebuildDepthMask();
                        rebuildWireframe();
                        rebuildBrushDecal();
                        rebuildCutPlane();
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

        // keepSelection: a painted region to put back on the reloaded model, already expressed in
        // the NEW triangle indices. Null (or empty) means the paint is gone, which is right for a
        // delete or a cut, whose triangles it can no longer refer to.
        private void ReloadPreview(Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>? keepSelection = null)
        {
            if (_webView.CoreWebView2 == null) return;

            // The reloaded model may have different triangle indices than whatever the paint
            // selection was recorded against (that's the whole point of Apply), so the JS side
            // throws its selection away on every reload. Where the operation has worked out the
            // new indices, they go back on once the viewer says it is ready (see
            // OnWebMessageReceived); otherwise this just keeps the WinForms side in sync.
            _pendingSelection = keepSelection != null && keepSelection.Values.Any(s => s.Count > 0) ? keepSelection : null;
            if (_pendingSelection == null)
            {
                _lblSelection.Text = "0 triangles painted";
                SetSelectionModeAvailable(false);
            }

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
                PushPaintVisible();
                PushPaintColor();
                PushWireframe();
                PushCutPlane();
                PushPendingSelection();
                return;
            }

            if (message.Action == "selectionChanged")
            {
                int count = message.Count ?? 0;
                _lblSelection.Text = count == 1 ? "1 triangle painted" : $"{count:N0} triangles painted";
                SetSelectionModeAvailable(count > 0);
            }
        }

        // Puts a carried-over painted region onto the freshly loaded model. The viewer answers
        // with selectionChanged, which is what updates the count and the ONLY/EXCEPT checkboxes.
        private void PushPendingSelection()
        {
            var selection = _pendingSelection;
            _pendingSelection = null;
            if (selection == null || _webView.CoreWebView2 == null) return;

            var payload = selection
                .Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => $"{kv.Key.MeshIndex}_{kv.Key.PrimitiveIndex}", kv => kv.Value.OrderBy(t => t).ToArray());
            string json = JsonSerializer.Serialize(payload);
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintSelection({json});");
        }

        private void PushPaintVisible()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintVisible({(_chkHidePaint.Checked ? "false" : "true")});");
        }

        private void PushPaintColor()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            int hex = (_paintColor.R << 16) | (_paintColor.G << 8) | _paintColor.B;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintColor({hex});");
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
            float fraction = _sliderBrush.Value / 1000f;
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
