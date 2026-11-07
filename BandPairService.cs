using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger;

// The character is the authoritative animated rig. Equipment keeps its mesh,
// textures, skin weights and inverse binds; only matching rig tracks are copied.
public static class BandPairService
{
    public static IReadOnlyDictionary<string, Node> Validate(ModelRoot character, ModelRoot equipment)
    {
        var nodes = UniqueNodes(character);
        var instrumentNodes = UniqueNodes(equipment);
        if (equipment.LogicalSkins.Count == 0)
            throw new InvalidOperationException("Load the rigged Equipment.glb for this performer, sharing its origin and skeleton.");
        foreach (var skin in equipment.LogicalSkins)
            for (int i = 0; i < skin.JointsCount; ++i)
            {
                var (joint, _) = skin.GetJoint(i);
                if (string.IsNullOrEmpty(joint.Name) || !nodes.TryGetValue(joint.Name, out var match))
                    throw new InvalidOperationException($"Instrument joint '{joint.Name}' is missing from the character rig.");
                if (joint.VisualParent?.Name != match.VisualParent?.Name)
                    throw new InvalidOperationException($"Joint '{joint.Name}' has a different parent. Use a matching character/equipment pair.");
            }
        foreach (var animation in character.LogicalAnimations)
            foreach (var channel in animation.Channels)
                if (channel.TargetNode?.Name is { } name && instrumentNodes.ContainsKey(name))
                {
                    var mode = channel.TargetNodePath switch {
                        PropertyPath.rotation => channel.GetRotationSampler().InterpolationMode,
                        PropertyPath.translation => channel.GetTranslationSampler().InterpolationMode,
                        PropertyPath.scale => channel.GetScaleSampler().InterpolationMode,
                        _ => AnimationInterpolationMode.LINEAR
                    };
                    if (mode == AnimationInterpolationMode.CUBICSPLINE)
                        throw new InvalidOperationException("Band editing requires linear or step joint tracks. Export the band clips with sampled keys first.");
                }
        return nodes;
    }

    private static Dictionary<string, Node> UniqueNodes(ModelRoot model)
    {
        var groups = model.LogicalNodes.Where(n => !string.IsNullOrEmpty(n.Name)).GroupBy(n => n.Name!);
        var ambiguous = groups.FirstOrDefault(g => g.Count() > 1);
        if (ambiguous != null) throw new InvalidOperationException($"Rig node name '{ambiguous.Key}' is not unique.");
        return groups.ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    public static ModelRoot Synchronize(ModelRoot character, ModelRoot equipment)
    {
        var source = Validate(character, equipment);
        var result = equipment.DeepClone();
        var target = UniqueNodes(result);
        foreach (var (name, node) in target)
            if (source.TryGetValue(name, out var match) && node.Mesh == null && match.Mesh == null)
                node.LocalTransform = match.LocalTransform;
        foreach (var animation in character.LogicalAnimations)
        {
            var clip = animation.Name ?? $"Anim_{animation.LogicalIndex}";
            var outputAnimation = result.LogicalAnimations.FirstOrDefault(a => a.Name == clip) ?? result.CreateAnimation(clip);
            foreach (var channel in animation.Channels)
            {
                if (channel.TargetNode?.Name is not { } name || !target.TryGetValue(name, out var node) || node.Mesh != null) continue;
                switch (channel.TargetNodePath)
                {
                    case PropertyPath.rotation:
                        var rotation = channel.GetRotationSampler();
                        outputAnimation.CreateRotationChannel(node, rotation.GetLinearKeys().ToDictionary(k => k.Key, k => k.Value), rotation.InterpolationMode != AnimationInterpolationMode.STEP);
                        break;
                    case PropertyPath.translation:
                        var translation = channel.GetTranslationSampler();
                        outputAnimation.CreateTranslationChannel(node, translation.GetLinearKeys().ToDictionary(k => k.Key, k => k.Value), translation.InterpolationMode != AnimationInterpolationMode.STEP);
                        break;
                    case PropertyPath.scale:
                        var scale = channel.GetScaleSampler();
                        outputAnimation.CreateScaleChannel(node, scale.GetLinearKeys().ToDictionary(k => k.Key, k => k.Value), scale.InterpolationMode != AnimationInterpolationMode.STEP);
                        break;
                }
            }
            var output = result.LogicalAnimations.FirstOrDefault(a => a.Name == clip);
            if (output != null) output.Extras = animation.Extras?.DeepClone();
        }
        return result;
    }

    // Both files are prepared before replacing either destination. A failed
    // second replacement rolls the first one back instead of leaving a mixed pair.
    public static (string Character, string Instrument) Export(ModelRoot character, ModelRoot equipment, string folder, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Choose a valid performer file name.", nameof(name));
        var synchronized = Synchronize(character, equipment);
        var characterPath = Path.Combine(folder, "Characters", name + ".glb");
        var instrumentPath = Path.Combine(folder, "Instruments", name + "Equipment.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(characterPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(instrumentPath)!);
        var paths = new[] { characterPath, instrumentPath };
        var token = Guid.NewGuid().ToString("N");
        var temps = paths.Select(p => p + "." + token + ".tmp").ToArray();
        var backups = paths.Select(p => p + "." + token + ".bak").ToArray();
        var installed = new bool[2];
        try
        {
            character.SaveGLB(temps[0]); synchronized.SaveGLB(temps[1]);
            for (int i = 0; i < 2; ++i)
            {
                if (File.Exists(paths[i])) File.Move(paths[i], backups[i]);
                File.Move(temps[i], paths[i]); installed[i] = true;
            }
        }
        catch
        {
            for (int i = 1; i >= 0; --i)
            {
                if (installed[i]) File.Delete(paths[i]);
                if (File.Exists(backups[i])) File.Move(backups[i], paths[i]);
            }
            throw;
        }
        finally
        {
            foreach (var temp in temps) if (File.Exists(temp)) File.Delete(temp);
        }
        foreach (var backup in backups) if (File.Exists(backup)) File.Delete(backup);
        return (characterPath, instrumentPath);
    }
}


