namespace ModelingEvolution.GenericAxis;

/// <summary>
/// The four error classes of protocol.md § Errors and debugging. The driver, both conformance checkers,
/// the reports and the logs use the same four names, and nothing translates an error from one class into
/// another. <see cref="MotionErrorClasses.Of"/> is the only place a class is decided.
/// </summary>
public enum ErrorClass
{
    /// <summary>The link failed: connect failed or was refused, the socket closed, a request got no answer
    /// within 500 ms, or the PLC returned a Modbus exception. Acted on by network, IP, port, unit id.</summary>
    Transport,

    /// <summary>The PLC answered, but not per the protocol. Acted on by the PLC programmer.</summary>
    Protocol,

    /// <summary>The PLC reports a fault, or the machine did not do what the PLC accepted. Acted on by
    /// maintenance or the operator.</summary>
    Machine,

    /// <summary>The driver refused before writing anything. Acted on by the caller, or the other
    /// commander.</summary>
    Commander,
}
