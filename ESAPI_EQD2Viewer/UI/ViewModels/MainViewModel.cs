using CommunityToolkit.Mvvm.ComponentModel;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Models;
using OxyPlot;
using OxyPlot.Axes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Logging;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    /// <summary>
    /// Main view model for the EQD2 Viewer re-irradiation assessment tool.
    /// 
    /// Split into partial classes by responsibility:
    ///   .cs              — Core lifecycle: constructor, dispose, shared state, isodose management
    ///   .Properties.cs   — All bindable properties (WPF data binding targets)
    ///   .Rendering.cs    — CT/dose/structure rendering pipeline (UI thread only)
    ///   .DVH.cs          — DVH calculation, structure settings, OxyPlot series management
    ///   .Summation.cs    — Multi-plan dose summation + per-structure EQD2 DVH
    ///   .Commands.cs     — All RelayCommand handlers (user actions from UI)
    /// 
    /// α/β architecture:
    ///   DisplayAlphaBeta  — controls isodose visualization only (slider in sidebar)
    ///   Per-structure α/β — controls DVH calculations (editable in structure settings grid)
    ///   Summation α/β     — set at summation dialog; display α/β syncs initially
    /// 
    /// Lifetime: Disposed when MainWindow closes. Must be disposed before ESAPI
    /// ScriptContext goes out of scope.
    /// </summary>
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        internal readonly ScriptContext _context;
        internal readonly PlanSetup _plan;
        internal readonly IImageRenderingService _renderingService;
        internal readonly IDebugExportService _debugExportService;
        internal readonly IDVHService _dvhService;

        // ── Clean Architecture data source ──
        internal readonly ClinicalSnapshot _snapshot;
        internal readonly bool _isSnapshotMode;

        private int _renderPendingFlag = 0;
        private bool _disposed;
        private volatile bool _isRendering;

        internal readonly List<DVHCacheEntry> _dvhCache = new List<DVHCacheEntry>();
        internal readonly List<Structure> _visibleStructures = new List<Structure>();

        public MainViewModel(ScriptContext context, IImageRenderingService renderingService,
            IDebugExportService debugExportService, IDVHService dvhService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _plan = context.ExternalPlanSetup;
            _renderingService = renderingService ?? throw new ArgumentNullException(nameof(renderingService));
            _debugExportService = debugExportService ?? throw new ArgumentNullException(nameof(debugExportService));
            _dvhService = dvhService ?? throw new ArgumentNullException(nameof(dvhService));

            _contourLines = new ObservableCollection<IsodoseContourData>();
            _structureContourLines = new ObservableCollection<StructureContourData>();

            var defaults = IsodoseLevel.GetEclipseDefaults();
            IsodoseLevels = new ObservableCollection<IsodoseLevel>(defaults);
            _isodoseLevelArray = defaults;
            WireIsodoseLevelEvents();

            int width = _context.Image.XSize;
            int height = _context.Image.YSize;
            _maxSlice = _context.Image.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            if (_plan != null)
                _numberOfFractions = _plan.NumberOfFractions ?? 1;

            _renderingService.Initialize(width, height);
            StatusText = "Initializing...";

            double prescriptionGy = GetPrescriptionGy();
            _renderingService.PreloadData(_context.Image, _plan?.Dose, prescriptionGy);

            CtImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            OverlayImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            InitializePlotModel();
            AutoPreset();
        }

        /// <summary>
        /// Clean Architecture constructor — no ESAPI, no ScriptContext.
        /// Used by DevRunner and future test infrastructure.
        /// All data comes from the pre-loaded ClinicalSnapshot.
        /// </summary>
        public MainViewModel(ClinicalSnapshot snapshot,
            IImageRenderingService renderingService,
            IDebugExportService debugExportService,
            IDVHService dvhService)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            _isSnapshotMode = true;

            // These remain null in snapshot mode — code checks _isSnapshotMode
            _context = null;
            _plan = null;

            _renderingService = renderingService ?? throw new ArgumentNullException(nameof(renderingService));
            _debugExportService = debugExportService ?? throw new ArgumentNullException(nameof(debugExportService));
            _dvhService = dvhService ?? throw new ArgumentNullException(nameof(dvhService));

            _contourLines = new ObservableCollection<IsodoseContourData>();
            _structureContourLines = new ObservableCollection<StructureContourData>();

            var defaults = IsodoseLevel.GetEclipseDefaults();
            IsodoseLevels = new ObservableCollection<IsodoseLevel>(defaults);
            _isodoseLevelArray = defaults;
            WireIsodoseLevelEvents();

            int width = snapshot.CtImage.XSize;
            int height = snapshot.CtImage.YSize;
            _maxSlice = snapshot.CtImage.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            _numberOfFractions = snapshot.ActivePlan?.NumberOfFractions ?? 1;

            // Note: Initialize + PreloadData already called by DevRunner App.xaml.cs
            // before passing the service to this constructor

            CtImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);
            OverlayImageSource = new WriteableBitmap(width, height, 96, 96,
                PixelFormats.Bgra32, null);

            InitializePlotModel();
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
            _isodoseLevelArray = new IsodoseLevel[IsodoseLevels.Count];
            IsodoseLevels.CopyTo(_isodoseLevelArray, 0);
        }

        internal double GetPrescriptionGy()
        {
            if (_isSnapshotMode)
                return _snapshot?.ActivePlan?.TotalDoseGy ?? 0;

            if (_plan == null) return 0;
            return _plan.TotalDose.Unit == DoseValue.DoseUnit.cGy
                ? _plan.TotalDose.Dose / 100.0
                : _plan.TotalDose.Dose;
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

        internal class DVHCacheEntry
        {
            public PlanSetup Plan { get; set; }
            public Structure Structure { get; set; }
            public DVHData DVHData { get; set; }
        }
    }

    /// <summary>
    /// Per-structure α/β setting for DVH EQD2 calculations.
    /// Default: 10 Gy for targets (PTV/CTV/GTV), 3 Gy for organs at risk.
    /// </summary>
    public class StructureAlphaBetaItem : INotifyPropertyChanged
    {
        public Structure Structure { get; }
        private double _alphaBeta;
        public string Id => Structure.Id;
        public string DicomType => Structure.DicomType;

        /// <summary>
        /// Tissue-specific α/β ratio [Gy].
        /// Used for both single-plan and summation DVH EQD2 calculations.
        /// </summary>
        public double AlphaBeta
        {
            get => _alphaBeta;
            set
            {
                if (value <= 0) value = 0.5; // Prevent clinically invalid values
                _alphaBeta = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AlphaBeta)));
            }
        }

        public StructureAlphaBetaItem(Structure structure, double alphaBeta)
        {
            Structure = structure;
            _alphaBeta = alphaBeta;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}