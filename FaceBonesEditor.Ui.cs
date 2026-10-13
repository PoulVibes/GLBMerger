using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using static GlbMerger.FaceAnchors;

namespace GlbMerger
{
    // FaceBonesEditor's left-hand controls (see FaceBonesEditor.cs).
    public partial class FaceBonesEditor
    {
        // The game's FacePreset order (FaceExpression.h).
        private static readonly string[] Presets = { "Neutral", "Focus", "Effort", "Surprise", "Pain", "Joy", "Dejected" };
        // The game's Viseme order (Viseme.h), after "(expression)".
        private static readonly string[] Visemes =
        {
            "(expression)", "Rest", "M B P", "AA (ah)", "EH (e)", "EE (ee)", "OH (o)", "OO (oo, w)", "F V",
            "T L N TH", "S Z", "SH CH J", "K G", "R", "H (breath)",
        };

        private readonly List<RadioButton> _radioFeature = new();
        private NumericUpDown _numX = null!, _numY = null!, _numZ = null!;
        private NumericUpDown _numW = null!, _numH = null!, _numD = null!, _numF = null!, _numCore = null!, _numChinDrop = null!;
        private CheckBox _chkSameSize = null!, _chkShowRegion = null!, _chkShowWeights = null!;
        private ComboBox _comboExpression = null!, _comboViseme = null!;
        private TrackBar _sliderStrength = null!;
        private Label _lblStatus = null!, _lblBones = null!;

