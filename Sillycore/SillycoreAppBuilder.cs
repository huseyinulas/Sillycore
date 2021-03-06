﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Anetta.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;
using Sillycore.BackgroundProcessing;
using Sillycore.Domain.Abstractions;
using Sillycore.Domain.Objects.DateTimeProviders;
using Sillycore.Extensions;

namespace Sillycore
{
    public class SillycoreAppBuilder : ISillycoreAppBuilder
    {
        private static HttpClient _httpClient = new HttpClient();
        private static SillycoreAppBuilder _appBuilder;
        public static SillycoreAppBuilder Instance => _appBuilder ?? (_appBuilder = new SillycoreAppBuilder());

        private readonly List<Action> _beforeBuildTasks = new List<Action>();
        private readonly List<Action> _afterBuildTasks = new List<Action>();

        public InMemoryDataStore DataStore = new InMemoryDataStore();
        public IServiceCollection Services = new ServiceCollection();
        public IConfiguration Configuration { get; private set; }
        public ILoggerFactory LoggerFactory { get; private set; }

        internal SillycoreAppBuilder()
        {
            InitializeConfiguration();
            SetGlobalJsonSerializerSettings();
            InitializeLogger();
            InitializeDateTimeProvider();
            InitializeBackgroundJobManager();
        }

        public SillycoreApp Build()
        {
            foreach (var task in _beforeBuildTasks)
            {
                task.Invoke();
            }

            BuildServiceProvider();
            SillycoreApp.Instance = new SillycoreApp(DataStore);

            foreach (var task in _afterBuildTasks)
            {
                task.Invoke();
            }

            return SillycoreApp.Instance;
        }

        public void BeforeBuild(Action action)
        {
            _beforeBuildTasks.Add(action);
        }

        public void AfterBuild(Action action)
        {
            _afterBuildTasks.Add(action);
        }

        public SillycoreAppBuilder UseLocalTimes()
        {
            DataStore.Delete(Constants.DateTimeProvider);
            DataStore.Set(Constants.DateTimeProvider, new LocalDateTimeProvider());
            Services.TryAddSingleton(DataStore.Get<IDateTimeProvider>(Constants.DateTimeProvider));

            return this;
        }

        public SillycoreAppBuilder UseUtcTimes()
        {
            DataStore.Delete(Constants.DateTimeProvider);
            DataStore.Set(Constants.DateTimeProvider, new UtcDateTimeProvider());
            Services.TryAddSingleton(DataStore.Get<IDateTimeProvider>(Constants.DateTimeProvider));

            return this;
        }

        public SillycoreAppBuilder UseConfigServer(string configServerAddress, string appName, int defaultReloadTimeInMiliseconds = 180000)
        {
            string environment = Configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "development";

            if (environment.ToLowerInvariant() == "development" || environment.ToLowerInvariant() == "ci")
            {
                return this;
            }

            DataStore.Set(Constants.ConfigServerAddress, configServerAddress);
            DataStore.Set(Constants.ConfigServerAppName, appName);
            DataStore.Set(Constants.ConfigServerReloadTimeInMiliseconds, defaultReloadTimeInMiliseconds);
            DataStore.Set(Constants.ConfigServerReloadTimer, new Timer(ReloadConfigurationFromConfigServer, null, defaultReloadTimeInMiliseconds, defaultReloadTimeInMiliseconds));

            ReloadConfigurationFromConfigServer(null);

            return this;
        }

        public SillycoreAppBuilder ConfigureServices(Action<IServiceCollection, IConfiguration> action)
        {
            action.Invoke(Services, Configuration);

            return this;
        }

        private void SetGlobalJsonSerializerSettings()
        {
            IDateTimeProvider dateTimeProvider = DataStore.Get<IDateTimeProvider>(Constants.DateTimeProvider);

            if (dateTimeProvider != null && dateTimeProvider.Kind == DateTimeKind.Local)
            {
                SillycoreApp.JsonSerializerSettings = GetJsonSerializerSettings(DateTimeZoneHandling.Local);
            }
            else if (dateTimeProvider != null && dateTimeProvider.Kind == DateTimeKind.Utc)
            {
                SillycoreApp.JsonSerializerSettings = GetJsonSerializerSettings(DateTimeZoneHandling.Utc);
            }
            else
            {
                SillycoreApp.JsonSerializerSettings = GetJsonSerializerSettings();
            }
        }

