using EQD2Viewer.Services.Rendering;
using EQD2Viewer.Services;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.App.UI.ViewModels;
using EQD2Viewer.App.UI.Views;
using EQD2Viewer.Registration.Services;
using System;
using System.IO;
using System.Reflection;

namespace EQD2Viewer.App
{
    /// <summary>
    /// Composition root for launching the EQD2 Viewer UI.
    /// Called from both the ESAPI Script.cs and the DevRunner.
    ///
    /// Tries to load EQD2Viewer.Registration.ITK.dll via reflection at startup.
    /// If found, an IRegistrationService is available for on-the-fly DIR computation.
    /// If not found, only pre-computed MHA deformation fields are supported.
    /// </summary>
    public static class AppLauncher
    {
        public static void Launch(
            ClinicalSnapshot snapshot,
            ISummationDataLoader? summationLoader = null,
            string? windowTitle = null,
            bool useShowDialog = true)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            // MhaReader is always available (EQD2Viewer.Registration is always built).
            var dfLoader = new MhaReader();

            // Try to load ITK registration service via reflection (Release-WithITK only).
            var itkService = TryLoadItkService();

            IImageRenderingService renderingService = new ImageRenderingService();
            IDebugExportService debugService = new DebugExportService();
            IDVHCalculation dvhService = new DVHService();

            int width  = snapshot.CtImage.XSize;
            int height = snapshot.CtImage.YSize;
            renderingService.Initialize(width, height);
            renderingService.PreloadData(snapshot.CtImage, snapshot.Dose);

            if (itkService != null)
                SimpleLogger.Info("ITK registration service loaded — on-the-fly DIR available.");
            else
                SimpleLogger.Info("ITK registration service not loaded — MHA-only DIR mode.");

            var factory = summationLoader != null
                ? new SummationServiceFactory(dfLoader)
                : null;

            var viewModel = new MainViewModel(
                snapshot,
                renderingService,
                debugService,
                dvhService,
                summationLoader,
                factory,
                itkService);

            var window = new MainWindow(viewModel);
            if (!string.IsNullOrEmpty(windowTitle))
                window.Title += $" {windowTitle}";

            if (useShowDialog)
                window.ShowDialog();
            else
                window.Show();
        }

        /// <summary>
        /// Probes the application directory for EQD2Viewer.Registration.ITK.dll and
        /// attempts to instantiate ItkRegistrationService via reflection.
        /// Returns null silently if the assembly is absent (standard Release build).
        /// </summary>
        private static IRegistrationService? TryLoadItkService()
        {
            try
            {
                string dir  = AppDomain.CurrentDomain.BaseDirectory;
                string path = Path.Combine(dir, "EQD2Viewer.Registration.ITK.dll");
                if (!File.Exists(path)) return null;

                var asm  = Assembly.LoadFrom(path);
                var type = asm.GetType("EQD2Viewer.Registration.ITK.Services.ItkRegistrationService");
                if (type == null) return null;

                var instance = Activator.CreateInstance(type);
                return instance as IRegistrationService;
            }
            catch (Exception ex)
            {
                SimpleLogger.Warning($"ITK registration service not loaded: {ex.Message}");
                return null;
            }
        }
    }
}
