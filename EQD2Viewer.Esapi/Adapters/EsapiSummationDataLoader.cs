#nullable enable annotations
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Calculations;
using VMS.TPS.Common.Model.Types;
using VMS.TPS.Common.Model.API;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.Linq;

namespace EQD2Viewer.Esapi.Adapters
{
    /// <summary>
    /// Provides a production implementation of <see cref="ISummationDataLoader"/> that loads plan dose and structure data from a live Eclipse environment via ESAPI.
    /// </summary>
    /// <remarks>
    /// This class MUST be instantiated and executed on the Eclipse UI thread due to strict ESAPI threading constraints.
    /// All ESAPI access for summation operations is encapsulated within this class, ensuring the domain-level summation services remain decoupled from VMS.TPS namespaces.
    /// </remarks>
    public class EsapiSummationDataLoader : ISummationDataLoader
    {
        private readonly Patient _patient;
        private const int DOSE_CAL_RAW = 10000;

        /// <summary>
        /// Initializes a new instance of the <see cref="EsapiSummationDataLoader"/> class.
        /// </summary>
        /// <param name="patient">The ESAPI patient context providing access to clinical data.</param>
        /// <exception cref="ArgumentNullException">Thrown if the provided patient context is null.</exception>
        public EsapiSummationDataLoader(Patient patient)
        {
            _patient = patient ?? throw new ArgumentNullException(nameof(patient));
        }

        /// <summary>
        /// Retrieves the dose grid data and its associated primary CT image for a specific treatment plan.
        /// </summary>
        /// <param name="courseId">The unique identifier of the course containing the plan.</param>
        /// <param name="planId">The unique identifier of the target treatment plan.</param>
        /// <param name="totalDoseGy">The total prescribed dose in Gray (Gy), utilized for relative percentage conversions.</param>
        /// <returns>A <see cref="SummationPlanDoseData"/> instance containing the loaded dose and image volumes, or null if the data cannot be found.</returns>
        public SummationPlanDoseData LoadPlanDose(string courseId, string planId, double totalDoseGy)
        {
            var course = _patient.Courses?.FirstOrDefault(c => c.Id == courseId);
            if (course == null) { SimpleLogger.Error($"Course not found: {courseId}"); return null; }

            var plan = course.PlanSetups?.FirstOrDefault(p => p.Id == planId);
            if (plan?.Dose == null) { SimpleLogger.Error($"Plan or dose not found: {planId}"); return null; }

            var dose = plan.Dose;
            int[][,] doseVoxels = EsapiVoxelLoader.LoadVoxels(dose);

            // Compute scaling calibration factors to map raw voxel values to absolute dose units.
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

            // Attempt to load the primary CT image associated with the plan's structure set for overlay visualization.
            VolumeData ctImage = null;
            try
            {
                var img = plan.StructureSet?.Image;
                if (img != null)
                {
                    int cx = img.XSize, cy = img.YSize, cz = img.ZSize;
                    var ctVoxels = EsapiVoxelLoader.LoadVoxels(img);

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

        /// <summary>
        /// Extracts anatomical structures and their corresponding 3D planar contours for a specified plan.
        /// </summary>
        /// <param name="courseId">The unique identifier of the course.</param>
        /// <param name="planId">The unique identifier of the treatment plan.</param>
        /// <returns>A collection of <see cref="StructureData"/> representing the parsed structures and their spatial geometries.</returns>
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

                    // Traverse image slices to gather available contour polygons.
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
                        catch { /* Intentionally ignored: Expected behavior when structures do not intersect specific slices. */ }
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
        /// Retrieves and computes the 4x4 affine transformation matrix for a specified spatial registration between coordinate frames.
        /// </summary>
        /// <param name="registrationId">The identifier of the spatial registration to retrieve.</param>
        /// <returns>A <see cref="RegistrationData"/> object detailing the transformation, or null if not found or invalid.</returns>
        public RegistrationData FindRegistration(string registrationId)
        {
            if (string.IsNullOrEmpty(registrationId) || _patient.Registrations == null)
                return null;

            var reg = _patient.Registrations.FirstOrDefault(r => r.Id == registrationId);
            if (reg == null) return null;

            try
            {
                // Derive the full affine transformation matrix by transforming standard basis vectors.
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

        /// <summary>
        /// Retrieves the Frame of Reference (FOR) UID for the primary image associated with a specific treatment plan.
        /// </summary>
        /// <param name="courseId">The identifier of the course.</param>
        /// <param name="planId">The identifier of the treatment plan.</param>
        /// <returns>The string representation of the Frame of Reference UID, or an empty string if it cannot be determined.</returns>
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

        // Geometry conversions delegate to EsapiGeometryConverter so both adapters
        // share a single source of truth for ESAPI -> Vec3 / VolumeGeometry mapping.
        private static VolumeGeometry ToGeometry(Image img) => EsapiGeometryConverter.ToVolumeGeometry(img);
        private static VolumeGeometry ToDoseGeometry(Dose dose) => EsapiGeometryConverter.ToVolumeGeometry(dose);
        private static Vec3 ToVec3(VVector v) => EsapiGeometryConverter.ToVec3(v);
    }
}