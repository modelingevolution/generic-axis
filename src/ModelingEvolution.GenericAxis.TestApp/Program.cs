using ModelingEvolution.GenericAxis.TestApp;
using ModelingEvolution.GenericAxis.TestApp.Components;
using ModelingEvolution.GenericAxis.TestApp.Conformance;
using MudBlazor.Services;

ThreadPoolFloor.Ensure();
if (args.Contains(CheckCommandLine.Flag)) return await CheckMode.RunAsync(args);
if (args.Contains(HeadlessMode.Flag)) return await HeadlessMode.RunAsync(args);
if (WebMode.Validate(args) is { } usageError)
{
    await Console.Error.WriteLineAsync($"error: {usageError}\n{WebMode.Usage}");
    return 2;
}

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration.GetUiUrls());

var simulator = builder.Configuration.GetSimulatedAxisOptions();
builder.Services.AddGenericAxisSimulator(simulator);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddScoped<ModelingEvolution.GenericAxis.TestApp.Driver.DriverSession>();

var app = builder.Build();

var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GenericAxis.TestApp");
log.LogInformation("UI on {Urls}", builder.Configuration.GetUiUrls());
log.LogSimulatorOptions(simulator);

if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseAntiforgery();
// UseStaticFiles goes through the composed provider, so MudBlazor's package assets resolve in Development,
// in a published build and in a container alike.
app.UseStaticFiles();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;
