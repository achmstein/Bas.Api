using Bas.Api.Admin;
using Bas.Api.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Bas.Api.Data;

/// <summary>
/// The service's own store. Postgres in every deployed environment; the model is kept
/// provider-neutral so the test suite can run it on SQLite without a Docker daemon.
/// </summary>
public sealed class BasDbContext(DbContextOptions<BasDbContext> options)
    : IdentityDbContext<AdminUser>(options)
{
    public DbSet<Partner> Partners => Set<Partner>();

    public DbSet<PartnerUserLink> PartnerUserLinks => Set<PartnerUserLink>();

    public DbSet<Worker> Workers => Set<Worker>();

    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    public DbSet<BasPeriod> BasPeriods => Set<BasPeriod>();

    public DbSet<SyncState> SyncStates => Set<SyncState>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Identity's own tables. They are only reachable by an administrator signing in, and they
        // have nothing to do with a worker or a partner, so they are prefixed to keep them visually
        // apart from the domain in a database browser.
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AdminUser>().ToTable("admin_users");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityRole>().ToTable("admin_roles");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityUserRole<string>>().ToTable("admin_user_roles");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityUserClaim<string>>().ToTable("admin_user_claims");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityUserLogin<string>>().ToTable("admin_user_logins");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityUserToken<string>>().ToTable("admin_user_tokens");
        modelBuilder.Entity<Microsoft.AspNetCore.Identity.IdentityRoleClaim<string>>().ToTable("admin_role_claims");

        modelBuilder.Entity<Partner>(entity =>
        {
            entity.ToTable("partners");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ClientId).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.PublicKeyPem).HasMaxLength(4000).IsRequired();
            entity.Property(e => e.AllowedScopes).HasMaxLength(500).IsRequired();
            entity.Property(e => e.WebhookUrl).HasMaxLength(2000);
            entity.Property(e => e.WebhookSecret).HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.HasIndex(e => e.ClientId).IsUnique();
        });

        modelBuilder.Entity<Worker>(entity =>
        {
            entity.ToTable("workers");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TfnLast3).HasMaxLength(3);
            entity.Property(e => e.Abn).HasMaxLength(11);
            entity.Property(e => e.FirstName).HasMaxLength(100);
            entity.Property(e => e.FamilyName).HasMaxLength(100);
            entity.Property(e => e.Email).HasMaxLength(320);
            entity.Property(e => e.Phone).HasMaxLength(40);
            entity.Ignore(e => e.IsCompleteForLodgement);
        });

        modelBuilder.Entity<BasPeriod>(entity =>
        {
            entity.ToTable("bas_periods");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.StatementType).HasMaxLength(4);
            entity.Property(e => e.VariationReasonCode).HasMaxLength(10);
            entity.Property(e => e.FailureReason).HasMaxLength(2000);

            // One statement per worker per quarter. A second row for the same period would mean two
            // sets of figures racing each other to the practice.
            entity.HasIndex(e => new { e.WorkerId, e.FinancialYear, e.Quarter }).IsUnique();

            // The reconciler in phase 3c sweeps for work by status.
            entity.HasIndex(e => e.Status);

            entity.HasOne(e => e.Worker)
                  .WithMany(w => w.BasPeriods)
                  .HasForeignKey(e => e.WorkerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.Ignore(e => e.IsEditable);
        });

        modelBuilder.Entity<SyncState>(entity =>
        {
            entity.ToTable("sync_states");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64);
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // One ledger row per statement.
            entity.HasIndex(e => e.BasPeriodId).IsUnique();

            // The reconciler's only query: due work, oldest first.
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });

            entity.HasOne(e => e.BasPeriod)
                  .WithOne()
                  .HasForeignKey<SyncState>(e => e.BasPeriodId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WebhookDelivery>(entity =>
        {
            entity.ToTable("webhook_deliveries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Payload).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.LastError).HasMaxLength(2000);

            // The dispatcher's only query: due deliveries, oldest first.
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt });

            entity.HasOne(e => e.Partner)
                  .WithMany()
                  .HasForeignKey(e => e.PartnerId)
                  .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.BasPeriod)
                  .WithMany()
                  .HasForeignKey(e => e.BasPeriodId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.ToTable("audit_entries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Subject).HasMaxLength(200);
            entity.Property(e => e.Detail).HasMaxLength(2000);

            // The only way it is ever read: newest first, optionally narrowed to one subject.
            entity.HasIndex(e => e.At);
            entity.HasIndex(e => new { e.Subject, e.At });
        });

        modelBuilder.Entity<PartnerUserLink>(entity =>
        {
            entity.ToTable("partner_user_links");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.PartnerSub).HasMaxLength(400).IsRequired();

            // The rule from the auth design, expressed where it cannot be forgotten: one worker
            // per (partner, partner subject), and no other way in. A concurrent first-contact for
            // the same subject loses here rather than creating a second worker.
            entity.HasIndex(e => new { e.PartnerId, e.PartnerSub }).IsUnique();

            entity.HasOne(e => e.Partner)
                  .WithMany(p => p.UserLinks)
                  .HasForeignKey(e => e.PartnerId)
                  .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(e => e.Worker)
                  .WithMany(w => w.PartnerLinks)
                  .HasForeignKey(e => e.WorkerId)
                  .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SigningKey>(entity =>
        {
            entity.ToTable("signing_keys");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kid).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Algorithm).HasMaxLength(20).IsRequired();
            entity.Property(e => e.PrivateKeyProtected).IsRequired();

            // Also the arbiter when two instances start at once and both try to create the first
            // key: one insert wins, the loser re-reads.
            entity.HasIndex(e => e.Kid).IsUnique();
        });
    }
}
