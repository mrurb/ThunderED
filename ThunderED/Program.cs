using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using ThunderED.API;
using ThunderED.Classes;
using ThunderED.Classes.Enums;
using ThunderED.Helpers;
using ThunderED.Modules.Sub;
using ThunderED.PollyPolicies;

namespace ThunderED
{
    internal partial class Program
    {


        private static async Task Main(string[] args)
        {
            await SetupSettingsAsync();

            var services = ConfigureServicesAsync();

            var serviceProvider = services.BuildServiceProvider();

            await serviceProvider.GetService<App>().RunAsync(args, VERSION);
        }

        internal static  IServiceCollection ConfigureServicesAsync()
        {

            IServiceCollection services = new ServiceCollection();

            services.AddSingleton<App>();

            services.AddScoped<DiscordAPI>()
                .AddScoped<ESIAPI>()
                .AddScoped<ZKillAPI>()
                .AddScoped<FleetUpAPI>();

            //Using microsoft.logging could be a future improvment to support things like application insights
            //services.AddLogging();

            services.AddHttpClient<ZkillService>()
                .AddPolicyHandler(ZKillResilicencyStrategy.DefineAndRetrieveResiliencyStrategy());

            return services;
        }


        internal static async Task SetupSettingsAsync()
        {
            if (!File.Exists(SettingsManager.FileSettingsPath))
            {
                await LogHelper.LogError("Please make sure you have settings.json file in bot folder! Create it and fill with correct settings.");
                try
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Please make sure you have settings.json file in bot folder! Create it and fill with correct settings.");
                    Console.ReadKey();
                }
                catch
                {
                    // ignored
                }

                return;
            }

            //load settings
            var result = await SettingsManager.Prepare();
            if (!string.IsNullOrEmpty(result))
            {
                await LogHelper.LogError(result);
                try
                {
                    Console.ReadKey();
                }
                catch
                {
                    // ignored
                }

                return;
            }
        }

    }
    public class ExternalAccess
    {
        private static Timer _timer;

        public static string GetVersion()
        {
            return Program.VERSION;
        }

        public static async Task<bool> Start()
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            var services = Program.ConfigureServicesAsync();

            var serviceProvider = services.BuildServiceProvider();

            var discordAPI = serviceProvider.GetService<DiscordAPI>();
            var eSIAPI = serviceProvider.GetService<ESIAPI>();
            var zKillAPI = serviceProvider.GetService<ZKillAPI>();
            var fleetUpAPI = serviceProvider.GetService<FleetUpAPI>();

            APIHelper.Prepare(discordAPI, eSIAPI, zKillAPI, fleetUpAPI);
            await LogHelper.LogInfo($"ThunderED v{Program.VERSION} is running!").ConfigureAwait(false);
            //load database provider
            var rs = await SQLHelper.LoadProvider();
            if (!string.IsNullOrEmpty(rs))
            {
                await LogHelper.LogError(rs);
                try
                {
                    Console.ReadKey();
                }
                catch
                {
                    // ignored
                }

                return false;
            }

            await SQLHelper.InitializeBackup();

            //load language
            await LM.Load();
            //load injected settings
            await SettingsManager.UpdateInjectedSettings();
            //load APIs
            await APIHelper.DiscordAPI.Start();

            while (!APIHelper.IsDiscordAvailable)
            {
                await Task.Delay(10);
            }

            if (APIHelper.DiscordAPI.GetGuild() == null)
            {
                await LogHelper.LogError("[CRITICAL] DiscordGuildId - Discord guild not found!");
                try
                {
                    Console.ReadKey();
                }
                catch
                {
                    // ignored
                }

                return false;
            }

            //initiate core timer
            _timer = new Timer(TickCallback, new AutoResetEvent(true), 100, 100);

            return true;
        }

        internal static volatile bool IsClosing = false;
        private static volatile bool _canClose = false;

        public static async Task Shutdown(bool isRestart = false)
        {
            try
            {
                await LogHelper.LogInfo(isRestart ? "Bot restart requested..." : $"Bot shutdown requested...");
                APIHelper.StopServices();
                IsClosing = true;
                while (!_canClose || !TickManager.AllModulesReadyToClose())
                {
                    await Task.Delay(10);
                }

                await LogHelper.LogInfo(isRestart ? "Bot is ready for restart" : "Bot shutdown complete");
            }
            catch
            {
                // ignore
            }

            return;
        }

        private static void TickCallback(object state)
        {
            _canClose = IsClosing;
            if (_canClose || IsClosing)
            {
                if (_timer != null)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
                return;
            }

            TickManager.Tick(state);

            if (_canClose)
            {
                _timer?.Dispose();
                _timer = null;
            }

        }

        public static async Task<WebQueryResult> ProcessCallback(string queryStringValue, CallbackTypeEnum type,
            string ip, WebAuthUserData data)
        {
            return await WebServerModule.ProcessWebCallbacks(queryStringValue, type, ip, data);
        }
    }
}
