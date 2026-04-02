using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace EQD2Viewer.Core.Models
{
    /// <summary>
    /// Determines how isodose thresholds are interpreted for display.
    /// </summary>
    public enum IsodoseMode
    {
        /// <summary>
        /// Thresholds as percentage of a reference dose (Eclipse-style).
        /// Typical for single-plan viewing where prescription dose is the reference.
        /// Example: 95% of 50 Gy = 47.5 Gy threshold.
        /// </summary>
        Relative,

        /// <summary>
        /// Thresholds as absolute Gy values (no reference dose needed).
        /// Used for EQD2 summation where clinical tolerances are defined in absolute Gy
        /// (e.g., spinal cord 45 Gy EQD2, brainstem 50 Gy EQD2).
        /// </summary>
        Absolute
    }

    /// <summary>
    /// Display unit for isodose level labels within <see cref="IsodoseMode.Relative"/> mode.
    /// Controls whether the label shows "95%" or "47.5 Gy".
    /// </summary>
    public enum IsodoseUnit
    {
        /// <summary>Show as percentage of reference dose (e.g., "95%").</summary>
        Percent,
        /// <summary>Show as absolute Gy computed from fraction × reference (e.g., "47.5 Gy").</summary>
        Gy
    }

    /// <summary>
    /// Represents a single isodose level with dual-mode threshold support, color, and visibility.
    /// 
    /// In <see cref="IsodoseMode.Relative"/>: threshold = <see cref="Fraction"/> × referenceDose.
    /// In <see cref="IsodoseMode.Absolute"/>: threshold = <see cref="AbsoluteDoseGy"/> directly.
    /// 
    /// Both modes share the same color, visibility, and alpha settings.
    /// Implements <see cref="INotifyPropertyChanged"/> for WPF data binding in the isodose DataGrid.
    /// </summary>
    public class IsodoseLevel : INotifyPropertyChanged
    {
        private double _fraction;
        private double _absoluteDoseGy;
        private string _label;
        private uint _color;
        private byte _alpha;
        private bool _isVisible = true;

        /// <summary>
        /// Threshold as fraction of reference dose (e.g., 1.10 = 110%, 0.50 = 50%).
        /// Used in <see cref="IsodoseMode.Relative"/> mode.
        /// Range: typically 0.05–1.20. Values above 1.0 represent hot spots.
        /// </summary>
        public double Fraction
        {
            get => _fraction;
            set { _fraction = value; OnPropertyChanged(); OnPropertyChanged(nameof(MediaColor)); }
        }

        /// <summary>
        /// Threshold as absolute dose in Gy.
        /// Used in <see cref="IsodoseMode.Absolute"/> mode (EQD2 summation re-irradiation assessment).
        /// Range: typically 5–80 Gy for clinical tolerances.
        /// </summary>
        public double AbsoluteDoseGy
        {
            get => _absoluteDoseGy;
            set { _absoluteDoseGy = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Display label shown in the isodose table (e.g., "110%", "45.0 Gy", "50%").
        /// Updated by the ViewModel when isodose mode, display unit, or reference dose changes.
        /// </summary>
        public string Label
        {
            get => _label;
            set { _label = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Isodose line/fill color as packed ARGB uint (0xAARRGGBB format).
        /// The alpha channel in <see cref="Color"/> is typically 0xFF (opaque);
        /// actual overlay transparency comes from the separate <see cref="Alpha"/> property.
        /// </summary>
        public uint Color
        {
            get => _color;
            set { _color = value; OnPropertyChanged(); OnPropertyChanged(nameof(MediaColor)); }
        }

        /// <summary>
        /// Overlay alpha for Fill display mode (0 = transparent, 255 = opaque).
        /// Line mode always renders at full opacity regardless of this value.
        /// Default: 140 (~55% opacity) for subtle fill overlay.
        /// </summary>
        public byte Alpha
        {
            get => _alpha;
            set { _alpha = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether this isodose level is rendered on the dose overlay.
        /// Toggled via checkbox in the isodose level DataGrid.
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set { _isVisible = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// WPF-bindable <see cref="System.Windows.Media.Color"/> for UI display
        /// (color swatch in DataGrid, color picker). Always fully opaque (A=255).
        /// </summary>
        public Color MediaColor => System.Windows.Media.Color.FromArgb(
            255,
            (byte)((_color >> 16) & 0xFF),
            (byte)((_color >> 8) & 0xFF),
            (byte)(_color & 0xFF));

        /// <summary>
        /// Creates an isodose level for relative (percentage) mode.
        /// </summary>
        /// <param name="fraction">Threshold as fraction of reference dose (e.g., 0.95 = 95%).</param>
        /// <param name="label">Display label (e.g., "95%").</param>
        /// <param name="color">ARGB color as uint (e.g., 0xFFFF0000 for red).</param>
        /// <param name="alpha">Fill overlay opacity (0–255). Default: 140.</param>
        public IsodoseLevel(double fraction, string label, uint color, byte alpha = 140)
        {
            _fraction = fraction;
            _absoluteDoseGy = 0;
            _label = label;
            _color = color;
            _alpha = alpha;
        }

        /// <summary>
        /// Creates an isodose level for absolute (Gy) mode.
        /// </summary>
        /// <param name="fraction">Relative fraction (can be 0 when only absolute is used).</param>
        /// <param name="absoluteGy">Absolute dose threshold in Gy.</param>
        /// <param name="label">Display label (e.g., "45 Gy").</param>
        /// <param name="color">ARGB color as uint.</param>
        /// <param name="alpha">Fill overlay opacity (0–255). Default: 140.</param>
        public IsodoseLevel(double fraction, double absoluteGy, string label, uint color, byte alpha = 140)
        {
            _fraction = fraction;
            _absoluteDoseGy = absoluteGy;
            _label = label;
            _color = color;
            _alpha = alpha;
        }

        // =================================================================
        // RELATIVE MODE PRESETS (single-plan viewing)
        // =================================================================

        /// <summary>
        /// Eclipse-style 10-level default isodose set.
        /// Covers hot spots (110%) down to low-dose spread (10%).
        /// Suitable for conventional fractionation plan review.
        /// </summary>
        public static IsodoseLevel[] GetEclipseDefaults()
        {
            return new[]
            {
                new IsodoseLevel(1.10, "110%", 0xFFFF0000, 160),   // Red — hot spot warning
                new IsodoseLevel(1.05, "105%", 0xFFFF4400, 150),   // Orange-red
                new IsodoseLevel(1.00, "100%", 0xFFFF8800, 140),   // Orange — prescription dose
                new IsodoseLevel(0.95, "95%",  0xFFFFFF00, 130),   // Yellow — target coverage
                new IsodoseLevel(0.90, "90%",  0xFF00FF00, 120),   // Green
                new IsodoseLevel(0.80, "80%",  0xFF00FFFF, 110),   // Cyan
                new IsodoseLevel(0.70, "70%",  0xFF0088FF, 100),   // Light blue
                new IsodoseLevel(0.50, "50%",  0xFF0000FF, 90),    // Blue — half prescription
                new IsodoseLevel(0.30, "30%",  0xFF8800FF, 80),    // Purple
                new IsodoseLevel(0.10, "10%",  0xFFFF00FF, 70),    // Magenta — low dose
            };
        }

        /// <summary>
        /// Compact 4-level set for quick plan review.
        /// Shows hot spots, prescription, target coverage, and half-prescription.
        /// </summary>
        public static IsodoseLevel[] GetDefaults()
        {
            return new[]
            {
                new IsodoseLevel(1.05, "105%", 0xFFFF0000, 140),
                new IsodoseLevel(1.00, "100%", 0xFFFF8800, 130),
                new IsodoseLevel(0.95, "95%",  0xFFFFFF00, 120),
                new IsodoseLevel(0.50, "50%",  0xFF0000FF, 100),
            };
        }

        /// <summary>
        /// Minimal 3-level set: hot spot, target boundary, and low dose.
        /// </summary>
        public static IsodoseLevel[] GetMinimalSet()
        {
            return new[]
            {
                new IsodoseLevel(1.05, "105%", 0xFFFF0000, 140),
                new IsodoseLevel(0.95, "95%",  0xFFFFFF00, 120),
                new IsodoseLevel(0.50, "50%",  0xFF0000FF, 100),
            };
        }

        // =================================================================
        // ABSOLUTE MODE PRESETS (EQD2 summation / re-irradiation)
        // =================================================================

        /// <summary>
        /// Re-irradiation assessment: OAR tolerance thresholds in EQD2 Gy (α/β = 3).
        /// Covers typical constraints:
        ///   60 Gy — critical overdose
        ///   50 Gy — brainstem tolerance
        ///   45 Gy — spinal cord tolerance (conventional)
        ///   30 Gy — brachial plexus partial volume
        /// </summary>
        public static IsodoseLevel[] GetReIrradiationPreset()
        {
            return new[]
            {
                new IsodoseLevel(0, 60, "60 Gy", 0xFFFF0000, 160),
                new IsodoseLevel(0, 50, "50 Gy", 0xFFFF4400, 150),
                new IsodoseLevel(0, 45, "45 Gy", 0xFFFF8800, 140),
                new IsodoseLevel(0, 40, "40 Gy", 0xFFFFFF00, 130),
                new IsodoseLevel(0, 35, "35 Gy", 0xFF88FF00, 120),
                new IsodoseLevel(0, 30, "30 Gy", 0xFF00FF00, 110),
                new IsodoseLevel(0, 20, "20 Gy", 0xFF00BBFF, 100),
                new IsodoseLevel(0, 10, "10 Gy", 0xFF0000FF, 80),
            };
        }

        /// <summary>
        /// Stereotactic re-irradiation: higher dose range for SBRT/SRS cases.
        /// Extends to 80 Gy EQD2 for high-dose targets.
        /// </summary>
        public static IsodoseLevel[] GetStereotacticPreset()
        {
            return new[]
            {
                new IsodoseLevel(0, 80, "80 Gy", 0xFFFF0000, 160),
                new IsodoseLevel(0, 60, "60 Gy", 0xFFFF4400, 150),
                new IsodoseLevel(0, 50, "50 Gy", 0xFFFF8800, 140),
                new IsodoseLevel(0, 40, "40 Gy", 0xFFFFFF00, 130),
                new IsodoseLevel(0, 30, "30 Gy", 0xFF00FF00, 110),
                new IsodoseLevel(0, 20, "20 Gy", 0xFF00BBFF, 100),
                new IsodoseLevel(0, 12, "12 Gy", 0xFF0000FF, 80),
            };
        }

        /// <summary>
        /// Palliative re-irradiation: lower dose range for palliative retreatment.
        /// Starts at 45 Gy with finer low-dose resolution.
        /// </summary>
        public static IsodoseLevel[] GetPalliativePreset()
        {
            return new[]
            {
                new IsodoseLevel(0, 45, "45 Gy", 0xFFFF0000, 160),
                new IsodoseLevel(0, 40, "40 Gy", 0xFFFF4400, 150),
                new IsodoseLevel(0, 35, "35 Gy", 0xFFFF8800, 140),
                new IsodoseLevel(0, 30, "30 Gy", 0xFFFFFF00, 130),
                new IsodoseLevel(0, 25, "25 Gy", 0xFF00FF00, 120),
                new IsodoseLevel(0, 20, "20 Gy", 0xFF00FFFF, 110),
                new IsodoseLevel(0, 10, "10 Gy", 0xFF0088FF, 90),
                new IsodoseLevel(0, 5,  "5 Gy",  0xFF0000FF, 70),
            };
        }

        // =================================================================
        // COLOR PALETTE
        // =================================================================

        /// <summary>
        /// Predefined 16-color palette for the isodose color picker.
        /// Cycles through on click. Uses high-saturation colors for visibility
        /// against both light and dark CT backgrounds.
        /// </summary>
        public static uint[] ColorPalette => new uint[]
        {
            0xFFFF0000, // Red
            0xFFFF4400, // Orange-red
            0xFFFF8800, // Orange
            0xFFFFBB00, // Gold
            0xFFFFFF00, // Yellow
            0xFF88FF00, // Yellow-green
            0xFF00FF00, // Green
            0xFF00FF88, // Spring green
            0xFF00FFFF, // Cyan
            0xFF00BBFF, // Light blue
            0xFF0088FF, // Sky blue
            0xFF0000FF, // Blue
            0xFF4400FF, // Indigo
            0xFF8800FF, // Purple
            0xFFFF00FF, // Magenta
            0xFFFF0088, // Hot pink
        };

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}