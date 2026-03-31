using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Calculations;
using EQD2Viewer.Core.Calculations;

namespace ESAPI_EQD2Viewer.Tests.Calculations
{
    /// <summary>
    /// Comprehensive tests for the EQD2 calculator — the most safety-critical component.
    /// 
    /// EQD2 formula: D × (d + α/β) / (2 + α/β), where d = D/n
    /// 
    /// Test categories:
    /// 1. Mathematical correctness against hand-calculated values
    /// 2. Clinical scenario validation (common fractionation schemes)
    /// 3. Edge cases and boundary conditions
    /// 4. Voxel scaling factor consistency
    /// 5. DVH curve conversion integrity
    /// 6. Mean EQD2 from DVH accuracy
    /// </summary>
    public class EQD2CalculatorTests
    {
        private const double Tolerance = 0.001;
        private const double StrictTolerance = 1e-10;

        // ════════════════════════════════════════════════════════
        // 1. BASIC FORMULA CORRECTNESS
        // ════════════════════════════════════════════════════════

        [Theory]
        [InlineData(50.0, 25, 3.0, 50.0)]     // 2 Gy/fx, α/β=3 → EQD2 = 50.0
        [InlineData(60.0, 30, 10.0, 60.0)]    // 2 Gy/fx, α/β=10 → EQD2 = 60.0
        [InlineData(50.0, 25, 10.0, 50.0)]    // 2 Gy/fx, α/β=10 → EQD2 = 50.0
        public void ToEQD2_WithStandard2GyFractions_ShouldReturnSameDose(
            double totalDose, int fractions, double alphaBeta, double expected)
        {
            // 2 Gy per fraction → EQD2 = total dose (by definition)
            double result = EQD2Calculator.ToEQD2(totalDose, fractions, alphaBeta);
            result.Should().BeApproximately(expected, Tolerance,
                "because 2 Gy/fraction always gives EQD2 = physical dose");
        }

        [Theory]
        [InlineData(45.0, 15, 3.0, 54.0)]     // 3 Gy/fx: 45*(3+3)/(2+3) = 54.0
        [InlineData(45.0, 15, 10.0, 48.75)]   // 3 Gy/fx: 45*(3+10)/(2+10) = 48.75
        [InlineData(20.0, 5, 3.0, 28.0)]      // 4 Gy/fx: 20*(4+3)/(2+3) = 28.0 → 20*7/5=28 ... wait
        public void ToEQD2_WithHypofractionation_ShouldReturnHigherDose(
            double totalDose, int fractions, double alphaBeta, double expected)
        {
            // d > 2 Gy → EQD2 > physical dose
            double result = EQD2Calculator.ToEQD2(totalDose, fractions, alphaBeta);
            result.Should().BeApproximately(expected, Tolerance);
            result.Should().BeGreaterThan(totalDose,
                "because dose per fraction > 2 Gy means EQD2 > physical dose");
        }

        [Theory]
        [InlineData(60.0, 60, 3.0, 48.0)]     // 1 Gy/fx: 60*(1+3)/(2+3) = 48.0
        [InlineData(60.0, 60, 10.0, 55.0)]    // 1 Gy/fx: 60*(1+10)/(2+10) = 55.0
        public void ToEQD2_WithHyperfractionation_ShouldReturnLowerDose(
            double totalDose, int fractions, double alphaBeta, double expected)
        {
            // d < 2 Gy → EQD2 < physical dose
            double result = EQD2Calculator.ToEQD2(totalDose, fractions, alphaBeta);
            result.Should().BeApproximately(expected, Tolerance);
            result.Should().BeLessThan(totalDose,
                "because dose per fraction < 2 Gy means EQD2 < physical dose");
        }

        // ════════════════════════════════════════════════════════
        // 2. CLINICAL SCENARIO VALIDATION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void ToEQD2_SBRT_SpinalCord_ShouldMatchHandCalculation()
        {
            // SBRT spine: 24 Gy in 3 fractions, cord α/β = 2 Gy
            // d = 24/3 = 8 Gy/fx
            // EQD2 = 24 * (8 + 2) / (2 + 2) = 24 * 10 / 4 = 60 Gy
            double result = EQD2Calculator.ToEQD2(24.0, 3, 2.0);
            result.Should().BeApproximately(60.0, Tolerance);
        }

