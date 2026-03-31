namespace ESAPI_EQD2Viewer.Core.Models
{
    /// <summary>
    /// Method for calculating EQD2 Dmean.
    /// </summary>
    public enum EQD2MeanMethod
    {
        /// <summary>Fast: directly converts physical Dmean to EQD2.</summary>
        Simple,

        /// <summary>Accurate: calculates weighted average from differential DVH bins.</summary>
        Differential
    }
}
