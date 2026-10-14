using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using static GlbMerger.FaceAnchors;

namespace GlbMerger
{
    // FaceBonesEditor's open-mouth stencil (FootballRoguelite MouthStencil.h, shaders/MouthOverlay.glsl):
    // the lips, opening, teeth and tongue the game draws over the painted mouth when an expression opens it,
    // and the skin mask that hides the painted closed mouth round it meanwhile. The lip line - its height at
    // the middle and the corners, and how wide it runs - is lined up with the painted one in the preview
    // (shown with the regions), then the opening hangs below it. Saved on the MouthAnchor's extras; a mouth
    // without "lipY" never opens.
    public partial class FaceBonesEditor
    {
        private sealed class MouthStyle
        {
            public bool Enabled;
            public float LipY = 0f, LipCornerY = 0.25f, LipWidthPct = 90f;   // region units; width as % of the region's
            public float OpenDepth = 1.2f, Edge = 0.03f, LipThickness = 0.3f, LipOpacity = 1f, LipArc = 1f;
            public bool AlwaysDrawn;   // the drawn mouth at rest too, never swapping back to the paint
            public float UpperTooth = 0.4f, LowerTooth = 0.28f;               // region units past the lip
            public float TeethWidthPct = 70f, ToothWidth = 1f, ToothGapPct = 0f, Bite = 0f;
            public float RestTeethUpper = 0f, RestTeethLower = 0f;   // how far each row shows at rest, 0..1
            public float MaskMargin = 0.3f;
            public float BaseOpen = 0f;      // how open the painted mouth is, 0..1; 0 = painted shut
            public float CreaseLength = 0f, CreaseWidth = 0.08f, CreaseCurve = 0.35f;   // length 0 = no creases
            public float CreaseAngle = 0f, CreaseOffset = 0.05f;                        // degrees; region units
            public Color Crease = Color.FromArgb(40, 20, 18);
            public Color Lips = Color.FromArgb(160, 72, 68);
            public Color Outline = Color.FromArgb(110, 50, 47);
            public Color? Skin;                                               // none: no mask
            public float? SkinRoughness, SkinMetalness;   // sampled with Skin: the preview's mouth shades as the face
            public Color Interior = Color.FromArgb(45, 8, 10);
            public Color Teeth = Color.FromArgb(245, 242, 230);
            public Color Tongue = Color.FromArgb(215, 100, 100);
        }
        private readonly MouthStyle _mouth = new();

        // Every number and colour control, with what it edits: the panel reads and writes through these.
        private readonly List<(NumericUpDown Control, Func<float> Get)> _mouthNumbers = new();
        private readonly List<(Button Swatch, Func<Color?> Get)> _mouthColors = new();
        private readonly Dictionary<string, Action<Color>> _mouthPickTargets = new();

        private CheckBox _chkMouth = null!, _chkAlwaysDrawn = null!, _chkTeethUpper = null!, _chkTeethLower = null!;
        private readonly List<NumericUpDown> _lipLineNumbers = new();   // set by the mouth points when there are some
        private TrackBar _sliderOpen = null!;

        // The region's half-width over its half-height: the lip's x reach in region units at 100%.
        private float MouthAspect => _regions[Mouth].RadiiPct.Y > 0 ? _regions[Mouth].RadiiPct.X / _regions[Mouth].RadiiPct.Y : 1f;

        // ---- Extras -------------------------------------------------------------------------

