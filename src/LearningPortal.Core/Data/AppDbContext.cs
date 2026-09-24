using LearningPortal.Core.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LearningPortal.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Material> Materials => Set<Material>();
    public DbSet<MaterialSection> MaterialSections => Set<MaterialSection>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<Exam> Exams => Set<Exam>();
    public DbSet<ExamQuestion> ExamQuestions => Set<ExamQuestion>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<AttemptAnswer> AttemptAnswers => Set<AttemptAnswer>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.DisplayName).HasMaxLength(AppUser.MaxDisplayNameLength);
            e.Ignore(x => x.ShownName);
        });

        b.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.UserName).HasMaxLength(320);
            e.Property(x => x.From).HasMaxLength(320);
            e.Property(x => x.FromName).HasMaxLength(200);
            e.Property(x => x.PublicUrl).HasMaxLength(500);
        });

        b.Entity<Topic>(e =>
        {
            e.HasIndex(x => x.UserId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Language).HasMaxLength(80);
        });

        b.Entity<Material>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TopicId });
            e.HasOne(x => x.Topic).WithMany(t => t.Materials).HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Title).HasMaxLength(300);
        });

        b.Entity<MaterialSection>(e =>
        {
            e.HasOne(x => x.Material).WithMany(m => m.Sections).HasForeignKey(x => x.MaterialId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Question>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TopicId, x.Type, x.Difficulty });
            e.HasOne(x => x.Topic).WithMany(t => t.Questions).HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.SourceSection).WithMany().HasForeignKey(x => x.SourceSectionId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Material>().WithMany().HasForeignKey(x => x.SourceMaterialId).OnDelete(DeleteBehavior.SetNull);
            e.HasMany(x => x.Options).WithOne().HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Exam>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.TopicId });
            e.HasOne(x => x.Topic).WithMany(t => t.Exams).HasForeignKey(x => x.TopicId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(200);
        });

        b.Entity<ExamQuestion>(e =>
        {
            e.HasKey(x => new { x.ExamId, x.QuestionId });
            e.HasOne(x => x.Exam).WithMany(x => x.Questions).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Attempt>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ExamId });
            e.HasOne(x => x.Exam).WithMany(x => x.Attempts).HasForeignKey(x => x.ExamId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AttemptAnswer>(e =>
        {
            e.HasIndex(x => new { x.AttemptId, x.Position });
            e.HasOne(x => x.Attempt).WithMany(x => x.Answers).HasForeignKey(x => x.AttemptId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Question).WithMany().HasForeignKey(x => x.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
