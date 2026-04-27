using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Fixtures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace EQD2Viewer.DevRunner
{
    /// <summary>
    /// Provides an <see cref="IClinicalDataSource"/> implementation that reads the test-fixture format (metadata.json and dose slices).
    /// </summary>
    /// <remarks>
    /// This format is produced by the FixtureExporter and contains selective, lightweight data specifically for testing purposes.
    /// For loading full snapshot data (produced by SnapshotSerializer), utilize EQD2Viewer.Fixtures.JsonDataSource.
    /// </remarks>
    public class FixtureFormatDataSource : IClinicalDataSource
    {
        private readonly string _fixtureDir;

        private static readonly JsonSerializerOptions JsonOpts = FixtureJsonOptions.Default;

        /// <summary>
        /// Initializes a new instance of the <see cref="FixtureFormatDataSource"/> class.
        /// </summary>
        /// <param name="fixtureDirectory">The directory path containing the JSON fixture files.</param>
        /// <exception cref="DirectoryNotFoundException">Thrown if the specified directory does not exist.</exception>
        public FixtureFormatDataSource(string fixtureDirectory)
        {
            if (!Directory.Exists(fixtureDirectory))
                throw new DirectoryNotFoundException($"Fixture directory not found: {fixtureDirectory}");

            _fixtureDir = fixtureDirectory;
        }

        /// <summary>
        /// Loads and constructs a complete clinical snapshot from the underlying JSON fixtures.
        /// </summary>
        /// <returns>A fully populated <see cref="ClinicalSnapshot"/> instance.</returns>
        public ClinicalSnapshot LoadSnapshot()
        {
            var snapshot = new ClinicalSnapshot();

            var meta = LoadJson<MetadataJson>("metadata.json");

            snapshot.Patient = new PatientData
            {
                Id = meta.patientId ?? "FIXTURE",
                LastName = "Fixture",
                FirstName = "Patient"
            };

            snapshot.ActivePlan = new PlanData
            {
                Id = meta.planId ?? "Plan1",
                CourseId = meta.courseId ?? "C1",
                TotalDoseGy = meta.totalDoseGy,
                NumberOfFractions = meta.numberOfFractions > 0 ? meta.numberOfFractions : 1,
                PlanNormalization = meta.planNormalization > 0 ? meta.planNormalization : 100.0
            };

            var scaling = LoadJson<DoseScalingJson>("dose_scaling.json");
            var imgGeo = LoadJson<GeometryJson>("image_geometry.json");
            var doseGeo = LoadJson<GeometryJson>("dose_geometry.json");

            snapshot.CtImage = BuildCtImage(imgGeo);
            snapshot.Dose = BuildDoseVolume(doseGeo, scaling);
            snapshot.Structures = LoadStructures(imgGeo.zSize);
            snapshot.DvhCurves = LoadDvhCurves();
            snapshot.Registrations = LoadRegistrations();

            snapshot.AllCourses = new List<CourseData>
            {
                new CourseData
                {
                    Id = meta.courseId ?? "C1",
                    Plans = new List<PlanSummaryData>
                    {
                        new PlanSummaryData
                        {
                            PlanId = meta.planId ?? "Plan1",
                            CourseId = meta.courseId ?? "C1",
                            TotalDoseGy = meta.totalDoseGy,
                            NumberOfFractions = meta.numberOfFractions,
                            PlanNormalization = meta.planNormalization,
                            HasDose = true,
                            ImageFOR = imgGeo.frameOfReference ?? ""
                        }
                    }
                }
            };

            return snapshot;
        }

        /// <summary>
        /// Builds the CT image volume representation from geometry and subsample data.
        /// </summary>
        private VolumeData BuildCtImage(GeometryJson geo)
        {
            int xSize = geo.xSize, ySize = geo.ySize, zSize = geo.zSize;

            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
                voxels[z] = new int[xSize, ySize];

            var ctSub = LoadJsonOptional<CtSubsampleJson>("ct_subsample.json");
            if (ctSub != null)
            {
                // Local non-nullable reference to prevent CS8602 warnings.
                CtSubsampleJson sub = ctSub;

                int huOffset = sub.detectedHuOffset;
                int midZ = zSize / 2;
                int step = sub.subsampleStep > 0 ? sub.subsampleStep : 4;

                if (sub.values != null)
                {
                    for (int sy = 0; sy < sub.height && sy * step < ySize; sy++)
                        for (int sx = 0; sx < sub.width && sx * step < xSize; sx++)
                        {
                            int val = sub.values[sy * sub.width + sx];

                            for (int dy = 0; dy < step && sy * step + dy < ySize; dy++)
                                for (int dx = 0; dx < step && sx * step + dx < xSize; dx++)
                                    voxels[midZ][sx * step + dx, sy * step + dy] = val;
                        }

                    int spread = Math.Min(5, zSize / 4);
                    for (int dz = 1; dz <= spread; dz++)
                    {
                        if (midZ + dz < zSize) Array.Copy(voxels[midZ], 0, voxels[midZ + dz], 0, xSize * ySize);
                        if (midZ - dz >= 0) Array.Copy(voxels[midZ], 0, voxels[midZ - dz], 0, xSize * ySize);
                    }
                }
            }

            return new VolumeData
            {
                Geometry = ToVolumeGeometry(geo),
                Voxels = voxels,
                HuOffset = ctSub?.detectedHuOffset ?? 0
            };
        }

        /// <summary>
        /// Reconstructs the 3D dose grid by loading individual dose slice JSON files and applying scaling.
        /// </summary>
        private DoseVolumeData BuildDoseVolume(GeometryJson doseGeo, DoseScalingJson scaling)
        {
            int xSize = doseGeo.xSize, ySize = doseGeo.ySize, zSize = doseGeo.zSize;
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
                voxels[z] = new int[xSize, ySize];

            var sliceFiles = Directory.GetFiles(_fixtureDir, "dose_slice_*.json").OrderBy(f => f).ToArray();

            foreach (var file in sliceFiles)
            {
                try
                {
                    var slice = JsonSerializer.Deserialize<DoseSliceJson>(ReadJson(file), JsonOpts);
                    if (slice?.sliceIndex >= 0 && slice.sliceIndex < zSize && slice.rawValues != null)
                    {
                        for (int y = 0; y < slice.height && y < ySize; y++)
                            for (int x = 0; x < slice.width && x < xSize; x++)
                                voxels[slice.sliceIndex][x, y] = slice.rawValues[y * slice.width + x];
                    }
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"JsonDataSource: failed to load dose slice '{file}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            InterpolateMissingDoseSlices(voxels, xSize, ySize, zSize, sliceFiles.Length);

            return new DoseVolumeData
            {
                Geometry = ToVolumeGeometry(doseGeo),
                Voxels = voxels,
                Scaling = new DoseScaling
                {
                    RawScale = scaling.rawScale,
                    RawOffset = scaling.rawOffset,
                    UnitToGy = scaling.unitToGy,
                    DoseUnit = scaling.doseUnit ?? "Gy"
                }
            };
        }

        /// <summary>
        /// Interpolates missing dose slices to ensure a continuous 3D volume across the Z-axis.
        /// </summary>
        private static void InterpolateMissingDoseSlices(int[][,] voxels, int xSize, int ySize, int zSize, int availableSliceCount)
        {
            if (availableSliceCount <= 1) return;

            var loadedSlices = new List<int>();
            for (int z = 0; z < zSize; z++)
            {
                bool hasData = false;
                for (int x = 0; x < Math.Min(xSize, 10) && !hasData; x++)
                    for (int y = 0; y < Math.Min(ySize, 10) && !hasData; y++)
                        if (voxels[z][x, y] != 0) hasData = true;

                if (hasData) loadedSlices.Add(z);
            }

            if (loadedSlices.Count < 2) return;

            for (int z = 0; z < zSize; z++)
            {
                if (loadedSlices.Contains(z)) continue;
                int nearest = loadedSlices.OrderBy(s => Math.Abs(s - z)).First();
                Buffer.BlockCopy(voxels[nearest], 0, voxels[z], 0, xSize * ySize * sizeof(int));
            }
        }

        /// <summary>
        /// Loads anatomical structures and their planar contours.
        /// </summary>
        private List<StructureData> LoadStructures(int imageZSize)
        {
            var result = new List<StructureData>();
            var files = Directory.GetFiles(_fixtureDir, "structure_*.json").OrderBy(f => f);

            foreach (var file in files)
            {
                try
                {
                    var sf = JsonSerializer.Deserialize<StructureFixtureJson>(ReadJson(file), JsonOpts);
                    if (sf == null) continue;

                    var sd = new StructureData
                    {
                        Id = sf.id ?? Path.GetFileNameWithoutExtension(file),
                        DicomType = sf.dicomType ?? "",
                        IsEmpty = false,
                        HasMesh = true,
                        ColorR = sf.color?.Length > 0 ? (byte)sf.color[0] : (byte)255,
                        ColorG = sf.color?.Length > 1 ? (byte)sf.color[1] : (byte)0,
                        ColorB = sf.color?.Length > 2 ? (byte)sf.color[2] : (byte)0,
                        ColorA = 255
                    };

                    if (sf.slices != null)
                    {
                        foreach (var slice in sf.slices)
                        {
                            if (slice.contours == null) continue;
                            var sliceContours = new List<double[][]>();
                            foreach (var contour in slice.contours)
                            {
                                if (contour.points != null && contour.points.Length >= 3)
                                    sliceContours.Add(contour.points);
                            }
                            if (sliceContours.Count > 0)
                                sd.ContoursBySlice[slice.sliceIndex] = sliceContours;
                        }
                    }
                    result.Add(sd);
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"JsonDataSource: failed to load structure '{file}': {ex.GetType().Name}: {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// Loads pre-calculated Dose-Volume Histogram (DVH) curve data.
        /// </summary>
        private List<DvhCurveData> LoadDvhCurves()
        {
            var result = new List<DvhCurveData>();
            var files = Directory.GetFiles(_fixtureDir, "dvh_*.json").OrderBy(f => f);

            foreach (var file in files)
            {
                try
                {
                    var df = JsonSerializer.Deserialize<DvhFixtureJson>(ReadJson(file), JsonOpts);
                    if (df == null) continue;

                    result.Add(new DvhCurveData
                    {
                        StructureId = df.structureId ?? "",
                        PlanId = df.planId ?? "",
                        DMaxGy = df.dmaxGy,
                        DMeanGy = df.dmeanGy,
                        DMinGy = df.dminGy,
                        VolumeCc = df.volumeCc,
                        Curve = df.curve ?? Array.Empty<double[]>()
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"JsonDataSource: failed to load DVH curve '{file}': {ex.GetType().Name}: {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// Loads spatial registration matrices for cross-frame-of-reference transformations.
        /// </summary>
        private List<RegistrationData> LoadRegistrations()
        {
            var result = new List<RegistrationData>();
            var regFile = LoadJsonOptional<RegistrationsFileJson>("registrations.json");
            if (regFile?.registrations == null) return result;

            foreach (var r in regFile.registrations)
            {
                result.Add(new RegistrationData
                {
                    Id = r.id ?? "",
                    SourceFOR = r.sourceFOR ?? "",
                    RegisteredFOR = r.registeredFOR ?? "",
                    CreationDateTime = DateTime.TryParse(r.date, out var dt) ? dt : null,
                    Matrix = r.matrix ?? Array.Empty<double>()
                });
            }
            return result;
        }

        /// <summary>
        /// Converts a lightweight geometry DTO into the domain-specific VolumeGeometry model.
        /// </summary>
        private static VolumeGeometry ToVolumeGeometry(GeometryJson g) => new()
        {
            XSize = g.xSize,
            YSize = g.ySize,
            ZSize = g.zSize,
            XRes = g.xRes,
            YRes = g.yRes,
            ZRes = g.zRes,
            Origin = Vec3.FromArray(g.origin ?? Array.Empty<double>()),
            XDirection = Vec3.FromArray(g.xDirection ?? Array.Empty<double>()),
            YDirection = Vec3.FromArray(g.yDirection ?? Array.Empty<double>()),
            ZDirection = Vec3.FromArray(g.zDirection ?? Array.Empty<double>()),
            FrameOfReference = g.frameOfReference ?? ""
        };

        /// <summary>
        /// Deserializes a required JSON file from the fixture directory.
        /// </summary>
        /// <exception cref="FileNotFoundException">Thrown when the required file is missing.</exception>
        private T LoadJson<T>(string fileName)
        {
            string path = Path.Combine(_fixtureDir, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required fixture file not found: {path}");

            return JsonSerializer.Deserialize<T>(ReadJson(path), JsonOpts)!;
        }

        /// <summary>
        /// Deserializes an optional JSON file from the fixture directory. Returns null if the file is missing or invalid.
        /// </summary>
        private T? LoadJsonOptional<T>(string fileName) where T : class
        {
            string path = Path.Combine(_fixtureDir, fileName);
            if (!File.Exists(path)) return null;

            try
            {
                return JsonSerializer.Deserialize<T>(ReadJson(path), JsonOpts);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads file text with UTF-8 encoding and strips the Byte Order Mark (BOM) if present.
        /// </summary>
        private static string ReadJson(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);
            return text;
        }

        // Internal Data Transfer Objects (DTOs) for JSON deserialization. Properties are nullable to handle incomplete fixture data.
        private class MetadataJson { public string? patientId { get; set; } public string? courseId { get; set; } public string? planId { get; set; } public double totalDoseGy { get; set; } public int numberOfFractions { get; set; } public double planNormalization { get; set; } }
        private class DoseScalingJson { public double rawScale { get; set; } public double rawOffset { get; set; } public string? doseUnit { get; set; } public double unitToGy { get; set; } }
        private class GeometryJson { public int xSize { get; set; } public int ySize { get; set; } public int zSize { get; set; } public double xRes { get; set; } public double yRes { get; set; } public double zRes { get; set; } public double[]? origin { get; set; } public double[]? xDirection { get; set; } public double[]? yDirection { get; set; } public double[]? zDirection { get; set; } public string? frameOfReference { get; set; } }
        private class DoseSliceJson { public int sliceIndex { get; set; } public int width { get; set; } public int height { get; set; } public double[]? valuesGy { get; set; } public int[]? rawValues { get; set; } }
        private class CtSubsampleJson { public int subsampleStep { get; set; } public int width { get; set; } public int height { get; set; } public int detectedHuOffset { get; set; } public int[]? values { get; set; } }
        private class StructureFixtureJson { public string? id { get; set; } public string? dicomType { get; set; } public int[]? color { get; set; } public StructureSliceJson[]? slices { get; set; } }
        private class StructureSliceJson { public int sliceIndex { get; set; } public ContourJson[]? contours { get; set; } }
        private class ContourJson { public double[][]? points { get; set; } }
        private class DvhFixtureJson { public string? structureId { get; set; } public string? planId { get; set; } public double dmaxGy { get; set; } public double dmeanGy { get; set; } public double dminGy { get; set; } public double volumeCc { get; set; } public double[][]? curve { get; set; } }
        private class RegistrationsFileJson { public RegistrationEntryJson[]? registrations { get; set; } }
        private class RegistrationEntryJson { public string? id { get; set; } public string? sourceFOR { get; set; } public string? registeredFOR { get; set; } public string? date { get; set; } public double[]? matrix { get; set; } }
    }
}