        private void ReadMouthStyle(JsonObject o)
        {
            if (o["lipY"] == null) return;
            var m = _mouth;
            m.Enabled = true;
            ReadClamped(o, "lipY", ref m.LipY, -3f, 3f);
            ReadClamped(o, "lipCornerY", ref m.LipCornerY, -3f, 3f);
            m.LipWidthPct = ReadScaled(o, "lipHalfWidth", m.LipWidthPct, MouthAspect / 100f, 0f, 20f);
            ReadClamped(o, "openDepth", ref m.OpenDepth, 0f, 5f);
            ReadClamped(o, "teethHeight", ref m.UpperTooth, 0f, 2f);
            m.LowerTooth = m.UpperTooth * 0.7f;
            ReadClamped(o, "lowerTeethHeight", ref m.LowerTooth, 0f, 2f);
            m.TeethWidthPct = ReadScaled(o, "teethWidth", m.TeethWidthPct, 0.01f, 0.05f, 1f);
            ReadClamped(o, "toothWidth", ref m.ToothWidth, 0.2f, 4f);
            m.ToothGapPct = ReadScaled(o, "toothGap", m.ToothGapPct, 0.01f, 0f, 0.9f);
            ReadClamped(o, "bite", ref m.Bite, -1f, 1f);
            ReadClamped(o, "restTeethUpper", ref m.RestTeethUpper, 0f, 1f);
            ReadClamped(o, "restTeethLower", ref m.RestTeethLower, 0f, 1f);
            ReadClamped(o, "edge", ref m.Edge, 0f, 1f);
            ReadClamped(o, "lipThickness", ref m.LipThickness, 0f, 2f);
            ReadClamped(o, "lipOpacity", ref m.LipOpacity, 0f, 1f);
            ReadClamped(o, "lipArc", ref m.LipArc, 0.05f, 4f);
            m.AlwaysDrawn = o["alwaysDrawn"] is JsonNode ad && ad.GetValue<double>() > 0.5;
            ReadClamped(o, "maskMargin", ref m.MaskMargin, 0f, 3f);
            ReadClamped(o, "baseOpen", ref m.BaseOpen, 0f, 1f);
            m.Lips = ReadColor(o, "lips") ?? m.Lips;
            m.Outline = ReadColor(o, "outline") ?? m.Outline;
            m.Skin = ReadColor(o, "skin");
            m.SkinRoughness = o["skinRoughness"] is JsonNode r ? Math.Clamp((float)r.GetValue<double>(), 0f, 1f) : null;
            m.SkinMetalness = o["skinMetalness"] is JsonNode mt ? Math.Clamp((float)mt.GetValue<double>(), 0f, 1f) : null;
            m.Crease = ReadColor(o, "crease") ?? m.Crease;
            ReadClamped(o, "creaseLength", ref m.CreaseLength, 0f, 5f);
            ReadClamped(o, "creaseWidth", ref m.CreaseWidth, 0f, 1f);
            ReadClamped(o, "creaseCurve", ref m.CreaseCurve, 0f, 1f);
            ReadClamped(o, "creaseAngle", ref m.CreaseAngle, -90f, 90f);
            ReadClamped(o, "creaseOffset", ref m.CreaseOffset, -2f, 2f);
            m.Interior = ReadColor(o, "interior") ?? m.Interior;
            m.Teeth = ReadColor(o, "teeth") ?? m.Teeth;
            m.Tongue = ReadColor(o, "tongue") ?? m.Tongue;
        }

        // A value saved as `panel * scale`, read back into the panel's units.
        private static float ReadScaled(JsonObject o, string key, float panel, float scale, float min, float max)
        {
            float saved = panel * scale;
            ReadClamped(o, key, ref saved, min, max);
            return scale > 0f ? saved / scale : panel;
        }

        private void WriteMouthStyle(JsonObject extras)
        {
            if (!_mouth.Enabled) return;
            foreach (var (key, value) in MouthValues()) extras[key] = Math.Round(value, 3);
            foreach (var (key, color) in MouthColors())
                if (color is Color c) extras[key] = ColorJson(c);
            if (_mouth.SkinRoughness is float r && _mouth.SkinMetalness is float mt)
            {
                extras["skinRoughness"] = Math.Round(r, 3);
                extras["skinMetalness"] = Math.Round(mt, 3);
            }
        }

        // The extras' numbers and colours, in their saved units - Save and the preview both read these.
        private IEnumerable<(string Key, float Value)> MouthValues()
        {
            var m = _mouth;
            yield return ("lipY", m.LipY);
            yield return ("lipCornerY", m.LipCornerY);
            yield return ("lipHalfWidth", m.LipWidthPct / 100f * MouthAspect);
            yield return ("openDepth", m.OpenDepth);
            yield return ("teethHeight", m.UpperTooth);
            yield return ("lowerTeethHeight", m.LowerTooth);
            yield return ("teethWidth", m.TeethWidthPct / 100f);
            yield return ("toothWidth", m.ToothWidth);
            yield return ("toothGap", m.ToothGapPct / 100f);
            yield return ("creaseLength", m.CreaseLength);
            yield return ("creaseWidth", m.CreaseWidth);
            yield return ("creaseCurve", m.CreaseCurve);
            yield return ("creaseAngle", m.CreaseAngle);
            yield return ("creaseOffset", m.CreaseOffset);
            yield return ("bite", m.Bite);
            yield return ("restTeethUpper", m.RestTeethUpper);
            yield return ("restTeethLower", m.RestTeethLower);
            yield return ("edge", m.Edge);
            yield return ("lipThickness", m.LipThickness);
            yield return ("lipOpacity", m.LipOpacity);
            yield return ("lipArc", m.LipArc);
            yield return ("alwaysDrawn", m.AlwaysDrawn ? 1f : 0f);
            yield return ("maskMargin", m.MaskMargin);
            yield return ("baseOpen", m.BaseOpen);
        }

