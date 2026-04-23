using EQD2Viewer.Core.Data;
using EQD2Viewer.Core.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Registration.Services
{
    /// <summary>
    /// Pass-through stub used when EQD2Viewer.Registration.ITK is not loaded.
    /// Returns null so callers fall back to affine-only summation.
    /// </summary>
    public class StubRegistrationService : IRegistrationService
    {
        public Task<DeformationField?> RegisterAsync(
            VolumeData fixed_,
            VolumeData moving,
            IProgress<int>? progress,
            CancellationToken ct)
            => Task.FromResult<DeformationField?>(null);
    }
}
