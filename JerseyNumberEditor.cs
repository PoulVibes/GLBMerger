using System;
using System.Collections.Generic;
using System.Drawing;
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
using static GlbMerger.FaceAnchors;
using static GlbMerger.SkinRegionWeights;

namespace GlbMerger
{
    // Moves the players' jersey numbers out of the texture and into the game's shader, so the
    // rigid regions that kept the baked numbers from shearing (RigidRegionEditor) can go - they
    // were what made the torso deform stiffly. Three parts:
    //
    //   Anchors - "JerseyNumberFront" / "JerseyNumberBack" locator nodes under a spine joint
    //             (JerseyNumbers has the convention), with the model's number style in both
    //             anchors' extras: "number" (the model's own number; the game may deal another),
    //             "rigidity" (0..1, see the Viewer), "fill" / "outline" ([r,g,b] sRGB 0..1),
    //             "outlineWidth", "digitWidth" and "digitGap" (in digit-cell heights), and "front"
    //             (the slab's reach out of the body, as a fraction of its depth into it).
    //   Erase   - paints the baked numbers out of the albedo inside the anchors' slabs, in the
    //             jersey green round them, so the game's team recolour covers the patch.
    //   Smooth  - rebuilds the skin weights of single-joint (rigid) skin inside the slabs from the
    //             skin round it - the Rigid Region editor's "Remove Saved Rigid Region" over the
    //             numbers' area, without painting it.
    //
    // Click a number in the preview to centre the anchor on it; the numbers fine-tune it. The
    // preview runs the overlay the game is to draw (JerseyNumberEditor.Viewer.cs), with animation
    // playback so Rigidity can be judged moving.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window.
    public partial class JerseyNumberEditor : UserControl, IUnappliedChanges
    {
        // Starting size for a freshly clicked anchor, percent of the model's height: half usable
        // width, half cell height, depth into the body; out of the body. Front and back alike,
        // as tuned on Bob (Width / Height 17.7%) - the first model converted.
        private static readonly Vector3[] DefaultRadiiPct = { new(8.85f, 8.85f, 5f), new(8.85f, 8.85f, 5f) };
        private const float DefaultOutPct = 2.5f;

        private struct NumberStyle
        {
            public int Number;
            public float Rigidity;       // 0..1
            public Color Fill, Outline;  // sRGB
            public float OutlineWidth;   // digit-cell heights
            public float DigitWidth;     // x the atlas cell's own aspect
            public float DigitGap;       // digit-cell heights
        }
        // Bob's style - the first model converted - so every other player starts matching him.
        // FootballRoguelite JerseyNumberRig::Style falls back to the same values.
        private static readonly NumberStyle DefaultStyle = new()
        {
            Number = 12, Rigidity = 0.85f, Fill = Color.White, Outline = Color.FromArgb(83, 83, 83),
            OutlineWidth = 0.015f, DigitWidth = 1f, DigitGap = -0.37f,
        };

        private readonly HelpTips _help;
        private readonly ModelRoot _model;
        private readonly float _modelHeight;
        private readonly List<Node> _joints;
        private readonly string? _fontFolder;   // the game's Graphics/UI, served to the preview

        private readonly AnchorRegion[] _anchors = new AnchorRegion[2];
        private readonly AnchorRegion[] _savedAnchors = new AnchorRegion[2];
        private NumberStyle _style = DefaultStyle, _savedStyle = DefaultStyle;
        private Node? _spine, _savedSpine;

        // What Erase and Smooth have changed this session, for Revert.
        private readonly Dictionary<int, byte[]> _originalImages = new();
        private readonly SkinSnapshots _originalSkin = new();

        private ComboBox _spineDropdown = null!, _fontDropdown = null!, _animDropdown = null!;
        private RadioButton _radioFront = null!, _radioBack = null!;
        private NumericUpDown _numX = null!, _numY = null!, _numZ = null!;
        private NumericUpDown _numW = null!, _numH = null!, _numIn = null!, _numOut = null!;
        private NumericUpDown _numNumber = null!, _numRigidity = null!, _numDigitWidth = null!, _numDigitGap = null!, _numOutlineWidth = null!;
        private NumericUpDown _numReach = null!;
        private Button _btnFill = null!, _btnOutline = null!, _btnRevert = null!;
        private CheckBox _chkSameSize = null!, _chkShowRegion = null!, _chkShowRigid = null!, _chkPlaying = null!;
        private TrackBar _sliderScrub = null!;
        private Label _lblStatus = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _suppressEvents;
        private int _previewVersion;
        private string? _previewPath;

