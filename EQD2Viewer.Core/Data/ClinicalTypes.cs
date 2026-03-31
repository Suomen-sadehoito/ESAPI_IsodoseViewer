using System;
using System.Collections.Generic;

namespace ESAPI_EQD2Viewer.Core.Data
{
    // ═══════════════════════════════════════════════════════════════
    // CLINICAL SNAPSHOT — Complete data package for a session.
    // Loaded once at startup, then the app operates on this only.
    // No ESAPI calls after loading. Any machine can run the app.
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Immutable snapshot of all clinical data needed to run the application.
    /// Populated by IClinicalDataSource (either from Eclipse or from JSON fixtures).
    /// After construction, the entire app runs on this data — zero ESAPI dependencies.
    /// </summary>
    public class ClinicalSnapshot
    {
        // ── Patient ──
        public PatientData Patient { get; set; }

        // ── Active plan ──
        public PlanData ActivePlan { get; set; }

        // ── CT image ──
        public VolumeData CtImage { get; set; }

        // ── Dose grid ──
        public DoseVolumeData Dose { get; set; }

        // ── Structures (from active plan's structure set) ──
        public List<StructureData> Structures { get; set; } = new List<StructureData>();

        // ── Pre-computed DVH curves (from Eclipse or fixtures) ──
        public List<DvhCurveData> DvhCurves { get; set; } = new List<DvhCurveData>();

        // ── Registrations (for summation) ──
        public List<RegistrationData> Registrations { get; set; } = new List<RegistrationData>();

        // ── All courses/plans for summation dialog ──
        public List<CourseData> AllCourses { get; set; } = new List<CourseData>();
    }

    // ═══════════════════════════════════════════════════════════════
    // PATIENT
    // ═══════════════════════════════════════════════════════════════

