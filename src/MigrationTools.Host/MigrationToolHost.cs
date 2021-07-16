using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using CommandLine;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.WorkerService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MigrationTools._EngineV1.Configuration;
using MigrationTools.Host.CommandLine;
using MigrationTools.Host.CustomDiagnostics;
using MigrationTools.Host.Services;
using MigrationTools.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace MigrationTools.Host
{
    public static class MigrationToolHost
    {
        public static IHostBuilder CreateDefaultBuilder(string[] args)
        {
            (var initOptions, var executeOptions) = ParseOptions(args);

            if (initOptions is null && executeOptions is null)
            {
                return null;
            }

            var hostBuilder = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
             .UseSerilog((hostingContext, services, loggerConfiguration) =>
             {
                 string logsPath = CreateLogsPath();
                 var logPath = Path.Combine(logsPath, "migration.log");
                 var logLevel = hostingContext.Configuration.GetValue<LogEventLevel>("LogLevel");
                 var levelSwitch = new LoggingLevelSwitch(logLevel);
                 loggerConfiguration
                     .MinimumLevel.ControlledBy(levelSwitch)
                     .ReadFrom.Configuration(hostingContext.Configuration)
                     .Enrich.FromLogContext()
                     .Enrich.WithMachineName()
                     .Enrich.WithProcessId()
                     .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Debug, theme: AnsiConsoleTheme.Code, outputTemplate:"[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
                     .WriteTo.ApplicationInsights(services.GetService<TelemetryClient>(), new CustomConverter(), LogEventLevel.Error)
                     .WriteTo.File(logPath, LogEventLevel.Verbose);
             })
             .ConfigureLogging((context, logBuilder) =>
             {
             })
             .ConfigureAppConfiguration(builder =>
             {
                 if (executeOptions is not null)
                 {
                     builder.AddJsonFile(executeOptions.ConfigFile);
                 }
             })
             .ConfigureServices((context, services) =>
             {
                 services.AddOptions();
                 services.Configure<EngineConfiguration>((config) =>
                 {
                     if(executeOptions is null)
                     {
                         return;
                     }

                     var sp = services.BuildServiceProvider();
                     var logger = sp.GetService<ILoggerFactory>().CreateLogger<EngineConfiguration>();
                     if (!File.Exists(executeOptions.ConfigFile))
                     {
                         logger.LogInformation("The config file {ConfigFile} does not exist, nor does the default 'configuration.json'. Use '{ExecutableName}.exe init' to create a configuration file first", executeOptions.ConfigFile, Assembly.GetEntryAssembly().GetName().Name);
                         throw new ArgumentException("missing configfile");
                     }
                     logger.LogInformation("Config Found, creating engine host");
                     var reader = sp.GetRequiredService<IEngineConfigurationReader>();
                     var parsed = reader.BuildFromFile(executeOptions.ConfigFile);
                     config.ChangeSetMappingFile = parsed.ChangeSetMappingFile;
                     config.FieldMaps = parsed.FieldMaps;
                     config.GitRepoMapping = parsed.GitRepoMapping;
                     config.Processors = parsed.Processors;
                     config.Source = parsed.Source;
                     config.Target = parsed.Target;
                     config.Version = parsed.Version;
                     config.workaroundForQuerySOAPBugEnabled = parsed.workaroundForQuerySOAPBugEnabled;
                     config.WorkItemTypeDefinition = parsed.WorkItemTypeDefinition;
                 });

                 // Application Insights
                 services.AddApplicationInsightsTelemetryWorkerService(new ApplicationInsightsServiceOptions { InstrumentationKey = "2d666f84-b3fb-4dcf-9aad-65de038d2772" });

                 // Services
                 services.AddTransient<IDetectOnlineService, DetectOnlineService>();
                 services.AddTransient<IDetectVersionService, DetectVersionService>();

                 // Config
                 services.AddSingleton<IEngineConfigurationBuilder, EngineConfigurationBuilder>();
                 services.AddTransient((provider) => provider.GetRequiredService<IEngineConfigurationBuilder>() as IEngineConfigurationReader);
                 services.AddTransient((provider) => provider.GetRequiredService<IEngineConfigurationBuilder>() as ISettingsWriter);

                 // Add Old v1Bits
                 services.AddMigrationToolServicesLegacy();
                 // New v2Bits
                 services.AddMigrationToolServices();

                 // Host Services
                 services.AddTransient<IStartupService, StartupService>();
                 if (initOptions is not null)
                 {
                     services.Configure<InitOptions>((opts) =>
                     {
                         opts.ConfigFile = initOptions.ConfigFile;
                         opts.Options = initOptions.Options;
                     });
                     services.AddHostedService<InitHostedService>();
                 }
                 if (executeOptions is not null)
                 {
                     services.Configure<NetworkCredentialsOptions>(cred =>
                     {
                         cred.Source = new Credentials
                         {
                             Domain = executeOptions.SourceDomain,
                             UserName = executeOptions.SourceUserName,
                             Password = executeOptions.SourcePassword
                         };
                         cred.Target = new Credentials
                         {
                             Domain = executeOptions.TargetDomain,
                             UserName = executeOptions.TargetUserName,
                             Password = executeOptions.TargetPassword
                         };
                     });
                     services.AddHostedService<ExecuteHostedService>();
                 }
             })
             .UseConsoleLifetime();
            return hostBuilder;
        }

        public static async Task RunMigrationTools(this IHostBuilder hostBuilder, string[] args)
        {
            var host = hostBuilder.Build();
            var startupService = host.InitializeMigrationSetup(args);
            if (startupService == null)
            {
                return;
            }
            await host.RunAsync();
            startupService.RunExitLogic();
        }

        private static string CreateLogsPath()
        {
            string exportPath;
            string assPath = Assembly.GetEntryAssembly().Location;
            exportPath = Path.Combine(Path.GetDirectoryName(assPath), "logs", DateTime.Now.ToString("yyyyMMddHHmmss"));
            if (!Directory.Exists(exportPath))
            {
                Directory.CreateDirectory(exportPath);
            }

            return exportPath;
        }

        private static (InitOptions init, ExecuteOptions execute) ParseOptions(string[] args)
        {
            InitOptions initOptions = null;
            ExecuteOptions executeOptions = null;
            Parser.Default.ParseArguments<InitOptions, ExecuteOptions>(args)
                            .WithParsed<InitOptions>(opts =>
                            {
                                initOptions = opts;
                            })
                            .WithParsed<ExecuteOptions>(opts =>
                            {
                                executeOptions = opts;
                            });
            return (initOptions, executeOptions);
        }
    }
}