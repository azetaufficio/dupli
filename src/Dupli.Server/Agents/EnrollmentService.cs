using Dupli.Contracts.Agents;
using Dupli.Server.Api;
using Dupli.Server.Auth;
using Dupli.Server.Configuration;
using Dupli.Server.Domain.Agents;
using Dupli.Server.Infrastructure.Database;
using Dupli.Server.Infrastructure.Security;
using Dupli.Server.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dupli.Server.Agents;

/// <summary>Enrollment token exchange, agent authentication and secret rotation.</summary>
public sealed class EnrollmentService(
    DupliDbContext db,
    SecretProtector protector,
    AgentTokenIssuer tokens,
    ReleaseMirror mirror,
    IOptions<DupliServerOptions> options,
    TimeProvider time,
    ILogger<EnrollmentService> logger)
{
    public async Task<string> CreateEnrollmentTokenAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([agentId], ct) ?? throw ApiException.NotFound("Agent");
        if (agent.Status == AgentStatus.Disabled)
            throw ApiException.Conflict("Agent is disabled");

        var token = SecretHashing.NewSecret();
        var now = time.GetUtcNow();
        db.EnrollmentTokens.Add(new EnrollmentToken
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            TokenHash = SecretHashing.Hash(token),
            CreatedAt = now,
            ExpiresAt = now + options.Value.Agents.EnrollmentTokenLifetime,
        });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Enrollment token created for agent {AgentId}", agentId);
        return token;
    }

    /// <summary>
    /// Single-use token → AgentId + fresh secret + escrowed repository/S3 credentials.
    /// A re-enrollment (reinstall) is accepted only from the machine the agent is bound to.
    /// </summary>
    public async Task<RegisterAgentResponse> RegisterAsync(RegisterAgentRequest request, string publicBaseUrl, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var hash = SecretHashing.Hash(request.EnrollmentToken);
        var token = await db.EnrollmentTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null || !token.IsUsable(now))
            throw ApiException.Unauthorized("Enrollment token invalid, expired or already used");

        var agent = await db.Agents.Include(a => a.StorageTarget).SingleAsync(a => a.Id == token.AgentId, ct);
        if (agent.Status == AgentStatus.Disabled)
            throw ApiException.Conflict("Agent is disabled");
        if (agent.MachineId is not null && agent.MachineId != request.MachineId)
            throw ApiException.Conflict("Agent is bound to a different machine");

        var platform = string.IsNullOrWhiteSpace(request.Platform) ? ReleaseMirror.DefaultPlatform : request.Platform;
        var manifest = await mirror.GetManifestAsync(ReleaseMirror.ResticProduct, platform, publicBaseUrl, ct)
            ?? throw ApiException.BadRequest($"No current restic release for platform '{platform}'");

        var secret = SecretHashing.NewSecret();
        token.UsedAt = now;
        agent.Status = AgentStatus.Active;
        agent.MachineId = request.MachineId;
        agent.Hostname = request.Hostname;
        agent.OsVersion = request.OsVersion;
        agent.Version = request.AgentVersion;
        agent.Platform = platform;
        agent.SecretHash = SecretHashing.Hash(secret);
        agent.SecretRotatedAt = now;
        agent.EnrolledAt = now;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Agent {AgentId} enrolled from {Hostname}", agent.Id, request.Hostname);
        var storage = agent.StorageTarget!;
        return new RegisterAgentResponse
        {
            AgentId = agent.Id.ToString(),
            AgentSecret = secret,
            Repository = new RepositoryDto
            {
                Endpoint = storage.Endpoint,
                Bucket = storage.Bucket,
                Prefix = agent.StoragePrefix,
                Region = storage.Region,
            },
            RepositoryPassword = protector.Unprotect(agent.RepositoryPasswordProtected),
            S3AccessKeyId = agent.S3AccessKeyId,
            S3SecretAccessKey = protector.Unprotect(agent.S3SecretKeyProtected),
            ResticManifest = manifest,
            PollIntervalSeconds = options.Value.Agents.PollIntervalSeconds,
        };
    }

    public async Task<AgentTokenResponse> IssueTokenAsync(AgentTokenRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(request.AgentId, out var agentId))
            throw ApiException.Unauthorized("Invalid agent credentials");

        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, ct);
        if (agent is not { Status: AgentStatus.Active, SecretHash: { } expected } || !SecretHashing.Verify(request.AgentSecret, expected))
            throw ApiException.Unauthorized("Invalid agent credentials");

        var (accessToken, expiresAt) = tokens.Issue(agentId);
        return new AgentTokenResponse { AccessToken = accessToken, ExpiresAt = expiresAt };
    }

    public async Task<RotateSecretResponse> RotateSecretAsync(Guid agentId, CancellationToken ct)
    {
        var agent = await db.Agents.FindAsync([agentId], ct) ?? throw ApiException.NotFound("Agent");
        var secret = SecretHashing.NewSecret();
        agent.SecretHash = SecretHashing.Hash(secret);
        agent.SecretRotatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Secret rotated for agent {AgentId}", agentId);
        return new RotateSecretResponse { AgentSecret = secret };
    }
}
