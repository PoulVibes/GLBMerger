using System.Windows.Forms;

namespace GlbMerger
{
    // Small "-"/"+" buttons flanking a TrackBar for fine adjustments (1 unit by default) beyond
    // what dragging the thumb allows. Shared by every editor's rotation/position/frame sliders
    // instead of each reimplementing its own nudge buttons.
    internal static class SliderNudge
    {
        private const int ButtonSize = 22;

        // Shrinks the slider to make room for a button at each end, within the same Left/Top/Width
        // footprint it already had - callers' existing row-pitch layout math doesn't need to
        // change. Returns the two buttons so the caller can add them to whatever Controls
        // collection it's using; they are not added automatically since that collection (and
        // whether it's a flat Controls.AddRange list or a FlowLayoutPanel row) varies per caller.
        public static (Button Minus, Button Plus) Attach(TrackBar slider, int step = 1)
        {
            int left = slider.Left;
            int width = slider.Width;
            int top = slider.Top + (slider.Height - ButtonSize) / 2;

            var minus = new Button { Text = "-", Left = left, Top = top, Width = ButtonSize, Height = ButtonSize, Margin = Padding.Empty };
            var plus = new Button { Text = "+", Left = left + width - ButtonSize, Top = top, Width = ButtonSize, Height = ButtonSize, Margin = Padding.Empty };

            slider.Left = left + ButtonSize + 4;
            slider.Width = width - 2 * (ButtonSize + 4);

            // Value's setter already clamps to [Minimum, Maximum], so no explicit bounds check is
            // needed here - reading slider.Minimum/Maximum at click time (rather than capturing
            // them now) also means this stays correct for AnimationTrimEditor's sliders, whose
            // Maximum changes at runtime once the selected clip's frame count is known.
            minus.Click += (s, e) => slider.Value = System.Math.Max(slider.Minimum, slider.Value - step);
            plus.Click += (s, e) => slider.Value = System.Math.Min(slider.Maximum, slider.Value + step);

            return (minus, plus);
        }
    }
}
