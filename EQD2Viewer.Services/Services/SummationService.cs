using EQD2Viewer.Core.Logging;
using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Services
{
    /// <summary>
    /// Two-phase dose summation with per-plan physical dose retention.
    /// 
    /// Phase 1 -- PrepareData() -- UI thread:
    ///   Loads plan data through ISummationDataLoader into plain arrays.
    ///   Rasterizes structure contours into masks for DVH.
    ///   Zero ESAPI dependencies -- all data access is via ISummationDataLoader.
    /// 
    /// Phase 2 -- ComputeAsync() -- background:
    ///   Accumulates per-plan physical dose at each voxel.
    ///   Computes an EQD2 display sum using the configured global alpha/beta.
    /// 
    /// Post-compute:
    ///   RecomputeEQD2DisplayAsync() -- recalculates display sum with a new alpha/beta.
    ///   ComputeStructureEQD2DVH()   -- per-structure DVH with structure-specific alpha/beta.
    /// 
    /// Memory: Stores N * W * H * Z * 8 bytes for N plans' physical doses,
    /// plus W * H * Z * 8 bytes for the display EQD2 sum.
    /// Typical: 2 plans, 512x512x200 = ~1.2 GB. Acceptable on clinical workstations.
    /// </summary>
    public class SummationService : ISummationService
    {
        private List<CachedPlanData>? _cachedPlans;
        private CachedRefGeometry? _refGeo;
        private SummationConfig? _config;
        private int _refW, _refH, _refZ;
        private int _refHuOffset;
        private string _referenceFOR = "";
        private double _voxelVolumeCc;

        private Dictionary<string, bool[][]>? _structureMasks;
        private List<string>? _cachedStructureIds;

        private double[][][]? _perPlanPhysicalSlices;
        private double[][]? _summedSlices;

        private double _summedReferenceDoseGy;
        private double _maxDoseGy;
        private int _maxSliceZ, _maxPixelX, _maxPixelY;
        private double _currentDisplayAlphaBeta;
        private volatile bool _hasSummedDose;
        private bool _disposed;

        private readonly VolumeData _referenceCtImage;
        private readonly ISummationDataLoader _dataLoader;
        private readonly List<RegistrationData> _registrations;
        private readonly IDeformationFieldLoader? _dfLoader;

        public bool HasSummedDose => _hasSummedDose;
        public double SummedReferenceDoseGy => _summedReferenceDoseGy;
        public double MaxDoseGy => _maxDoseGy;
        public int SliceCount => _refZ;

        public SummationService(VolumeData referenceCtImage, ISummationDataLoader dataLoader,
            List<RegistrationData> registrations, IDeformationFieldLoader? dfLoader = null)
        {
            _referenceCtImage = referenceCtImage ?? throw new ArgumentNullException(nameof(referenceCtImage));
            _dataLoader = dataLoader ?? throw new ArgumentNullException(nameof(dataLoader));
            _registrations = registrations ?? new List<RegistrationData>();
            _dfLoader = dfLoader;
        }

        public SummationResult PrepareData(SummationConfig config)
        {
            if (config == null || config.Plans.Count == 0)
                return new SummationResult { Success = false, StatusMessage = "No plans configured." };

            if (!config.Plans.Any(p => p.IsReference))
                return new SummationResult { Success = false, StatusMessage = "No reference plan selected." };

            try
            {
                _config = config;
                _refW = _referenceCtImage.XSize;
                _refH = _referenceCtImage.YSize;
                _refZ = _referenceCtImage.ZSize;
                _currentDisplayAlphaBeta = config.GlobalAlphaBeta;

                _voxelVolumeCc = (_referenceCtImage.XRes * _referenceCtImage.YRes * _referenceCtImage.ZRes) / 1000.0;

                _refGeo = new CachedRefGeometry
                {
                    Ox = _referenceCtImage.Origin.X,
                    Oy = _referenceCtImage.Origin.Y,
                    Oz = _referenceCtImage.Origin.Z,
                    Xx = _referenceCtImage.XDirection.X * _referenceCtImage.XRes,
                    Xy = _referenceCtImage.XDirection.Y * _referenceCtImage.XRes,
                    Xz = _referenceCtImage.XDirection.Z * _referenceCtImage.XRes,
                    Yx = _referenceCtImage.YDirection.X * _referenceCtImage.YRes,
                    Yy = _referenceCtImage.YDirection.Y * _referenceCtImage.YRes,
                    Yz = _referenceCtImage.YDirection.Z * _referenceCtImage.YRes,
                    Zx = _referenceCtImage.ZDirection.X * _referenceCtImage.ZRes,
                    Zy = _referenceCtImage.ZDirection.Y * _referenceCtImage.ZRes,
                    Zz = _referenceCtImage.ZDirection.Z * _referenceCtImage.ZRes,
                };

                _refHuOffset = _referenceCtImage.HuOffset;
                _referenceFOR = _referenceCtImage.FOR ?? "";

                _cachedPlans = new List<CachedPlanData>();
                foreach (var entry in config.Plans)
                {
                    var cached = CachePlanData(entry, config.Method, config.GlobalAlphaBeta, _referenceFOR);
                    if (cached == null)
                        return new SummationResult { Success = false, StatusMessage = $"Could not load plan: {entry.DisplayLabel}" };
                    _cachedPlans.Add(cached);
                }

                CacheStructureMasks();

                return new SummationResult { Success = true, StatusMessage = $"Loaded {config.Plans.Count} plans, {_refZ} slices. Computing..." };
            }
            catch (Exception ex)
            {
                SimpleLogger.Error("PrepareData failed", ex);
                return new SummationResult { Success = false, StatusMessage = $"Load error: {ex.Message}" };
            }
        }

        public Task<SummationResult> ComputeAsync(IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() => ComputeCore(progress, ct), ct);
        }

        private SummationResult ComputeCore(IProgress<int>? progress, CancellationToken ct)
        {
            try
            {
                int refW = _refW, refH = _refH, refZ = _refZ;
                int planCount = _cachedPlans!.Count;
                int sliceSize = refW * refH;

                _perPlanPhysicalSlices = new double[planCount][][];
                for (int p = 0; p < planCount; p++)
                    _perPlanPhysicalSlices[p] = new double[refZ][];

                _summedSlices = new double[refZ][];
                double globalMax = 0;
                int maxZ = 0, maxX = 0, maxY = 0;

                for (int z = 0; z < refZ; z++)
                {
                    ct.ThrowIfCancellationRequested();

                    for (int p = 0; p < planCount; p++)
                    {
                        double[] planSlice = new double[sliceSize];
                        var cp = _cachedPlans![p];

                        if (cp.IsReference)
                            AccumulatePhysicalDirect(cp, z, refW, refH, planSlice);
                        else if (cp.DeformationField != null)
                            AccumulatePhysicalDeformable(cp, z, refW, refH, planSlice);
                        else if (cp.TransformMatrix != null)
                            AccumulatePhysicalRegistered(cp, z, refW, refH, planSlice);
                        else
                            AccumulatePhysicalDirect(cp, z, refW, refH, planSlice);

                        _perPlanPhysicalSlices[p][z] = planSlice;
                    }

                    double[] eqd2Slice = new double[sliceSize];
                    for (int p = 0; p < planCount; p++)
                    {
                        var cp = _cachedPlans![p];
                        double[] phys = _perPlanPhysicalSlices[p][z];
                        double weight = cp.Weight;
                        bool useEqd2 = cp.UseEQD2;
                        double eq = cp.EQD2Q, el = cp.EQD2L;

                        for (int i = 0; i < sliceSize; i++)
                        {
                            double d = phys[i];
                            if (d <= 0) continue;
                            double eqd2 = useEqd2 ? (d * d * eq + d * el) : d;
                            eqd2Slice[i] += eqd2 * weight;
                        }
                    }

                    for (int i = 0; i < sliceSize; i++)
                    {
                        if (eqd2Slice[i] > globalMax)
                        {
                            globalMax = eqd2Slice[i];
                            maxZ = z; maxX = i % refW; maxY = i / refW;
                        }
                    }

                    _summedSlices[z] = eqd2Slice;

                    if (z % RenderConstants.SummationProgressInterval == 0)
                        progress?.Report((int)((z + 1) * 100.0 / refZ));
                }

                progress?.Report(100);
                _maxDoseGy = globalMax;
                _maxSliceZ = maxZ; _maxPixelX = maxX; _maxPixelY = maxY;
                _summedReferenceDoseGy = ComputeReferenceDose(_config!, _currentDisplayAlphaBeta);
                _hasSummedDose = true;

                string label = _config!.Method == SummationMethod.EQD2 ? "EQD2" : "Physical";
                return new SummationResult
                {
                    Success = true,
                    MaxDoseGy = globalMax,
                    TotalReferenceDoseGy = _summedReferenceDoseGy,
                    SliceCount = refZ,
                    MaxDoseSliceZ = maxZ,
                    MaxDosePixelX = maxX,
                    MaxDosePixelY = maxY,
                    StatusMessage = $"[{label} Sum] {_config.Plans.Count} plans | Max: {globalMax:F2} Gy | Ref: {_summedReferenceDoseGy:F2} Gy"
                };
            }
            catch (OperationCanceledException)
            {
                _hasSummedDose = false;
                return new SummationResult { Success = false, StatusMessage = "Summation cancelled." };
            }
            catch (Exception ex)
            {
                _hasSummedDose = false;
                SimpleLogger.Error("ComputeCore failed", ex);
                return new SummationResult { Success = false, StatusMessage = $"Compute error: {ex.Message}" };
            }
        }

        public Task<SummationResult> RecomputeEQD2DisplayAsync(double displayAlphaBeta,
            IProgress<int>? progress, CancellationToken ct)
        {
            return Task.Run(() =>
   {
       try
       {
           if (_perPlanPhysicalSlices == null || _cachedPlans == null)
               return new SummationResult { Success = false, StatusMessage = "No summation data available." };

           _currentDisplayAlphaBeta = displayAlphaBeta;

           var planFactors = new (double Q, double L, double Weight, bool UseEqd2)[_cachedPlans.Count];
           for (int p = 0; p < _cachedPlans.Count; p++)
           {
               var cp = _cachedPlans[p];
               bool useEqd2 = _config!.Method == SummationMethod.EQD2
                && cp.Entry.NumberOfFractions > 0 && displayAlphaBeta > 0;
               double q = 0, l = 1.0;
               if (useEqd2)
                   EQD2Calculator.GetVoxelScalingFactors(cp.Entry.NumberOfFractions, displayAlphaBeta, out q, out l);
               planFactors[p] = (q, l, cp.Weight, useEqd2);
           }

           int refZ = _refZ, sliceSize = _refW * _refH;
           int planCount = _cachedPlans.Count;
           double globalMax = 0;
           int maxZ = 0, maxX = 0, maxY = 0;

           for (int z = 0; z < refZ; z++)
           {
               ct.ThrowIfCancellationRequested();
               double[] eqd2Slice = new double[sliceSize];
               for (int p = 0; p < planCount; p++)
               {
                   double[] phys = _perPlanPhysicalSlices[p][z];
                   if (phys == null) continue;
                   var (eq, el, weight, useEqd2) = planFactors[p];
                   for (int i = 0; i < sliceSize; i++)
                   {
                       double d = phys[i];
                       if (d <= 0) continue;
                       double eqd2 = useEqd2 ? (d * d * eq + d * el) : d;
                       eqd2Slice[i] += eqd2 * weight;
                   }
               }
               for (int i = 0; i < sliceSize; i++)
               {
                   if (eqd2Slice[i] > globalMax)
                   {
                       globalMax = eqd2Slice[i];
                       maxZ = z; maxX = i % _refW; maxY = i / _refW;
                   }
               }
               _summedSlices![z] = eqd2Slice;
               if (z % RenderConstants.SummationProgressInterval == 0)
                   progress?.Report((int)((z + 1) * 100.0 / refZ));
           }

           progress?.Report(100);
           _maxDoseGy = globalMax;
           _maxSliceZ = maxZ; _maxPixelX = maxX; _maxPixelY = maxY;
           _summedReferenceDoseGy = ComputeReferenceDose(_config!, displayAlphaBeta);

           string label = _config!.Method == SummationMethod.EQD2 ? "EQD2" : "Physical";
           return new SummationResult
           {
               Success = true,
               MaxDoseGy = globalMax,
               TotalReferenceDoseGy = _summedReferenceDoseGy,
               SliceCount = refZ,
               MaxDoseSliceZ = maxZ,
               MaxDosePixelX = maxX,
               MaxDosePixelY = maxY,
               StatusMessage = $"[{label} Sum] {_config.Plans.Count} plans | Max: {globalMax:F2} Gy | Ref: {_summedReferenceDoseGy:F2} Gy | a/b={displayAlphaBeta:F1}"
           };
       }
       catch (OperationCanceledException) { return new SummationResult { Success = false, StatusMessage = "Recomputation cancelled." }; }
       catch (Exception ex) { SimpleLogger.Error("RecomputeEQD2Display failed", ex); return new SummationResult { Success = false, StatusMessage = $"Error: {ex.Message}" }; }
   }, ct);
        }

        public DoseVolumePoint[] ComputeStructureEQD2DVH(string structureId,
        double structureAlphaBeta, double maxDoseGy)
        {
            if (_perPlanPhysicalSlices == null || _structureMasks == null || maxDoseGy <= 0)
                return new DoseVolumePoint[0];
            if (!_structureMasks.TryGetValue(structureId, out var masks))
                return new DoseVolumePoint[0];

            int planCount = _cachedPlans!.Count;
            int sliceCount = Math.Min(_refZ, masks.Length);

            var factors = new (double Q, double L, double Weight, bool UseEqd2)[planCount];
            for (int p = 0; p < planCount; p++)
            {
                var cp = _cachedPlans![p];
                bool useEqd2 = _config!.Method == SummationMethod.EQD2
                && cp.Entry.NumberOfFractions > 0 && structureAlphaBeta > 0;
                double q = 0, l = 1.0;
                if (useEqd2)
                    EQD2Calculator.GetVoxelScalingFactors(cp.Entry.NumberOfFractions, structureAlphaBeta, out q, out l);
                factors[p] = (q, l, cp.Weight, useEqd2);
            }

            int numBins = DomainConstants.DvhHistogramBins;
            double binWidth = maxDoseGy * 1.1 / numBins;
            long[] histogram = new long[numBins];
            long totalVoxels = 0;

            for (int z = 0; z < sliceCount; z++)
            {
                bool[] mask = masks[z];
                if (mask == null) continue;
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i]) continue;
                    totalVoxels++;
                    double eqd2Sum = 0;
                    for (int p = 0; p < planCount; p++)
                    {
                        double[] phys = _perPlanPhysicalSlices[p][z];
                        if (phys == null) continue;
                        double d = phys[i];
                        if (d <= 0) continue;
                        var (eq, el, weight, useEqd2) = factors[p];
                        double eqd2 = useEqd2 ? (d * d * eq + d * el) : d;
                        eqd2Sum += eqd2 * weight;
                    }
                    if (eqd2Sum <= 0) continue;
                    int bin = (int)(eqd2Sum / binWidth);
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

        private double ComputeReferenceDose(SummationConfig config, double alphaBeta)
        {
            double refDose = 0;
            foreach (var entry in config.Plans)
            {
                double planRef = entry.TotalDoseGy * (entry.PlanNormalization / 100.0);
                if (config.Method == SummationMethod.EQD2 && alphaBeta > 0)
                    planRef = EQD2Calculator.ToEQD2(planRef, entry.NumberOfFractions, alphaBeta);
                refDose += planRef * entry.Weight;
            }
            return refDose;
        }

        private void AccumulatePhysicalDirect(CachedPlanData cp, int sliceZ, int refW, int refH, double[] sliceData)
        {
            var dg = cp.DoseGeo;
            var rg = _refGeo!;

            double baseWx = rg.Ox + sliceZ * rg.Zx;
            double baseWy = rg.Oy + sliceZ * rg.Zy;
            double baseWz = rg.Oz + sliceZ * rg.Zz;

            double diffX = baseWx - dg.Ox, diffY = baseWy - dg.Oy, diffZ = baseWz - dg.Oz;

            double zDose = (diffX * dg.ZDx + diffY * dg.ZDy + diffZ * dg.ZDz) / dg.ZRes;
            int doseSliceZ = (int)Math.Round(zDose);
            if (doseSliceZ < 0 || doseSliceZ >= dg.ZSize) return;

            double baseDx = (diffX * dg.XDx + diffY * dg.XDy + diffZ * dg.XDz) / dg.XRes;
            double baseDy = (diffX * dg.YDx + diffY * dg.YDy + diffZ * dg.YDz) / dg.YRes;

            double dxPerPx = (rg.Xx * dg.XDx + rg.Xy * dg.XDy + rg.Xz * dg.XDz) / dg.XRes;
            double dyPerPx = (rg.Xx * dg.YDx + rg.Xy * dg.YDy + rg.Xz * dg.YDz) / dg.YRes;
            double dxPerPy = (rg.Yx * dg.XDx + rg.Yy * dg.XDy + rg.Yz * dg.XDz) / dg.XRes;
            double dyPerPy = (rg.Yx * dg.YDx + rg.Yy * dg.YDy + rg.Yz * dg.YDz) / dg.YRes;

            int dxSize = dg.XSize, dySize = dg.YSize;
            int[,] doseSlice = cp.DoseVoxels[doseSliceZ];
            double rawScale = cp.RawScale, rawOffset = cp.RawOffset, unitToGy = cp.UnitToGy;

            for (int py = 0; py < refH; py++)
            {
                double rx = baseDx + py * dxPerPy;
                double ry = baseDy + py * dyPerPy;
                int ro = py * refW;
                for (int px = 0; px < refW; px++)
                {
                    double fx = rx + px * dxPerPx;
                    double fy = ry + px * dyPerPx;
                    double dGy = ImageUtils.BilinearSampleRaw(doseSlice, dxSize, dySize, fx, fy, rawScale, rawOffset, unitToGy);
                    if (dGy > 0)
                        sliceData[ro + px] += dGy;
                }
            }
        }

        private void AccumulatePhysicalDeformable(CachedPlanData cp, int sliceZ, int refW, int refH, double[] sliceData)
        {
            var dg = cp.DoseGeo;
            var rg = _refGeo!;
            var dvf = cp.DeformationField!;
            bool fast = cp.DvfMatchesRef;

            // DVF origin/direction pre-multiplied for the slow path.
            double dvfOx = dvf.Origin.X, dvfOy = dvf.Origin.Y, dvfOz = dvf.Origin.Z;
            double dvfXDx = dvf.XDirection.X, dvfXDy = dvf.XDirection.Y, dvfXDz = dvf.XDirection.Z;
            double dvfYDx = dvf.YDirection.X, dvfYDy = dvf.YDirection.Y, dvfYDz = dvf.YDirection.Z;
            double dvfZDx = dvf.ZDirection.X, dvfZDy = dvf.ZDirection.Y, dvfZDz = dvf.ZDirection.Z;
            double invDvfXRes = 1.0 / dvf.XRes, invDvfYRes = 1.0 / dvf.YRes, invDvfZRes = 1.0 / dvf.ZRes;

            for (int py = 0; py < refH; py++)
            {
                int ro = py * refW;
                for (int px = 0; px < refW; px++)
                {
                    // World position of reference voxel (px, py, sliceZ).
                    double wx = rg.Ox + sliceZ * rg.Zx + py * rg.Yx + px * rg.Xx;
                    double wy = rg.Oy + sliceZ * rg.Zy + py * rg.Yy + px * rg.Xy;
                    double wz = rg.Oz + sliceZ * rg.Zz + py * rg.Yz + px * rg.Xz;

                    // Displacement lookup — fast path when DVF grid == reference grid,
                    // slow path otherwise (world → DVF index via projection on its direction vectors).
                    double dx = 0, dy = 0, dz = 0;
                    if (fast)
                    {
                        if (px < dvf.XSize && py < dvf.YSize && sliceZ < dvf.ZSize)
                        {
                            var v = dvf.Vectors[sliceZ][px, py];
                            dx = v.X; dy = v.Y; dz = v.Z;
                        }
                    }
                    else
                    {
                        double dvfDiffX = wx - dvfOx, dvfDiffY = wy - dvfOy, dvfDiffZ = wz - dvfOz;
                        int dvfIx = (int)Math.Round((dvfDiffX * dvfXDx + dvfDiffY * dvfXDy + dvfDiffZ * dvfXDz) * invDvfXRes);
                        int dvfIy = (int)Math.Round((dvfDiffX * dvfYDx + dvfDiffY * dvfYDy + dvfDiffZ * dvfYDz) * invDvfYRes);
                        int dvfIz = (int)Math.Round((dvfDiffX * dvfZDx + dvfDiffY * dvfZDy + dvfDiffZ * dvfZDz) * invDvfZRes);
                        if (dvfIx >= 0 && dvfIx < dvf.XSize && dvfIy >= 0 && dvfIy < dvf.YSize && dvfIz >= 0 && dvfIz < dvf.ZSize)
                        {
                            var v = dvf.Vectors[dvfIz][dvfIx, dvfIy];
                            dx = v.X; dy = v.Y; dz = v.Z;
                        }
                    }

                    // Mapped position in the moving (plan) image's world frame.
                    double mwx = wx + dx, mwy = wy + dy, mwz = wz + dz;

                    double diffX = mwx - dg.Ox, diffY = mwy - dg.Oy, diffZ = mwz - dg.Oz;
                    double fz = (diffX * dg.ZDx + diffY * dg.ZDy + diffZ * dg.ZDz) / dg.ZRes;

                    // Trilinear in Z: sample two adjacent slices and blend by fractional z.
                    int iz0 = (int)Math.Floor(fz);
                    double fzFrac = fz - iz0;
                    int iz1 = iz0 + 1;
                    if (iz0 < 0 || iz0 >= dg.ZSize)
                    {
                        if (iz1 < 0 || iz1 >= dg.ZSize) continue;
                        iz0 = iz1; fzFrac = 0.0;
                    }
                    else if (iz1 >= dg.ZSize) { iz1 = iz0; fzFrac = 0.0; }

                    double fx = (diffX * dg.XDx + diffY * dg.XDy + diffZ * dg.XDz) / dg.XRes;
                    double fy = (diffX * dg.YDx + diffY * dg.YDy + diffZ * dg.YDz) / dg.YRes;

                    double d0 = ImageUtils.BilinearSampleRaw(
                        cp.DoseVoxels[iz0], dg.XSize, dg.YSize, fx, fy,
                        cp.RawScale, cp.RawOffset, cp.UnitToGy);
                    double dGy;
                    if (iz1 != iz0 && fzFrac > 0.0)
                    {
                        double d1 = ImageUtils.BilinearSampleRaw(
                            cp.DoseVoxels[iz1], dg.XSize, dg.YSize, fx, fy,
                            cp.RawScale, cp.RawOffset, cp.UnitToGy);
                        dGy = d0 * (1.0 - fzFrac) + d1 * fzFrac;
                    }
                    else dGy = d0;

                    if (dGy > 0)
                        sliceData[ro + px] += dGy;
                }
            }
        }

        private void AccumulatePhysicalRegistered(CachedPlanData cp, int sliceZ, int refW, int refH, double[] sliceData)
        {
            var dg = cp.DoseGeo;
            var rg = _refGeo!;
            var M = cp.TransformMatrix!;

            double rpxX = M[0, 0] * rg.Xx + M[0, 1] * rg.Xy + M[0, 2] * rg.Xz;
            double rpxY = M[1, 0] * rg.Xx + M[1, 1] * rg.Xy + M[1, 2] * rg.Xz;
            double rpxZ = M[2, 0] * rg.Xx + M[2, 1] * rg.Xy + M[2, 2] * rg.Xz;

            double rpyX = M[0, 0] * rg.Yx + M[0, 1] * rg.Yy + M[0, 2] * rg.Yz;
            double rpyY = M[1, 0] * rg.Yx + M[1, 1] * rg.Yy + M[1, 2] * rg.Yz;
            double rpyZ = M[2, 0] * rg.Yx + M[2, 1] * rg.Yy + M[2, 2] * rg.Yz;

            double bwx = rg.Ox + sliceZ * rg.Zx;
            double bwy = rg.Oy + sliceZ * rg.Zy;
            double bwz = rg.Oz + sliceZ * rg.Zz;
            double rbX = M[0, 0] * bwx + M[0, 1] * bwy + M[0, 2] * bwz + M[0, 3];
            double rbY = M[1, 0] * bwx + M[1, 1] * bwy + M[1, 2] * bwz + M[1, 3];
            double rbZ = M[2, 0] * bwx + M[2, 1] * bwy + M[2, 2] * bwz + M[2, 3];

            double dOrigDotX = dg.Ox * dg.XDx + dg.Oy * dg.XDy + dg.Oz * dg.XDz;
            double dOrigDotY = dg.Ox * dg.YDx + dg.Oy * dg.YDy + dg.Oz * dg.YDz;
            double dOrigDotZ = dg.Ox * dg.ZDx + dg.Oy * dg.ZDy + dg.Oz * dg.ZDz;

            double baseFdx = ((rbX * dg.XDx + rbY * dg.XDy + rbZ * dg.XDz) - dOrigDotX) / dg.XRes;
            double baseFdy = ((rbX * dg.YDx + rbY * dg.YDy + rbZ * dg.YDz) - dOrigDotY) / dg.YRes;
            double baseFdz = ((rbX * dg.ZDx + rbY * dg.ZDy + rbZ * dg.ZDz) - dOrigDotZ) / dg.ZRes;

            double fdxPerPx = (rpxX * dg.XDx + rpxY * dg.XDy + rpxZ * dg.XDz) / dg.XRes;
            double fdyPerPx = (rpxX * dg.YDx + rpxY * dg.YDy + rpxZ * dg.YDz) / dg.YRes;
            double fdzPerPx = (rpxX * dg.ZDx + rpxY * dg.ZDy + rpxZ * dg.ZDz) / dg.ZRes;
            double fdxPerPy = (rpyX * dg.XDx + rpyY * dg.XDy + rpyZ * dg.XDz) / dg.XRes;
            double fdyPerPy = (rpyX * dg.YDx + rpyY * dg.YDy + rpyZ * dg.YDz) / dg.YRes;
            double fdzPerPy = (rpyX * dg.ZDx + rpyY * dg.ZDy + rpyZ * dg.ZDz) / dg.ZRes;

            int dxSize = dg.XSize, dySize = dg.YSize, dzSize = dg.ZSize;
            double rawScale = cp.RawScale, rawOffset = cp.RawOffset, unitToGy = cp.UnitToGy;

            for (int py = 0; py < refH; py++)
            {
                double rowFdx = baseFdx + py * fdxPerPy;
                double rowFdy = baseFdy + py * fdyPerPy;
                double rowFdz = baseFdz + py * fdzPerPy;
                int ro = py * refW;
                for (int px = 0; px < refW; px++)
                {
                    double fz = rowFdz + px * fdzPerPx;
                    int iz = (int)Math.Round(fz);
                    if (iz < 0 || iz >= dzSize) continue;
                    double fx = rowFdx + px * fdxPerPx;
                    double fy = rowFdy + px * fdyPerPx;
                    int[,] doseSlice = cp.DoseVoxels[iz];
                    double dGy = ImageUtils.BilinearSampleRaw(doseSlice, dxSize, dySize, fx, fy, rawScale, rawOffset, unitToGy);
                    if (dGy > 0)
                        sliceData[ro + px] += dGy;
                }
            }
        }

        public bool[]? GetStructureMask(string structureId, int sliceIndex)
        {
            if (_structureMasks == null || string.IsNullOrEmpty(structureId)) return null;
            if (!_structureMasks.TryGetValue(structureId, out var masks)) return null;
            if (sliceIndex < 0 || sliceIndex >= masks.Length) return null;
            return masks[sliceIndex];
        }

        public IReadOnlyList<string> GetCachedStructureIds()
        {
            return _cachedStructureIds ?? (IReadOnlyList<string>)new string[0];
        }

        public double GetVoxelVolumeCc() => _voxelVolumeCc;

        public double[]? GetSummedSlice(int sliceIndex)
        {
            if (!_hasSummedDose || _summedSlices == null) return null;
            if (sliceIndex < 0 || sliceIndex >= _summedSlices.Length) return null;
            return _summedSlices[sliceIndex];
        }

        public int[]? GetRegisteredCtSlice(string planDisplayLabel, int sliceIndex)
        {
            if (_cachedPlans == null || string.IsNullOrEmpty(planDisplayLabel)) return null;
            var cp = _cachedPlans.FirstOrDefault(p => p.Entry.DisplayLabel == planDisplayLabel && !p.IsReference);
            if (cp == null || cp.CtVoxels == null || cp.CtGeo == null) return null;
            if (sliceIndex < 0 || sliceIndex >= _refZ) return null;

            int refW = _refW, refH = _refH;
            int[] result = new int[refW * refH];
            var cg = cp.CtGeo;
            var rg = _refGeo!;

            if (cp.DeformationField != null)
            {
                var dvf = cp.DeformationField;
                bool fast = cp.DvfMatchesRef;
                int huOff = cp.CtHuOffset;
                int cxSize = cg.XSize, cySize = cg.YSize, czSize = cg.ZSize;

                double dvfOx = dvf.Origin.X, dvfOy = dvf.Origin.Y, dvfOz = dvf.Origin.Z;
                double dvfXDx = dvf.XDirection.X, dvfXDy = dvf.XDirection.Y, dvfXDz = dvf.XDirection.Z;
                double dvfYDx = dvf.YDirection.X, dvfYDy = dvf.YDirection.Y, dvfYDz = dvf.YDirection.Z;
                double dvfZDx = dvf.ZDirection.X, dvfZDy = dvf.ZDirection.Y, dvfZDz = dvf.ZDirection.Z;
                double invDvfXRes = 1.0 / dvf.XRes, invDvfYRes = 1.0 / dvf.YRes, invDvfZRes = 1.0 / dvf.ZRes;

                for (int py = 0; py < refH; py++)
                {
                    int ro = py * refW;
                    for (int px = 0; px < refW; px++)
                    {
                        // World position of reference voxel.
                        double wx = rg.Ox + sliceIndex * rg.Zx + py * rg.Yx + px * rg.Xx;
                        double wy = rg.Oy + sliceIndex * rg.Zy + py * rg.Yy + px * rg.Xy;
                        double wz = rg.Oz + sliceIndex * rg.Zz + py * rg.Yz + px * rg.Xz;

                        double dx = 0, dy = 0, dz = 0;
                        if (fast)
                        {
                            if (px < dvf.XSize && py < dvf.YSize && sliceIndex < dvf.ZSize)
                            {
                                var v = dvf.Vectors[sliceIndex][px, py];
                                dx = v.X; dy = v.Y; dz = v.Z;
                            }
                        }
                        else
                        {
                            double dvfDiffX = wx - dvfOx, dvfDiffY = wy - dvfOy, dvfDiffZ = wz - dvfOz;
                            int dvfIx = (int)Math.Round((dvfDiffX * dvfXDx + dvfDiffY * dvfXDy + dvfDiffZ * dvfXDz) * invDvfXRes);
                            int dvfIy = (int)Math.Round((dvfDiffX * dvfYDx + dvfDiffY * dvfYDy + dvfDiffZ * dvfYDz) * invDvfYRes);
                            int dvfIz = (int)Math.Round((dvfDiffX * dvfZDx + dvfDiffY * dvfZDy + dvfDiffZ * dvfZDz) * invDvfZRes);
                            if (dvfIx >= 0 && dvfIx < dvf.XSize && dvfIy >= 0 && dvfIy < dvf.YSize && dvfIz >= 0 && dvfIz < dvf.ZSize)
                            {
                                var v = dvf.Vectors[dvfIz][dvfIx, dvfIy];
                                dx = v.X; dy = v.Y; dz = v.Z;
                            }
                        }

                        double mwx = wx + dx, mwy = wy + dy, mwz = wz + dz;

                        // Moving world -> moving image indices (nearest-neighbor — this is a preview view).
                        double diffX = mwx - cg.Ox, diffY = mwy - cg.Oy, diffZ = mwz - cg.Oz;
                        double fz = (diffX * cg.ZDx + diffY * cg.ZDy + diffZ * cg.ZDz) / cg.ZRes;
                        int iz = (int)Math.Round(fz);
                        if (iz < 0 || iz >= czSize) continue;

                        int ix = (int)Math.Round((diffX * cg.XDx + diffY * cg.XDy + diffZ * cg.XDz) / cg.XRes);
                        int iy = (int)Math.Round((diffX * cg.YDx + diffY * cg.YDy + diffZ * cg.YDz) / cg.YRes);

                        if (ix >= 0 && ix < cxSize && iy >= 0 && iy < cySize)
                            result[ro + px] = cp.CtVoxels[iz][ix, iy] - huOff;
                    }
                }
            }
            else if (cp.TransformMatrix == null)
            {
                double baseWx = rg.Ox + sliceIndex * rg.Zx, baseWy = rg.Oy + sliceIndex * rg.Zy, baseWz = rg.Oz + sliceIndex * rg.Zz;
                double diffX = baseWx - cg.Ox, diffY = baseWy - cg.Oy, diffZ = baseWz - cg.Oz;
                double zCt = (diffX * cg.ZDx + diffY * cg.ZDy + diffZ * cg.ZDz) / cg.ZRes;
                int ctSliceZ = (int)Math.Round(zCt);
                if (ctSliceZ < 0 || ctSliceZ >= cg.ZSize) return result;
                double baseCx = (diffX * cg.XDx + diffY * cg.XDy + diffZ * cg.XDz) / cg.XRes;
                double baseCy = (diffX * cg.YDx + diffY * cg.YDy + diffZ * cg.YDz) / cg.YRes;
                double dxPerPx = (rg.Xx * cg.XDx + rg.Xy * cg.XDy + rg.Xz * cg.XDz) / cg.XRes;
                double dyPerPx = (rg.Xx * cg.YDx + rg.Xy * cg.YDy + rg.Xz * cg.YDz) / cg.YRes;
                double dxPerPy = (rg.Yx * cg.XDx + rg.Yy * cg.XDy + rg.Yz * cg.XDz) / cg.XRes;
                double dyPerPy = (rg.Yx * cg.YDx + rg.Yy * cg.YDy + rg.Yz * cg.YDz) / cg.YRes;
                int[,] ctSlice = cp.CtVoxels[ctSliceZ];
                int cxSize = cg.XSize, cySize = cg.YSize;
                int huOff = cp.CtHuOffset;
                for (int py = 0; py < refH; py++)
                {
                    double rx = baseCx + py * dxPerPy, ry = baseCy + py * dyPerPy;
                    int ro = py * refW;
                    for (int px = 0; px < refW; px++)
                    {
                        int ix = (int)Math.Round(rx + px * dxPerPx), iy = (int)Math.Round(ry + px * dyPerPx);
                        if (ix >= 0 && ix < cxSize && iy >= 0 && iy < cySize)
                            result[ro + px] = ctSlice[ix, iy] - huOff;
                    }
                }
            }
            else
            {
                var M = cp.TransformMatrix;
                double rpxX = M[0, 0] * rg.Xx + M[0, 1] * rg.Xy + M[0, 2] * rg.Xz;
                double rpxY = M[1, 0] * rg.Xx + M[1, 1] * rg.Xy + M[1, 2] * rg.Xz;
                double rpxZ = M[2, 0] * rg.Xx + M[2, 1] * rg.Xy + M[2, 2] * rg.Xz;
                double rpyX = M[0, 0] * rg.Yx + M[0, 1] * rg.Yy + M[0, 2] * rg.Yz;
                double rpyY = M[1, 0] * rg.Yx + M[1, 1] * rg.Yy + M[1, 2] * rg.Yz;
                double rpyZ = M[2, 0] * rg.Yx + M[2, 1] * rg.Yy + M[2, 2] * rg.Yz;
                double bwx = rg.Ox + sliceIndex * rg.Zx, bwy = rg.Oy + sliceIndex * rg.Zy, bwz = rg.Oz + sliceIndex * rg.Zz;
                double rbX = M[0, 0] * bwx + M[0, 1] * bwy + M[0, 2] * bwz + M[0, 3];
                double rbY = M[1, 0] * bwx + M[1, 1] * bwy + M[1, 2] * bwz + M[1, 3];
                double rbZ = M[2, 0] * bwx + M[2, 1] * bwy + M[2, 2] * bwz + M[2, 3];
                double cOrigDotX = cg.Ox * cg.XDx + cg.Oy * cg.XDy + cg.Oz * cg.XDz;
                double cOrigDotY = cg.Ox * cg.YDx + cg.Oy * cg.YDy + cg.Oz * cg.YDz;
                double cOrigDotZ = cg.Ox * cg.ZDx + cg.Oy * cg.ZDy + cg.Oz * cg.ZDz;
                double baseFcx = ((rbX * cg.XDx + rbY * cg.XDy + rbZ * cg.XDz) - cOrigDotX) / cg.XRes;
                double baseFcy = ((rbX * cg.YDx + rbY * cg.YDy + rbZ * cg.YDz) - cOrigDotY) / cg.YRes;
                double baseFcz = ((rbX * cg.ZDx + rbY * cg.ZDy + rbZ * cg.ZDz) - cOrigDotZ) / cg.ZRes;
                double fcxPerPx = (rpxX * cg.XDx + rpxY * cg.XDy + rpxZ * cg.XDz) / cg.XRes;
                double fcyPerPx = (rpxX * cg.YDx + rpxY * cg.YDy + rpxZ * cg.YDz) / cg.YRes;
                double fczPerPx = (rpxX * cg.ZDx + rpxY * cg.ZDy + rpxZ * cg.ZDz) / cg.ZRes;
                double fcxPerPy = (rpyX * cg.XDx + rpyY * cg.XDy + rpyZ * cg.XDz) / cg.XRes;
                double fcyPerPy = (rpyX * cg.YDx + rpyY * cg.YDy + rpyZ * cg.YDz) / cg.YRes;
                double fczPerPy = (rpyX * cg.ZDx + rpyY * cg.ZDy + rpyZ * cg.ZDz) / cg.ZRes;
                int cxSize = cg.XSize, cySize = cg.YSize, czSize = cg.ZSize;
                int huOff = cp.CtHuOffset;
                for (int py = 0; py < refH; py++)
                {
                    double rowFcx = baseFcx + py * fcxPerPy, rowFcy = baseFcy + py * fcyPerPy, rowFcz = baseFcz + py * fczPerPy;
                    int ro = py * refW;
                    for (int px = 0; px < refW; px++)
                    {
                        double fz = rowFcz + px * fczPerPx;
                        int iz = (int)Math.Round(fz);
                        if (iz < 0 || iz >= czSize) continue;
                        int ix = (int)Math.Round(rowFcx + px * fcxPerPx), iy = (int)Math.Round(rowFcy + px * fcyPerPx);
                        if (ix >= 0 && ix < cxSize && iy >= 0 && iy < cySize)
                            result[ro + px] = cp.CtVoxels[iz][ix, iy] - huOff;
                    }
                }
            }
            return result;
        }

        private void CacheStructureMasks()
        {
            _structureMasks = new Dictionary<string, bool[][]>();
            _cachedStructureIds = new List<string>();
            var refEntry = _config!.Plans.FirstOrDefault(p => p.IsReference);
            if (refEntry == null) return;
            var structures = _dataLoader.LoadStructureContours(refEntry.CourseId, refEntry.PlanId);
            if (structures == null || structures.Count == 0) return;

            double imgOx = _referenceCtImage.Origin.X, imgOy = _referenceCtImage.Origin.Y, imgOz = _referenceCtImage.Origin.Z;
            double xDirX = _referenceCtImage.XDirection.X, xDirY = _referenceCtImage.XDirection.Y, xDirZ = _referenceCtImage.XDirection.Z;
            double yDirX = _referenceCtImage.YDirection.X, yDirY = _referenceCtImage.YDirection.Y, yDirZ = _referenceCtImage.YDirection.Z;
            double xRes = _referenceCtImage.XRes, yRes = _referenceCtImage.YRes;

            foreach (var structure in structures)
            {
                if (structure.IsEmpty) continue;
                try
                {
                    bool[][] sliceMasks = new bool[_refZ][];
                    bool hasAnyContour = false;
                    for (int z = 0; z < _refZ; z++)
                    {
                        if (!structure.ContoursBySlice.TryGetValue(z, out var contourList) || contourList == null || contourList.Count == 0)
                        { sliceMasks[z] = null!; continue; }
                        hasAnyContour = true;
                        var masks = new List<bool[]>();
                        foreach (var contour in contourList)
                        {
                            if (contour.Length < 3) continue;
                            var pixelPoints = new Point2D[contour.Length];
                            for (int i = 0; i < contour.Length; i++)
                            {
                                double dx = contour[i][0] - imgOx, dy = contour[i][1] - imgOy, dz = contour[i][2] - imgOz;
                                pixelPoints[i] = new Point2D(
                                       (dx * xDirX + dy * xDirY + dz * xDirZ) / xRes,
                                 (dx * yDirX + dy * yDirY + dz * yDirZ) / yRes);
                            }
                            masks.Add(StructureRasterizer.RasterizePolygon(pixelPoints, _refW, _refH));
                        }
                        sliceMasks[z] = StructureRasterizer.CombineContourMasks(masks, _refW, _refH);
                    }
                    if (hasAnyContour)
                    {
                        _structureMasks[structure.Id] = sliceMasks;
                        _cachedStructureIds.Add(structure.Id);
                    }
                }
                catch (Exception ex) { SimpleLogger.Warning($"Could not rasterize structure '{structure.Id}': {ex.Message}"); }
            }
            SimpleLogger.Info($"Cached {_cachedStructureIds.Count} structure masks for DVH");
        }

        private CachedPlanData? CachePlanData(SummationPlanEntry entry, SummationMethod method,
       double alphaBeta, string referenceFOR)
        {
            var planDoseData = _dataLoader.LoadPlanDose(entry.CourseId, entry.PlanId, entry.TotalDoseGy);
            if (planDoseData == null) { SimpleLogger.Error($"Could not load plan dose: {entry.CourseId}/{entry.PlanId}"); return null; }

            var doseGeo = planDoseData.DoseGeometry;
            var scaling = planDoseData.Scaling;

            double eqd2Q = 0, eqd2L = 1.0;
            bool useEqd2 = method == SummationMethod.EQD2 && entry.NumberOfFractions > 0 && alphaBeta > 0;
            if (useEqd2) EQD2Calculator.GetVoxelScalingFactors(entry.NumberOfFractions, alphaBeta, out eqd2Q, out eqd2L);

            var dg = new CachedDoseGeometry
            {
                Ox = doseGeo.Origin.X,
                Oy = doseGeo.Origin.Y,
                Oz = doseGeo.Origin.Z,
                XDx = doseGeo.XDirection.X,
                XDy = doseGeo.XDirection.Y,
                XDz = doseGeo.XDirection.Z,
                YDx = doseGeo.YDirection.X,
                YDy = doseGeo.YDirection.Y,
                YDz = doseGeo.YDirection.Z,
                ZDx = doseGeo.ZDirection.X,
                ZDy = doseGeo.ZDirection.Y,
                ZDz = doseGeo.ZDirection.Z,
                XRes = doseGeo.XRes,
                YRes = doseGeo.YRes,
                ZRes = doseGeo.ZRes,
                XSize = doseGeo.XSize,
                YSize = doseGeo.YSize,
                ZSize = doseGeo.ZSize
            };

            double[,]? regMatrix = null;
            if (!entry.IsReference && !string.IsNullOrEmpty(entry.RegistrationId))
            {
                var reg = _registrations?.FirstOrDefault(r => r.Id == entry.RegistrationId);
                if (reg != null)
                {
                    var forwardMatrix = reg.ToMatrix4x4();
                    if (forwardMatrix != null)
                    {
                        string planFOR = _dataLoader.GetPlanImageFOR(entry.CourseId, entry.PlanId);
                        bool sourceIsRef = string.Equals(reg.SourceFOR, referenceFOR, StringComparison.OrdinalIgnoreCase);
                        bool sourceIsPlan = string.Equals(reg.SourceFOR, planFOR, StringComparison.OrdinalIgnoreCase);
                        if (sourceIsRef) { regMatrix = forwardMatrix; }
                        else if (sourceIsPlan)
                        {
                            regMatrix = MatrixMath.Invert4x4(forwardMatrix);
                            if (regMatrix == null) SimpleLogger.Error($"Registration {reg.Id}: matrix inversion failed");
                        }
                        else { regMatrix = forwardMatrix; SimpleLogger.Warning($"Registration {reg.Id}: unexpected FOR direction, using forward"); }
                    }
                }
            }

            int[][,]? ctVoxels = null;
            CachedCtGeometry? ctGeo = null;
            int ctHuOffset = 0;
            if (!entry.IsReference && planDoseData.CtImage != null)
            {
                var img = planDoseData.CtImage;
                ctVoxels = img.Voxels;
                ctHuOffset = img.HuOffset;
                ctGeo = new CachedCtGeometry
                {
                    Ox = img.Origin.X,
                    Oy = img.Origin.Y,
                    Oz = img.Origin.Z,
                    XDx = img.XDirection.X,
                    XDy = img.XDirection.Y,
                    XDz = img.XDirection.Z,
                    YDx = img.YDirection.X,
                    YDy = img.YDirection.Y,
                    YDz = img.YDirection.Z,
                    ZDx = img.ZDirection.X,
                    ZDy = img.ZDirection.Y,
                    ZDz = img.ZDirection.Z,
                    XRes = img.XRes,
                    YRes = img.YRes,
                    ZRes = img.ZRes,
                    XSize = img.XSize,
                    YSize = img.YSize,
                    ZSize = img.ZSize
                };
            }

            // DIR deformation field — preferred over affine when available.
            // Priority: in-memory (from live ITK) > path (pre-computed MHA) > null (falls back to affine).
            DeformationField? deformationField = entry.DeformationField;

            if (deformationField == null
                && !entry.IsReference
                && !string.IsNullOrEmpty(entry.DeformationFieldPath)
                && _dfLoader != null)
            {
                // Loader is responsible for existence / error handling (no file IO in service layer).
                deformationField = _dfLoader.Load(entry.DeformationFieldPath);
                if (deformationField != null)
                    SimpleLogger.Info($"Loaded DVF for {entry.DisplayLabel}: {entry.DeformationFieldPath}");
                else
                    SimpleLogger.Warning($"DVF load failed for {entry.DisplayLabel}, falling back to affine");
            }

            // If DVF grid matches the reference exactly, voxel indices coincide — enables fast path.
            bool dvfMatchesRef = deformationField != null
                && deformationField.XSize == _refW
                && deformationField.YSize == _refH
                && deformationField.ZSize == _refZ
                && GeometriesMatch(deformationField, _referenceCtImage);

            if (deformationField != null && !dvfMatchesRef)
                SimpleLogger.Warning($"DVF for {entry.DisplayLabel}: grid differs from reference CT, using world-coordinate lookup.");

            return new CachedPlanData
            {
                Entry = entry,
                DoseVoxels = planDoseData.DoseVoxels,
                DoseGeo = dg,
                RawScale = scaling.RawScale,
                RawOffset = scaling.RawOffset,
                UnitToGy = scaling.UnitToGy,
                UseEQD2 = useEqd2,
                EQD2Q = eqd2Q,
                EQD2L = eqd2L,
                Weight = entry.Weight,
                IsReference = entry.IsReference,
                TransformMatrix = deformationField == null ? regMatrix : null,
                CtVoxels = ctVoxels,
                CtGeo = ctGeo,
                CtHuOffset = ctHuOffset,
                DeformationField = deformationField,
                DvfMatchesRef = dvfMatchesRef
            };
        }

        private static bool GeometriesMatch(DeformationField dvf, VolumeData refVol)
        {
            const double tol = 1e-4;
            return Math.Abs(dvf.XRes - refVol.XRes) < tol
                && Math.Abs(dvf.YRes - refVol.YRes) < tol
                && Math.Abs(dvf.ZRes - refVol.ZRes) < tol
                && Vec3Near(dvf.Origin, refVol.Origin, tol)
                && Vec3Near(dvf.XDirection, refVol.XDirection, tol)
                && Vec3Near(dvf.YDirection, refVol.YDirection, tol)
                && Vec3Near(dvf.ZDirection, refVol.ZDirection, tol);
        }

        private static bool Vec3Near(Vec3 a, Vec3 b, double tol)
            => Math.Abs(a.X - b.X) < tol && Math.Abs(a.Y - b.Y) < tol && Math.Abs(a.Z - b.Z) < tol;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _summedSlices = null;
            _perPlanPhysicalSlices = null;
            _cachedPlans = null;
            _structureMasks = null;
        }

        private class CachedRefGeometry { public double Ox, Oy, Oz, Xx, Xy, Xz, Yx, Yy, Yz, Zx, Zy, Zz; }
        private class CachedDoseGeometry { public double Ox, Oy, Oz, XDx, XDy, XDz, YDx, YDy, YDz, ZDx, ZDy, ZDz, XRes, YRes, ZRes; public int XSize, YSize, ZSize; }
        private class CachedCtGeometry { public double Ox, Oy, Oz, XDx, XDy, XDz, YDx, YDy, YDz, ZDx, ZDy, ZDz, XRes, YRes, ZRes; public int XSize, YSize, ZSize; }
        private class CachedPlanData
        {
            public SummationPlanEntry Entry = null!; public int[][,] DoseVoxels = null!; public CachedDoseGeometry DoseGeo = null!;
            public double RawScale, RawOffset, UnitToGy, Weight; public bool IsReference, UseEQD2;
            public double EQD2Q, EQD2L; public double[,]? TransformMatrix;
            public int[][,]? CtVoxels; public CachedCtGeometry? CtGeo; public int CtHuOffset;
            public DeformationField? DeformationField;
            public bool DvfMatchesRef;
        }
    }
}
