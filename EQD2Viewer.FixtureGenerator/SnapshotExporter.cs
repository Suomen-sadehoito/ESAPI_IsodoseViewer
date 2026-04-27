using EQD2Viewer.Core.Serialization;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.IO;

namespace EQD2Viewer.FixtureGenerator
{
    /// <summary>
    /// Serializes a pre-loaded ClinicalSnapshot to a directory using SnapshotSerializer.
    ///
    /// The caller (Script.cs) is responsible for loading the ClinicalSnapshot via
    /// EsapiDataSource -- this class only handles the serialization step.
    /// This avoids a circular project reference (FixtureGenerator -> EQD2Viewer.Esapi).
    ///
    /// Compared to the existing FixtureExporter (targeted testing tool):
    ///   FixtureExporter   -> selective fixture files for unit/integration tests (~1 MB)
    ///   SnapshotExporter  -> full ClinicalSnapshot for end-to-end QA (~5-30 MB binary)
    ///
    /// Output: Desktop\EQD2_Snapshots\{PatientId}_{CourseId}_{PlanId}_snapshot\
    /// </summary>
    public class SnapshotExporter
    {
        /// <summary>
        /// Serializes the given snapshot to <paramref name="outputDir"/> using binary format.
        /// Automatically generates reference dose points for end-to-end verification.
        /// Returns a human-readable export summary.
        /// </summary>
        public string ExportSnapshot(ClinicalSnapshot snapshot, string outputDir)
        {
            // Generate reference dose points for end-to-end verification
            if (snapshot.Dose != null && snapshot.CtImage != null)
            {
                snapshot.RenderSettings = BuildRenderSettings(snapshot);
            }

            return SnapshotSerializer.WriteBinary(snapshot, outputDir);
        }

        /// <summary>
        /// Builds RenderSettings with reference dose points sampled at a 5x5 grid of CT pixels.
        /// These are Eclipse-computed dose values that the app should reproduce exactly.
        /// </summary>
        private static RenderSettings BuildRenderSettings(ClinicalSnapshot snapshot)
        {
            var settings = new RenderSettings
            {
                WindowLevel = 40,
                WindowWidth = 400,
                ReferenceDosePoints = new List<ReferenceDosePoint>()
            };

            var ct = snapshot.CtImage;
            var dose = snapshot.Dose;
            if (ct == null || dose == null) return settings;

            int midSlice = ct.ZSize / 2;

            // Sample a 5x5 grid of test points across the CT image
            int[] testX = { ct.XSize / 8, ct.XSize / 4, ct.XSize / 2, 3 * ct.XSize / 4, 7 * ct.XSize / 8 };
            int[] testY = { ct.YSize / 8, ct.YSize / 4, ct.YSize / 2, 3 * ct.YSize / 4, 7 * ct.YSize / 8 };

            foreach (int px in testX)
            {
                foreach (int py in testY)
                {
                    // Map CT pixel to dose voxel using the same math as ImageRenderingService
                    Vec3 worldPos = ct.Geometry.Origin
                         + ct.Geometry.XDirection * (px * ct.Geometry.XRes)
                        + ct.Geometry.YDirection * (py * ct.Geometry.YRes)
                     + ct.Geometry.ZDirection * (midSlice * ct.Geometry.ZRes);

                    Vec3 diff = worldPos - dose.Geometry.Origin;
                    int dx = (int)Math.Round(diff.Dot(dose.Geometry.XDirection) / dose.Geometry.XRes);
                    int dy = (int)Math.Round(diff.Dot(dose.Geometry.YDirection) / dose.Geometry.YRes);
                    int dz = (int)Math.Round(diff.Dot(dose.Geometry.ZDirection) / dose.Geometry.ZRes);

                    bool inside = dx >= 0 && dx < dose.Geometry.XSize
                     && dy >= 0 && dy < dose.Geometry.YSize
                    && dz >= 0 && dz < dose.Geometry.ZSize;

                    double doseGy = 0;
                    if (inside)
                    {
                        doseGy = ImageUtils.RawToGy(dose.Voxels[dz][dx, dy], dose.Scaling);
                    }

                    settings.ReferenceDosePoints.Add(new ReferenceDosePoint
                    {
                        CtPixelX = px,
                        CtPixelY = py,
                        CtSlice = midSlice,
                        ExpectedDoseGy = Math.Round(doseGy, 6),
                        IsInsideDoseGrid = inside
                    });
                }
            }

            return settings;
        }

        public static string BuildOutputDirName(string patientId, string courseId, string planId)
        {
            string label = SanitizePath($"{patientId}_{courseId}_{planId}_snapshot");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "EQD2_Snapshots",
                label);
        }

        private static string SanitizePath(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
