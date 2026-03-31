using ESAPI_EQD2Viewer.Core.Data;

namespace ESAPI_EQD2Viewer.Core.Interfaces
{
    /// <summary>
    /// Single abstraction point for all clinical data access.
    /// 
    /// Two implementations:
    ///   1. EsapiDataSource  — reads from live Varian Eclipse via ESAPI (clinical workstation)
    ///   2. JsonDataSource   — reads from JSON fixture files (any developer machine)
    /// 
    /// The entire application operates on the ClinicalSnapshot returned by LoadSnapshot().
    /// After loading, zero ESAPI calls are made — the app is fully decoupled.
    /// 
    /// Usage:
    ///   IClinicalDataSource source = new EsapiDataSource(context);  // or JsonDataSource
    ///   ClinicalSnapshot snapshot = source.LoadSnapshot();
    ///   var viewModel = new MainViewModel(snapshot, services...);
    /// </summary>
    public interface IClinicalDataSource
    {
        /// <summary>
        /// Loads all clinical data into an in-memory snapshot.
        /// This is the only method that touches the data source (ESAPI or filesystem).
        /// Must be called on the UI thread when using ESAPI (Eclipse threading constraint).
        /// </summary>
        ClinicalSnapshot LoadSnapshot();
    }
}