        private static FlowLayoutPanel MakeRow() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 6),
        };

        private static Label Caption(string text, int bottom = 4) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(3, 0, 3, bottom) };

        private NumericUpDown AddNumber(FlowLayoutPanel flow, string label, decimal min, decimal max, int decimals,
                                        decimal step, string help)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 110, Margin = new Padding(3, 6, 3, 3) });
            var n = new NumericUpDown
            {
                Width = 110, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step,
                Margin = new Padding(3, 3, 3, 3),
            };
            n.ValueChanged += (s, e) => { if (!_suppressEvents) OnRegionChanged(); };
            row.Controls.Add(n);
            flow.Controls.Add(row);
            _help.Add(row, help);
            return n;
        }

        private Button AddButton(Control parent, string text, Action click)
        {
            var b = new Button { Text = text, AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
            b.Click += (s, e) => click();
            parent.Controls.Add(b);
            return b;
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

            BuildRegionSection(flow);
            BuildMouthSection(flow);
            BuildPreviewSection(flow);

            controlPanel.Controls.Add(flow);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private void BuildRegionSection(FlowLayoutPanel flow)
        {
            var save = AddButton(flow, "Save Bones && Weights", Save);
            save.Margin = new Padding(3, 0, 3, 10);
            _help.Add(save, "Adds the brow, mouth and chin bones under the head and weights the face inside each region to " +
                            "them (replacing any earlier face weights), then reloads the preview with them.");
            flow.Controls.Add(Caption("Head bone: " + (_head?.Name ?? "NOT FOUND"), 10));

            var row = MakeRow();
            row.WrapContents = true;                      // four features don't fit one line of the panel
            row.MaximumSize = new Size(310, 0);
            for (int f = 0; f < FeatureLabels.Length; f++)
            {
                var radio = new RadioButton { Text = FeatureLabels[f], AutoSize = true, Checked = f == 0, Margin = new Padding(3, 4, 10, 3) };
                radio.CheckedChanged += (s, e) => { if (((RadioButton)s!).Checked) { RefreshUiFromState(); PushState(); } };
                _radioFeature.Add(radio);
                row.Controls.Add(radio);
            }
            flow.Controls.Add(row);

            flow.Controls.Add(Caption("Click the feature in the preview to centre its region there, then size it round " +
                                      "the painted brow / mouth. Everything inside moves with it; skin near the edge " +
                                      "stretches. The chin's runs from the lips (its top edge) to the bottom of the " +
                                      "chin: the top stays, the lower half drops as the mouth opens. Shift+click goes " +
                                      "through glasses. The mouth is best placed by its points instead:", 6));
            BuildMouthPointRows(flow);

            const decimal far = 100000m;
            _numX = AddNumber(flow, "Centre X", -far, far, 4, 0.001m, "Region centre, mesh units.");
            _numY = AddNumber(flow, "Centre Y", -far, far, 4, 0.001m, "Region centre, mesh units.");
            _numZ = AddNumber(flow, "Centre Z", -far, far, 4, 0.001m, "Region centre, mesh units.");
            _numW = AddNumber(flow, "Width %", 0.05m, 20m, 2, 0.05m,
                "Half-width of the region, percent of the model's height. A brow's bone sits at its outer end; " +
                "the mouth's corner bones near its ends.");
            _numH = AddNumber(flow, "Height %", 0.05m, 20m, 2, 0.05m,
                "Half-height of the region. Expressions move in units of this: a brow raise is about a third of it.");
            _numD = AddNumber(flow, "Back depth %", 0.05m, 20m, 2, 0.05m,
                "How far behind the region's centre vertices are weighted.");
            _numF = AddNumber(flow, "Front depth %", 0m, 20m, 2, 0.05m,
                "How far in front of the centre - at most Back depth. Lower it to leave glasses unweighted.");
            _numCore = AddNumber(flow, "Weight core %", 0m, 95m, 0, 5m,
                "Vertices inside this much of the region move fully with its bone; past it the weight fades to " +
                "none at the edge. Higher keeps the whole feature rigid; lower spreads the stretch wider. " +
                "The chin's applies across only: up and down it fades in from the top edge to the middle.");
            _numChinDrop = AddNumber(flow, "Chin drop %", 0m, 200m, 0, 5m,
                "How far the chin region's lower half moves down with the mouth fully open, % of its " +
                "half-height. Expressions open the mouth part way, so they drop it part way.");

            _chkSameSize = new CheckBox { Text = "Same size for both brows", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 6) };
            flow.Controls.Add(_chkSameSize);
            AddButton(flow, "Clear This Feature", ClearSelected);
        }

        private void BuildPreviewSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Caption("Preview (not saved):", 6));
            _lblBones = Caption("", 8);
            flow.Controls.Add(_lblBones);

            _chkShowRegion = new CheckBox { Text = "Show regions", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 4) };
            _chkShowRegion.CheckedChanged += (s, e) => PushState();
            flow.Controls.Add(_chkShowRegion);
            _chkShowWeights = new CheckBox { Text = "Show face weights (saved)", AutoSize = true, Checked = false, Margin = new Padding(3, 0, 3, 8) };
            _chkShowWeights.CheckedChanged += (s, e) => PushState();
            flow.Controls.Add(_chkShowWeights);

            var exprRow = MakeRow();
            exprRow.Controls.Add(new Label { Text = "Expression", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _comboExpression = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 3, 3) };
            _comboExpression.Items.AddRange(Presets);
            _comboExpression.SelectedIndex = 0;
            _comboExpression.SelectedIndexChanged += (s, e) => PushState();
            exprRow.Controls.Add(_comboExpression);
            flow.Controls.Add(exprRow);
            _help.Add(exprRow, "Plays a preset on the saved bones, as the game does - Save first to see a region's changes.");

            var visemeRow = MakeRow();
            visemeRow.Controls.Add(new Label { Text = "Mouth shape", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _comboViseme = new ComboBox { Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 3, 3) };
            _comboViseme.Items.AddRange(Visemes);
            _comboViseme.SelectedIndex = 0;
            _comboViseme.SelectedIndexChanged += (s, e) => PushState();
            visemeRow.Controls.Add(_comboViseme);
            flow.Controls.Add(visemeRow);
            _help.Add(visemeRow, "Holds one of the shapes talking is made of over the expression's mouth, as a player " +
                                 "saying it would (the game reads them off each line's spelling). Test open overrides it.");

            flow.Controls.Add(Caption("Expression strength:"));
            _sliderStrength = new TrackBar { Minimum = 0, Maximum = 200, Value = 100, TickFrequency = 25, Width = 290, Margin = new Padding(3, 0, 3, 8) };
            _sliderStrength.ValueChanged += (s, e) => PushState();
            flow.Controls.Add(_sliderStrength);

            AddButton(flow, "Frame Head", () => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync("frameHead();"); });
            _lblStatus = Caption("", 14);
            flow.Controls.Add(_lblStatus);
        }

        // ---- Edits from the panel ---------------------------------------------------------

        private void OnRegionChanged()
        {
            int f = Selected;
            _core[f] = (float)_numCore.Value / 100f;
            _chinDrop = (float)_numChinDrop.Value / 100f;
            if (!_regions[f].Set) { _dirty = true; return; }

            var radii = new Vector3((float)_numW.Value, (float)_numH.Value, (float)_numD.Value);
            _regions[f].Centre = new Vector3((float)_numX.Value, (float)_numY.Value, (float)_numZ.Value);
            _regions[f].RadiiPct = radii;
            _regions[f].FrontPct = Math.Min((float)_numF.Value, radii.Z);
            if (f == Mouth && HasMouthPoints)
            {
                FitLipLineToPoints();   // the lip line is in region units: its height just changed
                _suppressEvents = true;
                try { RefreshMouthUi(); } finally { _suppressEvents = false; }
            }
            if (f < Mouth && _chkSameSize.Checked && _regions[1 - f].Set)
            {
                _regions[1 - f].RadiiPct = radii;
                _regions[1 - f].FrontPct = _regions[f].FrontPct;
                _core[1 - f] = _core[f];
            }
            _dirty = true;
            PushState();
        }

        private void RefreshUiFromState()
        {
            _suppressEvents = true;
            try
            {
                var region = _regions[Selected];
                foreach (var n in new[] { _numX, _numY, _numZ, _numW, _numH, _numD, _numF }) n.Enabled = region.Set;
                if (region.Set)
                {
                    SetClamped(_numX, region.Centre.X); SetClamped(_numY, region.Centre.Y); SetClamped(_numZ, region.Centre.Z);
                    SetClamped(_numW, region.RadiiPct.X); SetClamped(_numH, region.RadiiPct.Y);
                    SetClamped(_numD, region.RadiiPct.Z); SetClamped(_numF, region.FrontPct);
                }
                SetClamped(_numCore, _core[Selected] * 100f);
                SetClamped(_numChinDrop, _chinDrop * 100f);
                RefreshMouthUi();
                RefreshMouthPointUi();
                _chkSameSize.Enabled = Selected < Mouth;

                string State(int f) => _regions[f].Set ? "placed" : "-";
                _lblStatus.Text = _head == null
                    ? "No \"head\" bone found - bones can't be added to this model."
                    : $"Left brow: {State(0)}   Right brow: {State(1)}   Mouth: {State(2)}   Chin: {State(3)}";
            }
            finally { _suppressEvents = false; }
        }
    }
}
