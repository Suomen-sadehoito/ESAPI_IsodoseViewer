using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using EQD2Viewer.Fixtures.Models;

namespace EQD2Viewer.Fixtures
{
    /// <summary>
    /// Universal fixture loader for both test fixtures and development runtime.
    /// Discovers fixture directories and deserializes them into strongly-typed models.
    /// 
    /// Fixture directory layout:
    ///   TestFixtures/
    ///     sample_standard_2gy/     ← synthetic fixture (always present)
    ///     PHANTOM_001_C1_Plan1/    ← Eclipse-generated (after running FixtureGenerator)
    ///     PHANTOM_002_C1_SBRT/     ← Eclipse-generated
    /// </summary>
    public static class FixtureLoader
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Returns the base TestFixtures directory path.
        /// Searches upward from the current execution directory to find the project root.
        /// </summary>
        public static string GetFixturesBasePath()
        {
            // Start from test assembly location and walk up to find TestFixtures
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "TestFixtures");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
                if (dir == null) break;
            }

            // Fallback: try relative to working directory
            string fallback = Path.Combine(Directory.GetCurrentDirectory(), "TestFixtures");
            if (Directory.Exists(fallback)) return fallback;

            throw new DirectoryNotFoundException(
                "TestFixtures directory not found. Ensure test fixtures are copied to output directory.");
        }

        /// <summary>
        /// Lists all available fixture directories (each containing a metadata.json).
        /// </summary>
        public static IEnumerable<string> DiscoverFixtures()
        {
            string basePath = GetFixturesBasePath();
            return Directory.GetDirectories(basePath)
                .Where(d => File.Exists(Path.Combine(d, "metadata.json")))
                .OrderBy(d => d);
        }

        /// <summary>
        /// Returns fixture directory names for xUnit [MemberData] usage.
        /// </summary>
        public static IEnumerable<object[]> AllFixtureDirectories()
        {
            foreach (string dir in DiscoverFixtures())
                yield return new object[] { Path.GetFileName(dir) };
        }

        // ════════════════════════════════════════════════════════
        // INDIVIDUAL LOADERS
        // ════════════════════════════════════════════════════════

        public static string FixturePath(string fixtureName) =>
            Path.Combine(GetFixturesBasePath(), fixtureName);

        public static PlanMetadata LoadMetadata(string fixtureName) =>
            Load<PlanMetadata>(fixtureName, "metadata.json");

        public static DoseScaling LoadDoseScaling(string fixtureName) =>
            Load<DoseScaling>(fixtureName, "dose_scaling.json");

        public static GridGeometry LoadImageGeometry(string fixtureName) =>
            Load<GridGeometry>(fixtureName, "image_geometry.json");

        public static GridGeometry LoadDoseGeometry(string fixtureName) =>
            Load<GridGeometry>(fixtureName, "dose_geometry.json");

        public static CtSubsample LoadCtSubsample(string fixtureName) =>
            Load<CtSubsample>(fixtureName, "ct_subsample.json");

        public static ReferenceDosePoints LoadReferenceDosePoints(string fixtureName) =>
            Load<ReferenceDosePoints>(fixtureName, "reference_dose_points.json");

        public static RegistrationsFile LoadRegistrations(string fixtureName) =>
            LoadOptional<RegistrationsFile>(fixtureName, "registrations.json");

        /// <summary>
        /// Loads all dose slices found in the fixture directory.
        /// Files match pattern dose_slice_NNN.json.
        /// </summary>
        public static DoseSlice[] LoadDoseSlices(string fixtureName)
        {
            string dir = FixturePath(fixtureName);
            return Directory.GetFiles(dir, "dose_slice_*.json")
                .OrderBy(f => f)
                .Select(f => JsonSerializer.Deserialize<DoseSlice>(
                    ReadJsonFile(f), JsonOpts))
                .ToArray();
        }

        /// <summary>
        /// Loads all structure fixtures found in the fixture directory.
        /// </summary>
        public static StructureFixture[] LoadStructures(string fixtureName)
        {
            string dir = FixturePath(fixtureName);
            return Directory.GetFiles(dir, "structure_*.json")
                .OrderBy(f => f)
                .Select(f => JsonSerializer.Deserialize<StructureFixture>(
                    ReadJsonFile(f), JsonOpts))
                .ToArray();
        }

        /// <summary>
        /// Loads all DVH fixtures found in the fixture directory.
        /// </summary>
        public static DvhFixture[] LoadDvhCurves(string fixtureName)
        {
            string dir = FixturePath(fixtureName);
            return Directory.GetFiles(dir, "dvh_*.json")
                .OrderBy(f => f)
                .Select(f => JsonSerializer.Deserialize<DvhFixture>(
                    ReadJsonFile(f), JsonOpts))
                .ToArray();
        }

        /// <summary>
        /// Converts a flat valuesGy array into a double[,] grid (x, y indexing).
        /// Matches ESAPI convention: grid[x, y] where data is stored row-major (y*width+x).
        /// </summary>
        public static double[,] ToDoseGrid(DoseSlice slice)
        {
            var grid = new double[slice.width, slice.height];
            for (int y = 0; y < slice.height; y++)
                for (int x = 0; x < slice.width; x++)
                    grid[x, y] = slice.valuesGy[y * slice.width + x];
            return grid;
        }

        /// <summary>
        /// Converts a flat rawValues array into an int[,] grid (x, y indexing).
        /// </summary>
        public static int[,] ToRawGrid(DoseSlice slice)
        {
            var grid = new int[slice.width, slice.height];
            for (int y = 0; y < slice.height; y++)
                for (int x = 0; x < slice.width; x++)
                    grid[x, y] = slice.rawValues[y * slice.width + x];
            return grid;
        }

        /// <summary>
        /// Converts a flat 16-element array to a 4x4 matrix.
        /// </summary>
        public static double[,] ToMatrix4x4(double[] flat)
        {
            if (flat == null || flat.Length != 16) return null;
            var m = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r, c] = flat[r * 4 + c];
            return m;
        }

        // ════════════════════════════════════════════════════════
        // PRIVATE
        // ════════════════════════════════════════════════════════

        /// <summary>
        /// Reads file text with BOM-safe encoding.
        /// C# Encoding.UTF8 writes BOM by default; System.Text.Json doesn't accept it.
        /// </summary>
        private static string ReadJsonFile(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            // Strip UTF-8 BOM if present (EF BB BF → char 0xFEFF)
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);
            return text;
        }

        private static T Load<T>(string fixtureName, string fileName)
        {
            string path = Path.Combine(FixturePath(fixtureName), fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Fixture file not found: {path}");
            return JsonSerializer.Deserialize<T>(ReadJsonFile(path), JsonOpts);
        }

        private static T LoadOptional<T>(string fixtureName, string fileName) where T : class
        {
            string path = Path.Combine(FixturePath(fixtureName), fileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(ReadJsonFile(path), JsonOpts);
        }
    }
}
