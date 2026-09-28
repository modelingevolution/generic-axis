using System.Runtime.CompilerServices;
using FluentAssertions;
using RocketWelder.SDK.Devices.Motion;
using Xunit;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>protocol.md § Errors and debugging: every <see cref="MotionError"/> has exactly one class.</summary>
public sealed class ErrorClassTests
{
    public static TheoryData<MotionError, ErrorClass> Table => new()
    {
        { MotionError.CommunicationLost, ErrorClass.Transport },
        { MotionError.ProtocolMismatch, ErrorClass.Protocol },
        { MotionError.NotAcknowledged, ErrorClass.Protocol },
        { MotionError.DriveFault, ErrorClass.Machine },
        { MotionError.LimitTripped, ErrorClass.Machine },
        { MotionError.MotionFailed, ErrorClass.Machine },
        { MotionError.WatchdogTripped, ErrorClass.Machine },
        { MotionError.HomeLatchFailed, ErrorClass.Machine },
        { MotionError.SafetyStop, ErrorClass.Machine },
        { MotionError.Busy, ErrorClass.Commander },
        { MotionError.NotHomed, ErrorClass.Commander },
        { MotionError.OutOfRange, ErrorClass.Commander },
        { MotionError.UnreachableSpeed, ErrorClass.Commander },
        { MotionError.UnsupportedSense, ErrorClass.Commander },
        { MotionError.LeaseHeld, ErrorClass.Commander },
        { MotionError.UnknownAxis, ErrorClass.Commander },
        { MotionError.WrongAxisKind, ErrorClass.Commander },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Every_MotionError_Has_The_Protocol_Class(MotionError error, ErrorClass expected) =>
        MotionErrorClasses.Of(error).Should().Be(expected);

    [Fact]
    public void The_Table_Covers_Every_Member_Of_The_Sdk_Enum()
    {
        var covered = Table.Select(row => (MotionError)row[0]);

        covered.Should().BeEquivalentTo(Enum.GetValues<MotionError>(),
            "a MotionError the table does not name has no asserted class");
    }

    [Fact]
    public void An_Unnamed_Value_Is_A_Caller_Bug_And_Throws()
    {
        var classify = () => MotionErrorClasses.Of((MotionError)int.MaxValue);

        classify.Should().Throw<SwitchExpressionException>();
    }
}
