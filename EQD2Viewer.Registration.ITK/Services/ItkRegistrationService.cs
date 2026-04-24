using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Registration.ITK.Converters;
using itk.simple;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Registration.ITK.Services
{
    /// <summary>
    /// Deformable image registration using SimpleITK.
    /// Performs B-spline free-form deformation with Mattes mutual information.
    /// Only available when EQD2Viewer.Registration.ITK.dll is loaded (Release-WithITK).
    ///
    /// Progress reporting:
    ///   Volume conversion and DVF generation report at phase boundaries.
    ///   Affine and B-spline Execute() emit per-iteration progress via SimpleITK Command callback,
    ///   so the UI bar moves smoothly 20 to 50 (affine) and 50 to 85 (B-spline) rather than stalling.
    ///   The pct mapping uses GetCurrentLevel() so the bar does not snap back at each
    ///   pyramid-level transition (SimpleITK resets GetOptimizerIteration() per level).
    ///
    /// Cancellation:
    ///   The same Command callback polls the CancellationToken each iteration and calls
    ///   reg.Abort() to stop SimpleITK mid-optimization.
    ///
    /// Logging:
    ///   Per level we log start and end markers with metric value; per Execute() we log the
    ///   final metric and the optimizer stop-condition description so that a post-hoc read of
    ///   the log tells us whether a phase converged, hit the iteration cap, or was aborted.
    ///   Per-iteration log emits every 10th iter to avoid spam on large volumes.
    ///
    /// Tuning rationale (Package 4 — SimpleITK ImageRegistrationMethodBSpline3 pattern):
    ///   Uses BSplineTransformInitializer with a small initial mesh (3x3x3) and then
    ///   SetInitialTransformAsBSpline with scaleFactors = {1, 2}. The B-spline control
    ///   grid is automatically refined between pyramid levels:
    ///     Level 0 (shrink 2, half-res): mesh 3 -> 27 CP -> 81 parameters.  Fast.
    ///     Level 1 (shrink 1, full res): mesh 6 -> 216 CP -> 648 parameters. Manageable.
    ///   Parameter count grows with resolution — the opposite of Package 2, where a fixed
    ///   10x10x10 mesh forced 3000 parameters on the slow full-res level.
    ///
    ///   Optimizer changed from LBFGS-B to GradientDescentLineSearch:
    ///     line search tolerates noisy metrics, so RANDOM sampling works fine and we do
    ///     not depend on strict per-iteration determinism. This is the combination the
    ///     SimpleITK BSpline3 example recommends and is what the SimpleITK notebooks use
    ///     for most multi-resolution B-spline demos.
    ///
    ///   Budget: 50 iterations / level, convergence window 5.
    ///
    ///   Body mask preprocessing (Package 4.1):
    ///     A binary body mask is generated from each CT (threshold HU >= -500, largest
    ///     connected component, fill holes so lungs and bowel gas remain included) and
    ///     passed to reg.SetMetricFixedMask / SetMetricMovingMask. Mattes MI then only
    ///     evaluates samples inside the patient body. For typical thoracic CTs 40-60 % of
    ///     voxels are air outside the patient; masking them out reduces per-iteration
    ///     metric cost by approximately the same fraction with no impact on DIR quality.
    ///
    ///   Expected wall time on a desktop Xeon/Ryzen with 12 logical cores and a
    ///   ~500x500x280 CT: 3-6 min (after body masking; ~1.5-2x faster than without).
    /// </summary>
    public class ItkRegistrationService : IRegistrationService
    {
        private const uint BsplineInitialMeshSize = 3;
        private const uint BsplineMeshScaleFactor = 2; // level 0 -> 3^3, level 1 -> 6^3
        private const uint BsplineIterations = 50;
        private const double BsplineLearningRate = 1.0;
        private const double BsplineConvergenceMinValue = 1e-4;
        private const uint BsplineConvergenceWindowSize = 5;

        private const uint AffineIterations = 100;
        private const double MetricSamplingFraction = 0.05;
        private const int PyramidLevels = 2;

        public Task<DeformationField?> RegisterAsync(
            VolumeData fixed_,
            VolumeData moving,
            IProgress<int>? progress,
            CancellationToken ct)
            => Task.Run(() => Register(fixed_, moving, progress, ct), ct);

        private static DeformationField? Register(
            VolumeData fixed_,
            VolumeData moving,
            IProgress<int>? progress,
            CancellationToken ct)
        {
            try
            {
                uint threadCount;
                try { threadCount = ProcessObject.GetGlobalDefaultNumberOfThreads(); }
                catch { threadCount = 0; }

                SimpleLogger.Info(
                    $"[DIR] Settings: initMesh={BsplineInitialMeshSize}^3, " +
                    $"scaleFactors={{1,{BsplineMeshScaleFactor}}}, " +
                    $"bsplineIter={BsplineIterations}, affineIter={AffineIterations}, " +
                    $"sampling={MetricSamplingFraction:F3}, pyramidLevels={PyramidLevels}, " +
                    $"optimizer=GradientDescentLineSearch, samplingStrategy=RANDOM, " +
                    $"bodyMasking=on (HU>=-500, largest CC, fillhole), " +
                    $"itkThreads={threadCount}");

                ct.ThrowIfCancellationRequested();
                progress?.Report(5);

                using var fixedImg  = ItkImageConverter.VolumeToImage(fixed_);
                using var movingImg = ItkImageConverter.VolumeToImage(moving);

                ct.ThrowIfCancellationRequested();
                progress?.Report(15);

                // Cast to Float32 - required by most ITK metrics
                using var fixedF  = SimpleITK.Cast(fixedImg,  PixelIDValueEnum.sitkFloat32);
                using var movingF = SimpleITK.Cast(movingImg, PixelIDValueEnum.sitkFloat32);

                var reg = new ImageRegistrationMethod();
                reg.SetMetricAsMattesMutualInformation(numberOfHistogramBins: 50);
                // RANDOM sampling works for both phases when using gradient descent variants.
                reg.SetMetricSamplingStrategy(ImageRegistrationMethod.MetricSamplingStrategyType.RANDOM);
                reg.SetMetricSamplingPercentage(MetricSamplingFraction);

                reg.SetInterpolator(InterpolatorEnum.sitkLinear);

                // Body mask: restrict metric evaluation to inside-patient voxels. For a
                // typical thoracic CT 40-60 % of voxels are air outside the patient; without
                // masking, the optimizer pays per-iteration metric cost there for no
                // registration benefit. Mask is kept in scope of this method so the native
                // ITK metric retains a valid reference through both phases of Execute().
                using var fixedMask = BuildBodyMask(fixedF);
                using var movingMask = BuildBodyMask(movingF);
                var fixedCov = GetMaskCoverage(fixedMask);
                var movingCov = GetMaskCoverage(movingMask);
                SimpleLogger.Info(
                    $"[DIR] Body mask fixed:  {fixedCov.inside:N0} / {fixedCov.total:N0} voxels inside ({fixedCov.pct:F1}%)");
                SimpleLogger.Info(
                    $"[DIR] Body mask moving: {movingCov.inside:N0} / {movingCov.total:N0} voxels inside ({movingCov.pct:F1}%)");

                if (IsReasonableCoverage(fixedCov))
                    reg.SetMetricFixedMask(fixedMask);
                else
                    SimpleLogger.Warning("[DIR] Fixed body mask coverage implausible; registering without mask on the fixed side.");

                if (IsReasonableCoverage(movingCov))
                    reg.SetMetricMovingMask(movingMask);
                else
                    SimpleLogger.Warning("[DIR] Moving body mask coverage implausible; registering without mask on the moving side.");

                // 2-level pyramid: coarse half-resolution pass then full resolution.
                // Paired with the per-level progress mapping in ProgressReportingCommand.
                reg.SetShrinkFactorsPerLevel(new VectorUInt32(new uint[] { 2, 1 }));
                reg.SetSmoothingSigmasPerLevel(new VectorDouble(new double[] { 1, 0 }));
                reg.SmoothingSigmasAreSpecifiedInPhysicalUnitsOn();

                var initialTx = SimpleITK.CenteredTransformInitializer(
                    fixedF, movingF,
                    new AffineTransform(3),
                    CenteredTransformInitializerFilter.OperationModeType.GEOMETRY);
                reg.SetInitialTransform(initialTx, inPlace: true);

                reg.SetOptimizerAsGradientDescent(
                    learningRate: 1.0,
                    numberOfIterations: (uint)AffineIterations,
                    convergenceMinimumValue: 1e-6,
                    convergenceWindowSize: 10);
                reg.SetOptimizerScalesFromPhysicalShift();

                ct.ThrowIfCancellationRequested();
                progress?.Report(20);

                // Phase 1: affine pre-alignment.
                SimpleLogger.Info("[DIR] Affine phase starting");
                var affinePhaseSw = Stopwatch.StartNew();
                Transform affineTx;
                using (var affineProgress = new ProgressReportingCommand(
                        reg, progress, ct, startPct: 20, rangePct: 30,
                        maxIter: (int)AffineIterations, numLevels: PyramidLevels, phaseName: "affine"))
                using (var affineLevel = new LevelChangeCommand(reg, "affine"))
                {
                    reg.AddCommand(EventEnum.sitkIterationEvent, affineProgress);
                    reg.AddCommand(EventEnum.sitkMultiResolutionIterationEvent, affineLevel);
                    affineTx = reg.Execute(fixedF, movingF);
                    ct.ThrowIfCancellationRequested();
                }
                affinePhaseSw.Stop();
                SimpleLogger.Info(
                    $"[DIR] Affine phase done in {affinePhaseSw.Elapsed.TotalSeconds:F1}s, " +
                    $"final metric={SafeGetMetric(reg):E3}, " +
                    $"stop=\"{SafeGetStopCondition(reg)}\"");
                progress?.Report(50);

                // Phase 2: B-spline deformable (SimpleITK BSpline3 canonical pattern).
                ct.ThrowIfCancellationRequested();

                // BSplineTransformInitializer builds a B-spline whose domain matches the
                // fixed image (origin / direction / physical dimensions) with the requested
                // initial mesh size. Replaces the manual SetTransformDomain* boilerplate
                // used in Package 2 and is the recommended way in the SimpleITK docs.
                var initMesh = new VectorUInt32(new uint[]
                {
                    BsplineInitialMeshSize, BsplineInitialMeshSize, BsplineInitialMeshSize
                });
                var bsplineTx = SimpleITK.BSplineTransformInitializer(fixedF, initMesh);

                reg.SetMovingInitialTransform(affineTx);

                // SetInitialTransformAsBSpline with scale factors: SimpleITK automatically
                // resamples the B-spline control grid between pyramid levels so the number
                // of parameters grows with image resolution. Level 0 solves a low-dimensional
                // problem on a coarse image; level 1 refines on the full image.
                var scaleFactors = new VectorUInt32(new uint[] { 1, BsplineMeshScaleFactor });
                reg.SetInitialTransformAsBSpline(bsplineTx, inPlace: true, scaleFactors);

                // Gradient descent with line search: tolerates the noise introduced by
                // RANDOM metric sampling, unlike LBFGS-B. No function-eval cap needed —
                // the line search bounds the work per iteration on its own.
                reg.SetOptimizerAsGradientDescentLineSearch(
                    learningRate: BsplineLearningRate,
                    numberOfIterations: BsplineIterations,
                    convergenceMinimumValue: BsplineConvergenceMinValue,
                    convergenceWindowSize: BsplineConvergenceWindowSize);

                reg.RemoveAllCommands();

                SimpleLogger.Info("[DIR] B-spline phase starting");
                var bsplinePhaseSw = Stopwatch.StartNew();
                Transform finalBsplineTx;
                using (var bsplineProgress = new ProgressReportingCommand(
                        reg, progress, ct, startPct: 50, rangePct: 35,
                        maxIter: (int)BsplineIterations, numLevels: PyramidLevels, phaseName: "bspline"))
                using (var bsplineLevel = new LevelChangeCommand(reg, "bspline"))
                {
                    reg.AddCommand(EventEnum.sitkIterationEvent, bsplineProgress);
                    reg.AddCommand(EventEnum.sitkMultiResolutionIterationEvent, bsplineLevel);

                    ct.ThrowIfCancellationRequested();
                    finalBsplineTx = reg.Execute(fixedF, movingF);
                    ct.ThrowIfCancellationRequested();
                }
                bsplinePhaseSw.Stop();
                SimpleLogger.Info(
                    $"[DIR] B-spline phase done in {bsplinePhaseSw.Elapsed.TotalSeconds:F1}s, " +
                    $"final metric={SafeGetMetric(reg):E3}, " +
                    $"stop=\"{SafeGetStopCondition(reg)}\"");
                progress?.Report(85);

                // Compose affine + bspline for displacement-field generation on the fixed grid.
                var composite = new CompositeTransform(3);
                composite.AddTransform(affineTx);
                composite.AddTransform(finalBsplineTx);

                ct.ThrowIfCancellationRequested();
                var dvfFilter = new TransformToDisplacementFieldFilter();
                dvfFilter.SetReferenceImage(fixedF);
                dvfFilter.SetOutputPixelType(PixelIDValueEnum.sitkVectorFloat64);
                using var dvfImage = dvfFilter.Execute(composite);

                var field = ItkImageConverter.DisplacementImageToField(dvfImage, fixed_);
                progress?.Report(100);
                return field;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SimpleLogger.Error("ItkRegistrationService.Register failed", ex);
                return null;
            }
        }

        private static double SafeGetMetric(ImageRegistrationMethod reg)
        {
            try { return reg.GetMetricValue(); } catch { return double.NaN; }
        }

        /// <summary>
        /// Builds a binary body mask (sitkUInt8, 1 inside body, 0 outside) from a CT image.
        ///
        /// Algorithm:
        ///   1. Threshold HU >= -500 (excludes air inside and outside the patient).
        ///   2. Connected-component labelling, keep the largest component. This drops
        ///      couch rails, immobilisation devices, and disconnected air pockets.
        ///   3. BinaryFillhole: fills enclosed low-HU regions (lungs, bowel gas, sinuses)
        ///      so they are registered as body tissue rather than excluded as air.
        ///
        /// The mask is constructed once per registration and lives through both phases
        /// of reg.Execute() via the caller's 'using' scope.
        /// </summary>
        private static Image BuildBodyMask(Image ctFloat32)
        {
            using var thresholded = SimpleITK.BinaryThreshold(
                ctFloat32,
                lowerThreshold: -500.0,
                upperThreshold: 5000.0,
                insideValue: 1,
                outsideValue: 0);

            using var connected = SimpleITK.ConnectedComponent(thresholded, fullyConnected: false);
            using var relabeled = SimpleITK.RelabelComponent(connected,
                minimumObjectSize: 0, sortByObjectSize: true);

            // After RelabelComponent the largest component is label 1.
            using var largest = SimpleITK.BinaryThreshold(
                relabeled,
                lowerThreshold: 1.0,
                upperThreshold: 1.0,
                insideValue: 1,
                outsideValue: 0);

            return SimpleITK.BinaryFillhole(largest, fullyConnected: true, foregroundValue: 1);
        }

        private static MaskCoverage GetMaskCoverage(Image mask)
        {
            long total = 1;
            foreach (var s in mask.GetSize()) total *= s;

            var stats = new StatisticsImageFilter();
            stats.Execute(mask);
            long inside = (long)stats.GetSum();

            double pct = total > 0 ? 100.0 * inside / total : 0;
            return new MaskCoverage(inside, total, pct);
        }

        /// <summary>
        /// A mask covering less than 5 % or more than 99 % of the volume is unlikely to
        /// represent a real body outline — treat it as a failure and fall back to
        /// unmasked registration on that side. Keeps the registration robust against
        /// unusual CTs (e.g. phantoms, empty images, reconstruction errors).
        /// </summary>
        private static bool IsReasonableCoverage(MaskCoverage cov)
            => cov.total > 0 && cov.pct >= 5.0 && cov.pct <= 99.0;

        private readonly struct MaskCoverage
        {
            public readonly long inside;
            public readonly long total;
            public readonly double pct;
            public MaskCoverage(long inside, long total, double pct)
            { this.inside = inside; this.total = total; this.pct = pct; }
        }

        private static string SafeGetStopCondition(ImageRegistrationMethod reg)
        {
            try { return reg.GetOptimizerStopConditionDescription(); } catch { return "unknown"; }
        }

        /// <summary>
        /// SimpleITK Command that maps per-level optimizer iterations to a global percentage
        /// (factoring in the current pyramid level so the bar does not snap back on level
        /// transitions), cancels via reg.Abort() when the CancellationToken trips, and logs
        /// throttled per-iteration progress. Runs on SimpleITK's worker thread.
        /// </summary>
        private sealed class ProgressReportingCommand : Command
        {
            private readonly ImageRegistrationMethod _reg;
            private readonly IProgress<int>? _progress;
            private readonly CancellationToken _ct;
            private readonly int _startPct;
            private readonly int _rangePct;
            private readonly int _maxIter;
            private readonly int _numLevels;
            private readonly string _phaseName;
            private int _lastReported = -1;
            private int _lastLoggedLevel = -1;
            private int _lastLoggedIter = -1;

            public ProgressReportingCommand(
                ImageRegistrationMethod reg, IProgress<int>? progress, CancellationToken ct,
                int startPct, int rangePct, int maxIter, int numLevels, string phaseName)
            {
                _reg = reg;
                _progress = progress;
                _ct = ct;
                _startPct = startPct;
                _rangePct = rangePct;
                _maxIter = Math.Max(1, maxIter);
                _numLevels = Math.Max(1, numLevels);
                _phaseName = phaseName;
            }

            public override void Execute()
            {
                if (_ct.IsCancellationRequested)
                {
                    try { _reg.Abort(); } catch { /* Abort may not be exposed on all SimpleITK builds */ }
                    return;
                }

                try
                {
                    int level = SafeGetLevel();
                    int iter = (int)_reg.GetOptimizerIteration();

                    int perLevel = _rangePct / _numLevels;
                    int pct = _startPct
                            + level * perLevel
                            + Math.Min(iter, _maxIter) * perLevel / _maxIter;

                    int maxPct = _startPct + _rangePct;
                    if (pct > maxPct) pct = maxPct;
                    if (pct < _lastReported) pct = _lastReported;

                    if (pct != _lastReported)
                    {
                        _lastReported = pct;
                        _progress?.Report(pct);
                    }

                    bool levelChanged = level != _lastLoggedLevel;
                    bool decimalIter = iter != _lastLoggedIter && iter % 10 == 0;
                    if (levelChanged || decimalIter)
                    {
                        _lastLoggedLevel = level;
                        _lastLoggedIter = iter;
                        double metric = SafeGetMetric(_reg);
                        SimpleLogger.Info(
                            $"[DIR] {_phaseName} L{level} iter={iter} metric={metric:E3} pct={pct}");
                    }
                }
                catch { /* never propagate into ITK's native event loop */ }
            }

            private int SafeGetLevel()
            {
                try
                {
                    int level = (int)_reg.GetCurrentLevel();
                    if (level < 0) level = 0;
                    if (level >= _numLevels) level = _numLevels - 1;
                    return level;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// Logs pyramid-level transitions. Fires at the start of each multi-resolution level,
        /// and the final level's closing metric is captured in the phase-end log line.
        /// </summary>
        private sealed class LevelChangeCommand : Command
        {
            private readonly ImageRegistrationMethod _reg;
            private readonly string _phaseName;
            private int _lastLevel = -1;

            public LevelChangeCommand(ImageRegistrationMethod reg, string phaseName)
            {
                _reg = reg;
                _phaseName = phaseName;
            }

            public override void Execute()
            {
                try
                {
                    int level;
                    try { level = (int)_reg.GetCurrentLevel(); } catch { return; }
                    if (level == _lastLevel) return;

                    if (_lastLevel >= 0)
                        SimpleLogger.Info(
                            $"[DIR] {_phaseName} level {_lastLevel} -> {level}, " +
                            $"metric={SafeGetMetric(_reg):E3}");
                    else
                        SimpleLogger.Info($"[DIR] {_phaseName} level {level} starting");
                    _lastLevel = level;
                }
                catch { }
            }
        }
    }
}
