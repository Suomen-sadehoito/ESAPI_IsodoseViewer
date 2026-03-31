using System;
using System.Collections.Generic;
using System.Windows.Media.Imaging;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Models;
using VMS.TPS.Common.Model.API;

namespace ESAPI_EQD2Viewer.Core.Interfaces
{
    public interface IImageRenderingService : IDisposable
    {
        void Initialize(int width, int height);

        // ================================================================
        // ESAPI-based Methods
        // ================================================================

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
        /// </summary>
        ContourGenerationResult GenerateVectorContours(Image ctImage, Dose dose, int currentSlice,
            double planTotalDoseGy, double planNormalization, IsodoseLevel[] levels,
            EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Gets the dose in Gy at a specific CT pixel coordinate on the current slice.
        /// Returns NaN if out of dose grid bounds.
        /// </summary>
        double GetDoseAtPixel(Image ctImage, Dose dose, int currentSlice, int pixelX, int pixelY,
            EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Generates structure contour geometries for the current slice.
        /// Converts ESAPI structure contours to WPF StreamGeometry for overlay rendering.
        /// </summary>
        List<StructureContourData> GenerateStructureContours(Image ctImage, int currentSlice,
            IEnumerable<Structure> structures);

        // ================================================================
        // ESAPI-Free (Clean Architecture / DTO) Methods
        // ================================================================

        /// <summary>
        /// Preloads CT and dose data from Clean Architecture DTOs.
        /// Used by DevRunner and future test infrastructure.
        /// </summary>
        void PreloadData(VolumeData ctImage, DoseVolumeData dose);

        /// <summary>
        /// Renders CT image using cached data only. No ESAPI parameters.
        /// </summary>
        void RenderCtImage(WriteableBitmap targetBitmap, int currentSlice, double windowLevel, double windowWidth);

        /// <summary>
        /// Renders dose using cached geometry. No ESAPI parameters.
        /// </summary>
        string RenderDoseImage(WriteableBitmap targetBitmap, int currentSlice, double planTotalDoseGy,
            double planNormalization, IsodoseLevel[] levels, DoseDisplayMode displayMode = DoseDisplayMode.Line,
            double colorwashOpacity = 0.5, double colorwashMinPercent = 0.1, EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Generates vector contours using cached geometry. No ESAPI parameters.
        /// </summary>
        ContourGenerationResult GenerateVectorContours(int currentSlice, double planTotalDoseGy,
            double planNormalization, IsodoseLevel[] levels, EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Dose readout using cached geometry. No ESAPI parameters.
        /// </summary>
        double GetDoseAtPixel(int currentSlice, int pixelX, int pixelY, EQD2Settings eqd2Settings = null);

        /// <summary>
        /// Structure contours from StructureData DTOs. No ESAPI Structure objects.
        /// </summary>
        List<StructureContourData> GenerateStructureContours(int currentSlice, IEnumerable<StructureData> structures);
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