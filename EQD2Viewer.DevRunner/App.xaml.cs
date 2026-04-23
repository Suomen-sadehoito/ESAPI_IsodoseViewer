using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace EQD2Viewer.DevRunner
{
    /// <summary>
    /// Standalone development launcher for EQD2 Viewer.
    /// Replaces Eclipse's Script.Execute() entry point.
    /// Loads clinical data from JSON fixtures instead of live ESAPI.
    ///
    /// Supports two fixture formats:
    ///   1. Snapshot format (snapshot_meta.json) -- full ClinicalSnapshot from SnapshotSerializer
    ///   2. Test fixture format (metadata.json) -- selective data from FixtureExporter
    ///
    /// Uses AppLauncher for all service/ViewModel/Window wiring --
    /// this class only handles fixture discovery and snapshot loading.
    ///
    /// CLI flags:
    ///   --validate  : load snapshot, assert basic invariants, exit without UI.
    ///                 Used by DevRunnerSmokeTests. Exit 0 = OK, 1 = load error, 2 = exception.
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string[] rawArgs = e.Args ?? Array.Empty<string>();
            bool validateOnly = rawArgs.Any(a => string.Equals(a, "--validate", StringComparison.OrdinalIgnoreCase));
            string[] positionalArgs = rawArgs.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();

            try
            {
                SimpleLogger.EnableFileLogging("EQD2Viewer_Dev.log");
                SimpleLogger.Info(validateOnly ? "=== DevRunner --validate ===" : "=== DevRunner starting ===");

                // -- 1. Find fixture directory --
                string? fixturePath = ResolveFixturePath(positionalArgs);
                if (fixturePath == null)
                {
                    if (validateOnly)
                    {
                        SimpleLogger.Error("--validate: no fixture directory found");
                        Shutdown(1);
                        return;
                    }
                    MessageBox.Show(
                          "No fixture directory found.\n\n" +
                           "Usage:\n" +
                         "  EQD2Viewer.DevRunner.exe <fixture_path>\n\n" +
                              "Or place fixtures in TestFixtures/ next to the exe.\n\n" +
                                 "Generate fixtures by running FixtureGenerator in Eclipse.",
                               "EQD2 Viewer -- DevRunner",
                          MessageBoxButton.OK, MessageBoxImage.Information);

                    Shutdown(1);
                    return;
                }

                SimpleLogger.Info($"Using fixtures: {fixturePath}");

                // -- 2. Load clinical data -- auto-detect format --
                IClinicalDataSource dataSource;
                if (EQD2Viewer.Fixtures.JsonDataSource.IsSnapshotDirectory(fixturePath))
                {
                    SimpleLogger.Info("Detected snapshot format");
                    dataSource = new EQD2Viewer.Fixtures.JsonDataSource(fixturePath);
                }
                else
                {
                    SimpleLogger.Info("Detected test fixture format");
                    dataSource = new FixtureFormatDataSource(fixturePath);
                }

                ClinicalSnapshot snapshot = dataSource.LoadSnapshot();

                SimpleLogger.Info($"Snapshot: {snapshot.Patient.Id} | " +
               $"{snapshot.ActivePlan.CourseId}/{snapshot.ActivePlan.Id} | " +
             $"{snapshot.ActivePlan.TotalDoseGy:F1} Gy / {snapshot.ActivePlan.NumberOfFractions} fx");

                if (validateOnly)
                {
                    // Validate invariants for the smoke test.
                    if (snapshot.Patient == null) throw new InvalidDataException("Patient missing");
                    if (snapshot.ActivePlan == null) throw new InvalidDataException("ActivePlan missing");
                    if (snapshot.CtImage == null) throw new InvalidDataException("CtImage missing");
                    if (snapshot.CtImage.XSize <= 0 || snapshot.CtImage.YSize <= 0 || snapshot.CtImage.ZSize <= 0)
                        throw new InvalidDataException("CT volume has non-positive dimensions");
                    if (snapshot.ActivePlan.NumberOfFractions <= 0)
                        throw new InvalidDataException("Plan has non-positive fraction count");

                    SimpleLogger.Info("--validate: OK");
                    Shutdown(0);
                    return;
                }

                // -- 3. Launch via the shared composition root --
                EQD2Viewer.App.AppLauncher.Launch(
                 snapshot,
                windowTitle: "[DEV MODE -- Fixture Data]",
               useShowDialog: false);

                SimpleLogger.Info("DevRunner UI launched successfully");
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("DevRunner startup failed", ex);
                if (validateOnly)
                {
                    Shutdown(2);
                    return;
                }
                MessageBox.Show(
             $"Startup error:\n\n{ex.Message}\n\n{ex.StackTrace}",
                 "EQD2 Viewer -- DevRunner Error",
                  MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }

        /// <summary>
        /// Resolves fixture directory from command line args or auto-discovery.
        /// Supports both snapshot format (snapshot_meta.json) and test fixture format (metadata.json).
        /// </summary>
        private static string? ResolveFixturePath(string[] args)
        {
            // Explicit path wins — but if it was provided and doesn't exist,
            // treat it as a user error (typo) and fail rather than silently auto-discover.
            if (args != null && args.Length > 0)
            {
                return Directory.Exists(args[0]) ? args[0] : null;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // Look for TestFixtures/ next to exe
            string localFixtures = Path.Combine(baseDir, "TestFixtures");
            if (Directory.Exists(localFixtures))
            {
                string? first = Directory.GetDirectories(localFixtures)
                     .FirstOrDefault(d => IsFixtureDirectory(d));
                if (first != null) return first;
            }

            // Walk up the directory tree to find TestFixtures in the project
            string? dir = baseDir;
            for (int i = 0; i < 8; i++)
            {
                if (dir == null) break;
                string candidate = Path.Combine(dir, "EQD2Viewer.Tests", "TestFixtures");
                if (Directory.Exists(candidate))
                {
                    string? first = Directory.GetDirectories(candidate)
                          .FirstOrDefault(d => IsFixtureDirectory(d));
                    if (first != null) return first;
                }

                dir = Path.GetDirectoryName(dir);
            }

            return null;
        }

        private static bool IsFixtureDirectory(string dir)
        {
            return File.Exists(Path.Combine(dir, "metadata.json"))
      || EQD2Viewer.Fixtures.JsonDataSource.IsSnapshotDirectory(dir);
        }
    }
}
