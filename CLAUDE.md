# GLBMerger

WinForms (.NET 10) app; build with `dotnet build` from this folder.

## UI changes

Before adding, moving or resizing controls in any editor, read [docs/UI_LAYOUT.md](docs/UI_LAYOUT.md).
The left control column has a fixed width budget, and controls have been silently cut off before.
After the change, run the layout check and fix any new `ERROR`:

```bash
dotnet run --project tools/LayoutCheck -- <skinned, animated model.glb>
```

When telling the user about a new control, say where it is in the column. If it's below the
visible area, say they'll need to scroll down to it.
