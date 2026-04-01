using System;
using System.Linq;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Models;
using ESAPI_EQD2Viewer.Core.Models;

namespace ESAPI_EQD2Viewer.Services
{
    public class DVHService : IDVHCalculation
    {
        public DVHSummary BuildPhysicalSummaryFromCurve(DvhCurveData dvh, string planId)
        {
            return new DVHSummary
            {
                StructureId = dvh.StructureId,
                PlanId = planId,
                Type = "Physical",
                DMax = dvh.DMaxGy,
                DMean = dvh.DMeanGy,
                DMin = dvh.DMinGy,
                Volume = dvh.VolumeCc
            };
        }

        public DVHSummary BuildEQD2SummaryFromCurve(DvhCurveData dvh, string planId,
            int numberOfFractions, double alphaBeta, EQD2MeanMethod meanMethod)
        {
            double eqd2Dmax = EQD2Calculator.ToEQD2(dvh.DMaxGy, numberOfFractions, alphaBeta);
            double eqd2Dmin = EQD2Calculator.ToEQD2(dvh.DMinGy, numberOfFractions, alphaBeta);
            double eqd2Dmean;

            if (meanMethod == EQD2MeanMethod.Differential && dvh.Curve != null)
            {
                var curvePoints = dvh.Curve.Select(p => new DoseVolumePoint(p[0], p[1])).ToArray();
                eqd2Dmean = EQD2Calculator.CalculateMeanEQD2FromDVH(curvePoints, numberOfFractions, alphaBeta);
            }
            else
            {
                eqd2Dmean = EQD2Calculator.ToEQD2(dvh.DMeanGy, numberOfFractions, alphaBeta);
            }

            return new DVHSummary
            {
                StructureId = dvh.StructureId,
                PlanId = planId,
                Type = "EQD2",
                DMax = eqd2Dmax,
                DMean = eqd2Dmean,
                DMin = eqd2Dmin,
                Volume = dvh.VolumeCc
            };
        }

        public DoseVolumePoint[] CalculateDVHFromSummedDose(
            double[][] summedSlices, bool[][] structureMasks,
            double voxelVolumeCc, double maxDoseGy)
        {
            if (summedSlices == null || structureMasks == null || maxDoseGy <= 0)
                return new DoseVolumePoint[0];

            int numBins = DomainConstants.DvhHistogramBins;
            double binWidth = maxDoseGy * 1.1 / numBins;
            long[] histogram = new long[numBins];
            long totalVoxels = 0;
            int sliceCount = Math.Min(summedSlices.Length, structureMasks.Length);

            for (int z = 0; z < sliceCount; z++)
            {
                double[] doseSlice = summedSlices[z];
                bool[] mask = structureMasks[z];
                if (doseSlice == null || mask == null) continue;
                int len = Math.Min(doseSlice.Length, mask.Length);
                for (int i = 0; i < len; i++)
                {
                    if (!mask[i]) continue;
                    totalVoxels++;
                    if (doseSlice[i] <= 0) continue;
                    int bin = (int)(doseSlice[i] / binWidth);
                    if (bin >= numBins) bin = numBins - 1;
                    histogram[bin]++;
                }
            }

            if (totalVoxels == 0) return new DoseVolumePoint[0];

            var points = new DoseVolumePoint[numBins];
            long cumulative = totalVoxels;
            for (int i = 0; i < numBins; i++)
            {
                points[i] = new DoseVolumePoint(i * binWidth, cumulative * 100.0 / totalVoxels);
                cumulative -= histogram[i];
            }
            return points;
        }

        public DVHSummary BuildSummaryFromCurve(string structureId, string label, string type,
            DoseVolumePoint[] curve, double totalVolumeCc)
        {
            if (curve == null || curve.Length == 0)
                return new DVHSummary { StructureId = structureId, PlanId = label, Type = type, Volume = totalVolumeCc };

            double dMax = 0;
            for (int i = curve.Length - 1; i >= 0; i--)
                if (curve[i].VolumePercent > 0) { dMax = curve[i].DoseGy; break; }

            double dMin = 0;
            for (int i = 0; i < curve.Length; i++)
                if (curve[i].VolumePercent < 99.99) { dMin = (i > 0) ? curve[i - 1].DoseGy : 0; break; }

            double totalDose = 0, totalVolPct = 0;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double diff = curve[i].VolumePercent - curve[i + 1].VolumePercent;
                if (diff > 0) { totalDose += ((curve[i].DoseGy + curve[i + 1].DoseGy) / 2.0) * diff; totalVolPct += diff; }
            }

            return new DVHSummary
            {
                StructureId = structureId, PlanId = label, Type = type,
                DMax = dMax, DMean = totalVolPct > 0 ? totalDose / totalVolPct : 0,
                DMin = dMin, Volume = totalVolumeCc
            };
        }
    }
}