        private IEnumerable<(string Key, Color? Color)> MouthColors()
        {
            var m = _mouth;
            yield return ("lips", m.Lips);
            yield return ("outline", m.Outline);
            yield return ("skin", m.Skin);
            yield return ("crease", m.Crease);
            yield return ("interior", m.Interior);
            yield return ("teeth", m.Teeth);
            yield return ("tongue", m.Tongue);
        }

        private string MouthJson()
        {
            var parts = new List<string> { $"\"on\":{(_mouth.Enabled ? "true" : "false")}" };
            foreach (var (key, value) in MouthValues()) parts.Add($"\"{key}\":{Inv(value)}");
            foreach (var (key, color) in MouthColors()) parts.Add($"\"{key}\":{(color is Color c ? ColorJs(c) : "null")}");
            parts.Add($"\"testOpen\":{Inv(_sliderOpen.Value / 100f)}");
            parts.Add($"\"skinRoughness\":{(_mouth.SkinRoughness is float r ? Inv(r) : "null")}," +
                      $"\"skinMetalness\":{(_mouth.SkinMetalness is float mt ? Inv(mt) : "null")}");
            parts.Add($"\"teethUpper\":{(_chkTeethUpper.Checked ? 1 : 0)},\"teethLower\":{(_chkTeethLower.Checked ? 1 : 0)}");
            return "{" + string.Join(",", parts) + "}";
        }

        // ---- Panel ---------------------------------------------------------------------------

        private void BuildMouthSection(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Caption("Open mouth (drawn over the painted one when an expression opens it):", 4));
            _chkMouth = new CheckBox { Text = "Draw open mouth", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
            _chkMouth.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _mouth.Enabled = _chkMouth.Checked;
                MouthEdited();
                FillSkinIfMissing();
            };
            flow.Controls.Add(_chkMouth);
            _chkAlwaysDrawn = new CheckBox { Text = "Always draw the mouth", AutoSize = true, Margin = new Padding(3, 0, 3, 6) };
            _chkAlwaysDrawn.CheckedChanged += (s, e) =>
            {
                if (_suppressEvents) return;
                _mouth.AlwaysDrawn = _chkAlwaysDrawn.Checked;
                MouthEdited();
            };
            flow.Controls.Add(_chkAlwaysDrawn);
            _help.Add(_chkAlwaysDrawn, "The drawn mouth shows at rest too - shut lips, or the painted opening for a " +
                                       "painted-open mouth - instead of handing back to the painted mouth, so there's no " +
                                       "swap as it starts to move. The painted mouth stays masked in skin.");

            BuildLipRows(flow);
            BuildTeethRows(flow);
            BuildMaskRows(flow);
            BuildCreaseRows(flow);
            BuildMouthColorRows(flow);
            BuildMouthTestRows(flow);
        }

