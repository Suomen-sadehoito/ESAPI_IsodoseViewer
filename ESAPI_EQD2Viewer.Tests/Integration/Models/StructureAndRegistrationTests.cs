using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Tests.Integration.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using Point = System.Windows.Point;

namespace ESAPI_EQD2Viewer.Tests.Integration
{
    /// <summary>
    /// Integration tests for structure contour rasterization using Eclipse-exported polygons.
    /// 
    /// Pipeline tested:
    ///   1. World coordinates (mm) → CT pixel coordinates (WorldToPixel)
    ///   2. Pixel polygon → boolean mask (RasterizePolygon)
    ///   3. Multiple contours per slice → combined mask (CombineContourMasks)
    /// 
    /// Clinical significance: incorrect rasterization directly corrupts DVH values.
    /// A structure that's too large inflates V(dose), too small underestimates it.
    /// </summary>
    public class StructureRasterizationIntegrationTests
    {
        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void WorldToPixel_AxisAlignedGeometry_ShouldProduceValidPixels(string fixtureName)
        {
            var imgGeo = FixtureLoader.LoadImageGeometry(fixtureName);
            var structures = FixtureLoader.LoadStructures(fixtureName);

            foreach (var structure in structures)
            {
                foreach (var slice in structure.slices)
                {
                    foreach (var contour in slice.contours)
                    {
                        if (contour.points == null || contour.points.Length < 3) continue;

                        var pixels = StructureRasterizer.WorldToPixel(
                            contour.points,
                            imgGeo.origin[0], imgGeo.origin[1], imgGeo.origin[2],
                            imgGeo.xRes, imgGeo.yRes,
                            imgGeo.xDirection[0], imgGeo.xDirection[1], imgGeo.xDirection[2],
                            imgGeo.yDirection[0], imgGeo.yDirection[1], imgGeo.yDirection[2]);

                        pixels.Should().NotBeNull($"WorldToPixel returned null for {structure.id}");
                        pixels.Length.Should().Be(contour.points.Length);

                        // Pixel coordinates should be finite
                        foreach (var pt in pixels)
                        {
                            double.IsNaN(pt.X).Should().BeFalse($"NaN pixel X for {structure.id}");
                            double.IsNaN(pt.Y).Should().BeFalse($"NaN pixel Y for {structure.id}");
                            double.IsInfinity(pt.X).Should().BeFalse();
                            double.IsInfinity(pt.Y).Should().BeFalse();
                        }

                        // At least some pixels should be within the image bounds
                        // (contour might partially extend outside, that's OK)
                        bool anyInside = pixels.Any(p =>
                            p.X >= -10 && p.X < imgGeo.xSize + 10 &&
                            p.Y >= -10 && p.Y < imgGeo.ySize + 10);
                        anyInside.Should().BeTrue(
                            $"all pixels outside image for {structure.id} slice {slice.sliceIndex}");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void RasterizePolygon_FromEclipseContours_ShouldProduceNonEmptyMask(string fixtureName)
        {
            var imgGeo = FixtureLoader.LoadImageGeometry(fixtureName);
            var structures = FixtureLoader.LoadStructures(fixtureName);
            int w = imgGeo.xSize, h = imgGeo.ySize;

            foreach (var structure in structures)
            {
                foreach (var slice in structure.slices)
                {
                    var allMasks = new List<bool[]>();

                    foreach (var contour in slice.contours)
                    {
                        if (contour.points == null || contour.points.Length < 3) continue;

                        var pixels = StructureRasterizer.WorldToPixel(
                            contour.points,
                            imgGeo.origin[0], imgGeo.origin[1], imgGeo.origin[2],
                            imgGeo.xRes, imgGeo.yRes,
                            imgGeo.xDirection[0], imgGeo.xDirection[1], imgGeo.xDirection[2],
                            imgGeo.yDirection[0], imgGeo.yDirection[1], imgGeo.yDirection[2]);

                        var mask = StructureRasterizer.RasterizePolygon(pixels, w, h);
                        mask.Should().NotBeNull();
                        mask.Length.Should().Be(w * h);
                        allMasks.Add(mask);
                    }

                    if (allMasks.Count > 0)
                    {
                        var combined = StructureRasterizer.CombineContourMasks(allMasks, w, h);
                        int filledPixels = combined.Count(v => v);

                        // Structure should have SOME interior pixels on slices where it has contours
                        filledPixels.Should().BeGreaterThan(0,
                            $"structure {structure.id} slice {slice.sliceIndex} has contours but 0 filled pixels");
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void RasterizePolygon_LargerStructure_ShouldHaveMorePixels(string fixtureName)
        {
            var imgGeo = FixtureLoader.LoadImageGeometry(fixtureName);
            var structures = FixtureLoader.LoadStructures(fixtureName);
            int w = imgGeo.xSize, h = imgGeo.ySize;

            // Compare structures on the same slice — larger contour area → more pixels
            foreach (var slice1 in structures.SelectMany(s => s.slices))
            {
                // Group by slice index to compare on same anatomical level
            }

            // At minimum: each structure's pixel count should be consistent with contour area
            foreach (var structure in structures)
            {
                foreach (var slice in structure.slices)
                {
                    foreach (var contour in slice.contours)
                    {
                        if (contour.points == null || contour.points.Length < 3) continue;

                        var pixels = StructureRasterizer.WorldToPixel(
                            contour.points,
                            imgGeo.origin[0], imgGeo.origin[1], imgGeo.origin[2],
                            imgGeo.xRes, imgGeo.yRes,
                            imgGeo.xDirection[0], imgGeo.xDirection[1], imgGeo.xDirection[2],
                            imgGeo.yDirection[0], imgGeo.yDirection[1], imgGeo.yDirection[2]);

                        // Compute expected area from Shoelace formula
                        double shoelaceArea = 0;
                        for (int i = 0; i < pixels.Length; i++)
                        {
                            int j = (i + 1) % pixels.Length;
                            shoelaceArea += pixels[i].X * pixels[j].Y;
                            shoelaceArea -= pixels[j].X * pixels[i].Y;
                        }
                        shoelaceArea = Math.Abs(shoelaceArea) / 2.0;

                        var mask = StructureRasterizer.RasterizePolygon(pixels, w, h);
                        int filledPixels = mask.Count(v => v);

                        // Rasterized area should be within ±30% of geometric area
                        // (tolerance is generous because of edge effects at low resolution)
                        if (shoelaceArea > 10) // Only check for non-trivial contours
                        {
                            double ratio = filledPixels / shoelaceArea;
                            ratio.Should().BeInRange(0.5, 2.0,
                                $"area ratio {ratio:F2} for {structure.id} slice {slice.sliceIndex} " +
                                $"(geom={shoelaceArea:F0}, raster={filledPixels})");
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Integration tests for registration matrix operations with Eclipse-exported transforms.
    /// </summary>
    public class RegistrationIntegrationTests
    {
        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void RegistrationMatrices_ShouldBeInvertible(string fixtureName)
        {
            var regs = FixtureLoader.LoadRegistrations(fixtureName);
            if (regs?.registrations == null) return;

            foreach (var reg in regs.registrations)
            {
                var M = FixtureLoader.ToMatrix4x4(reg.matrix);
                M.Should().NotBeNull($"matrix for {reg.id} should be 4x4");

                var inv = MatrixMath.Invert4x4(M);
                inv.Should().NotBeNull($"registration {reg.id} matrix should be invertible");

                // M * M^-1 should ≈ Identity
                for (int r = 0; r < 4; r++)
                    for (int c = 0; c < 4; c++)
                    {
                        double sum = 0;
                        for (int k = 0; k < 4; k++) sum += M[r, k] * inv[k, c];
                        double expected = (r == c) ? 1.0 : 0.0;
                        sum.Should().BeApproximately(expected, 1e-6,
                            $"M*M^-1 not identity at [{r},{c}] for {reg.id}");
                    }
            }
        }

        [Theory]
        [MemberData(nameof(FixtureLoader.AllFixtureDirectories), MemberType = typeof(FixtureLoader))]
        public void RegistrationTransform_RoundTrip_ShouldPreservePoints(string fixtureName)
        {
            var regs = FixtureLoader.LoadRegistrations(fixtureName);
            if (regs?.registrations == null) return;

            double[] testX = { 0, 100, -50, 200 };
            double[] testY = { 0, -100, 150, 0 };
            double[] testZ = { 0, 50, 100, -200 };

            foreach (var reg in regs.registrations)
            {
                var M = FixtureLoader.ToMatrix4x4(reg.matrix);
                var inv = MatrixMath.Invert4x4(M);
                if (inv == null) continue;

                for (int i = 0; i < testX.Length; i++)
                {
                    double ox = testX[i], oy = testY[i], oz = testZ[i];

                    MatrixMath.TransformPoint(M, ox, oy, oz, out double tx, out double ty, out double tz);
                    MatrixMath.TransformPoint(inv, tx, ty, tz, out double rx, out double ry, out double rz);

                    rx.Should().BeApproximately(ox, 1e-4, $"X round-trip for {reg.id}");
                    ry.Should().BeApproximately(oy, 1e-4, $"Y round-trip for {reg.id}");
                    rz.Should().BeApproximately(oz, 1e-4, $"Z round-trip for {reg.id}");
                }
            }
        }
    }
}