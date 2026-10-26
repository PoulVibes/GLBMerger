# Adding controls to an editor without hiding them

Every Model Editor mode has the same layout: a fixed-width control column on the left and the 3D
preview filling the rest. Controls added to that column have gone missing more than once: they were
cut off at the right edge, pushed past the bottom, or squeezed out of a fixed-size host panel. This
page covers how the column is built, the size budget it has, and the check to run after any UI change.

## How the control column is built

```csharp
var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, AutoScroll = true };   // the column
var flow = new FlowLayoutPanel
{
    Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false,
    AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
};
controlPanel.Controls.Add(flow);
```

- **Height is handled for you.** `controlPanel` scrolls vertically, so a taller column is fine
  as long as everything sits inside `flow` (or, in the older editors such as
  `JointOrientationEditor`, is placed with `Left`/`Top` directly inside `controlPanel`).
  Content added below the visible area can only be reached by scrolling. If you tell someone about
  a new control, also tell them where it is.
- **Width is NOT handled for you.** `flow` is docked, so its width is always exactly
  `controlPanel`'s client width. It can't grow wider, and the column never scrolls sideways.
  Anything wider than that is silently cut off at the right edge.

## The width budget

```
usable width = controlPanel.Width − 17 (vertical scrollbar) − flow.Padding (left + right)
               − every enclosing group's Padding, border and Margin
```

Reserve the 17px **every time**, even if the column doesn't scroll on your screen today. At the
editor's minimum size (780×620) most columns scroll, and the scrollbar takes its 17px out of
the same width.

| Column `Width` | Client width with scrollbar | Inside `flow` (`Padding` 12) |
|---|---|---|
| 340 | 323 | 299 |
| 360 | 343 | 319 |
| 380 | 363 | 339 |

For a boxed group inside `flow` (for example Model Adjuster's "Adjust Bones" box: `Padding` 8,
`FixedSingle` border, children with `Margin` 3), subtract another ~20px. In that box, children
can be at most ~320px wide in a 380 column.

If something has to be wider, widen `controlPanel.Width` rather than shrinking the 3D view's
share in some other way, and re-run the check.

## Fixed-size hosts

A plain `Panel` with a fixed `Width`/`Height` doesn't scroll and doesn't grow. Every slider
lives in one, because `SliderNudge.Attach` places its `-`/`+` buttons by absolute position:

```csharp
var slider = new TrackBar { Width = 300, Height = 45, ... };
var (minus, plus) = SliderNudge.Attach(slider);           // shrinks the slider to fit both buttons
var host = new Panel { Width = 306, Height = 45, ... };   // must hold slider + buttons
host.Controls.Add(minus); host.Controls.Add(slider); host.Controls.Add(plus);
```

Keep the host at least as big as the slider's original `Width`/`Height`. If you change one,
change the other.

## Text

- Labels whose text changes at runtime (status lines, "Moves 3 child joints: …") should be
  `AutoSize = true` with `MaximumSize = new Size(<budget width>, 0)`, so they wrap and grow taller
  instead of being clipped.
- A fixed `Width`/`Height` label or button needs re-checking whenever its wording gets longer.

## Checking a change: `tools/LayoutCheck`

`tools/LayoutCheck` opens the real Model Editor on a model, switches through every mode at the
default (1060×880) and minimum (780×620) window sizes, and walks the live control tree:

```bash
dotnet run --project tools/LayoutCheck -- path/to/model.glb
```

Add a second argument to check only matching modes, e.g. `-- model.glb "Model Adjuster"`. Use a
skinned, animated model, so that the editors that disable or hide controls on a static mesh
show everything. The exit code is 1 if there are any errors.

| Finding | Meaning | Usual fix |
|---|---|---|
| `ERROR … cut off and not scrollable into view (right edge …)` | The control is wider than the column's usable width (see the budget above). | Narrow it, or widen `controlPanel.Width`. |
| `ERROR … cut off and not scrollable into view (bottom edge …)` | The control is below the bottom of a parent that can't scroll: a fixed-size host panel, or a column without `AutoScroll`. | Enlarge the host, or move the control into the scrolling column. |
| `warn … horizontal scrollbar is showing` | Content is wider than its scrolling viewport, usually from not reserving the 17px. | As for the right-edge error. |
| `warn … text doesn't fit` | A fixed-size label or button is too small for its text. | Make it `AutoSize`, or enlarge it. |

The tool only sees controls that are **visible right after the mode opens**. A control that
only appears after a selection or a checkbox isn't checked. Open that state by hand, or extend
the tool to select it before checking.

### After every UI change

1. `dotnet build` the app.
2. Run `tools/LayoutCheck` on a skinned, animated model. No new `ERROR`s.
3. Open the app, shrink the Model Editor to its minimum size, select the mode you changed, scroll
   the column to the bottom, and confirm every new control is there and fully visible.

## Known findings (2026-10-22)

These were already present when the tool was added, so they aren't regressions from new work:

- **Ball Anchor**, **Eye Anchors**, **Brows & Mouth**: column `Width = 340` (299px usable inside
  `flow`). Their grouped boxes extend 9–14px past the visible edge, so their right side is clipped.
- **Rigging (Move Joints)**: two fixed-height description labels need 75px but have 72px.
