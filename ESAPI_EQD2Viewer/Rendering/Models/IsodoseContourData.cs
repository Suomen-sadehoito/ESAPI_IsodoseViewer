using System.Windows.Media;

namespace EQD2Viewer.Core.Models
{
    public class IsodoseContourData
    {
        public StreamGeometry Geometry { get; set; }
        public SolidColorBrush Stroke { get; set; }
        public double StrokeThickness { get; set; } = 1.0;
    }
}
