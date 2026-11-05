using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();

    public DbSet<Quiz> Quizzes => Set<Quiz>();

    public DbSet<Question> Questions => Set<Question>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Quiz>(quiz =>
        {
            quiz.Property(q => q.Title).HasMaxLength(200);
            quiz.HasIndex(q => q.OwnerId);
            quiz.HasOne(q => q.Owner)
                .WithMany()
                .HasForeignKey(q => q.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
            quiz.HasMany(q => q.Questions)
                .WithOne()
                .HasForeignKey(q => q.QuizId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Question>(question =>
        {
            question.Property(q => q.CodeLanguage).HasMaxLength(30);
            question.Property(q => q.ImageUrl).HasMaxLength(500);
            question.HasMany(q => q.Options)
                .WithOne()
                .HasForeignKey(o => o.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
            question.HasMany(q => q.BlankAnswers)
                .WithOne()
                .HasForeignKey(a => a.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ChoiceOption>(option => option.Property(o => o.Text).HasMaxLength(500));

        builder.Entity<BlankAnswer>(answer => answer.Property(a => a.AcceptedText).HasMaxLength(200));

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
