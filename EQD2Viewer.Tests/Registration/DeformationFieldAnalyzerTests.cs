using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using FluentAssertions;
using System;

namespace EQD2Viewer.Tests.Registration
{
    /// <summary>
    /// Analytic sanity tests for <see cref="DeformationFieldAnalyzer"/> using known
    /// deformation fields where Jacobian and bending energy have closed-form values.
    /// </summary>
    public class DeformationFieldAnalyzerTests
    {
        private static DeformationField MakeField(int n, Func<int, int, int, Vec3> u, double res = 1.0)
        {
            var vectors = new Vec3[n][,];
            for (int z = 0; z < n; z++)
            {
                vectors[z] = new Vec3[n, n];
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        vectors[z][x, y] = u(x, y, z);
            }
            return new DeformationField
            {
                XSize = n, YSize = n, ZSize = n,
                XRes = res, YRes = res, ZRes = res,
                Origin = new Vec3(0, 0, 0),
                XDirection = new Vec3(1, 0, 0),
                YDirection = new Vec3(0, 1, 0),
                ZDirection = new Vec3(0, 0, 1),
                Vectors = vectors
            };
        }

        [Fact]
        public void IdentityField_HasUnitJacobianNoFoldsZeroDisplacement()
        {
            var f = MakeField(7, (x, y, z) => new Vec3(0, 0, 0));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.JacobianMin.Should().BeApproximately(1.0, 1e-9);
            r.JacobianMax.Should().BeApproximately(1.0, 1e-9);
            r.JacobianMean.Should().BeApproximately(1.0, 1e-9);
            r.JacobianStdDev.Should().BeApproximately(0.0, 1e-9);
            r.JacobianFoldCount.Should().Be(0);
            r.JacobianExtremeLowCount.Should().Be(0);
            r.JacobianCautionHighCount.Should().Be(0);
            r.JacobianExtremeHighCount.Should().Be(0);
            r.DisplacementMinMm.Should().Be(0);
            r.DisplacementMaxMm.Should().Be(0);
            r.DisplacementMeanMm.Should().Be(0);
            r.CurlMagMax.Should().BeApproximately(0.0, 1e-9);
            r.CurlMagMean.Should().BeApproximately(0.0, 1e-9);
            r.CurlOverLiverThresholdCount.Should().Be(0);
            r.BendingEnergyTotal.Should().BeApproximately(0.0, 1e-9);
            r.Verdict.Should().Be(DirQualityVerdict.Pass);
        }