        private void BuildLipRows(FlowLayoutPanel flow)
        {
            var m = _mouth;
            const string fromPoints = " Fitted through the mouth points when they're set.";
            _lipLineNumbers.Add(AddMouthNumber(flow, "Lip line Y", -3m, 3m, 0.02m, () => m.LipY, v => m.LipY = v,
                "Height of the resting lip line at the middle, region units (0 = the region's centre). Line it up " +
                "with the painted mouth - it's drawn over it with Show regions on." + fromPoints));
            _lipLineNumbers.Add(AddMouthNumber(flow, "Corner Y", -3m, 3m, 0.02m, () => m.LipCornerY, v => m.LipCornerY = v,
                "Height of the lip line at the corners: above the middle for a smile, below for a frown." + fromPoints));
            _lipLineNumbers.Add(AddMouthNumber(flow, "Lip width %", 5m, 100m, 1m, () => m.LipWidthPct, v => m.LipWidthPct = v,
                "How far the lip line runs either side, % of the region's width." + fromPoints, 0));
            AddMouthNumber(flow, "Painted open", 0m, 1m, 0.05m, () => m.BaseOpen,
                v => { m.BaseOpen = v; _sliderOpen.Value = (int)Math.Round(v * 100f); },
                "For a model painted with its mouth open (a grin): how open that is, as the drawn mouth would be. " +
                "It then rests there, drawn over the paint with lips round it: expressions open it further and " +
                "drop the chin, talking can shut it - the lips close over the painted mouth and the chin rises. " +
                "0 = painted shut, 1 = painted fully open.");
            AddMouthNumber(flow, "Open distance", 0m, 5m, 0.05m, () => m.OpenDepth, v => m.OpenDepth = v,
                "How far below the lip line a fully open mouth reaches, region units. Expressions and talking " +
                "open it part way.");
            AddMouthNumber(flow, "Lip thickness", 0m, 2m, 0.02m, () => m.LipThickness, v => m.LipThickness = v,
                "How thick the lips round the opening are, region units (the lower one a little fuller). 0 = no lips.");
            AddMouthNumber(flow, "Lip opacity", 0m, 1m, 0.05m, () => m.LipOpacity, v => m.LipOpacity = v,
                "How much the drawn lips cover the paint under them: 1 = solid, lower lets the painted mouth show through.");
            AddMouthNumber(flow, "Lip arc", 0.05m, 4m, 0.05m, () => m.LipArc, v => m.LipArc = v,
                "The bottom lip's curve as the mouth opens: 1 = round; lower is flatter, deep right out to the corners " +
                "(an even grin); higher drops the middle more than the sides.");
            AddMouthNumber(flow, "Outline width", 0m, 1m, 0.01m, () => m.Edge, v => m.Edge = v,
                "Width of the soft line round the outside of the lips, in the Outline colour.");
        }

        private void BuildTeethRows(FlowLayoutPanel flow)
        {
            var m = _mouth;
            AddMouthNumber(flow, "Upper tooth height", 0m, 2m, 0.02m, () => m.UpperTooth, v => m.UpperTooth = v,
                "How far the front upper teeth reach below the top lip, region units.");
            AddMouthNumber(flow, "Lower tooth height", 0m, 2m, 0.02m, () => m.LowerTooth, v => m.LowerTooth = v,
                "How far the front lower teeth reach above the bottom lip, region units.");
            AddMouthNumber(flow, "Teeth row width %", 5m, 100m, 1m, () => m.TeethWidthPct, v => m.TeethWidthPct = v,
                "How far either side the row of teeth reaches, % of the lip width. Past it the corners of the mouth are dark.", 0);
            AddMouthNumber(flow, "Tooth width", 0.2m, 4m, 0.05m, () => m.ToothWidth, v => m.ToothWidth = v,
                "Each tooth's width, x the standard: higher is fewer, bigger teeth.");
            AddMouthNumber(flow, "Tooth spacing %", 0m, 90m, 1m, () => m.ToothGapPct, v => m.ToothGapPct = v,
                "The gap between neighbouring teeth, % of a tooth. 0 = touching, with a thin shadow line.", 0);
            AddMouthNumber(flow, "Bite", -1m, 1m, 0.05m, () => m.Bite, v => m.Bite = v,
                "Overbite (up to 1): the upper teeth longer and in front of the lower. Underbite (down to -1): " +
                "the lower teeth longer, wider and in front. 0 = even, upper in front.");
            AddMouthNumber(flow, "Rest upper teeth", 0m, 1m, 0.05m, () => m.RestTeethUpper, v => m.RestTeethUpper = v,
                "How far the upper teeth show whenever the mouth is drawn, whatever the expression (1 = full length) - " +
                "a grin's teeth at rest. Expressions can show more; talking sets them outright.");
            AddMouthNumber(flow, "Rest lower teeth", 0m, 1m, 0.05m, () => m.RestTeethLower, v => m.RestTeethLower = v,
                "As Rest upper teeth, for the lower row.");
        }

        private void BuildMaskRows(FlowLayoutPanel flow)
        {
            var m = _mouth;
            AddMouthNumber(flow, "Mask margin", 0.02m, 3m, 0.02m, () => m.MaskMargin, v => m.MaskMargin = v,
                "Whenever the mouth is open, everything this close (region units) to the painted lip line - its " +
                "whole width - or to the opening is covered in the Skin colour, so the painted mouth (a baked-on " +
                "smile past an O's corners, painted lips) never shows round the drawn one. Raise it until none does.");
        }

