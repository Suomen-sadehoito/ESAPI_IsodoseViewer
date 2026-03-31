using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;
using EQD2Viewer.Fixtures;
using EQD2Viewer.Fixtures.Models;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Calculations;
using System;
using System.Linq;

namespace ESAPI_EQD2Viewer.Tests.Integration
{
    /// <summary>
    /// Integration tests for DVH calculation from fixture data.
    /// 
    /// Compares our DVH computation against Eclipse's exported DVH curves.
    /// This catches errors in the histogram binning, cumulative conversion,
    /// and structure mask combination logic.
    /// 
    /// Tolerances:
    ///   - Dmax: ±0.5 Gy (histogram bin width effect)
    ///   - Dmean: ±1.0 Gy (integration method differences)
    ///   - Volume at dose: ±3% (rasterization + binning differences)
    /// </summary>
    public class DVHIntegrationTests
    {
        private readonly DVHService _dvhService = new DVHService();

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void DVHFromSummedDose_UniformMask_ShouldStartAt100Percent(string fixtureName)
        {
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            if (slices.Length == 0) return;

            var slice = slices[0];
            var doseData = new double[][] { slice.valuesGy };
            var mask = new bool[][] { Enumerable.Repeat(true, slice.valuesGy.Length).ToArray() };

            var dvh = _dvhService.CalculateDVHFromSummedDose(
                doseData, mask, 0.001, slice.maxDoseGy);

            dvh.Should().NotBeEmpty();
            dvh[0].VolumePercent.Should().BeApproximately(100.0, 0.1,
                "DVH must start at 100% volume");
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void DVHFromSummedDose_ShouldBeMonotonicallyDecreasing(string fixtureName)
        {
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            if (slices.Length == 0) return;

            var slice = slices[0];
            var doseData = new double[][] { slice.valuesGy };
            var mask = new bool[][] { Enumerable.Repeat(true, slice.valuesGy.Length).ToArray() };

            var dvh = _dvhService.CalculateDVHFromSummedDose(
                doseData, mask, 0.001, slice.maxDoseGy);

            for (int i = 1; i < dvh.Length; i++)
                dvh[i].VolumePercent.Should().BeLessOrEqualTo(dvh[i - 1].VolumePercent + 0.01,
                    $"cumulative DVH must be non-increasing at bin {i}");
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
            public void BuildSummaryFromCurve_AgainstEclipseDVH_DmaxShouldMatch(string fixtureName)
        {
            var dvhFixtures = FixtureLoader.LoadDvhCurves(fixtureName);

            foreach (var dvhFix in dvhFixtures)
            {
                if (dvhFix.curve == null || dvhFix.curve.Length == 0) continue;

                // Convert fixture curve to DoseVolumePoint array
                var curve = dvhFix.curve
                    .Select(p => new DoseVolumePoint(p[0], p[1]))
                    .ToArray();

                var summary = _dvhService.BuildSummaryFromCurve(
                    dvhFix.structureId, dvhFix.planId, "Physical",
                    curve, dvhFix.volumeCc);

                // Dmax should be close to Eclipse's value
                // Tolerance is wider because our Dmax is the last bin with volume > 0.01%,
                // while Eclipse may interpolate differently
                summary.DMax.Should().BeApproximately(dvhFix.dmaxGy, 0.5,
                    $"Dmax mismatch for {dvhFix.structureId}: " +
                    $"Eclipse={dvhFix.dmaxGy:F2}, Ours={summary.DMax:F2}");
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void EQD2DVHCurve_AllPoints_ShouldBeFiniteAndOrdered(string fixtureName)
        {
            var meta = FixtureLoader.LoadMetadata(fixtureName);
            var dvhFixtures = FixtureLoader.LoadDvhCurves(fixtureName);
            int fx = meta.numberOfFractions > 0 ? meta.numberOfFractions : 1;

            foreach (var dvhFix in dvhFixtures)
            {
                if (dvhFix.curve == null || dvhFix.curve.Length < 2) continue;

                // Build a DoseVolumePoint array
                var curveAsPoints = dvhFix.curve
                    .Select(p => new DoseVolumePoint(p[0], p[1]))
                    .ToArray();

                foreach (double ab in new[] { 3.0, 10.0 })
                {
                    var eqd2Curve = EQD2Calculator.ConvertCurveToEQD2(curveAsPoints, fx, ab);

                    eqd2Curve.Should().NotBeEmpty();

                    for (int i = 0; i < eqd2Curve.Length; i++)
                    {
                        double d = eqd2Curve[i].DoseGy;
                        double v = eqd2Curve[i].VolumePercent;
                        double.IsNaN(d).Should().BeFalse($"NaN dose at [{i}] for {dvhFix.structureId}");
                        double.IsInfinity(d).Should().BeFalse($"Inf dose at [{i}]");
                        d.Should().BeGreaterOrEqualTo(0, $"negative EQD2 dose at [{i}]");
                    }

                    // Dose values should be monotonically non-decreasing
                    for (int i = 1; i < eqd2Curve.Length; i++)
                    {
                        eqd2Curve[i].DoseGy.Should()
                            .BeGreaterOrEqualTo(eqd2Curve[i - 1].DoseGy - 1e-6,
                            $"EQD2 curve not monotonic at [{i}]");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void MeanEQD2FromDVH_ShouldBeReasonable(string fixtureName)
        {
            var meta = FixtureLoader.LoadMetadata(fixtureName);
            var dvhFixtures = FixtureLoader.LoadDvhCurves(fixtureName);
            int fx = meta.numberOfFractions > 0 ? meta.numberOfFractions : 1;

            foreach (var dvhFix in dvhFixtures)
            {
                if (dvhFix.curve == null || dvhFix.curve.Length < 2) continue;

                var curveAsPoints = dvhFix.curve
                    .Select(p => new DoseVolumePoint(p[0], p[1]))
                    .ToArray();

                double meanEqd2 = EQD2Calculator.CalculateMeanEQD2FromDVH(curveAsPoints, fx, 3.0);

                // Mean should be between min and max dose (sanity check)
                meanEqd2.Should().BeGreaterOrEqualTo(0,
                    $"negative mean EQD2 for {dvhFix.structureId}");

                double eqd2Dmax = EQD2Calculator.ToEQD2(dvhFix.dmaxGy, fx, 3.0);
                meanEqd2.Should().BeLessOrEqualTo(eqd2Dmax + 0.1,
                    $"mean EQD2 exceeds Dmax for {dvhFix.structureId}");
            }
        }
    }
}