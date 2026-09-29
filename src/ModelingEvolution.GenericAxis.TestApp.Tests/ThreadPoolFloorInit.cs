using System.Runtime.CompilerServices;

namespace ModelingEvolution.GenericAxis.TestApp.Tests;

/// <summary>The in-process simulator and checker need the same thread-pool floor as the app (see <see cref="ThreadPoolFloor"/>).</summary>
internal static class ThreadPoolFloorInit
{
    [ModuleInitializer]
    internal static void Init() => ThreadPoolFloor.Ensure();
}
