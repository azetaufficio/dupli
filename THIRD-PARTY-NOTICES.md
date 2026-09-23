# Third-party notices

Dupli is licensed under the MIT License (see `LICENSE`). It depends on the third-party
components listed below, each distributed under its own license.

## Runtime dependencies (shipped with the agent)

| Component | License |
|---|---|
| AWSSDK.S3 | Apache-2.0 |
| Cronos | MIT |
| Microsoft.Extensions.* (Hosting, Hosting.WindowsServices, Http, Logging) | MIT |
| Npgsql | PostgreSQL License |
| Polly.Core | BSD-3-Clause |
| Serilog.Extensions.Hosting, Serilog.Formatting.Compact, Serilog.Sinks.Console, Serilog.Sinks.File | Apache-2.0 |
| SharpZipLib | MIT |
| System.CommandLine | MIT |
| System.Security.Cryptography.ProtectedData | MIT |
| .NET runtime (self-contained publish) | MIT |

## Runtime dependencies (management server and web UI)

| Component | License |
|---|---|
| ASP.NET Core (incl. Authentication.JwtBearer, Authentication.OpenIdConnect, Data Protection) | MIT |
| Entity Framework Core, EFCore.NamingConventions | MIT / Apache-2.0 |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL License |
| dbup-postgresql | MIT |
| MailKit | MIT |
| Azure.Identity (incl. Microsoft Identity Client) | MIT |
| Microsoft Graph .NET SDK (Microsoft.Graph, Microsoft.Graph.Core, Kiota) | MIT |
| Cronos | MIT |
| Serilog.AspNetCore | Apache-2.0 |
| Angular (`@angular/*`), RxJS, tslib | MIT / Apache-2.0 / 0BSD |

## External tools (not bundled)

| Tool | License | How it is used |
|---|---|---|
| [restic](https://github.com/restic/restic) | BSD-2-Clause | Downloaded at runtime from the official GitHub release (pinned version and SHA-256) and run as a separate process. The management server can mirror the same pinned release for agents; redistributing it must keep restic's license with it. |
| PostgreSQL client tools (`pg_dump`, `pg_dumpall`) | PostgreSQL License | Found on the host (existing PostgreSQL installation) and run as separate processes. |

## Test-only dependencies (not shipped)

| Component | License |
|---|---|
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Xunit.SkippableFact | MS-PL |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |
| Testcontainers.PostgreSql | MIT |
| RustFS (container image, S3-compatible) | Apache-2.0 |
