using EQD2Viewer.Core.Data;

namespace ESAPI_EQD2Viewer.Core.Interfaces
{
    public interface IDebugExportService
    {
        void ExportDebugLog(ClinicalSnapshot snapshot, int currentSlice);
    }
}
