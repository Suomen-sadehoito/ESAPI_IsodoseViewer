using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ESAPI_EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Interfaces;

namespace ESAPI_EQD2Viewer.DevRunner
{
    /// <summary>
    /// Development IClinicalDataSource: loads clinical data from JSON fixture files.
    /// 
    /// Reads the same fixture format produced by FixtureGenerator.
    /// Enables running the full EQD2 Viewer UI on any developer machine
    /// without Eclipse, Varian licenses, or patient data access.
    /// 
    /// Required fixture files:
    ///   metadata.json          — plan/patient info
    ///   dose_scaling.json      — raw → Gy calibration
    ///   image_geometry.json    — CT geometry
    ///   dose_geometry.json     — dose grid geometry
    ///   dose_slice_NNN.json    — dose voxel data (at least one)
    ///   ct_subsample.json      — CT sample for HU detection
    ///   structure_*.json       — structure contours (optional)
    ///   dvh_*.json             — DVH curves (optional)
    ///   registrations.json     — registrations (optional)
    /// </summary>
    public class JsonDataSource : IClinicalDataSource
    {
        private readonly string _fixtureDir;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public JsonDataSource(string fixtureDirectory)
        {
            if (!Directory.Exists(fixtureDirectory))
                throw new DirectoryNotFoundException($"Fixture directory not found: {fixtureDirectory}");
            _fixtureDir = fixtureDirectory;
        }

