using FluentAssertions;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EQD2Viewer.Tests.Fixtures
{
    /// <summary>
    /// Regression guard for the fixture JSON format produced by FixtureExporter.
    /// These tests don't exercise clinical logic — they verify the on-disk fixtures
    /// under TestFixtures/ contain the keys and value-types that the ~30 integration
    /// tests consume. If FixtureExporter changes its output schema, integration tests
    /// would otherwise fail later with cryptic NullReferenceExceptions; this file makes
    /// the failure early and obvious.
    ///
    /// Fixture naming convention: camelCase JSON keys, as produced by FixtureExporter.
    /// </summary>
    public class FixtureFormatTests
    {
        private const string FixtureDir = "TestFixtures/octavius_50gy_25fx";

        private static JsonDocument ReadJson(string relPath)
        {
            string path = Path.Combine(AppContext.BaseDirectory, relPath);
            File.Exists(path).Should().BeTrue($"expected fixture '{relPath}' to ship with test output");
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static string FixturesAbs => Path.Combine(AppContext.BaseDirectory, FixtureDir);

        [Fact]
        public void FixtureDirectory_Exists_WithAllExpectedFiles()
        {
            Directory.Exists(FixturesAbs).Should().BeTrue();
            var files = Directory.GetFiles(FixturesAbs, "*.json").Select(Path.GetFileName).ToArray();
            files.Should().Contain("metadata.json");
            files.Should().Contain("dose_scaling.json");
            files.Should().Contain("image_geometry.json");
            files.Should().Contain("dose_geometry.json");
            files.Should().Contain("ct_subsample.json");
            files.Should().Contain("reference_dose_points.json");
            files.Where(f => f!.StartsWith("dose_slice_")).Should().NotBeEmpty();
            files.Where(f => f!.StartsWith("structure_")).Should().NotBeEmpty();
            files.Where(f => f!.StartsWith("dvh_")).Should().NotBeEmpty();
        }

        [Fact]
        public void Metadata_HasPatientAndPlanFields()
        {
            using var doc = ReadJson($"{FixtureDir}/metadata.json");
            var root = doc.RootElement;
            root.TryGetProperty("patientId", out _).Should().BeTrue();
            root.TryGetProperty("planId", out _).Should().BeTrue();
            root.TryGetProperty("numberOfFractions", out var fx).Should().BeTrue();
            fx.GetInt32().Should().BeGreaterThan(0);
            root.TryGetProperty("totalDoseGy", out var dose).Should().BeTrue();
            dose.GetDouble().Should().BeGreaterThan(0);
        }

        [Fact]
        public void DoseScaling_HasThreeFactorsAndDoseUnit()
        {
            using var doc = ReadJson($"{FixtureDir}/dose_scaling.json");
            var root = doc.RootElement;
            root.TryGetProperty("rawScale", out var rawScale).Should().BeTrue();
            root.TryGetProperty("rawOffset", out _).Should().BeTrue();
            root.TryGetProperty("unitToGy", out var toGy).Should().BeTrue();
            root.TryGetProperty("doseUnit", out _).Should().BeTrue();
            rawScale.GetDouble().Should().BeGreaterThan(0);
            toGy.GetDouble().Should().BeGreaterThan(0);
        }

        [Fact]
        public void ImageGeometry_HasSizeSpacingDirectionOrigin()
        {
            using var doc = ReadJson($"{FixtureDir}/image_geometry.json");
            var root = doc.RootElement;
            foreach (var key in new[] { "xSize", "ySize", "zSize", "xRes", "yRes", "zRes", "origin", "xDirection", "yDirection", "zDirection" })
                root.TryGetProperty(key, out _).Should().BeTrue($"'{key}' should be present");

            root.GetProperty("xSize").GetInt32().Should().BeGreaterThan(0);
            root.GetProperty("xRes").GetDouble().Should().BeGreaterThan(0);
            root.GetProperty("origin").GetArrayLength().Should().Be(3);
            root.GetProperty("xDirection").GetArrayLength().Should().Be(3);
            root.GetProperty("yDirection").GetArrayLength().Should().Be(3);
            root.GetProperty("zDirection").GetArrayLength().Should().Be(3);
        }

        [Fact]
        public void ImageGeometry_DirectionVectors_AreUnit()
        {
            using var doc = ReadJson($"{FixtureDir}/image_geometry.json");
            var root = doc.RootElement;
            foreach (var key in new[] { "xDirection", "yDirection", "zDirection" })
            {
                var arr = root.GetProperty(key);
                double x = arr[0].GetDouble(), y = arr[1].GetDouble(), z = arr[2].GetDouble();
                double mag = System.Math.Sqrt(x * x + y * y + z * z);
                mag.Should().BeApproximately(1.0, 1e-6, $"{key} should be unit length");
            }
        }

        [Fact]
        public void DoseGeometry_HasSameShapeAsImageGeometry()
        {
            using var doc = ReadJson($"{FixtureDir}/dose_geometry.json");
            var root = doc.RootElement;
            foreach (var key in new[] { "xSize", "ySize", "zSize", "xRes", "yRes", "zRes", "origin", "xDirection", "yDirection", "zDirection" })
                root.TryGetProperty(key, out _).Should().BeTrue($"'{key}' should be present");
        }

        [Fact]
        public void DoseSlice_ContainsIndexAndVoxelGrid()
        {
            string file = Directory.GetFiles(FixturesAbs, "dose_slice_*.json").First();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            root.TryGetProperty("sliceIndex", out var idx).Should().BeTrue();
            root.TryGetProperty("width", out var w).Should().BeTrue();
            root.TryGetProperty("height", out var h).Should().BeTrue();
            root.TryGetProperty("maxDoseGy", out var maxDose).Should().BeTrue();
            idx.GetInt32().Should().BeGreaterOrEqualTo(0);
            w.GetInt32().Should().BeGreaterThan(0);
            h.GetInt32().Should().BeGreaterThan(0);
            maxDose.GetDouble().Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public void Structure_HasIdAndContourSlices()
        {
            string file = Directory.GetFiles(FixturesAbs, "structure_*.json").First();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            root.TryGetProperty("id", out var id).Should().BeTrue();
            root.TryGetProperty("slices", out var slices).Should().BeTrue();
            id.GetString().Should().NotBeNullOrEmpty();
            slices.GetArrayLength().Should().BeGreaterThan(0);

            // Each slice must carry sliceIndex + contours array.
            var first = slices[0];
            first.TryGetProperty("sliceIndex", out _).Should().BeTrue();
            first.TryGetProperty("contours", out var contours).Should().BeTrue();
            contours.GetArrayLength().Should().BeGreaterThan(0);

            // Each contour has a points array of [x,y,z] triplets.
            var firstContour = contours[0];
            firstContour.TryGetProperty("points", out var points).Should().BeTrue();
            points.GetArrayLength().Should().BeGreaterThan(2, "contour must have at least 3 points to form a polygon");
            points[0].GetArrayLength().Should().Be(3, "each point is an [x,y,z] triplet");
        }

        [Fact]
        public void DvhFile_HasStructureIdAndKeyStatistics()
        {
            string file = Directory.GetFiles(FixturesAbs, "dvh_*.json").First();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            root.TryGetProperty("structureId", out var id).Should().BeTrue();
            root.TryGetProperty("planId", out _).Should().BeTrue();
            root.TryGetProperty("dmaxGy", out var dmax).Should().BeTrue();
            root.TryGetProperty("dmeanGy", out var dmean).Should().BeTrue();
            id.GetString().Should().NotBeNullOrEmpty();
            dmax.GetDouble().Should().BeGreaterOrEqualTo(0);
            dmean.GetDouble().Should().BeGreaterOrEqualTo(0);
            dmax.GetDouble().Should().BeGreaterOrEqualTo(dmean.GetDouble(),
                "D_max must be ≥ D_mean");
        }

        [Fact]
        public void ReferenceDosePoints_HasSliceIndexAndPointArray()
        {
            using var doc = ReadJson($"{FixtureDir}/reference_dose_points.json");
            var root = doc.RootElement;
            root.TryGetProperty("ctSliceIndex", out _).Should().BeTrue();
            root.TryGetProperty("points", out var points).Should().BeTrue();
            points.ValueKind.Should().Be(JsonValueKind.Array);
            points.GetArrayLength().Should().BeGreaterThan(0);

            var first = points[0];
            first.TryGetProperty("ctPixelX", out _).Should().BeTrue();
            first.TryGetProperty("ctPixelY", out _).Should().BeTrue();
            first.TryGetProperty("doseGy", out var dose).Should().BeTrue();
            first.TryGetProperty("isInsideDoseGrid", out _).Should().BeTrue();
            dose.GetDouble().Should().BeGreaterOrEqualTo(0);
        }

        [Fact]
        public void CtSubsample_HasSubsamplingParametersAndData()
        {
            using var doc = ReadJson($"{FixtureDir}/ct_subsample.json");
            var root = doc.RootElement;
            root.TryGetProperty("originalSliceIndex", out _).Should().BeTrue();
            root.TryGetProperty("originalWidth", out _).Should().BeTrue();
            root.TryGetProperty("originalHeight", out _).Should().BeTrue();
            root.TryGetProperty("subsampleStep", out var step).Should().BeTrue();
            step.GetInt32().Should().BeGreaterThan(0);
        }

        [Fact]
        public void AllFixtureFiles_AreValidJson()
        {
            // Parse every .json file in the fixture directory to catch malformed output.
            var files = Directory.GetFiles(FixturesAbs, "*.json");
            files.Should().NotBeEmpty();
            foreach (var path in files)
            {
                var act = () => JsonDocument.Parse(File.ReadAllText(path));
                act.Should().NotThrow($"fixture file '{Path.GetFileName(path)}' must be valid JSON");
            }
        }
    }
}
