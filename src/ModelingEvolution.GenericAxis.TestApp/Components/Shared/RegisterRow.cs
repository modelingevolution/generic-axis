namespace ModelingEvolution.GenericAxis.TestApp.Components.Shared;

/// <summary>One row of a register table: address (with base), name, raw hex, decoded value.</summary>
public sealed record RegisterRow(string Address, string Name, string Raw, string Decoded);
