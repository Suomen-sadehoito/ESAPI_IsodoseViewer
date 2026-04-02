using CommunityToolkit.Mvvm.ComponentModel;
using EQD2Viewer.Core.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ESAPI_EQD2Viewer.UI.ViewModels
{
    /// <summary>
    /// Manages isodose level configuration, dose display mode, and EQD2 display settings.
    ///
    /// Extracted from MainViewModel to encapsulate all dose overlay concerns:
    ///   - Isodose level presets, add/remove, color cycling
    ///   - Display mode (Line / Fill / Colorwash)
    ///   - EQD2 on/off, display ?/? slider, fraction count
    ///   - Isodose mode (Relative % vs Absolute Gy)
    ///
    /// Communicates with other ViewModels through <see cref="ViewModelEventBus"/>:
    ///   - Publishes: RenderRequested, EQD2EnabledChanged, DisplayAlphaBetaChanged, FractionsChanged
    ///   - Subscribes: SummationStateChanged (to switch isodose presets on summation start)
    /// </summary>
    internal class DoseOverlayViewModel : ObservableObject
    {
        private readonly ViewModelEventBus _bus;
     private readonly double _prescriptionGy;
        private readonly double _planNormalization;
        private readonly int _initialFractions;

        public DoseOverlayViewModel(ViewModelEventBus bus, double prescriptionGy,
    double planNormalization, int initialFractions)
        {
      _bus = bus;
            _prescriptionGy = prescriptionGy;
            _planNormalization = planNormalization;
    _initialFractions = initialFractions;
       _numberOfFractions = initialFractions;

var defaults = IsodoseLevel.GetEclipseDefaults();
            IsodoseLevels = new ObservableCollection<IsodoseLevel>(defaults);
          _isodoseLevelArray = defaults;
            WireIsodoseLevelEvents();

   _bus.SummationStateChanged += OnSummationStateChanged;
    }

      // ????????????????????????????????????????????????????????
        // ISODOSE LEVELS
     // ????????????????????????????????????????????????????????

        public ObservableCollection<IsodoseLevel> IsodoseLevels { get; }
    internal IsodoseLevel[] _isodoseLevelArray;

        private string _isodosePresetName = "Eclipse (10)";
        public string IsodosePresetName
        {
         get => _isodosePresetName;
    set => SetProperty(ref _isodosePresetName, value);
    }

    internal void RebuildIsodoseArray()
{
 _isodoseLevelArray = new IsodoseLevel[IsodoseLevels.Count];
  IsodoseLevels.CopyTo(_isodoseLevelArray, 0);
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
    _bus.RequestRender();
}
        }

        // ????????????????????????????????????????????????????????
        // ISODOSE MODE & UNIT
        // ????????????????????????????????????????????????????????

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
      _bus.RequestRender();
             }
  }
        }

        public bool IsRelativeMode
        {
         get => _isodoseMode == IsodoseMode.Relative;
 set { if (value) CurrentIsodoseMode = IsodoseMode.Relative; }
        }

        public bool IsAbsoluteMode
        {
 get => _isodoseMode == IsodoseMode.Absolute;
        set { if (value) CurrentIsodoseMode = IsodoseMode.Absolute; }
        }

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

        public string IsodoseColumnHeader =>
       _isodoseMode == IsodoseMode.Absolute ? "Dose (Gy)" :
            _isodoseUnit == IsodoseUnit.Gy ? "Dose (Gy)" : "Level %";

 // ????????????????????????????????????????????????????????
        // REFERENCE DOSE
      // ????????????????????????????????????????????????????????

        public double ReferenceDoseGy
        {
            get
            {
            double norm = _planNormalization;
    if (double.IsNaN(norm) || norm <= 0) norm = 100.0;
        else if (norm < DomainConstants.NormalizationFractionThreshold) norm *= 100.0;

       double refGy = _prescriptionGy * (norm / 100.0);
      return refGy < DomainConstants.MinReferenceDoseGy ? _prescriptionGy : refGy;
            }
        }

        // ????????????????????????????????????????????????????????
        // DOSE DISPLAY MODE
        // ????????????????????????????????????????????????????????

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
    _bus.RequestRender();
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
        set { if (SetProperty(ref _colorwashOpacity, value)) _bus.RequestRender(); }
        }

        private double _colorwashMinPercent = 0.10;
        public double ColorwashMinPercent
    {
            get => _colorwashMinPercent;
  set { if (SetProperty(ref _colorwashMinPercent, value)) _bus.RequestRender(); }
        }

        // ????????????????????????????????????????????????????????
        // EQD2 DISPLAY SETTINGS
        // ????????????????????????????????????????????????????????

        private bool _isEQD2Enabled;
     public bool IsEQD2Enabled
        {
            get => _isEQD2Enabled;
            set
  {
           if (SetProperty(ref _isEQD2Enabled, value))
                {
 _bus.RequestRender();
    _bus.OnEQD2EnabledChanged(value);
    }
            }
}

        private double _displayAlphaBeta = 3.0;
        public double DisplayAlphaBeta
        {
            get => _displayAlphaBeta;
 set
            {
    if (value <= 0) value = 0.5;
       if (SetProperty(ref _displayAlphaBeta, value))
       {
     _bus.RequestRender();
      _bus.OnDisplayAlphaBetaChanged(value);
      }
    }
        }

        private int _numberOfFractions = 1;
        public int NumberOfFractions
        {
         get => _numberOfFractions;
    set
            {
  if (value < 1) value = 1;
            if (SetProperty(ref _numberOfFractions, value))
 {
        _bus.RequestRender();
           _bus.OnFractionsChanged(value);
              }
            }
        }

    private string _summationAlphaBetaLabel = "";
        public string SummationAlphaBetaLabel
        {
     get => _summationAlphaBetaLabel;
            set => SetProperty(ref _summationAlphaBetaLabel, value);
      }

        // ????????????????????????????????????????????????????????
        // HELPERS
        // ????????????????????????????????????????????????????????

