using EQD2Viewer.App.UI.Rendering;
using EQD2Viewer.Services;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.App.UI.ViewModels;
using EQD2Viewer.App.UI.Views;
using EQD2Viewer.Registration.Services;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

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

            LogResearchDisclaimer();

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
        /// Writes a prominent disclaimer banner to the log on every startup, stating
        /// that this software is a research prototype and is not a medical device.
        /// The banner is here so that any log excerpt shared or inspected during QA
        /// carries the disclaimer — regulator, reviewer, or successor developer alike.
        /// </summary>
        private static void LogResearchDisclaimer()
        {
            const string bar = "==============================================================";
            SimpleLogger.Info(bar);
            SimpleLogger.Info("EQD2 Viewer — RESEARCH PROTOTYPE");
            SimpleLogger.Info("Not a medical device (not CE-marked, not FDA-cleared).");
            SimpleLogger.Info("Not validated for clinical use. Outputs must not drive");
            SimpleLogger.Info("clinical decisions without independent verification against");
            SimpleLogger.Info("a validated reference system.");
            SimpleLogger.Info(bar);
        }

        /// <summary>
        /// Resolves the directory containing the currently running plugin.
        ///
        /// In ESAPI: scans loaded assemblies for one ending in <c>.esapi.dll</c>; Eclipse loaded
        /// it from the scripts folder so its <c>Location</c> points at exactly where we want.
        /// <see cref="AppDomain.CurrentDomain.BaseDirectory"/> would instead return Eclipse's
        /// own <c>bin</c> directory — the classic ESAPI-gotcha this method exists to avoid.
        ///
        /// In DevRunner / tests: no <c>.esapi.dll</c> is loaded, so we fall through to
        /// <c>BaseDirectory</c> which correctly points at the exe's folder.
        /// </summary>
        private static string ResolvePluginDirectory()
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string loc;
                try { loc = asm.Location; } catch { continue; } // Dynamic / in-memory throws
                if (string.IsNullOrEmpty(loc)) continue;
                if (loc.EndsWith(".esapi.dll", StringComparison.OrdinalIgnoreCase))
                {
                    string? dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir)) return dir!;
                }
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        /// <summary>
        /// Probes the plugin directory for <c>EQD2Viewer.Registration.ITK.dll</c> and attempts
        /// to instantiate <c>ItkRegistrationService</c> via reflection. Logs every step so
        /// "Why is DIR disabled?" is answerable from the log file.
        /// </summary>
        private static IRegistrationService? TryLoadItkService()
        {
            string pluginDir = ResolvePluginDirectory();
            string path = Path.Combine(pluginDir, "EQD2Viewer.Registration.ITK.dll");
            SimpleLogger.Info($"[DIR-probe] Plugin directory: '{pluginDir}'");
            SimpleLogger.Info($"[DIR-probe] Looking for '{path}'");

            if (!File.Exists(path))
            {
                SimpleLogger.Info("[DIR-probe] Registration.ITK.dll not found next to the plugin. " +
                    "Copy all 5 DLLs from BuildOutput\\02_Eclipse_With_ITK\\ to the Eclipse scripts " +
                    "folder (the same folder that contains the .esapi.dll).");
                return null;
            }

            // Verify the SimpleITK native DLL is also adjacent. This is where the plugin-directory
            // fix matters most: SimpleITKCSharpManaged P/Invokes into the native, and without the
            // native sitting beside it the reflection load succeeds but the first DIR call throws.
            string nativeDll = Path.Combine(pluginDir, "SimpleITKCSharpNative.dll");
            string nativeDllLegacy = Path.Combine(pluginDir, "SimpleITKCSharp.dll");
            bool hasNative = File.Exists(nativeDll) || File.Exists(nativeDllLegacy);
            if (!hasNative)
            {
                SimpleLogger.Warning($"[DIR-probe] Registration.ITK.dll found in '{pluginDir}' but the " +
                    "SimpleITK native DLL is missing (expected 'SimpleITKCSharpNative.dll' or legacy " +
                    "'SimpleITKCSharp.dll'). Copy ALL SimpleITK DLLs into the same folder.");
                return null;
            }

            // Windows' native LoadLibrary (used by P/Invoke) does NOT search the managed caller's
            // folder by default — only the process exe's folder, System32, CWD, and PATH. In ESAPI
            // this means the native DLL next to our plugin is invisible to LoadLibrary. SetDllDirectory
            // adds pluginDir to the Windows DLL search path WITHOUT removing defaults, so ESAPI's own
            // dependencies (loaded from Eclipse's bin) continue to resolve normally.
            if (!SetDllDirectory(pluginDir))
            {
                int lastErr = Marshal.GetLastWin32Error();
                SimpleLogger.Warning($"[DIR-probe] SetDllDirectory('{pluginDir}') failed (Win32 error {lastErr}). " +
                    "Native DLL lookup may still find the DLL if CWD or PATH covers it, but the ESAPI path usually won't.");
            }
            else
            {
                SimpleLogger.Info($"[DIR-probe] SetDllDirectory('{pluginDir}') primed — SimpleITK native DLL is discoverable.");
            }

            try
            {
                var asm = Assembly.LoadFrom(path);
                var type = asm.GetType("EQD2Viewer.Registration.ITK.Services.ItkRegistrationService");
                if (type == null)
                {
                    SimpleLogger.Warning("[DIR-probe] Registration.ITK.dll loaded but ItkRegistrationService " +
                        "type not found — likely version mismatch. Rebuild Release-WithITK and re-deploy " +
                        "ALL 5 DLLs together from the same build.");
                    return null;
                }

                var instance = Activator.CreateInstance(type) as IRegistrationService;
                if (instance != null)
                    SimpleLogger.Info("[DIR-probe] ItkRegistrationService instantiated successfully.");
                else
                    SimpleLogger.Warning("[DIR-probe] Activator returned null for ItkRegistrationService.");
                return instance;
            }
            catch (FileLoadException ex)
            {
                // Network-share deployments can hit Windows mark-of-the-web blocking.
                SimpleLogger.Error($"[DIR-probe] FileLoadException — often caused by Windows mark-of-the-web " +
                    $"on a network share. Right-click the DLL → Properties → Unblock, or deploy locally. " +
                    $"Detail: {ex.Message}", ex);
                return null;
            }
            catch (BadImageFormatException ex)
            {
                // x86/x64 mismatch. SimpleITKCSharpNative is x64-only.
                SimpleLogger.Error($"[DIR-probe] BadImageFormatException — architecture mismatch. " +
                    $"Ensure the Eclipse process is running as x64 (SimpleITK native is x64-only). " +
                    $"Detail: {ex.Message}", ex);
                return null;
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"[DIR-probe] Failed to load Registration.ITK: {ex.GetType().Name}: {ex.Message}", ex);
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDllDirectory(string lpPathName);
    }
}
