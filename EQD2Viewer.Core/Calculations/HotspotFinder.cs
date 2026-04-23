using EQD2Viewer.Core.Data;

namespace EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Locates the peak dose voxel in a plan's dose grid. Used by the UI to
    /// drive the "Jump to hotspot" button — it navigates the viewer to the slice
    /// that actually contains the maximum dose, instead of letting the user scroll.
    /// </summary>
    public readonly struct DoseHotspot
    {
        public readonly double MaxGy;
        public readonly int SliceZ;
        public readonly int PixelX;
        public readonly int PixelY;
        public readonly bool IsValid;

        public DoseHotspot(double maxGy, int z, int x, int y)
        {
            MaxGy = maxGy; SliceZ = z; PixelX = x; PixelY = y; IsValid = true;
        }
    }

    public static class HotspotFinder
    {
        /// <summary>
        /// Scans the entire dose grid for the voxel with the largest Gy value.
        /// Uses the dose's own geometry (no resampling) — pixel coordinates are in the dose grid.
        /// </summary>
        public static DoseHotspot FindInDoseVolume(DoseVolumeData dose)
        {
            if (dose?.Voxels == null) return default;
            var s = dose.Scaling ?? new DoseScaling { RawScale = 1.0, RawOffset = 0, UnitToGy = 1.0 };
            double rs = s.RawScale, ro = s.RawOffset, ug = s.UnitToGy;

            double maxGy = double.MinValue;
            int maxZ = 0, maxX = 0, maxY = 0;
            int xSize = dose.XSize, ySize = dose.YSize, zSize = dose.ZSize;
            for (int z = 0; z < zSize; z++)
            {
                var slice = dose.Voxels[z];
                if (slice == null) continue;
                for (int y = 0; y < ySize; y++)
                    for (int x = 0; x < xSize; x++)
                    {
                        double gy = (slice[x, y] * rs + ro) * ug;
                        if (gy > maxGy)
                        {
                            maxGy = gy; maxZ = z; maxX = x; maxY = y;
                        }
                    }
            }
            return maxGy > double.MinValue ? new DoseHotspot(maxGy, maxZ, maxX, maxY) : default;
        }
    }
}
