using System;
using System.Windows;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Services;
using ESAPI_EQD2Viewer.UI.ViewModels;

namespace ESAPI_EQD2Viewer.DevRunner
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// This acts purely as a launcher for the real application during development.
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            LaunchRealApplication();
        }

        private void LaunchRealApplication()
        {
            try
            {
                // Initialize CT Voxel Array (Jagged Array: int[][,])
                int ctSizeX = 512;
                int ctSizeY = 512;
                int ctSizeZ = 100;
                int[][,] ctVoxels = new int[ctSizeZ][,];
                for (int z = 0; z < ctSizeZ; z++)
                {
                    ctVoxels[z] = new int[ctSizeX, ctSizeY];
                }

                // Initialize Dose Voxel Array (Jagged Array: int[][,])
                int doseSizeX = 128;
                int doseSizeY = 128;
                int doseSizeZ = 100;
                int[][,] doseVoxels = new int[doseSizeZ][,];
                for (int z = 0; z < doseSizeZ; z++)
                {
                    doseVoxels[z] = new int[doseSizeX, doseSizeY];
                }

                // 1. Create a mock clinical snapshot with dummy data.
                var snapshot = new ClinicalSnapshot
                {
                    Patient = new PatientData
                    {
                        Id = "DEV-001",
                        FirstName = "Test",
                        LastName = "Patient"
                    },
                    ActivePlan = new PlanData
                    {
                        Id = "DevPlan",
                        TotalDoseGy = 60.0,
                        PlanNormalization = 100.0,
                        NumberOfFractions = 30
                    },
                    CtImage = new VolumeData
                    {
                        Geometry = new VolumeGeometry
                        {
                            XSize = ctSizeX,
                            YSize = ctSizeY,
                            ZSize = ctSizeZ
                        },
                        Voxels = ctVoxels // Assign the initialized array
                    },
                    Dose = new DoseVolumeData
                    {
                        Geometry = new VolumeGeometry
                        {
                            XSize = doseSizeX,
                            YSize = doseSizeY,
                            ZSize = doseSizeZ
                        },
                        Voxels = doseVoxels // Assign the initialized array
                    }
                };

                // 2. Instantiate required services
                var renderingService = new ImageRenderingService();
                var debugService = new DebugExportService();
                var dvhService = new DVHService();

                // 3. Initialize the MainViewModel using the Clean Architecture constructor
                var viewModel = new MainViewModel(snapshot, renderingService, debugService, dvhService);

                // 4. Instantiate the REAL MainWindow from the main ESAPI_EQD2Viewer project
                var realAppWindow = new ESAPI_EQD2Viewer.UI.Views.MainWindow(viewModel);

                // 5. Show the real application window
                realAppWindow.Show();

                // 6. Close this temporary DevRunner launcher window
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to launch dev environment:\n\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}",
                    "DevRunner Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}