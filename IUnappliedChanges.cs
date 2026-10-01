namespace GlbMerger
{
    // An editor whose edits wait for an explicit Apply / Save / Bake before they reach the model.
    // ModelEditorForm asks before closing one (Done, the window's close box, or switching to another
    // editor - which disposes it) while it has some, since closing simply throws them away.
    internal interface IUnappliedChanges
    {
        bool HasUnappliedChanges { get; }

        // What would be lost, for the question - "joint adjustments", "painted regions", ...
        string UnappliedChangesDescription { get; }
    }
}
