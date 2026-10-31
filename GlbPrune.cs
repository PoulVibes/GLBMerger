using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Lossless removal of data nothing in the file reads any more, applied to the written GLB.
    //
    // SharpGLTF can add to a model but never release: every Node.WithXAnimation call that
    // replaces a track (trim, loop smoothing, branch Size, rig normalising, ScaleKeySync, clip
    // restore) mints a new sampler and two new accessors and leaves the old sampler sitting in the
    // animation with no channel pointing at it - and so its keyframe bytes in the buffer, forever.
    // Rebuilding through SceneBuilder (GeometryOptimizer.BuildCleanedCopy) drops them too, but
    // re-emits the whole scene and can lose editor markers; this edits only the glTF index tables,
    // so everything still referenced is written back byte for byte.
    //
    // Prunes, in dependency order: animation samplers no channel uses, accessors nothing uses,
    // buffer views nothing uses, then repacks the binary chunk. Nodes, meshes, materials and
    // images are left alone. A file using an extension that can reference accessors or buffer
    // views on its own, or with more than the one embedded buffer, is written unchanged rather
    // than risk dropping something only that extension reads.
    public static class GlbPrune
    {
        public sealed record Stats(int Samplers, int Accessors, int BufferViews, long Bytes)
        {
            public bool Any => Samplers + Accessors + BufferViews > 0;
        }

        private static readonly HashSet<string> SafeExtensions = new(StringComparer.Ordinal)
        {
            "KHR_materials_emissive_strength", "KHR_materials_unlit", "KHR_materials_specular",
            "KHR_materials_ior", "KHR_materials_transmission", "KHR_materials_clearcoat",
            "KHR_materials_sheen", "KHR_materials_volume", "KHR_texture_transform",
            "KHR_mesh_quantization", "KHR_lights_punctual", "KHR_texture_basisu", "EXT_texture_webp",
        };

        private const uint JsonChunk = 0x4E4F534A, BinChunk = 0x004E4942;

        // What RemoveUnusedNodes takes out: leftover bones (animated) and empty nodes (not), by
        // name, the animation channels that keyed the bones, and any clip left with no channels
        // at all (dropped too - glTF requires at least one).
        public sealed record UnusedNodes(IReadOnlyList<string> Bones, IReadOnlyList<string> Empty,
            int Channels, IReadOnlyList<string> EmptiedClips);

        // The one way this app writes a GLB the user keeps. `removeUnusedNodes`, when given, is
        // asked whether to remove the unused nodes found (only called when there are some).
        public static Stats Save(ModelRoot model, string path, Func<UnusedNodes, bool>? removeUnusedNodes = null)
        {
            var glb = model.WriteGLB().ToArray();
            if (removeUnusedNodes != null) glb = RemoveUnusedNodes(glb, removeUnusedNodes).Glb;
            var (bytes, stats) = Prune(glb);
            File.WriteAllBytes(path, bytes);
            return stats;
        }

        // Removes nodes nothing draws with or reads, of two kinds. Both are in no skin, have no
        // mesh, camera, skin, extension or extras, and have only such nodes as children:
        //   - Leftover bones: ones some animation still keys. That's what Normalize Rig's "fold
        //     extra joints" leaves - it moves an extra joint's weights onto a shared joint and
        //     drops it from the skin, but SharpGLTF can't delete the node, so e.g. Eli kept every
        //     finger bone as a plain animated node (which the game's and this app's hand lookup
        //     then matched by name). No vertex can be weighted to a node outside every skin, so
        //     the mesh is untouched.
        //   - Empty nodes: unanimated ones NOT parented to a skin joint - the placeholders older
        //     merges left ("char1", "char1_GLB1_GLB1...", one "node_N" per merge for the skinned
        //     mesh's node; see GlbMergeService.BuildNodeTree). Every editor marker (BallAnchor,
        //     StiffArm, EyeAnchor, MouthAnchor, ...) hangs under a bone, which keeps them out.
        //
        // Edits the index tables like Prune, so everything else is written back as it was; run
        // Prune after it to drop the samplers and keyframe bytes the removed channels leave.
        public static (byte[] Glb, UnusedNodes? Removed) RemoveUnusedNodes(byte[] glb, Func<UnusedNodes, bool>? confirm = null)
        {
            if (!TryReadChunks(glb, out var json, out var bin)) return (glb, null);
            if (!CanPrune(json)) return (glb, null);

            var nodes = Array(json, "nodes");
            var skins = Array(json, "skins").Select(s => s!.AsObject()).ToList();
            var anims = Array(json, "animations").Select(a => a!.AsObject()).ToList();
            var inSkin = skins.SelectMany(s => Array(s, "joints").Select(j => j!.GetValue<int>())
                .Concat(s["skeleton"] != null ? new[] { Index(s, "skeleton") } : System.Array.Empty<int>())).ToHashSet();
            var animated = anims.SelectMany(a => Array(a, "channels"))
                .Select(c => c!["target"]?["node"]?.GetValue<int>() ?? -1).ToHashSet();
            var parent = Enumerable.Repeat(-1, nodes.Count).ToArray();
            for (int i = 0; i < nodes.Count; ++i)
                foreach (var c in Array(nodes[i]!.AsObject(), "children")) parent[c!.GetValue<int>()] = i;

            var removable = new bool?[nodes.Count];
            bool Removable(int i)
            {
                if (removable[i] is bool known) return known;
                removable[i] = false;   // guards a malformed cycle
                var n = nodes[i]!.AsObject();
                bool result = !inSkin.Contains(i) && (animated.Contains(i) || !inSkin.Contains(parent[i]))
                    && n["mesh"] == null && n["camera"] == null && n["skin"] == null
                    && n["extensions"] == null && n["extras"] == null
                    && Array(n, "children").All(c => Removable(c!.GetValue<int>()));
                removable[i] = result;
                return result;
            }
            var gone = Enumerable.Range(0, nodes.Count).Where(Removable).ToHashSet();
            if (gone.Count == 0) return (glb, null);

            int channels = 0;
            var emptied = new List<string>();
            foreach (var anim in anims)
            {
                var list = Array(anim, "channels");
                var drop = list.Where(c => c!["target"]?["node"] is JsonNode t && gone.Contains(t.GetValue<int>())).ToList();
                channels += drop.Count;
                if (drop.Count == list.Count && drop.Count > 0) emptied.Add(anim["name"]?.GetValue<string>() ?? "(unnamed)");
            }
            string Name(int i) => nodes[i]!["name"]?.GetValue<string>() ?? $"node {i}";
            var report = new UnusedNodes(
                gone.Order().Where(animated.Contains).Select(Name).ToList(),
                gone.Order().Where(i => !animated.Contains(i)).Select(Name).ToList(), channels, emptied);
            if (confirm != null && !confirm(report)) return (glb, null);

            var remap = Compact(nodes, Enumerable.Range(0, nodes.Count).Where(i => !gone.Contains(i)).ToHashSet());
            foreach (var n in nodes.Select(n => n!.AsObject()))
                if (n["children"] is JsonArray children) RemapList(n, "children", children, remap);
            foreach (var scene in Array(json, "scenes").Select(s => s!.AsObject()))
                if (scene["nodes"] is JsonArray roots) RemapList(scene, "nodes", roots, remap);
            foreach (var skin in skins)
            {
                RemapList(skin, "joints", Array(skin, "joints"), remap);
                if (skin["skeleton"] != null) Remap(skin, "skeleton", remap);
            }
            var animations = Array(json, "animations");
            foreach (var anim in anims)
            {
                var list = Array(anim, "channels");
                var keep = list.Where(c => c!["target"]?["node"] is not JsonNode t || remap[t.GetValue<int>()] >= 0).ToList();
                list.Clear();
                foreach (var c in keep)
                {
                    if (c!["target"]!["node"] != null) Remap(c["target"]!.AsObject(), "node", remap);
                    list.Add(c);
                }
                if (list.Count == 0) animations.Remove(anim);
            }
            return (WriteChunks(json, bin), report);
        }

        // Rewrites an index list through `remap`, dropping entries that map to -1 (and the key
        // itself when nothing is left, since glTF forbids empty index arrays).
        private static void RemapList(JsonObject owner, string key, JsonArray list, int[] remap)
        {
            var kept = list.Select(x => remap[x!.GetValue<int>()]).Where(x => x >= 0).ToList();
            if (kept.Count == 0) { owner.Remove(key); return; }
            owner[key] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray());
        }

        public static (byte[] Glb, Stats Stats) Prune(byte[] glb)
        {
            var none = new Stats(0, 0, 0, 0);
            if (!TryReadChunks(glb, out var json, out var bin)) return (glb, none);
            if (!CanPrune(json)) return (glb, none);

            int samplers = PruneSamplers(json);
            int accessors = PruneAccessors(json);
            var (views, newBin) = PruneBufferViews(json, bin);
            var stats = new Stats(samplers, accessors, views, bin.Length - newBin.Length);
            return stats.Any ? (WriteChunks(json, newBin), stats) : (glb, none);
        }

        private static bool CanPrune(JsonObject json)
        {
            var used = json["extensionsUsed"]?.AsArray().Select(e => e!.GetValue<string>()) ?? Enumerable.Empty<string>();
            if (used.Any(e => !SafeExtensions.Contains(e))) return false;
            var buffers = json["buffers"]?.AsArray();
            return buffers != null && buffers.Count == 1 && buffers[0]!["uri"] == null;
        }

        private static int PruneSamplers(JsonObject json)
        {
            int removed = 0;
            foreach (var anim in Array(json, "animations").Select(a => a!.AsObject()))
            {
                var samplers = Array(anim, "samplers");
                var channels = Array(anim, "channels");
                var used = channels.Select(c => Index(c!, "sampler")).ToHashSet();
                var remap = Compact(samplers, used);
                foreach (var c in channels) Remap(c!.AsObject(), "sampler", remap);
                removed += remap.Count(r => r < 0);
            }
            return removed;
        }

        private static int PruneAccessors(JsonObject json)
        {
            var refs = AccessorRefs(json).ToList();
            var remap = Compact(Array(json, "accessors"), refs.Select(r => Index(r.Owner, r.Key)).ToHashSet());
            foreach (var r in refs) Remap(r.Owner, r.Key, remap);
            return remap.Count(i => i < 0);
        }

        // Every place core glTF stores an accessor index.
        private static IEnumerable<(JsonObject Owner, string Key)> AccessorRefs(JsonObject json)
        {
            foreach (var prim in Array(json, "meshes").SelectMany(m => Array(m!.AsObject(), "primitives")).Select(p => p!.AsObject()))
            {
                if (prim["indices"] != null) yield return (prim, "indices");
                foreach (var key in prim["attributes"]!.AsObject().Select(kv => kv.Key).ToList())
                    yield return (prim["attributes"]!.AsObject(), key);
                foreach (var target in Array(prim, "targets").Select(t => t!.AsObject()))
                    foreach (var key in target.Select(kv => kv.Key).ToList())
                        yield return (target, key);
            }
            foreach (var skin in Array(json, "skins").Select(s => s!.AsObject()))
                if (skin["inverseBindMatrices"] != null) yield return (skin, "inverseBindMatrices");
            foreach (var sampler in Array(json, "animations").SelectMany(a => Array(a!.AsObject(), "samplers")).Select(s => s!.AsObject()))
            {
                yield return (sampler, "input");
                yield return (sampler, "output");
            }
        }

        private static IEnumerable<JsonObject> BufferViewOwners(JsonObject json)
        {
            foreach (var acc in Array(json, "accessors").Select(a => a!.AsObject()))
            {
                if (acc["bufferView"] != null) yield return acc;
                if (acc["sparse"] is JsonObject sparse)
                {
                    yield return sparse["indices"]!.AsObject();
                    yield return sparse["values"]!.AsObject();
                }
            }
            foreach (var image in Array(json, "images").Select(i => i!.AsObject()))
                if (image["bufferView"] != null) yield return image;
        }

        private static (int Removed, byte[] Bin) PruneBufferViews(JsonObject json, byte[] bin)
        {
            var owners = BufferViewOwners(json).ToList();
            var views = Array(json, "bufferViews");
            var remap = Compact(views, owners.Select(o => Index(o, "bufferView")).ToHashSet());
            foreach (var o in owners) Remap(o, "bufferView", remap);

            // Repacked in their existing order, each start 4-aligned - the strictest alignment a
            // core accessor component (float / uint32) needs.
            using var packed = new MemoryStream();
            foreach (var view in views.Select(v => v!.AsObject()))
            {
                while (packed.Length % 4 != 0) packed.WriteByte(0);
                int offset = view["byteOffset"]?.GetValue<int>() ?? 0;
                int length = view["byteLength"]!.GetValue<int>();
                view["byteOffset"] = (int)packed.Length;
                packed.Write(bin, offset, length);
            }
            while (packed.Length % 4 != 0) packed.WriteByte(0);
            Array(json, "buffers")[0]!["byteLength"] = (int)packed.Length;
            return (remap.Count(i => i < 0), packed.ToArray());
        }

        // Drops entries of `items` whose index isn't in `used`; returns old index -> new (-1 = gone).
        private static int[] Compact(JsonArray items, HashSet<int> used)
        {
            var remap = new int[items.Count];
            var kept = new List<JsonNode?>();
            for (int i = 0; i < items.Count; ++i)
            {
                remap[i] = used.Contains(i) ? kept.Count : -1;
                if (remap[i] >= 0) kept.Add(items[i]);
            }
            if (kept.Count == items.Count) return remap;
            items.Clear();
            foreach (var k in kept) { k?.Parent?.AsArray().Remove(k); items.Add(k); }
            return remap;
        }

        private static void Remap(JsonObject owner, string key, int[] remap) =>
            owner[key] = remap[Index(owner, key)];

        private static int Index(JsonNode node, string key) => node[key]!.GetValue<int>();

        private static JsonArray Array(JsonObject owner, string key) =>
            owner[key] as JsonArray ?? new JsonArray();

        private static bool TryReadChunks(byte[] glb, out JsonObject json, out byte[] bin)
        {
            json = null!; bin = System.Array.Empty<byte>();
            if (glb.Length < 20 || BitConverter.ToUInt32(glb, 0) != 0x46546C67) return false;
            int jsonLen = BitConverter.ToInt32(glb, 12);
            if (BitConverter.ToUInt32(glb, 16) != JsonChunk) return false;
            if (JsonNode.Parse(Encoding.UTF8.GetString(glb, 20, jsonLen)) is not JsonObject obj) return false;
            json = obj;
            int binAt = 20 + jsonLen;
            if (binAt + 8 > glb.Length || BitConverter.ToUInt32(glb, binAt + 4) != BinChunk) return false;
            int binLen = BitConverter.ToInt32(glb, binAt);
            bin = glb.AsSpan(binAt + 8, binLen).ToArray();
            return true;
        }

        private static byte[] WriteChunks(JsonObject json, byte[] bin)
        {
            var text = Encoding.UTF8.GetBytes(json.ToJsonString());
            int jsonPadded = (text.Length + 3) & ~3;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonPadded + 8 + bin.Length));
            w.Write((uint)jsonPadded); w.Write(JsonChunk); w.Write(text);
            for (int i = text.Length; i < jsonPadded; ++i) w.Write((byte)0x20);
            w.Write((uint)bin.Length); w.Write(BinChunk); w.Write(bin);
            w.Flush();
            return ms.ToArray();
        }
    }
}
