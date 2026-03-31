using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Fixtures;
using EQD2Viewer.Core.Calculations;
using System.Linq;

namespace ESAPI_EQD2Viewer.Tests.Integration
{
    /// <summary>
    /// Integration tests for the EQD2 calculation pipeline using fixture data.
    /// 
    /// These tests load dose grids exported from Eclipse (or synthetic equivalents)
    /// and verify the entire EQD2 conversion chain: raw→Gy→EQD2.
    /// 
    /// The key difference from unit tests:
    ///   - Unit tests use hand-crafted scalars (e.D2Calculator.ToEQD2(50, 25, 3))
    ///   - Integration tests use realistic voxel arrays through the full pipeline
    /// </summary>
    public class EQD2PipelineIntegrationTests
    {
        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void DoseScaling_RawToGy_ShouldMatchExportedValues(string fixtureName)
        {
            // ── Arrange: load raw voxels and the Gy values Eclipse computed ──
            var scaling = FixtureLoader.LoadDoseScaling(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            slices.Should().NotBeEmpty("fixture must contain at least one dose slice");

            foreach (var slice in slices)
            {
                // ── Act: apply our raw→Gy conversion (same as ImageRenderingService) ──
                for (int i = 0; i < slice.rawValues.Length; i++)
                {
                    double ourGy = (slice.rawValues[i] * scaling.rawScale + scaling.rawOffset)
                                   * scaling.unitToGy;

                    // ── Assert: must match the Gy value Eclipse exported ──
                    ourGy.Should().BeApproximately(slice.valuesGy[i], 0.001,
                        $"raw→Gy conversion mismatch at index {i} on slice {slice.sliceIndex}, " +
                        $"raw={slice.rawValues[i]}, expected={slice.valuesGy[i]:F6}, got={ourGy:F6}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void EQD2Conversion_VoxelLevel_FastPathMatchesStandard(string fixtureName)
        {
            var meta = FixtureLoader.LoadMetadata(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            int fx = meta.numberOfFractions;
            if (fx <= 0) return; // skip invalid

            foreach (double alphaBeta in new[] { 2.0, 3.0, 10.0 })
            {
                EQD2Calculator.GetVoxelScalingFactors(fx, alphaBeta, out double qf, out double lf);

                foreach (var slice in slices)
                {
                    for (int i = 0; i < slice.valuesGy.Length; i++)
                    {
                        double dGy = slice.valuesGy[i];
                        if (dGy <= 0) continue;

                        double standard = EQD2Calculator.ToEQD2(dGy, fx, alphaBeta);
                        double fast = EQD2Calculator.ToEQD2Fast(dGy, qf, lf);

                        fast.Should().BeApproximately(standard, 1e-8,
                            $"fast≠standard at slice {slice.sliceIndex}[{i}], " +
                            $"D={dGy:F4}, α/β={alphaBeta}");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void EQD2Conversion_ShouldMatchMathematicalFormula(string fixtureName)
        {
            var meta = FixtureLoader.LoadMetadata(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);

            // Cannot run the test if there are no fractions
            int fx = meta.numberOfFractions;
            if (fx <= 0) return;

            double alphaBeta = 3.0; // Use an alpha/beta ratio of 3.0 for the test

            foreach (var slice in slices)
            {
                for (int i = 0; i < slice.valuesGy.Length; i++)
                {
                    double dGy = slice.valuesGy[i];

                    // Skip near-zero doses to avoid computational noise
                    if (dGy <= 0.01) continue;

                    // 1. Calculate the expected value (independent answer key)
                    // Calculate the dose per fraction for THIS INDIVIDUAL voxel
                    double voxelDosePerFraction = dGy / fx;

                    // Calculate the expected EQD2 according to the linear-quadratic model
                    double expectedEqd2 = dGy * ((voxelDosePerFraction + alphaBeta) / (2.0 + alphaBeta));

                    // 2. Execute the implemented calculator
                    double ourEqd2 = EQD2Calculator.ToEQD2(dGy, fx, alphaBeta);

                    // 3. Assert: The implementation must match the exact mathematical formula
                    ourEqd2.Should().BeApproximately(expectedEqd2, 0.001,
                        $"EQD2 math failed for voxel: TotalDose={dGy:F4}, Fx={fx}, a/b={alphaBeta}");
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void EQD2Conversion_AllVoxels_ShouldBeNonNegativeAndFinite(string fixtureName)
        {
            var meta = FixtureLoader.LoadMetadata(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            int fx = meta.numberOfFractions > 0 ? meta.numberOfFractions : 1;

            foreach (var slice in slices)
            {
                for (int i = 0; i < slice.valuesGy.Length; i++)
                {
                    double dGy = slice.valuesGy[i];
                    double eqd2 = EQD2Calculator.ToEQD2(dGy, fx, 3.0);

                    eqd2.Should().BeGreaterOrEqualTo(0,
                        $"negative EQD2 at slice {slice.sliceIndex}[{i}]");
                    double.IsNaN(eqd2).Should().BeFalse($"NaN at slice {slice.sliceIndex}[{i}]");
                    double.IsInfinity(eqd2).Should().BeFalse($"Inf at slice {slice.sliceIndex}[{i}]");
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void EQD2Conversion_MaxDose_ShouldMatchSliceMaximum(string fixtureName)
        {
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);

            foreach (var slice in slices)
            {
                double computedMax = slice.valuesGy.Max();
                slice.maxDoseGy.Should().BeApproximately(computedMax, 0.01,
                    $"max dose mismatch on slice {slice.sliceIndex}");
            }
        }
    }
}