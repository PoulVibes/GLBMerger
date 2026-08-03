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
