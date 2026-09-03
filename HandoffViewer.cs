using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Read-only preview of the ball-handoff pose: the arm extended straight
    // ahead with the football still in that hand. Nothing here writes to the
    // model - there is no Save, and no marker is created - it exists purely
    // to see what the game will draw for a handoff, built out of data the
    // other editors already authored.
    //
    // What the game does (FootballRoguelite, RendererDraw::DrawOnePlayer's
    // handoff arm-reach block plus ModelRenderer::UpdateInstance):
    //   - The GIVER reaches with the hand that is already holding the ball,
    //     so that hand starts from its ball-carry lock (ArmIKConfig::right/
    //     left, baked as the BallCarry* markers BallAnchorEditor writes) and
    //     is blended TOWARD the reach pose by handoffReachWeight, rather than
    //     the reach replacing the lock outright - the hand never lets go of
    //     the ball mid-ease (see ModelRenderer.cpp's "blend into existing
    //     lock" branch).
    //   - The reach pose itself comes from ArmIKConfig::rightHandoff/
    //     leftHandoff, blended Forward->Side by the aim's yaw and then
    //     ->Down by its pitch (MakeStiffArmLock). In game the aim points at
    //     the other player; here it is pinned straight ahead, which is
    //     exactly yawFrac = pitchFrac = 0, i.e. the Forward pose alone. That
    //     is why there is no aim control below - straight ahead needs no
    //     blend.
    //   - The ball keeps riding the hand's own BallAnchor offset the whole
    //     time (ModelRenderer::HandWorld), which is what this reuses for the
    //     ball's placement instead of inventing a separate handoff anchor.
    //
    // The game has no baked-marker path for rightHandoff/leftHandoff yet
    // (ApplyBakedStiffArmPoses only fills rightStiff/leftStiff), so the
    // reach pose is sourced in that same order of preference: this model's
    // saved StiffArmForward* markers if it has them - the handoff poses are
    // seeded from the stiff-arm ones on the game side - otherwise the
    // hardcoded ArmIKConfig::rightHandoff/leftHandoff defaults. The status
    // panel says which of the two is on screen, so a model that has never
    // been through the Stiff Arm Poses editor is obviously showing generic
    // defaults rather than its own tuning.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there),
    // which owns the window chrome, so this control only contributes its own
    // left-hand controls and 3D preview.
    public class HandoffViewer : UserControl
    {
        private readonly ModelRoot _model;

        private readonly bool _darkMode;

        private ComboBox _animDropdown = null!;
        private RadioButton _radioRight = null!, _radioLeft = null!;
        private TrackBar _reachSlider = null!;
        private Label _lblReach = null!, _lblSource = null!, _lblChains = null!, _lblAnchor = null!;
        private Label _lblViewOnly = null!;
        private Button _btnPause = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;

        private readonly Node? _rightHand, _leftHand;
        private readonly Node? _rightElbow, _leftElbow;
        private readonly Node? _rightShoulder, _leftShoulder;

        private static readonly string[] RightNames = { "righthand", "hand_r", "r_hand", "hand.r",
            "wrist_r", "mixamorig:righthand", "handr", "rwrist" };
        private static readonly string[] LeftNames = { "lefthand", "hand_l", "l_hand", "hand.l",
            "wrist_l", "mixamorig:lefthand", "handl", "lwrist" };
        private static readonly string[] WeakNames = { "hand", "wrist", "palm" };

        private static readonly string[] JointNames = { "Shoulder", "Elbow", "Wrist" };

        // Degrees, matching ModelRenderer::ArmPoseConfig's three joints. Float
        // rather than the int the editing modes use - nothing here is bound to
        // an integer slider, and the hardcoded game defaults below are
        // fractional, so rounding them would show a subtly different pose than
        // the game's own.
        private struct ArmPose
        {
            public Vector3 Shoulder, Elbow, Wrist;

            public ArmPose With(int joint, Vector3 v)
            {
                var p = this;
                if (joint == 0) p.Shoulder = v;
                else if (joint == 1) p.Elbow = v;
                else p.Wrist = v;
                return p;
            }
        }

        // ArmIKConfig::right / ::left (ModelRenderer.h) - the fallback carry
        // hold for a model with no BallCarry* markers saved.
        private static readonly ArmPose DefaultCarryRight = new ArmPose
        {
            Shoulder = new Vector3(63.22f, 48.5f, -32.6f),
            Elbow = new Vector3(23.3f, -37.5f, -16.6f),
            Wrist = new Vector3(0f, -90f, 0f)
        };
        private static readonly ArmPose DefaultCarryLeft = new ArmPose
        {
            Shoulder = new Vector3(60f, -22.5f, 44f),
            Elbow = new Vector3(4.5f, -4f, 25f),
            Wrist = new Vector3(0f, 90f, 0f)
        };

        // ArmIKConfig::rightHandoff.forward / leftHandoff.forward
        // (ModelRenderer.h) - the fallback straight-ahead reach.
        private static readonly ArmPose DefaultReachRight = new ArmPose
        {
            Shoulder = new Vector3(-11.3f, -47.3f, -60f),
            Elbow = new Vector3(0f, -47.5f, 0f),
            Wrist = new Vector3(17.8f, 39f, 40f)
        };
        private static readonly ArmPose DefaultReachLeft = new ArmPose
        {
            Shoulder = new Vector3(-16.9f, 47.3f, 60f),
            Elbow = new Vector3(0f, 47.5f, 0f),
            Wrist = new Vector3(17.8f, -39f, -40f)
        };

        // [side] 0 = right, 1 = left. Resolved once at construction - none of
        // it can change while this control is alive, since nothing here edits.
        private readonly ArmPose[] _carry = new ArmPose[2];
        private readonly ArmPose[] _reach = new ArmPose[2];
        private readonly bool[] _carryFromModel = new bool[2];
        private readonly bool[] _reachFromModel = new bool[2];
        private readonly (Vector3 Pos, Quaternion Rot)[] _anchor = new (Vector3, Quaternion)[2];
        private readonly bool[] _anchorFound = new bool[2];

        public HandoffViewer(ModelRoot model, bool darkMode = false)
        {
            _model = model;
            _darkMode = darkMode;
            _rightHand = FindHandNode(_model, right: true);
            _leftHand = FindHandNode(_model, right: false);
            _rightElbow = _rightHand?.VisualParent;
            _leftElbow = _leftHand?.VisualParent;
            _rightShoulder = _rightElbow?.VisualParent;
            _leftShoulder = _leftElbow?.VisualParent;

            Dock = DockStyle.Fill;

            LoadPoses();
            LoadAnchors();

            BuildUi();
            PopulateAnimationList();

            ThemeManager.Apply(this, darkMode);

            // After theming, not before: ThemeManager repaints every Label's
            // ForeColor wholesale, so the two warning-colored labels have to
            // be coloured on this side of it or they'd only turn the right
            // colour the first time RefreshStatusLabels ran again (on a hand
            // switch), which reads as the panel changing colour on its own.
            RefreshStatusLabels();

            _ = InitializeViewerAsync();
        }

        // Same alias-matching convention ModelRenderer::FindHandJoints uses,
        // so this resolves the exact bones the game will pose and hang the
        // ball off of.
        private static Node? FindHandNode(ModelRoot model, bool right)
        {
            Node? Search(string[] names)
            {
                foreach (var n in model.LogicalNodes)
                {
                    if (string.IsNullOrEmpty(n.Name)) continue;
                    var lower = n.Name.ToLowerInvariant();
                    foreach (var key in names)
                        if (lower.Contains(key)) return n;
                }
                return null;
            }
            return right ? (Search(RightNames) ?? Search(WeakNames)) : Search(LeftNames);
        }

        private static Node? FindChildByName(Node? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.VisualChildren)
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            return null;
        }

        // Marker for joint J is a SIBLING of the real joint it overrides,
        // under that joint's own real parent - the convention every editor
        // that writes arm poses uses, and the one ApplyBakedCarryPoses /
        // ApplyBakedStiffArmPoses read back on the game side.
        private Node? MarkerParentFor(bool right, int joint)
        {
            var shoulder = right ? _rightShoulder : _leftShoulder;
            var elbow = right ? _rightElbow : _leftElbow;
            return joint switch
            {
                0 => shoulder?.VisualParent,
                1 => shoulder,
                2 => elbow,
                _ => null
            };
        }

        // Pulls one three-joint pose out of markers named
        // <prefix><Joint><R|L>. Returns false (leaving `pose` at whatever
        // default the caller seeded it with) unless at least one joint was
        // actually present, so a partially-authored model still shows its
        // real values for the joints it does have.
        private bool TryLoadPose(bool right, string prefix, ref ArmPose pose)
        {
            bool any = false;
            for (int joint = 0; joint < 3; joint++)
            {
                var marker = FindChildByName(MarkerParentFor(right, joint),
                    $"{prefix}{JointNames[joint]}{(right ? "R" : "L")}");
                if (marker == null) continue;

                pose = pose.With(joint, QuaternionToDegrees(marker.LocalTransform.GetDecomposed().Rotation));
                any = true;
            }
            return any;
        }

        private void LoadPoses()
        {
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0;

                _carry[side] = right ? DefaultCarryRight : DefaultCarryLeft;
                _carryFromModel[side] = TryLoadPose(right, "BallCarry", ref _carry[side]);

                // "StiffArmForward" rather than a "Handoff" prefix on
                // purpose: nothing writes handoff markers (this mode is a
                // viewer, and no editor authors them), and the game seeds
                // ArmIKConfig::rightHandoff/leftHandoff from the stiff-arm
                // poses, so a model's own tuned Forward stiff-arm pose is
                // the closest thing it actually has to its handoff reach.
                _reach[side] = right ? DefaultReachRight : DefaultReachLeft;
                _reachFromModel[side] = TryLoadPose(right, "StiffArmForward", ref _reach[side]);
            }
        }

        private void LoadAnchors()
        {
            for (int side = 0; side < 2; side++)
            {
                var hand = side == 0 ? _rightHand : _leftHand;
                _anchor[side] = (Vector3.Zero, Quaternion.Identity);
                if (hand == null) continue;

                foreach (var child in hand.VisualChildren)
                {
                    if (child.Name == null ||
                        child.Name.IndexOf("ballanchor", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    // GetDecomposed() rather than .Translation/.Rotation: an
                    // anchor that has been through a merge comes back in the
                    // matrix representation, where those two properties don't
                    // apply. Same reason BallAnchorEditor calls it.
                    var t = child.LocalTransform.GetDecomposed();
                    _anchor[side] = (t.Translation, t.Rotation);
                    _anchorFound[side] = true;
                    break;
                }
            }
        }

        // Inverse of ComputeOffsetQuaternion - the same pair every other
        // editor uses, so a pose read here matches the numbers those show.
        private static Vector3 QuaternionToDegrees(Quaternion q)
        {
            float sinPitch = Math.Clamp(2f * (q.W * q.Y - q.Z * q.X), -1f, 1f);
            float pitch = MathF.Asin(sinPitch);
            float yaw = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
            float roll = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
            const float r2d = 180f / MathF.PI;
            return new Vector3(roll * r2d, pitch * r2d, yaw * r2d);
        }

        private static Quaternion ComputeOffsetQuaternion(Vector3 deg)
        {
            const float d2r = MathF.PI / 180f;
            var rotX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg.X * d2r);
            var rotY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg.Y * d2r);
            var rotZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg.Z * d2r);
            return Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(rotZ, rotY), rotX));
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

            FlowLayoutPanel MakeRow() => new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 6),
            };

            Label Note(string text) => new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(320, 0),
                Margin = new Padding(3, 0, 3, 8),
            };

            _lblViewOnly = new Label
            {
                Text = "View only - nothing in this mode writes to the model.",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(320, 0),
                Margin = new Padding(3, 0, 3, 10),
            };
            flow.Controls.Add(_lblViewOnly);

            flow.Controls.Add(Note("Shows the handoff pose the game builds: the carry hand " +
                "extended straight ahead, still holding the ball at its saved anchor."));

            var sideRow = MakeRow();
            sideRow.Controls.Add(new Label { Text = "Carry / reach hand:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) });
            _radioRight = new RadioButton { Text = "Right", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioLeft = new RadioButton { Text = "Left", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            _radioRight.CheckedChanged += (s, e) => { if (_radioRight.Checked) OnSelectionChanged(); };
            _radioLeft.CheckedChanged += (s, e) => { if (_radioLeft.Checked) OnSelectionChanged(); };
            sideRow.Controls.Add(_radioRight);
            sideRow.Controls.Add(_radioLeft);
            flow.Controls.Add(sideRow);

            // Mirrors GameWorld::handoffReachWeight, which the game ramps
            // 0 -> 1 -> 0 across the exchange. Dragging it by hand is the
            // whole point of the mode: 0 shows the plain ball-carry hold,
            // 100 the fully extended give, and everything between is
            // literally what the player sees mid-handoff, since the game
            // blends the same two poses with the same slerp.
            var reachBox = new GroupBox { Text = "Reach", Width = 320, Height = 100, Margin = new Padding(0, 0, 0, 10) };
            _lblReach = new Label { Text = "Extension: 100%", Left = 10, Top = 24, AutoSize = true };
            _reachSlider = new TrackBar
            {
                Left = 10, Top = 48, Width = 290, Height = 45,
                Minimum = 0, Maximum = 100, Value = 100, TickFrequency = 10
            };
            _reachSlider.ValueChanged += (s, e) =>
            {
                _lblReach.Text = $"Extension: {_reachSlider.Value}%";
                PushPose();
            };
            var (reachMinus, reachPlus) = SliderNudge.Attach(_reachSlider);
            reachBox.Controls.Add(_lblReach);
            reachBox.Controls.Add(_reachSlider);
            reachBox.Controls.Add(reachMinus);
            reachBox.Controls.Add(reachPlus);
            flow.Controls.Add(reachBox);

            flow.Controls.Add(Note("0% = the ball-carry hold. 100% = the straight-ahead give. " +
                "In game this is handoffReachWeight easing between the two."));

            flow.Controls.Add(new Label { Text = "Preview Animation (free arm only):", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var animRow = MakeRow();
            _animDropdown = new ComboBox { Width = 220, Height = 26, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 8, 10) };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
            _btnPause.Click += (s, e) => TogglePause();
            animRow.Controls.Add(_animDropdown);
            animRow.Controls.Add(_btnPause);
            flow.Controls.Add(animRow);

            _lblChains = Note("");
            _lblSource = Note("");
            _lblAnchor = Note("");
            flow.Controls.Add(_lblChains);
            flow.Controls.Add(_lblSource);
            flow.Controls.Add(_lblAnchor);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private int CurrentSide() => _radioRight.Checked ? 0 : 1;

        private void OnSelectionChanged()
        {
            RefreshStatusLabels();
            PushPose();
        }

        // Says, per selected hand, exactly which numbers are on screen -
        // this model's own saved markers, or the game's hardcoded defaults.
        // Without it a model that has never been through the Ball Anchor /
        // Stiff Arm editors looks like it has a tuned handoff pose when it
        // is really showing a generic one.
        private void RefreshStatusLabels()
        {
            int side = CurrentSide();
            bool right = side == 0;

            // What a themed Label would have been left at - ThemeManager's
            // own two choices. Needed because the labels below flip between
            // a warning colour and the plain one, and hardcoding
            // SystemColors.ControlText for the plain case would paint black
            // text on the dark theme's near-black panel.
            var normal = _darkMode ? ThemeManager.DarkFore : System.Drawing.SystemColors.ControlText;
            var warn = System.Drawing.Color.Goldenrod;

            _lblViewOnly.ForeColor = warn;

            var shoulder = right ? _rightShoulder : _leftShoulder;
            var elbow = right ? _rightElbow : _leftElbow;
            var hand = right ? _rightHand : _leftHand;
            _lblChains.Text = $"{(right ? "Right" : "Left")} chain: " +
                $"{shoulder?.Name ?? "?"} / {elbow?.Name ?? "?"} / {hand?.Name ?? "NOT FOUND"}";

            string carrySrc = _carryFromModel[side] ? "this model's BallCarry markers" : "ArmIKConfig defaults";
            string reachSrc = _reachFromModel[side] ? "this model's StiffArmForward markers" : "ArmIKConfig defaults";
            _lblSource.Text = $"Carry pose from {carrySrc}.\r\nReach pose from {reachSrc}.";
            _lblSource.ForeColor = (_carryFromModel[side] && _reachFromModel[side]) ? normal : warn;

            _lblAnchor.Text = _anchorFound[side]
                ? "Ball anchor: using this hand's saved BallAnchor offset."
                : "Ball anchor: NONE saved for this hand - the ball is sitting at the hand bone's own origin. " +
                  "Set one in the Ball Anchor editor.";
            _lblAnchor.ForeColor = _anchorFound[side] ? normal : warn;
        }

        private static string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

        // Sends the selected hand's two end poses plus the anchor, and lets
        // the viewer slerp between them every frame - the same shape as
        // ModelRenderer::UpdateInstance's handoff branch, which slerps the
        // already-active carry lock's target toward the reach by
        // stiffArm.weight rather than swapping locks.
        private void PushPose()
        {
            if (!_viewerReady) return;
            int side = CurrentSide();
            bool right = side == 0;
            var handName = right ? _rightHand?.Name : _leftHand?.Name;
            if (handName == null) return;

            static string Quats(ArmPose p)
            {
                var s = ComputeOffsetQuaternion(p.Shoulder);
                var e = ComputeOffsetQuaternion(p.Elbow);
                var w = ComputeOffsetQuaternion(p.Wrist);
                return $"{Inv(s.X)},{Inv(s.Y)},{Inv(s.Z)},{Inv(s.W)}," +
                       $"{Inv(e.X)},{Inv(e.Y)},{Inv(e.Z)},{Inv(e.W)}," +
                       $"{Inv(w.X)},{Inv(w.Y)},{Inv(w.Z)},{Inv(w.W)}";
            }

            var (pos, rot) = _anchor[side];
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setHandoff('{EscapeJs(handName)}', " +
                $"{Quats(_carry[side])}, {Quats(_reach[side])}, " +
                $"{Inv(_reachSlider.Value / 100f)}, " +
                $"{Inv(pos.X)},{Inv(pos.Y)},{Inv(pos.Z)}," +
                $"{Inv(rot.X)},{Inv(rot.Y)},{Inv(rot.Z)},{Inv(rot.W)});");
        }

        private void PopulateAnimationList()
        {
            _animDropdown.Items.Add("None (Bind Pose)");
            foreach (var anim in _model.LogicalAnimations)
                _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");
            _animDropdown.SelectedIndex = _animDropdown.Items.Count > 1 ? 1 : 0;
        }

        private void OnAnimationSelected()
        {
            _paused = false;
            _btnPause.Text = "Pause";
            if (!_viewerReady) return;

            var name = _animDropdown.SelectedIndex > 0 ? (string)_animDropdown.SelectedItem! : null;
            _webView.CoreWebView2.ExecuteScriptAsync(
                name == null ? "setAnimationByName(null);" : $"setAnimationByName('{EscapeJs(name)}');");
            _webView.CoreWebView2.ExecuteScriptAsync("setPaused(false);");
        }

        private void TogglePause()
        {
            if (!_viewerReady) return;
            _paused = !_paused;
            _btnPause.Text = _paused ? "Resume" : "Pause";
            _webView.CoreWebView2.ExecuteScriptAsync(_paused ? "setPaused(true);" : "setPaused(false);");
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this
            // fire-and-forget startup may still be mid-await, so both the await itself
            // and everything after it have to tolerate that.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_handoff_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);

            // Same ball prop BallAnchorEditor previews with (see the
            // Assets\Football1.glb CopyToOutputDirectory item in the csproj),
            // copied next to the character preview so both are reachable
            // through the one virtual host mapping.
            string ballSourcePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Football1.glb");
            string ballPreviewPath = Path.Combine(tempFolder, "glbmerger_ball_prop.glb");
            bool haveBall = File.Exists(ballSourcePath);
            if (haveBall) File.Copy(ballSourcePath, ballPreviewPath, overwrite: true);

            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (_viewerReady) PushPose();
            };

            _model.SaveGLB(previewPath);
            var previewFileName = Path.GetFileName(previewPath);
            var ballFileName = haveBall ? Path.GetFileName(ballPreviewPath) : "";

            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewport { width: 100%; height: 100%; display: block; }
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
                <div id='error-overlay'></div>
                <script>
                    function showError(msg) {
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };

                    try {
                        var canvas = document.querySelector('#viewport');
                        var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
                        renderer.setPixelRatio(window.devicePixelRatio);
                        renderer.setSize(window.innerWidth, window.innerHeight);
                        renderer.outputEncoding = THREE.sRGBEncoding;
                        renderer.toneMapping = THREE.ACESFilmicToneMapping;
                        renderer.toneMappingExposure = 1.1;

                        var scene = new THREE.Scene();
                        scene.background = new THREE.Color(0x1a1c1e);

                        var camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.01, 10000);
                        var controls = new THREE.OrbitControls(camera, renderer.domElement);
                        controls.enableDamping = true;

                        scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
                        scene.add(new THREE.AmbientLight(0xffffff, 0.6));
                        var dirLight = new THREE.DirectionalLight(0xffffff, 1.5);
                        dirLight.position.set(5, 10, 7.5);
                        scene.add(dirLight);
                        var fillLight = new THREE.DirectionalLight(0xffffff, 0.8);
                        fillLight.position.set(-5, 5, -7.5);
                        scene.add(fillLight);

                        var mixer = null;
                        var clock = new THREE.Clock();
                        var paused = false;
                        var bonesByName = {};
                        // Captured before any lock has touched a bone, so
                        // switching hands can hand the just-released arm back
                        // to a clean rest pose instead of leaving it frozen
                        // wherever the lock last put it (the loaded clip may
                        // not animate that chain at all).
                        var bindQuatByName = {};
                        var characterLoaded = false;

                        // The one locked chain: the carry hand, held at
                        // slerp(carry, reach, reachT). Only ever one side at
                        // a time - the other arm keeps running the clip, the
                        // same as in game, where only the giver's ball hand
                        // is locked for the reach.
                        var lockedHandName = null;
                        var carryQ = null, reachQ = null, reachT = 1;

                        // Invisible marker parented to the carry hand at the
                        // model's saved BallAnchor offset. The visible ball is
                        // never reparented - it just samples this marker's
                        // world transform each frame, so a hand switch shows
                        // up on the very next frame with no reattach step to
                        // get wrong. Same approach BallAnchorEditor uses.
                        var anchorRef = null;
                        var pendingAnchor = null;

                        var ballObj = null;
                        var ballPivot = null;
                        var characterHeight = null;
                        var ballNativeHeight = null;

                        // The game's own ball-size convention: FootballRenderer
                        // draws the carried ball at kBallScale = 0.675 world
                        // units tall while characters render at
                        // CameraController::kPlayerHeight = 4.8. Their ratio is
                        // dimensionless, so it reproduces correctly against
                        // whatever height this model happens to load at here.
                        var kBallHeightFraction = 0.675 / 4.8;

                        function fitBallScale() {
                            if (!ballPivot || characterHeight === null || ballNativeHeight === null) return;
                            var target = kBallHeightFraction * characterHeight;
                            ballPivot.scale.setScalar(ballNativeHeight > 1e-6 ? target / ballNativeHeight : 1);
                        }

                        window.setHandoff = function (handName,
                                                      csx,csy,csz,csw, cex,cey,cez,cew, cwx,cwy,cwz,cww,
                                                      rsx,rsy,rsz,rsw, rex,rey,rez,rew, rwx,rwy,rwz,rww,
                                                      t, ax, ay, az, aqx, aqy, aqz, aqw) {
                            if (lockedHandName && lockedHandName !== handName) releaseChain(lockedHandName);

                            lockedHandName = handName;
                            carryQ = {
                                shoulder: new THREE.Quaternion(csx,csy,csz,csw),
                                elbow:    new THREE.Quaternion(cex,cey,cez,cew),
                                wrist:    new THREE.Quaternion(cwx,cwy,cwz,cww)
                            };
                            reachQ = {
                                shoulder: new THREE.Quaternion(rsx,rsy,rsz,rsw),
                                elbow:    new THREE.Quaternion(rex,rey,rez,rew),
                                wrist:    new THREE.Quaternion(rwx,rwy,rwz,rww)
                            };
                            reachT = t;

                            pendingAnchor = { x: ax, y: ay, z: az, qx: aqx, qy: aqy, qz: aqz, qw: aqw };
                            attachAnchor();
                            applyLock();
                        };

                        // Moves the anchor marker onto whichever hand is now
                        // selected, creating it on first use. Reparenting
                        // rather than keeping one per side: only one hand ever
                        // holds the ball here, so a second marker would only
                        // be a second thing to keep in sync.
                        function attachAnchor() {
                            if (!pendingAnchor) return;
                            var bone = lockedHandName ? bonesByName[lockedHandName] : null;
                            if (!bone) {
                                if (characterLoaded) showError('Handoff: hand bone not found: ""' + lockedHandName + '""');
                                return;
                            }
                            if (!anchorRef) anchorRef = new THREE.Object3D();
                            if (anchorRef.parent !== bone) bone.add(anchorRef);
                            anchorRef.position.set(pendingAnchor.x, pendingAnchor.y, pendingAnchor.z);
                            anchorRef.quaternion.set(pendingAnchor.qx, pendingAnchor.qy, pendingAnchor.qz, pendingAnchor.qw);
                        }

                        function releaseChain(handName) {
                            var wrist = bonesByName[handName];
                            if (!wrist) return;
                            var elbow = wrist.parent;
                            var shoulder = elbow ? elbow.parent : null;
                            if (bindQuatByName[handName]) wrist.quaternion.copy(bindQuatByName[handName]);
                            if (elbow && bindQuatByName[elbow.name]) elbow.quaternion.copy(bindQuatByName[elbow.name]);
                            if (shoulder && bindQuatByName[shoulder.name]) shoulder.quaternion.copy(bindQuatByName[shoulder.name]);
                        }

                        // Absolute override at full weight, exactly like
                        // ArmIK.cpp's LockArmPose at weight 1 - the target
                        // itself is the carry pose slerped toward the reach by
                        // reachT, which is what ModelRenderer::UpdateInstance
                        // does when a handoff reach lands on a hand that is
                        // already carry-locked.
                        function applyLock() {
                            if (!lockedHandName || !carryQ || !reachQ) return;
                            var wrist = bonesByName[lockedHandName];
                            if (!wrist) return;
                            var elbow = wrist.parent;
                            var shoulder = elbow ? elbow.parent : null;
                            if (!elbow || !shoulder) return;

                            shoulder.quaternion.copy(carryQ.shoulder).slerp(reachQ.shoulder, reachT);
                            elbow.quaternion.copy(carryQ.elbow).slerp(reachQ.elbow, reachT);
                            wrist.quaternion.copy(carryQ.wrist).slerp(reachQ.wrist, reachT);
                        }

                        window.setAnimationByName = function (name) {
                            if (!mixer) return;
                            mixer.stopAllAction();
                            if (!name) return;
                            var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                            if (clip) mixer.clipAction(clip).play();
                        };

                        window.setPaused = function (value) { paused = value; };

                        var charLoader = new THREE.GLTFLoader();
                        charLoader.load('https://appassets.local/" + previewFileName + @"', function (gltf) {
                            try {
                                scene.add(gltf.scene);

                                var orderedMaterials = [];
                                gltf.scene.traverse(function (obj) {
                                    if (obj.isMesh && obj.material) {
                                        var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                                        mats.forEach(function (mat) {
                                            mat.side = THREE.DoubleSide;
                                            if (orderedMaterials.indexOf(mat) === -1) orderedMaterials.push(mat);
                                        });
                                    }
                                    if (obj.name) {
                                        bonesByName[obj.name] = obj;
                                        bindQuatByName[obj.name] = obj.quaternion.clone();
                                    }
                                });

                                orderedMaterials.forEach(function (mat, i) {
                                    var visible = i === 0;
                                    mat.transparent = !visible;
                                    mat.opacity = visible ? 1 : 0.05;
                                    mat.depthWrite = visible;
                                    if (typeof mat.metalness === 'number') mat.metalness = Math.min(mat.metalness, 0.15);
                                    if (typeof mat.roughness === 'number') mat.roughness = Math.max(mat.roughness, 0.7);
                                });

                                var box = new THREE.Box3().setFromObject(gltf.scene);
                                var size = box.getSize(new THREE.Vector3());
                                var center = box.getCenter(new THREE.Vector3());
                                var maxDim = (Math.max(size.x, size.y, size.z) || 1) * 2.5;

                                controls.target.copy(center);
                                camera.position.copy(center).add(new THREE.Vector3(maxDim, maxDim * 0.6, maxDim));
                                camera.near = maxDim / 1000;
                                camera.far = maxDim * 100;
                                camera.updateProjectionMatrix();
                                controls.update();

                                characterLoaded = true;
                                characterHeight = size.y;
                                fitBallScale();
                                attachAnchor();
                                applyLock();
                                if (lockedHandName && !bonesByName[lockedHandName])
                                    showError('Handoff: hand bone not found: ""' + lockedHandName + '""');

                                window._clips = gltf.animations || [];
                                if (window._clips.length > 0) {
                                    mixer = new THREE.AnimationMixer(gltf.scene);
                                    mixer.clipAction(window._clips[0]).play();
                                }
                            } catch (innerErr) {
                                showError('Error setting up character preview: ' + innerErr.message);
                            }
                        }, undefined, function (error) {
                            showError('Failed to load character preview: ' + (error && error.message ? error.message : error));
                        });
" + (string.IsNullOrEmpty(ballFileName) ? @"
                        showError('Handoff: Assets/Football1.glb was not found next to the exe - showing no ball prop.');
" : @"
                        var ballLoader = new THREE.GLTFLoader();
                        ballLoader.load('https://appassets.local/" + ballFileName + @"', function (gltf) {
                            ballObj = gltf.scene;

                            // Re-center on the mesh's own geometric middle to
                            // match FootballRenderer::Init's normalize matrix,
                            // which nets out to 'true center -> origin' - the
                            // BallAnchor offset means where the ball's CENTER
                            // sits, not the source file's authored origin.
                            var bbox = new THREE.Box3().setFromObject(ballObj);
                            var bsize = bbox.getSize(new THREE.Vector3());
                            var bcenter = bbox.getCenter(new THREE.Vector3());
                            ballNativeHeight = bsize.y;
                            ballObj.position.set(-bcenter.x, -bcenter.y, -bcenter.z);

                            ballPivot = new THREE.Group();
                            ballPivot.add(ballObj);
                            scene.add(ballPivot);
                            fitBallScale();
                        }, undefined, function (error) {
                            showError('Failed to load ball prop: ' + (error && error.message ? error.message : error));
                        });
") + @"
                        window.addEventListener('resize', function () {
                            camera.aspect = window.innerWidth / window.innerHeight;
                            camera.updateProjectionMatrix();
                            renderer.setSize(window.innerWidth, window.innerHeight);
                        });

                        var aPos = new THREE.Vector3(), aQuat = new THREE.Quaternion();

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            if (mixer && !paused) mixer.update(delta);

                            // After mixer.update(), so the lock overrides
                            // whatever the clip posed the carry arm to -
                            // the same ordering the game uses.
                            applyLock();
                            scene.updateMatrixWorld(true);

                            if (ballPivot) {
                                if (anchorRef && anchorRef.parent) {
                                    anchorRef.getWorldPosition(aPos);
                                    anchorRef.getWorldQuaternion(aQuat);
                                    ballPivot.position.copy(aPos);
                                    ballPivot.quaternion.copy(aQuat);
                                    ballPivot.visible = true;
                                } else {
                                    ballPivot.visible = false;
                                }
                            }

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
