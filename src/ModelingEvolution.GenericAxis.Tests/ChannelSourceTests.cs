using System.Reflection;
using System.Reflection.Emit;
using FluentAssertions;

namespace ModelingEvolution.GenericAxis.Tests;

/// <summary>
/// Review #46: the channel never parks a thread on a Modbus frame. GA-U-134 guards the CALLER's thread; the plausible wrong
/// fix — keep FluentModbus' synchronous read/write and wrap it in Task.Run — parks a pool thread per frame instead (the
/// PR #6 starvation) and is invisible to any timing test except by load. This guard reads ModbusChannel's IL (its own
/// methods and every compiler-generated lambda/state machine nested in it): no synchronous FluentModbus I/O, no
/// Task.Wait*, no Task&lt;T&gt;.Result, no awaiter GetResult beyond the compiler's own awaits (review #48), and no
/// Task.Run / TaskFactory.StartNew (the channel has no reason to hop threads). Deterministic and independent of pool
/// size, CPU count and load.
/// </summary>
public class ChannelSourceTests
{
    /// <summary>Non-async FluentModbus members the channel may call: none of them waits on the network.</summary>
    private static readonly HashSet<string> AllowedSync =
        [".ctor", "Initialize", "Disconnect", "Dispose", "get_IsConnected",
            "get_ExceptionCode"]; // ModbusException's code (ADR-37 refusal), a field read

    [Fact(DisplayName = "GA-U-136 ModbusChannel never blocks on a frame or hops threads (sync FluentModbus I/O, Wait, Result, GetResult, Task.Run)")]
    public void ModbusChannel_IL_NoBlockingCalls()
    {
        var channel = typeof(ModbusChannel);
        var calls = Calls(channel).ToList();
        var called = calls.Select(c => c.Target).ToList();

        called.Should().Contain(m => m.DeclaringType!.Namespace == "FluentModbus" && m.Name == "ReadHoldingRegistersAsync",
            "anchor: the walker sees the channel's FluentModbus calls, lambdas included");
        called.Should().Contain(m => m.DeclaringType!.Namespace == "FluentModbus" && m.Name == "ReadInputRegistersAsync",
            "anchor: the FC04 status read (ADR-36) is walked too");

        var offending = called.Where(m =>
                (m.DeclaringType!.Namespace == "FluentModbus" && !m.Name.EndsWith("Async") && !AllowedSync.Contains(m.Name))
                || (m.DeclaringType == typeof(Task) && m.Name is "Wait" or "WaitAll" or "WaitAny")
                || (m.DeclaringType == typeof(Task) && m.Name == "Run")
                || (m.DeclaringType is { } f && (f == typeof(TaskFactory) || (f.IsGenericType
                    && f.GetGenericTypeDefinition() == typeof(TaskFactory<>))) && m.Name == "StartNew")
                || (m.DeclaringType is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(Task<>)
                    && m.Name == "get_Result"))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .Distinct()
            .ToList();

        // Review #48: an awaiter's GetResult is blocking unless the compiler emitted it for an await, which always checks
        // IsCompleted on that awaiter first. `.GetAwaiter().GetResult()` has no such check, so per method the GetResult
        // calls on awaiters may not outnumber the IsCompleted checks.
        foreach (var method in calls.GroupBy(c => c.Caller))
        {
            var results = method.Count(c => IsAwaiter(c.Target.DeclaringType) && c.Target.Name == "GetResult");
            var checks = method.Count(c => IsAwaiter(c.Target.DeclaringType) && c.Target.Name == "get_IsCompleted");
            if (results > checks)
                offending.Add($"{method.Key.DeclaringType!.Name}.{method.Key.Name}: {results} awaiter GetResult, {checks} IsCompleted");
        }

        offending.Should().BeEmpty("a frame must never park a thread (review #46, PR #6)");
    }

    private static bool IsAwaiter(Type? type) =>
        type is not null && type.Name.Contains("Awaiter") && type.Namespace == "System.Runtime.CompilerServices";

    /// <summary>Every (caller, callee) pair of call/callvirt/newobj/ldftn in the type's methods and its nested types' methods.</summary>
    private static IEnumerable<(MethodBase Caller, MethodBase Target)> Calls(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                 BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = new List<Type> { type };
        for (var i = 0; i < types.Count; i++) types.AddRange(types[i].GetNestedTypes(all));
        foreach (var t in types)
        foreach (var method in t.GetMethods(all).Cast<MethodBase>().Concat(t.GetConstructors(all)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            foreach (var token in CallTokens(il))
            {
                MethodBase? target;
                try
                {
                    target = method.Module.ResolveMethod(token,
                        t.IsGenericType ? t.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (target is not null) yield return (method, target);
            }
        }
    }

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>A plain IL walk: the metadata tokens of call, callvirt, newobj and ldftn/ldvirtftn operands.</summary>
    private static IEnumerable<int> CallTokens(byte[] il)
    {
        var i = 0;
        while (i < il.Length)
        {
            short value = il[i] == 0xFE ? (short)(0xFE00 | il[i + 1]) : il[i];
            i += il[i] == 0xFE ? 2 : 1;
            var op = OpCodesByValue[value];
            if (op == OpCodes.Call || op == OpCodes.Callvirt || op == OpCodes.Newobj || op == OpCodes.Ldftn
                || op == OpCodes.Ldvirtftn)
                yield return BitConverter.ToInt32(il, i);
            i += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                _ => 4,
            };
        }
    }
}
