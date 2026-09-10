using Dashboard.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dashboard.Infrastructure.Identity;

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<AppConfig> Configs => Set<AppConfig>();
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Model> Models => Set<Model>();
    public DbSet<LaunchSpecEntity> LaunchSpecs => Set<LaunchSpecEntity>();
    public DbSet<MetricsSampleEntity> MetricsSamples => Set<MetricsSampleEntity>();
    public DbSet<MetricsRollupEntity> MetricsRollups => Set<MetricsRollupEntity>();

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
        });

        builder.Entity<Model>(entity =>
        {
            entity.ToTable("Models");
            entity.HasKey(m => m.Id);
            entity.Property(m => m.Name).HasMaxLength(300).IsRequired();
            entity.Property(m => m.DisplayName).HasMaxLength(300);
            entity.Property(m => m.SwapArgs).HasMaxLength(1000);
            entity.HasIndex(m => new { m.SourceId, m.Name }).IsUnique();
            entity.HasOne(m => m.Source)
                .WithMany(s => s.Models)
                .HasForeignKey(m => m.SourceId)
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
            entity.HasOne(s => s.Source)
                .WithMany()
                .HasForeignKey(s => s.SourceId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<MetricsRollupEntity>(entity =>
        {
            entity.ToTable("MetricsRollups");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).ValueGeneratedOnAdd();
            entity.Property(r => r.ModelName).HasMaxLength(300).IsRequired();
            entity.HasIndex(r => new { r.SourceId, r.ModelName, r.BucketStart }).IsUnique();
            entity.HasOne(r => r.Source)
                .WithMany()
                .HasForeignKey(r => r.SourceId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
