using ModelingEvolution.GenericAxis.TestApp.Conformance;
using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis.TestApp.Tests.Conformance;

/// <summary>GA-U-94 (review #33): the class and the MotionError stated once, the endpoint and unit once, no "..".</summary>
public sealed class CheckerTextTests
{
    private const string DriverMessage =
        "127.0.0.1:5049: CommunicationLost: read C+0…C+11 (read 0…11) on 127.0.0.1:5049 unit 1 failed twice (reconnected once): Connection timed out..";

    [Fact]
    public void GA_U_94_ADriverMessage_IsRenderedWithEachFactOnce()
    {
        var text = CheckerText.Describe(new MotionException(MotionError.CommunicationLost, DriverMessage));

        text.Should().Be("Transport/CommunicationLost: read C+0…C+11 (read 0…11) on 127.0.0.1:5049 unit 1 failed twice (reconnected once): Connection timed out.");
    }

    [Theory]
    [InlineData(MotionError.LeaseHeld, ErrorClass.Commander, "LeaseHeld")]
    [InlineData(MotionError.NotAcknowledged, ErrorClass.Protocol, "NotAcknowledged")]
    [InlineData(MotionError.DriveFault, ErrorClass.Machine, "DriveFault")]
    [InlineData(MotionError.CommunicationLost, ErrorClass.Transport, "CommunicationLost")]
    public void GA_U_94_AMotionException_KeepsItsOwnNameAndClass(MotionError error, ErrorClass cls, string name)
    {
        var failure = Failure.FromMotion(new MotionException(error, $"carriage: {error}: held by owner 1."));

        failure.Class.Should().Be(cls);
        failure.Name.Should().Be(name, "a non-CommunicationLost MotionException is not relabelled ProtocolMismatch");
        failure.Text.Should().Be("held by owner 1.");
    }
}
