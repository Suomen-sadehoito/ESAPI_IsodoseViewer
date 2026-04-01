using System;
using System.Collections.Generic;
using System.Linq;
using ESAPI_EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Models;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_EQD2Viewer.Adapters
{
    /// <summary>
    /// Production ISummationDataLoader: loads plan dose/structure data from live Eclipse via ESAPI.
    /// 
    /// MUST be called on the Eclipse UI thread (ESAPI threading constraint).
    /// All ESAPI access for summation is concentrated here — SummationService itself
    /// never touches VMS.TPS namespaces.
    /// </summary>
    public class EsapiSummationDataLoader : ISummationDataLoader
    {
        private readonly Patient _patient;
        private const int DOSE_CAL_RAW = 10000;

        public EsapiSummationDataLoader(Patient patient)
        {
            _patient = patient ?? throw new ArgumentNullException(nameof(patient));
        }

        public SummationPlanDoseData LoadPlanDose(string courseId, string planId, double totalDoseGy)
        {
            var course = _patient.Courses?.FirstOrDefault(c => c.Id == courseId);
            if (course == null) { SimpleLogger.Error($"Course not found: {courseId}"); return null; }

            var plan = course.PlanSetups?.FirstOrDefault(p => p.Id == planId);
            if (plan?.Dose == null) { SimpleLogger.Error($"Plan or dose not found: {planId}"); return null; }

            var dose = plan.Dose;
            int dx = dose.XSize, dy = dose.YSize, dz = dose.ZSize;

            // Load dose voxels
            int[][,] doseVoxels = new int[dz][,];
            for (int z = 0; z < dz; z++)
            {
                doseVoxels[z] = new int[dx, dy];
                dose.GetVoxels(z, doseVoxels[z]);
            }

            // Compute dose scaling calibration
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;

            double unitToGy;
            string unitName = dvRef.Unit.ToString();
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = totalDoseGy / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else
                unitToGy = 1.0;

            // Load secondary CT for overlay
            VolumeData ctImage = null;
            try
            {
                var img = plan.StructureSet?.Image;
                if (img != null)
                {
                    int cx = img.XSize, cy = img.YSize, cz = img.ZSize;
                    var ctVoxels = new int[cz][,];
                    for (int z = 0; z < cz; z++)
                    {
                        ctVoxels[z] = new int[cx, cy];
                        img.GetVoxels(z, ctVoxels[z]);
                    }
                    int midZ = cz / 2;
                    int huOffset = ImageUtils.DetermineHuOffset(ctVoxels[midZ], cx, cy);

                    ctImage = new VolumeData
                    {
                        Geometry = ToGeometry(img),
                        Voxels = ctVoxels,
                        HuOffset = huOffset
                    };
                }
            }
            catch (Exception ex)
            {
                SimpleLogger.Warning($"CT loading failed for {courseId}/{planId}: {ex.Message}");
            }

            return new SummationPlanDoseData
            {
                DoseVoxels = doseVoxels,
                DoseGeometry = ToDoseGeometry(dose),
                Scaling = new DoseScaling
                {
                    RawScale = rawScale,
                    RawOffset = rawOffset,
                    UnitToGy = unitToGy,
                    DoseUnit = unitName
                },
                CtImage = ctImage
            };
        }

        public List<StructureData> LoadStructureContours(string courseId, string planId)
        {
            var result = new List<StructureData>();
            var course = _patient.Courses?.FirstOrDefault(c => c.Id == courseId);
            var plan = course?.PlanSetups?.FirstOrDefault(p => p.Id == planId);
            if (plan?.StructureSet?.Structures == null) return result;

            var image = plan.StructureSet.Image;
            int imageZSize = image?.ZSize ?? 0;
            if (imageZSize == 0) return result;

            foreach (var structure in plan.StructureSet.Structures)
            {
                if (structure.IsEmpty) continue;

                try
                {
                    var sd = new StructureData
                    {
                        Id = structure.Id,
                        DicomType = structure.DicomType ?? "",
                        IsEmpty = structure.IsEmpty,
                        ColorR = structure.Color.R,
                        ColorG = structure.Color.G,
                        ColorB = structure.Color.B,
                        ColorA = structure.Color.A,
                        HasMesh = structure.MeshGeometry != null
                    };

                    for (int z = 0; z < imageZSize; z++)
                    {
                        try
                        {
                            var contours = structure.GetContoursOnImagePlane(z);
                            if (contours == null || contours.Length == 0) continue;

                            var sliceContours = new List<double[][]>();
                            foreach (var contour in contours)
                            {
                                if (contour.Length < 3) continue;
                                var points = new double[contour.Length][];
                                for (int i = 0; i < contour.Length; i++)
                                    points[i] = new double[] { contour[i].x, contour[i].y, contour[i].z };
                                sliceContours.Add(points);
                            }

                            if (sliceContours.Count > 0)
                                sd.ContoursBySlice[z] = sliceContours;
                        }
                        catch { /* Some structures may not have contours on all slices */ }
                    }

                    result.Add(sd);
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"Could not load structure '{structure.Id}': {ex.Message}");
                }
            }

            return result;
        }

        public RegistrationData FindRegistration(string registrationId)
        {
            if (string.IsNullOrEmpty(registrationId) || _patient.Registrations == null)
                return null;

            var reg = _patient.Registrations.FirstOrDefault(r => r.Id == registrationId);
            if (reg == null) return null;

            try
            {
                VVector o = reg.TransformPoint(new VVector(0, 0, 0));
                VVector x = reg.TransformPoint(new VVector(1, 0, 0));
                VVector y = reg.TransformPoint(new VVector(0, 1, 0));
                VVector z = reg.TransformPoint(new VVector(0, 0, 1));

                return new RegistrationData
                {
                    Id = reg.Id,
                    SourceFOR = reg.SourceFOR ?? "",
                    RegisteredFOR = reg.RegisteredFOR ?? "",
                    CreationDateTime = reg.CreationDateTime,
                    Matrix = new double[]
                    {
                        x.x - o.x, y.x - o.x, z.x - o.x, o.x,
                        x.y - o.y, y.y - o.y, z.y - o.y, o.y,
                        x.z - o.z, y.z - o.z, z.z - o.z, o.z,
                        0, 0, 0, 1
                    }
                };
            }
            catch (Exception ex)
            {
                SimpleLogger.Error($"FindRegistration failed for {registrationId}", ex);
                return null;
            }
        }

        public string GetPlanImageFOR(string courseId, string planId)
        {
            try
            {
                var course = _patient.Courses?.FirstOrDefault(c => c.Id == courseId);
                var plan = course?.PlanSetups?.FirstOrDefault(p => p.Id == planId);
                return plan?.StructureSet?.Image?.FOR ?? "";
            }
            catch (Exception ex)
            {
                SimpleLogger.Warning($"Could not get plan FOR: {ex.Message}");
                return "";
            }
        }

        // ════════════════════════════════════════════════════════
        // GEOMETRY CONVERTERS
        // ════════════════════════════════════════════════════════

        private static VolumeGeometry ToGeometry(Image img) => new VolumeGeometry
        {
            XSize = img.XSize, YSize = img.YSize, ZSize = img.ZSize,
            XRes = img.XRes, YRes = img.YRes, ZRes = img.ZRes,
            Origin = ToVec3(img.Origin),
            XDirection = ToVec3(img.XDirection),
            YDirection = ToVec3(img.YDirection),
            ZDirection = ToVec3(img.ZDirection),
            FrameOfReference = img.FOR ?? "",
            Id = img.Id ?? ""
        };

        private static VolumeGeometry ToDoseGeometry(Dose dose) => new VolumeGeometry
        {
            XSize = dose.XSize, YSize = dose.YSize, ZSize = dose.ZSize,
            XRes = dose.XRes, YRes = dose.YRes, ZRes = dose.ZRes,
            Origin = ToVec3(dose.Origin),
            XDirection = ToVec3(dose.XDirection),
            YDirection = ToVec3(dose.YDirection),
            ZDirection = ToVec3(dose.ZDirection)
        };

        private static Vec3 ToVec3(VVector v) => new Vec3(v.x, v.y, v.z);
    }
}
