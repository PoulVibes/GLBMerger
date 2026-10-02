using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Flatten Model: replaces a static high-detail model (the game's buildings) with a few flat
    // billboards - see ModelFlattener for the algorithm. The Strength slider is the flattening
    // tolerance (how far any surface point may move to land on its billboard), log-scaled from
    // 0.1% to 100% of the model's size: low flattens brick grooves, mid flattens window wells,
    // max flattens the building into a card. Minimum Billboard Size decides what is too small to
    // deserve a billboard of its own; those leftovers are kept as mesh or dropped.
    //
    // Bake renders each billboard's texture and packs the atlas (see BillboardBaker) and shows
    // the textured result; Apply swaps the model's meshes for it, Revert swaps them back. Like
    // every editor mode, Apply edits the shared model in place, and Revert only lasts while this
    // mode stays open.
    //
    // The preview is a hand-rolled Three.js scene (same stack as ModelSmootherEditor) rather than
    // <model-viewer>, because what it mostly shows isn't a glTF at all: the source triangles and
    // the billboard quads are streamed to it as raw binary files (see WriteSourceFile /
    // WriteResultFile) so a slider change never has to re-save a 60 MB model.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there).
    public partial class ModelFlattenerEditor : UserControl, IUnappliedChanges
    {
        // Each setting's description, shown on hover (see HelpTips).
        private readonly HelpTips _help;

        // Billboards whose triangles cover less than this much of their rectangle will need
        // transparency once textured.
        private const float TransparencyCoverage = 0.95f;

        private readonly ModelRoot _model;
        private readonly AppSettings _settings;
        private readonly bool _darkMode;

        private WebView2 _webView = null!;
        private TrackBar _sliderStrength = null!, _sliderMinSize = null!, _sliderSideMinSize = null!, _sliderDetailBoost = null!, _sliderDetailStrength = null!, _sliderRecess = null!;
        private TableLayoutPanel _groupList = null!;
        private FeatureGroup _groupFocus = FeatureGroup.None;
        private Label _lblStrength = null!, _lblMinSize = null!, _lblSideMinSize = null!, _lblDetailBoost = null!, _lblDetailStrength = null!, _lblRecess = null!, _lblStats = null!, _lblStatus = null!;
        private CheckBox _chkKeepLeftovers = null!, _chkSideDetail = null!, _chkTrimCorners = null!, _chkBakeNormals = null!, _chkBakeMr = null!, _chkOutline = null!, _chkHighlight = null!;
        private Button _btnBackground = null!;
        private NumericUpDown _numBudget = null!;
        private Button _btnFitBudget = null!;
        private ComboBox _previewDropdown = null!, _cmbCurves = null!;
        private ComboBox _cmbConfig = null!;
        private Button _btnSaveConfig = null!, _btnDeleteConfig = null!;
        private Button _btnBake = null!, _btnApply = null!, _btnRevert = null!;
        private CheckBox _chkPaintDelete = null!;
        private Button _btnUndoStroke = null!, _btnRestoreDeleted = null!, _btnDeleteHighlighted = null!, _btnClearHighlight = null!;
        // What's painted red in the preview, as indices into the result it was painted on (tag).
        private List<int> _highlighted = new();
        private int _highlightTag;
        private Label _lblBake = null!;
        // The status bar along the bottom: what's running (flattening, fitting the budget,
        // baking) and how far it's got. Several can overlap (a slider moved mid-bake); the one
        // started last is shown, and when it ends the bar goes back to the one before.
        private StatusStrip _statusStrip = null!;
        private ToolStripStatusLabel _progressLabel = null!;
        private ToolStripProgressBar _progressBar = null!;
        private sealed class Activity
        {
            public string Name = "";
            public double Fraction;
            public string Stage = "";
        }
        private readonly List<Activity> _activities = new();
        private PictureBox _picAtlas = null!;
        private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };

        // The model as gathered, and (built on first use) the same with its curves swapped for
        // simplified mesh; the result, bake source and bake always go with the input they came
        // from.
        private ModelFlattener.FlattenInput? _input, _curveMeshInput, _resultInput, _bakeSourceFor;
        // _rawResult is what the flattener computed; _result is it with the painted-out billboards
        // removed, and is what's shown, baked and applied.
        private ModelFlattener.FlattenResult? _rawResult, _result;
        private CancellationTokenSource? _cts;

        // Billboards painted out in the preview, one list per brush stroke (for Undo). Kept as
        // where the billboard was rather than its index, so a deletion survives a recompute
        // with other settings: whatever billboard of the new result sits in the same place is
        // deleted too. Kind keeps a facade from being matched by the backdrop or detail layer
        // stacked behind it, which faces the same way and is about the same size.
        private readonly record struct DeletedBillboard(Vector3 Normal, Vector3 Center, float Size, int Kind);
        private readonly List<List<DeletedBillboard>> _deletedStrokes = new();
        private int _pushTag;
        private int _computeVersion;

        private bool _viewerReady;
        private readonly string _sessionTag = Guid.NewGuid().ToString("N").Substring(0, 8);
        private string? _sourcePath, _resultPath, _originalPath;
        private int _resultVersion;

        // Decoded source textures etc., pulled out of the model on first bake.
        private BillboardBaker.BakeSource? _bakeSource;
        private BillboardBaker.BakeResult? _bake;
        private ModelFlattener.FlattenResult? _bakedFrom, _pendingBakeOf;
        private Task<bool>? _bakeTask;
        private string? _bakePath, _bakeMapPath;
        private int _bakeTag;   // _pushTag of the result the current bake was made from
        private int _bakeVersion;

        // Every mesh node's original mesh, taken on first Apply - Revert restores these. The
        // model is shared with every other editor mode and nothing else keeps a copy.
        private List<(Node Node, Mesh Mesh)>? _originalMeshes;

        private enum PreviewMode { Billboards, Baked, Source, Original, Groups }
        private PreviewMode Mode => (PreviewMode)_previewDropdown.SelectedIndex;

        public ModelFlattenerEditor(ModelRoot model, bool darkMode = false, AppSettings? settings = null, string? sourcePath = null)
        {
            _help = new HelpTips(this);
            _model = model;
            _modelPath = sourcePath;
            _settings = settings ?? new AppSettings();
            _darkMode = darkMode;

            Dock = DockStyle.Fill;

            BuildUi();
            HoldWhileDragging(this);
            TrackConfigurationEdits();
            _uiReady = true;

            ThemeManager.Apply(this, darkMode);
            UpdateBackgroundButton();   // the theme repaints buttons; keep the colour swatch

            _debounce.Tick += (s, e) => { _debounce.Stop(); RunCompute(); };

            string? why = ModelFlattener.WhyNotFlattenable(model);
            if (why != null)
            {
                _lblStatus.Text = why;
                _lblStatus.ForeColor = System.Drawing.Color.OrangeRed;
                foreach (Control c in new Control[] { _cmbConfig, _btnSaveConfig, _btnDeleteConfig, _sliderStrength, _sliderMinSize, _chkKeepLeftovers, _chkSideDetail, _cmbCurves, _sliderSideMinSize, _sliderDetailBoost, _sliderDetailStrength, _sliderRecess, _chkTrimCorners, _chkBakeNormals, _chkBakeMr, _numBudget, _btnFitBudget, _previewDropdown, _chkOutline, _chkHighlight, _chkPaintDelete, _btnBake, _btnApply })
                    c.Enabled = false;
                return;
            }

            if (ModelFlattener.MeshNodes(model).Any(n => n.Mesh?.Extras?[BillboardBaker.FlattenedMarker] != null))
                _lblStatus.Text = "This model has already been flattened - flattening again works on the flattened result.";

            _input = ModelFlattener.Gather(model);
            if (_input.TriangleCount == 0)
            {
                _lblStatus.Text = "This model has no triangles to flatten.";
                return;
            }
            UpdateLabels();
            LoadOverrides();

            _ = InitializeViewerAsync();
            RunCompute();
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
                Text = "Flatten Model (Billboards)",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            _help.Add(flow, 
                "Replaces the model with flat, textured billboards, for static models like buildings. " +
                "Each billboard's texture is the model seen straight on from its front.");

            flow.Controls.Add(new Label { Text = "Configuration:", AutoSize = true, Margin = new Padding(3, 0, 3, 0) });
            _cmbConfig = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _cmbConfig.SelectedIndexChanged += (s, e) => OnConfigurationSelected();
            flow.Controls.Add(_cmbConfig);
            var configButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            _btnSaveConfig = new Button { Text = "Save Configuration...", AutoSize = true, Margin = new Padding(3, 0, 6, 3) };
            _btnSaveConfig.Click += (s, e) => SaveConfiguration();
            _btnDeleteConfig = new Button { Text = "Delete", AutoSize = true, Margin = new Padding(0, 0, 3, 3) };
            _btnDeleteConfig.Click += (s, e) => DeleteConfiguration();
            configButtons.Controls.Add(_btnSaveConfig);
            configButtons.Controls.Add(_btnDeleteConfig);
            flow.Controls.Add(configButtons);

            _help.Add(flow,
                "Saved sets of every setting below. Picking one loads its settings; changing any " +
                "setting afterwards switches this back to Custom Configuration. Save Configuration " +
                "stores the current settings under a name (an existing name is replaced).");

            BuildSections(flow);

            _lblStrength = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderStrength = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 0, Maximum = 1000,
                Value = Math.Clamp(_settings.FlattenStrength, 0, 1000),
                TickFrequency = 100, SmallChange = 5, LargeChange = 50, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderStrength.ValueChanged += (s, e) =>
            {
                _settings.FlattenStrength = _sliderStrength.Value;
                FollowFrontStrength();
                UpdateLabels();
                ScheduleCompute();
            };
            _secWalls.Controls.Add(_lblStrength);
            _secWalls.Controls.Add(_sliderStrength);

            _help.Add(_secWalls, 
                "How far any point of the surface may move to land on its billboard. Low flattens " +
                "brick grooves, medium flattens window wells, max flattens the whole model into a card.");

            var budgetRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            budgetRow.Controls.Add(new Label { Text = "Budget:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _numBudget = new NumericUpDown
            {
                Width = 80, Minimum = 4, Maximum = 1000000, Increment = 50,
                Value = Math.Clamp(_settings.FlattenTriangleBudget, 4, 1000000),
                Margin = new Padding(0, 4, 6, 3),
            };
            _numBudget.ValueChanged += (s, e) => _settings.FlattenTriangleBudget = (int)_numBudget.Value;
            budgetRow.Controls.Add(_numBudget);
            _btnFitBudget = new Button { Text = "Fit Strength", AutoSize = true, Margin = new Padding(0, 3, 3, 3) };
            _btnFitBudget.Click += async (s, e) =>
                await FitGroupAsync("Front walls", new[] { FeatureGroup.Front }, (int)_numBudget.Value, _sliderStrength, null, v => SettingsAt(v));
            budgetRow.Controls.Add(_btnFitBudget);
            _secWalls.Controls.Add(budgetRow);

            _help.Add(_secWalls, 
                "The triangles the front walls end up as. Fit Strength finds the lowest Strength (most " +
                "detail) that meets it, with every other setting as it is; groups set to the front's " +
                "Strength move with it.");

            _lblMinSize = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderMinSize = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 0, Maximum = 100,
                Value = Math.Clamp(_settings.FlattenMinSize, 0, 100),
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderMinSize.ValueChanged += (s, e) =>
            {
                _settings.FlattenMinSize = _sliderMinSize.Value;
                UpdateLabels();
                ScheduleCompute();
            };
            _secWalls.Controls.Add(_lblMinSize);
            _secWalls.Controls.Add(_sliderMinSize);

            _chkKeepLeftovers = new CheckBox
            {
                Text = "Keep leftovers as mesh (off = drop them)",
                AutoSize = true,
                Checked = _settings.FlattenKeepLeftovers,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkKeepLeftovers.CheckedChanged += (s, e) =>
            {
                _settings.FlattenKeepLeftovers = _chkKeepLeftovers.Checked;
                ScheduleCompute();
            };
            _secWalls.Controls.Add(_chkKeepLeftovers);

            _help.Add(_secWalls, 
                "Surface patches smaller than this don't get a billboard of their own - awnings, " +
                "AC units, curved or noisy bits. Kept, they stay as real triangles; dropped, they vanish.");

            _chkSideDetail = new CheckBox
            {
                Text = "Side billboards for protruding details",
                AutoSize = true,
                Checked = _settings.FlattenSideDetail,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkSideDetail.CheckedChanged += (s, e) =>
            {
                _settings.FlattenSideDetail = _chkSideDetail.Checked;
                _sliderSideMinSize.Enabled = _chkSideDetail.Checked;
                ScheduleCompute();
            };
            _secSides.Controls.Add(_chkSideDetail);

            _lblSideMinSize = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderSideMinSize = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 1, Maximum = 100,
                Value = Math.Clamp(_settings.FlattenSideMinSize, 1, 100),
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
                Enabled = _settings.FlattenSideDetail,
            };
            _sliderSideMinSize.ValueChanged += (s, e) =>
            {
                _settings.FlattenSideMinSize = _sliderSideMinSize.Value;
                UpdateLabels();
                ScheduleCompute();
            };
            _secSides.Controls.Add(_lblSideMinSize);
            _secSides.Controls.Add(_sliderSideMinSize);

            _help.Add(_secSides, 
                "Balcony side rails and stair stringers face sideways, so flattening them into the " +
                "front billboard makes them vanish from any side view. With this on they get side-facing " +
                "billboards of their own, down to this size.");

            _secCurves.Controls.Add(new Label { Text = "Curved walls (round bays, towers):", AutoSize = true, Margin = new Padding(3, 0, 3, 0) });
            _cmbCurves = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _cmbCurves.Items.AddRange(new object[] { "Flat planes", "Curved billboards", "Keep as simplified mesh" });
            _cmbCurves.SelectedIndex = Math.Clamp(_settings.FlattenCurves, 0, 2);
            _cmbCurves.SelectedIndexChanged += (s, e) =>
            {
                _settings.FlattenCurves = _cmbCurves.SelectedIndex;
                ScheduleCompute();
            };
            _secCurves.Controls.Add(_cmbCurves);

            _help.Add(_secCurves, 
                "Flat planes: cheapest, but bands and cornices round a curve break into chevrons from " +
                "below. Curved billboards: one billboard bent round each curve, a few dozen triangles, " +
                "reads as round. Keep as simplified mesh: closest to the original, but thousands of " +
                "triangles per curve.");

            _lblDetailStrength = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderDetailStrength = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 10, Maximum = 100,
                Value = Math.Clamp(_settings.FlattenDetailStrength, 10, 100),
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderDetailStrength.ValueChanged += (s, e) =>
            {
                _settings.FlattenDetailStrength = _sliderDetailStrength.Value;
                UpdateLabels();
                ScheduleCompute();
            };
            _secFire.Controls.Add(_lblDetailStrength);
            _secFire.Controls.Add(_sliderDetailStrength);

            _help.Add(_secFire, 
                "See-through structures (fire escapes, railings) are flattened with this share of the " +
                "Strength instead, so a stair's front and wall-side stringers stay separate layers and " +
                "still read as stairs from an angle. 100% = same as everything else.");

            _lblRecess = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderRecess = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 0, Maximum = 100,
                Value = Math.Clamp(_settings.FlattenRecessDarkening, 0, 100),
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderRecess.ValueChanged += (s, e) =>
            {
                _settings.FlattenRecessDarkening = _sliderRecess.Value;
                UpdateLabels();
                ScheduleCompute();
            };
            _secGlass.Controls.Add(_lblRecess);
            _secGlass.Controls.Add(_sliderRecess);

            _help.Add(_secGlass, 
                "Darkens what sat deeper than the surface around it (window glass, grooves), to keep " +
                "a hint of the depth that flattening removed.");

            _chkTrimCorners = new CheckBox
            {
                Text = "Trim empty corners off billboards",
                AutoSize = true,
                Checked = _settings.FlattenTrimCorners,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkTrimCorners.CheckedChanged += (s, e) =>
            {
                _settings.FlattenTrimCorners = _chkTrimCorners.Checked;
                ScheduleCompute();
            };
            _secBake.Controls.Add(_chkTrimCorners);

            _help.Add(_secBake, 
                "Cuts a billboard's empty corners off diagonally (a stepped roofline, a stair flight): " +
                "one more triangle per corner, but less see-through area for the game to alpha-test.");

            _chkBakeNormals = new CheckBox
            {
                Text = "Bake normal map from the full model",
                AutoSize = true,
                Checked = _settings.FlattenBakeNormals,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkBakeNormals.CheckedChanged += (s, e) =>
            {
                _settings.FlattenBakeNormals = _chkBakeNormals.Checked;
                ScheduleCompute();
            };
            _secBake.Controls.Add(_chkBakeNormals);

            _help.Add(_secBake, 
                "A second atlas with the original surface's normals (vertex normals plus the source " +
                "normal map), so flattened grooves and ornament still shade. Written as the material's " +
                "normal texture - the game doesn't read it yet.");

            _chkBakeMr = new CheckBox
            {
                Text = "Carry over metallic/roughness",
                AutoSize = true,
                Checked = _settings.FlattenBakeMetallicRoughness,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkBakeMr.CheckedChanged += (s, e) =>
            {
                _settings.FlattenBakeMetallicRoughness = _chkBakeMr.Checked;
                ScheduleCompute();
            };
            _secBake.Controls.Add(_chkBakeMr);

            _help.Add(_secBake, 
                "A third atlas with the source's metallic/roughness texture, same layout. Without a " +
                "source texture (or with this off) the source material's factors are used as constants. " +
                "The game doesn't read it yet.");

            _lblDetailBoost = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderDetailBoost = new TrackBar
            {
                Width = 320, Height = 45, Minimum = 10, Maximum = 40,
                Value = Math.Clamp(_settings.FlattenDetailBoost, 10, 40),
                TickFrequency = 5, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderDetailBoost.ValueChanged += (s, e) =>
            {
                _settings.FlattenDetailBoost = _sliderDetailBoost.Value;
                UpdateLabels();
                ScheduleCompute();
            };
            _secFire.Controls.Add(_lblDetailBoost);
            _secFire.Controls.Add(_sliderDetailBoost);

            _help.Add(_secFire, 
                "Extra texture resolution for small see-through billboards (rails, fire escapes), " +
                "relative to the big walls - never beyond the source texture's own. The game caps " +
                "textures at 2048, so this trades against the walls' resolution.");

            BuildGroupControls();

            flow.Controls.Add(new Label { Text = "Preview", AutoSize = true, Margin = new Padding(3, 4, 3, 4) });

            _previewDropdown = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _previewDropdown.Items.AddRange(new object[]
            {
                "Billboards (one colour each)",
                "Textured result (baked)",
                "Source triangles, coloured by billboard",
                "Original model",
                "Source triangles, coloured by group",
            });
            _previewDropdown.SelectedIndex = 0;
            _previewDropdown.SelectedIndexChanged += (s, e) =>
            {
                PushViewState();
                if (Mode == PreviewMode.Baked && !BakeIsCurrent) _ = RunBakeAsync();
                // The section headers take the group colours in the "coloured by group" preview.
                if (_result != null) ShowGroups(_result);
            };
            flow.Controls.Add(_previewDropdown);

            var bgRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            bgRow.Controls.Add(new Label { Text = "Background:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _btnBackground = new Button { AutoSize = true, Margin = new Padding(0, 3, 6, 3) };
            _btnBackground.Click += (s, e) => PickBackground();
            bgRow.Controls.Add(_btnBackground);
            var btnBackgroundReset = new Button { Text = "Reset", AutoSize = true, Margin = new Padding(0, 3, 3, 3) };
            btnBackgroundReset.Click += (s, e) => SetBackground(DefaultBackground);
            bgRow.Controls.Add(btnBackgroundReset);
            flow.Controls.Add(bgRow);
            UpdateBackgroundButton();

            _chkOutline = new CheckBox { Text = "Outline billboards", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 0) };
            _chkOutline.CheckedChanged += (s, e) => PushViewState();
            flow.Controls.Add(_chkOutline);

            _chkHighlight = new CheckBox
            {
                Text = "Highlight billboards that will need transparency",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            };
            _chkHighlight.CheckedChanged += (s, e) => PushViewState();
            flow.Controls.Add(_chkHighlight);

            _help.Add(flow, 
                "Back faces show dark grey: billboards are one-sided, like the game draws them. " +
                "Highlighted (orange) billboards cover less than 95% of their rectangle - " +
                "the gaps become transparent once textured.");

            _chkPaintDelete = new CheckBox
            {
                Text = "Click to select billboards for deleting",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 0),
            };
            _chkPaintDelete.CheckedChanged += (s, e) =>
            {
                if (!_chkPaintDelete.Checked) ClearHighlight();
                PushPaintState();
            };
            flow.Controls.Add(_chkPaintDelete);

            var highlightRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 0),
            };
            _btnDeleteHighlighted = new Button { Text = "Delete Selected", AutoSize = true, Enabled = false, Margin = new Padding(3, 3, 6, 3) };
            _btnDeleteHighlighted.Click += (s, e) => DeleteHighlighted();
            highlightRow.Controls.Add(_btnDeleteHighlighted);
            _btnClearHighlight = new Button { Text = "Clear Selection", AutoSize = true, Enabled = false, Margin = new Padding(0, 3, 3, 3) };
            _btnClearHighlight.Click += (s, e) => ClearHighlight();
            highlightRow.Controls.Add(_btnClearHighlight);
            flow.Controls.Add(highlightRow);

            var deleteRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 4),
            };
            _btnUndoStroke = new Button { Text = "Undo Last Delete", AutoSize = true, Enabled = false, Margin = new Padding(3, 3, 6, 3) };
            _btnUndoStroke.Click += (s, e) =>
            {
                if (_deletedStrokes.Count == 0) return;
                _deletedStrokes.RemoveAt(_deletedStrokes.Count - 1);
                _changedSinceApply = true;
                RefilterResult();
            };
            deleteRow.Controls.Add(_btnUndoStroke);
            _btnRestoreDeleted = new Button { Text = "Restore All", AutoSize = true, Enabled = false, Margin = new Padding(0, 3, 3, 3) };
            _btnRestoreDeleted.Click += (s, e) =>
            {
                _deletedStrokes.Clear();
                _changedSinceApply = true;
                RefilterResult();
            };
            deleteRow.Controls.Add(_btnRestoreDeleted);
            flow.Controls.Add(deleteRow);

            _help.Add(flow, 
                "Click a billboard in the preview to select it (red); click the same spot again to " +
                "step to the next billboard behind it, round to the front again. Dragging still " +
                "orbits. Delete Selected (or the Delete key in the preview) removes it and drops its " +
                "triangles - works in every preview but Original. Deletions carry over when you " +
                "change settings, onto whatever billboard lands in the same place.");

            BuildTagUi(flow);

            _lblStats = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 4, 3, 6),
            };
            flow.Controls.Add(_lblStats);

            flow.Controls.Add(new Label { Text = "Groups", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
            _groupList = new TableLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3,
                Margin = new Padding(3, 0, 3, 6),
            };
            _groupList.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _groupList.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _groupList.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            flow.Controls.Add(_groupList);

            _help.Add(flow,
                "What each part of the building was taken to be, and what it became: source " +
                "triangles in, billboards, and triangles out. Click a group to show only it in the " +
                "\"coloured by group\" preview; click it again to show them all.");

            flow.Controls.Add(new Label { Text = "Texture and apply", AutoSize = true, Margin = new Padding(3, 8, 3, 4) });

            _btnBake = MakeButton("Bake Textures (Preview)");
            _btnBake.Click += async (s, e) =>
            {
                if (await RunBakeAsync()) _previewDropdown.SelectedIndex = (int)PreviewMode.Baked;
            };
            flow.Controls.Add(_btnBake);

            _btnApply = MakeButton("Apply to Model");
            _btnApply.Click += async (s, e) => await ApplyAsync();
            flow.Controls.Add(_btnApply);

            _btnRevert = MakeButton("Revert Flatten");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => Revert();
            flow.Controls.Add(_btnRevert);

            _help.Add(flow, 
                "Apply replaces every mesh in the model with the flattened one (one material, one " +
                $"atlas of up to {BillboardBaker.MaxAtlasSize}x{BillboardBaker.MaxAtlasSize}). The original " +
                "meshes and textures are dropped from the file when you save with cleanup.");

            _lblBake = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 0, 3, 6),
            };
            flow.Controls.Add(_lblBake);

            // Magenta behind the atlas shows exactly which texels are cut out.
            _picAtlas = new PictureBox
            {
                Width = 330, Height = 330,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = System.Drawing.Color.FromArgb(255, 0, 255),
                Margin = new Padding(3, 0, 3, 6),
                Visible = false,
            };
            flow.Controls.Add(_picAtlas);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 4, 3, 6), ForeColor = System.Drawing.Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);

            _progressLabel = new ToolStripStatusLabel { Text = "", Spring = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            _progressBar = new ToolStripProgressBar { Width = 260, Visible = false, Minimum = 0, Maximum = 1000, Style = ProgressBarStyle.Continuous };
            _statusStrip = new StatusStrip { Dock = DockStyle.Bottom, SizingGrip = false };
            _statusStrip.Items.Add(_progressLabel);
            _statusStrip.Items.Add(_progressBar);
            // Added after the two panes so it is docked before them and runs the full width.
            Controls.Add(_statusStrip);
        }

        private static Button MakeButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new System.Drawing.Size(330, 0),
            Margin = new Padding(3, 3, 3, 3),
        };


        // --- settings --------------------------------------------------------------------------

        // 0..1000 -> 0.1% .. 100% of the model's largest dimension, logarithmically: the
        // interesting range (grooves at a fraction of a percent, whole walls at tens of percent)
        // spans three orders of magnitude.
        private float StrengthFraction => Fraction(_sliderStrength.Value);

        private static float Fraction(int strength) => MathF.Pow(10f, -3f + 3f * strength / 1000f);

        // 0 = off, 1..100 -> 0.01% .. 10% of the model's surface area, logarithmically.
        private float MinSizePercent => SizePercent(_sliderMinSize.Value);

        private static float SizePercent(int slider) => slider == 0 ? 0f : MathF.Pow(10f, -2f + 3f * (slider - 1) / 99f);

        private float SideMinSizePercent => SizePercent(_sliderSideMinSize.Value);

        private float DetailBoost => _sliderDetailBoost.Value / 10f;

        private ModelFlattener.FlattenSettings CurrentSettings() => SettingsAt(_sliderStrength.Value);

        private ModelFlattener.FlattenSettings SettingsAt(int strength, int? side = null, int? back = null, int? roof = null, int? detail = null) => new()
        {
            Tolerance = Fraction(strength) * (_input?.Extent ?? 1f),
            MinBillboardArea = MinSizePercent / 100f * (_input?.TotalArea ?? 0f),
            KeepLeftoversAsMesh = _chkKeepLeftovers.Checked,
            SideDetail = _chkSideDetail.Checked,
            SideMinArea = SideMinSizePercent / 100f * (_input?.TotalArea ?? 0f),
            DetailTolerance = (detail ?? _sliderDetailStrength.Value) >= 100 ? 0f : Fraction(strength) * (detail ?? _sliderDetailStrength.Value) / 100f * (_input?.Extent ?? 1f),
            DetailMinArea = MathF.Min(MinSizePercent, SideMinSizePercent) / 100f * (_input?.TotalArea ?? 0f),
            Curves = CurveHandling,
            Tags = ActiveTags(),
            GroupToleranceScale = GroupScales(strength, side, back, roof),
            Overhangs = _chkOverhangs.Checked,
            EdgeStrips = _chkEdgeStrips.Checked,
            Pediments = _chkPediments.Checked,
            Columns = _chkColumns.Checked,
            MeshOrnamentMaxTris = _cmbOrnaments.SelectedIndex == 0 ? (int)_numOrnamentMax.Value : 0,
            Backdrops = _chkBackdrops.Checked,
        };

        private ModelFlattener.CurveHandling CurveHandling => (ModelFlattener.CurveHandling)Math.Clamp(_cmbCurves.SelectedIndex, 0, 2);

        // The input for the current curve setting. The simplified-mesh one reads the model, so
        // it's built here on the UI thread, once.
        private ModelFlattener.FlattenInput CurrentInput(Activity? activity = null)
        {
            if (CurveHandling != ModelFlattener.CurveHandling.KeepAsMesh) return _input!;
            if (_curveMeshInput == null)
            {
                if (activity != null) ShowStage(activity, 0, "Simplifying curved walls");
                Cursor = Cursors.WaitCursor;
                try { _curveMeshInput = ModelFlattener.PrepareInput(_model, _input!, ModelFlattener.CurveHandling.KeepAsMesh); }
                finally { Cursor = Cursors.Default; }
            }
            return _curveMeshInput;
        }

        private void UpdateLabels()
        {
            float frac = StrengthFraction;
            string units = _input != null ? $" ({(frac * _input.Extent).ToString("0.####", CultureInfo.InvariantCulture)} units)" : "";
            _lblStrength.Text = $"Strength: {FormatPercent(frac * 100f)} of model size{units}";
            _lblMinSize.Text = _sliderMinSize.Value == 0
                ? "Minimum billboard size: off"
                : $"Minimum billboard size: {FormatPercent(MinSizePercent)} of surface area";
            _lblSideMinSize.Text = $"Minimum side billboard size: {FormatPercent(SideMinSizePercent)} of surface area";
            _lblDetailBoost.Text = $"Detail texture boost: {DetailBoost.ToString("0.0", CultureInfo.InvariantCulture)}x";
            _lblRecess.Text = $"Recess darkening: {_sliderRecess.Value}%";
            if (_sideStrength != null)
            {
                _sideStrength.Label.Text = GroupStrengthText(_sideStrength);
                _backStrength.Label.Text = GroupStrengthText(_backStrength);
                _roofStrength.Label.Text = GroupStrengthText(_roofStrength);
            }
            _lblDetailStrength.Text = _sliderDetailStrength.Value >= 100
                ? "Detail flattening: same as Strength"
                : $"Detail flattening: {_sliderDetailStrength.Value}% of Strength";
        }

        private static string FormatPercent(float pct) =>
            (pct < 1 ? pct.ToString("0.###", CultureInfo.InvariantCulture) : pct.ToString("0.#", CultureInfo.InvariantCulture)) + "%";

        // --- computing -------------------------------------------------------------------------

        // Settings changed or billboards deleted since the editor opened or the last Apply /
        // Revert. Only once it's up: building the panel sets every control from the saved settings.
        private bool _changedSinceApply, _uiReady;
        public bool HasUnappliedChanges => _changedSinceApply;
        public string UnappliedChangesDescription => "flatten settings or deleted billboards not yet applied";

        private void ScheduleCompute()
        {
            if (_uiReady) _changedSinceApply = true;
            if (_input == null) return;
            _debounce.Stop();
            // While a slider is being dragged only its label follows; the flatten waits for the
            // release (HoldWhileDragging) rather than starting over at every step of the drag.
            if (_draggingSlider) { _computeOnRelease = true; return; }
            _debounce.Start();
        }

        private const string CustomConfiguration = "Custom Configuration";
        private bool _applyingConfiguration, _listingConfigurations;

        // Fills the Configuration dropdown: Custom first, then the saved ones by name, with the
        // one the settings match selected. checkMatch (on opening) drops the remembered name if
        // the settings no longer match it.
        private void RefreshConfigurations(bool checkMatch = false)
        {
            var saved = _settings.FlattenConfigurations;
            var current = saved.Find(c => c.Name == _settings.FlattenConfigurationName);
            if (checkMatch && current != null && !current.SameSettingsAs(CaptureConfiguration(current.Name)))
                current = null;
            _settings.FlattenConfigurationName = current?.Name;

            _listingConfigurations = true;
            try
            {
                _cmbConfig.Items.Clear();
                _cmbConfig.Items.Add(CustomConfiguration);
                foreach (var c in saved) _cmbConfig.Items.Add(c.Name);
                _cmbConfig.SelectedIndex = current == null ? 0 : saved.IndexOf(current) + 1;
            }
            finally { _listingConfigurations = false; }
            _btnDeleteConfig.Enabled = current != null;
        }

        private void OnConfigurationSelected()
        {
            if (_listingConfigurations) return;
            int i = _cmbConfig.SelectedIndex;
            _btnDeleteConfig.Enabled = i > 0;
            // Custom keeps whatever the settings are now.
            if (i <= 0) { _settings.FlattenConfigurationName = null; return; }

            var config = _settings.FlattenConfigurations[i - 1];
            ApplyConfiguration(config);
            _settings.FlattenConfigurationName = config.Name;
        }

        private FlattenConfiguration CaptureConfiguration(string name) => new()
        {
            Name = name,
            Strength = _sliderStrength.Value,
            TriangleBudget = (int)_numBudget.Value,
            MinSize = _sliderMinSize.Value,
            KeepLeftovers = _chkKeepLeftovers.Checked,
            SideDetail = _chkSideDetail.Checked,
            SideMinSize = _sliderSideMinSize.Value,
            Curves = _cmbCurves.SelectedIndex,
            DetailStrength = _sliderDetailStrength.Value,
            RecessDarkening = _sliderRecess.Value,
            TrimCorners = _chkTrimCorners.Checked,
            BakeNormals = _chkBakeNormals.Checked,
            BakeMetallicRoughness = _chkBakeMr.Checked,
            DetailBoost = _sliderDetailBoost.Value,
            SideStrength = _sideStrength.Value,
            BackStrength = _backStrength.Value,
            RoofStrength = _roofStrength.Value,
            SideBudget = (int)_numSideBudget.Value,
            BackBudget = (int)_numBackBudget.Value,
            RoofBudget = (int)_numRoofBudget.Value,
            FireEscapeBudget = (int)_numFireBudget.Value,
            Overhangs = _chkOverhangs.Checked,
            EdgeStrips = _chkEdgeStrips.Checked,
            Pediments = _chkPediments.Checked,
            Columns = _chkColumns.Checked,
            OrnamentMethod = _cmbOrnaments.SelectedIndex,
            OrnamentMeshMax = (int)_numOrnamentMax.Value,
            Backdrops = _chkBackdrops.Checked,
        };

        // Sets every control, whose own handlers then update the saved settings and recompute
        // exactly as if each had been changed by hand - just without dropping back to Custom.
        private void ApplyConfiguration(FlattenConfiguration c)
        {
            static int Clamp(int v, TrackBar t) => Math.Clamp(v, t.Minimum, t.Maximum);
            _applyingConfiguration = true;
            try
            {
                _sliderStrength.Value = Clamp(c.Strength, _sliderStrength);
                _numBudget.Value = Math.Clamp(c.TriangleBudget, _numBudget.Minimum, _numBudget.Maximum);
                _sliderMinSize.Value = Clamp(c.MinSize, _sliderMinSize);
                _chkKeepLeftovers.Checked = c.KeepLeftovers;
                _chkSideDetail.Checked = c.SideDetail;
                _sliderSideMinSize.Value = Clamp(c.SideMinSize, _sliderSideMinSize);
                _cmbCurves.SelectedIndex = Math.Clamp(c.Curves, 0, _cmbCurves.Items.Count - 1);
                _sliderDetailStrength.Value = Clamp(c.DetailStrength, _sliderDetailStrength);
                _sliderRecess.Value = Clamp(c.RecessDarkening, _sliderRecess);
                _chkTrimCorners.Checked = c.TrimCorners;
                _chkBakeNormals.Checked = c.BakeNormals;
                _chkBakeMr.Checked = c.BakeMetallicRoughness;
                _sliderDetailBoost.Value = Clamp(c.DetailBoost, _sliderDetailBoost);
                foreach (var (g, v) in new[] { (_sideStrength, c.SideStrength), (_backStrength, c.BackStrength), (_roofStrength, c.RoofStrength) })
                {
                    g.Same.Checked = v < 0;
                    if (v >= 0) g.Slider.Value = Clamp(v, g.Slider);
                }
                _numSideBudget.Value = Math.Clamp(c.SideBudget, 0, 1000000);
                _numBackBudget.Value = Math.Clamp(c.BackBudget, 0, 1000000);
                _numRoofBudget.Value = Math.Clamp(c.RoofBudget, 0, 1000000);
                _numFireBudget.Value = Math.Clamp(c.FireEscapeBudget, 0, 1000000);
                _chkOverhangs.Checked = c.Overhangs;
                _chkEdgeStrips.Checked = c.EdgeStrips;
                _chkPediments.Checked = c.Pediments;
                _chkColumns.Checked = c.Columns;
                _cmbOrnaments.SelectedIndex = Math.Clamp(c.OrnamentMethod, 0, 1);
                _numOrnamentMax.Value = Math.Clamp(c.OrnamentMeshMax, 1, 5000);
                _chkBackdrops.Checked = c.Backdrops;
            }
            finally { _applyingConfiguration = false; }
        }

        // Fills the dropdown once every setting exists, then drops back to Custom whenever one is
        // changed by hand (or by Fit Strength) and so no longer matches the saved configuration.
        private void TrackConfigurationEdits()
        {
            RefreshConfigurations(checkMatch: true);

            void Edited(object? s, EventArgs e)
            {
                if (_applyingConfiguration || _listingConfigurations || _cmbConfig.SelectedIndex <= 0) return;
                _listingConfigurations = true;
                try { _cmbConfig.SelectedIndex = 0; }
                finally { _listingConfigurations = false; }
                _settings.FlattenConfigurationName = null;
                _btnDeleteConfig.Enabled = false;
            }

            foreach (var t in new[] { _sliderStrength, _sliderMinSize, _sliderSideMinSize, _sliderDetailStrength, _sliderRecess, _sliderDetailBoost })
                t.ValueChanged += Edited;
            foreach (var c in new[] { _chkKeepLeftovers, _chkSideDetail, _chkTrimCorners, _chkBakeNormals, _chkBakeMr })
                c.CheckedChanged += Edited;
            _cmbCurves.SelectedIndexChanged += Edited;
            _numBudget.ValueChanged += Edited;
            foreach (var g in new[] { _sideStrength, _backStrength, _roofStrength })
            {
                g.Same.CheckedChanged += Edited;
                g.Slider.ValueChanged += Edited;
            }
            foreach (var n in new[] { _numSideBudget, _numBackBudget, _numRoofBudget, _numFireBudget, _numOrnamentMax })
                n.ValueChanged += Edited;
            foreach (var c in new[] { _chkOverhangs, _chkEdgeStrips, _chkBackdrops, _chkPediments, _chkColumns })
                c.CheckedChanged += Edited;
            _cmbOrnaments.SelectedIndexChanged += Edited;
        }

        private void SaveConfiguration()
        {
            string initial = _settings.FlattenConfigurationName ?? "";
            string? name = NamePrompt.Show(this, "Save Configuration", "Name for the current flatten settings:", initial, _darkMode);
            if (name == null) return;
            if (string.Equals(name, CustomConfiguration, StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, $"\"{CustomConfiguration}\" is reserved for unsaved settings - choose another name.",
                    "Save Configuration", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Re-saving the selected configuration updates it without asking; any other existing
            // name is confirmed first.
            var saved = _settings.FlattenConfigurations;
            int existing = saved.FindIndex(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && saved[existing].Name != _settings.FlattenConfigurationName
                && MessageBox.Show(this, $"A configuration called \"{saved[existing].Name}\" already exists. Replace it?",
                    "Save Configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            if (existing >= 0) saved.RemoveAt(existing);
            saved.Add(CaptureConfiguration(name));
            saved.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            _settings.FlattenConfigurationName = name;
            _settings.Save();
            RefreshConfigurations();
        }

        private void DeleteConfiguration()
        {
            var current = _settings.FlattenConfigurations.Find(c => c.Name == _settings.FlattenConfigurationName);
            if (current == null) return;
            if (MessageBox.Show(this, $"Delete the saved configuration \"{current.Name}\"? The current settings stay as they are.",
                    "Delete Configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;

            _settings.FlattenConfigurations.Remove(current);
            _settings.FlattenConfigurationName = null;
            _settings.Save();
            RefreshConfigurations();
        }

        private bool _draggingSlider, _computeOnRelease;

        // Every slider in the panel: mouse down on it holds recomputing until the button comes up
        // again. Keyboard and mouse-wheel steps aren't drags and recompute as they always did.
        private void HoldWhileDragging(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TrackBar slider)
                {
                    slider.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) _draggingSlider = true; };
                    slider.MouseUp += (s, e) => ReleaseSlider();
                    // Released somewhere the slider doesn't hear about (focus stolen mid-drag).
                    slider.MouseCaptureChanged += (s, e) => { if (Control.MouseButtons == MouseButtons.None) ReleaseSlider(); };
                }
                HoldWhileDragging(c);
            }
        }

        private void ReleaseSlider()
        {
            if (!_draggingSlider) return;
            _draggingSlider = false;
            if (_computeOnRelease)
            {
                _computeOnRelease = false;
                ScheduleCompute();
            }
        }

        private async void RunCompute()
        {
            if (_input == null) return;

            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            int version = ++_computeVersion;
            var activity = BeginActivity("Flattening");
            ModelFlattener.FlattenResult result;
            ModelFlattener.FlattenInput input;
            var sw = Stopwatch.StartNew();
            try
            {
                input = CurrentInput(activity);
                var settings = CurrentSettings();
                _lblStatus.Text = "Computing billboards...";
                var progress = ProgressFor(activity);
                result = await Task.Run(() => ModelFlattener.Compute(input, settings, cts.Token, progress), cts.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (!IsDisposed && version == _computeVersion) _lblStatus.Text = "Flatten failed: " + ex.Message;
                return;
            }
            finally { EndActivity(activity); }
            if (IsDisposed || version != _computeVersion) return;

            _rawResult = result;
            _resultInput = input;
            _result = WithoutDeleted(result);
            ShowStats(_result, sw.Elapsed);
            PushResult();
            UpdateButtons();
            if (Mode == PreviewMode.Baked) _ = RunBakeAsync();
        }

        // --- painted-out billboards ------------------------------------------------------------

        private static DeletedBillboard Mark(ModelFlattener.Billboard bb)
        {
            var c0 = bb.Corner(0);
            var c2 = bb.Corner(2);
            int kind = (bb.Backdrop ? 1 : 0) | (bb.Part ? 2 : 0) | (bb.Side ? 4 : 0) | (bb.Detail ? 8 : 0)
                | (bb.Recessed ? 16 : 0) | (bb.Curve != null ? 32 : 0);
            return new DeletedBillboard(bb.Normal, (c0 + bb.Corner(1) + c2 + bb.Corner(3)) / 4f, Vector3.Distance(c0, c2), kind);
        }

        // How far this billboard is from being the deleted one, or null if it can't be: the same
        // kind, facing the same way, about the same size, and centred within a fifth of its size
        // of where the deleted one was - close enough to follow a billboard through a settings
        // change. Distance along the normal counts double, since parallel layers
        // (a facade and the backdrop behind it) differ mostly in depth.
        private static float? MatchDistance(DeletedBillboard d, DeletedBillboard mark, float slack)
        {
            if (d.Kind != mark.Kind || Vector3.Dot(d.Normal, mark.Normal) < 0.95f) return null;
            float big = MathF.Max(d.Size, mark.Size), small = MathF.Min(d.Size, mark.Size);
            if (small < big * 0.5f) return null;
            var delta = mark.Center - d.Center;
            float depth = MathF.Abs(Vector3.Dot(delta, d.Normal));
            float across = (delta - d.Normal * Vector3.Dot(delta, d.Normal)).Length();
            float distance = across + 2f * depth;
            return distance <= big * 0.2f + slack ? distance : null;
        }

        private int DeletedCount => (_rawResult?.Billboards.Count ?? 0) - (_result?.Billboards.Count ?? 0);

        private ModelFlattener.FlattenResult WithoutDeleted(ModelFlattener.FlattenResult raw) =>
            WithoutDeleted(raw, _deletedStrokes.SelectMany(s => s).ToList(), (_input?.Extent ?? 1f) * 1e-3f);

        private static ModelFlattener.FlattenResult WithoutDeleted(ModelFlattener.FlattenResult raw, List<DeletedBillboard> deleted, float slack)
        {
            if (deleted.Count == 0) return raw;
            // Each deletion takes exactly one billboard - its closest match - and each billboard
            // answers to one deletion, so deleting one billboard can never take its neighbours.
            var marks = raw.Billboards.Select(Mark).ToList();
            var pairs = new List<(float Distance, int Deleted, int Billboard)>();
            for (int d = 0; d < deleted.Count; d++)
                for (int i = 0; i < marks.Count; i++)
                    if (MatchDistance(deleted[d], marks[i], slack) is float distance) pairs.Add((distance, d, i));

            var remove = new HashSet<int>();
            var used = new HashSet<int>();
            foreach (var (_, d, i) in pairs.OrderBy(p => p.Distance))
                if (!used.Contains(d) && !remove.Contains(i)) { used.Add(d); remove.Add(i); }
            return raw.Without(remove);
        }

        // The viewer reports its highlight after every stroke; nothing is deleted until the
        // Delete Selected button.
        private void SetHighlighted(int tag, List<int> indices)
        {
            _highlightTag = tag;
            _highlighted = indices;
            UpdateButtons();
        }

        // Indices are into the result the viewer was last sent (tag), which is _result unless a
        // newer one has gone out since - then the highlight is of billboards that no longer exist
        // (the viewer has already cleared it).
        private void DeleteHighlighted()
        {
            if (_result == null || _highlightTag != _pushTag) return;
            var stroke = _highlighted.Where(i => i >= 0 && i < _result.Billboards.Count)
                .Distinct().Select(i => Mark(_result.Billboards[i])).ToList();
            if (stroke.Count == 0) return;
            _deletedStrokes.Add(stroke);
            _changedSinceApply = true;
            RefilterResult();
        }

        private void ClearHighlight()
        {
            SetHighlighted(_pushTag, new List<int>());
            if (_viewerReady && _webView.CoreWebView2 != null)
                _ = _webView.CoreWebView2.ExecuteScriptAsync("clearHighlight();");
        }

        private void RefilterResult()
        {
            SaveOverrides();
            if (_rawResult == null) return;
            _result = WithoutDeleted(_rawResult);
            ShowStats(_result, TimeSpan.Zero);
            _lblStatus.Text = DeletedCount > 0 ? $"{DeletedCount:N0} billboard(s) deleted." : "All deleted billboards restored.";
            PushResult();
            UpdateButtons();
            if (Mode == PreviewMode.Baked) _ = RunBakeAsync();
        }

        // --- baking / applying -------------------------------------------------------------------

        private bool BakeIsCurrent => _bake != null && _result != null && ReferenceEquals(_bakedFrom, _result);

        // Bakes the current result unless that's already done or under way. False if the bake
        // was superseded by a newer result or failed.
        private async Task<bool> RunBakeAsync()
        {
            if (_input == null || _result == null) return false;
            if (BakeIsCurrent) return true;
            if (_bakeTask != null && ReferenceEquals(_pendingBakeOf, _result)) return await _bakeTask;

            _pendingBakeOf = _result;
            _bakeTask = BakeAsync(_result);
            return await _bakeTask;
        }

        private async Task<bool> BakeAsync(ModelFlattener.FlattenResult flat)
        {
            int version = ++_bakeVersion;
            var input = _resultInput!;
            var bakeSettings = new BillboardBaker.BakeSettings
            {
                DetailBoost = DetailBoost,
                RecessDarkening = _sliderRecess.Value / 100f,
                TrimCorners = _chkTrimCorners.Checked,
                BakeNormals = _chkBakeNormals.Checked,
                BakeMetallicRoughness = _chkBakeMr.Checked,
            };
            _lblBake.Text = "Baking textures...";
            UpdateButtons();
            var sw = Stopwatch.StartNew();
            var activity = BeginActivity("Baking textures");
            var progress = ProgressFor(activity);

            try
            {
                // Read from the model on the UI thread: it's shared with the rest of the app, and
                // this is the only read of it the bake needs.
                if (_bakeSource == null || !ReferenceEquals(_bakeSourceFor, input))
                {
                    ShowStage(activity, 0, "Reading the source textures");
                    Cursor = Cursors.WaitCursor;
                    try { _bakeSource = BillboardBaker.ExtractSource(_model, input); _bakeSourceFor = input; }
                    finally { Cursor = Cursors.Default; }
                }
                var source = _bakeSource;
                string path = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_bake_{_sessionTag}_{version}.glb");
                string mapPath = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_bakemap_{_sessionTag}_{version}.bin");
                var bake = await Task.Run(() =>
                {
                    var b = BillboardBaker.Bake(input, flat, source, bakeSettings, CancellationToken.None, progress);
                    progress.Report(new ModelFlattener.StageProgress(BillboardBaker.PngDone, "Writing the preview"));
                    BillboardBaker.SavePreview(b, path);
                    WriteBakeMap(b, mapPath);
                    return b;
                });
                if (IsDisposed) return false;
                if (version != _bakeVersion || !ReferenceEquals(flat, _result))
                {
                    TryDelete(path);
                    TryDelete(mapPath);
                    return false;
                }

                TryDelete(_bakePath);
                TryDelete(_bakeMapPath);
                _bakePath = path;
                _bakeMapPath = mapPath;
                _bakeTag = _pushTag;
                _bake = bake;
                _bakedFrom = flat;
                ShowBake(bake, sw.Elapsed);
                PushBaked();
                return true;
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _lblBake.Text = "Bake failed: " + ex.Message;
                return false;
            }
            finally
            {
                EndActivity(activity);
                if (version == _bakeVersion) { _bakeTask = null; _pendingBakeOf = null; }
                if (!IsDisposed) UpdateButtons();
            }
        }

        // --- status bar ---------------------------------------------------------------------------

        private Activity BeginActivity(string name)
        {
            var a = new Activity { Name = name };
            _activities.Add(a);
            ShowActivity();
            return a;
        }

        private void EndActivity(Activity a)
        {
            _activities.Remove(a);
            if (!IsDisposed) ShowActivity();
        }

        // Posts back to the UI thread (made there). Ignored once the activity has ended - its last
        // posts can land after that - and never backwards: reports from parallel work can arrive
        // a step out of order.
        private IProgress<ModelFlattener.StageProgress> ProgressFor(Activity a) =>
            new Progress<ModelFlattener.StageProgress>(p =>
            {
                if (IsDisposed || !_activities.Contains(a) || p.Fraction < a.Fraction) return;
                a.Fraction = p.Fraction;
                a.Stage = p.Stage;
                ShowActivity();
            });

        // For work done right here on the UI thread: shown straight away, since nothing repaints
        // until it's finished.
        private void ShowStage(Activity a, double fraction, string stage)
        {
            a.Fraction = fraction;
            a.Stage = stage;
            ShowActivity();
            _statusStrip.Refresh();
        }

        private void ShowActivity()
        {
            if (_activities.Count == 0)
            {
                _progressBar.Visible = false;
                _progressLabel.Text = "";
                return;
            }
            var a = _activities[^1];
            _progressBar.Visible = true;
            _progressBar.Value = Math.Clamp((int)(a.Fraction * 1000), 0, 1000);
            _progressLabel.Text = a.Stage.Length > 0
                ? $"{a.Name}: {a.Stage}... {a.Fraction * 100:0}%"
                : $"{a.Name}...";
        }

        // One run's progress mapped into a slice of a longer job's bar (the budget search's tries).
        private sealed class SliceProgress(IProgress<ModelFlattener.StageProgress> outer, double from, double to, string label)
            : IProgress<ModelFlattener.StageProgress>
        {
            public void Report(ModelFlattener.StageProgress p) =>
                outer.Report(new ModelFlattener.StageProgress(from + (to - from) * p.Fraction, $"{label} - {p.Stage}"));
        }

        private void ShowBake(BillboardBaker.BakeResult bake, TimeSpan elapsed)
        {
            _lblBake.Text =
                $"Atlas {bake.Width}x{bake.Height} at {bake.TexelsPerUnit:0} texels/unit ({bake.DetailTexelsPerUnit:0} for detail), " +
                $"{bake.AtlasPng.Length / 1024.0 / 1024.0:0.0} MB PNG" +
                (bake.HasNormals ? $" + {bake.NormalPng.Length / 1024.0 / 1024.0:0.0} MB normal map" : "") +
                (bake.HasMetallicRoughness ? $" + {bake.MetallicRoughnessPng.Length / 1024.0 / 1024.0:0.0} MB metallic/roughness" : "") + "\n" +
                $"{bake.TriangleCount:N0} triangles; {bake.MaskedBillboards} of {bake.Billboards} billboards use " +
                $"transparency (alpha cutoff 0.5)\nBaked in {elapsed.TotalSeconds:0.0} s.";

            var old = _picAtlas.Image;
            using (var ms = new MemoryStream(bake.AtlasPng))
            using (var img = System.Drawing.Image.FromStream(ms))
                _picAtlas.Image = new System.Drawing.Bitmap(img);
            old?.Dispose();
            _picAtlas.Visible = true;
        }

        private async Task ApplyAsync()
        {
            if (!await RunBakeAsync() || _bake == null || IsDisposed) return;

            // The "Original model" preview has to be captured before the model stops being it.
            if (_originalPath == null)
            {
                Cursor = Cursors.WaitCursor;
                try
                {
                    _originalPath = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_orig_{_sessionTag}.glb");
                    _model.SaveGLB(_originalPath);
                }
                finally { Cursor = Cursors.Default; }
            }

            if (_originalMeshes == null)
                _originalMeshes = ModelFlattener.MeshNodes(_model).Select(n => (n, n.Mesh!)).ToList();
            else
                RestoreOriginalMeshes(); // re-applying replaces the previous flatten rather than stacking on it

            // The flattened mesh goes on the first mesh node, re-expressed in its local space, so
            // no node has to be added for it (or removed again on Revert).
            var host = _originalMeshes[0].Node;
            var world = host.WorldMatrix;
            if (!System.Numerics.Matrix4x4.Invert(world, out _))
            {
                _lblStatus.Text = "Can't apply: the model's first mesh node has a degenerate transform.";
                return;
            }

            var mesh = _model.CreateMesh(BillboardBaker.BuildMesh(_bake, world, "Flattened"));
            foreach (var (node, _) in _originalMeshes) node.Mesh = null;
            host.Mesh = mesh;

            _btnRevert.Enabled = true;
            _changedSinceApply = false;
            _lblStatus.Text = $"Applied: the model is now {_bake.TriangleCount:N0} triangles. " +
                "Save with cleanup to drop the original meshes and textures from the file.";
        }

        private void Revert()
        {
            if (_originalMeshes == null) return;
            RestoreOriginalMeshes();
            _originalMeshes = null;
            _btnRevert.Enabled = false;
            _changedSinceApply = false;
            _lblStatus.Text = "Flatten reverted - the original meshes are back.";
        }

        private void RestoreOriginalMeshes()
        {
            foreach (var (node, mesh) in _originalMeshes!) node.Mesh = mesh;
        }

        private void UpdateButtons()
        {
            bool baking = _bakeTask != null;
            _btnBake.Enabled = !baking && _result != null;
            _btnApply.Enabled = !baking && _result != null;
            bool anyHighlighted = _highlighted.Count > 0 && _highlightTag == _pushTag;
            _btnDeleteHighlighted.Enabled = anyHighlighted;
            _btnClearHighlight.Enabled = anyHighlighted;
            _btnUndoStroke.Enabled = _deletedStrokes.Count > 0;
            _btnRestoreDeleted.Enabled = _deletedStrokes.Count > 0;
        }

        private void ShowStats(ModelFlattener.FlattenResult r, TimeSpan elapsed)
        {
            int before = _input!.TriangleCount;
            int after = r.OutputTriangles;
            int needAlpha = 0;
            foreach (var bb in r.Billboards) if (bb.Coverage < TransparencyCoverage) needAlpha++;

            string pct = before > 0 ? $" ({100.0 * (before - after) / before:0.#}% fewer)" : "";
            _lblStats.Text =
                $"Triangles: {before:N0} -> {after:N0}{pct}\n" +
                $"Billboards: {r.Billboards.Count:N0} quads ({r.Billboards.Count * 2:N0} tris) on {r.PlaneCount:N0} planes\n" +
                $"Kept as mesh: {r.KeptTriangles:N0} tris    Dropped: {r.DroppedTriangles:N0} tris\n" +
                $"Will need transparency: {needAlpha:N0} billboard(s)" +
                (DeletedCount > 0 ? $"\nDeleted by painting: {DeletedCount:N0} billboard(s)" : "");
            _lblStatus.Text = $"Computed in {elapsed.TotalSeconds:0.00} s.";
            ShowGroups(r);
        }

        // One row per group present: its colour, name, and source triangles -> billboards ->
        // triangles out. Rebuilt with every result.
        private void ShowGroups(ModelFlattener.FlattenResult r)
        {
            var over = UpdateBudgetHeaders(r);
            var tris = new Dictionary<FeatureGroup, int>();
            foreach (var g in r.TriangleGroup) tris[g] = tris.GetValueOrDefault(g) + 1;
            var boards = new Dictionary<FeatureGroup, int>();
            var outTris = new Dictionary<FeatureGroup, int>();
            foreach (var bb in r.Billboards)
            {
                boards[bb.Group] = boards.GetValueOrDefault(bb.Group) + 1;
                outTris[bb.Group] = outTris.GetValueOrDefault(bb.Group) + (bb.Curve != null ? 2 * bb.Curve.Segments : 2);
            }
            // What stays mesh is its own output.
            for (int t = 0; t < r.Assignment.Length && t < r.TriangleGroup.Length; t++)
                if (r.Assignment[t] == ModelFlattener.FlattenResult.Kept)
                    outTris[r.TriangleGroup[t]] = outTris.GetValueOrDefault(r.TriangleGroup[t]) + 1;

            _groupList.SuspendLayout();
            foreach (Control c in _groupList.Controls.Cast<Control>().ToList()) c.Dispose();
            _groupList.Controls.Clear();
            _groupList.RowStyles.Clear();
            _groupList.RowCount = 1;
            _groupList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var header = new Label { Text = "tris in > billboards > tris out", AutoSize = true, Margin = new Padding(0, 0, 0, 2) };
            _groupList.Controls.Add(header, 2, 0);
            foreach (var g in Enum.GetValues<FeatureGroup>())
            {
                int inCount = tris.GetValueOrDefault(g), bbCount = boards.GetValueOrDefault(g);
                if (inCount == 0 && bbCount == 0) continue;
                int rgb = FeatureGroups.Color(g);
                var swatch = new Label
                {
                    AutoSize = false, Size = new System.Drawing.Size(12, 12), Margin = new Padding(0, 4, 6, 0),
                    BackColor = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255),
                };
                var name = new Label { Text = FeatureGroups.Name(g), AutoSize = true, Margin = new Padding(0, 1, 8, 1) };
                if (g == _groupFocus) name.Font = new System.Drawing.Font(name.Font, System.Drawing.FontStyle.Bold);
                string inText = inCount > 0 ? $"{inCount:N0}" : "-";
                var counts = new Label
                {
                    Text = g == FeatureGroup.Dropped ? $"{inCount:N0}" : $"{inText} > {bbCount:N0} > {outTris.GetValueOrDefault(g):N0}",
                    AutoSize = true, Margin = new Padding(0, 1, 0, 1),
                };
                int row = _groupList.RowCount++;
                _groupList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _groupList.Controls.Add(swatch, 0, row);
                _groupList.Controls.Add(name, 1, row);
                _groupList.Controls.Add(counts, 2, row);
                foreach (var c in new Control[] { swatch, name, counts })
                {
                    c.Cursor = Cursors.Hand;
                    c.Click += (s, e) => FocusGroup(g == _groupFocus ? FeatureGroup.None : g);
                }
            }
            _groupList.ResumeLayout();
            foreach (Control c in _groupList.Controls)
                if (c.AutoSize) c.ForeColor = ForeColor;
            // In the "coloured by group" preview, each name in its group's colour.
            if (Mode == PreviewMode.Groups)
                for (int row = 1; row < _groupList.RowCount; row++)
                    if (_groupList.GetControlFromPosition(1, row) is Label name
                        && Enum.GetValues<FeatureGroup>().FirstOrDefault(g => FeatureGroups.Name(g) == name.Text) is var g && g != FeatureGroup.None)
                    {
                        int rgb = FeatureGroups.Color(g);
                        name.ForeColor = System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
                    }
            // Over its budget: the counts in orange.
            for (int row = 1; row < _groupList.RowCount; row++)
                if (_groupList.GetControlFromPosition(1, row) is Label name && _groupList.GetControlFromPosition(2, row) is Label counts
                    && Enum.GetValues<FeatureGroup>().FirstOrDefault(g => FeatureGroups.Name(g) == name.Text) is var g && over.GetValueOrDefault(g))
                    counts.ForeColor = System.Drawing.Color.DarkOrange;
        }

        // Shows only one group in the "coloured by group" preview (None: all of them).
        private void FocusGroup(FeatureGroup g)
        {
            _groupFocus = g;
            if (_result != null) ShowGroups(_result);
            if (g != FeatureGroup.None && Mode != PreviewMode.Groups) _previewDropdown.SelectedIndex = (int)PreviewMode.Groups;
            PushGroupFocus();
        }

        private void PushGroupFocus()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setGroupFocus({(int)_groupFocus});");
        }

        // The group colours, indexed by FeatureGroup, for the preview script.
        private static string GroupColorsJs =>
            "[" + string.Join(",", Enum.GetValues<FeatureGroup>().Select(g => "0x" + FeatureGroups.Color(g).ToString("X6"))) + "]";

        // --- preview files -----------------------------------------------------------------------

        // Every source triangle's three world-space corners as float32 - written once, since the
        // geometry itself never changes while this editor is open.
        private string WriteSourceFile()
        {
            _sourcePath = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_src_{_sessionTag}.bin");
            using var fs = new FileStream(_sourcePath, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            foreach (var p in _input!.Corners) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            return Path.GetFileName(_sourcePath);
        }

        // Layout: int32 assignment per source triangle, then float32 x12 corners per billboard,
        // (groups at the end - see below)
        // then float32 coverage per billboard. A fresh file per result, so the browser never
        // reads a cached older one.
        private string WriteResultFile(ModelFlattener.FlattenResult r)
        {
            var previous = _resultPath;
            _resultPath = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_res_{_sessionTag}_{_resultVersion++}.bin");
            using (var fs = new FileStream(_resultPath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(fs))
            {
                // The preview shows the gathered model; a rebuilt input (curves kept as simplified
                // mesh) is mapped back onto it, the replaced triangles showing as kept mesh.
                var gathered = _resultInput?.GatheredIndex ?? Array.Empty<int>();
                if (gathered.Length == 0)
                    foreach (int a in r.Assignment) w.Write(a);
                else
                {
                    var assignment = Enumerable.Repeat(ModelFlattener.FlattenResult.Kept, _input!.TriangleCount).ToArray();
                    for (int t = 0; t < gathered.Length; t++)
                        if (gathered[t] >= 0) assignment[gathered[t]] = r.Assignment[t];
                    foreach (int a in assignment) w.Write(a);
                }
                foreach (var bb in r.Billboards)
                    for (int i = 0; i < 4; i++)
                    {
                        var c = bb.Corner(i);
                        w.Write(c.X); w.Write(c.Y); w.Write(c.Z);
                    }
                foreach (var bb in r.Billboards) w.Write(bb.Coverage);
                // Then int32 group per billboard, and per source triangle (the curves a rebuilt
                // input replaced show as curved walls).
                foreach (var bb in r.Billboards) w.Write((int)bb.Group);
                if (gathered.Length == 0)
                    foreach (var g in r.TriangleGroup) w.Write((int)g);
                else
                {
                    var groups = Enumerable.Repeat((int)FeatureGroup.CurvedWall, _input!.TriangleCount).ToArray();
                    for (int t = 0; t < gathered.Length; t++)
                        if (gathered[t] >= 0 && t < r.TriangleGroup.Length) groups[gathered[t]] = (int)r.TriangleGroup[t];
                    foreach (int g in groups) w.Write(g);
                }
            }
            TryDelete(previous);
            return Path.GetFileName(_resultPath);
        }

        private static void TryDelete(string? path)
        {
            if (path == null) return;
            try { File.Delete(path); }
            catch (IOException) { /* still held by the browser - harmless, it's a temp file */ }
            catch (UnauthorizedAccessException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cts?.Cancel();
                _debounce.Dispose();
                TryDelete(_sourcePath);
                TryDelete(_resultPath);
                TryDelete(_originalPath);
                TryDelete(_bakePath);
                TryDelete(_bakeMapPath);
                _picAtlas.Image?.Dispose();
            }
            base.Dispose(disposing);
        }

        // --- viewer ------------------------------------------------------------------------------

        private void PushResult()
        {
            if (!_viewerReady || _result == null || _webView.CoreWebView2 == null) return;
            string file = WriteResultFile(_result);
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"showResult('https://appassets.local/{EscapeJs(file)}', {_result.Billboards.Count}, {++_pushTag});");
        }

        private void PushPaintState()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setPaintState({(_chkPaintDelete.Checked ? "true" : "false")});");
        }

        private void PushBaked()
        {
            if (!_viewerReady || _bakePath == null || _bakeMapPath == null || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"loadBaked('https://appassets.local/{EscapeJs(Path.GetFileName(_bakePath))}', " +
                $"'https://appassets.local/{EscapeJs(Path.GetFileName(_bakeMapPath))}', {_bakeTag});");
        }

        // Which billboard each baked triangle belongs to, with its positions and atlas UVs, so the
        // preview can paint a selected billboard's visible texture - what deleting it removes.
        // Layout: count, then per triangle its billboard (-1 = kept mesh), then 9 position floats
        // per triangle, then 6 UV floats per triangle.
        private static void WriteBakeMap(BillboardBaker.BakeResult bake, string path)
        {
            using var w = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write));
            w.Write(bake.TriangleCount);
            for (int t = 0; t < bake.TriangleCount; t++) w.Write(bake.TriangleBillboard[t]);
            foreach (var p in bake.Positions) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            foreach (var uv in bake.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
        }

        private static readonly System.Drawing.Color DefaultBackground = System.Drawing.Color.FromArgb(0x1A, 0x1C, 0x1E);

        private System.Drawing.Color BackgroundColor
        {
            get
            {
                try { return System.Drawing.ColorTranslator.FromHtml(_settings.FlattenBackgroundColor); }
                catch { return DefaultBackground; }
            }
        }

        private void PickBackground()
        {
            using var dlg = new ColorDialog { Color = BackgroundColor, FullOpen = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) SetBackground(dlg.Color);
        }

        private void SetBackground(System.Drawing.Color c)
        {
            _settings.FlattenBackgroundColor = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            UpdateBackgroundButton();
            PushBackground();
        }

        private void UpdateBackgroundButton()
        {
            var c = BackgroundColor;
            _btnBackground.Text = _settings.FlattenBackgroundColor.ToUpperInvariant();
            _btnBackground.BackColor = c;
            _btnBackground.ForeColor = c.GetBrightness() > 0.5f ? System.Drawing.Color.Black : System.Drawing.Color.White;
            _btnBackground.UseVisualStyleBackColor = false;
        }

        private void PushBackground()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            var c = BackgroundColor;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setBackground({(c.R << 16) | (c.G << 8) | c.B});");
        }

        private void PushViewState()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;

            string mode = Mode switch
            {
                PreviewMode.Baked => "baked",
                PreviewMode.Source => "source",
                PreviewMode.Original => "original",
                PreviewMode.Groups => "groups",
                _ => "billboards",
            };
            if (mode == "original" && _originalPath == null)
            {
                // Only written on first request: the original, with its full-size textures, is
                // by far the slowest thing this editor would otherwise save up front.
                Cursor = Cursors.WaitCursor;
                try
                {
                    _originalPath = Path.Combine(Path.GetTempPath(), $"glbmerger_flatten_orig_{_sessionTag}.glb");
                    _model.SaveGLB(_originalPath);
                }
                finally { Cursor = Cursors.Default; }
                _ = _webView.CoreWebView2.ExecuteScriptAsync(
                    $"loadOriginal('https://appassets.local/{EscapeJs(Path.GetFileName(_originalPath))}');");
            }

            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setViewState('{mode}', {(_chkOutline.Checked ? "true" : "false")}, " +
                $"{(_chkHighlight.Checked ? "true" : "false")}, {TransparencyCoverage.ToString(CultureInfo.InvariantCulture)});");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(e.TryGetWebMessageAsString()); }
            catch { return; }
            using (doc)
            {
                string? action;
                int tag = 0;
                var indices = new List<int>();
                var root = doc.RootElement;
                try
                {
                    action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
                    if (root.TryGetProperty("tag", out var tg)) tag = tg.GetInt32();
                    if (root.TryGetProperty("indices", out var idx))
                        foreach (var i in idx.EnumerateArray()) indices.Add(i.GetInt32());
                }
                catch { return; }

                if (IsDisposed) return;
                if (action == "ready")
                {
                    _viewerReady = true;
                    PushBackground();
                    PushViewState();
                    PushPaintState();
                    PushResult();
                    PushBaked();
                    PushGroupFocus();
                    PushFrame();
                }
                else if (action == "highlight")
                    SetHighlighted(tag, indices);
                else if (action == "deleteSelected")
                    DeleteHighlighted();
                else
                    OnTagMessage(action, root, indices);
            }
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

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

            string sourceFile = WriteSourceFile();

            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewport { width: 100%; height: 100%; display: block; }
                    #pickInfo {
                        position: absolute; right: 10px; bottom: 10px; color: #ff9a9a;
                        font-family: Segoe UI, sans-serif; font-size: 12px; pointer-events: none;
                    }
                    #info {
                        position: absolute; left: 10px; bottom: 10px; color: #ccc;
                        font-family: Segoe UI, sans-serif; font-size: 12px; pointer-events: none;
                    }
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
                <div id='pickInfo'></div>
                <div id='info'></div>
                <div id='error-overlay'></div>
                <script>
                    function showError(msg) {
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };
                    function setInfo(text) { document.querySelector('#info').textContent = text; }

                    try {
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

                        scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.0));
                        scene.add(new THREE.AmbientLight(0xffffff, 0.35));
                        var dirLight = new THREE.DirectionalLight(0xffffff, 0.9);
                        dirLight.position.set(5, 10, 7.5);
                        scene.add(dirLight);
                        var fillLight = new THREE.DirectionalLight(0xffffff, 0.4);
                        fillLight.position.set(-5, 5, -7.5);
                        scene.add(fillLight);

                        // --- state ------------------------------------------------------------
                        var srcPositions = null, triCount = 0;
                        var assignment = null, quads = null, coverage = null, quadCount = 0;
                        var triGroups = null, groupFocus = 0;
                        var groupColors = " + GroupColorsJs + @";
                        var mode = 'billboards', outline = true, highlight = false, alphaThreshold = 0.95;

                        var srcMesh = null, keptMesh = null, bbFront = null, bbBack = null, outlines = null, originalRoot = null, bakedRoot = null;

                        var tmpColor = new THREE.Color();
                        function billboardColor(i) {
                            if (highlight) return tmpColor.setHex(coverage[i] < alphaThreshold ? 0xff8a1f : 0x5a6f8a);
                            return tmpColor.setHSL((i * 0.6180339887) % 1, 0.6, 0.55);
                        }
                        function assignmentColor(a) {
                            if (a === -1) return tmpColor.setHex(0x9a9a9a);  // kept as mesh
                            if (a === -2) return tmpColor.setHex(0x7a1010);  // dropped
                            return billboardColor(a);
                        }

                        function disposeObject(obj) {
                            if (!obj) return;
                            scene.remove(obj);
                            if (obj.geometry) obj.geometry.dispose();
                        }

                        // --- source triangles -------------------------------------------------
                        function buildSource() {
                            var geo = new THREE.BufferGeometry();
                            geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(srcPositions), 3));
                            geo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(srcPositions.length), 3));
                            geo.computeVertexNormals();
                            srcMesh = new THREE.Mesh(geo, new THREE.MeshLambertMaterial({ vertexColors: true, side: THREE.DoubleSide }));
                            scene.add(srcMesh);
                        }

                        // By group: the group's colour; with one group picked, the rest dark.
                        function groupColor(t) {
                            var g = triGroups ? triGroups[t] : 0;
                            if (groupFocus && g !== groupFocus) return tmpColor.setHex(0x2a2d30);
                            return tmpColor.setHex(groupColors[g] || 0xff00ff);
                        }

                        function updateSourceColors() {
                            if (!srcMesh || !assignment) return;
                            var col = srcMesh.geometry.attributes.color.array;
                            for (var t = 0; t < triCount; t++) {
                                if (mode === 'groups') groupColor(t); else assignmentColor(assignment[t]);
                                for (var k = 0; k < 3; k++) {
                                    var o = (t * 3 + k) * 3;
                                    col[o] = tmpColor.r; col[o + 1] = tmpColor.g; col[o + 2] = tmpColor.b;
                                }
                            }
                            srcMesh.geometry.attributes.color.needsUpdate = true;
                        }

                        function buildKept() {
                            disposeObject(keptMesh);
                            keptMesh = null;
                            var n = 0;
                            for (var t = 0; t < triCount; t++) if (assignment[t] === -1) n++;
                            if (n === 0) return;
                            var pos = new Float32Array(n * 9), w = 0;
                            for (var t2 = 0; t2 < triCount; t2++) {
                                if (assignment[t2] !== -1) continue;
                                for (var i = 0; i < 9; i++) pos[w++] = srcPositions[t2 * 9 + i];
                            }
                            var geo = new THREE.BufferGeometry();
                            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
                            geo.computeVertexNormals();
                            keptMesh = new THREE.Mesh(geo, new THREE.MeshLambertMaterial({ color: 0x9a9a9a, side: THREE.DoubleSide }));
                            scene.add(keptMesh);
                        }

                        // --- billboards -------------------------------------------------------
                        var frontMaterial = new THREE.MeshLambertMaterial({
                            vertexColors: true, side: THREE.FrontSide,
                            polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
                        });
                        var backMaterial = new THREE.MeshBasicMaterial({
                            color: 0x2c3036, side: THREE.BackSide,
                            polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
                        });
                        var outlineMaterial = new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.55 });

                        function buildBillboards() {
                            disposeObject(bbFront); disposeObject(bbBack); disposeObject(outlines);
                            bbFront = bbBack = outlines = null;
                            if (quadCount === 0) return;

                            var pos = new Float32Array(quadCount * 18);
                            var col = new Float32Array(quadCount * 18);
                            var lines = new Float32Array(quadCount * 24);
                            var order = [0, 1, 2, 0, 2, 3];
                            for (var q = 0; q < quadCount; q++) {
                                billboardColor(q);
                                for (var k = 0; k < 6; k++) {
                                    var c = order[k];
                                    for (var a = 0; a < 3; a++) pos[q * 18 + k * 3 + a] = quads[q * 12 + c * 3 + a];
                                    col[q * 18 + k * 3] = tmpColor.r; col[q * 18 + k * 3 + 1] = tmpColor.g; col[q * 18 + k * 3 + 2] = tmpColor.b;
                                }
                                for (var e = 0; e < 4; e++) {
                                    var c0 = e, c1 = (e + 1) % 4;
                                    for (var a2 = 0; a2 < 3; a2++) {
                                        lines[q * 24 + e * 6 + a2] = quads[q * 12 + c0 * 3 + a2];
                                        lines[q * 24 + e * 6 + 3 + a2] = quads[q * 12 + c1 * 3 + a2];
                                    }
                                }
                            }

                            var geo = new THREE.BufferGeometry();
                            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
                            geo.setAttribute('color', new THREE.BufferAttribute(col, 3));
                            geo.computeVertexNormals();
                            bbFront = new THREE.Mesh(geo, frontMaterial);
                            bbBack = new THREE.Mesh(geo, backMaterial);
                            scene.add(bbFront);
                            scene.add(bbBack);

                            var lineGeo = new THREE.BufferGeometry();
                            lineGeo.setAttribute('position', new THREE.BufferAttribute(lines, 3));
                            outlines = new THREE.LineSegments(lineGeo, outlineMaterial);
                            scene.add(outlines);
                        }

                        function applyVisibility() {
                            if (srcMesh) srcMesh.visible = mode === 'source' || mode === 'groups';
                            if (keptMesh) keptMesh.visible = mode === 'billboards';
                            if (bbFront) bbFront.visible = mode === 'billboards';
                            if (bbBack) bbBack.visible = mode === 'billboards';
                            if (outlines) outlines.visible = outline;
                            if (originalRoot) originalRoot.visible = mode === 'original';
                            if (bakedRoot) bakedRoot.visible = mode === 'baked';
                            if (mode === 'original' && !originalRoot) setInfo('Loading original model...');
                            else if (mode === 'baked' && !bakedRoot) setInfo('Baking textures...');
                            else setInfo('');
                        }

                        window.setBackground = function (hex) {
                            scene.background = new THREE.Color(hex);
                            document.body.style.background = '#' + ('000000' + hex.toString(16)).slice(-6);
                        };

                        window.setGroupFocus = function (g) {
                            groupFocus = g;
                            updateSourceColors();
                        };

                        window.setViewState = function (m, o, h, threshold) {
                            var recolor = h !== highlight || (m === 'groups') !== (mode === 'groups');
                            mode = m; outline = o; highlight = h; alphaThreshold = threshold;
                            if (recolor && assignment) { updateSourceColors(); buildBillboards(); }
                            // The pink comes from the bake only in the baked preview.
                            if (pending.size > 0) rebuildPending();
                            applyVisibility();
                        };

                        window.showResult = function (url, count, tag) {
                            fetch(url).then(function (r) { return r.arrayBuffer(); }).then(function (buf) {
                                // Results can arrive out of order; only the newest is the one
                                // the host's indices refer to.
                                if (tag < resultTag) return;
                                resultTag = tag;
                                quadCount = count;
                                clearPending();
                                assignment = new Int32Array(buf, 0, triCount);
                                quads = new Float32Array(buf, triCount * 4, count * 12);
                                coverage = new Float32Array(buf, triCount * 4 + count * 48, count);
                                var groupsAt = triCount * 4 + count * 52;
                                triGroups = buf.byteLength >= groupsAt + count * 4 + triCount * 4
                                    ? new Int32Array(buf, groupsAt + count * 4, triCount) : null;
                                updateSourceColors();
                                buildKept();
                                buildBillboards();
                                applyVisibility();
                            }).catch(function (err) { showError('Failed to load result: ' + err); });
                        };

                        // The baked result is drawn the way the game will draw it: one-sided and
                        // alpha-tested - GLTFLoader sets both from the file's own material.
                        window.loadBaked = function (url, mapUrl, tag) {
                            fetch(mapUrl).then(function (r) { return r.arrayBuffer(); }).then(function (buf) {
                                var n = new Int32Array(buf, 0, 1)[0];
                                bakeMap = {
                                    tag: tag,
                                    billboard: new Int32Array(buf, 4, n),
                                    positions: new Float32Array(buf, 4 + n * 4, n * 9),
                                    uvs: new Float32Array(buf, 4 + n * 40, n * 6),
                                };
                                rebuildPending();
                            }).catch(function () { bakeMap = null; });
                            new THREE.GLTFLoader().load(url, function (gltf) {
                                if (bakedRoot) {
                                    scene.remove(bakedRoot);
                                    bakedRoot.traverse(function (obj) {
                                        if (obj.geometry) obj.geometry.dispose();
                                        if (obj.material && obj.material.map) obj.material.map.dispose();
                                    });
                                }
                                bakedRoot = gltf.scene;
                                bakedAtlas = null;
                                bakedRoot.traverse(function (obj) {
                                    if (!bakedAtlas && obj.isMesh && obj.material && obj.material.map) bakedAtlas = obj.material.map;
                                });
                                scene.add(bakedRoot);
                                rebuildPending();
                                applyVisibility();
                            }, undefined, function (error) {
                                showError('Failed to load baked result: ' + (error && error.message ? error.message : error));
                            });
                        };

                        window.loadOriginal = function (url) {
                            new THREE.GLTFLoader().load(url, function (gltf) {
                                originalRoot = gltf.scene;
                                originalRoot.traverse(function (obj) {
                                    if (!obj.isMesh || !obj.material) return;
                                    var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                                    mats.forEach(function (mat) {
                                        mat.side = THREE.DoubleSide;
                                        // Opaque with depth writes, but still alpha-tested where
                                        // the texture has cut-outs; real translucency (glass) keeps
                                        // its blending.
                                        if (mat.transparent && mat.opacity >= 0.5) {
                                            mat.transparent = false;
                                            mat.depthWrite = true;
                                            if (!mat.alphaTest) mat.alphaTest = 0.5;
                                        }
                                        mat.polygonOffset = true; mat.polygonOffsetFactor = 1; mat.polygonOffsetUnits = 1;
                                    });
                                });
                                scene.add(originalRoot);
                                applyVisibility();
                            }, undefined, function (error) {
                                showError('Failed to load original model: ' + (error && error.message ? error.message : error));
                            });
                        };

                        // --- selecting billboards to delete --------------------------------------
                        // A click (press and release without dragging, so orbiting still works)
                        // selects the nearest billboard under the cursor: its whole quad drawn red,
                        // and what of it is actually visible - the texture the delete takes away -
                        // neon pink. Both show through whatever is in front, so a billboard picked
                        // from behind the front ones is still plain to see. Billboards
                        // stack - a facade, the window layer behind it, a backdrop behind them all -
                        // so clicking the same spot again steps to the next billboard along the
                        // ray, and round to the front again after the last. The selection goes to
                        // the host, which deletes it on its Delete button (or the Delete key here)
                        // and sends back the new result, which clears the selection.
                        var pickInfo = document.querySelector('#pickInfo');
                        var paintMode = false, downAt = null, lastClick = null;
                        var resultTag = 0, pending = new Set(), pendingMesh = null;
                        var raycaster = new THREE.Raycaster();
                        var pendingMaterial = new THREE.MeshBasicMaterial({
                            color: 0xff3030, transparent: true, opacity: 0.45, side: THREE.DoubleSide,
                            depthTest: false, depthWrite: false,
                        });
                        var bakeMap = null, bakedAtlas = null, pendingTexMesh = null;
                        // The baked atlas's own alpha cut-out, in solid pink.
                        var pendingTexMaterial = new THREE.ShaderMaterial({
                            uniforms: { map: { value: null } },
                            vertexShader: 'varying vec2 vUv; void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }',
                            fragmentShader: 'uniform sampler2D map; varying vec2 vUv; void main() { if (texture2D(map, vUv).a < 0.5) discard; gl_FragColor = vec4(1.0, 0.063, 0.941, 0.9); }',
                            transparent: true, side: THREE.DoubleSide, depthTest: false, depthWrite: false,
                        });
                        var pendingSrcMaterial = new THREE.MeshBasicMaterial({
                            color: 0xff10f0, transparent: true, opacity: 0.9, side: THREE.DoubleSide,
                            depthTest: false, depthWrite: false,
                        });

                        window.setPaintState = function (enabled) {
                            paintMode = enabled;
                            canvas.style.cursor = enabled ? 'crosshair' : '';
                        };

                        function postSelection() {
                            if (window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({
                                    action: 'highlight', tag: resultTag, indices: Array.from(pending),
                                }));
                            }
                        }

                        function clearPending() {
                            pending.clear();
                            lastClick = null;
                            pickInfo.textContent = '';
                            rebuildPending();
                        }
                        window.clearHighlight = clearPending;

                        function rebuildPending() {
                            disposeObject(pendingMesh);
                            disposeObject(pendingTexMesh);
                            pendingMesh = pendingTexMesh = null;
                            if (pending.size === 0) return;
                            pendingTexMesh = buildPendingTexture();
                            if (pendingTexMesh) { pendingTexMesh.renderOrder = 11; scene.add(pendingTexMesh); }
                            var pos = new Float32Array(pending.size * 18), w = 0;
                            var order = [0, 1, 2, 0, 2, 3];
                            pending.forEach(function (q) {
                                for (var k = 0; k < 6; k++)
                                    for (var a = 0; a < 3; a++) pos[w++] = quads[q * 12 + order[k] * 3 + a];
                            });
                            var geo = new THREE.BufferGeometry();
                            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
                            pendingMesh = new THREE.Mesh(geo, pendingMaterial);
                            pendingMesh.renderOrder = 10;
                            scene.add(pendingMesh);
                        }

                        // The selected billboards' visible texture. From the bake when it was made
                        // from this result (the indices match); otherwise the source triangles the
                        // billboards were made from, which is what their texture will show.
                        function buildPendingTexture() {
                            var geo = new THREE.BufferGeometry();
                            if (mode === 'baked' && bakeMap && bakedAtlas && bakeMap.tag === resultTag) {
                                var tris = [];
                                for (var t = 0; t < bakeMap.billboard.length; t++) if (pending.has(bakeMap.billboard[t])) tris.push(t);
                                if (tris.length === 0) return null;
                                var pos = new Float32Array(tris.length * 9), uv = new Float32Array(tris.length * 6);
                                tris.forEach(function (t, k) {
                                    pos.set(bakeMap.positions.subarray(t * 9, t * 9 + 9), k * 9);
                                    uv.set(bakeMap.uvs.subarray(t * 6, t * 6 + 6), k * 6);
                                });
                                geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
                                geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
                                pendingTexMaterial.uniforms.map.value = bakedAtlas;
                                return new THREE.Mesh(geo, pendingTexMaterial);
                            }
                            if (!assignment || !srcPositions) return null;
                            var src = [];
                            for (var s = 0; s < triCount; s++) if (pending.has(assignment[s])) src.push(s);
                            if (src.length === 0) return null;
                            var spos = new Float32Array(src.length * 9);
                            src.forEach(function (s, k) { spos.set(srcPositions.subarray(s * 9, s * 9 + 9), k * 9); });
                            geo.setAttribute('position', new THREE.BufferAttribute(spos, 3));
                            return new THREE.Mesh(geo, pendingSrcMaterial);
                        }

                        // Every billboard a ray through this pixel passes through, nearest first.
                        // Works off the billboard (or source) geometry even while it's hidden, so
                        // it also picks in the baked preview, which is drawn from the same quads.
                        var _ndc = new THREE.Vector2();
                        function billboardsAt(x, y) {
                            var rect = canvas.getBoundingClientRect();
                            _ndc.set((x - rect.left) / rect.width * 2 - 1, -((y - rect.top) / rect.height) * 2 + 1);
                            raycaster.setFromCamera(_ndc, camera);
                            var stack = [];
                            function add(q) { if (q >= 0 && q < quadCount && stack.indexOf(q) < 0) stack.push(q); }
                            if (mode === 'source' || mode === 'groups') {
                                if (srcMesh) raycaster.intersectObject(srcMesh, false).forEach(function (h) { add(assignment[h.faceIndex]); });
                                return stack;
                            }
                            var targets = [bbFront, bbBack].filter(function (o) { return o; });
                            raycaster.intersectObjects(targets, false).forEach(function (h) { add(Math.floor(h.faceIndex / 2)); });
                            return stack;
                        }

                        function selectAt(x, y) {
                            var stack = billboardsAt(x, y);
                            var index = 0;
                            // Same spot as last time: the next one back from what's selected.
                            if (lastClick && Math.abs(x - lastClick.x) <= 4 && Math.abs(y - lastClick.y) <= 4 && pending.size === 1) {
                                var current = stack.indexOf(pending.values().next().value);
                                if (current >= 0) index = (current + 1) % stack.length;
                            }
                            pending.clear();
                            if (stack.length > 0) pending.add(stack[index]);
                            lastClick = { x: x, y: y };
                            pickInfo.textContent = stack.length === 0 ? '' :
                                'Billboard ' + (index + 1) + ' of ' + stack.length + ' here' +
                                (stack.length > 1 ? ' - click again for the one behind' : '');
                            rebuildPending();
                            postSelection();
                        }

                        canvas.addEventListener('pointerdown', function (event) {
                            downAt = event.button === 0 ? { x: event.clientX, y: event.clientY } : null;
                        });
                        canvas.addEventListener('pointerup', function (event) {
                            var start = downAt;
                            downAt = null;
                            if (tagTool !== 'none' || !paintMode || !start || event.button !== 0 || !assignment || mode === 'original') return;
                            // A drag is an orbit, not a click.
                            if (Math.abs(event.clientX - start.x) + Math.abs(event.clientY - start.y) > 4) return;
                            selectAt(event.clientX, event.clientY);
                        });
                        window.addEventListener('keydown', function (event) {
                            if (!paintMode || pending.size === 0) return;
                            if (event.key === 'Delete' && window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({ action: 'deleteSelected' }));
                            } else if (event.key === 'Escape') {
                                clearPending();
                                postSelection();
                            }
                        });

                        // --- tagging parts: a box searched for a group, or a brush ----------------
                        // With a tool on, the left button draws or paints and the right one orbits.
                        // Boxes are aligned with the building's own axes (frameX, up, frameZ).
                        var tagTool = 'none';
                        var frameX = new THREE.Vector3(1, 0, 0), frameZ = new THREE.Vector3(0, 0, 1), modelExtent = 1;
                        var tagBox = null, tagBoxCenter = new THREE.Vector3(), tagBoxSize = new THREE.Vector3();
                        var boxDrag = null, rectStart = null, brushDown = false, brushErase = false, brushSet = null;
                        // The painting so far: strokes add to it (left button) or take from it
                        // (right button or Ctrl), until it's accepted or cancelled.
                        var paintSel = new Set();
                        var tagPink = null, tagRed = null, brushMesh = null;
                        var rectDiv = document.createElement('div');
                        rectDiv.style.cssText = 'position:fixed;border:1px dashed #ff3030;background:rgba(255,48,48,0.08);pointer-events:none;display:none;';
                        document.body.appendChild(rectDiv);
                        var tagPinkMaterial = new THREE.MeshBasicMaterial({ color: 0xff10f0, transparent: true, opacity: 0.75, side: THREE.DoubleSide, depthTest: false, depthWrite: false });
                        var tagRedMaterial = new THREE.MeshBasicMaterial({ color: 0xff3030, transparent: true, opacity: 0.55, side: THREE.DoubleSide, depthTest: false, depthWrite: false });
                        var boxFaceMaterial = new THREE.MeshBasicMaterial({ color: 0xff3030, transparent: true, opacity: 0.06, side: THREE.DoubleSide, depthWrite: false });
                        var boxEdgeMaterial = new THREE.LineBasicMaterial({ color: 0xff3030 });

                        function post(msg) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(msg)); }

                        window.setFrame = function (xx, xy, xz, zx, zy, zz, ext) {
                            frameX.set(xx, xy, xz).normalize(); frameZ.set(zx, zy, zz).normalize(); modelExtent = ext;
                        };
                        // Drawing a box takes the left button (the right one orbits). Painting and
                        // erasing only take a press that lands on the model, as in the other
                        // editors; one beside it orbits as usual.
                        window.setTagTool = function (t) {
                            tagTool = t;
                            controls.mouseButtons.LEFT = t === 'box' ? -1 : THREE.MOUSE.ROTATE;
                            controls.mouseButtons.RIGHT = t === 'box' ? THREE.MOUSE.ROTATE : THREE.MOUSE.PAN;
                            canvas.style.cursor = t === 'none' ? (paintMode ? 'crosshair' : '') : 'crosshair';
                            window.clearTagPaint();
                        };
                        window.clearTagPaint = function () {
                            paintSel.clear();
                            disposeObject(brushMesh); brushMesh = null;
                        };

                        function trianglesMesh(list, material) {
                            if (!list || list.length === 0 || !srcPositions) return null;
                            var pos = new Float32Array(list.length * 9);
                            for (var i = 0; i < list.length; i++) pos.set(srcPositions.subarray(list[i] * 9, list[i] * 9 + 9), i * 9);
                            var geo = new THREE.BufferGeometry();
                            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
                            var m = new THREE.Mesh(geo, material);
                            m.renderOrder = 12;
                            scene.add(m);
                            return m;
                        }
                        window.setTagPending = function (pink, red) {
                            disposeObject(tagPink); disposeObject(tagRed);
                            tagRed = trianglesMesh(red, tagRedMaterial);
                            tagPink = trianglesMesh(pink, tagPinkMaterial);
                            if (tagPink) tagPink.renderOrder = 13;
                        };

                        // The box: a unit cube placed by a matrix with the frame axes as columns.
                        function placeBox() {
                            if (!tagBox) {
                                tagBox = new THREE.Group();
                                var faces = new THREE.Mesh(new THREE.BoxGeometry(1, 1, 1), boxFaceMaterial);
                                faces.name = 'faces';
                                var edges = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1)), boxEdgeMaterial);
                                tagBox.add(faces); tagBox.add(edges);
                                tagBox.matrixAutoUpdate = false;
                                scene.add(tagBox);
                            }
                            var m = new THREE.Matrix4().makeBasis(frameX, new THREE.Vector3(0, 1, 0), frameZ);
                            m.multiply(new THREE.Matrix4().makeScale(tagBoxSize.x, tagBoxSize.y, tagBoxSize.z));
                            m.setPosition(tagBoxCenter);
                            tagBox.matrix.copy(m);
                            tagBox.updateMatrixWorld(true);
                        }
                        window.clearTagBox = function () {
                            if (tagBox) { scene.remove(tagBox); tagBox.children.forEach(function (c) { c.geometry.dispose(); }); tagBox = null; }
                        };
                        function postBox() {
                            post({ action: 'tagBox', center: tagBoxCenter.toArray(), size: tagBoxSize.toArray() });
                        }

                        function ndcAt(x, y) {
                            var rect = canvas.getBoundingClientRect();
                            return new THREE.Vector2((x - rect.left) / rect.width * 2 - 1, -((y - rect.top) / rect.height) * 2 + 1);
                        }
                        function sourceHit(x, y) {
                            if (!srcMesh) return null;
                            raycaster.setFromCamera(ndcAt(x, y), camera);
                            var hits = raycaster.intersectObject(srcMesh, false);
                            return hits.length > 0 ? hits[0] : null;
                        }

                        // A box round what the dragged rectangle shows: the surface under a grid of
                        // rays through it, padded so a part standing out from the wall fits.
                        function boxFromRect(a, b) {
                            var lo = new THREE.Vector3(Infinity, Infinity, Infinity), hi = new THREE.Vector3(-Infinity, -Infinity, -Infinity), any = false;
                            for (var i = 0; i <= 14; i++)
                                for (var j = 0; j <= 14; j++) {
                                    var h = sourceHit(a.x + (b.x - a.x) * i / 14, a.y + (b.y - a.y) * j / 14);
                                    if (!h) continue;
                                    var q = new THREE.Vector3(h.point.dot(frameX), h.point.y, h.point.dot(frameZ));
                                    lo.min(q); hi.max(q); any = true;
                                }
                            if (!any) return false;
                            var pad = modelExtent * 0.02;
                            lo.subScalar(pad); hi.addScalar(pad);
                            var mid = lo.clone().add(hi).multiplyScalar(0.5);
                            tagBoxSize.copy(hi).sub(lo);
                            tagBoxCenter.copy(frameX).multiplyScalar(mid.x).add(new THREE.Vector3(0, mid.y, 0)).add(frameZ.clone().multiplyScalar(mid.z));
                            return true;
                        }

                        // Which face of the box is under the pointer: axis 0/1/2 and side +1/-1.
                        function boxFaceAt(x, y) {
                            if (!tagBox) return null;
                            raycaster.setFromCamera(ndcAt(x, y), camera);
                            var hits = raycaster.intersectObject(tagBox.getObjectByName('faces'), false);
                            if (hits.length === 0) return null;
                            var n = hits[0].face.normal;
                            var axis = Math.abs(n.x) > 0.5 ? 0 : Math.abs(n.y) > 0.5 ? 1 : 2;
                            return { axis: axis, side: (axis === 0 ? n.x : axis === 1 ? n.y : n.z) > 0 ? 1 : -1 };
                        }
                        function axisDir(axis) { return axis === 0 ? frameX.clone() : axis === 1 ? new THREE.Vector3(0, 1, 0) : frameZ.clone(); }
                        function toScreen(p) {
                            var v = p.clone().project(camera), rect = canvas.getBoundingClientRect();
                            return new THREE.Vector2((v.x + 1) / 2 * rect.width, (1 - v.y) / 2 * rect.height);
                        }

                        var eraseMaterial = new THREE.MeshBasicMaterial({ color: 0x9a9a9a, transparent: true, opacity: 0.7, depthTest: false, side: THREE.DoubleSide });
                        function brushAt(x, y) {
                            var r = 7;
                            [[0, 0], [r, 0], [-r, 0], [0, r], [0, -r], [r * 0.7, r * 0.7], [-r * 0.7, r * 0.7], [r * 0.7, -r * 0.7], [-r * 0.7, -r * 0.7]].forEach(function (o) {
                                var h = sourceHit(x + o[0], y + o[1]);
                                if (!h) return;
                                if (tagTool === 'erase') brushSet.add(h.faceIndex);
                                else if (brushErase) paintSel.delete(h.faceIndex);
                                else paintSel.add(h.faceIndex);
                            });
                            disposeObject(brushMesh);
                            brushMesh = tagTool === 'erase' ? trianglesMesh(Array.from(brushSet), eraseMaterial) : trianglesMesh(Array.from(paintSel), tagRedMaterial);
                        }

                        // Paint and Erase: a press on the model starts a stroke, ahead of the orbit
                        // controls (capturing, on the window), so they never see it.
                        window.addEventListener('pointerdown', function (event) {
                            if ((tagTool !== 'paint' && tagTool !== 'erase') || event.target !== canvas || event.button === 1) return;
                            if (!sourceHit(event.clientX, event.clientY)) return;
                            event.stopPropagation();
                            event.preventDefault();
                            brushDown = true;
                            brushErase = tagTool === 'paint' && (event.button === 2 || event.ctrlKey);
                            brushSet = new Set();
                            // While painting, the selection so far shows on its own, red.
                            window.setTagPending([], []);
                            brushAt(event.clientX, event.clientY);
                        }, true);
                        window.addEventListener('pointermove', function (event) {
                            if (brushDown) brushAt(event.clientX, event.clientY);
                        });
                        window.addEventListener('pointerup', function () {
                            if (!brushDown) return;
                            brushDown = false;
                            if (tagTool === 'erase') {
                                disposeObject(brushMesh); brushMesh = null;
                                post({ action: 'tagErase', indices: Array.from(brushSet) });
                            } else {
                                post({ action: 'tagPaint', indices: Array.from(paintSel) });
                            }
                        });
                        canvas.addEventListener('contextmenu', function (event) {
                            if (tagTool !== 'none') event.preventDefault();
                        });

                        canvas.addEventListener('pointerdown', function (event) {
                            if (tagTool !== 'box' || event.button !== 0) return;
                            canvas.setPointerCapture(event.pointerId);
                            {
                                var face = boxFaceAt(event.clientX, event.clientY);
                                if (face) {
                                    boxDrag = { face: face, x: event.clientX, y: event.clientY, center: tagBoxCenter.clone(), size: tagBoxSize.clone() };
                                } else {
                                    rectStart = { x: event.clientX, y: event.clientY };
                                }
                            }
                        });
                        canvas.addEventListener('pointermove', function (event) {
                            if (tagTool === 'none') return;
                            if (rectStart) {
                                var l = Math.min(rectStart.x, event.clientX), t = Math.min(rectStart.y, event.clientY);
                                rectDiv.style.left = l + 'px'; rectDiv.style.top = t + 'px';
                                rectDiv.style.width = Math.abs(event.clientX - rectStart.x) + 'px';
                                rectDiv.style.height = Math.abs(event.clientY - rectStart.y) + 'px';
                                rectDiv.style.display = 'block';
                            } else if (boxDrag) {
                                // Move the face along its axis by the drag's share of the axis on screen.
                                var dir = axisDir(boxDrag.face.axis);
                                var s0 = toScreen(boxDrag.center), s1 = toScreen(boxDrag.center.clone().add(dir));
                                var screenDir = s1.sub(s0);
                                var len2 = screenDir.lengthSq();
                                if (len2 < 1e-6) return;
                                var delta = ((event.clientX - boxDrag.x) * screenDir.x + (event.clientY - boxDrag.y) * screenDir.y) / len2;
                                var size = boxDrag.size.clone(), center = boxDrag.center.clone();
                                var comp = ['x', 'y', 'z'][boxDrag.face.axis];
                                var grown = Math.max(modelExtent * 0.002, size[comp] + boxDrag.face.side * delta);
                                var moved = (grown - size[comp]) * boxDrag.face.side;
                                size[comp] = grown;
                                center.add(dir.multiplyScalar(moved / 2));
                                tagBoxSize.copy(size); tagBoxCenter.copy(center);
                                placeBox();
                            }
                        });
                        canvas.addEventListener('pointerup', function (event) {
                            if (tagTool !== 'box' || event.button !== 0) return;
                            if (rectStart) {
                                var a = rectStart, b = { x: event.clientX, y: event.clientY };
                                rectStart = null;
                                rectDiv.style.display = 'none';
                                if (Math.abs(a.x - b.x) < 4 || Math.abs(a.y - b.y) < 4) return;
                                if (!boxFromRect(a, b)) { pickInfo.textContent = 'Nothing of the model under that box'; return; }
                                pickInfo.textContent = 'Drag a face of the box to resize it';
                                placeBox();
                                postBox();
                            } else if (boxDrag) {
                                boxDrag = null;
                                postBox();
                            }
                        });
                        window.addEventListener('keydown', function (event) {
                            if (tagTool !== 'none' && event.key === 'Escape') {
                                window.clearTagBox();
                                window.clearTagPaint();
                                window.setTagPending([], []);
                                post({ action: 'tagCancel' });
                            }
                        });

                        // --- startup ------------------------------------------------------------
                        fetch('https://appassets.local/" + sourceFile + @"')
                            .then(function (r) { return r.arrayBuffer(); })
                            .then(function (buf) {
                                srcPositions = new Float32Array(buf);
                                triCount = srcPositions.length / 9;
                                buildSource();

                                srcMesh.geometry.computeBoundingBox();
                                var box = srcMesh.geometry.boundingBox;
                                var size = box.getSize(new THREE.Vector3());
                                var maxDim = Math.max(size.x, size.y, size.z) || 1;
                                var center = box.getCenter(new THREE.Vector3());
                                var dist = maxDim * 1.8;
                                controls.target.copy(center);
                                camera.position.copy(center).add(new THREE.Vector3(dist * 0.6, dist * 0.35, dist));
                                camera.near = maxDim / 1000;
                                camera.far = maxDim * 100;
                                camera.updateProjectionMatrix();
                                controls.update();
                                applyVisibility();

                                if (window.chrome && window.chrome.webview) {
                                    window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                                }
                            })
                            .catch(function (err) { showError('Failed to load source triangles: ' + err); });

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
                    } catch (err) {
                        showError('Setup error: ' + err.message);
                    }
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(htmlContent);
        }
    }
}
