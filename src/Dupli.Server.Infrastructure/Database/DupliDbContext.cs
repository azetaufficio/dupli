using Dupli.Server.Domain.Agents;
using Dupli.Server.Domain.Jobs;
using Dupli.Server.Domain.Monitoring;
using Dupli.Server.Domain.Operators;
using Dupli.Server.Domain.Policies;
using Dupli.Server.Domain.Tools;
using Microsoft.EntityFrameworkCore;

namespace Dupli.Server.Infrastructure.Database;

/// <summary>
/// Query/mapping only. The schema is owned by the DbUp scripts in <c>Database/Scripts</c>:
/// no EF migrations, and this model must match those scripts.
/// </summary>
public sealed class DupliDbContext(DbContextOptions<DupliDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<StorageTarget> StorageTargets => Set<StorageTarget>();
    public DbSet<PgConnection> PgConnections => Set<PgConnection>();
    public DbSet<BackupPolicy> Policies => Set<BackupPolicy>();
    public DbSet<BackupSource> Sources => Set<BackupSource>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<BackupRun> Runs => Set<BackupRun>();
    public DbSet<AgentLog> Logs => Set<AgentLog>();
    public DbSet<SoftwareRelease> Releases => Set<SoftwareRelease>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<OperatorUser> OperatorUsers => Set<OperatorUser>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public DbSet<OperatorNotification> Notifications => Set<OperatorNotification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<StorageTarget>().ToTable("storage_target");

        b.Entity<Agent>(e =>
        {
            e.ToTable("agent");
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.S3AccessKeyId).HasColumnName("s3_access_key_id");
            e.Property(x => x.S3SecretKeyProtected).HasColumnName("s3_secret_key_protected");
            // The snake_case convention does not split "S3" the way we want (e.g. "s3credentials_version"):
            // same reason S3AccessKeyId/S3SecretKeyProtected above are spelled out too.
            e.Property(x => x.S3CredentialsVersion).HasColumnName("s3_credentials_version");
            e.Property(x => x.S3CredentialsAppliedVersion).HasColumnName("s3_credentials_applied_version");
            e.Property(x => x.S3CredentialsUpdatedAt).HasColumnName("s3_credentials_updated_at");
            e.Property(x => x.S3CredentialsUpdatedBy).HasColumnName("s3_credentials_updated_by");
            e.HasOne(x => x.StorageTarget).WithMany().HasForeignKey(x => x.StorageTargetId);
        });

        b.Entity<EnrollmentToken>().ToTable("enrollment_token");

        b.Entity<PgConnection>(e =>
        {
            e.ToTable("pg_connection");
            e.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BackupPolicy>(e =>
        {
            e.ToTable("backup_policy");
            e.HasMany(x => x.Sources).WithOne().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<BackupSource>(e =>
        {
            e.ToTable("backup_source");
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Spec).HasColumnType("jsonb");
        });

        b.Entity<Job>(e =>
        {
            e.ToTable("job");
            e.Property(x => x.Type).HasConversion<string>();
            e.Property(x => x.Trigger).HasConversion<string>();
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.ResultItems).HasColumnType("jsonb");
            e.Ignore(x => x.IsTerminal);
        });

        b.Entity<BackupRun>(e =>
        {
            e.ToTable("backup_run");
            e.Property(x => x.Items).HasColumnType("jsonb");
        });

        b.Entity<AgentLog>(e =>
        {
            e.ToTable("agent_log");
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Properties).HasColumnType("jsonb");
        });

        b.Entity<SoftwareRelease>().ToTable("software_release");

        b.Entity<Alert>(e =>
        {
            e.ToTable("alert");
            e.Property(x => x.Kind).HasConversion<string>();
        });

        b.Entity<OperatorUser>(e =>
        {
            e.ToTable("operator_user");
            e.Property(x => x.Role).HasConversion<string>();
            e.Ignore(x => x.IsBound);
            e.Ignore(x => x.IsActive);
        });

        b.Entity<NotificationPreference>(e =>
        {
            e.ToTable("notification_preference");
            e.HasKey(x => new { x.UserId, x.Kind });
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasOne<OperatorUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<OperatorNotification>(e =>
        {
            e.ToTable("notification");
            e.Property(x => x.Kind).HasConversion<string>();
            e.Property(x => x.Event).HasConversion<string>();
            e.Property(x => x.EmailStatus).HasConversion<string>();
            e.HasOne<OperatorUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Alert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
