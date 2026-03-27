using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Tests.Integration.Models;
using System;

namespace ESAPI_EQD2Viewer.Tests.Integration
{
    /// <summary>
    /// Integration tests for dose-to-CT coordinate mapping and bilinear interpolation.
    /// 
    /// These test the critical path: given a CT pixel coordinate, look up the dose
    /// value in the dose grid, accounting for different resolutions and origins.
    /// Errors here cause dose misregistration — the most dangerous bug category.
    /// 
    /// Uses fixture data to verify coordinate transforms with real geometry parameters.
    /// </summary>
    public class DoseResamplingIntegrationTests
    {
        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void BilinearSample_OnFixtureGrid_ExactPointsShouldMatch(string fixtureName)
        {
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);

            foreach (var slice in slices)
            {
                var grid = FixtureLoader.ToDoseGrid(slice);

                // Sample at exact grid points — should return exact values
                for (int x = 0; x < slice.width; x++)
                    for (int y = 0; y < slice.height; y++)
                    {
                        double sampled = ImageUtils.BilinearSample(
                            grid, slice.width, slice.height, x, y);
                        double expected = grid[x, y];

                        sampled.Should().BeApproximately(expected, 1e-10,
                            $"exact grid point ({x},{y}) on slice {slice.sliceIndex}");
                    }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void BilinearSample_OnFixtureGrid_InterpolatedShouldBeBounded(string fixtureName)
        {
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);

            foreach (var slice in slices)
            {
                var grid = FixtureLoader.ToDoseGrid(slice);
                int w = slice.width, h = slice.height;

                // Sample at midpoints between grid points
                for (int x = 0; x < w - 1; x++)
                    for (int y = 0; y < h - 1; y++)
                    {
                        double sampled = ImageUtils.BilinearSample(
                            grid, w, h, x + 0.5, y + 0.5);

                        // Interpolated value must be bounded by surrounding corners
                        double min4 = Math.Min(Math.Min(grid[x, y], grid[x + 1, y]),
                                               Math.Min(grid[x, y + 1], grid[x + 1, y + 1]));
                        double max4 = Math.Max(Math.Max(grid[x, y], grid[x + 1, y]),
                                               Math.Max(grid[x, y + 1], grid[x + 1, y + 1]));

                        sampled.Should().BeGreaterOrEqualTo(min4 - 1e-10,
                            $"interpolated below min at ({x}.5,{y}.5)");
                        sampled.Should().BeLessOrEqualTo(max4 + 1e-10,
                            $"interpolated above max at ({x}.5,{y}.5)");
                    }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void BilinearSampleRaw_ShouldMatchPrecomputedGy(string fixtureName)
        {
            var scaling = FixtureLoader.LoadDoseScaling(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);

            foreach (var slice in slices)
            {
                var rawGrid = FixtureLoader.ToRawGrid(slice);
                int w = slice.width, h = slice.height;

                // Sample raw at exact grid points — should match Gy values
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        double sampledGy = ImageUtils.BilinearSampleRaw(
                            rawGrid, w, h, x, y,
                            scaling.rawScale, scaling.rawOffset, scaling.unitToGy);

                        sampledGy.Should().BeApproximately(slice.valuesGy[y * w + x], 0.001,
                            $"raw→Gy at ({x},{y}) on slice {slice.sliceIndex}");
                    }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void CoordinateMapping_CtToDose_ShouldMatchReferencePoints(string fixtureName)
        {
            var refPoints = FixtureLoader.LoadReferenceDosePoints(fixtureName);
            var imgGeo = FixtureLoader.LoadImageGeometry(fixtureName);
            var doseGeo = FixtureLoader.LoadDoseGeometry(fixtureName);
            var scaling = FixtureLoader.LoadDoseScaling(fixtureName);
            var slices = FixtureLoader.LoadDoseSlices(fixtureName);
            if (refPoints?.points == null) return;

            foreach (var pt in refPoints.points)
            {
                if (!pt.isInsideDoseGrid) continue;

                // ── Reproduce the CT pixel → dose voxel mapping ──
                // This is the same math as ImageRenderingService.PrepareDoseGrid

                // CT pixel → world coordinate
                double wx = imgGeo.origin[0]
                    + pt.ctPixelX * imgGeo.xRes * imgGeo.xDirection[0]
                    + pt.ctPixelY * imgGeo.yRes * imgGeo.yDirection[0]
                    + pt.ctSlice * imgGeo.zRes * imgGeo.zDirection[0];
                double wy = imgGeo.origin[1]
                    + pt.ctPixelX * imgGeo.xRes * imgGeo.xDirection[1]
                    + pt.ctPixelY * imgGeo.yRes * imgGeo.yDirection[1]
                    + pt.ctSlice * imgGeo.zRes * imgGeo.zDirection[1];
                double wz = imgGeo.origin[2]
                    + pt.ctPixelX * imgGeo.xRes * imgGeo.xDirection[2]
                    + pt.ctPixelY * imgGeo.yRes * imgGeo.yDirection[2]
                    + pt.ctSlice * imgGeo.zRes * imgGeo.zDirection[2];

                // World → dose voxel (nearest neighbor, matching FixtureExporter logic)
                double diffX = wx - doseGeo.origin[0];
                double diffY = wy - doseGeo.origin[1];
                double diffZ = wz - doseGeo.origin[2];

                double fdx = (diffX * doseGeo.xDirection[0] + diffY * doseGeo.xDirection[1]
                             + diffZ * doseGeo.xDirection[2]) / doseGeo.xRes;
                double fdy = (diffX * doseGeo.yDirection[0] + diffY * doseGeo.yDirection[1]
                             + diffZ * doseGeo.yDirection[2]) / doseGeo.yRes;
                double fdz = (diffX * doseGeo.zDirection[0] + diffY * doseGeo.zDirection[1]
                             + diffZ * doseGeo.zDirection[2]) / doseGeo.zRes;

                int ix = (int)Math.Round(fdx);
                int iy = (int)Math.Round(fdy);

                // Verify mapped dose voxel matches what the exporter computed
                ix.Should().Be(pt.doseVoxelX,
                    $"dose voxel X mismatch for CT pixel ({pt.ctPixelX},{pt.ctPixelY})");
                iy.Should().Be(pt.doseVoxelY,
                    $"dose voxel Y mismatch for CT pixel ({pt.ctPixelX},{pt.ctPixelY})");

                // Verify dose value matches
                if (pt.doseGy >= 0)
                {
                    // Find the right slice
                    DoseSlice matchingSlice = null;
                    foreach (var s in slices)
                        if (s.sliceIndex == pt.doseVoxelZ) { matchingSlice = s; break; }

                    if (matchingSlice != null)
                    {
                        int idx = iy * matchingSlice.width + ix;
                        if (idx >= 0 && idx < matchingSlice.valuesGy.Length)
                        {
                            double gridDose = matchingSlice.valuesGy[idx];
                            gridDose.Should().BeApproximately(pt.doseGy, 0.01,
                                $"dose mismatch at CT({pt.ctPixelX},{pt.ctPixelY})→Dose({ix},{iy})");
                        }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void HuOffsetDetection_ShouldMatchExportedValue(string fixtureName)
        {
            var ct = FixtureLoader.LoadCtSubsample(fixtureName);
            if (ct == null) return;

            // Reconstruct int[,] grid from flat array
            var grid = new int[ct.width, ct.height];
            for (int y = 0; y < ct.height; y++)
                for (int x = 0; x < ct.width; x++)
                    grid[x, y] = ct.values[y * ct.width + x];

            int detected = ImageUtils.DetermineHuOffset(grid, ct.width, ct.height);
            detected.Should().Be(ct.detectedHuOffset,
                "HU offset detection should match Eclipse-side detection");
        }
    }
}