        private JsonSerializerSettings GetJsonSerializerSettings(DateTimeZoneHandling dateTimeZoneHandling = DateTimeZoneHandling.Utc)
        {
            var settings = new JsonSerializerSettings()
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                DateTimeZoneHandling = dateTimeZoneHandling
            };

            settings.Converters.Add(new StringEnumConverter { CamelCaseText = true });
            return settings;
        }

        private void BuildServiceProvider()
        {
            if (DataStore.Get(Constants.ServiceProvider) == null)
            {
                IServiceProvider serviceProvider = Services.BuildAnettaServiceProvider();
                DataStore.Set(Constants.ServiceProvider, serviceProvider);
            }
        }

        private void InitializeLogger()
        {
            IConfigureOptions<LoggerFilterOptions> options = new ConfigureOptions<LoggerFilterOptions>(filterOptions =>
            {
                filterOptions.MinLevel = LogLevel.Trace;
            });

            var loggerFactory = new LoggerFactory();
            DataStore.Set(Constants.LoggerFactory, loggerFactory);
            loggerFactory.AddConsole(Configuration.GetSection("Logging"));

            LoggerFactory = loggerFactory;

            Services.AddOptions();
            Services.TryAdd(ServiceDescriptor.Singleton<ILoggerFactory>(loggerFactory));
            Services.TryAdd(ServiceDescriptor.Singleton(typeof(ILogger<>), typeof(Logger<>)));
            Services.TryAddEnumerable(ServiceDescriptor.Singleton(options));
        }

        private void InitializeConfiguration()
        {
            Configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", true, true)
                .AddJsonFile("appsettings.ci.json", true, true)
                .AddJsonFile("appsettings.test.json", true, true)
                .AddJsonFile("appsettings.staging.json", true, true)
                .AddJsonFile("appsettings.production.json", true, true)
                .AddEnvironmentVariables()
                .Build();

            if (!String.IsNullOrWhiteSpace(Configuration["ASPNETCORE_ENVIRONMENT"]))
            {
                Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", Configuration["ASPNETCORE_ENVIRONMENT"]);
            }

            Services.TryAdd(ServiceDescriptor.Singleton(Configuration));

            DataStore.Set(Constants.Configuration, Configuration);
        }

        private void InitializeDateTimeProvider()
        {
            DataStore.Set(Constants.DateTimeProvider, new UtcDateTimeProvider());
        }

        private void InitializeBackgroundJobManager()
        {
            Assembly ass = Assembly.GetEntryAssembly();

            foreach (TypeInfo ti in ass.DefinedTypes)
            {
                if (ti.ImplementedInterfaces.Contains(typeof(IJob)))
                {
                    Services.AddTransient(ti);
                }
            }

            Services.AddSingleton<BackgroundJobManager>();
        }

        private void ReloadConfigurationFromConfigServer(object state)
        {
            Timer timer = DataStore.Get<Timer>(Constants.ConfigServerReloadTimer);
            timer.Change(-1, -1);

            string configServerAddress = DataStore.Get<string>(Constants.ConfigServerAddress);
            string appName = DataStore.Get<string>(Constants.ConfigServerAppName);
            string environment = Configuration["ASPNETCORE_ENVIRONMENT"] ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "development";
            string url = $"{configServerAddress?.TrimEnd('/')}/{appName}/{environment.ToLowerInvariant()}/master/config.json";

            try
            {
                Dictionary<string, string> configurations = JsonConvert.DeserializeObject<Dictionary<string, string>>(_httpClient.GetStringAsync(url).Result);

                if (configurations.HasElements())
                {
                    foreach (var pair in configurations)
                    {
                        Configuration[pair.Key] = pair.Value;
                    }
                }

                DataStore.Set(Constants.ConfigServerFirstLoadSucceeded, true);
            }
            catch (Exception e)
            {
                ILogger<SillycoreAppBuilder> logger = LoggerFactory.CreateLogger<SillycoreAppBuilder>();
                logger.LogError(e, $"There was a problem while loading config from config server:{url}");

                if (!DataStore.Get<bool>(Constants.ConfigServerFirstLoadSucceeded))
                {
                    throw new ApplicationException($"There was a problem while loading config from config server:{url}", e);
                }
            }

            int reloadTime = DataStore.Get<int>(Constants.ConfigServerReloadTimeInMiliseconds);
            timer.Change(reloadTime, reloadTime);
        }
    }
}