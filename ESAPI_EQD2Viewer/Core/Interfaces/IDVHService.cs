using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Logging;
using ESAPI_EQD2Viewer.Core.Models;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;


namespace ESAPI_EQD2Viewer.Core.Interfaces
{
    /// <summary>
    /// ESAPI-dependent DVH data retrieval and summary building.
    /// (Pure math calculation methods have been moved to IDVHCalculation.cs)
    /// </summary>
    public interface IDVHService : IDVHCalculation
    {
        DVHData GetDVH(PlanSetup plan, Structure structure);

        DVHSummary BuildPhysicalSummary(PlanSetup plan, Structure structure, DVHData dvhData);

        DVHSummary BuildEQD2Summary(PlanSetup plan, Structure structure, DVHData dvhData,
            int numberOfFractions, double alphaBeta, EQD2MeanMethod meanMethod);
    }
}