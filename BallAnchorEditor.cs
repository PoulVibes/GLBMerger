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
    // Places a fixed "ball anchor" locator under each hand bone: a plain
    // transform child node (no mesh, no animation channels) marking exactly
    // where the football should sit relative to that hand. Saved straight
    // into the model, so the game reads the offset from the file itself
    // instead of it living only as an in-game debug value that has to be
    // re-tuned by eye, per model, every time the rig changes.
    //
    // Deliberately NOT tied to any particular animation clip (unlike
    // JointOrientationForm) - an anchor is a single constant offset from the
    // hand bone, and the game always re-derives the ball's world position by
    // carrying that same local offset through whatever pose the hand is in
    // that frame (see ModelRenderer::HandWorld), so there is nothing to
    // author "per clip" here.
    //
    // The live preview shows the actual football prop (Assets/Football1.glb,
    // copied alongside the exe) instead of a placeholder shape, and previews
    // it in one of three modes at a time - Right hand, Left hand, or Merged.
    // Selecting Right or Left animates the ball across to that hand in real
    // time using the game's own handoff curve (Renderer::CarriedBallPose's
    // position-lerp/rotation-slerp-around-a-smoothstepped-window, duplicated
    // exactly in the preview's JS below), so it plays out the same swap the
    // game shows instead of an instant snap. Merged doesn't move the ball at
    // all - it just previews the shared mid-handoff arm pose (see
    // CurrentArmCategory) with the ball left wherever Right/Left last
    // animated it to.
    public class BallAnchorForm : Form
    {
        private readonly ModelRoot _model;

        private ComboBox _animDropdown = null!;
        private RadioButton _radioRight = null!, _radioLeft = null!, _radioMerged = null!;
        private Panel _handEditPanel = null!;
        private NumericUpDown _numPosX = null!, _numPosY = null!, _numPosZ = null!;
        private TrackBar _sliderRotX = null!, _sliderRotY = null!, _sliderRotZ = null!;
        private Label _lblRotX = null!, _lblRotY = null!, _lblRotZ = null!;
        private Label _lblStatus = null!, _lblBoneRight = null!, _lblBoneLeft = null!;
        private Button _btnPause = null!, _btnSave = null!, _btnReset = null!, _btnCopyAnchor = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;
        private bool _suppressEvents;

        private readonly Node? _rightHand;
        private readonly Node? _leftHand;
        private readonly Node? _rightElbow, _leftElbow;
        private readonly Node? _rightShoulder, _leftShoulder;

        // Arm-pose editing: which arm pose the ball's carry/handoff
        // animation locks to, per ModelRenderer::ArmIKConfig - authored here
        // alongside the ball offset itself since both only make sense
        // together (a correctly-placed ball on a badly-bent arm still looks
        // wrong). Driven entirely by the SAME Right/Left/Merged switch that
        // drives the ball preview above (see CurrentArmCategory) - there is
        // no separate side/pose selector for this section anymore.
        private readonly TrackBar[,] _armSliders = new TrackBar[3, 3];        // [joint(shoulder/elbow/wrist), axis(x/y/z)]
        private readonly Label[,] _armSliderLabels = new Label[3, 3];
        private Button _btnArmSave = null!, _btnArmReset = null!, _btnCopyArm = null!, _btnCopyFromCarry = null!;
        private Label _lblArmStatus = null!;

        private struct JointDeg { public int X, Y, Z; }
        // [category (0=rightCarry,1=leftCarry,2=rightMeet,3=leftMeet), joint (0=shoulder,1=elbow,2=wrist)]
        private readonly JointDeg[,] _armDeg = new JointDeg[4, 3];

        // Right/left each have their own independent working values (position
        // in raw file units, rotation in degrees) - only the selected radio
        // button's set is shown on the sliders, but both stay live in the 3D
        // preview (as the two ends of the animated hand swap) so left/right
        // symmetry is easy to eyeball even while editing just one.
        private (float X, float Y, float Z) _rightPos, _leftPos;
        private (int X, int Y, int Z) _rightRotDeg, _leftRotDeg;

        // Preview-only: mirrors the game's Renderer::CarriedBallPose
        // kHandoffHalfWidth constant (currently hardcoded to 0.15f in the
        // C++). Not yet written back to the model or read by the game - see
        // the class comment. Defaults to match that hardcoded value so the
        // preview reflects real in-game behavior out of the box. No longer
        // user-editable (there's no Transition panel to host a control for
        // it anymore) - it only shapes the automatic hand-swap animation.
        private const float DefaultHalfWidth = 0.15f;

        private static readonly string[] RightNames = { "righthand", "hand_r", "r_hand", "hand.r",
            "wrist_r", "mixamorig:righthand", "handr", "rwrist" };
        private static readonly string[] LeftNames = { "lefthand", "hand_l", "l_hand", "hand.l",
            "wrist_l", "mixamorig:lefthand", "handl", "lwrist" };
        private static readonly string[] WeakNames = { "hand", "wrist", "palm" };

        // Distinct per side - SharpGLTF's NodeBuilder.IsValidArmature (run by
        // AddSkinnedMesh whenever a merge rebuilds the skinned mesh's joint
        // list) requires every node name under the armature root to be
        // unique, not just skin joints. Saving both anchors as the same
        // literal "BallAnchor" passed that check right up until the first
        // re-merge of the saved file, which failed it and crashed with
        // ArgumentException(joints). ModelRenderer::FindHandJoints on the
        // game side only ever does a "contains 'ballanchor'" substring match
        // (see ModelRenderer.cpp), so either suffix still resolves correctly
        // there - only this tool's own uniqueness requirement needed fixing.
        private const string RightAnchorNodeName = "BallAnchorRight";
        private const string LeftAnchorNodeName = "BallAnchorLeft";

        // category index -> (marker prefix, is-right-side). Mirrors
        // ModelRenderer::ApplyBakedCarryPoses's kCategories exactly - same
        // prefixes, same side assignment - so a marker saved here is found
        // there.
        private static readonly (string Prefix, bool Right)[] ArmCategories =
        {
            ("BallCarry", true), ("BallCarry", false), ("BallMeet", true), ("BallMeet", false)
        };
        private static readonly string[] JointNames = { "Shoulder", "Elbow", "Wrist" };

        public BallAnchorForm(ModelRoot model, bool darkMode = false)
        {
            _model = model;
            _rightHand = FindHandNode(right: true);
            _leftHand = FindHandNode(right: false);
            _rightElbow = _rightHand?.VisualParent;
            _leftElbow = _leftHand?.VisualParent;
            _rightShoulder = _rightElbow?.VisualParent;
            _leftShoulder = _leftElbow?.VisualParent;

            Text = "Ball Anchor";
            Width = 950;
            Height = 820;
            MinimumSize = new System.Drawing.Size(700, 600);
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();
            LoadExistingAnchor(right: true);
            LoadExistingAnchor(right: false);
            LoadAllExistingArmPoses();
            PopulateAnimationList();
            RefreshUiFromState();
            RefreshArmUiFromState();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        // Same alias-matching convention ModelRenderer::FindHandJoints uses on
        // the C++ side, so this tool resolves the exact same bone the game
        // will look under for the anchor it saves here.
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

        private static Node? FindExistingAnchor(Node? hand)
        {
            if (hand == null) return null;
            foreach (var child in hand.VisualChildren)
                if (child.Name != null && child.Name.IndexOf("ballanchor", StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            return null;
        }

        private void LoadExistingAnchor(bool right)
        {
            var hand = right ? _rightHand : _leftHand;
            var anchor = FindExistingAnchor(hand);
            if (anchor == null) return;

            // A node saved via a plain SRT translation/rotation (as this tool
            // writes) round-trips fine, but a node that's passed through a
            // merge - which rebuilds the hierarchy from each source node's
            // raw LocalMatrix (see GlbMergeService.BuildNodeTree) - comes
            // back as a matrix-representation AffineTransform instead.
            // .Rotation/.Translation only work on the decomposed (SRT) form;
            // GetDecomposed() converts either representation into that form,
            // so it's safe to call unconditionally here.
            var t = anchor.LocalTransform.GetDecomposed();
            var pos = t.Translation;
            var deg = QuaternionToDegrees(t.Rotation);

            if (right) { _rightPos = (pos.X, pos.Y, pos.Z); _rightRotDeg = deg; }
            else       { _leftPos  = (pos.X, pos.Y, pos.Z); _leftRotDeg  = deg; }
        }

        // Marker for joint J is saved as a SIBLING of the real joint it
        // overrides, parented under that real joint's own real parent - so
        // the marker's own local rotation, relative to that shared parent,
        // IS directly the absolute value LockArmPose wants (see ArmIK.cpp
        // and ModelRenderer::ApplyBakedCarryPoses on the game side, which
        // looks for exactly this).
        private Node? ArmMarkerParentFor(bool right, int joint)
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

        private static Node? FindExistingChildByName(Node? parent, string name)
        {
            if (parent == null) return null;
            foreach (var child in parent.VisualChildren)
                if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                    return child;
            return null;
        }

        private static string ArmMarkerName(int category, int joint) =>
            $"{ArmCategories[category].Prefix}{JointNames[joint]}{(ArmCategories[category].Right ? "R" : "L")}";

        private void LoadAllExistingArmPoses()
        {
            var found = new bool[4];
            for (int category = 0; category < 4; category++)
            {
                bool right = ArmCategories[category].Right;
                for (int joint = 0; joint < 3; joint++)
                {
                    var parent = ArmMarkerParentFor(right, joint);
                    var marker = FindExistingChildByName(parent, ArmMarkerName(category, joint));
                    if (marker == null) continue;

                    found[category] = true;
                    var deg = QuaternionToDegrees(marker.LocalTransform.GetDecomposed().Rotation);
                    _armDeg[category, joint] = new JointDeg { X = deg.X, Y = deg.Y, Z = deg.Z };
                }
            }

            // Merged (category 2/3, "BallMeet") is authored as ONE shared
            // pose - right is canonical, left always mirrors it (see
            // CurrentArmCategory/MirrorMeetIntoLeft). If nothing was ever
            // saved for it, seed the canonical side from the right-hand
            // Carry pose rather than identity: a handoff's meeting point is
            // much closer to "both arms already extended holding the ball"
            // than "arms hanging at rest", and starting from 0,0,0 was part
            // of why a fresh Merged edit looked like it wasn't doing
            // anything - the pose really was empty, on top of the preview
            // visibility bug fixed by PushMeetPreview below.
            if (!found[2])
                for (int joint = 0; joint < 3; joint++)
                    _armDeg[2, joint] = _armDeg[0, joint];
            MirrorMeetIntoLeft();
        }

        private static JointDeg Mirror(JointDeg d) => new JointDeg { X = d.X, Y = -d.Y, Z = -d.Z };

        // category 3 (leftMeet) is never independently edited or saved from
        // its own sliders - it's always a mirror of category 2 (rightMeet),
        // same X-same/Y,Z-negate convention as CopyArmFromOtherHand, since
        // both arms genuinely do need to be symmetric at the point they
        // meet to hand the ball off.
        private void MirrorMeetIntoLeft()
        {
            for (int joint = 0; joint < 3; joint++)
                _armDeg[3, joint] = Mirror(_armDeg[2, joint]);
        }

        // Inverse of ComputeOffsetQuaternion below - decomposes back to X/Y/Z
        // degrees so re-opening this dialog restores the sliders an existing
        // anchor was saved with, rather than resetting to zero.
        private static (int X, int Y, int Z) QuaternionToDegrees(Quaternion q)
        {
            // Standard ZYX-composed Euler extraction matching
            // ComputeOffsetQuaternion's rotZ * rotY * rotX composition order.
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

        private static FlowLayoutPanel MakeRow() => new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 6),
        };

        // Stacks through a top-down FlowLayoutPanel instead of hand-computed
        // Left/Top offsets - every control sizes itself against its ACTUAL
        // rendered text/font instead of an assumed 96-DPI pixel count, so
        // nothing overlaps or clips if the system font/DPI turns out bigger
        // than what these numbers were eyeballed against. _handEditPanel is
        // a flow child too: FlowLayoutPanel skips a child entirely when its
        // Visible is false, so toggling mode correctly collapses the space
        // it occupied instead of leaving a gap the old fixed-Top layout
        // didn't have to account for.
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


            flow.Controls.Add(new Label { Text = "Preview:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var modeRow = MakeRow();
            _radioRight  = new RadioButton { Text = "Right Hand", AutoSize = true, Checked = true, Margin = new Padding(3, 4, 16, 3) };
            _radioLeft   = new RadioButton { Text = "Left Hand",  AutoSize = true, Margin = new Padding(3, 4, 16, 3) };
            _radioMerged = new RadioButton { Text = "Merged",     AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            // Switching Right<->Left animates the ball across in real time
            // using the game's own handoff curve (see setMode/animate in the
            // preview JS) instead of snapping instantly, so this switch
            // doubles as watching the actual in-game hand swap. Merged
            // previews the shared meet-point arm pose without moving the
            // ball at all - it just stays wherever Right/Left last left it.
            _radioRight.CheckedChanged  += (s, e) => { if (_radioRight.Checked)  OnModeChanged(); };
            _radioLeft.CheckedChanged   += (s, e) => { if (_radioLeft.Checked)   OnModeChanged(); };
            _radioMerged.CheckedChanged += (s, e) => { if (_radioMerged.Checked) OnModeChanged(); };
            modeRow.Controls.Add(_radioRight);
            modeRow.Controls.Add(_radioLeft);
            modeRow.Controls.Add(_radioMerged);
            flow.Controls.Add(modeRow);

            _lblBoneRight = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0), Margin = new Padding(3, 0, 3, 2),
                Text = "Right bone: " + (_rightHand?.Name ?? "NOT FOUND")
            };
            _lblBoneLeft = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0), Margin = new Padding(3, 0, 3, 10),
                Text = "Left bone: " + (_leftHand?.Name ?? "NOT FOUND")
            };
            flow.Controls.Add(_lblBoneRight);
            flow.Controls.Add(_lblBoneLeft);

            flow.Controls.Add(new Label { Text = "Preview Animation:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var animRow = MakeRow();
            _animDropdown = new ComboBox { Width = 190, Height = 26, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 8, 10) };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", AutoSize = true, Margin = new Padding(3, 3, 3, 10) };
            _btnPause.Click += (s, e) => TogglePause();
            animRow.Controls.Add(_animDropdown);
            animRow.Controls.Add(_btnPause);
            flow.Controls.Add(animRow);

            // Both panels below size themselves from named row-pitch
            // constants rather than hand-picked per-call offsets - the
            // previous version kept the OLD tight offsets (rows only 30-70px
            // apart) even after the rest of the form moved to generously-
            // spaced rows elsewhere, which is exactly the kind of drift that
            // caused the stiff-arm editor's slider clipping. Deriving each
            // panel's own fixed Height from the same constants the rows use
            // can't drift out of sync the same way.
            const int kPosRowPitch  = 34;   // X/Y/Z position fields, label+numeric on one row
            const int kRotLabelToSlider = 26;
            const int kRotSliderHeight  = 45;
            const int kRotGapAfterSlider = 16;
            const int kRotRowPitch = kRotLabelToSlider + kRotSliderHeight + kRotGapAfterSlider;

            // ---- Right/Left edit panel ----
            int posBlockTop = 24;                                   // below lblPos
            int rotBlockTop = posBlockTop + 3 * kPosRowPitch + 14;   // gap after position block
            int handPanelHeight = rotBlockTop + 3 * kRotRowPitch + 10;

            _handEditPanel = new Panel { Width = 310, Height = handPanelHeight, Margin = new Padding(0) };
            var lblPos = new Label { Text = "Position (relative to hand bone):", Left = 0, Top = 0, AutoSize = true };
            var lblX = new Label { Text = "X:", Left = 0, Top = posBlockTop, Width = 20 };
            var lblY = new Label { Text = "Y:", Left = 0, Top = posBlockTop + kPosRowPitch, Width = 20 };
            var lblZ = new Label { Text = "Z:", Left = 0, Top = posBlockTop + 2 * kPosRowPitch, Width = 20 };
            _numPosX = MakeNumeric(24, posBlockTop - 2);
            _numPosY = MakeNumeric(24, posBlockTop + kPosRowPitch - 2);
            _numPosZ = MakeNumeric(24, posBlockTop + 2 * kPosRowPitch - 2);

            (_lblRotX, _sliderRotX) = MakeSlider("X Rotation", rotBlockTop);
            (_lblRotY, _sliderRotY) = MakeSlider("Y Rotation", rotBlockTop + kRotRowPitch);
            (_lblRotZ, _sliderRotZ) = MakeSlider("Z Rotation", rotBlockTop + 2 * kRotRowPitch);

            _handEditPanel.Controls.AddRange(new Control[]
            {
                lblPos, lblX, lblY, lblZ, _numPosX, _numPosY, _numPosZ,
                _lblRotX, _sliderRotX, _lblRotY, _sliderRotY, _lblRotZ, _sliderRotZ
            });
            flow.Controls.Add(_handEditPanel);

            _btnSave = new Button { Text = "Save Anchor for This Hand", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnSave.Click += (s, e) => SaveAnchor();

            _btnReset = new Button { Text = "Reset This Hand to Origin", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnReset.Click += (s, e) => ResetCurrentHand();

            _btnCopyAnchor = new Button { Text = "Copy Anchor from Other Hand", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnCopyAnchor.Click += (s, e) => CopyAnchorFromOtherHand();

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0),
                Margin = new Padding(3, 6, 3, 10), ForeColor = System.Drawing.Color.LightGreen
            };

            flow.Controls.Add(_btnSave);
            flow.Controls.Add(_btnReset);
            flow.Controls.Add(_btnCopyAnchor);
            flow.Controls.Add(_lblStatus);

            BuildArmPoseSection(flow);

            var btnClose = new Button { Text = "Done", AutoSize = true, Margin = new Padding(3, 4, 3, 4), DialogResult = DialogResult.OK };
            flow.Controls.Add(btnClose);
            AcceptButton = btnClose;

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        // Carry/meet arm-pose editor: which of the 4 ArmIKConfig poses is
        // being edited, plus the 3 joint x 3 axis sliders for it. A
        // correctly-placed ball anchor still looks wrong on a badly-bent
        // arm, so this lives in the same dialog as the ball offset rather
        // than a separate tool. Row geometry is computed from named
        // constants (see StiffArmPoseForm's own joint GroupBoxes, where an
        // earlier hand-picked mismatch between row pitch and a fixed
        // GroupBox height actually clipped the third slider) so the box
        // height can't drift out of sync with the rows again.
        // Wrapped in its own top-down FlowLayoutPanel (rather than adding
        // straight into the outer `flow`) so the whole section can be shown
        // or hidden as one unit via _armPosePanel.Visible - it doesn't apply
        // during Transition, same as the ball position panel next to it.
        // Side comes from the SAME _radioRight/_radioLeft the ball anchor
        // above uses (see CurrentArmCategory) - only Carry/Meet is this
        // section's own choice.
        private FlowLayoutPanel _armPosePanel = null!;

        private void BuildArmPoseSection(FlowLayoutPanel flow)
        {
            _armPosePanel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
            };
            var armFlow = _armPosePanel;

            armFlow.Controls.Add(new Label { Text = "Arm Pose:", AutoSize = true, Margin = new Padding(3, 10, 3, 4) });

            var lblArmInfo = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0), Margin = new Padding(3, 0, 3, 10),
                Text = "Right/Left above = the carry pose that hand locks to while holding the ball. " +
                       "Merged above = the single shared pose both arms ease toward mid-handoff - edited once, " +
                       "right-handed, and mirrored onto the left arm automatically, since they meet in the middle " +
                       "symmetrically. The preview always shows Merged at full strength, with the ball staying " +
                       "wherever Right/Left last left it."
            };
            armFlow.Controls.Add(lblArmInfo);

            const int kRowHeaderTop   = 26;
            const int kLabelToSlider  = 26;
            const int kSliderHeight   = 45;
            const int kGapAfterSlider = 14;
            const int kRowPitch = kLabelToSlider + kSliderHeight + kGapAfterSlider;
            const int kBottomPad = 20;
            int groupHeight = kRowHeaderTop + 3 * kRowPitch + kBottomPad;

            for (int joint = 0; joint < 3; joint++)
            {
                var group = new GroupBox { Text = JointNames[joint], Width = 320, Height = groupHeight, Margin = new Padding(0, 0, 0, 10) };
                for (int axis = 0; axis < 3; axis++)
                {
                    int a = axis;
                    int rowTop = kRowHeaderTop + axis * kRowPitch;
                    string axisLabel = axis == 0 ? "X Rotation" : axis == 1 ? "Y Rotation" : "Z Rotation";
                    var lbl = new Label { Text = axisLabel + ": 0°", Left = 10, Top = rowTop, AutoSize = true };
                    var slider = new TrackBar
                    {
                        Left = 10, Top = rowTop + kLabelToSlider, Width = 290, Height = kSliderHeight,
                        Minimum = -180, Maximum = 180, Value = 0, TickFrequency = 30
                    };
                    slider.ValueChanged += (s, e) => { lbl.Text = axisLabel + $": {slider.Value}°"; OnArmSliderChanged(); };
                    group.Controls.Add(lbl);
                    group.Controls.Add(slider);
                    _armSliders[joint, axis] = slider;
                    _armSliderLabels[joint, axis] = lbl;
                }
                armFlow.Controls.Add(group);
            }

            _btnArmSave = new Button { Text = "Save This Arm Pose", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnArmSave.Click += (s, e) => SaveArmPose();

            _btnArmReset = new Button { Text = "Reset This Arm Pose to 0", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnArmReset.Click += (s, e) => ResetArmPose();

            _btnCopyArm = new Button { Text = "Copy Arm Pose from Other Hand", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnCopyArm.Click += (s, e) => CopyArmFromOtherHand();

            // Merged-only: seeds the shared meet pose from the right-hand
            // Carry pose already tuned above, since a fresh Merged edit
            // otherwise starts from whatever LoadAllExistingArmPoses seeded
            // it with once and has no easy way to re-sync later if Carry
            // gets retuned afterward.
            _btnCopyFromCarry = new Button { Text = "Copy Both Arms from Carry", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnCopyFromCarry.Click += (s, e) => CopyMergedFromCarry();

            _lblArmStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0),
                Margin = new Padding(3, 6, 3, 10), ForeColor = System.Drawing.Color.LightGreen
            };

            armFlow.Controls.Add(_btnArmSave);
            armFlow.Controls.Add(_btnArmReset);
            armFlow.Controls.Add(_btnCopyArm);
            armFlow.Controls.Add(_btnCopyFromCarry);
            armFlow.Controls.Add(_lblArmStatus);

            flow.Controls.Add(_armPosePanel);
        }

        // Unitless (same reasoning as JointOrientationForm's position
        // controls): the file's own translation units vary by rig scale, so
        // a fixed +/-9999 range works regardless of how big or small this
        // particular model is. Integer-only per-click step, not fractional -
        // fine-grained placement is still reachable by typing a value
        // directly, this just keeps the up/down arrows from having to be
        // clicked dozens of times to see a visible move.
        private NumericUpDown MakeNumeric(int left, int top)
        {
            var numeric = new NumericUpDown
            {
                Left = left, Top = top, Width = 120,
                Minimum = -9999m, Maximum = 9999m, DecimalPlaces = 0, Increment = 1m, Value = 0m
            };
            numeric.ValueChanged += (s, e) => OnValueChanged();
            return numeric;
        }

        private (Label, TrackBar) MakeSlider(string text, int top)
        {
            // AutoSize=true (the Label default) rather than false+assumed
            // height - a frozen explicit height can end up too short once
            // the control inherits its real font from the parent/theme,
            // which happens after construction, not before.
            var lbl = new Label { Text = $"{text}: 0°", Left = 0, Top = top, AutoSize = true };
            var slider = new TrackBar
            {
                Left = 0, Top = top + 20, Width = 300, Height = 45,
                Minimum = -180, Maximum = 180, Value = 0,
                TickFrequency = 30
            };
            slider.ValueChanged += (s, e) => { lbl.Text = $"{text}: {slider.Value}°"; OnValueChanged(); };
            return (lbl, slider);
        }

        private void OnModeChanged()
        {
            bool merged = _radioMerged.Checked;
            // The ball anchor is per-hand (Right/Left only) - Merged has no
            // anchor of its own, the ball just stays wherever it already
            // was, so editing/saving an anchor doesn't apply there.
            _handEditPanel.Visible = !merged;
            _btnSave.Enabled = !merged;
            _btnReset.Enabled = !merged;
            _btnCopyAnchor.Enabled = !merged;
            RefreshUiFromState();
            RefreshArmUiFromState();
            PushMode();
        }

        // Pushes whichever hand's stored values are live onto the controls,
        // without re-triggering OnValueChanged (which would overwrite the
        // OTHER hand's stored state with whatever the fields happened to show
        // a moment before the switch). Both hands' transforms are always sent
        // to the preview regardless of mode, since the animated hand-swap
        // needs both ends live even while only one (or neither) is the one
        // being edited.
        private void RefreshUiFromState()
        {
            bool right = _radioRight.Checked;
            var pos = right ? _rightPos : _leftPos;
            var rot = right ? _rightRotDeg : _leftRotDeg;

            _suppressEvents = true;
            _numPosX.Value = Math.Clamp((decimal)pos.X, _numPosX.Minimum, _numPosX.Maximum);
            _numPosY.Value = Math.Clamp((decimal)pos.Y, _numPosY.Minimum, _numPosY.Maximum);
            _numPosZ.Value = Math.Clamp((decimal)pos.Z, _numPosZ.Minimum, _numPosZ.Maximum);
            _sliderRotX.Value = rot.X;
            _sliderRotY.Value = rot.Y;
            _sliderRotZ.Value = rot.Z;
            _lblRotX.Text = $"X Rotation: {rot.X}°";
            _lblRotY.Text = $"Y Rotation: {rot.Y}°";
            _lblRotZ.Text = $"Z Rotation: {rot.Z}°";
            _suppressEvents = false;

            PushLiveTransform(right: true);
            PushLiveTransform(right: false);
        }

        private void OnValueChanged()
        {
            if (_suppressEvents) return;
            bool right = _radioRight.Checked;
            var pos = ((float)_numPosX.Value, (float)_numPosY.Value, (float)_numPosZ.Value);
            var rot = (_sliderRotX.Value, _sliderRotY.Value, _sliderRotZ.Value);
            if (right) { _rightPos = pos; _rightRotDeg = rot; }
            else       { _leftPos  = pos; _leftRotDeg  = rot; }

            PushLiveTransform(right);
        }

        // 'right'/'left' animate the ball across to that hand over real
        // time using the game's own handoff curve (see setMode/animate in
        // the preview JS) - this is no longer an instant snap. 'merged'
        // doesn't move the ball at all; it just leaves it wherever Right/
        // Left last animated it to, while the arm-lock preview separately
        // forces itself into the shared Merged pose (see setMeetPreview).
        private void PushMode()
        {
            if (!_viewerReady) return;
            string mode = _radioMerged.Checked ? "merged" : (_radioRight.Checked ? "right" : "left");
            _webView.CoreWebView2.ExecuteScriptAsync($"setMode('{mode}');");
        }

        private static string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

        private void PushLiveTransform(bool right)
        {
            if (!_viewerReady) return;
            var boneName = right ? _rightHand?.Name : _leftHand?.Name;
            if (boneName == null) return;

            var pos = right ? _rightPos : _leftPos;
            var rot = right ? _rightRotDeg : _leftRotDeg;
            var q = ComputeOffsetQuaternion(rot.X, rot.Y, rot.Z);
            string side = right ? "right" : "left";

            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setAnchorTransform('{side}', '{EscapeJs(boneName)}', {Inv(pos.X)}, {Inv(pos.Y)}, {Inv(pos.Z)}, " +
                $"{Inv(q.X)}, {Inv(q.Y)}, {Inv(q.Z)}, {Inv(q.W)});");
        }

        private void SaveAnchor()
        {
            bool right = _radioRight.Checked;
            var hand = right ? _rightHand : _leftHand;
            if (hand == null)
            {
                MessageBox.Show(this, "No bone resolved for this hand — cannot save.", "Ball Anchor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var pos = right ? _rightPos : _leftPos;
            var rot = right ? _rightRotDeg : _leftRotDeg;

            var anchorName = right ? RightAnchorNodeName : LeftAnchorNodeName;
            var anchor = FindExistingAnchor(hand) ?? hand.CreateNode(anchorName);
            anchor.Name = anchorName;
            anchor.WithLocalTranslation(new Vector3(pos.X, pos.Y, pos.Z));
            anchor.WithLocalRotation(ComputeOffsetQuaternion(rot.X, rot.Y, rot.Z));

            _lblStatus.Text = $"Saved {(right ? "right" : "left")}-hand anchor under \"{hand.Name}\".";
        }

        // SharpGLTF has no direct "detach node" API, so this can't delete the
        // anchor node outright - it re-saves it at the hand's own origin
        // (zero offset, zero rotation) instead, which is functionally a no-op
        // ball placement rather than a leftover stale value.
        private void ResetCurrentHand()
        {
            bool right = _radioRight.Checked;
            if (right) { _rightPos = (0, 0, 0); _rightRotDeg = (0, 0, 0); }
            else       { _leftPos  = (0, 0, 0); _leftRotDeg  = (0, 0, 0); }
            RefreshUiFromState();
            _lblStatus.Text = "Reset — remember to Save to write this to the model.";
        }

        // Plain copy, deliberately NOT mirrored: unlike ArmPoseConfig's
        // shoulder/elbow/wrist rotations (whose X-same/Y,Z-negated mirror
        // convention is documented against ArmRom.h's own axis meanings -
        // see CopyArmFromOtherHand below), there's no established axis
        // convention for a ball anchor's raw position/rotation offset on
        // this rig to mirror correctly by. Safer to copy verbatim and let
        // the user flip whichever axis needs it than to guess a sign
        // convention that could be silently wrong.
        private void CopyAnchorFromOtherHand()
        {
            bool right = _radioRight.Checked;
            if (right) { _rightPos = _leftPos; _rightRotDeg = _leftRotDeg; }
            else       { _leftPos  = _rightPos; _leftRotDeg  = _rightRotDeg; }
            RefreshUiFromState();
            _lblStatus.Text = "Copied from other hand (unmirrored) — remember to Save.";
        }

        // Selecting Merged edits one shared pose (category 2, right-
        // canonical) rather than a per-side one, since the whole point is
        // both arms meeting symmetrically. Right/Left each edit their own
        // independent Carry pose - nothing requires a right-handed carry to
        // mirror a left-handed one.
        private int CurrentArmCategory()
        {
            if (_radioMerged.Checked) return 2;
            return _radioRight.Checked ? 0 : 1;
        }

        private void RefreshArmUiFromState()
        {
            int category = CurrentArmCategory();
            bool merged = _radioMerged.Checked;
            // Merged mirrors right->left automatically - there is no "other
            // hand" version to copy from anymore. Copy-from-Carry is the
            // reverse situation: only meaningful for Merged (Carry is the
            // source, not a target).
            _btnCopyArm.Enabled = !merged;
            _btnCopyFromCarry.Enabled = merged;

            _suppressEvents = true;
            for (int joint = 0; joint < 3; joint++)
            {
                var d = _armDeg[category, joint];
                _armSliders[joint, 0].Value = d.X;
                _armSliders[joint, 1].Value = d.Y;
                _armSliders[joint, 2].Value = d.Z;
                _armSliderLabels[joint, 0].Text = $"X Rotation: {d.X}°";
                _armSliderLabels[joint, 1].Text = $"Y Rotation: {d.Y}°";
                _armSliderLabels[joint, 2].Text = $"Z Rotation: {d.Z}°";
            }
            _suppressEvents = false;

            PushMeetPreview();
        }

        // blendT (the ball's own right<->left position) only reaches 0.5 -
        // the ONLY value where the game's own arm-lock weighting (see
        // applyCarryLocks below, mirroring ModelRenderer::UpdateInstance)
        // has both arms fully in their Meet pose at once - transiently,
        // mid-animation. Without this override, editing Merged while the
        // ball happened to be sitting at an extreme (blendT 0 or 1, i.e.
        // Right or Left) moved sliders that had zero visible effect: not a
        // rendering bug, just previewing at a blend value where the pose
        // being edited carries zero weight. This forces the arm-lock's own
        // blend input to 0.5 while Merged is selected (it does not touch
        // the ball position's own blendT, which stays wherever Right/Left
        // last left it).
        private void PushMeetPreview()
        {
            if (!_viewerReady) return;
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setMeetPreview({(_radioMerged.Checked ? "true" : "false")});");
        }

        private void OnArmSliderChanged()
        {
            if (_suppressEvents) return;
            int category = CurrentArmCategory();
            for (int joint = 0; joint < 3; joint++)
            {
                _armDeg[category, joint] = new JointDeg
                {
                    X = _armSliders[joint, 0].Value,
                    Y = _armSliders[joint, 1].Value,
                    Z = _armSliders[joint, 2].Value
                };
            }
            if (category == 2)
            {
                MirrorMeetIntoLeft();
                PushArmPose(2);
                PushArmPose(3);
            }
            else
            {
                PushArmPose(category);
            }
        }

        // category -> JS side key ('right'/'left' carry or meet), matching
        // setCarryPose's expected argument on the preview side.
        private static string ArmCategoryJsKey(int category) => category switch
        {
            0 => "rightCarry", 1 => "leftCarry", 2 => "rightMeet", _ => "leftMeet"
        };

        private void PushArmPose(int category)
        {
            if (!_viewerReady) return;
            var sQ = ComputeOffsetQuaternion(_armDeg[category, 0].X, _armDeg[category, 0].Y, _armDeg[category, 0].Z);
            var eQ = ComputeOffsetQuaternion(_armDeg[category, 1].X, _armDeg[category, 1].Y, _armDeg[category, 1].Z);
            var wQ = ComputeOffsetQuaternion(_armDeg[category, 2].X, _armDeg[category, 2].Y, _armDeg[category, 2].Z);
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setCarryPose('{ArmCategoryJsKey(category)}', " +
                $"{Inv(sQ.X)},{Inv(sQ.Y)},{Inv(sQ.Z)},{Inv(sQ.W)}, " +
                $"{Inv(eQ.X)},{Inv(eQ.Y)},{Inv(eQ.Z)},{Inv(eQ.W)}, " +
                $"{Inv(wQ.X)},{Inv(wQ.Y)},{Inv(wQ.Z)},{Inv(wQ.W)});");
        }

        private void PushAllArmPoses()
        {
            for (int category = 0; category < 4; category++) PushArmPose(category);
        }

        private bool SaveArmCategory(int category)
        {
            bool right = ArmCategories[category].Right;
            var wrist = right ? _rightHand : _leftHand;
            if (wrist == null) return false;

            for (int joint = 0; joint < 3; joint++)
            {
                var parent = ArmMarkerParentFor(right, joint);
                if (parent == null) continue;

                var name = ArmMarkerName(category, joint);
                var marker = FindExistingChildByName(parent, name) ?? parent.CreateNode(name);
                marker.Name = name;
                var d = _armDeg[category, joint];
                marker.WithLocalRotation(ComputeOffsetQuaternion(d.X, d.Y, d.Z));
            }
            return true;
        }

        private void SaveArmPose()
        {
            int category = CurrentArmCategory();
            if (category == 2)
            {
                MirrorMeetIntoLeft();
                bool okRight = SaveArmCategory(2);
                bool okLeft = SaveArmCategory(3);
                if (!okRight || !okLeft)
                {
                    MessageBox.Show(this, "No arm chain resolved for one or both hands — cannot save.", "Ball Anchor",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _lblArmStatus.Text = "Saved merged pose (both arms, mirrored).";
                return;
            }

            if (!SaveArmCategory(category))
            {
                MessageBox.Show(this, "No arm chain resolved for this hand — cannot save.", "Ball Anchor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool right = ArmCategories[category].Right;
            _lblArmStatus.Text = $"Saved {ArmCategories[category].Prefix.Replace("Ball", "")} pose for the {(right ? "right" : "left")} arm.";
        }

        private void ResetArmPose()
        {
            int category = CurrentArmCategory();
            for (int joint = 0; joint < 3; joint++) _armDeg[category, joint] = new JointDeg();
            if (category == 2)
            {
                MirrorMeetIntoLeft();
                RefreshArmUiFromState();
                PushArmPose(2);
                PushArmPose(3);
            }
            else
            {
                RefreshArmUiFromState();
                PushArmPose(category);
            }
            _lblArmStatus.Text = "Reset — remember to Save to write this to the model.";
        }

        // Mirror guess: X (flex/extend) keeps the same sign, Y (twist) and Z
        // (raise/cross) flip sign - the same convention ArmIKConfig's own
        // rightStiff was originally guessed from a tuned leftStiff (see
        // ModelRenderer.h and StiffArmPoseForm.CopyFromOtherArm). Copies the
        // SAME pose type on the other side - rightCarry from leftCarry,
        // rightMeet from leftMeet, never across the carry/meet divide, since
        // those are different poses entirely. Only ever a starting point;
        // magnitudes commonly differ per side by up to ~2x in practice.
        private void CopyArmFromOtherHand()
        {
            int category = CurrentArmCategory();
            if (category == 2) return; // Merged mirrors automatically; button is disabled for this case anyway
            int otherCategory = category ^ 1;   // 0<->1 (carry)
            for (int joint = 0; joint < 3; joint++)
            {
                var src = _armDeg[otherCategory, joint];
                _armDeg[category, joint] = new JointDeg { X = src.X, Y = -src.Y, Z = -src.Z };
            }
            RefreshArmUiFromState();
            PushArmPose(category);
            _lblArmStatus.Text = "Copied from other hand (mirrored guess) — remember to Save.";
        }

        // Seeds the Merged pose (category 2, right-canonical) straight from
        // the right-hand Carry pose (category 0), then mirrors it onto the
        // left arm same as any other Merged edit (see MirrorMeetIntoLeft) -
        // both arms end up matching whatever Carry was last tuned to,
        // mirrored, rather than the identity-or-old-Meet-value starting
        // point Merged would otherwise keep. Only meaningful while Merged
        // is selected - the button is disabled otherwise.
        private void CopyMergedFromCarry()
        {
            if (!_radioMerged.Checked) return;
            for (int joint = 0; joint < 3; joint++)
                _armDeg[2, joint] = _armDeg[0, joint];
            MirrorMeetIntoLeft();
            RefreshArmUiFromState();
            PushArmPose(2);
            PushArmPose(3);
            _lblArmStatus.Text = "Copied both arms from Carry (mirrored) — remember to Save.";
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
            await _webView.EnsureCoreWebView2Async(null);

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_ball_anchor_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);

            // The ball prop ships next to the exe (see GlbMerger.csproj's
            // Assets\Football1.glb CopyToOutputDirectory item) and gets
            // copied alongside the character preview so both are reachable
            // through the same virtual host mapping.
            string ballSourcePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Football1.glb");
            string ballPreviewPath = Path.Combine(tempFolder, "glbmerger_ball_prop.glb");
            bool haveBall = File.Exists(ballSourcePath);
            if (haveBall) File.Copy(ballSourcePath, ballPreviewPath, overwrite: true);

            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (_viewerReady)
                {
                    PushLiveTransform(right: true);
                    PushLiveTransform(right: false);
                    PushMode();
                    PushAllArmPoses();
                    PushMeetPreview();
                }
            };

            // Saved once just to give the loader a file to read, same as
            // JointOrientationForm's viewer - everything after that is live
            // Three.js scene manipulation, no further save/reload.
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
                        var currentClip = null;
                        var currentAction = null;
                        var characterLoaded = false;

                        // 'right'/'left' -> an invisible Object3D parented to
                        // that hand's resolved bone, holding exactly the local
                        // position/quaternion setAnchorTransform was last
                        // called with. These are the only things that get
                        // reparented to a bone - the visible ball itself never
                        // moves in the hierarchy, it just samples these two
                        // reference points' WORLD transforms every frame (see
                        // animate() below) and is positioned directly, so a
                        // slider move is reflected on screen the very next
                        // frame with no separate 'did it actually move' step
                        // to get wrong.
                        var refs = {};
                        var pendingRefs = {};
                        var handBoneNames = {};

                        // The loaded football asset (gltf.scene) sits inside
                        // ballPivot, pre-shifted so the mesh's own geometric
                        // center - not whatever the source file's authored
                        // origin happens to be - sits at ballPivot's local
                        // origin. ballPivot itself is the thing driven every
                        // frame in animate(), and is scaled once both it and
                        // the character have loaded (see fitBallScale) so its
                        // height, relative to the character's own height in
                        // this scene, matches kBallHeightFraction below -
                        // otherwise the ball would render at Football1.glb's
                        // raw authored size, which has no reason to match how
                        // big the game actually draws it.
                        var ballObj = null;
                        var ballPivot = null;
                        var characterHeight = null;
                        var ballNativeHeight = null;
                        var currentMode = 'right';  // 'right' | 'left' | 'merged'
                        var blendT = 0;             // 0..1 - 0 = fully right hand, 1 = fully left hand
                        var halfWidth = " + Inv(DefaultHalfWidth) + @";

                        // Selecting Right/Left animates blendT from wherever
                        // it currently sits to the new extreme over real
                        // time, instead of snapping - see setMode/animate.
                        // blendAnimFrom/To/Start capture the tween; totalTime
                        // is a running clock (advanced once per frame in
                        // animate()) rather than Date.now(), so pausing the
                        // animation clip doesn't also desync the hand-swap.
                        var blendTarget = 0;
                        var blendAnimFrom = 0, blendAnimTo = 0, blendAnimStart = 0;
                        var blendAnimDuration = 0.6;
                        var totalTime = 0;

                        // While the Merged arm-pose section is being edited,
                        // force the arm-lock's own blend input to 0.5 - the
                        // only point where ModelRenderer::UpdateInstance's
                        // weighting has BOTH arms fully in their Meet pose at
                        // once (see applyCarryLocks). Deliberately a separate
                        // flag rather than writing 0.5 into blendT itself, so
                        // this never fights with the ball position, which
                        // still reads blendT exactly as the Right/Left switch
                        // last animated it to.
                        var meetPreviewActive = false;
                        window.setMeetPreview = function (active) { meetPreviewActive = active; };

                        // Mirrors the game's own ball-size convention exactly:
                        // FootballRenderer draws the carried ball at a fixed
                        // kBallScale = 0.675 world-unit height (RendererDraw.cpp)
                        // while characters render at CameraController::kPlayerHeight
                        // = Grid::kCellSize(2.4) * (6ft/3) = 4.8. Their ratio is
                        // the ball's height as a fraction of character height -
                        // a dimensionless number this preview can reproduce
                        // directly against whatever height the character model
                        // happens to render at in THIS scene.
                        var kBallHeightFraction = 0.675 / 4.8;

                        function fitBallScale() {
                            if (!ballPivot || characterHeight === null || ballNativeHeight === null) return;
                            var targetHeight = kBallHeightFraction * characterHeight;
                            ballPivot.scale.setScalar(ballNativeHeight > 1e-6 ? targetHeight / ballNativeHeight : 1);
                        }

                        // 'rightCarry'/'leftCarry'/'rightMeet'/'leftMeet' ->
                        // {shoulder, elbow, wrist} quaternions, set by
                        // setCarryPose below whenever the corresponding
                        // sliders change. Applied every frame in
                        // applyCarryLocks() using the SAME blend math
                        // ModelRenderer::UpdateInstance uses at runtime, so
                        // this preview shows the true locked-arm carry pose
                        // instead of just whatever the loaded animation clip
                        // happens to do with the arms - which is what the
                        // game actually shows too, since the arm chain is
                        // always locked while the ball is being carried (see
                        // UpdateInstance's isBallCarrier gate).
                        var carryPoses = { rightCarry: null, leftCarry: null, rightMeet: null, leftMeet: null };
                        var _tmpQuat = new THREE.Quaternion();

                        window.setCarryPose = function (category, sx,sy,sz,sw, ex,ey,ez,ew, wx,wy,wz,ww) {
                            carryPoses[category] = {
                                shoulder: new THREE.Quaternion(sx,sy,sz,sw),
                                elbow:    new THREE.Quaternion(ex,ey,ez,ew),
                                wrist:    new THREE.Quaternion(wx,wy,wz,ww)
                            };
                        };

                        function smoothStep01(t) {
                            t = Math.max(0, Math.min(1, t));
                            return t * t * (3 - 2 * t);
                        }

                        // fromCat/toCat interpolate by poseT for the TARGET
                        // pose, then that target is slerped onto the bone's
                        // CURRENT rotation (whatever mixer.update() just set
                        // it to) by weight - identical in shape to
                        // ArmIK.cpp's LockArmPose: nodes[node].r =
                        // slerp(nodes[node].r, target, weight).
                        function applyCarrySide(side, fromCat, toCat, poseT, weight) {
                            if (weight <= 0) return;
                            var from = carryPoses[fromCat], to = carryPoses[toCat];
                            if (!from || !to) return;
                            var wrist = bonesByName[handBoneNames[side]];
                            if (!wrist) return;
                            var elbow = wrist.parent;
                            var shoulder = elbow ? elbow.parent : null;
                            if (!elbow || !shoulder) return;

                            shoulder.quaternion.slerp(_tmpQuat.copy(from.shoulder).slerp(to.shoulder, poseT), weight);
                            elbow.quaternion.slerp(_tmpQuat.copy(from.elbow).slerp(to.elbow, poseT), weight);
                            wrist.quaternion.slerp(_tmpQuat.copy(from.wrist).slerp(to.wrist, poseT), weight);
                        }

                        // Exactly UpdateInstance's rightPoseT/rightWeight/
                        // leftPoseT/leftWeight (ModelRenderer.cpp) - right
                        // eases from its carry pose to the shared meet pose
                        // as blendT goes 0->0.5, then releases (weight fades
                        // to 0) as blendT continues 0.5->1; left mirrors it,
                        // releasing near 0 and locking in from meet->carry
                        // by blendT=1.
                        function applyCarryLocks() {
                            var b = meetPreviewActive ? 0.5 : blendT, kMeetPoint = 0.5;
                            var rightPoseT  = smoothStep01(b / kMeetPoint);
                            var rightWeight = 1 - smoothStep01((b - kMeetPoint) / (1 - kMeetPoint));
                            var leftPoseT   = smoothStep01((b - kMeetPoint) / (1 - kMeetPoint));
                            var leftWeight  = smoothStep01(b / kMeetPoint);
                            applyCarrySide('right', 'rightCarry', 'rightMeet', rightPoseT, rightWeight);
                            applyCarrySide('left',  'leftMeet',   'leftCarry', leftPoseT,  leftWeight);
                        }

                        window.setMode = function (mode) {
                            currentMode = mode;
                            // Right/Left kick off a real-time tween of
                            // blendT toward the matching extreme, starting
                            // from wherever it currently sits - so the ball
                            // (and, via applyCarryLocks reading the same
                            // blendT, the arm lock releasing/engaging) plays
                            // out the same handoff curve the game itself
                            // uses, instead of snapping instantly. 'merged'
                            // deliberately leaves blendTarget alone: the
                            // ball just stays wherever Right/Left last
                            // animated it to, and the Merged pose is
                            // previewed separately via meetPreviewActive
                            // (see setMeetPreview / applyCarryLocks), which
                            // never touches blendT itself.
                            if (mode === 'right' || mode === 'left') {
                                blendAnimFrom = blendT;
                                blendAnimTo = mode === 'right' ? 0 : 1;
                                blendAnimStart = totalTime;
                                blendTarget = blendAnimTo;
                            }
                        };

                        window.setAnchorTransform = function (side, boneName, x, y, z, qx, qy, qz, qw) {
                            handBoneNames[side] = boneName;
                            var xf = { x: x, y: y, z: z, qx: qx, qy: qy, qz: qz, qw: qw };
                            pendingRefs[side] = xf;
                            attachRef(side);
                        };

                        function attachRef(side) {
                            var xf = pendingRefs[side];
                            if (!xf) return;
                            var boneName = handBoneNames[side];
                            var bone = boneName ? bonesByName[boneName] : null;
                            if (!bone) {
                                if (characterLoaded) showError('Ball Anchor: bone not found for ' + side + ' hand: ""' + boneName + '""');
                                return;
                            }
                            var ref = refs[side];
                            if (!ref) {
                                ref = new THREE.Object3D();
                                bone.add(ref);
                                refs[side] = ref;
                            }
                            ref.position.set(xf.x, xf.y, xf.z);
                            ref.quaternion.set(xf.qx, xf.qy, xf.qz, xf.qw);
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
                                    if (obj.name) bonesByName[obj.name] = obj;
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
                                attachRef('right');
                                attachRef('left');
                                if (!refs.right) showError('Ball Anchor: right hand bone not found in this model.');
                                if (!refs.left)  showError('Ball Anchor: left hand bone not found in this model.');

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
" + (string.IsNullOrEmpty(ballFileName) ? @"
                        showError('Ball Anchor: Assets/Football1.glb was not found next to the exe - showing no ball prop.');
" : @"
                        var ballLoader = new THREE.GLTFLoader();
                        ballLoader.load('https://appassets.local/" + ballFileName + @"', function (gltf) {
                            ballObj = gltf.scene;

                            // Re-center on the mesh's own geometric middle,
                            // matching FootballRenderer::Init's normalize
                            // matrix (translate(0,-0.5,0) * BuildNormalize's
                            // bottom-anchored height-1 normalize, which nets
                            // out to exactly 'true center -> origin') - the
                            // anchor point this tool positions is meant to be
                            // where the ball's center sits, not wherever the
                            // source file's own authored origin happens to be.
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

                        var rPos = new THREE.Vector3(), rQuat = new THREE.Quaternion();
                        var lPos = new THREE.Vector3(), lQuat = new THREE.Quaternion();

                        // Same window+smoothstep shape as the game's
                        // Renderer::CarriedBallPose: flat at 0 until
                        // (0.5 - halfWidth), smoothly rises to flat at 1 by
                        // (0.5 + halfWidth), so the ball only actually moves
                        // during a short window straddling the midpoint
                        // instead of drifting the whole time the blend value
                        // is changing.
                        function handoffFraction(b, hw) {
                            var s = (b - (0.5 - hw)) / (2 * hw);
                            s = Math.max(0, Math.min(1, s));
                            return s * s * (3 - 2 * s);
                        }

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            totalTime += delta;

                            // Ease blendT toward blendTarget over
                            // blendAnimDuration seconds (see setMode) - once
                            // it arrives, blendT === blendTarget exactly and
                            // this is a no-op, so 'merged' mode (which never
                            // changes blendTarget) leaves the ball parked.
                            if (blendT !== blendTarget) {
                                var t = blendAnimDuration > 0
                                    ? Math.max(0, Math.min(1, (totalTime - blendAnimStart) / blendAnimDuration))
                                    : 1;
                                blendT = blendAnimFrom + (blendAnimTo - blendAnimFrom) * t;
                                if (t >= 1) blendT = blendTarget;
                            }

                            if (mixer && !paused) mixer.update(delta);
                            applyCarryLocks();
                            scene.updateMatrixWorld(true);

                            if (ballPivot && refs.right && refs.left) {
                                refs.right.getWorldPosition(rPos);
                                refs.right.getWorldQuaternion(rQuat);
                                refs.left.getWorldPosition(lPos);
                                refs.left.getWorldQuaternion(lQuat);

                                // Always driven by blendT/handoffFraction now
                                // (the same curve for all three modes) -
                                // Right/Left just animate blendT to 0/1 (see
                                // setMode) instead of snapping ballPivot
                                // directly, and Merged leaves blendT (and so
                                // the ball) exactly where it already was.
                                var s = handoffFraction(blendT, halfWidth);
                                ballPivot.position.lerpVectors(rPos, lPos, s);
                                // copy(from).slerp(to, t) is the documented instance form -
                                // avoids relying on the static slerpQuaternions' exact argument
                                // order, which has differed across Three.js versions.
                                ballPivot.quaternion.copy(rQuat).slerp(lQuat, s);
                                ballPivot.visible = true;
                            } else if (ballPivot) {
                                ballPivot.visible = false;
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
