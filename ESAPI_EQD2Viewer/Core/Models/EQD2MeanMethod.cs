namespace ESAPI_EQD2Viewer.Core.Models
{
    /// <summary>
    /// Method for calculating mean dose in EQD2 conversion.
    /// 
    /// Simple (default): Uses mean dose directly, applies LQ model.
    ///   D_mean_EQD2 = D_mean * (d + α/β) / (2 + α/β)
    /// 
    /// Differential: Integrates LQ model over the DVH curve (proper physics).
    ///   D_mean_EQD2 = ∫[D_min to D_max] EQD2(D) * dV/dD dD / V_total
    /// </summary>
    public enum EQD2MeanMethod
    {
        Simple,
        Differential
    }
}
