using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using System;

namespace EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Computes the axis-aligned bounding box of two <see cref="VolumeData"/> volumes in
    /// their own patient coordinate system, and the overlap both (a) in raw coordinates
    /// and (b) after translating the moving volume so its center coincides with the fixed
    /// volume center. The latter matches what SimpleITK's CenteredTransformInitializer in
    /// GEOMETRY mode does as the first step of registration, and is the overlap that DIR
    /// actually has to work with.
    ///
    /// Runs in microseconds - uses metadata only, no voxel access.
    /// </summary>
    public static class VolumeOverlapAnalyzer
    {
        public static VolumeOverlapReport Analyze(VolumeData fixed_, VolumeData moving)
        {
            if (fixed_ == null) throw new ArgumentNullException(nameof(fixed_));
            if (moving == null) throw new ArgumentNullException(nameof(moving));

            var (fMin, fMax) = Aabb(fixed_);
            var (mMin, mMax) = Aabb(moving);

            var fCenter = Midpoint(fMin, fMax);
            var mCenter = Midpoint(mMin, mMax);
            var offset = new Vec3(mCenter.X - fCenter.X, mCenter.Y - fCenter.Y, mCenter.Z - fCenter.Z);
            double offsetMag = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y + offset.Z * offset.Z);

            // Raw overlap (in absolute patient coordinates).
            var (rawExtent, rawHas, rawVolCm3) = OverlapBox(fMin, fMax, mMin, mMax);

            // Centered overlap: translate moving AABB so its center equals fixed center.
            var mMinCentered = new Vec3(mMin.X - offset.X, mMin.Y - offset.Y, mMin.Z - offset.Z);
            var mMaxCentered = new Vec3(mMax.X - offset.X, mMax.Y - offset.Y, mMax.Z - offset.Z);
            var (centExtent, _, centVolCm3) = OverlapBox(fMin, fMax, mMinCentered, mMaxCentered);

            double fVolCm3 = BoxVolumeCm3(fMin, fMax);
            double mVolCm3 = BoxVolumeCm3(mMin, mMax);

            return new VolumeOverlapReport
            {
                FixedId = fixed_.Geometry.Id,
                MovingId = moving.Geometry.Id,
                FixedFOR = fixed_.FOR,
                MovingFOR = moving.FOR,

                FixedXSize = fixed_.XSize, FixedYSize = fixed_.YSize, FixedZSize = fixed_.ZSize,
                FixedXRes = fixed_.XRes, FixedYRes = fixed_.YRes, FixedZRes = fixed_.ZRes,
                FixedAabbMin = fMin, FixedAabbMax = fMax, FixedCenter = fCenter,

                MovingXSize = moving.XSize, MovingYSize = moving.YSize, MovingZSize = moving.ZSize,
                MovingXRes = moving.XRes, MovingYRes = moving.YRes, MovingZRes = moving.ZRes,
                MovingAabbMin = mMin, MovingAabbMax = mMax, MovingCenter = mCenter,

                CenterOffset = offset,
                CenterOffsetMagnitude = offsetMag,

                RawHasOverlap = rawHas,
                RawOverlapExtent = rawExtent,
                RawOverlapVolumeCm3 = rawVolCm3,
                RawOverlapPercentOfFixed = fVolCm3 > 0 ? 100.0 * rawVolCm3 / fVolCm3 : 0.0,
                RawOverlapPercentOfMoving = mVolCm3 > 0 ? 100.0 * rawVolCm3 / mVolCm3 : 0.0,

                CenteredOverlapExtent = centExtent,
                CenteredOverlapVolumeCm3 = centVolCm3,
                CenteredOverlapPercentOfFixed = fVolCm3 > 0 ? 100.0 * centVolCm3 / fVolCm3 : 0.0,
                CenteredOverlapPercentOfMoving = mVolCm3 > 0 ? 100.0 * centVolCm3 / mVolCm3 : 0.0,

                FixedVolumeCm3 = fVolCm3,
                MovingVolumeCm3 = mVolCm3
            };
        }

        /// <summary>
        /// Axis-aligned bounding box of a volume, computed from its 8 corner voxel centers.
        /// Works for any direction matrix, not just identity.
        /// </summary>
        private static (Vec3 min, Vec3 max) Aabb(VolumeData v)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

            for (int ix = 0; ix <= 1; ix++)
            for (int iy = 0; iy <= 1; iy++)
            for (int iz = 0; iz <= 1; iz++)
            {
                double i = ix * (v.XSize - 1);
                double j = iy * (v.YSize - 1);
                double k = iz * (v.ZSize - 1);

                double x = v.Origin.X
                         + i * v.XRes * v.XDirection.X
                         + j * v.YRes * v.YDirection.X
                         + k * v.ZRes * v.ZDirection.X;
                double y = v.Origin.Y
                         + i * v.XRes * v.XDirection.Y
                         + j * v.YRes * v.YDirection.Y
                         + k * v.ZRes * v.ZDirection.Y;
                double z = v.Origin.Z
                         + i * v.XRes * v.XDirection.Z
                         + j * v.YRes * v.YDirection.Z
                         + k * v.ZRes * v.ZDirection.Z;

                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            return (new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
        }

        private static Vec3 Midpoint(Vec3 a, Vec3 b)
            => new Vec3(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y), 0.5 * (a.Z + b.Z));

        private static double BoxVolumeCm3(Vec3 min, Vec3 max)
        {
            double lx = max.X - min.X;
            double ly = max.Y - min.Y;
            double lz = max.Z - min.Z;
            return (lx * ly * lz) / 1000.0; // mm^3 -> cm^3
        }

        private static (Vec3 extent, bool hasOverlap, double volCm3) OverlapBox(
            Vec3 aMin, Vec3 aMax, Vec3 bMin, Vec3 bMax)
        {
            double ox = Math.Min(aMax.X, bMax.X) - Math.Max(aMin.X, bMin.X);
            double oy = Math.Min(aMax.Y, bMax.Y) - Math.Max(aMin.Y, bMin.Y);
            double oz = Math.Min(aMax.Z, bMax.Z) - Math.Max(aMin.Z, bMin.Z);

            bool has = ox > 0 && oy > 0 && oz > 0;
            double vol = has ? (ox * oy * oz) / 1000.0 : 0.0;
            var extent = new Vec3(Math.Max(0, ox), Math.Max(0, oy), Math.Max(0, oz));
            return (extent, has, vol);
        }
    }
}
