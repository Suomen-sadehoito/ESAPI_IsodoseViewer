using VMS.TPS.Common.Model.API;

namespace ESAPI_IsodoseViewer.Services
{
    public interface IDebugExportService
    {
        void ExportDebugLog(ScriptContext context, PlanSetup plan, int currentSlice);
    }
}