        public JerseyNumberEditor(ModelRoot model, bool darkMode, AppSettings settings)
        {
            _help = new HelpTips(this);
            _model = model;
            _modelHeight = MeasureModelHeight(model);
            _joints = JerseyNumbers.JointsSpineFirst(model);
            _spine = JerseyNumbers.DefaultSpine(model);
            _fontFolder = JerseyNumbers.FindGameFontFolder(settings.AnimationLibraryDirectory);

            Dock = DockStyle.Fill;
            LoadExistingAnchors();
            Array.Copy(_anchors, _savedAnchors, 2);
            _savedStyle = _style;
            _savedSpine = _spine;

            BuildUi();
            // Theme first: it repaints every button's background, which would wipe the colour swatches.
            ThemeManager.Apply(this, darkMode);
            RefreshUiFromState();

            _ = InitializeViewerAsync();
        }

        public bool HasUnappliedChanges =>
            !_anchors.SequenceEqual(_savedAnchors) || !_style.Equals(_savedStyle) || _spine != _savedSpine;
        public string UnappliedChangesDescription => "jersey number anchor edits not yet saved";

        private int Side => _radioBack.Checked ? 1 : 0;

        private List<JerseyNumbers.Slab> PlacedSlabs() => Enumerable.Range(0, 2)
            .Where(s => _anchors[s].Set)
            .Select(s => new JerseyNumbers.Slab(s, _anchors[s], _modelHeight))
            .ToList();

        // ---- Load / save ------------------------------------------------------------------

        private void LoadExistingAnchors()
        {
            for (int side = 0; side < 2; side++)
            {
                var node = FindAnchorNode(_model, JerseyNumbers.AnchorNames[side], includeUnused: false);
                if (node == null) continue;

                _anchors[side] = ReadRegion(_model, node, _modelHeight);
                if (node.VisualParent != null) _spine = node.VisualParent;
                ReadStyle(node);
            }
        }

        // Missing keys keep whatever is already set (the defaults, or the other anchor's).
        private void ReadStyle(Node node)
        {
            if (node.Extras is not JsonObject o) return;
            try
            {
                float number = _style.Number;
                ReadClamped(o, "number", ref number, 0f, 99f);
                _style.Number = (int)MathF.Round(number);
                ReadUnit(o, "rigidity", ref _style.Rigidity);
                ReadClamped(o, "outlineWidth", ref _style.OutlineWidth, 0f, 0.12f);
                ReadClamped(o, "digitWidth", ref _style.DigitWidth, 0.3f, 2f);
                ReadClamped(o, "digitGap", ref _style.DigitGap, -0.5f, 0.5f);
                _style.Fill = ReadColor(o, "fill") ?? _style.Fill;
                _style.Outline = ReadColor(o, "outline") ?? _style.Outline;
            }
            catch (InvalidOperationException) { }
            catch (FormatException) { }
        }

        private JsonObject StyleExtras() => new()
        {
            ["number"] = _style.Number,
            ["rigidity"] = Math.Round(_style.Rigidity, 3),
            ["fill"] = ColorJson(_style.Fill),
            ["outline"] = ColorJson(_style.Outline),
            ["outlineWidth"] = Math.Round(_style.OutlineWidth, 4),
            ["digitWidth"] = Math.Round(_style.DigitWidth, 3),
            ["digitGap"] = Math.Round(_style.DigitGap, 3),
        };

