using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlbMerger
{
    // What the Flatten Model editor saves for one model, next to its GLB as
    // <name>.flatten.json: the parts tagged by hand and the billboards deleted. Triangle numbers
    // are the gathered input's (ModelFlattener.Gather), which follow the GLB's own order; the
    // Model block identifies the GLB they belong to, so a changed model is noticed instead of
    // having its tags land on the wrong triangles.
    public sealed class FlattenOverrides
    {
        public int Version { get; set; } = 1;
        public ModelInfo Model { get; set; } = new();
        public List<TagEntry> Overrides { get; set; } = new();
        // One list per delete (for Undo), each billboard as where it was.
        public List<List<DeletedEntry>> Deleted { get; set; } = new();

        public sealed class ModelInfo
        {
            public string File { get; set; } = "";
            public int Triangles { get; set; }
            public float[][] Bounds { get; set; } = Array.Empty<float[]>();
        }

        public sealed class TagEntry
        {
            public string Id { get; set; } = "";
            public string Label { get; set; } = "";
            [JsonConverter(typeof(JsonStringEnumConverter))]
            public FeatureGroup Group { get; set; }
            public bool Enabled { get; set; } = true;
            // How it was made: a box searched for the group, or painting (grown or not).
            public BoxEntry? Box { get; set; }
            public int[]? Painted { get; set; }
            public bool Grow { get; set; }
            // What was accepted - what re-flattening uses.
            public int[] Found { get; set; } = Array.Empty<int>();
        }

        public sealed class BoxEntry
        {
            public float[] Center { get; set; } = new float[3];
            public float[] Size { get; set; } = new float[3];
        }

        public sealed class DeletedEntry
        {
            public float[] Normal { get; set; } = new float[3];
            public float[] Center { get; set; } = new float[3];
            public float Size { get; set; }
            public int Kind { get; set; }
        }

        public static string PathFor(string modelPath) => Path.ChangeExtension(modelPath, ".flatten.json");

        public static ModelInfo Describe(string modelPath, ModelFlattener.FlattenInput input) => new()
        {
            File = Path.GetFileName(modelPath),
            Triangles = input.TriangleCount,
            Bounds = new[]
            {
                new[] { Round(input.BoundsMin.X), Round(input.BoundsMin.Y), Round(input.BoundsMin.Z) },
                new[] { Round(input.BoundsMax.X), Round(input.BoundsMax.Y), Round(input.BoundsMax.Z) },
            },
        };

        // Whether this file was made for the model `current` describes.
        public bool Matches(ModelInfo current)
        {
            if (Model.Triangles != current.Triangles || Model.Bounds.Length != 2 || current.Bounds.Length != 2) return false;
            for (int i = 0; i < 2; i++)
                for (int k = 0; k < 3; k++)
                    if (MathF.Abs(Model.Bounds[i][k] - current.Bounds[i][k]) > 1e-3f) return false;
            return true;
        }

        private static float Round(float v) => MathF.Round(v, 4);

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Null when there's no file; throws when there's one that can't be read.
        public static FlattenOverrides? Load(string path) =>
            File.Exists(path) ? JsonSerializer.Deserialize<FlattenOverrides>(File.ReadAllText(path), Options) : null;

        public void Save(string path)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, path, overwrite: true);
        }

        public static float[] Vec(Vector3 v) => new[] { v.X, v.Y, v.Z };
        public static Vector3 Vec(float[] a) => a.Length >= 3 ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;
    }
}
