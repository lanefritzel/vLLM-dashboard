using Dashboard.Infrastructure.Persistence;
using V2 = Dashboard.Infrastructure.Persistence.V2;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Infrastructure.Identity;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AppConfig> Configs => Set<AppConfig>();
    public DbSet<PhysicalServer> PhysicalServers => Set<PhysicalServer>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<LaunchSpecEntity> LaunchSpecs => Set<LaunchSpecEntity>();
    public DbSet<MetricsSampleEntity> MetricsSamples => Set<MetricsSampleEntity>();
    public DbSet<MetricsRollupEntity> MetricsRollups => Set<MetricsRollupEntity>();
    public DbSet<AppLogEntity> AppLogs => Set<AppLogEntity>();

    // New schema (P01a) -- additive only, not yet read/written outside tests. "V2"
    // suffix distinguishes these from the legacy tables above, which the old
    // swap-era code paths keep using until P16.
    public DbSet<V2.Server> ServersV2 => Set<V2.Server>();
    public DbSet<V2.Instance> InstancesV2 => Set<V2.Instance>();
    public DbSet<V2.Model> ModelsV2 => Set<V2.Model>();
    public DbSet<V2.Recipe> RecipesV2 => Set<V2.Recipe>();
    public DbSet<V2.Deployment> DeploymentsV2 => Set<V2.Deployment>();
    public DbSet<V2.DeploymentEvent> DeploymentEventsV2 => Set<V2.DeploymentEvent>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppConfig>(entity =>
        {
            entity.HasKey(c => c.Key);
            entity.Property(c => c.Key).HasMaxLength(100);
            entity.Property(c => c.Value).IsRequired();
            entity.Property(c => c.UpdatedAt).IsRequired();
        });

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.Status).HasMaxLength(20).IsRequired();
            entity.Property(u => u.ThemeMode).HasMaxLength(20).IsRequired();
        });

        builder.Entity<PhysicalServer>(entity =>
        {
            entity.ToTable("PhysicalServers");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Label).HasMaxLength(200).IsRequired();
            entity.Property(s => s.DockerHost).HasMaxLength(500).IsRequired();
            entity.Property(s => s.GpuVendor).HasMaxLength(20).IsRequired();
            entity.Property(s => s.HfCacheHostPath).HasMaxLength(1000);
            entity.Property(s => s.HfToken).HasMaxLength(500);
        });

        builder.Entity<Source>(entity =>
        {
            entity.ToTable("Sources");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Slug).HasMaxLength(60).IsRequired();
            entity.HasIndex(s => s.Slug).IsUnique();
            entity.Property(s => s.Label).HasMaxLength(200).IsRequired();
            entity.Property(s => s.Type).HasMaxLength(20).IsRequired();
            entity.Property(s => s.MetricsUrl).HasMaxLength(500).IsRequired();
            entity.Property(s => s.InferenceUrl).HasMaxLength(500).IsRequired();
            entity.Property(s => s.DockerHost).HasMaxLength(500);
            entity.Property(s => s.ContainerName).HasMaxLength(200);
            // Restrict, not cascade: deleting a physical server out from under a vLLM
            // instance that still points at it would silently break hot-swap for that
            // instance — force the user to reassign or delete the instance first.
            entity.HasOne(s => s.PhysicalServer)
                .WithMany(p => p.Sources)
                .HasForeignKey(s => s.PhysicalServerId)
                .OnDelete(DeleteBehavior.Restrict);
            // "Active"/"preload" are per-instance state pointing into the physical
            // server's shared model catalog — SetNull (not Restrict/Cascade) so
            // deleting a model just clears whichever instances had it active/
            // preloaded, rather than being blocked or taking the instance down.
            entity.HasOne<Model>()
                .WithMany()
                .HasForeignKey(s => s.ActiveModelId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<Model>()
                .WithMany()
                .HasForeignKey(s => s.PreloadModelId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<Model>(entity =>
        {
            entity.ToTable("Models");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Name).HasMaxLength(300).IsRequired();
            entity.Property(m => m.DisplayName).HasMaxLength(300);
            entity.Property(m => m.SwapArgs).HasMaxLength(1000);
            entity.HasIndex(m => new { m.PhysicalServerId, m.Name }).IsUnique();
            // A model is registered on the physical server, shared by every vLLM
            // instance on it — deleting the server takes its whole catalog with it.
            // The old per-instance ownership (SourceId) is deliberately left
            // unconfigured as a relationship: it's a plain deprecated column now,
            // read only by the upgrade backfill.
            entity.HasOne(m => m.PhysicalServer)
                .WithMany()
                .HasForeignKey(m => m.PhysicalServerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LaunchSpecEntity>(entity =>
        {
            entity.ToTable("LaunchSpecs");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Image).HasMaxLength(500).IsRequired();
            entity.Property(l => l.ModelArgTemplate).HasMaxLength(1000).IsRequired();
            entity.Property(l => l.RestartPolicy).HasMaxLength(50).IsRequired();
            entity.Property(l => l.ExtraRunArgs).HasMaxLength(2000);
            entity.Property(l => l.GpuOptions).HasMaxLength(500);
            entity.HasOne(l => l.Source)
                .WithOne(s => s.LaunchSpec)
                .HasForeignKey<LaunchSpecEntity>(l => l.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<MetricsSampleEntity>(entity =>
        {
            entity.ToTable("MetricsSamples");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedOnAdd();
            entity.Property(s => s.SourceId).HasMaxLength(200);
            entity.Property(s => s.SourceLabel).HasMaxLength(200).IsRequired();
            entity.Property(s => s.ModelName).HasMaxLength(300);
            entity.Property(s => s.KvPct).HasPrecision(5, 2);
            entity.Property(s => s.GenTps).HasPrecision(12, 4);
            entity.Property(s => s.PromptTps).HasPrecision(12, 4);
            entity.Property(s => s.ReqTps).HasPrecision(12, 4);
            entity.Property(s => s.TtftAvgSec).HasPrecision(12, 4);
            entity.Property(s => s.ItlAvgSec).HasPrecision(12, 4);
            entity.Property(s => s.E2eAvgSec).HasPrecision(12, 4);
            entity.Property(s => s.QueueAvgSec).HasPrecision(12, 4);
            entity.Property(s => s.PrefixHitRatio).HasPrecision(5, 4);
            entity.HasIndex(s => new { s.SourceId, s.Ts });
            entity.HasIndex(s => s.Ts);
            // No FK to Sources anymore (P16a finding #21) -- SourceId is a
            // plain string now, shared by legacy sources (a Guid's own
            // ToString()) and V2 instances ("instance-N") alike, with no
            // table that could hold both kinds of id as its primary key.
        });

        builder.Entity<MetricsRollupEntity>(entity =>
        {
            entity.ToTable("MetricsRollups");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.SourceId).HasMaxLength(200).IsRequired();
            entity.Property(r => r.ModelName).HasMaxLength(300).IsRequired();
            entity.HasIndex(r => new { r.SourceId, r.ModelName, r.BucketStart }).IsUnique();
        });

        builder.Entity<AppLogEntity>(entity =>
        {
            entity.ToTable("AppLogs");
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Id).ValueGeneratedOnAdd();
            entity.Property(l => l.Level).HasMaxLength(20).IsRequired();
            entity.Property(l => l.Category).HasMaxLength(300).IsRequired();
            entity.Property(l => l.Message).IsRequired();
            // Retention pruning (RollupService) deletes by age; browsing by recency is
            // the other natural access pattern — both want this index.
            entity.HasIndex(l => l.TimestampUtc);
        });

        ConfigureV2Schema(builder);
    }

    /// <summary>New schema (P01a) — additive only. Per the phase page, these use
    /// GENERATED ALWAYS AS IDENTITY (not this project's usual IDENTITY BY DEFAULT)
    /// so a seed fixture or conversion migration can't silently collide with a
    /// value Postgres already handed out.</summary>
    private static void ConfigureV2Schema(ModelBuilder builder)
    {
        builder.Entity<V2.Server>(entity =>
        {
            entity.ToTable("servers");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).UseIdentityAlwaysColumn();
            entity.Property(s => s.Label).IsRequired();
            entity.HasIndex(s => s.Label).IsUnique();
            entity.Property(s => s.ModelsDir).IsRequired();
            entity.Property(s => s.CreatedAt).HasDefaultValueSql("now()");
        });

        builder.Entity<V2.Instance>(entity =>
        {
            entity.ToTable("instances");
            entity.HasKey(i => i.Id);
            entity.Property(i => i.Id).UseIdentityAlwaysColumn();
            entity.Property(i => i.Label).IsRequired();
            entity.Property(i => i.ProxySlug).IsRequired();
            entity.HasIndex(i => i.ProxySlug).IsUnique();
            entity.Property(i => i.CreatedAt).HasDefaultValueSql("now()");
            // Restrict: a server with instances on it shouldn't vanish out from under
            // them — the operator reassigns/deletes the instance first, same rationale
            // as the legacy Source -> PhysicalServer relationship above.
            entity.HasOne(i => i.Server)
                .WithMany()
                .HasForeignKey(i => i.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<V2.Model>(entity =>
        {
            entity.ToTable("models");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Id).UseIdentityAlwaysColumn();
            entity.Property(m => m.Name).IsRequired();
            entity.Property(m => m.SourceType).HasMaxLength(20).IsRequired();
            entity.Property(m => m.Path).IsRequired();
            entity.Property(m => m.LoadFormat).IsRequired();
            entity.Property(m => m.Config).HasColumnType("jsonb");
            entity.Property(m => m.Status).HasMaxLength(20).IsRequired();
            entity.Property(m => m.CreatedAt).HasDefaultValueSql("now()");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_models_source_type", "\"SourceType\" IN ('local','huggingface')");
                t.HasCheckConstraint("ck_models_status", "\"Status\" IN ('available','downloading','download_failed')");
            });
            entity.HasOne(m => m.Server)
                .WithMany()
                .HasForeignKey(m => m.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<V2.Recipe>(entity =>
        {
            entity.ToTable("recipes");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).UseIdentityAlwaysColumn();
            entity.Property(r => r.Name).IsRequired();
            entity.Property(r => r.ServedModelName).IsRequired();
            entity.Property(r => r.KvCacheDtype).IsRequired();
            entity.Property(r => r.Dtype).IsRequired();
            entity.Property(r => r.ExtraArgs).HasColumnType("jsonb");
            entity.Property(r => r.ExtraEnv).HasColumnType("jsonb");
            entity.Property(r => r.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(r => new { r.ServerId, r.Name }).IsUnique();
            entity.HasOne(r => r.Server)
                .WithMany()
                .HasForeignKey(r => r.ServerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<V2.Deployment>(entity =>
        {
            entity.ToTable("deployments");
            entity.HasKey(d => d.Id);
            entity.Property(d => d.Id).UseIdentityAlwaysColumn();
            entity.Property(d => d.ArgsSnapshot).HasColumnType("jsonb").IsRequired();
            entity.Property(d => d.Status).HasMaxLength(20).IsRequired();
            entity.Property(d => d.StopReason).HasMaxLength(20);
            entity.Property(d => d.FailureReason).HasMaxLength(20);
            entity.Property(d => d.PendingStopReason).HasMaxLength(20);
            entity.Property(d => d.LastSeenSeq).HasDefaultValue(0);
            entity.Property(d => d.CreatedAt).HasDefaultValueSql("now()");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_deployments_status", "\"Status\" IN ('pending','active','stopped','failed')");
                t.HasCheckConstraint("ck_deployments_stop_reason", "\"StopReason\" IN ('swapped','rollback','manual','crash','cancelled')");
                t.HasCheckConstraint("ck_deployments_failure_reason", "\"FailureReason\" IN ('process_exited','health_timeout','bad_args','out_of_memory')");
                t.HasCheckConstraint("ck_deployments_pending_stop_reason", "\"PendingStopReason\" IN ('swapped','rollback')");
            });
            // Partial unique index: at most one PENDING deployment per instance —
            // makes a second concurrent swap impossible at the DB level, not just
            // in application code (doc 4). Originally also covered 'active', but
            // P10a's own swap flow needs the new 'pending' row to coexist with the
            // previous 'active' one until the new one reaches ready (when they
            // flip together, in one transaction) — a row that's merely active
            // and not being swapped away from isn't "a swap in progress" the way
            // a second 'pending' row would be, so only 'pending' needs excluding
            // a duplicate (narrowed by P10a, see its own migration).
            entity.HasIndex(d => d.InstanceId)
                .IsUnique()
                .HasDatabaseName("one_pending_per_instance")
                .HasFilter("\"Status\" = 'pending'");
            entity.HasOne(d => d.Instance)
                .WithMany()
                .HasForeignKey(d => d.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(d => d.Model)
                .WithMany()
                .HasForeignKey(d => d.ModelId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(d => d.Recipe)
                .WithMany()
                .HasForeignKey(d => d.RecipeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<V2.DeploymentEvent>(entity =>
        {
            entity.ToTable("deployment_events");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.State).IsRequired();
            entity.Property(e => e.EventAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.DeploymentId, e.Seq }).IsUnique();
            // Cascade: an event log has no meaning once its own deployment is gone —
            // unlike the Restrict relationships above, which guard against losing a
            // still-meaningful parent (a server, a model, a recipe).
            entity.HasOne(e => e.Deployment)
                .WithMany()
                .HasForeignKey(e => e.DeploymentId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
