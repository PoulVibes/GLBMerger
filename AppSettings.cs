using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GlbMerger
{
    // Persisted UI preferences only (dark mode + how the resizable boxes/panels were last split) -
    // nothing about loaded models, since those are always picked fresh each run. Stored as a
    // fraction (0..1) of each splitter's container rather than an absolute pixel distance, so a
    // saved layout still makes sense if the window reopens at a different size.
    public class AppSettings
    {
        public bool DarkMode { get; set; }
        public double MainSplitFraction { get; set; } = 0.5;
        public double Panel1PrimarySplitFraction { get; set; } = 0.25;
        public double Panel1SecondarySplitFraction { get; set; } = 0.4667;
        public double Panel2PrimarySplitFraction { get; set; } = 0.45;
        public double AnimationTrimPlaybackSpeed { get; set; } = 1.0;
        // ModelEditorForm.EditorMode name of the editor last opened, which the next one starts on.
        public string? LastEditorMode { get; set; }
        public int FlattenStrength { get; set; } = 500;
        public int FlattenMinSize { get; set; } = 40;
        public bool FlattenKeepLeftovers { get; set; } = true;
        public bool FlattenSideDetail { get; set; } = true;
        public int FlattenSideMinSize { get; set; } = 40;
        // ModelFlattener.CurveHandling: 0 = planes, 1 = curved billboards, 2 = keep as mesh.
        public int FlattenCurves { get; set; } = 1;
        public int FlattenDetailBoost { get; set; } = 20;
        public int FlattenDetailStrength { get; set; } = 35;
        public int FlattenRecessDarkening { get; set; } = 30;
        public bool FlattenTrimCorners { get; set; } = true;
        public bool FlattenBakeNormals { get; set; } = true;
        public bool FlattenBakeMetallicRoughness { get; set; } = true;
        // The front walls' triangle budget (0 = none).
        public int FlattenTriangleBudget { get; set; } = 500;
        // Per group (see ModelFlattenerEditor's sections): Strength slider position, -1 = same as
        // the front walls; budgets, 0 = none.
        public int FlattenSideStrength { get; set; } = -1;
        public int FlattenBackStrength { get; set; } = -1;
        public int FlattenRoofStrength { get; set; } = -1;
        public int FlattenSideBudget { get; set; }
        public int FlattenBackBudget { get; set; }
        public int FlattenRoofBudget { get; set; }
        public int FlattenFireEscapeBudget { get; set; }
        public bool FlattenOverhangs { get; set; } = true;
        public bool FlattenEdgeStrips { get; set; } = true;
        public bool FlattenPediments { get; set; } = true;
        public bool FlattenColumns { get; set; } = true;
        // 0 = small roof ornaments stay real triangles (up to FlattenOrnamentMeshMax), 1 = crossing billboards.
        public int FlattenOrnamentMethod { get; set; }
        public int FlattenOrnamentMeshMax { get; set; } = 150;
        public bool FlattenBackdrops { get; set; } = true;
        // Which of the flattener's group sections are open.
        public List<string> FlattenOpenSections { get; set; } = new() { "Walls (front)" };
        // Named sets of the Flatten* values above, and which one they currently match (null =
        // "Custom Configuration", edited since or never saved).
        public List<FlattenConfiguration> FlattenConfigurations { get; set; } = new();
        public string? FlattenConfigurationName { get; set; }
        // Flatten editor viewport background, as #RRGGBB.
        public string FlattenBackgroundColor { get; set; } = "#1A1C1E";
        public string AnimationLibraryDirectory { get; set; } = @"C:\Projects\FootballRoguelite\Graphics\Models\Players";

        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GlbMerger", "settings.json");

        // Best-effort: a missing/corrupt settings file just falls back to defaults rather than
        // blocking startup - these are cosmetic preferences, not data the user would want to lose
        // sleep over.
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                    if (loaded != null) return loaded;
                }
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
            }
            catch { }
        }
    }
}
