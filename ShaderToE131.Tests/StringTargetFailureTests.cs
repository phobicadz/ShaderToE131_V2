using Xunit;

namespace ShaderToE131.Tests;

/// <summary>
/// The LED string target is normally an mDNS name, and an mDNS name goes away with the
/// device. Constructing the sender for it has to be able to fail as a result — a reason
/// and a null sender — because the alternative is an unhandled exception out of Main:
/// which takes the LED matrix (a different device, reached by a literal address) dark
/// along with the string, and leaves launchd restarting a process that dies again ten
/// seconds later. That is not hypothetical; it happened two thousand times.
/// </summary>
public class StringTargetFailureTests
{
    [Fact]
    public void UnresolvableStringTarget_FailsAsAResult_NotAnException()
    {
        // ".invalid" is reserved and guaranteed never to resolve, so this says nothing
        // about the LAN or about whether a controller happens to be awake.
        bool ok = StringOutput.TryCreateSender(
            "ledstring.invalid", 5568, out var sender, out string? failure);

        Assert.False(ok);
        Assert.Null(sender);
        Assert.False(string.IsNullOrWhiteSpace(failure),
            "the caller is left with a disabled output and has to be told why, because "
            + "the log line is the only place a string that is not sending gets explained");
    }

    [Fact]
    public void LiteralAddress_StillConstructsTheSender()
    {
        // The guard must not make construction unreliable. A target written as an
        // address — which is exactly how the matrix target is configured — keeps working,
        // and loopback keeps the test off the LAN.
        bool ok = StringOutput.TryCreateSender(
            "127.0.0.1", 5568, out var sender, out string? failure);

        Assert.True(ok, failure ?? "no reason given");
        Assert.NotNull(sender);
        sender?.Dispose();
    }

    [Fact]
    public void AFailedTargetDoesNotLeaveAHalfBuiltSenderBehind()
    {
        // The two out-parameters are one answer: a sender with no destination, or a
        // failure with a sender still in it, are both ways of lying about the state.
        bool ok = StringOutput.TryCreateSender(
            "ledstring.invalid", 5568, out var sender, out _);

        Assert.False(ok);
        Assert.Null(sender);
    }
}
