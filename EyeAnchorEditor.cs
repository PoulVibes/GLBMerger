using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace GlbMerger
{
    // Places the two "EyeAnchorL" / "EyeAnchorR" locator nodes the game's eye overlay reads
    // (FootballRoguelite: EyeRig.h, shaders/EyeOverlay.glsl). The players' eyes are painted into
    // the texture atlas - split across charts, each rotated its own way - so the game repaints
    // them in the mesh's own bind-pose space instead: anything inside an anchor's ellipse that is
    // the painted eye (white or very dark) becomes white, with a pupil drawn where the player is
    // looking.
    //
    // An anchor is a child of the head bone. Its position is the eye's centre and its scale the
    // eye's half-extents in MESH units along the mesh's own axes: x across, y up, z the depth of
    // the slab the eye is projected through. The game maps it back into mesh space with the head
    // joint's inverse-bind matrix, so this tool writes its local transform through the same
    // matrix - which is what keeps it right on rigs whose skeleton and mesh use different units.
    //
    // The model's eye style - everything under "Eye style" in the panel - is written to both
    // anchors' glTF extras (EyeRig::ReadStyle reads it; anything a model leaves out falls back to
    // the game's Eyes debug panel): "pupilRadius", "maxOffset" (pupil travel), "irisRadius" (all
    // in eye radii), "irisColor" ([r,g,b] sRGB 0..1, as picked), "rimWidth", "rimStrength",
    // "fill", and the lids: "lidColor" (sRGB - no lids in game until it is set), "lidRest" (upper
    // lid closure at rest), "lowerRest" (lower lid), "lashWidth", "lashStrength", and the preview-only
    // "lidRoughness" / "lidMetalness" (the game has no metallic/roughness) sampled with the colour. Eyeball fill is how much of each ellipse
    // is eyeball: 0 = only the painted eye inside it is repainted, so the
    // paint bounds the eyeball and Width/Height only limit the search; 1 = the whole ellipse is
    // eyeball, outlined by a dark rim - for when the painted eye is too small or the wrong shape.
    //
    // Each eye's projection slab runs Back depth behind its centre but only Front depth ahead of
    // it (saved as that anchor's "front" extra, a fraction of the back depth) - short enough, it
    // stops before glasses sitting just in front of the eye, so their frames aren't painted over.
    //
    // Click an eye in the preview to centre the selected anchor there (Shift+click goes through
    // the first surface hit - the glasses - to the one behind it); the numbers then fine-tune
    // it. The preview runs a copy of the game's overlay shader (EyeAnchorEditor.Viewer.cs) with
    // test-gaze sliders, so the pupils can be checked looking every way before saving.
    public partial class EyeAnchorEditor : UserControl, IUnappliedChanges
    {
        private static readonly string[] AnchorNames = { "EyeAnchorL", "EyeAnchorR" };
        private const string UnusedSuffix = "_unused";

        // Starting size for a freshly clicked eye, as a percent of the model's height.
        private static readonly Vector3 DefaultRadiiPct = new(2.2f, 1.8f, 3.0f);

        private readonly HelpTips _help;
        private readonly ModelRoot _model;
        private readonly Node? _head;
        private readonly Matrix4x4 _meshToHead;   // row-vector: mesh space -> head-local (its inverse bind)
        private readonly float _modelHeight;

        private struct EyeState
        {
            public bool Set;
            public Vector3 Centre;     // mesh units
            public Vector3 RadiiPct;   // percent of model height; Z = depth behind the centre
            public float FrontPct;     // depth in front of the centre, percent of model height, <= RadiiPct.Z
        }
        private readonly EyeState[] _eyes = new EyeState[2];
        private readonly EyeState[] _savedEyes = new EyeState[2];
        // Model-wide, both eyes.
        private struct EyeStyle
        {
            public float Fill;          // 0..1
            public float PupilRadius;   // eye radii
            public float MaxOffset;     // pupil travel at a full look
            public float IrisRadius;    // eye radii
            public Color IrisColor;     // sRGB
            public float RimWidth;      // eye radii
            public float RimStrength;   // 0..1
            public Color? LidColor;     // sRGB; null = no lids
            public float? LidRoughness; // preview only: the clicked skin's, so lids don't shine like the eye
            public float? LidMetalness;
            public float LidRest;       // upper lid closure at rest, 0..1
            public float LowerRest;     // lower lid closure at rest, 0..1
            public float LashWidth;     // eye radii
            public float LashStrength;  // 0..1
            public float Cleanup;       // eye radii past the ellipse where stray painted eye becomes lid skin
            public float LimitAbove;    // overlay's reach past the resting upper / lower lid edge, eye radii (3 = none)
            public float LimitBelow;
            public float Tilt;          // degrees, outer corners up; mirrored per eye
        }
        // The game's EyeLook defaults.
        private static readonly EyeStyle DefaultStyle = new()
        {
            Fill = 1f, PupilRadius = 0.45f, MaxOffset = 0.50f, IrisRadius = 0.70f,
            IrisColor = Color.FromArgb(102, 64, 33), RimWidth = 0.15f, RimStrength = 0.20f,
            LidColor = null, LidRest = 0.18f, LowerRest = 0.05f, LashWidth = 0.10f, LashStrength = 0.85f, Cleanup = 0.15f,
            LimitAbove = 3f, LimitBelow = 3f, Tilt = 0f,
        };
        private EyeStyle _style = DefaultStyle, _savedStyle = DefaultStyle;

        private RadioButton _radioLeft = null!, _radioRight = null!;
        private NumericUpDown _numX = null!, _numY = null!, _numZ = null!;
        private NumericUpDown _numW = null!, _numH = null!, _numD = null!, _numF = null!;
        private CheckBox _chkSameSize = null!, _chkShowRegion = null!;
        private TrackBar _sliderYaw = null!, _sliderPitch = null!;
        private NumericUpDown _numPupil = null!, _numTravel = null!;   // saved with the style
        private NumericUpDown _numFill = null!, _numRimWidth = null!, _numRimStrength = null!;
        private NumericUpDown _numIrisRadius = null!;
        private Button _btnIrisColor = null!, _btnLidColor = null!;
        private Color _swatchDefault = SystemColors.Control;   // the theme's button colour, for "no lid colour"
        private NumericUpDown _numCleanup = null!, _numLimitAbove = null!, _numLimitBelow = null!, _numTilt = null!;
        private NumericUpDown _numLidRest = null!, _numLowerRest = null!, _numLashWidth = null!, _numLashStrength = null!;
        private CheckBox _chkAutoBlink = null!;
        private TrackBar _sliderClosure = null!;
        private Label _lblClosure = null!;
        private Label _lblStatus = null!, _lblGaze = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _suppressEvents;

        public EyeAnchorEditor(ModelRoot model, bool darkMode = false)
        {
            _help = new HelpTips(this);
            _model = model;
            _head = FindHead(model);
            _meshToHead = MeshToNode(model, _head);
            _modelHeight = MeasureModelHeight(model);

            Dock = DockStyle.Fill;
            LoadExistingAnchors();
            Array.Copy(_eyes, _savedEyes, 2);
            _savedStyle = _style;

            BuildUi();
            // Theme first: it repaints every button's background, which would wipe the iris /
            // lid colour swatches RefreshUiFromState paints on theirs.
            ThemeManager.Apply(this, darkMode);
            _swatchDefault = _btnLidColor.BackColor;
            RefreshUiFromState();

            _ = InitializeViewerAsync();
        }

        public bool HasUnappliedChanges => !_eyes.SequenceEqual(_savedEyes) || !_style.Equals(_savedStyle);
        public string UnappliedChangesDescription => "eye anchor edits not yet saved";

        private int Side => _radioRight.Checked ? 1 : 0;

        // ---- Rig lookups ------------------------------------------------------------------

        // The game's convention (BoneNames::Normalise): lowercase alphanumerics, minus a
        // "mixamorig" prefix, compared exactly - so "mixamorig:Head" matches but "HeadTop_End"
        // does not.
        private static string Normalise(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var s = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            return s.StartsWith("mixamorig", StringComparison.Ordinal) ? s.Substring("mixamorig".Length) : s;
        }

        private static Node? FindHead(ModelRoot model) =>
            model.LogicalNodes.FirstOrDefault(n => Normalise(n.Name) == "head");

        // The node's inverse-bind matrix when it is a skin joint - the same mapping the game uses
        // (EyeRig.cpp BindToMesh) - else the inverse of its rest world matrix.
        private static Matrix4x4 MeshToNode(ModelRoot model, Node? node)
        {
            if (node == null) return Matrix4x4.Identity;
            foreach (var skin in model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (joint, inverseBind) = skin.GetJoint(i);
                    if (joint == node) return inverseBind;
                }
            return Matrix4x4.Invert(node.WorldMatrix, out var inv) ? inv : Matrix4x4.Identity;
        }

        private static float MeasureModelHeight(ModelRoot model)
        {
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                    if (positions == null) continue;
                    foreach (var p in positions) { minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
                }
            return maxY > minY ? maxY - minY : 1f;
        }

        private Node? FindAnchorNode(int side, bool includeUnused)
        {
            string name = AnchorNames[side];
            return _model.LogicalNodes.FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? (includeUnused
                    ? _model.LogicalNodes.FirstOrDefault(n => string.Equals(n.Name, name + UnusedSuffix, StringComparison.OrdinalIgnoreCase))
                    : null);
        }

        // ---- Load / save ------------------------------------------------------------------

        private void LoadExistingAnchors()
        {
            for (int side = 0; side < 2; side++)
            {
                var node = FindAnchorNode(side, includeUnused: false);
                if (node == null) continue;

                // Row-vector: local -> parent-local -> mesh.
                var meshToParent = MeshToNode(_model, node.VisualParent);
                if (!Matrix4x4.Invert(meshToParent, out var parentToMesh)) continue;
                var m = node.LocalTransform.Matrix * parentToMesh;

                var radii = new Vector3(new Vector3(m.M11, m.M12, m.M13).Length(),
                                        new Vector3(m.M21, m.M22, m.M23).Length(),
                                        new Vector3(m.M31, m.M32, m.M33).Length());
                var radiiPct = radii / _modelHeight * 100f;
                _eyes[side] = new EyeState
                {
                    Set = true, Centre = m.Translation, RadiiPct = radiiPct, FrontPct = ReadFront(node) * radiiPct.Z,
                };
                ReadStyle(node);
            }
        }

        // Missing keys keep whatever is already set (the defaults, or the other anchor's).
        private void ReadStyle(Node node)
        {
            if (node.Extras is not JsonObject o) return;
            try
            {
                ReadUnit(o, "fill", ref _style.Fill);
                ReadUnit(o, "pupilRadius", ref _style.PupilRadius);
                ReadUnit(o, "maxOffset", ref _style.MaxOffset);
                ReadUnit(o, "irisRadius", ref _style.IrisRadius);
                ReadUnit(o, "rimWidth", ref _style.RimWidth);
                ReadUnit(o, "rimStrength", ref _style.RimStrength);
                ReadUnit(o, "lidRest", ref _style.LidRest);
                ReadUnit(o, "lowerRest", ref _style.LowerRest);
                ReadUnit(o, "lashWidth", ref _style.LashWidth);
                ReadUnit(o, "lashStrength", ref _style.LashStrength);
                ReadUnit(o, "cleanup", ref _style.Cleanup);
                ReadLimit(o, "limitAbove", ref _style.LimitAbove);
                ReadLimit(o, "limitBelow", ref _style.LimitBelow);
                if (o["tilt"] is JsonValue tv && tv.TryGetValue(out double tilt))
                    _style.Tilt = Math.Clamp((float)tilt, -45f, 45f);
                if (o["irisColor"] is JsonArray c && c.Count == 3)
                    _style.IrisColor = Color.FromArgb(ToByte(c[0]), ToByte(c[1]), ToByte(c[2]));
                if (o["lidRoughness"] is JsonValue lr && lr.TryGetValue(out double rough))
                    _style.LidRoughness = Math.Clamp((float)rough, 0f, 1f);
                if (o["lidMetalness"] is JsonValue lm && lm.TryGetValue(out double metal))
                    _style.LidMetalness = Math.Clamp((float)metal, 0f, 1f);
                if (o["lidColor"] is JsonArray l && l.Count == 3)
                    _style.LidColor = Color.FromArgb(ToByte(l[0]), ToByte(l[1]), ToByte(l[2]));
            }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }

        private static float ReadFront(Node node)
        {
            float front = 1f;
            if (node.Extras is JsonObject o)
                try { ReadUnit(o, "front", ref front); } catch (InvalidOperationException) { }
            return front;
        }

        private static void ReadLimit(JsonObject o, string key, ref float field)
        {
            if (o[key] is JsonValue v && v.TryGetValue(out double value)) field = Math.Clamp((float)value, 0f, 3f);
        }

        private static void ReadUnit(JsonObject o, string key, ref float field)
        {
            if (o[key] is JsonValue v && v.TryGetValue(out double value)) field = Math.Clamp((float)value, 0f, 1f);
        }

        private static int ToByte(JsonNode? n) => (int)Math.Round(Math.Clamp(n!.GetValue<double>(), 0, 1) * 255);
        private static double Unit(int channel) => Math.Round(channel / 255.0, 4);

        private JsonObject StyleExtras()
        {
            var extras = BaseStyleExtras();
            extras["lidRest"] = Math.Round(_style.LidRest, 3);
            extras["lowerRest"] = Math.Round(_style.LowerRest, 3);
            extras["lashWidth"] = Math.Round(_style.LashWidth, 3);
            extras["lashStrength"] = Math.Round(_style.LashStrength, 3);
            extras["cleanup"] = Math.Round(_style.Cleanup, 3);
            extras["limitAbove"] = Math.Round(_style.LimitAbove, 3);
            extras["limitBelow"] = Math.Round(_style.LimitBelow, 3);
            extras["tilt"] = Math.Round(_style.Tilt, 2);
            if (_style.LidColor is Color lid)
                extras["lidColor"] = new JsonArray(Unit(lid.R), Unit(lid.G), Unit(lid.B));
            if (_style.LidRoughness is float lr && _style.LidMetalness is float lm)
            {
                extras["lidRoughness"] = Math.Round(lr, 3);
                extras["lidMetalness"] = Math.Round(lm, 3);
            }
            return extras;
        }

        private JsonObject BaseStyleExtras() => new()
        {
            ["fill"] = Math.Round(_style.Fill, 3),
            ["pupilRadius"] = Math.Round(_style.PupilRadius, 3),
            ["maxOffset"] = Math.Round(_style.MaxOffset, 3),
            ["irisRadius"] = Math.Round(_style.IrisRadius, 3),
            ["rimWidth"] = Math.Round(_style.RimWidth, 3),
            ["rimStrength"] = Math.Round(_style.RimStrength, 3),
            ["irisColor"] = new JsonArray(Unit(_style.IrisColor.R), Unit(_style.IrisColor.G), Unit(_style.IrisColor.B)),
        };

        private void SaveAnchors()
        {
            if (_head == null)
            {
                MessageBox.Show(this, "No \"head\" bone found - nowhere to parent the eye anchors.", "Eye Anchors",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int saved = 0;
            for (int side = 0; side < 2; side++)
            {
                var existing = FindAnchorNode(side, includeUnused: true);
                if (!_eyes[side].Set)
                {
                    // SharpGLTF can't detach a node: a cleared anchor is renamed out of the game's
                    // sight instead, and reused if this eye is placed again.
                    if (existing != null) existing.Name = AnchorNames[side] + UnusedSuffix;
                    continue;
                }

                // Anchor in mesh space (scale then place), carried into head-local by the head's
                // inverse bind - the exact inverse of how the game reads it back.
                var radii = _eyes[side].RadiiPct * (_modelHeight / 100f);
                var meshAnchor = Matrix4x4.CreateScale(radii) * Matrix4x4.CreateTranslation(_eyes[side].Centre);
                if (!Matrix4x4.Decompose(meshAnchor * _meshToHead, out var scale, out var rotation, out var translation))
                    continue;

                var node = existing ?? _head.CreateNode(AnchorNames[side]);
                node.Name = AnchorNames[side];
                node.LocalTransform = new AffineTransform(scale, rotation, translation);
                var extras = StyleExtras();
                var eye = _eyes[side];
                extras["front"] = Math.Round(eye.RadiiPct.Z > 0 ? Math.Clamp(eye.FrontPct / eye.RadiiPct.Z, 0f, 1f) : 1f, 3);
                node.Extras = extras;
                saved++;
            }

            Array.Copy(_eyes, _savedEyes, 2);
            _savedStyle = _style;
            _lblStatus.Text = $"Saved {saved} eye anchor(s) under \"{_head.Name}\". Save the GLB to keep them.";
        }

        // ---- Editing ----------------------------------------------------------------------

        private void PlaceSelectedEye(Vector3 meshPoint)
        {
            int side = Side;
            var sizeFrom = _eyes[side].Set ? _eyes[side]
                         : _eyes[1 - side].Set ? _eyes[1 - side]
                         : new EyeState { RadiiPct = DefaultRadiiPct, FrontPct = DefaultRadiiPct.Z };
            _eyes[side] = new EyeState { Set = true, Centre = meshPoint, RadiiPct = sizeFrom.RadiiPct, FrontPct = sizeFrom.FrontPct };

            // Straight on to the other eye if it still needs placing.
            if (!_eyes[1 - side].Set) (side == 0 ? _radioRight : _radioLeft).Checked = true;
            RefreshUiFromState();
            PushEyes();
        }

        private void OnStyleChanged()
        {
            if (_suppressEvents) return;
            _style.Fill = (float)_numFill.Value / 100f;
            _style.PupilRadius = (float)_numPupil.Value;
            _style.MaxOffset = (float)_numTravel.Value;
            _style.IrisRadius = (float)_numIrisRadius.Value;
            _style.RimWidth = (float)_numRimWidth.Value;
            _style.RimStrength = (float)_numRimStrength.Value;
            _style.LidRest = (float)_numLidRest.Value;
            _style.LowerRest = (float)_numLowerRest.Value;
            _style.LashWidth = (float)_numLashWidth.Value;
            _style.LashStrength = (float)_numLashStrength.Value;
            _style.Cleanup = (float)_numCleanup.Value;
            _style.LimitAbove = (float)_numLimitAbove.Value;
            _style.LimitBelow = (float)_numLimitBelow.Value;
            _style.Tilt = (float)_numTilt.Value;
            PushEyes();
        }

        private void PickIrisColor()
        {
            using var dialog = new ColorDialog { Color = _style.IrisColor, FullOpen = true, AnyColor = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _style.IrisColor = dialog.Color;
            _btnIrisColor.BackColor = dialog.Color;
            PushEyes();
        }

        private void OnNumbersChanged()
        {
            if (_suppressEvents) return;
            int side = Side;
            if (!_eyes[side].Set) return;

            var radii = new Vector3((float)_numW.Value, (float)_numH.Value, (float)_numD.Value);
            float front = Math.Min((float)_numF.Value, radii.Z);   // the slab never reaches further forward than back
            _eyes[side].Centre = new Vector3((float)_numX.Value, (float)_numY.Value, (float)_numZ.Value);
            _eyes[side].RadiiPct = radii;
            _eyes[side].FrontPct = front;
            if (_chkSameSize.Checked && _eyes[1 - side].Set)
            {
                _eyes[1 - side].RadiiPct = radii;
                _eyes[1 - side].FrontPct = front;
            }
            PushEyes();
        }

        private void ClearSelectedEye()
        {
            _eyes[Side] = default;
            RefreshUiFromState();
            PushEyes();
        }

        private void RefreshUiFromState()
        {
            _suppressEvents = true;
            try
            {
                var eye = _eyes[Side];
                foreach (var n in new[] { _numX, _numY, _numZ, _numW, _numH, _numD, _numF }) n.Enabled = eye.Set;
                if (eye.Set)
                {
                    SetClamped(_numX, eye.Centre.X); SetClamped(_numY, eye.Centre.Y); SetClamped(_numZ, eye.Centre.Z);
                    SetClamped(_numW, eye.RadiiPct.X); SetClamped(_numH, eye.RadiiPct.Y); SetClamped(_numD, eye.RadiiPct.Z);
                    SetClamped(_numF, eye.FrontPct);
                }

                SetClamped(_numFill, _style.Fill * 100f);
                SetClamped(_numIrisRadius, _style.IrisRadius);
                SetClamped(_numPupil, _style.PupilRadius);
                SetClamped(_numTravel, _style.MaxOffset);
                SetClamped(_numRimWidth, _style.RimWidth);
                SetClamped(_numRimStrength, _style.RimStrength);
                _btnIrisColor.BackColor = _style.IrisColor;
                SetClamped(_numLidRest, _style.LidRest);
                SetClamped(_numLowerRest, _style.LowerRest);
                SetClamped(_numLashWidth, _style.LashWidth);
                SetClamped(_numLashStrength, _style.LashStrength);
                SetClamped(_numCleanup, _style.Cleanup);
                SetClamped(_numLimitAbove, _style.LimitAbove);
                SetClamped(_numLimitBelow, _style.LimitBelow);
                SetClamped(_numTilt, _style.Tilt);
                ShowLidColor();

                string State(int s) => _eyes[s].Set ? "placed" : "not placed";
                _lblStatus.Text = _head == null
                    ? "No \"head\" bone found - anchors can't be saved on this model."
                    : $"Left eye: {State(0)}   Right eye: {State(1)}";
            }
            finally { _suppressEvents = false; }
        }

        private static void SetClamped(NumericUpDown n, float v) =>
            n.Value = Math.Clamp((decimal)v, n.Minimum, n.Maximum);

        // ---- Viewer bridge ----------------------------------------------------------------

        private static string Inv(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        private void PushEyes()
        {
            if (!_viewerReady) return;

            string Eye(int s)
            {
                var e = _eyes[s];
                var r = e.RadiiPct * (_modelHeight / 100f);
                return $"{{\"on\":{(e.Set ? "true" : "false")},\"c\":[{Inv(e.Centre.X)},{Inv(e.Centre.Y)},{Inv(e.Centre.Z)}]," +
                       $"\"r\":[{Inv(r.X)},{Inv(r.Y)},{Inv(r.Z)}],\"f\":{Inv(e.FrontPct * _modelHeight / 100f)}}}";
            }

            float yaw = _sliderYaw.Value * MathF.PI / 180f, pitch = _sliderPitch.Value * MathF.PI / 180f;
            _lblGaze.Text = $"Test gaze: yaw {_sliderYaw.Value} deg, pitch {_sliderPitch.Value} deg";
            string json = $"{{\"eyes\":[{Eye(0)},{Eye(1)}],\"yaw\":{Inv(yaw)},\"pitch\":{Inv(pitch)}," +
                          $"\"pupilRadius\":{Inv(_style.PupilRadius)},\"maxOffset\":{Inv(_style.MaxOffset)}," +
                          $"\"fill\":{Inv(_style.Fill)},\"rimWidth\":{Inv(_style.RimWidth)}," +
                          $"\"irisRadius\":{Inv(_style.IrisRadius)}," +
                          $"\"irisColor\":[{Inv(_style.IrisColor.R / 255f)},{Inv(_style.IrisColor.G / 255f)},{Inv(_style.IrisColor.B / 255f)}]," +
                          $"\"rimStrength\":{Inv(_style.RimStrength)}," +
                          $"\"lidColor\":{LidColorJson()},\"lidMR\":{LidMRJson()},\"lidRest\":{Inv(_style.LidRest)},\"lowerRest\":{Inv(_style.LowerRest)}," +
                          $"\"lashWidth\":{Inv(_style.LashWidth)},\"lashStrength\":{Inv(_style.LashStrength)},\"cleanup\":{Inv(_style.Cleanup)}," +
                          $"\"limitAbove\":{Inv(_style.LimitAbove)},\"limitBelow\":{Inv(_style.LimitBelow)}," +
                          $"\"tilt\":{Inv(_style.Tilt * MathF.PI / 180f)}," +
                          $"\"autoBlink\":{(_chkAutoBlink.Checked ? "true" : "false")}," +
                          $"\"testClosure\":{Inv(_sliderClosure.Value / 100f)}," +
                          $"\"showRegion\":{(_chkShowRegion.Checked ? "true" : "false")},\"selected\":{Side}}}";
            _webView.CoreWebView2.ExecuteScriptAsync($"setEyes({json});");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ViewerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return; }

            if (message == null || IsDisposed) return;

            // The page has the model in and its shaders patched: send the current state.
            if (message.Action == "loaded") PushEyes();

            if (message.Action == "eyeClicked" && message.P is { Length: 3 } p)
                PlaceSelectedEye(new Vector3(p[0], p[1], p[2]));

            if (message.Action == "colorPicked" && message.Rgb is { Length: 3 } rgb)
            {
                if (message.Mr is { Length: 2 } mr)
                    (_style.LidRoughness, _style.LidMetalness) = (Math.Clamp(mr[0], 0f, 1f), Math.Clamp(mr[1], 0f, 1f));
                SetLidColor(Color.FromArgb(ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2])));
            }
        }

        private static int ToByte(float unit) => (int)Math.Round(Math.Clamp(unit, 0f, 1f) * 255f);

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public float[]? P { get; set; }
            public float[]? Rgb { get; set; }   // colorPicked: the texture's sRGB under the click
            public float[]? Mr { get; set; }    // colorPicked: roughness, metalness there
        }

        // ---- Lids -------------------------------------------------------------------------

        private string LidColorJson() => _style.LidColor is Color c
            ? $"[{Inv(c.R / 255f)},{Inv(c.G / 255f)},{Inv(c.B / 255f)}]"
            : "null";

        private string LidMRJson() => _style.LidRoughness is float r && _style.LidMetalness is float m
            ? $"[{Inv(r)},{Inv(m)}]"
            : "null";

        private void ShowLidColor()
        {
            _btnLidColor.BackColor = _style.LidColor ?? _swatchDefault;
            _btnLidColor.Text = _style.LidColor == null ? "None - pick..." : "Pick...";
        }

        private void SetLidColor(Color? color)
        {
            _style.LidColor = color;
            ShowLidColor();
            _lblStatus.Text = color == null ? "Lid colour cleared - no lids in game." : "Lid colour set.";
            PushEyes();
        }

        private void PickLidColor()
        {
            using var dialog = new ColorDialog { Color = _style.LidColor ?? Color.FromArgb(205, 140, 100), FullOpen = true, AnyColor = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) SetLidColor(dialog.Color);
        }

        // The next click in the preview samples the texture there instead of placing an eye.
        private void StartLidEyedropper()
        {
            if (!_viewerReady) return;
            _webView.CoreWebView2.ExecuteScriptAsync("setPickMode(true);");
            _lblStatus.Text = "Click the skin next to an eye to take the lid colour from it.";
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            try { await _webView.EnsureCoreWebView2Async(null); }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_eyes_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) => { _viewerReady = e.IsSuccess; };
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _model.SaveGLB(previewPath);
            _webView.CoreWebView2.NavigateToString(BuildViewerHtml(Path.GetFileName(previewPath)));
        }
    }
}
