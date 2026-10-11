using ShaderToE131;
using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// Unit tests for <see cref="MqttClient.TryParseLightCommand"/> — the parser for Home Assistant
/// MQTT light commands (bare ON/OFF payloads and the JSON {"state":…,"effect":…} form).
/// </summary>
public class MqttLightCommandTests
{
    [Theory]
    [InlineData("ON", "ON", null)]
    [InlineData("OFF", "OFF", null)]
    [InlineData("off", "OFF", null)]
    [InlineData("TOGGLE", "TOGGLE", null)]
    public void ParsesBareStatePayloads(string payload, string expectedState, string? expectedEffect)
    {
        Assert.True(MqttClient.TryParseLightCommand(payload, out string? state, out string? effect, out _));
        Assert.Equal(expectedState, state);
        Assert.Equal(expectedEffect, effect);
    }

    [Fact]
    public void ParsesJsonEffectOnly()
    {
        Assert.True(MqttClient.TryParseLightCommand("{\"effect\":\"neon_pulse_grid\"}", out string? state, out string? effect, out _));
        Assert.Null(state);
        Assert.Equal("neon_pulse_grid", effect);
    }

    [Fact]
    public void ParsesJsonStateAndEffect()
    {
        Assert.True(MqttClient.TryParseLightCommand("{\"state\":\"ON\",\"effect\":\"audio_spectrum_pro\"}", out string? state, out string? effect, out _));
        Assert.Equal("ON", state);
        Assert.Equal("audio_spectrum_pro", effect);
    }

    [Theory]
    [InlineData("")]                                   // empty payload
    [InlineData("HELLO")]                             // not a state keyword
    [InlineData("{}")]                                // neither state nor effect
    [InlineData("{\"state\":5}")]                    // wrong type
    [InlineData("{\"effect\":\"x\",\"state\":\"BOGUS\"}")] // unknown state
    public void RejectsInvalidPayloads(string payload)
    {
        Assert.False(MqttClient.TryParseLightCommand(payload, out _, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void TryParseDurationSeconds_AcceptsPlainNumberFromNumberEntity()
    {
        Assert.True(MqttClient.TryParseDurationSeconds("45", out int seconds, out string? error));
        Assert.Equal(45, seconds);
        Assert.Null(error);
    }

    [Fact]
    public void TryParseDurationSeconds_ClampsToAdvertisedRange()
    {
        Assert.True(MqttClient.TryParseDurationSeconds("9999", out int high, out _));
        Assert.Equal(MqttClient.MaxNotifyDurationSec, high);

        Assert.True(MqttClient.TryParseDurationSeconds("0", out int low, out _));
        Assert.Equal(MqttClient.MinNotifyDurationSec, low);
    }

    [Fact]
    public void TryParseDurationSeconds_AcceptsJsonObject()
    {
        Assert.True(MqttClient.TryParseDurationSeconds("{\"duration\":20}", out int seconds, out _));
        Assert.Equal(20, seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("{}")]
    [InlineData("{\"text\":\"hi\"}")]
    [InlineData("{not json")]
    public void TryParseDurationSeconds_RejectsGarbage(string payload)
    {
        Assert.False(MqttClient.TryParseDurationSeconds(payload, out _, out string? error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
