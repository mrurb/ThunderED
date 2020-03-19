﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ThunderED.Classes;
using ThunderED.Helpers;
using ThunderED.Modules.Sub;

namespace ThunderED
{
    internal partial class Program
    {
        private static Timer _timer;

        private static async Task Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;

            ulong replyChannelId = 0;
            if (args.Length > 0)
                ulong.TryParse(args[0], out replyChannelId);

            // var x = string.IsNullOrWhiteSpace("");

            // var ssss = new List<JsonZKill.ZkillOnly>().Count(a => a.killmail_id == 0);
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

            if (replyChannelId > 0)
                LogHelper.WriteConsole($"Launch after restart");


            APIHelper.Prepare();
            await LogHelper.LogInfo($"ThunderED v{VERSION} is running!").ConfigureAwait(false);
            //load database provider
            var rs = await SQLHelper.LoadProvider();
            if (!string.IsNullOrEmpty(rs))
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

            await SQLHelper.InitializeBackup();

            //load language
            await LM.Load();
            //load injected settings
            await SettingsManager.UpdateInjectedSettings();
            //load APIs
            await APIHelper.DiscordAPI.Start();

            while (!APIHelper.DiscordAPI.IsAvailable)
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

                return;
            }

            //initiate core timer
            _timer = new Timer(TickCallback, new AutoResetEvent(true), 100, 100);

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = false;
                _timer?.Dispose();
                APIHelper.DiscordAPI.Stop();
            };

            AppDomain.CurrentDomain.UnhandledException += async (sender, eventArgs) =>
            {
                await LogHelper.LogEx($"[UNHANDLED EXCEPTION]", (Exception)eventArgs.ExceptionObject);
                await LogHelper.LogWarning($"Consider restarting the service...");
            };

            if (replyChannelId > 0)
                await APIHelper.DiscordAPI.SendMessageAsync(replyChannelId, LM.Get("sysRestartComplete"));


            while (true)
            {

                if (!SettingsManager.Settings.Config.RunAsServiceCompatibility)
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey();
                        if (key.Key == ConsoleKey.Escape)
                        {
                            await Shutdown();
                            return;
                        }
                    }
                }

                    /*if (!SettingsManager.Settings.Config.RunAsServiceCompatibility)
                    {
                        var command = Console.ReadLine();
                        var arr = command?.Split(" ");
                        if ((arr?.Length ?? 0) == 0) continue;
                        switch (arr[0])
                        {
                            case "help":
                                Console.WriteLine("List of available commands:");
                                Console.WriteLine(" quit    - quit app");
                                Console.WriteLine(" flushn  - flush all notification IDs from database");
                                Console.WriteLine(" getnurl - display notification auth url");
                                Console.WriteLine(" flushcache - flush all cache from database");
                                Console.WriteLine(" token [ID] - refresh and display EVE character token from database");
                                break;
                            case "token":
                                if (arr.Length == 1) continue;
                                if (!long.TryParse(arr[1], out var id))
                                    continue;
                                var rToken = await SQLHelper.GetRefreshTokenDefault(id);
                                Console.WriteLine(await APIHelper.ESIAPI
                                    .RefreshToken(rToken, SettingsManager.Settings.WebServerModule.CcpAppClientId, SettingsManager.Settings.WebServerModule.CcpAppSecret));
                                break;
                        }

                        await Task.Delay(10);
                    }
                    else                 */
                    await Task.Delay(10);
                if(_confirmClose) return;
            }
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

        internal static volatile bool IsClosing = false;
        private static volatile bool _canClose = false;
        private static volatile bool _confirmClose = false;

        internal static async Task Shutdown()
        {
            try
            {
                await LogHelper.LogInfo($"Server shutdown requested...");
                APIHelper.DiscordAPI.Stop();
                IsClosing = true;
                while (!_canClose || !TickManager.AllModulesReadyToClose())
                {
                    await Task.Delay(10);
                }

                await LogHelper.LogInfo($"Server shutdown complete!");

            }
            finally
            {
                _confirmClose = true;
            }

            return;
        }

        public static async Task Restart(ulong channelId)
        {
            try
            {
                await LogHelper.LogInfo($"Server restart requested...");
                APIHelper.DiscordAPI.Stop();
                IsClosing = true;
                while (!_canClose || !TickManager.AllModulesReadyToClose())
                {
                    await Task.Delay(10);
                }

                var file = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location),
                    $"Restarter{(SettingsManager.IsLinux ? null : ".exe")}");

                var start = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    CreateNoWindow = false,
                    FileName = file,
                    //Arguments = channelId.ToString(),
                    WorkingDirectory = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)),
                };
                start.ArgumentList.Add(channelId.ToString());

                await LogHelper.LogInfo("Starting restarter...");
                using var proc = new Process {StartInfo = start};
                proc.Start();
            }
            catch (Exception ex)
            {
                await LogHelper.LogEx("Restart", ex);

            }
            finally
            {
                _confirmClose = true;
            }
        }
    }
}
