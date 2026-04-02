// NOTE: This file is kept for reference only.
// The canonical Eclipse ESAPI entry point has been moved to EQD2Viewer.Esapi\Script.cs,
// which is the sole assembly carrying the [ESAPIScript] attribute.
// This file should NOT be deployed to Eclipse — deploy EQD2Viewer.Esapi.dll instead.

using System;
using System.Windows;
using VMS.TPS.Common.Model.API;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Esapi.Adapters;
using ESAPI_EQD2Viewer.Services;
using ESAPI_EQD2Viewer.UI.ViewModels;
using ESAPI_EQD2Viewer.UI.Views;

namespace VMS.TPS
{
 /// <summary>
    /// Legacy Eclipse entry point — superseded by EQD2Viewer.Esapi\Script.cs.
 /// Retained so the ESAPI_EQD2Viewer project continues to compile as a
/// standalone WPF library without breaking the DevRunner or tests.
    /// </summary>
    public class Script
    {
        [System.Runtime.CompilerServices.MethodImpl(
   System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context)
        {
            if (context.Patient == null || context.Image == null)
    {
        MessageBox.Show(
            "Please open a patient with an image before running the script.",
    "EQD2 Viewer",
              MessageBoxButton.OK,
            MessageBoxImage.Warning);
       return;
      }

            try
 {
      SimpleLogger.EnableFileLogging();

    // -- Load clinical snapshot via the canonical ESAPI adapter --
       var dataSource = new EsapiDataSource(context);
                var snapshot = dataSource.LoadSnapshot();

    // -- Create WPF-layer services --
          IImageRenderingService renderingService = new ImageRenderingService();
         IDebugExportService debugService = new DebugExportService();
          IDVHCalculation dvhService = new DVHService();

          // -- Summation data loader --
  ISummationDataLoader summationLoader =
        new EsapiSummationDataLoader(context.Patient);

       // -- Initialise rendering pipeline --
        int width = snapshot.CtImage.XSize;
           int height = snapshot.CtImage.YSize;
  renderingService.Initialize(width, height);
        renderingService.PreloadData(snapshot.CtImage, snapshot.Dose);

     // -- Build ViewModel and launch window --
       var viewModel = new MainViewModel(
      snapshot,
              renderingService,
    debugService,
      dvhService,
                    summationLoader);

              var window = new MainWindow(viewModel);
             window.ShowDialog();
            }
            catch (Exception ex)
         {
     SimpleLogger.Error("Fatal error in Script.Execute", ex);
                MessageBox.Show(
        $"Error:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
      "EQD2 Viewer Error",
       MessageBoxButton.OK,
      MessageBoxImage.Error);
            }
     }
    }
}