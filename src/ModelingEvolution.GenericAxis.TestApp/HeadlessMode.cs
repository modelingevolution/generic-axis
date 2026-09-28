using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ModelingEvolution.GenericAxis.TestApp;

/// <summary>
/// <c>--headless [--port N] [--kind linear|rotary]</c>: the simulator alone, no web host — for CI and for the Python
/// checker. Environment overrides (<c>Simulator__Faults__SuppressAck=true</c>, …) apply as in the full app; the
/// command-line flags win over them.
/// </summary>
public static class HeadlessMode
{
    public const string Flag = "--headless";

    public static async Task<int> RunAsync(string[] args)
    {
        var overrides = new Dictionary<string, string?>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case Flag:
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port) && port is >= 0 and <= 65535:
                    overrides["Simulator:Port"] = port.ToString();
                    i++;
                    break;
                case "--kind" when i + 1 < args.Length && args[i + 1] is "linear" or "rotary":
                    overrides["Simulator:Kind"] = args[i + 1] == "rotary" ? "Rotary" : "Linear";
                    i++;
                    break;
                default:
                    await Console.Error.WriteLineAsync(
                        $"Unrecognised or invalid argument '{args[i]}'. Usage: --headless [--port N] [--kind linear|rotary]");
                    return 2;
            }
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Configuration.AddInMemoryCollection(overrides);
        var options = builder.Configuration.GetSimulatedAxisOptions();
        builder.Services.AddGenericAxisSimulator(options);

        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GenericAxis.TestApp");
        logger.LogInformation("Headless simulator (no UI)");
        logger.LogSimulatorOptions(options);
        await host.RunAsync();
        return 0;
    }
}
