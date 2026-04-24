using EQD2Viewer.Core.Data;
using System.Text;

namespace EQD2Viewer.Core.Models
{
    /// <summary>
    /// FOV (field-of-view) diagnostic for a pair of CT volumes about to be deformably
    /// registered. Reports the raw axis-aligned bounding boxes in patient coordinates,
    /// how much they overlap, and the overlap after centering the moving volume on the
    /// fixed volume (which mimics SimpleITK's CenteredTransformInitializer in GEOMETRY
    /// mode). Produced by <c>EQD2Viewer.Core.Calculations.VolumeOverlapAnalyzer</c>.
    /// </summary>
    public class VolumeOverlapReport
    {
        public string FixedId { get; set; } = "";
        public string MovingId { get; set; } = "";

        public string FixedFOR { get; set; } = "";
        public string MovingFOR { get; set; } = "";
        public bool FORMatch => !string.IsNullOrEmpty(FixedFOR) && FixedFOR == MovingFOR;

        public int FixedXSize { get; set; }
        public int FixedYSize { get; set; }
        public int FixedZSize { get; set; }
        public double FixedXRes { get; set; }
        public double FixedYRes { get; set; }
        public double FixedZRes { get; set; }
        public Vec3 FixedAabbMin { get; set; }
        public Vec3 FixedAabbMax { get; set; }
        public Vec3 FixedCenter { get; set; }

        public int MovingXSize { get; set; }
        public int MovingYSize { get; set; }
        public int MovingZSize { get; set; }
        public double MovingXRes { get; set; }
        public double MovingYRes { get; set; }
        public double MovingZRes { get; set; }
        public Vec3 MovingAabbMin { get; set; }
        public Vec3 MovingAabbMax { get; set; }
        public Vec3 MovingCenter { get; set; }

        public Vec3 CenterOffset { get; set; }   // moving center minus fixed center
        public double CenterOffsetMagnitude { get; set; }

        // Raw overlap in absolute patient coordinates (meaningful only if FORs match).
        public Vec3 RawOverlapExtent { get; set; }       // per-axis overlap length in mm
        public double RawOverlapVolumeCm3 { get; set; }
        public double RawOverlapPercentOfFixed { get; set; }
        public double RawOverlapPercentOfMoving { get; set; }
        public bool RawHasOverlap { get; set; }

        // Centered overlap: after translating moving AABB so its center equals fixed
        // center. Mirrors what SimpleITK does with GEOMETRY-mode centering.
        public Vec3 CenteredOverlapExtent { get; set; }
        public double CenteredOverlapVolumeCm3 { get; set; }
        public double CenteredOverlapPercentOfFixed { get; set; }
        public double CenteredOverlapPercentOfMoving { get; set; }

        public double FixedVolumeCm3 { get; set; }
        public double MovingVolumeCm3 { get; set; }

        public double FixedExtentX => FixedAabbMax.X - FixedAabbMin.X;
        public double FixedExtentY => FixedAabbMax.Y - FixedAabbMin.Y;
        public double FixedExtentZ => FixedAabbMax.Z - FixedAabbMin.Z;
        public double MovingExtentX => MovingAabbMax.X - MovingAabbMin.X;
        public double MovingExtentY => MovingAabbMax.Y - MovingAabbMin.Y;
        public double MovingExtentZ => MovingAabbMax.Z - MovingAabbMin.Z;

        public VolumeOverlapVerdict Verdict
        {
            get
            {
                double key = FORMatch ? RawOverlapPercentOfFixed : CenteredOverlapPercentOfFixed;
                if (key < 50.0) return VolumeOverlapVerdict.Fail;
                if (key < 70.0) return VolumeOverlapVerdict.Warning;
                return VolumeOverlapVerdict.Ok;
            }
        }

