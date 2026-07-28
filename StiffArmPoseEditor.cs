using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Places the 3 stiff-arm reference poses (Forward/Side/Down) for each
    // hand directly into the model, the same way BallAnchorEditor bakes the
    // ball's hand offset in - these poses turned out to be just as rig-
    // specific as the ball offset was (ModelRenderer::ArmIKConfig's
    // hardcoded Euler-degree defaults were tuned against one rig's bind/
    // axis convention, and don't bend a differently-authored rig's arm the
    // same way), so tuning them live in-game per model doesn't scale any
    // better than the ball offset did.
    //
    // Each of the 9 combinations (2 sides x 3 poses) needs 3 joints
    // (shoulder/elbow/wrist), each an absolute local rotation - not a
    // correction relative to the clip's animated pose, since
    // LockArmPose fully replaces the joint's local rotation at weight 1
    // (see ArmIK.cpp). A marker for joint J is saved as a SIBLING of the
    // real joint it overrides - parented under that real joint's own real
    // parent - so the marker's own local rotation, relative to that shared
    // parent, IS directly the value to assign. ModelRenderer::
    // ApplyBakedStiffArmPoses on the game side looks for exactly that.
    //
    // One of the four modes hosted by ModelEditorForm (see EditorMode there),
    // which owns the window chrome shared by all four, so this control only
    // contributes its own left-hand controls and 3D preview.
    public class StiffArmPoseEditor : UserControl
    {
        private readonly ModelRoot _model;

        private ComboBox _animDropdown = null!;
        private RadioButton _radioRight = null!, _radioLeft = null!;
        private RadioButton _radioForward = null!, _radioSide = null!, _radioDown = null!;
        private Label _lblStatus = null!, _lblBoneRight = null!, _lblBoneLeft = null!;
        private Button _btnPause = null!, _btnCopyOther = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;
        private bool _suppressEvents;

        // [shoulder=0/elbow=1/wrist=2] slider triplets for whichever
        // side+pose is currently selected - rebuilt from _deg on every
        // switch (see RefreshUiFromState), same pattern as BallAnchorEditor.
        private TrackBar[,] _sliders = new TrackBar[3, 3];   // [joint, axis]
        private Label[,] _sliderLabels = new Label[3, 3];

        private struct JointDeg { public int X, Y, Z; }
        // [side (0=right,1=left), pose (0=forward,1=side,2=down), joint (0=shoulder,1=elbow,2=wrist)]
        private readonly JointDeg[,,] _deg = new JointDeg[2, 3, 3];

        private readonly Node? _rightWrist, _leftWrist;
        private readonly Node? _rightElbow, _leftElbow;
        private readonly Node? _rightShoulder, _leftShoulder;

        private static readonly string[] RightNames = { "righthand", "hand_r", "r_hand", "hand.r",
            "wrist_r", "mixamorig:righthand", "handr", "rwrist" };
        private static readonly string[] LeftNames = { "lefthand", "hand_l", "l_hand", "hand.l",
            "wrist_l", "mixamorig:lefthand", "handl", "lwrist" };
        private static readonly string[] WeakNames = { "hand", "wrist", "palm" };

        private static readonly string[] PoseNames = { "Forward", "Side", "Down" };
        private static readonly string[] JointNames = { "Shoulder", "Elbow", "Wrist" };

        public StiffArmPoseEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;
            _rightWrist = FindHandNode(right: true);
            _leftWrist = FindHandNode(right: false);
            _rightElbow = _rightWrist?.VisualParent;
            _leftElbow = _leftWrist?.VisualParent;
            _rightShoulder = _rightElbow?.VisualParent;
            _leftShoulder = _leftElbow?.VisualParent;

            Dock = DockStyle.Fill;

            BuildUi();
            LoadAllExisting();
            PopulateAnimationList();
            RefreshUiFromState();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        // Same alias-matching convention ModelRenderer::FindHandJoints uses,
        // so this resolves the exact bones the game will look under.
        private Node? FindHandNode(bool right)
        {
            Node? Search(string[] names)
            {
                foreach (var n in _model.LogicalNodes)
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

        private static Node? FindExistingMarker(Node? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.VisualChildren)
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            return null;
        }

        private static string MarkerName(bool right, int pose, int joint) =>
            $"StiffArm{PoseNames[pose]}{JointNames[joint]}{(right ? "R" : "L")}";

        // Joint J's marker lives under joint J's own real PARENT (see the
        // class comment) - shoulder's marker parent is one level further up
        // than the shoulder bone itself.
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

        private void LoadAllExisting()
        {
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0;
                for (int pose = 0; pose < 3; pose++)
                {
                    for (int joint = 0; joint < 3; joint++)
                    {
                        var parent = MarkerParentFor(right, joint);
                        var marker = FindExistingMarker(parent, MarkerName(right, pose, joint));
                        if (marker == null) continue;

                        var deg = QuaternionToDegrees(marker.LocalTransform.GetDecomposed().Rotation);
                        _deg[side, pose, joint] = new JointDeg { X = deg.X, Y = deg.Y, Z = deg.Z };
                    }
                }
            }
        }

        // Inverse of ComputeOffsetQuaternion below.
        private static (int X, int Y, int Z) QuaternionToDegrees(Quaternion q)
        {
            float sinPitch = 2f * (q.W * q.Y - q.Z * q.X);
            sinPitch = Math.Clamp(sinPitch, -1f, 1f);
            float pitch = MathF.Asin(sinPitch);
            float yaw   = MathF.Atan2(2f * (q.W * q.Z + q.X * q.Y), 1f - 2f * (q.Y * q.Y + q.Z * q.Z));
            float roll  = MathF.Atan2(2f * (q.W * q.X + q.Y * q.Z), 1f - 2f * (q.X * q.X + q.Y * q.Y));
            const float r2d = 180f / MathF.PI;
            return ((int)MathF.Round(roll * r2d), (int)MathF.Round(pitch * r2d), (int)MathF.Round(yaw * r2d));
        }

        private static Quaternion ComputeOffsetQuaternion(int xDeg, int yDeg, int zDeg)
        {
            const float d2r = MathF.PI / 180f;
            var rotX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xDeg * d2r);
            var rotY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yDeg * d2r);
            var rotZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zDeg * d2r);
            return Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(rotZ, rotY), rotX));
        }

        // Everything below stacks through a top-down FlowLayoutPanel instead
        // of hand-computed Left/Top offsets - the previous version's
        // "top += N" bookkeeping drifted out of sync as controls were added
        // over several edits (missed labels, overlapping rows), and every
        // control's size measures itself against the ACTUAL rendered font
        // instead of an assumed 96-DPI pixel count, so nothing clips if the
        // system font/DPI turns out larger than expected. Only the sliders
        // and the two RadioButton rows (which need LeftToRight flow inside
        // the TopDown outer flow, and their own grouping - see below) still
        // set explicit sizes, and those are sized with real margin instead
        // of the previous tightly-packed pixel math.
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

            // Each RadioButton set needs its OWN container - WinForms treats
            // every RadioButton added directly to the same parent as one
            // mutually-exclusive group, regardless of which logical set it's
            // meant to belong to. Without two separate rows, picking a Pose
            // option also silently unchecked whichever Hand option was
            // selected (and vice versa), since all five were one group.
            var sideRow = MakeRow();
            sideRow.Controls.Add(new Label { Text = "Hand:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) });
            _radioRight = new RadioButton { Text = "Right", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioLeft  = new RadioButton { Text = "Left",  AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            _radioRight.CheckedChanged += (s, e) => { if (_radioRight.Checked) OnSelectionChanged(); };
            _radioLeft.CheckedChanged  += (s, e) => { if (_radioLeft.Checked)  OnSelectionChanged(); };
            sideRow.Controls.Add(_radioRight);
            sideRow.Controls.Add(_radioLeft);
            flow.Controls.Add(sideRow);

            var poseRow = MakeRow();
            poseRow.Controls.Add(new Label { Text = "Pose:", AutoSize = true, Margin = new Padding(3, 7, 8, 3) });
            _radioForward = new RadioButton { Text = "Forward", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioSide    = new RadioButton { Text = "Side",    AutoSize = true, Margin = new Padding(3, 4, 16, 3) };
            _radioDown    = new RadioButton { Text = "Down",    AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            _radioForward.CheckedChanged += (s, e) => { if (_radioForward.Checked) OnSelectionChanged(); };
            _radioSide.CheckedChanged    += (s, e) => { if (_radioSide.Checked)    OnSelectionChanged(); };
            _radioDown.CheckedChanged    += (s, e) => { if (_radioDown.Checked)    OnSelectionChanged(); };
            poseRow.Controls.Add(_radioForward);
            poseRow.Controls.Add(_radioSide);
            poseRow.Controls.Add(_radioDown);
            flow.Controls.Add(poseRow);

            // Copies the OTHER side's values for the CURRENTLY selected pose
            // onto this side, using the same X-same/Y,Z-negated mirror guess
            // ArmIKConfig's own hardcoded defaults started from (see
            // ModelRenderer.h's comment above ArmIKConfig) - a starting
            // point to hand-tune from, not a substitute for it.
            _btnCopyOther = new Button { Text = "Copy Settings from Other Arm", AutoSize = true, Margin = new Padding(3, 2, 3, 10) };
            _btnCopyOther.Click += (s, e) => CopyFromOtherArm();
            flow.Controls.Add(_btnCopyOther);

            _lblBoneRight = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(320, 0), Margin = new Padding(3, 0, 3, 2),
                Text = "Right chain: " + (_rightShoulder?.Name ?? "?") + " / " + (_rightElbow?.Name ?? "?") + " / " + (_rightWrist?.Name ?? "NOT FOUND")
            };
            _lblBoneLeft = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(320, 0), Margin = new Padding(3, 0, 3, 10),
                Text = "Left chain: " + (_leftShoulder?.Name ?? "?") + " / " + (_leftElbow?.Name ?? "?") + " / " + (_leftWrist?.Name ?? "NOT FOUND")
            };
            flow.Controls.Add(_lblBoneRight);
            flow.Controls.Add(_lblBoneLeft);

            flow.Controls.Add(new Label { Text = "Preview Animation (unlocked arm only):", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var animRow = MakeRow();
            _animDropdown = new ComboBox { Width = 220, Height = 26, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 8, 10) };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
            _btnPause.Click += (s, e) => TogglePause();
            animRow.Controls.Add(_animDropdown);
            animRow.Controls.Add(_btnPause);
            flow.Controls.Add(animRow);

            // Row geometry is computed from these constants rather than
            // eyeballed per-call - the previous version hand-picked a 54px
            // row pitch but left the GroupBox's own fixed Height at the
            // OLDER, tighter value, so the third row's slider actually
            // extended a few pixels past the box's own bottom edge and got
            // clipped. Deriving GroupBox height FROM the same constants the
            // rows use can't drift out of sync the same way again.
            const int kRowHeaderTop    = 26;  // clears the GroupBox's own title text
            const int kLabelToSlider   = 26;  // room for the label, generous for a taller font
            const int kSliderHeight    = 45;
            const int kGapAfterSlider  = 14;  // slack before the next row's label
            const int kRowPitch        = kLabelToSlider + kSliderHeight + kGapAfterSlider;
            const int kBottomPad       = 20;
            int groupHeight = kRowHeaderTop + 3 * kRowPitch + kBottomPad;

            for (int joint = 0; joint < 3; joint++)
            {
                var group = new GroupBox { Text = JointNames[joint], Width = 320, Height = groupHeight, Margin = new Padding(0, 0, 0, 10) };
                for (int axis = 0; axis < 3; axis++)
                {
                    int a = axis;
                    int rowTop = kRowHeaderTop + axis * kRowPitch;
                    var lbl = new Label { Text = AxisLabel(axis) + ": 0°", Left = 10, Top = rowTop, AutoSize = true };
                    var slider = new TrackBar
                    {
                        Left = 10, Top = rowTop + kLabelToSlider, Width = 290, Height = kSliderHeight,
                        Minimum = -180, Maximum = 180, Value = 0, TickFrequency = 30
                    };
                    slider.ValueChanged += (s, e) => { lbl.Text = AxisLabel(a) + $": {slider.Value}°"; OnSliderChanged(); };
                    group.Controls.Add(lbl);
                    group.Controls.Add(slider);
                    _sliders[joint, axis] = slider;
                    _sliderLabels[joint, axis] = lbl;
                }
                flow.Controls.Add(group);
            }

            var btnSave = new Button { Text = "Save This Hand/Pose", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            btnSave.Click += (s, e) => SaveCurrent();

            var btnReset = new Button { Text = "Reset This Hand/Pose to 0", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            btnReset.Click += (s, e) => ResetCurrent();

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(320, 0),
                Margin = new Padding(3, 6, 3, 10), ForeColor = System.Drawing.Color.LightGreen
            };

            flow.Controls.Add(btnSave);
            flow.Controls.Add(btnReset);
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        // Mirror guess: X (flex/extend) keeps the same sign, Y (twist) and Z
        // (raise/cross) flip sign - the same convention ArmIKConfig's own
        // rightStiff was originally guessed from a tuned leftStiff (see
        // ModelRenderer.h). Only ever a starting point; magnitudes commonly
        // differ per side by up to ~2x in practice, so this is not a
        // substitute for tuning the copied-to side directly afterward.
        private void CopyFromOtherArm()
        {
            var (side, pose) = CurrentSelection();
            int otherSide = 1 - side;
            for (int joint = 0; joint < 3; joint++)
            {
                var src = _deg[otherSide, pose, joint];
                _deg[side, pose, joint] = new JointDeg { X = src.X, Y = -src.Y, Z = -src.Z };
            }
            RefreshUiFromState();
            PushLockedSide();
            _lblStatus.Text = "Copied from other arm (mirrored guess) — remember to Save.";
        }

        private static string AxisLabel(int axis) => axis == 0 ? "X Rotation" : axis == 1 ? "Y Rotation" : "Z Rotation";

        private (int Side, int Pose) CurrentSelection() =>
            (_radioRight.Checked ? 0 : 1, _radioForward.Checked ? 0 : _radioSide.Checked ? 1 : 2);

        private void OnSelectionChanged()
        {
            RefreshUiFromState();
            PushLockedSide();
        }

        private void RefreshUiFromState()
        {
            var (side, pose) = CurrentSelection();
            _suppressEvents = true;
            for (int joint = 0; joint < 3; joint++)
            {
                var d = _deg[side, pose, joint];
                _sliders[joint, 0].Value = d.X;
                _sliders[joint, 1].Value = d.Y;
                _sliders[joint, 2].Value = d.Z;
                _sliderLabels[joint, 0].Text = AxisLabel(0) + $": {d.X}°";
                _sliderLabels[joint, 1].Text = AxisLabel(1) + $": {d.Y}°";
                _sliderLabels[joint, 2].Text = AxisLabel(2) + $": {d.Z}°";
            }
            _suppressEvents = false;
        }

        private void OnSliderChanged()
        {
            if (_suppressEvents) return;
            var (side, pose) = CurrentSelection();
            for (int joint = 0; joint < 3; joint++)
            {
                _deg[side, pose, joint] = new JointDeg
                {
                    X = _sliders[joint, 0].Value,
                    Y = _sliders[joint, 1].Value,
                    Z = _sliders[joint, 2].Value
                };
            }
            PushLockedSide();
        }

        private static string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

        // Locks whichever side is currently selected to its (possibly
        // unsaved) slider values, live, exactly mirroring LockArmPose's
        // weight-1 absolute override - the other side keeps animating
        // normally. Re-sent on every slider move and on side/pose switch.
        private void PushLockedSide()
        {
            if (!_viewerReady) return;
            var (side, pose) = CurrentSelection();
            bool right = side == 0;
            var wristName = right ? _rightWrist?.Name : _leftWrist?.Name;
            if (wristName == null) return;

            var sQ = ComputeOffsetQuaternion(_deg[side, pose, 0].X, _deg[side, pose, 0].Y, _deg[side, pose, 0].Z);
            var eQ = ComputeOffsetQuaternion(_deg[side, pose, 1].X, _deg[side, pose, 1].Y, _deg[side, pose, 1].Z);
            var wQ = ComputeOffsetQuaternion(_deg[side, pose, 2].X, _deg[side, pose, 2].Y, _deg[side, pose, 2].Z);

            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setLockedChain('{(right ? "right" : "left")}', '{EscapeJs(wristName)}', " +
                $"{Inv(sQ.X)},{Inv(sQ.Y)},{Inv(sQ.Z)},{Inv(sQ.W)}, " +
                $"{Inv(eQ.X)},{Inv(eQ.Y)},{Inv(eQ.Z)},{Inv(eQ.W)}, " +
                $"{Inv(wQ.X)},{Inv(wQ.Y)},{Inv(wQ.Z)},{Inv(wQ.W)});");
        }

        private void SaveCurrent()
        {
            var (side, pose) = CurrentSelection();
            bool right = side == 0;

            var wrist = right ? _rightWrist : _leftWrist;
            if (wrist == null)
            {
                MessageBox.Show(this, "No arm chain resolved for this hand — cannot save.", "Stiff Arm Poses",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            for (int joint = 0; joint < 3; joint++)
            {
                var parent = MarkerParentFor(right, joint);
                if (parent == null) continue;

                var name = MarkerName(right, pose, joint);
                var marker = FindExistingMarker(parent, name) ?? parent.CreateNode(name);
                marker.Name = name;
                var d = _deg[side, pose, joint];
                marker.WithLocalRotation(ComputeOffsetQuaternion(d.X, d.Y, d.Z));
            }

            _lblStatus.Text = $"Saved {(right ? "right" : "left")} {PoseNames[pose].ToLowerInvariant()} pose.";
        }

        private void ResetCurrent()
        {
            var (side, pose) = CurrentSelection();
            for (int joint = 0; joint < 3; joint++)
                _deg[side, pose, joint] = new JointDeg();
            RefreshUiFromState();
            PushLockedSide();
            _lblStatus.Text = "Reset — remember to Save to write this to the model.";
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
            var script = name == null ? "setAnimationByName(null);" : $"setAnimationByName('{EscapeJs(name)}');";
            _webView.CoreWebView2.ExecuteScriptAsync(script);
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
            string previewPath = Path.Combine(tempFolder, "glbmerger_stiffarm_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (_viewerReady) PushLockedSide();
            };

            _model.SaveGLB(previewPath);
            var previewFileName = Path.GetFileName(previewPath);

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
                        // Captured once, right after the model loads, before
                        // any lock has ever touched a bone - lets releaseChain
                        // below put a just-deselected side back to a clean
                        // rest pose instead of leaving it wherever the lock
                        // last left it.
                        var bindQuatByName = {};
                        var currentClip = null;
                        var currentAction = null;

                        // The chain currently locked to explicit slider-driven
                        // quaternions, re-applied every frame AFTER mixer.update()
                        // so it overrides whatever the clip would otherwise pose
                        // those 3 bones to - exactly mirroring LockArmPose's
                        // weight-1 absolute override on the game side. Only one
                        // side is ever locked at a time; the other keeps
                        // animating normally for context.
                        var lockedSide = null;
                        var lockedWristName = null;
                        var lockShoulderQ = null, lockElbowQ = null, lockWristQ = null;

                        window.setLockedChain = function (side, wristName, sx,sy,sz,sw, ex,ey,ez,ew, wx,wy,wz,ww) {
                            // Switching sides: the arm that was locked a
                            // moment ago must be handed back to normal
                            // animation, not just abandoned - if the current
                            // clip (or bind pose, with no clip playing) never
                            // touches its shoulder/elbow/wrist, mixer.update()
                            // has nothing to overwrite the old locked
                            // rotation with, and it would otherwise sit
                            // frozen in the previous pose forever. Releasing
                            // it to its captured bind rotation here means the
                            // very next mixer.update() either drives it
                            // correctly (if the clip does animate it) or
                            // leaves it at a clean rest pose (if it doesn't) -
                            // never at a stale lock.
                            if (lockedWristName && lockedWristName !== wristName) releaseChain(lockedWristName);

                            lockedSide = side;
                            lockedWristName = wristName;
                            lockShoulderQ = new THREE.Quaternion(sx,sy,sz,sw);
                            lockElbowQ    = new THREE.Quaternion(ex,ey,ez,ew);
                            lockWristQ    = new THREE.Quaternion(wx,wy,wz,ww);
                            applyLock();
                        };

                        function releaseChain(wristName) {
                            var wrist = bonesByName[wristName];
                            if (!wrist) return;
                            var elbow = wrist.parent;
                            var shoulder = elbow ? elbow.parent : null;
                            if (bindQuatByName[wristName]) wrist.quaternion.copy(bindQuatByName[wristName]);
                            if (elbow && bindQuatByName[elbow.name]) elbow.quaternion.copy(bindQuatByName[elbow.name]);
                            if (shoulder && bindQuatByName[shoulder.name]) shoulder.quaternion.copy(bindQuatByName[shoulder.name]);
                        }

                        function applyLock() {
                            if (!lockedWristName) return;
                            var wrist = bonesByName[lockedWristName];
                            if (!wrist) return;
                            var elbow = wrist.parent;
                            var shoulder = elbow ? elbow.parent : null;
                            if (!elbow || !shoulder) return;
                            shoulder.quaternion.copy(lockShoulderQ);
                            elbow.quaternion.copy(lockElbowQ);
                            wrist.quaternion.copy(lockWristQ);
                        }

                        window.setAnimationByName = function (name) {
                            currentClip = null;
                            currentAction = null;
                            if (!mixer) return;
                            mixer.stopAllAction();
                            if (!name) return;
                            var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                            if (clip) {
                                currentClip = clip;
                                currentAction = mixer.clipAction(clip);
                                currentAction.play();
                            }
                        };

                        window.setPaused = function (value) { paused = value; };

                        var loader = new THREE.GLTFLoader();
                        loader.load('https://appassets.local/" + previewFileName + @"', function (gltf) {
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

                                applyLock();
                                if (lockedWristName && !bonesByName[lockedWristName])
                                    showError('Stiff Arm Poses: wrist bone not found: ""' + lockedWristName + '""');

                                window._clips = gltf.animations || [];
                                if (window._clips.length > 0) {
                                    mixer = new THREE.AnimationMixer(gltf.scene);
                                    currentClip = window._clips[0];
                                    currentAction = mixer.clipAction(currentClip);
                                    currentAction.play();
                                }
                            } catch (innerErr) {
                                showError('Error setting up character preview: ' + innerErr.message);
                            }
                        }, undefined, function (error) {
                            showError('Failed to load character preview: ' + (error && error.message ? error.message : error));
                        });

                        window.addEventListener('resize', function () {
                            camera.aspect = window.innerWidth / window.innerHeight;
                            camera.updateProjectionMatrix();
                            renderer.setSize(window.innerWidth, window.innerHeight);
                        });

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            if (mixer && !paused) mixer.update(delta);
                            applyLock();
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