        [Fact]
        public void ToEQD2_ConventionalProstate_ShouldMatchHandCalculation()
        {
            // Prostate: 78 Gy in 39 fractions, tumor α/β = 1.5 Gy (low for prostate)
            // d = 78/39 = 2 Gy/fx
            // EQD2 = 78 * (2 + 1.5) / (2 + 1.5) = 78 Gy (identity at 2 Gy/fx)
            double result = EQD2Calculator.ToEQD2(78.0, 39, 1.5);
            result.Should().BeApproximately(78.0, Tolerance);
        }

        [Fact]
        public void ToEQD2_SRS_SingleFraction_ShouldMatchHandCalculation()
        {
            // SRS: 20 Gy in 1 fraction, α/β = 10
            // d = 20/1 = 20 Gy
            // EQD2 = 20 * (20 + 10) / (2 + 10) = 20 * 30 / 12 = 50 Gy
            double result = EQD2Calculator.ToEQD2(20.0, 1, 10.0);
            result.Should().BeApproximately(50.0, Tolerance);
        }

        [Fact]
        public void ToEQD2_Palliative_830_ShouldMatchHandCalculation()
        {
            // Palliative: 8 Gy single fraction, α/β = 3
            // d = 8/1 = 8 Gy
            // EQD2 = 8 * (8 + 3) / (2 + 3) = 8 * 11 / 5 = 17.6 Gy
            double result = EQD2Calculator.ToEQD2(8.0, 1, 3.0);
            result.Should().BeApproximately(17.6, Tolerance);
        }

        [Fact]
        public void ToEQD2_Palliative_2005_ShouldMatchHandCalculation()
        {
            // 20 Gy in 5 fractions, α/β = 3
            // d = 20/5 = 4 Gy/fx
            // EQD2 = 20 * (4 + 3) / (2 + 3) = 20 * 7 / 5 = 28 Gy
            double result = EQD2Calculator.ToEQD2(20.0, 5, 3.0);
            result.Should().BeApproximately(28.0, Tolerance);
        }

        [Fact]
        public void ToEQD2_BreastHypo_ShouldMatchHandCalculation()
        {
            // Breast hypofractionation: 40.05 Gy in 15 fractions, α/β = 4
            // d = 40.05/15 = 2.67 Gy/fx
            // EQD2 = 40.05 * (2.67 + 4) / (2 + 4) = 40.05 * 6.67 / 6 = 44.5
            double result = EQD2Calculator.ToEQD2(40.05, 15, 4.0);
            double expected = 40.05 * (40.05 / 15.0 + 4.0) / (2.0 + 4.0);
            result.Should().BeApproximately(expected, Tolerance);
        }

        // ════════════════════════════════════════════════════════
        // 3. EDGE CASES AND BOUNDARY CONDITIONS
        // ════════════════════════════════════════════════════════

        [Fact]
        public void ToEQD2_ZeroFractions_ShouldReturnTotalDose()
        {
            double result = EQD2Calculator.ToEQD2(50.0, 0, 3.0);
            result.Should().Be(50.0, "fallback when fractions invalid");
        }

        [Fact]
        public void ToEQD2_NegativeFractions_ShouldReturnTotalDose()
        {
            double result = EQD2Calculator.ToEQD2(50.0, -5, 3.0);
            result.Should().Be(50.0);
        }

        [Fact]
        public void ToEQD2_ZeroAlphaBeta_ShouldReturnTotalDose()
        {
            double result = EQD2Calculator.ToEQD2(50.0, 25, 0.0);
            result.Should().Be(50.0, "α/β=0 is clinically invalid, should fallback");
        }

        [Fact]
        public void ToEQD2_NegativeAlphaBeta_ShouldReturnTotalDose()
        {
            double result = EQD2Calculator.ToEQD2(50.0, 25, -3.0);
            result.Should().Be(50.0);
        }

