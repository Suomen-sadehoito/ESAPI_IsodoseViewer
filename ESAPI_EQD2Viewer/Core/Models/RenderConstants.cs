namespace ESAPI_EQD2Viewer.Core.Models
{
    /// <summary>
    /// Constants related to UI rendering and interaction logic.
    /// (Domain and physics constants have been moved to DomainConstants.cs)
    /// </summary>
    public static class RenderConstants
    {
        // ── Colorwash ──
        /// <summary>Maximum dose as fraction of reference dose for colorwash upper bound.</summary>
        public const double ColorwashMaxFraction = 1.15;

        // ── Registration overlay ──
        /// <summary>Checkerboard block size in pixels for registration verification overlay.</summary>
        public const int CheckerboardBlockSize = 32;

        // ── Rendering ──
        /// <summary>Minimum zoom factor for image viewer.</summary>
        public const double MinZoom = 0.1;
        /// <summary>Maximum zoom factor for image viewer.</summary>
        public const double MaxZoom = 10.0;
        /// <summary>Zoom step multiplier per scroll tick.</summary>
        public const double ZoomStepFactor = 1.1;
        /// <summary>Windowing mouse sensitivity (pixels to HU).</summary>
        public const double WindowingSensitivity = 2.0;
        /// <summary>Minimum frame interval in ms for windowing throttle.</summary>
        public const double WindowingThrottleMs = 33.0;

        // ── Structure rendering ──
        /// <summary>Default stroke thickness for structure contours in CT pixel units.</summary>
        public const double StructureContourThickness = 1.5;

        // ── Summation & UI ──
        /// <summary>Progress report interval (every Nth slice).</summary>
        public const int SummationProgressInterval = 4;
        /// <summary>Debounce delay in ms for α/β slider during active summation.</summary>
        public const int AlphaBetaDebounceMs = 500;
    }
}