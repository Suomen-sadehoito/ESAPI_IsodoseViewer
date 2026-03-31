using Xunit;
using FluentAssertions;
using ESAPI_EQD2Viewer.Core.Models;
using EQD2Viewer.Core.Models;
using System.Linq;

namespace ESAPI_EQD2Viewer.Tests.Models
{
    /// <summary>
    /// Tests for isodose level presets and configuration.
    /// Ensures clinical defaults are correct and consistent.
    /// </summary>
    public class IsodoseLevelTests
    {
        // ════════════════════════════════════════════════════════
        // PRESET VALIDATION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void GetEclipseDefaults_ShouldReturn10Levels()
        {
            var levels = IsodoseLevel.GetEclipseDefaults();
            levels.Should().HaveCount(10);
        }

        [Fact]
        public void GetEclipseDefaults_ShouldBeInDescendingOrder()
        {
            var levels = IsodoseLevel.GetEclipseDefaults();
            for (int i = 1; i < levels.Length; i++)
                levels[i].Fraction.Should().BeLessThan(levels[i - 1].Fraction,
                    "levels should be ordered highest to lowest");
        }

        [Fact]
        public void GetEclipseDefaults_ShouldContainCriticalLevels()
        {
            var levels = IsodoseLevel.GetEclipseDefaults();
            levels.Should().Contain(l => l.Fraction == 1.0, "must include 100% (prescription)");
            levels.Should().Contain(l => l.Fraction == 0.95, "must include 95% (PTV coverage)");
            levels.Should().Contain(l => l.Fraction == 0.50, "must include 50% (low dose)");
        }

        [Fact]
        public void GetEclipseDefaults_AllLevelsShouldBeVisible()
        {
            var levels = IsodoseLevel.GetEclipseDefaults();
            levels.Should().OnlyContain(l => l.IsVisible,
                "all default levels should start visible");
        }

        [Fact]
        public void GetEclipseDefaults_AllFractions_ShouldBeInValidRange()
        {
            var levels = IsodoseLevel.GetEclipseDefaults();
            levels.Should().OnlyContain(l => l.Fraction > 0 && l.Fraction <= 1.5,
                "fractions should be between 0 and 150%");
        }

        // ════════════════════════════════════════════════════════
        // ABSOLUTE MODE PRESETS
        // ════════════════════════════════════════════════════════

        [Fact]
        public void GetReIrradiationPreset_ShouldContainClinicalTolerances()
        {
            var levels = IsodoseLevel.GetReIrradiationPreset();
            levels.Should().Contain(l => l.AbsoluteDoseGy == 45,
                "should include 45 Gy spinal cord tolerance");
            levels.Should().Contain(l => l.AbsoluteDoseGy == 50,
                "should include 50 Gy brainstem tolerance");
        }

        [Fact]
        public void GetReIrradiationPreset_ShouldBeInDescendingDoseOrder()
        {
            var levels = IsodoseLevel.GetReIrradiationPreset();
            for (int i = 1; i < levels.Length; i++)
                levels[i].AbsoluteDoseGy.Should().BeLessThan(levels[i - 1].AbsoluteDoseGy);
        }

        [Fact]
        public void GetStereotacticPreset_ShouldExtendToHighDose()
        {
            var levels = IsodoseLevel.GetStereotacticPreset();
            levels.Max(l => l.AbsoluteDoseGy).Should().BeGreaterOrEqualTo(60,
                "stereotactic preset should cover high doses");
        }

        [Fact]
        public void GetPalliativePreset_MaxDose_ShouldBeLowerThanStereotactic()
        {
            var palliative = IsodoseLevel.GetPalliativePreset();
            var stereotactic = IsodoseLevel.GetStereotacticPreset();
            palliative.Max(l => l.AbsoluteDoseGy).Should()
                .BeLessThan(stereotactic.Max(l => l.AbsoluteDoseGy));
        }

        // ════════════════════════════════════════════════════════
        // ALL PRESETS SHOULD HAVE UNIQUE COLORS
        // ════════════════════════════════════════════════════════

        [Theory]
        [InlineData("Eclipse")]
        [InlineData("ReIrradiation")]
        [InlineData("Stereotactic")]
        [InlineData("Palliative")]
        public void AllPresets_ShouldHaveDistinctColors(string presetName)
        {
            IsodoseLevel[] levels = presetName switch
            {
                "Eclipse" => IsodoseLevel.GetEclipseDefaults(),
                "ReIrradiation" => IsodoseLevel.GetReIrradiationPreset(),
                "Stereotactic" => IsodoseLevel.GetStereotacticPreset(),
                "Palliative" => IsodoseLevel.GetPalliativePreset(),
                _ => IsodoseLevel.GetDefaults()
            };

            var colors = levels.Select(l => l.Color).ToList();
            colors.Distinct().Count().Should().Be(colors.Count,
                $"{presetName} preset should have all unique colors for visual distinction");
        }

