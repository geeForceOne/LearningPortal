using LearningPortal.Core.Data;
using LearningPortal.Core.Email;
using LearningPortal.Core.Security;
using LearningPortal.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LearningPortal.Core;

public static class ServiceCollectionExtensions
{
    // Registers persistence and the Core services. The host is responsible for Data Protection
    // (key storage location) and Identity.
    public static IServiceCollection AddLearningPortalCore(
        this IServiceCollection services, string connectionString, LearningPortalOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<EmailSettingsService>();
        services.AddSingleton<EmailSender>();
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TopicService>();
        services.AddSingleton<MaterialService>();
        services.AddSingleton<QuestionGenerationService>();
        services.AddSingleton<ExamService>();
        services.AddSingleton<AttemptService>();
        services.AddSingleton<StatisticsService>();
        services.AddSingleton<UserActivityService>();
        services.AddSingleton<AdminStatisticsService>();
        return services;
    }
}
