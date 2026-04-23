using EQD2Viewer.Core.Data;
using itk.simple;
using System;
using System.Runtime.InteropServices;

namespace EQD2Viewer.Registration.ITK.Converters
{
    /// <summary>
    /// Converts between EQD2Viewer domain objects (VolumeData, DeformationField)
    /// and SimpleITK Image objects.
    ///
    /// ITK direction matrix convention (row-major serialization):
    ///   D is a 3x3 matrix where column i is the direction of image axis i in physical space.
    ///   GetDirection() / SetDirection() flatten row-by-row:
    ///     [D[0,0], D[0,1], D[0,2], D[1,0], D[1,1], D[1,2], D[2,0], D[2,1], D[2,2]]
    ///   Thus for VolumeData whose XDirection/YDirection/ZDirection are each axis direction:
    ///     D[r, 0] = XDirection[r], D[r, 1] = YDirection[r], D[r, 2] = ZDirection[r].
    /// </summary>
    internal static class ItkImageConverter
    {
        internal static Image VolumeToImage(VolumeData vol)
        {
            int xSize = vol.XSize, ySize = vol.YSize, zSize = vol.ZSize;

            var size = new VectorUInt32(new uint[] { (uint)xSize, (uint)ySize, (uint)zSize });
            var img = new Image(size, PixelIDValueEnum.sitkInt16);

            img.SetSpacing(new VectorDouble(new double[] { vol.XRes, vol.YRes, vol.ZRes }));
            img.SetOrigin(new VectorDouble(new double[] { vol.Origin.X, vol.Origin.Y, vol.Origin.Z }));
            // Row-major of [XDir | YDir | ZDir] as columns — each row holds one physical component.
            img.SetDirection(new VectorDouble(new double[]
            {
                vol.XDirection.X, vol.YDirection.X, vol.ZDirection.X,
                vol.XDirection.Y, vol.YDirection.Y, vol.ZDirection.Y,
                vol.XDirection.Z, vol.YDirection.Z, vol.ZDirection.Z
            }));

            // Bulk copy: build an X-fastest flat buffer, then memcpy into the ITK image buffer.
            // This replaces ~50M per-pixel SWIG calls with one Marshal.Copy.
            int total = xSize * ySize * zSize;
            short[] buffer = new short[total];
            int huOffset = vol.HuOffset;
            int idx = 0;
            for (int z = 0; z < zSize; z++)
            {
                var zSlice = vol.Voxels[z];
                for (int y = 0; y < ySize; y++)
                    for (int x = 0; x < xSize; x++)
                        buffer[idx++] = (short)(zSlice[x, y] - huOffset);
            }

            IntPtr dst = img.GetBufferAsInt16();
            Marshal.Copy(buffer, 0, dst, total);
            return img;
        }

        internal static DeformationField DisplacementImageToField(Image dvfImage, VolumeData referenceVol)
        {
            var sz = dvfImage.GetSize();
            int xSize = (int)sz[0], ySize = (int)sz[1], zSize = (int)sz[2];
            var sp = dvfImage.GetSpacing();
            var orig = dvfImage.GetOrigin();
            var dir = dvfImage.GetDirection();

            // Bulk copy: read the interleaved [vx,vy,vz] float64 buffer once, then unpack.
            int total = xSize * ySize * zSize * 3;
            double[] flat = new double[total];
            IntPtr src = dvfImage.GetBufferAsDouble();
            Marshal.Copy(src, flat, 0, total);

            var vectors = new Vec3[zSize][,];
            int rowStride = xSize * 3;
            int sliceStride = ySize * rowStride;
            for (int z = 0; z < zSize; z++)
            {
                var plane = new Vec3[xSize, ySize];
                vectors[z] = plane;
                int zBase = z * sliceStride;
                for (int y = 0; y < ySize; y++)
                {
                    int yBase = zBase + y * rowStride;
                    for (int x = 0; x < xSize; x++)
                    {
                        int i = yBase + x * 3;
                        plane[x, y] = new Vec3(flat[i], flat[i + 1], flat[i + 2]);
                    }
                }
            }

            return new DeformationField
            {
                XSize = xSize, YSize = ySize, ZSize = zSize,
                XRes = sp[0], YRes = sp[1], ZRes = sp[2],
                Origin = new Vec3(orig[0], orig[1], orig[2]),
                // Column-vector orientation: column i = dir[i], dir[i+3], dir[i+6].
                XDirection = new Vec3(dir[0], dir[3], dir[6]),
                YDirection = new Vec3(dir[1], dir[4], dir[7]),
                ZDirection = new Vec3(dir[2], dir[5], dir[8]),
                SourceFOR = referenceVol.FOR,
                Vectors = vectors
            };
        }
    }
}
