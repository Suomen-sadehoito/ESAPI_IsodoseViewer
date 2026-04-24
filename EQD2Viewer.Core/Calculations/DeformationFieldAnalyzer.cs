using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using System;
using System.Diagnostics;
using System.Threading;

namespace EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Computes quality metrics for a deformation vector field following ESTRO / Bosma et al.
    /// 2024 and AAPM TG-132 recommendations:
    /// Jacobian determinant statistics (min/max/mean/std, folds, extremes, histogram) with
    /// Bosma-endorsed thresholds (folds at J &lt;= 0, caution in 1.2..2.0, extreme at J &gt; 2.0);
    /// displacement magnitude statistics (min/max/mean/percentiles);
    /// curl magnitude of the DVF (rotational component, liver threshold 0.2 from Zachiu 2020);
    /// and B-spline bending energy as a smoothness indicator.
    ///
    /// Assumes the DVF axes are aligned with physical X/Y/Z. This holds for SimpleITK
    /// registrations on standard CT data where the direction matrix is identity; a
    /// non-axis-aligned DVF would need the deformation gradient rotated into physical
    /// space before taking the determinant.
    /// </summary>
    public static class DeformationFieldAnalyzer
    {
        // Jacobian histogram: captures the expected DIR range around 1.0 with headroom.
        private const double JacHistMin = -0.5;
        private const double JacHistMax = 2.5;
        private const int JacHistBins = 60;

        // Jacobian clinical thresholds, per Bosma et al. 2024 (ESTRO DIR QA consensus).
        private const double JacCautionHighLow = 1.2;  // caution band starts
        private const double JacCautionHighHigh = 2.0; // caution band ends == extreme starts

        // Curl magnitude histogram: 0..2 (covers typical tissue curl values comfortably).
        private const double CurlHistMaxValue = 2.0;
        private const int CurlHistBins = 40;
        private const double CurlLiverThreshold = 0.2; // Zachiu 2020 tagged-MR liver upper bound

        // Displacement-magnitude histogram for percentile estimation: 0..500 mm, 0.5 mm bins.
        // Bin index DispHistBins is reserved for overflow (|u| >= DispHistMaxMm).
        private const double DispHistMaxMm = 500.0;
        private const double DispHistBinWidthMm = 0.5;
        private const int DispHistBins = 1000;

        public static DirQualityReport Analyze(DeformationField field, CancellationToken ct = default)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (field.Vectors == null) throw new ArgumentException("field.Vectors is null", nameof(field));
            if (field.XSize < 3 || field.YSize < 3 || field.ZSize < 3)
                throw new ArgumentException("DeformationField too small (need >= 3 voxels per axis).");

            var sw = Stopwatch.StartNew();
            int xSize = field.XSize, ySize = field.YSize, zSize = field.ZSize;
            double dx = field.XRes, dy = field.YRes, dz = field.ZRes;

            double jMin = double.PositiveInfinity, jMax = double.NegativeInfinity;
            double jSum = 0.0, jSqSum = 0.0;
            long jCount = 0, jFolds = 0, jExtremeLow = 0, jCautionHigh = 0, jExtremeHigh = 0;
            var jHist = new int[JacHistBins];
            double jBinWidth = (JacHistMax - JacHistMin) / JacHistBins;

            double cMin = double.PositiveInfinity, cMax = double.NegativeInfinity;
            double cSum = 0.0, cSqSum = 0.0;
            long cCount = 0, cOverLiver = 0;
            var cHist = new int[CurlHistBins];
            double cBinWidth = CurlHistMaxValue / CurlHistBins;

            double dMin = double.PositiveInfinity, dMax = double.NegativeInfinity;
            double dSum = 0.0;
            long dCount = 0;
            var dHist = new int[DispHistBins + 1];

            double beSum = 0.0;
            long beCount = 0;

            double inv2dx = 1.0 / (2.0 * dx);
            double inv2dy = 1.0 / (2.0 * dy);
            double inv2dz = 1.0 / (2.0 * dz);
            double invDx2 = 1.0 / (dx * dx);
            double invDy2 = 1.0 / (dy * dy);
            double invDz2 = 1.0 / (dz * dz);
            double inv4dxdy = 1.0 / (4.0 * dx * dy);
            double inv4dxdz = 1.0 / (4.0 * dx * dz);
            double inv4dydz = 1.0 / (4.0 * dy * dz);

            for (int z = 0; z < zSize; z++)
            {
                if ((z & 0x1F) == 0) ct.ThrowIfCancellationRequested();

                var plane = field.Vectors[z];

                // Pass 1: displacement magnitude over every voxel.
                for (int y = 0; y < ySize; y++)
                {
                    for (int x = 0; x < xSize; x++)
                    {
                        var v = plane[x, y];
                        double mag = Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                        if (mag < dMin) dMin = mag;
                        if (mag > dMax) dMax = mag;
                        dSum += mag;
                        dCount++;
                        int bin = (int)(mag / DispHistBinWidthMm);
                        if (bin >= DispHistBins) bin = DispHistBins;
                        dHist[bin]++;
                    }
                }

                if (z == 0 || z == zSize - 1) continue;

                var planeM = field.Vectors[z - 1];
                var planeP = field.Vectors[z + 1];

                // Pass 2: Jacobian (interior voxels with central differences available).
                for (int y = 1; y < ySize - 1; y++)
                {
                    for (int x = 1; x < xSize - 1; x++)
                    {
                        var vxm = plane[x - 1, y];
                        var vxp = plane[x + 1, y];
                        var vym = plane[x, y - 1];
                        var vyp = plane[x, y + 1];
                        var vzm = planeM[x, y];
                        var vzp = planeP[x, y];

                        double duxdx = (vxp.X - vxm.X) * inv2dx;
                        double duydx = (vxp.Y - vxm.Y) * inv2dx;
                        double duzdx = (vxp.Z - vxm.Z) * inv2dx;

                        double duxdy = (vyp.X - vym.X) * inv2dy;
                        double duydy = (vyp.Y - vym.Y) * inv2dy;
                        double duzdy = (vyp.Z - vym.Z) * inv2dy;

                        double duxdz = (vzp.X - vzm.X) * inv2dz;
                        double duydz = (vzp.Y - vzm.Y) * inv2dz;
                        double duzdz = (vzp.Z - vzm.Z) * inv2dz;

                        // Deformation gradient F = I + grad(u).
                        double a = 1.0 + duxdx, b = duxdy, c = duxdz;
                        double d = duydx, e = 1.0 + duydy, f = duydz;
                        double g = duzdx, h = duzdy, ii = 1.0 + duzdz;

                        double jdet = a * (e * ii - f * h)
                                    - b * (d * ii - f * g)
                                    + c * (d * h - e * g);

                        if (jdet < jMin) jMin = jdet;
                        if (jdet > jMax) jMax = jdet;
                        jSum += jdet;
                        jSqSum += jdet * jdet;
                        jCount++;

                        if (jdet <= 0.0) jFolds++;
                        else if (jdet < 0.2) jExtremeLow++;
                        else if (jdet > JacCautionHighHigh) jExtremeHigh++;
                        else if (jdet > JacCautionHighLow) jCautionHigh++;

                        int jb = (int)((jdet - JacHistMin) / jBinWidth);
                        if (jb < 0) jb = 0;
                        else if (jb >= JacHistBins) jb = JacHistBins - 1;
                        jHist[jb]++;

                        // Curl magnitude — rotational component of the deformation gradient.
                        // curl u = (du_z/dy - du_y/dz, du_x/dz - du_z/dx, du_y/dx - du_x/dy).
                        double curlX = duzdy - duydz;
                        double curlY = duxdz - duzdx;
                        double curlZ = duydx - duxdy;
                        double curlMag = Math.Sqrt(curlX * curlX + curlY * curlY + curlZ * curlZ);

                        if (curlMag < cMin) cMin = curlMag;
                        if (curlMag > cMax) cMax = curlMag;
                        cSum += curlMag;
                        cSqSum += curlMag * curlMag;
                        cCount++;
                        if (curlMag > CurlLiverThreshold) cOverLiver++;

                        int cb = (int)(curlMag / cBinWidth);
                        if (cb < 0) cb = 0;
                        else if (cb >= CurlHistBins) cb = CurlHistBins - 1;
                        cHist[cb]++;

                        // Bending energy needs cross-derivatives, which require one extra voxel
                        // of margin. Skip voxels in the 1-voxel rim around the interior.
                        if (x < 2 || x > xSize - 3 || y < 2 || y > ySize - 3 || z < 2 || z > zSize - 3)
                            continue;

                        var vc = plane[x, y];

                        double d2xx_x = (vxp.X - 2 * vc.X + vxm.X) * invDx2;
                        double d2xx_y = (vxp.Y - 2 * vc.Y + vxm.Y) * invDx2;
                        double d2xx_z = (vxp.Z - 2 * vc.Z + vxm.Z) * invDx2;

                        double d2yy_x = (vyp.X - 2 * vc.X + vym.X) * invDy2;
                        double d2yy_y = (vyp.Y - 2 * vc.Y + vym.Y) * invDy2;
                        double d2yy_z = (vyp.Z - 2 * vc.Z + vym.Z) * invDy2;

                        double d2zz_x = (vzp.X - 2 * vc.X + vzm.X) * invDz2;
                        double d2zz_y = (vzp.Y - 2 * vc.Y + vzm.Y) * invDz2;
                        double d2zz_z = (vzp.Z - 2 * vc.Z + vzm.Z) * invDz2;

                        var vxpyp = plane[x + 1, y + 1];
                        var vxpym = plane[x + 1, y - 1];
                        var vxmyp = plane[x - 1, y + 1];
                        var vxmym = plane[x - 1, y - 1];
                        double d2xy_x = (vxpyp.X - vxpym.X - vxmyp.X + vxmym.X) * inv4dxdy;
                        double d2xy_y = (vxpyp.Y - vxpym.Y - vxmyp.Y + vxmym.Y) * inv4dxdy;
                        double d2xy_z = (vxpyp.Z - vxpym.Z - vxmyp.Z + vxmym.Z) * inv4dxdy;

                        var vxpzp = planeP[x + 1, y];
                        var vxpzm = planeM[x + 1, y];
                        var vxmzp = planeP[x - 1, y];
                        var vxmzm = planeM[x - 1, y];
                        double d2xz_x = (vxpzp.X - vxpzm.X - vxmzp.X + vxmzm.X) * inv4dxdz;
                        double d2xz_y = (vxpzp.Y - vxpzm.Y - vxmzp.Y + vxmzm.Y) * inv4dxdz;
                        double d2xz_z = (vxpzp.Z - vxpzm.Z - vxmzp.Z + vxmzm.Z) * inv4dxdz;

                        var vypzp = planeP[x, y + 1];
                        var vypzm = planeM[x, y + 1];
                        var vymzp = planeP[x, y - 1];
                        var vymzm = planeM[x, y - 1];
                        double d2yz_x = (vypzp.X - vypzm.X - vymzp.X + vymzm.X) * inv4dydz;
                        double d2yz_y = (vypzp.Y - vypzm.Y - vymzp.Y + vymzm.Y) * inv4dydz;
                        double d2yz_z = (vypzp.Z - vypzm.Z - vymzp.Z + vymzm.Z) * inv4dydz;

                        // Rueckert-style bending energy: sum over components c and pairs (i,j)
                        // of (d2 u_c / d x_i d x_j)^2. Diagonal terms counted once, symmetric
                        // off-diagonal pairs counted twice.
                        double be =
                            d2xx_x * d2xx_x + d2xx_y * d2xx_y + d2xx_z * d2xx_z
                          + d2yy_x * d2yy_x + d2yy_y * d2yy_y + d2yy_z * d2yy_z
                          + d2zz_x * d2zz_x + d2zz_y * d2zz_y + d2zz_z * d2zz_z
                          + 2.0 * (d2xy_x * d2xy_x + d2xy_y * d2xy_y + d2xy_z * d2xy_z)
                          + 2.0 * (d2xz_x * d2xz_x + d2xz_y * d2xz_y + d2xz_z * d2xz_z)
                          + 2.0 * (d2yz_x * d2yz_x + d2yz_y * d2yz_y + d2yz_z * d2yz_z);
                        beSum += be;
                        beCount++;
                    }
                }
            }

            double jMean = jCount > 0 ? jSum / jCount : 0.0;
            double jVar = jCount > 0 ? (jSqSum / jCount) - jMean * jMean : 0.0;
            double jStd = jVar > 0 ? Math.Sqrt(jVar) : 0.0;

            double cMean = cCount > 0 ? cSum / cCount : 0.0;
            double cVar = cCount > 0 ? (cSqSum / cCount) - cMean * cMean : 0.0;
            double cStd = cVar > 0 ? Math.Sqrt(cVar) : 0.0;

            double p95 = PercentileFromHistogram(dHist, dCount, 0.95, DispHistBinWidthMm, DispHistMaxMm);
            double p99 = PercentileFromHistogram(dHist, dCount, 0.99, DispHistBinWidthMm, DispHistMaxMm);

            return new DirQualityReport
            {
                XSize = xSize,
                YSize = ySize,
                ZSize = zSize,
                XRes = dx,
                YRes = dy,
                ZRes = dz,

                JacobianMin = jCount > 0 ? jMin : 0.0,
                JacobianMax = jCount > 0 ? jMax : 0.0,
                JacobianMean = jMean,
                JacobianStdDev = jStd,
                JacobianVoxelCount = jCount,
                JacobianFoldCount = jFolds,
                JacobianExtremeLowCount = jExtremeLow,
                JacobianCautionHighCount = jCautionHigh,
                JacobianExtremeHighCount = jExtremeHigh,
                JacobianHistogramBinWidth = jBinWidth,
                JacobianHistogramMin = JacHistMin,
                JacobianHistogram = jHist,

                CurlMagMin = cCount > 0 ? cMin : 0.0,
                CurlMagMax = cCount > 0 ? cMax : 0.0,
                CurlMagMean = cMean,
                CurlMagStdDev = cStd,
                CurlVoxelCount = cCount,
                CurlOverLiverThresholdCount = cOverLiver,
                CurlHistogramBinWidth = cBinWidth,
                CurlHistogramMax = CurlHistMaxValue,
                CurlHistogram = cHist,

                DisplacementMinMm = dCount > 0 ? dMin : 0.0,
                DisplacementMaxMm = dCount > 0 ? dMax : 0.0,
                DisplacementMeanMm = dCount > 0 ? dSum / dCount : 0.0,
                DisplacementP95Mm = p95,
                DisplacementP99Mm = p99,
                DisplacementVoxelCount = dCount,

                BendingEnergyMean = beCount > 0 ? beSum / beCount : 0.0,
                BendingEnergyTotal = beSum,
                BendingEnergyVoxelCount = beCount,

                ComputeDuration = sw.Elapsed
            };
        }

        private static double PercentileFromHistogram(int[] hist, long totalCount, double percentile,
            double binWidth, double maxValue)
        {
            if (totalCount == 0) return 0.0;
            long target = (long)Math.Ceiling(percentile * totalCount);
            if (target < 1) target = 1;
            long cum = 0;
            for (int i = 0; i < hist.Length - 1; i++)
            {
                cum += hist[i];
                if (cum >= target)
                {
                    long prevCum = cum - hist[i];
                    double frac = hist[i] > 0 ? (double)(target - prevCum) / hist[i] : 0.0;
                    return (i + frac) * binWidth;
                }
            }
            return maxValue;
        }
    }
}
