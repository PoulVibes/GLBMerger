using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
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

namespace GlbMerger
{
    // Prevents a painted patch of a skinned mesh (typically a jersey number baked into the
    // albedo texture) from visibly shearing under animation. GPU skinning blends bone matrices
    // per vertex, so wherever a triangle's three corners are weighted toward different bones by
    // different amounts, the triangle deforms non-uniformly as the skeleton moves - which reads
    // as the flat, painted texture on top of it warping. Painting a region here and pinning it to
    // a single bone forces every vertex in that patch through the SAME rigid transform
    // (rotate/translate only, no differential blending), so the patch moves as one undistorted
    // rigid body and whatever is painted on it holds its shape. A feather ring at the boundary
    // blends back to the mesh's original weights gradually instead of tearing at a hard seam.
    //
    // The paint tool itself (brush, triangle selection, posed-mesh hit-testing) is the same
    // approach TextureEditorEditor and GeometryOptimizerEditor use - see TextureEditorEditor's
    // paintAllInBrush for the fuller rationale on bounding-sphere overlap and facing-only. What's
    // new here is what Apply does with the selection: rather than restricting a 2D texture
    // operation to the painted triangles, the painted triangles' vertices have their
    // JOINTS_0/WEIGHTS_0 skin data rewritten directly, the same way ModelAdjusterEditor already
    // rewrites POSITION/NORMAL for its own vertex edits.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there), which owns the window.
    public class RigidRegionEditor : UserControl
    {
        private readonly ModelRoot _model;

        private ComboBox _boneDropdown = null!;
        private CheckBox _chkPaintMode = null!;
        private TrackBar _sliderBrush = null!;
        private Label _lblBrush = null!, _lblSelection = null!, _lblStatus = null!;
        private Button _btnClearSelection = null!, _btnApply = null!, _btnRevert = null!;
        private Button _btnRemoveRigidity = null!, _btnColorize = null!;
        private CheckBox _chkWireframe = null!;
        private NumericUpDown _numFeatherRings = null!;
        private ComboBox _animDropdown = null!;
        private CheckBox _chkPlaying = null!;
        private TrackBar _sliderScrub = null!;
        private WebView2 _webView = null!;

        // Snapshot of a primitive's original JOINTS_0/WEIGHTS_0, taken the first time Apply
        // touches it - keyed by the JOINTS_0 accessor since the two are always declared together
        // per primitive. Revert restores from here and is a single "back to pristine" action, not
        // a per-step undo - same contract as ModelAdjusterEditor's and TextureEditorEditor's own
        // Revert.
        private readonly Dictionary<Accessor, (Accessor WeightsAcc, Vector4[] OrigJoints, Vector4[] OrigWeights)> _originalSkin = new();

        private int _glbVersion;
        private string? _paintGlbPath;
        private string? _previewGlbPath;
        private string _currentPaintFileName = "";
        private bool _viewerReady;
        private bool _hasSkins;
        private bool _rigidityColors;

        // The Paint pane's hit-test copy is loaded once and kept - but the rigidity colouring is
        // computed from that copy's skin weights, which an Apply/Remove/Revert has just made out
        // of date. Nothing else over there reads them, so the re-read is deferred until the
        // colouring is actually on screen rather than paid for on every edit.
        private bool _paintGeometryStale;

        public RigidRegionEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            BuildUi();
            PopulateBones();
            PopulateAnimations();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

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

            flow.Controls.Add(new Label
            {
                Text = "Rigid Region (Prevent Skew)",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            flow.Controls.Add(HelpText(
                "Paint the area over a jersey number or other flat detail, pick the bone it " +
                "should move with, and Apply pins that patch's vertices to move as one rigid " +
                "body instead of blending across bones - which is what causes painted detail to " +
                "shear during animation. Best on flat torso/back areas; avoid painting across a " +
                "joint that needs to bend on its own."));

            flow.Controls.Add(new Label { Text = "Pin painted region to bone:", AutoSize = true, Margin = new Padding(3, 4, 3, 2) });
            _boneDropdown = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 8) };
            flow.Controls.Add(_boneDropdown);

            flow.Controls.Add(new Label
            {
                Text = "Paint a region",
                AutoSize = true,
                Margin = new Padding(3, 4, 3, 4),
            });

