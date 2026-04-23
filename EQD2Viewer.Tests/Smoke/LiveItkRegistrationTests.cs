using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using FluentAssertions;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Tests.Smoke
{
    /// <summary>
    /// Integration tests that exercise the real SimpleITK-backed ItkRegistrationService.
    ///
    /// Skipped silently when the SimpleITK native binaries are not available next to the test
    /// binary (i.e. regular Release builds, or CI jobs that don't cache the SimpleITK zip).
    /// Runs fully under Release-WithITK where lib\SimpleITK\*.dll has been deployed.
    ///
    /// These tests are slow (multi-second registrations). Keep the volumes tiny (~16^3 voxels)
    /// so the suite stays below a few seconds even with B-spline optimization.
    /// </summary>
    public class LiveItkRegistrationTests
    {
        // net48 lacks double.IsFinite; define inline.
        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        // Treat the service as available only if the deployed DLL is loadable.
        private static (bool available, IRegistrationService? service) TryLoadService()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string[] candidates =
                {
                    Path.Combine(baseDir, "EQD2Viewer.Registration.ITK.dll"),
                    Path.Combine(baseDir, "03_ITK_Registration", "EQD2Viewer.Registration.ITK.dll")
                };
                string? dllPath = null;
                foreach (var p in candidates) if (File.Exists(p)) { dllPath = p; break; }
                if (dllPath == null) return (false, null);

                // SimpleITK native DLL must also be locatable for the managed wrapper to work at runtime.
                string dir = Path.GetDirectoryName(dllPath)!;
                bool hasNative = File.Exists(Path.Combine(dir, "SimpleITKCSharpNative.dll"))
                    || File.Exists(Path.Combine(dir, "SimpleITKCSharp.dll"));
                if (!hasNative) return (false, null);

                var asm = Assembly.LoadFrom(dllPath);
                var type = asm.GetType("EQD2Viewer.Registration.ITK.Services.ItkRegistrationService");
                if (type == null) return (false, null);
                var instance = System.Activator.CreateInstance(type) as IRegistrationService;
                return (instance != null, instance);
            }
            catch
            {
                return (false, null);
            }
        }

        /// <summary>
        /// Builds a CT volume with a gradient pattern — sufficient mutual information
        /// for Mattes MI metric to find a meaningful registration on toy data.
        /// Volume is 32x32x32 with spacing 2mm to match B-spline mesh scale.
        /// </summary>
        private static VolumeData MakeGradientVolume(int size, int offsetX)
        {
            var voxels = new int[size][,];
            for (int z = 0; z < size; z++)
            {
                voxels[z] = new int[size, size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int sx = x - offsetX;
                        if (sx < 0 || sx >= size) { voxels[z][x, y] = 0; continue; }
                        // 3D gradient plus centered sphere to give both global and local intensity structure.
                        int r = (int)System.Math.Sqrt(
                            (sx - size / 2) * (sx - size / 2) +
                            (y - size / 2) * (y - size / 2) +
                            (z - size / 2) * (z - size / 2));
                        voxels[z][x, y] = 100 * sx + 50 * y + 20 * z + (r < size / 4 ? 500 : 0);
                    }
            }

            return new VolumeData
            {
                Geometry = new VolumeGeometry
                {
                    XSize = size, YSize = size, ZSize = size,
                    XRes = 2.0, YRes = 2.0, ZRes = 2.0,
                    Origin = new Vec3(0, 0, 0),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1),
                    FrameOfReference = "LIVE_TEST_FOR"
                },
                Voxels = voxels,
                HuOffset = 0
            };
        }

        /// <summary>
        /// Smoke test: exercises the full SimpleITK pipeline end-to-end. Verifies structural
        /// invariants rather than registration accuracy — toy synthetic volumes aren't a fair
        /// test of B-spline DIR quality, but they are enough to prove native DLLs load and the
        /// managed↔native boundary works.
        /// </summary>
        [Fact]
        public async Task Register_IdenticalVolumes_ProducesFiniteFiniteSizedDvf()
        {
            var (available, svc) = TryLoadService();
            if (!available) return; // silently skip outside Release-WithITK

            const int size = 32;
            var fixed_ = MakeGradientVolume(size, offsetX: 0);
            var moving = MakeGradientVolume(size, offsetX: 0);

            var dvf = await svc!.RegisterAsync(fixed_, moving, null, CancellationToken.None);
            dvf.Should().NotBeNull("SimpleITK pipeline must produce a DVF");
            dvf!.XSize.Should().Be(size);
            dvf.YSize.Should().Be(size);
            dvf.ZSize.Should().Be(size);
            dvf.Vectors.Should().NotBeNull();

            // Every vector component must be finite — any NaN/Inf indicates a numerical blow-up
            // in the native optimizer. (net48 lacks double.IsFinite, so check explicitly.)
            for (int z = 0; z < dvf.ZSize; z++)
                for (int y = 0; y < dvf.YSize; y++)
                    for (int x = 0; x < dvf.XSize; x++)
                    {
                        var v = dvf.Vectors[z][x, y];
                        IsFinite(v.X).Should().BeTrue($"DVF[{z}][{x},{y}].X must be finite");
                        IsFinite(v.Y).Should().BeTrue($"DVF[{z}][{x},{y}].Y must be finite");
                        IsFinite(v.Z).Should().BeTrue($"DVF[{z}][{x},{y}].Z must be finite");
                    }
        }

        /// <summary>
        /// Verifies that a shifted input yields a non-trivial DVF (not identically zero),
        /// proving the optimizer is actually running and B-spline parameters are being updated.
        /// Precise direction/magnitude is not checked — it's a smoke test, not an accuracy test.
        /// </summary>
        [Fact]
        public async Task Register_ShiftedVolume_ProducesNonZeroDvf()
        {
            var (available, svc) = TryLoadService();
            if (!available) return;

            const int size = 32;
            var fixed_ = MakeGradientVolume(size, offsetX: 0);
            var moving = MakeGradientVolume(size, offsetX: 3);

            var dvf = await svc!.RegisterAsync(fixed_, moving, null, CancellationToken.None);
            dvf.Should().NotBeNull();

            double maxMag = 0;
            for (int z = 0; z < dvf!.ZSize; z++)
                for (int y = 0; y < dvf.YSize; y++)
                    for (int x = 0; x < dvf.XSize; x++)
                    {
                        var v = dvf.Vectors[z][x, y];
                        double m = System.Math.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
                        if (m > maxMag) maxMag = m;
                    }
            maxMag.Should().BeGreaterThan(0.01,
                "with a 3-voxel shift the optimizer must produce some displacement");
        }
    }
}
