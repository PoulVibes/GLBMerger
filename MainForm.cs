using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GlbMerger
{
    public class MainForm : Form
    {
        private Button btnFile1, btnMerge, btnSave, btnViewResult, btnFixJoints;
        private Label lblStatus;
        private GlbInfoPanel panel1, panel2;
        private string? path1, path2; // GLB path for a slot; null if that slot has no GLB loaded
        private FbxAnimationSource? fbxAnims1; // slot 1's single FBX source, if it holds one
        private List<FbxAnimationSource> fbxAnimsList2 = new(); // slot 2 accumulates any number of dropped FBX sources
        private SharpGLTF.Schema2.ModelRoot? latestMergedModel; // in-memory result of the last "Process Merge" - not yet saved anywhere the user chose
        private string? latestPreviewPath; // scratch copy of latestMergedModel, used only so the viewer has a file to load

        public MainForm()
        {
            Text = "GLB Texture & Animation Merger";
            Width = 850;
            Height = 650;
            MinimumSize = new System.Drawing.Size(600, 450);

            var toolbar = new Panel { Dock = DockStyle.Top, Height = 64, BorderStyle = BorderStyle.FixedSingle };
            btnFile1 = new Button { Text = "Load Model 1", Left = 8, Top = 8, Width = 90 };
            btnMerge = new Button { Text = "Process Merge", Left = 104, Top = 8, Width = 110, Enabled = false };
            btnSave = new Button { Text = "Save...", Left = 220, Top = 8, Width = 90, Enabled = false };
            btnViewResult = new Button { Text = "🖥️ Open Model Viewer", Left = 316, Top = 8, Width = 150, Enabled = false };
            btnFixJoints = new Button { Text = "Fix Joint Orientation...", Left = 472, Top = 8, Width = 150, Enabled = false };

            lblStatus = new Label { Left = 8, Top = 38, Width = 700, AutoEllipsis = true };

            toolbar.Controls.AddRange(new Control[] { btnFile1, btnMerge, btnSave, btnViewResult, btnFixJoints, lblStatus });

            var splitter = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                IsSplitterFixed = true
            };

            // Keep the two info panels locked to an even 50/50 split of the visible width,
            // regardless of how the window is resized.
            void KeepSplitterCentered(object? s, EventArgs e)
            {
                if (splitter.Width > 0)
                    splitter.SplitterDistance = splitter.Width / 2;
            }
            splitter.SizeChanged += KeepSplitterCentered;

            panel1 = new GlbInfoPanel { Dock = DockStyle.Fill, ShowAnimationCheckboxes = true };
            panel1.UpdateTitle("Model 1 (geometry, materials, animations)");

            panel2 = new GlbInfoPanel(showGeometryBox: false) { Dock = DockStyle.Fill, ShowAnimationCheckboxes = true };
            panel2.UpdateTitle("Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");
            panel2.EnableDropTargets();
            panel2.GlbFileDropped += async path => await LoadGlbIntoSlot2(path);
            panel2.FbxFilesDropped += async paths => await LoadFbxFilesIntoSlot2(paths);

            splitter.Panel1.Controls.Add(panel1);
            splitter.Panel2.Controls.Add(panel2);

            btnFile1.Click += async (s, e) => await PickModel1();
            btnMerge.Click += OnProcessMerge;
            btnSave.Click += OnSave;

            btnViewResult.Click += (s, e) => {
                if (!string.IsNullOrEmpty(latestPreviewPath) && File.Exists(latestPreviewPath)) {
                    using var viewer = new ViewerForm(latestPreviewPath);
                    viewer.ShowDialog(this);
                }
            };

            // Corrections apply directly to latestMergedModel in place, so closing this dialog
            // needs no extra "commit" step - Save already picks up whatever was changed. The
            // on-disk preview file is regenerated too, so "Open Model Viewer" reflects the
            // corrected result as well, not just Save.
            btnFixJoints.Click += (s, e) => {
                if (latestMergedModel == null || latestPreviewPath == null) return;
                using var jointForm = new JointOrientationForm(latestMergedModel);
                jointForm.ShowDialog(this);
                latestMergedModel.SaveGLB(latestPreviewPath);
                lblStatus.Text = "Joint corrections applied.";
            };

            Controls.Add(splitter);
            Controls.Add(toolbar);
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
            if (busy) { btnSave.Enabled = false; btnViewResult.Enabled = false; btnFixJoints.Enabled = false; }
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
                    firstMat1, firstMat2, firstAnim1, firstAnim2);

                latestPreviewPath = Path.Combine(Path.GetTempPath(), "glbmerger_preview.glb");
                latestMergedModel.SaveGLB(latestPreviewPath);

                btnSave.Enabled = true;
                btnViewResult.Enabled = true;
                btnFixJoints.Enabled = true;
                lblStatus.Text = "Merge processed - use View or Save.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                latestMergedModel = null;
                btnSave.Enabled = false;
                btnViewResult.Enabled = false;
                btnFixJoints.Enabled = false;
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
