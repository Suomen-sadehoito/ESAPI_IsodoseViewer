using System;
using System.IO;
using System.Windows;
using ESAPI_EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.Services
{
    public class DebugExportService : IDebugExportService
    {
        public void ExportDebugLog(ClinicalSnapshot snapshot, int currentSlice)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "EQD2Viewer_Debug.txt");
                using (StreamWriter sw = new StreamWriter(path))
                {
                    sw.WriteLine("==================================================================================");
                    sw.WriteLine($"=== EQD2 VIEWER DEBUG LOG - {DateTime.Now} ===");
                    sw.WriteLine("==================================================================================");

                    sw.WriteLine("\n--- 1. PLAN & CONTEXT ---");
                    var plan = snapshot?.ActivePlan;
                    if (plan == null) sw.WriteLine("PLAN IS NULL!");
                    else
                    {
                        sw.WriteLine($"Plan ID: {plan.Id}");
                        sw.WriteLine($"Total Dose: {plan.TotalDoseGy:F2} Gy");
                        sw.WriteLine($"Plan Normalization: {plan.PlanNormalization}%");
                        sw.WriteLine($"Number of Fractions: {plan.NumberOfFractions}");
                    }

                    var image = snapshot?.CtImage;
                    sw.WriteLine("\n--- 2. IMAGE GEOMETRY (CT) ---");
                    if (image == null) { sw.WriteLine("IMAGE IS NULL!"); return; }
                    sw.WriteLine($"Size (X, Y, Z): {image.XSize}, {image.YSize}, {image.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {image.XRes:F4}, {image.YRes:F4}, {image.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({image.Origin.X:F2}, {image.Origin.Y:F2}, {image.Origin.Z:F2})");

                    var dose = snapshot?.Dose;
                    sw.WriteLine("\n--- 3. DOSE GEOMETRY ---");
                    if (dose == null) { sw.WriteLine("DOSE IS NULL!"); return; }
                    sw.WriteLine($"Size (X, Y, Z): {dose.XSize}, {dose.YSize}, {dose.ZSize}");
                    sw.WriteLine($"Res (X, Y, Z):  {dose.XRes:F4}, {dose.YRes:F4}, {dose.ZRes:F4} mm");
                    sw.WriteLine($"Origin (mm):    ({dose.Origin.X:F2}, {dose.Origin.Y:F2}, {dose.Origin.Z:F2})");

                    sw.WriteLine("\n--- 4. SCALING FACTORS ---");
                    if (dose.Scaling != null)
                    {
                        sw.WriteLine($"Raw Scale: {dose.Scaling.RawScale:E8}");
                        sw.WriteLine($"Raw Offset: {dose.Scaling.RawOffset}");
                        sw.WriteLine($"Unit to Gy: {dose.Scaling.UnitToGy}");
                        sw.WriteLine($"Dose Unit: {dose.Scaling.DoseUnit}");
                    }

                    sw.WriteLine("\n--- 5. SLICE INFO ---");
                    sw.WriteLine($"Current CT Slice Index: {currentSlice}");
                    sw.WriteLine($"CT Z slices: {image.ZSize}");
                    sw.WriteLine($"Dose Z slices: {dose.ZSize}");
                }
                MessageBox.Show($"Debug report saved: {path}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex}");
            }
        }
    }
}
