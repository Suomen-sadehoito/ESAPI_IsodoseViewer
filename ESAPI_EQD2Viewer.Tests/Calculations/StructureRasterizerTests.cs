using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using System.Collections.Generic;
using System.Linq;

namespace ESAPI_EQD2Viewer.Tests.Calculations
{
    /// <summary>
    /// Tests for structure contour rasterization used in DVH computation.
    /// Incorrect rasterization directly corrupts DVH — safety-critical.
    /// </summary>
    public class StructureRasterizerTests
    {
        // ════════════════════════════════════════════════════════
        // BASIC POLYGON RASTERIZATION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void RasterizePolygon_FullImageRectangle_ShouldFillEntireGrid()
        {
            int w = 10, h = 10;
            var polygon = new Point2D[]
            {
                new Point2D(0, 0), new Point2D(10, 0),
                new Point2D(10, 10), new Point2D(0, 10)
            };

            bool[] mask = StructureRasterizer.RasterizePolygon(polygon, w, h);

            mask.Should().NotBeNull();
            mask.Length.Should().Be(w * h);

            int filledCount = mask.Count(v => v);
            filledCount.Should().BeGreaterThan(0, "rectangle covering image should fill pixels");
        }

        [Fact]
        public void RasterizePolygon_SmallSquare_ShouldFillCorrectArea()
        {
            int w = 20, h = 20;
            // Square from (5,5) to (15,15)
            var polygon = new Point2D[]
            {
                new Point2D(5, 5), new Point2D(15, 5),
                new Point2D(15, 15), new Point2D(5, 15)
            };

            bool[] mask = StructureRasterizer.RasterizePolygon(polygon, w, h);

            // Check that center is inside
            mask[10 * w + 10].Should().BeTrue("center of square should be inside");

            // Check that corners are outside
            mask[0 * w + 0].Should().BeFalse("top-left corner should be outside");
            mask[19 * w + 19].Should().BeFalse("bottom-right corner should be outside");
        }

        [Fact]
        public void RasterizePolygon_Triangle_ShouldFillCorrectArea()
        {
            int w = 20, h = 20;
            var triangle = new Point2D[]
            {
                new Point2D(10, 2), new Point2D(18, 18), new Point2D(2, 18)
            };

            bool[] mask = StructureRasterizer.RasterizePolygon(triangle, w, h);

            // Centroid (~10, 12.7) should be inside
            mask[12 * w + 10].Should().BeTrue("centroid of triangle should be inside");
            // Top-left should be outside
            mask[0 * w + 0].Should().BeFalse();
        }

        // ════════════════════════════════════════════════════════
        // EDGE CASES
        // ════════════════════════════════════════════════════════

        [Fact]
        public void RasterizePolygon_NullInput_ShouldReturnEmptyMask()
        {
            bool[] mask = StructureRasterizer.RasterizePolygon(null, 10, 10);
            mask.Should().NotBeNull();
            mask.All(v => !v).Should().BeTrue("null polygon should produce empty mask");
        }

        [Fact]
        public void RasterizePolygon_TooFewPoints_ShouldReturnEmptyMask()
        {
            bool[] mask = StructureRasterizer.RasterizePolygon(
                new Point2D[] { new Point2D(0, 0), new Point2D(1, 1) }, 10, 10);
            mask.All(v => !v).Should().BeTrue("degenerate polygon should produce empty mask");
        }

        [Fact]
        public void RasterizePolygon_PolygonOutsideGrid_ShouldReturnEmptyMask()
        {
            int w = 10, h = 10;
            var polygon = new Point2D[]
            {
                new Point2D(20, 20), new Point2D(30, 20),
                new Point2D(30, 30), new Point2D(20, 30)
            };

            bool[] mask = StructureRasterizer.RasterizePolygon(polygon, w, h);
            mask.All(v => !v).Should().BeTrue("polygon outside grid has no interior pixels");
        }

        [Fact]
        public void RasterizePolygon_VerySmallPolygon_ShouldNotCrash()
        {
            int w = 100, h = 100;
            var polygon = new Point2D[]
            {
                new Point2D(50, 50), new Point2D(50.1, 50),
                new Point2D(50.1, 50.1), new Point2D(50, 50.1)
            };

            var action = () => StructureRasterizer.RasterizePolygon(polygon, w, h);
            action.Should().NotThrow();
        }

        // ════════════════════════════════════════════════════════
        // VOLUME CONSERVATION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void RasterizePolygon_LargerPolygon_ShouldHaveMorePixels()
        {
            int w = 50, h = 50;
            var small = new Point2D[] {
                new Point2D(20, 20), new Point2D(30, 20),
                new Point2D(30, 30), new Point2D(20, 30)
            };
            var large = new Point2D[] {
                new Point2D(10, 10), new Point2D(40, 10),
                new Point2D(40, 40), new Point2D(10, 40)
            };

            bool[] maskSmall = StructureRasterizer.RasterizePolygon(small, w, h);
            bool[] maskLarge = StructureRasterizer.RasterizePolygon(large, w, h);

            int countSmall = maskSmall.Count(v => v);
            int countLarge = maskLarge.Count(v => v);

            countLarge.Should().BeGreaterThan(countSmall,
                "larger polygon should contain more pixels");
        }

