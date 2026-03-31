using EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Models;

namespace EQD2Viewer.Core.Interfaces
{
    public interface IDVHCalculation
    {
        DoseVolumePoint[] CalculateDVHFromSummedDose(
            double[][] summedSlices, bool[][] structureMasks,
            double voxelVolumeCc, double maxDoseGy);

        DVHSummary BuildSummaryFromCurve(string structureId, string label,
            string type, DoseVolumePoint[] curve, double totalVolumeCc);
    }
}