using System.Collections.Generic;

namespace EQD2Viewer.Core.Models
{
    /// <summary>
    /// Configuration for multi-plan dose summation.
    /// See original documentation for mathematical basis.
    /// </summary>
    public class SummationConfig
    {
        public List<SummationPlanEntry> Plans { get; set; } = new List<SummationPlanEntry>();
        public SummationMethod Method { get; set; } = SummationMethod.EQD2;
        public double GlobalAlphaBeta { get; set; } = 3.0;
    }

    public class SummationPlanEntry
    {
        public string DisplayLabel { get; set; } = "";
        public string CourseId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public int NumberOfFractions { get; set; } = 1;
        public double TotalDoseGy { get; set; }
        public double PlanNormalization { get; set; } = 100.0;
        public bool IsReference { get; set; }
        public string RegistrationId { get; set; } = "";
        public double Weight { get; set; } = 1.0;

        /// <summary>
        /// Optional path to a pre-computed deformation vector field (.mha).
        /// When set, DIR-based voxel mapping is used instead of the affine RegistrationId.
        /// </summary>
        public string DeformationFieldPath { get; set; } = "";

        /// <summary>
        /// Optional in-memory deformation field. 
        /// Overrides both RegistrationId and DeformationFieldPath if set.
        /// </summary>
        public Data.DeformationField? DeformationField { get; set; }
    }

    public enum SummationMethod
    {
        Physical,
        EQD2
    }
}
