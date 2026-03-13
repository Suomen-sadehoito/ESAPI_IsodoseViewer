using System.Windows.Media;
using System.Windows.Media.Imaging;
using ESAPI_IsodoseViewer.Mvvm;
using ESAPI_IsodoseViewer.Services;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_IsodoseViewer.ViewModels
{
    public class MainViewModel : ObservableObject
    {
        private readonly ScriptContext _context;
        private readonly PlanSetup _plan;
        private readonly IImageRenderingService _renderingService;
        private readonly IDebugExportService _debugExportService;

        private WriteableBitmap _ctImageSource;
        public WriteableBitmap CtImageSource
        {
            get => _ctImageSource;
            set => SetProperty(ref _ctImageSource, value);
        }

        private WriteableBitmap _doseImageSource;
        public WriteableBitmap DoseImageSource
        {
            get => _doseImageSource;
            set => SetProperty(ref _doseImageSource, value);
        }

        private int _currentSlice;
        public int CurrentSlice
        {
            get => _currentSlice;
            set
            {
                if (SetProperty(ref _currentSlice, value)) RenderScene();
            }
        }

        private int _maxSlice;
        public int MaxSlice
        {
            get => _maxSlice;
            set => SetProperty(ref _maxSlice, value);
        }

        private double _windowLevel;
        public double WindowLevel
        {
            get => _windowLevel;
            set
            {
                if (SetProperty(ref _windowLevel, value)) RenderScene();
            }
        }

        private double _windowWidth;
        public double WindowWidth
        {
            get => _windowWidth;
            set
            {
                if (SetProperty(ref _windowWidth, value)) RenderScene();
            }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public RelayCommand AutoPresetCommand { get; }
        public RelayCommand PresetCommand { get; }
        public RelayCommand DebugCommand { get; }

        public MainViewModel(ScriptContext context)
        {
            _context = context;
            _plan = context.ExternalPlanSetup;

            // In enterprise apps, these are injected via Dependency Injection (IoC container)
            _renderingService = new ImageRenderingService();
            _debugExportService = new DebugExportService();

            int width = _context.Image.XSize;
            int height = _context.Image.YSize;

            _maxSlice = _context.Image.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            _renderingService.Initialize(width, height);

            CtImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            AutoPresetCommand = new RelayCommand(o => ExecuteAutoPreset());
            PresetCommand = new RelayCommand(param => ExecutePreset((string)param));
            DebugCommand = new RelayCommand(o => ExecuteDebug());

            ExecuteAutoPreset();
        }

        private void RenderScene()
        {
            if (_context.Image == null) return;

            _renderingService.RenderCtImage(_context.Image, CtImageSource, _currentSlice, _windowLevel, _windowWidth);

            double planTotalDose = _plan?.TotalDose.Unit == DoseValue.DoseUnit.cGy
                ? _plan.TotalDose.Dose / 100.0
                : _plan?.TotalDose.Dose ?? 0;

            double planNormalization = _plan?.PlanNormalizationValue ?? 100.0;

            StatusText = _renderingService.RenderDoseImage(
                _context.Image,
                _plan?.Dose,
                DoseImageSource,
                _currentSlice,
                planTotalDose,
                planNormalization);
        }

        private void ExecuteAutoPreset()
        {
            // Set base values
            _windowLevel = 40;
            _windowWidth = 400;

            OnPropertyChanged(nameof(WindowLevel));
            OnPropertyChanged(nameof(WindowWidth));
            RenderScene();
        }

        private void ExecutePreset(string type)
        {
            switch (type)
            {
                case "Soft": WindowLevel = 40; WindowWidth = 400; break;
                case "Lung": WindowLevel = -600; WindowWidth = 1600; break;
                case "Bone": WindowLevel = 300; WindowWidth = 1500; break;
            }
        }

        private void ExecuteDebug()
        {
            _debugExportService.ExportDebugLog(_context, _plan, _currentSlice);
        }
    }
}