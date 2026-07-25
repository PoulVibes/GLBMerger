using System;
using System.Windows.Forms;

namespace GlbMerger
{
    // Shared helpers for driving a SplitContainer's distance from a saved 0..1 fraction and
    // reading it back. A SplitContainer throws if SplitterDistance is set before it has real
    // size (construction time, Height/Width still 0), so applying a fraction defers to the
    // control's first real SizeChanged when necessary instead of setting it immediately.
    public static class SplitterFractionPersistence
    {
        public static void ApplyFraction(SplitContainer split, double fraction)
        {
            void Apply()
            {
                int total = split.Orientation == Orientation.Horizontal ? split.Height : split.Width;
                int min = split.Panel1MinSize;
                int max = total - split.Panel2MinSize;
                if (max <= min) return;

                int distance = (int)Math.Round(total * fraction);
                split.SplitterDistance = Math.Clamp(distance, min, max);
            }

            // IsHandleCreated (not just "is the size big enough") is the right gate here: an
            // unparented SplitContainer still reports a placeholder default size (~150x150) that
            // can easily satisfy the min-size check, but applying against that placeholder before
            // it's ever been through real Dock/layout throws the fraction off once real layout
            // does happen. A created handle means it's actually part of a shown window's layout.
            if (split.IsHandleCreated)
            {
                Apply();
                return;
            }

            void OnceHandler(object? s, EventArgs e)
            {
                Apply();
                split.SizeChanged -= OnceHandler;
            }
            split.SizeChanged += OnceHandler;
        }

        public static double GetFraction(SplitContainer split)
        {
            int total = split.Orientation == Orientation.Horizontal ? split.Height : split.Width;
            return total > 0 ? (double)split.SplitterDistance / total : 0.5;
        }
    }
}
