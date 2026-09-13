using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="StringOutput"/> — the sACN validation applied to
/// --string-size / --string-universe before the string buffer is allocated.
/// </summary>
public class StringOutputValidationTests
{
    [Fact]
    public void Validate_DefaultSettings_AreValid()
    {
        // 50 LEDs (150 channels → 1 universe) at universe 5, the default auto base.
        Assert.Null(StringOutput.Validate(50, 5));
    }

    [Fact]
    public void Validate_DefaultAutoUniverse_AfterMatrixUniverses()
    {
        int matrixUniverses = (PixelMapper.TotalChannels + 509) / 510; // 4 for the 53×11 matrix
        int baseUniverse = 1 + matrixUniverses;                        // matrix universe 1 + its universes
        Assert.Equal(5, baseUniverse);
        Assert.Null(StringOutput.Validate(50, baseUniverse));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_InvalidSize_Fails(int size)
        => Assert.Contains("--string-size", StringOutput.Validate(size, 5));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_UniverseBelowRange_Fails(int baseUniverse)
        => Assert.Contains("--string-universe", StringOutput.Validate(50, baseUniverse));

    [Theory]
    [InlineData(50, 64000)]        // wraps to an invalid universe when written as ushort
    [InlineData(50, int.MaxValue)] // absurd base
    [InlineData(715827883, 5)]     // size whose ×3 overflowed int before validation existed
    [InlineData(int.MaxValue, 5)]   // largest possible size
    public void Validate_BeyondUniverseCapacity_Fails(int size, int baseUniverse)
    {
        string? error = StringOutput.Validate(size, baseUniverse);
        Assert.NotNull(error);
        if (baseUniverse > StringOutput.MaxUniverse)
            Assert.Contains("must be 1..63999", error);
        else
            Assert.Contains("universes are limited", error);
    }

    [Fact]
    public void Validate_StringRunningPastLastUniverse_Fails()
    {
        // 171 LEDs = 513 channels → 2 universes; base 63999 would end at universe 64000.
        Assert.NotNull(StringOutput.Validate(171, 63999));

        // 170 LEDs = 510 channels → exactly 1 universe; base 63999 fits.
        Assert.Null(StringOutput.Validate(170, 63999));

        // 2 universes ending exactly at 63999 is valid.
        Assert.Null(StringOutput.Validate(171, 63998));
    }

    [Fact]
    public void Validate_ErrorMessage_IncludesSizeAndUniverseLimit()
    {
        string? error = StringOutput.Validate(715827883, 5);
        Assert.NotNull(error);
        Assert.Contains("715827883", error);
        Assert.Contains(StringOutput.MaxUniverse.ToString(), error);
    }

    [Theory]
    [InlineData(1, 1L)]       // 3 channels → 1 universe
    [InlineData(50, 1L)]      // 150 channels → 1 universe
    [InlineData(170, 1L)]     // 510 channels → exactly 1 universe
    [InlineData(171, 2L)]     // 513 channels → 2 universes
    [InlineData(340, 2L)]     // 1020 channels → 2 universes
    [InlineData(341, 3L)]     // 1023 channels → 3 universes
    [InlineData(512, 4L)]     // 1536 channels → 4 universes
    public void UniversesRequired_CeilingsChannelsBy510(int ledCount, long expected)
        => Assert.Equal(expected, StringOutput.UniversesRequired(ledCount));

    [Fact]
    public void UniversesRequired_DoesNotOverflowForHugeCounts()
    {
        // 715827883 × 3 overflows int; the long arithmetic must still yield the exact ceiling.
        Assert.Equal(4210753L, StringOutput.UniversesRequired(715827883));
        Assert.Equal(12632257L, StringOutput.UniversesRequired(int.MaxValue));
    }
}
