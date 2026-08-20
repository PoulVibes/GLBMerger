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
    // Repositions a joint's REST (bind) location and rebakes the skeleton/skin around the new
    // position - unlike JointOrientationEditor, which only ever patches an animation's rotation/
    // translation curves and never touches the bind pose, this edits the actual skeleton
    // structure: the joint's own LocalTransform, its connected bones (its children, which either
    // ride along with it or are compensated to hold still - see "Move children with joint"), and
    // every skin's inverse bind matrices, so the mesh doesn't distort at rest around a joint that
    // has moved. That inverse-bind-matrix recompute is the actual "rebake" - without it the joint
    // would visibly have moved but the mesh would still deform as though it hadn't.
    //
    // Nothing is written to the model until "Bake Rig Changes" is clicked; up to that point,
    // moving the sliders only repositions marker spheres and the skeleton lines between them in
    // the live preview; the mesh itself is left completely alone so it stays a clean, undistorted
    // reference to align the joint against.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window
    // chrome shared by all of them, so this control only contributes its own left-hand controls
    // and 3D preview.
    public class RiggingEditor : UserControl
    {
        private readonly ModelRoot _model;
        private readonly AppSettings _settings;

        private Panel _movePanel = null!;
        private Panel _addRiggingPanel = null!;
        private ComboBox _boneDropdown = null!;
        private ComboBox _animDropdown = null!;
        private NumericUpDown _numX = null!, _numY = null!, _numZ = null!;
        private CheckBox _chkMoveChildren = null!;
        private CheckBox _chkMirror = null!;
        private CheckBox _chkShowJoints = null!;
        private ComboBox _libraryCombo = null!;
        private NumericUpDown _numWeightBlend = null!;
        private Button _btnPause = null!;
        private Label _lblStatus = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;
        private bool _suppressEvents;

        // Bone name -> not-yet-baked WORLD-space offset from its original rest position, plus
        // whether that offset should carry its connected child bones along with it (rigid subtree
        // move) or leave them exactly where they were (stretching just this one bone). Nothing
        // here is written to the model until Bake Rig Changes - see BakeRigChanges.
        private readonly Dictionary<string, (Vector3 Delta, bool MoveChildren)> _pendingOffsets = new();

        // Bone name -> whether "Mirror to opposite side" was checked while that bone was
        // selected, kept per bone (like _pendingOffsets) so switching away and back restores the
        // checkbox exactly, the same pattern JointOrientationEditor uses for its own mirror flags.
        private readonly Dictionary<string, bool> _mirrorFlags = new();

        private string[] _jointNames = Array.Empty<string>();
        private Dictionary<string, Node> _nodeByName = new();

        private int _glbVersion;

        public RiggingEditor(ModelRoot model, bool darkMode = false, AppSettings? settings = null)
        {
            _model = model;
            _settings = settings ?? new AppSettings();

            Dock = DockStyle.Fill;

            BuildUi();
            PopulateBoneList();
            PopulateAnimationList();
            RefreshRiggedState();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, Padding = new Padding(12), AutoScroll = true };

            int y = 12;
            const int gap = 6;

            // Shown only while the model has no skin at all - lets a bare/unrigged mesh be
            // rigged from a properly-rigged reference model before there's any joint to move.
            _addRiggingPanel = BuildAddRiggingPanel();
            _addRiggingPanel.Left = 0;
            _addRiggingPanel.Top = y;
            y += _addRiggingPanel.Height + gap * 2;

            _movePanel = BuildMovePanel();
            _movePanel.Left = 0;
            _movePanel.Top = y;
            y += _movePanel.Height + gap;

            _lblStatus = new Label { Left = 12, Top = y, Width = 340, Height = 48, AutoSize = false, ForeColor = System.Drawing.Color.LightGreen };

            controlPanel.Controls.Add(_addRiggingPanel);
            controlPanel.Controls.Add(_movePanel);
            controlPanel.Controls.Add(_lblStatus);

            _webView = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        // Everything needed to bootstrap a skeleton onto a model that doesn't have one yet -
        // hidden once the model actually has a skin (see RefreshRiggedState).
        private Panel BuildAddRiggingPanel()
        {
            var panel = new Panel { Width = 348, AutoSize = false };
            int y = 0;

            var lblHelp = new Label
            {
                Text = "This model has no rigging yet. Pick a properly-rigged reference model " +
                       "from your animation library and Add Rigging will copy its skeleton in, " +
                       "scaled to this model's own height and width as a starting position - " +
                       "move individual joints below afterward to line them up exactly.",
                Left = 0, Top = y, Width = 340, AutoSize = false, Height = 84,
            };
            y += lblHelp.Height + 4;

            var lblLibrary = new Label { Text = "Reference rigged model:", Left = 0, Top = y, AutoSize = true };
            y += lblLibrary.Height + 2;
            _libraryCombo = new ComboBox { Left = 0, Top = y, Width = 240, DropDownStyle = ComboBoxStyle.DropDownList };
            RefreshLibraryCombo();
            var btnChangeDir = new Button { Text = "...", Left = 246, Top = y, Width = 30 };
            btnChangeDir.Click += (s, e) => ChangeLibraryDirectory();
            y += _libraryCombo.Height + 6;

            // How far the weight blend spreads out from each bone boundary, in edge rings. 0
            // gives the old fully-rigid behaviour (every vertex bound 100% to one bone, which
            // tears at bending joints); higher values give a softer, wider blend. Anatomically
            // this wants to be roughly the width of the crease at an elbow/knee, so a handful of
            // rings suits typical character meshes.
            var lblBlend = new Label { Text = "Joint blend width (0 = fully rigid):", Left = 0, Top = y, AutoSize = true };
            y += lblBlend.Height + 2;
            _numWeightBlend = new NumericUpDown
            {
                Left = 0, Top = y, Width = 80,
                Minimum = 0, Maximum = 40, Value = 8, Increment = 1,
            };
            y += _numWeightBlend.Height + 8;

            var btnAddRigging = new Button { Text = "Add Rigging From Selected Model", Left = 0, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(340, 0) };
            btnAddRigging.Click += (s, e) => AddRiggingFromLibrary();
            y += btnAddRigging.Height + 4;

            panel.Height = y;
            panel.Controls.AddRange(new Control[] { lblHelp, lblLibrary, _libraryCombo, btnChangeDir, lblBlend, _numWeightBlend, btnAddRigging });
            return panel;
        }

        private Panel BuildMovePanel()
        {
            var panel = new Panel { Width = 348, AutoSize = false };
            int y = 0;
            const int gap = 6;

            var lblHelp = new Label
            {
                Text = "Move a joint's rest location. Connected bones follow it unless \"Move " +
                       "children with joint\" is unchecked, in which case only this joint's own " +
                       "bones stretch and everything below it holds still. Nothing changes in the " +
                       "model until Bake Rig Changes is clicked.",
                Left = 0, Top = y, Width = 340, AutoSize = false, Height = 72,
            };
            y += lblHelp.Height + gap;

            var lblBone = new Label { Text = "Joint / Bone:", Left = 0, Top = y, AutoSize = true };
            y += lblBone.Height + 2;
            _boneDropdown = new ComboBox { Left = 0, Top = y, Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
            _boneDropdown.SelectedIndexChanged += (s, e) => OnBoneSelected();
            y += _boneDropdown.Height + gap * 2;

            var lblAnim = new Label { Text = "Preview Animation:", Left = 0, Top = y, AutoSize = true };
            y += lblAnim.Height + 2;
            _animDropdown = new ComboBox { Left = 0, Top = y, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", Left = 198, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(82, 0) };
            _btnPause.Click += (s, e) => TogglePause();
            y += Math.Max(_animDropdown.Height, _btnPause.Height) + gap;

            _chkShowJoints = new CheckBox { Text = "Show Joints (click to select)", Left = 0, Top = y, AutoSize = true, Checked = true };
            _chkShowJoints.CheckedChanged += (s, e) => PushShowJoints();
            y += _chkShowJoints.Height + gap * 2;

            _chkMoveChildren = new CheckBox { Text = "Move children with joint", Left = 0, Top = y, AutoSize = true, Checked = true };
            _chkMoveChildren.CheckedChanged += (s, e) => OnOffsetChanged();
            y += _chkMoveChildren.Height + gap;

            _chkMirror = new CheckBox { Text = "Mirror to opposite side (Left/Right)", Left = 0, Top = y, AutoSize = true, Checked = true };
            _chkMirror.CheckedChanged += (s, e) => OnOffsetChanged();
            y += _chkMirror.Height + gap * 2;

            (_numX, _) = MakeNumericPosition("X Offset", ref y);
            (_numY, _) = MakeNumericPosition("Y Offset", ref y);
            (_numZ, _) = MakeNumericPosition("Z Offset", ref y);

            y += gap;

            var btnBake = new Button { Text = "Bake Rig Changes", Left = 0, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(340, 0) };
            btnBake.Click += (s, e) => BakeRigChanges();
            y += btnBake.Height + gap;

            var btnResetJoint = new Button { Text = "Reset This Joint", Left = 0, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(340, 0) };
            btnResetJoint.Click += (s, e) => ResetCurrentBone();
            y += btnResetJoint.Height + gap;

            var btnResetAll = new Button { Text = "Reset All Pending Moves", Left = 0, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(340, 0) };
            btnResetAll.Click += (s, e) => ResetAllPending();
            y += btnResetAll.Height + gap;

            panel.Height = y;
            panel.Controls.AddRange(new Control[]
            {
                lblHelp, lblBone, _boneDropdown, lblAnim, _animDropdown, _btnPause,
                _chkShowJoints, _chkMoveChildren, _chkMirror,
                _numX, _numY, _numZ,
                btnBake, btnResetJoint, btnResetAll
            });
            return panel;
        }

        private void RefreshLibraryCombo()
        {
            _libraryCombo.Items.Clear();
            foreach (var file in LibraryDirectoryHelper.ListGlbFiles(_settings.AnimationLibraryDirectory))
                _libraryCombo.Items.Add(new LibraryDirectoryHelper.LibraryEntry { Path = file });
            if (_libraryCombo.Items.Count > 0) _libraryCombo.SelectedIndex = 0;
        }

        private void ChangeLibraryDirectory()
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(_settings.AnimationLibraryDirectory) ? _settings.AnimationLibraryDirectory : "",
                Description = "Choose the folder containing rigged reference .glb files",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _settings.AnimationLibraryDirectory = dlg.SelectedPath;
            _settings.Save();
            RefreshLibraryCombo();
        }

        // Shows the "Add Rigging" panel only while the model genuinely has no skin, and the
        // joint-move panel only once it does - there being nothing to move before that.
        private void RefreshRiggedState()
        {
            bool hasSkin = _model.LogicalSkins.Count > 0;
            _addRiggingPanel.Visible = !hasSkin;
            _movePanel.Visible = hasSkin;
        }

        // Same rationale as JointOrientationEditor's own MakeNumericPosition: a NumericUpDown
        // rather than a slider (joint placement needs typed precision), left unitless/wide since
        // this app's rigs range across wildly different scales.
        private (NumericUpDown, Label) MakeNumericPosition(string text, ref int y)
        {
            var lbl = new Label { Text = $"{text}: 0", Left = 12, Top = y, AutoSize = true };
            y += lbl.Height + 2;
            var numeric = new NumericUpDown
            {
                Left = 12, Top = y, Width = 140,
                Minimum = -9999m, Maximum = 9999m, DecimalPlaces = 3, Increment = 0.01m, Value = 0m
            };
            y += numeric.Height + gapConst;
            numeric.ValueChanged += (s, e) => { lbl.Text = $"{text}: {numeric.Value}"; OnOffsetChanged(); };
            return (numeric, lbl);
        }

        private const int gapConst = 10;

        private void PopulateBoneList()
        {
            _jointNames = GetJointNames(_model);
            _nodeByName = _model.LogicalNodes
                .Where(n => !string.IsNullOrEmpty(n.Name))
                .GroupBy(n => n.Name!)
                .ToDictionary(g => g.Key, g => g.First());

            _boneDropdown.Items.AddRange(_jointNames);
            if (_jointNames.Length > 0) _boneDropdown.SelectedIndex = 0;
        }

        // Every node name that actually acts as a skin's joint - every other named node is
        // geometry (a mesh-bearing part, or an empty grouping node), not something with a "rest
        // location" worth rigging here.
        private static string[] GetJointNames(ModelRoot model)
        {
            var jointNames = new HashSet<string>();
            foreach (var skin in model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (jointNode, _) = skin.GetJoint(i);
                    if (!string.IsNullOrEmpty(jointNode.Name))
                        jointNames.Add(jointNode.Name);
                }

            return jointNames.OrderBy(n => n).ToArray();
        }

        private void PopulateAnimationList()
        {
            _animDropdown.Items.Add("None (Bind Pose)");
            foreach (var anim in _model.LogicalAnimations)
                _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");

            // Defaults to the bind pose, not an animation - placing a joint is easiest against an
            // undistorted rest pose, and every marker/preview here is bind-pose based anyway.
            _animDropdown.SelectedIndex = 0;
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

        private void OnBoneSelected()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;

            var (delta, moveChildren) = _pendingOffsets.TryGetValue(boneName, out var pending) ? pending : (Vector3.Zero, true);

            _suppressEvents = true;
            _numX.Value = Math.Clamp((decimal)delta.X, _numX.Minimum, _numX.Maximum);
            _numY.Value = Math.Clamp((decimal)delta.Y, _numY.Minimum, _numY.Maximum);
            _numZ.Value = Math.Clamp((decimal)delta.Z, _numZ.Minimum, _numZ.Maximum);
            _chkMoveChildren.Checked = moveChildren;
            _chkMirror.Checked = !_mirrorFlags.TryGetValue(boneName, out var mirror) || mirror;
            _suppressEvents = false;

            if (_viewerReady)
                _webView.CoreWebView2.ExecuteScriptAsync($"setSelectedJoint('{EscapeJs(boneName)}');");
        }

        // Finds the opposite-side counterpart of a bone by swapping "Left"/"Right" in its name -
        // the same Mixamo-style convention JointOrientationEditor's own mirror support and
        // GlbMergeService's LegChains table already assume. Returns null for bones with no such
        // pair (e.g. Spine, Hips, Head).
        private static string? GetMirrorBoneName(string boneName)
        {
            if (boneName.Contains("Left", StringComparison.Ordinal)) return boneName.Replace("Left", "Right");
            if (boneName.Contains("Right", StringComparison.Ordinal)) return boneName.Replace("Right", "Left");
            return null;
        }

        private void OnOffsetChanged()
        {
            if (_suppressEvents) return;
            if (_boneDropdown.SelectedItem is not string boneName) return;

            var delta = new Vector3((float)_numX.Value, (float)_numY.Value, (float)_numZ.Value);
            bool moveChildren = _chkMoveChildren.Checked;
            if (delta == Vector3.Zero && moveChildren)
                _pendingOffsets.Remove(boneName);
            else
                _pendingOffsets[boneName] = (delta, moveChildren);

            _mirrorFlags[boneName] = _chkMirror.Checked;

            // Lateral (X) is negated, Y/Z carried over as-is - the same mirror-sign convention
            // JointOrientationEditor's own position mirroring uses, since this app's rigs are all
            // authored with X running left-right.
            var mirrorName = GetMirrorBoneName(boneName);
            if (_chkMirror.Checked && mirrorName != null && _jointNames.Contains(mirrorName))
            {
                var mirroredDelta = new Vector3(-delta.X, delta.Y, delta.Z);
                if (mirroredDelta == Vector3.Zero && moveChildren)
                    _pendingOffsets.Remove(mirrorName);
                else
                    _pendingOffsets[mirrorName] = (mirroredDelta, moveChildren);
            }

            _lblStatus.Text = "";
            PushPreviewPositions();
        }

        private void ResetCurrentBone()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;
            _pendingOffsets.Remove(boneName);
            _mirrorFlags.Remove(boneName);
            OnBoneSelected();
            PushPreviewPositions();
            _lblStatus.Text = "";
        }

        private void ResetAllPending()
        {
            _pendingOffsets.Clear();
            _mirrorFlags.Clear();
            if (_boneDropdown.SelectedItem is string) OnBoneSelected();
            PushPreviewPositions();
            _lblStatus.Text = "";
        }

        // The world-space displacement `node` should preview at, given every not-yet-baked
        // offset currently pending (its own, plus whatever it inherits from an ancestor that
        // hasn't opted its children out via "Move children with joint"). Purely additive/
        // recursive math against the model's UNCHANGED bind pose - nothing here mutates anything,
        // which is what lets the preview move markers without ever distorting the actual mesh.
        private Vector3 GetOwnTotalDelta(Node node, Dictionary<Node, Vector3> memo)
        {
            if (memo.TryGetValue(node, out var cached)) return cached;

            var passDown = Vector3.Zero;
            var parent = node.VisualParent;
            if (parent != null)
            {
                var parentTotal = GetOwnTotalDelta(parent, memo);
                bool parentPassesThrough = string.IsNullOrEmpty(parent.Name)
                    || !_pendingOffsets.TryGetValue(parent.Name, out var parentPending)
                    || parentPending.MoveChildren;
                passDown = parentPassesThrough ? parentTotal : Vector3.Zero;
            }

            var own = !string.IsNullOrEmpty(node.Name) && _pendingOffsets.TryGetValue(node.Name, out var mine)
                ? mine.Delta
                : Vector3.Zero;

            var total = passDown + own;
            memo[node] = total;
            return total;
        }

        private void PushPreviewPositions()
        {
            if (!_viewerReady) return;

            var memo = new Dictionary<Node, Vector3>();
            var positions = new Dictionary<string, float[]>();
            foreach (var name in _jointNames)
            {
                if (!_nodeByName.TryGetValue(name, out var node)) continue;
                var total = GetOwnTotalDelta(node, memo);
                var pos = node.WorldMatrix.Translation + total;
                positions[name] = new[] { pos.X, pos.Y, pos.Z };
            }

            var json = JsonSerializer.Serialize(positions);
            _webView.CoreWebView2.ExecuteScriptAsync($"setJointPreviewPositions({json});");
        }

        // Commits every not-yet-baked joint move at once: repositions each moved joint's own
        // LocalTransform (and, for any bone that isn't dragging its children along, restores
        // those direct children back to their original world position), shifts that joint's
        // existing translation animation keyframes (if any - typically only ever present on a
        // root/hips-style bone) by the same local delta so the correction holds during playback
        // too, and finally recomputes every skin's inverse bind matrices from the joints' new
        // positions. That last step is the actual "rebake" - without it the joints would have
        // moved but the mesh would still deform as though they hadn't.
        private void BakeRigChanges()
        {
            var toBake = _pendingOffsets.Where(kv => kv.Value.Delta != Vector3.Zero).ToList();
            if (toBake.Count == 0)
            {
                MessageBox.Show(this, "No joint moves to bake.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Snapshot direct children's world matrices for every bone that's keeping its
            // children in place, before ANY edits happen - this is the "hold still" reference
            // each such child gets restored to once its parent has actually moved.
            var childSnapshots = new Dictionary<Node, Matrix4x4>();
            foreach (var (name, pending) in toBake)
            {
                if (pending.MoveChildren) continue;
                if (!_nodeByName.TryGetValue(name, out var node)) continue;
                foreach (var child in node.VisualChildren)
                    childSnapshots[child] = child.WorldMatrix;
            }

            foreach (var (name, pending) in toBake)
            {
                if (!_nodeByName.TryGetValue(name, out var node)) continue;

                // A world-space offset only ever means "move by exactly this much in world
                // space" once converted into the joint's own parent-local frame - that has to
                // divide out the parent's world SCALE as well as rotate by its inverse, or it's
                // wrong by exactly that scale factor. Several of these reference rigs carry a
                // ~0.01 scale on the armature root (a cm-to-m unit conversion), which previously
                // made a requested 0.3m move land as an actual ~0.003m move - visually
                // indistinguishable from the joint never having moved at all, even though the
                // live preview (computed entirely in world space, with no such conversion) showed
                // it exactly where it was dropped right up until Bake. Rotation is never touched
                // by this tool, so the parent's world rotation/scale can be read at any point in
                // this loop with no ordering concerns.
                var parentWorldRotation = Quaternion.Identity;
                var parentWorldScale = Vector3.One;
                if (node.VisualParent != null)
                    Matrix4x4.Decompose(node.VisualParent.WorldMatrix, out parentWorldScale, out parentWorldRotation, out _);
                var rotatedDelta = Vector3.Transform(pending.Delta, Quaternion.Inverse(parentWorldRotation));
                var localDelta = new Vector3(
                    MathF.Abs(parentWorldScale.X) > 1e-6f ? rotatedDelta.X / parentWorldScale.X : rotatedDelta.X,
                    MathF.Abs(parentWorldScale.Y) > 1e-6f ? rotatedDelta.Y / parentWorldScale.Y : rotatedDelta.Y,
                    MathF.Abs(parentWorldScale.Z) > 1e-6f ? rotatedDelta.Z / parentWorldScale.Z : rotatedDelta.Z);

                Matrix4x4.Decompose(node.LocalMatrix, out _, out _, out var originalLocalTranslation);
                node.WithLocalTranslation(originalLocalTranslation + localDelta);

                foreach (var anim in _model.LogicalAnimations)
                {
                    var channel = anim.Channels.FirstOrDefault(c => c.TargetNode == node && c.TargetNodePath == PropertyPath.translation);
                    if (channel == null) continue;

                    var keys = channel.GetTranslationSampler().GetLinearKeys()
                        .OrderBy(k => k.Key)
                        .Select(k => (k.Key, k.Value + localDelta))
                        .ToArray();
                    node.WithTranslationAnimation(anim.Name, keys);
                }
            }

            // Grandchildren (and deeper) of a "hold still" bone need no fix-up of their own -
            // their local transforms never changed, so once their direct parent is put back
            // exactly where it was, they follow it right back to where they were too.
            foreach (var (child, oldWorld) in childSnapshots)
                child.WorldMatrix = oldWorld;

            foreach (var skin in _model.LogicalSkins)
            {
                var meshWorld = skin.VisualParents.FirstOrDefault()?.WorldMatrix ?? Matrix4x4.Identity;
                skin.BindJoints(meshWorld, skin.Joints.ToArray());
            }

            _pendingOffsets.Clear();
            _lblStatus.Text = $"Baked {toBake.Count} joint move(s) and rebuilt skin binding.";
            OnBoneSelected();
            ReloadPreview();
        }

        private void AddRiggingFromLibrary()
        {
            if (_libraryCombo.SelectedItem is not LibraryDirectoryHelper.LibraryEntry entry)
            {
                MessageBox.Show(this, "Pick a reference model from the library dropdown first.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            ModelRoot template;
            try
            {
                template = ModelRoot.Load(entry.Path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load '{Path.GetFileName(entry.Path)}':\n{ex.Message}", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var templateSkin = template.LogicalSkins.FirstOrDefault();
            if (templateSkin == null)
            {
                MessageBox.Show(this, $"'{Path.GetFileName(entry.Path)}' has no rigging of its own to copy.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var targetMeshNodes = _model.LogicalNodes.Where(n => n.Mesh != null).ToList();
            if (targetMeshNodes.Count == 0)
            {
                MessageBox.Show(this, "This model has no mesh geometry to rig.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var targetBounds = ComputeMeshBounds(_model);
            var templateBounds = ComputeMeshBounds(template);
            if (targetBounds == null || templateBounds == null)
            {
                MessageBox.Show(this, "Couldn't measure one of the models' geometry.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Non-uniform scale: height (Y) and horizontal footprint (the wider of X/Z) are
            // matched independently, since a taller-but-narrower reference rig shouldn't get
            // stretched sideways just because it's also being stretched vertically.
            var targetSize = targetBounds.Value.Max - targetBounds.Value.Min;
            var templateSize = templateBounds.Value.Max - templateBounds.Value.Min;
            float scaleY = templateSize.Y > 0.0001f ? targetSize.Y / templateSize.Y : 1f;
            float targetWidth = Math.Max(targetSize.X, targetSize.Z);
            float templateWidth = Math.Max(templateSize.X, templateSize.Z);
            float scaleXZ = templateWidth > 0.0001f ? targetWidth / templateWidth : 1f;

            var templatePivot = new Vector3(
                (templateBounds.Value.Min.X + templateBounds.Value.Max.X) / 2f,
                templateBounds.Value.Min.Y,
                (templateBounds.Value.Min.Z + templateBounds.Value.Max.Z) / 2f);
            var targetPivot = new Vector3(
                (targetBounds.Value.Min.X + targetBounds.Value.Max.X) / 2f,
                targetBounds.Value.Min.Y,
                (targetBounds.Value.Min.Z + targetBounds.Value.Max.Z) / 2f);

            // Only nodes that are themselves a skin joint, or an ancestor of one, are worth
            // cloning - this is what leaves behind the template's own attachment markers (ball
            // anchors, stiff-arm poses, etc.) instead of copying those in too.
            var templateJoints = new HashSet<Node>(templateSkin.Joints);
            var hasJointDescendant = new Dictionary<Node, bool>();
            bool HasJointDescendant(Node n)
            {
                if (hasJointDescendant.TryGetValue(n, out var cached)) return cached;
                bool result = templateJoints.Contains(n) || n.VisualChildren.Any(HasJointDescendant);
                hasJointDescendant[n] = result;
                return result;
            }

            // This panel only shows while the model has no active skin (see RefreshRiggedState),
            // so any node already sitting in the model whose name matches one we're about to
            // clone in - most commonly "Armature" itself, plus a full leftover joint chain - can
            // only be orphaned clutter from an earlier Add Rigging attempt that got stripped back
            // out without also removing its now-unused nodes (SharpGLTF has no node-deletion API,
            // so a strip can only unlink a skin, never remove the joint nodes it once pointed at).
            // Left in place, a second Add Rigging run would produce two identically-named "Hips"
            // (etc.) chains side by side - which is exactly what made a later Process/merge fail
            // with an ArgumentException out of SharpGLTF's own armature validation, since it has
            // no way to tell the two "Hips" apart by name. Renamed out of the way here instead of
            // silently colliding.
            var namesAboutToClone = new HashSet<string>();
            void CollectClonedNames(Node n)
            {
                if (!string.IsNullOrEmpty(n.Name)) namesAboutToClone.Add(n.Name);
                foreach (var c in n.VisualChildren.Where(HasJointDescendant)) CollectClonedNames(c);
            }
            foreach (var topLevel in template.DefaultScene.VisualChildren.Where(HasJointDescendant))
                CollectClonedNames(topLevel);

            int renamedOrphans = 0;
            foreach (var node in _model.LogicalNodes)
            {
                if (node.Name != null && namesAboutToClone.Contains(node.Name))
                {
                    node.Name = $"{node.Name}_orphaned{node.LogicalIndex}";
                    renamedOrphans++;
                }
            }

            var targetScene = _model.DefaultScene ?? (_model.DefaultScene = _model.UseScene(0));
            var newJoints = new List<Node>();

            Vector3 ScaledWorldPos(Node templateN)
            {
                Matrix4x4.Decompose(templateN.WorldMatrix, out _, out _, out var worldPos);
                return targetPivot + (worldPos - templatePivot) * new Vector3(scaleXZ, scaleY, scaleXZ);
            }

            // A joint chain with exactly one continuation gets reoriented; a fork (Hips into both
            // legs plus the spine, or Spine into both shoulders plus the neck) picks whichever
            // branch keeps the chain going straight up the trunk, since that's the one whose own
            // orientation correction actually matters - the two lateral branches get corrected
            // individually once CloneJointTree reaches them.
            Node? ChoosePrimaryChild(Node templateNode)
            {
                var candidates = templateNode.VisualChildren.Where(HasJointDescendant).ToList();
                if (candidates.Count == 0) return null;
                if (candidates.Count == 1) return candidates[0];
                return candidates.FirstOrDefault(c =>
                    c.Name != null && !c.Name.Contains("Left", StringComparison.OrdinalIgnoreCase)
                                   && !c.Name.Contains("Right", StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];
            }

            // Shortest rotation that carries unit vector `from` onto unit vector `to`.
            static Quaternion RotationBetween(Vector3 from, Vector3 to)
            {
                float d = Vector3.Dot(from, to);
                if (d > 0.999999f) return Quaternion.Identity;
                if (d < -0.999999f)
                {
                    var axis = Vector3.Cross(Vector3.UnitX, from);
                    if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, from);
                    return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
                }
                var cross = Vector3.Cross(from, to);
                float s = MathF.Sqrt((1 + d) * 2);
                return Quaternion.Normalize(new Quaternion(cross / s, s * 0.5f));
            }

            // newParentNode is null only for a template top-level node being cloned straight into
            // the scene - treated as sitting at world origin with no rotation/scale of its own,
            // the same way any other scene-root node would. There is deliberately no synthetic
            // wrapper node created around this clone: an earlier version added one of its own
            // ("Armature") on top of the template's own top-level node (also typically named
            // "Armature"), producing a double-wrapped Armature/Armature chain that looked harmless
            // but made SharpGLTF.Scenes.NodeBuilder.IsValidArmature reject the whole skeleton the
            // moment the model was later merged/processed - confirmed directly: an isolated
            // single-wrapper chain passes IsValidArmature, the same chain with one extra wrapper
            // level does not. Cloning the template's own top-level structure as-is, with nothing
            // added on top, is both the fix and the more faithful copy of the reference rig anyway.
            Node CloneJointTree(Node templateNode, Node? newParentNode)
            {
                var scaledWorldPos = ScaledWorldPos(templateNode);

                // Converting a world-space target position into the new node's LOCAL translation
                // has to divide out the parent's world SCALE, not just rotate by its inverse -
                // several of these reference rigs (Mixamo-style exports especially) carry a
                // ~0.01 scale on the armature root for a cm-to-m unit conversion. Ignoring that
                // scale here previously divided every joint's offset from its parent by ~100,
                // which is what collapsed the whole cloned skeleton down near the root instead of
                // spreading it out across the model.
                var parentWorldScale = Vector3.One;
                var parentWorldRot = Quaternion.Identity;
                var parentWorldPos = Vector3.Zero;
                if (newParentNode != null)
                    Matrix4x4.Decompose(newParentNode.WorldMatrix, out parentWorldScale, out parentWorldRot, out parentWorldPos);
                var localDelta = Vector3.Transform(scaledWorldPos - parentWorldPos, Quaternion.Inverse(parentWorldRot));
                var localTranslation = new Vector3(
                    MathF.Abs(parentWorldScale.X) > 1e-6f ? localDelta.X / parentWorldScale.X : localDelta.X,
                    MathF.Abs(parentWorldScale.Y) > 1e-6f ? localDelta.Y / parentWorldScale.Y : localDelta.Y,
                    MathF.Abs(parentWorldScale.Z) > 1e-6f ? localDelta.Z / parentWorldScale.Z : localDelta.Z);

                Matrix4x4.Decompose(templateNode.LocalMatrix, out var localScale, out var templateLocalRotation, out _);

                // A joint's rotation was previously just copied straight from the template, which
                // is fine for POSITION (every child's translation is independently solved to land
                // exactly on its own scaled target position, further down) but leaves the child's
                // local translation pointing off at whatever odd angle the template's own bone
                // axis happened to be, rather than straight along whichever axis the rest of this
                // rig's convention (and this app's own animation/IK code) expects a bone to point
                // down. Nothing looks wrong at rest - the mesh isn't touched, and posing math still
                // lands each joint in the right place - but the moment anything rotates a joint
                // (an animation channel, this app's arm IK, or just a manual tweak in Fix Joint
                // Orientation), it bends around an axis that no longer matches the limb's actual
                // direction, which reads as the limb twisting. Re-deriving the rotation from an
                // "aim" at the joint's own primary child - in the TARGET's scaled positions, not
                // the template's - keeps that bone-to-child offset pointing the same direction it
                // always has, while still preserving whatever roll/twist the template's own
                // convention had around that axis (only the aim axis itself is corrected, via the
                // shortest rotation between the template's bone direction and the target's).
                var primaryChild = ChoosePrimaryChild(templateNode);
                Quaternion localRotation;
                if (primaryChild != null)
                {
                    Matrix4x4.Decompose(templateNode.WorldMatrix, out _, out var templateWorldRot, out var templateWorldPos);
                    Matrix4x4.Decompose(primaryChild.WorldMatrix, out _, out _, out var templateChildWorldPos);
                    var templateBoneDir = Vector3.Normalize(templateChildWorldPos - templateWorldPos);
                    var targetBoneDir = Vector3.Normalize(ScaledWorldPos(primaryChild) - scaledWorldPos);

                    var alignDelta = RotationBetween(templateBoneDir, targetBoneDir);
                    var desiredWorldRot = Quaternion.Normalize(Quaternion.Multiply(alignDelta, templateWorldRot));
                    localRotation = Quaternion.Normalize(Quaternion.Multiply(Quaternion.Inverse(parentWorldRot), desiredWorldRot));
                }
                else
                {
                    // Leaf joint (hand, toe, head tip) - no child to aim at, so there's nothing
                    // better to fall back on than the template's own local rotation.
                    localRotation = templateLocalRotation;
                }

                var newNode = (newParentNode != null ? newParentNode.CreateNode(templateNode.Name) : targetScene.CreateNode(templateNode.Name))
                    .WithLocalScale(localScale)
                    .WithLocalRotation(localRotation)
                    .WithLocalTranslation(localTranslation);

                if (templateJoints.Contains(templateNode)) newJoints.Add(newNode);

                foreach (var child in templateNode.VisualChildren.Where(HasJointDescendant))
                    CloneJointTree(child, newNode);

                return newNode;
            }

            foreach (var topLevel in template.DefaultScene.VisualChildren.Where(HasJointDescendant))
                CloneJointTree(topLevel, null);

            if (newJoints.Count == 0)
            {
                MessageBox.Show(this, "Couldn't find any joints to copy from that model.", "Rigging Editor",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // One shared skin across every mesh-bearing node - true for every rig this app deals
            // with, where all mesh nodes sit at the same (usually identity) world transform.
            var skin = _model.CreateSkin("Skin");
            skin.BindJoints(targetMeshNodes[0].WorldMatrix, newJoints.ToArray());
            foreach (var meshNode in targetMeshNodes)
                meshNode.Skin = skin;

            // Line-of-sight testing every vertex against its candidate bones runs to ~100k ray
            // casts on a character mesh - a few seconds, with no intermediate UI to update.
            Cursor = Cursors.WaitCursor;
            try
            {
                AssignNearestJointWeights(targetMeshNodes, newJoints, (int)_numWeightBlend.Value);
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            PopulateBoneList();
            RefreshRiggedState();
            _lblStatus.Text = $"Added {newJoints.Count} joint(s) from '{Path.GetFileName(entry.Path)}', scaled to this model."
                + (renamedOrphans > 0 ? $" (renamed {renamedOrphans} leftover node(s) from an earlier rig attempt out of the way.)" : "");
            ReloadPreview();
        }

        // Bind-pose bounding box of every mesh in `model`, in world space - used to work out how
        // much to scale a reference rig by to match this specific model's own proportions.
        private static (Vector3 Min, Vector3 Max)? ComputeMeshBounds(ModelRoot model)
        {
            Vector3? min = null, max = null;
            foreach (var node in model.LogicalNodes)
            {
                if (node.Mesh == null) continue;
                // A SKINNED mesh node's own transform is ignored per the glTF spec - the vertices
                // are placed entirely by the joint matrices, and the skin's inverse bind matrices
                // have already absorbed whatever that node transform was, so raw POSITION is
                // already in the skeleton's own space. Applying the node matrix anyway would be
                // double-counting it: a real reference rig in this app's own library carries a
                // 0.01 scale on its mesh node (a cm-to-m conversion the IBMs cancel out), and
                // measuring it "in world space" that way reported the model as 100x smaller than
                // it actually is - which would then be used as the template's height/width and
                // scale the copied skeleton wildly wrong.
                var world = node.Skin != null ? Matrix4x4.Identity : node.WorldMatrix;
                foreach (var prim in node.Mesh.Primitives)
                {
                    if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                    foreach (var p in posAcc.AsVector3Array())
                    {
                        var wp = Vector3.Transform(p, world);
                        min = min.HasValue ? Vector3.Min(min.Value, wp) : wp;
                        max = max.HasValue ? Vector3.Max(max.Value, wp) : wp;
                    }
                }
            }
            return min.HasValue ? (min.Value, max!.Value) : null;
        }

        // Builds the starting skin: every vertex is first bound rigidly to whichever bone it sits
        // closest to, then those weights are SMOOTHED across the mesh surface so the boundary
        // between two bones becomes a gradual blend instead of a hard seam.
        //
        // That smoothing pass is what makes the difference between a rig that merely holds
        // together at rest and one that deforms plausibly. With purely rigid weights (every
        // vertex at weight 1.0 on a single bone - what this did before) nothing blends anywhere,
        // so a bending elbow tears the mesh into two rigid chunks that visibly separate and
        // crease at the joint. Comparing against a model rigged by a dedicated tool makes the
        // contrast measurable: that model averages ~1.8 bone influences per vertex with only 26%
        // of vertices fully rigid, and its blending is concentrated exactly where two bones meet
        // (deep inside a limb its vertices are ~1.0 rigid; where a vertex sits equidistant
        // between two bones the dominant weight drops to ~0.73), overwhelmingly between
        // parent/child joint pairs. A purely rigid skin is 100% rigid everywhere by construction,
        // which is why it reads as solid green in the Rigid Region editor's rigidity colouring
        // while a properly skinned model shows red/amber bands at every joint.
        //
        // Smoothing over the mesh's own TOPOLOGY (rather than by raw proximity) is what keeps the
        // blend anatomically sane: weight bleeds along connected surface, so a chest vertex never
        // picks up upper-arm influence just because the arm hangs geometrically close to it.
        // Interior-of-a-limb vertices, whose neighbours all share the same bone, are left exactly
        // 1.0 rigid - the averaging is a no-op there - so rigidity is preserved everywhere it
        // should be and spent only at the joints.
        //
        // "Closest bone" is measured against the SEGMENT from a joint to each of its children,
        // not just the joint's own point - a limb is a stretch of geometry between two joints,
        // not a single point, so measuring distance to joint POSITIONS only was consistently
        // misassigning vertices near the far end of any decently long bone (say, a thigh) to the
        // WRONG neighboring joint (the knee, and therefore the shin) just because that joint
        // happens to be the nearer of the two points, even though the vertex plainly belongs to
        // the thigh. A joint owns the segment leading to each of its children (rotating the hip
        // joint should carry the whole thigh with it, all the way down to the knee), so that's
        // the segment a vertex in that stretch is measured against and rigidly bound to. Leaf
        // joints (hands, toes, the head tip) have no child to form a segment with, so they get a
        // continuation segment of their own instead - see the fallback branch below.
        private static void AssignNearestJointWeights(List<Node> meshNodes, List<Node> joints, int smoothingIterations)
        {
            var jointWorldPositions = joints.Select(j => j.WorldMatrix.Translation).ToArray();
            var jointIndexByNode = joints.Select((j, i) => (j, i)).ToDictionary(t => t.j, t => t.i);

            var segments = new List<(int JointIndex, Vector3 A, Vector3 B)>();
            for (int i = 0; i < joints.Count; i++)
            {
                bool hasChildSegment = false;
                foreach (var child in joints[i].VisualChildren)
                {
                    if (!jointIndexByNode.TryGetValue(child, out var childIndex)) continue;
                    segments.Add((i, jointWorldPositions[i], jointWorldPositions[childIndex]));
                    hasChildSegment = true;
                }
                if (!hasChildSegment)
                {
                    // Leaf joint (hand, toe, head tip) - a zero-length "segment" (just its own
                    // point) here would claim essentially nothing: the parent's own segment ends
                    // exactly at this same position and, at worst, ties it on every comparison,
                    // so the leaf's entire extremity mesh (the actual hand, the toes, the top of
                    // the head) would end up rigidly bound to the PARENT bone instead - confirmed
                    // directly, this is what a zero-length segment here produced (every leaf
                    // joint got 0 vertices). Extending a "continuation" segment past the joint,
                    // the same length and direction as the bone that fed into it, gives the leaf
                    // a fair claim over the mesh beyond it instead.
                    var parent = joints[i].VisualParent;
                    var dir = Vector3.UnitY;
                    var len = 0.05f;
                    if (parent != null && jointIndexByNode.TryGetValue(parent, out var parentIndex))
                    {
                        var incoming = jointWorldPositions[i] - jointWorldPositions[parentIndex];
                        if (incoming.LengthSquared() > 1e-8f)
                        {
                            len = incoming.Length();
                            dir = Vector3.Normalize(incoming);
                        }
                    }
                    segments.Add((i, jointWorldPositions[i], jointWorldPositions[i] + dir * len));
                }
            }

            foreach (var meshNode in meshNodes)
            {
                // The mesh isn't skinned yet at this point (that's what this method is building),
                // so unlike ComputeMeshBounds above, the node's own transform IS still meaningful
                // here and has to be applied to compare against joint world positions. It matches
                // what BindJoints was handed as the mesh's bind transform, so the two agree.
                var world = meshNode.WorldMatrix;

                // Every primitive of this mesh is solved together in one flat index space: a mesh
                // is commonly split into several primitives (one per material), and smoothing each
                // in isolation would leave a hard weight discontinuity along every material
                // boundary.
                var prims = meshNode.Mesh.Primitives
                    .Where(p => p.VertexAccessors.ContainsKey("POSITION"))
                    .Select(p => (Prim: p, Positions: p.VertexAccessors["POSITION"].AsVector3Array()))
                    .ToList();
                if (prims.Count == 0) continue;

                var offsets = new int[prims.Count];
                int totalVerts = 0;
                for (int i = 0; i < prims.Count; i++) { offsets[i] = totalVerts; totalVerts += prims[i].Positions.Count; }
                if (totalVerts == 0) continue;

                var worldPositions = new Vector3[totalVerts];
                for (int i = 0; i < prims.Count; i++)
                    for (int v = 0; v < prims[i].Positions.Count; v++)
                        worldPositions[offsets[i] + v] = Vector3.Transform(prims[i].Positions[v], world);

                var primList = prims.Select(p => p.Prim).ToList();
                var topology = MeshTopology.Build(primList, offsets, totalVerts, worldPositions);

                var rigidJoint = AssignBonesAcrossSurface(primList, offsets, totalVerts, worldPositions, segments, joints.Count, topology);

                var weights = SmoothWeightsOverSurface(totalVerts, rigidJoint, smoothingIterations, topology);

                for (int i = 0; i < prims.Count; i++)
                {
                    var (prim, positions) = prims[i];
                    var jointsAcc = CreateVertexAccessor(meshNode.LogicalParent, "JOINTS_0", positions.Count);
                    var weightsAcc = CreateVertexAccessor(meshNode.LogicalParent, "WEIGHTS_0", positions.Count);
                    var jointsArr = jointsAcc.AsVector4Array();
                    var weightsArr = weightsAcc.AsVector4Array();

                    // Reused across every vertex rather than allocated per iteration - a
                    // stackalloc in this loop is never reclaimed until the whole method returns,
                    // so on a dense mesh it would pile up into a stack overflow.
                    var quantized = new int[4];

                    for (int v = 0; v < positions.Count; v++)
                    {
                        // glTF allows at most 4 influences per vertex in one JOINTS_0/WEIGHTS_0
                        // set, so keep the 4 strongest and renormalise - dropping the tail of a
                        // smoothed distribution without renormalising would leave total weight
                        // under 1 and shrink the vertex toward the origin when posed.
                        var top = weights[v + offsets[i]]
                            .OrderByDescending(kv => kv.Value)
                            .Take(4)
                            .ToArray();
                        float sum = top.Sum(kv => kv.Value);
                        if (sum <= 1e-8f) { top = new[] { new KeyValuePair<int, float>(rigidJoint[v + offsets[i]], 1f) }; sum = 1f; }

                        // WEIGHTS_0 is written as normalized UNSIGNED_BYTE (see
                        // CreateVertexAccessor), so each weight lands on a 1/255 step. Writing
                        // exact fractions and letting them round independently can leave the four
                        // bytes summing to 254 or 256 instead of 255, which the spec requires to
                        // be 1.0 and validators flag - and which shows up in a renderer as a
                        // vertex very slightly shrinking toward or drifting from the origin.
                        // Quantising deliberately here, then giving the rounding remainder to the
                        // largest weight, keeps the stored bytes summing to exactly 255.
                        int quantizedTotal = 0, largestIndex = 0;
                        Array.Clear(quantized);
                        for (int k = 0; k < top.Length; k++)
                        {
                            quantized[k] = (int)MathF.Floor(top[k].Value / sum * 255f);
                            quantizedTotal += quantized[k];
                            if (top[k].Value > top[largestIndex].Value) largestIndex = k;
                        }
                        quantized[largestIndex] += 255 - quantizedTotal;

                        var jv = Vector4.Zero;
                        var wv = Vector4.Zero;
                        for (int k = 0; k < top.Length; k++)
                        {
                            float normalized = quantized[k] / 255f;
                            switch (k)
                            {
                                case 0: jv.X = top[k].Key; wv.X = normalized; break;
                                case 1: jv.Y = top[k].Key; wv.Y = normalized; break;
                                case 2: jv.Z = top[k].Key; wv.Z = normalized; break;
                                case 3: jv.W = top[k].Key; wv.W = normalized; break;
                            }
                        }
                        jointsArr[v] = jv;
                        weightsArr[v] = wv;
                    }

                    prim.SetVertexAccessor("JOINTS_0", jointsAcc);
                    prim.SetVertexAccessor("WEIGHTS_0", weightsAcc);
                }
            }
        }

        // Diffuses the initial one-bone-per-vertex assignment across the mesh surface, so a
        // vertex near where two bones meet ends up sharing weight between them while a vertex
        // deep inside a single bone's territory keeps its full 1.0. Each iteration mixes a
        // vertex's weights with the average of its edge-connected neighbours' - so the blend
        // widens by roughly one edge ring per iteration, and `iterations` is effectively the
        // blend width in rings.
        private static List<Dictionary<int, float>> SmoothWeightsOverSurface(
            int totalVerts, int[] rigidJoint, int iterations, MeshTopology topology)
        {
            var weights = new List<Dictionary<int, float>>(totalVerts);
            for (int gv = 0; gv < totalVerts; gv++)
                weights.Add(new Dictionary<int, float> { [rigidJoint[gv]] = 1f });

            if (iterations <= 0) return weights;

            int slotCount = topology.SlotCount;
            var current = new Dictionary<int, float>[slotCount];
            for (int gv = 0; gv < totalVerts; gv++)
                current[topology.SlotOf[gv]] = weights[gv];

            const float lambda = 0.5f;
            for (int iter = 0; iter < iterations; iter++)
            {
                var next = new Dictionary<int, float>[slotCount];
                for (int slot = 0; slot < slotCount; slot++)
                {
                    var neighbors = topology.Neighbors[slot];
                    if (neighbors.Count == 0) { next[slot] = current[slot]; continue; }

                    var blended = new Dictionary<int, float>();
                    foreach (var (joint, w) in current[slot])
                        blended[joint] = w * (1f - lambda);

                    float perNeighbor = lambda / neighbors.Count;
                    foreach (var nb in neighbors)
                        foreach (var (joint, w) in current[nb])
                            blended[joint] = blended.GetValueOrDefault(joint) + w * perNeighbor;

                    // Pruning the long tail each pass keeps these dictionaries small (and the
                    // whole pass fast) without changing the result meaningfully - anything this
                    // faint is far below the 4 influences that survive to the final buffer anyway.
                    if (blended.Count > 6)
                        blended = blended.OrderByDescending(kv => kv.Value).Take(6).ToDictionary(kv => kv.Key, kv => kv.Value);

                    next[slot] = blended;
                }
                current = next;
            }

            for (int gv = 0; gv < totalVerts; gv++)
                weights[gv] = current[topology.SlotOf[gv]];

            return weights;
        }

        // The welded vertex graph of one mesh: which distinct surface point each raw vertex maps
        // to, and which of those points are joined by a triangle edge. Both the bone assignment
        // and the weight smoothing walk this same graph, so it is built once per mesh.
        private sealed class MeshTopology
        {
            public int[] SlotOf = Array.Empty<int>();          // raw vertex index -> welded slot
            public int[] RawOf = Array.Empty<int>();           // welded slot -> a representative raw vertex
            public List<int>[] Neighbors = Array.Empty<List<int>>();
            public int SlotCount => RawOf.Length;

            // Vertices that share a position are the same physical point split apart by a UV (or
            // material) seam. Welding them into one slot is what lets both passes cross those
            // seams: otherwise each side is solved independently, the two copies disagree, and
            // the seam visibly tears open the moment the joint bends. Same rationale (and the
            // same 4-decimal tolerance) as RigidRegionEditor's own seam bridging.
            public static MeshTopology Build(
                IReadOnlyList<MeshPrimitive> prims, int[] offsets, int totalVerts, Vector3[] worldPositions)
            {
                var slotOf = new int[totalVerts];
                var rawOf = new List<int>();
                var byPosition = new Dictionary<(int, int, int), int>();
                for (int gv = 0; gv < totalVerts; gv++)
                {
                    var p = worldPositions[gv];
                    var key = ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
                    if (byPosition.TryGetValue(key, out var slot)) slotOf[gv] = slot;
                    else { slot = rawOf.Count; byPosition[key] = slot; rawOf.Add(gv); slotOf[gv] = slot; }
                }

                var neighbors = new List<int>[rawOf.Count];
                for (int i = 0; i < neighbors.Length; i++) neighbors[i] = new List<int>();
                void Link(int a, int b)
                {
                    if (a == b) return;
                    if (!neighbors[a].Contains(b)) neighbors[a].Add(b);
                    if (!neighbors[b].Contains(a)) neighbors[b].Add(a);
                }
                for (int i = 0; i < prims.Count; i++)
                    foreach (var (a, b, c) in prims[i].GetTriangleIndices())
                    {
                        int sa = slotOf[offsets[i] + a], sb = slotOf[offsets[i] + b], sc = slotOf[offsets[i] + c];
                        Link(sa, sb); Link(sb, sc); Link(sa, sc);
                    }

                return new MeshTopology { SlotOf = slotOf, RawOf = rawOf.ToArray(), Neighbors = neighbors };
            }
        }

        // Decides which single bone each vertex belongs to, measuring distance the way the model
        // is actually shaped rather than as the crow flies.
        //
        // Plain nearest-bone-by-straight-line quietly binds geometry ACROSS AIR GAPS: on a
        // character whose head sits close to the shoulders, hair hanging behind the head and the
        // sides of the head are physically nearer to the shoulder bone than to the head bone, so
        // they get bound to the shoulder and then swing with the arm. Measured on a real model
        // here, a hair vertex sat 0.168 from the shoulder bone versus 0.183 from the head bone -
        // the shoulder won by a hair's breadth, through empty space.
        //
        // The fix is to let a bone claim a vertex directly ONLY when it can actually "see" it -
        // when the straight line between them stays inside the model. Everything else has to be
        // reached the long way, travelling along the mesh surface. So the cost of binding vertex
        // v to bone b is the shortest path in a graph where a bone may jump straight to any
        // vertex with clear line of sight (cost = that straight distance), and otherwise the path
        // has to crawl edge by edge over the surface. Hair reaches the head bone cheaply because
        // it is attached to the scalp; reaching the shoulder means travelling all the way down the
        // neck, so the head wins - which is the anatomically right answer.
        //
        // Deliberately conservative: on the model this was validated against only ~4% of vertices
        // change binding versus the straight-line answer, and limbs, hands and feet are untouched.
        // A vertex that somehow cannot be reached at all falls back to plain nearest-bone.
        private static int[] AssignBonesAcrossSurface(
            IReadOnlyList<MeshPrimitive> prims, int[] offsets, int totalVerts, Vector3[] worldPositions,
            List<(int JointIndex, Vector3 A, Vector3 B)> segments, int jointCount, MeshTopology topology)
        {
            // Nearest point on each bone, per vertex - the target a line-of-sight test aims at.
            var nearestOnBone = new Vector3[totalVerts][];
            var distToBone = new float[totalVerts][];
            var euclidNearest = new int[totalVerts];
            for (int gv = 0; gv < totalVerts; gv++)
            {
                var d = new float[jointCount];
                var c = new Vector3[jointCount];
                for (int j = 0; j < jointCount; j++) d[j] = float.MaxValue;
                foreach (var (jointIndex, a, b) in segments)
                {
                    var closest = ClosestPointOnSegment(worldPositions[gv], a, b);
                    float dist = Vector3.Distance(worldPositions[gv], closest);
                    if (dist < d[jointIndex]) { d[jointIndex] = dist; c[jointIndex] = closest; }
                }
                distToBone[gv] = d;
                nearestOnBone[gv] = c;
                int best = 0;
                for (int j = 1; j < jointCount; j++) if (d[j] < d[best]) best = j;
                euclidNearest[gv] = best;
            }

            var occluders = TriangleGrid.Build(prims, offsets, worldPositions);

            int slotCount = topology.SlotCount;
            var cost = new float[slotCount];
            var bone = new int[slotCount];
            for (int s = 0; s < slotCount; s++) { cost[s] = float.MaxValue; bone[s] = -1; }

            // Only the few nearest bones are worth a line-of-sight test; anything further away
            // could never win the shortest path anyway, and each test costs a ray cast.
            const int Candidates = 6;
            var queue = new PriorityQueue<int, float>();
            for (int s = 0; s < slotCount; s++)
            {
                int gv = topology.RawOf[s];
                var d = distToBone[gv];
                foreach (var j in NearestBones(d, Candidates))
                {
                    if (!occluders.IsClearLineOfSight(worldPositions[gv], nearestOnBone[gv][j])) continue;
                    if (d[j] < cost[s]) { cost[s] = d[j]; bone[s] = j; }
                }
                if (bone[s] >= 0) queue.Enqueue(s, cost[s]);
            }

            while (queue.TryDequeue(out int current, out float currentCost))
            {
                if (currentCost > cost[current] + 1e-9f) continue;
                var from = worldPositions[topology.RawOf[current]];
                foreach (var nb in topology.Neighbors[current])
                {
                    float step = currentCost + Vector3.Distance(from, worldPositions[topology.RawOf[nb]]);
                    if (step < cost[nb]) { cost[nb] = step; bone[nb] = bone[current]; queue.Enqueue(nb, step); }
                }
            }

            var result = new int[totalVerts];
            for (int gv = 0; gv < totalVerts; gv++)
            {
                int b = bone[topology.SlotOf[gv]];
                result[gv] = b >= 0 ? b : euclidNearest[gv];
            }
            return result;
        }

        private static IEnumerable<int> NearestBones(float[] distances, int count)
        {
            var indices = new int[distances.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (x, y) => distances[x].CompareTo(distances[y]));
            return indices.Take(count);
        }

        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float lenSq = ab.LengthSquared();
            float t = lenSq > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / lenSq, 0f, 1f) : 0f;
            return a + ab * t;
        }

        // A uniform grid of the mesh's triangles, used purely to answer "does the straight line
        // between these two points stay inside the model, or does it break out through the
        // surface?" for AssignBonesAcrossSurface. A brute-force test against every triangle would
        // be far too slow at the ~100k line-of-sight queries a character mesh needs.
        private sealed class TriangleGrid
        {
            private const int Resolution = 48;
            private Vector3 _min, _cell;
            private Vector3[] _positions = Array.Empty<Vector3>();
            private (int A, int B, int C)[] _triangles = Array.Empty<(int, int, int)>();
            private Dictionary<(int, int, int), List<int>> _cells = new();
            private int[] _visitStamp = Array.Empty<int>();
            private int _stamp;

            public static TriangleGrid Build(
                IReadOnlyList<MeshPrimitive> prims, int[] offsets, Vector3[] worldPositions)
            {
                var triangles = new List<(int, int, int)>();
                for (int i = 0; i < prims.Count; i++)
                    foreach (var (a, b, c) in prims[i].GetTriangleIndices())
                        triangles.Add((offsets[i] + a, offsets[i] + b, offsets[i] + c));

                var min = worldPositions[0];
                var max = worldPositions[0];
                foreach (var p in worldPositions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
                var extent = max - min;

                var grid = new TriangleGrid
                {
                    _min = min,
                    _cell = new Vector3(
                        MathF.Max(extent.X, 1e-6f) / Resolution,
                        MathF.Max(extent.Y, 1e-6f) / Resolution,
                        MathF.Max(extent.Z, 1e-6f) / Resolution),
                    _positions = worldPositions,
                    _triangles = triangles.ToArray(),
                    _visitStamp = new int[triangles.Count],
                };

                for (int t = 0; t < grid._triangles.Length; t++)
                {
                    var (ia, ib, ic) = grid._triangles[t];
                    var lo = grid.CellOf(Vector3.Min(worldPositions[ia], Vector3.Min(worldPositions[ib], worldPositions[ic])));
                    var hi = grid.CellOf(Vector3.Max(worldPositions[ia], Vector3.Max(worldPositions[ib], worldPositions[ic])));
                    for (int x = lo.X; x <= hi.X; x++)
                        for (int y = lo.Y; y <= hi.Y; y++)
                            for (int z = lo.Z; z <= hi.Z; z++)
                            {
                                var key = (x, y, z);
                                if (!grid._cells.TryGetValue(key, out var list)) grid._cells[key] = list = new List<int>();
                                list.Add(t);
                            }
                }
                return grid;
            }

            private (int X, int Y, int Z) CellOf(Vector3 p) => (
                Math.Clamp((int)((p.X - _min.X) / _cell.X), 0, Resolution - 1),
                Math.Clamp((int)((p.Y - _min.Y) / _cell.Y), 0, Resolution - 1),
                Math.Clamp((int)((p.Z - _min.Z) / _cell.Z), 0, Resolution - 1));

            public bool IsClearLineOfSight(Vector3 from, Vector3 to)
            {
                _stamp++;
                var lo = CellOf(Vector3.Min(from, to));
                var hi = CellOf(Vector3.Max(from, to));
                for (int x = lo.X; x <= hi.X; x++)
                    for (int y = lo.Y; y <= hi.Y; y++)
                        for (int z = lo.Z; z <= hi.Z; z++)
                        {
                            if (!_cells.TryGetValue((x, y, z), out var list)) continue;
                            foreach (var t in list)
                            {
                                // A triangle can sit in many cells; only test it once per query.
                                if (_visitStamp[t] == _stamp) continue;
                                _visitStamp[t] = _stamp;
                                var (ia, ib, ic) = _triangles[t];
                                if (SegmentHitsTriangle(from, to, _positions[ia], _positions[ib], _positions[ic]))
                                    return false;
                            }
                        }
                return true;
            }

            // Möller-Trumbore. The hit window deliberately excludes both endpoints: `from` is
            // itself a mesh vertex, so its own triangles all intersect at t=0 and would otherwise
            // report every vertex as blocked from everything.
            private static bool SegmentHitsTriangle(Vector3 p, Vector3 q, Vector3 a, Vector3 b, Vector3 c)
            {
                var dir = q - p;
                var e1 = b - a;
                var e2 = c - a;
                var h = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-14f) return false;
                float inv = 1f / det;
                var s = p - a;
                float u = inv * Vector3.Dot(s, h);
                if (u < 0f || u > 1f) return false;
                var qv = Vector3.Cross(s, e1);
                float v = inv * Vector3.Dot(dir, qv);
                if (v < 0f || u + v > 1f) return false;
                float t = inv * Vector3.Dot(e2, qv);
                return t > 1e-4f && t < 1f - 1e-5f;
            }
        }

        // Toolkit.WithVertexAccessor(primitive, name, IReadOnlyList<Vector4>) - the obvious way to
        // write JOINTS_0/WEIGHTS_0 - always encodes as plain 32-bit FLOAT regardless of attribute
        // name. That's spec-illegal for JOINTS_0 (glTF requires an integer component type there,
        // UNSIGNED_BYTE or UNSIGNED_SHORT) and is exactly what made a model rigged this way fail to
        // open elsewhere afterward, even though SharpGLTF's own (lenient) reader tolerated it fine.
        // Building the accessor by hand from MemoryAccessInfo's own recommended per-attribute
        // encoding (UNSIGNED_BYTE, normalized for WEIGHTS_0) is what a spec-correct writer actually
        // uses for these two attributes.
        private static Accessor CreateVertexAccessor(ModelRoot model, string attributeName, int count)
        {
            var format = SharpGLTF.Memory.MemoryAccessInfo.CreateDefaultElement(attributeName).Format;
            var info = new SharpGLTF.Memory.MemoryAccessInfo(attributeName, 0, count, 0, format);
            var buffer = new byte[count * format.ByteSize];
            var memAccessor = new SharpGLTF.Memory.MemoryAccessor(new ArraySegment<byte>(buffer), info);
            return SharpGLTF.Schema2.Toolkit.CreateVertexAccessor(model, memAccessor);
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private void PushShowJoints()
        {
            if (!_viewerReady) return;
            _webView.CoreWebView2.ExecuteScriptAsync($"setJointMarkersVisible({(_chkShowJoints.Checked ? "true" : "false")});");
        }

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

            if (message?.Action != "jointSelected" || message.Bone == null || IsDisposed) return;
            if (_boneDropdown.Items.Contains(message.Bone))
                _boneDropdown.SelectedItem = message.Bone;
        }

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public string? Bone { get; set; }
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-
            // forget startup may still be mid-await, so both the await itself and everything
            // after it have to tolerate the control having gone away underneath them.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (!_viewerReady) return;
                PushShowJoints();
                PushPreviewPositions();
            };
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            NavigateToModel();
        }

        // Re-saves the model to its preview file and re-navigates the WebView to it - used both
        // for the initial load and to refresh the view once Bake Rig Changes has actually
        // mutated the model, so the user sees the rebaked (now undistorted-at-rest) result
        // without having to leave and re-enter this mode.
        private void NavigateToModel()
        {
            _glbVersion++;
            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_rigging_preview.glb");
            _model.SaveGLB(previewPath);
            var previewFileName = Path.GetFileName(previewPath);

            var jointNamesJson = JsonSerializer.Serialize(_jointNames);
            var jointParents = new Dictionary<string, string>();
            foreach (var name in _jointNames)
            {
                if (!_nodeByName.TryGetValue(name, out var node)) continue;
                var ancestor = node.VisualParent;
                while (ancestor != null && (string.IsNullOrEmpty(ancestor.Name) || !_jointNames.Contains(ancestor.Name)))
                    ancestor = ancestor.VisualParent;
                if (ancestor?.Name != null) jointParents[name] = ancestor.Name;
            }
            var jointParentsJson = JsonSerializer.Serialize(jointParents);

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
                        var jointNames = " + jointNamesJson + @";
                        var jointParents = " + jointParentsJson + @";
                        var jointMarkerMeshes = [];
                        var jointMarkersVisible = false;
                        var selectedJointName = null;
                        // boneName -> [x,y,z] not-yet-baked preview world position, pushed from
                        // the .NET side every time a slider/numeric field changes. Markers use
                        // this instead of the bone's own live position so the mesh itself is
                        // never touched pre-bake - it stays a clean, undistorted reference to
                        // line the joint up against.
                        var previewPositions = {};
                        var skeletonLines = null;

                        window.setJointPreviewPositions = function (json) {
                            previewPositions = json;
                        };

                        window.setAnimationByName = function (name) {
                            if (!mixer) return;
                            mixer.stopAllAction();
                            if (!name) return;
                            var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                            if (clip) mixer.clipAction(clip).play();
                        };

                        window.setPaused = function (value) { paused = value; };

                        function applyJointMarkersVisibility() {
                            jointMarkerMeshes.forEach(function (m) { m.visible = jointMarkersVisible; });
                            if (skeletonLines) skeletonLines.visible = jointMarkersVisible;
                        }
                        function applySelectedJointHighlight() {
                            jointMarkerMeshes.forEach(function (m) {
                                m.material.color.set(m.userData.boneName === selectedJointName ? 0xffee00 : 0x00e5ff);
                            });
                        }
                        window.setJointMarkersVisible = function (value) {
                            jointMarkersVisible = value;
                            applyJointMarkersVisibility();
                        };
                        window.setSelectedJoint = function (name) {
                            selectedJointName = name;
                            applySelectedJointHighlight();
                        };

                        var loader = new THREE.GLTFLoader();
                        loader.load('https://appassets.local/" + previewFileName + @"?v=" + _glbVersion + @"', function (gltf) {
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

                                var groundSize = (Math.max(size.x, size.z) || 1) * 4;
                                var groundY = box.min.y;
                                var groundQuad = new THREE.Mesh(
                                    new THREE.PlaneGeometry(groundSize, groundSize),
                                    new THREE.MeshStandardMaterial({ color: 0x30343a, side: THREE.DoubleSide, transparent: true, opacity: 0.85, metalness: 0, roughness: 1 })
                                );
                                groundQuad.rotation.x = -Math.PI / 2;
                                groundQuad.position.set(center.x, groundY, center.z);
                                scene.add(groundQuad);

                                var groundGrid = new THREE.GridHelper(groundSize, 20, 0x6a6f76, 0x454951);
                                groundGrid.position.set(center.x, groundY + maxDim * 0.0005, center.z);
                                scene.add(groundGrid);

                                var jointGeom = new THREE.SphereGeometry(1, 12, 8);
                                var jointRadius = maxDim * 0.01;
                                jointNames.forEach(function (name) {
                                    var bone = bonesByName[name];
                                    if (!bone) return;
                                    var marker = new THREE.Mesh(jointGeom, new THREE.MeshBasicMaterial({ color: 0x00e5ff, depthTest: false }));
                                    marker.scale.setScalar(jointRadius);
                                    marker.renderOrder = 999;
                                    marker.visible = jointMarkersVisible;
                                    marker.userData.boneName = name;
                                    marker.userData.bone = bone;
                                    scene.add(marker);
                                    jointMarkerMeshes.push(marker);
                                });

                                // One line segment per joint-to-parent-joint pair, updated every
                                // frame from the markers' own (possibly preview-overridden)
                                // positions - this is what makes a moved joint visibly drag its
                                // connected bones along in the preview.
                                var maxSegments = jointMarkerMeshes.length;
                                var lineGeom = new THREE.BufferGeometry();
                                var linePositions = new Float32Array(maxSegments * 2 * 3);
                                lineGeom.setAttribute('position', new THREE.BufferAttribute(linePositions, 3));
                                skeletonLines = new THREE.LineSegments(lineGeom, new THREE.LineBasicMaterial({ color: 0x8fd9ff, depthTest: false, transparent: true, opacity: 0.85 }));
                                skeletonLines.renderOrder = 998;
                                skeletonLines.visible = jointMarkersVisible;
                                scene.add(skeletonLines);

                                applyJointMarkersVisibility();
                                applySelectedJointHighlight();

                                controls.target.copy(center);
                                camera.position.copy(center).add(new THREE.Vector3(maxDim, maxDim * 0.6, maxDim));
                                camera.near = maxDim / 1000;
                                camera.far = maxDim * 100;
                                camera.updateProjectionMatrix();
                                controls.update();

                                window._clips = gltf.animations || [];
                                if (window._clips.length > 0) mixer = new THREE.AnimationMixer(gltf.scene);
                            } catch (innerErr) {
                                showError('Error setting up loaded model: ' + innerErr.message);
                            }
                        }, undefined, function (error) {
                            showError('Failed to load preview.glb: ' + (error && error.message ? error.message : error));
                        });

                        window.addEventListener('resize', function () {
                            camera.aspect = window.innerWidth / window.innerHeight;
                            camera.updateProjectionMatrix();
                            renderer.setSize(window.innerWidth, window.innerHeight);
                        });

                        var raycaster = new THREE.Raycaster();
                        var pickVec = new THREE.Vector2();
                        canvas.addEventListener('click', function (event) {
                            if (jointMarkerMeshes.length === 0) return;
                            var rect = canvas.getBoundingClientRect();
                            pickVec.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
                            pickVec.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
                            raycaster.setFromCamera(pickVec, camera);
                            var hits = raycaster.intersectObjects(jointMarkerMeshes, false);
                            if (hits.length > 0 && window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({ action: 'jointSelected', bone: hits[0].object.userData.boneName }));
                            }
                        });

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            if (mixer && !paused) mixer.update(delta);

                            for (var mi = 0; mi < jointMarkerMeshes.length; mi++) {
                                var marker = jointMarkerMeshes[mi];
                                var override = previewPositions[marker.userData.boneName];
                                if (override) marker.position.set(override[0], override[1], override[2]);
                                else marker.userData.bone.getWorldPosition(marker.position);
                            }

                            if (skeletonLines && jointMarkersVisible) {
                                var posAttr = skeletonLines.geometry.attributes.position;
                                var segIndex = 0;
                                for (var mj = 0; mj < jointMarkerMeshes.length; mj++) {
                                    var childMarker = jointMarkerMeshes[mj];
                                    var parentName = jointParents[childMarker.userData.boneName];
                                    if (!parentName) continue;
                                    var parentMarker = jointMarkerMeshes.filter(function (m) { return m.userData.boneName === parentName; })[0];
                                    if (!parentMarker) continue;
                                    posAttr.setXYZ(segIndex * 2, childMarker.position.x, childMarker.position.y, childMarker.position.z);
                                    posAttr.setXYZ(segIndex * 2 + 1, parentMarker.position.x, parentMarker.position.y, parentMarker.position.z);
                                    segIndex++;
                                }
                                skeletonLines.geometry.setDrawRange(0, segIndex * 2);
                                posAttr.needsUpdate = true;
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

        private void ReloadPreview()
        {
            _viewerReady = false;
            NavigateToModel();
        }
    }
}