            _chkPaintMode = new CheckBox
            {
                Text = "Paint mode (drag on the Paint 3D view; Ctrl or right-click to erase)",
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkPaintMode.CheckedChanged += (s, e) => PushPaintMode();
            flow.Controls.Add(_chkPaintMode);

            _lblBrush = new Label { Text = "Brush size: 5%", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderBrush = new TrackBar
            {
                Width = 300, Height = 45, Minimum = 1, Maximum = 40, Value = 5,
                TickFrequency = 5, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderBrush.ValueChanged += (s, e) => { _lblBrush.Text = $"Brush size: {_sliderBrush.Value}%"; PushBrushRadius(); };
            flow.Controls.Add(_lblBrush);
            flow.Controls.Add(_sliderBrush);

            _chkWireframe = new CheckBox { Text = "Wireframe", AutoSize = true, Margin = new Padding(3, 0, 3, 2) };
            _chkWireframe.CheckedChanged += (s, e) => PushWireframe();
            flow.Controls.Add(_chkWireframe);
            flow.Controls.Add(HelpText(
                "Draws the triangle edges over the Paint view - useful for seeing how dense the " +
                "mesh actually is under the brush, and how far a feather ring of a given width " +
                "will really reach."));

            _btnColorize = MakeButton("Colorize by Rigidity");
            _btnColorize.Click += (s, e) => ToggleRigidityColors();
            flow.Controls.Add(_btnColorize);
            flow.Controls.Add(HelpText(
                "Shades the Paint view by how rigidly each vertex is already bound: green where a " +
                "single bone owns it outright (nothing painted there can shear), through yellow, " +
                "to red where the weight is split evenly across four bones - the areas most likely " +
                "to skew, and the ones worth painting. Regions you Apply turn green."));

            _btnClearSelection = MakeButton("Clear Selection");
            _btnClearSelection.Click += (s, e) => ClearSelection();
            flow.Controls.Add(_btnClearSelection);

            _lblSelection = new Label { Text = "0 triangles painted", AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
            flow.Controls.Add(_lblSelection);

            var featherRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 4, 0, 2),
            };
            featherRow.Controls.Add(new Label { Text = "Feather width (rings):", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _numFeatherRings = new NumericUpDown { Width = 70, Minimum = 0, Maximum = 8, Value = 2, Margin = new Padding(0, 4, 3, 3) };
            featherRow.Controls.Add(_numFeatherRings);
            flow.Controls.Add(featherRow);
            flow.Controls.Add(HelpText(
                "Triangles just outside the painted core are blended gradually toward the pinned " +
                "bone instead of snapping outright, so the edge doesn't crease. 0 = hard edge, " +
                "higher = softer/wider transition."));

            _btnApply = MakeButton("Apply");
            _btnApply.Click += async (s, e) => await ApplyAsync();
            flow.Controls.Add(_btnApply);

            _btnRemoveRigidity = MakeButton("Remove Rigidity from Painted Faces");
            _btnRemoveRigidity.Enabled = false;
            _btnRemoveRigidity.Click += async (s, e) => await RemovePaintedRigidityAsync();
            flow.Controls.Add(_btnRemoveRigidity);
            flow.Controls.Add(HelpText(
                "Un-pins just the faces painted right now, putting their original skin weights " +
                "back and leaving the rest of what you applied alone - paint over the part that " +
                "got pinned too far and take only that back. Feather width applies here too: the " +
                "painted core goes fully back to original and the rings ease into whatever pinning " +
                "survives around them, so the cut doesn't leave a crease."));

            _btnRevert = MakeButton("Revert All Rigid Regions");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertAll();
            flow.Controls.Add(_btnRevert);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new Size(300, 0),
                Margin = new Padding(3, 8, 3, 4), ForeColor = Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            flow.Controls.Add(new Label
            {
                Text = "Preview animation",
                AutoSize = true,
                Margin = new Padding(3, 10, 3, 4),
            });

            flow.Controls.Add(new Label { Text = "Animation:", AutoSize = true, Margin = new Padding(3, 0, 3, 2) });
            _animDropdown = new ComboBox { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _animDropdown.SelectedIndexChanged += (s, e) => PushAnimationState();
            flow.Controls.Add(_animDropdown);

            _chkPlaying = new CheckBox { Text = "Playing", Checked = true, AutoSize = true, Margin = new Padding(3, 0, 3, 4) };
            _chkPlaying.CheckedChanged += (s, e) => { _sliderScrub.Enabled = !_chkPlaying.Checked; PushAnimationState(); };
            flow.Controls.Add(_chkPlaying);

            _sliderScrub = new TrackBar
            {
                Width = 300, Height = 45, Minimum = 0, Maximum = 1000, Value = 0,
                Enabled = false, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderScrub.Scroll += (s, e) => Seek();
            flow.Controls.Add(_sliderScrub);
            flow.Controls.Add(HelpText(
                "Watch the right pane while animating to confirm the pinned region holds its " +
                "shape. Pause and drag the scrub bar to freeze on whichever frame used to show " +
                "the worst skew."));

            controlPanel.Controls.Add(flow);

            var previewPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            previewPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            var lblPaint = new Label { Text = "Paint here (bind pose)", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            var lblPreview = new Label { Text = "Animated preview", AutoSize = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
            previewPanel.Controls.Add(lblPaint, 0, 0);
            previewPanel.Controls.Add(lblPreview, 1, 0);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            previewPanel.Controls.Add(_webView, 0, 1);
            previewPanel.SetColumnSpan(_webView, 2);

            Controls.Add(previewPanel);
            Controls.Add(controlPanel);
        }

        private static Label HelpText(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(3, 0, 3, 10),
            ForeColor = Color.Gray,
        };

        private static Button MakeButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new Size(300, 0),
            Margin = new Padding(3, 3, 3, 3),
        };

        // ---------------------------------------------------------------------------------------
        // Bones / animations
        // ---------------------------------------------------------------------------------------

        private void PopulateBones()
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var skin in _model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var name = skin.GetJoint(i).Joint.Name;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name)) names.Add(name);
                }

            _boneDropdown.Items.Clear();
            foreach (var n in names) _boneDropdown.Items.Add(n);

            _hasSkins = names.Count > 0;
            SetControlsEnabled(_hasSkins);
            if (_hasSkins)
            {
                _boneDropdown.SelectedIndex = 0;
            }
            else
            {
                _lblStatus.Text = "No skinned meshes with named joints found in this model - nothing to pin a region to.";
            }
        }

        private void SetControlsEnabled(bool enabled)
        {
            _boneDropdown.Enabled = enabled;
            _chkPaintMode.Enabled = enabled;
            _sliderBrush.Enabled = enabled;
            _btnColorize.Enabled = enabled;
            _btnClearSelection.Enabled = enabled;
            _numFeatherRings.Enabled = enabled;
            _btnApply.Enabled = enabled;
        }

        private void PopulateAnimations()
        {
            _animDropdown.Items.Clear();
            _animDropdown.Items.Add("(Bind pose)");
            foreach (var anim in _model.LogicalAnimations)
                _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");
            _animDropdown.SelectedIndex = _model.LogicalAnimations.Count > 0 ? 1 : 0;
        }

        private string? CurrentAnimationName() =>
            _animDropdown.SelectedIndex > 0 ? (string?)_animDropdown.Items[_animDropdown.SelectedIndex] : null;

        private float CurrentAnimationDuration()
        {
            int idx = _animDropdown.SelectedIndex;
            if (idx <= 0 || idx - 1 >= _model.LogicalAnimations.Count) return 0f;
            return _model.LogicalAnimations[idx - 1].Duration;
        }

        // ---------------------------------------------------------------------------------------
        // Apply / Revert - the actual skin-weight rewrite
        // ---------------------------------------------------------------------------------------

        // 1.0 at the painted core, linearly down to just above 0 at the outermost feather ring,
        // 0 (untouched) past that - so the boundary blends rather than creasing.
        private static float FeatherFactor(int level, int maxRings)
        {
            if (level < 0 || level > maxRings) return 0f;
            if (level == 0) return 1f;
            return 1f - (float)level / (maxRings + 1);
        }

        // Blends a vertex's original skin weights toward 100% on chosenJoint by factor t (1 =
        // fully rigid, 0 = untouched). The chosen bone may not be among the vertex's original up-
        // to-4 joints, so up to 5 candidates are considered and the smallest is dropped to fit
        // back into 4 slots, renormalizing so weights still sum to 1.
        private static (Vector4 Joints, Vector4 Weights) ComputeRigidBlend(
            Vector4 origJoints, Vector4 origWeights, int chosenJoint, float t)
        {
            var slots = new List<(int Joint, float Weight)>();
            void AddOrig(float j, float w) { if (w > 0f) slots.Add(((int)j, w)); }
            AddOrig(origJoints.X, origWeights.X);
            AddOrig(origJoints.Y, origWeights.Y);
            AddOrig(origJoints.Z, origWeights.Z);
            AddOrig(origJoints.W, origWeights.W);

            float existingChosen = 0f;
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Joint != chosenJoint) continue;
                existingChosen = slots[i].Weight;
                slots.RemoveAt(i);
                break;
            }

            var blended = new List<(int Joint, float Weight)> { (chosenJoint, existingChosen * (1f - t) + t) };
            foreach (var s in slots) blended.Add((s.Joint, s.Weight * (1f - t)));

            blended = blended.OrderByDescending(s => s.Weight).Take(4).ToList();
            float sum = blended.Sum(s => s.Weight);
            if (sum < 1e-6f) { blended = new List<(int, float)> { (chosenJoint, 1f) }; sum = 1f; }

            var j = new float[4];
            var w = new float[4];
            for (int i = 0; i < blended.Count; i++)
            {
                j[i] = blended[i].Joint;
                w[i] = blended[i].Weight / sum;
            }
            return (new Vector4(j[0], j[1], j[2], j[3]), new Vector4(w[0], w[1], w[2], w[3]));
        }

        // Blends a vertex's CURRENT skin weights back toward the originals it had before any
        // Apply touched it, by factor t (1 = fully original, 0 = leave the pinning as it is).
        // Joint INDICES can't be interpolated, so the two sets are merged in weight space
        // instead: each side contributes its share, entries naming the same joint are summed,
        // and the four heaviest survive - the same drop-the-smallest-and-renormalize rule
        // ComputeRigidBlend uses to fit back into glTF's four slots.
        private static (Vector4 Joints, Vector4 Weights) BlendTowardOriginal(
            Vector4 curJoints, Vector4 curWeights, Vector4 origJoints, Vector4 origWeights, float t)
        {
            var merged = new Dictionary<int, float>();
            void Add(Vector4 j, Vector4 w, float scale)
            {
                void One(float joint, float weight)
                {
                    if (weight <= 0f) return;
                    int idx = (int)joint;
                    merged[idx] = merged.TryGetValue(idx, out var existing) ? existing + weight * scale : weight * scale;
                }
                One(j.X, w.X); One(j.Y, w.Y); One(j.Z, w.Z); One(j.W, w.W);
            }
            Add(origJoints, origWeights, t);
            Add(curJoints, curWeights, 1f - t);

            var top = merged.OrderByDescending(kv => kv.Value).Take(4).ToList();
            float sum = top.Sum(kv => kv.Value);
            if (top.Count == 0 || sum < 1e-6f) return (origJoints, origWeights);

            var j2 = new float[4];
            var w2 = new float[4];
            for (int i = 0; i < top.Count; i++)
            {
                j2[i] = top[i].Key;
                w2[i] = top[i].Value / sum;
            }
            return (new Vector4(j2[0], j2[1], j2[2], j2[3]), new Vector4(w2[0], w2[1], w2[2], w2[3]));
        }

        private Skin? FindSkinForMesh(SharpGLTF.Schema2.Mesh mesh)
        {
            foreach (var node in _model.LogicalNodes)
                if (node.Mesh == mesh && node.Skin != null) return node.Skin;
            return null;
        }

        private static int FindJointIndex(Skin skin, string boneName)
        {
            for (int i = 0; i < skin.JointsCount; i++)
                if (skin.GetJoint(i).Joint.Name == boneName) return i;
            return -1;
        }

