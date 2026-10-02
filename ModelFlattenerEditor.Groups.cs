using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GlbMerger
{
    // The settings panel by group: one collapsible section per part of the building, each with
    // its own settings and, where a setting can trade detail for triangles, its own budget and
    // Fit button.
    public partial class ModelFlattenerEditor
    {
        private FlowLayoutPanel _secWalls = null!, _secSides = null!, _secBack = null!, _secRoof = null!, _secCornices = null!,
            _secCorners = null!, _secColumns = null!, _secPediments = null!, _secOrnaments = null!, _secFire = null!, _secCurves = null!, _secGlass = null!,
            _secBackdrops = null!, _secBake = null!;
        private readonly Dictionary<FlowLayoutPanel, (Button Header, string Title)> _sections = new();

        private GroupStrength _sideStrength = null!, _backStrength = null!, _roofStrength = null!;
        private NumericUpDown _numSideBudget = null!, _numBackBudget = null!, _numRoofBudget = null!, _numFireBudget = null!;
        private CheckBox _chkOverhangs = null!, _chkEdgeStrips = null!, _chkBackdrops = null!, _chkPediments = null!, _chkColumns = null!;
        private ComboBox _cmbOrnaments = null!;
        private NumericUpDown _numOrnamentMax = null!;
        private readonly List<Button> _fitButtons = new();

        // A wall group's own Strength: the front walls' unless unticked.
        private sealed class GroupStrength
        {
            public CheckBox Same = null!;
            public TrackBar Slider = null!;
            public Label Label = null!;
            public FeatureGroup[] Groups = Array.Empty<FeatureGroup>();
            // Slider position, or -1 for "same as front".
            public int Value => Same.Checked ? -1 : Slider.Value;
        }

        // One collapsible section; its body is where its controls and help go.
        private FlowLayoutPanel Section(FlowLayoutPanel flow, string title)
        {
            var header = new Button
            {
                Width = 336, Height = 28, TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 4, 0, 0), FlatStyle = FlatStyle.Flat,
            };
            var body = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(8, 2, 0, 4),
                Visible = _settings.FlattenOpenSections.Contains(title),
            };
            header.Click += (s, e) =>
            {
                body.Visible = !body.Visible;
                _settings.FlattenOpenSections.RemoveAll(t => t == title);
                if (body.Visible) _settings.FlattenOpenSections.Add(title);
                UpdateSectionHeader(body, null);
            };
            flow.Controls.Add(header);
            flow.Controls.Add(body);
            _sections[body] = (header, title);
            UpdateSectionHeader(body, null);
            return body;
        }

        private void UpdateSectionHeader(FlowLayoutPanel body, string? summary)
        {
            var (header, title) = _sections[body];
            if (summary != null) header.Tag = summary;
            string tail = header.Tag as string ?? "";
            header.Text = (body.Visible ? "▾ " : "▸ ") + title + (tail.Length > 0 ? "    " + tail : "");
        }

        // Creates the sections, in the panel's order, before BuildUi fills them.
        private void BuildSections(FlowLayoutPanel flow)
        {
            _secWalls = Section(flow, "Walls (front)");
            _secSides = Section(flow, "Sides");
            _secBack = Section(flow, "Back");
            _secRoof = Section(flow, "Roof and undersides");
            _secCornices = Section(flow, "Cornices");
            _secCorners = Section(flow, "Corners");
            _secColumns = Section(flow, "Columns and pilasters");
            _secPediments = Section(flow, "Pediments and gables");
            _secOrnaments = Section(flow, "Roof ornaments");
            _secFire = Section(flow, "Fire escapes and railings");
            _secCurves = Section(flow, "Curved walls and bays");
            _secGlass = Section(flow, "Window glass");
            _secBackdrops = Section(flow, "Backdrops");
            _secBake = Section(flow, "Bake (whole building)");
        }

        // The controls that are new with the group sections; BuildUi has already put the older
        // ones into their sections.
        private void BuildGroupControls()
        {
            _sideStrength = AddGroupStrength(_secSides, _settings.FlattenSideStrength, new[] { FeatureGroup.Side });
            _numSideBudget = AddBudget(_secSides, _settings.FlattenSideBudget, v => _settings.FlattenSideBudget = v,
                () => FitGroupAsync("Sides", new[] { FeatureGroup.Side }, (int)_numSideBudget.Value, _sideStrength.Slider, _sideStrength.Same,
                    v => SettingsAt(_sliderStrength.Value, side: v)));
            _help.Add(_secSides,
                "Strength for planes facing the building's sides, unless Same as front is ticked. The budget " +
                "counts the triangles the sides end up as; Fit finds the lowest side Strength that meets it.");

            _backStrength = AddGroupStrength(_secBack, _settings.FlattenBackStrength, new[] { FeatureGroup.Back });
            _numBackBudget = AddBudget(_secBack, _settings.FlattenBackBudget, v => _settings.FlattenBackBudget = v,
                () => FitGroupAsync("Back", new[] { FeatureGroup.Back }, (int)_numBackBudget.Value, _backStrength.Slider, _backStrength.Same,
                    v => SettingsAt(_sliderStrength.Value, back: v)));
            _help.Add(_secBack,
                "Strength for planes facing back, unless Same as front is ticked. Backs are often bare shells " +
                "seen only from behind and in shadows, so they can usually be much coarser.");

            _roofStrength = AddGroupStrength(_secRoof, _settings.FlattenRoofStrength, new[] { FeatureGroup.Roof, FeatureGroup.Underside });
            _numRoofBudget = AddBudget(_secRoof, _settings.FlattenRoofBudget, v => _settings.FlattenRoofBudget = v,
                () => FitGroupAsync("Roof and undersides", new[] { FeatureGroup.Roof, FeatureGroup.Underside }, (int)_numRoofBudget.Value,
                    _roofStrength.Slider, _roofStrength.Same, v => SettingsAt(_sliderStrength.Value, roof: v)));
            _help.Add(_secRoof,
                "Strength for planes facing up (roofs, the tops of sills and copings) and down (soffits, the " +
                "undersides of balconies), unless Same as front is ticked.");

            _chkOverhangs = AddToggle(_secCornices, "Flatten cornices as one tilted billboard", _settings.FlattenOverhangs, v => _settings.FlattenOverhangs = v);
            _help.Add(_secCornices,
                "A cornice overhanging the top of a facade becomes one billboard sloping from the wall up to its " +
                "lip, like an awning, showing the whole ledge and its brackets from below. Off: it's cut up " +
                "between the facade, its soffit and backdrops, which leaks sky between the brackets.");

            _chkEdgeStrips = AddToggle(_secCorners, "Give a wall's proud ends their own strip", _settings.FlattenEdgeStrips, v => _settings.FlattenEdgeStrips = v);
            _help.Add(_secCorners,
                "Where the ends of a wall stand out from it (a corner pier, quoins), they get a narrow billboard " +
                "at their own depth, so looking along the facade there's no slit of sky up the far corner.");

            _chkColumns = AddToggle(_secColumns, "Give pilasters their own strips", _settings.FlattenColumns, v => _settings.FlattenColumns = v);
            _help.Add(_secColumns,
                "A pilaster or column standing out from a wall nearly its full height gets a strip at its own " +
                "depth and one for each side face, so seen along the facade it has depth instead of being " +
                "painted flat on the wall. Six triangles each.");

            _chkPediments = AddToggle(_secPediments, "Flatten pediments as a front quad and their slopes", _settings.FlattenPediments, v => _settings.FlattenPediments = v);
            _help.Add(_secPediments,
                "A pediment or gable rising over the cornice (one or two narrow peaks along the top) becomes " +
                "one quad at the depth of its outline, drawing all of it, plus a tilted quad for each sloped " +
                "top - six triangles. Off: it's split between the cornice, the roof and ornaments. Tag one by " +
                "hand (Tag parts) if it isn't found.");

            _cmbOrnaments = new ComboBox { Width = 320, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 0, 3, 4) };
            _cmbOrnaments.Items.AddRange(new object[] { "Keep small ones as real triangles", "Two crossing billboards each" });
            _cmbOrnaments.SelectedIndex = Math.Clamp(_settings.FlattenOrnamentMethod, 0, 1);
            var maxRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            maxRow.Controls.Add(new Label { Text = "Real triangles up to:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            _numOrnamentMax = new NumericUpDown { Width = 70, Minimum = 1, Maximum = 5000, Increment = 25, Value = Math.Clamp(_settings.FlattenOrnamentMeshMax, 1, 5000), Margin = new Padding(0, 4, 3, 3) };
            maxRow.Controls.Add(_numOrnamentMax);
            maxRow.Controls.Add(new Label { Text = "triangles each", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            _cmbOrnaments.SelectedIndexChanged += (s, e) =>
            {
                _settings.FlattenOrnamentMethod = _cmbOrnaments.SelectedIndex;
                _numOrnamentMax.Enabled = _cmbOrnaments.SelectedIndex == 0;
                ScheduleCompute();
            };
            _numOrnamentMax.Enabled = _cmbOrnaments.SelectedIndex == 0;
            _numOrnamentMax.ValueChanged += (s, e) => { _settings.FlattenOrnamentMeshMax = (int)_numOrnamentMax.Value; ScheduleCompute(); };
            _secOrnaments.Controls.Add(_cmbOrnaments);
            _secOrnaments.Controls.Add(maxRow);
            _help.Add(_secOrnaments,
                "Finials, chimney pots and the like standing on the roof. Real triangles are exact from every " +
                "side and cost a little more; bigger ornaments, or all of them with crossing billboards, are drawn " +
                "on two upright billboards crossing at their middle.");

            _numFireBudget = AddBudget(_secFire, _settings.FlattenFireEscapeBudget, v => _settings.FlattenFireEscapeBudget = v,
                () => FitGroupAsync("Fire escapes", new[] { FeatureGroup.FireEscape }, (int)_numFireBudget.Value, _sliderDetailStrength, null,
                    v => SettingsAt(_sliderStrength.Value, detail: v)));
            _help.Add(_secFire,
                "The triangles fire escapes, railings and other see-through structure end up as. Fit finds the " +
                "lowest Detail flattening that meets it.");

            _chkBackdrops = AddToggle(_secBackdrops, "Fill gaps with backdrops", _settings.FlattenBackdrops, v => _settings.FlattenBackdrops = v);
            _help.Add(_secBackdrops,
                "Low-resolution quads behind the billboards, showing the building's own colours through gaps " +
                "seen at a slant instead of sky. They cost two triangles each and a little atlas space.");
        }

        private GroupStrength AddGroupStrength(FlowLayoutPanel body, int saved, FeatureGroup[] groups)
        {
            var g = new GroupStrength
            {
                Groups = groups,
                Same = new CheckBox { Text = "Same Strength as the front walls", AutoSize = true, Checked = saved < 0, Margin = new Padding(3, 0, 3, 2) },
                Label = new Label { AutoSize = true, Margin = new Padding(3, 0, 3, 0) },
                Slider = new TrackBar
                {
                    Width = 320, Height = 45, Minimum = 0, Maximum = 1000, TickFrequency = 100, SmallChange = 5, LargeChange = 50,
                    Value = Math.Clamp(saved < 0 ? _sliderStrength.Value : saved, 0, 1000), Margin = new Padding(3, 0, 3, 4),
                    Enabled = saved >= 0,
                },
            };
            g.Same.CheckedChanged += (s, e) =>
            {
                g.Slider.Enabled = !g.Same.Checked;
                if (g.Same.Checked) g.Slider.Value = _sliderStrength.Value;
                SaveGroupStrengths();
                UpdateLabels();
                ScheduleCompute();
            };
            g.Slider.ValueChanged += (s, e) =>
            {
                if (g.Same.Checked) return;
                SaveGroupStrengths();
                UpdateLabels();
                ScheduleCompute();
            };
            body.Controls.Add(g.Same);
            body.Controls.Add(g.Label);
            body.Controls.Add(g.Slider);
            return g;
        }

        private void SaveGroupStrengths()
        {
            _settings.FlattenSideStrength = _sideStrength.Value;
            _settings.FlattenBackStrength = _backStrength.Value;
            _settings.FlattenRoofStrength = _roofStrength.Value;
        }

        // The front walls' slider moved: groups that follow it move with it.
        private void FollowFrontStrength()
        {
            foreach (var g in new[] { _sideStrength, _backStrength, _roofStrength })
                if (g != null && g.Same.Checked) g.Slider.Value = _sliderStrength.Value;
        }

        private NumericUpDown AddBudget(FlowLayoutPanel body, int saved, Action<int> store, Func<Task>? fit)
        {
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
            row.Controls.Add(new Label { Text = "Budget:", AutoSize = true, Margin = new Padding(3, 7, 6, 3) });
            var num = new NumericUpDown { Width = 80, Minimum = 0, Maximum = 1000000, Increment = 25, Value = Math.Clamp(saved, 0, 1000000), Margin = new Padding(0, 4, 6, 3) };
            num.ValueChanged += (s, e) => { store((int)num.Value); if (_result != null) ShowGroups(_result); };
            row.Controls.Add(num);
            row.Controls.Add(new Label { Text = "tris (0 = none)", AutoSize = true, Margin = new Padding(0, 7, 6, 3) });
            if (fit != null)
            {
                var button = new Button { Text = "Fit", AutoSize = true, Margin = new Padding(0, 3, 3, 3) };
                button.Click += async (s, e) => { if (num.Value > 0) await fit(); };
                _fitButtons.Add(button);
                row.Controls.Add(button);
            }
            body.Controls.Add(row);
            return num;
        }

        private CheckBox AddToggle(FlowLayoutPanel body, string text, bool saved, Action<bool> store)
        {
            var c = new CheckBox { Text = text, AutoSize = true, Checked = saved, Margin = new Padding(3, 0, 3, 4) };
            c.CheckedChanged += (s, e) => { store(c.Checked); ScheduleCompute(); };
            body.Controls.Add(c);
            return c;
        }

        // The tolerance scale of each wall group that has its own Strength, relative to `front`.
        private Dictionary<FeatureGroup, float> GroupScales(int front, int? side, int? back, int? roof)
        {
            var scales = new Dictionary<FeatureGroup, float>();
            void Put(GroupStrength? g, int? over)
            {
                if (g == null) return;
                int v = over ?? g.Value;
                if (v < 0) return;
                float scale = Fraction(v) / Fraction(front);
                foreach (var group in g.Groups) scales[group] = scale;
            }
            Put(_sideStrength, side);
            Put(_backStrength, back);
            Put(_roofStrength, roof);
            return scales;
        }

        private string GroupStrengthText(GroupStrength g) =>
            g.Same.Checked ? "Strength: same as the front walls"
                : $"Strength: {FormatPercent(Fraction(g.Slider.Value) * 100f)} of model size";

        // Triangles the given groups end up as: their billboards and what of them stays mesh.
        private static int GroupOut(ModelFlattener.FlattenResult r, ICollection<FeatureGroup> groups)
        {
            int n = 0;
            foreach (var bb in r.Billboards)
                if (groups.Contains(bb.Group)) n += bb.Curve != null ? 2 * bb.Curve.Segments : 2;
            for (int t = 0; t < r.Assignment.Length && t < r.TriangleGroup.Length; t++)
                if (r.Assignment[t] == ModelFlattener.FlattenResult.Kept && groups.Contains(r.TriangleGroup[t])) n++;
            return n;
        }

        // Each group's section, the groups it covers, and its budget (0: none or not budgeted).
        private IEnumerable<(FlowLayoutPanel Section, FeatureGroup[] Groups, int Budget)> Budgets()
        {
            yield return (_secWalls, new[] { FeatureGroup.Front }, (int)_numBudget.Value);
            yield return (_secSides, new[] { FeatureGroup.Side }, (int)_numSideBudget.Value);
            yield return (_secBack, new[] { FeatureGroup.Back }, (int)_numBackBudget.Value);
            yield return (_secRoof, new[] { FeatureGroup.Roof, FeatureGroup.Underside }, (int)_numRoofBudget.Value);
            yield return (_secCornices, new[] { FeatureGroup.Overhang }, 0);
            yield return (_secCorners, new[] { FeatureGroup.Corner }, 0);
            yield return (_secColumns, new[] { FeatureGroup.Column }, 0);
            yield return (_secPediments, new[] { FeatureGroup.Pediment }, 0);
            yield return (_secOrnaments, new[] { FeatureGroup.RoofOrnament }, 0);
            yield return (_secFire, new[] { FeatureGroup.FireEscape }, (int)_numFireBudget.Value);
            yield return (_secCurves, new[] { FeatureGroup.CurvedWall, FeatureGroup.Bay }, 0);
            yield return (_secGlass, new[] { FeatureGroup.Reveal }, 0);
            yield return (_secBackdrops, new[] { FeatureGroup.Backdrop }, 0);
        }

        // Every group section's header: how many billboards the group made and the triangles it
        // ends up as (against its budget, if it has one). In the "coloured by group" preview the
        // header takes the group's colour, so the panel reads as the preview's key; over budget
        // it's orange whatever the preview. Returns which groups are over budget, for the list.
        private Dictionary<FeatureGroup, bool> UpdateBudgetHeaders(ModelFlattener.FlattenResult r)
        {
            var over = new Dictionary<FeatureGroup, bool>();
            bool byGroup = Mode == PreviewMode.Groups;
            foreach (var (section, groups, budget) in Budgets())
            {
                int n = GroupOut(r, groups);
                int boards = r.Billboards.Count(b => groups.Contains(b.Group));
                bool isOver = budget > 0 && n > budget;
                foreach (var g in groups) over[g] = isOver;
                string tris = budget > 0 ? $"{n:N0} / {budget:N0} tris{(isOver ? "  OVER" : "")}" : $"{n:N0} tris";
                UpdateSectionHeader(section, $"{boards:N0} billboard{(boards == 1 ? "" : "s")}, {tris}");
                int rgb = FeatureGroups.Color(groups[0]);
                _sections[section].Header.ForeColor = isOver ? System.Drawing.Color.DarkOrange
                    : byGroup ? System.Drawing.Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255)
                    : ForeColor;
            }
            return over;
        }

        // Binary search over a group's slider for the lowest value whose flatten keeps that
        // group within its budget. Assumes a higher value never means more triangles - true of
        // Strength and of Detail flattening, roughly.
        private async Task FitGroupAsync(string name, FeatureGroup[] groups, int budget, TrackBar slider, CheckBox? same,
            Func<int, ModelFlattener.FlattenSettings> settingsFor)
        {
            if (_input == null || budget <= 0) return;
            var activity = BeginActivity($"Fitting {name} to its budget");
            var input = CurrentInput(activity);
            var progress = ProgressFor(activity);
            int min = slider.Minimum, max = slider.Maximum;
            var settings = Enumerable.Range(min, max - min + 1).Select(settingsFor).ToArray();
            var deleted = _deletedStrokes.SelectMany(s => s).ToList();
            float slack = _input.Extent * 1e-3f;
            var set = new HashSet<FeatureGroup>(groups);

            foreach (var b in _fitButtons) b.Enabled = false;
            _btnFitBudget.Enabled = false;
            _lblStatus.Text = $"Searching for {name} settings that fit {budget:N0} triangles...";
            try
            {
                var (value, triangles) = await Task.Run(() =>
                {
                    int tries = (int)Math.Ceiling(Math.Log2(max - min + 1)) + 1, tried = 0;
                    int Count(int v)
                    {
                        int k = tried++;
                        var slice = new SliceProgress(progress, (double)k / tries, (double)(k + 1) / tries, $"try {k + 1} of up to {tries}");
                        return GroupOut(WithoutDeleted(ModelFlattener.Compute(input, settings[v - min], CancellationToken.None, slice), deleted, slack), set);
                    }
                    int atMax = Count(max);
                    if (atMax > budget) return (-1, atMax);
                    int lo = min, hi = max, best = atMax;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        int n = Count(mid);
                        if (n <= budget) { hi = mid; best = n; } else lo = mid + 1;
                    }
                    return (lo, best);
                });
                if (IsDisposed) return;
                if (value < 0)
                {
                    _lblStatus.Text = $"Even at its coarsest, {name.ToLowerInvariant()} come to {triangles:N0} triangles - over the {budget:N0} budget.";
                    return;
                }
                if (same != null) same.Checked = false;
                slider.Value = value;
                _lblStatus.Text = $"{name} fitted to the budget: {triangles:N0} triangles.";
            }
            finally
            {
                EndActivity(activity);
                if (!IsDisposed)
                {
                    foreach (var b in _fitButtons) b.Enabled = true;
                    _btnFitBudget.Enabled = true;
                }
            }
        }
    }
}
