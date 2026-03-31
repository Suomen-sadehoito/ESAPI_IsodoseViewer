namespace ESAPI_EQD2Viewer.Core.Data
{
    /// <summary>
    /// ESAPI-independent 3D vector type. Replaces VMS.TPS.Common.Model.Types.VVector
    /// in all Clean Architecture code. Identical mathematical semantics.
    /// </summary>
    public struct Vec3
    {
        public double X, Y, Z;

        public Vec3(double x, double y, double z)
        {
            X = x; Y = y; Z = z;
        }

        public static Vec3 operator +(Vec3 a, Vec3 b)
            => new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

        public static Vec3 operator -(Vec3 a, Vec3 b)
            => new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vec3 operator *(Vec3 v, double s)
            => new Vec3(v.X * s, v.Y * s, v.Z * s);

        public static Vec3 operator *(double s, Vec3 v)
            => new Vec3(v.X * s, v.Y * s, v.Z * s);

        public double Dot(Vec3 other)
            => X * other.X + Y * other.Y + Z * other.Z;

        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";

        /// <summary>
        /// Converts from fixture double[] format [x, y, z].
        /// </summary>
        public static Vec3 FromArray(double[] arr)
            => arr != null && arr.Length >= 3 ? new Vec3(arr[0], arr[1], arr[2]) : default;
    }
}
