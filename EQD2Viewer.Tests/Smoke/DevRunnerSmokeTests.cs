using FluentAssertions;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace EQD2Viewer.Tests.Smoke
{
    /// <summary>
    /// End-to-end smoke tests that launch the compiled DevRunner.exe in --validate mode.
    /// These exercise the full composition root (AppLauncher → services → ViewModel) with
    /// a real fixture, without opening any window. Slow compared to unit tests (~2s each)
    /// but the only way to catch "everything wired up" regressions.
    ///
    /// Skipped silently if the DevRunner binary isn't built yet — e.g. when running the
    /// test project in isolation without the full solution build.
    /// </summary>
    public class DevRunnerSmokeTests
    {
        private const string FixtureDir = "TestFixtures/octavius_50gy_25fx";

        private static string? LocateDevRunnerExe()
        {
            // Walk up to the repo root and find BuildOutput/<Configuration>/EQD2Viewer.DevRunner.exe
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate1 = Path.Combine(dir, "BuildOutput", "Debug", "EQD2Viewer.DevRunner.exe");
                string candidate2 = Path.Combine(dir, "BuildOutput", "Release", "EQD2Viewer.DevRunner.exe");
                string candidate3 = Path.Combine(dir, "BuildOutput", "Release-WithITK", "EQD2Viewer.DevRunner.exe");
                foreach (var c in new[] { candidate1, candidate2, candidate3 })
                    if (File.Exists(c)) return c;

                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        private static string? LocateFixture()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "EQD2Viewer.Tests", "TestFixtures", "octavius_50gy_25fx");
                if (Directory.Exists(candidate)) return candidate;
                string local = Path.Combine(dir, FixtureDir);
                if (Directory.Exists(local)) return local;
                var parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        private static string Quote(string arg)
            => arg.Contains(" ") ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

        private static (int ExitCode, string Stdout, string Stderr) Run(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi)!;
            // 30 s hard cap so a deadlocked process never hangs the suite.
            bool exited = proc.WaitForExit(30_000);
            if (!exited)
            {
                try { proc.Kill(); } catch { }
                return (-1, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
            }
            return (proc.ExitCode, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
        }

        [Fact]
        public void DevRunner_ValidateMode_ReturnsZeroForKnownFixture()
        {
            string? exe = LocateDevRunnerExe();
            if (exe == null) return;   // silently skip if binary isn't built
            string? fixture = LocateFixture();
            fixture.Should().NotBeNull("test fixture must exist under EQD2Viewer.Tests\\TestFixtures\\");

            var (code, _, stderr) = Run(exe, fixture!, "--validate");
            code.Should().Be(0, $"DevRunner --validate should succeed; stderr:\n{stderr}");
        }

        [Fact]
        public void DevRunner_ValidateMode_MissingFixturePath_ExitsWithCodeOne()
        {
            string? exe = LocateDevRunnerExe();
            if (exe == null) return;

            // Pass a directory that definitely doesn't exist.
            string bogus = Path.Combine(Path.GetTempPath(), "definitely_not_a_real_fixture_" + Guid.NewGuid().ToString("N"));
            var (code, _, _) = Run(exe, bogus, "--validate");
            code.Should().Be(1, "missing fixture should yield exit code 1");
        }

        [Fact]
        public void DevRunner_ValidateMode_ExitsWithinReasonableTime()
        {
            string? exe = LocateDevRunnerExe();
            if (exe == null) return;
            string? fixture = LocateFixture();
            if (fixture == null) return;

            var sw = Stopwatch.StartNew();
            var (code, _, _) = Run(exe, fixture, "--validate");
            sw.Stop();

            code.Should().Be(0);
            sw.Elapsed.TotalSeconds.Should().BeLessThan(15,
                "snapshot load + validation on octavius fixture should complete quickly");
        }
    }
}
