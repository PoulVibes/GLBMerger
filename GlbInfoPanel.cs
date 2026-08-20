using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.ComponentModel;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    public class GlbInfoPanel : UserControl // Swapped to UserControl for better group paint boundary management
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool ShowAnimationCheckboxes { get; set; }

        private GroupBox mainGroup;
        private DataGridView grdGeometry;
        private DataGridView grdMaterials;
        private DataGridView grdAnimations;
        private Label lblPrim, lblMat, lblAnim;
        private SplitContainer _primarySplit;
        private SplitContainer? _secondarySplit;

        // Slot 2 can independently carry a GLB's own baked-in animations and a supplemental FBX's
        // animations at the same time (e.g. a GLB dropped for materials that also happens to have
        // animations, plus an FBX dropped separately for more). The grid is rebuilt from both
        // lists together whenever either one changes, so loading a new file into one drop target
        // never wipes out whatever is already loaded in the other.
        private List<string> _glbAnimNames = new();
        private List<(string Name, string MergedAs)> _fbxAnimNames = new();
        // Clip name -> total distinct-keyframe count, used to bound the frame-trim columns to
        // each animation's own length (glTF/FBX clips don't store a frame count, so this is
        // computed at load time the same way GlbMergeService computes it at merge time - see
        // ComputeAnimationFrameCount). Entries persist across RebuildAnimationGrid calls same as
        // _glbAnimNames/_fbxAnimNames, since dropping one more FBX shouldn't lose the frame counts
        // of clips already loaded from the other source.
        private readonly Dictionary<string, int> _animFrameCounts = new();

        // Clip name -> the loop/pause setting (and which frame to resume at, in that clip's own
        // pre-trim numbering) already baked into its *source* file's glTF extras - read at load
        // time from a native GLB (LoadMaterialsFromGlb) or a library GLB (see
        // AddSupplementalFbxAnimations' loop-aware overload), so re-loading a GLB this app (or the
        // Animation Trim editor) already stamped a loop setting onto shows that setting instead of
        // always resetting to the row default. FBX sources carry no such data, so they simply never
        // add an entry here - AddRow's own (true, 0) fallback covers them, same as before this
        // existed. Only consulted for a brand-new row; an already-present row's own edited Loop/
        // LoopFrame cells always win (see RebuildAnimationGrid's previousSettings).
        private readonly Dictionary<string, (bool Loop, int LoopFrame)> _animLoopByName = new();

        public event Action<string>? GlbFileDropped;
        public event Action<List<string>>? FbxFilesDropped;

        // Fired on any user edit to a per-item setting (Include, Loop, Merged As, frame trim,
        // material channel, etc.) in this panel's grids - after a merge has already been produced,
        // this is the caller's signal that latestMergedModel no longer reflects what's on screen,
        // so Save shouldn't write it out until Process Merge runs again. Deliberately does NOT
        // fire while a grid is being repopulated from a fresh load (DataGridView.Rows.Add doesn't
        // raise CellValueChanged for the values it's initializing a new row with - only genuine
        // post-load edits, and header-click bulk toggles, actually assign into an existing cell).
        public event Action? SettingsChanged;

        // Fired when the user picks a file from the animation-library dropdown (full path) or
        // clicks the button next to it to change which folder the dropdown lists. See
        // EnableAnimationLibrary.
        public event Action<string>? LibraryModelSelected;
        public event Action? LibraryDirectoryChangeRequested;

        private FlowLayoutPanel? _libraryBar;
        private ComboBox? _libraryCombo;

        // Default state of a newly-added animation row's "Fix Bone Length"/"Fix Hip Rotation"
        // checkboxes - see RebuildAnimationGrid. Differs by slot: model 1's own clips are already
        // in the structural model's own bone lengths/bind pose, so both corrections would be a
        // no-op there and default off; model 2 (and any FBX/library source layered onto it) is
        // being retargeted onto a *different* rig, where both corrections are the normal case, so
        // both default on.
        private readonly bool _defaultFixBoneLength;
        private readonly bool _defaultFixHipRotation;

        // Model 2 never uses its own geometry (model 1's is always used), so the "Available
        // Geometry" reference list is meaningless there - showGeometryBox omits it entirely and
        // lets the materials/animations lists expand to fill the space instead.
        public GlbInfoPanel(bool showGeometryBox = true, bool defaultFixBoneLength = false, bool defaultFixHipRotation = false)
        {
            _defaultFixBoneLength = defaultFixBoneLength;
            _defaultFixHipRotation = defaultFixHipRotation;

            Padding = new Padding(4);

            mainGroup = new GroupBox { Text = "No Model Loaded", Dock = DockStyle.Fill };

            lblPrim = MakeLabel("Geometry (Merged As renames the output mesh)");
            grdGeometry = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
            };
            grdGeometry.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Geometry", ReadOnly = true, FillWeight = 30 });
            grdGeometry.Columns.Add(new DataGridViewTextBoxColumn { Name = "Info", HeaderText = "Materials", ReadOnly = true, FillWeight = 30 });
            // Verts/faces summed across the mesh's own primitives - shown for quick sanity-checking
            // of geometry complexity without needing to open a separate model viewer.
            grdGeometry.Columns.Add(new DataGridViewTextBoxColumn { Name = "VertsFaces", HeaderText = "Verts / Faces", ReadOnly = true, FillWeight = 20 });
            // Editable output name: lets the user rename how this mesh/node appears in the merged
            // file, without touching the source model - matched against the merge's own baseName
            // (Mesh.Name, falling back to Node.Name) so it lands on exactly the right part.
            grdGeometry.Columns.Add(new DataGridViewTextBoxColumn { Name = "MergedAs", HeaderText = "Merged As", FillWeight = 30 });
            grdGeometry.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grdGeometry.IsCurrentCellDirty)
                    grdGeometry.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grdGeometry.CellValueChanged += (s, e) => SettingsChanged?.Invoke();

            lblMat = MakeLabel("Textures / Materials (Check to Inject)");
            grdMaterials = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
            };
            grdMaterials.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", FillWeight = 15 });
            grdMaterials.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Material", ReadOnly = true, FillWeight = 22 });
            // One checkbox per known channel (see GlbMergeService.KnownMaterialChannelNames) - lets
            // the user merge only some of a material's channels instead of all-or-nothing. Checked
            // by default for whichever channels this material actually has; a channel it doesn't
            // have gets its cell disabled (ChannelColumnName below) so it can't be turned on.
            foreach (var channelName in GlbMergeService.KnownMaterialChannelNames)
                grdMaterials.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    Name = ChannelColumnName(channelName), HeaderText = ChannelHeaderText(channelName), FillWeight = 10
                });
            // Editable output name: lets the user rename how a material appears in the merged
            // file, without touching the source model - selection matching still keys off "Name".
            grdMaterials.Columns.Add(new DataGridViewTextBoxColumn { Name = "MergedAs", HeaderText = "Merged As", FillWeight = 20 });
            // Marks which material should be written first into the output's material list (some
            // engines/tools default to index 0) - only one row can be marked at a time.
            grdMaterials.Columns.Add(new DataGridViewCheckBoxColumn { Name = "First", HeaderText = "First", FillWeight = 13 });
            grdMaterials.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grdMaterials.IsCurrentCellDirty)
                    grdMaterials.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grdMaterials.CellValueChanged += (s, e) => EnforceSingleFirst(grdMaterials, e);
            grdMaterials.CellValueChanged += (s, e) => SettingsChanged?.Invoke();
            grdMaterials.ColumnHeaderMouseClick += (s, e) => ToggleAllInColumn(
                grdMaterials, e.ColumnIndex,
                new HashSet<string>(new[] { "Include" }.Concat(GlbMergeService.KnownMaterialChannelNames.Select(ChannelColumnName))));

            lblAnim = MakeLabel("Animation Clips (Check to Inject)");

            grdAnimations = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = SystemColors.Window,
            };
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", FillWeight = 20 });
            grdAnimations.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Animation", ReadOnly = true, FillWeight = 40 });
            // Editable output name: lets the user rename how a clip appears in the merged file
            // (e.g. an FBX's generic Assimp take name like "mixamo.com") without touching the
            // source file itself - selection/lock-in-place matching still keys off "Name".
            grdAnimations.Columns.Add(new DataGridViewTextBoxColumn { Name = "MergedAs", HeaderText = "Merged As", FillWeight = 40 });
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "InPlace", HeaderText = "Lock In Place", FillWeight = 30 });
            // Playback behavior once the engine reaches the clip's last frame: checked loops back
            // to the clip's own first frame (frame 0 after any trim above already rebases it
            // there) and keeps playing; unchecked holds/pauses on the last frame. Written into the
            // output animation's own glTF "extras" (see GlbMergeService.MergeTargeted) since
            // glTF/GLB has no native concept of loop-vs-pause - it's purely a runtime engine
            // decision, so the merged file just carries the author's intent for the engine to read.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Loop", HeaderText = "Loop (else Pause)", FillWeight = 30 });
            // Analyzes the clip's own foot motion to find its most "grounded" pose (feet level
            // with each other and at their lowest point) and shifts the whole clip vertically so
            // that pose's feet align with the target rig's own resting ground height - corrects a
            // uniform float/sink that can happen when a retargeted clip's natural knee-bend leaves
            // its feet slightly off the ground even though the root translation itself is correct.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "GroundFix", HeaderText = "Fix Floating", FillWeight = 30 });
            // Re-anchors this clip's translation channels (usually just a bone's static length,
            // repeated every frame) from the source rig's own bone lengths onto the target rig's,
            // so a differently-proportioned donor rig doesn't stretch/squish the target's limbs.
            // Purely geometric - doesn't touch rotation at all. Checked by default: a no-op
            // whenever both rigs already share the same proportions.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "FixBoneLength", HeaderText = "Fix Bone Length", FillWeight = 30 });
            // Corrects just the Hips joint's rotation using a fixed delta computed from each rig's
            // own Hips bind pose - narrower than a full per-joint rotation retarget (which this
            // app tried and found made limb rotations worse). Hips is the exception: its own
            // bind-rotation mismatch between two independently-authored rigs shows up as the torso
            // reading as tilted/shifted relative to the legs, without any downstream joint able to
            // absorb it the way a leg can absorb its own bind mismatch. Off by default - only
            // needed when the torso visibly leans/shifts relative to the hips.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "FixHipRotation", HeaderText = "Fix Hip Rotation", FillWeight = 30 });
            // Manual turn of the whole clip around the world Y axis (facing direction and root
            // motion path both rotate together, pivoting on the clip's own starting position) -
            // for cases where the retargeted clip simply faces/walks the wrong way. Out-of-range
            // values wrap around (400 -> 40) rather than clamping at the boundary, since an angle
            // past 359.99 is still a meaningful angle, not an error.
            grdAnimations.Columns.Add(new NumericUpDownColumn
            {
                Name = "YRotation", HeaderText = "Rot", FillWeight = 25,
                Minimum = 0m, Maximum = 359.99m, Increment = 1m, DecimalPlaces = 2, WrapAround = true
            });
            // Manual additional vertical nudge on top of whatever Fix Floating already computed -
            // for the residual cases where the automatic ground correction doesn't quite land.
            grdAnimations.Columns.Add(new NumericUpDownColumn
            {
                Name = "YOffset", HeaderText = "Offset", FillWeight = 25,
                Minimum = -9999m, Maximum = 9999m, Increment = 1m, DecimalPlaces = 2
            });

            // Which keyframes of the clip actually make it into the merge - editable right here
            // instead of needing the model viewer's own trim sliders. Column-level Minimum/Maximum
            // are just a fallback; each row's cell gets its own MaximumOverride set to that clip's
            // real last-frame index once its frame count is known (see RebuildAnimationGrid).
            grdAnimations.Columns.Add(new NumericUpDownColumn
            {
                Name = "StartFrame", HeaderText = "First Frame", FillWeight = 25,
                Minimum = 0m, Maximum = 0m, Increment = 1m, DecimalPlaces = 0
            });
            grdAnimations.Columns.Add(new NumericUpDownColumn
            {
                Name = "EndFrame", HeaderText = "Last Frame", FillWeight = 25,
                Minimum = 0m, Maximum = 0m, Increment = 1m, DecimalPlaces = 0
            });
            // Which frame (in this clip's own pre-trim numbering, same as First/Last Frame above)
            // a looping clip resumes at once playback reaches the end - only meaningful when Loop
            // is checked. Bound to the clip's own full range rather than the current First/Last
            // Frame selection; if it ends up outside whatever range those two keep, the merge
            // clamps it to the nearest kept frame automatically (see GlbMergeService.
            // ComputeLoopTimes) rather than rejecting the edit.
            grdAnimations.Columns.Add(new NumericUpDownColumn
            {
                Name = "LoopFrame", HeaderText = "Loop Frame", FillWeight = 25,
                Minimum = 0m, Maximum = 0m, Increment = 1m, DecimalPlaces = 0
            });

            // Marks which animation should be written first into the output's animation list
            // (some engines/tools default to playing/showing index 0) - only one row can be
            // marked at a time.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "First", HeaderText = "First", FillWeight = 15 });

            // Commit checkbox/numeric edits immediately instead of waiting for the cell to lose focus
            grdAnimations.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grdAnimations.IsCurrentCellDirty)
                    grdAnimations.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grdAnimations.CellValueChanged += (s, e) => EnforceSingleFirst(grdAnimations, e);
            grdAnimations.CellValueChanged += (s, e) => EnforceFrameOrder(grdAnimations, e);
            grdAnimations.CellValueChanged += (s, e) => SettingsChanged?.Invoke();
            // Clicking a checkbox column's header toggles that checkbox for every row at once -
            // "First" is excluded since only one row can ever be marked First (EnforceSingleFirst
            // would just immediately undo a bulk-check anyway).
            grdAnimations.ColumnHeaderMouseClick += (s, e) => ToggleAllInColumn(
                grdAnimations, e.ColumnIndex,
                new HashSet<string> { "Include", "InPlace", "GroundFix", "FixBoneLength", "FixHipRotation", "Loop" });

            // Each box (label + list/grid) sits in its own SplitContainer panel with a draggable
            // divider between it and its neighbor, instead of the old TableLayoutPanel's fixed
            // percentage rows - so the user can drag to give one box more room at another's
            // expense. Default distances still roughly mirror the old 25/35/40 (or 45/55) split,
            // but only as a starting point; ApplySplitFraction/GetSplitFraction let the caller
            // persist and restore whatever the user actually dragged them to.
            if (showGeometryBox)
            {
                _secondarySplit = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterWidth = 6,
                    Panel1MinSize = 40,
                    Panel2MinSize = 40
                };
                _secondarySplit.Panel1.Controls.Add(MakeBox(lblMat, grdMaterials));
                _secondarySplit.Panel2.Controls.Add(MakeBox(lblAnim, grdAnimations));

                _primarySplit = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterWidth = 6,
                    Panel1MinSize = 40,
                    Panel2MinSize = 80
                };
                _primarySplit.Panel1.Controls.Add(MakeBox(lblPrim, grdGeometry));
                _primarySplit.Panel2.Controls.Add(_secondarySplit);

                SplitterFractionPersistence.ApplyFraction(_primarySplit, 0.25);
                SplitterFractionPersistence.ApplyFraction(_secondarySplit, 0.4667);
            }
            else
            {
                _secondarySplit = null;
                _primarySplit = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterWidth = 6,
                    Panel1MinSize = 40,
                    Panel2MinSize = 40
                };
                _primarySplit.Panel1.Controls.Add(MakeBox(lblMat, grdMaterials));
                _primarySplit.Panel2.Controls.Add(MakeBox(lblAnim, grdAnimations));

                SplitterFractionPersistence.ApplyFraction(_primarySplit, 0.45);
            }

            mainGroup.Controls.Add(_primarySplit);
            Controls.Add(mainGroup);
        }

        // Wraps a label + its list/grid into one Dock=Fill unit suitable for a SplitContainer
        // panel - the content docks Fill first so the label (docked Top, added after) claims its
        // own row above it rather than being squeezed out.
        private static Panel MakeBox(Label label, Control content)
        {
            var box = new Panel { Dock = DockStyle.Fill };
            content.Dock = DockStyle.Fill;
            label.Dock = DockStyle.Top;
            box.Controls.Add(content);
            box.Controls.Add(label);
            return box;
        }

        // Fraction (0..1) of this panel's primary divider - geometry-vs-rest when the geometry
        // box is shown, otherwise materials-vs-animations directly. Used by the host form to
        // persist/restore the user's chosen box sizes across runs.
        public double PrimarySplitFraction
        {
            get => SplitterFractionPersistence.GetFraction(_primarySplit);
            set => SplitterFractionPersistence.ApplyFraction(_primarySplit, value);
        }

        // Fraction (0..1) of the materials-vs-animations divider nested under the geometry box.
        // Null when this panel has no geometry box (there's only one divider in that case,
        // already covered by PrimarySplitFraction).
        public double? SecondarySplitFraction
        {
            get => _secondarySplit != null ? SplitterFractionPersistence.GetFraction(_secondarySplit) : (double?)null;
            set { if (_secondarySplit != null && value.HasValue) SplitterFractionPersistence.ApplyFraction(_secondarySplit, value.Value); }
        }

        // Lets this panel accept a GLB dropped onto the materials list and/or one or more FBX
        // files dropped onto the animations grid (all at once, or one after another over
        // multiple drops) - independently of each other, so dropping one never clears the other.
        // Used for slot 2, which always supplements slot 1's geometry rather than replacing it.
        public void EnableDropTargets()
        {
            grdMaterials.AllowDrop = true;
            grdMaterials.DragEnter += (s, e) => HandleDragEnter(e, ".glb");
            grdMaterials.DragDrop += (s, e) =>
            {
                var file = MatchingFiles(e, ".glb").FirstOrDefault();
                if (file != null) GlbFileDropped?.Invoke(file);
            };

            grdAnimations.AllowDrop = true;
            grdAnimations.DragEnter += (s, e) => HandleDragEnter(e, ".fbx");
            grdAnimations.DragDrop += (s, e) =>
            {
                var files = MatchingFiles(e, ".fbx");
                if (files.Count > 0) FbxFilesDropped?.Invoke(files);
            };
        }

        // Adds a small toolbar (a library dropdown + "Change Directory..." button) directly above
        // the Animation Clips box, letting the user pick from a folder of pre-made GLB animation
        // sources instead of dragging files in one at a time. Opt-in rather than always present,
        // since only slot 2 uses it - slot 1 is always a single file picked via dialog.
        public void EnableAnimationLibrary()
        {
            if (_libraryBar != null) return;

            _libraryCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 220,
                Margin = new Padding(0, 2, 6, 0),
            };
            var changeDirButton = new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Text = "Change Directory...",
                Margin = new Padding(0),
            };

            _libraryBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 0, 0, 4),
            };
            _libraryBar.Controls.Add(new Label { Text = "Animation Library:", AutoSize = true, Margin = new Padding(0, 6, 6, 0) });
            _libraryBar.Controls.Add(_libraryCombo);
            _libraryBar.Controls.Add(changeDirButton);

            // Resets back to unselected after firing, so picking the same file again later (to
            // add another supplemental source) still raises SelectedIndexChanged instead of
            // being a no-op because the selection didn't change.
            _libraryCombo.SelectedIndexChanged += (s, e) =>
            {
                if (_libraryCombo.SelectedItem is LibraryDirectoryHelper.LibraryEntry entry)
                {
                    LibraryModelSelected?.Invoke(entry.Path);
                    _libraryCombo.SelectedIndex = -1;
                }
            };
            changeDirButton.Click += (s, e) => LibraryDirectoryChangeRequested?.Invoke();

            // lblAnim's parent is the Panel MakeBox created for it - adding a Dock=Top control to
            // it after the label (itself Dock=Top) puts this bar above the label, i.e. above the
            // whole Animation Clips box, matching MakeBox's own "later Top addition claims the
            // outer position" ordering.
            lblAnim.Parent!.Controls.Add(_libraryBar);
        }

        // Repopulates the dropdown from every .glb file directly inside `directory`
        // (non-recursive). A missing/inaccessible directory just leaves the dropdown empty
        // rather than throwing, since this can run from a stale saved setting on startup before
        // the user has confirmed the path still exists.
        public void SetAnimationLibraryDirectory(string directory)
        {
            if (_libraryCombo == null) return;

            _libraryCombo.Items.Clear();
            foreach (var file in LibraryDirectoryHelper.ListGlbFiles(directory))
                _libraryCombo.Items.Add(new LibraryDirectoryHelper.LibraryEntry { Path = file });
        }

        private static void HandleDragEnter(DragEventArgs e, string requiredExtension)
        {
            e.Effect = DragDropEffects.None;
            if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            if (files.Any(f => f.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase)))
                e.Effect = DragDropEffects.Copy;
        }

        private static List<string> MatchingFiles(DragEventArgs e, string requiredExtension)
        {
            if (!e.Data!.GetDataPresent(DataFormats.FileDrop)) return new List<string>();

            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            return files.Where(f => f.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Lets a checkbox column's header act as a "select all" button: if every row is currently
        // checked, unchecks them all; otherwise checks them all (so a partially-checked column
        // fills the rest in, rather than requiring a second click). Only columns named in
        // toggleableColumns respond - e.g. "First" is deliberately excluded wherever this is
        // wired up, since EnforceSingleFirst would immediately collapse a bulk-check back down to
        // one row anyway.
        private static void ToggleAllInColumn(DataGridView grid, int columnIndex, HashSet<string> toggleableColumns)
        {
            if (columnIndex < 0 || columnIndex >= grid.Columns.Count) return;
            if (grid.Rows.Count == 0) return;

            var column = grid.Columns[columnIndex];
            if (!toggleableColumns.Contains(column.Name)) return;

            bool allChecked = grid.Rows.Cast<DataGridViewRow>()
                .All(r => r.Cells[columnIndex].Value is bool b && b);
            bool newValue = !allChecked;

            foreach (DataGridViewRow row in grid.Rows)
                row.Cells[columnIndex].Value = newValue;
        }

        // Keeps the "First" checkbox mutually exclusive within a grid: checking one row's "First"
        // cell unchecks every other row's. Setting a cell to false re-triggers this handler
        // recursively, but since that recursive call's own cell is already false, it returns
        // immediately instead of looping.
        private static void EnforceSingleFirst(DataGridView grid, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            if (grid.Columns[e.ColumnIndex].Name != "First") return;
            if (grid.Rows[e.RowIndex].Cells["First"].Value is not bool isChecked || !isChecked) return;

            foreach (DataGridViewRow row in grid.Rows)
                if (row.Index != e.RowIndex && row.Cells["First"].Value is bool otherChecked && otherChecked)
                    row.Cells["First"].Value = false;
        }

        // Keeps a row's StartFrame from passing its own EndFrame (and vice versa) - pushes
        // whichever handle wasn't just edited along with it instead of rejecting the edit, same
        // trade-off the model viewer's trim sliders make. Setting the other cell re-triggers this
        // handler, but by then Start == End so no further change happens - recursion stops there.
        private static void EnforceFrameOrder(DataGridView grid, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var colName = grid.Columns[e.ColumnIndex].Name;
            if (colName != "StartFrame" && colName != "EndFrame") return;

            var row = grid.Rows[e.RowIndex];
            if (row.Cells["StartFrame"].Value is not decimal start) return;
            if (row.Cells["EndFrame"].Value is not decimal end) return;
            if (start <= end) return;

            if (colName == "StartFrame") row.Cells["EndFrame"].Value = start;
            else row.Cells["StartFrame"].Value = end;
        }

        public void UpdateTitle(string title)
        {
            mainGroup.Text = title;
        }

        // Wipes this panel back to its just-opened state: empty grids, no remembered GLB/FBX
        // animation names (so a later RebuildAnimationGrid call from a fresh load doesn't
        // resurrect a previous drop's rows), and the caller-supplied default title.
        public void Reset(string defaultTitle)
        {
            this.SuspendLayout();

            grdGeometry.Rows.Clear();
            grdMaterials.Rows.Clear();
            grdAnimations.Rows.Clear();
            _glbAnimNames = new List<string>();
            _fbxAnimNames = new List<(string Name, string MergedAs)>();
            _animFrameCounts.Clear();
            _animLoopByName.Clear();
            mainGroup.Text = defaultTitle;

            this.ResumeLayout(true);
        }

        // Exclusive load: a GLB in this slot replaces anything previously loaded, including any
        // supplemental FBX animations. Used for slot 1's single-file picker.
        public void LoadModel(string path)
        {
            _fbxAnimNames = new List<(string Name, string MergedAs)>();
            LoadMaterialsFromGlb(path);
        }

        // FBX sources only ever contribute animation clips - geometry/materials are ignored, so
        // those two lists stay empty rather than trying to render a fake model for them. Exclusive
        // load: replaces anything previously loaded, including materials from a GLB. Used for
        // slot 1's single-file picker. clips are already final, guaranteed-unique names (computed
        // by the caller, e.g. from the FBX's filename) paired with that clip's own frame count
        // (GlbMergeService.ComputeAnimationFrameCount, computed by the caller since it already has
        // the raw AnimationClipData) - matching and default "Merged As" both key off the name.
        public void LoadFbxAnimations(List<(string Name, int FrameCount)> clips)
        {
            this.SuspendLayout();

            grdGeometry.Rows.Clear();
            grdGeometry.Rows.Add("(FBX source - animation only, no geometry)", "", "", "");

            grdMaterials.Rows.Clear();

            _glbAnimNames = new List<string>();
            _fbxAnimNames = clips.Select(c => (Name: c.Name, MergedAs: c.Name)).ToList();
            foreach (var c in clips) _animFrameCounts[c.Name] = c.FrameCount;
            RebuildAnimationGrid();

            this.ResumeLayout(true);
        }

        // Supplemental load for slot 2: populates primitives/materials and this GLB's own baked
        // animations, without touching any FBX animations already loaded via drag-and-drop.
        public void LoadMaterialsFromGlb(string path)
        {
            this.SuspendLayout();

            grdGeometry.Rows.Clear();
            grdMaterials.Rows.Clear();

            var model = ModelRoot.Load(path);

            // One row per node/mesh, keyed by the same baseName GlbMergeService uses (Mesh.Name,
            // falling back to Node.Name) - so what the user types in "Merged As" here lands on
            // exactly the part they're looking at, however many primitives/materials it has.
            var seenBaseNames = new HashSet<string>();
            void WalkNode(Node node)
            {
                if (node.Mesh != null)
                {
                    var baseName = node.Mesh.Name ?? node.Name ?? "mesh";
                    if (seenBaseNames.Add(baseName))
                    {
                        var matNames = node.Mesh.Primitives.Select(p => p.Material?.Name ?? "No Material").Distinct();
                        long vertCount = node.Mesh.Primitives.Sum(p =>
                            p.VertexAccessors.TryGetValue("POSITION", out var posAcc) ? posAcc.Count : 0);
                        long faceCount = node.Mesh.Primitives.Sum(p => p.GetTriangleIndices().Count());
                        grdGeometry.Rows.Add(baseName, string.Join(", ", matNames), $"{vertCount:N0} / {faceCount:N0}", baseName);
                    }
                }

                foreach (var child in node.VisualChildren)
                    WalkNode(child);
            }

            if (model.DefaultScene != null)
                foreach (var node in model.DefaultScene.VisualChildren)
                    WalkNode(node);

            if (grdGeometry.Rows.Count == 0)
                grdGeometry.Rows.Add("(no geometry found)", "", "", "");

            foreach (var mat in model.LogicalMaterials)
            {
                // Shared with the merge service on purpose - the string put in this grid is what
                // comes back as the user's selection, and it has to be the exact same key the merge
                // matches against. See GlbMergeService.MaterialKey.
                var matName = GlbMergeService.MaterialKey(mat);

                var rowValues = new List<object> { true, matName };
                var presence = new Dictionary<string, bool>();
                foreach (var channelName in GlbMergeService.KnownMaterialChannelNames)
                {
                    var found = mat.FindChannel(channelName);
                    bool present = found.HasValue && found.Value.Texture != null;
                    presence[channelName] = present;
                    rowValues.Add(present); // checked by default when the material actually has it
                }
                rowValues.Add(matName); // MergedAs
                rowValues.Add(false); // First

                int rowIndex = grdMaterials.Rows.Add(rowValues.ToArray());
                var row = grdMaterials.Rows[rowIndex];
                foreach (var channelName in GlbMergeService.KnownMaterialChannelNames)
                    if (!presence[channelName])
                        row.Cells[ChannelColumnName(channelName)].ReadOnly = true; // no such channel on this material - nothing to select
            }

            _glbAnimNames = model.LogicalAnimations.Select(a => a.Name ?? $"Anim_{a.LogicalIndex}").ToList();
            foreach (var anim in model.LogicalAnimations)
            {
                var name = anim.Name ?? $"Anim_{anim.LogicalIndex}";
                var times = ComputeGlbAnimationFrameTimes(anim);
                _animFrameCounts[name] = times.Length;

                // Seeds the Loop checkbox/Loop Frame default from whatever this GLB's own extras
                // already say (e.g. a file this app previously exported, or one hand-authored to
                // the same convention) - see GlbMergeService.GetAnimationLoop. Only when extras
                // actually carry a saved loop setting (HasAnimationLoopExtras) - otherwise leave
                // _animLoopByName untouched for this clip so RebuildAnimationGrid's own (true, 0)
                // "no data" default applies, instead of GetAnimationLoop's (false, 0) "nothing to
                // report" fallback masquerading as an explicit Pause.
                if (GlbMergeService.HasAnimationLoopExtras(model, name))
                {
                    var (loop, loopTime) = GlbMergeService.GetAnimationLoop(model, name);
                    _animLoopByName[name] = (loop, NearestFrameIndex(times, loopTime));
                }
            }
            RebuildAnimationGrid();

            this.ResumeLayout(true);
        }

        // Distinct keyframe times for a glTF-native animation, read directly off the ModelRoot's
        // own channel data, sorted ascending - same "frame" numbering GlbMergeService.
        // ComputeAnimationFrameCount/CollectFrameTimes use for the AnimationClipData form, computed
        // independently here only because this runs before that extraction step (at load time, for
        // display).
        private static float[] ComputeGlbAnimationFrameTimes(Animation anim)
        {
            var times = new SortedSet<float>();
            foreach (var ch in anim.Channels)
            {
                if (ch.TargetNodePath == PropertyPath.translation)
                    foreach (var k in ch.GetTranslationSampler().GetLinearKeys()) times.Add(k.Key);
                else if (ch.TargetNodePath == PropertyPath.rotation)
                    foreach (var k in ch.GetRotationSampler().GetLinearKeys()) times.Add(k.Key);
                else if (ch.TargetNodePath == PropertyPath.scale)
                    foreach (var k in ch.GetScaleSampler().GetLinearKeys()) times.Add(k.Key);
            }
            return times.ToArray();
        }

        // Index of whichever entry in `times` is closest to `target` - converts a stored
        // loopTime (seconds) back to the nearest matching frame index for the Loop Frame column,
        // the reverse of what the column itself does when the merge resolves a chosen frame back
        // to a time (GlbMergeService.ComputeLoopTimes).
        private static int NearestFrameIndex(float[] times, float target)
        {
            if (times.Length == 0) return 0;

            int best = 0;
            float bestDist = Math.Abs(times[0] - target);
            for (int i = 1; i < times.Length; i++)
            {
                float dist = Math.Abs(times[i] - target);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;
        }

        // Supplemental load for slot 2: ADDS this FBX's clips to the animation grid without
        // touching whatever materials/animations (or previously dropped FBX clips) are already
        // loaded - so dropping multiple FBX files at once, or one after another, accumulates
        // rather than replaces. clips are already final, guaranteed-unique names (computed by the
        // caller, e.g. from each FBX's filename) paired with that clip's own frame count. Default
        // "Merged As" is the same as the (already-unique) Name, since an FBX's own clip name is
        // often generic (e.g. Mixamo's literal "mixamo.com") and not worth showing as-is.
        public void AddSupplementalFbxAnimations(List<(string Name, int FrameCount)> clips) =>
            AddSupplementalFbxAnimations(clips.Select(c => (c.Name, MergedAs: c.Name, c.FrameCount)).ToList());

        // Same accumulation as above, but for a caller (the animation-library dropdown) that
        // wants "Merged As" to default to something other than the row's own unique Name - e.g.
        // the clip's original animation name (say "Idle") when Name itself had to be
        // disambiguated with a numeric suffix (say "Idle_2") because a same-named clip was
        // already loaded from another source.
        public void AddSupplementalFbxAnimations(List<(string Name, string MergedAs, int FrameCount)> clips)
        {
            this.SuspendLayout();
            _fbxAnimNames.AddRange(clips.Select(c => (Name: c.Name, MergedAs: c.MergedAs)));
            foreach (var c in clips) _animFrameCounts[c.Name] = c.FrameCount;
            RebuildAnimationGrid();
            this.ResumeLayout(true);
        }

        // Same accumulation again, but for the animation-library dropdown specifically: unlike an
        // FBX, a library GLB's own clips can already carry a loop/pause setting in their glTF
        // extras (e.g. a file this app previously exported), so this overload also seeds
        // _animLoopByName from it - same "reflect what the source already says" treatment
        // LoadMaterialsFromGlb gives a directly-dropped GLB. LoopInfo is null for a clip whose
        // source never saved a loop setting at all (the common case for a plain, never-processed
        // library GLB) - such a clip is deliberately left OUT of _animLoopByName so
        // RebuildAnimationGrid's own (true, 0) "no data" default applies, rather than forcing
        // every library clip to (false, 0) just because there was nothing to report. The caller
        // (MainForm) resolves the frame index itself via GlbMergeService.NearestLoopFrame, since
        // it's the one holding the actual AnimationClipData needed to look up that clip's own
        // keyframe times.
        public void AddSupplementalFbxAnimations(List<(string Name, string MergedAs, int FrameCount, (bool Loop, int LoopFrame)? LoopInfo)> clips)
        {
            this.SuspendLayout();
            _fbxAnimNames.AddRange(clips.Select(c => (Name: c.Name, MergedAs: c.MergedAs)));
            foreach (var c in clips)
            {
                _animFrameCounts[c.Name] = c.FrameCount;
                if (c.LoopInfo.HasValue) _animLoopByName[c.Name] = c.LoopInfo.Value;
            }
            RebuildAnimationGrid();
            this.ResumeLayout(true);
        }

        // Every row's "Name" currently in the grid (GLB-native and FBX alike), regardless of
        // Include state - used by the caller to keep newly assigned FBX clip names from colliding
        // with anything already present before adding more rows.
        public List<string> GetAllAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
                result.Add((string)row.Cells["Name"].Value!);
            return result;
        }

        // Preserves any per-row settings the user already configured (Include, Merged As, Lock In
        // Place, Fix Floating, Rot, Offset, frame trim, First), keyed by clip name, across a
        // rebuild - so dropping an additional GLB/FBX into this slot only adds rows for its own
        // new clips, without resetting settings on animations that were already there from an
        // earlier drop.
        private void RebuildAnimationGrid()
        {
            var previousSettings = new Dictionary<string, (bool Include, string MergedAs, bool InPlace, bool GroundFix, bool FixBoneLength, bool FixHipRotation, decimal YRotation, decimal YOffset, decimal StartFrame, decimal EndFrame, bool First, bool Loop, decimal LoopFrame)>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                var name = (string)row.Cells["Name"].Value!;
                previousSettings[name] = (
                    row.Cells["Include"].Value is bool inc && inc,
                    (string?)row.Cells["MergedAs"].Value ?? name,
                    row.Cells["InPlace"].Value is bool ip && ip,
                    row.Cells["GroundFix"].Value is bool gf && gf,
                    row.Cells["FixBoneLength"].Value is bool fbl && fbl,
                    row.Cells["FixHipRotation"].Value is bool fhr && fhr,
                    row.Cells["YRotation"].Value is decimal yr ? yr : 0m,
                    row.Cells["YOffset"].Value is decimal yo ? yo : 0m,
                    row.Cells["StartFrame"].Value is decimal sf ? sf : 0m,
                    row.Cells["EndFrame"].Value is decimal ef ? ef : 0m,
                    row.Cells["First"].Value is bool f && f,
                    row.Cells["Loop"].Value is bool lp && lp,
                    row.Cells["LoopFrame"].Value is decimal lf ? lf : 0m);
            }

            grdAnimations.Rows.Clear();

            void AddRow(string name, string defaultMergedAs)
            {
                // 0 (rather than the true keyframe count) for a clip this panel never computed a
                // frame count for - shouldn't happen in practice (every load path sets one), but
                // falling back to "no trim range available" is safer than a misleading bound.
                int lastFrame = Math.Max(0, _animFrameCounts.GetValueOrDefault(name, 1) - 1);

                int rowIndex;
                if (previousSettings.TryGetValue(name, out var s))
                {
                    decimal start = Math.Clamp(s.StartFrame, 0m, lastFrame);
                    decimal end = Math.Clamp(s.EndFrame, start, lastFrame);
                    decimal loopFrame = Math.Clamp(s.LoopFrame, 0m, lastFrame);
                    rowIndex = grdAnimations.Rows.Add(s.Include, name, s.MergedAs, s.InPlace, s.Loop, s.GroundFix, s.FixBoneLength, s.FixHipRotation, s.YRotation, s.YOffset, start, end, loopFrame, s.First);
                }
                else
                {
                    // Both corrections' defaults are set per-panel (see _defaultFixBoneLength/
                    // _defaultFixHipRotation) - on for a slot being retargeted onto a different
                    // rig (model 2), off for model 1's own clips, which are already in the
                    // structural model's own bone lengths/bind pose and need neither. Loop/Loop
                    // Frame default from whatever the source file's own extras already said (see
                    // _animLoopByName) - falling back to (true, frame 0) for a clip with no such
                    // data (FBX, or a GLB never touched by this app before): most clips dropped
                    // into this app are locomotion/idle loops, and a one-shot clip (attack, death,
                    // etc.) is the exception and easy to uncheck per-row.
                    var (defaultLoop, defaultLoopFrame) = _animLoopByName.GetValueOrDefault(name, (true, 0));
                    decimal loopFrame = Math.Clamp((decimal)defaultLoopFrame, 0m, lastFrame);
                    rowIndex = grdAnimations.Rows.Add(true, name, defaultMergedAs, false, defaultLoop, false, _defaultFixBoneLength, _defaultFixHipRotation, 0m, 0m, 0m, (decimal)lastFrame, loopFrame, false);
                }

                var row = grdAnimations.Rows[rowIndex];
                var startCell = (NumericUpDownCell)row.Cells["StartFrame"];
                var endCell = (NumericUpDownCell)row.Cells["EndFrame"];
                var loopFrameCell = (NumericUpDownCell)row.Cells["LoopFrame"];
                startCell.MaximumOverride = lastFrame;
                endCell.MaximumOverride = lastFrame;
                loopFrameCell.MaximumOverride = lastFrame;
                // A clip with fewer than 2 distinct keyframes has nothing to trim (or loop within)
                // - disable the cells rather than offering a range that can only ever be [0, 0].
                startCell.ReadOnly = endCell.ReadOnly = loopFrameCell.ReadOnly = lastFrame == 0;
            }

            foreach (var name in _glbAnimNames)
                AddRow(name, name);
            foreach (var (name, mergedAs) in _fbxAnimNames)
                AddRow(name, mergedAs);
        }

        public List<string> GetSelectedMaterialNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdMaterials.Rows)
            {
                if (row.Cells["Include"].Value is bool included && included)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        // The original name of the material marked "First" (only meaningful if it's also
        // included), or null if none is marked.
        public string? GetFirstMaterialName()
        {
            foreach (DataGridViewRow row in grdMaterials.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool first = row.Cells["First"].Value is bool f && f;
                if (included && first) return (string)row.Cells["Name"].Value!;
            }
            return null;
        }

        // Maps each included material's original name to whatever the user typed in "Merged As",
        // so the merge can rename the material in the output file without ever touching the
        // source model. Only included rows are returned since unselected materials never reach
        // the merge in the first place.
        public Dictionary<string, string> GetMaterialRenameMap()
        {
            var result = new Dictionary<string, string>();
            foreach (DataGridViewRow row in grdMaterials.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;

                var originalName = (string)row.Cells["Name"].Value!;
                var mergedAs = row.Cells["MergedAs"].Value as string;
                result[originalName] = string.IsNullOrWhiteSpace(mergedAs) ? originalName : mergedAs;
            }
            return result;
        }

        // Maps each included material's original name to the set of its channels the user left
        // checked (BaseColor/MetallicRoughness/Normal/Occlusion/Emissive) - passed straight
        // through to GlbMergeService.MergeTargeted's matChannels parameters. Only included rows
        // are returned since unselected materials never reach the merge in the first place.
        public Dictionary<string, HashSet<string>> GetSelectedChannelsByMaterial()
        {
            var result = new Dictionary<string, HashSet<string>>();
            foreach (DataGridViewRow row in grdMaterials.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;

                var originalName = (string)row.Cells["Name"].Value!;
                var channels = new HashSet<string>();
                foreach (var channelName in GlbMergeService.KnownMaterialChannelNames)
                    if (row.Cells[ChannelColumnName(channelName)].Value is bool v && v)
                        channels.Add(channelName);

                result[originalName] = channels;
            }
            return result;
        }

        // Maps each geometry row's original baseName to whatever the user typed in "Merged As",
        // so the merge can rename the output mesh/node without ever touching the source model.
        // Unlike materials/animations there's no "Include" checkbox here - model 1's geometry is
        // always used in full - so every row contributes an entry.
        public Dictionary<string, string> GetGeometryRenameMap()
        {
            var result = new Dictionary<string, string>();
            foreach (DataGridViewRow row in grdGeometry.Rows)
            {
                var originalName = row.Cells["Name"].Value as string;
                if (string.IsNullOrEmpty(originalName)) continue;

                var mergedAs = row.Cells["MergedAs"].Value as string;
                result[originalName] = string.IsNullOrWhiteSpace(mergedAs) ? originalName : mergedAs;
            }
            return result;
        }

        public List<string> GetSelectedAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                if (row.Cells["Include"].Value is bool included && included)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        // The original name of the animation marked "First" (only meaningful if it's also
        // included), or null if none is marked.
        public string? GetFirstAnimationName()
        {
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool first = row.Cells["First"].Value is bool f && f;
                if (included && first) return (string)row.Cells["Name"].Value!;
            }
            return null;
        }

        public List<string> GetGroundFixAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool groundFix = row.Cells["GroundFix"].Value is bool gf && gf;
                if (included && groundFix)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        public List<string> GetFixBoneLengthAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool fixBoneLength = row.Cells["FixBoneLength"].Value is bool fbl && fbl;
                if (included && fixBoneLength)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        public List<string> GetFixHipRotationAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool fixHipRotation = row.Cells["FixHipRotation"].Value is bool fhr && fhr;
                if (included && fixHipRotation)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        public List<string> GetInPlaceAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool inPlace = row.Cells["InPlace"].Value is bool ip && ip;
                if (included && inPlace)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        // Clips whose engine playback should loop back to frame 0 once finished, rather than
        // pause on the last frame - see the "Loop" column's comment above. Passed through to
        // GlbMergeService.MergeTargeted, which stamps the resulting choice onto each output
        // animation's own glTF extras for the engine to read at load time.
        public List<string> GetLoopAnimationNames()
        {
            var result = new List<string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool loop = row.Cells["Loop"].Value is bool lp && lp;
                if (included && loop)
                    result.Add((string)row.Cells["Name"].Value!);
            }
            return result;
        }

        // Which frame (in this clip's own pre-trim numbering) a looping clip should resume at,
        // per included+looping clip - passed to GlbMergeService.MergeTargeted's loopFrameByName1/2,
        // which resolves it to a rebased seconds-offset *after* trimming, so Process Merge never
        // silently resets it back to "loop to frame 0". Only present for rows this method's own
        // GetLoopAnimationNames would also return - a row that isn't looping has nothing to resume.
        public Dictionary<string, int> GetLoopFrameByAnimation()
        {
            var result = new Dictionary<string, int>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                bool included = row.Cells["Include"].Value is bool inc && inc;
                bool loop = row.Cells["Loop"].Value is bool lp && lp;
                if (!included || !loop) continue;

                int loopFrame = row.Cells["LoopFrame"].Value is decimal lf ? (int)lf : 0;
                result[(string)row.Cells["Name"].Value!] = loopFrame;
            }
            return result;
        }

        // Y-axis turn (degrees, 0-359.99) per included clip - only present in the map when
        // non-zero, so callers can treat "not in the dictionary" as "no rotation requested".
        public Dictionary<string, float> GetYRotationByAnimation()
        {
            var result = new Dictionary<string, float>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;
                if (!float.TryParse(row.Cells["YRotation"].Value?.ToString(), out var degrees) || degrees == 0f) continue;

                result[(string)row.Cells["Name"].Value!] = Math.Clamp(degrees, 0f, 359.99f);
            }
            return result;
        }

        // Manual additional Y offset per included clip, applied on top of Fix Floating - only
        // present in the map when non-zero.
        public Dictionary<string, float> GetYOffsetByAnimation()
        {
            var result = new Dictionary<string, float>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;
                if (!float.TryParse(row.Cells["YOffset"].Value?.ToString(), out var offset) || offset == 0f) continue;

                result[(string)row.Cells["Name"].Value!] = Math.Clamp(offset, -9999f, 9999f);
            }
            return result;
        }

        // [Start, End] keyframe-index range per included clip, only present when it doesn't cover
        // the clip's full length (Start != 0 or End != its last frame) - so a row nobody touched
        // costs nothing extra in the merge, same "absent means default" convention as
        // GetYRotationByAnimation/GetYOffsetByAnimation. Passed straight through to
        // GlbMergeService.MergeTargeted's frameTrimByName parameter.
        public Dictionary<string, (int Start, int End)> GetFrameTrimByAnimation()
        {
            var result = new Dictionary<string, (int Start, int End)>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;
                if (row.Cells["StartFrame"] is not NumericUpDownCell startCell) continue;
                if (row.Cells["EndFrame"] is not NumericUpDownCell endCell) continue;
                if (startCell.MaximumOverride is not decimal maxFrame) continue;

                int start = row.Cells["StartFrame"].Value is decimal sv ? (int)sv : 0;
                int end = row.Cells["EndFrame"].Value is decimal ev ? (int)ev : (int)maxFrame;
                if (start == 0 && end == (int)maxFrame) continue;

                result[(string)row.Cells["Name"].Value!] = (start, end);
            }
            return result;
        }

        // Maps each included clip's original name to whatever the user typed in "Merged As",
        // so the merge can rename the animation track in the output file without ever touching
        // the source model/FBX. Only included rows are returned since unselected clips never
        // reach the merge in the first place.
        public Dictionary<string, string> GetAnimationRenameMap()
        {
            var result = new Dictionary<string, string>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                if (row.Cells["Include"].Value is not bool included || !included) continue;

                var originalName = (string)row.Cells["Name"].Value!;
                var mergedAs = row.Cells["MergedAs"].Value as string;
                result[originalName] = string.IsNullOrWhiteSpace(mergedAs) ? originalName : mergedAs;
            }
            return result;
        }

        // Grid column name / header for a known material channel's Include checkbox - kept as
        // small helpers rather than inline string concatenation since both the column-setup code
        // and every reader (LoadMaterialsFromGlb, GetSelectedChannelsByMaterial) need to agree on
        // the exact same column name.
        private static string ChannelColumnName(string channelName) => "Ch_" + channelName;

        private static string ChannelHeaderText(string channelName) => channelName switch
        {
            "MetallicRoughness" => "Metal/Rough",
            _ => channelName
        };

        private static Label MakeLabel(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) };
    }
}