        // ════════════════════════════════════════════════════════
        // COLOR PALETTE
        // ════════════════════════════════════════════════════════

        [Fact]
        public void ColorPalette_ShouldHave16Colors()
        {
            IsodoseLevel.ColorPalette.Should().HaveCount(16);
        }

        [Fact]
        public void ColorPalette_ShouldHaveAllUniqueColors()
        {
            IsodoseLevel.ColorPalette.Distinct().Count().Should().Be(16);
        }

        [Fact]
        public void ColorPalette_AllColors_ShouldBeFullyOpaque()
        {
            foreach (uint color in IsodoseLevel.ColorPalette)
            {
                byte alpha = (byte)(color >> 24 & 0xFF);
                alpha.Should().Be(0xFF, "palette colors should have full alpha");
            }
        }

        // ════════════════════════════════════════════════════════
        // PROPERTY CHANGE NOTIFICATION
        // ════════════════════════════════════════════════════════

        [Fact]
        public void IsodoseLevel_FractionChange_ShouldRaisePropertyChanged()
        {
            var level = new IsodoseLevel(0.95, "95%", 0xFFFF0000);
            bool notified = false;
            level.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(IsodoseLevel.Fraction)) notified = true; };

            level.Fraction = 0.90;
            notified.Should().BeTrue();
        }

        [Fact]
        public void IsodoseLevel_ColorChange_ShouldRaiseMediaColorChanged()
        {
            var level = new IsodoseLevel(0.95, "95%", 0xFFFF0000);
            bool mediaColorNotified = false;
            level.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(IsodoseLevel.MediaColor)) mediaColorNotified = true; };

            level.Color = 0xFF00FF00;
            mediaColorNotified.Should().BeTrue("MediaColor depends on Color");
        }

        // ════════════════════════════════════════════════════════
        // CONSTRUCTOR TESTS
        // ════════════════════════════════════════════════════════

        [Fact]
        public void Constructor_Relative_ShouldSetPropertiesCorrectly()
        {
            var level = new IsodoseLevel(0.95, "95%", 0xFFFF0000, 140);
            level.Fraction.Should().Be(0.95);
            level.Label.Should().Be("95%");
            level.Color.Should().Be(0xFFFF0000u);
            level.Alpha.Should().Be(140);
            level.AbsoluteDoseGy.Should().Be(0);
            level.IsVisible.Should().BeTrue();
        }

        [Fact]
        public void Constructor_Absolute_ShouldSetPropertiesCorrectly()
        {
            var level = new IsodoseLevel(0, 45, "45 Gy", 0xFFFF8800, 150);
            level.Fraction.Should().Be(0);
            level.AbsoluteDoseGy.Should().Be(45);
            level.Label.Should().Be("45 Gy");
            level.Alpha.Should().Be(150);
        }
    }

    /// <summary>
    /// Tests for rendering constants — ensures magic numbers are reasonable.
    /// </summary>
    public class RenderConstantsTests
    {
        [Fact]
        public void ZoomLimits_ShouldBeReasonable()
        {
            RenderConstants.MinZoom.Should().BeGreaterThan(0);
            RenderConstants.MaxZoom.Should().BeGreaterThan(RenderConstants.MinZoom);
            RenderConstants.ZoomStepFactor.Should().BeGreaterThan(1.0);
        }

        [Fact]
        public void DvhResolution_ShouldBePositive()
        {
            DomainConstants.DvhSamplingResolution.Should().BeGreaterThan(0);
            DomainConstants.DvhHistogramBins.Should().BeGreaterThan(100);
        }

        [Fact]
        public void HuOffset_ShouldBe32768()
        {
            DomainConstants.HuOffsetValue.Should().Be(32768,
                "DICOM unsigned-to-signed HU offset is 2^15");
        }

        [Fact]
        public void NormalizationThreshold_ShouldDistinguishPercentFromFraction()
        {
            // Values < 5 are assumed to be fractions (0-1), ≥5 are percentages
            DomainConstants.NormalizationFractionThreshold.Should().Be(5.0);
        }
    }
}