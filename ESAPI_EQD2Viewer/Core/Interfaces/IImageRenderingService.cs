using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using VMS.TPS.Common.Model.API;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.Core.Interfaces
{
    public interface IImageRenderingService : IDisposable
    {
        void Initialize(int width, int height);

        /// <summary>
        /// Preloads CT and dose voxel data into memory caches.
        /// </summary>
        void PreloadData(Image ctImage, Dose dose, double prescriptionDoseGy);

        void RenderCtImage(Image ctImage, WriteableBitmap targetBitmap, int currentSlice,
            double windowLevel, double windowWidth);

        /// <summary>
        /// Renders dose overlay as bitmap (Fill and Colorwash modes).
        /// In Line mode, clears the bitmap only (vector contours are separate).
        /// </summary>
        string RenderDoseImage(Image ctImage, Dose dose, WriteableBitmap targetBitmap, int currentSlice,
            double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            DoseDisplayMode displayMode = DoseDisplayMode.Line,
            double colorwashOpacity = 0.5, double colorwashMinPercent = 0.1,
            EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Generates vector isodose contours using marching squares.
        /// Returns one IsodoseContourData per visible level, each containing
        /// a frozen StreamGeometry that scales perfectly at any zoom level.
        /// </summary>
        /// <returns>Contour data list and status text</returns>
        ContourGenerationResult GenerateVectorContours(Image ctImage, Dose dose, int currentSlice,
            double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Gets the dose in Gy at a specific CT pixel coordinate on the current slice.
        /// Returns NaN if out of dose grid bounds.
        /// </summary>
        double GetDoseAtPixel(Image ctImage, Dose dose, int currentSlice, int pixelX, int pixelY,
            EQD2Settings eqd2Settings = null);
    }

    /// <summary>
    /// Result of vector contour generation: contour geometries + status text.
    /// </summary>
    public class ContourGenerationResult
    {
        public List<IsodoseContourData> Contours { get; set; }
        public string StatusText { get; set; }
    }
}