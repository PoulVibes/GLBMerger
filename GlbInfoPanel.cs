using System;
using System.Collections.Generic;
using System.Drawing;
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
        private ListBox lstPrimitives;
        private DataGridView grdMaterials;
        private DataGridView grdAnimations;
        private Label lblPrim, lblMat, lblAnim;

        // Slot 2 can independently carry a GLB's own baked-in animations and a supplemental FBX's
        // animations at the same time (e.g. a GLB dropped for materials that also happens to have
        // animations, plus an FBX dropped separately for more). The grid is rebuilt from both
        // lists together whenever either one changes, so loading a new file into one drop target
        // never wipes out whatever is already loaded in the other.
        private List<string> _glbAnimNames = new();
        private List<(string Name, string MergedAs)> _fbxAnimNames = new();

        public event Action<string>? GlbFileDropped;
        public event Action<List<string>>? FbxFilesDropped;

        // Model 2 never uses its own geometry (model 1's is always used), so the "Available
        // Geometry" reference list is meaningless there - showGeometryBox omits it entirely and
        // lets the materials/animations lists expand to fill the space instead.
        public GlbInfoPanel(bool showGeometryBox = true)
        {
            Padding = new Padding(4);

            mainGroup = new GroupBox { Text = "No Model Loaded", Dock = DockStyle.Fill };

            lblPrim = MakeLabel("Available Geometry (Reference Layout)");
            lstPrimitives = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

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
            grdMaterials.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Include", FillWeight = 20 });
            grdMaterials.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Material", ReadOnly = true, FillWeight = 30 });
            grdMaterials.Columns.Add(new DataGridViewTextBoxColumn { Name = "Channels", HeaderText = "Channels", ReadOnly = true, FillWeight = 25 });
            // Editable output name: lets the user rename how a material appears in the merged
            // file, without touching the source model - selection matching still keys off "Name".
            grdMaterials.Columns.Add(new DataGridViewTextBoxColumn { Name = "MergedAs", HeaderText = "Merged As", FillWeight = 25 });
            // Marks which material should be written first into the output's material list (some
            // engines/tools default to index 0) - only one row can be marked at a time.
            grdMaterials.Columns.Add(new DataGridViewCheckBoxColumn { Name = "First", HeaderText = "First", FillWeight = 15 });
            grdMaterials.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grdMaterials.IsCurrentCellDirty)
                    grdMaterials.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grdMaterials.CellValueChanged += (s, e) => EnforceSingleFirst(grdMaterials, e);

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
            // Analyzes the clip's own foot motion to find its most "grounded" pose (feet level
            // with each other and at their lowest point) and shifts the whole clip vertically so
            // that pose's feet align with the target rig's own resting ground height - corrects a
            // uniform float/sink that can happen when a retargeted clip's natural knee-bend leaves
            // its feet slightly off the ground even though the root translation itself is correct.
            grdAnimations.Columns.Add(new DataGridViewCheckBoxColumn { Name = "GroundFix", HeaderText = "Fix Floating", FillWeight = 30 });
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

            // Setup rigid UI distribution matrix
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = showGeometryBox ? 6 : 4,
                Padding = new Padding(4)
            };

            // Setup explicit percentage distributions to prevent "0-height" flattening bugs
            if (showGeometryBox)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Text label
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 25F));     // Prim List
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Text label
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));     // Material List
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Text label
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40F));     // Anim List

                layout.Controls.Add(lblPrim, 0, 0);
                layout.Controls.Add(lstPrimitives, 0, 1);
                layout.Controls.Add(lblMat, 0, 2);
                layout.Controls.Add(grdMaterials, 0, 3);
                layout.Controls.Add(lblAnim, 0, 4);
                layout.Controls.Add(grdAnimations, 0, 5);
            }
            else
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Text label
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45F));     // Material List
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));          // Text label
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55F));     // Anim List

                layout.Controls.Add(lblMat, 0, 0);
                layout.Controls.Add(grdMaterials, 0, 1);
                layout.Controls.Add(lblAnim, 0, 2);
                layout.Controls.Add(grdAnimations, 0, 3);
            }

            mainGroup.Controls.Add(layout);
            Controls.Add(mainGroup);
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

        public void UpdateTitle(string title)
        {
            mainGroup.Text = title;
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
        // slot 1's single-file picker. clipNames are already final, guaranteed-unique names
        // (computed by the caller, e.g. from the FBX's filename) - matching and default "Merged
        // As" both key off them directly.
        public void LoadFbxAnimations(List<string> clipNames)
        {
            this.SuspendLayout();

            lstPrimitives.Items.Clear();
            lstPrimitives.Items.Add("(FBX source - animation only, no geometry)");

            grdMaterials.Rows.Clear();

            _glbAnimNames = new List<string>();
            _fbxAnimNames = clipNames.Select(n => (Name: n, MergedAs: n)).ToList();
            RebuildAnimationGrid();

            this.ResumeLayout(true);
        }

        // Supplemental load for slot 2: populates primitives/materials and this GLB's own baked
        // animations, without touching any FBX animations already loaded via drag-and-drop.
        public void LoadMaterialsFromGlb(string path)
        {
            this.SuspendLayout();

            lstPrimitives.Items.Clear();
            grdMaterials.Rows.Clear();

            var model = ModelRoot.Load(path);

            foreach (var mesh in model.LogicalMeshes)
                foreach (var prim in mesh.Primitives)
                {
                    var matName = prim.Material?.Name ?? "No Material";
                    lstPrimitives.Items.Add($"{mesh.Name ?? "mesh"} [Mat: {matName}]");
                }

            if (lstPrimitives.Items.Count == 0)
                lstPrimitives.Items.Add("(no geometry found)");

            foreach (var mat in model.LogicalMaterials)
            {
                var channels = new List<string>();
                foreach (var ch in new[] { "BaseColor", "Normal", "Emissive", "Occlusion", "MetallicRoughness" })
                {
                    var found = mat.FindChannel(ch);
                    if (found.HasValue && found.Value.Texture != null)
                        channels.Add(ch);
                }
                var matName = mat.Name ?? $"Material_{mat.LogicalIndex}";
                grdMaterials.Rows.Add(true, matName, string.Join(", ", channels), matName, false);
            }

            _glbAnimNames = model.LogicalAnimations.Select(a => a.Name ?? $"Anim_{a.LogicalIndex}").ToList();
            RebuildAnimationGrid();

            this.ResumeLayout(true);
        }

        // Supplemental load for slot 2: ADDS this FBX's clips to the animation grid without
        // touching whatever materials/animations (or previously dropped FBX clips) are already
        // loaded - so dropping multiple FBX files at once, or one after another, accumulates
        // rather than replaces. clipNames are already final, guaranteed-unique names (computed by
        // the caller, e.g. from each FBX's filename).
        public void AddSupplementalFbxAnimations(List<string> clipNames)
        {
            this.SuspendLayout();
            _fbxAnimNames.AddRange(clipNames.Select(n => (Name: n, MergedAs: n)));
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
        // Place, Fix Floating, Rot, Offset, First), keyed by clip name, across a rebuild - so
        // dropping an additional GLB/FBX into this slot only adds rows for its own new clips,
        // without resetting settings on animations that were already there from an earlier drop.
        private void RebuildAnimationGrid()
        {
            var previousSettings = new Dictionary<string, (bool Include, string MergedAs, bool InPlace, bool GroundFix, decimal YRotation, decimal YOffset, bool First)>();
            foreach (DataGridViewRow row in grdAnimations.Rows)
            {
                var name = (string)row.Cells["Name"].Value!;
                previousSettings[name] = (
                    row.Cells["Include"].Value is bool inc && inc,
                    (string?)row.Cells["MergedAs"].Value ?? name,
                    row.Cells["InPlace"].Value is bool ip && ip,
                    row.Cells["GroundFix"].Value is bool gf && gf,
                    row.Cells["YRotation"].Value is decimal yr ? yr : 0m,
                    row.Cells["YOffset"].Value is decimal yo ? yo : 0m,
                    row.Cells["First"].Value is bool f && f);
            }

            grdAnimations.Rows.Clear();

            void AddRow(string name, string defaultMergedAs)
            {
                if (previousSettings.TryGetValue(name, out var s))
                    grdAnimations.Rows.Add(s.Include, name, s.MergedAs, s.InPlace, s.GroundFix, s.YRotation, s.YOffset, s.First);
                else
                    grdAnimations.Rows.Add(true, name, defaultMergedAs, false, false, 0m, 0m, false);
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

        private static Label MakeLabel(string text) =>
            new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 4, 0, 2) };
    }
}
