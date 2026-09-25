using Dupli.Contracts;
using Dupli.Server.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Background;
using Dupli.Server.Configuration;
using Dupli.Server.Hosting;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Notifications;
using Dupli.Server.Infrastructure.Security;
using Dupli.Server.Jobs;
using Dupli.Server.Notifications;
using Dupli.Server.Restore;
using Dupli.Server.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

if (!builder.Environment.IsEnvironment("Testing"))
{
    // A fatal error (e.g. invalid configuration at startup) must end the process with a non-zero code so the
    // container restart policy applies; in containers the default crash path can hang instead of exiting.
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Console.Error.WriteLine($"Fatal: {e.ExceptionObject}");
        Environment.Exit(1);
    };
}

builder.Services.AddSerilog((_, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var dupli = builder.Configuration.GetSection(DupliServerOptions.Section);
var serverOptions = dupli.Get<DupliServerOptions>() ?? new DupliServerOptions();
builder.Services.Configure<DupliServerOptions>(dupli);
builder.Services.AddDupliNotifications(builder.Configuration);

var connectionString = builder.Configuration.GetConnectionString("Dupli")
    ?? throw new InvalidOperationException("ConnectionStrings:Dupli is required");
builder.Services.AddDbContext<DupliDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddDupliDataProtection(builder.Configuration);
builder.Services.AddDupliRateLimiting(serverOptions.RateLimiting);
builder.Services.AddDupliHealthChecks();

builder.Services.ConfigureHttpJsonOptions(o => DupliJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddHttpClient();
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Caddy runs in the same compose network; trust its X-Forwarded-* headers.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentSigningKey>();
builder.Services.AddSingleton<AgentTokenIssuer>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<JobCredentialsService>();
builder.Services.AddScoped<NotificationDispatcher>();
builder.Services.AddScoped<ReleaseMirror>();
builder.Services.AddScoped<DesiredVersionResolver>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<RepositoryBrowser>();
builder.Services.AddHttpClient(AdminApi.GitHubClientName);

var runWorkers = serverOptions.RunBackgroundServices;
builder.Services.AddPeriodicTask<JobScheduler>(sp => Opt(sp).Jobs.SchedulerInterval, runWorkers);
builder.Services.AddPeriodicTask<JobSweeper>(sp => Opt(sp).Jobs.SchedulerInterval, runWorkers);
builder.Services.AddPeriodicTask<AlertEvaluator>(sp => Opt(sp).Alerts.EvaluationInterval, runWorkers);

builder.AddDupliAuthentication(serverOptions);
builder.Services.AddAuthentication()
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, _ => { })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AdminApiKeyHandler>(AuthConstants.AdminScheme, null);
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<AgentSigningKey>((o, key) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthConstants.Issuer,
            ValidAudience = AuthConstants.AgentAudience,
            IssuerSigningKey = key.Key,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    });

var app = builder.Build();

var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
DatabaseMigrator.Migrate(connectionString, loggerFactory.CreateLogger("Dupli.Migrations"));

var authLogger = loggerFactory.CreateLogger("Dupli.Auth");
await OperatorDirectory.EnsureBootstrapConfiguredAsync(app.Services, serverOptions, authLogger);

// Load the key ring now: an unreachable Key Vault or unwritable key directory must fail the startup, not every
// request that later unprotects a secret or a session cookie.
app.Services.GetRequiredService<IKeyManager>().GetAllKeys();

app.UseForwardedHeaders();
app.UseSecurityHeaders(serverOptions);
app.UseSerilogRequestLogging();
app.UseApiExceptions();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseOperatorLogContext();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(HealthChecks.ReadyTag),
}).AllowAnonymous();
app.MapAgentApi();
app.MapAdminApi();
app.MapBff();
app.MapWebUi();

app.Run();

static DupliServerOptions Opt(IServiceProvider sp) => sp.GetRequiredService<IOptions<DupliServerOptions>>().Value;

public partial class Program;