        private void SaveAnchors()
        {
            if (_spine == null)
            {
                MessageBox.Show(this, "Pick the spine joint to parent the number anchors to.", "Jersey Numbers",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int saved = 0;
            for (int side = 0; side < 2; side++)
            {
                string name = JerseyNumbers.AnchorNames[side];
                if (!_anchors[side].Set) { Retire(FindAnchorNode(_model, name, includeUnused: true), name); continue; }

                var node = JerseyNumbers.WriteAnchor(_model, _spine, side, _anchors[side], _modelHeight);
                if (node == null) continue;
                var extras = StyleExtras();
                extras["front"] = FrontRatio(_anchors[side]);
                node.Extras = extras;
                saved++;
            }

            Array.Copy(_anchors, _savedAnchors, 2);
            _savedStyle = _style;
            _savedSpine = _spine;
            _lblStatus.Text = $"Saved {saved} number anchor(s) under \"{_spine.Name}\". Save the GLB to keep them.";
        }

        // ---- Erase / smooth / revert -------------------------------------------------------

        private void EraseBakedNumbers()
        {
            var slabs = PlacedSlabs();
            if (slabs.Count == 0) { _lblStatus.Text = "Place a number anchor over each baked number first."; return; }

            var results = JerseyNumbers.EraseBakedNumbers(_model, slabs);
            var notes = new List<string>();
            int texels = 0;
            foreach (var r in results)
            {
                if (r.Problem != null) { notes.Add($"image {r.ImageIndex}: {r.Problem}"); continue; }
                if (r.Png == null || r.Texels == 0) continue;
                foreach (var (imageIndex, png) in r.Others.Append((r.ImageIndex, r.Png)))
                {
                    if (!_originalImages.ContainsKey(imageIndex))
                        _originalImages[imageIndex] = TextureAtlasUtil.SnapshotContent(_model, imageIndex);
                    TextureAtlasUtil.Apply(_model, imageIndex, png);
                }
                texels += r.Texels;
                if (r.Fill is var (fr, fg, fb)) notes.Add($"image {r.ImageIndex} filled with RGB {fr},{fg},{fb}");
            }

            _lblStatus.Text = texels > 0
                ? $"Erased {texels:N0} texel(s) ({string.Join("; ", notes)}). Included the next time you save."
                : "Nothing erased" + (notes.Count > 0 ? $": {string.Join("; ", notes)}." : " - no textured faces inside the number boxes.");
            if (texels > 0) AfterModelEdit();
        }

        private void SmoothRigidSkin()
        {
            var slabs = PlacedSlabs();
            if (slabs.Count == 0) { _lblStatus.Text = "Place the number anchors first - the boxes say where to smooth."; return; }

            var selection = JerseyNumbers.RigidTrianglesIn(_model, slabs, (float)_numReach.Value / 100f);
            if (selection.Count == 0) { _lblStatus.Text = "No single-joint skin inside the (grown) number boxes - nothing to smooth."; return; }

            int rebuilt = 0, skipped = 0;
            foreach (int meshIdx in selection.Keys.Select(k => k.MeshIndex).Distinct())
                rebuilt += RebuildWeightsInMesh(_model.LogicalMeshes[meshIdx], meshIdx, selection, 2, _originalSkin, ref skipped);

            _lblStatus.Text = rebuilt > 0
                ? $"Smoothed: {rebuilt:N0} vertex weight(s) rebuilt from the skin round them. Included the next time you save."
                : "Nothing smoothed - the rigid skin there has no unpinned skin round it to rebuild from. Raise Reach.";
            if (rebuilt > 0) AfterModelEdit();
        }

        private void RevertEdits()
        {
            foreach (var (imageIndex, bytes) in _originalImages)
                TextureAtlasUtil.Apply(_model, imageIndex, bytes);
            _originalImages.Clear();

            foreach (var (jointsAcc, (weightsAcc, origJoints, origWeights)) in _originalSkin)
            {
                var joints = jointsAcc.AsVector4Array();
                var weights = weightsAcc.AsVector4Array();
                for (int i = 0; i < origJoints.Length; i++) { joints[i] = origJoints[i]; weights[i] = origWeights[i]; }
            }
            _originalSkin.Clear();

            _lblStatus.Text = "Reverted - the texture and skin weights are back as they were.";
            AfterModelEdit();
        }

        private void AfterModelEdit()
        {
            _btnRevert.Enabled = _originalImages.Count > 0 || _originalSkin.Count > 0;
            ReloadPreview();
        }

        // ---- Editing ----------------------------------------------------------------------

        private void PlaceAnchor(Vector3 meshPoint, bool back)
        {
            int side = back ? 1 : 0;
            if (Side != side) { _suppressEvents = true; (back ? _radioBack : _radioFront).Checked = true; _suppressEvents = false; }
            var size = _anchors[side].Set ? _anchors[side]
                     : _chkSameSize.Checked && _anchors[1 - side].Set ? _anchors[1 - side]
                     : new AnchorRegion { RadiiPct = DefaultRadiiPct[side], FrontPct = DefaultOutPct };
            _anchors[side] = new AnchorRegion { Set = true, Centre = meshPoint, RadiiPct = size.RadiiPct, FrontPct = size.FrontPct };
            RefreshUiFromState();
            PushState();
        }

        private void OnAnchorNumbersChanged()
        {
            if (_suppressEvents) return;
            int side = Side;
            if (!_anchors[side].Set) return;
            var radii = new Vector3((float)_numW.Value / 2f, (float)_numH.Value / 2f, (float)_numIn.Value);
            _anchors[side].Centre = new Vector3((float)_numX.Value, (float)_numY.Value, (float)_numZ.Value);
            _anchors[side].RadiiPct = radii;
            _anchors[side].FrontPct = Math.Min((float)_numOut.Value, radii.Z);
            if (_chkSameSize.Checked) CopySizeToOtherSide();
            PushState();
        }

        // The box size only: the two numbers still sit wherever each was placed.
        private void CopySizeToOtherSide()
        {
            int side = Side;
            if (!_anchors[side].Set || !_anchors[1 - side].Set) return;
            _anchors[1 - side].RadiiPct = _anchors[side].RadiiPct;
            _anchors[1 - side].FrontPct = _anchors[side].FrontPct;
        }

        private void OnSameSizeChanged()
        {
            if (!_chkSameSize.Checked) return;
            CopySizeToOtherSide();
            PushState();
        }

        private void OnStyleChanged()
        {
            if (_suppressEvents) return;
            _style.Number = (int)_numNumber.Value;
            _style.Rigidity = (float)_numRigidity.Value / 100f;
            _style.DigitWidth = (float)_numDigitWidth.Value;
            _style.DigitGap = (float)_numDigitGap.Value;
            _style.OutlineWidth = (float)_numOutlineWidth.Value;
            PushState();
        }

        private void OnSpineChanged()
        {
            if (_suppressEvents) return;
            _spine = _spineDropdown.SelectedIndex >= 0 ? _joints[_spineDropdown.SelectedIndex] : null;
            PushState();
        }

        private void PickColor(bool outline)
        {
            using var dialog = new ColorDialog { Color = outline ? _style.Outline : _style.Fill, FullOpen = true, AnyColor = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            if (outline) _style.Outline = dialog.Color; else _style.Fill = dialog.Color;
            RefreshUiFromState();
            PushState();
        }

        private void ClearSelectedAnchor()
        {
            _anchors[Side] = default;
            RefreshUiFromState();
            PushState();
        }

        private void RefreshUiFromState()
        {
            _suppressEvents = true;
            try
            {
                var a = _anchors[Side];
                foreach (var n in new[] { _numX, _numY, _numZ, _numW, _numH, _numIn, _numOut }) n.Enabled = a.Set;
                if (a.Set)
                {
                    SetClamped(_numX, a.Centre.X); SetClamped(_numY, a.Centre.Y); SetClamped(_numZ, a.Centre.Z);
                    SetClamped(_numW, a.RadiiPct.X * 2f); SetClamped(_numH, a.RadiiPct.Y * 2f);
                    SetClamped(_numIn, a.RadiiPct.Z); SetClamped(_numOut, a.FrontPct);
                }

                _spineDropdown.SelectedIndex = _spine == null ? -1 : _joints.IndexOf(_spine);
                SetClamped(_numNumber, _style.Number);
                SetClamped(_numRigidity, _style.Rigidity * 100f);
                SetClamped(_numDigitWidth, _style.DigitWidth);
                SetClamped(_numDigitGap, _style.DigitGap);
                SetClamped(_numOutlineWidth, _style.OutlineWidth);
                _btnFill.BackColor = _style.Fill;
                _btnOutline.BackColor = _style.Outline;
                _btnRevert.Enabled = _originalImages.Count > 0 || _originalSkin.Count > 0;

                string State(int s) => _anchors[s].Set ? "placed" : "not placed";
                _lblStatus.Text = _joints.Count == 0
                    ? "No skin joints in this model - nothing to anchor the numbers to."
                    : $"Front: {State(0)}   Back: {State(1)}";
            }
            finally { _suppressEvents = false; }
        }

        // ---- Viewer bridge ----------------------------------------------------------------

        private void PushState()
        {
            if (!_viewerReady) return;

            string Anchor(int s)
            {
                var a = _anchors[s];
                var r = a.RadiiPct * (_modelHeight / 100f);
                return $"{{\"on\":{(a.Set ? "true" : "false")},\"c\":[{Inv(a.Centre.X)},{Inv(a.Centre.Y)},{Inv(a.Centre.Z)}]," +
                       $"\"r\":[{Inv(r.X)},{Inv(r.Y)},{Inv(r.Z)}],\"f\":{Inv(a.FrontPct * _modelHeight / 100f)}}}";
            }

            string anim = _animDropdown.SelectedIndex > 0 ? JsonSerializer.Serialize((string)_animDropdown.SelectedItem!) : "null";
            string json = $"{{\"anchors\":[{Anchor(0)},{Anchor(1)}]," +
                          $"\"spine\":{JsonSerializer.Serialize(_spine?.Name)}," +
                          $"\"number\":{_style.Number},\"rigidity\":{Inv(_style.Rigidity)}," +
                          $"\"fill\":{ColorJs(_style.Fill)},\"outline\":{ColorJs(_style.Outline)}," +
                          $"\"outlineWidth\":{Inv(_style.OutlineWidth)},\"digitWidth\":{Inv(_style.DigitWidth)}," +
                          $"\"digitGap\":{Inv(_style.DigitGap)}," +
                          $"\"font\":{JsonSerializer.Serialize(FontFamily())},\"fontUrl\":{FontUrl()}," +
                          $"\"showRegion\":{(_chkShowRegion.Checked ? "true" : "false")}," +
                          $"\"showRigid\":{(_chkShowRigid.Checked ? "true" : "false")}," +
                          $"\"anim\":{anim},\"playing\":{(_chkPlaying.Checked ? "true" : "false")}," +
                          $"\"time\":{Inv(_sliderScrub.Value / 1000f)}}}";
            _webView.CoreWebView2.ExecuteScriptAsync($"setState({json});");
        }

        // A game font goes to the page as a family name (its file name, less the extension) and the
        // URL it is served at; a Windows stand-in by name alone.
        private string FontFamily() => Path.GetFileNameWithoutExtension((string)_fontDropdown.SelectedItem!);

        private string FontUrl() => _fontFolder == null
            ? "null"
            : JsonSerializer.Serialize("https://gamefonts.local/" + Uri.EscapeDataString((string)_fontDropdown.SelectedItem!));

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

            if (message.Action == "loaded") PushState();

            if (message.Action == "placed" && message.P is { Length: 3 } p)
                PlaceAnchor(new Vector3(p[0], p[1], p[2]), message.Back);

            if (message.Action == "atlas" && message.Png is string dataUrl)
                SaveAtlas(dataUrl);
        }

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public float[]? P { get; set; }
            public bool Back { get; set; }
            public string? Png { get; set; }   // atlas: a PNG data URL
        }

        private void SaveAtlas(string dataUrl)
        {
            int comma = dataUrl.IndexOf(',');
            if (comma < 0) return;
            using var dialog = new SaveFileDialog
            {
                Title = "Save Digit Atlas", Filter = "PNG image (*.png)|*.png", FileName = "JerseyDigits.png",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(dataUrl.Substring(comma + 1)));
            _lblStatus.Text = $"Digit atlas saved to {dialog.FileName} ({JerseyNumbers.CellW * 10}x{JerseyNumbers.CellH}, " +
                              $"SDF in red, {JerseyNumbers.SdfSpread}px spread).";
        }

        private async Task InitializeViewerAsync()
        {
            try { await _webView.EnsureCoreWebView2Async(null); }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            if (_fontFolder != null)
                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("gamefonts.local", _fontFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) => { _viewerReady = e.IsSuccess; };
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ReloadPreview();
        }

        // Erase and Smooth change the model itself, so the page reloads it - a fresh file name each
        // time, so the browser can't serve the previous one from its cache.
        private void ReloadPreview()
        {
            if (_webView.CoreWebView2 == null) return;
            _viewerReady = false;
            string old = _previewPath ?? "";
            _previewPath = Path.Combine(Path.GetTempPath(), $"glbmerger_jersey_preview_{++_previewVersion}.glb");
            _model.SaveGLB(_previewPath);
            _webView.CoreWebView2.NavigateToString(BuildViewerHtml(Path.GetFileName(_previewPath)));
            try { if (File.Exists(old)) File.Delete(old); } catch (IOException) { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                try { if (_previewPath != null && File.Exists(_previewPath)) File.Delete(_previewPath); } catch (IOException) { }
            base.Dispose(disposing);
        }
    }
}
