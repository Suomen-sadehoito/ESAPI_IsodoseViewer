using VMS.TPS.Common.Model.API;

namespace EQD2Viewer.Esapi.Adapters
{
    /// <summary>
    /// Shared bulk voxel-load helpers for ESAPI <see cref="Image"/> and
    /// <see cref="Dose"/> grids. Both adapters formerly carried byte-identical
    /// `for (z) { vox[z] = new int[X,Y]; src.GetVoxels(z, vox[z]); }` loops.
    /// </summary>
    internal static class EsapiVoxelLoader
    {
        /// <summary>
        /// Loads every CT slice of <paramref name="image"/> into a jagged
        /// <c>int[zSize][xSize, ySize]</c> array via <see cref="Image.GetVoxels(int, int[,])"/>.
        /// Slice 0 is the bottom of the volume.
        /// </summary>
        public static int[][,] LoadVoxels(Image image)
        {
            int zSize = image.ZSize, xSize = image.XSize, ySize = image.YSize;
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                image.GetVoxels(z, voxels[z]);
            }
            return voxels;
        }

        /// <summary>
        /// Loads every dose slice of <paramref name="dose"/> into a jagged
        /// <c>int[zSize][xSize, ySize]</c> array via <see cref="Dose.GetVoxels(int, int[,])"/>.
        /// </summary>
        public static int[][,] LoadVoxels(Dose dose)
        {
            int zSize = dose.ZSize, xSize = dose.XSize, ySize = dose.YSize;
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                dose.GetVoxels(z, voxels[z]);
            }
            return voxels;
        }
    }
}
