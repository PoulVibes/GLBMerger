using System;
using System.Runtime.InteropServices;

namespace GlbMerger
{
    // Direct P/Invoke into meshoptimizer's simplification entry points.
    //
    // The native library itself comes from the Meshoptimizer.NET package (it ships prebuilt
    // meshoptimizer binaries for win-x64 and linux-x64), but that package's managed wrapper only
    // binds the meshlet/remap/vertex-cache half of the API - meshopt_simplify and friends aren't
    // declared there, so they're declared here against the same native library instead. The
    // shipped DLL does export them; see the build notes in GeometryOptimizer.
    //
    // Signatures are taken verbatim from meshoptimizer.h. Getting one wrong corrupts the stack
    // rather than failing cleanly, so they are deliberately spelled out in full rather than
    // simplified.
    internal static class MeshoptNative
    {
        private const string Library = "meshoptimizer";

        [Flags]
        public enum SimplifyOptions : uint
        {
            None = 0,
            /// <summary>Keeps mesh borders (open edges / UV island edges) exactly where they are.</summary>
            LockBorder = 1 << 0,
            Sparse = 1 << 1,
            ErrorAbsolute = 1 << 2,
            Prune = 1 << 3,
        }

        [DllImport(Library, EntryPoint = "meshopt_simplify", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nuint Simplify(
            uint* destination,
            uint* indices, nuint indexCount,
            float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
            nuint targetIndexCount, float targetError, uint options, float* resultError);

        [DllImport(Library, EntryPoint = "meshopt_simplifyWithAttributes", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nuint SimplifyWithAttributes(
            uint* destination,
            uint* indices, nuint indexCount,
            float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride,
            float* vertexAttributes, nuint vertexAttributesStride,
            float* attributeWeights, nuint attributeCount,
            byte* vertexLock,
            nuint targetIndexCount, float targetError, uint options, float* resultError);

        /// <summary>
        /// The scale meshopt_simplify's relative error is measured against - multiply a returned
        /// or requested error by this to get model units.
        /// </summary>
        [DllImport(Library, EntryPoint = "meshopt_simplifyScale", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe float SimplifyScale(
            float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

        [DllImport(Library, EntryPoint = "meshopt_optimizeVertexCache", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void OptimizeVertexCache(
            uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

        /// <summary>
        /// Reorders triangles so nearer-to-the-camera ones are drawn first, cutting how often a
        /// fragment is shaded and then overwritten. Threshold caps how much vertex-cache efficiency
        /// it is allowed to give back to do it - 1.05 means "up to 5% worse ACMR".
        /// </summary>
        [DllImport(Library, EntryPoint = "meshopt_optimizeOverdraw", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void OptimizeOverdraw(
            uint* destination, uint* indices, nuint indexCount,
            float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride, float threshold);

        /// <summary>
        /// Builds an old-index -> new-index table that puts vertex records in the order the index
        /// buffer first reaches them. Returns the number of vertices that survive; unreferenced
        /// vertices map to ~0u and are meant to be dropped.
        /// </summary>
        [DllImport(Library, EntryPoint = "meshopt_optimizeVertexFetchRemap", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe nuint OptimizeVertexFetchRemap(
            uint* destination, uint* indices, nuint indexCount, nuint vertexCount);

        [DllImport(Library, EntryPoint = "meshopt_remapIndexBuffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe void RemapIndexBuffer(
            uint* destination, uint* indices, nuint indexCount, uint* remap);

        /// <summary>
        /// meshopt_OverdrawStatistics. Returned by value from the analyzer below - three 4-byte
        /// fields, sequential and blittable, so the runtime marshals it without a custom layout.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct OverdrawStatistics
        {
            public uint PixelsCovered;
            public uint PixelsShaded;
            /// <summary>PixelsShaded / PixelsCovered. 1.0 = every covered pixel shaded exactly once.</summary>
            public float Overdraw;
        }

        /// <summary>
        /// Software-rasterizes the mesh from several directions to measure how many times an average
        /// covered pixel gets shaded. This is what the overdraw pass spends vertex-cache efficiency
        /// to reduce, so it is the other half of that trade.
        /// </summary>
        [DllImport(Library, EntryPoint = "meshopt_analyzeOverdraw", CallingConvention = CallingConvention.Cdecl)]
        public static extern unsafe OverdrawStatistics AnalyzeOverdraw(
            uint* indices, nuint indexCount,
            float* vertexPositions, nuint vertexCount, nuint vertexPositionsStride);

        // Probes the native library once so a missing/incompatible meshoptimizer binary surfaces as
        // a clean "unavailable" in the UI rather than a DllNotFoundException from somewhere deep in
        // an optimization run.
        private static bool? _available;

        public static bool IsAvailable
        {
            get
            {
                if (_available.HasValue) return _available.Value;
                try
                {
                    unsafe
                    {
                        var probe = stackalloc float[9];
                        for (int i = 0; i < 9; i++) probe[i] = i;
                        SimplifyScale(probe, 3, sizeof(float) * 3);
                    }
                    _available = true;
                }
                catch (DllNotFoundException) { _available = false; }
                catch (EntryPointNotFoundException) { _available = false; }
                catch (BadImageFormatException) { _available = false; }
                return _available.Value;
            }
        }
    }
}
