using EQD2Viewer.Core.Data;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace EQD2Viewer.Core.Interfaces
{
    /// <summary>
    /// Performs deformable image registration between two CT volumes.
    /// Implementation is optional — the App loads an implementation (e.g. ItkRegistrationService)
    /// at runtime via reflection when available. When null, DIR-based summation is unavailable.
    /// </summary>
    public interface IRegistrationService
    {
        /// <summary>
        /// Registers <paramref name="moving"/> onto <paramref name="fixed_"/> and returns
        /// a deformation vector field on the fixed image grid. Returns null if the
        /// registration is unavailable or fails.
        /// </summary>
        Task<DeformationField?> RegisterAsync(
            VolumeData fixed_,
            VolumeData moving,
            IProgress<int>? progress,
            CancellationToken ct);
    }
}
