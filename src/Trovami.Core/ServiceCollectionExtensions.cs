using Trovami.Core.Ai;
using Trovami.Core.Backup;
using Trovami.Core.Data;
using Trovami.Core.Email;
using Trovami.Core.Photos;
using Trovami.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Trovami.Core;

public static class ServiceCollectionExtensions
{
    // Registers persistence and the Core services. The host is responsible for Data Protection
    // (key storage location), Identity, calling Scheduler.RunOnceAsync every minute and running
    // AiNamingService.RunAsync and AiTranslationService.RunAsync.
    public static IServiceCollection AddTrovamiCore(this IServiceCollection services, StoragePaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={paths.DatabasePath}"));
        services.AddSingleton<EmailSettingsService>();
        services.AddSingleton<EmailSender>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<PhotoStore>();
        services.AddSingleton<HouseholdService>();
        services.AddSingleton<LocationService>();
        services.AddSingleton<ItemService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<DeviceService>();
        services.AddSingleton<SyncService>();
        services.AddSingleton<AiNamingQueue>();
        services.AddSingleton<AiNamingService>();
        services.AddSingleton<AiTranslationService>();
        services.AddSingleton<AiKeywordGrouper>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<Scheduler>();
        services.AddSingleton<AccountRequestService>();
        services.AddSingleton<UserActivityService>();
        services.AddSingleton<AdminStatisticsService>();
        services.AddSingleton<HomepageStatsService>();
        services.AddSingleton<WhatsNewService>();
        return services;
    }
}