        [Fact]
        public void ToEQD2_ZeroDose_ShouldReturnZero()
        {
            double result = EQD2Calculator.ToEQD2(0.0, 25, 3.0);
            result.Should().Be(0.0);
        }

        [Fact]
        public void ToEQD2_VerySmallDose_ShouldNotOverflow()
        {
            double result = EQD2Calculator.ToEQD2(0.001, 1, 3.0);
            result.Should().BeGreaterThan(0);
            result.Should().BeLessThan(1.0);
            double.IsNaN(result).Should().BeFalse();
            double.IsInfinity(result).Should().BeFalse();
        }

        [Fact]
        public void ToEQD2_VeryLargeDose_ShouldNotOverflow()
        {
            double result = EQD2Calculator.ToEQD2(1000.0, 1, 3.0);
            double.IsNaN(result).Should().BeFalse();
            double.IsInfinity(result).Should().BeFalse();
            result.Should().BeGreaterThan(1000.0);
        }

        [Fact]
        public void ToEQD2_VerySmallAlphaBeta_ShouldNotOverflow()
        {
            // α/β = 0.1 (extreme but valid for some models)
            double result = EQD2Calculator.ToEQD2(50.0, 25, 0.1);
            double.IsNaN(result).Should().BeFalse();
            double.IsInfinity(result).Should().BeFalse();
        }

        [Fact]
        public void ToEQD2_VeryLargeAlphaBeta_ShouldApproachPhysicalDose()
        {
            // As α/β → ∞, EQD2 → D (because (d+α/β)/(2+α/β) → 1)
            double result = EQD2Calculator.ToEQD2(50.0, 25, 1000.0);
            result.Should().BeApproximately(50.0, 0.1,
                "because very large α/β makes fractionation effect negligible");
        }

        [Fact]
        public void ToEQD2_OneFraction_ShouldMatchFormula()
        {
            // 18 Gy × 1 fx, α/β = 10: EQD2 = 18 * (18+10)/(2+10) = 18*28/12 = 42
            double result = EQD2Calculator.ToEQD2(18.0, 1, 10.0);
            result.Should().BeApproximately(42.0, Tolerance);
        }

        // ════════════════════════════════════════════════════════
        // 4. MONOTONICITY AND CONSISTENCY PROPERTIES
        // ════════════════════════════════════════════════════════

        [Theory]
        [InlineData(3.0)]
        [InlineData(10.0)]
        public void ToEQD2_IncreasingDose_ShouldBeMonotonic(double alphaBeta)
        {
            // Higher total dose → higher EQD2 (same fractionation)
            double prev = 0;
            for (int dose = 1; dose <= 100; dose += 5)
            {
                double eqd2 = EQD2Calculator.ToEQD2(dose, 25, alphaBeta);
                eqd2.Should().BeGreaterThan(prev,
                    $"EQD2 must increase monotonically with dose (D={dose})");
                prev = eqd2;
            }
        }

        [Theory]
        [InlineData(3.0)]
        [InlineData(10.0)]
        public void ToEQD2_MoreFractions_SameDose_ShouldDecrease(double alphaBeta)
        {
            // Same total dose, more fractions → lower dose per fraction → lower EQD2
            double totalDose = 60.0;
            double prev = double.MaxValue;
            foreach (int fx in new[] { 5, 10, 15, 20, 25, 30, 40, 60 })
            {
                double eqd2 = EQD2Calculator.ToEQD2(totalDose, fx, alphaBeta);
                eqd2.Should().BeLessThan(prev,
                    $"More fractions at same total dose should reduce EQD2 (fx={fx})");
                prev = eqd2;
            }
        }

        [Fact]
        public void ToEQD2_LowerAlphaBeta_ShouldGiveHigherEQD2ForHypo()
        {
            // For hypofractionation (d > 2 Gy), lower α/β → higher EQD2
            double dose = 30.0;
            int fx = 5; // 6 Gy/fx
            double eqd2_ab3 = EQD2Calculator.ToEQD2(dose, fx, 3.0);
            double eqd2_ab10 = EQD2Calculator.ToEQD2(dose, fx, 10.0);
            eqd2_ab3.Should().BeGreaterThan(eqd2_ab10,
                "lower α/β tissues are more sensitive to large fractions");
        }

