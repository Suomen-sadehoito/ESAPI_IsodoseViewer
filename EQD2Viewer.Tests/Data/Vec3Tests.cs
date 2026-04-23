using EQD2Viewer.Core.Data;
using FluentAssertions;

namespace EQD2Viewer.Tests.Data
{
    /// <summary>
    /// Unit tests for the Vec3 struct — ESAPI-independent 3D vector.
    /// Exercises arithmetic operators, Dot product, and FromArray deserialization.
    /// </summary>
    public class Vec3Tests
    {
        [Fact]
        public void Constructor_StoresComponents()
        {
            var v = new Vec3(1.5, -2.5, 3.25);
            v.X.Should().Be(1.5);
            v.Y.Should().Be(-2.5);
            v.Z.Should().Be(3.25);
        }

        [Fact]
        public void Default_IsZero()
        {
            var v = default(Vec3);
            v.X.Should().Be(0);
            v.Y.Should().Be(0);
            v.Z.Should().Be(0);
        }

        [Fact]
        public void Addition_Componentwise()
        {
            var result = new Vec3(1, 2, 3) + new Vec3(10, 20, 30);
            result.X.Should().Be(11);
            result.Y.Should().Be(22);
            result.Z.Should().Be(33);
        }

        [Fact]
        public void Subtraction_Componentwise()
        {
            var result = new Vec3(5, 7, 9) - new Vec3(1, 2, 3);
            result.X.Should().Be(4);
            result.Y.Should().Be(5);
            result.Z.Should().Be(6);
        }

        [Fact]
        public void ScalarMultiplication_Left_ScalesAllComponents()
        {
            var result = 3.0 * new Vec3(1, 2, 3);
            result.X.Should().Be(3);
            result.Y.Should().Be(6);
            result.Z.Should().Be(9);
        }

        [Fact]
        public void ScalarMultiplication_Right_ScalesAllComponents()
        {
            var result = new Vec3(1, 2, 3) * 2.5;
            result.X.Should().Be(2.5);
            result.Y.Should().Be(5);
            result.Z.Should().Be(7.5);
        }

        [Fact]
        public void Dot_OrthogonalVectors_IsZero()
        {
            var a = new Vec3(1, 0, 0);
            var b = new Vec3(0, 1, 0);
            a.Dot(b).Should().Be(0);
        }

        [Fact]
        public void Dot_ParallelUnitVectors_IsOne()
        {
            var a = new Vec3(1, 0, 0);
            a.Dot(a).Should().Be(1);
        }

        [Fact]
        public void Dot_AntiparallelUnitVectors_IsNegativeOne()
        {
            var a = new Vec3(1, 0, 0);
            var b = new Vec3(-1, 0, 0);
            a.Dot(b).Should().Be(-1);
        }

        [Fact]
        public void Dot_GeneralVectors_SumOfProducts()
        {
            // (1,2,3) · (4,5,6) = 4 + 10 + 18 = 32
            new Vec3(1, 2, 3).Dot(new Vec3(4, 5, 6)).Should().Be(32);
        }

        [Fact]
        public void FromArray_ThreeElementArray_Parses()
        {
            var v = Vec3.FromArray(new[] { 7.0, 8.0, 9.0 });
            v.X.Should().Be(7);
            v.Y.Should().Be(8);
            v.Z.Should().Be(9);
        }

        [Fact]
        public void FromArray_NullArray_ReturnsDefault()
        {
            var v = Vec3.FromArray(null!);
            v.X.Should().Be(0);
            v.Y.Should().Be(0);
            v.Z.Should().Be(0);
        }

        [Fact]
        public void FromArray_TooShortArray_ReturnsDefault()
        {
            var v = Vec3.FromArray(new[] { 1.0, 2.0 });
            v.X.Should().Be(0);
            v.Y.Should().Be(0);
            v.Z.Should().Be(0);
        }

        [Fact]
        public void FromArray_LongerArray_UsesFirstThree()
        {
            var v = Vec3.FromArray(new[] { 1.0, 2.0, 3.0, 4.0, 5.0 });
            v.X.Should().Be(1);
            v.Y.Should().Be(2);
            v.Z.Should().Be(3);
        }

        [Fact]
        public void ToString_ProducesReadableFormat()
        {
            // Culture-agnostic: check numeric magnitude only (decimal separator varies by locale).
            var v = new Vec3(1.234, -2.567, 3.891);
            var s = v.ToString();
            s.Should().StartWith("(").And.EndWith(")");
            s.Should().MatchRegex(@"1[.,]23").And.MatchRegex(@"-2[.,]57").And.MatchRegex(@"3[.,]89");
        }

        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(1, 2, 3)]
        [InlineData(-10.5, 0, 100.25)]
        public void AddSubtract_RoundTrip_IsIdentity(double x, double y, double z)
        {
            var original = new Vec3(x, y, z);
            var offset = new Vec3(5, -3, 2);
            var roundTrip = (original + offset) - offset;
            roundTrip.X.Should().BeApproximately(x, 1e-10);
            roundTrip.Y.Should().BeApproximately(y, 1e-10);
            roundTrip.Z.Should().BeApproximately(z, 1e-10);
        }
    }
}
