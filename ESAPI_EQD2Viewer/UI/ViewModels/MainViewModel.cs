using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPI_EQD2Viewer.Core.Calculations;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Services;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel : ObservableObject, IDisposable
    {
        private readonly ScriptContext _context;
        private readonly PlanSetup _plan;
        private readonly IImageRenderingService _renderingService;
        private readonly IDebugExportService _debugExportService;
        private readonly IDVHService _dvhService;

        private int _renderPendingFlag = 0;
        private bool _disposed;

        // DVH data cache
        private readonly List<DVHCacheEntry> _dvhCache = new List<DVHCacheEntry>();

        #region Isodose Image Properties

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
            set { if (SetProperty(ref _currentSlice, value)) RequestRender(); }
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
            set { if (SetProperty(ref _windowLevel, value)) RequestRender(); }
        }

        private double _windowWidth;
        public double WindowWidth
        {
            get => _windowWidth;
            set { if (SetProperty(ref _windowWidth, value)) RequestRender(); }
        }

        private string _statusText;
        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        /// <summary>
        /// Vector isodose contour lines for Line mode.
        /// Contains frozen StreamGeometry objects that render as WPF Path elements.
        /// Bound to InteractiveImageViewer's ContourLines property.
        /// </summary>
        private ObservableCollection<IsodoseContourData> _contourLines;
        public ObservableCollection<IsodoseContourData> ContourLines
        {
            get => _contourLines;
            set => SetProperty(ref _contourLines, value);
        }

        #endregion

        #region Dose Display Mode Properties

        private DoseDisplayMode _doseDisplayMode = DoseDisplayMode.Line;
        public DoseDisplayMode DoseDisplayMode
        {
            get => _doseDisplayMode;
            set
            {
                if (SetProperty(ref _doseDisplayMode, value))
                {
                    OnPropertyChanged(nameof(IsLineMode));
                    OnPropertyChanged(nameof(IsFillMode));
                    OnPropertyChanged(nameof(IsColorwashMode));
                    RequestRender();
                }
            }
        }

        public bool IsLineMode
        {
            get => _doseDisplayMode == DoseDisplayMode.Line;
            set { if (value) DoseDisplayMode = DoseDisplayMode.Line; }
        }
        public bool IsFillMode
        {
            get => _doseDisplayMode == DoseDisplayMode.Fill;
            set { if (value) DoseDisplayMode = DoseDisplayMode.Fill; }
        }
        public bool IsColorwashMode
        {
            get => _doseDisplayMode == DoseDisplayMode.Colorwash;
            set { if (value) DoseDisplayMode = DoseDisplayMode.Colorwash; }
        }

        private double _colorwashOpacity = 0.45;
        public double ColorwashOpacity
        {
            get => _colorwashOpacity;
            set { if (SetProperty(ref _colorwashOpacity, value)) RequestRender(); }
        }

        private double _colorwashMinPercent = 0.10;
        public double ColorwashMinPercent
        {
            get => _colorwashMinPercent;
            set { if (SetProperty(ref _colorwashMinPercent, value)) RequestRender(); }
        }

        #endregion

        #region Isodose Unit (% / Gy)

        private IsodoseUnit _isodoseUnit = IsodoseUnit.Percent;
        public IsodoseUnit IsodoseUnit
        {
            get => _isodoseUnit;
            set
            {
                if (SetProperty(ref _isodoseUnit, value))
                {
                    OnPropertyChanged(nameof(IsPercentMode));
                    OnPropertyChanged(nameof(IsGyMode));
                    OnPropertyChanged(nameof(IsodoseColumnHeader));
                    UpdateIsodoseLabels();
                }
            }
        }

        public bool IsPercentMode
        {
            get => _isodoseUnit == IsodoseUnit.Percent;
            set { if (value) IsodoseUnit = IsodoseUnit.Percent; }
        }

        public bool IsGyMode
        {
            get => _isodoseUnit == IsodoseUnit.Gy;
            set { if (value) IsodoseUnit = IsodoseUnit.Gy; }
        }

        public string IsodoseColumnHeader => _isodoseUnit == IsodoseUnit.Gy ? "Dose (Gy)" : "Level %";

        public double ReferenceDoseGy
        {
            get
            {
                double prescGy = GetPrescriptionGy();
                double norm = _plan?.PlanNormalizationValue ?? 100.0;
                if (double.IsNaN(norm) || norm <= 0) norm = 100.0;
                else if (norm < 5.0) norm *= 100.0;
                double refGy = prescGy * (norm / 100.0);
                return refGy < 0.1 ? prescGy : refGy;
            }
        }

        private void UpdateIsodoseLabels()
        {
            double refGy = ReferenceDoseGy;
            foreach (var level in IsodoseLevels)
            {
                if (_isodoseUnit == IsodoseUnit.Gy)
                    level.Label = $"{(level.Fraction * refGy):F1} Gy";
                else
                    level.Label = $"{(level.Fraction * 100):F0}%";
            }
        }

        #endregion

        #region EQD2 Properties

        private bool _isEQD2Enabled;
        public bool IsEQD2Enabled
        {
            get => _isEQD2Enabled;
            set
            {
                if (SetProperty(ref _isEQD2Enabled, value))
                {
                    RequestRender();
                    if (_dvhCache.Any()) RecalculateAllDVH();
                }
            }
        }

        private double _globalAlphaBeta = 3.0;
        public double GlobalAlphaBeta
        {
            get => _globalAlphaBeta;
            set
            {
                if (SetProperty(ref _globalAlphaBeta, value))
                    RequestRender();
            }
        }

        private int _numberOfFractions = 1;
        public int NumberOfFractions
        {
            get => _numberOfFractions;
            set
            {
                if (SetProperty(ref _numberOfFractions, value))
                {
                    RequestRender();
                    if (_dvhCache.Any()) RecalculateAllDVH();
                }
            }
        }

        private EQD2MeanMethod _meanMethod = EQD2MeanMethod.Simple;
        public EQD2MeanMethod MeanMethod
        {
            get => _meanMethod;
            set
            {
                if (SetProperty(ref _meanMethod, value))
                    if (_dvhCache.Any()) RecalculateAllDVH();
            }
        }

        private bool _useDifferentialMethod;
        public bool UseDifferentialMethod
        {
            get => _useDifferentialMethod;
            set
            {
                if (SetProperty(ref _useDifferentialMethod, value))
                    MeanMethod = value ? EQD2MeanMethod.Differential : EQD2MeanMethod.Simple;
            }
        }

        #endregion

        #region DVH Properties

        private bool _showPhysicalDVH = true;
        public bool ShowPhysicalDVH
        {
            get => _showPhysicalDVH;
            set { if (SetProperty(ref _showPhysicalDVH, value)) UpdatePlotVisibility(); }
        }

        private bool _showEQD2DVH = true;
        public bool ShowEQD2DVH
        {
            get => _showEQD2DVH;
            set { if (SetProperty(ref _showEQD2DVH, value)) UpdatePlotVisibility(); }
        }

        public PlotModel PlotModel { get; private set; }
        public ObservableCollection<DVHSummary> SummaryData { get; } = new ObservableCollection<DVHSummary>();
        public ObservableCollection<StructureAlphaBetaItem> StructureSettings { get; } = new ObservableCollection<StructureAlphaBetaItem>();

        #endregion

        #region Isodose Levels

        public ObservableCollection<IsodoseLevel> IsodoseLevels { get; }
        private IsodoseLevel[] _isodoseLevelArray;

        private string _isodosePresetName = "Default (4)";
        public string IsodosePresetName
        {
            get => _isodosePresetName;
            set => SetProperty(ref _isodosePresetName, value);
        }

        #endregion

        public MainViewModel(ScriptContext context, IImageRenderingService renderingService,
            IDebugExportService debugExportService, IDVHService dvhService)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _plan = context.ExternalPlanSetup;
            _renderingService = renderingService ?? throw new ArgumentNullException(nameof(renderingService));
            _debugExportService = debugExportService ?? throw new ArgumentNullException(nameof(debugExportService));
            _dvhService = dvhService ?? throw new ArgumentNullException(nameof(dvhService));

            // Vector contour collection for Line mode
            _contourLines = new ObservableCollection<IsodoseContourData>();

            // Isodose levels — start with Eclipse defaults
            var defaults = IsodoseLevel.GetEclipseDefaults();
            IsodoseLevels = new ObservableCollection<IsodoseLevel>(defaults);
            _isodoseLevelArray = defaults;
            _isodosePresetName = "Eclipse (10)";

            // Wire up change notifications
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

            // Image setup
            int width = _context.Image.XSize;
            int height = _context.Image.YSize;
            _maxSlice = _context.Image.ZSize - 1;
            _currentSlice = _maxSlice / 2;

            if (_plan != null)
                _numberOfFractions = _plan.NumberOfFractions ?? 1;

            // Initialize rendering
            _renderingService.Initialize(width, height);
            StatusText = "Loading data...";

            double prescriptionGy = GetPrescriptionGy();
            _renderingService.PreloadData(_context.Image, _plan?.Dose, prescriptionGy);

            CtImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            DoseImageSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            InitializePlotModel();
            AutoPreset();
        }

        private void OnIsodoseLevelChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IsodoseLevel.IsVisible) ||
                e.PropertyName == nameof(IsodoseLevel.Fraction) ||
                e.PropertyName == nameof(IsodoseLevel.Alpha))
            {
                RebuildIsodoseArray();
                RequestRender();
            }
        }

        private void RebuildIsodoseArray()
        {
            _isodoseLevelArray = new IsodoseLevel[IsodoseLevels.Count];
            IsodoseLevels.CopyTo(_isodoseLevelArray, 0);
        }

        private double GetPrescriptionGy()
        {
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
                TitleColor = OxyColors.White,
                PlotAreaBorderColor = OxyColor.FromRgb(80, 80, 80),
                Background = OxyColor.FromRgb(26, 26, 26),
                TextColor = OxyColors.White,
            };

            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Dose (Gy)",
                Minimum = 0,
                TitleColor = OxyColors.White,
                TextColor = OxyColor.FromRgb(200, 200, 200),
                TicklineColor = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(55, 55, 55),
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColor.FromRgb(40, 40, 40),
                AxislineColor = OxyColors.Gray,
            });

            PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Volume (%)",
                Minimum = 0,
                Maximum = 101,
                TitleColor = OxyColors.White,
                TextColor = OxyColor.FromRgb(200, 200, 200),
                TicklineColor = OxyColors.Gray,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = OxyColor.FromRgb(55, 55, 55),
                MinorGridlineStyle = LineStyle.Dot,
                MinorGridlineColor = OxyColor.FromRgb(40, 40, 40),
                AxislineColor = OxyColors.Gray,
            });

            PlotModel.Legends.Add(new OxyPlot.Legends.Legend
            {
                LegendPosition = OxyPlot.Legends.LegendPosition.RightTop,
                LegendTextColor = OxyColors.White,
                LegendBackground = OxyColor.FromArgb(200, 30, 30, 30),
                LegendBorder = OxyColors.Gray,
                LegendBorderThickness = 1,
            });
        }

        #region Rendering

        private void RequestRender()
        {
            if (Interlocked.CompareExchange(ref _renderPendingFlag, 1, 0) != 0)
                return;

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref _renderPendingFlag, 0);
                RenderScene();
            }), DispatcherPriority.Render);
        }

        private void RenderScene()
        {
            if (_disposed || _context.Image == null) return;

            // Always render CT
            _renderingService.RenderCtImage(_context.Image, CtImageSource, CurrentSlice, WindowLevel, WindowWidth);

            double planTotalDoseGy = GetPrescriptionGy();
            double planNormalization = _plan?.PlanNormalizationValue ?? 100.0;

            EQD2Settings eqd2 = null;
            if (_isEQD2Enabled)
            {
                eqd2 = new EQD2Settings
                {
                    IsEnabled = true,
                    AlphaBeta = _globalAlphaBeta,
                    NumberOfFractions = _numberOfFractions
                };
            }

            if (_doseDisplayMode == DoseDisplayMode.Line)
            {
                // === LINE MODE: vector contours ===
                // Clear dose bitmap (RenderDoseImage in Line mode just clears)
                _renderingService.RenderDoseImage(
                    _context.Image, _plan?.Dose, DoseImageSource, CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray,
                    _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);

                // Generate vector contours via marching squares
                var contourResult = _renderingService.GenerateVectorContours(
                    _context.Image, _plan?.Dose, CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray, eqd2);

                // Replace contour collection (triggers UI update)
                ContourLines = new ObservableCollection<IsodoseContourData>(contourResult.Contours);

                StatusText = contourResult.StatusText ?? "";
            }
            else
            {
                // === FILL / COLORWASH MODE: bitmap rendering ===
                // Clear vector contours
                if (_contourLines != null && _contourLines.Count > 0)
                    ContourLines = new ObservableCollection<IsodoseContourData>();

                StatusText = _renderingService.RenderDoseImage(
                    _context.Image, _plan?.Dose, DoseImageSource, CurrentSlice,
                    planTotalDoseGy, planNormalization, _isodoseLevelArray,
                    _doseDisplayMode, _colorwashOpacity, _colorwashMinPercent, eqd2);
            }
        }

        #endregion

        #region DVH Management

        public void AddStructuresForDVH(IEnumerable<Structure> structures)
        {
            if (_plan == null || structures == null) return;

            foreach (var structure in structures)
            {
                if (_dvhCache.Any(c => c.Structure.Id == structure.Id))
                    continue;

                DVHData dvhData = _dvhService.GetDVH(_plan, structure);
                if (dvhData == null) continue;

                _dvhCache.Add(new DVHCacheEntry
                {
                    Plan = _plan,
                    Structure = structure,
                    DVHData = dvhData
                });

                double defaultAB = (structure.DicomType == "PTV" || structure.DicomType == "CTV" || structure.DicomType == "GTV")
                    ? 10.0 : 3.0;

                var settingItem = new StructureAlphaBetaItem(structure, defaultAB);
                settingItem.PropertyChanged += OnStructureSettingChanged;
                StructureSettings.Add(settingItem);

                var physSummary = _dvhService.BuildPhysicalSummary(_plan, structure, dvhData);
                SummaryData.Add(physSummary);

                var color = OxyColor.FromArgb(structure.Color.A, structure.Color.R, structure.Color.G, structure.Color.B);
                var series = new LineSeries
                {
                    Title = $"{structure.Id} ({_plan.Id})",
                    Tag = $"Physical_{_plan.Id}_{structure.Id}",
                    Color = color,
                    StrokeThickness = 2
                };
                series.Points.AddRange(dvhData.CurveData.Select(p =>
                    new DataPoint(ConvertDoseToGy(p.DoseValue), p.Volume)));
                PlotModel.Series.Add(series);
            }

            if (_isEQD2Enabled) RecalculateAllDVH();
            RefreshPlot();
        }

        public void ClearDVH()
        {
            _dvhCache.Clear();
            StructureSettings.Clear();
            PlotModel.Series.Clear();
            SummaryData.Clear();
            RefreshPlot();
        }

        private void RecalculateAllDVH()
        {
            var oldSeries = PlotModel.Series.Where(s => (s.Tag as string)?.StartsWith("EQD2_") ?? false).ToList();
            foreach (var s in oldSeries) PlotModel.Series.Remove(s);

            var oldSummaries = SummaryData.Where(s => s.Type == "EQD2").ToList();
            foreach (var s in oldSummaries) SummaryData.Remove(s);

            if (!_isEQD2Enabled) { RefreshPlot(); return; }

            foreach (var entry in _dvhCache)
            {
                var setting = StructureSettings.FirstOrDefault(s => s.Structure.Id == entry.Structure.Id);
                double alphaBeta = setting?.AlphaBeta ?? 3.0;

                var eqd2Summary = _dvhService.BuildEQD2Summary(
                    entry.Plan, entry.Structure, entry.DVHData,
                    _numberOfFractions, alphaBeta, _meanMethod);
                SummaryData.Add(eqd2Summary);

                var curveInGy = entry.DVHData.CurveData.Select(p =>
                    new DVHPoint(
                        new DoseValue(ConvertDoseToGy(p.DoseValue), DoseValue.DoseUnit.Gy),
                        p.Volume, p.VolumeUnit)).ToArray();

                var eqd2Curve = EQD2Calculator.ConvertCurveToEQD2(curveInGy, _numberOfFractions, alphaBeta);
                var color = OxyColor.FromArgb(
                    entry.Structure.Color.A, entry.Structure.Color.R,
                    entry.Structure.Color.G, entry.Structure.Color.B);

                var eqd2Series = new LineSeries
                {
                    Title = $"{entry.Structure.Id} EQD2 (α/β={alphaBeta:F1})",
                    LineStyle = LineStyle.Dash,
                    Tag = $"EQD2_{entry.Plan.Id}_{entry.Structure.Id}",
                    Color = color,
                    StrokeThickness = 2
                };
                eqd2Series.Points.AddRange(eqd2Curve.Select(p =>
                    new DataPoint(p.DoseValue.Dose, p.Volume)));
                PlotModel.Series.Add(eqd2Series);
            }

            RefreshPlot();
        }

        private void OnStructureSettingChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(StructureAlphaBetaItem.AlphaBeta) && _isEQD2Enabled)
                RecalculateAllDVH();
        }

        private void UpdatePlotVisibility()
        {
            foreach (var series in PlotModel.Series)
            {
                if (series.Tag is string tag)
                {
                    series.IsVisible =
                        (tag.StartsWith("Physical_") && _showPhysicalDVH) ||
                        (tag.StartsWith("EQD2_") && _showEQD2DVH);
                }
            }
            PlotModel.InvalidatePlot(true);
        }

        private void RefreshPlot()
        {
            UpdatePlotVisibility();
            PlotModel.InvalidatePlot(true);
        }

        private static double ConvertDoseToGy(DoseValue dv)
        {
            return dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;
        }

        #endregion

        #region Commands

        [RelayCommand]
        private void AutoPreset()
        {
            WindowLevel = 40;
            WindowWidth = 400;
        }

        [RelayCommand]
        private void Preset(string type)
        {
            switch (type)
            {
                case "Soft": WindowLevel = 40; WindowWidth = 400; break;
                case "Lung": WindowLevel = -600; WindowWidth = 1600; break;
                case "Bone": WindowLevel = 300; WindowWidth = 1500; break;
            }
        }

        [RelayCommand]
        private void LoadIsodosePreset(string preset)
        {
            IsodoseLevel[] levels;
            switch (preset)
            {
                case "Eclipse":
                    levels = IsodoseLevel.GetEclipseDefaults();
                    IsodosePresetName = "Eclipse (10)";
                    break;
                case "Minimal":
                    levels = IsodoseLevel.GetMinimalSet();
                    IsodosePresetName = "Minimal (3)";
                    break;
                default:
                    levels = IsodoseLevel.GetDefaults();
                    IsodosePresetName = "Default (4)";
                    break;
            }

            IsodoseLevels.Clear();
            foreach (var l in levels) IsodoseLevels.Add(l);
            RebuildIsodoseArray();
            UpdateIsodoseLabels();
            RequestRender();
        }

        [RelayCommand]
        private void AddIsodoseLevel()
        {
            var newLevel = new IsodoseLevel(0.60, "60%", 0xFF9900FF);
            newLevel.PropertyChanged += OnIsodoseLevelChanged;
            IsodoseLevels.Add(newLevel);
            RebuildIsodoseArray();
            UpdateIsodoseLabels();
            RequestRender();
        }

        [RelayCommand]
        private void RemoveIsodoseLevel(IsodoseLevel level)
        {
            if (level != null && IsodoseLevels.Contains(level))
            {
                level.PropertyChanged -= OnIsodoseLevelChanged;
                IsodoseLevels.Remove(level);
                RebuildIsodoseArray();
                RequestRender();
            }
        }

        [RelayCommand]
        private void ToggleAllIsodose(string visibleStr)
        {
            bool visible = visibleStr?.ToLower() == "true";
            foreach (var level in IsodoseLevels)
                level.IsVisible = visible;
        }

        [RelayCommand]
        private void CalculateEQD2()
        {
            IsEQD2Enabled = true;
            RecalculateAllDVH();
        }

        [RelayCommand]
        private void ExportCSV()
        {
            if (SummaryData.Any())
                ExportService.ExportSummaryToCSV(SummaryData);
        }

        [RelayCommand]
        private void Debug()
        {
            _debugExportService.ExportDebugLog(_context, _plan, CurrentSlice);
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _renderingService?.Dispose();
            _ctImageSource = null;
            _doseImageSource = null;
        }

        private class DVHCacheEntry
        {
            public PlanSetup Plan { get; set; }
            public Structure Structure { get; set; }
            public DVHData DVHData { get; set; }
        }
    }

    /// <summary>
    /// Per-structure alpha/beta setting for the UI DataGrid.
    /// </summary>
    public class StructureAlphaBetaItem : INotifyPropertyChanged
    {
        public Structure Structure { get; }
        private double _alphaBeta;

        public string Id => Structure.Id;
        public string DicomType => Structure.DicomType;

        public double AlphaBeta
        {
            get => _alphaBeta;
            set { _alphaBeta = value; OnPropertyChanged(); }
        }

        public StructureAlphaBetaItem(Structure structure, double alphaBeta)
        {
            Structure = structure;
            _alphaBeta = alphaBeta;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}