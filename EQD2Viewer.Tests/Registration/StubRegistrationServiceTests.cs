using EQD2Viewer.Core.Data;
using EQD2Viewer.Registration.Services;
using FluentAssertions;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Tests.Registration
{
    /// <summary>
    /// Verifies that StubRegistrationService is a no-op that always reports "unavailable"
    /// — callers rely on its null return to fall back to the affine pathway.
    /// </summary>
    public class StubRegistrationServiceTests
    {
        private readonly StubRegistrationService _stub = new StubRegistrationService();

        private static VolumeData MinimalVolume()
        {
            var vox = new int[1][,]; vox[0] = new int[1, 1];
            return new VolumeData
            {
                Geometry = new VolumeGeometry
                {
                    XSize = 1, YSize = 1, ZSize = 1,
                    XRes = 1, YRes = 1, ZRes = 1,
                    Origin = new Vec3(0, 0, 0),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1),
                },
                Voxels = vox,
                HuOffset = 0,
            };
        }

        [Fact]
        public async Task RegisterAsync_AlwaysReturnsNull()
        {
            var result = await _stub.RegisterAsync(
                MinimalVolume(), MinimalVolume(),
                progress: null, ct: CancellationToken.None);
            result.Should().BeNull();
        }

        [Fact]
        public async Task RegisterAsync_WithCancelledToken_StillReturnsNullWithoutThrowing()
        {
            // Stub does no work, so cancellation is irrelevant; contract is "null, no throw".
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = await _stub.RegisterAsync(
                MinimalVolume(), MinimalVolume(),
                progress: null, ct: cts.Token);
            result.Should().BeNull();
        }

        [Fact]
        public async Task RegisterAsync_IgnoresProgressReporter()
        {
            int reportedValue = -1;
            var progress = new System.Progress<int>(p => reportedValue = p);
            var result = await _stub.RegisterAsync(
                MinimalVolume(), MinimalVolume(), progress, CancellationToken.None);
            result.Should().BeNull();
            reportedValue.Should().Be(-1, "stub doesn't run any work, so should never report progress");
        }
    }
}
