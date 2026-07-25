using System.Drawing;
using System.Windows.Forms;

namespace GlbMerger
{
    // Recursively paints a control tree for dark/light mode. Applied once whenever the mode
    // changes (or once at startup for dialogs that don't live-toggle), rather than baked into
    // each form's own construction, so every form in the app shares one definition of "dark".
    public static class ThemeManager
    {
        public static readonly Color DarkBack = Color.FromArgb(32, 32, 32);
        public static readonly Color DarkPanel = Color.FromArgb(45, 45, 48);
        public static readonly Color DarkFore = Color.Gainsboro;
        public static readonly Color DarkFieldBack = Color.FromArgb(37, 37, 38);
        public static readonly Color DarkGridHeader = Color.FromArgb(51, 51, 55);
        public static readonly Color DarkGridLine = Color.FromArgb(63, 63, 70);
        public static readonly Color DarkSelection = Color.FromArgb(0, 122, 204);

        public static void Apply(Control root, bool dark)
        {
            ApplyToControl(root, dark);
            foreach (Control child in root.Controls)
                Apply(child, dark);
        }

        private static void ApplyToControl(Control control, bool dark)
        {
            switch (control)
            {
                case DataGridView grid:
                    StyleGrid(grid, dark);
                    break;
                case GroupBox gb:
                    gb.BackColor = dark ? DarkBack : SystemColors.Control;
                    gb.ForeColor = dark ? DarkFore : SystemColors.ControlText;
                    break;
                case Button btn:
                    btn.BackColor = dark ? DarkPanel : SystemColors.Control;
                    btn.ForeColor = dark ? DarkFore : SystemColors.ControlText;
                    btn.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    btn.FlatAppearance.BorderColor = dark ? DarkGridLine : SystemColors.ControlDark;
                    break;
                case CheckBox chk:
                    chk.ForeColor = dark ? DarkFore : SystemColors.ControlText;
                    break;
                case ListBox lb:
                    lb.BackColor = dark ? DarkFieldBack : SystemColors.Window;
                    lb.ForeColor = dark ? DarkFore : SystemColors.WindowText;
                    break;
                case ComboBox cmb:
                    cmb.BackColor = dark ? DarkFieldBack : SystemColors.Window;
                    cmb.ForeColor = dark ? DarkFore : SystemColors.WindowText;
                    break;
                case TextBoxBase tb:
                    tb.BackColor = dark ? DarkFieldBack : SystemColors.Window;
                    tb.ForeColor = dark ? DarkFore : SystemColors.WindowText;
                    break;
                case Label lbl:
                    lbl.ForeColor = dark ? DarkFore : SystemColors.ControlText;
                    break;
                case SplitContainer split:
                    split.BackColor = dark ? DarkGridLine : SystemColors.ControlDark;
                    break;
                case Panel pnl:
                    pnl.BackColor = dark ? DarkBack : SystemColors.Control;
                    break;
                case Form form:
                    form.BackColor = dark ? DarkBack : SystemColors.Control;
                    form.ForeColor = dark ? DarkFore : SystemColors.ControlText;
                    break;
            }
        }

        private static void StyleGrid(DataGridView grid, bool dark)
        {
            grid.EnableHeadersVisualStyles = !dark;
            grid.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D;
            grid.BackgroundColor = dark ? DarkFieldBack : SystemColors.Window;
            grid.GridColor = dark ? DarkGridLine : SystemColors.ControlDark;

            grid.DefaultCellStyle.BackColor = dark ? DarkFieldBack : SystemColors.Window;
            grid.DefaultCellStyle.ForeColor = dark ? DarkFore : SystemColors.WindowText;
            grid.DefaultCellStyle.SelectionBackColor = dark ? DarkSelection : SystemColors.Highlight;
            grid.DefaultCellStyle.SelectionForeColor = dark ? Color.White : SystemColors.HighlightText;

            grid.ColumnHeadersDefaultCellStyle.BackColor = dark ? DarkGridHeader : SystemColors.Control;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = dark ? DarkFore : SystemColors.ControlText;

            grid.RowHeadersDefaultCellStyle.BackColor = dark ? DarkGridHeader : SystemColors.Control;
            grid.RowHeadersDefaultCellStyle.ForeColor = dark ? DarkFore : SystemColors.ControlText;
        }
    }
}
