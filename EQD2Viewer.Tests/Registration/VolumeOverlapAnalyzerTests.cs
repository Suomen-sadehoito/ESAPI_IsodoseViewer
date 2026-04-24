using EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Models;
using FluentAssertions;

namespace EQD2Viewer.Tests.Registration
{
    /// <summary>
    /// Analytic tests for <see cref="VolumeOverlapAnalyzer"/> using axis-aligned volumes
    /// with known geometry.
    /// </summary>
    public class VolumeOverlapAnalyzerTests
    {
        private static VolumeData MakeVolume(
            int xSize, int ySize, int zSize,
            double xRes = 1.0, double yRes = 1.0, double zRes = 1.0,
            double originX = 0, double originY = 0, double originZ = 0,
            string forId = "FOR_A", string id = "")
        {
            return new VolumeData
            {
                Geometry = new VolumeGeometry
                {
                    XSize = xSize, YSize = ySize, ZSize = zSize,
                    XRes = xRes, YRes = yRes, ZRes = zRes,
                    Origin = new Vec3(originX, originY, originZ),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1),
                    FrameOfReference = forId,
                    Id = id
                },
                Voxels = new int[zSize][,]
            };
        }

        [Fact]
        public void IdenticalVolumes_HaveFullOverlap()
        {
            var fixed_ = MakeVolume(100, 100, 100);
            var moving = MakeVolume(100, 100, 100);

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            r.RawHasOverlap.Should().BeTrue();
            r.RawOverlapPercentOfFixed.Should().BeApproximately(100.0, 1e-6);
            r.RawOverlapPercentOfMoving.Should().BeApproximately(100.0, 1e-6);
            r.CenterOffsetMagnitude.Should().BeApproximately(0.0, 1e-9);
            r.FORMatch.Should().BeTrue();
            r.Verdict.Should().Be(VolumeOverlapVerdict.Ok);
        }

        [Fact]
        public void DisjointVolumes_HaveNoRawOverlap()
        {
            var fixed_ = MakeVolume(100, 100, 100, originX: 0);
            var moving = MakeVolume(100, 100, 100, originX: 500); // shifted far in X

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            r.RawHasOverlap.Should().BeFalse();
            r.RawOverlapVolumeCm3.Should().BeApproximately(0.0, 1e-9);
            r.RawOverlapPercentOfFixed.Should().BeApproximately(0.0, 1e-9);
            // Centered overlap should still be full since volumes are the same size.
            r.CenteredOverlapPercentOfFixed.Should().BeApproximately(100.0, 1e-6);
        }

        [Fact]
        public void PartialOverlap_IsReportedCorrectly()
        {
            // Fixed covers x=[0,99], moving covers x=[50,149]: raw X overlap = 49 of 99
            var fixed_ = MakeVolume(100, 100, 100, originX: 0);
            var moving = MakeVolume(100, 100, 100, originX: 50);

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            r.RawHasOverlap.Should().BeTrue();
            r.RawOverlapExtent.X.Should().BeApproximately(49.0, 1e-6);
            r.RawOverlapExtent.Y.Should().BeApproximately(99.0, 1e-6);
            r.RawOverlapExtent.Z.Should().BeApproximately(99.0, 1e-6);
            // Volume ratio = 49 / 99 ~ 49.5%
            r.RawOverlapPercentOfFixed.Should().BeApproximately(49.0 / 99.0 * 100.0, 1e-6);
        }

        [Fact]
        public void CenteredOverlap_CompensatesForShift()
        {
            // Different origins but same dimensions -> centered overlap 100%.
            var fixed_ = MakeVolume(100, 100, 100, originX: 0);
            var moving = MakeVolume(100, 100, 100, originX: 50);

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            r.CenteredOverlapPercentOfFixed.Should().BeApproximately(100.0, 1e-6);
            r.CenterOffset.X.Should().BeApproximately(50.0, 1e-9);
        }

        [Fact]
        public void DifferentSizes_CenteredOverlapReflectsSmallerVolume()
        {
            // Fixed is 100 wide, moving is 80 wide, same center.
            var fixed_ = MakeVolume(100, 100, 100, originX: 0);   // extent 99, span [0,99]
            var moving = MakeVolume(80, 80, 80, originX: 10);     // extent 79, span [10,89]

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            // Raw: overlap X = 79 (moving is fully inside fixed along X).
            r.RawOverlapExtent.X.Should().BeApproximately(79.0, 1e-6);
            // overlap / moving = 100% (all of moving is inside fixed).
            r.RawOverlapPercentOfMoving.Should().BeApproximately(100.0, 1e-6);
        }

        [Fact]
        public void ForMismatch_IsDetected()
        {
            var fixed_ = MakeVolume(100, 100, 100, forId: "FOR_1");
            var moving = MakeVolume(100, 100, 100, forId: "FOR_2");

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            r.FORMatch.Should().BeFalse();
        }

        [Fact]
        public void SmallOverlapTriggersFailVerdict()
        {
            // Force centered overlap < 50% by making moving completely wider in X.
            var fixed_ = MakeVolume(100, 100, 100, originX: 0);
            var moving = MakeVolume(100, 100, 100, originX: 80);  // raw overlap 19% X

            var r = VolumeOverlapAnalyzer.Analyze(fixed_, moving);

            // Raw overlap ~19.2%, centered overlap 100%. With matching FORs the verdict
            // uses raw overlap, which is low.
            r.RawOverlapPercentOfFixed.Should().BeLessThan(50.0);
            r.Verdict.Should().Be(VolumeOverlapVerdict.Fail);
        }

        [Fact]
        public void Analyze_ThrowsOnNullFixed()
        {
            var moving = MakeVolume(10, 10, 10);
            System.Action act = () => VolumeOverlapAnalyzer.Analyze(null!, moving);
            act.Should().Throw<System.ArgumentNullException>();
        }

        [Fact]
        public void Analyze_ThrowsOnNullMoving()
        {
            var fixed_ = MakeVolume(10, 10, 10);
            System.Action act = () => VolumeOverlapAnalyzer.Analyze(fixed_, null!);
            act.Should().Throw<System.ArgumentNullException>();
        }
    }
}
