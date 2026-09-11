using System.Globalization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using WorkPlanStudio;
using WorkPlanStudio.Data;
using WorkPlanStudio.Services;
using WorkPlanStudio.Services.Auth;
using WorkPlanStudio.Services.Remote;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// English / German UI translations via IStringLocalizer. No ResourcesPath is
// set on purpose: the SharedResource marker class lives in the same namespace
// as the .resx files (WorkPlanStudio.Resources), so the resource base name
// matches the embedded resource name exactly.
builder.Services.AddLocalization();

// Authorization: the standard pipeline (policies, AuthorizeView, IAuthorizationService)
// fed by a persona chosen in the UI and remembered per browser. The service layer
// asks the same policies before every write - see docs/adr/0013.
builder.Services.AddAuthorizationCore(options => options.AddWorkspacePolicies());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IPersonaStore, JsPersonaStore>();
builder.Services.AddScoped<DemoAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<DemoAuthenticationStateProvider>());
builder.Services.AddScoped<IPermissionGuard, PermissionGuard>();

// EF logs every command it executes at Information, and the browser console is
// the only sink here - a published build was printing all of its DDL and every
// query on load. Keep that for local debugging, drop it in a release build.
#if !DEBUG
builder.Logging.SetMinimumLevel(LogLevel.Warning);
#endif

// EF Core + SQLite, running entirely in the browser.
// Schema 6: cost centres as master data, the work centres a released order still
// depends on as real rows, and decimals stored as text so they survive the trip.
// A schema 5 payload is upgraded on load instead of being refused - the version
// lives beside the upgrade steps so adding one and forgetting the bump is a
// single edit rather than two (see Data/SchemaUpgrades.cs).
var databaseOptions = new BrowserDatabaseOptions("/data/workplan.db", SchemaUpgrades.CurrentVersion);
builder.Services.AddSingleton(databaseOptions);
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite($"Data Source={databaseOptions.DatabasePath}"));
builder.Services.AddSingleton<IBrowserDatabaseStorage, JsBrowserDatabaseStorage>();
// Scoped, not a singleton: reset and import destroy or replace every row in the
// browser, so BrowserDatabase asks IPermissionGuard the way the services do - and
// the guard is scoped because it reads the current persona. WebAssembly has one
// scope, so this is still one instance; registering it as a singleton fails the
// container's own scope validation, which is the right answer.
builder.Services.AddScoped<BrowserDatabase>();
builder.Services.AddScoped<WorkPlanService>();
builder.Services.AddScoped<WorkCenterService>();
builder.Services.AddScoped<ProductionOrderService>();
builder.Services.AddScoped<PlantSettingsService>();
builder.Services.AddScoped<IProductionScheduleService, ProductionScheduleService>();

// Schedule assistant: an always-on rule-based narrator plus an optional,
// bring-your-own-key AI one behind the same façade (see docs/AI-ASSISTANT.md).
builder.Services.AddScoped<RuleBasedNarrator>();
builder.Services.AddScoped<IAssistantConfig, AssistantSettingsService>();
builder.Services.AddScoped<ScheduleAssistant>();
builder.Services.AddScoped<WorkPlanStudio.Services.Chat.OfflineScheduleAnswerer>();
builder.Services.AddScoped<WorkPlanStudio.Services.Chat.ScheduleChat>();

// Cost centres: master data in their own right since schema 6 (docs/adr/0016).
builder.Services.AddScoped<CostCenterService>();

// Optional backend. With no wwwroot/appsettings.json (the default, and what
// GitHub Pages gets) this registers one value object and nothing else changes:
// personas, the in-browser database and the local scheduler as before. With
// Api:BaseAddress configured it replaces the persona provider with a real
// sign-in and runs the schedule on the server - see docs/adr/0020.
builder.Services.AddOptionalApi(builder.Configuration);
// How the scheduling search is executed. WebAssembly has one thread and the app
// links SQLite into the module, which rules out WasmEnableThreads outright, so the
// shipped strategy slices the run on that one thread and hands it back to the
// browser between multi-start descents. Swapping this line is the whole change if
// a worker ever becomes available - see docs/adr/0019.
builder.Services.AddScoped<WorkPlanStudio.Services.Scheduling.IScheduleYield,
    WorkPlanStudio.Services.Scheduling.BrowserScheduleYield>();
builder.Services.AddScoped<WorkPlanStudio.Services.Scheduling.IScheduleRunner,
    WorkPlanStudio.Services.Scheduling.CooperativeScheduleRunner>();

// Export: the schedule as a PDF report, an Excel workbook or a CSV file. The
// writers are hand-written and dependency-free, so this costs the download
// nothing - see docs/adr/0017.
builder.Services.AddScoped<ScheduleExportBuilder>();
builder.Services.AddScoped<FileDownloadService>();
builder.Services.AddScoped<ScheduleExportService>();

var host = builder.Build();

// Apply the language the user picked last time (stored in the browser).
var js = host.Services.GetRequiredService<IJSRuntime>();
var stored = await js.InvokeAsync<string?>("blazorCulture.get");
var culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(stored) ? "en-US" : stored);
CultureInfo.DefaultThreadCurrentCulture = culture;
CultureInfo.DefaultThreadCurrentUICulture = culture;
await js.InvokeVoidAsync("documentLanguage.set", culture.TwoLetterISOLanguageName);

await host.RunAsync();
