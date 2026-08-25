using System;
using System.Configuration;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Owin;
using Owin;
using SaviSchedular.Services;

[assembly: OwinStartup(typeof(SaviSchedular.Startup))]
namespace SaviSchedular
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            ConfigureHangfire(app);
        }

        private void ConfigureHangfire(IAppBuilder app)
        {
            try
            {
                int pollSeconds = int.TryParse(
                    ConfigurationManager.AppSettings["HangfirePollIntervalSeconds"],
                    out int ps) ? ps : 30;

                // Hangfire uses SaviSchedular DB for its own tables
                GlobalConfiguration.Configuration
                    .UseSqlServerStorage("SaviSchedularConnection", new SqlServerStorageOptions
                    {
                        CommandBatchMaxTimeout       = TimeSpan.FromMinutes(5),
                        SlidingInvisibilityTimeout   = TimeSpan.FromMinutes(5),
                        QueuePollInterval            = TimeSpan.FromSeconds(pollSeconds),
                        UseRecommendedIsolationLevel = true,
                        DisableGlobalLocks           = true,
                        SchemaName                   = "dbo"
                    });

                app.UseHangfireServer(new BackgroundJobServerOptions
                {
                    WorkerCount = 4  // 4 concurrent workers
                });

                app.UseHangfireDashboard("/hangfire", new DashboardOptions
                {
                    Authorization = new[] { new HangfireNoAuthFilter() }
                });

                // Global config cache warm-up (DB se settings load karo)
                GlobalConfigService.Reload();

                // DB se sabhi active jobs load karo aur Hangfire mein register karo
                SchoolSchedulerService.RegisterAllJobsFromDb();

                Console.WriteLine("[SaviSchedular] ✓ Hangfire + Universal Scheduler initialized successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] HANGFIRE INIT ERROR: {ex.Message}");
            }
        }
    }

    public class HangfireNoAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
    {
        public bool Authorize(Hangfire.Dashboard.DashboardContext context) => true;
    }
}

