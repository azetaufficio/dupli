using Dupli.Contracts;
using Dupli.Server.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Background;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Notifications;
using Dupli.Server.Infrastructure.Security;
using Dupli.Server.Jobs;
using Dupli.Server.Tools;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSerilog((_, lc) => lc
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

var dupli = builder.Configuration.GetSection(DupliServerOptions.Section);
var serverOptions = dupli.Get<DupliServerOptions>() ?? new DupliServerOptions();
builder.Services.Configure<DupliServerOptions>(dupli);
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));

var connectionString = builder.Configuration.GetConnectionString("Dupli")
    ?? throw new InvalidOperationException("ConnectionStrings:Dupli is required");
builder.Services.AddDbContext<DupliDbContext>(o => o
    .UseNpgsql(connectionString)
    .UseSnakeCaseNamingConvention());

builder.Services.AddDataProtection()
    .SetApplicationName("Dupli")
    .PersistKeysToFileSystem(new DirectoryInfo(serverOptions.DataProtectionKeysPath));

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
builder.Services.AddSingleton<INotificationChannel, SmtpNotificationChannel>();
builder.Services.AddScoped<EnrollmentService>();
builder.Services.AddScoped<JobService>();
builder.Services.AddScoped<ResticMirror>();

var runWorkers = serverOptions.RunBackgroundServices;
builder.Services.AddPeriodicTask<JobScheduler>(sp => Opt(sp).Jobs.SchedulerInterval, runWorkers);
builder.Services.AddPeriodicTask<JobSweeper>(sp => Opt(sp).Jobs.SchedulerInterval, runWorkers);
builder.Services.AddPeriodicTask<AlertEvaluator>(sp => Opt(sp).Alerts.EvaluationInterval, runWorkers);

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
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthConstants.AgentPolicy, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser())
    .AddPolicy(AuthConstants.AdminPolicy, p => p
        .AddAuthenticationSchemes(AuthConstants.AdminScheme)
        .RequireAuthenticatedUser());

var app = builder.Build();

DatabaseMigrator.Migrate(connectionString, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dupli.Migrations"));

app.UseForwardedHeaders();
app.UseSerilogRequestLogging();
app.UseApiExceptions();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAgentApi();
app.MapAdminApi();

app.Run();

static DupliServerOptions Opt(IServiceProvider sp) => sp.GetRequiredService<IOptions<DupliServerOptions>>().Value;

public partial class Program;