        public string FormatSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== Volume FOV / overlap diagnostic ===");
            sb.AppendLine($"  Fixed ({FixedId}): {FixedXSize}x{FixedYSize}x{FixedZSize} @ " +
                          $"{FixedXRes:F2}x{FixedYRes:F2}x{FixedZRes:F2} mm");
            sb.AppendLine($"    bbox X=[{FixedAabbMin.X,8:F1},{FixedAabbMax.X,8:F1}]  " +
                          $"Y=[{FixedAabbMin.Y,8:F1},{FixedAabbMax.Y,8:F1}]  " +
                          $"Z=[{FixedAabbMin.Z,8:F1},{FixedAabbMax.Z,8:F1}]  mm");
            sb.AppendLine($"    extent (mm): {FixedExtentX:F1} x {FixedExtentY:F1} x {FixedExtentZ:F1}   " +
                          $"volume {FixedVolumeCm3:F0} cm^3");
            sb.AppendLine($"    FOR: {TruncateFor(FixedFOR)}");
            sb.AppendLine($"  Moving ({MovingId}): {MovingXSize}x{MovingYSize}x{MovingZSize} @ " +
                          $"{MovingXRes:F2}x{MovingYRes:F2}x{MovingZRes:F2} mm");
            sb.AppendLine($"    bbox X=[{MovingAabbMin.X,8:F1},{MovingAabbMax.X,8:F1}]  " +
                          $"Y=[{MovingAabbMin.Y,8:F1},{MovingAabbMax.Y,8:F1}]  " +
                          $"Z=[{MovingAabbMin.Z,8:F1},{MovingAabbMax.Z,8:F1}]  mm");
            sb.AppendLine($"    extent (mm): {MovingExtentX:F1} x {MovingExtentY:F1} x {MovingExtentZ:F1}   " +
                          $"volume {MovingVolumeCm3:F0} cm^3");
            sb.AppendLine($"    FOR: {TruncateFor(MovingFOR)}");
            sb.AppendLine();
            sb.AppendLine($"  FOR match: {(FORMatch ? "YES" : "NO - different patient frames, compare with care")}");
            sb.AppendLine($"  Center offset (moving - fixed): " +
                          $"({CenterOffset.X:F1}, {CenterOffset.Y:F1}, {CenterOffset.Z:F1}) mm   " +
                          $"|offset| = {CenterOffsetMagnitude:F1} mm");
            sb.AppendLine();
            sb.AppendLine("  Raw overlap (volumes as stored in patient coords):");
            if (!RawHasOverlap)
            {
                sb.AppendLine("    NO raw overlap - volumes do not intersect in absolute coordinates.");
            }
            else
            {
                sb.AppendLine($"    overlap mm: X={RawOverlapExtent.X:F1}  Y={RawOverlapExtent.Y:F1}  Z={RawOverlapExtent.Z:F1}");
                sb.AppendLine($"    overlap volume: {RawOverlapVolumeCm3:F0} cm^3");
                sb.AppendLine($"    overlap / fixed  = {RawOverlapPercentOfFixed:F1}%");
                sb.AppendLine($"    overlap / moving = {RawOverlapPercentOfMoving:F1}%");
            }
            sb.AppendLine();
            sb.AppendLine("  Centered overlap (after aligning volume centers - mimics SimpleITK GEOMETRY init):");
            sb.AppendLine($"    overlap mm: X={CenteredOverlapExtent.X:F1}  Y={CenteredOverlapExtent.Y:F1}  Z={CenteredOverlapExtent.Z:F1}");
            sb.AppendLine($"    overlap volume: {CenteredOverlapVolumeCm3:F0} cm^3");
            sb.AppendLine($"    overlap / fixed  = {CenteredOverlapPercentOfFixed:F1}%");
            sb.AppendLine($"    overlap / moving = {CenteredOverlapPercentOfMoving:F1}%");
            sb.AppendLine();
            sb.AppendLine($"  Verdict: {Verdict}");
            if (Verdict != VolumeOverlapVerdict.Ok)
            {
                sb.AppendLine("    DIR on this pair is likely to fail or produce large spurious deformations.");
                sb.AppendLine("    Consider cropping both volumes to their common anatomical region first.");
            }
            return sb.ToString();
        }

        private static string TruncateFor(string forId)
        {
            if (string.IsNullOrEmpty(forId)) return "(missing)";
            const int head = 16;
            const int tail = 8;
            return forId.Length <= head + tail + 3
                ? forId
                : forId.Substring(0, head) + "..." + forId.Substring(forId.Length - tail);
        }
    }

    public enum VolumeOverlapVerdict
    {
        Ok,
        Warning,
        Fail
    }
}
