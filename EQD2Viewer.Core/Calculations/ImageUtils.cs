using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using System;
using System.Runtime.CompilerServices;

namespace EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Shared image processing utilities used by both ImageRenderingService and SummationService.
    /// Eliminates duplication of DetermineHuOffset and BilinearSample.
    /// </summary>
    public static class ImageUtils
    {
        /// <summary>
        /// Converts a single raw dose voxel value to absolute Gy via the
        /// linear calibration <c>(raw * rawScale + rawOffset) * unitToGy</c>.
        /// Centralises the per-pixel scaling formula used by every rendering
        /// and export path that walks a dose grid without bilinear resampling.
        /// Marked <see cref="MethodImplOptions.AggressiveInlining"/> because
        /// callers sit inside per-voxel hot loops (see docs/hot-paths.md §2).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double RawToGy(int raw, double rawScale, double rawOffset, double unitToGy)
            => (raw * rawScale + rawOffset) * unitToGy;

        /// <summary>
        /// Convenience overload taking a <see cref="DoseScaling"/> directly.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double RawToGy(int raw, DoseScaling scaling)
            => RawToGy(raw, scaling.RawScale, scaling.RawOffset, scaling.UnitToGy);

        /// <summary>
        /// Determines whether CT voxels need a 32768 offset subtracted.
        /// Some ESAPI CT images store HU as unsigned (0-65535) rather than signed.
        /// Samples a sparse grid from the mid-slice and checks if most values are above threshold.
        /// </summary>
        /// <param name="midSlice">2D voxel array [x, y] from the middle slice</param>
        /// <param name="xSize">Image X dimension</param>
        /// <param name="ySize">Image Y dimension</param>
        public static int DetermineHuOffset(int[,] midSlice, int xSize, int ySize)
        {
            int step = DomainConstants.HuOffsetSampleStep;
            int countAboveThreshold = 0;
            int totalSamples = 0;

            for (int y = 0; y < ySize; y += step)
            {
                for (int x = 0; x < xSize; x += step)
                {
                    int v = midSlice[x, y];
                    // Any negative value proves signed storage (unsigned has no negative values
                    // by definition). Early-exit catches the metal-dominated mid-slice edge case
                    // where >50% of samples exceed the 30000-threshold but the image is really
                    // signed — misclassifying it as unsigned would shift structure masks onto
                    // the wrong anatomy.
                    if (v < 0) return 0;
                    totalSamples++;
                    if (v > DomainConstants.HuOffsetRawThreshold)
                        countAboveThreshold++;
                }
            }

            return (totalSamples > 0 && countAboveThreshold > totalSamples / 2)
                ? DomainConstants.HuOffsetValue : 0;
        }

        /// <summary>
        /// Bilinear interpolation on a double[,] grid.
        ///
        /// Sampling convention:
        ///   fx, fy are voxel-index coordinates (not world mm). A value of 0 means "centre
        ///   of voxel 0", not "left edge of voxel 0".
        ///   * fx ∈ [0, gw-1]: valid interior or boundary — returns interpolated value, clamping
        ///     the far edge (fx == gw-1) to the last column so no OOB access occurs.
        ///   * fx ∈ [-0.5, 0) or (gw-1, gw-0.5]: within half a voxel of the grid — clamps to
        ///     the nearest valid voxel. This preserves peripheral dose that would otherwise
        ///     be lost to rounding.
        ///   * Anything further outside: returns 0 (no data).
        /// </summary>
        public static double BilinearSample(double[,] grid, int gw, int gh, double fx, double fy)
        {
            if (fx < -0.5 || fy < -0.5 || fx > gw - 0.5 || fy > gh - 0.5) return 0;
            if (gw <= 0 || gh <= 0) return 0;

            // Clamp to [0, gw-1] for interior interpolation; then clamp x0 so x0+1 stays in range.
            if (fx < 0) fx = 0; else if (fx > gw - 1) fx = gw - 1;
            if (fy < 0) fy = 0; else if (fy > gh - 1) fy = gh - 1;

            int x0 = (int)fx; if (x0 >= gw - 1) x0 = gw - 2; if (x0 < 0) x0 = 0;
            int y0 = (int)fy; if (y0 >= gh - 1) y0 = gh - 2; if (y0 < 0) y0 = 0;

            // Degenerate dimensions (1-voxel rows/cols): fall back to nearest.
            if (gw == 1 || gh == 1)
            {
                int nx = gw == 1 ? 0 : (int)Math.Round(fx);
                int ny = gh == 1 ? 0 : (int)Math.Round(fy);
                if (nx >= gw) nx = gw - 1; if (ny >= gh) ny = gh - 1;
                return grid[nx, ny];
            }

            double tx = fx - x0, ty = fy - y0;
            if (tx < 0) tx = 0; else if (tx > 1) tx = 1;
            if (ty < 0) ty = 0; else if (ty > 1) ty = 1;

            return grid[x0, y0] * (1 - tx) * (1 - ty)
                 + grid[x0 + 1, y0] * tx * (1 - ty)
                 + grid[x0, y0 + 1] * (1 - tx) * ty
                 + grid[x0 + 1, y0 + 1] * tx * ty;
        }

        /// <summary>
        /// Bilinear interpolation on a raw int[,] dose voxel grid with scaling.
        /// Boundary handling mirrors <see cref="BilinearSample"/> to prevent peripheral
        /// dose voxels from silently returning 0 at the edges of the dose grid.
        /// </summary>
        public static double BilinearSampleRaw(int[,] grid, int gw, int gh,
            double fx, double fy, double rawScale, double rawOffset, double unitToGy)
        {
            if (fx < -0.5 || fy < -0.5 || fx > gw - 0.5 || fy > gh - 0.5) return 0;
            if (gw <= 0 || gh <= 0) return 0;

            if (fx < 0) fx = 0; else if (fx > gw - 1) fx = gw - 1;
            if (fy < 0) fy = 0; else if (fy > gh - 1) fy = gh - 1;

            int x0 = (int)fx; if (x0 >= gw - 1) x0 = gw - 2; if (x0 < 0) x0 = 0;
            int y0 = (int)fy; if (y0 >= gh - 1) y0 = gh - 2; if (y0 < 0) y0 = 0;

            if (gw == 1 || gh == 1)
            {
                int nx = gw == 1 ? 0 : (int)Math.Round(fx);
                int ny = gh == 1 ? 0 : (int)Math.Round(fy);
                if (nx >= gw) nx = gw - 1; if (ny >= gh) ny = gh - 1;
                return (grid[nx, ny] * rawScale + rawOffset) * unitToGy;
            }

            double tx = fx - x0, ty = fy - y0;
            if (tx < 0) tx = 0; else if (tx > 1) tx = 1;
            if (ty < 0) ty = 0; else if (ty > 1) ty = 1;

            double raw = grid[x0, y0] * (1 - tx) * (1 - ty)
                       + grid[x0 + 1, y0] * tx * (1 - ty)
                       + grid[x0, y0 + 1] * (1 - tx) * ty
                       + grid[x0 + 1, y0 + 1] * tx * ty;

            return (raw * rawScale + rawOffset) * unitToGy;
        }
    }
}
