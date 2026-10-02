using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Windows.Forms;

namespace GlbMerger
{
    // Tagging parts of the model by hand (a box searched for a group, or painting), and the
    // <model>.flatten.json file that keeps the tags and the deleted billboards between sessions.
    public partial class ModelFlattenerEditor
    {
        private readonly string? _modelPath;
        private readonly List<FlattenOverrides.TagEntry> _tags = new();
        // The file there was made for a different version of the model: it isn't overwritten
        // without asking.
        private bool _overridesForOtherModel;

        private ComboBox _cmbTagGroup = null!;
        private CheckBox _chkToolBox = null!, _chkToolPaint = null!, _chkToolErase = null!, _chkTagGrow = null!;
        private Button _btnTagAccept = null!, _btnTagWhole = null!, _btnTagCancel = null!, _btnTagRename = null!, _btnTagDelete = null!;
        private Label _lblTagPending = null!, _lblTagFile = null!;
        private CheckedListBox _lstTags = null!;
        private bool _listingTags;

        // What a box search or a painting found, waiting for Accept.
        private FlattenOverrides.BoxEntry? _pendingBox;
        private int[]? _pendingPainted;
        private List<int> _pendingFound = new(), _pendingInBox = new();

        private string? OverridesPath =>
            _modelPath != null && string.Equals(Path.GetExtension(_modelPath), ".glb", StringComparison.OrdinalIgnoreCase)
                ? FlattenOverrides.PathFor(_modelPath) : null;

        private FeatureGroup TagGroup => FeatureTagging.Taggable[Math.Clamp(_cmbTagGroup.SelectedIndex, 0, FeatureTagging.Taggable.Length - 1)];

        // The enabled tags, for the flattener.
        private List<FeatureTag> ActiveTags() =>
            _tags.Where(t => t.Enabled && t.Found.Length > 0).Select(t => new FeatureTag { Group = t.Group, Triangles = t.Found }).ToList();

        private void BuildTagUi(FlowLayoutPanel flow)
        {
            flow.Controls.Add(new Label { Text = "Tag parts", AutoSize = true, Margin = new Padding(3, 8, 3, 2) });

            var groupRow = Row();
            groupRow.Controls.Add(new Label { Text = "Tag as:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _cmbTagGroup = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 3, 3, 3) };
            foreach (var g in FeatureTagging.Taggable) _cmbTagGroup.Items.Add(FeatureGroups.Name(g));
            _cmbTagGroup.SelectedIndex = Array.IndexOf(FeatureTagging.Taggable, FeatureGroup.RoofOrnament);
            groupRow.Controls.Add(_cmbTagGroup);
            flow.Controls.Add(groupRow);

            var toolRow = Row();
            _chkToolBox = ToolButton("Draw Box");
            _chkToolPaint = ToolButton("Paint");
            _chkToolErase = ToolButton("Erase");
            toolRow.Controls.AddRange(new Control[] { _chkToolBox, _chkToolPaint, _chkToolErase });
            flow.Controls.Add(toolRow);
            _chkTagGrow = new CheckBox { Text = "Grow to the whole feature", AutoSize = true, Checked = true, Margin = new Padding(3, 0, 3, 4) };
            flow.Controls.Add(_chkTagGrow);

            _lblTagPending = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(330, 0), Margin = new Padding(3, 2, 3, 2) };
            flow.Controls.Add(_lblTagPending);
            var pendingRow = Row();
            _btnTagAccept = new Button { Text = "Accept", AutoSize = true, Enabled = false, Margin = new Padding(3, 0, 6, 3) };
            _btnTagWhole = new Button { Text = "Take Whole Box", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 6, 3) };
            _btnTagCancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 3, 3) };
            _btnTagAccept.Click += (s, e) => AcceptPending(wholeBox: false);
            _btnTagWhole.Click += (s, e) => AcceptPending(wholeBox: true);
            _btnTagCancel.Click += (s, e) => CancelPending();
            pendingRow.Controls.AddRange(new Control[] { _btnTagAccept, _btnTagWhole, _btnTagCancel });
            flow.Controls.Add(pendingRow);

