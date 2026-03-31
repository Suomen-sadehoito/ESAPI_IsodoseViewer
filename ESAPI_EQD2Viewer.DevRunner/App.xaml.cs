using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using ESAPI_EQD2Viewer.Services;
using ESAPI_EQD2Viewer.UI.ViewModels;
using ESAPI_EQD2Viewer.UI.Views;

namespace ESAPI_EQD2Viewer.DevRunner
{
    /// <summary>
    /// Standalone development launcher for EQD2 Viewer.
    /// Replaces Eclipse's Script.Execute() entry point.
    /// Loads clinical data from JSON fixtures instead of live ESAPI.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            try
            {
                SimpleLogger.EnableFileLogging("EQD2Viewer_Dev.log");
                SimpleLogger.Info("=== DevRunner starting ===");

                // ── 1. Find fixture directory ──
                string? fixturePath = ResolveFixturePath(e.Args);   // ← CS8603 korjattu
                if (fixturePath == null)
                {
                    MessageBox.Show(
                        "No fixture directory found.\n\n" +
                        "Usage:\n" +
                        " ESAPI_EQD2Viewer.DevRunner.exe <fixture_path>\n\n" +
                        "Or place fixtures in TestFixtures/ next to the exe.\n\n" +
                        "Generate fixtures by running FixtureGenerator in Eclipse.",
                        "EQD2 Viewer — DevRunner",
                        MessageBoxButton.OK, MessageBoxImage.Information);

                    Shutdown(1);
                    return;
                }

                SimpleLogger.Info($"Using fixtures: {fixturePath}");

                // ── 2. Load clinical data from JSON ──
                IClinicalDataSource dataSource = new JsonDataSource(fixturePath);
                ClinicalSnapshot snapshot = dataSource.LoadSnapshot();

                SimpleLogger.Info($"Snapshot: {snapshot.Patient.Id} | " +
                                  $"{snapshot.ActivePlan.CourseId}/{snapshot.ActivePlan.Id} | " +
                                  $"{snapshot.ActivePlan.TotalDoseGy:F1} Gy / {snapshot.ActivePlan.NumberOfFractions} fx");

                // ── 3. Create services (same as production) ──
                IImageRenderingService renderingService = new ImageRenderingService();
                IDebugExportService debugService = new DebugExportService();
                IDVHService dvhService = new DVHService();

                // ── 4. Initialize rendering from snapshot ──
                int width = snapshot.CtImage.XSize;
                int height = snapshot.CtImage.YSize;
                renderingService.Initialize(width, height);
                renderingService.PreloadData(snapshot.CtImage, snapshot.Dose);

                // ── 5. Create ViewModel with snapshot (no ScriptContext!) ──
                var viewModel = new MainViewModel(snapshot, renderingService, debugService, dvhService);

                // ── 6. Launch the UI ──
                var window = new ESAPI_EQD2Viewer.UI.Views.MainWindow(viewModel);
                window.Title += " [DEV MODE — Fixture Data]";
                window.Show();

                SimpleLogger.Info("DevRunner UI launched successfully");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("DevRunner startup failed", ex);
                MessageBox.Show(
                    $"Startup error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                    "EQD2 Viewer — DevRunner Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Resolves fixture directory from command line args or auto-discovery.
        /// </summary>
        private static string? ResolveFixturePath(string[]? args)   // ← muutettu string? + args?
        {
            // Command-line argument
            if (args != null && args.Length > 0 && Directory.Exists(args[0]))
                return args[0];

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Look for TestFixtures/ next to exe
            string localFixtures = Path.Combine(baseDir, "TestFixtures");
            if (Directory.Exists(localFixtures))
            {
                string first = Directory.GetDirectories(localFixtures)
                    .FirstOrDefault(d => File.Exists(Path.Combine(d, "metadata.json")));
                if (first != null) return first;
            }

            // Walk up the directory tree to find TestFixtures in the project
            string dir = baseDir;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "ESAPI_EQD2Viewer.Tests", "TestFixtures");
                if (Directory.Exists(candidate))
                {
                    string first = Directory.GetDirectories(candidate)
                        .FirstOrDefault(d => File.Exists(Path.Combine(d, "metadata.json")));
                    if (first != null) return first;
                }

                dir = Path.GetDirectoryName(dir);
                if (dir == null) break;
            }

            return null;   // ← sallittu koska palautustyyppi on nyt string?
        }
    }
}