        private async Task ApplyAsync()
        {
            if (!_hasSkins || _boneDropdown.SelectedItem is not string boneName) return;

            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            if (selection == null || selection.Count == 0)
            {
                _lblStatus.Text = "Nothing painted - paint a region first.";
                return;
            }

            int maxRings = (int)_numFeatherRings.Value;
            int touchedVerts = 0, skippedPrims = 0;

            foreach (int meshIdx in selection.Keys.Select(k => k.MeshIndex).Distinct())
            {
                if (meshIdx < 0 || meshIdx >= _model.LogicalMeshes.Count) { skippedPrims++; continue; }
                var mesh = _model.LogicalMeshes[meshIdx];

                var skin = FindSkinForMesh(mesh);
                if (skin == null) { skippedPrims++; continue; }

                int chosenJoint = FindJointIndex(skin, boneName);
                if (chosenJoint < 0) { skippedPrims++; continue; }

                touchedVerts += ApplyRigidRegionToMesh(mesh, meshIdx, selection, chosenJoint, maxRings, ref skippedPrims);
            }

            RefreshAfterSkinEdit();

            _lblStatus.Text = touchedVerts > 0
                ? $"Applied: {touchedVerts:N0} vertex weight(s) pinned to '{boneName}'" +
                  (skippedPrims > 0 ? $" ({skippedPrims} painted primitive(s) skipped - no matching skin/bone)." : ".") +
                  " Included the next time you save."
                : "Nothing applied - the painted primitive(s) have no skin, or that bone isn't part of it.";
        }

        // The local counterpart to Revert All: instead of unwinding every region this session
        // has pinned, it takes back only what is under the paint right now, so an Apply that
        // reached further than it should can be trimmed rather than redone from scratch.
        private async Task RemovePaintedRigidityAsync()
        {
            if (!_hasSkins) return;
            if (_originalSkin.Count == 0)
            {
                _lblStatus.Text = "Nothing to remove - no region has been pinned in this session.";
                return;
            }

            var selection = await ReadSelectionAsync();
            if (IsDisposed) return;
            if (selection == null || selection.Count == 0)
            {
                _lblStatus.Text = "Nothing painted - paint over the faces you want un-pinned first.";
                return;
            }

            int maxRings = (int)_numFeatherRings.Value;
            int restoredVerts = 0, skippedPrims = 0;

            foreach (int meshIdx in selection.Keys.Select(k => k.MeshIndex).Distinct())
            {
                if (meshIdx < 0 || meshIdx >= _model.LogicalMeshes.Count) { skippedPrims++; continue; }
                restoredVerts += RemoveRigidityFromMesh(
                    _model.LogicalMeshes[meshIdx], meshIdx, selection, maxRings, ref skippedPrims);
            }

            RefreshAfterSkinEdit();

            _lblStatus.Text = restoredVerts > 0
                ? $"Removed: {restoredVerts:N0} vertex weight(s) restored to their original values" +
                  (skippedPrims > 0 ? $" ({skippedPrims} painted primitive(s) skipped - no skin data)." : ".") +
                  " Included the next time you save."
                : "Nothing removed - the painted faces were never pinned in this session.";
        }

        // A jersey-number-style paint almost always sits on its own UV island, which means the
        // mesh is topologically SPLIT right at the edge of the painted patch - duplicate,
        // position-coincident vertices so the texture atlas can give the number its own space.
        // Feathering by raw vertex-INDEX adjacency alone stops dead at that seam (the far side is
        // a different vertex index even though it's the same point in space), which pins one side
        // of the seam while its duplicate stays completely untouched - exactly the mismatch that
        // opens a visible hole as the two coincident-but-differently-weighted copies diverge
        // under animation. Unioning every position-coincident vertex (across every primitive of
        // this mesh, since a UV split commonly also splits a primitive in two) into one "logical
        // vertex" before running the ring BFS is what lets the feather actually cross the seam -
        // both copies of a seam vertex always land in the same ring, get the same blend factor,
        // and so stay coincident as the bone moves instead of tearing apart.
        private PaintedRegion? BuildPaintedRegion(
            SharpGLTF.Schema2.Mesh mesh, int meshIdx,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            int maxRings)
        {
            var prims = new List<(int PrimIndex, Accessor Joints, Accessor Weights,
                IList<Vector3> Positions, List<(int A, int B, int C)> Triangles)>();
            for (int primIdx = 0; primIdx < mesh.Primitives.Count; primIdx++)
            {
                var prim = mesh.Primitives[primIdx];
                if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;
                if (!prim.VertexAccessors.TryGetValue("POSITION", out var pAcc)) continue;
                prims.Add((primIdx, jAcc, wAcc, pAcc.AsVector3Array(), prim.GetTriangleIndices().ToList()));
            }
            if (prims.Count == 0) return null;

            var offsets = new int[prims.Count];
            int total = 0;
            for (int i = 0; i < prims.Count; i++) { offsets[i] = total; total += prims[i].Positions.Count; }
            if (total == 0) return null;

            var dsu = new DisjointSet(total);

            // DSU is used ONLY to identify seam duplicates (below) - NOT ordinary triangle
            // topology. A whole connected mesh's vertices are transitively joined by shared
            // triangle edges, so unioning on every triangle edge here would collapse the entire
            // mesh into one component and make every vertex look like part of the painted core.
            // Real triangle-edge adjacency is tracked separately in rootAdjacency below, which is
            // what the ring BFS actually walks.

            // Seam bridge: any vertex (in any primitive of this mesh - they all share the same
            // node, hence the same local coordinate space) sitting at the same bind-pose position
            // as another is the same physical point, regardless of index or which primitive it's
            // in. 4 decimal places (~0.1mm at meter scale) is tight enough to never merge
            // genuinely distinct nearby vertices while still catching true duplicates, which are
            // normally bit-for-bit identical anyway.
            var byPosition = new Dictionary<(int, int, int), int>();
            for (int i = 0; i < prims.Count; i++)
            {
                var positions = prims[i].Positions;
                for (int v = 0; v < positions.Count; v++)
                {
                    var p = positions[v];
                    var key = ((int)MathF.Round(p.X * 10000f), (int)MathF.Round(p.Y * 10000f), (int)MathF.Round(p.Z * 10000f));
                    int gid = offsets[i] + v;
                    if (byPosition.TryGetValue(key, out var existing)) dsu.Union(existing, gid);
                    else byPosition[key] = gid;
                }
            }

            // Adjacency between logical vertices (DSU roots) - built from the same triangles as
            // above, but keyed by root rather than raw index, so two triangles on opposite sides
            // of a seam that was just bridged become directly linked in this graph even though
            // they never shared a raw vertex index.
            var rootAdjacency = new Dictionary<int, HashSet<int>>();
            void Link(int ra, int rb)
            {
                if (ra == rb) return;
                if (!rootAdjacency.TryGetValue(ra, out var set)) rootAdjacency[ra] = set = new HashSet<int>();
                set.Add(rb);
                if (!rootAdjacency.TryGetValue(rb, out var set2)) rootAdjacency[rb] = set2 = new HashSet<int>();
                set2.Add(ra);
            }
            for (int i = 0; i < prims.Count; i++)
                foreach (var (a, b, c) in prims[i].Triangles)
                {
                    int ra = dsu.Find(offsets[i] + a), rb = dsu.Find(offsets[i] + b), rc = dsu.Find(offsets[i] + c);
                    Link(ra, rb); Link(rb, rc); Link(ra, rc);
                }

            // Seed the BFS at every painted (core) vertex's logical root.
            var level = new Dictionary<int, int>();
            var frontier = new List<int>();
            for (int i = 0; i < prims.Count; i++)
            {
                if (!selection.TryGetValue((meshIdx, prims[i].PrimIndex), out var triSet)) continue;
                var triangles = prims[i].Triangles;
                foreach (var t in triSet)
                {
                    if (t < 0 || t >= triangles.Count) continue;
                    var (a, b, c) = triangles[t];
                    foreach (var v in new[] { a, b, c })
                    {
                        int root = dsu.Find(offsets[i] + v);
                        if (level.ContainsKey(root)) continue;
                        level[root] = 0;
                        frontier.Add(root);
                    }
                }
            }

            for (int ring = 1; ring <= maxRings && frontier.Count > 0; ring++)
            {
                var next = new List<int>();
                foreach (var root in frontier)
                {
                    if (!rootAdjacency.TryGetValue(root, out var neighbors)) continue;
                    foreach (var n in neighbors)
                    {
                        if (level.ContainsKey(n)) continue;
                        level[n] = ring;
                        next.Add(n);
                    }
                }
                frontier = next;
            }

            return new PaintedRegion(prims, offsets, dsu, level);
        }