            _lstTags = new CheckedListBox { Width = 330, Height = 96, CheckOnClick = false, IntegralHeight = false, Margin = new Padding(3, 2, 3, 2) };
            _lstTags.ItemCheck += (s, e) =>
            {
                if (_listingTags || e.Index < 0 || e.Index >= _tags.Count) return;
                _tags[e.Index].Enabled = e.NewValue == CheckState.Checked;
                BeginInvoke(new Action(TagsChanged));
            };
            _lstTags.SelectedIndexChanged += (s, e) =>
            {
                bool any = _lstTags.SelectedIndex >= 0;
                _btnTagRename.Enabled = _btnTagDelete.Enabled = any;
                if (any && _pendingFound.Count == 0) ShowTriangles(_tags[_lstTags.SelectedIndex].Found, Array.Empty<int>());
            };
            flow.Controls.Add(_lstTags);
            var listRow = Row();
            _btnTagRename = new Button { Text = "Rename...", AutoSize = true, Enabled = false, Margin = new Padding(3, 0, 6, 3) };
            _btnTagDelete = new Button { Text = "Delete Tag", AutoSize = true, Enabled = false, Margin = new Padding(0, 0, 3, 3) };
            _btnTagRename.Click += (s, e) => RenameTag();
            _btnTagDelete.Click += (s, e) => DeleteTag();
            listRow.Controls.AddRange(new Control[] { _btnTagRename, _btnTagDelete });
            flow.Controls.Add(listRow);
            _lblTagFile = new Label { AutoSize = true, MaximumSize = new System.Drawing.Size(330, 0), Margin = new Padding(3, 0, 3, 6) };
            flow.Controls.Add(_lblTagFile);

            _help.Add(flow,
                "Tell the flattener what a part of the building is. Draw Box: drag over the part, " +
                "pull the box's faces to fit it, and the box is searched for the chosen group - what " +
                "it found shows pink, the rest of the box red (while drawing boxes the right button " +
                "orbits). Paint: left-drag on the model adds triangles to the painting (red), right-drag " +
                "or Ctrl takes them away again, stroke after stroke; dragging beside the model orbits. " +
                "With Grow ticked the painting spreads to the connected pieces of the same feature " +
                "(pink). Accept makes it a tag; Erase removes triangles from tags. Tick a tag to use it; " +
                "tags and deleted billboards are saved next to the model as <name>.flatten.json.");

