using Microsoft.EntityFrameworkCore;

namespace IntegrationPanel.Api.Data;

public sealed class PanelDbContext(DbContextOptions<PanelDbContext> options) : DbContext(options)
{
    public DbSet<IntegrationLog> IntegrationLogs => Set<IntegrationLog>();
    public DbSet<StatusMapping> StatusMappings => Set<StatusMapping>();
    public DbSet<StatusMappingProposal> StatusMappingProposals => Set<StatusMappingProposal>();
    public DbSet<AiCall> AiCalls => Set<AiCall>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IntegrationLog>(e =>
        {
            e.ToTable("IntegrationLogs");
            e.HasIndex(l => new { l.Entity, l.ExternalId }).IsUnique();
            e.HasIndex(l => new { l.Entity, l.Status });
            e.HasIndex(l => l.UpdatedAtUtc);
            e.Property(l => l.Entity).HasMaxLength(50);
            e.Property(l => l.ExternalId).HasMaxLength(50);
            e.Property(l => l.Message).HasMaxLength(1000);
            e.Property(l => l.ErrorCode).HasMaxLength(50);
            e.Property(l => l.ErrorDetail).HasMaxLength(200);
        });

        modelBuilder.Entity<StatusMapping>(e =>
        {
            e.ToTable("StatusMappings");
            e.HasKey(m => m.ErpStatus);
            e.Property(m => m.ErpStatus).HasMaxLength(30);
            e.Property(m => m.StoreStatus).HasMaxLength(30);
        });

        modelBuilder.Entity<StatusMappingProposal>(e =>
        {
            e.ToTable("StatusMappingProposals");
            e.HasIndex(p => new { p.ErpStatus, p.State });
            e.Property(p => p.ErpStatus).HasMaxLength(30);
            e.Property(p => p.SuggestedStoreStatus).HasMaxLength(30);
            e.Property(p => p.Reason).HasMaxLength(500);
            e.Property(p => p.Source).HasMaxLength(100);
            e.Property(p => p.DecisionNote).HasMaxLength(500);
            e.Property(p => p.DecidedBy).HasMaxLength(50);
        });

        modelBuilder.Entity<AiCall>(e =>
        {
            e.ToTable("AiCalls");
            e.HasIndex(c => c.AtUtc);
            e.Property(c => c.Provider).HasMaxLength(100);
            e.Property(c => c.Purpose).HasMaxLength(50);
            e.Property(c => c.CostUsd).HasPrecision(18, 6);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(u => u.UserName).IsUnique();
            e.Property(u => u.UserName).HasMaxLength(50);
            e.Property(u => u.DisplayName).HasMaxLength(100);
            e.Property(u => u.Role).HasMaxLength(20);
            e.Property(u => u.PasswordHash).HasMaxLength(500);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasIndex(a => a.AtUtc);
            e.Property(a => a.UserName).HasMaxLength(50);
            e.Property(a => a.Action).HasMaxLength(50);
            e.Property(a => a.Target).HasMaxLength(100);
            e.Property(a => a.Details).HasMaxLength(500);
        });
    }
}
