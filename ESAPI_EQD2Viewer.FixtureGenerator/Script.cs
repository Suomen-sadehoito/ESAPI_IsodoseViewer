using System;
using System.IO;
using System.Linq;
using System.Windows;
using VMS.TPS.Common.Model.API;

[assembly: ESAPIScript(IsWriteable = false)]

namespace VMS.TPS
{
    /// <summary>
    /// ESAPI script that exports plan data as JSON fixtures for integration testing.
    /// 
    /// Run in Eclipse:
    ///   1. Open patient → select plan with calculated dose
    ///   2. Run this script
    ///   3. Fixtures are saved to Desktop\EQD2_Fixtures\{PatientId}_{PlanId}\
    ///   4. Copy the folder to ESAPI_EQD2Viewer.Tests\TestFixtures\
    /// 
    /// Each export captures:
    ///   - Plan metadata (dose, fractions, normalization)
    ///   - Dose scaling calibration (raw↔Gy conversion factors)
    ///   - Image and dose grid geometry (origins, directions, spacing)
    ///   - 3 representative dose slices (25%, 50%, 75%) as Gy values
    ///   - CT subsample for HU offset validation
    ///   - Structure contour polygons (world coordinates)
    ///   - Eclipse DVH data (ground truth for comparison)
    ///   - Dose values at specific test points
    ///   - Registration matrices (if available)
    /// </summary>
    public class Script
    {
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context)
        {
            if (context.Patient == null || context.Image == null)
            {
                MessageBox.Show("Avaa potilas ja kuva ennen skriptin ajoa.",
                    "Fixture Generator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var plan = context.ExternalPlanSetup;
            if (plan == null || plan.Dose == null)
            {
                MessageBox.Show("Valitse suunnitelma jossa on laskettu annos.",
                    "Fixture Generator", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Create output directory
                string basePath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "EQD2_Fixtures");
                string planLabel = SanitizePath($"{context.Patient.Id}_{plan.Course?.Id}_{plan.Id}");
                string outputDir = Path.Combine(basePath, planLabel);
                Directory.CreateDirectory(outputDir);

                // Export all fixture data
                var exporter = new ESAPI_EQD2Viewer.FixtureGenerator.FixtureExporter();
                string report = exporter.ExportAll(context, plan, outputDir);

                MessageBox.Show(
                    $"Fixtures tallennettu:\n{outputDir}\n\n{report}\n\n" +
                    "Kopioi kansio projektiin:\n" +
                    "ESAPI_EQD2Viewer.Tests\\TestFixtures\\",
                    "Fixture Generator — Valmis",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Virhe:\n{ex.Message}\n\n{ex.StackTrace}",
                    "Fixture Generator — Virhe",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string SanitizePath(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
