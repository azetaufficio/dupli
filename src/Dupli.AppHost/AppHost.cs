// Local test stack (no Azure): PostgreSQL, Mailpit (SMTP), RustFS (S3), the management server, the Angular
// dev server and a Linux container running an agent that enrolls itself and backs up sample data.
// Everything is ephemeral: each run starts from an empty database and bucket.

var builder = DistributedApplication.CreateBuilder(args);

const string adminKey = "aspire-dev-admin-key";
const string s3AccessKey = "dupli-dev";
const string s3SecretKey = "dupli-dev-secret";

// Operators sign in with a dev Entra ID app registration, as in production (Parameters:* in the AppHost user secrets).
var entraTenantId = builder.AddParameter("entra-tenant-id");
var entraClientId = builder.AddParameter("entra-client-id");
var entraClientSecret = builder.AddParameter("entra-client-secret", secret: true);
var bootstrapOwnerEmail = builder.AddParameter("bootstrap-owner-email");

var postgres = builder.AddPostgres("postgres")
    .WithImageTag("18"); // the agent image ships pg_dump 18
var dupliDb = postgres.AddDatabase("Dupli", databaseName: "dupli");
var sampleDb = postgres.AddDatabase("sampledb");

var mailpit = builder.AddContainer("mailpit", "axllent/mailpit")
    .WithHttpEndpoint(targetPort: 8025, name: "http")
    .WithEndpoint(targetPort: 1025, name: "smtp", scheme: "tcp");

var rustfs = builder.AddContainer("rustfs", "rustfs/rustfs", "1.0.0")
    .WithEnvironment("RUSTFS_ACCESS_KEY", s3AccessKey)
    .WithEnvironment("RUSTFS_SECRET_KEY", s3SecretKey)
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithHttpHealthCheck("/health", endpointName: "s3");

var smtp = mailpit.GetEndpoint("smtp");
var server = builder.AddProject<Projects.Dupli_Server>("server", launchProfileName: "http")
    .WithReference(dupliDb)
    .WaitFor(dupliDb)
    .WithEnvironment("Dupli__Auth__Mode", "EntraId")
    .WithEnvironment("Dupli__Auth__EntraId__TenantId", entraTenantId)
    .WithEnvironment("Dupli__Auth__EntraId__ClientId", entraClientId)
    .WithEnvironment("Dupli__Auth__EntraId__ClientSecret", entraClientSecret)
    .WithEnvironment("Dupli__Auth__BootstrapOwnerEmail", bootstrapOwnerEmail)
    .WithEnvironment("Dupli__Admin__ApiKey", adminKey)
    // Lets a locally built agent be registered as a release with a file:// source, to try updates and rollbacks.
    .WithEnvironment("Dupli__Releases__AllowInsecureSources", "true")
    // The agent registers RustFS by its container-network name; the server (on the host) browses snapshots through
    // the published port.
    .WithEnvironment("Dupli__Restore__EndpointOverrides__0__From", "http://rustfs.dev.internal:9000")
    .WithEnvironment("Dupli__Restore__EndpointOverrides__0__To", rustfs.GetEndpoint("s3"))
    .WithEnvironment("Notifications__Channel", "Smtp")
    .WithEnvironment("Notifications__Smtp__Host", smtp.Property(EndpointProperty.Host))
    .WithEnvironment("Notifications__Smtp__Port", smtp.Property(EndpointProperty.Port))
    .WithEnvironment("Notifications__Smtp__Security", "None")
    .WithEnvironment("Notifications__Smtp__From", "dupli@dupli.local")
    // Who receives mail is per operator (notification preferences), not a fixed recipient: the bootstrap
    // owner (Parameters:bootstrap-owner-email) gets e-mail by default and Mailpit catches all outgoing SMTP
    // regardless of address, so no separate seed recipient is needed here.
    .WithHttpHealthCheck("/health")
    .WaitFor(mailpit);

// Angular dev server on :4200, proxying /api and /bff to the server on :5000 (proxy.conf.json).
builder.AddJavaScriptApp("web", "../Dupli.Web", "start")
    .WithHttpEndpoint(port: 4200, isProxied: false)
    .WithReference(server)
    .WaitFor(server);

var pg = postgres.GetEndpoint("tcp");
builder.AddDockerfile("agent", "../..", "deploy/agent/Dockerfile")
    .WithEnvironment("DUPLI_SERVER_URL", server.GetEndpoint("http"))
    .WithEnvironment("DUPLI_ADMIN_KEY", adminKey)
    .WithEnvironment("DUPLI_ALLOW_INSECURE_HTTP", "true")
    .WithEnvironment("S3_ENDPOINT", rustfs.GetEndpoint("s3"))
    .WithEnvironment("S3_ACCESS_KEY", s3AccessKey)
    .WithEnvironment("S3_SECRET_KEY", s3SecretKey)
    .WithEnvironment("PG_HOST", pg.Property(EndpointProperty.Host))
    .WithEnvironment("PG_PORT", pg.Property(EndpointProperty.TargetPort))
    .WithEnvironment("PG_USER", "postgres")
    .WithEnvironment("PG_PASSWORD", postgres.Resource.PasswordParameter)
    .WithEnvironment("PG_SAMPLE_DB", "sampledb")
    .WaitFor(server)
    .WaitFor(rustfs)
    .WaitFor(sampleDb);

builder.Build().Run();
