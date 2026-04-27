using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Calculations;
using VMS.TPS.Common.Model.Types;
using VMS.TPS.Common.Model.API;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;

namespace EQD2Viewer.Esapi.Adapters
{
    /// <summary>
    /// Provides a production implementation of <see cref="IClinicalDataSource"/> that reads live data from Varian Eclipse via ESAPI.
    /// </summary>
    /// <remarks>
    /// This class MUST be instantiated and called on the Eclipse UI thread due to strict ESAPI threading constraints.
    /// All ESAPI access is encapsulated within this class to ensure the remainder of the application remains entirely decoupled from VMS.TPS namespaces.
    /// 
    /// Example usage:
    /// var source = new EsapiDataSource(context);
    /// var snapshot = source.LoadSnapshot();
    /// </remarks>
    public class EsapiDataSource : IClinicalDataSource
    {
        private readonly ScriptContext _context;
        private const int DOSE_CAL_RAW = 10000;

        /// <summary>
        /// Initializes a new instance of the <see cref="EsapiDataSource"/> class.
        /// </summary>
        /// <param name="context">The ESAPI script context providing access to live clinical data.</param>
        /// <exception cref="ArgumentNullException">Thrown if the provided context is null.</exception>
        public EsapiDataSource(ScriptContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// Loads and constructs a complete clinical data snapshot from the current ESAPI context.
        /// </summary>
        /// <returns>A fully populated <see cref="ClinicalSnapshot"/> instance ready for domain processing.</returns>
        public ClinicalSnapshot LoadSnapshot()
        {
            var snapshot = new ClinicalSnapshot();
            var patient = _context.Patient;
            var plan = _context.ExternalPlanSetup;
            var image = _context.Image;

            // Populate patient demographics.
            snapshot.Patient = new PatientData
            {
                Id = patient?.Id ?? "",
                LastName = patient?.LastName ?? "",
                FirstName = patient?.FirstName ?? ""
            };

            // Populate active plan details.
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

            // Load the primary CT image volume.
            if (image != null)
            {
                snapshot.CtImage = LoadImageVolume(image);
            }

            // Load the primary calculated dose grid.
            if (plan?.Dose != null)
            {
                snapshot.Dose = LoadDoseVolume(plan.Dose, plan);
            }

            // Load anatomical structures and their contours.
            if (plan?.StructureSet != null && image != null)
            {
                snapshot.Structures = LoadStructures(plan.StructureSet, image.ZSize);
            }

            // Load pre-calculated Dose-Volume Histogram (DVH) curves.
            if (plan?.StructureSet != null)
            {
                snapshot.DvhCurves = LoadDvhCurves(plan);
            }

            // Load available spatial registrations.
            if (patient?.Registrations != null)
            {
                snapshot.Registrations = LoadRegistrations(patient);
            }

            // Load all available courses and plans for the summation dialog interface.
            if (patient?.Courses != null)
            {
                snapshot.AllCourses = LoadAllCourses(patient);
            }

            SimpleLogger.Info($"Snapshot loaded: {snapshot.CtImage?.ZSize ?? 0} CT slices, " +
                              $"{snapshot.Dose?.ZSize ?? 0} dose slices, " +
                              $"{snapshot.Structures?.Count ?? 0} structures");

            return snapshot;
        }

        /// <summary>
        /// Extracts and processes the CT image volume data.
        /// </summary>
        private VolumeData LoadImageVolume(Image image)
        {
            int zSize = image.ZSize;
            int xSize = image.XSize;
            int ySize = image.YSize;

            // Pre-load all CT voxels into memory to facilitate rapid rendering.
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                image.GetVoxels(z, voxels[z]);
            }

            // Dynamically detect the Hounsfield Unit (HU) offset required for DICOM conversion.
            int midSlice = zSize / 2;
            int huOffset = ImageUtils.DetermineHuOffset(voxels[midSlice], xSize, ySize);

            return new VolumeData
            {
                Geometry = ToGeometry(image),
                Voxels = voxels,
                HuOffset = huOffset
            };
        }

        /// <summary>
        /// Extracts and calibrates the 3D dose volume data.
        /// </summary>
        private DoseVolumeData LoadDoseVolume(Dose dose, PlanSetup plan)
        {
            int zSize = dose.ZSize;
            int xSize = dose.XSize;
            int ySize = dose.YSize;

            // Pre-load all dose voxels into memory.
            var voxels = new int[zSize][,];
            for (int z = 0; z < zSize; z++)
            {
                voxels[z] = new int[xSize, ySize];
                dose.GetVoxels(z, voxels[z]);
            }

            // Compute scaling calibration factors to map raw voxel integers to absolute dose values.
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

        /// <summary>
        /// Extracts anatomical structures and their planar contour data.
        /// </summary>
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

                    // Iterate through all slices to retrieve available contour polygons.
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
                        catch { /* Intentionally ignored: Structures may legitimately lack contours on specific slices. */ }
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

        /// <summary>
        /// Retrieves pre-calculated cumulative DVH data for all eligible structures within the plan.
        /// </summary>
        private List<DvhCurveData> LoadDvhCurves(PlanSetup plan)
        {
            var result = new List<DvhCurveData>();
            if (plan?.StructureSet?.Structures == null) return result;

            foreach (var structure in plan.StructureSet.Structures)
            {
                if (structure.IsEmpty) continue;

                string structureIdForLog = structure.Id;
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
                catch (Exception ex)
                {
                    // DVH metrics may legitimately not be calculable for some structures
                    // (e.g. mesh-less structures, point structures). Log so a missing
                    // DVH curve is at least visible during snapshot diagnosis.
                    SimpleLogger.Warning($"Could not load DVH curve for structure '{structureIdForLog}': {ex.GetType().Name}: {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Retrieves spatial registration matrices required for cross-frame-of-reference summation operations.
        /// </summary>
        private List<RegistrationData> LoadRegistrations(Patient patient)
        {
            var result = new List<RegistrationData>();
            if (patient.Registrations == null) return result;

            foreach (var reg in patient.Registrations)
            {
                try
                {
                    // Extract the 4x4 affine transformation matrix by transforming the spatial basis vectors.
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

        /// <summary>
        /// Iterates through the patient's clinical history to construct a lightweight summary of available courses and plans.
        /// </summary>
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
                    catch (Exception ex)
                    {
                        // Plan-to-image association can be missing on imported plans or
                        // when the structure set's reference image is not accessible.
                        // Log so the empty ImageId / ImageFOR in the resulting summary is
                        // explainable from the log rather than mistaken for a real "no image" state.
                        SimpleLogger.Warning($"Could not resolve image association for plan '{course.Id}/{plan.Id}': {ex.GetType().Name}: {ex.Message}");
                    }

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

        // Geometry conversions delegate to EsapiGeometryConverter so both adapters
        // share a single source of truth for ESAPI -> Vec3 / VolumeGeometry mapping.
        private static VolumeGeometry ToGeometry(Image img) => EsapiGeometryConverter.ToVolumeGeometry(img);
        private static VolumeGeometry ToDoseGeometry(Dose dose) => EsapiGeometryConverter.ToVolumeGeometry(dose);
        private static Vec3 ToVec3(VVector v) => EsapiGeometryConverter.ToVec3(v);

        /// <summary>
        /// Normalizes an ESAPI <see cref="DoseValue"/> into an absolute dose measured in Gray (Gy).
        /// </summary>
        private static double ToGy(DoseValue dv) =>
            dv.Unit == DoseValue.DoseUnit.cGy ? dv.Dose / 100.0 : dv.Dose;
    }
}