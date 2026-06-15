using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using WorkPlanStudio;
using WorkPlanStudio.Data;
using WorkPlanStudio.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// English / German UI translations via IStringLocalizer. No ResourcesPath is
// set on purpose: the SharedResource marker class lives in the same namespace
// as the .resx files (WorkPlanStudio.Resources), so the resource base name
// matches the embedded resource name exactly.
builder.Services.AddLocalization();

// EF Core + SQLite, running entirely in the browser.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite("Data Source=/data/workplan.db"));
builder.Services.AddSingleton<BrowserDatabase>();
builder.Services.AddScoped<WorkPlanService>();
builder.Services.AddScoped<WorkCenterService>();
builder.Services.AddScoped<WorkPlanSchedulingService>();

var host = builder.Build();

// Apply the language the user picked last time (stored in the browser).
var js = host.Services.GetRequiredService<IJSRuntime>();
var stored = await js.InvokeAsync<string?>("blazorCulture.get");
var culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(stored) ? "en-US" : stored);
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;

await host.RunAsync();