        // The painted patch resolved against one mesh: the primitives that carry skin data, the
        // running offsets that let (primitive, vertex) be addressed as one flat index space, the
        // union-find that bridged the UV seams, and the ring level every logical vertex the paint
        // reached ended up on. Both Apply and Remove walk exactly this - they differ only in what
        // they write into the weights once a vertex's feather factor is in hand.
        private sealed record PaintedRegion(
            List<(int PrimIndex, Accessor Joints, Accessor Weights,
                IList<Vector3> Positions, List<(int A, int B, int C)> Triangles)> Prims,
            int[] Offsets,
            DisjointSet Dsu,
            Dictionary<int, int> Level)
        {
            // 1 at the painted core, tapering across the feather rings, 0 for anything the paint
            // never reached (which callers skip entirely).
            public float FeatherAt(int primSlot, int vertex, int maxRings)
            {
                int root = Dsu.Find(Offsets[primSlot] + vertex);
                return Level.TryGetValue(root, out int lvl) ? FeatherFactor(lvl, maxRings) : 0f;
            }
        }

        private int ApplyRigidRegionToMesh(
            SharpGLTF.Schema2.Mesh mesh, int meshIdx,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            int chosenJoint, int maxRings, ref int skippedPrims)
        {
            var region = BuildPaintedRegion(mesh, meshIdx, selection, maxRings);
            if (region == null) { skippedPrims++; return 0; }

            int touched = 0;
            for (int i = 0; i < region.Prims.Count; i++)
            {
                var (_, jointsAcc, weightsAcc, positions, _) = region.Prims[i];
                IList<Vector4>? joints = null, weights = null;

                for (int v = 0; v < positions.Count; v++)
                {
                    float f = region.FeatherAt(i, v, maxRings);
                    if (f <= 0f) continue;

                    joints ??= jointsAcc.AsVector4Array();
                    weights ??= weightsAcc.AsVector4Array();

                    if (!_originalSkin.ContainsKey(jointsAcc))
                        _originalSkin[jointsAcc] = (weightsAcc, joints.ToArray(), weights.ToArray());

                    var (nj, nw) = ComputeRigidBlend(joints[v], weights[v], chosenJoint, f);
                    joints[v] = nj;
                    weights[v] = nw;
                    touched++;
                }
            }
            return touched;
        }

        // Apply in reverse, over the same seam-bridged region and the same feather taper, writing
        // the snapshot back instead of pinning: the painted core returns to exactly the weights it
        // had before the first Apply, and the rings land part-way so the boundary between what was
        // taken back and what is still pinned is as gradual as the boundary Apply itself makes.
        // A primitive with no snapshot was never pinned here, so there is nothing to give back -
        // it is left alone rather than counted as skipped.
        private int RemoveRigidityFromMesh(
            SharpGLTF.Schema2.Mesh mesh, int meshIdx,
            Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>> selection,
            int maxRings, ref int skippedPrims)
        {
            var region = BuildPaintedRegion(mesh, meshIdx, selection, maxRings);
            if (region == null) { skippedPrims++; return 0; }

            int touched = 0;
            for (int i = 0; i < region.Prims.Count; i++)
            {
                var (_, jointsAcc, weightsAcc, positions, _) = region.Prims[i];
                if (!_originalSkin.TryGetValue(jointsAcc, out var snapshot)) continue;

                IList<Vector4>? joints = null, weights = null;

                for (int v = 0; v < positions.Count; v++)
                {
                    if (v >= snapshot.OrigJoints.Length) break;
                    float f = region.FeatherAt(i, v, maxRings);
                    if (f <= 0f) continue;

                    joints ??= jointsAcc.AsVector4Array();
                    weights ??= weightsAcc.AsVector4Array();

                    var (nj, nw) = BlendTowardOriginal(
                        joints[v], weights[v], snapshot.OrigJoints[v], snapshot.OrigWeights[v], f);
                    joints[v] = nj;
                    weights[v] = nw;
                    touched++;
                }
            }
            return touched;
        }

        // Minimal union-find with path compression and union-by-rank, used to collapse
        // position-coincident (seam-duplicate) vertices into single logical nodes.
        private sealed class DisjointSet
        {
            private readonly int[] _parent;
            private readonly int[] _rank;

            public DisjointSet(int size)
            {
                _parent = new int[size];
                _rank = new int[size];
                for (int i = 0; i < size; i++) _parent[i] = i;
            }

            public int Find(int x)
            {
                while (_parent[x] != x)
                {
                    _parent[x] = _parent[_parent[x]];
                    x = _parent[x];
                }
                return x;
            }

            public void Union(int a, int b)
            {
                int ra = Find(a), rb = Find(b);
                if (ra == rb) return;
                if (_rank[ra] < _rank[rb]) (ra, rb) = (rb, ra);
                _parent[rb] = ra;
                if (_rank[ra] == _rank[rb]) _rank[ra]++;
            }
        }

        private void RevertAll()
        {
            foreach (var (jointsAcc, (weightsAcc, origJoints, origWeights)) in _originalSkin)
            {
                var joints = jointsAcc.AsVector4Array();
                var weights = weightsAcc.AsVector4Array();
                for (int i = 0; i < origJoints.Length; i++)
                {
                    joints[i] = origJoints[i];
                    weights[i] = origWeights[i];
                }
            }
            _originalSkin.Clear();

            RefreshAfterSkinEdit();
            _lblStatus.Text = "Reverted - all painted regions are back to their original skin weights.";
        }

        // ---------------------------------------------------------------------------------------
        // 3D panes / paint tool
        // ---------------------------------------------------------------------------------------

        private async Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-
            // forget startup may still be mid-await - both the await itself and everything after
            // it have to tolerate that (same guard TextureEditorEditor/GeometryOptimizerEditor use).
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            _currentPaintFileName = WriteTaggedGlbFile(_model, ref _paintGlbPath);
            string previewFileName = WriteGlbFile(_model, ref _previewGlbPath);

