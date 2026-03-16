using System.Windows.Media;

namespace ESAPI_EQD2Viewer.Core.Models
{
    /// <summary>
    /// One isodose level's vector contour data for WPF rendering.
    /// Contains a frozen StreamGeometry (all polylines for this level)
    /// and stroke properties. Bound directly to WPF Path elements.
    /// </summary>
    public class IsodoseContourData
    {
        /// <summary>
        /// All contour polylines for this isodose level, compiled into
        /// a single frozen StreamGeometry for maximum WPF rendering performance.
        /// </summary>
        public StreamGeometry Geometry { get; set; }

        /// <summary>
        /// Stroke color brush (matches the isodose level color).
        /// </summary>
        public SolidColorBrush Stroke { get; set; }

        /// <summary>
        /// Line thickness in CT pixel units.
        /// Scales with zoom (thicker when zoomed in, thinner when zoomed out).
        /// </summary>
        public double StrokeThickness { get; set; } = 1.0;
    }
}