using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;
using static GlbMerger.FaceAnchors;

namespace GlbMerger
{
    // Brow, mouth and chin bones (FootballRoguelite FaceBones.h). Place a region round each painted brow
    // and round the mouth - a brow's a little taller than the brow, so it has room to move - and one
    // from the lips down past the bottom of the chin, which stretches down as the mouth opens. Save:
    // FaceBoneRigger adds the bones under the head and weights the face inside each region to them,
    // full in the middle (Weight core) and fading out at the edge. The game then moves those bones by
    // expression, which moves the actual painted features. The preview reloads with the new weights,
    // can tint them, and plays the expression presets on the bones exactly as the game does.
    //
    // The regions themselves are kept as "BrowAnchorL" / "BrowAnchorR" / "MouthAnchor" / "ChinAnchor"
    // nodes under the head (the eye anchors' convention; extras "front", "core", and the chin's "drop")
    // so the next session can edit and
    // re-weight them. The mouth's also carries the open-mouth stencil (FaceBonesEditor.Mouth.cs).
    // Re-saving is safe: the old face weights are stripped before the new ones go in.
    public partial class FaceBonesEditor : UserControl, IUnappliedChanges
    {
        private static readonly string[] AnchorNames = { "BrowAnchorL", "BrowAnchorR", "MouthAnchor", "ChinAnchor" };
        private static readonly string[] FeatureNames = { "BrowL", "BrowR", "Mouth", "Chin" };
        private static readonly string[] FeatureLabels = { "Left Brow", "Right Brow", "Mouth", "Chin" };
        private const int Mouth = 2, Chin = 3;
        private static int FeatureCount => AnchorNames.Length;

        // Starting region for a freshly clicked feature, percent of model height.
        private static readonly Vector3 DefaultBrowPct = new(3.0f, 2.2f, 3.0f);
        private static readonly Vector3 DefaultMouthPct = new(4.0f, 2.0f, 3.0f);
        private static readonly Vector3 DefaultChinPct = new(4.0f, 3.0f, 3.0f);
        private const float DefaultCore = 0.4f;
        private const float DefaultChinDrop = 0.5f;   // x the chin region's half-height, at a full open

        private readonly AnchorRegion[] _regions = new AnchorRegion[4];
        private readonly float[] _core = { DefaultCore, DefaultCore, DefaultCore, DefaultCore };
        private float _chinDrop = DefaultChinDrop;
        private readonly float[] _browTilt = new float[2];   // degrees; positive raises the region's outer end
        private bool _dirty;

        private readonly HelpTips _help;
        private readonly ModelRoot _model;
        private readonly Node? _head;
        private readonly Matrix4x4 _meshToHead;
        private readonly float _modelHeight;

        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _suppressEvents;

        public FaceBonesEditor(ModelRoot model, bool darkMode = false)
        {
            _help = new HelpTips(this);
            _model = model;
            _head = FindHead(model);
            _meshToHead = MeshToNode(model, _head);
            _modelHeight = MeasureModelHeight(model);

            Dock = DockStyle.Fill;
            LoadExisting();

            BuildUi();
            ThemeManager.Apply(this, darkMode);
            RefreshUiFromState();

            _ = InitializeViewerAsync();
        }

        public bool HasUnappliedChanges => _dirty;
        public string UnappliedChangesDescription => "brow / mouth / chin regions not yet saved";

        private int Selected => _radioFeature.FindIndex(r => r.Checked) is int i && i >= 0 ? i : 0;

        // ---- Load / save ------------------------------------------------------------------

        private void LoadExisting()
        {
            for (int f = 0; f < AnchorNames.Length; f++)
            {
                var node = FindAnchorNode(_model, AnchorNames[f], includeUnused: false);
                if (node == null) continue;
                _regions[f] = ReadRegion(_model, node, _modelHeight);
                if (node.Extras is not JsonObject o) continue;
                ReadUnit(o, "core", ref _core[f]);
                if (f == Mouth) { ReadMouthStyle(o); ReadMouthPoints(o); }
                if (f == Chin) ReadClamped(o, "drop", ref _chinDrop, 0f, 2f);
                if (f < Mouth) ReadClamped(o, "tilt", ref _browTilt[f], -60f, 60f);
            }
        }

