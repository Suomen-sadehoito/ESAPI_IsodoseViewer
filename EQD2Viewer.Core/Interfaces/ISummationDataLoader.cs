using System.Collections.Generic;
using ESAPI_EQD2Viewer.Core.Data;

namespace EQD2Viewer.Core.Interfaces
{
    /// <summary>
    /// Abstracts on-demand loading of plan dose data for multi-plan summation.
    /// 
    /// Implementations:
    ///   EsapiSummationDataLoader — loads from live Eclipse via ESAPI (in EQD2Viewer.Esapi)
    ///   (future) JsonSummationDataLoader — loads from fixture files
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
    }

    /// <summary>
    /// Dose voxel data for a single plan, loaded on demand for summation.
    /// </summary>
    public class SummationPlanDoseData
    {
        public int[][,] DoseVoxels { get; set; }
        public VolumeGeometry DoseGeometry { get; set; }
        public DoseScaling Scaling { get; set; }
    }
}
