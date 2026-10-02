namespace GlbMerger
{
    // What part of a building a billboard or source triangle is: the groups the flattener
    // handles in its own way (see ModelFlattener's passes). Labels for now - the Flatten Model
    // editor shows them - and the basis for per-group settings later.
    public enum FeatureGroup : byte
    {
        None,
        Front,
        Side,
        Back,
        Roof,
        Underside,
        Corner,
        Column,
        Overhang,
        Pediment,
        RoofOrnament,
        FireEscape,
        Bay,
        CurvedWall,
        Reveal,
        Backdrop,
        Leftover,
        Dropped,
    }

    public static class FeatureGroups
    {
        public static string Name(FeatureGroup g) => g switch
        {
            FeatureGroup.Front => "Front",
            FeatureGroup.Side => "Sides",
            FeatureGroup.Back => "Back",
            FeatureGroup.Roof => "Roof and tops",
            FeatureGroup.Underside => "Undersides",
            FeatureGroup.Corner => "Corners",
            FeatureGroup.Column => "Columns and pilasters",
            FeatureGroup.Overhang => "Cornices",
            FeatureGroup.Pediment => "Pediments and gables",
            FeatureGroup.RoofOrnament => "Roof ornaments",
            FeatureGroup.FireEscape => "Fire escapes",
            FeatureGroup.Bay => "Bay windows",
            FeatureGroup.CurvedWall => "Curved walls",
            FeatureGroup.Reveal => "Window glass",
            FeatureGroup.Backdrop => "Backdrops",
            FeatureGroup.Leftover => "Kept as mesh",
            FeatureGroup.Dropped => "Dropped",
            _ => "Unlabelled",
        };

        // Preview colour, as 0xRRGGBB - the same in the group list and the 3D view. Distinct
        // hues, since the preview's lighting washes out anything close.
        public static int Color(FeatureGroup g) => g switch
        {
            FeatureGroup.Front => 0x4E79A7,
            FeatureGroup.Side => 0x59A14F,
            FeatureGroup.Back => 0x9C755F,
            FeatureGroup.Roof => 0xEDC948,
            FeatureGroup.Underside => 0x76B7B2,
            FeatureGroup.Corner => 0xF28E2B,
            FeatureGroup.Column => 0x8CD17D,
            FeatureGroup.Overhang => 0xE15759,
            FeatureGroup.Pediment => 0xFF9DA7,
            FeatureGroup.RoofOrnament => 0xB07AA1,
            FeatureGroup.FireEscape => 0x17BECF,
            FeatureGroup.Bay => 0x9467BD,
            FeatureGroup.CurvedWall => 0xBCBD22,
            FeatureGroup.Reveal => 0x393B79,
            FeatureGroup.Backdrop => 0x7F7F7F,
            FeatureGroup.Leftover => 0xE0E0E0,
            FeatureGroup.Dropped => 0x7A1010,
            _ => 0xFF00FF,
        };
    }
}
