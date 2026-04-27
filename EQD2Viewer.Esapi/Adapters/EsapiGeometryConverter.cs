using EQD2Viewer.Core.Data;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EQD2Viewer.Esapi.Adapters
{
    /// <summary>
    /// Shared conversions between Varian ESAPI spatial types and the
    /// domain-side <see cref="VolumeGeometry"/> / <see cref="Vec3"/> types.
    /// Both <see cref="EsapiDataSource"/> and <see cref="EsapiSummationDataLoader"/>
    /// formerly carried byte-identical copies of these helpers.
    /// </summary>
    internal static class EsapiGeometryConverter
    {
        /// <summary>
        /// Builds a <see cref="VolumeGeometry"/> from an ESAPI <see cref="Image"/>,
        /// preserving direction cosines, spacing, frame-of-reference id, and image id.
        /// </summary>
        public static VolumeGeometry ToVolumeGeometry(Image img) => new VolumeGeometry
        {
            XSize = img.XSize,
            YSize = img.YSize,
            ZSize = img.ZSize,
            XRes = img.XRes,
            YRes = img.YRes,
            ZRes = img.ZRes,
            Origin = ToVec3(img.Origin),
            XDirection = ToVec3(img.XDirection),
            YDirection = ToVec3(img.YDirection),
            ZDirection = ToVec3(img.ZDirection),
            FrameOfReference = img.FOR ?? "",
            Id = img.Id ?? ""
        };

        /// <summary>
        /// Builds a <see cref="VolumeGeometry"/> from an ESAPI <see cref="Dose"/>
        /// grid. The dose grid carries no FOR or id — those fields are left blank.
        /// </summary>
        public static VolumeGeometry ToVolumeGeometry(Dose dose) => new VolumeGeometry
        {
            XSize = dose.XSize,
            YSize = dose.YSize,
            ZSize = dose.ZSize,
            XRes = dose.XRes,
            YRes = dose.YRes,
            ZRes = dose.ZRes,
            Origin = ToVec3(dose.Origin),
            XDirection = ToVec3(dose.XDirection),
            YDirection = ToVec3(dose.YDirection),
            ZDirection = ToVec3(dose.ZDirection)
        };

        /// <summary>
        /// Converts an ESAPI <see cref="VVector"/> to the domain-side
        /// <see cref="Vec3"/>.
        /// </summary>
        public static Vec3 ToVec3(VVector v) => new Vec3(v.x, v.y, v.z);
    }
}
