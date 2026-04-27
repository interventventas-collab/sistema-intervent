using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Integration> Integrations => Set<Integration>();
    public DbSet<MeliAccount> MeliAccounts => Set<MeliAccount>();
    public DbSet<MeliOrder> MeliOrders => Set<MeliOrder>();
    public DbSet<MeliItem> MeliItems => Set<MeliItem>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ScheduledProcess> ScheduledProcesses => Set<ScheduledProcess>();
    public DbSet<ProcessExecutionLog> ProcessExecutionLogs => Set<ProcessExecutionLog>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<BackupFile> BackupFiles => Set<BackupFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasIndex(u => u.Username).IsUnique();
            entity.HasIndex(u => u.Email).IsUnique();
            entity.HasOne(u => u.RoleNav)
                  .WithMany(r => r.Users)
                  .HasForeignKey(u => u.RoleId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasIndex(r => r.Name).IsUnique();
        });

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasIndex(rp => new { rp.RoleId, rp.MenuKey }).IsUnique();
            entity.HasOne(rp => rp.Role)
                  .WithMany(r => r.Permissions)
                  .HasForeignKey(rp => rp.RoleId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppSetting>().HasKey(a => a.Key);

        modelBuilder.Entity<Integration>(entity =>
        {
            entity.HasIndex(i => i.Provider).IsUnique();
        });

        modelBuilder.Entity<MeliAccount>(entity =>
        {
            entity.HasIndex(a => a.MeliUserId).IsUnique();
        });

        modelBuilder.Entity<MeliOrder>(entity =>
        {
            entity.HasIndex(o => new { o.MeliOrderId, o.ItemId }).IsUnique();
            entity.HasIndex(o => o.MeliAccountId);
            entity.HasIndex(o => o.DateCreated);
            entity.HasIndex(o => o.PackId);
            entity.HasOne(o => o.MeliAccount)
                  .WithMany()
                  .HasForeignKey(o => o.MeliAccountId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MeliItem>(entity =>
        {
            entity.HasIndex(i => i.MeliItemId).IsUnique();
            entity.HasIndex(i => i.MeliAccountId);
            entity.HasIndex(i => i.Status);
            entity.HasIndex(i => i.UserProductId);
            entity.HasIndex(i => i.FamilyId);
            entity.HasOne(i => i.MeliAccount)
                  .WithMany()
                  .HasForeignKey(i => i.MeliAccountId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(i => i.ProductId);
            entity.HasOne(i => i.Product)
                  .WithMany(p => p.MeliItems)
                  .HasForeignKey(i => i.ProductId)
                  .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<AuditLog>(entity =>
        {
            entity.HasIndex(a => new { a.EntityType, a.EntityId });
            entity.HasIndex(a => a.CreatedAt);
        });

        modelBuilder.Entity<ScheduledProcess>(entity =>
        {
            entity.HasIndex(p => p.Code).IsUnique();
        });

        modelBuilder.Entity<BackupFile>(entity =>
        {
            entity.HasIndex(b => b.FileName).IsUnique();
            entity.HasIndex(b => b.CreatedAt);
        });

        modelBuilder.Entity<ProcessExecutionLog>(entity =>
        {
            entity.HasIndex(l => l.ProcessCode);
            entity.HasIndex(l => l.StartedAt);
            entity.HasOne(l => l.Process)
                  .WithMany()
                  .HasForeignKey(l => l.ProcessCode)
                  .HasPrincipalKey(p => p.Code)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
