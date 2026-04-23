using EQD2Viewer.Services.Rendering;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Data;
using OxyPlot;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace EQD2Viewer.App.UI.ViewModels
{
    public partial class MainViewModel
    {
        // ════════════════════════════════════════════════════════
        // PATIENT / PLAN DISPLAY (read-only, from snapshot)
        // ════════════════════════════════════════════════════════

        public string PatientDisplayName
        {
            get
            {
                var p = _snapshot?.Patient;
                if (p == null) return "No patient";
                string name = $"{p.LastName}, {p.FirstName}";
                if (!string.IsNullOrEmpty(p.Id)) name += $"  ({p.Id})";
                return name;
            }
        }

        public string PlanDisplayLabel
        {
            get
            {
                var plan = _snapshot?.ActivePlan;
                if (plan == null) return "No plan";
                return string.IsNullOrEmpty(plan.CourseId) ? plan.Id : $"{plan.CourseId} / {plan.Id}";
            }
        }

        public string PrescriptionDisplayLabel
        {
            get
            {
                double gy = GetPrescriptionGy();
                return gy > 0 ? $"{gy:F1} Gy" : "No Rx";
            }
        }

        public string FractionDisplayLabel
        {
            get
            {
                int fx = _snapshot?.ActivePlan?.NumberOfFractions ?? 0;
                if (fx <= 0) return "";
                double gy = GetPrescriptionGy();
                return $"{fx} fx × {(fx > 0 ? gy / fx : 0):F2} Gy";
            }
        }

        /// <summary>
        /// All structures available in the current snapshot.
        /// Exposed for the structure selection dialog — avoids direct access
        /// to internal snapshot state from the View layer.
        /// </summary>
        public IReadOnlyList<StructureData> AvailableStructures =>
            _snapshot?.Structures ?? (IReadOnlyList<StructureData>)new StructureData[0];

        // ════════════════════════════════════════════════════════
        // CT IMAGE VIEWER STATE
        // ════════════════════════════════════════════════════════

        private WriteableBitmap _ctImageSource = null!;
        public WriteableBitmap CtImageSource { get => _ctImageSource; set => SetProperty(ref _ctImageSource, value); }

        private WriteableBitmap _doseImageSource = null!;
        public WriteableBitmap DoseImageSource { get => _doseImageSource; set => SetProperty(ref _doseImageSource, value); }

        private int _currentSlice;
        public int CurrentSlice { get => _currentSlice; set { if (SetProperty(ref _currentSlice, value)) RequestRender(); } }

        private int _maxSlice;
        public int MaxSlice { get => _maxSlice; set => SetProperty(ref _maxSlice, value); }

        private double _windowLevel;
        public double WindowLevel { get => _windowLevel; set { if (SetProperty(ref _windowLevel, value)) RequestRender(); } }

        private double _windowWidth;
        public double WindowWidth { get => _windowWidth; set { if (SetProperty(ref _windowWidth, value)) RequestRender(); } }

        private string _statusText = "";
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        private string _doseCursorText = "";
        public string DoseCursorText { get => _doseCursorText; set => SetProperty(ref _doseCursorText, value); }

        // ════════════════════════════════════════════════════════
        // CONTOUR COLLECTIONS (bound by ItemsControl in XAML)
        // ════════════════════════════════════════════════════════

        private ObservableCollection<IsodoseContourData> _contourLines;
        public ObservableCollection<IsodoseContourData> ContourLines { get => _contourLines; set => SetProperty(ref _contourLines, value); }

        private ObservableCollection<StructureContourData> _structureContourLines;
        public ObservableCollection<StructureContourData> StructureContourLines { get => _structureContourLines; set => SetProperty(ref _structureContourLines, value); }

        private bool _showStructureContours;
        public bool ShowStructureContours { get => _showStructureContours; set { if (SetProperty(ref _showStructureContours, value)) RequestRender(); } }

        // ════════════════════════════════════════════════════════
        // DOSE OVERLAY — delegated to DoseOverlayViewModel
        // These properties forward to _doseOverlay so existing XAML bindings
        // continue to work without path changes. New XAML should bind via
        // DoseOverlay.PropertyName instead.
        // ════════════════════════════════════════════════════════

        /// <summary>Exposes the child ViewModel for new XAML bindings.</summary>
        internal DoseOverlayViewModel DoseOverlay => _doseOverlay;

        public DoseDisplayMode DoseDisplayMode
        {
            get => _doseOverlay.DoseDisplayMode;
            set => _doseOverlay.DoseDisplayMode = value;
        }

        public bool IsLineMode { get => _doseOverlay.IsLineMode; set => _doseOverlay.IsLineMode = value; }
        public bool IsFillMode { get => _doseOverlay.IsFillMode; set => _doseOverlay.IsFillMode = value; }
        public bool IsColorwashMode { get => _doseOverlay.IsColorwashMode; set => _doseOverlay.IsColorwashMode = value; }

        public double ColorwashOpacity { get => _doseOverlay.ColorwashOpacity; set => _doseOverlay.ColorwashOpacity = value; }
        public double ColorwashMinPercent { get => _doseOverlay.ColorwashMinPercent; set => _doseOverlay.ColorwashMinPercent = value; }

        public IsodoseMode CurrentIsodoseMode { get => _doseOverlay.CurrentIsodoseMode; set => _doseOverlay.CurrentIsodoseMode = value; }
        public bool IsRelativeMode { get => _doseOverlay.IsRelativeMode; set => _doseOverlay.IsRelativeMode = value; }
        public bool IsAbsoluteMode { get => _doseOverlay.IsAbsoluteMode; set => _doseOverlay.IsAbsoluteMode = value; }
        public bool IsRelativeModeSettingsVisible => _doseOverlay.IsRelativeModeSettingsVisible;

        public IsodoseUnit IsodoseUnit { get => _doseOverlay.IsodoseUnit; set => _doseOverlay.IsodoseUnit = value; }
        public bool IsPercentMode { get => _doseOverlay.IsPercentMode; set => _doseOverlay.IsPercentMode = value; }
        public bool IsGyMode { get => _doseOverlay.IsGyMode; set => _doseOverlay.IsGyMode = value; }
        public string IsodoseColumnHeader => _doseOverlay.IsodoseColumnHeader;

        public double ReferenceDoseGy => _doseOverlay.ReferenceDoseGy;

        public ObservableCollection<IsodoseLevel> IsodoseLevels { get; }
        internal IsodoseLevel[] _isodoseLevelArray;

        public string IsodosePresetName { get => _doseOverlay.IsodosePresetName; set => _doseOverlay.IsodosePresetName = value; }

        internal void UpdateIsodoseLabels() => _doseOverlay.UpdateIsodoseLabels();

        internal double GetThresholdGy(IsodoseLevel level, double referenceDoseGy)
            => _doseOverlay.GetThresholdGy(level, referenceDoseGy);

        public bool IsEQD2Enabled { get => _doseOverlay.IsEQD2Enabled; set => _doseOverlay.IsEQD2Enabled = value; }

        public double DisplayAlphaBeta { get => _doseOverlay.DisplayAlphaBeta; set => _doseOverlay.DisplayAlphaBeta = value; }

        public int NumberOfFractions { get => _doseOverlay.NumberOfFractions; set => _doseOverlay.NumberOfFractions = value; }

        public string SummationAlphaBetaLabel { get => _doseOverlay.SummationAlphaBetaLabel; set => _doseOverlay.SummationAlphaBetaLabel = value; }

        // ════════════════════════════════════════════════════════
        // DOSE HOTSPOT (max dose location — navigable via "Jump to hotspot")
        // ════════════════════════════════════════════════════════

        private double _hotspotDoseGy;
        private int _hotspotSliceZ = -1;
        private int _hotspotPixelX;
        private int _hotspotPixelY;

        /// <summary>The absolute dose value at the hotspot, in Gy. 0 if no dose loaded.</summary>
        public double HotspotDoseGy
        {
            get => _hotspotDoseGy;
            private set
            {
                if (SetProperty(ref _hotspotDoseGy, value))
                {
                    OnPropertyChanged(nameof(HotspotLabel));
                    OnPropertyChanged(nameof(HasHotspot));
                }
            }
        }

        /// <summary>CT slice index where the hotspot lies.</summary>
        public int HotspotSliceZ
        {
            get => _hotspotSliceZ;
            private set
            {
                if (SetProperty(ref _hotspotSliceZ, value))
                    OnPropertyChanged(nameof(HotspotLabel));
            }
        }

        public int HotspotPixelX { get => _hotspotPixelX; private set => SetProperty(ref _hotspotPixelX, value); }
        public int HotspotPixelY { get => _hotspotPixelY; private set => SetProperty(ref _hotspotPixelY, value); }

        public bool HasHotspot => _hotspotSliceZ >= 0 && _hotspotDoseGy > 0;

        /// <summary>Compact human-readable label: "Dmax: 56.3 Gy @ slice 118".</summary>
        public string HotspotLabel => HasHotspot
            ? $"Dmax: {_hotspotDoseGy:F2} Gy @ slice {_hotspotSliceZ}"
            : "Dmax: (not computed)";

        internal void SetHotspot(double gy, int z, int x, int y)
        {
            HotspotDoseGy = gy;
            HotspotSliceZ = z;
            HotspotPixelX = x;
            HotspotPixelY = y;
        }

        internal void ClearHotspot() => SetHotspot(0, -1, 0, 0);

        private EQD2MeanMethod _meanMethod = EQD2MeanMethod.Simple;
        public EQD2MeanMethod MeanMethod
        {
            get => _meanMethod;
            set { if (SetProperty(ref _meanMethod, value) && _dvhCache.Count > 0) RecalculateAllDVH(); }
        }

        private bool _useDifferentialMethod;
        public bool UseDifferentialMethod
        {
            get => _useDifferentialMethod;
            set { if (SetProperty(ref _useDifferentialMethod, value)) MeanMethod = value ? EQD2MeanMethod.Differential : EQD2MeanMethod.Simple; }
        }

        // ════════════════════════════════════════════════════════
        // REGISTRATION OVERLAY
        // ════════════════════════════════════════════════════════

        public enum OverlayMode { Off, Checkerboard, Blend }

        private OverlayMode _overlayMode = OverlayMode.Off;
        public OverlayMode CurrentOverlayMode
        {
            get => _overlayMode;
            set
            {
                if (SetProperty(ref _overlayMode, value))
                {
                    OnPropertyChanged(nameof(IsOverlayOff)); OnPropertyChanged(nameof(IsOverlayCheckerboard));
                    OnPropertyChanged(nameof(IsOverlayBlend)); OnPropertyChanged(nameof(IsOverlayVisible));
                    OnPropertyChanged(nameof(OverlayModeLabel)); RequestRender();
                }
            }
        }

        public bool IsOverlayOff { get => _overlayMode == OverlayMode.Off; set { if (value) CurrentOverlayMode = OverlayMode.Off; } }
        public bool IsOverlayCheckerboard { get => _overlayMode == OverlayMode.Checkerboard; set { if (value) CurrentOverlayMode = OverlayMode.Checkerboard; } }
        public bool IsOverlayBlend { get => _overlayMode == OverlayMode.Blend; set { if (value) CurrentOverlayMode = OverlayMode.Blend; } }
        public bool IsOverlayVisible => _overlayMode != OverlayMode.Off;

        public string OverlayModeLabel => _overlayMode == OverlayMode.Checkerboard ? "REGISTRATION CHECK — Checkerboard"
            : _overlayMode == OverlayMode.Blend ? "REGISTRATION CHECK — Blend" : "";

        private double _overlayOpacity = 0.5;
        public double OverlayOpacity { get => _overlayOpacity; set { if (SetProperty(ref _overlayOpacity, value)) RequestRender(); } }

        private string? _selectedOverlayPlanLabel;
        public string? SelectedOverlayPlanLabel { get => _selectedOverlayPlanLabel; set { if (SetProperty(ref _selectedOverlayPlanLabel, value)) RequestRender(); } }

        public ObservableCollection<string> OverlayPlanOptions { get; } = new ObservableCollection<string>();

        private WriteableBitmap _overlayImageSource = null!;
        public WriteableBitmap OverlayImageSource { get => _overlayImageSource; set => SetProperty(ref _overlayImageSource, value); }

        // ════════════════════════════════════════════════════════
        // DVH DISPLAY
        // ════════════════════════════════════════════════════════

        private bool _showPhysicalDVH = true;
        public bool ShowPhysicalDVH { get => _showPhysicalDVH; set { if (SetProperty(ref _showPhysicalDVH, value)) UpdatePlotVisibility(); } }

        private bool _showEQD2DVH = true;
        public bool ShowEQD2DVH { get => _showEQD2DVH; set { if (SetProperty(ref _showEQD2DVH, value)) UpdatePlotVisibility(); } }

        public PlotModel PlotModel { get; private set; } = null!;
        public ObservableCollection<DVHSummary> SummaryData { get; } = new ObservableCollection<DVHSummary>();
        public ObservableCollection<StructureAlphaBetaItem> StructureSettings { get; } = new ObservableCollection<StructureAlphaBetaItem>();
    }
}
