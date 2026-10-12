using System.Collections.Generic;
using System.Text.Json.Nodes;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Carries named nodes' glTF extras across a SceneBuilder rebuild. Both rebuilds this app does -
    // the merge (GlbMergeService.MergeTargeted, which re-emits the source hierarchy 1:1 by name and
    // local matrix) and "clean up unused geometry" (GeometryOptimizer.BuildCleanedCopy) - keep every
    // node's name and transform but have no notion of extras, so without this anything an editor
    // stored there was silently reset by the next Merge or cleanup. The Eye Anchors editor's eye
    // style (EyeAnchorL/R extras) is what exposed it.
    //
    // Matched by node name: the rebuilds preserve names, and unnamed nodes are structural and carry
    // nothing an editor wrote. First node wins when a rig reuses a name.
    internal static class NodeExtras
    {
        public static Dictionary<string, JsonNode> Capture(ModelRoot model)
        {
            var extras = new Dictionary<string, JsonNode>();
            foreach (var node in model.LogicalNodes)
                if (!string.IsNullOrEmpty(node.Name) && node.Extras != null)
                    extras.TryAdd(node.Name, node.Extras.DeepClone());
            return extras;
        }

        // Only fills nodes that came out of the rebuild with no extras of their own.
        public static void Restore(ModelRoot model, Dictionary<string, JsonNode> extras)
        {
            if (extras.Count == 0) return;
            foreach (var node in model.LogicalNodes)
                if (!string.IsNullOrEmpty(node.Name) && node.Extras == null &&
                    extras.TryGetValue(node.Name, out var value))
                    node.Extras = value.DeepClone();
        }
    }
}
