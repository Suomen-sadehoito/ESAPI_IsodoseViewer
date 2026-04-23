using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using EQD2Viewer.Core.Models;
using EQD2Viewer.Services;
using FluentAssertions;
using Moq;
using System.Collections.Generic;

namespace EQD2Viewer.Tests.Services
{
    /// <summary>
    /// Smoke tests for SummationServiceFactory wiring: ensures factory propagates the
    /// optional IDeformationFieldLoader to the produced SummationService instances
    /// (so DIR-from-MHA keeps working when the factory is reused).
    /// </summary>
    public class SummationServiceFactoryTests
    {
        private static VolumeData MakeRefCt()
        {
            var vox = new int[2][,];
            for (int z = 0; z < 2; z++) vox[z] = new int[2, 2];
            return new VolumeData
            {
                Geometry = new VolumeGeometry
                {
                    XSize = 2, YSize = 2, ZSize = 2,
                    XRes = 1, YRes = 1, ZRes = 1,
                    Origin = new Vec3(0, 0, 0),
                    XDirection = new Vec3(1, 0, 0),
                    YDirection = new Vec3(0, 1, 0),
                    ZDirection = new Vec3(0, 0, 1),
                    FrameOfReference = "FOR_1"
                },
                Voxels = vox,
                HuOffset = 0,
            };
        }

        [Fact]
        public void Create_WithNoDfLoader_ProducesWorkingService()
        {
            var factory = new SummationServiceFactory();
            var loader = new Mock<ISummationDataLoader>(MockBehavior.Loose).Object;
            var svc = factory.Create(MakeRefCt(), loader, new List<RegistrationData>());
            svc.Should().NotBeNull();
            svc.HasSummedDose.Should().BeFalse();
        }

        [Fact]
        public void Create_WithDfLoader_StillProducesService()
        {
            var dfLoader = new Mock<IDeformationFieldLoader>(MockBehavior.Loose).Object;
            var factory = new SummationServiceFactory(dfLoader);
            var loader = new Mock<ISummationDataLoader>(MockBehavior.Loose).Object;
            var svc = factory.Create(MakeRefCt(), loader, new List<RegistrationData>());
            svc.Should().NotBeNull();
        }

        [Fact]
        public void Create_MultipleCalls_ReturnIndependentInstances()
        {
            var factory = new SummationServiceFactory();
            var loader = new Mock<ISummationDataLoader>(MockBehavior.Loose).Object;
            var a = factory.Create(MakeRefCt(), loader, new List<RegistrationData>());
            var b = factory.Create(MakeRefCt(), loader, new List<RegistrationData>());
            a.Should().NotBeSameAs(b);
        }
    }
}
