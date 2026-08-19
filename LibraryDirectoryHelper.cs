using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GlbMerger
{
    // Shared by every "animation library" dropdown (MainForm's Model 2 panel, Ball Anchor editor,
    // Stiff Arm Poses editor) so they all list a folder's .glb files, and display them in a
    // ComboBox, the same way.
    internal static class LibraryDirectoryHelper
    {
        // ComboBox item wrapper so a dropdown can show just the filename while still handing the
        // full path back to the caller on selection.
        public sealed class LibraryEntry
        {
            public required string Path { get; init; }
            public override string ToString() => System.IO.Path.GetFileNameWithoutExtension(Path);
        }

        // Every .glb file directly inside `directory` (non-recursive), sorted by full path. A
        // missing/inaccessible directory just yields an empty list rather than throwing, since
        // this can run from a stale saved setting before the user has confirmed the path still
        // exists.
        public static List<string> ListGlbFiles(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    return Directory.GetFiles(directory, "*.glb")
                        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .ToList();
            }
            catch
            {
                // best-effort - an unreadable directory just yields an empty list
            }
            return new List<string>();
        }
    }
}
