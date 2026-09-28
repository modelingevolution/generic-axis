using RocketWelder.SDK.Devices.Motion;

namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The protocol's fixed map from <see cref="MotionError"/> to <see cref="ErrorClass"/>
/// (protocol.md § Errors and debugging; design.md § Error classes). It is the only place a class is
/// decided.
/// </summary>
public static class MotionErrorClasses
{
    /// <summary>The class of <paramref name="error"/>.</summary>
    /// <remarks>
    /// The switch names every member of SDK 2.30.0 and has no default arm. A member added by a later SDK
    /// is compiler warning CS8509, which this project treats as an error, so the map cannot fall behind
    /// the enum silently. A value outside the named members is a caller bug and throws
    /// <see cref="System.Runtime.CompilerServices.SwitchExpressionException"/>.
    /// </remarks>
    public static ErrorClass Of(MotionError error)
    {
#pragma warning disable CS8524 // Unnamed enum values are a caller bug; they throw SwitchExpressionException.
        return error switch
        {
            MotionError.CommunicationLost => ErrorClass.Transport,

            MotionError.ProtocolMismatch => ErrorClass.Protocol,
            MotionError.NotAcknowledged => ErrorClass.Protocol,

            MotionError.DriveFault => ErrorClass.Machine,
            MotionError.LimitTripped => ErrorClass.Machine,
            MotionError.MotionFailed => ErrorClass.Machine,
            MotionError.WatchdogTripped => ErrorClass.Machine,
            MotionError.HomeLatchFailed => ErrorClass.Machine,
            MotionError.SafetyStop => ErrorClass.Machine,

            MotionError.Busy => ErrorClass.Commander,
            MotionError.NotHomed => ErrorClass.Commander,
            MotionError.OutOfRange => ErrorClass.Commander,
            MotionError.UnreachableSpeed => ErrorClass.Commander,
            MotionError.UnsupportedSense => ErrorClass.Commander,
            MotionError.LeaseHeld => ErrorClass.Commander,
            // Not in the protocol's table: SDK binding refusals raised before anything is written.
            MotionError.UnknownAxis => ErrorClass.Commander,
            MotionError.WrongAxisKind => ErrorClass.Commander,
        };
#pragma warning restore CS8524
    }
}
