using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // The undo stack behind GeometryOptimizerEditor's History list. Each entry is a complete,
    // independently restorable capture of the model's geometry as it stood after one operation,
    // so any point can be jumped back to directly rather than only the single "before the first
    // Apply" state the editor used to keep.
    //
    // Restoring an old entry leaves the newer ones in place - stepping back through the list and
    // forward again is free and lossless. Recording a NEW operation while sitting on an old entry
    // is what discards them, because those newer states were built on geometry the new operation
    // has just replaced and could no longer be reached by replaying anything.
    //
    // What a capture has to contain differs per operation, and that difference is the reason for
    // the two-call BeginChange/Record shape:
    //
    //  - Simplification only rewrites index buffers, so an index snapshot restores it completely.
    //    Index buffers are small enough to capture for every entry unconditionally.
    //  - Hole filling appends vertices and rewrites vertex accessors, which no index snapshot can
    //    undo. Full vertex contents are an order of magnitude larger than the indices - tens of
    //    megabytes on a dense model - so they are captured lazily: only when an operation that
    //    actually touches them is about to run, and shared by reference with every later entry
    //    that leaves them alone. An entry with no vertex capture of its own means "same vertices
    //    as the nearest entry below that has one", which is what LookupVertexData resolves.
    public sealed class GeometryHistory
    {
        // Beyond this the oldest entries are dropped (never the original, which has to stay
        // reachable). Chosen well above any plausible number of passes in one session while still
        // bounding what the vertex captures above can hold on to.
        private const int MaxEntries = 40;

        // Every primitive's vertex accessor contents, decoded to Vector4 and kept alongside the
        // accessor they came from - the accessor is read for its encoding/dimensions only, so it
        // stays usable after the model has orphaned it.
        internal sealed class VertexState
        {
            internal sealed class PrimitiveVertices
            {
                public required Dictionary<string, Accessor> Sources { get; init; }
                public required Dictionary<string, IReadOnlyList<Vector4>> Values { get; init; }
            }

            public required List<PrimitiveVertices?> Primitives { get; init; }
        }

        public sealed class Entry
        {
            public required string Label { get; init; }
            public required int Triangles { get; init; }

            // Vertices still referenced by a triangle. This, not TotalVertices, is what a
            // simplification pass moves: it only rewrites indices, so the vertices it stops
            // referencing sit in the buffer at full size until a save compacts them away.
            public required int LiveVertices { get; init; }
            public required int TotalVertices { get; init; }

            // Per primitive in LogicalMeshes x Primitives order; null for a primitive that isn't a
            // triangle list, which nothing here rewrites.
            internal List<int[]?> Indices { get; init; } = new();

            internal VertexState? VertexData { get; set; }
        }

        private readonly ModelRoot _model;
        private readonly List<Entry> _entries = new();
        private int _current;

        // Which VertexState the model's vertex buffers currently hold, so a restore that doesn't
        // change them can skip rewriting every vertex accessor - each rewrite orphans the accessors
        // it replaces and grows the file until a save rebuilds it.
        private VertexState? _appliedVertexData;

        // Same idea for the index buffers: a restore only rewrites the primitives whose indices
        // actually differ from what the model already holds. With a painted region in play a pass
        // often changes one primitive out of dozens, and rewriting the rest would orphan a dead
        // index accessor apiece for nothing.
        private List<int[]?> _appliedIndices;

        public GeometryHistory(ModelRoot model, string originalLabel = "Original merge result")
        {
            _model = model;
            var original = Capture(originalLabel);
            _entries.Add(original);
            _appliedIndices = original.Indices;
            _current = 0;
        }

        public IReadOnlyList<Entry> Entries => _entries;
        public int CurrentIndex => _current;
        public Entry Current => _entries[_current];

        // Called immediately before an operation runs. An operation that rewrites vertex data has
        // to have the pre-operation vertices captured now, or there would be nothing to restore
        // them from afterwards.
        public void BeginChange(bool touchesVertexData)
        {
            if (!touchesVertexData) return;

            // Nothing captured anywhere at or below the current entry means no vertex-touching
            // operation has run in this lineage, so the model's vertices are still exactly the
            // original ones - hence storing this capture on entry 0, where every entry's lookup
            // can reach it, rather than on the current entry.
            if (LookupVertexData(_current) != null) return;

            var state = CaptureVertexState();
            _entries[0].VertexData = state;
            _appliedVertexData = state;
        }

        // Records the model's state after an operation. touchedVertexData must match what was
        // passed to the preceding BeginChange call.
        public Entry Record(string label, bool touchedVertexData)
        {
            // Recording on top of a restored older entry drops what came after it.
            if (_current < _entries.Count - 1)
                _entries.RemoveRange(_current + 1, _entries.Count - _current - 1);

            var entry = Capture(label);
            if (touchedVertexData)
            {
                entry.VertexData = CaptureVertexState();
                _appliedVertexData = entry.VertexData;
            }

            _entries.Add(entry);
            _appliedIndices = entry.Indices;
            _current = _entries.Count - 1;
            Trim();
            return entry;
        }

        // Puts the model back into the state recorded by an entry, leaving every entry in the list
        // intact.
        public void RestoreTo(int index)
        {
            if (index < 0 || index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));

            var entry = _entries[index];

            // Vertices first: the index buffers written below are the ones that address them, and
            // a restore that shrinks the vertex count would otherwise leave the old, longer index
            // buffer pointing past the end of the new accessors in between the two writes.
            var vertexData = LookupVertexData(index);
            bool vertexDataRewritten = false;
            if (vertexData != null && !ReferenceEquals(vertexData, _appliedVertexData))
            {
                ApplyVertexState(vertexData);
                _appliedVertexData = vertexData;
                vertexDataRewritten = true;
            }

            int i = 0;
            foreach (var prim in _model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (i >= entry.Indices.Count) break;
                var indices = entry.Indices[i];
                var applied = i < _appliedIndices.Count ? _appliedIndices[i] : null;
                i++;

                if (indices == null) continue;

                // The skip is only safe while the vertices underneath stayed put: a vertex rewrite
                // can shorten an accessor, and leaving an index buffer that addresses the longer
                // one in place would point past its end.
                if (!vertexDataRewritten && applied != null && applied.AsSpan().SequenceEqual(indices)) continue;
                prim.WithIndicesAccessor(PrimitiveType.TRIANGLES, indices);
            }

            _appliedIndices = entry.Indices;
            _current = index;
        }

        // One primitive's geometry as it stood at some entry, in that entry's own vertex numbering.
        // UvWarpRepair measures the current surface against entry 0 (the one state whose UV mapping
        // is known to be right, and which never changes - Trim keeps it); RegionRestorer puts
        // triangles back from the oldest entry whose vertices are still the model's current ones.
        internal sealed class ReferencePrimitive
        {
            public required int[] Indices { get; init; }
            public required IReadOnlyList<Vector3> Positions { get; init; }
            public required Dictionary<string, IReadOnlyList<Vector2>> UvSets { get; init; }
        }

        internal List<ReferencePrimitive?> GetOriginalGeometry() => GetReferenceGeometry(0);

        // An entry's triangles with the vertices they address; null for a primitive that isn't a
        // triangle list or has no POSITION. Per primitive in LogicalMeshes x Primitives order.
        //
        // Where the vertices come from depends on whether anything has rewritten them yet. A
        // vertex capture only exists once some vertex-touching operation has run (BeginChange
        // stores its pre-operation capture on entry 0, so every later lookup can reach it), and
        // until then the live accessors still hold exactly the original contents - so a missing
        // capture is read straight off the model rather than treated as an error.
        internal List<ReferencePrimitive?> GetReferenceGeometry(int entryIndex)
        {
            var entry = _entries[entryIndex];
            var vertexData = LookupVertexData(entryIndex);
            var result = new List<ReferencePrimitive?>();

            int i = 0;
            foreach (var prim in _model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                int index = i++;
                var indices = index < entry.Indices.Count ? entry.Indices[index] : null;
                if (indices == null) { result.Add(null); continue; }

                var captured = vertexData != null && index < vertexData.Primitives.Count
                    ? vertexData.Primitives[index]
                    : null;

                IReadOnlyList<Vector3>? positions = null;
                var uvSets = new Dictionary<string, IReadOnlyList<Vector2>>();

                if (captured != null)
                {
                    foreach (var (name, values) in captured.Values)
                    {
                        if (name == "POSITION")
                            positions = values.Select(v => new Vector3(v.X, v.Y, v.Z)).ToList();
                        else if (name.StartsWith("TEXCOORD_", StringComparison.Ordinal))
                            uvSets[name] = values.Select(v => new Vector2(v.X, v.Y)).ToList();
                    }
                }
                else
                {
                    foreach (var (name, accessor) in prim.VertexAccessors)
                    {
                        if (name == "POSITION")
                            positions = accessor.AsVector3Array().ToList();
                        else if (name.StartsWith("TEXCOORD_", StringComparison.Ordinal))
                            uvSets[name] = accessor.AsVector2Array().ToList();
                    }
                }

                if (positions == null) { result.Add(null); continue; }
                result.Add(new ReferencePrimitive { Indices = indices, Positions = positions, UvSets = uvSets });
            }

            return result;
        }

        // The oldest entry whose vertex positions are the ones the model holds right now - the
        // furthest back an index-only operation can safely reach for triangles. A simplify pass or
        // the texture-warp fix leaves POSITION alone, so the run of entries since the last delete,
        // cut or fill all share it; that operation's own entry is where the run starts. Entry 0
        // when nothing has ever rewritten the vertices.
        //
        // Judged on the POSITION list by reference: CaptureVertexState shares an attribute's
        // decoded list between captures whenever the accessor underneath was not rewritten, so
        // two entries whose POSITION lists are the same object provably hold the same positions
        // in the same numbering, whatever happened to their UVs in between.
        public int OldestEntrySharingCurrentVertices()
        {
            var current = LookupVertexData(_current);
            int oldest = _current;
            for (int i = _current - 1; i >= 0; i--)
                if (SamePositions(LookupVertexData(i), current)) oldest = i;
            return oldest;
        }

        private static bool SamePositions(VertexState? a, VertexState? b)
        {
            if (a == null || b == null) return a == null && b == null;   // both null: untouched originals
            if (ReferenceEquals(a, b)) return true;
            if (a.Primitives.Count != b.Primitives.Count) return false;

            for (int i = 0; i < a.Primitives.Count; i++)
            {
                var pa = a.Primitives[i];
                var pb = b.Primitives[i];
                if (pa == null || pb == null) { if (pa != pb) return false; continue; }

                pa.Values.TryGetValue("POSITION", out var la);
                pb.Values.TryGetValue("POSITION", out var lb);
                if (!ReferenceEquals(la, lb)) return false;
            }
            return true;
        }

        // The vertex contents that apply to an entry: its own capture, or the nearest one below it.
        private VertexState? LookupVertexData(int index)
        {
            for (int i = index; i >= 0; i--)
                if (_entries[i].VertexData != null) return _entries[i].VertexData;
            return null;
        }

        private void Trim()
        {
            while (_entries.Count > MaxEntries)
            {
                // Entry 0 stays: "restore to the original" has to remain available however long the
                // session runs. Dropping the one above it can strand a vertex capture that later
                // entries resolve to by looking down the list, so hand it to the next entry along
                // unless that one has a capture of its own.
                if (_entries[1].VertexData != null && _entries[2].VertexData == null)
                    _entries[2].VertexData = _entries[1].VertexData;
                _entries.RemoveAt(1);
                _current--;
            }
        }

        private Entry Capture(string label)
        {
            var indices = new List<int[]?>();
            int triangles = 0, live = 0, total = 0;

            foreach (var prim in _model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                prim.VertexAccessors.TryGetValue("POSITION", out var position);
                int vertexCount = position?.Count ?? 0;
                total += vertexCount;

                if (prim.DrawPrimitiveType != PrimitiveType.TRIANGLES)
                {
                    // Nothing in this editor rewrites a non-triangle-list primitive, so it has no
                    // index snapshot and all of its vertices count as live.
                    indices.Add(null);
                    live += vertexCount;
                    continue;
                }

                var flat = prim.GetTriangleIndices().SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                indices.Add(flat);
                triangles += flat.Length / 3;

                var used = new bool[vertexCount];
                foreach (int index in flat)
                    if (index >= 0 && index < vertexCount) used[index] = true;
                foreach (bool u in used) if (u) live++;
            }

            return new Entry
            {
                Label = label,
                Triangles = triangles,
                LiveVertices = live,
                TotalVertices = total,
                Indices = indices,
            };
        }

        // Nothing here ever writes into an accessor in place - both WriteAttribute and
        // GeometryOptimizer's own writes mint a NEW accessor and leave the old object behind - so
        // an attribute still pointing at the same Accessor instance as the last capture provably
        // holds the same bytes. Both halves below lean on that: a capture shares the previous
        // capture's decoded list for those attributes instead of re-reading them, and a restore can
        // then tell by reference alone which attributes it actually has to write back. On a model
        // where one mesh out of several was filled, that is the difference between carrying (and
        // rewriting) every mesh's vertices per step and carrying only the one that moved.
        private VertexState CaptureVertexState()
        {
            var previous = _appliedVertexData;
            var primitives = new List<VertexState.PrimitiveVertices?>();

            int i = 0;
            foreach (var prim in _model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                var unchanged = previous != null && i < previous.Primitives.Count ? previous.Primitives[i] : null;
                i++;

                var sources = new Dictionary<string, Accessor>();
                var values = new Dictionary<string, IReadOnlyList<Vector4>>();
                foreach (var (name, accessor) in prim.VertexAccessors)
                {
                    sources[name] = accessor;

                    if (unchanged != null
                        && unchanged.Sources.TryGetValue(name, out var previousAccessor)
                        && ReferenceEquals(previousAccessor, accessor))
                    {
                        values[name] = unchanged.Values[name];
                        continue;
                    }

                    // ReadAsVector4 hands back a live view over the accessor's own bytes for VEC4
                    // attributes rather than a copy, and this outlives many rewrites of exactly
                    // those buffers - so materialize anything that isn't already a standalone list.
                    var read = WatertightRepair.ReadAsVector4(accessor);
                    values[name] = read as List<Vector4> ?? read.ToList();
                }

                primitives.Add(new VertexState.PrimitiveVertices { Sources = sources, Values = values });
            }
            return new VertexState { Primitives = primitives };
        }

        private void ApplyVertexState(VertexState state)
        {
            var applied = _appliedVertexData;

            int i = 0;
            foreach (var prim in _model.LogicalMeshes.SelectMany(m => m.Primitives))
            {
                if (i >= state.Primitives.Count) break;
                var captured = state.Primitives[i];
                var current = applied != null && i < applied.Primitives.Count ? applied.Primitives[i] : null;
                i++;
                if (captured == null) continue;

                foreach (var (name, values) in captured.Values)
                {
                    if (current != null
                        && current.Values.TryGetValue(name, out var live)
                        && ReferenceEquals(live, values))
                        continue;

                    WatertightRepair.WriteAttribute(prim, name, captured.Sources[name], values);
                }
            }
        }
    }
}
