using VMS.TPS.Common.Model.Types;
using VMS.TPS.Common.Model.API;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EQD2Viewer.FixtureGenerator
{
    /// <summary>
    /// Extracts clinical data from the ESAPI environment and serializes it into JSON fixture files for integration testing.
    /// </summary>
    /// <remarks>
    /// This exporter supports both <see cref="PlanSetup"/> and <see cref="PlanSum"/> via the <see cref="PlanningItem"/> interface.
    /// To maintain strict .NET 4.8 compatibility and enable single-DLL deployment without external dependencies, 
    /// this class utilizes a custom, lightweight JSON formatter rather than third-party serialization libraries.
    /// </remarks>
    public class FixtureExporter
    {
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;
        private const int DOSE_CAL_RAW = 10000;
        private static readonly Encoding UTF8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Exports all necessary fixture data for the specified planning item into the target directory.
        /// </summary>
        /// <param name="context">The active ESAPI script context providing access to patient and image data.</param>
        /// <param name="plan">The planning item (either a PlanSetup or PlanSum) to export.</param>
        /// <param name="planType">A string identifier specifying the type of the plan.</param>
        /// <param name="outputDir">The directory path where the JSON fixture files will be saved.</param>
        /// <returns>A summary string detailing the files and data points that were successfully exported.</returns>
        public string ExportAll(ScriptContext context, PlanningItem plan, string planType, string outputDir)
        {
            var sb = new StringBuilder();
            var image = context.Image;
            var dose = plan.Dose;

            // 1. Export core metadata.
            ExportMetadata(context.Patient?.Id ?? "", plan, planType, outputDir);
            sb.AppendLine("✓ metadata.json");

            // 2. Export dose scaling calibration factors.
            ExportDoseScaling(plan, outputDir);
            sb.AppendLine("✓ dose_scaling.json");

            // 3. Export spatial geometry of the primary image.
            ExportImageGeometry(image, outputDir);
            sb.AppendLine("✓ image_geometry.json");

            // 4. Export spatial geometry of the dose grid.
            ExportDoseGeometry(dose, outputDir);
            sb.AppendLine("✓ dose_geometry.json");

            // 5. Export representative dose slices (25%, 50%, 75% depth) to limit file size.
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

            // 6. Export a subsampled CT slice for Hounsfield Unit offset detection.
            ExportCtSubsample(image, outputDir);
            sb.AppendLine("✓ ct_subsample.json");

            // 7. Export all structures and their cumulative DVH data without artificial limitations.
            StructureSet structureSet = GetStructureSet(plan);
            if (structureSet != null)
            {
                var structures = structureSet.Structures
                    .Where(s => !s.IsEmpty && s.DicomType != "SUPPORT")
                    .OrderBy(s => s.Id)
                    .ToList();

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
                sb.AppendLine($"  {structures.Count} structures total");
            }

            // 8. Export reference dose points for accurate verification testing.
            ExportReferenceDosePoints(image, dose, plan, outputDir);
            sb.AppendLine("✓ reference_dose_points.json");

            // 9. Export all available spatial registrations.
            int regCount = ExportRegistrations(context.Patient, image, outputDir);
            if (regCount > 0)
                sb.AppendLine($"✓ registrations.json ({regCount} registrations)");
            else
                sb.AppendLine("  (no registrations)");

            return sb.ToString();
        }

        /// <summary>
        /// Exports basic plan metadata including dose totals and fractionation.
        /// </summary>
        private void ExportMetadata(string patientId, PlanningItem plan, string planType, string dir)
        {
            double totalGy = ToGyFromPlanningItem(plan);
            int fractions = GetNumberOfFractions(plan);
            string courseId = GetCourseId(plan);

            var w = new JsonBuilder();
            w.BeginObject();
            w.String("patientId", patientId);
            w.String("courseId", courseId);
            w.String("planId", plan.Id);
            w.String("planType", planType);
            w.Number("totalDoseGy", totalGy);
            w.Number("numberOfFractions", fractions);
            w.Number("planNormalization", GetPlanNormalization(plan));
            w.Number("dosePerFractionGy", fractions > 0 ? totalGy / fractions : 0);
            w.String("generatedAt", DateTime.Now.ToString("o"));
            w.String("generatorVersion", "1.1.0");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "metadata.json"), w.ToString(), UTF8NoBom);
        }

        /// <summary>
        /// Computes and exports the calibration scaling factors required to convert raw ESAPI dose voxels into Gray.
        /// </summary>
        private void ExportDoseScaling(PlanningItem plan, string dir)
        {
            var dose = plan.Dose;
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);

            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double unitToGy;
            string unitName = dvRef.Unit.ToString();

            double totalGy = ToGyFromPlanningItem(plan);
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = totalGy / 100.0;
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

        /// <summary>
        /// Exports the 3D spatial properties and frame of reference for the image volume.
        /// </summary>
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

        /// <summary>
        /// Exports the 3D spatial properties for the dose grid volume.
        /// </summary>
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

        /// <summary>
        /// Exports a 2D slice of the dose matrix, converting raw values to absolute Gray measurements.
        /// </summary>
        private void ExportDoseSlice(Dose dose, PlanningItem plan, int sliceZ, string dir)
        {
            int dx = dose.XSize, dy = dose.YSize;
            var rawBuffer = new int[dx, dy];
            dose.GetVoxels(sliceZ, rawBuffer);

            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double unitToGy;
            double totalGy = ToGyFromPlanningItem(plan);

            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = totalGy / 100.0;
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

        /// <summary>
        /// Exports a down-sampled slice of the CT image used heuristically to detect baseline Hounsfield Unit offsets.
        /// </summary>
        private void ExportCtSubsample(Image image, string dir)
        {
            int midSlice = image.ZSize / 2;
            var fullSlice = new int[image.XSize, image.YSize];
            image.GetVoxels(midSlice, fullSlice);

            int step = 4;
            int sw = image.XSize / step;
            int sh = image.YSize / step;
            var subValues = new int[sw * sh];

            for (int y = 0; y < sh; y++)
                for (int x = 0; x < sw; x++)
                    subValues[y * sw + x] = fullSlice[x * step, y * step];

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

        /// <summary>
        /// Exports the metadata and 2D contour polygons for a single anatomical structure across specified slices.
        /// </summary>
        private void ExportStructure(Structure structure, Image image, int[] sliceIndices, string dir)
        {
            var w = new JsonBuilder();
            w.BeginObject();
            w.String("id", structure.Id);
            w.String("dicomType", structure.DicomType ?? "");
            w.IntArray("color", new int[] { structure.Color.R, structure.Color.G, structure.Color.B });

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
            File.WriteAllText(Path.Combine(dir, $"structure_{SanitizeName(structure.Id)}.json"), w.ToString(), UTF8NoBom);
        }

        /// <summary>
        /// Exports the calculated cumulative Dose-Volume Histogram (DVH) curve for a specified structure.
        /// </summary>
        private void ExportDVH(PlanningItem plan, Structure structure, string dir)
        {
            DVHData dvh = null;
            try
            {
                dvh = plan.GetDVHCumulativeData(structure, DoseValuePresentation.Absolute, VolumePresentation.Relative, 0.01);
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

            // Subsample curve points to reduce file bloat if the data array is excessively large.
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
            if (dvh.CurveData.Length > 1 && step > 1)
            {
                var last = dvh.CurveData[dvh.CurveData.Length - 1];
                w.Raw($",[{F(ToGy(last.DoseValue))},{F(last.Volume)}]");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, $"dvh_{SanitizeName(structure.Id)}.json"), w.ToString(), UTF8NoBom);
        }

        /// <summary>
        /// Extracts point-based reference dose data across spatial cross-sections, used for end-to-end testing validation.
        /// </summary>
        private void ExportReferenceDosePoints(Image image, Dose dose, PlanningItem plan, string dir)
        {
            var points = new List<DoseTestPoint>();

            int midSlice = image.ZSize / 2;
            int[] testPixelsX = { image.XSize / 4, image.XSize / 2, 3 * image.XSize / 4 };
            int[] testPixelsY = { image.YSize / 4, image.YSize / 2, 3 * image.YSize / 4 };

            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;
            double totalGy = ToGyFromPlanningItem(plan);
            double unitToGy;
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = totalGy / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else unitToGy = 1.0;

            foreach (int px in testPixelsX)
                foreach (int py in testPixelsY)
                {
                    VVector worldPos = image.Origin
                        + image.XDirection * (px * image.XRes)
                        + image.YDirection * (py * image.YRes)
                        + image.ZDirection * (midSlice * image.ZRes);

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
                string insideGrid = p.IsInsideDoseGrid ? "true" : "false";
                w.Raw($"\"doseGy\":{F(p.DoseGy)},\"isInsideDoseGrid\":{insideGrid}");
                w.Raw("}");
            }
            w.Raw("]");
            w.EndObject();
            File.WriteAllText(Path.Combine(dir, "reference_dose_points.json"),
             w.ToString(), UTF8NoBom);
        }

        /// <summary>
        /// Exports patient registration matrices required for cross-image mapping and summation operations.
        /// </summary>
        private int ExportRegistrations(Patient patient, Image refImage, string dir)
        {
            if (patient.Registrations == null) return 0;

            var regs = new List<RegData>();
            string refFOR = refImage.FOR ?? "";

            foreach (var reg in patient.Registrations)
            {
                try
                {
                    // Transform spatial basis points to extract the row-major 4x4 transformation matrix.
                    VVector o = reg.TransformPoint(new VVector(0, 0, 0));
                    VVector ex = reg.TransformPoint(new VVector(1, 0, 0));
                    VVector ey = reg.TransformPoint(new VVector(0, 1, 0));
                    VVector ez = reg.TransformPoint(new VVector(0, 0, 1));

                    regs.Add(new RegData
                    {
                        Id = reg.Id,
                        SourceFOR = reg.SourceFOR ?? "",
                        RegisteredFOR = reg.RegisteredFOR ?? "",
                        Date = reg.CreationDateTime?.ToString("o") ?? "",
                        Matrix = MatrixMath.BuildAffineFromBasisImages(
                            new Vec3(o.x, o.y, o.z),
                            new Vec3(ex.x, ex.y, ex.z),
                            new Vec3(ey.x, ey.y, ey.z),
                            new Vec3(ez.x, ez.y, ez.z))
                    });
                }
                catch
                {
                    // Record an identity matrix as fallback for non-rigid or unsupported registration entities.
                    regs.Add(new RegData
                    {
                        Id = reg.Id,
                        SourceFOR = reg.SourceFOR ?? "",
                        RegisteredFOR = reg.RegisteredFOR ?? "",
                        Date = reg.CreationDateTime?.ToString("o") ?? "",
                        Matrix = new double[] {
                            1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1
                        },
                        IsUnsupported = true
                    });
                }
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
                w.Raw($"\"isUnsupported\":{(r.IsUnsupported ? "true" : "false")},");
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

        /// <summary>
        /// Derives the aggregate total dose measured in Gray from the active planning item.
        /// </summary>
        private static double ToGyFromPlanningItem(PlanningItem item)
        {
            if (item is PlanSetup ps) return ToGy(ps.TotalDose);
            if (item is PlanSum sum)
            {
                double total = 0;
                foreach (var component in sum.PlanSetups)
                    total += ToGy(component.TotalDose);
                return total;
            }
            return 0;
        }

        /// <summary>
        /// Extracts the total number of delivered fractions associated with the planning item.
        /// </summary>
        private static int GetNumberOfFractions(PlanningItem item)
        {
            if (item is PlanSetup ps) return ps.NumberOfFractions ?? 0;
            if (item is PlanSum sum)
            {
                int total = 0;
                foreach (var component in sum.PlanSetups)
                    total += component.NumberOfFractions ?? 0;
                return total;
            }
            return 0;
        }

        /// <summary>
        /// Retrieves the plan normalization factor. Returns NaN for aggregated sums where a single factor is undefined.
        /// </summary>
        private static double GetPlanNormalization(PlanningItem item)
        {
            if (item is PlanSetup ps) return ps.PlanNormalizationValue;
            return double.NaN;
        }

        /// <summary>
        /// Resolves the parent course identifier corresponding to the given planning item.
        /// </summary>
        private static string GetCourseId(PlanningItem item)
        {
            if (item is PlanSetup ps) return ps.Course?.Id ?? "";
            if (item is PlanSum sum) return sum.Course?.Id ?? "";
            return "";
        }

        /// <summary>
        /// Extracts the primary <see cref="StructureSet"/> related to the planning item.
        /// </summary>
        private static StructureSet GetStructureSet(PlanningItem item)
        {
            if (item is PlanSetup ps) return ps.StructureSet;
            if (item is PlanSum sum)
            {
                return sum.PlanSetups.FirstOrDefault()?.StructureSet;
            }
            return null;
        }

        /// <summary>
        /// Computes the corresponding CT slice index for a specified spatial dose slice plane.
        /// </summary>
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

        /// <summary>
        /// Cleans identifiers, replacing invalid characters with underscores to ensure file system compatibility.
        /// </summary>
        private static string SanitizeName(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// Converts the given ESAPI dose presentation value into absolute Gray format.
        /// </summary>
        private static double ToGy(DoseValue dv) =>
            dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;

        /// <summary>
        /// Evaluates a representative CT data slice to determine the implicit DICOM Hounsfield Unit offset.
        /// </summary>
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

        /// <summary>
        /// Provides a DTO struct used internally to store validation points exported for unit testing.
        /// </summary>
        private struct DoseTestPoint
        {
            public int CtPixelX, CtPixelY, CtSlice;
            public int DoseVoxelX, DoseVoxelY, DoseVoxelZ;
            public double DoseGy;
            public bool IsInsideDoseGrid;
        }

        /// <summary>
        /// Provides a DTO struct mapping an extracted spatial registration matrix to its identifying properties.
        /// </summary>
        private struct RegData
        {
            public string Id, SourceFOR, RegisteredFOR, Date;
            public double[] Matrix;
            public bool IsUnsupported;
        }
    }

    /// <summary>
    /// A minimal JSON stream builder ensuring readable output and robust formatting for .NET 4.8 compatibility.
    /// Eliminates the need for external framework dependencies such as Newtonsoft.Json within the Eclipse script runtime.
    /// </summary>
    internal class JsonBuilder
    {
        private readonly StringBuilder _sb = new StringBuilder();
        private int _depth = 0;
        private bool _needComma = false;
        private static readonly CultureInfo INV = CultureInfo.InvariantCulture;

        /// <summary>
        /// Starts a new JSON object scope, appropriately incrementing the formatting indent level.
        /// </summary>
        public void BeginObject()
        {
            _sb.AppendLine("{"); _depth++; _needComma = false;
        }

        /// <summary>
        /// Terminates the current JSON object scope and finalizes indentation.
        /// </summary>
        public void EndObject()
        {
            _sb.AppendLine(); _depth--;
            Indent(); _sb.Append("}"); _needComma = true;
        }

        /// <summary>
        /// Emits a key-value pair where the value is serialized as a JSON string.
        /// </summary>
        public void String(string key, string value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": \"{Esc(value)}\"");
            _needComma = true;
        }

        /// <summary>
        /// Emits a key-value pair where the value is serialized as a double-precision JSON number.
        /// </summary>
        public void Number(string key, double value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": {value.ToString("G15", INV)}");
            _needComma = true;
        }

        /// <summary>
        /// Emits a key-value pair where the value is serialized as a discrete JSON integer.
        /// </summary>
        public void Number(string key, int value)
        {
            Comma(); Indent();
            _sb.Append($"\"{key}\": {value}");
            _needComma = true;
        }

        /// <summary>
        /// Appends a one-dimensional array of double values associated with the specified key.
        /// </summary>
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

        /// <summary>
        /// Appends a one-dimensional array of integer values associated with the specified key.
        /// </summary>
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

        /// <summary>
        /// Injects pre-formatted or complex JSON content directly into the builder's stream output.
        /// </summary>
        public void Raw(string json)
        {
            if (_needComma && json.StartsWith("\"")) { _sb.Append(",\n"); Indent(); }
            _sb.Append(json);
            _needComma = false;
        }

        private void Comma() { if (_needComma) _sb.AppendLine(","); }
        private void Indent() { _sb.Append(new string(' ', _depth * 2)); }
        private static string Esc(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

        /// <summary>
        /// Compiles and returns the finalized JSON document string.
        /// </summary>
        public override string ToString() => _sb.ToString();
    }
}