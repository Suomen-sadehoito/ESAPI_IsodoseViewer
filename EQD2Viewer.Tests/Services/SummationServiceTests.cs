using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Services;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Tests.Services
{
    /// <summary>
    /// Core tests for SummationService — particularly the deformable-registration pathway
    /// introduced for multi-plan EQD2 summation.
    ///
    /// Test strategy:
    ///   * Build a tiny 4x4x2 reference CT with identity orientation.
    ///   * Mock ISummationDataLoader to return deterministic dose voxels.
    ///   * Configure DeformationField in-memory (no file IO) to exercise the DIR path.
    ///   * Assert voxel-level dose placement matches the expected deformation mapping.
    /// </summary>
    public class SummationServiceTests
    {
        private const int RefX = 4, RefY = 4, RefZ = 2;

        // ── Test fixtures ─────────────────────────────────────────────────

        private static VolumeData MakeReferenceCt()
        {
            var vox = new int[RefZ][,];
            for (int z = 0; z < RefZ; z++) vox[z] = new int[RefX, RefY];
            return new VolumeData
            {
                Geometry = new VolumeGeometry
                {
                    XSize = RefX, YSize = RefY, ZSize = RefZ,
                    XRes = 1.0, YRes = 1.0, ZRes = 1.0,
                    Origin = new Vec3(0, 0, 0),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1),
                    FrameOfReference = "FOR_REF"
                },
                Voxels = vox,
                HuOffset = 0
            };
        }

        /// <summary>
        /// Builds a dose grid co-located with the reference CT, with the specified per-voxel
        /// dose in Gy stored in raw voxels (RawScale=1, offset=0, unit=Gy).
        /// </summary>
        private static SummationPlanDoseData MakeDoseData(int[][,] doseGy)
            => new SummationPlanDoseData
            {
                DoseVoxels = doseGy,
                DoseGeometry = new VolumeGeometry
                {
                    XSize = RefX, YSize = RefY, ZSize = RefZ,
                    XRes = 1.0, YRes = 1.0, ZRes = 1.0,
                    Origin = new Vec3(0, 0, 0),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1)
                },
                Scaling = new DoseScaling { RawScale = 1.0, RawOffset = 0, UnitToGy = 1.0, DoseUnit = "Gy" }
            };

        private static int[][,] FillDose(int value)
        {
            var data = new int[RefZ][,];
            for (int z = 0; z < RefZ; z++)
            {
                data[z] = new int[RefX, RefY];
                for (int y = 0; y < RefY; y++)
                    for (int x = 0; x < RefX; x++)
                        data[z][x, y] = value;
            }
            return data;
        }

        private static DeformationField MakeDvf(Vec3 uniformDisplacement)
        {
            var vectors = new Vec3[RefZ][,];
            for (int z = 0; z < RefZ; z++)
            {
                vectors[z] = new Vec3[RefX, RefY];
                for (int y = 0; y < RefY; y++)
                    for (int x = 0; x < RefX; x++)
                        vectors[z][x, y] = uniformDisplacement;
            }
            return new DeformationField
            {
                XSize = RefX, YSize = RefY, ZSize = RefZ,
                XRes = 1.0, YRes = 1.0, ZRes = 1.0,
                Origin = new Vec3(0, 0, 0),
                XDirection = new Vec3(1, 0, 0),
                YDirection = new Vec3(0, 1, 0),
                ZDirection = new Vec3(0, 0, 1),
                Vectors = vectors
            };
        }

        private static Mock<ISummationDataLoader> MakeLoader(
            int refDoseGy,
            int movingDoseGy,
            DeformationField? movingDvf,
            string movingFor = "FOR_MOV")
        {
            var loader = new Mock<ISummationDataLoader>(MockBehavior.Strict);
            loader.Setup(l => l.LoadPlanDose("C1", "PlanRef", It.IsAny<double>()))
                  .Returns(MakeDoseData(FillDose(refDoseGy)));
            loader.Setup(l => l.LoadPlanDose("C1", "PlanMov", It.IsAny<double>()))
                  .Returns(MakeDoseData(FillDose(movingDoseGy)));
            loader.Setup(l => l.LoadStructureContours(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(new List<StructureData>());
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanRef")).Returns("FOR_REF");
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanMov")).Returns(movingFor);
            return loader;
        }

        private static SummationConfig MakeConfig(DeformationField? dvf)
            => new SummationConfig
            {
                Method = SummationMethod.Physical,
                GlobalAlphaBeta = 3.0,
                Plans = new List<SummationPlanEntry>
                {
                    new SummationPlanEntry { CourseId = "C1", PlanId = "PlanRef", DisplayLabel = "Ref",
                        NumberOfFractions = 25, TotalDoseGy = 50, Weight = 1.0, IsReference = true },
                    new SummationPlanEntry { CourseId = "C1", PlanId = "PlanMov", DisplayLabel = "Mov",
                        NumberOfFractions = 25, TotalDoseGy = 50, Weight = 1.0, IsReference = false,
                        DeformationField = dvf }
                }
            };

        // ── PrepareData validation ─────────────────────────────────────────

        [Fact]
        public void PrepareData_EmptyConfig_ReturnsFailure()
        {
            var svc = new SummationService(MakeReferenceCt(),
                new Mock<ISummationDataLoader>().Object, new List<RegistrationData>());
            var result = svc.PrepareData(new SummationConfig { Plans = new List<SummationPlanEntry>() });
            result.Success.Should().BeFalse();
            result.StatusMessage.Should().Contain("No plans");
        }

        [Fact]
        public void PrepareData_NoReferencePlan_ReturnsFailure()
        {
            var svc = new SummationService(MakeReferenceCt(),
                new Mock<ISummationDataLoader>().Object, new List<RegistrationData>());
            var config = new SummationConfig
            {
                Plans = new List<SummationPlanEntry>
                {
                    new SummationPlanEntry { CourseId = "C", PlanId = "P", IsReference = false }
                }
            };
            var result = svc.PrepareData(config);
            result.Success.Should().BeFalse();
            result.StatusMessage.Should().Contain("reference");
        }

        // ── ComputeAsync: reference-only pathway ───────────────────────────

        [Fact]
        public async Task ComputeAsync_ReferenceOnlyPlan_AccumulatesDirectDose()
        {
            var loader = MakeLoader(refDoseGy: 5, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            var config = new SummationConfig
            {
                Method = SummationMethod.Physical,
                GlobalAlphaBeta = 3.0,
                Plans = new List<SummationPlanEntry>
                {
                    new SummationPlanEntry { CourseId = "C1", PlanId = "PlanRef", DisplayLabel = "Ref",
                        NumberOfFractions = 25, TotalDoseGy = 50, Weight = 1.0, IsReference = true }
                }
            };
            svc.PrepareData(config).Success.Should().BeTrue();
            var r = await svc.ComputeAsync(null, CancellationToken.None);
            r.Success.Should().BeTrue();
            r.MaxDoseGy.Should().BeApproximately(5, 1e-6);
        }

        // ── DIR pathway: identity DVF ──────────────────────────────────────

        [Fact]
        public async Task ComputeAsync_DirWithZeroDisplacement_SummedEqualsRefPlusMov()
        {
            // Ref grid dose = 3 Gy, Mov dose = 7 Gy, DVF = identity (no shift).
            // Physical summation: per-voxel sum = 3 + 7 = 10 Gy (weight 1 each).
            var loader = MakeLoader(refDoseGy: 3, movingDoseGy: 7,
                movingDvf: MakeDvf(new Vec3(0, 0, 0)));
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(MakeDvf(new Vec3(0, 0, 0)))).Success.Should().BeTrue();

            var r = await svc.ComputeAsync(null, CancellationToken.None);
            r.Success.Should().BeTrue();
            r.MaxDoseGy.Should().BeApproximately(10, 1e-6,
                "identity DVF preserves moving dose so ref+mov = 3+7 = 10 Gy");
        }

        // ── DIR pathway: grid mismatch triggers slow path ──────────────────

        [Fact]
        public async Task ComputeAsync_DirWithMismatchedGrid_UsesSlowPathWithoutThrowing()
        {
            // DVF has different spacing than reference — must use world-coordinate lookup.
            // Still expected to produce non-zero summed dose (DVF origin covers ref region).
            var dvfVectors = new Vec3[RefZ][,];
            for (int z = 0; z < RefZ; z++)
            {
                dvfVectors[z] = new Vec3[RefX, RefY];
                for (int y = 0; y < RefY; y++)
                    for (int x = 0; x < RefX; x++)
                        dvfVectors[z][x, y] = new Vec3(0, 0, 0);
            }
            var mismatchedDvf = new DeformationField
            {
                XSize = RefX, YSize = RefY, ZSize = RefZ,
                XRes = 1.0, YRes = 1.0, ZRes = 1.0,
                // Different origin → slow path forced
                Origin = new Vec3(0.5, 0.5, 0.5),
                XDirection = new Vec3(1, 0, 0),
                YDirection = new Vec3(0, 1, 0),
                ZDirection = new Vec3(0, 0, 1),
                Vectors = dvfVectors
            };

            var loader = MakeLoader(refDoseGy: 2, movingDoseGy: 8, movingDvf: mismatchedDvf);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(mismatchedDvf)).Success.Should().BeTrue();

            var r = await svc.ComputeAsync(null, CancellationToken.None);
            r.Success.Should().BeTrue();
            r.MaxDoseGy.Should().BeGreaterThan(0, "slow path must still sample dose");
        }

        [Fact]
        public async Task ComputeAsync_WithCancelledToken_PropagatesCancellation()
        {
            var loader = MakeLoader(refDoseGy: 1, movingDoseGy: 1, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            // Task.Run with a pre-cancelled token schedules a canceled Task; awaiting surfaces
            // TaskCanceledException. That is the contract the UI layer expects — it catches
            // OperationCanceledException to distinguish user-cancel from server error.
            System.Func<Task> act = async () => await svc.ComputeAsync(null, cts.Token);
            await act.Should().ThrowAsync<System.Threading.Tasks.TaskCanceledException>();
        }

        [Fact]
        public void Dispose_ClearsInternalState()
        {
            var loader = MakeLoader(refDoseGy: 1, movingDoseGy: 1, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();
            svc.Dispose();
            svc.HasSummedDose.Should().BeFalse();
        }

        [Fact]
        public void SliceCount_MatchesReferenceZSize()
        {
            var loader = MakeLoader(refDoseGy: 0, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null));
            svc.SliceCount.Should().Be(RefZ);
        }

        [Fact]
        public void VoxelVolume_ComputedFromSpacing_1mm3IsOneNanoLiter()
        {
            var loader = MakeLoader(refDoseGy: 0, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null));
            // 1mm × 1mm × 1mm = 0.001 cm³ = 1 µL
            svc.GetVoxelVolumeCc().Should().BeApproximately(0.001, 1e-9);
        }

        // ── DIR pathway: positive X shift verifies mapping direction ──────

        /// <summary>
        /// Moving dose varies as (x+1)*10 Gy per voxel so each X column is distinguishable.
        /// With a uniform DVF of (+1, 0, 0) mm, the reference voxel (x, y, z) should receive
        /// the moving dose sampled at (x+1, y, z). Verifying specific voxels locks down the
        /// "DVF is forward mapping (ref → moving)" convention and rules out sign errors.
        /// </summary>
        [Fact]
        public async Task ComputeAsync_DirWithPositiveXShift_MapsDoseFromShiftedSource()
        {
            // Build moving dose = (x+1)*10 Gy everywhere.
            var movingDose = new int[RefZ][,];
            for (int z = 0; z < RefZ; z++)
            {
                movingDose[z] = new int[RefX, RefY];
                for (int y = 0; y < RefY; y++)
                    for (int x = 0; x < RefX; x++)
                        movingDose[z][x, y] = (x + 1) * 10;
            }
            var movingDoseData = MakeDoseData(movingDose);

            // Reference plan has zero dose so the sum == mapped moving dose only.
            var refDoseData = MakeDoseData(FillDose(0));

            var loader = new Mock<ISummationDataLoader>(MockBehavior.Strict);
            loader.Setup(l => l.LoadPlanDose("C1", "PlanRef", It.IsAny<double>())).Returns(refDoseData);
            loader.Setup(l => l.LoadPlanDose("C1", "PlanMov", It.IsAny<double>())).Returns(movingDoseData);
            loader.Setup(l => l.LoadStructureContours(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(new List<StructureData>());
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanRef")).Returns("FOR_REF");
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanMov")).Returns("FOR_MOV");

            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(MakeDvf(new Vec3(1, 0, 0)))).Success.Should().BeTrue();
            var r = await svc.ComputeAsync(null, CancellationToken.None);
            r.Success.Should().BeTrue();

            var slice0 = svc.GetSummedSlice(0);
            slice0.Should().NotBeNull();
            // ref (0, 0, 0) sampled at moving (1, 0, 0) → dose (1+1)*10 = 20
            slice0![0 + 0 * RefX].Should().BeApproximately(20, 1e-4);
            // ref (1, 0, 0) sampled at moving (2, 0, 0) → dose (2+1)*10 = 30
            slice0[1 + 0 * RefX].Should().BeApproximately(30, 1e-4);
            // ref (2, 0, 0) sampled at moving (3, 0, 0) → dose (3+1)*10 = 40
            slice0[2 + 0 * RefX].Should().BeApproximately(40, 1e-4);
            // ref (3, 0, 0) sampled at moving (4, 0, 0) → out of bounds → 0
            slice0[3 + 0 * RefX].Should().BeApproximately(0, 1e-4);
        }

        // ── Affine (registered) pathway ────────────────────────────────────

        /// <summary>
        /// Affine path (RegistrationId set, no DVF) with identity matrix must behave exactly
        /// like the direct path — covers AccumulatePhysicalRegistered.
        /// </summary>
        [Fact]
        public async Task ComputeAsync_AffineIdentityMatrix_SamplesAtSamePosition()
        {
            // Moving dose varies by X so we can verify per-voxel correctness.
            var movingDose = new int[RefZ][,];
            for (int z = 0; z < RefZ; z++)
            {
                movingDose[z] = new int[RefX, RefY];
                for (int y = 0; y < RefY; y++)
                    for (int x = 0; x < RefX; x++)
                        movingDose[z][x, y] = x * 10;
            }

            var loader = new Mock<ISummationDataLoader>(MockBehavior.Strict);
            loader.Setup(l => l.LoadPlanDose("C1", "PlanRef", It.IsAny<double>())).Returns(MakeDoseData(FillDose(0)));
            loader.Setup(l => l.LoadPlanDose("C1", "PlanMov", It.IsAny<double>())).Returns(MakeDoseData(movingDose));
            loader.Setup(l => l.LoadStructureContours(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns(new List<StructureData>());
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanRef")).Returns("FOR_REF");
            loader.Setup(l => l.GetPlanImageFOR("C1", "PlanMov")).Returns("FOR_REF"); // same FOR

            var identityMatrix = new RegistrationData
            {
                Id = "REG_ID",
                SourceFOR = "FOR_REF",
                RegisteredFOR = "FOR_REF",
                Matrix = new double[]
                {
                    1, 0, 0, 0,
                    0, 1, 0, 0,
                    0, 0, 1, 0,
                    0, 0, 0, 1
                }
            };

            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData> { identityMatrix });
            var config = new SummationConfig
            {
                Method = SummationMethod.Physical,
                GlobalAlphaBeta = 3.0,
                Plans = new List<SummationPlanEntry>
                {
                    new SummationPlanEntry { CourseId = "C1", PlanId = "PlanRef", DisplayLabel = "Ref",
                        NumberOfFractions = 25, TotalDoseGy = 50, Weight = 1.0, IsReference = true },
                    new SummationPlanEntry { CourseId = "C1", PlanId = "PlanMov", DisplayLabel = "Mov",
                        NumberOfFractions = 25, TotalDoseGy = 50, Weight = 1.0, IsReference = false,
                        RegistrationId = "REG_ID" }
                }
            };
            svc.PrepareData(config).Success.Should().BeTrue();
            var r = await svc.ComputeAsync(null, CancellationToken.None);
            r.Success.Should().BeTrue();

            var slice = svc.GetSummedSlice(0);
            slice.Should().NotBeNull();
            // Identity transform → each ref voxel gets moving dose at same index
            // ref (0, 0, 0) → moving (0, 0, 0) → 0*10 = 0
            slice![0 + 0 * RefX].Should().BeApproximately(0, 1e-4);
            // ref (2, 0, 0) → moving (2, 0, 0) → 2*10 = 20
            slice[2 + 0 * RefX].Should().BeApproximately(20, 1e-4);
        }

        // ── Structure DVH ──────────────────────────────────────────────────

        [Fact]
        public async Task ComputeStructureEQD2DVH_NonexistentStructure_ReturnsEmpty()
        {
            var loader = MakeLoader(refDoseGy: 5, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();
            await svc.ComputeAsync(null, CancellationToken.None);

            var dvh = svc.ComputeStructureEQD2DVH("NonExistent", structureAlphaBeta: 3.0, maxDoseGy: 10);
            dvh.Should().BeEmpty();
        }

        [Fact]
        public async Task ComputeStructureEQD2DVH_ZeroMaxDose_ReturnsEmpty()
        {
            var loader = MakeLoader(refDoseGy: 5, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();
            await svc.ComputeAsync(null, CancellationToken.None);

            var dvh = svc.ComputeStructureEQD2DVH("AnyStruct", structureAlphaBeta: 3.0, maxDoseGy: 0);
            dvh.Should().BeEmpty();
        }

        // ── Display α/β recompute ──────────────────────────────────────────

        [Fact]
        public async Task RecomputeEQD2DisplayAsync_WithoutPriorCompute_ReturnsFailure()
        {
            var loader = MakeLoader(refDoseGy: 5, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();
            // Note: no ComputeAsync call → no cached physical slices
            var r = await svc.RecomputeEQD2DisplayAsync(3.0, null, CancellationToken.None);
            r.Success.Should().BeFalse();
            r.StatusMessage.Should().Contain("No");
        }

        [Fact]
        public async Task RecomputeEQD2DisplayAsync_PhysicalMode_ReproducesOriginalMaxDose()
        {
            // In Physical mode, changing α/β should NOT change the summed dose — it's a no-op.
            var loader = MakeLoader(refDoseGy: 3, movingDoseGy: 7, movingDvf: MakeDvf(new Vec3(0, 0, 0)));
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(MakeDvf(new Vec3(0, 0, 0)))).Success.Should().BeTrue();
            var original = await svc.ComputeAsync(null, CancellationToken.None);
            double originalMax = original.MaxDoseGy;

            var recomputed = await svc.RecomputeEQD2DisplayAsync(10.0, null, CancellationToken.None);
            recomputed.Success.Should().BeTrue();
            recomputed.MaxDoseGy.Should().BeApproximately(originalMax, 1e-6,
                "Physical-mode summation is α/β-independent");
        }

        // ── Round-trip: GetSummedSlice / GetStructureMask ──────────────────

        [Fact]
        public async Task GetSummedSlice_OutOfRangeIndex_ReturnsNull()
        {
            var loader = MakeLoader(refDoseGy: 1, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null)).Success.Should().BeTrue();
            await svc.ComputeAsync(null, CancellationToken.None);

            svc.GetSummedSlice(-1).Should().BeNull();
            svc.GetSummedSlice(RefZ).Should().BeNull();
            svc.GetSummedSlice(RefZ + 10).Should().BeNull();
        }

        [Fact]
        public void GetSummedSlice_BeforeCompute_ReturnsNull()
        {
            var loader = MakeLoader(refDoseGy: 1, movingDoseGy: 0, movingDvf: null);
            var svc = new SummationService(MakeReferenceCt(), loader.Object, new List<RegistrationData>());
            svc.PrepareData(MakeConfig(null));
            svc.GetSummedSlice(0).Should().BeNull("no compute → no data");
        }
    }
}