        public ClinicalSnapshot LoadSnapshot()
        {
            var snapshot = new ClinicalSnapshot();

            // ── Metadata → Patient + Plan ──
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

            // ── Dose scaling ──
            var scaling = LoadJson<DoseScalingJson>("dose_scaling.json");

            // ── Image geometry ──
            var imgGeo = LoadJson<GeometryJson>("image_geometry.json");

            // ── Dose geometry ──
            var doseGeo = LoadJson<GeometryJson>("dose_geometry.json");

            // ── Build CT image (synthetic from geometry — real voxels from CT subsample) ──
            snapshot.CtImage = BuildCtImage(imgGeo);

            // ── Build dose volume ──
            snapshot.Dose = BuildDoseVolume(doseGeo, scaling);

            // ── Structures ──
            snapshot.Structures = LoadStructures(imgGeo.zSize);

            // ── DVH curves ──
            snapshot.DvhCurves = LoadDvhCurves();

            // ── Registrations ──
            snapshot.Registrations = LoadRegistrations();

            // ── Course data for summation dialog ──
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

        // ════════════════════════════════════════════════════════
        // CT IMAGE — synthetic full volume from geometry + subsample
        // ════════════════════════════════════════════════════════

        private VolumeData BuildCtImage(GeometryJson geo)
        {
            int xSize = geo.xSize, ySize = geo.ySize, zSize = geo.zSize;

            // Create empty CT volume (black background)
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
                voxels[z] = new int[xSize, ySize];

            // Load CT subsample if available
            int huOffset = 0;
            var ctSub = LoadJsonOptional<CtSubsampleJson>("ct_subsample.json");
            if (ctSub != null)
            {
                huOffset = ctSub.detectedHuOffset;

                // Paint subsample data onto the middle slice to have something visible
                int midZ = zSize / 2;
                int step = ctSub.subsampleStep > 0 ? ctSub.subsampleStep : 4;
                for (int sy = 0; sy < ctSub.height && sy * step < ySize; sy++)
                    for (int sx = 0; sx < ctSub.width && sx * step < xSize; sx++)
                    {
                        int val = ctSub.values[sy * ctSub.width + sx];
                        // Spread to full-res block
                        for (int dy = 0; dy < step && sy * step + dy < ySize; dy++)
                            for (int dx = 0; dx < step && sx * step + dx < xSize; dx++)
                                voxels[midZ][sx * step + dx, sy * step + dy] = val;
                    }

                // Copy mid-slice to nearby slices for basic 3D navigation
                int spread = Math.Min(5, zSize / 4);
                for (int dz = 1; dz <= spread; dz++)
                {
                    if (midZ + dz < zSize) Array.Copy(voxels[midZ], voxels[midZ + dz], 0);
                    if (midZ - dz >= 0) Array.Copy(voxels[midZ], voxels[midZ - dz], 0);
                }
            }

            return new VolumeData
            {
                Geometry = ToVolumeGeometry(geo),
                Voxels = voxels,
                HuOffset = huOffset
            };
        }

        // ════════════════════════════════════════════════════════
        // DOSE VOLUME — from fixture dose slices
        // ════════════════════════════════════════════════════════

        private DoseVolumeData BuildDoseVolume(GeometryJson doseGeo, DoseScalingJson scaling)
        {
            int xSize = doseGeo.xSize, ySize = doseGeo.ySize, zSize = doseGeo.zSize;

            // Create dose voxel array
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
                voxels[z] = new int[xSize, ySize];

            // Load available dose slices
            var sliceFiles = Directory.GetFiles(_fixtureDir, "dose_slice_*.json").OrderBy(f => f).ToArray();
            foreach (var file in sliceFiles)
            {
                try
                {
                    var slice = JsonSerializer.Deserialize<DoseSliceJson>(ReadJson(file), JsonOpts);
                    if (slice.sliceIndex >= 0 && slice.sliceIndex < zSize && slice.rawValues != null)
                    {
                        for (int y = 0; y < slice.height && y < ySize; y++)
                            for (int x = 0; x < slice.width && x < xSize; x++)
                                voxels[slice.sliceIndex][x, y] = slice.rawValues[y * slice.width + x];
                    }
                }
                catch { /* Skip unparseable slices */ }
            }

            // Interpolate missing slices between available ones
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

        private static void InterpolateMissingDoseSlices(int[][,] voxels, int xSize, int ySize,
            int zSize, int availableSliceCount)
        {
            if (availableSliceCount <= 1) return;

            // Find slices that have data (non-zero)
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

            // Fill gaps between loaded slices with nearest neighbor
            for (int z = 0; z < zSize; z++)
            {
                if (loadedSlices.Contains(z)) continue;
                int nearest = loadedSlices.OrderBy(s => Math.Abs(s - z)).First();
                Buffer.BlockCopy(voxels[nearest], 0, voxels[z], 0, xSize * ySize * sizeof(int));
            }
        }

        // ════════════════════════════════════════════════════════
        // STRUCTURES
        // ════════════════════════════════════════════════════════

        private List<StructureData> LoadStructures(int imageZSize)
        {
            var result = new List<StructureData>();
            var files = Directory.GetFiles(_fixtureDir, "structure_*.json").OrderBy(f => f);

            foreach (var file in files)
            {
                try
                {
                    var sf = JsonSerializer.Deserialize<StructureFixtureJson>(ReadJson(file), JsonOpts);
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
                catch { /* Skip unparseable structures */ }
            }

            return result;
        }

        // ════════════════════════════════════════════════════════
        // DVH CURVES
        // ════════════════════════════════════════════════════════

        private List<DvhCurveData> LoadDvhCurves()
        {
            var result = new List<DvhCurveData>();
            var files = Directory.GetFiles(_fixtureDir, "dvh_*.json").OrderBy(f => f);

            foreach (var file in files)
            {
                try
                {
                    var df = JsonSerializer.Deserialize<DvhFixtureJson>(ReadJson(file), JsonOpts);
                    result.Add(new DvhCurveData
                    {
                        StructureId = df.structureId ?? "",
                        PlanId = df.planId ?? "",
                        DMaxGy = df.dmaxGy,
                        DMeanGy = df.dmeanGy,
                        DMinGy = df.dminGy,
                        VolumeCc = df.volumeCc,
                        Curve = df.curve
                    });
                }
                catch { }
            }

            return result;
        }

        // ════════════════════════════════════════════════════════
        // REGISTRATIONS
        // ════════════════════════════════════════════════════════

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
                    CreationDateTime = DateTime.TryParse(r.date, out var dt) ? dt : (DateTime?)null,
                    Matrix = r.matrix
                });
            }