            string html = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #wrap { display: flex; width: 100%; height: 100%; }
                    .pane { flex: 1; position: relative; }
                    .pane:first-child { border-right: 1px solid #444; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; position: absolute; top: 0; left: 0; }
                    #overlayCanvas { position: absolute; top: 0; left: 0; width: 100%; height: 100%; pointer-events: none; }
                    .paneLabel {
                        position: absolute; top: 6px; left: 8px; color: #ddd; font: 12px sans-serif;
                        background: rgba(0, 0, 0, 0.45); padding: 2px 6px; border-radius: 3px; pointer-events: none;
                        z-index: 2;
                    }
                </style>
            </head>
            <body>
                <div id='wrap'>
                    <div class='pane' id='panepaint'>
                        <div class='paneLabel'>Paint (bind pose)</div>
                        <model-viewer id='paint' src='https://appassets.local/" + _currentPaintFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                        <canvas id='overlayCanvas'></canvas>
                    </div>
                    <div class='pane'>
                        <div class='paneLabel'>Animated preview</div>
                        <model-viewer id='preview' src='https://appassets.local/" + previewFileName + @"'
                            camera-controls shadow-intensity='1' environment-image='neutral' exposure='1'>
                        </model-viewer>
                    </div>
                </div>
                <script>
                    function fixMaterials(viewer) {
                        (viewer.model.materials || []).forEach(function (mat) {
                            mat.setAlphaMode('OPAQUE');
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });
                    }

                    var paintViewer = document.getElementById('paint');
                    var previewViewer = document.getElementById('preview');
                    var panepaint = document.getElementById('panepaint');

                    // --- Animated preview pane --------------------------------------------------
                    // Ported from AnimationTrimEditor's plain-playback loop (see that file for the
                    // fuller rationale on driving currentTime by hand rather than viewer.play()).
                    var previewLoaded = false;
                    var playRaf = null, playLastTs = null, playActive = false;
                    var currentAnimName = null;

                    function stopPlayLoop() {
                        playActive = false;
                        playLastTs = null;
                        if (playRaf !== null) { cancelAnimationFrame(playRaf); playRaf = null; }
                    }

                    function playLoopTick(ts) {
                        if (!playActive) return;
                        if (playLastTs === null) playLastTs = ts;
                        var dt = (ts - playLastTs) / 1000;
                        playLastTs = ts;
                        var duration = previewViewer.duration || 0;
                        if (duration > 0) {
                            var t = previewViewer.currentTime + dt;
                            previewViewer.currentTime = t >= duration ? (t % duration) : t;
                        }
                        playRaf = requestAnimationFrame(playLoopTick);
                    }

                    window.setPreviewAnimation = function (name, playing) {
                        stopPlayLoop();
                        currentAnimName = name;
                        if (!previewLoaded) return;
                        previewViewer.pause();
                        if (name) {
                            previewViewer.animationName = name;
                            previewViewer.currentTime = 0;
                            if (playing) { playActive = true; playRaf = requestAnimationFrame(playLoopTick); }
                        } else {
                            previewViewer.currentTime = 0;
                        }
                    };

                    window.setPreviewPlaying = function (playing) {
                        if (!previewLoaded || !currentAnimName) return;
                        if (playing) {
                            playActive = true;
                            playLastTs = null;
                            playRaf = requestAnimationFrame(playLoopTick);
                        } else {
                            stopPlayLoop();
                        }
                    };

                    window.seekPreview = function (t) {
                        stopPlayLoop();
                        if (!previewLoaded) return;
                        previewViewer.pause();
                        previewViewer.currentTime = t;
                    };

                    previewViewer.addEventListener('load', function (e) {
                        fixMaterials(e.target);
                        previewLoaded = true;
                        window.chrome && window.chrome.webview &&
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'previewReady' }));
                    });
                    // --- end animated preview pane ------------------------------------------------

                    // --- Paint-a-region tool (Paint pane only) ------------------------------------
                    // Ported near-verbatim from TextureEditorEditor's paint tool - see that file's
                    // paintAllInBrush for the fuller rationale (bounding-sphere overlap vs brush
                    // radius, facing-the-camera-only, no connectivity requirement).
                    var paintableMeshes = [];      // { meshIndex, primIndex, object, centroids, radii, normals }
                    var selection = {};             // 'meshIndex_primIndex' -> Set<triangleIndex>
                    var paintMode = false;
                    var painting = false;
                    var brushFraction = 0.05;
                    var wireframe = false;
                    var rigidityColorsEnabled = false;
                    var brushRadius = 0.05;
                    var modelMaxDim = 1;

                    function resolveBrushRadius() { brushRadius = brushFraction * (modelMaxDim || 1); }

                    var overlayCanvas = document.getElementById('overlayCanvas');
                    var overlayRenderer = new THREE.WebGLRenderer({ canvas: overlayCanvas, alpha: true, antialias: true });
                    overlayRenderer.setPixelRatio(window.devicePixelRatio);
                    var overlayScene = new THREE.Scene();
                    var overlayCamera = new THREE.PerspectiveCamera(45, 1, 0.01, 10000);
                    var overlayGroup = new THREE.Group();
                    overlayScene.add(overlayGroup);
                    var highlightMaterial = new THREE.MeshBasicMaterial({
                        color: 0x33bbff, transparent: true, opacity: 0.5, depthTest: true,
                        side: THREE.DoubleSide, polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
                        skinning: true,
                    });

                    function paneSize() {
                        return { w: panepaint.clientWidth || 1, h: panepaint.clientHeight || 1 };
                    }

                    function makeSkinnedCopy(src, geometry, material) {
                        var mesh;
                        if (src.isSkinnedMesh) {
                            mesh = new THREE.SkinnedMesh(geometry, material);
                            mesh.bind(src.skeleton, src.bindMatrix);
                        } else {
                            mesh = new THREE.Mesh(geometry, material);
                        }
                        mesh.matrixAutoUpdate = false;
                        mesh.matrix.copy(src.matrixWorld);
                        mesh.frustumCulled = false;
                        return mesh;
                    }

                    var depthMaskMaterial = new THREE.MeshBasicMaterial({
                        colorWrite: false, side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: 1, polygonOffsetUnits: 1,
                        skinning: true,
                    });
                    var depthMaskGroup = new THREE.Group();
                    overlayScene.add(depthMaskGroup);

                    function rebuildDepthMask() {
                        for (var i = depthMaskGroup.children.length - 1; i >= 0; i--) {
                            depthMaskGroup.remove(depthMaskGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var maskMesh = makeSkinnedCopy(meshInfo.object, meshInfo.object.geometry, depthMaskMaterial);
                            depthMaskGroup.add(maskMesh);
                        });
                    }

                    // --- Wireframe -------------------------------------------------------------
                    // Same approach as GeometryOptimizerEditor's wireframe toggle (see that file
                    // for the fuller rationale): drawn from the same hit-test geometry everything
                    // else here uses, and only built while switched on since WireframeGeometry
                    // expands to six vertices per triangle.
                    var wireframeMaterial = new THREE.LineBasicMaterial({
                        color: 0x8ab4f8, transparent: true, opacity: 0.55, depthTest: true,
                    });
                    var wireframeGroup = new THREE.Group();
                    overlayScene.add(wireframeGroup);

                    function rebuildWireframe() {
                        for (var i = wireframeGroup.children.length - 1; i >= 0; i--) {
                            var old = wireframeGroup.children[i];
                            wireframeGroup.remove(old);
                            old.geometry.dispose();
                        }
                        if (!wireframe) return;
                        paintableMeshes.forEach(function (meshInfo) {
                            var lines = new THREE.LineSegments(
                                new THREE.WireframeGeometry(meshInfo.object.geometry), wireframeMaterial);
                            lines.matrixAutoUpdate = false;
                            lines.matrix.copy(meshInfo.object.matrixWorld);
                            wireframeGroup.add(lines);
                        });
                    }

                    window.setWireframe = function (enabled) {
                        wireframe = enabled;
                        rebuildWireframe();
                    };
                    // --- end wireframe -----------------------------------------------------------

                    // --- Rigidity colouring ------------------------------------------------------
                    // Paints the hit-test copy green-to-red by how rigidly each vertex is already
                    // bound, so it's obvious at a glance which areas are worth painting a region
                    // over. The metric is the vertex's single heaviest skin weight: 1.0 means one
                    // bone owns the vertex outright (already rigid - can't shear from blending,
                    // whatever RigidRegionEditor's own Apply would also produce), 0.25 means it's
                    // split evenly across all four slots (the worst case for shearing). Unskinned
                    // geometry has no blending to shear from in the first place, so it's treated as
                    // fully rigid (green) rather than left uncoloured.
                    // Drawn as an opaque overlay pulled toward the camera with the same
                    // polygonOffset trick the paint-selection highlight uses, rather than swapping
                    // the real material - so turning it off needs no material bookkeeping on the
                    // model itself.
                    var rigidityColorMaterial = new THREE.MeshBasicMaterial({
                        vertexColors: true, side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1,
                        skinning: true,
                    });
                    var rigidityColorGroup = new THREE.Group();
                    rigidityColorGroup.visible = false;
                    overlayScene.add(rigidityColorGroup);

                    var RIGIDITY_RED = new THREE.Color(0xe04030);
                    var RIGIDITY_YELLOW = new THREE.Color(0xe0c020);
                    var RIGIDITY_GREEN = new THREE.Color(0x30c040);
                    var _rigidityTmp = new THREE.Color();

                    function rigidityColorFor(maxWeight) {
                        var t = THREE.MathUtils.clamp((maxWeight - 0.25) / 0.75, 0, 1);
                        return t < 0.5
                            ? _rigidityTmp.copy(RIGIDITY_RED).lerp(RIGIDITY_YELLOW, t / 0.5)
                            : _rigidityTmp.copy(RIGIDITY_YELLOW).lerp(RIGIDITY_GREEN, (t - 0.5) / 0.5);
                    }

                    function rebuildRigidityColors() {
                        for (var i = rigidityColorGroup.children.length - 1; i >= 0; i--) {
                            var old = rigidityColorGroup.children[i];
                            rigidityColorGroup.remove(old);
                            old.geometry.dispose();
                        }
                        if (!rigidityColorsEnabled) return;
                        paintableMeshes.forEach(function (meshInfo) {
                            var srcGeom = meshInfo.object.geometry;
                            var vertexCount = srcGeom.attributes.position.count;
                            var skinWeight = srcGeom.attributes.skinWeight;
                            var colors = new Float32Array(vertexCount * 3);
                            for (var v = 0; v < vertexCount; v++) {
                                var maxWeight = 1;
                                if (skinWeight) {
                                    maxWeight = Math.max(
                                        skinWeight.getX(v), skinWeight.getY(v),
                                        skinWeight.getZ(v), skinWeight.getW(v));
                                }
                                var c = rigidityColorFor(maxWeight);
                                colors[v * 3] = c.r; colors[v * 3 + 1] = c.g; colors[v * 3 + 2] = c.b;
                            }
                            var coloredGeom = new THREE.BufferGeometry();
                            coloredGeom.setAttribute('position', srcGeom.attributes.position);
                            coloredGeom.setAttribute('color', new THREE.BufferAttribute(colors, 3));
                            if (meshInfo.object.isSkinnedMesh) {
                                coloredGeom.setAttribute('skinIndex', srcGeom.attributes.skinIndex);
                                coloredGeom.setAttribute('skinWeight', srcGeom.attributes.skinWeight);
                            }
                            coloredGeom.setIndex(srcGeom.index);
                            var coloredMesh = makeSkinnedCopy(meshInfo.object, coloredGeom, rigidityColorMaterial);
                            rigidityColorGroup.add(coloredMesh);
                        });
                    }

                    window.setRigidityColors = function (enabled) {
                        rigidityColorsEnabled = enabled;
                        rebuildRigidityColors();
                        rigidityColorGroup.visible = enabled;
                    };
                    // --- end rigidity colouring ---------------------------------------------------

                    var brushDecalMaterial = new THREE.ShaderMaterial({
                        uniforms: {
                            uCenter: { value: new THREE.Vector3() },
                            uRadius: { value: 1 },
                            uColor: { value: new THREE.Color(0x33bbff) },
                        },
                        vertexShader: [
                            '#include <common>',
                            '#include <skinning_pars_vertex>',
                            'varying vec3 vWorld;',
                            'void main() {',
                            '    #include <begin_vertex>',
                            '    #include <beginnormal_vertex>',
                            '    #include <skinbase_vertex>',
                            '    #include <skinning_vertex>',
                            '    vec4 world = modelMatrix * vec4(transformed, 1.0);',
                            '    vWorld = world.xyz;',
                            '    gl_Position = projectionMatrix * viewMatrix * world;',
                            '}',
                        ].join('\n'),
                        fragmentShader: [
                            'uniform vec3 uCenter;',
                            'uniform float uRadius;',
                            'uniform vec3 uColor;',
                            'varying vec3 vWorld;',
                            'void main() {',
                            '    float d = distance(vWorld, uCenter);',
                            '    if (d > uRadius) discard;',
                            '    float rim = smoothstep(uRadius * 0.82, uRadius, d);',
                            '    gl_FragColor = vec4(uColor, mix(0.32, 0.85, rim));',
                            '}',
                        ].join('\n'),
                        transparent: true,
                        depthTest: true,
                        depthWrite: false,
                        side: THREE.DoubleSide,
                        polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4,
                        skinning: true,
                    });
                    var brushDecalGroup = new THREE.Group();
                    brushDecalGroup.visible = false;
                    overlayScene.add(brushDecalGroup);

                    function rebuildBrushDecal() {
                        for (var i = brushDecalGroup.children.length - 1; i >= 0; i--) {
                            brushDecalGroup.remove(brushDecalGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var decal = makeSkinnedCopy(meshInfo.object, meshInfo.object.geometry, brushDecalMaterial);
                            brushDecalGroup.add(decal);
                        });
                    }

                    function showBrushCursor(point) {
                        brushDecalMaterial.uniforms.uCenter.value.copy(point);
                        brushDecalMaterial.uniforms.uRadius.value = brushRadius;
                        brushDecalGroup.visible = true;
                    }

                    function hideBrushCursor() { brushDecalGroup.visible = false; }

                    function syncOverlayCamera() {
                        if (typeof paintViewer.getCameraOrbit !== 'function') return;
                        var size = paneSize();
                        overlayRenderer.setSize(size.w, size.h);
                        var orbit = paintViewer.getCameraOrbit();
                        var target = paintViewer.getCameraTarget();
                        overlayCamera.fov = paintViewer.getFieldOfView();
                        overlayCamera.aspect = size.w / size.h;
                        var span = modelMaxDim * 2;
                        overlayCamera.near = Math.max(orbit.radius - span, modelMaxDim / 1000);
                        overlayCamera.far = orbit.radius + span;
                        overlayCamera.updateProjectionMatrix();
                        var x = target.x + orbit.radius * Math.sin(orbit.phi) * Math.sin(orbit.theta);
                        var y = target.y + orbit.radius * Math.cos(orbit.phi);
                        var z = target.z + orbit.radius * Math.sin(orbit.phi) * Math.cos(orbit.theta);
                        overlayCamera.position.set(x, y, z);
                        overlayCamera.lookAt(target.x, target.y, target.z);
                    }

                    function renderOverlayLoop() {
                        requestAnimationFrame(renderOverlayLoop);
                        syncOverlayCamera();
                        overlayRenderer.render(overlayScene, overlayCamera);
                    }
                    renderOverlayLoop();

                    var _skinIndex = new THREE.Vector4();
                    var _skinWeight = new THREE.Vector4();
                    var _bindPos = new THREE.Vector3();
                    var _bindNrm = new THREE.Vector3();
                    var _boneMatrix = new THREE.Matrix4();
                    var _boneNormalMat = new THREE.Matrix3();
                    var _skinTmp = new THREE.Vector3();

                    function skinnedVertex(mesh, index, posAttr, nrmAttr, outPos, outNormal) {
                        _skinIndex.fromBufferAttribute(mesh.geometry.attributes.skinIndex, index);
                        _skinWeight.fromBufferAttribute(mesh.geometry.attributes.skinWeight, index);
                        _bindPos.fromBufferAttribute(posAttr, index).applyMatrix4(mesh.bindMatrix);
                        if (outNormal && nrmAttr) _bindNrm.fromBufferAttribute(nrmAttr, index).transformDirection(mesh.bindMatrix);

                        outPos.set(0, 0, 0);
                        if (outNormal) outNormal.set(0, 0, 0);

                        for (var i = 0; i < 4; i++) {
                            var weight = _skinWeight.getComponent(i);
                            if (weight === 0) continue;
                            var boneIndex = _skinIndex.getComponent(i);
                            _boneMatrix.multiplyMatrices(
                                mesh.skeleton.bones[boneIndex].matrixWorld,
                                mesh.skeleton.boneInverses[boneIndex]);
                            outPos.addScaledVector(_skinTmp.copy(_bindPos).applyMatrix4(_boneMatrix), weight);
                            if (outNormal && nrmAttr) {
                                _boneNormalMat.getNormalMatrix(_boneMatrix);
                                outNormal.addScaledVector(_skinTmp.copy(_bindNrm).applyMatrix3(_boneNormalMat), weight);
                            }
                        }
                        outPos.applyMatrix4(mesh.bindMatrixInverse);
                        if (outNormal && nrmAttr) { outNormal.transformDirection(mesh.bindMatrixInverse); }
                    }

                    function computeTriangleData(mesh) {
                        mesh.updateMatrixWorld(true);
                        var index = mesh.geometry.index;
                        var pos = mesh.geometry.attributes.position;
                        var nrm = mesh.geometry.attributes.normal;
                        var isSkinned = !!(mesh.isSkinnedMesh && mesh.skeleton
                            && mesh.geometry.attributes.skinIndex && mesh.geometry.attributes.skinWeight);
                        var triCount = index.count / 3;
                        var centroids = new Array(triCount);
                        var radii = new Float32Array(triCount);
                        var normals = new Float32Array(triCount * 3);

                        var normalMatrix = new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld);
                        var a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3();
                        var na = new THREE.Vector3(), nb = new THREE.Vector3(), nc = new THREE.Vector3();
                        var n = new THREE.Vector3(), tmp = new THREE.Vector3(), edge = new THREE.Vector3();

                        for (var t = 0; t < triCount; t++) {
                            var ia = index.getX(t * 3), ib = index.getX(t * 3 + 1), ic = index.getX(t * 3 + 2);

                            if (isSkinned) {
                                skinnedVertex(mesh, ia, pos, nrm, a, nrm ? na : null);
                                skinnedVertex(mesh, ib, pos, nrm, b, nrm ? nb : null);
                                skinnedVertex(mesh, ic, pos, nrm, c, nrm ? nc : null);
                                a.applyMatrix4(mesh.matrixWorld);
                                b.applyMatrix4(mesh.matrixWorld);
                                c.applyMatrix4(mesh.matrixWorld);
                                if (nrm) {
                                    na.transformDirection(mesh.matrixWorld);
                                    nb.transformDirection(mesh.matrixWorld);
                                    nc.transformDirection(mesh.matrixWorld);
                                }
                            } else {
                                a.fromBufferAttribute(pos, ia).applyMatrix4(mesh.matrixWorld);
                                b.fromBufferAttribute(pos, ib).applyMatrix4(mesh.matrixWorld);
                                c.fromBufferAttribute(pos, ic).applyMatrix4(mesh.matrixWorld);
                            }

                            var centroid = new THREE.Vector3().add(a).add(b).add(c).multiplyScalar(1 / 3);
                            centroids[t] = centroid;
                            radii[t] = Math.sqrt(Math.max(
                                centroid.distanceToSquared(a),
                                centroid.distanceToSquared(b),
                                centroid.distanceToSquared(c)));

                            if (nrm && isSkinned) {
                                n.set(0, 0, 0).add(na).add(nb).add(nc);
                            } else if (nrm) {
                                n.set(0, 0, 0);
                                n.add(tmp.fromBufferAttribute(nrm, ia));
                                n.add(tmp.fromBufferAttribute(nrm, ib));
                                n.add(tmp.fromBufferAttribute(nrm, ic));
                                n.applyMatrix3(normalMatrix);
                            } else {
                                n.subVectors(b, a).cross(edge.subVectors(c, a));
                            }
                            if (n.lengthSq() > 1e-20) n.normalize();
                            normals[t * 3] = n.x; normals[t * 3 + 1] = n.y; normals[t * 3 + 2] = n.z;
                        }
                        return { centroids: centroids, radii: radii, normals: normals };
                    }

                    function selectionKey(meshInfo) { return meshInfo.meshIndex + '_' + meshInfo.primIndex; }

                    function paintAllInBrush(point, erase) {
                        var camera = overlayCamera.position;
                        var changed = false;

                        for (var m = 0; m < paintableMeshes.length; m++) {
                            var meshInfo = paintableMeshes[m];
                            var centroids = meshInfo.centroids;
                            var radii = meshInfo.radii;
                            var normals = meshInfo.normals;
                            var key = selectionKey(meshInfo);
                            var set = selection[key];

                            for (var t = 0; t < centroids.length; t++) {
                                var centroid = centroids[t];
                                var reach = brushRadius + radii[t];
                                if (centroid.distanceToSquared(point) > reach * reach) continue;

                                var dot = normals[t * 3] * (centroid.x - camera.x)
                                    + normals[t * 3 + 1] * (centroid.y - camera.y)
                                    + normals[t * 3 + 2] * (centroid.z - camera.z);
                                if (dot > 0) continue;

                                if (erase) {
                                    if (set && set.delete(t)) changed = true;
                                } else {
                                    if (!set) { set = selection[key] = new Set(); }
                                    if (!set.has(t)) { set.add(t); changed = true; }
                                }
                            }
                        }
                        return changed;
                    }

                    function rebuildOverlay() {
                        for (var i = overlayGroup.children.length - 1; i >= 0; i--) {
                            overlayGroup.remove(overlayGroup.children[i]);
                        }
                        paintableMeshes.forEach(function (meshInfo) {
                            var set = selection[selectionKey(meshInfo)];
                            if (!set || set.size === 0) return;

                            var src = meshInfo.object;
                            var srcGeom = src.geometry;
                            var srcIndex = srcGeom.index;
                            var newIndex = new Uint32Array(set.size * 3);
                            var i = 0;
                            set.forEach(function (tri) {
                                newIndex[i * 3] = srcIndex.getX(tri * 3);
                                newIndex[i * 3 + 1] = srcIndex.getX(tri * 3 + 1);
                                newIndex[i * 3 + 2] = srcIndex.getX(tri * 3 + 2);
                                i++;
                            });

                            var overlayGeom = new THREE.BufferGeometry();
                            overlayGeom.setAttribute('position', srcGeom.attributes.position);
                            if (src.isSkinnedMesh) {
                                overlayGeom.setAttribute('skinIndex', srcGeom.attributes.skinIndex);
                                overlayGeom.setAttribute('skinWeight', srcGeom.attributes.skinWeight);
                            }
                            overlayGeom.setIndex(new THREE.BufferAttribute(newIndex, 1));
                            var overlayMesh = makeSkinnedCopy(src, overlayGeom, highlightMaterial);
                            overlayGroup.add(overlayMesh);
                        });
                    }

                    function pushSelectionCount() {
                        var total = 0;
                        Object.keys(selection).forEach(function (k) { total += selection[k].size; });
                        if (window.chrome && window.chrome.webview) {
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'selectionChanged', count: total }));
                        }
                    }

                    window.setPaintMode = function (enabled) {
                        paintMode = enabled;
                        if (!enabled) { painting = false; hideBrushCursor(); }
                    };

                    window.setBrushRadius = function (fraction) {
                        brushFraction = fraction;
                        resolveBrushRadius();
                    };

                    window.clearPaintSelection = function () {
                        selection = {};
                        rebuildOverlay();
                        pushSelectionCount();
                    };

                    window.getPaintSelection = function () {
                        var result = {};
                        Object.keys(selection).forEach(function (k) { result[k] = Array.from(selection[k]); });
                        return result;
                    };

                    var lastOverlayUpdate = 0;

                    function pickSurface(event) {
                        if (typeof paintViewer.positionAndNormalFromPoint !== 'function') return null;
                        var rect = paintViewer.getBoundingClientRect();
                        return paintViewer.positionAndNormalFromPoint(event.clientX - rect.left, event.clientY - rect.top);
                    }

                    function pickAndPaint(event) {
                        var hit = pickSurface(event);
                        if (!hit) { hideBrushCursor(); return false; }

                        var point = new THREE.Vector3(hit.position.x, hit.position.y, hit.position.z);
                        showBrushCursor(point);

                        var erase = event.ctrlKey || (event.buttons & 2) !== 0;
                        if (paintAllInBrush(point, erase)) {
                            var now = performance.now();
                            if (now - lastOverlayUpdate > 50) {
                                rebuildOverlay();
                                pushSelectionCount();
                                lastOverlayUpdate = now;
                            }
                        }
                        return true;
                    }

                    paintViewer.addEventListener('pointerdown', function (event) {
                        if (!paintMode || event.button === 1) return;
                        if (pickAndPaint(event)) {
                            painting = true;
                            event.stopPropagation();
                            event.preventDefault();
                        }
                    }, true);
                    paintViewer.addEventListener('pointermove', function (event) {
                        if (!paintMode) return;
                        if (painting) { pickAndPaint(event); return; }
                        var hit = pickSurface(event);
                        if (!hit) { hideBrushCursor(); return; }
                        showBrushCursor(new THREE.Vector3(hit.position.x, hit.position.y, hit.position.z));
                    });
                    paintViewer.addEventListener('pointerleave', function () {
                        if (!painting) hideBrushCursor();
                    });
                    window.addEventListener('pointerup', function () {
                        if (painting) {
                            painting = false;
                            rebuildOverlay();
                            pushSelectionCount();
                        }
                    });
                    paintViewer.addEventListener('contextmenu', function (event) {
                        if (paintMode) event.preventDefault();
                    });

                    var hitTestLoader = new THREE.GLTFLoader();
                    function loadHitTestGeometry(url) {
                        hitTestLoader.load(url, function (gltf) {
                            gltf.scene.updateMatrixWorld(true);
                            var meshes = [];
                            gltf.scene.traverse(function (obj) {
                                if (!obj.isMesh || !obj.userData || obj.userData.glbMergerMeshIndex === undefined) return;
                                var meshIndex = obj.userData.glbMergerMeshIndex;
                                var primIndex = 0;
                                var parent = obj.parent;
                                if (parent && parent.children.length > 1 && parent.children.every(function (c) {
                                    return c.userData && c.userData.glbMergerMeshIndex === meshIndex;
                                })) {
                                    primIndex = parent.children.indexOf(obj);
                                }
                                var triData = computeTriangleData(obj);
                                meshes.push({
                                    object: obj, meshIndex: meshIndex, primIndex: primIndex,
                                    centroids: triData.centroids,
                                    radii: triData.radii,
                                    normals: triData.normals,
                                });
                            });
                            paintableMeshes = meshes;
                            rebuildDepthMask();
                            rebuildBrushDecal();
                            rebuildOverlay();
                            rebuildWireframe();
                            rebuildRigidityColors();

                            var box = new THREE.Box3().setFromObject(gltf.scene);
                            var size = box.getSize(new THREE.Vector3());
                            modelMaxDim = Math.max(size.x, size.y, size.z) || 1;
                            resolveBrushRadius();
                        }, undefined, function (error) {
                            console.error('Failed to load paint hit-test geometry: ' + (error && error.message ? error.message : error));
                        });
                    }

                    // Called after .NET rewrites skin weights (Apply / Remove Rigidity / Revert)
                    // and hands back a freshly written GLB - reloads just the hit-test copy this
                    // paint tool (and the rigidity colouring) runs against. The visible <model-
                    // viewer> pane is deliberately left alone: bind pose looks identical before and
                    // after a skin-weight edit, so there is nothing there to refresh, and the
                    // painted selection - recorded by triangle index, which a skin-weight edit
                    // never changes - stays valid across the reload.
                    window.reloadPaintGeometry = function (url) {
                        loadHitTestGeometry(url);
                    };

                    paintViewer.addEventListener('load', function (e) {
                        fixMaterials(e.target);
                        loadHitTestGeometry(paintViewer.src);
                        window.chrome && window.chrome.webview &&
                            window.chrome.webview.postMessage(JSON.stringify({ action: 'paintReady' }));
                    });

                    window.addEventListener('resize', function () {
                        var size = paneSize();
                        overlayRenderer.setSize(size.w, size.h);
                    });
                    // --- end paint-a-region tool -------------------------------------------------
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(html);
        }

        private string WriteGlbFile(ModelRoot model, ref string? slotPath)
        {
            var previous = slotPath;
            string path = Path.Combine(Path.GetTempPath(), $"glbmerger_rigidregion_preview_{_glbVersion++}.glb");
            model.SaveGLB(path);
            slotPath = path;

            if (previous != null)
            {
                try { File.Delete(previous); }
                catch (IOException) { /* still held by the browser - harmless, it's a temp file */ }
                catch (UnauthorizedAccessException) { }
            }

            return Path.GetFileName(path);
        }

        // Tags every mesh with its own LogicalMeshes index via glTF extras, purely so the paint
        // tool's raycast hits (against the Paint pane's Three.js objects) can be mapped back to a
        // (meshIndex, primIndex) pair - same tagging/lookup convention TextureEditorEditor and
        // GeometryOptimizerEditor's paint tools use. Extras are cleared again immediately after
        // saving - this tag has no business surviving into the real output file.
        private string WriteTaggedGlbFile(ModelRoot model, ref string? slotPath)
        {
            var previousExtras = new JsonNode?[model.LogicalMeshes.Count];
            for (int i = 0; i < model.LogicalMeshes.Count; i++)
            {
                previousExtras[i] = model.LogicalMeshes[i].Extras;
                model.LogicalMeshes[i].Extras = new JsonObject { ["glbMergerMeshIndex"] = i };
            }
            try
            {
                return WriteGlbFile(model, ref slotPath);
            }
            finally
            {
                for (int i = 0; i < model.LogicalMeshes.Count; i++)
                    model.LogicalMeshes[i].Extras = previousExtras[i]!;
            }
        }

        // Refreshes the animated preview pane from the model's current committed state - called
        // after Apply/Revert so the pane reflects whatever the skin weights actually are now.
        // Bind pose is visually unaffected by a skin-weight edit (all bones sit at their bind
        // transform there), so the Paint pane deliberately does NOT reload here - its hit-test
        // geometry and selection stay valid across an Apply.
        private void RefreshPreviewViewer()
        {
            string fileName = WriteGlbFile(_model, ref _previewGlbPath);
            PushSrc("setPreviewSrc", fileName);
        }

        // Re-syncs both 3D panes after any skin-weight edit, and re-derives which of the two
        // un-pinning buttons have anything left to act on.
        private void RefreshAfterSkinEdit()
        {
            _btnRevert.Enabled = _originalSkin.Count > 0;
            _btnRemoveRigidity.Enabled = _originalSkin.Count > 0;

            _paintGeometryStale = true;
            if (_rigidityColors) RefreshPaintGeometry();

            RefreshPreviewViewer();
        }

        // Re-reads the Paint pane's hit-test copy from the model's current state. The pane's
        // <model-viewer> deliberately keeps showing the GLB it loaded at startup (a skin-weight
        // edit doesn't move the bind pose, so there is nothing new to see there) - it is the
        // hit-test copy alone that has to catch up, because the rigidity colouring is computed
        // from its JOINTS_0/WEIGHTS_0. Those two accessors are also the ONLY thing an edit here
        // ever rewrites, so triangle indexing is untouched and the paint selection stays valid
        // across the reload.
        private void RefreshPaintGeometry()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;

            _currentPaintFileName = WriteTaggedGlbFile(_model, ref _paintGlbPath);
            _paintGeometryStale = false;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"reloadPaintGeometry('https://appassets.local/{EscapeJs(_currentPaintFileName)}');");
        }

        private void PushSrc(string jsFunction, string fileName)
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"document.getElementById('preview').src = 'https://appassets.local/{EscapeJs(fileName)}';");
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

            if (message == null || IsDisposed) return;

            if (message.Action == "paintReady")
            {
                _viewerReady = true;
                PushPaintMode();
                PushBrushRadius();
                PushWireframe();
                PushRigidityColors();
                return;
            }

            if (message.Action == "previewReady")
            {
                PushAnimationState();
                return;
            }

            if (message.Action == "selectionChanged")
            {
                int count = message.Count ?? 0;
                _lblSelection.Text = count == 1 ? "1 triangle painted" : $"{count:N0} triangles painted";
            }
        }

        private void PushPaintMode()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintMode({(_chkPaintMode.Checked ? "true" : "false")});");
        }

        private void PushBrushRadius()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            float fraction = _sliderBrush.Value / 100f;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setBrushRadius({fraction.ToString(CultureInfo.InvariantCulture)});");
        }

        private void PushWireframe()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setWireframe({(_chkWireframe.Checked ? "true" : "false")});");
        }

        private void ToggleRigidityColors()
        {
            _rigidityColors = !_rigidityColors;
            _btnColorize.Text = _rigidityColors ? "Hide Rigidity Colors" : "Colorize by Rigidity";

            // Switching it on is the first moment the colouring is worth paying for, so this is
            // where a reload deferred by an earlier Apply/Remove/Revert is settled.
            if (_rigidityColors && _paintGeometryStale) RefreshPaintGeometry();
            PushRigidityColors();
        }

        private void PushRigidityColors()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setRigidityColors({(_rigidityColors ? "true" : "false")});");
        }

        private void PushAnimationState()
        {
            if (_webView.CoreWebView2 == null) return;
            var name = CurrentAnimationName();
            string js = name == null
                ? "setPreviewAnimation(null, false);"
                : $"setPreviewAnimation('{EscapeJs(name)}', {(_chkPlaying.Checked ? "true" : "false")});";
            _ = _webView.CoreWebView2.ExecuteScriptAsync(js);
        }

        private void Seek()
        {
            if (_webView.CoreWebView2 == null) return;
            float duration = CurrentAnimationDuration();
            if (duration <= 0f) return;
            float t = _sliderScrub.Value / 1000f * duration;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"seekPreview({t.ToString(CultureInfo.InvariantCulture)});");
        }

        private void ClearSelection()
        {
            _lblSelection.Text = "0 triangles painted";
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync("clearPaintSelection();");
        }

        // Reads the paint tool's current selection back from the JS side. ExecuteScriptAsync
        // JSON-encodes whatever the script evaluates to, so a script that itself returns
        // JSON.stringify(...) comes back as a JSON string *literal* - it has to be unwrapped once
        // before the selection object inside it can be parsed. Returns null (nothing painted)
        // when the viewer isn't ready yet or nothing is painted.
        private async Task<Dictionary<(int MeshIndex, int PrimitiveIndex), HashSet<int>>?> ReadSelectionAsync()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return null;

            string outer;
            try
            {
                outer = await _webView.CoreWebView2.ExecuteScriptAsync("JSON.stringify(getPaintSelection())");
            }
            catch
            {
                return null;
            }
            if (IsDisposed) return null;

            string inner = JsonSerializer.Deserialize<string>(outer) ?? "{}";
            var raw = JsonSerializer.Deserialize<Dictionary<string, int[]>>(inner) ?? new Dictionary<string, int[]>();

            var result = new Dictionary<(int, int), HashSet<int>>();
            foreach (var (key, tris) in raw)
            {
                var parts = key.Split('_');
                if (parts.Length != 2) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int meshIdx)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int primIdx)) continue;
                if (tris.Length == 0) continue;
                result[(meshIdx, primIdx)] = new HashSet<int>(tris);
            }
            return result.Count > 0 ? result : null;
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public int? Count { get; set; }
        }
    }
}
