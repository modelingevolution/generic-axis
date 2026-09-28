using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelingEvolution.GenericAxis.TestApp.Simulation;

namespace ModelingEvolution.GenericAxis.TestApp;

/// <summary>Configuration access for the test app (docs/standards/dotnet.md: extension methods, keys in one place).</summary>
public static class SimulatorConfiguration
{
    private const string UrlsKey = "urls";
    private const string DefaultUrls = "http://0.0.0.0:5070";

    /// <summary>
    /// Section <c>Simulator</c> bound onto <see cref="SimulatedAxisOptions"/>; the record's initialisers are the
    /// defaults. Every <see cref="SimFaults"/> member binds from <c>Simulator:Faults:&lt;Name&gt;</c>. Validated.
    /// </summary>
    public static SimulatedAxisOptions GetSimulatedAxisOptions(this IConfiguration configuration) =>
        (configuration.GetSection(SimulatedAxisOptions.SectionName).Get<SimulatedAxisOptions>() ?? new SimulatedAxisOptions())
        .Validate();

    /// <summary>The UI address: <c>urls</c> / <c>ASPNETCORE_URLS</c> when set, else port 5070 on every interface.</summary>
    public static string GetUiUrls(this IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration[UrlsKey]) ? DefaultUrls : configuration[UrlsKey]!;

    /// <summary>Registers one <see cref="SimulatorHost"/> and the loop that scans it.</summary>
    public static IServiceCollection AddGenericAxisSimulator(this IServiceCollection services, SimulatedAxisOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(sp => new SimulatorHost(options, sp.GetRequiredService<ILoggerFactory>()));
        services.AddHostedService<SimulatorService>();
        return services;
    }

    /// <summary>Logs every simulator value at startup.</summary>
    public static void LogSimulatorOptions(this ILogger logger, SimulatedAxisOptions options) =>
        logger.LogInformation("Simulator configuration: {Options}", options);
}
