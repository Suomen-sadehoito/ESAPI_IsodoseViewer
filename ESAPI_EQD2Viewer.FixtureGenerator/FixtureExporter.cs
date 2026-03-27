using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_EQD2Viewer.FixtureGenerator
{
    /// <summary>
    /// Extracts ESAPI data and writes JSON fixture files for integration testing.
    /// All data is read through the same ESAPI API that the production code uses,
    /// ensuring the fixtures represent exactly what the application would receive.
    /// 
    /// No external JSON library — uses manual formatting for .NET 4.8 compatibility
    /// and single-DLL deployment (no additional dependencies needed).
    /// </summary>
    public class FixtureExporter
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private const int DOSE_CAL_RAW = 10000;
        // UTF-8 without BOM — prevents System.Text.Json parse errors
        private static readonly Encoding UTF8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Exports all fixture data for the given plan context.
        /// Returns a summary string of what was exported.
        /// </summary>
        public string ExportAll(ScriptContext context, PlanSetup plan, string outputDir)
        {
            var sb = new StringBuilder();
            var image = context.Image;
            var dose = plan.Dose;

            // ── 1. Metadata ──
            ExportMetadata(context.Patient?.Id ?? "", plan, outputDir);
            sb.AppendLine("✓ metadata.json");

            // ── 2. Dose scaling calibration ──
            ExportDoseScaling(plan, outputDir);
            sb.AppendLine("✓ dose_scaling.json");

            // ── 3. Image geometry ──
            ExportImageGeometry(image, outputDir);
            sb.AppendLine("✓ image_geometry.json");

            // ── 4. Dose geometry ──
            ExportDoseGeometry(dose, outputDir);
            sb.AppendLine("✓ dose_geometry.json");

            // ── 5. Representative dose slices (25%, 50%, 75%) ──
            int[] doseSlices = {
                dose.ZSize / 4,
                dose.ZSize / 2,
                3 * dose.ZSize / 4
            };
            foreach (int z in doseSlices)
            {
                ExportDoseSlice(dose, plan, z, outputDir);
                sb.AppendLine($"✓ dose_slice_{z:D3}.json");
            }

            // ── 6. CT subsample for HU offset detection ──
            ExportCtSubsample(image, outputDir);
            sb.AppendLine("✓ ct_subsample.json");

            // ── 7. Structures and DVH ──
            if (plan.StructureSet != null)
            {
                var structures = plan.StructureSet.Structures
                    .Where(s => !s.IsEmpty && s.DicomType != "SUPPORT")
                    .OrderBy(s => s.Id)
                    .Take(8)  // Limit to keep fixture size reasonable
                    .ToList();

                // Map dose slices to CT slice indices for contour export
                int[] ctSlicesForContours = doseSlices
                    .Select(dz => MapDoseSliceToCt(image, dose, dz))
                    .Where(cz => cz >= 0 && cz < image.ZSize)
                    .Distinct()
                    .ToArray();

                foreach (var structure in structures)
                {
                    ExportStructure(structure, image, ctSlicesForContours, outputDir);
                    sb.AppendLine($"✓ structure_{SanitizeName(structure.Id)}.json");

                    ExportDVH(plan, structure, outputDir);
                    sb.AppendLine($"✓ dvh_{SanitizeName(structure.Id)}.json");
                }
            }

            // ── 8. Reference dose points ──
            ExportReferenceDosePoints(image, dose, plan, outputDir);
            sb.AppendLine("✓ reference_dose_points.json");

            // ── 9. Registrations ──
            int regCount = ExportRegistrations(context.Patient, image, outputDir);
            if (regCount > 0) sb.AppendLine($"✓ registrations.json ({regCount} kpl)");

            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════
        // INDIVIDUAL EXPORTERS
        // ════════════════════════════════════════════════════════

        private void ExportMetadata(string patientId, PlanSetup plan, string dir)
        {
            double totalGy = ToGy(plan.TotalDose);
            var w = new JsonBuilder();
            w.BeginObject();
            w.String("patientId", patientId);
            w.String("courseId", plan.Course?.Id ?? "");
            w.String("planId", plan.Id);
            w.Number("totalDoseGy", totalGy);
            w.Number("numberOfFractions", plan.NumberOfFractions ?? 0);
            w.Number("planNormalization", plan.PlanNormalizationValue);
            w.Number("dosePerFractionGy", (plan.NumberOfFractions ?? 1) > 0
                ? totalGy / (plan.NumberOfFractions ?? 1) : 0);
            w.String("generatedAt", DateTime.Now.ToString("o"));
            w.String("generatorVersion", "1.0.0");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "metadata.json"), w.ToString(), UTF8NoBom);
        }

        private void ExportDoseScaling(PlanSetup plan, string dir)
        {
            var dose = plan.Dose;
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);

            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double unitToGy;
            string unitName = dvRef.Unit.ToString();

            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = ToGy(plan.TotalDose) / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else
                unitToGy = 1.0;

            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("rawScale", rawScale);
            w.Number("rawOffset", rawOffset);
            w.String("doseUnit", unitName);
            w.Number("unitToGy", unitToGy);
            w.Number("calibrationRawValue", DOSE_CAL_RAW);
            w.Number("calibrationDoseValue", dvRef.Dose);
            w.String("calibrationDoseUnit", unitName);
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "dose_scaling.json"), w.ToString(), UTF8NoBom);
        }

        private void ExportImageGeometry(Image image, string dir)
        {
            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("xSize", image.XSize);
            w.Number("ySize", image.YSize);
            w.Number("zSize", image.ZSize);
            w.Number("xRes", image.XRes);
            w.Number("yRes", image.YRes);
            w.Number("zRes", image.ZRes);
            w.NumberArray("origin", V(image.Origin));
            w.NumberArray("xDirection", V(image.XDirection));
            w.NumberArray("yDirection", V(image.YDirection));
            w.NumberArray("zDirection", V(image.ZDirection));
            w.String("frameOfReference", image.FOR ?? "");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "image_geometry.json"), w.ToString(), UTF8NoBom);
        }

        private void ExportDoseGeometry(Dose dose, string dir)
        {
            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("xSize", dose.XSize);
            w.Number("ySize", dose.YSize);
            w.Number("zSize", dose.ZSize);
            w.Number("xRes", dose.XRes);
            w.Number("yRes", dose.YRes);
            w.Number("zRes", dose.ZRes);
            w.NumberArray("origin", V(dose.Origin));
            w.NumberArray("xDirection", V(dose.XDirection));
            w.NumberArray("yDirection", V(dose.YDirection));
            w.NumberArray("zDirection", V(dose.ZDirection));
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "dose_geometry.json"), w.ToString(), UTF8NoBom);
        }

        private void ExportDoseSlice(Dose dose, PlanSetup plan, int sliceZ, string dir)
        {
            int dx = dose.XSize, dy = dose.YSize;
            var rawBuffer = new int[dx, dy];
            dose.GetVoxels(sliceZ, rawBuffer);

            // Calibrate raw → Gy
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double unitToGy;
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = ToGy(plan.TotalDose) / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else unitToGy = 1.0;

            var gyValues = new double[dx * dy];
            var rawValues = new int[dx * dy];
            double maxGy = 0, sumGy = 0;

            for (int y = 0; y < dy; y++)
                for (int x = 0; x < dx; x++)
                {
                    int idx = y * dx + x;
                    int raw = rawBuffer[x, y];
                    rawValues[idx] = raw;
                    double gy = (raw * rawScale + rawOffset) * unitToGy;
                    gyValues[idx] = Math.Round(gy, 6);
                    if (gy > maxGy) maxGy = gy;
                    sumGy += gy;
                }

            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("sliceIndex", sliceZ);
            w.Number("width", dx);
            w.Number("height", dy);
            w.Number("maxDoseGy", Math.Round(maxGy, 4));
            w.Number("meanDoseGy", Math.Round(sumGy / (dx * dy), 4));
            w.NumberArray("valuesGy", gyValues);
            w.IntArray("rawValues", rawValues);
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, $"dose_slice_{sliceZ:D3}.json"),
                w.ToString(), UTF8NoBom);
        }

        private void ExportCtSubsample(Image image, string dir)
        {
            int midSlice = image.ZSize / 2;
            var fullSlice = new int[image.XSize, image.YSize];
            image.GetVoxels(midSlice, fullSlice);

            // Subsample every 4th pixel
            int step = 4;
            int sw = image.XSize / step;
            int sh = image.YSize / step;
            var subValues = new int[sw * sh];

            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    subValues[y * sw + x] = fullSlice[x * step, y * step];

            // Detect HU offset using full slice (same as production code)
            int detectedOffset = DetectHuOffset(fullSlice, image.XSize, image.YSize);

            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("originalSliceIndex", midSlice);
            w.Number("originalWidth", image.XSize);
            w.Number("originalHeight", image.YSize);
            w.Number("subsampleStep", step);
            w.Number("width", sw);
            w.Number("height", sh);
            w.Number("detectedHuOffset", detectedOffset);
            w.IntArray("values", subValues);
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "ct_subsample.json"), w.ToString(), UTF8NoBom);
        }

        private void ExportStructure(Structure structure, Image image,
            int[] sliceIndices, string dir)
        {
            var w = new JsonBuilder();
            w.BeginObject();
            w.String("id", structure.Id);
            w.String("dicomType", structure.DicomType ?? "");
            w.IntArray("color", new int[] { structure.Color.R, structure.Color.G, structure.Color.B });

            // Export contours on specified slices
            w.Raw("\"slices\": [");
            bool firstSlice = true;
            foreach (int z in sliceIndices)
            {
                VVector[][] contours = null;
                try { contours = structure.GetContoursOnImagePlane(z); }
                catch { continue; }
                if (contours == null || contours.Length == 0) continue;

                if (!firstSlice) w.Raw(",");
                firstSlice = false;

                w.Raw("{");
                w.Raw($"\"sliceIndex\": {z},");
                w.Raw("\"contours\": [");
                bool firstContour = true;
                foreach (var contour in contours)
                {
                    if (contour.Length < 3) continue;
                    if (!firstContour) w.Raw(",");
                    firstContour = false;

                    w.Raw("{\"points\": [");
                    for (int i = 0; i < contour.Length; i++)
                    {
                        if (i > 0) w.Raw(",");
                        w.Raw($"[{F(contour[i].x)},{F(contour[i].y)},{F(contour[i].z)}]");
                    }
                    w.Raw("]}");
                }
                w.Raw("]}");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, $"structure_{SanitizeName(structure.Id)}.json"),
                w.ToString(), UTF8NoBom);
        }

        private void ExportDVH(PlanSetup plan, Structure structure, string dir)
        {
            DVHData dvh = null;
            try
            {
                dvh = plan.GetDVHCumulativeData(structure,
                    DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.01);
            }
            catch { return; }
            if (dvh == null || dvh.CurveData == null) return;

            var w = new JsonBuilder();
            w.BeginObject();
            w.String("structureId", structure.Id);
            w.String("planId", plan.Id);
            w.Number("dmaxGy", ToGy(dvh.MaxDose));
            w.Number("dmeanGy", ToGy(dvh.MeanDose));
            w.Number("dminGy", ToGy(dvh.MinDose));
            w.Number("volumeCc", dvh.Volume);
            w.Number("curvePointCount", dvh.CurveData.Length);

            // Export DVH curve — subsample if very large
            int step = dvh.CurveData.Length > 2000 ? dvh.CurveData.Length / 1000 : 1;
            w.Raw("\"curve\": [");
            bool first = true;
            for (int i = 0; i < dvh.CurveData.Length; i += step)
            {
                if (!first) w.Raw(",");
                first = false;
                double dGy = ToGy(dvh.CurveData[i].DoseValue);
                double vol = dvh.CurveData[i].Volume;
                w.Raw($"[{F(dGy)},{F(vol)}]");
            }
            // Always include last point
            if (dvh.CurveData.Length > 1 && step > 1)
            {
                var last = dvh.CurveData[dvh.CurveData.Length - 1];
                w.Raw($",[{F(ToGy(last.DoseValue))},{F(last.Volume)}]");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, $"dvh_{SanitizeName(structure.Id)}.json"),
                w.ToString(), UTF8NoBom);
        }

        private void ExportReferenceDosePoints(Image image, Dose dose,
            PlanSetup plan, string dir)
        {
            // Sample dose at several known anatomical positions
            // These serve as ground-truth points for the rendering pipeline test
            var points = new List<DoseTestPoint>();

            int midSlice = image.ZSize / 2;
            int[] testPixelsX = { image.XSize / 4, image.XSize / 2, 3 * image.XSize / 4 };
            int[] testPixelsY = { image.YSize / 4, image.YSize / 2, 3 * image.YSize / 4 };

            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double unitToGy;
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = ToGy(plan.TotalDose) / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else unitToGy = 1.0;

            foreach (int px in testPixelsX)
                foreach (int py in testPixelsY)
                {
                    // Map CT pixel to world coordinate
                    VVector worldPos = image.Origin
                        + image.XDirection * (px * image.XRes)
                        + image.YDirection * (py * image.YRes)
                        + image.ZDirection * (midSlice * image.ZRes);

                    // Map world to dose voxel
                    VVector diff = worldPos - dose.Origin;
                    double fdx = Dot(diff, dose.XDirection) / dose.XRes;
                    double fdy = Dot(diff, dose.YDirection) / dose.YRes;
                    double fdz = Dot(diff, dose.ZDirection) / dose.ZRes;

                    int ix = (int)Math.Round(fdx);
                    int iy = (int)Math.Round(fdy);
                    int iz = (int)Math.Round(fdz);

                    double doseGy = double.NaN;
                    if (ix >= 0 && ix < dose.XSize && iy >= 0 && iy < dose.YSize
                        && iz >= 0 && iz < dose.ZSize)
                    {
                        var rawSlice = new int[dose.XSize, dose.YSize];
                        dose.GetVoxels(iz, rawSlice);
                        doseGy = (rawSlice[ix, iy] * rawScale + rawOffset) * unitToGy;
                    }

                    points.Add(new DoseTestPoint
                    {
                        CtPixelX = px,
                        CtPixelY = py,
                        CtSlice = midSlice,
                        DoseVoxelX = ix,
                        DoseVoxelY = iy,
                        DoseVoxelZ = iz,
                        DoseGy = double.IsNaN(doseGy) ? -1 : Math.Round(doseGy, 6),
                        IsInsideDoseGrid = !double.IsNaN(doseGy)
                    });
                }

            var w = new JsonBuilder();
            w.BeginObject();
            w.Number("ctSliceIndex", midSlice);
            w.Raw("\"points\": [");
            for (int i = 0; i < points.Count; i++)
            {
                if (i > 0) w.Raw(",");
                var p = points[i];
                w.Raw("{");
                w.Raw($"\"ctPixelX\":{p.CtPixelX},\"ctPixelY\":{p.CtPixelY},\"ctSlice\":{p.CtSlice},");
                w.Raw($"\"doseVoxelX\":{p.DoseVoxelX},\"doseVoxelY\":{p.DoseVoxelY},\"doseVoxelZ\":{p.DoseVoxelZ},");
                w.Raw($"\"doseGy\":{F(p.DoseGy)},\"isInsideDoseGrid\":{(p.IsInsideDoseGrid ? "true" : "false")}");
                w.Raw("}");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "reference_dose_points.json"),
                w.ToString(), UTF8NoBom);
        }

        private int ExportRegistrations(Patient patient, Image refImage, string dir)
        {
            if (patient.Registrations == null) return 0;

            var regs = new List<RegData>();
            string refFOR = refImage.FOR ?? "";

            foreach (var reg in patient.Registrations)
            {
                try
                {
                    // Extract 4x4 matrix by transforming basis vectors
                    VVector o = reg.TransformPoint(new VVector(0, 0, 0));
                    VVector x = reg.TransformPoint(new VVector(1, 0, 0));
                    VVector y = reg.TransformPoint(new VVector(0, 1, 0));
                    VVector z = reg.TransformPoint(new VVector(0, 0, 1));

                    regs.Add(new RegData
                    {
                        Id = reg.Id,
                        SourceFOR = reg.SourceFOR ?? "",
                        RegisteredFOR = reg.RegisteredFOR ?? "",
                        Date = reg.CreationDateTime?.ToString("o") ?? "",
                        Matrix = new double[] {
                            x.x - o.x, y.x - o.x, z.x - o.x, o.x,
                            x.y - o.y, y.y - o.y, z.y - o.y, o.y,
                            x.z - o.z, y.z - o.z, z.z - o.z, o.z,
                            0, 0, 0, 1
                        }
                    });
                }
                catch { }
            }

            if (regs.Count == 0) return 0;

            var w = new JsonBuilder();
            w.BeginObject();
            w.String("referenceImageFOR", refFOR);
            w.Raw("\"registrations\": [");
            for (int i = 0; i < regs.Count; i++)
            {
                if (i > 0) w.Raw(",");
                var r = regs[i];
                w.Raw("{");
                w.Raw($"\"id\":\"{Esc(r.Id)}\",");
                w.Raw($"\"sourceFOR\":\"{Esc(r.SourceFOR)}\",");
                w.Raw($"\"registeredFOR\":\"{Esc(r.RegisteredFOR)}\",");
                w.Raw($"\"date\":\"{r.Date}\",");
                w.Raw("\"matrix\":[");
                for (int j = 0; j < 16; j++)
                {
                    if (j > 0) w.Raw(",");
                    w.Raw(F(r.Matrix[j]));
                }
                w.Raw("]}");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "registrations.json"), w.ToString(), UTF8NoBom);
            return regs.Count;
        }

        // ════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════

        private static int MapDoseSliceToCt(Image image, Dose dose, int doseSliceZ)
        {
            VVector doseSliceWorld = dose.Origin + dose.ZDirection * (doseSliceZ * dose.ZRes);
            VVector diff = doseSliceWorld - image.Origin;
            double zCt = Dot(diff, image.ZDirection) / image.ZRes;
            return (int)Math.Round(zCt);
        }

        private static double Dot(VVector a, VVector b) =>
            a.x * b.x + a.y * b.y + a.z * b.z;

        private static double[] V(VVector v) => new double[] { v.x, v.y, v.z };
        private static string F(double v) => v.ToString("G10", INV);
        private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        private static string SanitizeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static double ToGy(DoseValue dv) =>
            dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;

        private static int DetectHuOffset(int[,] slice, int xSize, int ySize)
        {
            int step = 8, above = 0, total = 0;
            for (int y = 0; y < ySize; y += step)
                for (int x = 0; x < xSize; x += step)
                {
                    total++;
                    if (slice[x, y] > 30000) above++;
                }
            return (total > 0 && above > total / 2) ? 32768 : 0;
        }

        private struct DoseTestPoint
        {
            public int CtPixelX, CtPixelY, CtSlice;
            public int DoseVoxelX, DoseVoxelY, DoseVoxelZ;
            public double DoseGy;
            public bool IsInsideDoseGrid;
        }

        private struct RegData
        {
            public string Id, SourceFOR, RegisteredFOR, Date;
            public double[] Matrix;
        }
    }

    /// <summary>
    /// Minimal JSON writer for .NET 4.8 with no external dependencies.
    /// Produces readable, indented JSON for fixture files.
    /// </summary>
    internal class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _depth = 0;
        private bool _needComma = false;
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        public void BeginObject()
        {
            _sb.AppendLine("{"); _depth++; _needComma = false;
        }

        public void EndObject()
        {
            _sb.AppendLine(); _depth--;
            Indent(); _sb.Append("}"); _needComma = true;
        }

        public void String(string key, string value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": \"{Esc(value)}\"");
            _needComma = true;
        }

        public void Number(string key, double value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": {value.ToString("G15", INV)}");
            _needComma = true;
        }

        public void Number(string key, int value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": {value}");
            _needComma = true;
        }

        public void NumberArray(string key, double[] values)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": [");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) _sb.Append(", ");
                _sb.Append(values[i].ToString("G10", INV));
            }
            _sb.Append("]");
            _needComma = true;
        }

        public void IntArray(string key, int[] values)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": [");
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) _sb.Append(",");
                _sb.Append(values[i]);
            }
            _sb.Append("]");
            _needComma = true;
        }

        public void Raw(string json)
        {
            if (_needComma && json.StartsWith("\"")) { _sb.Append(",\n"); Indent(); }
            _sb.Append(json);
            _needComma = false;
        }

        private void Comma() { if (_needComma) _sb.AppendLine(","); }
        private void Indent() { _sb.Append(new string(' ', _depth * 2)); }
        private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";
        public override string ToString() => _sb.ToString();
    }
}