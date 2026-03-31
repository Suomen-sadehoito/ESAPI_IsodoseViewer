using EQD2Viewer.Core.Models;
using System;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Shared image processing utilities used by both ImageRenderingService and SummationService.
    /// Eliminates duplication of DetermineHuOffset and BilinearSample.
    /// </summary>
    public static class ImageUtils
    {
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
                    totalSamples++;
                    if (midSlice[x, y] > DomainConstants.HuOffsetRawThreshold)
                        countAboveThreshold++;
                }
            }

            return (totalSamples > 0 && countAboveThreshold > totalSamples / 2)
                ? DomainConstants.HuOffsetValue : 0;
        }

        /// <summary>
        /// Bilinear interpolation on a double[,] grid.
        /// Returns 0 for coordinates outside the grid, nearest-neighbor at edges.
        /// Used for dose resampling at CT resolution.
        /// </summary>
        public static double BilinearSample(double[,] grid, int gw, int gh, double fx, double fy)
        {
            if (fx < 0 || fy < 0 || fx >= gw - 1 || fy >= gh - 1)
            {
                int nx = (int)Math.Round(fx), ny = (int)Math.Round(fy);
                return (nx >= 0 && nx < gw && ny >= 0 && ny < gh) ? grid[nx, ny] : 0;
            }

            int x0 = (int)fx, y0 = (int)fy;
            double tx = fx - x0, ty = fy - y0;

            return grid[x0, y0] * (1 - tx) * (1 - ty)
                 + grid[x0 + 1, y0] * tx * (1 - ty)
                 + grid[x0, y0 + 1] * (1 - tx) * ty
                 + grid[x0 + 1, y0 + 1] * tx * ty;
        }

        /// <summary>
        /// Bilinear interpolation on a raw int[,] dose voxel grid with scaling.
        /// Converts raw voxel values to Gy during interpolation.
        /// Used by SummationService for registered dose accumulation.
        /// </summary>
        public static double BilinearSampleRaw(int[,] grid, int gw, int gh,
            double fx, double fy, double rawScale, double rawOffset, double unitToGy)
        {
            if (fx < 0 || fy < 0 || fx >= gw - 1 || fy >= gh - 1)
            {
                int nx = (int)Math.Round(fx), ny = (int)Math.Round(fy);
                return (nx >= 0 && nx < gw && ny >= 0 && ny < gh)
                    ? (grid[nx, ny] * rawScale + rawOffset) * unitToGy : 0;
            }

            int x0 = (int)fx, y0 = (int)fy;
            double tx = fx - x0, ty = fy - y0;

            double raw = grid[x0, y0] * (1 - tx) * (1 - ty)
                       + grid[x0 + 1, y0] * tx * (1 - ty)
                       + grid[x0, y0 + 1] * (1 - tx) * ty
                       + grid[x0 + 1, y0 + 1] * tx * ty;

            return (raw * rawScale + rawOffset) * unitToGy;
        }
    }
}
