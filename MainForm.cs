using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    public class MainForm : Form
    {
        private Button btnFile1, btnClear, btnMerge, btnSave, btnViewResult, btnFixJoints, btnBallAnchor, btnStiffArm, btnSetHeight;
        private NumericUpDown numModelHeightIn;
        private Label lblStatus;
        private const float InchesToMeters = 0.0254f;
        private GlbInfoPanel panel1, panel2;
        private CheckBox chkDarkMode;
        private SplitContainer splitter;
        private readonly AppSettings _settings;
        private string? path1, path2; // GLB path for a slot; null if that slot has no GLB loaded
        private FbxAnimationSource? fbxAnims1; // slot 1's single FBX source, if it holds one
        private List<FbxAnimationSource> fbxAnimsList2 = new(); // slot 2 accumulates any number of dropped FBX sources
        private SharpGLTF.Schema2.ModelRoot? latestMergedModel; // in-memory result of the last "Process Merge" - not yet saved anywhere the user chose
        private string? latestPreviewPath; // scratch copy of latestMergedModel, used only so the viewer has a file to load

        public MainForm()
        {
            _settings = AppSettings.Load();

            Text = "GLB Texture & Animation Merger";
            Width = 850;
            Height = 650;
            MinimumSize = new System.Drawing.Size(600, 450);

            // A TableLayoutPanel (button row on top of the status label) with a FlowLayoutPanel
            // for the buttons themselves - both AutoSize, so the whole toolbar's real height and
            // each button's real width/position are resolved by the layout engine every time it
            // runs, not computed once from a snapshot taken mid-construction. Manually computing
            // Left/Top from AutoSize buttons' Width/Height in the constructor (the previous
            // approach) read those values before the control tree was parented and DPI/font
            // auto-scaling had run, so the numbers baked into Left/Top went stale as soon as
            // scaling changed the buttons' real size afterward - which is what was still
            // overlapping. Docked/flowed layout has no such snapshot to go stale.
            var toolbar = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                RowCount = 2,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(8),
            };

            var buttonRow = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
            };

            static Button MakeToolbarButton(string text, bool enabled = true) => new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(0, 0, 8, 0),
                Text = text,
                Enabled = enabled
            };
            btnFile1 = MakeToolbarButton("Load Model 1");
            btnClear = MakeToolbarButton("Clear");
            btnMerge = MakeToolbarButton("Process Merge", enabled: false);
            btnSave = MakeToolbarButton("Save...", enabled: false);
            btnViewResult = MakeToolbarButton("🖥️ Open Model Viewer", enabled: false);
            btnFixJoints = MakeToolbarButton("Fix Joint Orientation...", enabled: false);
            btnBallAnchor = MakeToolbarButton("Ball Anchor...", enabled: false);
            btnStiffArm = MakeToolbarButton("Stiff Arm Poses...", enabled: false);

            var lblHeight = new Label
            {
                AutoSize = true,
                Text = "Height (in):",
                Margin = new Padding(8, 8, 4, 0),
            };
            numModelHeightIn = new NumericUpDown
            {
                Width = 65,
                Margin = new Padding(0, 4, 4, 0),
                Minimum = 1m,
                Maximum = 999m,
                DecimalPlaces = 1,
                Increment = 0.5m,
                Value = 70m,
            };
            btnSetHeight = MakeToolbarButton("Set Height", enabled: false);

            buttonRow.Controls.AddRange(new Control[] { btnFile1, btnClear, btnMerge, btnSave, btnViewResult, btnFixJoints, btnBallAnchor, btnStiffArm, lblHeight, numModelHeightIn, btnSetHeight });

            lblStatus = new Label { AutoSize = false, Width = 700, Height = 20, Margin = new Padding(0, 6, 0, 0), AutoEllipsis = true };

            toolbar.Controls.Add(buttonRow, 0, 0);
            toolbar.Controls.Add(lblStatus, 0, 1);

            // Bottom-right dark mode toggle, in its own strip so it stays clear of the resizable
            // boxes above it. Positioned manually (not just Anchor) so it tracks the right edge
            // immediately even before the form has laid out once.
            var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 30 };
            chkDarkMode = new CheckBox { Text = "Dark Mode", AutoSize = true, Checked = _settings.DarkMode };
            void PositionDarkModeCheckbox(object? s, EventArgs e)
            {
                chkDarkMode.Left = bottomBar.Width - chkDarkMode.Width - 12;
                chkDarkMode.Top = (bottomBar.Height - chkDarkMode.Height) / 2;
            }
            bottomBar.Resize += PositionDarkModeCheckbox;
            bottomBar.Controls.Add(chkDarkMode);
            chkDarkMode.CheckedChanged += (s, e) =>
            {
                _settings.DarkMode = chkDarkMode.Checked;
                ThemeManager.Apply(this, chkDarkMode.Checked);
            };

            splitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };
            // Panel1MinSize/Panel2MinSize default to 25, which is all that's needed to stop a
            // drag from collapsing a side entirely - deliberately not raised further here, since
            // setting a larger min before the control has been sized/parented (its unparented
            // default width is ~150) throws "SplitterDistance must be between Panel1MinSize and
            // Width - Panel2MinSize".

            panel1 = new GlbInfoPanel { Dock = DockStyle.Fill, ShowAnimationCheckboxes = true };
            panel1.UpdateTitle("Model 1 (geometry, materials, animations)");

            panel2 = new GlbInfoPanel(showGeometryBox: false) { Dock = DockStyle.Fill, ShowAnimationCheckboxes = true };
            panel2.UpdateTitle("Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");
            panel2.EnableDropTargets();
            panel2.GlbFileDropped += async path => await LoadGlbIntoSlot2(path);
            panel2.FbxFilesDropped += async paths => await LoadFbxFilesIntoSlot2(paths);

            splitter.Panel1.Controls.Add(panel1);
            splitter.Panel2.Controls.Add(panel2);

            // Deferred to Shown rather than applied here: nested Dock=Fill layout can still shift
            // sizes several times between construction and the window's first real paint (each
            // ancestor being parented/docked nudges things again), and a SplitContainer's
            // SizeChanged can fire against one of those in-between sizes rather than the final
            // one. By Shown, the whole tree has gone through its real layout pass at least once,
            // so restoring here (rather than piecemeal during construction) is what actually lands
            // on the intended proportions instead of drifting toward a default ~50/50.
            Shown += (s, e) =>
            {
                panel1.PrimarySplitFraction = _settings.Panel1PrimarySplitFraction;
                panel1.SecondarySplitFraction = _settings.Panel1SecondarySplitFraction;
                panel2.PrimarySplitFraction = _settings.Panel2PrimarySplitFraction;
                SplitterFractionPersistence.ApplyFraction(splitter, _settings.MainSplitFraction);
            };

            btnFile1.Click += async (s, e) => await PickModel1();
            btnClear.Click += (s, e) => ClearAll();
            btnMerge.Click += OnProcessMerge;
            btnSave.Click += OnSave;

            btnViewResult.Click += (s, e) => {
                if (!string.IsNullOrEmpty(latestPreviewPath) && File.Exists(latestPreviewPath)) {
                    using var viewer = new ViewerForm(latestPreviewPath, _settings.DarkMode);
                    viewer.ShowDialog(this);
                }
            };

            // Corrections apply directly to latestMergedModel in place, so closing this dialog
            // needs no extra "commit" step - Save already picks up whatever was changed. The
            // on-disk preview file is regenerated too, so "Open Model Viewer" reflects the
            // corrected result as well, not just Save.
            btnFixJoints.Click += (s, e) => {
                if (latestMergedModel == null || latestPreviewPath == null) return;
                using var jointForm = new JointOrientationForm(latestMergedModel, _settings.DarkMode);
                jointForm.ShowDialog(this);
                latestMergedModel.SaveGLB(latestPreviewPath);
                lblStatus.Text = "Joint corrections applied.";
            };

            // Same in-place-edit pattern as Fix Joint Orientation above: the
            // form writes directly to latestMergedModel, so closing it just
            // needs the preview file regenerated to reflect what changed.
            btnBallAnchor.Click += (s, e) => {
                if (latestMergedModel == null || latestPreviewPath == null) return;
                using var anchorForm = new BallAnchorForm(latestMergedModel, _settings.DarkMode);
                anchorForm.ShowDialog(this);
                latestMergedModel.SaveGLB(latestPreviewPath);
                lblStatus.Text = "Ball anchor updated.";
            };

            btnStiffArm.Click += (s, e) => {
                if (latestMergedModel == null || latestPreviewPath == null) return;
                using var stiffArmForm = new StiffArmPoseForm(latestMergedModel, _settings.DarkMode);
                stiffArmForm.ShowDialog(this);
                latestMergedModel.SaveGLB(latestPreviewPath);
                lblStatus.Text = "Stiff-arm poses updated.";
            };

            btnSetHeight.Click += (s, e) => OnSetModelHeight();

            Controls.Add(splitter);
            Controls.Add(toolbar);
            Controls.Add(bottomBar);
            PositionDarkModeCheckbox(this, EventArgs.Empty);

            ThemeManager.Apply(this, _settings.DarkMode);

            FormClosing += (s, e) => SavePreferences();
        }

        // Fractions are only meaningful once each SplitContainer has real size, so this reads
        // back whatever the user (or the startup restore) last settled on rather than anything
        // computed ahead of time.
        private void SavePreferences()
        {
            _settings.DarkMode = chkDarkMode.Checked;
            _settings.MainSplitFraction = SplitterFractionPersistence.GetFraction(splitter);
            _settings.Panel1PrimarySplitFraction = panel1.PrimarySplitFraction;
            if (panel1.SecondarySplitFraction is double p1s) _settings.Panel1SecondarySplitFraction = p1s;
            _settings.Panel2PrimarySplitFraction = panel2.PrimarySplitFraction;
            _settings.Save();
        }

        // Resets the form to exactly how it looks right after opening: both slots' loaded
        // files/animations forgotten, both panels wiped back to their default titles/empty
        // grids, and every button re-disabled until the user loads something again. The scratch
        // preview file on disk is deliberately left alone - it's regenerated (or simply
        // overwritten) the next time Process Merge runs, so there's nothing to clean up here.
        private void ClearAll()
        {
            path1 = null;
            path2 = null;
            fbxAnims1 = null;
            fbxAnimsList2 = new List<FbxAnimationSource>();
            latestMergedModel = null;
            latestPreviewPath = null;

            panel1.Reset("Model 1 (geometry, materials, animations)");
            panel2.Reset("Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");

            btnMerge.Enabled = false;
            btnSave.Enabled = false;
            btnViewResult.Enabled = false;
            btnFixJoints.Enabled = false;
            btnBallAnchor.Enabled = false;
            btnStiffArm.Enabled = false;
            btnSetHeight.Enabled = false;
            numModelHeightIn.Value = 70m;

            lblStatus.Text = "Cleared.";
        }

        // Slot 1 is always a single GLB or FBX picked via dialog, and always supplies the merged
        // output's geometry - so loading a new file here is exclusive, same as before.
        private async Task PickModel1()
        {
            using var dlg = new OpenFileDialog { Filter = "3D Models|*.glb;*.fbx|GLB Files|*.glb|FBX Files|*.fbx" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            bool isFbx = string.Equals(Path.GetExtension(dlg.FileName), ".fbx", StringComparison.OrdinalIgnoreCase);

            SetBusy(true, $"Loading {Path.GetFileName(dlg.FileName)}...");
            try
            {
                if (isFbx)
                {
                    var source = await Task.Run(() => FbxImportService.ExtractAnimationClips(dlg.FileName));
                    var uniqueNames = new HashSet<string>();
                    var renamed = RenameClipsToUniqueNames(source, dlg.FileName, uniqueNames);

                    path1 = null;
                    fbxAnims1 = renamed;
                    panel1.UpdateTitle($"Model 1: {Path.GetFileName(dlg.FileName)} (FBX - animation only)");
                    panel1.LoadFbxAnimations(renamed.Clips.Select(c => c.Name).ToList());
                }
                else
                {
                    var glbPath = dlg.FileName;
                    await Task.Run(() => { }); // keep the busy indicator visible for at least one yield
                    path1 = glbPath;
                    fbxAnims1 = null;
                    panel1.UpdateTitle($"Model 1: {Path.GetFileName(dlg.FileName)}");
                    panel1.LoadModel(glbPath);
                    UpdateHeightBoxFromGlb(glbPath);
                }

                lblStatus.Text = $"Loaded {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show($"Failed to load '{Path.GetFileName(dlg.FileName)}':\n{ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        // Slot 2's GLB (dropped onto the materials list) and FBX files (dropped onto the
        // animations grid, all at once or one after another) are independent of each other -
        // loading one never clears the other, since a GLB dropped for its materials might have no
        // useful animation, or the user might want to mix a GLB's own baked animations with any
        // number of supplemental FBX retargets.
        private async Task LoadGlbIntoSlot2(string glbPath)
        {
            SetBusy(true, $"Loading {Path.GetFileName(glbPath)}...");
            try
            {
                await Task.Run(() => { });
                path2 = glbPath;
                UpdateSlot2Title();
                panel2.LoadMaterialsFromGlb(glbPath);
                lblStatus.Text = $"Loaded {Path.GetFileName(glbPath)} into Model 2 materials";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show($"Failed to load '{Path.GetFileName(glbPath)}':\n{ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        // Accumulates every dropped FBX (whether dropped together in one gesture or across
        // several separate drops) into fbxAnimsList2, instead of replacing whatever was already
        // there - each FBX's clips get a unique name (based on its filename) that can't collide
        // with anything already loaded into this slot, so they all merge cleanly together.
        private async Task LoadFbxFilesIntoSlot2(List<string> fbxPaths)
        {
            SetBusy(true, $"Loading {fbxPaths.Count} FBX file(s)...");
            try
            {
                var usedNames = new HashSet<string>(panel2.GetAllAnimationNames());
                var newlyAddedNames = new List<string>();

                foreach (var fbxPath in fbxPaths)
                {
                    var source = await Task.Run(() => FbxImportService.ExtractAnimationClips(fbxPath));
                    var renamed = RenameClipsToUniqueNames(source, fbxPath, usedNames);

                    fbxAnimsList2.Add(renamed);
                    newlyAddedNames.AddRange(renamed.Clips.Select(c => c.Name));
                }

                UpdateSlot2Title();
                panel2.AddSupplementalFbxAnimations(newlyAddedNames);
                lblStatus.Text = $"Loaded {fbxPaths.Count} FBX file(s) into Model 2 animations";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show($"Failed to load one or more FBX files:\n{ex.Message}", "Import Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false, null);
            }
        }

        private void UpdateSlot2Title()
        {
            var parts = new List<string>();
            if (path2 != null) parts.Add(Path.GetFileName(path2));
            if (fbxAnimsList2.Count > 0) parts.Add($"{fbxAnimsList2.Count} FBX animation source(s)");
            panel2.UpdateTitle(parts.Count > 0
                ? "Model 2: " + string.Join(" + ", parts)
                : "Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");
        }

        // An FBX's own internal clip name is often generic (e.g. Mixamo's literal "mixamo.com"),
        // which collides across different downloaded FBX files - so every clip gets renamed to a
        // name derived from its FBX's filename (spaces -> underscores) instead, disambiguated
        // against usedNames (already-loaded clips, plus everything renamed so far in this same
        // batch) so multiple FBX files can never produce the same clip identity.
        private static FbxAnimationSource RenameClipsToUniqueNames(FbxAnimationSource source, string fbxPath, HashSet<string> usedNames)
        {
            var baseName = Path.GetFileNameWithoutExtension(fbxPath).Replace(' ', '_');
            var multiClip = source.Clips.Count > 1;

            var renamedClips = new List<AnimationClipData>();
            for (int i = 0; i < source.Clips.Count; i++)
            {
                var candidate = multiClip ? $"{baseName}_{i + 1}" : baseName;
                var uniqueName = MakeUnique(candidate, usedNames);

                var renamed = new AnimationClipData { Name = uniqueName };
                renamed.NodeChannels.AddRange(source.Clips[i].NodeChannels);
                renamedClips.Add(renamed);
            }

            return new FbxAnimationSource { Clips = renamedClips, RootTranslationCorrection = source.RootTranslationCorrection };
        }

        private static string MakeUnique(string candidate, HashSet<string> usedNames)
        {
            if (usedNames.Add(candidate)) return candidate;

            int i = 2;
            while (!usedNames.Add($"{candidate}_{i}")) i++;
            return $"{candidate}_{i}";
        }

        private void SetBusy(bool busy, string? statusText)
        {
            btnFile1.Enabled = !busy;
            btnMerge.Enabled = !busy && CanMerge();
            // Loading a new file invalidates whatever was last processed, so Save/View shouldn't
            // offer up a stale result while busy - once done, they stay off until Process Merge
            // runs again.
            if (busy) { btnSave.Enabled = false; btnViewResult.Enabled = false; btnFixJoints.Enabled = false; btnBallAnchor.Enabled = false; btnStiffArm.Enabled = false; btnSetHeight.Enabled = false; }
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (statusText != null) lblStatus.Text = statusText;
        }

        // Model 1 always supplies geometry, so it's the only thing strictly required to merge.
        private bool CanMerge() => path1 != null;

        // Builds the merge in memory and stages a scratch copy for the viewer, without asking
        // where to save it - "Save..." handles that as its own separate step.
        private void OnProcessMerge(object? sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Processing targeted composite...";
                Application.DoEvents();

                bool loaded2 = path2 != null || fbxAnimsList2.Count > 0;

                var selectedTextures1 = panel1.GetSelectedMaterialNames();
                var selectedAnims1 = panel1.GetSelectedAnimationNames();
                var inPlaceAnims1 = panel1.GetInPlaceAnimationNames();
                var groundFixAnims1 = panel1.GetGroundFixAnimationNames();
                var yRotationAnims1 = panel1.GetYRotationByAnimation();
                var yOffsetAnims1 = panel1.GetYOffsetByAnimation();
                var renameMap1 = panel1.GetAnimationRenameMap();
                var matRenameMap1 = panel1.GetMaterialRenameMap();
                var geomRenameMap1 = panel1.GetGeometryRenameMap();
                var firstMat1 = panel1.GetFirstMaterialName();
                var firstAnim1 = panel1.GetFirstAnimationName();

                var selectedTextures2 = loaded2 ? panel2.GetSelectedMaterialNames() : new List<string>();
                var selectedAnims2 = loaded2 ? panel2.GetSelectedAnimationNames() : new List<string>();
                var inPlaceAnims2 = loaded2 ? panel2.GetInPlaceAnimationNames() : new List<string>();
                var groundFixAnims2 = loaded2 ? panel2.GetGroundFixAnimationNames() : new List<string>();
                var yRotationAnims2 = loaded2 ? panel2.GetYRotationByAnimation() : new Dictionary<string, float>();
                var yOffsetAnims2 = loaded2 ? panel2.GetYOffsetByAnimation() : new Dictionary<string, float>();
                var renameMap2 = loaded2 ? panel2.GetAnimationRenameMap() : new Dictionary<string, string>();
                var matRenameMap2 = loaded2 ? panel2.GetMaterialRenameMap() : new Dictionary<string, string>();
                var firstMat2 = loaded2 ? panel2.GetFirstMaterialName() : null;
                var firstAnim2 = loaded2 ? panel2.GetFirstAnimationName() : null;

                latestMergedModel = GlbMergeService.MergeTargeted(
                    path1, selectedTextures1, selectedAnims1, inPlaceAnims1, fbxAnims1,
                    path2, selectedTextures2, selectedAnims2, inPlaceAnims2, fbxAnimsList2,
                    renameMap1, renameMap2,
                    groundFixAnims1, groundFixAnims2,
                    yRotationAnims1, yRotationAnims2, yOffsetAnims1, yOffsetAnims2,
                    matRenameMap1, matRenameMap2,
                    firstMat1, firstMat2, firstAnim1, firstAnim2,
                    geomRenameMap1: geomRenameMap1);

                latestPreviewPath = Path.Combine(Path.GetTempPath(), "glbmerger_preview.glb");
                latestMergedModel.SaveGLB(latestPreviewPath);

                btnSave.Enabled = true;
                btnViewResult.Enabled = true;
                btnFixJoints.Enabled = true;
                btnBallAnchor.Enabled = true;
                btnStiffArm.Enabled = true;
                btnSetHeight.Enabled = true;
                lblStatus.Text = "Merge processed - use View or Save.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                latestMergedModel = null;
                btnSave.Enabled = false;
                btnViewResult.Enabled = false;
                btnFixJoints.Enabled = false;
                btnBallAnchor.Enabled = false;
                btnStiffArm.Enabled = false;
                btnSetHeight.Enabled = false;
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Rescales the whole merged model (mesh + skeleton together, via each
        // DefaultScene root node's own local transform) so its rest-pose
        // bounding-box height matches the requested inches value. Scaling at
        // the root - rather than per-mesh - keeps a skinned mesh's world-space
        // vertices consistent with its skeleton, since skin matrices are
        // joint-world-matrix * inverse-bind-matrix: a uniform scale applied
        // above both the mesh and the skeleton root scales that product
        // correctly without needing to touch inverse bind matrices at all.
        private void OnSetModelHeight()
        {
            if (latestMergedModel == null || latestPreviewPath == null) return;

            try
            {
                float currentHeightMeters = ComputeModelHeightMeters(latestMergedModel);
                if (currentHeightMeters <= 0f)
                {
                    MessageBox.Show(this, "Could not measure the current model height (no mesh geometry found).",
                        "Set Height", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                float desiredMeters = (float)numModelHeightIn.Value * InchesToMeters;
                float factor = desiredMeters / currentHeightMeters;

                // A skinned mesh instance's own node is required (by the glTF spec, enforced by
                // SharpGLTF) to carry an identity local transform - its vertices are positioned
                // entirely by the skin's joint matrices instead. Scaling it directly throws
                // ArgumentException("Node instances with a Skin must not contain spatial
                // transformations."). Skipping it is safe: scaling its skeleton's root bone (a
                // sibling root node here, not skinned itself) already scales every joint's world
                // matrix, and thus the skinned mesh, by the same factor.
                foreach (var node in latestMergedModel.DefaultScene.VisualChildren)
                {
                    if (node.Skin != null) continue;
                    node.LocalMatrix = node.LocalMatrix * Matrix4x4.CreateScale(factor);
                }

                latestMergedModel.SaveGLB(latestPreviewPath);
                lblStatus.Text = $"Model rescaled to {numModelHeightIn.Value} in (x{factor:0.###}).";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(this, ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Rest-pose height. For a rigid (unskinned) mesh, that's just its
        // node's own WorldMatrix applied to POSITION. For a skinned mesh the
        // node's WorldMatrix is meaningless - per the glTF spec a skinned
        // mesh node must carry an identity local transform (see
        // OnSetModelHeight), so its vertices are positioned entirely by the
        // skin instead: each vertex's world position is the weighted blend,
        // over its up-to-4 joints, of (inverseBindMatrix * jointWorldMatrix).
        // This is what actually responds to scaling the skeleton root, so
        // it's what has to be measured for the "before" and "after" of a
        // Set Height rescale to agree with each other - a plain node.WorldMatrix
        // read here would report the same bind-space height regardless of
        // how the skeleton was scaled.
        private static float ComputeModelHeightMeters(ModelRoot model)
        {
            float min = float.MaxValue, max = float.MinValue;
            bool any = false;

            void Accumulate(Vector3 worldPos)
            {
                if (worldPos.Y < min) min = worldPos.Y;
                if (worldPos.Y > max) max = worldPos.Y;
                any = true;
            }

            foreach (var node in model.LogicalNodes)
            {
                if (node.Mesh == null) continue;

                if (node.Skin != null)
                {
                    var skin = node.Skin;
                    var skinMatrices = new Matrix4x4[skin.JointsCount];
                    for (int i = 0; i < skin.JointsCount; i++)
                    {
                        var (jointNode, invBind) = skin.GetJoint(i);
                        skinMatrices[i] = invBind * jointNode.WorldMatrix;
                    }

                    foreach (var prim in node.Mesh.Primitives)
                    {
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue("JOINTS_0", out var jAcc)) continue;
                        if (!prim.VertexAccessors.TryGetValue("WEIGHTS_0", out var wAcc)) continue;

                        var positions = posAcc.AsVector3Array();
                        var joints = jAcc.AsVector4Array();
                        var weights = wAcc.AsVector4Array();

                        for (int i = 0; i < positions.Count; i++)
                        {
                            var p = positions[i];
                            var j = joints[i];
                            var w = weights[i];
                            var blended =
                                Vector3.Transform(p, skinMatrices[(int)j.X]) * w.X +
                                Vector3.Transform(p, skinMatrices[(int)j.Y]) * w.Y +
                                Vector3.Transform(p, skinMatrices[(int)j.Z]) * w.Z +
                                Vector3.Transform(p, skinMatrices[(int)j.W]) * w.W;
                            Accumulate(blended);
                        }
                    }
                }
                else
                {
                    var world = node.WorldMatrix;
                    foreach (var prim in node.Mesh.Primitives)
                    {
                        if (!prim.VertexAccessors.TryGetValue("POSITION", out var posAcc)) continue;
                        foreach (var p in posAcc.AsVector3Array())
                            Accumulate(Vector3.Transform(p, world));
                    }
                }
            }

            return any ? (max - min) : 0f;
        }

        // Loads Model 1's freshly-picked GLB a second time (separately from panel1.LoadModel's
        // own internal load, which doesn't expose the ModelRoot it reads) purely to measure its
        // rest-pose height and seed the box with it - so the field starts at "what this model
        // actually is" instead of always defaulting to 70". Best-effort: a measurement failure
        // here shouldn't block the load that already succeeded, so it's swallowed rather than
        // surfaced as an error.
        private void UpdateHeightBoxFromGlb(string glbPath)
        {
            try
            {
                var model = ModelRoot.Load(glbPath);
                float heightMeters = ComputeModelHeightMeters(model);
                if (heightMeters <= 0f) return;

                decimal heightInches = (decimal)(heightMeters / InchesToMeters);
                numModelHeightIn.Value = Math.Clamp(heightInches, numModelHeightIn.Minimum, numModelHeightIn.Maximum);
            }
            catch
            {
                // Measurement is a convenience, not a requirement - leave whatever value was
                // already in the box if this GLB can't be re-read for some reason.
            }
        }

        private void OnSave(object? sender, EventArgs e)
        {
            if (latestMergedModel == null) return;

            using var dlg = new SaveFileDialog { Filter = "GLB Files|*.glb", FileName = "merged.glb" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                latestMergedModel.SaveGLB(dlg.FileName);
                lblStatus.Text = $"Saved: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
