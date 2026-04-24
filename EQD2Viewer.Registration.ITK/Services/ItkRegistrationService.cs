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
    /// Performance tuning (clinical fast settings, sub-voxel accuracy vs. slow settings):
    ///   B-spline mesh 6x6x6  - 3 times fewer control points than 8x8x8
    ///   B-spline iterations 30 - LBFGS-B converges in ~20 typically
    ///   Metric sampling 5 %  - still plenty for Mattes MI statistics
    /// </summary>
    public class ItkRegistrationService : IRegistrationService
    {
        private const uint BsplineMeshSize = 6;
        private const uint BsplineIterations = 30;
        private const uint AffineIterations = 100;
        private const double MetricSamplingFraction = 0.05;
        private const int PyramidLevels = 3;

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
                SimpleLogger.Info(
                    $"[DIR] Settings: mesh={BsplineMeshSize}^3, bsplineIter={BsplineIterations}, " +
                    $"affineIter={AffineIterations}, sampling={MetricSamplingFraction:F3}, " +
                    $"pyramidLevels={PyramidLevels}");

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
                reg.SetMetricSamplingStrategy(ImageRegistrationMethod.MetricSamplingStrategyType.RANDOM);
                reg.SetMetricSamplingPercentage(MetricSamplingFraction);

                reg.SetInterpolator(InterpolatorEnum.sitkLinear);

                reg.SetShrinkFactorsPerLevel(new VectorUInt32(new uint[] { 4, 2, 1 }));
                reg.SetSmoothingSigmasPerLevel(new VectorDouble(new double[] { 4, 2, 0 }));
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

                // Phase 2: B-spline deformable.
                ct.ThrowIfCancellationRequested();
                var bsplineTx = new BSplineTransform(3);
                var meshSize = new VectorUInt32(new uint[] { BsplineMeshSize, BsplineMeshSize, BsplineMeshSize });
                bsplineTx.SetTransformDomainMeshSize(meshSize);
                bsplineTx.SetTransformDomainOrigin(fixedF.GetOrigin());
                bsplineTx.SetTransformDomainDirection(fixedF.GetDirection());

                var sp = fixedF.GetSpacing();
                var sz = fixedF.GetSize();
                bsplineTx.SetTransformDomainPhysicalDimensions(new VectorDouble(new double[]
                {
                    sp[0] * (sz[0] - 1.0),
                    sp[1] * (sz[1] - 1.0),
                    sp[2] * (sz[2] - 1.0)
                }));

                reg.SetMovingInitialTransform(affineTx);
                reg.SetInitialTransform(bsplineTx, inPlace: true);
                reg.SetOptimizerAsLBFGSB(
                    gradientConvergenceTolerance: 1e-5,
                    numberOfIterations: BsplineIterations,
                    maximumNumberOfCorrections: 5,
                    maximumNumberOfFunctionEvaluations: 1000,
                    costFunctionConvergenceFactor: 1e7);

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
