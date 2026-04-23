using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;

namespace EQD2Viewer.Services.Rendering
{
    /// <summary>
    /// WPF-specific rendering service interface.
    /// Operates on WriteableBitmap and produces WPF StreamGeometry contours.
    /// 
    /// This interface lives in the Services layer because it bridges
    /// pure domain data (from Core) with WPF rendering primitives.
    /// 
    /// All data inputs come from Core types (VolumeData, DoseVolumeData, etc.)
    /// -- this is the adapter boundary between pure domain data and WPF rendering.
    /// </summary>
    public interface IImageRenderingService : IDisposable
    {
        void Initialize(int width, int height);

        void PreloadData(VolumeData ctImage, DoseVolumeData dose);

        void RenderCtImage(WriteableBitmap targetBitmap, int currentSlice, double windowLevel, double windowWidth);

        string RenderDoseImage(WriteableBitmap targetBitmap, int currentSlice, double planTotalDoseGy,
       double planNormalization, IsodoseLevel[] levels, DoseDisplayMode displayMode = DoseDisplayMode.Line,
            double colorwashOpacity = 0.5, double colorwashMinPercent = 0.1, EQD2Settings? eqd2Settings = null);

        ContourGenerationResult GenerateVectorContours(int currentSlice, double planTotalDoseGy,
    double planNormalization, IsodoseLevel[] levels, EQD2Settings? eqd2Settings = null);

        double GetDoseAtPixel(int currentSlice, int pixelX, int pixelY, EQD2Settings? eqd2Settings = null);

        List<StructureContourData> GenerateStructureContours(int currentSlice, IEnumerable<StructureData> structures);

        (double windowLevel, double windowWidth) ComputeAutoWindow(int slice);
    }

    public class ContourGenerationResult
    {
        public List<IsodoseContourData> Contours { get; set; } = new List<IsodoseContourData>();
        public string? StatusText { get; set; }
    }
}