        // ════════════════════════════════════════════════════════
        // COMBINING MASKS — XOR for hole handling
        // ════════════════════════════════════════════════════════

        [Fact]
        public void CombineContourMasks_SingleMask_ShouldReturnSame()
        {
            int w = 10, h = 10;
            bool[] mask = new bool[w * h];
            mask[55] = true; mask[44] = true;

            var result = StructureRasterizer.CombineContourMasks(
                new List<bool[]> { mask }, w, h);

            result[55].Should().BeTrue();
            result[44].Should().BeTrue();
            result[0].Should().BeFalse();
        }

        [Fact]
        public void CombineContourMasks_TwoOverlapping_ShouldXOR()
        {
            int w = 5, h = 5;
            int size = w * h;
            bool[] outer = new bool[size];
            bool[] inner = new bool[size];

            // Outer: all true
            for (int i = 0; i < size; i++) outer[i] = true;
            // Inner: center pixel true (simulating a hole)
            inner[12] = true;

            var result = StructureRasterizer.CombineContourMasks(
                new List<bool[]> { outer, inner }, w, h);

            // XOR: center pixel should be false (hole)
            result[12].Should().BeFalse("XOR of outer + inner creates a hole");
            result[0].Should().BeTrue("corners should remain filled");
        }

        [Fact]
        public void CombineContourMasks_NullOrEmpty_ShouldReturnEmptyMask()
        {
            int w = 10, h = 10;
            var result1 = StructureRasterizer.CombineContourMasks(null, w, h);
            result1.All(v => !v).Should().BeTrue();

            var result2 = StructureRasterizer.CombineContourMasks(
                new List<bool[]>(), w, h);
            result2.All(v => !v).Should().BeTrue();
        }

        // ════════════════════════════════════════════════════════
        // WORLD TO PIXEL COORDINATE CONVERSION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void WorldToPixel_AxisAligned_ShouldConvertCorrectly()
        {
            // Standard axial CT: X→right, Y→down, spacing 1mm
            double originX = 0, originY = 0, originZ = 0;
            double spacingX = 1.0, spacingY = 1.0;
            double xDirX = 1, xDirY = 0, xDirZ = 0;
            double yDirX = 0, yDirY = 1, yDirZ = 0;

            var worldPoints = new double[][] {
                new double[] { 10, 20, 0 },
                new double[] { 0, 0, 0 },
                new double[] { 5.5, 3.5, 0 }
            };

            var pixels = StructureRasterizer.WorldToPixel(worldPoints,
                originX, originY, originZ, spacingX, spacingY,
                xDirX, xDirY, xDirZ, yDirX, yDirY, yDirZ);

            pixels.Should().NotBeNull();
            pixels.Length.Should().Be(3);
            pixels[0].X.Should().BeApproximately(10, 1e-10);
            pixels[0].Y.Should().BeApproximately(20, 1e-10);
            pixels[1].X.Should().BeApproximately(0, 1e-10);
            pixels[1].Y.Should().BeApproximately(0, 1e-10);
            pixels[2].X.Should().BeApproximately(5.5, 1e-10);
            pixels[2].Y.Should().BeApproximately(3.5, 1e-10);
        }

        [Fact]
        public void WorldToPixel_WithSpacing_ShouldScaleCorrectly()
        {
            // 2mm pixel spacing: world 10mm → pixel 5
            double spacingX = 2.0, spacingY = 2.0;
            var worldPoints = new double[][] { new double[] { 10, 20, 0 } };

            var pixels = StructureRasterizer.WorldToPixel(worldPoints,
                0, 0, 0, spacingX, spacingY,
                1, 0, 0, 0, 1, 0);

            pixels[0].X.Should().BeApproximately(5.0, 1e-10);
            pixels[0].Y.Should().BeApproximately(10.0, 1e-10);
        }

        [Fact]
        public void WorldToPixel_WithOriginOffset_ShouldSubtractOrigin()
        {
            var worldPoints = new double[][] { new double[] { 110, 220, 0 } };

            var pixels = StructureRasterizer.WorldToPixel(worldPoints,
                100, 200, 0, 1, 1,
                1, 0, 0, 0, 1, 0);

            pixels[0].X.Should().BeApproximately(10, 1e-10);
            pixels[0].Y.Should().BeApproximately(20, 1e-10);
        }

        [Fact]
        public void WorldToPixel_NullInput_ShouldReturnNull()
        {
            var pixels = StructureRasterizer.WorldToPixel(null, 0, 0, 0, 1, 1, 1, 0, 0, 0, 1, 0);
            pixels.Should().BeNull();
        }
    }
}