internal void UpdateIsodoseLabels()
        {
            if (_isodoseMode == IsodoseMode.Absolute)
  {
 foreach (var level in IsodoseLevels)
   level.Label = $"{level.AbsoluteDoseGy:F1} Gy";
     }
            else
  {
     double refGy = ReferenceDoseGy;
  foreach (var level in IsodoseLevels)
          level.Label = _isodoseUnit == IsodoseUnit.Gy
   ? $"{(level.Fraction * refGy):F1} Gy"
          : $"{(level.Fraction * 100):F0}%";
       }
        }

      /// <summary>
        /// Resolves the absolute Gy threshold for a given isodose level,
        /// accounting for the current isodose mode (relative vs absolute).
   /// </summary>
        public double GetThresholdGy(IsodoseLevel level, double referenceDoseGy)
        {
  return _isodoseMode == IsodoseMode.Absolute
           ? level.AbsoluteDoseGy
            : referenceDoseGy * level.Fraction;
        }

        /// <summary>
    /// Loads a named isodose preset, replacing all current levels.
        /// </summary>
        public void LoadPreset(string preset)
        {
            IsodoseLevel[] levels;
    switch (preset)
        {
case "Eclipse":
    levels = IsodoseLevel.GetEclipseDefaults();
  IsodosePresetName = "Eclipse (10)";
      CurrentIsodoseMode = IsodoseMode.Relative;
               break;
      case "Minimal":
              levels = IsodoseLevel.GetMinimalSet();
        IsodosePresetName = "Minimal (3)";
            CurrentIsodoseMode = IsodoseMode.Relative;
        break;
   case "Default":
       levels = IsodoseLevel.GetDefaults();
           IsodosePresetName = "Default (4)";
          CurrentIsodoseMode = IsodoseMode.Relative;
            break;
    case "ReIrradiation":
        levels = IsodoseLevel.GetReIrradiationPreset();
     IsodosePresetName = "Re-irradiation";
    CurrentIsodoseMode = IsodoseMode.Absolute;
          break;
     case "Stereotactic":
      levels = IsodoseLevel.GetStereotacticPreset();
   IsodosePresetName = "Stereotactic";
      CurrentIsodoseMode = IsodoseMode.Absolute;
        break;
           case "Palliative":
       levels = IsodoseLevel.GetPalliativePreset();
       IsodosePresetName = "Palliative";
      CurrentIsodoseMode = IsodoseMode.Absolute;
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
            _bus.RequestRender();
        }

        /// <summary>
        /// Adds a new isodose level with sensible defaults for the current mode.
        /// </summary>
      public void AddLevel()
        {
  var newLevel = _isodoseMode == IsodoseMode.Absolute
                ? new IsodoseLevel(0, 25, "25.0 Gy", 0xFF9900FF)
       : new IsodoseLevel(0.60, "60%", 0xFF9900FF);
      newLevel.PropertyChanged += OnIsodoseLevelChanged;
            IsodoseLevels.Add(newLevel);
            RebuildIsodoseArray();
      UpdateIsodoseLabels();
    _bus.RequestRender();
        }

        /// <summary>
        /// Removes a specific isodose level.
        /// </summary>
    public void RemoveLevel(IsodoseLevel level)
        {
     if (level != null && IsodoseLevels.Contains(level))
       {
          level.PropertyChanged -= OnIsodoseLevelChanged;
       IsodoseLevels.Remove(level);
   RebuildIsodoseArray();
        _bus.RequestRender();
        }
        }

        /// <summary>
        /// Sets all isodose levels to visible or hidden.
     /// </summary>
  public void ToggleAllVisibility(bool visible)
        {
   foreach (var level in IsodoseLevels)
                level.IsVisible = visible;
        }

  // ????????????????????????????????????????????????????????
      // EVENT HANDLERS
        // ????????????????????????????????????????????????????????

        private void OnSummationStateChanged(SummationStateChangedArgs args)
        {
      if (args.IsActive)
   {
     if (_isodoseMode != IsodoseMode.Absolute)
     LoadPreset("ReIrradiation");
            }
     }
    }
}
