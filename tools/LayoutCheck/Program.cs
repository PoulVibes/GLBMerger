using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using GlbMerger;
using SharpGLTF.Schema2;

// Opens the Model Editor on a real model, switches through every mode at each window size below,
// and walks the live control tree looking for layout mistakes a person would only find by
// scrolling around (or not find at all). See docs/UI_LAYOUT.md for what each finding means and
// how to fix it.
//
//   dotnet run --project tools/LayoutCheck -- <model.glb> [mode filter]
//
// Exit code 0 = no errors (warnings allowed), 1 = at least one error, 2 = bad arguments/crash.
internal static class Program
{
    // The Model Editor's default size and its MinimumSize (ModelEditorForm constructor). Content
    // that only fits at the default is still broken for anyone who shrinks the window.
    private static readonly Size[] WindowSizes = { new(1060, 880), new(780, 620) };

    private static int _errors, _warnings;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("usage: LayoutCheck <model.glb> [mode filter]");
            return 2;
        }
        string? filter = args.Length > 1 ? args[1] : null;

        // An editor that pops a modal dialog would block this forever; give up loudly instead.
        using var watchdog = new System.Threading.Timer(_ =>
        {
            Console.Error.WriteLine("LayoutCheck timed out (an editor probably opened a modal dialog).");
            Environment.Exit(2);
        }, null, TimeSpan.FromMinutes(3), Timeout.InfiniteTimeSpan);

        ApplicationConfiguration.Initialize();
        var model = ModelRoot.Load(args[0]);

        // A throwaway AppSettings: switching modes only changes it in memory, and nothing here
        // calls Save, so the user's real settings file is never touched.
        using var form = new ModelEditorForm(model, darkMode: false, new AppSettings(), args[0]);
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(0, 0);
        form.Show();
        Pump();

        var dropdown = (ComboBox)typeof(ModelEditorForm)
            .GetField("_modeDropdown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(form)!;

        foreach (var size in WindowSizes)
        {
            form.Size = size;
            Pump();

            for (int i = 0; i < dropdown.Items.Count; i++)
            {
                string mode = dropdown.Items[i]!.ToString()!;
                if (filter != null && !mode.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    dropdown.SelectedIndex = i;
                    Pump();
                }
                catch (Exception ex)
                {
                    Report(true, $"{size.Width}x{size.Height} {mode}", "editor", $"threw while opening: {ex.Message}");
                    continue;
                }

                Check(form, $"{size.Width}x{size.Height} {mode}", "");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_errors} error(s), {_warnings} warning(s).");
        return _errors > 0 ? 1 : 0;
    }

    private static void Pump()
    {
        for (int i = 0; i < 5; i++) Application.DoEvents();
    }

    private static void Check(Control control, string where, string path)
    {
        foreach (Control child in control.Controls)
        {
            if (!child.Visible) continue;
            string childPath = path + "/" + Describe(child);

            CheckReachable(child, control, where, childPath);
            CheckClippedText(child, where, childPath);

            if (child is ScrollableControl { AutoScroll: true } scroller && scroller.HorizontalScroll.Visible)
                Report(false, where, childPath,
                    $"horizontal scrollbar is showing - content is {scroller.DisplayRectangle.Width}px wide in a {scroller.ClientSize.Width}px viewport " +
                    "(usually the vertical scrollbar eating ~17px of a column sized with no room for it)");

            if (ShouldRecurse(child)) Check(child, where, childPath);
        }
    }

    // The bug this tool was written for: a control placed where its parent can't show it and
    // can't scroll to it. An AutoScroll parent's DisplayRectangle IS its scrollable extent, so
    // anything inside that is reachable; for any other parent only the client area is.
    private static void CheckReachable(Control child, Control parent, string where, string path)
    {
        // DisplayRectangle has the parent's Padding taken off, but the scroll range still reaches
        // a control sitting in that padding, so it is added back.
        var area = parent.ClientRectangle;
        if (parent is ScrollableControl { AutoScroll: true } scroller)
        {
            var display = scroller.DisplayRectangle;
            var pad = scroller.Padding;
            display = Rectangle.FromLTRB(display.Left - pad.Left, display.Top - pad.Top, display.Right + pad.Right, display.Bottom + pad.Bottom);
            area = Rectangle.Union(display, area);
        }
        area.Inflate(1, 1);

        if (area.Contains(child.Bounds)) return;

        var b = child.Bounds;
        string side = b.Bottom > area.Bottom ? $"bottom edge at {b.Bottom}, parent shows to {area.Bottom - 1}"
            : b.Right > area.Right ? $"right edge at {b.Right}, parent shows to {area.Right - 1}"
            : $"starts at ({b.X},{b.Y}), outside the parent";
        Report(true, where, path, $"cut off and not scrollable into view ({side})");
    }

    // Fixed-size text controls whose text no longer fits - usually a label or button whose
    // wording was lengthened after its Width was picked.
    private static void CheckClippedText(Control c, string where, string path)
    {
        if (c.AutoSize || string.IsNullOrEmpty(c.Text)) return;
        if (c is not (Label or ButtonBase)) return;

        Size needed;
        bool clipped;
        if (c is Label)
        {
            // Wrapped to its own width, so this is "does it need more lines than it has room for".
            needed = c.GetPreferredSize(new Size(c.Width, 0));
            clipped = needed.Height > c.Height + 1;
        }
        else if (c is Button)
        {
            // The bare text against the face, not GetPreferredSize: that adds the button's own
            // padding and flags every tiny "+"/"-" nudge button (SliderNudge) whose glyph fits fine.
            needed = TextRenderer.MeasureText(c.Text, c.Font);
            clipped = needed.Width > c.Width - 4 || needed.Height > c.Height - 2;
        }
        else
        {
            needed = c.GetPreferredSize(Size.Empty);
            clipped = needed.Width > c.Width + 1;
        }

        if (clipped)
            Report(false, where, path, $"text doesn't fit ({c.Width}x{c.Height}, needs {needed.Width}x{needed.Height})");
    }

    // Containers only. Composite controls (ComboBox, NumericUpDown, DataGridView...) own private
    // child windows that are laid out by the control itself, and WebView2 is a browser, not a
    // WinForms tree.
    private static bool ShouldRecurse(Control c) =>
        c is Panel or GroupBox or SplitContainer or TabControl or UserControl or Form;

    private static string Describe(Control c)
    {
        string text = c.Text.Length > 30 ? c.Text[..30] + "…" : c.Text;
        return text.Length > 0 ? $"{c.GetType().Name}'{text}'" : c.GetType().Name;
    }

    private static void Report(bool error, string where, string path, string message)
    {
        if (error) _errors++; else _warnings++;
        Console.WriteLine($"{(error ? "ERROR" : "warn ")} [{where}] {path}: {message}");
    }
}
