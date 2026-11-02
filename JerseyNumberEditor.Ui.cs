using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace GlbMerger
{
    // JerseyNumberEditor's left-hand controls (see JerseyNumberEditor.cs).
    public partial class JerseyNumberEditor
    {
        // Only if the game's own fonts can't be found (JerseyNumbers.FindGameFontFolder): ones every
        // Windows install has, heaviest first.
        private static readonly string[] FallbackFonts = { "Arial Black", "Impact", "Bahnschrift" };

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

        private static Label Heading(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font(Control.DefaultFont, FontStyle.Bold), Margin = new Padding(3, 10, 3, 6) };

        private static Button MakeButton(string text) => new Button
        {
            Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(300, 0), Margin = new Padding(3, 0, 3, 6),
        };

        private NumericUpDown AddNumber(FlowLayoutPanel flow, string label, decimal min, decimal max,
                                        int decimals, decimal step, System.Action changed, string help)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 120, Margin = new Padding(3, 6, 3, 3) });
            var n = new NumericUpDown
            {
                Width = 110, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step,
                Margin = new Padding(3, 3, 3, 3),
            };
            n.ValueChanged += (s, e) => changed();
            row.Controls.Add(n);
            flow.Controls.Add(row);
            _help.Add(row, help);
            return n;
        }

        private ComboBox AddDropdown(FlowLayoutPanel flow, string caption)
        {
            flow.Controls.Add(Caption(caption, 2));
            var combo = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 8) };
            flow.Controls.Add(combo);
            return combo;
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 360, AutoScroll = true };
            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };

            BuildAnchorSection(flow);
            BuildBakedSection(flow);
            BuildStyleSection(flow);
            BuildPreviewSection(flow);

            controlPanel.Controls.Add(flow);
            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private void BuildAnchorSection(FlowLayoutPanel flow)
        {
            var btnSave = MakeButton("Save Number Anchors");
            btnSave.Margin = new Padding(3, 0, 3, 10);
            btnSave.Click += (s, e) => SaveAnchors();
            flow.Controls.Add(btnSave);

            _spineDropdown = AddDropdown(flow, "Spine joint the numbers move with:");
            foreach (var j in _joints) _spineDropdown.Items.Add(j.Name ?? $"node {j.LogicalIndex}");
            _spineDropdown.SelectedIndexChanged += (s, e) => OnSpineChanged();
            _help.Add(_spineDropdown,
                "The joint both anchors hang from, and the one Rigidity moves the digits with. The middle " +
                "of the spine chain is picked by default (Spine1 on a Spine/Spine1/Spine2 rig).");

            var sideRow = MakeRow();
            _radioFront = new RadioButton { Text = "Front (chest)", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioBack = new RadioButton { Text = "Back", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            _radioFront.CheckedChanged += (s, e) => { if (_radioFront.Checked && !_suppressEvents) { RefreshUiFromState(); PushState(); } };
            _radioBack.CheckedChanged += (s, e) => { if (_radioBack.Checked && !_suppressEvents) { RefreshUiFromState(); PushState(); } };
            sideRow.Controls.Add(_radioFront);
            sideRow.Controls.Add(_radioBack);
            flow.Controls.Add(sideRow);

            flow.Controls.Add(Caption("Click a baked number in the preview to centre its anchor there - " +
                                      "a click on the back moves to the back anchor. Then size the box to " +
                                      "cover the old number.", 10));

            const decimal far = 100000m;
            _numX = AddNumber(flow, "Centre X", -far, far, 4, 0.001m, OnAnchorNumbersChanged, "Number centre, mesh units.");
            _numY = AddNumber(flow, "Centre Y", -far, far, 4, 0.001m, OnAnchorNumbersChanged, "Number centre, mesh units.");
            _numZ = AddNumber(flow, "Centre Z", -far, far, 4, 0.001m, OnAnchorNumbersChanged, "Number centre, mesh units.");
            _numH = AddNumber(flow, "Height %", 0.5m, 40m, 2, 0.1m, OnAnchorNumbersChanged,
                "The digit cell's height, as a percent of the model's height. The digits themselves are " +
                "three quarters of it; Erase clears the whole box.");
            _numW = AddNumber(flow, "Width %", 0.5m, 60m, 2, 0.1m, OnAnchorNumbersChanged,
                "How wide the box is, as a percent of the model's height. Digits past its sides are cut " +
                "off, so leave room for two; Erase clears the whole box.");
            _numIn = AddNumber(flow, "Depth in %", 0.1m, 20m, 2, 0.1m, OnAnchorNumbersChanged,
                "How far into the body from the centre the box reaches. Enough to follow the torso's " +
                "curve; too much can catch the arms' inner side.");
            _numOut = AddNumber(flow, "Depth out %", 0m, 20m, 2, 0.1m, OnAnchorNumbersChanged,
                "How far out of the body from the centre the box reaches - at most Depth in.");

            _chkSameSize = new CheckBox { Text = "Same box size front and back", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 6) };
            _chkSameSize.CheckedChanged += (s, e) => OnSameSizeChanged();
            flow.Controls.Add(_chkSameSize);
            _help.Add(_chkSameSize,
                "Keeps the front and back boxes the same size: changing Height, Width or either depth on one " +
                "changes the other too, and the second number placed starts at the first one's size. Turning " +
                "it on copies the side being edited to the other. Each box keeps its own centre.");

            var btnClear = MakeButton("Clear This Anchor");
            btnClear.Click += (s, e) => ClearSelectedAnchor();
            flow.Controls.Add(btnClear);

            _lblStatus = Caption("", 6);
            _lblStatus.ForeColor = Color.LightGreen;
            flow.Controls.Add(_lblStatus);
        }

        private void BuildBakedSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Heading("Retire the baked numbers"));

            var btnErase = MakeButton("Erase Baked Numbers in Boxes");
            btnErase.Click += (s, e) => EraseBakedNumbers();
            flow.Controls.Add(btnErase);
            _help.Add(btnErase,
                "Paints every texel inside the front and back boxes, on faces pointing out of them, in the " +
                "jersey green round the number - a colour the game's team recolour fully replaces, so the " +
                "patch matches the rest of the shirt in game.");

            _numReach = AddNumber(flow, "Smooth reach %", 100m, 400m, 0, 10m, () => { },
                "How far past the number boxes (across and up) Smooth Rigid Skin looks, as a percent of " +
                "their size. Raise it until the whole old rigid patch is covered - Show rigid skin marks it red.");
            _numReach.Value = 150m;

            var btnSmooth = MakeButton("Smooth Rigid Skin in Boxes");
            btnSmooth.Click += (s, e) => SmoothRigidSkin();
            flow.Controls.Add(btnSmooth);
            _help.Add(btnSmooth,
                "Rebuilds the weights of skin bound to a single joint inside the (grown) boxes from the skin " +
                "round it, the way Rigid Region's \"Remove Saved Rigid Region\" does - so the torso bends " +
                "smoothly again. The shader's Rigidity keeps the new numbers from shearing.");

            _btnRevert = MakeButton("Revert Erase && Smooth");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertEdits();
            flow.Controls.Add(_btnRevert);
        }

        private void BuildStyleSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Heading("Number style (saved with the anchors)"));

            _numNumber = AddNumber(flow, "Number", 0m, 99m, 0, 1m, OnStyleChanged,
                "This model's own number - the one that was baked in. The game may deal it another.");
            _numRigidity = AddNumber(flow, "Rigidity %", 0m, 100m, 0, 5m, OnStyleChanged,
                "100: the digits move rigidly with the spine joint and can't shear, though the cloth slides " +
                "under them a little where it moves away from the spine. 0: glued to the cloth, shearing " +
                "as the baked numbers did. Play an animation to judge it.");
            _numDigitWidth = AddNumber(flow, "Digit width", 0.3m, 2m, 2, 0.05m, OnStyleChanged,
                "Stretches each digit across: 1 = the font's own shape.");
            _numDigitGap = AddNumber(flow, "Digit gap", -0.5m, 0.5m, 2, 0.01m, OnStyleChanged,
                "Space between two digits, in digit-cell heights. Negative closes up the cells' own margins.");
            _numOutlineWidth = AddNumber(flow, "Outline width", 0m, 0.12m, 3, 0.005m, OnStyleChanged,
                "The outline round each digit, in digit-cell heights. 0 = none.");

            var colorRow = MakeRow();
            colorRow.Controls.Add(new Label { Text = "Fill / outline", Width = 120, Margin = new Padding(3, 6, 3, 3) });
            _btnFill = new Button { Text = "Fill", Width = 70, Margin = new Padding(3, 3, 3, 3) };
            _btnFill.Click += (s, e) => PickColor(outline: false);
            _btnOutline = new Button { Text = "Outline", Width = 70, Margin = new Padding(3, 3, 3, 3) };
            _btnOutline.Click += (s, e) => PickColor(outline: true);
            colorRow.Controls.Add(_btnFill);
            colorRow.Controls.Add(_btnOutline);
            flow.Controls.Add(colorRow);
            _help.Add(colorRow, "The digits' colour and their outline's, as they should look on screen.");
        }

        private void BuildPreviewSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Heading("Preview only (not saved)"));

            _chkShowRegion = new CheckBox { Text = "Show number boxes", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 4) };
            _chkShowRegion.CheckedChanged += (s, e) => PushState();
            flow.Controls.Add(_chkShowRegion);

            _chkShowRigid = new CheckBox { Text = "Show rigid skin (single joint)", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
            _chkShowRigid.CheckedChanged += (s, e) => PushState();
            flow.Controls.Add(_chkShowRigid);
            _help.Add(_chkShowRigid, "Tints red the skin bound entirely to one joint - what a rigid region leaves.");

            _fontDropdown = AddDropdown(flow, _fontFolder != null
                ? "Digit font (the game's, from Graphics/UI):"
                : "Digit font (game fonts NOT FOUND - Windows stand-ins):");
            foreach (var f in _fontFolder != null ? JerseyNumbers.GameFonts(_fontFolder) : FallbackFonts.ToList())
                _fontDropdown.Items.Add(f);
            _fontDropdown.SelectedIndex = 0;
            _fontDropdown.SelectedIndexChanged += (s, e) => PushState();
            _help.Add(_fontDropdown, _fontFolder != null
                ? "The game's own fonts, from " + _fontFolder + ". The atlas is built from the one picked."
                : "The game's Graphics/UI fonts weren't found next to the animation library folder, so these " +
                  "Windows fonts stand in. Don't save an atlas for the game from them.");

            var btnAtlas = MakeButton("Save Digit Atlas...");
            btnAtlas.Click += (s, e) => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync("exportAtlas();"); };
            flow.Controls.Add(btnAtlas);
            _help.Add(btnAtlas,
                "Writes the digits 0-9 in the font above as the signed-distance atlas the game samples " +
                "(FootballRoguelite Graphics/JerseyDigits.png).");

            _animDropdown = AddDropdown(flow, "Animation:");
            _animDropdown.Items.Add("(Bind pose)");
            foreach (var anim in _model.LogicalAnimations) _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");
            _animDropdown.SelectedIndex = _model.LogicalAnimations.Count > 0 ? 1 : 0;
            _animDropdown.SelectedIndexChanged += (s, e) => PushState();

            _chkPlaying = new CheckBox { Text = "Playing", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 4) };
            _chkPlaying.CheckedChanged += (s, e) => { _sliderScrub.Enabled = !_chkPlaying.Checked; PushState(); };
            flow.Controls.Add(_chkPlaying);

            _sliderScrub = new TrackBar { Width = 300, Minimum = 0, Maximum = 1000, TickFrequency = 100, Enabled = false, Margin = new Padding(3, 0, 3, 6) };
            _sliderScrub.Scroll += (s, e) => PushState();
            flow.Controls.Add(_sliderScrub);

            var frameRow = MakeRow();
            var btnFront = new Button { Text = "Frame Front", AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
            btnFront.Click += (s, e) => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync("frameSide(false);"); };
            var btnBack = new Button { Text = "Frame Back", AutoSize = true, Margin = new Padding(3, 0, 3, 3) };
            btnBack.Click += (s, e) => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync("frameSide(true);"); };
            frameRow.Controls.Add(btnFront);
            frameRow.Controls.Add(btnBack);
            flow.Controls.Add(frameRow);
        }
    }
}
