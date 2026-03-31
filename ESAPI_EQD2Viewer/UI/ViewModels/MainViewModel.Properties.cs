using CommunityToolkit.Mvvm.ComponentModel;
using ESAPI_EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Models;
using OxyPlot;
using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    public partial class MainViewModel
    {
        // ═══════════════════════════════════════════
        // PATIENT & PLAN DISPLAY
        // ═══════════════════════════════════════════

        public string PatientDisplayName
        {
            get
            {
                if (_isSnapshotMode)
                {
                    var p = _snapshot.Patient;
                    if (p == null) return "No patient";
                    string name = $"{p.LastName}, {p.FirstName}";
                    if (!string.IsNullOrEmpty(p.Id)) name += $"  ({p.Id})";
                    return name;
                }

                if (_context?.Patient == null) return "No patient";
                var patient = _context.Patient;
                string n = $"{patient.LastName}, {patient.FirstName}";
                if (!string.IsNullOrEmpty(patient.Id)) n += $"  ({patient.Id})";
                return n;
            }
        }

        public string PlanDisplayLabel
        {
            get
            {
                if (_isSnapshotMode)
                {
                    var plan = _snapshot.ActivePlan;
                    if (plan == null) return "No plan";
                    return string.IsNullOrEmpty(plan.CourseId) ? plan.Id : $"{plan.CourseId} / {plan.Id}";
                }

                if (_plan == null) return "No plan";
                string course = _plan.Course?.Id ?? "";
                return string.IsNullOrEmpty(course) ? _plan.Id : $"{course} / {_plan.Id}";
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
                int fx = 0;
                if (_isSnapshotMode)
                {
                    fx = _snapshot?.ActivePlan?.NumberOfFractions ?? 0;
                }
                else
                {
                    fx = _plan?.NumberOfFractions ?? 0;
                }

                if (fx <= 0) return "";
                double gy = GetPrescriptionGy();
                return $"{fx} fx × {(fx > 0 ? gy / fx : 0):F2} Gy";
            }
        }

        // ═══════════════════════════════════════════
        // CT IMAGE & SLICE
        // ═══════════════════════════════════════════

        private WriteableBitmap _ctImageSource;
        public WriteableBitmap CtImageSource { get => _ctImageSource; set => SetProperty(ref _ctImageSource, value); }

        private WriteableBitmap _doseImageSource;
        public WriteableBitmap DoseImageSource { get => _doseImageSource; set => SetProperty(ref _doseImageSource, value); }

        private int _currentSlice;
        public int CurrentSlice { get => _currentSlice; set { if (SetProperty(ref _currentSlice, value)) RequestRender(); } }

        private int _maxSlice;
        public int MaxSlice { get => _maxSlice; set => SetProperty(ref _maxSlice, value); }

        private double _windowLevel;
        public double WindowLevel { get => _windowLevel; set { if (SetProperty(ref _windowLevel, value)) RequestRender(); } }

        private double _windowWidth;
        public double WindowWidth { get => _windowWidth; set { if (SetProperty(ref _windowWidth, value)) RequestRender(); } }

        private string _statusText;
        public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

        // ═══════════════════════════════════════════
        // ISODOSE CONTOURS
        // ═══════════════════════════════════════════

        private ObservableCollection<IsodoseContourData> _contourLines;
        public ObservableCollection<IsodoseContourData> ContourLines { get => _contourLines; set => SetProperty(ref _contourLines, value); }

        // ═══════════════════════════════════════════
        // STRUCTURE CONTOURS
        // ═══════════════════════════════════════════

        private ObservableCollection<StructureContourData> _structureContourLines;
        public ObservableCollection<StructureContourData> StructureContourLines { get => _structureContourLines; set => SetProperty(ref _structureContourLines, value); }

        private bool _showStructureContours;
        public bool ShowStructureContours { get => _showStructureContours; set { if (SetProperty(ref _showStructureContours, value)) RequestRender(); } }

        // ═══════════════════════════════════════════
        // DOSE CURSOR
        // ═══════════════════════════════════════════

        private string _doseCursorText = "";
        public string DoseCursorText { get => _doseCursorText; set => SetProperty(ref _doseCursorText, value); }

        // ═══════════════════════════════════════════
        // DOSE DISPLAY MODE
        // ═══════════════════════════════════════════

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

        public bool IsLineMode { get => _doseDisplayMode == DoseDisplayMode.Line; set { if (value) DoseDisplayMode = DoseDisplayMode.Line; } }
        public bool IsFillMode { get => _doseDisplayMode == DoseDisplayMode.Fill; set { if (value) DoseDisplayMode = DoseDisplayMode.Fill; } }
        public bool IsColorwashMode { get => _doseDisplayMode == DoseDisplayMode.Colorwash; set { if (value) DoseDisplayMode = DoseDisplayMode.Colorwash; } }

        private double _colorwashOpacity = 0.45;
        public double ColorwashOpacity { get => _colorwashOpacity; set { if (SetProperty(ref _colorwashOpacity, value)) RequestRender(); } }

        private double _colorwashMinPercent = 0.10;
        public double ColorwashMinPercent { get => _colorwashMinPercent; set { if (SetProperty(ref _colorwashMinPercent, value)) RequestRender(); } }

        // ═══════════════════════════════════════════
        // ISODOSE MODE & UNIT
        // ═══════════════════════════════════════════

        private IsodoseMode _isodoseMode = IsodoseMode.Relative;
        public IsodoseMode CurrentIsodoseMode
        {
            get => _isodoseMode;
            set
            {
                if (SetProperty(ref _isodoseMode, value))
                {
                    OnPropertyChanged(nameof(IsRelativeMode));
                    OnPropertyChanged(nameof(IsAbsoluteMode));
                    OnPropertyChanged(nameof(IsodoseColumnHeader));
                    OnPropertyChanged(nameof(IsRelativeModeSettingsVisible));
                    UpdateIsodoseLabels();
                    RequestRender();
                }
            }
        }

        public bool IsRelativeMode { get => _isodoseMode == IsodoseMode.Relative; set { if (value) CurrentIsodoseMode = IsodoseMode.Relative; } }
        public bool IsAbsoluteMode { get => _isodoseMode == IsodoseMode.Absolute; set { if (value) CurrentIsodoseMode = IsodoseMode.Absolute; } }
        public bool IsRelativeModeSettingsVisible => _isodoseMode == IsodoseMode.Relative;

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

        public bool IsPercentMode { get => _isodoseUnit == IsodoseUnit.Percent; set { if (value) IsodoseUnit = IsodoseUnit.Percent; } }
        public bool IsGyMode { get => _isodoseUnit == IsodoseUnit.Gy; set { if (value) IsodoseUnit = IsodoseUnit.Gy; } }

        public string IsodoseColumnHeader =>
            _isodoseMode == IsodoseMode.Absolute ? "Dose (Gy)" :
            _isodoseUnit == IsodoseUnit.Gy ? "Dose (Gy)" : "Level %";

        public double ReferenceDoseGy
        {
            get
            {
                double prescGy = GetPrescriptionGy();
                double norm = _isSnapshotMode
                    ? (_snapshot?.ActivePlan?.PlanNormalization ?? 100.0)
                    : (_plan?.PlanNormalizationValue ?? 100.0);

                if (double.IsNaN(norm) || norm <= 0) norm = 100.0;
                else if (norm < DomainConstants.NormalizationFractionThreshold) norm *= 100.0;

                double refGy = prescGy * (norm / 100.0);
                return refGy < DomainConstants.MinReferenceDoseGy ? prescGy : refGy;
            }
        }

        public ObservableCollection<IsodoseLevel> IsodoseLevels { get; }
        internal IsodoseLevel[] _isodoseLevelArray;

        private string _isodosePresetName = "Eclipse (10)";
        public string IsodosePresetName { get => _isodosePresetName; set => SetProperty(ref _isodosePresetName, value); }

        internal void UpdateIsodoseLabels()
        {
            if (_isodoseMode == IsodoseMode.Absolute)
                foreach (var level in IsodoseLevels) level.Label = $"{level.AbsoluteDoseGy:F1} Gy";
            else
            {
                double refGy = ReferenceDoseGy;
                foreach (var level in IsodoseLevels)
                    level.Label = _isodoseUnit == IsodoseUnit.Gy ? $"{(level.Fraction * refGy):F1} Gy" : $"{(level.Fraction * 100):F0}%";
            }
        }

        internal double GetThresholdGy(IsodoseLevel level, double referenceDoseGy)
        {
            return _isodoseMode == IsodoseMode.Absolute ? level.AbsoluteDoseGy : referenceDoseGy * level.Fraction;
        }

        // ═══════════════════════════════════════════
        // EQD2 DISPLAY SETTINGS
        // (Controls isodose visualization ONLY.
        //  DVH uses per-structure α/β values.)
        // ═══════════════════════════════════════════

        private bool _isEQD2Enabled;
        public bool IsEQD2Enabled
        {
            get => _isEQD2Enabled;
            set { if (SetProperty(ref _isEQD2Enabled, value)) { RequestRender(); if (_dvhCache.Count > 0) RecalculateAllDVH(); } }
        }

        /// <summary>
        /// α/β ratio for isodose LINE/FILL/COLORWASH visualization only.
        /// Changing this re-renders the current slice; does NOT re-run summation.
        /// For summation: triggers a fast EQD2 recomputation from stored physical doses.
        /// DVH calculations use per-structure α/β from StructureSettings instead.
        /// </summary>
        private double _displayAlphaBeta = 3.0;
        public double DisplayAlphaBeta
        {
            get => _displayAlphaBeta;
            set
            {
                if (value <= 0) value = 0.5; // Prevent invalid input
                if (SetProperty(ref _displayAlphaBeta, value))
                {
                    RequestRender();
                    // Recompute display sum from stored physical doses (fast, no re-summation)
                    RecomputeDisplayEQD2IfActive();
                }
            }
        }

        /// <summary>
        /// Number of fractions for single-plan EQD2 display.
        /// In summation mode, each plan has its own fraction count.
        /// </summary>
        private int _numberOfFractions = 1;
        public int NumberOfFractions
        {
            get => _numberOfFractions;
            set
            {
                if (value < 1) value = 1; // Prevent invalid input
                if (SetProperty(ref _numberOfFractions, value))
                {
                    RequestRender();
                    if (_dvhCache.Count > 0) RecalculateAllDVH();
                }
            }
        }

        /// <summary>
        /// Read-only display of the α/β used in the active summation.
        /// Shown in the UI when summation is active so users know what was used.
        /// </summary>
        private string _summationAlphaBetaLabel = "";
        public string SummationAlphaBetaLabel { get => _summationAlphaBetaLabel; set => SetProperty(ref _summationAlphaBetaLabel, value); }

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

        // ═══════════════════════════════════════════
        // REGISTRATION OVERLAY
        // ═══════════════════════════════════════════

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

        private string _selectedOverlayPlanLabel;
        public string SelectedOverlayPlanLabel { get => _selectedOverlayPlanLabel; set { if (SetProperty(ref _selectedOverlayPlanLabel, value)) RequestRender(); } }

        public ObservableCollection<string> OverlayPlanOptions { get; } = new ObservableCollection<string>();

        private WriteableBitmap _overlayImageSource;
        public WriteableBitmap OverlayImageSource { get => _overlayImageSource; set => SetProperty(ref _overlayImageSource, value); }

        // ═══════════════════════════════════════════
        // DVH DISPLAY
        // ═══════════════════════════════════════════

        private bool _showPhysicalDVH = true;
        public bool ShowPhysicalDVH { get => _showPhysicalDVH; set { if (SetProperty(ref _showPhysicalDVH, value)) UpdatePlotVisibility(); } }

        private bool _showEQD2DVH = true;
        public bool ShowEQD2DVH { get => _showEQD2DVH; set { if (SetProperty(ref _showEQD2DVH, value)) UpdatePlotVisibility(); } }

        public PlotModel PlotModel { get; private set; }
        public ObservableCollection<DVHSummary> SummaryData { get; } = new ObservableCollection<DVHSummary>();
        public ObservableCollection<StructureAlphaBetaItem> StructureSettings { get; } = new ObservableCollection<StructureAlphaBetaItem>();
    }
}