using System.Windows.Forms;

namespace GlbMerger
{
    // One named set of Flatten Model settings, saved from the flattener's Configuration dropdown
    // and kept in AppSettings. Same units as the AppSettings.Flatten* values (raw slider
    // positions), so a configuration and the editor's live settings compare directly.
    public sealed record FlattenConfiguration
    {
        public string Name { get; set; } = "";
        public int Strength { get; set; } = 500;
        public int TriangleBudget { get; set; } = 500;
        public int MinSize { get; set; } = 40;
        public bool KeepLeftovers { get; set; } = true;
        public bool SideDetail { get; set; } = true;
        public int SideMinSize { get; set; } = 40;
        public int Curves { get; set; } = 1;
        public int DetailStrength { get; set; } = 35;
        public int RecessDarkening { get; set; } = 30;
        public bool TrimCorners { get; set; } = true;
        public bool BakeNormals { get; set; } = true;
        public bool BakeMetallicRoughness { get; set; } = true;
        public int DetailBoost { get; set; } = 20;

        // Whether the two hold the same settings, whatever they're called.
        public bool SameSettingsAs(FlattenConfiguration other) => this with { Name = "" } == other with { Name = "" };
    }

    // Asks for a configuration's name. WinForms has no input box of its own.
    internal static class NamePrompt
    {
        public static string? Show(IWin32Window owner, string title, string prompt, string initial, bool darkMode)
        {
            using var dlg = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10),
            };
            var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill };
            var label = new Label { Text = prompt, AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
            var box = new TextBox { Text = initial, Width = 300, Margin = new Padding(3, 0, 3, 10) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true, Anchor = AnchorStyles.Right };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            layout.Controls.Add(label, 0, 0);
            layout.SetColumnSpan(label, 2);
            layout.Controls.Add(box, 0, 1);
            layout.SetColumnSpan(box, 2);
            layout.Controls.Add(ok, 0, 2);
            layout.Controls.Add(cancel, 1, 2);
            dlg.Controls.Add(layout);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            box.TextChanged += (s, e) => ok.Enabled = box.Text.Trim().Length > 0;
            ok.Enabled = initial.Trim().Length > 0;
            ThemeManager.Apply(dlg, darkMode);
            dlg.Shown += (s, e) => box.SelectAll();

            return dlg.ShowDialog(owner) == DialogResult.OK ? box.Text.Trim() : null;
        }
    }
}
