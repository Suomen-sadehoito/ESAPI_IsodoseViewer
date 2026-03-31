using System;
using System.Collections.Generic;
using ESAPI_EQD2Viewer.Core.Data;
using ESAPI_EQD2Viewer.Core.Interfaces;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Models;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace ESAPI_EQD2Viewer.Adapters
{
    /// <summary>
    /// Production IClinicalDataSource: reads live data from Varian Eclipse via ESAPI.
    /// 
    /// MUST be called on the Eclipse UI thread (ESAPI threading constraint).
    /// All ESAPI access is concentrated in this single class — the rest of the app
    /// never touches VMS.TPS namespaces.
    /// 
    /// Usage (in Script.cs):
    ///   var source = new EsapiDataSource(context);
    ///   var snapshot = source.LoadSnapshot();
    /// </summary>
    public class EsapiDataSource : IClinicalDataSource
    {
        private readonly ScriptContext _context;
        private const int DOSE_CAL_RAW = 10000;

        public EsapiDataSource(ScriptContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public ClinicalSnapshot LoadSnapshot()
        {
            var snapshot = new ClinicalSnapshot();
            var patient = _context.Patient;
            var plan = _context.ExternalPlanSetup;
            var image = _context.Image;

            // ── Patient ──
            snapshot.Patient = new PatientData
            {
                Id = patient?.Id ?? "",
                LastName = patient?.LastName ?? "",
                FirstName = patient?.FirstName ?? ""
            };

            // ── Active plan ──
            if (plan != null)
            {
                snapshot.ActivePlan = new PlanData
                {
                    Id = plan.Id,
                    CourseId = plan.Course?.Id ?? "",
                    TotalDoseGy = ToGy(plan.TotalDose),
                    NumberOfFractions = plan.NumberOfFractions ?? 1,
                    PlanNormalization = plan.PlanNormalizationValue
                };
            }

            // ── CT Image ──
            if (image != null)
            {
                snapshot.CtImage = LoadImageVolume(image);
            }

            // ── Dose ──
            if (plan?.Dose != null)
            {
                snapshot.Dose = LoadDoseVolume(plan.Dose, plan);
            }

            // ── Structures ──
            if (plan?.StructureSet != null && image != null)
            {
                snapshot.Structures = LoadStructures(plan.StructureSet, image.ZSize);
            }

            // ── DVH curves ──
            if (plan?.StructureSet != null)
            {
                snapshot.DvhCurves = LoadDvhCurves(plan);
            }

            // ── Registrations ──
            if (patient?.Registrations != null)
            {
                snapshot.Registrations = LoadRegistrations(patient);
            }

            // ── All courses/plans (for summation dialog) ──
            if (patient?.Courses != null)
            {
                snapshot.AllCourses = LoadAllCourses(patient);
            }

            SimpleLogger.Info($"Snapshot loaded: {snapshot.CtImage?.ZSize ?? 0} CT slices, " +
                              $"{snapshot.Dose?.ZSize ?? 0} dose slices, " +
                              $"{snapshot.Structures?.Count ?? 0} structures");

            return snapshot;
        }

        // ════════════════════════════════════════════════════════
        // PRIVATE LOADERS
        // ════════════════════════════════════════════════════════

        private VolumeData LoadImageVolume(Image image)
        {
            int zSize = image.ZSize;
            int xSize = image.XSize;
            int ySize = image.YSize;

            // Pre-load all CT voxels
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                image.GetVoxels(z, voxels[z]);
            }

            // Detect HU offset
            int midSlice = zSize / 2;
            int huOffset = ImageUtils.DetermineHuOffset(voxels[midSlice], xSize, ySize);

            return new VolumeData
            {
                Geometry = ToGeometry(image),
                Voxels = voxels,
                HuOffset = huOffset
            };
        }

        private DoseVolumeData LoadDoseVolume(Dose dose, PlanSetup plan)
        {
            int zSize = dose.ZSize;
            int xSize = dose.XSize;
            int ySize = dose.YSize;

            // Pre-load all dose voxels
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                dose.GetVoxels(z, voxels[z]);
            }

            // Compute dose scaling calibration
            DoseValue dv0 = dose.VoxelToDoseValue(0);
            DoseValue dvRef = dose.VoxelToDoseValue(DOSE_CAL_RAW);
            double rawScale = (dvRef.Dose - dv0.Dose) / (double)DOSE_CAL_RAW;
            double rawOffset = dv0.Dose;

            double unitToGy;
            string unitName = dvRef.Unit.ToString();
            if (dvRef.Unit == DoseValue.DoseUnit.Percent)
                unitToGy = ToGy(plan.TotalDose) / 100.0;
            else if (dvRef.Unit == DoseValue.DoseUnit.cGy)
                unitToGy = 0.01;
            else
                unitToGy = 1.0;

            return new DoseVolumeData
            {
                Geometry = ToDoseGeometry(dose),
                Voxels = voxels,
                Scaling = new DoseScaling
                {
                    RawScale = rawScale,
                    RawOffset = rawOffset,
                    UnitToGy = unitToGy,
                    DoseUnit = unitName
                }
            };
        }

        private List<StructureData> LoadStructures(StructureSet structureSet, int imageZSize)
        {
            var result = new List<StructureData>();
            if (structureSet?.Structures == null) return result;

            foreach (var structure in structureSet.Structures)
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

                    // Load contours for all slices
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

        private List<DvhCurveData> LoadDvhCurves(PlanSetup plan)
        {
            var result = new List<DvhCurveData>();
            if (plan?.StructureSet?.Structures == null) return result;

            foreach (var structure in plan.StructureSet.Structures)
            {
                if (structure.IsEmpty) continue;

                try
                {
                    var dvh = plan.GetDVHCumulativeData(structure,
                        DoseValuePresentation.Absolute, VolumePresentation.Relative,
                        DomainConstants.DvhSamplingResolution);

                    if (dvh?.CurveData == null || dvh.CurveData.Length == 0) continue;

                    var curve = new double[dvh.CurveData.Length][];
                    for (int i = 0; i < dvh.CurveData.Length; i++)
                    {
                        curve[i] = new double[]
                        {
                            ToGy(dvh.CurveData[i].DoseValue),
                            dvh.CurveData[i].Volume
                        };
                    }

                    result.Add(new DvhCurveData
                    {
                        StructureId = structure.Id,
                        PlanId = plan.Id,
                        DMaxGy = ToGy(dvh.MaxDose),
                        DMeanGy = ToGy(dvh.MeanDose),
                        DMinGy = ToGy(dvh.MinDose),
                        VolumeCc = dvh.Volume,
                        Curve = curve
                    });
                }
                catch { /* DVH not available for all structures */ }
            }

            return result;
        }

        private List<RegistrationData> LoadRegistrations(Patient patient)
        {
            var result = new List<RegistrationData>();
            if (patient.Registrations == null) return result;

            foreach (var reg in patient.Registrations)
            {
                try
                {
                    // Extract 4×4 matrix by transforming basis vectors
                    VVector o = reg.TransformPoint(new VVector(0, 0, 0));
                    VVector x = reg.TransformPoint(new VVector(1, 0, 0));
                    VVector y = reg.TransformPoint(new VVector(0, 1, 0));
                    VVector z = reg.TransformPoint(new VVector(0, 0, 1));

                    result.Add(new RegistrationData
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
                    });
                }
                catch (Exception ex)
                {
                    SimpleLogger.Warning($"Could not load registration '{reg.Id}': {ex.Message}");
                }
            }

            return result;
        }

        private List<CourseData> LoadAllCourses(Patient patient)
        {
            var result = new List<CourseData>();
            if (patient.Courses == null) return result;

            foreach (var course in patient.Courses)
            {
                var cd = new CourseData { Id = course.Id };
                if (course.PlanSetups == null) { result.Add(cd); continue; }

                foreach (var plan in course.PlanSetups)
                {
                    string imageId = "", imageFOR = "";
                    try
                    {
                        var img = plan.StructureSet?.Image;
                        if (img != null) { imageId = img.Id ?? ""; imageFOR = img.FOR ?? ""; }
                    }
                    catch { }

                    cd.Plans.Add(new PlanSummaryData
                    {
                        PlanId = plan.Id,
                        CourseId = course.Id,
                        ImageId = imageId,
                        ImageFOR = imageFOR,
                        TotalDoseGy = ToGy(plan.TotalDose),
                        NumberOfFractions = plan.NumberOfFractions ?? 1,
                        PlanNormalization = plan.PlanNormalizationValue,
                        HasDose = plan.Dose != null
                    });
                }
                result.Add(cd);
            }

            return result;
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

        private static double ToGy(DoseValue dv) =>
            dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;
    }
}