        // ════════════════════════════════════════════════════════
        // 5. VOXEL SCALING FACTOR TESTS
        // ════════════════════════════════════════════════════════

        [Theory]
        [InlineData(25, 3.0)]
        [InlineData(25, 10.0)]
        [InlineData(1, 3.0)]
        [InlineData(5, 2.0)]
        public void GetVoxelScalingFactors_ToEQD2Fast_ShouldMatchToEQD2(int fractions, double alphaBeta)
        {
            // The fast path (ToEQD2Fast) must give identical results to ToEQD2
            EQD2Calculator.GetVoxelScalingFactors(fractions, alphaBeta,
                out double qf, out double lf);

            foreach (double dose in new[] { 0.0, 0.5, 1.0, 2.0, 5.0, 10.0, 50.0, 100.0 })
            {
                double standard = EQD2Calculator.ToEQD2(dose, fractions, alphaBeta);
                double fast = EQD2Calculator.ToEQD2Fast(dose, qf, lf);
                fast.Should().BeApproximately(standard, 1e-8,
                    $"Fast path must match standard for D={dose}, n={fractions}, α/β={alphaBeta}");
            }
        }

        [Fact]
        public void GetVoxelScalingFactors_InvalidInput_ShouldReturnIdentity()
        {
            EQD2Calculator.GetVoxelScalingFactors(0, 3.0, out double qf, out double lf);
            qf.Should().Be(0);
            lf.Should().Be(1.0, "identity transform for invalid fractions");
        }

        // ════════════════════════════════════════════════════════
        // 6. RE-IRRADIATION SUMMATION SCENARIO
        // ════════════════════════════════════════════════════════

        [Fact]
        public void ToEQD2_ReIrradiationSummation_ShouldBeSumOfIndividualEQD2()
        {
            // Plan 1: 50 Gy / 25 fx, α/β = 3
            // Plan 2: 30 Gy / 10 fx, α/β = 3
            // Total EQD2 = EQD2(plan1) + EQD2(plan2)
            double eqd2_plan1 = EQD2Calculator.ToEQD2(50.0, 25, 3.0); // = 50 Gy (2 Gy/fx)
            double eqd2_plan2 = EQD2Calculator.ToEQD2(30.0, 10, 3.0); // = 30*(3+3)/(2+3) = 36 Gy
            double totalEQD2 = eqd2_plan1 + eqd2_plan2;

            eqd2_plan1.Should().BeApproximately(50.0, Tolerance);
            eqd2_plan2.Should().BeApproximately(36.0, Tolerance);
            totalEQD2.Should().BeApproximately(86.0, Tolerance);
        }

        // ════════════════════════════════════════════════════════
        // 7. SYMMETRY AND MATHEMATICAL PROPERTIES
        // ════════════════════════════════════════════════════════

        [Fact]
        public void ToEQD2_ShouldBeSymmetricInScaling()
        {
            // EQD2(2D, 2n, α/β) should NOT equal 2 * EQD2(D, n, α/β)
            // because dose per fraction stays the same
            double eqd2_single = EQD2Calculator.ToEQD2(30.0, 10, 3.0);
            double eqd2_double = EQD2Calculator.ToEQD2(60.0, 20, 3.0);

            // Same dose per fraction → same scaling → EQD2 doubles linearly
            eqd2_double.Should().BeApproximately(2 * eqd2_single, Tolerance,
                "doubling both dose and fractions preserves dose/fraction, so EQD2 scales linearly");
        }

        [Fact]
        public void ToEQD2_ShouldBeNonNegativeForNonNegativeInput()
        {
            for (double dose = 0; dose <= 100; dose += 2.5)
            {
                for (int fx = 1; fx <= 40; fx += 5)
                {
                    double result = EQD2Calculator.ToEQD2(dose, fx, 3.0);
                    result.Should().BeGreaterOrEqualTo(0,
                        $"EQD2 must be non-negative for D={dose}, n={fx}");
                }
            }
        }
    }
}