        private void Save()
        {
            if (_head == null)
            {
                MessageBox.Show(this, "No \"head\" bone found - nowhere to put the face bones.", "Brows & Mouth",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            for (int f = 0; f < AnchorNames.Length; f++)
            {
                var existing = FindAnchorNode(_model, AnchorNames[f], includeUnused: true);
                if (!_regions[f].Set) { Retire(existing, AnchorNames[f]); continue; }
                var node = WriteRegion(_head, _meshToHead, existing, AnchorNames[f], _regions[f], _modelHeight);
                if (node == null) continue;
                var extras = new JsonObject { ["front"] = FrontRatio(_regions[f]), ["core"] = Math.Round(_core[f], 3) };
                if (f == Mouth) { WriteMouthStyle(extras); WriteMouthPoints(extras); }
                if (f == Chin) extras["drop"] = Math.Round(_chinDrop, 3);
                if (f < Mouth) extras["tilt"] = Math.Round(_browTilt[f], 2);
                node.Extras = extras;
            }

            var features = Enumerable.Range(0, FeatureCount)
                .Select(f => new FaceBoneRigger.Feature
                {
                    Name = FeatureNames[f], Region = _regions[f], Core = _core[f], Drop = f == Chin ? _chinDrop : 0f,
                    MouthPoints = f == Mouth ? MouthPointsForRig() : null,
                    TiltDeg = f < Mouth ? _browTilt[f] : 0f,
                    ChinStartY = f == Chin ? MouthOpenBottomY() : null,
                    SculptedLips = f == Mouth && _mouth.Enabled && _mouth.SculptedLips,
                })
                .ToList();
            string result = FaceBoneRigger.Apply(_model, _head, _meshToHead, _modelHeight, features);

            _dirty = false;
            _lblStatus.Text = result + " Save the GLB to keep them.";
            ReloadPreview();
        }

        // ---- Editing ----------------------------------------------------------------------

        private void PlaceSelected(Vector3 meshPoint)
        {
            int f = Selected;
            if (f == Mouth && HasMouthPoints)
            {
                _lblStatus.Text = "The mouth is placed by its points: drag a dot, or Pick Mouth Points to redo them.";
                return;
            }
            if (!_regions[f].Set)
            {
                var other = f < Mouth ? _regions[1 - f] : default;
                var size = other.Set ? other.RadiiPct : f == Mouth ? DefaultMouthPct : f == Chin ? DefaultChinPct : DefaultBrowPct;
                _regions[f] = new AnchorRegion { RadiiPct = size, FrontPct = other.Set ? other.FrontPct : size.Z };
            }
            _regions[f].Set = true;
            _regions[f].Centre = meshPoint;
            _dirty = true;
            RefreshUiFromState();
            PushState();
        }

        private void ClearSelected()
        {
            _regions[Selected] = default;
            if (Selected == Mouth) { _mouthPoints.Clear(); _pickingMouthPoints = false; }
            _dirty = true;
            RefreshUiFromState();
            PushState();
        }

        // ---- Viewer bridge ----------------------------------------------------------------

        private string RegionJson(int f)
        {
            var g = _regions[f];
            var r = g.RadiiPct * (_modelHeight / 100f);
            return $"{{\"on\":{(g.Set ? "true" : "false")},\"c\":[{Inv(g.Centre.X)},{Inv(g.Centre.Y)},{Inv(g.Centre.Z)}]," +
                   $"\"r\":[{Inv(r.X)},{Inv(r.Y)},{Inv(r.Z)}],\"f\":{Inv(g.FrontPct * _modelHeight / 100f)}," +
                   $"\"t\":{Inv(RegionTurn(f))},\"core\":{Inv(_core[f])}}}";
        }

        // A brow region's tilt as a turn about z, radians: positive tilt raises the end away from the
        // other brow, as FaceBoneRigger turns it.
        private float RegionTurn(int f)
        {
            if (f >= Mouth || !_regions[f].Set) return 0f;
            float midline = _regions[0].Set && _regions[1].Set ? 0.5f * (_regions[0].Centre.X + _regions[1].Centre.X) : 0f;
            float outer = _regions[f].Centre.X >= midline ? 1f : -1f;
            return outer * _browTilt[f] * MathF.PI / 180f;
        }

        private void PushState()
        {
            if (!_viewerReady) return;
            string json = $"{{\"regions\":[{string.Join(",", Enumerable.Range(0, FeatureCount).Select(RegionJson))}]," +
                          $"\"preset\":{Math.Max(_comboExpression.SelectedIndex, 0)},\"viseme\":{_comboViseme.SelectedIndex - 1}," +
                          $"\"strength\":{Inv(_sliderStrength.Value / 100f)}," +
                          $"\"showRegion\":{(_chkShowRegion.Checked ? "true" : "false")}," +
                          $"\"showWeights\":{(_chkShowWeights.Checked ? "true" : "false")},\"selected\":{Selected}," +
                          $"\"showPoints\":{(_chkShowPoints.Checked ? "true" : "false")}," +
                          $"\"chinDrop\":{Inv(_chinDrop)},\"mouth\":{MouthJson()}," +
                          $"\"mouthPoints\":{MouthPointsJson()},\"pickPoints\":{(_pickingMouthPoints ? "true" : "false")}}}";
            _webView.CoreWebView2.ExecuteScriptAsync($"setFace({json});");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ViewerMessage? m;
            try
            {
                m = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { return; }
            if (m == null || IsDisposed) return;

            if (m.Action == "loaded")
            {
                PushState();
                FillSkinIfMissing();
                if (m.Bones is int bones)
                    _lblBones.Text = bones > 0 ? $"Preview: {bones} face bone(s) on this model." : "Preview: no face bones yet - Save to add them.";
            }
            if (m.Action == "clicked" && m.P is { Length: 3 } p) PlaceSelected(new Vector3(p[0], p[1], p[2]));
            if (m.Action == "mouthPoint" && m.P is { Length: 3 } mp) OnMouthPointClicked(new Vector3(mp[0], mp[1], mp[2]));
            if (m.Action == "mouthPointMoved" && m.P is { Length: 3 } mv && m.I is int i)
                OnMouthPointMoved(i, new Vector3(mv[0], mv[1], mv[2]));
            if (m.Action == "colorPicked" && m.Rgb is { Length: 3 } rgb) OnMouthColorPicked(m.Target, rgb, m.Mr, m.MrOnly == true);
        }

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public float[]? P { get; set; }    // clicked, mouthPoint, mouthPointMoved: mesh point
            public int? I { get; set; }        // mouthPointMoved: which point
            public int? Bones { get; set; }    // loaded: face bones found
            public string? Target { get; set; } // colorPicked: which mouth colour
            public float[]? Rgb { get; set; }   // colorPicked: the texture's sRGB under the click
            public float[]? Mr { get; set; }    // colorPicked: roughness, metalness there
            public bool? MrOnly { get; set; }   // colorPicked: only the roughness / metalness wanted
        }

        private string _previewPath = "";

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            try { await _webView.EnsureCoreWebView2Async(null); }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            _previewPath = Path.Combine(tempFolder, "glbmerger_face_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) => { _viewerReady = e.IsSuccess; };
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ReloadPreview();
        }

        // The page loads the model as it is now - with whatever bones and weights it has.
        private void ReloadPreview()
        {
            if (_webView.CoreWebView2 == null || string.IsNullOrEmpty(_previewPath)) return;
            _viewerReady = false;
            _model.SaveGLB(_previewPath);
            // Cache-busted: the file name doesn't change between saves, its bones and weights do.
            string url = Path.GetFileName(_previewPath) + "?v=" + DateTime.Now.Ticks;
            _webView.CoreWebView2.NavigateToString(FacePreviewPage.Build(url,
                "Click a brow or the mouth to place the selected region. Drag to orbit, wheel to zoom.", ViewerScript));
        }
    }
}
