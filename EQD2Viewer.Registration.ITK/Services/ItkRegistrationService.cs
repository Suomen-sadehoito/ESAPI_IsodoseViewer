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
    /// </summary>
    public class ItkRegistrationService : IRegistrationService
    {
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
                reg.SetMetricSamplingPercentage(0.1);

                reg.SetInterpolator(InterpolatorEnum.sitkLinear);

                // Multi-resolution: 3 → 2 → 1 shrink factors
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
                    numberOfIterations: 100,
                    convergenceMinimumValue: 1e-6,
                    convergenceWindowSize: 10);
                reg.SetOptimizerScalesFromPhysicalShift();

                // -- Phase 1: affine pre-alignment --
                ct.ThrowIfCancellationRequested();
                progress?.Report(20);
                var affineTx = reg.Execute(fixedF, movingF);
                progress?.Report(50);

                // -- Phase 2: B-spline deformable --
                ct.ThrowIfCancellationRequested();
                var bsplineTx = new BSplineTransform(3);
                var meshSize = new VectorUInt32(new uint[] { 8, 8, 8 });
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
                // parameters are optimized in phase 2. This is SimpleITK's idiomatic equivalent
                // of ITK's SetOnlyMostRecentTransformToOptimizeOn and preserves the affine
                // pre-alignment exactly.
                reg.SetMovingInitialTransform(affineTx);
                reg.SetInitialTransform(bsplineTx, inPlace: true);
                reg.SetOptimizerAsLBFGSB(
                    gradientConvergenceTolerance: 1e-5,
                    numberOfIterations: 50,
                    maximumNumberOfCorrections: 5,
                    maximumNumberOfFunctionEvaluations: 1000,
                    costFunctionConvergenceFactor: 1e7);

                ct.ThrowIfCancellationRequested();
                var finalBsplineTx = reg.Execute(fixedF, movingF);
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
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SimpleLogger.Error("ItkRegistrationService.Register failed", ex);
                return null;
            }
        }
    }
}
