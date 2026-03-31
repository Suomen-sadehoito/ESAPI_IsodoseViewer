using System.Linq;
using EQD2Viewer.Core.Models;

namespace EQD2Viewer.Core.Calculations
{
    /// <summary>
    /// Unified EQD2 calculator for both voxel-level isodose rendering and DVH curve conversion.
    /// EQD2 = D * (d + alpha/beta) / (2 + alpha/beta), where d = D/n
    /// </summary>
    public static class EQD2Calculator
    {
        public static double ToEQD2(double totalDoseGy, int numberOfFractions, double alphaBeta)
        {
            if (numberOfFractions <= 0 || alphaBeta <= 0)
                return totalDoseGy;

            double dosePerFraction = totalDoseGy / numberOfFractions;
            return totalDoseGy * (dosePerFraction + alphaBeta) / (2.0 + alphaBeta);
        }

        public static void GetVoxelScalingFactors(int numberOfFractions, double alphaBeta,
            out double quadraticFactor, out double linearFactor)
        {
            double denom = 2.0 + alphaBeta;
            if (numberOfFractions <= 0 || denom <= 0)
            {
                quadraticFactor = 0;
                linearFactor = 1.0;
                return;
            }

            quadraticFactor = 1.0 / (numberOfFractions * denom);
            linearFactor = alphaBeta / denom;
        }

        public static double ToEQD2Fast(double totalDoseGy, double quadraticFactor, double linearFactor)
        {
            return totalDoseGy * totalDoseGy * quadraticFactor + totalDoseGy * linearFactor;
        }

        public static DoseVolumePoint[] ConvertCurveToEQD2(DoseVolumePoint[] originalCurve, int numberOfFractions, double alphaBeta)
        {
            if (originalCurve == null || originalCurve.Length == 0)
                return new DoseVolumePoint[0];

            return originalCurve.Select(p => new DoseVolumePoint(
                ToEQD2(p.DoseGy, numberOfFractions, alphaBeta),
                p.VolumePercent
            )).ToArray();
        }

        public static double CalculateMeanEQD2FromDVH(DoseVolumePoint[] cumulativeCurve, int numberOfFractions, double alphaBeta)
        {
            if (cumulativeCurve == null || cumulativeCurve.Length < 2)
                return 0.0;

            double totalVolume = cumulativeCurve[0].VolumePercent;
            if (totalVolume <= 0)
                return 0.0;

            double totalBioDose = 0;

            for (int i = 0; i < cumulativeCurve.Length - 1; i++)
            {
                DoseVolumePoint p1 = cumulativeCurve[i];
                DoseVolumePoint p2 = cumulativeCurve[i + 1];
                double volumeSegment = p1.VolumePercent - p2.VolumePercent;

                if (volumeSegment > 0)
                {
                    double midDose = (p1.DoseGy + p2.DoseGy) / 2.0;
                    double eqd2Segment = ToEQD2(midDose, numberOfFractions, alphaBeta);
                    totalBioDose += eqd2Segment * volumeSegment;
                }
            }

            return totalBioDose / totalVolume;
        }
    }
}