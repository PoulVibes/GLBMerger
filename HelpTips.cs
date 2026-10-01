using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace GlbMerger
{
    // Hover help for an editor's settings, in place of the paragraphs of grey text that used to
    // sit under every slider. Add(container, text) gives the text, as a tooltip, to the controls
    // added to that container since the last Add for it - a setting's one-line name label, the
    // slider or checkbox itself, and everything inside a row - which is how the old help labels
    // were placed: right after what they described. Help that comes first in a section (nothing
    // added since the last one) goes to the controls added after it instead, until the next Add.
    internal sealed class HelpTips
    {
        private readonly ToolTip _tip = new()
        {
            AutoPopDelay = 30000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true,
        };
        private readonly Dictionary<Control, int> _boundary = new();
        private readonly Dictionary<Control, string> _pending = new();

        public HelpTips(Control owner) => owner.Disposed += (s, e) => _tip.Dispose();

        public void Add(Control container, string text)
        {
            text = Wrap(text);
            int from = _boundary.GetValueOrDefault(container), to = container.Controls.Count;
            if (to > from)
            {
                for (int i = from; i < to; i++) Attach(container.Controls[i], text);
                _pending.Remove(container);
                // The help label also kept one setting apart from the next; keep the gap.
                var last = container.Controls[to - 1];
                if (last.Margin.Bottom < 10) last.Margin = new Padding(last.Margin.Left, last.Margin.Top, last.Margin.Right, 10);
            }
            else if (_pending.TryGetValue(container, out var earlier))
                _pending[container] = earlier + "\n\n" + text;
            else
            {
                _pending[container] = text;
                container.ControlAdded += (s, e) =>
                {
                    if (_pending.TryGetValue(container, out var forward)) Attach(e.Control!, forward);
                };
            }
            _boundary[container] = to;
        }

        // The control and everything in it: a tooltip on a row panel alone doesn't show while
        // the pointer is over the label or box inside it.
        private void Attach(Control c, string text)
        {
            string? existing = _tip.GetToolTip(c);
            _tip.SetToolTip(c, string.IsNullOrEmpty(existing) ? text : existing + "\n\n" + text);
            foreach (Control child in c.Controls) Attach(child, text);
        }

        // Tooltips don't wrap on their own; a paragraph on one line runs right across the screen.
        private static string Wrap(string text, int width = 72)
        {
            var sb = new StringBuilder();
            foreach (var paragraph in text.Replace("\r", "").Split('\n'))
            {
                if (sb.Length > 0) sb.Append('\n');
                int line = 0;
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line > 0 && line + 1 + word.Length > width) { sb.Append('\n'); line = 0; }
                    else if (line > 0) { sb.Append(' '); line++; }
                    sb.Append(word);
                    line += word.Length;
                }
            }
            return sb.ToString();
        }
    }
}
