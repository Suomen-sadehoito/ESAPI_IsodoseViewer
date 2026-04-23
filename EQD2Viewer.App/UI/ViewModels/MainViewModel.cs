using EQD2Viewer.Services.Rendering;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using OxyPlot;
using OxyPlot.Axes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EQD2Viewer.App.UI.ViewModels
{
    /// <summary>
    /// Main view model for the EQD2 Viewer re-irradiation assessment tool.
    /// 
    /// Split into partial classes by responsibility:
    ///   .cs              â€” Core lifecycle: constructor, dispose, shared state, isodose management
    ///   .Properties.cs   â€” All bindable properties (WPF data binding targets)
    ///   .Rendering.cs    â€” CT/dose/structure rendering pipeline (UI thread only)
    ///   .DVH.cs          â€” DVH calculation, structure settings, OxyPlot series management
    ///   .Summation.cs    â€” Multi-plan dose summation + per-structure EQD2 DVH
    ///   .Commands.cs     â€” All RelayCommand handlers (user actions from UI)
    /// 
    /// Î±/Î² architecture:
    ///   DisplayAlphaBeta  â€” controls isodose visualization only (slider in sidebar)
    ///   Per-structure Î±/Î² â€” controls DVH calculations (editable in structure settings grid)
    ///   Summation Î±/Î²     â€” set at summation dialog; display Î±/Î² syncs initially
    /// 
    /// All data comes from ClinicalSnapshot â€” zero ESAPI dependencies at runtime.
    /// </summary>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        internal readonly IImageRenderingService _renderingService;
        internal readonly IDebugExportService _debugExportService;
        internal readonly IDVHCalculation _dvhService;

        // == Clean Architecture data source ==
        internal readonly ClinicalSnapshot _snapshot;

        /// <summary>
        /// Optional summation data loader â€” null when summation is not available
        /// (e.g. in DevRunner without full ESAPI data access).
        /// </summary>
        internal readonly ISummationDataLoader? _summationDataLoader;

        /// <summary>
        /// Factory for creating per-session summation service instances.
        /// Null when summation is not available.
        /// </summary>
        internal readonly ISummationServiceFactory? _summationServiceFactory;

        // == Child ViewModels (decomposed responsibilities) ==
        internal readonly ViewModelEventBus _eventBus = new ViewModelEventBus();
        internal readonly DoseOverlayViewModel _doseOverlay;

        private int _renderPendingFlag = 0;
        private bool _disposed;
        private volatile bool _isRendering;

        internal readonly List<DVHCacheEntry> _dvhCache = new List<DVHCacheEntry>();
        internal readonly List<string> _visibleStructureIds = new List<string>();

        public MainViewModel(ClinicalSnapshot snapshot,
            IImageRenderingService renderingService,
            IDebugExportService debugExportService,
          IDVHCalculation dvhService,
    ISummationDataLoader? summationDataLoader = null,
   ISummationServiceFactory? summationServiceFactory = null)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _renderingService = renderingService ?? throw new ArgumentNullException(nameof(renderingService));
            _debugExportService = debugExportService ?? throw new ArgumentNullException(nameof(debugExportService));
            _dvhService = dvhService ?? throw new ArgumentNullException(nameof(dvhService));
            _summationDataLoader = summationDataLoader;
            _summationServiceFactory = summationServiceFactory;

            // == Initialize child ViewModels ==
            double prescGy = snapshot.ActivePlan?.TotalDoseGy ?? 0;
            double norm = snapshot.ActivePlan?.PlanNormalization ?? 100.0;
            int fx = snapshot.ActivePlan?.NumberOfFractions ?? 1;

            _doseOverlay = new DoseOverlayViewModel(_eventBus, prescGy, norm, fx);

            // == Wire event bus subscriptions ==
            _eventBus.RenderRequested += RequestRender;
            _eventBus.EQD2EnabledChanged += _ => { if (_dvhCache.Count > 0) RecalculateAllDVH(); };
            _eventBus.FractionsChanged += _ => { if (_dvhCache.Count > 0) RecalculateAllDVH(); };
            _eventBus.DisplayAlphaBetaChanged += _ => RecomputeDisplayEQD2IfActive();

            // == Forward DoseOverlay property changes so existing XAML bindings still work ==
            _doseOverlay.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);

            _contourLines = new ObservableCollection<IsodoseContourData>();
            _structureContourLines = new ObservableCollection<StructureContourData>();

            // == Legacy property sync (bridge until XAML migrates to DoseOverlay.xxx) ==
            _isodoseLevelArray = _doseOverlay._isodoseLevelArray;
            IsodoseLevels = _doseOverlay.IsodoseLevels;

            int width = snapshot.CtImage.XSize;
            int height = snapshot.CtImage.YSize;
            _maxSlice = snapshot.CtImage.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            CtImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);
            OverlayImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);

            InitializePlotModel();
            _doseOverlay.LoadPreset("Eclipse");
            AutoPreset();
        }

        private void WireIsodoseLevelEvents()
        {
            IsodoseLevels.CollectionChanged += (s, e) =>
            {
                RebuildIsodoseArray();
                if (e.NewItems != null)
                    foreach (IsodoseLevel item in e.NewItems)
                        item.PropertyChanged += OnIsodoseLevelChanged;
                if (e.OldItems != null)
                    foreach (IsodoseLevel item in e.OldItems)
                        item.PropertyChanged -= OnIsodoseLevelChanged;
            };
            foreach (var level in IsodoseLevels)
                level.PropertyChanged += OnIsodoseLevelChanged;
        }

        private void OnIsodoseLevelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IsodoseLevel.IsVisible) ||
                e.PropertyName == nameof(IsodoseLevel.Fraction) ||
                e.PropertyName == nameof(IsodoseLevel.Alpha) ||
                e.PropertyName == nameof(IsodoseLevel.AbsoluteDoseGy) ||
                e.PropertyName == nameof(IsodoseLevel.Color))
            {
                RebuildIsodoseArray();
                RequestRender();
            }
        }

        internal void RebuildIsodoseArray()
        {
            _doseOverlay.RebuildIsodoseArray();
            _isodoseLevelArray = _doseOverlay._isodoseLevelArray;
        }

        internal double GetPrescriptionGy()
        {
            return _snapshot?.ActivePlan?.TotalDoseGy ?? 0;
        }

        private void InitializePlotModel()
        {
            PlotModel = new PlotModel
            {
                Title = "DVH",
                TitleColor = OxyColor.FromRgb(240, 242, 245),
                PlotAreaBorderColor = OxyColor.FromRgb(42, 48, 64),
                Background = OxyColor.FromRgb(18, 21, 27),
                TextColor = OxyColor.FromRgb(240, 242, 245),
            };

            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Dose (Gy)",
                Minimum = 0,
                TitleColor = OxyColor.FromRgb(155, 163, 176),
                TextColor = OxyColor.FromRgb(155, 163, 176),
                TicklineColor = OxyColor.FromRgb(92, 100, 117),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(26, 30, 38),
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColor.FromRgb(22, 26, 34),
            });

            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Volume (%)",
                Minimum = 0,
                Maximum = 101,
                TitleColor = OxyColor.FromRgb(155, 163, 176),
                TextColor = OxyColor.FromRgb(155, 163, 176),
                TicklineColor = OxyColor.FromRgb(92, 100, 117),
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(26, 30, 38),
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColor.FromRgb(22, 26, 34),
            });

            PlotModel.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
                LegendTextColor = OxyColor.FromRgb(240, 242, 245),
                LegendBackground = OxyColor.FromArgb(220, 18, 21, 27),
                LegendBorder = OxyColor.FromRgb(42, 48, 64),
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _summationCts?.Cancel();
            _recomputeCts?.Cancel();
            _displayAlphaBetaDebounce?.Stop();
            _renderingService?.Dispose();
            _summationService?.Dispose();
        }

        /// <summary>
        /// DVH cache entry using Clean Architecture DTOs.
        /// </summary>
        internal class DVHCacheEntry
        {
            public string PlanId { get; set; } = "";
            public StructureData Structure { get; set; } = null!;
            public DvhCurveData DvhCurve { get; set; } = null!;
        }
    }

    /// <summary>
    /// Per-structure Î±/Î² setting for DVH EQD2 calculations.
    /// Default: 10 Gy for targets (PTV/CTV/GTV), 3 Gy for organs at risk.
    /// </summary>
    public class StructureAlphaBetaItem : INotifyPropertyChanged
    {
        public StructureData Structure { get; }
        private double _alphaBeta;
        public string Id => Structure.Id;
        public string DicomType => Structure.DicomType;

        public double AlphaBeta
        {
            get => _alphaBeta;
            set
            {
                if (value <= 0) value = 0.5;
                _alphaBeta = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlphaBeta)));
            }
        }

        public StructureAlphaBetaItem(StructureData structure, double alphaBeta)
        {
            Structure = structure;
            _alphaBeta = alphaBeta;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
