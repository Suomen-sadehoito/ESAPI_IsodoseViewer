using System;
using System.Windows;
using VMS.TPS.Common.Model.API;
using ESAPI_EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using ESAPI_EQD2Viewer.Services;
using ESAPI_EQD2Viewer.UI.ViewModels;
using ESAPI_EQD2Viewer.UI.Views;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Logging;

[assembly: ESAPIScript(IsWriteable = false)]
namespace VMS.TPS
{
    public class Script
    {
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context)
        {
            if (context.Patient == null || context.Image == null)
            {
                MessageBox.Show("Please open a patient with an image before running the script.",
                    "EQD2 Viewer", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                SimpleLogger.EnableFileLogging();

                // ── Load data through Clean Architecture adapter ──
                var dataSource = new ESAPI_EQD2Viewer.Adapters.EsapiDataSource(context);
                var snapshot = dataSource.LoadSnapshot();

                IImageRenderingService renderingService = new ImageRenderingService();
                IDebugExportService debugService = new DebugExportService();
                IDVHService dvhService = new DVHService();

                int width = snapshot.CtImage.XSize;
                int height = snapshot.CtImage.YSize;

                renderingService.Initialize(width, height);
                renderingService.PreloadData(snapshot.CtImage, snapshot.Dose);

                // Use the new snapshot-based constructor
                var viewModel = new MainViewModel(snapshot, renderingService, debugService, dvhService);

                // Use the snapshot-based constructor for MainWindow
                var window = new MainWindow(viewModel);

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("Fatal error in Script.Execute", ex);
                MessageBox.Show($"Error:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "EQD2 Viewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}