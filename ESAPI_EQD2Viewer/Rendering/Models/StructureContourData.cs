using System.Windows.Media;

namespace EQD2Viewer.Core.Models
{
    /// <summary>
    /// One structure's contour data for WPF rendering on a single CT slice.
    /// Similar to IsodoseContourData but for anatomical structures.
    /// </summary>
    public class StructureContourData
    {
        /// <summary>
        /// All contour polylines for this structure on the current slice,
        /// compiled into a single frozen StreamGeometry.
        /// </summary>
        public StreamGeometry Geometry { get; set; }

        /// <summary>
        /// Stroke color (matches the structure's ESAPI color).
        /// </summary>
        public SolidColorBrush Stroke { get; set; }

        /// <summary>
        /// Line thickness in CT pixel units.
        /// </summary>
        public double StrokeThickness { get; set; } = RenderConstants.StructureContourThickness;

        /// <summary>
        /// Structure ID for legend display.
        /// </summary>
        public string StructureId { get; set; }

        /// <summary>
        /// Optional dashed pattern for specific structure types (e.g. support structures).
        /// Null = solid line.
        /// </summary>
        public DoubleCollection StrokeDashArray { get; set; }
    }
}
