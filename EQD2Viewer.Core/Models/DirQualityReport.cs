using System;
using System.Text;

namespace EQD2Viewer.Core.Models
{
    /// <summary>
    /// Quality report for a deformation vector field, inspired by AAPM TG-132 DIR QA
    /// recommendations. Produced by <c>EQD2Viewer.Core.Calculations.DeformationFieldAnalyzer</c>.
    /// </summary>
    public class DirQualityReport
    {
        public int XSize { get; set; }
        public int YSize { get; set; }
        public int ZSize { get; set; }
        public double XRes { get; set; }
        public double YRes { get; set; }
        public double ZRes { get; set; }

        public double JacobianMin { get; set; }
        public double JacobianMax { get; set; }
        public double JacobianMean { get; set; }
        public double JacobianStdDev { get; set; }
        public long JacobianVoxelCount { get; set; }

        /// <summary>Voxels with J &lt;= 0 (folded, non-invertible).</summary>
        public long JacobianFoldCount { get; set; }

        /// <summary>Voxels with 0 &lt; J &lt; 0.2 (severe compression).</summary>
        public long JacobianExtremeLowCount { get; set; }

        /// <summary>
        /// Voxels with 1.2 &lt; J &lt;= 2.0 (grey zone — elevated expansion, still within the
        /// [0, 2] tolerance Bosma et al. 2024 cite as generally acceptable).
        /// </summary>
        public long JacobianCautionHighCount { get; set; }

        /// <summary>
        /// Voxels with J &gt; 2.0 (implausible expansion per Bosma et al. 2024 / ESTRO 2021).
        /// Tagged-MR bounds for solid organs are considerably tighter (liver [0.85, 1.10],
        /// kidney [0.94, 1.07]).
        /// </summary>
        public long JacobianExtremeHighCount { get; set; }

        public double JacobianHistogramBinWidth { get; set; }
        public double JacobianHistogramMin { get; set; }
        public int[] JacobianHistogram { get; set; } = Array.Empty<int>();

        public double JacobianFoldPercent =>
            JacobianVoxelCount > 0 ? 100.0 * JacobianFoldCount / JacobianVoxelCount : 0.0;

        public double JacobianExtremePercent =>
            JacobianVoxelCount > 0
                ? 100.0 * (JacobianExtremeLowCount + JacobianExtremeHighCount) / JacobianVoxelCount
                : 0.0;

        public double JacobianCautionHighPercent =>
            JacobianVoxelCount > 0 ? 100.0 * JacobianCautionHighCount / JacobianVoxelCount : 0.0;

        public double DisplacementMinMm { get; set; }
        public double DisplacementMaxMm { get; set; }
        public double DisplacementMeanMm { get; set; }
        public double DisplacementP95Mm { get; set; }
        public double DisplacementP99Mm { get; set; }
        public long DisplacementVoxelCount { get; set; }

        public double BendingEnergyMean { get; set; }
        public double BendingEnergyTotal { get; set; }
        public long BendingEnergyVoxelCount { get; set; }

        /// <summary>
        /// Curl magnitude of the DVF per voxel (|curl u|). Rotational component per unit
        /// length; large values in homogeneous tissue suggest spurious rotation.
        /// Tagged-MR reference ranges: liver &lt; 0.2, kidney &lt; 0.1 (Zachiu et al. 2020).
        /// </summary>
        public double CurlMagMin { get; set; }
        public double CurlMagMax { get; set; }
        public double CurlMagMean { get; set; }
        public double CurlMagStdDev { get; set; }
        public long CurlVoxelCount { get; set; }

        /// <summary>Voxels with |curl u| &gt; 0.2 (above liver tagged-MR threshold).</summary>
        public long CurlOverLiverThresholdCount { get; set; }

        public double CurlHistogramBinWidth { get; set; }
        public double CurlHistogramMax { get; set; }
        public int[] CurlHistogram { get; set; } = Array.Empty<int>();

        public double CurlOverLiverThresholdPercent =>
            CurlVoxelCount > 0 ? 100.0 * CurlOverLiverThresholdCount / CurlVoxelCount : 0.0;

        public TimeSpan ComputeDuration { get; set; }

        public DirQualityVerdict Verdict
        {
            get
            {
                if (JacobianFoldCount > 0) return DirQualityVerdict.Fail;
                if (JacobianExtremePercent > 1.0) return DirQualityVerdict.Caution;
                return DirQualityVerdict.Pass;
            }
        }

        public string FormatSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("DIR Quality Report (AAPM TG-132 style)");
            sb.AppendLine($"  Grid: {XSize}x{YSize}x{ZSize} @ {XRes:F2}x{YRes:F2}x{ZRes:F2} mm");
            sb.AppendLine($"  Analyzed interior voxels: {JacobianVoxelCount:N0}");
            sb.AppendLine($"  Compute time: {ComputeDuration.TotalSeconds:F1} s");
            sb.AppendLine();
            sb.AppendLine("  Jacobian determinant:");
            sb.AppendLine($"    min    = {JacobianMin:F3}");
            sb.AppendLine($"    max    = {JacobianMax:F3}");
            sb.AppendLine($"    mean   = {JacobianMean:F3}");
            sb.AppendLine($"    stddev = {JacobianStdDev:F3}");
            sb.AppendLine($"    folds        (J <= 0)         : {JacobianFoldCount:N0} ({JacobianFoldPercent:F4}%)");
            sb.AppendLine($"    extreme low  (J < 0.2)        : {JacobianExtremeLowCount:N0}");
            sb.AppendLine($"    caution high (1.2 < J <= 2.0) : {JacobianCautionHighCount:N0} ({JacobianCautionHighPercent:F2}%)");
            sb.AppendLine($"    extreme high (J > 2.0)        : {JacobianExtremeHighCount:N0}");
            sb.AppendLine();
            sb.AppendLine("  Curl magnitude |curl u| (unitless):");
            sb.AppendLine($"    min    = {CurlMagMin:F4}");
            sb.AppendLine($"    max    = {CurlMagMax:F4}");
            sb.AppendLine($"    mean   = {CurlMagMean:F4}");
            sb.AppendLine($"    stddev = {CurlMagStdDev:F4}");
            sb.AppendLine($"    over liver threshold (|curl| > 0.2): {CurlOverLiverThresholdCount:N0} ({CurlOverLiverThresholdPercent:F2}%)");
            sb.AppendLine();
            sb.AppendLine("  Displacement magnitude (mm):");
            sb.AppendLine($"    min  = {DisplacementMinMm:F2}");
            sb.AppendLine($"    max  = {DisplacementMaxMm:F2}");
            sb.AppendLine($"    mean = {DisplacementMeanMm:F2}");
            sb.AppendLine($"    p95  = {DisplacementP95Mm:F2}");
            sb.AppendLine($"    p99  = {DisplacementP99Mm:F2}");
            sb.AppendLine();
            sb.AppendLine("  Bending energy (mm^-2):");
            sb.AppendLine($"    mean per voxel = {BendingEnergyMean:E3}");
            sb.AppendLine($"    total          = {BendingEnergyTotal:E3}");
            sb.AppendLine($"    voxels sampled = {BendingEnergyVoxelCount:N0}");
            sb.AppendLine();
            sb.AppendLine($"  Verdict: {Verdict}");
            return sb.ToString();
        }
    }

    public enum DirQualityVerdict
    {
        Pass,
        Caution,
        Fail
    }
}
