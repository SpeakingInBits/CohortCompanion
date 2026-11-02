using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(user =>
        {
            user.Property(u => u.DisplayName).HasMaxLength(100);
            user.HasOne(u => u.InviteCode)
                .WithMany(c => c.RedeemedBy)
                .HasForeignKey(u => u.InviteCodeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        builder.Entity<InviteCode>(code =>
        {
            code.Property(c => c.Code).HasMaxLength(16);
            code.HasIndex(c => c.Code).IsUnique();
            code.Property(c => c.Note).HasMaxLength(200);
            code.Property(c => c.LockedToEmail).HasMaxLength(256);
            code.HasOne(c => c.CreatedBy)
                .WithMany()
                .HasForeignKey(c => c.CreatedById)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