        // A crease at each corner of the open mouth, like the ones painted on a smiling face.
        private void BuildCreaseRows(FlowLayoutPanel flow)
        {
            var m = _mouth;
            AddMouthNumber(flow, "Crease length", 0m, 5m, 0.02m, () => m.CreaseLength, v => m.CreaseLength = v,
                "A crease at each corner of the open mouth, this long (region units), just outside the corner. 0 = none.");
            AddMouthNumber(flow, "Crease width", 0m, 1m, 0.01m, () => m.CreaseWidth, v => m.CreaseWidth = v,
                "How thick the crease is in its middle; it tapers to points at the ends.");
            AddMouthNumber(flow, "Crease curve", 0m, 1m, 0.05m, () => m.CreaseCurve, v => m.CreaseCurve = v,
                "0 = a straight stroke, 1 = a half circle. It bulges away from the mouth - a ) at the right corner.");
            AddMouthNumber(flow, "Crease angle", -90m, 90m, 5m, () => m.CreaseAngle, v => m.CreaseAngle = v,
                "Degrees the crease's middle turns from straight out to the side: positive up, negative down.", 0);
            AddMouthNumber(flow, "Crease offset", -2m, 2m, 0.02m, () => m.CreaseOffset, v => m.CreaseOffset = v,
                "How far outside the opening's corner the crease sits, region units (negative: overlapping it).");
            AddMouthColor(flow, "Crease", c => m.Crease = c, () => m.Crease, fromModel: true,
                "The creases' colour. \"From Model\" takes it from the next spot you click - a painted crease, say.");
        }

        private void BuildMouthColorRows(FlowLayoutPanel flow)
        {
            var m = _mouth;
            AddMouthColor(flow, "Lips", c => m.Lips = c, () => m.Lips, fromModel: true,
                "The lips' colour. \"From Model\" takes it from the next spot you click on the model.");
            AddMouthColor(flow, "Outline", c => m.Outline = c, () => m.Outline, fromModel: true,
                "The line round the outside of the lips. \"From Model\" takes it from the next spot you click.");
            AddMouthColor(flow, "Skin", c => m.Skin = c, () => m.Skin, fromModel: true,
                "The skin round the mouth, for the mask. Taken from the cheek beside the mouth region when the " +
                "open mouth is first on; \"From Model\" takes it from the next spot you click instead.");
            AddMouthColor(flow, "Inside", c => m.Interior = c, () => m.Interior, fromModel: false, "The inside of the mouth.");
            AddMouthColor(flow, "Teeth", c => m.Teeth = c, () => m.Teeth, fromModel: false, "The teeth.");
            AddMouthColor(flow, "Tongue", c => m.Tongue = c, () => m.Tongue, fromModel: false, "The tongue.");
        }

        private void BuildMouthTestRows(FlowLayoutPanel flow)
        {
            flow.Controls.Add(Caption("Test open (preview only; starts at the painted opening):"));
            _sliderOpen = new TrackBar
            {
                Minimum = 0, Maximum = 100, Value = (int)Math.Round(_mouth.BaseOpen * 100f), TickFrequency = 10, Width = 290,
                Margin = new Padding(3, 0, 3, 4),
            };
            _sliderOpen.ValueChanged += (s, e) => PushState();
            flow.Controls.Add(_sliderOpen);
            var teethRow = MakeRow();
            _chkTeethUpper = new CheckBox { Text = "Upper teeth", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 10, 8) };
            _chkTeethLower = new CheckBox { Text = "Lower teeth", AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
            _chkTeethUpper.CheckedChanged += (s, e) => PushState();
            _chkTeethLower.CheckedChanged += (s, e) => PushState();
            teethRow.Controls.Add(_chkTeethUpper);
            teethRow.Controls.Add(_chkTeethLower);
            flow.Controls.Add(teethRow);
        }

        private void SetLipLineEditable(bool editable)
        {
            foreach (var n in _lipLineNumbers) n.Enabled = editable;
        }