            return result;
        }

        // ════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════

        private static VolumeGeometry ToVolumeGeometry(GeometryJson g) => new VolumeGeometry
        {
            XSize = g.xSize, YSize = g.ySize, ZSize = g.zSize,
            XRes = g.xRes, YRes = g.yRes, ZRes = g.zRes,
            Origin = Vec3.FromArray(g.origin),
            XDirection = Vec3.FromArray(g.xDirection),
            YDirection = Vec3.FromArray(g.yDirection),
            ZDirection = Vec3.FromArray(g.zDirection),
            FrameOfReference = g.frameOfReference ?? ""
        };

        private T LoadJson<T>(string fileName)
        {
            string path = Path.Combine(_fixtureDir, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"Required fixture file not found: {path}");
            return JsonSerializer.Deserialize<T>(ReadJson(path), JsonOpts);
        }

        private T LoadJsonOptional<T>(string fileName) where T : class
        {
            string path = Path.Combine(_fixtureDir, fileName);
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<T>(ReadJson(path), JsonOpts); }
            catch { return null; }
        }

        private static string ReadJson(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);
            return text;
        }

        // ════════════════════════════════════════════════════════
        // JSON MODELS (matching FixtureGenerator output)
        // ════════════════════════════════════════════════════════

        private class MetadataJson
        {
            public string patientId { get; set; }
            public string courseId { get; set; }
            public string planId { get; set; }
            public double totalDoseGy { get; set; }
            public int numberOfFractions { get; set; }
            public double planNormalization { get; set; }
        }

        private class DoseScalingJson
        {
            public double rawScale { get; set; }
            public double rawOffset { get; set; }
            public string doseUnit { get; set; }
            public double unitToGy { get; set; }
        }

        private class GeometryJson
        {
            public int xSize { get; set; }
            public int ySize { get; set; }
            public int zSize { get; set; }
            public double xRes { get; set; }
            public double yRes { get; set; }
            public double zRes { get; set; }
            public double[] origin { get; set; }
            public double[] xDirection { get; set; }
            public double[] yDirection { get; set; }
            public double[] zDirection { get; set; }
            public string frameOfReference { get; set; }
        }

        private class DoseSliceJson
        {
            public int sliceIndex { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public double[] valuesGy { get; set; }
            public int[] rawValues { get; set; }
        }

        private class CtSubsampleJson
        {
            public int subsampleStep { get; set; }
            public int width { get; set; }
            public int height { get; set; }
            public int detectedHuOffset { get; set; }
            public int[] values { get; set; }
        }

        private class StructureFixtureJson
        {
            public string id { get; set; }
            public string dicomType { get; set; }
            public int[] color { get; set; }
            public StructureSliceJson[] slices { get; set; }
        }

        private class StructureSliceJson
        {
            public int sliceIndex { get; set; }
            public ContourJson[] contours { get; set; }
        }

        private class ContourJson
        {
            public double[][] points { get; set; }
        }

        private class DvhFixtureJson
        {
            public string structureId { get; set; }
            public string planId { get; set; }
            public double dmaxGy { get; set; }
            public double dmeanGy { get; set; }
            public double dminGy { get; set; }
            public double volumeCc { get; set; }
            public double[][] curve { get; set; }
        }

        private class RegistrationsFileJson
        {
            public RegistrationEntryJson[] registrations { get; set; }
        }

        private class RegistrationEntryJson
        {
            public string id { get; set; }
            public string sourceFOR { get; set; }
            public string registeredFOR { get; set; }
            public string date { get; set; }
            public double[] matrix { get; set; }
        }
    }
}