            static FlowLayoutPanel Row() => new()
            {
                FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 2),
            };
        }

        private CheckBox ToolButton(string text)
        {
            var b = new CheckBox { Text = text, Appearance = Appearance.Button, AutoSize = true, Margin = new Padding(3, 0, 3, 3), TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
            b.CheckedChanged += (s, e) =>
            {
                if (!b.Checked) { if (!_chkToolBox.Checked && !_chkToolPaint.Checked && !_chkToolErase.Checked) SetTagTool("none"); return; }
                foreach (var other in new[] { _chkToolBox, _chkToolPaint, _chkToolErase })
                    if (other != b) other.Checked = false;
                SetTagTool(b == _chkToolBox ? "box" : b == _chkToolPaint ? "paint" : "erase");
            };
            return b;
        }

        private void SetTagTool(string tool)
        {
            if (tool != "none")
            {
                _chkPaintDelete.Checked = false;
                if (Mode != PreviewMode.Groups) _previewDropdown.SelectedIndex = (int)PreviewMode.Groups;
            }
            if (_viewerReady && _webView.CoreWebView2 != null)
                _ = _webView.CoreWebView2.ExecuteScriptAsync($"setTagTool('{tool}');");
        }

        private void PushFrame()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null || _input == null) return;
            string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
            var x = _input.FrameX;
            var z = _input.FrameZ;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setFrame({F(x.X)},{F(x.Y)},{F(x.Z)},{F(z.X)},{F(z.Y)},{F(z.Z)},{F(_input.Extent)});");
        }

        // The viewer drew or reshaped a box: search it for the chosen group.
        private void OnTagBox(Vector3 center, Vector3 size)
        {
            if (_input == null) return;
            var box = new FeatureBox(center, size);
            _pendingBox = new FlattenOverrides.BoxEntry { Center = FlattenOverrides.Vec(center), Size = FlattenOverrides.Vec(size) };
            _pendingPainted = null;
            _pendingInBox = FeatureTagging.InBox(_input, box);
            _pendingFound = FeatureTagging.FindInBox(_input, TagGroup, box).ToList();
            var found = new HashSet<int>(_pendingFound);
            ShowTriangles(_pendingFound, _pendingInBox.Where(t => !found.Contains(t)).ToArray());
            _lblTagPending.Text = _pendingInBox.Count == 0
                ? "Nothing lies wholly inside the box - pull its faces out to take in the part."
                : _pendingFound.Count == 0
                    ? $"No {FeatureGroups.Name(TagGroup).ToLowerInvariant()} found in the box ({_pendingInBox.Count:N0} triangles in it). Take Whole Box to tag all of them."
                    : $"{_pendingFound.Count:N0} of the {_pendingInBox.Count:N0} triangles in the box look like {FeatureGroups.Name(TagGroup).ToLowerInvariant()} (pink).";
            UpdateTagButtons();
        }

        // The viewer finished a brush stroke.
        private void OnTagPaint(List<int> painted, bool erase)
        {
            if (_input == null) return;
            // Everything painted was taken away again: nothing to accept.
            if (painted.Count == 0 && !erase)
            {
                _pendingPainted = null;
                _pendingFound = new List<int>();
                _lblTagPending.Text = "";
                ShowTriangles(Array.Empty<int>(), Array.Empty<int>());
                UpdateTagButtons();
                return;
            }
            if (painted.Count == 0) return;
            if (erase)
            {
                var gone = new HashSet<int>(painted);
                foreach (var tag in _tags)
                {
                    tag.Found = tag.Found.Where(t => !gone.Contains(t)).ToArray();
                    if (tag.Painted != null) tag.Painted = tag.Painted.Where(t => !gone.Contains(t)).ToArray();
                }
                _tags.RemoveAll(t => t.Found.Length == 0);
                TagsChanged();
                _lblTagPending.Text = $"Erased {painted.Count:N0} triangle(s) from the tags.";
                return;
            }
            var seed = painted.Distinct().ToList();
            _pendingBox = null;
            _pendingInBox = new List<int>();
            _pendingPainted = seed.ToArray();
            _pendingFound = _chkTagGrow.Checked
                ? FeatureTagging.GrowFeature(_input, seed, new HashSet<int>(_tags.Where(t => t.Enabled).SelectMany(t => t.Found)))
                : seed;
            var painted0 = new HashSet<int>(seed);
            ShowTriangles(_pendingFound.Where(t => !painted0.Contains(t)).ToArray(), seed.ToArray());
            _lblTagPending.Text = _chkTagGrow.Checked
                ? $"{seed.Count:N0} painted (red), grown to {_pendingFound.Count:N0} with the rest of the feature (pink)."
                : $"{seed.Count:N0} painted.";
            UpdateTagButtons();
        }

        private void AcceptPending(bool wholeBox)
        {
            var tris = wholeBox ? _pendingInBox : _pendingFound;
            if (tris.Count == 0) return;
            int n = 1;
            while (_tags.Any(t => t.Id == $"t{n}")) n++;
            int sameGroup = _tags.Count(t => t.Group == TagGroup) + 1;
            // A triangle belongs to one tag: the newest.
            var taken = new HashSet<int>(tris);
            foreach (var tag in _tags) tag.Found = tag.Found.Where(t => !taken.Contains(t)).ToArray();
            _tags.RemoveAll(t => t.Found.Length == 0);
            _tags.Add(new FlattenOverrides.TagEntry
            {
                Id = $"t{n}",
                Label = $"{FeatureGroups.Name(TagGroup)} {sameGroup}",
                Group = TagGroup,
                Box = _pendingBox,
                Painted = _pendingPainted,
                Grow = _pendingPainted != null && _chkTagGrow.Checked,
                Found = tris.Distinct().OrderBy(t => t).ToArray(),
            });
            CancelPending();
            TagsChanged();
            _lblTagPending.Text = $"Tagged {tris.Count:N0} triangles as {FeatureGroups.Name(TagGroup).ToLowerInvariant()}.";
        }

        private void CancelPending()
        {
            _pendingBox = null;
            _pendingPainted = null;
            _pendingFound = new List<int>();
            _pendingInBox = new List<int>();
            _lblTagPending.Text = "";
            ShowTriangles(Array.Empty<int>(), Array.Empty<int>());
            if (_viewerReady && _webView.CoreWebView2 != null) _ = _webView.CoreWebView2.ExecuteScriptAsync("clearTagBox(); clearTagPaint();");
            UpdateTagButtons();
        }

        private void UpdateTagButtons()
        {
            _btnTagAccept.Enabled = _pendingFound.Count > 0;
            _btnTagWhole.Enabled = _pendingBox != null && _pendingInBox.Count > 0;
            _btnTagCancel.Enabled = _pendingBox != null || _pendingPainted != null;
        }

        private void RenameTag()
        {
            int i = _lstTags.SelectedIndex;
            if (i < 0) return;
            string? name = NamePrompt.Show(this, "Rename Tag", "Name for this tag:", _tags[i].Label, _darkMode);
            if (string.IsNullOrWhiteSpace(name)) return;
            _tags[i].Label = name;
            RefreshTagList();
            SaveOverrides();
        }

        private void DeleteTag()
        {
            int i = _lstTags.SelectedIndex;
            if (i < 0) return;
            _tags.RemoveAt(i);
            ShowTriangles(Array.Empty<int>(), Array.Empty<int>());
            TagsChanged();
        }

        // Tags changed: list, file, and a new flatten with them.
        private void TagsChanged()
        {
            RefreshTagList();
            SaveOverrides();
            _changedSinceApply = true;
            ScheduleCompute();
        }

        private void RefreshTagList()
        {
            _listingTags = true;
            try
            {
                int sel = _lstTags.SelectedIndex;
                _lstTags.Items.Clear();
                foreach (var tag in _tags)
                    _lstTags.Items.Add($"{tag.Label}  ({FeatureGroups.Name(tag.Group)}, {tag.Found.Length:N0} tris)", tag.Enabled);
                if (sel >= 0 && sel < _lstTags.Items.Count) _lstTags.SelectedIndex = sel;
            }
            finally { _listingTags = false; }
            _btnTagRename.Enabled = _btnTagDelete.Enabled = _lstTags.SelectedIndex >= 0;
        }

        // Pink and red overlays on source triangles in the viewer.
        private void ShowTriangles(IEnumerable<int> pink, IEnumerable<int> red)
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setTagPending([{string.Join(",", pink)}],[{string.Join(",", red)}]);");
        }

        // --- the file ----------------------------------------------------------------------------

        private void LoadOverrides()
        {
            string? path = OverridesPath;
            if (path == null || _input == null)
            {
                _lblTagFile.Text = "This model wasn't loaded from a GLB, so tags and deletions aren't saved.";
                return;
            }
            FlattenOverrides? file;
            try { file = FlattenOverrides.Load(path); }
            catch (Exception ex)
            {
                _overridesForOtherModel = true;
                _lblTagFile.Text = $"Couldn't read {Path.GetFileName(path)}: {ex.Message}";
                return;
            }
            if (file == null)
            {
                _lblTagFile.Text = $"Tags and deletions will be saved in {Path.GetFileName(path)}.";
                return;
            }
            if (!file.Matches(FlattenOverrides.Describe(_modelPath!, _input)))
            {
                _overridesForOtherModel = true;
                _lblTagFile.Text = $"{Path.GetFileName(path)} was made for a different version of this model " +
                    $"({file.Model.Triangles:N0} triangles, this one has {_input.TriangleCount:N0}), so its tags weren't used.";
                return;
            }
            _tags.AddRange(file.Overrides.Where(t => FeatureTagging.Taggable.Contains(t.Group)));
            foreach (var stroke in file.Deleted)
                _deletedStrokes.Add(stroke.Select(d => new DeletedBillboard(FlattenOverrides.Vec(d.Normal), FlattenOverrides.Vec(d.Center), d.Size, d.Kind)).ToList());
            RefreshTagList();
            int deleted = _deletedStrokes.Sum(s => s.Count);
            _lblTagFile.Text = $"Loaded {_tags.Count} tag(s) and {deleted} deleted billboard(s) from {Path.GetFileName(path)}.";
        }

        private void SaveOverrides()
        {
            string? path = OverridesPath;
            if (path == null || _input == null || _modelPath == null) return;
            if (_overridesForOtherModel)
            {
                if (MessageBox.Show(this,
                        $"{Path.GetFileName(path)} was made for a different version of this model. Replace it with this model's " +
                        "tags and deletions? (The old file is kept as .bak.)",
                        "Replace tags file", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                    return;
                try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { }
                _overridesForOtherModel = false;
            }
            var file = new FlattenOverrides
            {
                Model = FlattenOverrides.Describe(_modelPath, _input),
                Overrides = _tags.ToList(),
                Deleted = _deletedStrokes.Select(s => s.Select(d => new FlattenOverrides.DeletedEntry
                {
                    Normal = FlattenOverrides.Vec(d.Normal), Center = FlattenOverrides.Vec(d.Center), Size = d.Size, Kind = d.Kind,
                }).ToList()).ToList(),
            };
            try
            {
                file.Save(path);
                _lblTagFile.Text = $"Saved in {Path.GetFileName(path)}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _lblTagFile.Text = $"Couldn't save {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        // Tag messages from the viewer; false when the message wasn't one.
        private bool OnTagMessage(string? action, JsonElement root, List<int> indices)
        {
            switch (action)
            {
                case "tagBox":
                    if (root.TryGetProperty("center", out var c) && root.TryGetProperty("size", out var sz))
                        OnTagBox(ReadVec(c), ReadVec(sz));
                    return true;
                case "tagPaint": OnTagPaint(indices, erase: false); return true;
                case "tagErase": OnTagPaint(indices, erase: true); return true;
                case "tagCancel": CancelPending(); return true;
            }
            return false;

            static Vector3 ReadVec(JsonElement e)
            {
                var v = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
                return v.Length >= 3 ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;
            }
        }
    }
}
