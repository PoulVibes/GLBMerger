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
        private Button btnFile1, btnClear, btnMerge, btnSave, btnEditModel, btnSetHeight;
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
        private List<GlbAnimationSource> libraryAnimsList2 = new(); // slot 2 also accumulates any number of GLBs added from the animation library dropdown
        private SharpGLTF.Schema2.ModelRoot? latestMergedModel; // in-memory result of the last "Process Merge" - not yet saved anywhere the user chose

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
            // The single column takes the toolbar's whole width so the button row below can be
            // docked into it. Without that the row would be sized to whatever one unwrapped line
            // needs, which is what used to push the last button (Set Height) off the right edge at
            // the default window width.
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            var buttonRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                // Wraps onto a second line rather than clipping when the window is too narrow to
                // fit every button; the AutoSize toolbar grows to match.
                WrapContents = true,
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
            btnEditModel = MakeToolbarButton("🖥️ Open Model Editor", enabled: false);

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

            // The three height controls flow as one unit, so a wrap can't leave the label stranded
            // on the line above the box it labels.
            var heightGroup = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
            };
            heightGroup.Controls.AddRange(new Control[] { lblHeight, numModelHeightIn, btnSetHeight });

            buttonRow.Controls.AddRange(new Control[] { btnFile1, btnClear, btnMerge, btnSave, btnEditModel, heightGroup });

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
            panel1.SettingsChanged += OnPanelSettingsChanged;

            panel2 = new GlbInfoPanel(showGeometryBox: false, defaultFixBoneLength: true, defaultFixHipRotation: true) { Dock = DockStyle.Fill, ShowAnimationCheckboxes = true };
            panel2.SettingsChanged += OnPanelSettingsChanged;
            panel2.UpdateTitle("Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");
            panel2.EnableDropTargets();
            panel2.GlbFileDropped += async path => await LoadGlbIntoSlot2(path);
            panel2.FbxFilesDropped += async paths => await LoadFbxFilesIntoSlot2(paths);

            panel2.EnableAnimationLibrary();
            panel2.SetAnimationLibraryDirectory(_settings.AnimationLibraryDirectory);
            panel2.LibraryModelSelected += async path => await AddLibraryModelToSlot2(path);
            panel2.LibraryDirectoryChangeRequested += OnChangeAnimationLibraryDirectory;

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

            // Every mode of the editor (joint corrections, ball anchor, stiff-arm poses, animation
            // trimming) edits latestMergedModel in place, so closing it needs no extra "commit"
            // step - Save already writes out whatever was changed. Each mode also stages its own
            // preview file for its 3D view, so there's nothing here to keep in sync either.
            btnEditModel.Click += (s, e) => {
                if (latestMergedModel == null) return;
                using var editor = new ModelEditorForm(latestMergedModel, _settings.DarkMode, _settings);
                editor.ShowDialog(this);
                _settings.Save();
                lblStatus.Text = "Model editor closed - use Save to write the result out.";
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
        // preview files the editor writes into the temp folder are deliberately left alone -
        // they're rewritten the next time the editor opens, so there's nothing to clean up here.
        private void ClearAll()
        {
            path1 = null;
            path2 = null;
            fbxAnims1 = null;
            fbxAnimsList2 = new List<FbxAnimationSource>();
            libraryAnimsList2 = new List<GlbAnimationSource>();
            latestMergedModel = null;

            panel1.Reset("Model 1 (geometry, materials, animations)");
            panel2.Reset("Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");

            btnMerge.Enabled = false;
            btnSave.Enabled = false;
            btnEditModel.Enabled = false;
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
                    panel1.LoadFbxAnimations(renamed.Clips.Select(c => (c.Name, GlbMergeService.ComputeAnimationFrameCount(c))).ToList());
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
                var newlyAddedClips = new List<(string Name, int FrameCount)>();

                foreach (var fbxPath in fbxPaths)
                {
                    var source = await Task.Run(() => FbxImportService.ExtractAnimationClips(fbxPath));
                    var renamed = RenameClipsToUniqueNames(source, fbxPath, usedNames);

                    fbxAnimsList2.Add(renamed);
                    newlyAddedClips.AddRange(renamed.Clips.Select(c => (c.Name, GlbMergeService.ComputeAnimationFrameCount(c))));
                }

                UpdateSlot2Title();
                panel2.AddSupplementalFbxAnimations(newlyAddedClips);
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
            if (libraryAnimsList2.Count > 0) parts.Add($"{libraryAnimsList2.Count} library animation source(s)");
            panel2.UpdateTitle(parts.Count > 0
                ? "Model 2: " + string.Join(" + ", parts)
                : "Model 2 - drop a GLB onto Materials, FBX file(s) onto Animations");
        }

        // Lets the user repoint the animation-library dropdown at a different folder (e.g. a
        // different character's pre-made animation set) instead of always reading from whatever
        // directory shipped as the default - persisted so it's remembered next launch.
        private void OnChangeAnimationLibraryDirectory()
        {
            using var dlg = new FolderBrowserDialog
            {
                SelectedPath = Directory.Exists(_settings.AnimationLibraryDirectory) ? _settings.AnimationLibraryDirectory : "",
                Description = "Choose the folder containing animation library .glb files",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _settings.AnimationLibraryDirectory = dlg.SelectedPath;
            _settings.Save();
            panel2.SetAnimationLibraryDirectory(dlg.SelectedPath);
            lblStatus.Text = $"Animation library directory set to {dlg.SelectedPath}";
        }

        // Adds one library GLB's animations to slot 2, on top of whatever it already carries
        // (its own native clips, any dropped FBX sources, and any library GLBs added earlier) -
        // the animation-library dropdown's equivalent of dropping an FBX file onto the animations
        // grid, just sourced from a folder of pre-made GLBs instead of a file picker/drag-drop.
        private async Task AddLibraryModelToSlot2(string glbPath)
        {
            SetBusy(true, $"Adding {Path.GetFileName(glbPath)}...");
            try
            {
                var raw = await Task.Run(() => GlbMergeService.ExtractGlbAnimationSource(glbPath));

                // Unlike FBX (whose own clip name is often generic, e.g. Mixamo's literal
                // "mixamo.com", so the whole batch gets renamed off the filename instead), a
                // library GLB's clip names are the whole point - "Idle", "Run", etc - so they're
                // kept as-is for both the internal Name and the "Merged As" the user sees. Name
                // only gets a numeric suffix on an actual collision (e.g. two different library
                // files both having an "Idle"); MergedAs always keeps the true original name so
                // the output track is still called "Idle" either way.
                var usedNames = new HashSet<string>(panel2.GetAllAnimationNames());
                var renamedClips = new List<AnimationClipData>();
                var displayInfo = new List<(string Name, string MergedAs, int FrameCount, (bool Loop, int LoopFrame)? LoopInfo)>();
                foreach (var clip in raw.Clips)
                {
                    var uniqueName = MakeUnique(clip.Name, usedNames);
                    var renamed = new AnimationClipData { Name = uniqueName };
                    renamed.NodeChannels.AddRange(clip.NodeChannels);
                    renamedClips.Add(renamed);

                    // Seeds the grid row's Loop checkbox/Loop Frame from whatever this library
                    // GLB's own clip already carries in its extras (see GlbAnimationSource.
                    // LoopByClipName, only populated for a clip that actually had a saved loop
                    // setting), converting its stored seconds-offset back to the nearest matching
                    // frame index - same "reflect the source" treatment a directly-dropped GLB
                    // gets in LoadMaterialsFromGlb. Left null for a clip with no saved setting at
                    // all, so the panel's own "Loop checked" default applies instead of forcing
                    // every never-processed library clip to Pause.
                    (bool Loop, int LoopFrame)? loopInfo = null;
                    if (raw.LoopByClipName != null && raw.LoopByClipName.TryGetValue(clip.Name, out var loopData))
                        loopInfo = (loopData.Loop, GlbMergeService.NearestLoopFrame(clip, loopData.LoopTime));
                    displayInfo.Add((uniqueName, clip.Name, GlbMergeService.ComputeAnimationFrameCount(clip), loopInfo));
                }

                libraryAnimsList2.Add(new GlbAnimationSource
                {
                    Clips = renamedClips,
                    BindTranslationsByName = raw.BindTranslationsByName,
                    HipsBindRotation = raw.HipsBindRotation
                });

                UpdateSlot2Title();
                panel2.AddSupplementalFbxAnimations(displayInfo);
                lblStatus.Text = $"Added {Path.GetFileName(glbPath)} to Model 2 animations";
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

        // An FBX's own internal clip name is often generic (e.g. Mixamo's literal "mixamo.com"),
        // which collides across different downloaded FBX files - so every clip gets renamed to a
        // name derived from its FBX's filename (spaces -> underscores) instead, disambiguated
        // against usedNames (already-loaded clips, plus everything renamed so far in this same
        // batch) so multiple FBX files can never produce the same clip identity.
        private static FbxAnimationSource RenameClipsToUniqueNames(FbxAnimationSource source, string fbxPath, HashSet<string> usedNames)
        {
            var baseName = Path.GetFileNameWithoutExtension(fbxPath).Replace(' ', '_');
            var renamedClips = RenameClipsToUniqueNames(source.Clips, baseName, usedNames);
            return new FbxAnimationSource { Clips = renamedClips, RootTranslationCorrection = source.RootTranslationCorrection };
        }

        // Renames every clip in `clips` to a name derived from `baseName` (its source file's
        // name, spaces -> underscores), disambiguated against usedNames - shared by the FBX
        // overload above and by the animation-library GLB loader, so a clip name colliding with
        // anything already loaded into slot 2 (from either source) can't silently merge two
        // different clips together under one grid row.
        private static List<AnimationClipData> RenameClipsToUniqueNames(List<AnimationClipData> clips, string baseName, HashSet<string> usedNames)
        {
            var multiClip = clips.Count > 1;

            var renamedClips = new List<AnimationClipData>();
            for (int i = 0; i < clips.Count; i++)
            {
                var candidate = multiClip ? $"{baseName}_{i + 1}" : baseName;
                var uniqueName = MakeUnique(candidate, usedNames);

                var renamed = new AnimationClipData { Name = uniqueName };
                renamed.NodeChannels.AddRange(clips[i].NodeChannels);
                renamedClips.Add(renamed);
            }

            return renamedClips;
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
            // Loading a new file invalidates whatever was last processed, so Save/Edit shouldn't
            // offer up a stale result while busy - once done, they stay off until Process Merge
            // runs again.
            if (busy) { btnSave.Enabled = false; btnEditModel.Enabled = false; btnSetHeight.Enabled = false; }
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
            if (statusText != null) lblStatus.Text = statusText;
        }

        // Model 1 always supplies geometry, so it's the only thing strictly required to merge.
        private bool CanMerge() => path1 != null;

        // Fired by either panel's SettingsChanged whenever the user edits a per-item setting
        // (Include, Loop, frame trim, etc.) in its grids. latestMergedModel is a snapshot built
        // from whatever the panels said the *moment* Process Merge last ran - a later edit here
        // doesn't retroactively change it, so Save/Edit Model have to be taken off the table until
        // Process Merge runs again, rather than silently writing/opening the now-stale snapshot.
        // A no-op before the first merge (nothing to go stale yet).
        private void OnPanelSettingsChanged()
        {
            if (latestMergedModel == null) return;

            btnSave.Enabled = false;
            btnEditModel.Enabled = false;
            btnSetHeight.Enabled = false;
            lblStatus.Text = "Settings changed since last merge - click Process Merge to update before saving.";
        }

        // Builds the merge in memory, without asking where to save it - "Save..." handles that as
        // its own separate step, and the editor works off the same in-memory result.
        private void OnProcessMerge(object? sender, EventArgs e)
        {
            try
            {
                lblStatus.Text = "Processing targeted composite...";
                Application.DoEvents();

                bool loaded2 = path2 != null || fbxAnimsList2.Count > 0 || libraryAnimsList2.Count > 0;

                var selectedTextures1 = panel1.GetSelectedMaterialNames();
                var selectedAnims1 = panel1.GetSelectedAnimationNames();
                var inPlaceAnims1 = panel1.GetInPlaceAnimationNames();
                var loopAnims1 = panel1.GetLoopAnimationNames();
                var loopFrameAnims1 = panel1.GetLoopFrameByAnimation();
                var groundFixAnims1 = panel1.GetGroundFixAnimationNames();
                var fixBoneLengthAnims1 = panel1.GetFixBoneLengthAnimationNames();
                var fixHipRotationAnims1 = panel1.GetFixHipRotationAnimationNames();
                var yRotationAnims1 = panel1.GetYRotationByAnimation();
                var yOffsetAnims1 = panel1.GetYOffsetByAnimation();
                var renameMap1 = panel1.GetAnimationRenameMap();
                var matRenameMap1 = panel1.GetMaterialRenameMap();
                var matChannels1 = panel1.GetSelectedChannelsByMaterial();
                var geomRenameMap1 = panel1.GetGeometryRenameMap();
                var firstMat1 = panel1.GetFirstMaterialName();
                var firstAnim1 = panel1.GetFirstAnimationName();
                var frameTrim1 = panel1.GetFrameTrimByAnimation();

                var selectedTextures2 = loaded2 ? panel2.GetSelectedMaterialNames() : new List<string>();
                var selectedAnims2 = loaded2 ? panel2.GetSelectedAnimationNames() : new List<string>();
                var inPlaceAnims2 = loaded2 ? panel2.GetInPlaceAnimationNames() : new List<string>();
                var loopAnims2 = loaded2 ? panel2.GetLoopAnimationNames() : new List<string>();
                var loopFrameAnims2 = loaded2 ? panel2.GetLoopFrameByAnimation() : new Dictionary<string, int>();
                var groundFixAnims2 = loaded2 ? panel2.GetGroundFixAnimationNames() : new List<string>();
                var fixBoneLengthAnims2 = loaded2 ? panel2.GetFixBoneLengthAnimationNames() : new List<string>();
                var fixHipRotationAnims2 = loaded2 ? panel2.GetFixHipRotationAnimationNames() : new List<string>();
                var yRotationAnims2 = loaded2 ? panel2.GetYRotationByAnimation() : new Dictionary<string, float>();
                var yOffsetAnims2 = loaded2 ? panel2.GetYOffsetByAnimation() : new Dictionary<string, float>();
                var renameMap2 = loaded2 ? panel2.GetAnimationRenameMap() : new Dictionary<string, string>();
                var matRenameMap2 = loaded2 ? panel2.GetMaterialRenameMap() : new Dictionary<string, string>();
                var matChannels2 = loaded2 ? panel2.GetSelectedChannelsByMaterial() : new Dictionary<string, HashSet<string>>();
                var firstMat2 = loaded2 ? panel2.GetFirstMaterialName() : null;
                var firstAnim2 = loaded2 ? panel2.GetFirstAnimationName() : null;
                var frameTrim2 = loaded2 ? panel2.GetFrameTrimByAnimation() : new Dictionary<string, (int Start, int End)>();

                latestMergedModel = GlbMergeService.MergeTargeted(
                    path1, selectedTextures1, selectedAnims1, inPlaceAnims1, fbxAnims1,
                    path2, selectedTextures2, selectedAnims2, inPlaceAnims2, fbxAnimsList2,
                    renameMap1, renameMap2,
                    groundFixAnims1, groundFixAnims2,
                    yRotationAnims1, yRotationAnims2, yOffsetAnims1, yOffsetAnims2,
                    matRenameMap1, matRenameMap2,
                    matChannels1, matChannels2,
                    firstMat1, firstMat2, firstAnim1, firstAnim2,
                    frameTrim1, frameTrim2,
                    fixBoneLengthAnims1, fixBoneLengthAnims2,
                    fixHipRotationAnims1, fixHipRotationAnims2,
                    geomRenameMap1: geomRenameMap1,
                    libraryAnims2: libraryAnimsList2,
                    loopAnims1: loopAnims1, loopAnims2: loopAnims2,
                    loopFrameByName1: loopFrameAnims1, loopFrameByName2: loopFrameAnims2);

                btnSave.Enabled = true;
                btnEditModel.Enabled = true;
                btnSetHeight.Enabled = true;
                lblStatus.Text = "Merge processed - use Open Model Editor or Save.";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                latestMergedModel = null;
                btnSave.Enabled = false;
                btnEditModel.Enabled = false;
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
            if (latestMergedModel == null) return;

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
                var toSave = latestMergedModel;

                // Optimizing rewrites indices only, so a model that has been through the geometry
                // optimizer carries the vertices its triangles stopped referencing plus a dead index
                // buffer per pass. Both are only reclaimable by rebuilding, which produces a new
                // ModelRoot - fine here, because this saves a copy and leaves the session's model
                // alone, but it's a real change to the file, so it's offered rather than assumed.
                var estimate = GeometryOptimizer.EstimateCleanup(latestMergedModel);
                if (estimate.HasWaste)
                {
                    var choice = MessageBox.Show(
                        DescribeCleanup(estimate),
                        "Clean up unused geometry?",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

                    if (choice == DialogResult.Cancel) return;
                    if (choice == DialogResult.Yes)
                    {
                        var cleaned = BuildCleanedCopyChecked(latestMergedModel);
                        if (cleaned == null) return;
                        toSave = cleaned;
                    }
                }

                Cursor = Cursors.WaitCursor;
                try { toSave.SaveGLB(dlg.FileName); }
                finally { Cursor = Cursors.Default; }

                long written = new FileInfo(dlg.FileName).Length;
                lblStatus.Text = $"Saved: {Path.GetFileName(dlg.FileName)} ({FormatBytes(written)})";
            }
            catch (Exception ex)
            {
                lblStatus.Text = "Error: " + ex.Message;
                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static string DescribeCleanup(GeometryOptimizer.CleanupEstimate estimate)
        {
            var lines = new List<string>();

            if (estimate.UnusedVertices > 0)
            {
                lines.Add($"{estimate.UnusedVertices:N0} of {estimate.TotalVertices:N0} vertices are no longer "
                    + $"referenced by any triangle ({FormatBytes(estimate.UnusedVertexBytes)}).");
            }
            if (estimate.OrphanedBufferViews > 0)
            {
                lines.Add($"{estimate.OrphanedBufferViews} buffer(s) left behind by earlier optimizer passes "
                    + $"are still in the file ({FormatBytes(estimate.OrphanedBytes)}).");
            }

            lines.Add("");
            lines.Add($"Cleaning up rebuilds the model and reclaims about {FormatBytes(estimate.ReclaimableBytes)}. "
                + "It changes only what gets written to disk - the model open in this session is left as it is.");
            lines.Add("");
            lines.Add("Clean up before saving?");

            return string.Join(Environment.NewLine, lines);
        }

        // The rebuild re-emits the scene rather than editing it, so it is checked against the
        // original before anything is written - the BallAnchor/StiffArm markers are empty nodes and
        // are the likeliest thing to be dropped. Returns null when the save should be abandoned.
        private ModelRoot? BuildCleanedCopyChecked(ModelRoot model)
        {
            ModelRoot cleaned;
            Cursor = Cursors.WaitCursor;
            try { cleaned = GeometryOptimizer.BuildCleanedCopy(model); }
            finally { Cursor = Cursors.Default; }

            string? losses = GeometryOptimizer.DescribeLosses(model, cleaned);
            if (losses == null) return cleaned;

            var proceed = MessageBox.Show(
                "The rebuild did not carry everything across:" + Environment.NewLine + Environment.NewLine
                + losses + Environment.NewLine + Environment.NewLine
                + "Save the original model uncleaned instead? (No saves the cleaned model anyway.)",
                "Cleanup would lose data",
                MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

            return proceed switch
            {
                DialogResult.Yes => model,
                DialogResult.No => cleaned,
                _ => null,
            };
        }

        private static string FormatBytes(long bytes) => bytes switch
        {
            >= 1L << 20 => $"{bytes / (double)(1L << 20):N1} MB",
            >= 1L << 10 => $"{bytes / (double)(1L << 10):N1} KB",
            _ => $"{bytes:N0} bytes",
        };
    }
}
