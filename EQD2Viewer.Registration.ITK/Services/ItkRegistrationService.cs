using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Logging;
using EQD2Viewer.Registration.ITK.Converters;
using itk.simple;
using System;
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
    ///   so the UI bar moves smoothly 20→50 (affine) and 50→85 (B-spline) rather than stalling.
    ///
    /// Cancellation:
    ///   The same Command callback polls the CancellationToken each iteration and calls
    ///   reg.Abort() to stop SimpleITK mid-optimization. Without this, the .NET cancel
    ///   would only take effect between phases (5–15 min latency for large volumes).
    ///
    /// Performance tuning (clinical fast settings, sub-voxel accuracy vs. slow settings):
    ///   B-spline mesh 6×6×6 (was 8×8×8)  — 3× fewer control points
    ///   B-spline iterations 30 (was 50)  — LBFGS-B converges in ~20 typically
    ///   Metric sampling 5 % (was 10 %)   — still plenty for Mattes MI statistics
    /// </summary>
    public class ItkRegistrationService : IRegistrationService
    {
        // Clinical-fast tuning constants. Bumping the first two up increases accuracy at the
        // cost of O(mesh³ × iter) time; sub-voxel gains rarely justify the wait.
        private const uint BsplineMeshSize = 6;
        private const uint BsplineIterations = 30;
        private const uint AffineIterations = 100;
        private const double MetricSamplingFraction = 0.05;

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
                ct.ThrowIfCancellationRequested();
                progress?.Report(5);

                using var fixedImg  = ItkImageConverter.VolumeToImage(fixed_);
                using var movingImg = ItkImageConverter.VolumeToImage(moving);

                ct.ThrowIfCancellationRequested();
                progress?.Report(15);

                // Cast to Float32 — required by most ITK metrics
                using var fixedF  = SimpleITK.Cast(fixedImg,  PixelIDValueEnum.sitkFloat32);
                using var movingF = SimpleITK.Cast(movingImg, PixelIDValueEnum.sitkFloat32);

                // -- Registration framework --
                var reg = new ImageRegistrationMethod();
                reg.SetMetricAsMattesMutualInformation(numberOfHistogramBins: 50);
                reg.SetMetricSamplingStrategy(ImageRegistrationMethod.MetricSamplingStrategyType.RANDOM);
                reg.SetMetricSamplingPercentage(MetricSamplingFraction);

                reg.SetInterpolator(InterpolatorEnum.sitkLinear);

                // Multi-resolution: 4 → 2 → 1 shrink factors
                reg.SetShrinkFactorsPerLevel(new VectorUInt32(new uint[] { 4, 2, 1 }));
                reg.SetSmoothingSigmasPerLevel(new VectorDouble(new double[] { 4, 2, 0 }));
                reg.SmoothingSigmasAreSpecifiedInPhysicalUnitsOn();

                // Initial affine alignment
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

                // -- Phase 1: affine pre-alignment --
                ct.ThrowIfCancellationRequested();
                progress?.Report(20);

                // Per-iteration progress + cancel polling: maps affine iterations to the 20..50 band.
                using (var affineProgress = new ProgressReportingCommand(
                    reg, progress, ct, startPct: 20, rangePct: 30, maxIter: (int)AffineIterations))
                {
                    reg.AddCommand(EventEnum.sitkIterationEvent, affineProgress);
                    var affineTx = reg.Execute(fixedF, movingF);
                    ct.ThrowIfCancellationRequested();   // If Command.Abort() was called, surface it here.
                    progress?.Report(50);

                    // -- Phase 2: B-spline deformable --
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

                    // Apply affine as a fixed "moving-initial" transform so only the B-spline
                    // parameters are optimized in phase 2. Preserves affine exactly.
                    reg.SetMovingInitialTransform(affineTx);
                    reg.SetInitialTransform(bsplineTx, inPlace: true);
                    reg.SetOptimizerAsLBFGSB(
                        gradientConvergenceTolerance: 1e-5,
                        numberOfIterations: BsplineIterations,
                        maximumNumberOfCorrections: 5,
                        maximumNumberOfFunctionEvaluations: 1000,
                        costFunctionConvergenceFactor: 1e7);

                    // Swap in a fresh progress reporter for the B-spline phase (50..85 band).
                    reg.RemoveAllCommands();
                    using var bsplineProgress = new ProgressReportingCommand(
                        reg, progress, ct, startPct: 50, rangePct: 35, maxIter: (int)BsplineIterations);
                    reg.AddCommand(EventEnum.sitkIterationEvent, bsplineProgress);

                    ct.ThrowIfCancellationRequested();
                    var finalBsplineTx = reg.Execute(fixedF, movingF);
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(85);

                    // Compose affine+bspline for displacement-field generation on the fixed grid.
                    var composite = new CompositeTransform(3);
                    composite.AddTransform(affineTx);
                    composite.AddTransform(finalBsplineTx);

                    // -- Convert to displacement field --
                    ct.ThrowIfCancellationRequested();
                    var dvfFilter = new TransformToDisplacementFieldFilter();
                    dvfFilter.SetReferenceImage(fixedF);
                    dvfFilter.SetOutputPixelType(PixelIDValueEnum.sitkVectorFloat64);
                    using var dvfImage = dvfFilter.Execute(composite);

                    var field = ItkImageConverter.DisplacementImageToField(dvfImage, fixed_);
                    progress?.Report(100);
                    return field;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SimpleLogger.Error("ItkRegistrationService.Register failed", ex);
                return null;
            }
        }

        /// <summary>
        /// SimpleITK Command subclass that (a) maps the optimizer's current iteration to a
        /// percentage in a configurable band and (b) aborts the registration if the caller
        /// cancels. Runs on SimpleITK's worker thread — IProgress.Report marshals back to UI.
        /// </summary>
        private sealed class ProgressReportingCommand : Command
        {
            private readonly ImageRegistrationMethod _reg;
            private readonly IProgress<int>? _progress;
            private readonly CancellationToken _ct;
            private readonly int _startPct;
            private readonly int _rangePct;
            private readonly int _maxIter;
            private int _lastReported = -1;

            public ProgressReportingCommand(ImageRegistrationMethod reg, IProgress<int>? progress,
                CancellationToken ct, int startPct, int rangePct, int maxIter)
            {
                _reg = reg;
                _progress = progress;
                _ct = ct;
                _startPct = startPct;
                _rangePct = rangePct;
                _maxIter = System.Math.Max(1, maxIter);
            }

            public override void Execute()
            {
                if (_ct.IsCancellationRequested)
                {
                    // reg.Abort() signals SimpleITK/ITK to stop the optimizer at the next safe point.
                    // Within a few iterations the outer reg.Execute() returns. The caller's
                    // ThrowIfCancellationRequested then surfaces the cancellation to .NET.
                    try { _reg.Abort(); } catch { /* Abort may not be exposed on all SimpleITK builds */ }
                    return;
                }

                try
                {
                    int iter = (int)_reg.GetOptimizerIteration();
                    int pct = _startPct + (System.Math.Min(iter, _maxIter) * _rangePct) / _maxIter;
                    if (pct > _startPct + _rangePct) pct = _startPct + _rangePct;
                    // Throttle: only emit when the integer percentage actually advances.
                    if (pct != _lastReported)
                    {
                        _lastReported = pct;
                        _progress?.Report(pct);
                    }
                }
                catch
                {
                    // Never propagate exceptions back into SimpleITK's native event loop.
                }
            }
        }
    }
}
