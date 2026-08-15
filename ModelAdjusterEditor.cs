using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Proportion edits to the rig itself, as opposed to the pose/orientation fixes the other modes
    // make. The first (and so far only) adjustment is bone length; the boxed group it lives in is
    // meant to be joined by sibling groups as more are added.
    //
    // A "bone's length" is not a property a glTF node carries - it is the distance from that joint
    // to the joint(s) below it, i.e. the local translation of its CHILD joints. So lengthening
    // LeftArm means scaling LeftForeArm's local translation, which carries the forearm, hand and
    // everything below it along with it. Bones whose children are all non-joint nodes (attachment
    // markers like the ones StiffArmPoseEditor and BallAnchorEditor write) have no length to
    // adjust and are left out of the dropdown; those markers are also deliberately NOT scaled when
    // their parent bone is lengthened, since an anchor's offset from the hand is a fixed hand-
    // relative position, not part of the skeleton's proportions.
    //
    // Unlike the rotation editors, this is one adjustment per bone for the WHOLE model, not a
    // per-animation correction: Apply writes the scaled translation into the bind pose AND into
    // every animation's translation channel for the affected joints, so the model has one set of
    // proportions no matter which clip is playing.
    //
    // Skins' inverse bind matrices are deliberately left untouched. A vertex's skinning matrix is
    // jointWorld * inverseBind, so leaving the inverse binds at their original values is exactly
    // what makes the mesh stretch along with the moved joints. Rewriting them to match the new
    // joint positions would cancel the change out and leave the mesh looking unmodified.
    //
    // Thickness is the second adjustment, and it is a different kind of edit: it moves MESH
    // VERTICES, not joints. Each vertex is pushed away from (or pulled toward) the bone's axis by
    // its own skin weight for that bone, so a limb swells smoothly and blends away at the joints
    // where the neighbouring bone takes over. Scaling the joint node instead would have been far
    // less code and the wrong shape - a node's scale is about its ORIGIN, so a thigh scaled at the
    // hip tapers from unchanged at the hip to ballooning at the knee, and it drags every
    // descendant bone and the bone's own length along with it.
    //
    // Because vertex positions live in the skin's bind space - the space the inverse bind matrices
    // map out of - the bone axes this measures against come from those same inverse binds, which
    // no adjustment here ever touches. That is what lets length and thickness compose cleanly: the
    // displacement is perpendicular to the bone in bind space, and skinning then carries it
    // through whatever the length adjustment did to the joints.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window
    // chrome, so this control only contributes its own left-hand controls and 3D preview.
    public class ModelAdjusterEditor : UserControl
    {
        private const int MinPercent = 25;
        private const int MaxPercent = 300;
        private const int DefaultPercent = 100;

        private readonly ModelRoot _model;

        private ComboBox _targetDropdown = null!;
        private TrackBar _sliderLength = null!, _sliderThickness = null!, _sliderGroupSize = null!;
        private Label _lblLength = null!, _lblThickness = null!, _lblGroupSize = null!;
        // Index 0/1/2 = X/Y/Z, so the three rows are built and read in one loop rather than as
        // three near-identical copies.
        private readonly TrackBar[] _sliderSize = new TrackBar[3];
        private readonly Label[] _lblSize = new Label[3];
        private CheckBox _chkUniformSize = null!;
        private Label _lblTargetInfo = null!, _lblMirrorInfo = null!, _lblStatus = null!;
        private CheckBox _chkMirror = null!, _chkShowBones = null!;
        private ComboBox _animDropdown = null!;
        private Button _btnPause = null!, _btnApply = null!, _btnResetTarget = null!, _btnResetAll = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;
        private bool _suppressEvents;

        // Every node that acts as a skin joint - the difference between a real bone and the
        // geometry/marker nodes that share the same hierarchy.
        private readonly HashSet<Node> _jointNodes = new();
        // Only the joints that actually have a length (see the class comment), by name.
        private readonly Dictionary<string, Node> _bonesByName = new(StringComparer.Ordinal);

        // What the slider currently drives: either one bone, or a chain of them adjusted together.
        // Parallel to _targetDropdown.Items, so the dropdown's selected index indexes this too.
        private sealed record Target(string Label, string[] Bones, bool IsGroup);
        private readonly List<Target> _targets = new();

        // Bone name -> length percentage dialled in but not necessarily written to the model yet.
        // Group members and mirrored bones each get their own entry here, so everything downstream
        // (preview, Apply, reset) works in terms of individual bones and needs no special case for
        // how a bone came to be adjusted.
        private readonly Dictionary<string, int> _pendingPercent = new(StringComparer.Ordinal);

        // Bone name -> the length FACTOR last written to the model, so the status line can tell
        // dialled-in-but-unapplied apart from applied, and so a reset knows whether it has to put
        // anything back. A factor rather than a percentage because what actually gets written is
        // the length slider and the group size multiplied together.
        private readonly Dictionary<string, float> _appliedLengthFactor = new(StringComparer.Ordinal);

        // Thickness counterparts to the two above. Kept separate rather than folded into one
        // struct per bone because the two adjustments are written to completely different places -
        // lengths to node transforms and animation channels, thickness to mesh vertices - and are
        // therefore applied by separate passes that each need their own "what is already baked".
        private readonly Dictionary<string, int> _pendingThickness = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _appliedThickness = new(StringComparer.Ordinal);

        // Size: a uniform scale of the bone's geometry about the joint's own origin, in every
        // direction at once. It is the adjustment that works on a bone with nothing below it -
        // length and thickness both need a child joint to define an axis, so on a rig whose hand
        // has no finger bones they have nothing to measure against, while size only needs the one
        // joint. Scaling about the JOINT rather than the geometry's centre is what keeps the hand
        // attached to the wrist while it grows.
        // Per-axis, not one number, because the two directional adjustments above are both
        // radially symmetric and neither can change one direction on its own: thickness scales
        // everything perpendicular to the bone at once (on a foot, wider AND taller together), and
        // it needs a child joint for its axis, which a tip toe does not have. Splitting size into
        // X/Y/Z is what makes "widen the feet without lengthening them" expressible at all.
        //
        // Axes are the model's own, matching the Y-up / Z-forward / X-lateral convention the rest
        // of this app's rig handling assumes, so X really is width on these rigs.
        private readonly record struct ScalePercent(int X, int Y, int Z)
        {
            public static readonly ScalePercent Default = new(DefaultPercent, DefaultPercent, DefaultPercent);
            public bool IsDefault => Equals(Default);
            public Vector3 Factor => new(X / 100f, Y / 100f, Z / 100f);
            public int this[int axis] => axis == 0 ? X : axis == 1 ? Y : Z;
            public ScalePercent With(int axis, int value) => axis switch
            {
                0 => this with { X = value },
                1 => this with { Y = value },
                _ => this with { Z = value },
            };
        }

        private readonly Dictionary<string, ScalePercent> _pendingSize = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ScalePercent> _appliedSize = new(StringComparer.Ordinal);

        // Whole-branch size, keyed by the chain's ROOT bone: everything from that joint down
        // scaled as one unit - the arm growing out of the shoulder rather than each bone swelling
        // around itself.
        //
        // This one is a node SCALE on the root joint, not a vertex edit, and that is not a
        // shortcut - it is the only formulation that is actually correct. A node's scale already
        // propagates down the transform chain to every descendant joint, and skinning carries it
        // into their geometry: posed = S(k)*bindWorld_j * bindWorld_j^-1 * v = k*v, a clean uniform
        // scale of the whole branch. Doing it by hand instead - scaling the vertices AND scaling
        // the member bones' lengths so the skeleton keeps up - double-counts, because moving a
        // joint while leaving its inverse bind alone ALREADY drags that joint's geometry with it
        // (that is exactly how the Length slider works). Those two paths add up to
        // k*v + (k-1)*origin instead of k*v, and the limb overshoots by a whole joint offset.
        private readonly Dictionary<string, int> _pendingGroupSize = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _appliedGroupSize = new(StringComparer.Ordinal);

        // A root joint's local scale before any branch size was applied, so repeated applies set an
        // absolute scale rather than compounding - the scale counterpart to _originalTranslation.
        private readonly Dictionary<Node, Vector3> _originalScale = new();
        private readonly Dictionary<(Node Root, string Anim), (float Time, Vector3 Value)[]?> _originalScaleKeys = new();

        // A child joint's local translation, and its translation keys per animation, exactly as
        // they were before this editor first touched them. Every Apply scales these pristine
        // values rather than whatever is currently in the model, so applying repeatedly (or
        // dragging the slider and applying at each stop) sets an absolute length instead of
        // compounding one scale onto the last.
        private readonly Dictionary<Node, Vector3> _originalTranslation = new();
        private readonly Dictionary<(Node Child, string Anim), (float Time, Vector3 Value)[]?> _originalAnimKeys = new();

        // A bone's line in the skin's bind space: where it starts, and the unit direction toward
        // its first child joint. Thickness is measured as distance from this line.
        private sealed record BoneAxis(Vector3 Origin, Vector3 Direction);

        // One skinned primitive's everything-needed-to-rebuild-it. Joints and weights are read
        // once and held as decoded Vector4s (JOINTS_0 is an integer format on disk, which
        // AsVector4Array unpacks) because thickness rebuilds re-read them on every Apply.
        private sealed record SkinnedPrimitive(
            Accessor Positions,
            Accessor? Normals,
            IList<Vector4> Joints,
            IList<Vector4> Weights,
            BoneAxis?[] AxisByJoint,
            Vector3[] OriginByJoint,
            string?[] NameByJoint);

        private readonly List<SkinnedPrimitive> _skinnedPrimitives = new();
        private int _skippedPrimitives;

        // Vertex data exactly as it was before any thickness was applied, keyed by accessor
        // (rather than by primitive) because two primitives sharing one interleaved vertex buffer
        // must not each capture the other's already-modified values as "original".
        private readonly Dictionary<Accessor, Vector3[]> _originalVertexData = new();

        // Left/Right naming conventions this app's rigs use, tried in order. The two-character
        // entries are side SUFFIXES and only match at the end of the name ("Arm_L"); the rest are
        // words that can appear anywhere ("LeftArm", "mixamorig:LeftArm").
        private static readonly (string A, string B)[] MirrorTokens =
        {
            ("Left", "Right"), ("left", "right"), ("LEFT", "RIGHT"),
            ("_L", "_R"), ("_l", "_r"), (".L", ".R"), (".l", ".r"),
        };

        // Turns a detected chain into a name a person would recognise, and orders the groups so the
        // big structural ones come before the fingers. Scanned in order, first keyword that any
        // member's name contains wins - which is why "forearm" and "upleg" are listed above the
        // plainer "arm"/"leg", and every finger above "hand": "LeftHandIndex1" contains both
        // "hand" and "index", and it is the finger that makes it a distinct chain.
        private static readonly (string Keyword, string Part, int Rank)[] ChainParts =
        {
            ("spine",   "Spine",         0),
            ("neck",    "Neck",          1),
            ("forearm", "Arm",           2),
            ("upleg",   "Leg",           3),
            ("thigh",   "Leg",           3),
            ("arm",     "Arm",           2),
            ("thumb",   "Thumb",         6),
            ("index",   "Index Finger",  6),
            ("middle",  "Middle Finger", 6),
            ("ring",    "Ring Finger",   6),
            ("pinky",   "Pinky Finger",  6),
            ("little",  "Little Finger", 6),
            ("leg",     "Leg",           3),
            ("foot",    "Foot",          4),
            ("toe",     "Toe",           4),
            ("hand",    "Hand",          5),
        };

        public ModelAdjusterEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            CollectBones();
            CollectSkinnedPrimitives();
            BuildUi();
            PopulateTargetList();
            PopulateAnimationList();
            RefreshUiFromState();

            // The 3D preview is a WebView2 rendering its own already-dark scene, so only the
            // surrounding WinForms control panel needs theming.
            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void CollectBones()
        {
            foreach (var skin in _model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                    _jointNodes.Add(skin.GetJoint(i).Joint);

            foreach (var joint in _jointNodes)
            {
                if (string.IsNullOrEmpty(joint.Name)) continue;
                // Leaf joints are listed too, even though they have no length and no axis. On many
                // rigs the hand IS a leaf - no finger bones under the wrist - and leaving those out
                // meant the entire hand was unreachable, which is exactly the geometry most likely
                // to need resizing. Size works off the joint origin alone, so it applies to them.
                // First one wins if a rig somehow reuses a name; the dropdown is keyed by name and
                // has nothing to tell two same-named joints apart with anyway.
                _bonesByName.TryAdd(joint.Name, joint);
            }
        }

        private IEnumerable<Node> JointChildren(Node bone) => bone.VisualChildren.Where(_jointNodes.Contains);

        // Everything the thickness pass needs, resolved once. Only skinned primitives qualify:
        // thickness is defined by a vertex's weight for a bone, so a rigid mesh has nothing to
        // measure against and is left alone.
        private void CollectSkinnedPrimitives()
        {
            var seenPositions = new HashSet<Accessor>();

            foreach (var node in _model.LogicalNodes)
            {
                if (node.Mesh == null || node.Skin == null) continue;
                var skin = node.Skin;

                // The joint's own position in bind space is the translation of the INVERSE of its
                // inverse bind matrix - the inverse bind maps bind space to joint-local, so
                // undoing it puts the joint back where it sits among the vertices.
                var origins = new Vector3[skin.JointsCount];
                var names = new string?[skin.JointsCount];
                var indexOfJoint = new Dictionary<Node, int>();
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (joint, inverseBind) = skin.GetJoint(i);
                    names[i] = joint.Name;
                    indexOfJoint[joint] = i;
                    origins[i] = Matrix4x4.Invert(inverseBind, out var bind) ? bind.Translation : Vector3.Zero;
                }

                var axes = new BoneAxis?[skin.JointsCount];
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var child = JointChildren(skin.GetJoint(i).Joint).FirstOrDefault();
                    // A leaf joint, or one whose child is not part of this skin, has no direction
                    // to be thick around; it stays null and is skipped per vertex.
                    if (child == null || !indexOfJoint.TryGetValue(child, out int childIndex)) continue;

                    var direction = origins[childIndex] - origins[i];
                    if (direction.LengthSquared() < 1e-12f) continue;
                    axes[i] = new BoneAxis(origins[i], Vector3.Normalize(direction));
                }

                foreach (var prim in node.Mesh.Primitives)
                {
                    if (!prim.VertexAccessors.TryGetValue("POSITION", out var positions)) continue;
                    if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var joints)) continue;
                    if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var weights)) continue;

                    // A quantized POSITION (KHR_mesh_quantization) decodes to floats on read but
                    // cannot be written back through the same view without re-encoding, so those
                    // primitives are reported rather than silently half-processed.
                    if (positions.Encoding != EncodingType.FLOAT) { _skippedPrimitives++; continue; }

                    // Two primitives can share one vertex buffer; rebuilding it twice would take
                    // the second pass's "original" from the first pass's output.
                    if (!seenPositions.Add(positions)) continue;

                    prim.VertexAccessors.TryGetValue("NORMAL", out var normals);
                    if (normals != null && normals.Encoding != EncodingType.FLOAT) normals = null;

                    _skinnedPrimitives.Add(new SkinnedPrimitive(
                        positions, normals, joints.AsVector4Array(), weights.AsVector4Array(), axes, origins, names));
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Chain detection
        // ---------------------------------------------------------------------------------------

        // Groups are found from the skeleton's own shape rather than from a table of expected bone
        // names, so they work on any rig this app is handed instead of only Mixamo-named ones. A
        // joint is a "link" when it has exactly ONE joint child - it continues a chain rather than
        // branching - and a chain is a maximal run of links. That falls out as the arm (shoulder
        // through forearm, ending where the hand fans out into fingers), the leg, the spine
        // (ending at the chest, where the neck and both clavicles branch off), the neck, and each
        // finger: exactly the runs where "make this whole limb longer" is a sensible single edit.
        private List<List<Node>> FindChains()
        {
            var links = _bonesByName.Values.Where(n => JointChildren(n).Count() == 1).ToHashSet();
            var chains = new List<List<Node>>();

            foreach (var link in links)
            {
                // Only start at the head of a run - if this link's parent is a link too, this bone
                // is in the middle of a chain that some earlier iteration already walked through.
                var parent = link.VisualParent;
                if (parent != null && links.Contains(parent)) continue;

                var chain = new List<Node>();
                for (var current = link; current != null && links.Contains(current); current = JointChildren(current).FirstOrDefault())
                    chain.Add(current);

                // A one-bone "chain" is just that bone, which the dropdown already lists on its own.
                if (chain.Count >= 2) chains.Add(chain);
            }

            return chains;
        }

        private static string? SideOf(string boneName)
        {
            foreach (var (a, b) in MirrorTokens)
            {
                bool isSuffix = a.Length == 2;
                if (isSuffix ? boneName.EndsWith(a, StringComparison.Ordinal) : boneName.Contains(a, StringComparison.Ordinal)) return "Left";
                if (isSuffix ? boneName.EndsWith(b, StringComparison.Ordinal) : boneName.Contains(b, StringComparison.Ordinal)) return "Right";
            }
            return null;
        }

        private static (string? Name, int Rank) FriendlyChainName(List<Node> chain)
        {
            var lowered = chain.Select(n => n.Name!.ToLowerInvariant()).ToList();
            foreach (var (keyword, part, rank) in ChainParts)
            {
                if (!lowered.Any(n => n.Contains(keyword, StringComparison.Ordinal))) continue;
                var side = SideOf(chain[0].Name!);
                return (side == null ? part : $"{side} {part}", rank);
            }
            // Unnamed by any convention this knows - still a real chain, just labelled by its ends.
            return (null, 9);
        }

        // ---------------------------------------------------------------------------------------
        // UI
        // ---------------------------------------------------------------------------------------

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, AutoScroll = true };

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

            // The one adjustment this mode currently offers gets its own box so a second one can be
            // added below it without either having to re-explain which controls belong to it.
            var lengthGroup = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 10),
                Padding = new Padding(8),
                BorderStyle = BorderStyle.FixedSingle,
            };

            lengthGroup.Controls.Add(new Label { Text = "Adjust Bones", AutoSize = true, Margin = new Padding(3, 0, 3, 8) });

            lengthGroup.Controls.Add(new Label { Text = "Bone or group:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
            _targetDropdown = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 6) };
            _targetDropdown.SelectedIndexChanged += (s, e) => OnTargetSelected();
            lengthGroup.Controls.Add(_targetDropdown);

            _lblTargetInfo = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(320, 0), Margin = new Padding(3, 0, 3, 8),
            };
            lengthGroup.Controls.Add(_lblTargetInfo);

            _lblLength = new Label { Text = LengthLabel(DefaultPercent), AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            lengthGroup.Controls.Add(_lblLength);

            _sliderLength = new TrackBar
            {
                Width = 300, Height = 45, Minimum = MinPercent, Maximum = MaxPercent, Value = DefaultPercent,
                TickFrequency = 25, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderLength.ValueChanged += (s, e) => OnLengthChanged();
            var (btnMinus, btnPlus) = SliderNudge.Attach(_sliderLength);

            // SliderNudge lays its buttons out by absolute Left/Top inside a fixed-position parent,
            // so the slider and its two buttons go into a plain Panel of the same footprint rather
            // than straight into the flow, which would re-position all three itself.
            var sliderHost = new Panel { Width = 306, Height = 45, Margin = new Padding(3, 0, 3, 4) };
            sliderHost.Controls.Add(btnMinus);
            sliderHost.Controls.Add(_sliderLength);
            sliderHost.Controls.Add(btnPlus);
            lengthGroup.Controls.Add(sliderHost);

            _lblThickness = new Label { Text = ThicknessLabel(DefaultPercent), AutoSize = true, Margin = new Padding(3, 4, 3, 0) };
            lengthGroup.Controls.Add(_lblThickness);

            _sliderThickness = new TrackBar
            {
                Width = 300, Height = 45, Minimum = MinPercent, Maximum = MaxPercent, Value = DefaultPercent,
                TickFrequency = 25, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderThickness.ValueChanged += (s, e) => OnThicknessChanged();
            var (btnThinner, btnFatter) = SliderNudge.Attach(_sliderThickness);

            var thicknessHost = new Panel { Width = 306, Height = 45, Margin = new Padding(3, 0, 3, 4) };
            thicknessHost.Controls.Add(btnThinner);
            thicknessHost.Controls.Add(_sliderThickness);
            thicknessHost.Controls.Add(btnFatter);
            lengthGroup.Controls.Add(thicknessHost);

            // Ticked by default so the common "make this bigger" case stays a single drag; unticked
            // is what lets one direction move on its own.
            _chkUniformSize = new CheckBox
            {
                Text = "Size: keep X/Y/Z together", AutoSize = true, Checked = true, Margin = new Padding(3, 6, 3, 2),
            };
            lengthGroup.Controls.Add(_chkUniformSize);

            for (int axis = 0; axis < 3; axis++)
            {
                int a = axis;
                _lblSize[a] = new Label { Text = SizeLabel(a, DefaultPercent), AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
                lengthGroup.Controls.Add(_lblSize[a]);

                _sliderSize[a] = new TrackBar
                {
                    Width = 300, Height = 45, Minimum = MinPercent, Maximum = MaxPercent, Value = DefaultPercent,
                    TickFrequency = 25, Margin = new Padding(3, 0, 3, 4),
                };
                _sliderSize[a].ValueChanged += (s, e) => OnSizeChanged(a);
                var (btnSmaller, btnBigger) = SliderNudge.Attach(_sliderSize[a]);

                var sizeHost = new Panel { Width = 306, Height = 45, Margin = new Padding(3, 0, 3, 4) };
                sizeHost.Controls.Add(btnSmaller);
                sizeHost.Controls.Add(_sliderSize[a]);
                sizeHost.Controls.Add(btnBigger);
                lengthGroup.Controls.Add(sizeHost);
            }

            _lblGroupSize = new Label { Text = GroupSizeLabel(DefaultPercent), AutoSize = true, Margin = new Padding(3, 4, 3, 0) };
            lengthGroup.Controls.Add(_lblGroupSize);

            _sliderGroupSize = new TrackBar
            {
                Width = 300, Height = 45, Minimum = MinPercent, Maximum = MaxPercent, Value = DefaultPercent,
                TickFrequency = 25, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderGroupSize.ValueChanged += (s, e) => OnGroupSizeChanged();
            var (btnGroupSmaller, btnGroupBigger) = SliderNudge.Attach(_sliderGroupSize);

            var groupSizeHost = new Panel { Width = 306, Height = 45, Margin = new Padding(3, 0, 3, 4) };
            groupSizeHost.Controls.Add(btnGroupSmaller);
            groupSizeHost.Controls.Add(_sliderGroupSize);
            groupSizeHost.Controls.Add(btnGroupBigger);
            lengthGroup.Controls.Add(groupSizeHost);

            _chkMirror = new CheckBox
            {
                Text = "Mirror left / right", AutoSize = true, Checked = true, Margin = new Padding(3, 2, 3, 0),
            };
            _chkMirror.CheckedChanged += (s, e) => OnMirrorToggled();
            lengthGroup.Controls.Add(_chkMirror);

            _lblMirrorInfo = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0), Margin = new Padding(22, 0, 3, 8),
            };
            lengthGroup.Controls.Add(_lblMirrorInfo);

            var resetRow = MakeRow();
            _btnResetTarget = new Button { Text = "Reset Selection", AutoSize = true, Margin = new Padding(3, 0, 6, 0) };
            _btnResetTarget.Click += (s, e) => ResetSelection();
            _btnResetAll = new Button { Text = "Reset All Bones", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _btnResetAll.Click += (s, e) => ResetAllBones();
            resetRow.Controls.Add(_btnResetTarget);
            resetRow.Controls.Add(_btnResetAll);
            lengthGroup.Controls.Add(resetRow);

            flow.Controls.Add(lengthGroup);

            _chkShowBones = new CheckBox
            {
                Text = "Show bones (click one to select it)", AutoSize = true, Margin = new Padding(3, 0, 3, 8),
            };
            _chkShowBones.CheckedChanged += (s, e) => PushShowBones();
            flow.Controls.Add(_chkShowBones);

            flow.Controls.Add(new Label { Text = "Preview Animation:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var animRow = MakeRow();
            _animDropdown = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 8, 8) };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", AutoSize = true, Margin = new Padding(3, 0, 3, 8) };
            _btnPause.Click += (s, e) => TogglePause();
            animRow.Controls.Add(_animDropdown);
            animRow.Controls.Add(_btnPause);
            flow.Controls.Add(animRow);

            _btnApply = new Button { Text = "Apply to Model", AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
            _btnApply.Click += (s, e) => ApplyToModel();
            flow.Controls.Add(_btnApply);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 6, 3, 10), ForeColor = System.Drawing.Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        // Groups first (they are the coarse edits, and the ones worth reaching for before dialling
        // an individual bone), then every adjustable bone on its own.
        private void PopulateTargetList()
        {
            foreach (var chain in FindChains()
                         .Select(c => (Chain: c, Friendly: FriendlyChainName(c)))
                         .OrderBy(c => c.Friendly.Rank)
                         .ThenBy(c => c.Chain[0].Name, StringComparer.Ordinal))
            {
                var names = chain.Chain.Select(n => n.Name!).ToArray();
                var label = chain.Friendly.Name != null
                    ? $"Group: {chain.Friendly.Name} ({names.Length} bones)"
                    : $"Group: {names[0]} → {names[^1]} ({names.Length} bones)";
                _targets.Add(new Target(label, names, IsGroup: true));
            }

            foreach (var name in _bonesByName.Keys.OrderBy(n => n, StringComparer.Ordinal))
                _targets.Add(new Target(name, new[] { name }, IsGroup: false));

            foreach (var target in _targets) _targetDropdown.Items.Add(target.Label);

            if (_targets.Count > 0)
            {
                _targetDropdown.SelectedIndex = 0;
            }
            else
            {
                // No skin at all - nothing here can do anything.
                _sliderLength.Enabled = false;
                _sliderThickness.Enabled = false;
                foreach (var slider in _sliderSize) slider.Enabled = false;
                _sliderGroupSize.Enabled = false;
                _chkUniformSize.Enabled = false;
                _chkMirror.Enabled = false;
                _chkShowBones.Enabled = false;
                _btnApply.Enabled = false;
                _btnResetTarget.Enabled = false;
                _btnResetAll.Enabled = false;
                _lblTargetInfo.Text = "This model has no skinned bones with child joints to lengthen.";
            }
        }

        private void PopulateAnimationList()
        {
            _animDropdown.Items.Add("None (Bind Pose)");
            foreach (var anim in _model.LogicalAnimations)
                _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");
            _animDropdown.SelectedIndex = _animDropdown.Items.Count > 1 ? 1 : 0;
        }

        private Target? SelectedTarget =>
            _targetDropdown.SelectedIndex >= 0 && _targetDropdown.SelectedIndex < _targets.Count
                ? _targets[_targetDropdown.SelectedIndex]
                : null;

        private static string LengthLabel(int percent) => $"Length (along the bone): {percent}%";
        private static string ThicknessLabel(int percent) => $"Thickness (around the bone): {percent}%";
        private static readonly string[] AxisNames = { "X (width)", "Y (height)", "Z (depth)" };
        private static string SizeLabel(int axis, int percent) => $"Size {AxisNames[axis]}: {percent}%";
        private static string GroupSizeLabel(int percent) => $"Size, whole branch: {percent}%";

        private int PercentOf(string bone) => _pendingPercent.TryGetValue(bone, out var v) ? v : DefaultPercent;
        private int ThicknessOf(string bone) => _pendingThickness.TryGetValue(bone, out var v) ? v : DefaultPercent;
        private ScalePercent SizeOf(string bone) => _pendingSize.TryGetValue(bone, out var v) ? v : ScalePercent.Default;
        private int GroupSizeOf(string bone) => _pendingGroupSize.TryGetValue(bone, out var v) ? v : DefaultPercent;

        // Length needs no group term: branch size rides on the root's node scale, which multiplies
        // through the transform chain on its own. The two compose without either knowing about the
        // other - a branch at 150% with one bone lengthened to 120% puts that bone at 180% of its
        // original reach, and moving either slider leaves the other's intent intact.
        private float EffectiveLengthFactor(string bone) => PercentOf(bone) / 100f;

        // Length moves a bone's child joints, so a bone with none has nothing to lengthen.
        private bool HasLength(string bone) =>
            _bonesByName.TryGetValue(bone, out var node) && JointChildren(node).Any();

        // Thickness needs a direction to be thick AROUND, which is the offset to the first child
        // joint. A child sitting exactly on its parent gives no direction, so that counts as no
        // axis rather than producing a degenerate one.
        private bool HasAxis(string bone)
        {
            if (!_bonesByName.TryGetValue(bone, out var node)) return false;
            var child = JointChildren(node).FirstOrDefault();
            return child != null && GetOriginalTranslation(child).LengthSquared() > 1e-12f;
        }

        // A group whose members were dialled in separately (one at a time, or by a mirror that only
        // matched some of them) has no single percentage to show. The slider falls back to 100 and
        // the info label says so, rather than silently showing one member's value as if it spoke
        // for all of them.
        private static bool IsMixed<T>(Target target, Func<string, T> valueOf) =>
            target.Bones.Select(valueOf).Distinct().Count() > 1;

        private int PercentOf(Target target) => IsMixed(target, PercentOf) ? DefaultPercent : PercentOf(target.Bones[0]);
        private int ThicknessOf(Target target) => IsMixed(target, ThicknessOf) ? DefaultPercent : ThicknessOf(target.Bones[0]);
        private ScalePercent SizeOf(Target target) => IsMixed(target, SizeOf) ? ScalePercent.Default : SizeOf(target.Bones[0]);
        // Branch size lives on the root bone alone, so unlike the others there is no per-member
        // spread and nothing to be "mixed".
        private int GroupSizeOf(Target target) => GroupSizeOf(target.Bones[0]);

        // Summed over the target's bones, so a chain reports the length of the whole limb. Each
        // bone contributes the distance to its FIRST child joint - a branching bone (Hips, feeding
        // the spine and both legs) has no single length, which is why the info label names every
        // child such a bone would move.
        private (float Original, float Adjusted) LengthOf(Target target)
        {
            float original = 0f, adjusted = 0f;
            foreach (var bone in target.Bones)
            {
                if (!_bonesByName.TryGetValue(bone, out var node)) continue;
                var child = JointChildren(node).FirstOrDefault();
                if (child == null) continue;
                float length = GetOriginalTranslation(child).Length();
                original += length;
                adjusted += length * PercentOf(bone) / 100f;
            }
            return (original, adjusted);
        }

        private Vector3 GetOriginalTranslation(Node child)
        {
            if (_originalTranslation.TryGetValue(child, out var cached)) return cached;
            var translation = child.LocalTransform.GetDecomposed().Translation;
            _originalTranslation[child] = translation;
            return translation;
        }

        private void OnTargetSelected()
        {
            RefreshUiFromState();
            PushSelectionToPreview();
        }

        private void RefreshUiFromState()
        {
            var target = SelectedTarget;
            if (target == null) return;

            int percent = PercentOf(target);
            int thickness = ThicknessOf(target);
            var size = SizeOf(target);
            int groupSize = GroupSizeOf(target);

            // Which of the four this selection can actually do. A tip bone - a wrist with no
            // finger joints, a head with nothing above it - supports only size, and saying so on
            // the disabled slider beats leaving one that moves and changes nothing.
            //
            // Branch size is offered for a lone bone as well as a group, because the two size
            // sliders differ in SCOPE, not just pivot: per-bone size moves only the geometry
            // weighted to that one bone, while branch size takes its descendants with it. Those
            // coincide only for a tip bone - which is why a leaf hand was fine with per-bone size
            // but a foot was not, its toes being left behind at their original size.
            bool canLength = target.Bones.Any(HasLength);
            bool canThickness = _skinnedPrimitives.Count > 0 && target.Bones.Any(HasAxis);
            bool canSize = _skinnedPrimitives.Count > 0;
            bool canGroupSize = canSize && HasLength(target.Bones[0]);

            _suppressEvents = true;
            _sliderLength.Value = Math.Clamp(percent, MinPercent, MaxPercent);
            _sliderThickness.Value = Math.Clamp(thickness, MinPercent, MaxPercent);
            for (int axis = 0; axis < 3; axis++)
                _sliderSize[axis].Value = Math.Clamp(size[axis], MinPercent, MaxPercent);
            _sliderGroupSize.Value = Math.Clamp(groupSize, MinPercent, MaxPercent);
            _suppressEvents = false;

            _sliderLength.Enabled = canLength;
            _sliderThickness.Enabled = canThickness;
            for (int axis = 0; axis < 3; axis++) _sliderSize[axis].Enabled = canSize;
            _chkUniformSize.Enabled = canSize;
            _sliderGroupSize.Enabled = canGroupSize;

            _lblLength.Text = canLength ? LengthLabel(percent) : "Length: n/a (no child joint to move)";
            _lblThickness.Text = canThickness ? ThicknessLabel(thickness) : "Thickness: n/a (no child joint to measure around)";
            for (int axis = 0; axis < 3; axis++)
                _lblSize[axis].Text = canSize
                    ? SizeLabel(axis, size[axis])
                    : $"Size {AxisNames[axis]}: unavailable (no skinned mesh vertices found)";
            _lblGroupSize.Text = canGroupSize
                ? GroupSizeLabel(groupSize) + $" (from {target.Bones[0]} down)"
                : "Size, whole branch: n/a (nothing below this bone — use Size, per bone)";

            var (original, adjusted) = LengthOf(target);
            if (target.IsGroup)
            {
                _lblTargetInfo.Text = $"Scales {target.Bones.Length} bones: {string.Join(", ", target.Bones)} — length total {Fmt(original)} → {Fmt(adjusted)}"
                    + (IsMixed(target, PercentOf) || IsMixed(target, ThicknessOf) || IsMixed(target, SizeOf)
                        ? " (members currently differ; moving a slider sets them all)" : "");
            }
            else
            {
                var node = _bonesByName[target.Bones[0]];
                var children = JointChildren(node).ToList();
                string childList = string.Join(", ", children.Select(c => string.IsNullOrEmpty(c.Name) ? "(unnamed)" : c.Name));
                _lblTargetInfo.Text = children.Count switch
                {
                    0 => "Tip bone — no joints below it, so only Size applies here.",
                    1 => $"Moves {childList} — {Fmt(original)} → {Fmt(adjusted)}",
                    _ => $"Moves {children.Count} child joints: {childList}",
                };
            }

            var mirror = MirrorTargetLabel(target);
            _lblMirrorInfo.Text = !_chkMirror.Checked ? ""
                : mirror != null ? $"also applies to {mirror}"
                : "no left/right counterpart for this selection";

            UpdateStatus();
        }

        private static string Fmt(float meters) => meters.ToString("0.###", CultureInfo.InvariantCulture);

        private void OnLengthChanged()
        {
            if (_suppressEvents) return;
            var target = SelectedTarget;
            if (target == null) return;

            int percent = _sliderLength.Value;
            foreach (var bone in BonesFor(target)) _pendingPercent[bone] = percent;

            RefreshUiFromState();
            PushLengthsToPreview();
        }

        private void OnThicknessChanged()
        {
            if (_suppressEvents) return;
            var target = SelectedTarget;
            if (target == null) return;

            int percent = _sliderThickness.Value;
            foreach (var bone in BonesFor(target)) _pendingThickness[bone] = percent;

            RefreshUiFromState();
            PushThicknessToPreview();
        }

        private void OnSizeChanged(int axis)
        {
            if (_suppressEvents) return;
            var target = SelectedTarget;
            if (target == null) return;

            int percent = _sliderSize[axis].Value;
            foreach (var bone in BonesFor(target))
            {
                // Uniform drives all three from whichever slider moved, so the ordinary
                // "make this bigger" case is still a single drag.
                _pendingSize[bone] = _chkUniformSize.Checked
                    ? new ScalePercent(percent, percent, percent)
                    : SizeOf(bone).With(axis, percent);
            }

            RefreshUiFromState();
            PushSizeToPreview();
        }

        // The whole branch scaled about its root joint, recorded on that root alone - the scale
        // propagates to the rest by itself. For a group that root is the head of the chain; for a
        // single bone it is that bone, which is how a foot carries its toes. The mirrored branch
        // scales about the mirror of the root, so a mirrored arm grows from its own shoulder
        // rather than the other side's.
        private void OnGroupSizeChanged()
        {
            if (_suppressEvents) return;
            var target = SelectedTarget;
            if (target == null) return;

            int percent = _sliderGroupSize.Value;
            string root = target.Bones[0];
            _pendingGroupSize[root] = percent;

            var mirrorRoot = _chkMirror.Checked ? GetMirrorBoneName(root) : null;
            if (mirrorRoot != null) _pendingGroupSize[mirrorRoot] = percent;

            RefreshUiFromState();
            PushGroupSizeToPreview();
        }

        // Every bone the slider should drive for this target: its own bones, plus their opposite-
        // side counterparts when Mirror is on. A group mirrors member by member, which lands
        // exactly on the counterpart chain without needing to know the two are paired.
        private List<string> BonesFor(Target target)
        {
            var bones = new List<string>(target.Bones);
            if (!_chkMirror.Checked) return bones;

            foreach (var bone in target.Bones)
            {
                var mirror = GetMirrorBoneName(bone);
                if (mirror != null && !bones.Contains(mirror, StringComparer.Ordinal)) bones.Add(mirror);
            }
            return bones;
        }

        // Names what Mirror will additionally hit, preferring the label of a group that is exactly
        // the mirrored set (so a chain reads "also applies to Group: Right Arm (3 bones)" rather
        // than listing its bones).
        private string? MirrorTargetLabel(Target target)
        {
            var mirrored = target.Bones.Select(GetMirrorBoneName).OfType<string>().ToList();
            if (mirrored.Count == 0) return null;

            var asSet = mirrored.ToHashSet(StringComparer.Ordinal);
            var match = _targets.FirstOrDefault(t => t.Bones.Length == asSet.Count && t.Bones.All(asSet.Contains));
            return match?.Label ?? string.Join(", ", mirrored);
        }

        // Ticking Mirror after a selection has already been dialled in pulls the counterpart up to
        // the same value straight away, rather than waiting for the next slider move - the checkbox
        // otherwise looks like it did nothing. Unticking deliberately leaves the counterpart where
        // it is: it was a real adjustment, and silently undoing it would be a surprise.
        private void OnMirrorToggled()
        {
            var target = SelectedTarget;
            if (target != null && _chkMirror.Checked)
            {
                foreach (var bone in target.Bones)
                {
                    var mirror = GetMirrorBoneName(bone);
                    if (mirror == null) continue;
                    _pendingPercent[mirror] = PercentOf(bone);
                    _pendingThickness[mirror] = ThicknessOf(bone);
                    _pendingSize[mirror] = SizeOf(bone);
                }
                PushLengthsToPreview();
                PushThicknessToPreview();
                PushSizeToPreview();
            }
            RefreshUiFromState();
        }

        // The opposite-side bone under whichever naming convention this rig uses. The candidate is
        // only accepted if it is itself an adjustable bone, which is what keeps a rig that happens
        // to contain "Left" in an unpaired name from mirroring onto nothing.
        private string? GetMirrorBoneName(string boneName)
        {
            foreach (var (a, b) in MirrorTokens)
            {
                bool isSuffix = a.Length == 2;
                foreach (var (from, to) in new[] { (a, b), (b, a) })
                {
                    string? candidate = isSuffix
                        ? (boneName.EndsWith(from, StringComparison.Ordinal) ? boneName[..^from.Length] + to : null)
                        : (boneName.Contains(from, StringComparison.Ordinal) ? boneName.Replace(from, to) : null);

                    if (candidate != null && candidate != boneName && _bonesByName.ContainsKey(candidate))
                        return candidate;
                }
            }
            return null;
        }

        private void UpdateStatus(string? message = null)
        {
            if (message != null) { _lblStatus.Text = message; return; }

            int unapplied = UnappliedLengths()
                .Union(Unapplied(_pendingThickness, _appliedThickness), StringComparer.Ordinal)
                .Union(UnappliedSizes(), StringComparer.Ordinal)
                .Union(Unapplied(_pendingGroupSize, _appliedGroupSize), StringComparer.Ordinal)
                .Count();

            _lblStatus.Text = unapplied == 0
                ? ""
                : $"{unapplied} bone(s) adjusted in the preview but not written to the model yet — click Apply.";
        }

        // Bones whose dialled-in value differs from what is baked. Both directions matter: a bone
        // present in `applied` but dropped from `pending` (reset while its change was already
        // written) is just as unapplied as a freshly moved slider.
        private static IEnumerable<string> Unapplied<T>(
            Dictionary<string, T> pending, Dictionary<string, T> applied, T unset)
        {
            foreach (var bone in pending.Keys.Concat(applied.Keys).Distinct(StringComparer.Ordinal))
            {
                T want = pending.TryGetValue(bone, out var p) ? p : unset;
                T have = applied.TryGetValue(bone, out var a) ? a : unset;
                if (!EqualityComparer<T>.Default.Equals(want, have)) yield return bone;
            }
        }

        private static IEnumerable<string> Unapplied(Dictionary<string, int> pending, Dictionary<string, int> applied) =>
            Unapplied(pending, applied, DefaultPercent);

        private IEnumerable<string> UnappliedSizes() => Unapplied(_pendingSize, _appliedSize, ScalePercent.Default);

        // Length's own version of the above: what gets compared is the COMBINED factor, since a
        // bone can be out of date because its own length slider moved or because the group scale
        // above it did.
        private IEnumerable<string> UnappliedLengths()
        {
            var bones = _pendingPercent.Keys
                .Concat(_pendingGroupSize.Keys)
                .Concat(_appliedLengthFactor.Keys)
                .Distinct(StringComparer.Ordinal);

            foreach (var bone in bones)
            {
                float have = _appliedLengthFactor.TryGetValue(bone, out var a) ? a : 1f;
                if (MathF.Abs(EffectiveLengthFactor(bone) - have) > 1e-6f) yield return bone;
            }
        }

        // ---------------------------------------------------------------------------------------
        // Writing to the model
        // ---------------------------------------------------------------------------------------

        private void ApplyToModel()
        {
            var unnamedAnimations = _model.LogicalAnimations.Count(a => string.IsNullOrEmpty(a.Name));
            var duplicateNames = _model.LogicalAnimations
                .Where(a => !string.IsNullOrEmpty(a.Name))
                .GroupBy(a => a.Name!, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            int changed = 0;
            foreach (var bone in UnappliedLengths().ToList())
            {
                if (ApplyBone(bone)) changed++;
            }

            int thickened = ApplyThickness();
            int scaled = ApplyGroupSizes();

            if (changed == 0 && thickened == 0 && scaled == 0)
            {
                UpdateStatus("Nothing to apply — the model already matches the sliders.");
                return;
            }

            var status = changed > 0
                ? $"Applied {changed} bone length(s) to the bind pose and all {_model.LogicalAnimations.Count} animation(s)."
                : "";
            if (thickened > 0)
                status += (status.Length > 0 ? " " : "") + $"Rebuilt {thickened} mesh vertex buffer(s) for the thickness/size change.";
            if (scaled > 0)
                status += (status.Length > 0 ? " " : "") + $"Scaled {scaled} whole branch(es) from their root joint.";
            if (_skippedPrimitives > 0)
                status += $" {_skippedPrimitives} primitive(s) with compressed positions were skipped by the vertex pass.";
            // Both of these are properties of the file rather than of anything this editor did, but
            // they are the two cases where a clip can silently keep the old proportions, so they
            // are worth surfacing at the moment the lengths are written. Thickness is immune to
            // both - it never touches animation data - so they only apply when a length was.
            if (changed > 0 && unnamedAnimations > 0)
                status += $" {unnamedAnimations} unnamed animation(s) were skipped.";
            if (changed > 0 && duplicateNames.Count > 0)
                status += $" Animations sharing a name ({string.Join(", ", duplicateNames)}) were only written once.";
            UpdateStatus(status);
        }

        // Scales one bone's child joints to its combined length factor (own slider x any group
        // scale above it) times their ORIGINAL translation, in the bind pose and in every named
        // animation that animates them. Returns whether the model was actually written to - a bone
        // already sitting at this exact factor is skipped, which is what makes a second Apply with
        // nothing moved in between a genuine no-op rather than a rewrite of identical values.
        private bool ApplyBone(string bone)
        {
            if (!_bonesByName.TryGetValue(bone, out var node)) return false;

            float factor = EffectiveLengthFactor(bone);
            float current = _appliedLengthFactor.TryGetValue(bone, out var a) ? a : 1f;
            if (MathF.Abs(current - factor) < 1e-6f)
            {
                _appliedLengthFactor[bone] = factor;
                return false;
            }

            foreach (var child in JointChildren(node))
            {
                child.WithLocalTranslation(GetOriginalTranslation(child) * factor);

                foreach (var anim in _model.LogicalAnimations)
                {
                    if (string.IsNullOrEmpty(anim.Name)) continue;

                    var keys = GetOriginalAnimKeys(child, anim);
                    // No translation channel in this clip means the (already scaled) node
                    // translation is what the clip uses, so there is nothing else to write.
                    if (keys == null) continue;

                    child.WithTranslationAnimation(anim.Name,
                        keys.Select(k => (k.Time, k.Value * factor)).ToArray());
                }
            }

            _appliedLengthFactor[bone] = factor;
            return true;
        }

        // Rebuilds every skinned vertex buffer from its pristine copy, applying ALL bones' pending
        // thickness at once. Doing every bone in a single pass (rather than one bone at a time) is
        // what makes a vertex straddling two adjusted bones come out right: its displacement is the
        // weight-blended sum of both bones' radial pushes, which is also what makes the swell fade
        // out smoothly across a joint instead of ending at a seam. Returns how many vertex buffers
        // were rewritten.
        private int ApplyThickness()
        {
            if (!Unapplied(_pendingThickness, _appliedThickness).Any() &&
                !UnappliedSizes().Any()) return 0;

            foreach (var prim in _skinnedPrimitives)
            {
                var factors = new float[prim.NameByJoint.Length];
                var sizeFactors = new Vector3[prim.NameByJoint.Length];
                for (int i = 0; i < factors.Length; i++)
                {
                    var name = prim.NameByJoint[i];
                    factors[i] = name != null && _pendingThickness.TryGetValue(name, out var percent)
                        ? percent / 100f
                        : 1f;
                    sizeFactors[i] = name != null && _pendingSize.TryGetValue(name, out var sizePercent)
                        ? sizePercent.Factor
                        : Vector3.One;
                }

                var originalPositions = OriginalVertexData(prim.Positions);
                var positions = prim.Positions.AsVector3Array();
                var originalNormals = prim.Normals != null ? OriginalVertexData(prim.Normals) : null;
                var normals = prim.Normals?.AsVector3Array();

                for (int v = 0; v < originalPositions.Length; v++)
                {
                    var source = originalPositions[v];
                    var jointIndices = prim.Joints[v];
                    var jointWeights = prim.Weights[v];

                    var displacement = Vector3.Zero;
                    // How much the vertex's neighbourhood is being scaled overall, used to tilt the
                    // normal back. Starts at 1 and accumulates each bone's weighted contribution,
                    // so an un-adjusted vertex comes out exactly 1.
                    float effectiveScale = 1f;
                    BoneAxis? dominantAxis = null;
                    float dominantContribution = 0f;
                    // Per-axis counterpart of effectiveScale, for the non-uniform size correction.
                    var effectiveSize = Vector3.One;

                    for (int k = 0; k < 4; k++)
                    {
                        float weight = Component(jointWeights, k);
                        if (weight <= 0f) continue;

                        int jointIndex = (int)Component(jointIndices, k);
                        if (jointIndex < 0 || jointIndex >= factors.Length) continue;

                        // Size first: a push straight out from the joint's own origin, per axis and
                        // needing no bone direction at all. This is the one that works on a tip
                        // bone, and the only one that can move a single direction - X alone widens
                        // a foot without lengthening it.
                        var sizeFactor = sizeFactors[jointIndex];
                        if (sizeFactor != Vector3.One)
                        {
                            displacement += weight * ((source - prim.OriginByJoint[jointIndex]) * (sizeFactor - Vector3.One));
                            effectiveSize += weight * (sizeFactor - Vector3.One);
                        }

                        float factor = factors[jointIndex];
                        if (factor == 1f) continue;

                        var axis = prim.AxisByJoint[jointIndex];
                        if (axis == null) continue;

                        // Radial offset from the bone's line: the part of the vertex that thickness
                        // acts on. The component ALONG the bone is deliberately untouched, which is
                        // what keeps thickening from also lengthening. Size and thickness simply
                        // add - each is its own displacement of the same original vertex, so
                        // dialling both just moves it by the sum.
                        var relative = source - axis.Origin;
                        var perpendicular = relative - Vector3.Dot(relative, axis.Direction) * axis.Direction;

                        displacement += weight * (factor - 1f) * perpendicular;
                        effectiveScale += weight * (factor - 1f);

                        float contribution = weight * MathF.Abs(factor - 1f);
                        if (contribution > dominantContribution)
                        {
                            dominantContribution = contribution;
                            dominantAxis = axis;
                        }
                    }

                    positions[v] = source + displacement;

                    if (normals == null || originalNormals == null) continue;

                    var normal = dominantAxis == null || effectiveScale < 1e-4f
                        ? originalNormals[v]
                        : ScaleNormal(originalNormals[v], dominantAxis.Direction, effectiveScale);
                    // A NON-uniform size does tilt normals (a uniform one does not - see
                    // ScaleNormal), and the correction is the inverse-transpose of a diagonal
                    // scale, i.e. dividing each component by that axis's factor. Applied after the
                    // thickness tilt because the two displacements were applied in that order.
                    normals[v] = effectiveSize == Vector3.One ? normal : ScaleNormalPerAxis(normal, effectiveSize);
                }

                // The accessor caches the min/max it was built with, and saving validates the
                // contents against them - without this a widened mesh is rejected on write.
                prim.Positions.UpdateBounds();
                prim.Normals?.UpdateBounds();
            }

            _appliedThickness.Clear();
            foreach (var (bone, percent) in _pendingThickness) _appliedThickness[bone] = percent;
            _appliedSize.Clear();
            foreach (var (bone, percent) in _pendingSize) _appliedSize[bone] = percent;

            return _skinnedPrimitives.Count;
        }

        // Writes each dialled-in branch size as a uniform local scale on that chain's root joint,
        // in the bind pose and in any animation that keyframes that joint's scale (rare, but a
        // clip that does would otherwise stomp the value the moment it plays - the same reason the
        // length pass rewrites translation channels). Returns how many roots were written.
        private int ApplyGroupSizes()
        {
            int written = 0;
            foreach (var root in Unapplied(_pendingGroupSize, _appliedGroupSize).ToList())
            {
                if (!_bonesByName.TryGetValue(root, out var node)) continue;

                float factor = GroupSizeOf(root) / 100f;
                node.WithLocalScale(GetOriginalScale(node) * factor);

                foreach (var anim in _model.LogicalAnimations)
                {
                    if (string.IsNullOrEmpty(anim.Name)) continue;
                    var keys = GetOriginalScaleKeys(node, anim);
                    if (keys == null) continue;
                    node.WithScaleAnimation(anim.Name, keys.Select(k => (k.Time, k.Value * factor)).ToArray());
                }

                written++;
            }

            if (written == 0) return 0;

            _appliedGroupSize.Clear();
            foreach (var (bone, percent) in _pendingGroupSize) _appliedGroupSize[bone] = percent;
            return written;
        }

        private Vector3 GetOriginalScale(Node node)
        {
            if (_originalScale.TryGetValue(node, out var cached)) return cached;
            var scale = node.LocalTransform.GetDecomposed().Scale;
            _originalScale[node] = scale;
            return scale;
        }

        private (float Time, Vector3 Value)[]? GetOriginalScaleKeys(Node node, Animation anim)
        {
            var key = (node, anim.Name!);
            if (_originalScaleKeys.TryGetValue(key, out var cached)) return cached;

            var channel = anim.Channels.FirstOrDefault(c => c.TargetNode == node && c.TargetNodePath == PropertyPath.scale);
            var keys = channel?.GetScaleSampler()
                .GetLinearKeys()
                .OrderBy(k => k.Key)
                .Select(k => (k.Key, k.Value))
                .ToArray();

            _originalScaleKeys[key] = keys;
            return keys;
        }

        private static Vector3 ScaleNormalPerAxis(Vector3 normal, Vector3 scale)
        {
            // Guarded because a near-zero axis factor would blow the component up to infinity; the
            // sliders cannot reach zero, but the weighted blend that produces `scale` can in
            // principle land there when opposing adjustments cancel.
            var safe = new Vector3(
                MathF.Abs(scale.X) < 1e-4f ? 1f : scale.X,
                MathF.Abs(scale.Y) < 1e-4f ? 1f : scale.Y,
                MathF.Abs(scale.Z) < 1e-4f ? 1f : scale.Z);
            var scaled = normal / safe;
            return scaled.LengthSquared() > 1e-12f ? Vector3.Normalize(scaled) : normal;
        }

        private static float Component(Vector4 v, int index) => index switch
        {
            0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W,
        };

        // A radial scale of `scale` around `axis` stretches everything perpendicular to the axis,
        // so the surface normal has to tilt the OPPOSITE way to stay perpendicular to the surface:
        // its across-axis part shrinks by the same factor the geometry grew by (the inverse-
        // transpose of the scale), while its along-axis part is unchanged. Skipping this leaves a
        // noticeably mis-lit limb at the larger settings. Only thickness feeds `scale` here: a
        // UNIFORM scale leaves normals alone (its inverse-transpose is the same uniform scale,
        // which normalizing cancels out), so Size contributes nothing to correct for.
        private static Vector3 ScaleNormal(Vector3 normal, Vector3 axis, float scale)
        {
            var along = Vector3.Dot(normal, axis) * axis;
            var scaled = along + (normal - along) / scale;
            return scaled.LengthSquared() > 1e-12f ? Vector3.Normalize(scaled) : normal;
        }

        private Vector3[] OriginalVertexData(Accessor accessor)
        {
            if (_originalVertexData.TryGetValue(accessor, out var cached)) return cached;
            var copy = accessor.AsVector3Array().ToArray();
            _originalVertexData[accessor] = copy;
            return copy;
        }

        // A child joint's translation keys in one animation as they were before any Apply, or null
        // if that animation never animates this joint's translation. Cached (nulls included) so
        // the pristine values survive the write that immediately follows the first lookup.
        private (float Time, Vector3 Value)[]? GetOriginalAnimKeys(Node child, Animation anim)
        {
            var key = (child, anim.Name!);
            if (_originalAnimKeys.TryGetValue(key, out var cached)) return cached;

            var channel = anim.Channels.FirstOrDefault(c => c.TargetNode == child && c.TargetNodePath == PropertyPath.translation);
            var keys = channel?.GetTranslationSampler()
                .GetLinearKeys()
                .OrderBy(k => k.Key)
                .Select(k => (k.Key, k.Value))
                .ToArray();

            _originalAnimKeys[key] = keys;
            return keys;
        }

        private void ResetSelection()
        {
            var target = SelectedTarget;
            if (target == null) return;

            var bones = BonesFor(target);
            foreach (var bone in bones) ResetBone(bone);
            ApplyThickness();
            ApplyGroupSizes();

            RefreshUiFromState();
            PushLengthsToPreview();
            PushThicknessToPreview();
            PushSizeToPreview();
            PushGroupSizeToPreview();
            UpdateStatus($"Reset {bones.Count} bone(s) to their original length, thickness and size.");
        }

        private void ResetAllBones()
        {
            foreach (var name in _pendingPercent.Keys
                         .Concat(_appliedLengthFactor.Keys)
                         .Concat(_pendingThickness.Keys)
                         .Concat(_appliedThickness.Keys)
                         .Concat(_pendingSize.Keys)
                         .Concat(_appliedSize.Keys)
                         .Concat(_pendingGroupSize.Keys)
                         .Concat(_appliedGroupSize.Keys)
                         .Distinct(StringComparer.Ordinal).ToList())
                ResetBone(name);
            ApplyThickness();
            ApplyGroupSizes();

            RefreshUiFromState();
            PushLengthsToPreview();
            PushThicknessToPreview();
            PushSizeToPreview();
            PushGroupSizeToPreview();
            UpdateStatus("Reset all bones to their original length, thickness and size.");
        }

        // Clears the pending values, and - only if this bone's length was already written to the
        // model - writes the original lengths back, so a reset never leaves a baked change behind
        // that the slider no longer shows. Thickness needs no per-bone undo here: it is rebuilt
        // wholesale from the pristine vertex data by the ApplyThickness call that follows, and
        // dropping the pending entry is all that pass needs to put this bone back.
        private void ResetBone(string bone)
        {
            _pendingPercent.Remove(bone);
            if (_appliedLengthFactor.TryGetValue(bone, out var applied) && MathF.Abs(applied - 1f) > 1e-6f)
                ApplyBone(bone);
            _appliedLengthFactor.Remove(bone);

            // Cleared before ApplyGroupSizes runs, so that pass restores the root's original scale.
            _pendingGroupSize.Remove(bone);

            _pendingThickness.Remove(bone);
            _pendingSize.Remove(bone);
        }

        // ---------------------------------------------------------------------------------------
        // Live preview
        // ---------------------------------------------------------------------------------------

        // The preview does the same scaling on the JS side, per frame, rather than re-exporting the
        // model on every slider move: nothing here needs the file to change, and a reload would
        // restart whatever animation is playing on each tick of the slider.
        private void PushLengthsToPreview()
        {
            if (!_viewerReady) return;

            var factors = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var bone in _pendingPercent.Keys.Concat(_pendingGroupSize.Keys).Distinct(StringComparer.Ordinal))
            {
                if (!_bonesByName.TryGetValue(bone, out var node)) continue;
                float factor = EffectiveLengthFactor(bone);
                foreach (var child in JointChildren(node))
                    if (!string.IsNullOrEmpty(child.Name))
                        factors[child.Name] = factor;
            }

            CallJs("setLengthFactors", factors);
        }

        // Keyed by the BONE being thickened (unlike lengths, which are keyed by the child joint
        // whose translation moves) - thickness is measured around the bone's own axis.
        private void PushThicknessToPreview()
        {
            if (!_viewerReady) return;

            var factors = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (bone, percent) in _pendingThickness) factors[bone] = percent / 100f;

            CallJs("setThicknessFactors", factors);
        }

        private void PushSizeToPreview()
        {
            if (!_viewerReady) return;

            var factors = new Dictionary<string, float[]>(StringComparer.Ordinal);
            foreach (var (bone, percent) in _pendingSize)
            {
                var f = percent.Factor;
                factors[bone] = new[] { f.X, f.Y, f.Z };
            }

            CallJs("setSizeFactors", factors);
        }

        // Root bone -> the uniform scale to put on that joint. The preview applies it the same way
        // the bake does, so three.js propagates it down the branch for free.
        private void PushGroupSizeToPreview()
        {
            if (!_viewerReady) return;

            var factors = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (bone, percent) in _pendingGroupSize) factors[bone] = percent / 100f;

            CallJs("setBranchScales", factors);
        }

        private void PushSelectionToPreview()
        {
            if (!_viewerReady) return;
            CallJs("setSelectedBones", SelectedTarget?.Bones ?? Array.Empty<string>());
        }

        private void PushShowBones()
        {
            if (!_viewerReady) return;
            _webView.CoreWebView2.ExecuteScriptAsync($"setBonesVisible({(_chkShowBones.Checked ? "true" : "false")});");
        }

        // Serialized twice on purpose: the inner call produces the JSON, the outer one turns that
        // JSON into a correctly escaped JS string literal to pass it in as a single argument.
        private void CallJs(string function, object payload)
        {
            var json = JsonSerializer.Serialize(payload);
            _webView.CoreWebView2.ExecuteScriptAsync($"{function}({JsonSerializer.Serialize(json)});");
        }

        // Fired when a bone marker in the preview is clicked. Selecting the matching item in the
        // dropdown does the rest - OnTargetSelected already restores that bone's slider and pushes
        // the highlight, exactly as if it had been picked from the dropdown directly. Clicking
        // always lands on the individual bone rather than on a group containing it, so a click is
        // an unambiguous "this one bone" even while a group happens to be selected.
        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            ViewerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (message?.Action != "boneSelected" || message.Bone == null || IsDisposed) return;

            int index = _targets.FindIndex(t => !t.IsGroup && t.Bones[0] == message.Bone);
            if (index >= 0) _targetDropdown.SelectedIndex = index;
        }

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public string? Bone { get; set; }
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
            // fire-and-forget startup may still be mid-await, so both the await itself and
            // everything after it have to tolerate that.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_adjuster_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (!_viewerReady) return;
                PushLengthsToPreview();
                PushThicknessToPreview();
                PushSizeToPreview();
                PushSelectionToPreview();
                PushShowBones();
            };
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _model.SaveGLB(previewPath);
            var previewFileName = Path.GetFileName(previewPath);

            // Every joint gets a sphere (including the leaf joints that have no length of their
            // own) so the overlay reads as a whole skeleton, while the segments - one per bone per
            // child joint - are the clickable representation of the thing this editor actually
            // edits. Both carry RAW glTF names, which is what the click messages post back and what
            // the C# side matches on.
            var jointNamesJson = JsonSerializer.Serialize(
                _jointNodes.Where(n => !string.IsNullOrEmpty(n.Name)).Select(n => n.Name).ToArray());
            var segmentsJson = JsonSerializer.Serialize(
                _bonesByName.Values
                    .SelectMany(bone => JointChildren(bone)
                        .Where(child => !string.IsNullOrEmpty(child.Name))
                        .Select(child => new { p = bone.Name, c = child.Name }))
                    .ToArray());

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
                        // Each bone's translation as loaded, i.e. before any length factor was
                        // ever applied - the equivalent of the C# side's _originalTranslation
                        // cache, and for the same reason: scaling has to run against a fixed
                        // original every frame, not against the previous frame's result.
                        var bindPos = {};
                        var currentClip = null;

                        // Raw glTF names, straight from the C# side.
                        var jointNames = " + jointNamesJson + @";
                        var boneSegments = " + segmentsJson + @";

                        // Child bone name -> factor to scale its translation by. Keys are
                        // sanitized (see sanitize below) so they match what GLTFLoader named the
                        // objects it created.
                        var lengthFactors = {};
                        // Which bones the CURRENT clip drives the translation of. Those have no
                        // fixed source value to scale - the mixer rewrites their translation every
                        // frame - so they are scaled in place, and appliedFactors below records
                        // what was scaled in so the next frame can divide it back out first.
                        // Everything else is set outright from its bind translation, which needs
                        // no such bookkeeping and can't accumulate rounding error.
                        var animatedPos = {};
                        var appliedFactors = {};

                        // The skeleton overlay: one sphere per joint, one capsule-ish cylinder per
                        // bone segment. Both are added to `scene` rather than parented to the bones
                        // and just copy world positions every frame - a bone's LOCAL scale is often
                        // not 1 (a root bone scaled 0.01x is common on Mixamo-style rigs), and a
                        // real child would inherit that scale, shrinking the marker into invisible
                        // or distorted geometry. depthTest is off so the skeleton stays visible
                        // inside the mesh.
                        var jointMarkers = [];
                        var segmentMarkers = [];
                        var bonesVisible = false;
                        var selectedBones = {};

                        // One entry per skinned mesh, holding its pristine vertex data and each
                        // bone's bind-space axis - the JS mirror of the C# side's
                        // _skinnedPrimitives, computing the identical displacement so the preview
                        // and the bake agree.
                        var skinnedMeshes = [];
                        // Raw bone name -> factor, as sent from C#. Resolved against each mesh's
                        // own skeleton indices inside rebuildThickness.
                        var thicknessFactors = {};
                        var sizeFactors = {};
                        // Root bone -> uniform scale for its whole branch, applied straight to that
                        // bone's node scale so three.js propagates it down the chain exactly as
                        // the exported file will.
                        var branchScales = {};
                        var bindScale = {};

                        var COLOR_JOINT = 0x00e5ff, COLOR_SEGMENT = 0x0d94ad, COLOR_SELECTED = 0xffee00;

                        // GLTFLoader runs node names through THREE.PropertyBinding.sanitizeNodeName
                        // before using them, so a glTF joint called 'mixamorig:LeftArm' is called
                        // 'mixamorigLeftArm' here and in the animation tracks. The C# side sends
                        // raw glTF names, so every name coming in gets the same treatment before
                        // it is used as a lookup - while the raw name is what gets posted back.
                        function sanitize(name) {
                            return THREE.PropertyBinding
                                ? THREE.PropertyBinding.sanitizeNodeName(name)
                                : name;
                        }

                        function boneFor(rawName) { return bonesByName[sanitize(rawName)]; }

                        window.setLengthFactors = function (json) {
                            var raw = JSON.parse(json);
                            var next = {};
                            for (var name in raw) next[sanitize(name)] = raw[name];
                            // A bone dropped from the map (its adjustment was reset) has to be
                            // carried forward at 1 rather than simply forgotten - forgetting it
                            // would just stop updating it, leaving it stuck at whatever scale it
                            // last had.
                            for (var old in lengthFactors)
                                if (!(old in next)) next[old] = 1;
                            lengthFactors = next;
                        };

                        window.setThicknessFactors = function (json) {
                            thicknessFactors = JSON.parse(json);
                            rebuildThickness();
                        };

                        window.setSizeFactors = function (json) {
                            sizeFactors = JSON.parse(json);
                            rebuildThickness();
                        };

                        window.setBranchScales = function (json) {
                            var raw = JSON.parse(json);
                            var next = {};
                            for (var name in raw) next[sanitize(name)] = raw[name];
                            // Same carry-forward-at-1 rule the length factors use: a root dropped
                            // from the map has been reset and must be driven back to its bind
                            // scale, not simply left wherever it was.
                            for (var old in branchScales)
                                if (!(old in next)) next[old] = 1;
                            branchScales = next;
                        };

                        // Recomputed only when a factor actually changes, never per frame: this is
                        // a full pass over every skinned vertex, and the result is static geometry
                        // that the skinning shader then animates for free.
                        function rebuildThickness() {
                            for (var m = 0; m < skinnedMeshes.length; m++) {
                                var entry = skinnedMeshes[m];

                                // Bone-name keys resolved to this skeleton's own joint indices.
                                var factors = new Float32Array(entry.axes.length);
                                // [x,y,z] per bone - size is per-axis so one direction can move on
                                // its own, which is the only way to widen a foot without
                                // lengthening it.
                                var sizes = [];
                                var anyChange = false;
                                for (var b = 0; b < factors.length; b++) {
                                    var name = entry.boneNames[b];
                                    var f = (name && thicknessFactors[name] !== undefined) ? thicknessFactors[name] : 1;
                                    var s = (name && sizeFactors[name] !== undefined) ? sizeFactors[name] : null;
                                    factors[b] = f;
                                    sizes.push(s);
                                    if (f !== 1 || (s && (s[0] !== 1 || s[1] !== 1 || s[2] !== 1))) anyChange = true;
                                }

                                var geo = entry.mesh.geometry;
                                var pos = geo.attributes.position, nrm = geo.attributes.normal;
                                var si = geo.attributes.skinIndex, sw = geo.attributes.skinWeight;
                                var op = entry.originalPositions, on = entry.originalNormals;

                                for (var v = 0; v < op.length / 3; v++) {
                                    var px = op[v * 3], py = op[v * 3 + 1], pz = op[v * 3 + 2];
                                    var dx = 0, dy = 0, dz = 0, effective = 1;
                                    var esx = 1, esy = 1, esz = 1;
                                    var bestAxis = null, bestContribution = 0;

                                    if (anyChange) {
                                        for (var k = 0; k < 4; k++) {
                                            var weight = k === 0 ? sw.getX(v) : k === 1 ? sw.getY(v) : k === 2 ? sw.getZ(v) : sw.getW(v);
                                            if (weight <= 0) continue;
                                            var ji = k === 0 ? si.getX(v) : k === 1 ? si.getY(v) : k === 2 ? si.getZ(v) : si.getW(v);
                                            if (ji < 0 || ji >= factors.length) continue;

                                            // Size: straight out from the joint origin, no axis
                                            // needed - the one that works on a tip bone.
                                            var sizeFactor = sizes[ji];
                                            if (sizeFactor) {
                                                var o = entry.origins[ji];
                                                dx += weight * (px - o[0]) * (sizeFactor[0] - 1);
                                                dy += weight * (py - o[1]) * (sizeFactor[1] - 1);
                                                dz += weight * (pz - o[2]) * (sizeFactor[2] - 1);
                                                esx += weight * (sizeFactor[0] - 1);
                                                esy += weight * (sizeFactor[1] - 1);
                                                esz += weight * (sizeFactor[2] - 1);
                                            }

                                            var factor = factors[ji];
                                            if (factor === 1) continue;
                                            var axis = entry.axes[ji];
                                            if (!axis) continue;

                                            var rx = px - axis[0], ry = py - axis[1], rz = pz - axis[2];
                                            var along = rx * axis[3] + ry * axis[4] + rz * axis[5];
                                            var scale = weight * (factor - 1);
                                            dx += scale * (rx - along * axis[3]);
                                            dy += scale * (ry - along * axis[4]);
                                            dz += scale * (rz - along * axis[5]);
                                            effective += scale;

                                            var contribution = weight * Math.abs(factor - 1);
                                            if (contribution > bestContribution) { bestContribution = contribution; bestAxis = axis; }
                                        }
                                    }

                                    pos.setXYZ(v, px + dx, py + dy, pz + dz);

                                    if (!nrm || !on) continue;
                                    var sx = on[v * 3], sy = on[v * 3 + 1], sz = on[v * 3 + 2];
                                    if (bestAxis && effective >= 1e-4) {
                                        var na = sx * bestAxis[3] + sy * bestAxis[4] + sz * bestAxis[5];
                                        var ax = na * bestAxis[3], ay = na * bestAxis[4], az = na * bestAxis[5];
                                        sx = ax + (sx - ax) / effective;
                                        sy = ay + (sy - ay) / effective;
                                        sz = az + (sz - az) / effective;
                                    }
                                    // Inverse-transpose of the per-axis size, matching
                                    // ScaleNormalPerAxis on the C# side.
                                    if (esx !== 1 || esy !== 1 || esz !== 1) {
                                        sx /= (Math.abs(esx) < 1e-4 ? 1 : esx);
                                        sy /= (Math.abs(esy) < 1e-4 ? 1 : esy);
                                        sz /= (Math.abs(esz) < 1e-4 ? 1 : esz);
                                    }
                                    var len = Math.sqrt(sx * sx + sy * sy + sz * sz) || 1;
                                    nrm.setXYZ(v, sx / len, sy / len, sz / len);
                                }

                                pos.needsUpdate = true;
                                if (nrm) nrm.needsUpdate = true;
                                geo.computeBoundingSphere();
                            }
                        }

                        window.setSelectedBones = function (json) {
                            selectedBones = {};
                            JSON.parse(json).forEach(function (name) { selectedBones[name] = true; });
                            applySelectionHighlight();
                        };

                        window.setBonesVisible = function (value) {
                            bonesVisible = value;
                            jointMarkers.forEach(function (m) { m.visible = value; });
                            segmentMarkers.forEach(function (m) { m.visible = value; });
                            // Placed right now rather than on the next animation frame: a segment
                            // marker starts life as a unit cylinder at the origin, so showing one
                            // before it has been fitted to its bone flashes a huge misplaced tube
                            // across the middle of the model.
                            if (value) updateBoneOverlay();
                        };

                        // Both marker arrays are built when the model finishes loading, but the
                        // C# side pushes visibility and selection as soon as the page navigates -
                        // these all just update state and re-apply it, so whichever arrives first
                        // is a no-op until the other catches up.
                        function applySelectionHighlight() {
                            jointMarkers.forEach(function (m) {
                                m.material.color.setHex(selectedBones[m.userData.boneName] ? COLOR_SELECTED : COLOR_JOINT);
                            });
                            segmentMarkers.forEach(function (m) {
                                m.material.color.setHex(selectedBones[m.userData.boneName] ? COLOR_SELECTED : COLOR_SEGMENT);
                            });
                        }

                        function refreshAnimatedPos() {
                            animatedPos = {};
                            if (!currentClip) return;
                            currentClip.tracks.forEach(function (track) {
                                var dot = track.name.lastIndexOf('.');
                                if (dot > 0 && track.name.substring(dot + 1) === 'position')
                                    animatedPos[track.name.substring(0, dot)] = true;
                            });
                        }

                        // Takes back whatever was scaled in place last frame, so the value
                        // applyLengths scales is always the clip's own, unscaled one. Necessary
                        // because the mixer is not guaranteed to have overwritten it since -
                        // while paused it writes nothing at all, and multiplying the already
                        // scaled translation again is how a paused preview would otherwise walk
                        // a bone off to infinity one frame at a time.
                        function unapplyLengths() {
                            for (var name in appliedFactors) {
                                var bone = bonesByName[name];
                                if (bone) bone.position.multiplyScalar(1 / appliedFactors[name]);
                            }
                            appliedFactors = {};
                        }

                        function applyLengths() {
                            for (var name in lengthFactors) {
                                var bone = bonesByName[name];
                                if (!bone) continue;
                                var factor = lengthFactors[name];
                                if (animatedPos[name]) {
                                    if (factor !== 1) {
                                        bone.position.multiplyScalar(factor);
                                        appliedFactors[name] = factor;
                                    }
                                } else if (bindPos[name]) {
                                    bone.position.copy(bindPos[name]).multiplyScalar(factor);
                                }
                            }

                            // Branch scale goes on the root bone itself and three.js carries it
                            // down the chain, so this is the whole of it - no per-vertex work, and
                            // the preview matches what the exported node scale will do. Set from
                            // the captured bind scale every frame, so an animation's own scale
                            // track can't leave a stale value behind.
                            for (var rootName in branchScales) {
                                var root = bonesByName[rootName];
                                if (!root || !bindScale[rootName]) continue;
                                root.scale.copy(bindScale[rootName]).multiplyScalar(branchScales[rootName]);
                            }
                        }

                        // Which bone each bone feeds into, from the same segment list the overlay
                        // uses - a bone's axis points at its first child joint.
                        var childOfBone = {};
                        boneSegments.forEach(function (seg) {
                            if (childOfBone[seg.p] === undefined) childOfBone[seg.p] = seg.c;
                        });

                        // GLTFLoader's sanitized bone name back to the raw glTF name C# uses.
                        var rawBySanitized = {};
                        jointNames.forEach(function (n) { rawBySanitized[sanitize(n)] = n; });

                        // Each skinned mesh's bind-space bone axes and a pristine copy of its
                        // vertex data. The axis of bone i starts at the translation of the INVERSE
                        // of its inverse bind matrix - undoing the inverse bind puts the joint back
                        // among the vertices - brought into the geometry's own space by
                        // bindMatrixInverse, which is what geometry positions are expressed in.
                        function collectSkinnedMeshes() {
                            scene.traverse(function (obj) {
                                if (!obj.isSkinnedMesh || !obj.skeleton) return;
                                var geo = obj.geometry;
                                if (!geo.attributes.position || !geo.attributes.skinIndex || !geo.attributes.skinWeight) return;

                                var bones = obj.skeleton.bones;
                                var inverses = obj.skeleton.boneInverses;
                                var indexByName = {};
                                for (var i = 0; i < bones.length; i++) indexByName[bones[i].name] = i;

                                var points = [];
                                var tmp = new THREE.Matrix4(), v = new THREE.Vector3();
                                for (var i = 0; i < bones.length; i++) {
                                    tmp.copy(inverses[i]).invert().premultiply(obj.bindMatrixInverse);
                                    points.push(v.setFromMatrixPosition(tmp).clone());
                                }

                                // Every bone gets an origin and a raw name - Size applies to bones
                                // with nothing below them, so this cannot be limited to the ones
                                // that appear as a segment's parent. Axes stay null for those.
                                var axes = [], origins = [], boneNames = [];
                                for (var i = 0; i < bones.length; i++) {
                                    // The factors C# sends are keyed by RAW glTF name, while the
                                    // skeleton is indexed by GLTFLoader's sanitized ones.
                                    boneNames.push(rawBySanitized[bones[i].name] || null);
                                    origins.push([points[i].x, points[i].y, points[i].z]);
                                    axes.push(null);
                                }
                                // [originX, originY, originZ, dirX, dirY, dirZ] per bone that has
                                // a child in this skeleton to point at.
                                for (var raw in childOfBone) {
                                    var pi = indexByName[sanitize(raw)];
                                    var ci = indexByName[sanitize(childOfBone[raw])];
                                    if (pi === undefined || ci === undefined) continue;
                                    var d = new THREE.Vector3().subVectors(points[ci], points[pi]);
                                    if (d.lengthSq() < 1e-12) continue;
                                    d.normalize();
                                    axes[pi] = [points[pi].x, points[pi].y, points[pi].z, d.x, d.y, d.z];
                                }

                                var pos = geo.attributes.position, nrm = geo.attributes.normal;
                                var originalPositions = new Float32Array(pos.count * 3);
                                for (var q = 0; q < pos.count; q++) {
                                    originalPositions[q * 3] = pos.getX(q);
                                    originalPositions[q * 3 + 1] = pos.getY(q);
                                    originalPositions[q * 3 + 2] = pos.getZ(q);
                                }
                                var originalNormals = null;
                                if (nrm) {
                                    originalNormals = new Float32Array(nrm.count * 3);
                                    for (var q = 0; q < nrm.count; q++) {
                                        originalNormals[q * 3] = nrm.getX(q);
                                        originalNormals[q * 3 + 1] = nrm.getY(q);
                                        originalNormals[q * 3 + 2] = nrm.getZ(q);
                                    }
                                }

                                skinnedMeshes.push({
                                    mesh: obj, axes: axes, origins: origins, boneNames: boneNames,
                                    originalPositions: originalPositions, originalNormals: originalNormals
                                });
                            });
                        }

                        var segFrom = new THREE.Vector3(), segTo = new THREE.Vector3(), segDir = new THREE.Vector3();
                        var segUp = new THREE.Vector3(0, 1, 0);

                        // Run after applyLengths so the overlay shows the adjusted skeleton, which
                        // is the whole point of watching it while dragging the slider.
                        function updateBoneOverlay() {
                            for (var i = 0; i < jointMarkers.length; i++)
                                jointMarkers[i].userData.bone.getWorldPosition(jointMarkers[i].position);

                            for (var j = 0; j < segmentMarkers.length; j++) {
                                var seg = segmentMarkers[j];
                                seg.userData.from.getWorldPosition(segFrom);
                                seg.userData.to.getWorldPosition(segTo);
                                segDir.subVectors(segTo, segFrom);
                                var length = segDir.length();
                                // A zero-length bone has no direction to orient to, and
                                // setFromUnitVectors on a zero vector produces NaNs that would
                                // poison the marker's matrix for good.
                                if (length < 1e-9) { seg.visible = false; continue; }
                                seg.visible = bonesVisible;
                                seg.position.copy(segFrom).addScaledVector(segDir, 0.5);
                                seg.quaternion.setFromUnitVectors(segUp, segDir.divideScalar(length));
                                // The unit cylinder is 1 tall along its own Y, so scaling Y by the
                                // bone's length is what makes the marker grow with the adjustment.
                                seg.scale.set(seg.userData.radius, length, seg.userData.radius);
                            }
                        }

                        window.setAnimationByName = function (name) {
                            currentClip = null;
                            if (mixer) mixer.stopAllAction();
                            if (name && mixer) {
                                var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                                if (clip) {
                                    currentClip = clip;
                                    mixer.clipAction(clip).play();
                                }
                            }
                            refreshAnimatedPos();
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
                                        bindPos[obj.name] = obj.position.clone();
                                        bindScale[obj.name] = obj.scale.clone();
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

                                var jointGeom = new THREE.SphereGeometry(1, 12, 8);
                                var jointRadius = maxDim * 0.006;
                                jointNames.forEach(function (name) {
                                    var bone = boneFor(name);
                                    if (!bone) return;
                                    var marker = new THREE.Mesh(jointGeom, new THREE.MeshBasicMaterial({ color: COLOR_JOINT, depthTest: false }));
                                    marker.scale.setScalar(jointRadius);
                                    marker.renderOrder = 1000;
                                    marker.visible = bonesVisible;
                                    marker.userData.boneName = name;
                                    marker.userData.bone = bone;
                                    scene.add(marker);
                                    jointMarkers.push(marker);
                                });

                                // Unit height along +Y and unit radius, so updateBoneOverlay can
                                // express both the bone's thickness and its length purely as
                                // scale. Capped rather than open-ended so a bone pointing straight
                                // at the camera still presents a face to pick.
                                var segGeom = new THREE.CylinderGeometry(1, 1, 1, 8);
                                boneSegments.forEach(function (seg) {
                                    var from = boneFor(seg.p), to = boneFor(seg.c);
                                    if (!from || !to) return;
                                    var marker = new THREE.Mesh(segGeom, new THREE.MeshBasicMaterial({ color: COLOR_SEGMENT, depthTest: false }));
                                    marker.renderOrder = 999;
                                    marker.visible = bonesVisible;
                                    // Named for the PARENT: the segment is that bone's length, so
                                    // clicking it has to select the bone whose slider moves it.
                                    marker.userData.boneName = seg.p;
                                    marker.userData.from = from;
                                    marker.userData.to = to;
                                    marker.userData.radius = maxDim * 0.0025;
                                    scene.add(marker);
                                    segmentMarkers.push(marker);
                                });
                                applySelectionHighlight();
                                collectSkinnedMeshes();
                                rebuildThickness();

                                controls.target.copy(center);
                                camera.position.copy(center).add(new THREE.Vector3(maxDim, maxDim * 0.6, maxDim));
                                camera.near = maxDim / 1000;
                                camera.far = maxDim * 100;
                                camera.updateProjectionMatrix();
                                controls.update();

                                window._clips = gltf.animations || [];
                                if (window._clips.length > 0) {
                                    mixer = new THREE.AnimationMixer(gltf.scene);
                                    currentClip = window._clips[0];
                                    mixer.clipAction(currentClip).play();
                                }
                                refreshAnimatedPos();
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

                        // Hidden markers are skipped by THREE's own hit-testing (it checks
                        // .visible internally), so unticking 'Show bones' disables picking for
                        // free. The pointerdown/up distance check is what keeps releasing an orbit
                        // drag over a bone from counting as a click on it.
                        var raycaster = new THREE.Raycaster();
                        var pickVec = new THREE.Vector2();
                        var pressX = 0, pressY = 0;
                        canvas.addEventListener('pointerdown', function (event) {
                            pressX = event.clientX; pressY = event.clientY;
                        });
                        canvas.addEventListener('click', function (event) {
                            if (!bonesVisible) return;
                            if (Math.abs(event.clientX - pressX) > 4 || Math.abs(event.clientY - pressY) > 4) return;

                            var rect = canvas.getBoundingClientRect();
                            pickVec.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
                            pickVec.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
                            raycaster.setFromCamera(pickVec, camera);
                            var hits = raycaster.intersectObjects(jointMarkers.concat(segmentMarkers), false);
                            if (hits.length > 0 && window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({
                                    action: 'boneSelected', bone: hits[0].object.userData.boneName
                                }));
                            }
                        });

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            unapplyLengths();
                            if (mixer && !paused) mixer.update(delta);
                            // After the mixer, so the scaling wins over whatever translation the
                            // clip just wrote - the same ordering the baked version ends up with,
                            // where the animation's own keys are the thing being scaled.
                            applyLengths();
                            if (bonesVisible) updateBoneOverlay();
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
