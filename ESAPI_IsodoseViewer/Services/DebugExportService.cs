using ESAPI_IsodoseViewer.Helpers;
using System;
using System.IO;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_IsodoseViewer.Services
{
    public class DebugExportService : IDebugExportService
    {
        public void ExportDebugLog(ScriptContext context, PlanSetup plan, int currentSlice)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "ESAPI_DeepDebug.txt");
                using (StreamWriter sw = new StreamWriter(path))
                {
                    sw.WriteLine("==================================================================================");
                    sw.WriteLine($"=== ESAPI MASSIVE DEBUG LOG - {DateTime.Now} ===");
                    sw.WriteLine("==================================================================================");

                    sw.WriteLine("\n--- 1. PLAN & CONTEXT ---");
                    if (plan == null) sw.WriteLine("PLAN IS NULL!");
                    else
                    {
                        sw.WriteLine($"Plan ID: {plan.Id}");
                        sw.WriteLine($"Total Dose: {plan.TotalDose.Dose} {plan.TotalDose.Unit}");
                        sw.WriteLine($"Plan Normalization: {plan.PlanNormalizationValue}%");
                        sw.WriteLine($"Dose Value presentation: {plan.DoseValuePresentation}");
                    }

                    var image = context.Image;
                    sw.WriteLine("\n--- 2. IMAGE GEOMETRY (CT) ---");
                    if (image == null)
                    {
                        sw.WriteLine("IMAGE IS NULL!");
                        return;
                    }
                    sw.WriteLine($"Size (X, Y, Z): {image.XSize}, {image.YSize}, {image.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {image.XRes:F4}, {image.YRes:F4}, {image.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({image.Origin.x:F2}, {image.Origin.y:F2}, {image.Origin.z:F2})");
                    sw.WriteLine($"X-Direction:    ({image.XDirection.x:F4}, {image.XDirection.y:F4}, {image.XDirection.z:F4})");
                    sw.WriteLine($"Y-Direction:    ({image.YDirection.x:F4}, {image.YDirection.y:F4}, {image.YDirection.z:F4})");
                    sw.WriteLine($"Z-Direction:    ({image.ZDirection.x:F4}, {image.ZDirection.y:F4}, {image.ZDirection.z:F4})");

                    var dose = plan?.Dose;
                    sw.WriteLine("\n--- 3. DOSE GEOMETRY ---");
                    if (dose == null)
                    {
                        sw.WriteLine("DOSE IS NULL!");
                        return;
                    }
                    sw.WriteLine($"Size (X, Y, Z): {dose.XSize}, {dose.YSize}, {dose.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {dose.XRes:F4}, {dose.YRes:F4}, {dose.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({dose.Origin.x:F2}, {dose.Origin.y:F2}, {dose.Origin.z:F2})");

                    sw.WriteLine("\n--- 4. SCALING FACTORS (Raw Int -> Physical Gy) ---");
                    DoseValue dv0 = dose.VoxelToDoseValue(0);
                    DoseValue dv10k = dose.VoxelToDoseValue(10000);

                    double gy0 = (dv0.Unit == DoseValue.DoseUnit.cGy) ? dv0.Dose / 100.0 : dv0.Dose;
                    double gyRef = (dv10k.Unit == DoseValue.DoseUnit.cGy) ? dv10k.Dose / 100.0 : dv10k.Dose;
                    double dScale = (gyRef - gy0) / 10000.0;

                    sw.WriteLine($"Calculated Offset (Gy): {gy0}");
                    sw.WriteLine($"Calculated Scale (Gy/RawUnit): {dScale:E8}");

                    sw.WriteLine("\n--- 5. SLICE MAPPING (Current View) ---");
                    sw.WriteLine($"Current CT Slice Index: {currentSlice}");

                    VVector ctPlaneCenterWorld = image.Origin + image.ZDirection * (currentSlice * image.ZRes);
                    VVector relativeToDoseOrigin = ctPlaneCenterWorld - dose.Origin;
                    double zDiff = relativeToDoseOrigin.Dot(dose.ZDirection);
                    int doseSliceIndex = (int)Math.Round(zDiff / dose.ZRes);

                    sw.WriteLine($"CT Slice Z World Pos: {ctPlaneCenterWorld.z:F2} mm");
                    sw.WriteLine($"Diff from Dose Origin Z: {zDiff:F2} mm");
                    sw.WriteLine($"Calculated Dose Slice Index: {doseSliceIndex}");
                }
                MessageBox.Show($"Debug report created: {path}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving debug log: {ex}");
            }
        }
    }
}