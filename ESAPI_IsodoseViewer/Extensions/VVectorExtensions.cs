using VMS.TPS.Common.Model.Types;

// MUUTOS TÄSSÄ: Lisää .Helpers
namespace ESAPI_IsodoseViewer.Helpers
{
    public static class VVectorExtensions
    {
        // Pistetulo: v1.Dot(v2)
        public static double Dot(this VVector v1, VVector v2)
        {
            return v1.x * v2.x + v1.y * v2.y + v1.z * v2.z;
        }

        // Ristitulo: v1.Cross(v2)
        public static VVector Cross(this VVector v1, VVector v2)
        {
            return new VVector(
                v1.y * v2.z - v1.z * v2.y,
                v1.z * v2.x - v1.x * v2.z,
                v1.x * v2.y - v1.y * v2.x
            );
        }
    }
}