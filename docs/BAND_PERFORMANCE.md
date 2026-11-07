# Band Performance editor

Open a character GLB in **Model Editor**, then select **Band Performance** in the
editor dropdown. Matching `../Instruments/<Name>Equipment.glb` loads automatically;
otherwise choose **Load Instrument...** at the top of the left column.

Use a rigged character/equipment pair with the same origin, joint names, hierarchy
and source scale. Equipment retains its mesh, materials, skin weights and inverse
binds. Do not normalize the parts independently. Unrigged equipment and mismatched
joint hierarchies are rejected. Joint tracks must use linear or step interpolation.

The editor starts with **Play**. Source BPM defaults to 120 and Preview BPM to 138;
these change preview speed, preserving authored clip timing. Scrub with the time
slider or seconds field to pause on a playing pose. The beat display uses source
time. Scroll below the instrument controls for animation, pause/resume, joint
selection, visible joint markers, rotation and position controls. Those controls
work like **Fix Joint Orientation**, including mirror/invert and per-animation
corrections. Equipment follows corrected joints and sockets during playback.

Use **Save Adjustments to Animation** for the selected clip, or **Save Adjustments
to All Animations** deliberately. Corrections are offsets over the selected clip,
not individual timeline keyframes. The existing joint reset controls remain available.

At the top of the column, **Export Character + Instrument...** saves pending changes
for the selected clip, then writes `Characters/<Name>.glb` and
`Instruments/<Name>Equipment.glb` under your chosen folder. It copies the character's
matching rig channels into the equipment and preserves equipment geometry and
textures. Both files are staged before replacement; a failed second replacement
rolls back the first. Existing output files require confirmation. The main window's
**Save** saves only the character; use paired export for a game-ready assembly.
Source input files are unchanged unless you explicitly select their folder for export.

## Validation

From the project directory:

```powershell
dotnet build
dotnet run --project tools/LayoutCheck -- <character.glb> "Band Performance"
dotnet run --project tools/BandCheck -- <character.glb> <equipment.glb> <qa-folder> --ui
```

BandCheck samples every joint across all clips, checks geometry preservation,
paired export/re-export, mismatch rejection, rollback and input hashes. `--ui`
also checks instrument auto-load, default Play, tempo, scrub/pause, live pose sync,
paused corrections, clip switching and minimum-size scrolling, saving preview images.
