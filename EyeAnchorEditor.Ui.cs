using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace GlbMerger
{
    // EyeAnchorEditor's left-hand controls (see EyeAnchorEditor.cs).
    public partial class EyeAnchorEditor
    {
        private static FlowLayoutPanel MakeRow() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 6),
        };

        private static Label Caption(string text, int bottom = 4) =>
            new Label { Text = text, AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0), Margin = new Padding(3, 0, 3, bottom) };

        private NumericUpDown AddNumber(FlowLayoutPanel flow, string label, decimal min, decimal max,
                                        int decimals, decimal step, string help)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 110, Margin = new Padding(3, 6, 3, 3) });
            var n = new NumericUpDown
            {
                Width = 110, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step,
                Margin = new Padding(3, 3, 3, 3),
            };
            n.ValueChanged += (s, e) => OnNumbersChanged();
            row.Controls.Add(n);
            flow.Controls.Add(row);
            _help.Add(row, help);
            return n;
        }

        private TrackBar AddGazeSlider(FlowLayoutPanel flow, int range)
        {
            var t = new TrackBar
            {
                Minimum = -range, Maximum = range, Value = 0, TickFrequency = 15, Width = 290,
                Margin = new Padding(3, 0, 3, 4),
            };
            t.ValueChanged += (s, e) => PushEyes();
            flow.Controls.Add(t);
            return t;
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

            BuildPlacementSection(flow);
            BuildPreviewSection(flow);

            controlPanel.Controls.Add(flow);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private void BuildPlacementSection(FlowLayoutPanel flow)
        {
            var btnSave = new Button { Text = "Save Eye Anchors", AutoSize = true, Margin = new Padding(3, 0, 3, 10) };
            btnSave.Click += (s, e) => SaveAnchors();
            flow.Controls.Add(btnSave);

            flow.Controls.Add(Caption("Head bone: " + (_head?.Name ?? "NOT FOUND"), 10));

            flow.Controls.Add(Caption("Editing (the character's own left / right):"));
            var sideRow = MakeRow();
            _radioLeft = new RadioButton { Text = "Left Eye", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioRight = new RadioButton { Text = "Right Eye", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            _radioLeft.CheckedChanged += (s, e) => { if (_radioLeft.Checked) { RefreshUiFromState(); PushEyes(); } };
            _radioRight.CheckedChanged += (s, e) => { if (_radioRight.Checked) { RefreshUiFromState(); PushEyes(); } };
            sideRow.Controls.Add(_radioLeft);
            sideRow.Controls.Add(_radioRight);
            flow.Controls.Add(sideRow);

            flow.Controls.Add(Caption("Click the eye in the preview to centre it there. Placing one " +
                                      "eye moves on to the other if it still needs placing. Shift+click " +
                                      "goes through glasses to the eye behind them.", 10));

            const decimal far = 100000m;
            _numX = AddNumber(flow, "Centre X", -far, far, 4, 0.001m, "Eye centre, mesh units.");
            _numY = AddNumber(flow, "Centre Y", -far, far, 4, 0.001m, "Eye centre, mesh units.");
            _numZ = AddNumber(flow, "Centre Z", -far, far, 4, 0.001m, "Eye centre, mesh units.");
            _numW = AddNumber(flow, "Width %", 0.05m, 20m, 2, 0.05m,
                "Half-width of the eye ellipse, as a percent of the model's height.");
            _numH = AddNumber(flow, "Height %", 0.05m, 20m, 2, 0.05m,
                "Half-height of the eye ellipse, as a percent of the model's height. The pupil's size is a fraction of this.");
            _numD = AddNumber(flow, "Back depth %", 0.05m, 20m, 2, 0.05m,
                "How far behind the eye's centre (into the head) it is projected. Too shallow loses the " +
                "eye's curved edge; too deep can catch the eyelid behind it.");
            _numF = AddNumber(flow, "Front depth %", 0m, 20m, 2, 0.05m,
                "How far in front of the eye's centre it is projected - at most Back depth. Turn it down " +
                "until glasses in front of the eye stop being painted over.");

            flow.Controls.Add(Caption("Eye style (both eyes, saved with the anchors):", 4));

            var fillRow = MakeRow();
            fillRow.Controls.Add(new Label { Text = "Eyeball fill %", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _numFill = new NumericUpDown
            {
                Width = 110, Minimum = 0m, Maximum = 100m, DecimalPlaces = 0, Increment = 5m,
                Margin = new Padding(3, 3, 3, 3),
            };
            _numFill.ValueChanged += (s, e) => OnStyleChanged();
            fillRow.Controls.Add(_numFill);
            flow.Controls.Add(fillRow);
            _help.Add(fillRow,
                "How much of each ellipse is eyeball (both eyes). 0: only the painted eye inside it is " +
                "repainted - the paint sets the eyeball's edge and Width/Height only limit where it looks. " +
                "100: the whole ellipse is eyeball with a dark rim, so Width/Height ARE the eyeball's edge.");

            BuildIrisRows(flow);
            _numPupil = AddStyleNumber(flow, "Pupil radius",
                "Pupil size in eye radii (1 = the eye's full height). Smaller shows more iris.");
            _numTravel = AddStyleNumber(flow, "Pupil travel",
                "How far the pupil moves at a full sideways / vertical look, in eye radii.");
            _numRimWidth = AddStyleNumber(flow, "Rim width",
                "The dark outline round a filled eyeball (Eyeball fill above 0), in eye radii.");
            _numRimStrength = AddStyleNumber(flow, "Rim strength", "How dark that outline is.");
            BuildLidRows(flow);

            _chkSameSize = new CheckBox { Text = "Same size for both eyes", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 3, 6) };
            flow.Controls.Add(_chkSameSize);

            var btnClear = new Button { Text = "Clear This Eye", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
            btnClear.Click += (s, e) => ClearSelectedEye();
            flow.Controls.Add(btnClear);

            _lblStatus = Caption("", 14);
            flow.Controls.Add(_lblStatus);
        }

        // The iris round the pupil - both eyes, saved with the anchors.
        private void BuildIrisRows(FlowLayoutPanel flow)
        {
            var radiusRow = MakeRow();
            radiusRow.Controls.Add(new Label { Text = "Iris radius", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _numIrisRadius = new NumericUpDown
            {
                Width = 110, Minimum = 0m, Maximum = 1m, DecimalPlaces = 2, Increment = 0.05m,
                Margin = new Padding(3, 3, 3, 3),
            };
            _numIrisRadius.ValueChanged += (s, e) => OnStyleChanged();
            radiusRow.Controls.Add(_numIrisRadius);
            flow.Controls.Add(radiusRow);
            _help.Add(radiusRow,
                "Size of the coloured iris round the pupil, in eye radii (1 = the eye's full height). " +
                "Must be bigger than the game's pupil radius (Eyes panel, 0.45 by default) to show.");

            var colorRow = MakeRow();
            colorRow.Controls.Add(new Label { Text = "Iris colour", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _btnIrisColor = new Button { Text = "Pick...", Width = 110, Margin = new Padding(3, 3, 3, 3) };
            _btnIrisColor.Click += (s, e) => PickIrisColor();
            colorRow.Controls.Add(_btnIrisColor);
            flow.Controls.Add(colorRow);
            _help.Add(colorRow, "The iris colour, as it should look on screen. Its edge is drawn a little darker.");
        }

        // Lids: the skin they're painted in, how shut they sit, the lash line on their edge.
        private void BuildLidRows(FlowLayoutPanel flow)
        {
            var colorRow = MakeRow();
            colorRow.Controls.Add(new Label { Text = "Lid colour", Width = 110, Margin = new Padding(3, 6, 3, 3) });
            _btnLidColor = new Button { Text = "Pick...", Width = 110, Margin = new Padding(3, 3, 3, 3) };
            _btnLidColor.Click += (s, e) => PickLidColor();
            var btnSample = new Button { Text = "From Model", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
            btnSample.Click += (s, e) => StartLidEyedropper();
            colorRow.Controls.Add(_btnLidColor);
            colorRow.Controls.Add(btnSample);
            flow.Controls.Add(colorRow);
            _help.Add(colorRow,
                "The skin the eyelids are painted in. \"From Model\" takes it from the next spot you click " +
                "in the preview - click the skin just above an eye. A model has no lids in game until this is set.");

            _numLidRest = AddStyleNumber(flow, "Upper lid rest",
                "How far the upper lid sits closed at rest: 0 = wide open, 1 = shut. Blinks, squints " +
                "and looking down add to it in game.");
            _numLowerRest = AddStyleNumber(flow, "Lower lid rest",
                "How far the lower lid sits raised at rest: 0 = at the bottom of the eye, 1 = shut. " +
                "Blinks and squints add to it in game.");
            _numLashWidth = AddStyleNumber(flow, "Lash width",
                "The dark lash line along each lid's edge, in eye radii. The lower one is half as wide.");
            _numLashStrength = AddStyleNumber(flow, "Lash strength", "How dark the lash line is.");
            _numCleanup = AddStyleNumber(flow, "Cleanup margin",
                "A band this wide (eye radii) just outside each eye where anything still looking like the " +
                "painted eye - white, or very dark - is covered in the lid colour. Raise it until no stray " +
                "white outline is left round an eye the anchor doesn't quite fit. Shown light magenta with " +
                "Show anchor region on. Needs a lid colour.");
            _numLimitAbove = AddLimitNumber(flow, "Limit above",
                "How far above where the upper lid meets the eye at rest anything is changed - eye, lids " +
                "or cleanup - in eye radii. The cut-off follows the lid's curve, widening by the same " +
                "amount at the corners. 3 = no limit. For marks painted above an eye.");
            _numLimitBelow = AddLimitNumber(flow, "Limit below",
                "The same, below where the lower lid meets the eye at rest. 3 = no limit. For marks " +
                "painted under an eye, like Billy's.");
        }

        private void BuildPreviewSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Caption("Preview only (not saved):", 8));

            _chkShowRegion = new CheckBox { Text = "Show anchor region", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 6) };
            _chkShowRegion.CheckedChanged += (s, e) => PushEyes();
            flow.Controls.Add(_chkShowRegion);

            _lblGaze = Caption("Test gaze:");
            flow.Controls.Add(_lblGaze);
            _sliderYaw = AddGazeSlider(flow, 60);
            _sliderPitch = AddGazeSlider(flow, 45);

            var btnCentre = new Button { Text = "Centre Gaze", AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
            btnCentre.Click += (s, e) => { _sliderYaw.Value = 0; _sliderPitch.Value = 0; };
            flow.Controls.Add(btnCentre);

            _chkAutoBlink = new CheckBox { Text = "Blink", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 4) };
            _chkAutoBlink.CheckedChanged += (s, e) => PushEyes();
            flow.Controls.Add(_chkAutoBlink);

            _lblClosure = Caption("Test lid closure: rest");
            flow.Controls.Add(_lblClosure);
            _sliderClosure = new TrackBar { Minimum = 0, Maximum = 100, Value = 0, TickFrequency = 10, Width = 290, Margin = new Padding(3, 0, 3, 8) };
            _sliderClosure.ValueChanged += (s, e) =>
            {
                _lblClosure.Text = _sliderClosure.Value == 0 ? "Test lid closure: rest" : $"Test lid closure: {_sliderClosure.Value}%";
                PushEyes();
            };
            flow.Controls.Add(_sliderClosure);

            var btnFrame = new Button { Text = "Frame Head", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            btnFrame.Click += (s, e) => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync("frameHead();"); };
            flow.Controls.Add(btnFrame);
        }

        private NumericUpDown AddLimitNumber(FlowLayoutPanel flow, string label, string help)
        {
            var n = AddStyleNumber(flow, label, help);
            n.Maximum = 3m;
            return n;
        }

        private NumericUpDown AddStyleNumber(FlowLayoutPanel flow, string label, string help)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 110, Margin = new Padding(3, 6, 3, 3) });
            var n = new NumericUpDown
            {
                Width = 110, Minimum = 0m, Maximum = 1m, DecimalPlaces = 2, Increment = 0.05m,
                Margin = new Padding(3, 3, 3, 3),
            };
            n.ValueChanged += (s, e) => OnStyleChanged();
            row.Controls.Add(n);
            flow.Controls.Add(row);
            _help.Add(row, help);
            return n;
        }
    }
}
