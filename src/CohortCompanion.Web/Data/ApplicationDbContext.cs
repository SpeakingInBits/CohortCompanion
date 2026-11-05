using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<InviteCode> InviteCodes => Set<InviteCode>();

    public DbSet<Quiz> Quizzes => Set<Quiz>();

    public DbSet<Question> Questions => Set<Question>();

    public DbSet<Presentation> Presentations => Set<Presentation>();

    public DbSet<Slide> Slides => Set<Slide>();

    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();

    public DbSet<Participant> Participants => Set<Participant>();

    public DbSet<Response> Responses => Set<Response>();

    public DbSet<SessionSummary> SessionSummaries => Set<SessionSummary>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Presentation>(presentation =>
        {
            presentation.Property(p => p.Title).HasMaxLength(200);
            presentation.Property(p => p.ThemeId).HasMaxLength(30);
            presentation.HasIndex(p => p.OwnerId);
            presentation.HasOne(p => p.Owner)
                .WithMany()
                .HasForeignKey(p => p.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
            presentation.HasMany(p => p.Slides)
                .WithOne()
                .HasForeignKey(s => s.PresentationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<LiveSession>(session =>
        {
            session.Property(s => s.JoinCode).HasMaxLength(6);
            session.Property(s => s.Title).HasMaxLength(200);
            // A code is only reserved while its session is live. 2 is SessionStatus.Ended.
            session.HasIndex(s => s.JoinCode).IsUnique().HasFilter("[Status] <> 2");
            session.HasIndex(s => new { s.HostId, s.Status });
            session.HasOne(s => s.Host)
                .WithMany()
                .HasForeignKey(s => s.HostId)
                .OnDelete(DeleteBehavior.Cascade);
            session.HasMany(s => s.Participants)
                .WithOne()
                .HasForeignKey(p => p.LiveSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Participant>(participant =>
        {
            participant.Property(p => p.Nickname).HasMaxLength(20);
            participant.Property(p => p.TokenHash).HasMaxLength(64);
            participant.HasIndex(p => p.TokenHash);
            participant.HasMany(p => p.Responses)
                .WithOne()
                .HasForeignKey(r => r.ParticipantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Response>(response => response.HasIndex(r => new { r.ParticipantId, r.QuestionId }).IsUnique());

        builder.Entity<SessionSummary>(summary =>
        {
            summary.Property(s => s.PromptText).HasMaxLength(500);
            summary.HasIndex(s => s.LiveSessionId);
            summary.HasOne<LiveSession>()
                .WithMany()
                .HasForeignKey(s => s.LiveSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Quiz>(quiz =>
        {
            quiz.Property(q => q.Title).HasMaxLength(200);
            quiz.HasIndex(q => q.OwnerId);
            quiz.HasOne(q => q.Owner)
                .WithMany()
                .HasForeignKey(q => q.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
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