        private NumericUpDown AddMouthNumber(FlowLayoutPanel flow, string label, decimal min, decimal max, decimal step,
                                             Func<float> get, Action<float> set, string help, int decimals = 2)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 110, Margin = new Padding(3, 6, 3, 3) });
            var n = new NumericUpDown { Width = 110, Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Margin = new Padding(3, 3, 3, 3) };
            n.ValueChanged += (s, e) => { if (!_suppressEvents) { set((float)n.Value); MouthEdited(); } };
            row.Controls.Add(n);
            flow.Controls.Add(row);
            _help.Add(row, help);
            _mouthNumbers.Add((n, get));
            return n;
        }

        private void AddMouthColor(FlowLayoutPanel flow, string label, Action<Color> set, Func<Color?> get, bool fromModel, string help)
        {
            var row = MakeRow();
            row.Controls.Add(new Label { Text = label, Width = 110, Margin = new Padding(3, 6, 3, 3) });
            var swatch = new Button { Text = "Pick...", Width = 110, Margin = new Padding(3, 3, 3, 3) };
            void Apply(Color c) { set(c); ShowSwatch(swatch, c); MouthEdited(); }
            swatch.Click += (s, e) =>
            {
                using var dialog = new ColorDialog { Color = get() ?? Color.Gray, FullOpen = true, AnyColor = true };
                if (dialog.ShowDialog(this) == DialogResult.OK) Apply(dialog.Color);
            };
            row.Controls.Add(swatch);
            if (fromModel)
            {
                _mouthPickTargets[label] = Apply;
                var sample = new Button { Text = "From Model", AutoSize = true, Margin = new Padding(3, 3, 3, 3) };
                sample.Click += (s, e) => StartMouthEyedropper(label);
                row.Controls.Add(sample);
            }
            flow.Controls.Add(row);
            _help.Add(row, help);
            _mouthColors.Add((swatch, get));
        }

        private static void ShowSwatch(Button swatch, Color? c)
        {
            swatch.BackColor = c ?? SystemColors.Control;
            swatch.Text = c == null ? "Pick... (none)" : "Pick...";
        }

        // The next click in the preview samples the texture there for `target` instead of placing a region.
        private void StartMouthEyedropper(string target)
        {
            if (!_viewerReady) return;
            _webView.CoreWebView2.ExecuteScriptAsync($"setPickMode('{target}');");
            _lblStatus.Text = $"Click the model to take the {target.ToLowerInvariant()} colour from it.";
        }

        // The mask needs a skin colour: with none picked, the preview samples the cheek beside the mouth
        // region (sampleSkinBeside) and sends it back as a Skin pick. A skin colour without its roughness
        // and metalness (set by hand, or saved before they were kept) gets just those from the cheek.
        private void FillSkinIfMissing()
        {
            if (!_mouth.Enabled || !_regions[Mouth].Set || !_viewerReady) return;
            if (_mouth.Skin == null) _webView.CoreWebView2.ExecuteScriptAsync("sampleSkinBeside(false);");
            else if (_mouth.SkinRoughness == null) _webView.CoreWebView2.ExecuteScriptAsync("sampleSkinBeside(true);");
        }

        // From the preview: the texture's sRGB under the eyedropper's click, and for the skin its
        // roughness and metalness there too.
        private void OnMouthColorPicked(string? target, float[] rgb, float[]? mr, bool mrOnly)
        {
            if (target == null || !_mouthPickTargets.TryGetValue(target, out var apply)) return;
            if (target == "Skin" && mr is { Length: 2 })
            {
                (_mouth.SkinRoughness, _mouth.SkinMetalness) = (Math.Clamp(mr[0], 0f, 1f), Math.Clamp(mr[1], 0f, 1f));
                _dirty = true;
            }
            if (mrOnly) { PushState(); return; }
            apply(Color.FromArgb(ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2])));
        }

        // The lowest the drawn mouth reaches fully open, mesh y: the lip line's middle less the opening, the
        // lips round it and the outline, and most of a region height of room: the mesh's triangles are big round a
        // mouth, and one pulled at its lower corner bends the drawn lip above it. The chin's pull starts below
        // it (FaceBoneRigger).
        private float? MouthOpenBottomY()
        {
            if (!_mouth.Enabled || !_regions[Mouth].Set) return null;
            var m = _mouth;
            float ry = _regions[Mouth].RadiiPct.Y * _modelHeight / 100f;
            float shut = m.BaseOpen > 0f || m.AlwaysDrawn ? 2f * m.LipThickness : 0f;
            return _regions[Mouth].Centre.Y + (m.LipY - m.OpenDepth - shut - m.Edge - 0.8f) * ry;
        }

        private void MouthEdited()
        {
            _dirty = true;
            PushState();
        }

        private void RefreshMouthUi()
        {
            _chkMouth.Checked = _mouth.Enabled;
            _chkAlwaysDrawn.Checked = _mouth.AlwaysDrawn;
            foreach (var (control, get) in _mouthNumbers) SetClamped(control, get());
            foreach (var (swatch, get) in _mouthColors) ShowSwatch(swatch, get());
        }
    }
}
