using EQD2Viewer.Core.Data;
using System.Collections.Generic;

namespace EQD2Viewer.Core.Interfaces
{
    /// <summary>
    /// Abstracts on-demand loading of plan dose data for multi-plan summation.
    /// 
    /// Implementations:
    ///   EsapiSummationDataLoader â€” loads from live Eclipse via ESAPI (in EQD2Viewer.Esapi)
    ///   (future) JsonSummationDataLoader â€” loads from fixture files
    /// 
    /// Separates the summation algorithm from ESAPI data access,
    /// allowing SummationService to live in the ESAPI-free project.
    /// </summary>
    public interface ISummationDataLoader
    {
        /// <summary>
        /// Loads dose voxel data for a specific plan identified by course/plan IDs.
        /// Returns null if the plan or dose cannot be found.
        /// </summary>
        SummationPlanDoseData LoadPlanDose(string courseId, string planId, double totalDoseGy);

        /// <summary>
        /// Loads structure contour data from the reference plan's structure set.
        /// Returns contours per structure, used for DVH mask rasterization.
        /// </summary>
        List<StructureData> LoadStructureContours(string courseId, string planId);

        /// <summary>
        /// Finds a registration transform by its ID.
        /// Returns null if no registration is found.
        /// </summary>
        RegistrationData FindRegistration(string registrationId);

        /// <summary>
        /// Finds the Frame of Reference (FOR) for a plan's image.
        /// Returns empty string if not found.
        /// </summary>
        string GetPlanImageFOR(string courseId, string planId);

        /// <summary>
        /// Loads the full CT volume for a specific plan.
        /// Used for deformable image registration (DIR).
        /// </summary>
        VolumeData? LoadCtVolume(string courseId, string planId);
    }

    /// <summary>
    /// Dose voxel data for a single plan, loaded on demand for summation.
    /// </summary>
    public class SummationPlanDoseData
    {
        public int[][,] DoseVoxels { get; set; } = null!;
        public VolumeGeometry DoseGeometry { get; set; } = null!;
        public DoseScaling Scaling { get; set; } = null!;

        /// <summary>
        /// Optional CT image for overlay (only populated for non-reference plans).
        /// </summary>
        public VolumeData? CtImage { get; set; }
    }
}