        [Fact]
        public void UniformTranslation_HasUnitJacobianAndConstantDisplacement()
        {
            var t = new Vec3(1, 2, 3);
            double expectedMag = Math.Sqrt(1 + 4 + 9);

            var f = MakeField(7, (x, y, z) => t);
            var r = DeformationFieldAnalyzer.Analyze(f);

            r.JacobianMean.Should().BeApproximately(1.0, 1e-9);
            r.JacobianFoldCount.Should().Be(0);
            r.DisplacementMinMm.Should().BeApproximately(expectedMag, 1e-9);
            r.DisplacementMaxMm.Should().BeApproximately(expectedMag, 1e-9);
            r.DisplacementMeanMm.Should().BeApproximately(expectedMag, 1e-9);
            r.BendingEnergyTotal.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void IsotropicScaling_JacobianEqualsScaleCubedAndLandsInCautionBand()
        {
            // u(x) = (s-1) * x  =>  phi(x) = x + u(x) = s * x  =>  J = s^3 = 1.728
            // With Bosma thresholds: 1.728 is in (1.2, 2.0] caution band, not extreme.
            const double s = 1.2;
            var f = MakeField(7, (x, y, z) => new Vec3((s - 1) * x, (s - 1) * y, (s - 1) * z));

            var r = DeformationFieldAnalyzer.Analyze(f);

            double expected = s * s * s;
            r.JacobianMin.Should().BeApproximately(expected, 1e-9);
            r.JacobianMax.Should().BeApproximately(expected, 1e-9);
            r.JacobianMean.Should().BeApproximately(expected, 1e-9);
            r.JacobianFoldCount.Should().Be(0);
            r.JacobianCautionHighCount.Should().Be(r.JacobianVoxelCount);
            r.JacobianExtremeHighCount.Should().Be(0);
            r.BendingEnergyTotal.Should().BeApproximately(0.0, 1e-9);
        }

        [Fact]
        public void LargeScaling_FallsIntoExtremeHigh()
        {
            // s = 1.5  =>  J = 3.375 > 2.0 -> extreme high
            const double s = 1.5;
            var f = MakeField(7, (x, y, z) => new Vec3((s - 1) * x, (s - 1) * y, (s - 1) * z));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.JacobianMean.Should().BeApproximately(s * s * s, 1e-9);
            r.JacobianExtremeHighCount.Should().Be(r.JacobianVoxelCount);
            r.JacobianCautionHighCount.Should().Be(0);
            r.Verdict.Should().Be(DirQualityVerdict.Caution);
        }

        [Fact]
        public void FoldingField_DetectsFoldsEverywhere()
        {
            // u_x = -2 x  =>  du_x/dx = -2  =>  F = diag(-1, 1, 1)  =>  J = -1 everywhere.
            var f = MakeField(7, (x, y, z) => new Vec3(-2.0 * x, 0, 0));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.JacobianMax.Should().BeLessThan(0.0);
            r.JacobianFoldCount.Should().Be(r.JacobianVoxelCount);
            r.JacobianFoldPercent.Should().BeApproximately(100.0, 1e-9);
            r.Verdict.Should().Be(DirQualityVerdict.Fail);
        }

        [Fact]
        public void QuadraticField_HasBendingEnergyEqualToExpectedValue()
        {
            // u_x = x^2  =>  d2 u_x / dx^2 = 2, other 2nd derivatives = 0.
            // Per interior voxel bending energy = 2^2 = 4.
            var f = MakeField(7, (x, y, z) => new Vec3((double)x * x, 0, 0));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.BendingEnergyVoxelCount.Should().BeGreaterThan(0);
            r.BendingEnergyMean.Should().BeApproximately(4.0, 1e-9);
            r.BendingEnergyTotal.Should().BeApproximately(4.0 * r.BendingEnergyVoxelCount, 1e-9);
        }

        [Fact]
        public void Analyze_ThrowsWhenFieldTooSmall()
        {
            var f = MakeField(2, (x, y, z) => new Vec3(0, 0, 0));

            Action act = () => DeformationFieldAnalyzer.Analyze(f);

            act.Should().Throw<ArgumentException>();
        }

        [Fact]
        public void Analyze_ThrowsOnNullField()
        {
            Action act = () => DeformationFieldAnalyzer.Analyze(null!);

            act.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Analyze_ReportsGridMetadata()
        {
            var f = MakeField(5, (x, y, z) => new Vec3(0, 0, 0), res: 2.5);

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.XSize.Should().Be(5);
            r.YSize.Should().Be(5);
            r.ZSize.Should().Be(5);
            r.XRes.Should().Be(2.5);
            r.YRes.Should().Be(2.5);
            r.ZRes.Should().Be(2.5);
            r.JacobianVoxelCount.Should().Be(27); // 3x3x3 interior
        }

        [Fact]
        public void UniformScaling_ResPickedUpCorrectly()
        {
            // With res=2.0, physical position of voxel (i,j,k) = (2i, 2j, 2k).
            // u(phys) = (s-1) * phys  =>  per-voxel u_x = (s-1) * 2i.
            const double s = 0.8; // contraction
            const double res = 2.0;
            var f = MakeField(7, (x, y, z)
                => new Vec3((s - 1) * res * x, (s - 1) * res * y, (s - 1) * res * z), res);

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.JacobianMean.Should().BeApproximately(s * s * s, 1e-9);
            r.JacobianFoldCount.Should().Be(0);
        }

        [Fact]
        public void IdentityField_HasZeroCurl()
        {
            var f = MakeField(7, (x, y, z) => new Vec3(0, 0, 0));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.CurlMagMin.Should().BeApproximately(0.0, 1e-9);
            r.CurlMagMax.Should().BeApproximately(0.0, 1e-9);
            r.CurlMagMean.Should().BeApproximately(0.0, 1e-9);
            r.CurlOverLiverThresholdCount.Should().Be(0);
        }

        [Fact]
        public void UniformTranslation_HasZeroCurl()
        {
            var f = MakeField(7, (x, y, z) => new Vec3(1, 2, 3));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.CurlMagMax.Should().BeApproximately(0.0, 1e-9);
            r.CurlOverLiverThresholdCount.Should().Be(0);
        }

        [Fact]
        public void RotationalField_HasExpectedCurlMagnitude()
        {
            // Rigid rotation about z-axis: u = (-y, x, 0)
            //   du_x/dy = -1, du_y/dx = 1, all other 1st partials zero
            //   curl_z = du_y/dx - du_x/dy = 1 - (-1) = 2, curl_x = curl_y = 0
            //   |curl| = 2 everywhere.
            var f = MakeField(7, (x, y, z) => new Vec3(-y, x, 0));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.CurlMagMin.Should().BeApproximately(2.0, 1e-9);
            r.CurlMagMax.Should().BeApproximately(2.0, 1e-9);
            r.CurlMagMean.Should().BeApproximately(2.0, 1e-9);
            r.CurlOverLiverThresholdCount.Should().Be(r.CurlVoxelCount);
        }

        [Fact]
        public void IsotropicScaling_HasZeroCurl()
        {
            // u = (s-1) * x  =>  curl has no rotational component (pure divergence).
            const double s = 1.15;
            var f = MakeField(7, (x, y, z) => new Vec3((s - 1) * x, (s - 1) * y, (s - 1) * z));

            var r = DeformationFieldAnalyzer.Analyze(f);

            r.CurlMagMax.Should().BeApproximately(0.0, 1e-9);
        }
    }
}