    public class PatientData
    {
        public string Id { get; set; } = "";
        public string LastName { get; set; } = "";
        public string FirstName { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════
    // PLAN
    // ═══════════════════════════════════════════════════════════════

    public class PlanData
    {
        public string Id { get; set; } = "";
        public string CourseId { get; set; } = "";
        public double TotalDoseGy { get; set; }
        public int NumberOfFractions { get; set; } = 1;
        public double PlanNormalization { get; set; } = 100.0;
    }

    // ═══════════════════════════════════════════════════════════════
    // VOLUME GEOMETRY — shared by CT image and dose grid
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 3D grid geometry: dimensions, resolution, spatial orientation.
    /// Used for both CT images and dose grids.
    /// </summary>
    public class VolumeGeometry
    {
        public int XSize { get; set; }
        public int YSize { get; set; }
        public int ZSize { get; set; }
        public double XRes { get; set; }
        public double YRes { get; set; }
        public double ZRes { get; set; }
        public Vec3 Origin { get; set; }
        public Vec3 XDirection { get; set; }
        public Vec3 YDirection { get; set; }
        public Vec3 ZDirection { get; set; }
        public string FrameOfReference { get; set; } = "";
        public string Id { get; set; } = "";
    }

    // ═══════════════════════════════════════════════════════════════
    // CT IMAGE VOLUME — geometry + voxel data + HU offset
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Complete CT image: geometry, voxel data, and HU offset.
    /// Voxels are pre-loaded into memory for fast rendering.
    /// </summary>
    public class VolumeData
    {
        public VolumeGeometry Geometry { get; set; } = new VolumeGeometry();

        /// <summary>
        /// CT voxel data indexed by [sliceZ][x, y].
        /// Values are raw DICOM stored values (may need HuOffset subtraction).
        /// </summary>
        public int[][,] Voxels { get; set; }

        /// <summary>
        /// HU offset detected from voxel data.
        /// 0 for signed storage, 32768 for unsigned storage.
        /// </summary>
        public int HuOffset { get; set; }

        // ── Convenience accessors matching the shape of ESAPI Image ──
        public int XSize => Geometry.XSize;
        public int YSize => Geometry.YSize;
        public int ZSize => Geometry.ZSize;
        public double XRes => Geometry.XRes;
        public double YRes => Geometry.YRes;
        public double ZRes => Geometry.ZRes;
        public Vec3 Origin => Geometry.Origin;
        public Vec3 XDirection => Geometry.XDirection;
        public Vec3 YDirection => Geometry.YDirection;
        public Vec3 ZDirection => Geometry.ZDirection;
        public string FOR => Geometry.FrameOfReference;
    }

    // ═══════════════════════════════════════════════════════════════
    // DOSE VOLUME — geometry + voxel data + scaling calibration
    // ═══════════════════════════════════════════════════════════════

    public class DoseScaling
    {
        /// <summary>Linear scale: doseUnit = rawVoxel * RawScale + RawOffset</summary>
        public double RawScale { get; set; }

        /// <summary>Constant offset in dose unit space</summary>
        public double RawOffset { get; set; }

        /// <summary>Conversion factor from dose unit to Gy (e.g., 0.01 for cGy, 1.0 for Gy)</summary>
        public double UnitToGy { get; set; } = 1.0;

        /// <summary>Original dose unit name from Eclipse (Gy, cGy, Percent)</summary>
        public string DoseUnit { get; set; } = "Gy";
    }

    /// <summary>
    /// Complete dose grid: geometry, raw voxel data, and calibration.
    /// Dose in Gy at voxel [x,y]: (Voxels[z][x,y] * Scaling.RawScale + Scaling.RawOffset) * Scaling.UnitToGy
    /// </summary>
    public class DoseVolumeData
    {
        public VolumeGeometry Geometry { get; set; } = new VolumeGeometry();

        /// <summary>
        /// Raw dose voxels indexed by [sliceZ][x, y].
        /// Apply DoseScaling to convert to Gy.
        /// </summary>
        public int[][,] Voxels { get; set; }

        /// <summary>
        /// Calibration factors for raw → Gy conversion.
        /// </summary>
        public DoseScaling Scaling { get; set; } = new DoseScaling();

        // ── Convenience accessors ──
        public int XSize => Geometry.XSize;
        public int YSize => Geometry.YSize;
        public int ZSize => Geometry.ZSize;
        public double XRes => Geometry.XRes;
        public double YRes => Geometry.YRes;
        public double ZRes => Geometry.ZRes;
        public Vec3 Origin => Geometry.Origin;
        public Vec3 XDirection => Geometry.XDirection;
        public Vec3 YDirection => Geometry.YDirection;
        public Vec3 ZDirection => Geometry.ZDirection;
    }

    // ═══════════════════════════════════════════════════════════════
    // STRUCTURE — anatomy contours
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Anatomical structure: metadata, color, and contour polygons per slice.
    /// </summary>
    public class StructureData
    {
        public string Id { get; set; } = "";
        public string DicomType { get; set; } = "";
        public bool IsEmpty { get; set; }
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
        public byte ColorA { get; set; } = 255;

        /// <summary>
        /// Whether this structure has 3D mesh geometry (used for existence check only).
        /// </summary>
        public bool HasMesh { get; set; }

        /// <summary>
        /// Contour polygons per CT slice.
        /// Key: slice index.  Value: list of polygons (each polygon is array of [x,y,z] points in mm).
        /// </summary>
        public Dictionary<int, List<double[][]>> ContoursBySlice { get; set; }
            = new Dictionary<int, List<double[][]>>();      
    }

    // ═══════════════════════════════════════════════════════════════
    // DVH CURVE DATA
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Pre-computed cumulative DVH curve for one structure.
    /// </summary>
    public class DvhCurveData
    {
        public string StructureId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public double DMaxGy { get; set; }
        public double DMeanGy { get; set; }
        public double DMinGy { get; set; }
        public double VolumeCc { get; set; }

        /// <summary>Curve points: each [doseGy, volumePercent]</summary>
        public double[][] Curve { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════
    // REGISTRATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Spatial registration between two frames of reference.
    /// </summary>
    public class RegistrationData
    {
        public string Id { get; set; } = "";
        public string SourceFOR { get; set; } = "";
        public string RegisteredFOR { get; set; } = "";
        public DateTime? CreationDateTime { get; set; }

        /// <summary>4×4 affine transform matrix in row-major order (16 elements).</summary>
        public double[] Matrix { get; set; }

        /// <summary>Converts the flat matrix to a 4×4 array for MatrixMath.</summary>
        public double[,] ToMatrix4x4()
        {
            if (Matrix == null || Matrix.Length != 16) return null;
            var m = new double[4, 4];
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++)
                    m[r, c] = Matrix[r * 4 + c];
            return m;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // COURSE / PLAN DATA (for summation dialog)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Course with its plan list — for the summation dialog plan picker.
    /// </summary>
    public class CourseData
    {
        public string Id { get; set; } = "";
        public List<PlanSummaryData> Plans { get; set; } = new List<PlanSummaryData>();
    }

    /// <summary>
    /// Lightweight plan summary for the summation dialog grid.
    /// Full voxel data is loaded only when the plan is selected for summation.
    /// </summary>
    public class PlanSummaryData
    {
        public string PlanId { get; set; } = "";
        public string CourseId { get; set; } = "";
        public string ImageId { get; set; } = "";
        public string ImageFOR { get; set; } = "";
        public double TotalDoseGy { get; set; }
        public int NumberOfFractions { get; set; } = 1;
        public double PlanNormalization { get; set; } = 100.0;
        public bool HasDose { get; set; }
    }
}
