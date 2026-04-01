using EQD2Viewer.Core.Data;

namespace EQD2Viewer.Core.Interfaces
{
public interface IDebugExportService
    {
        void ExportDebugLog(ClinicalSnapshot snapshot, int currentSlice);
    }
}
