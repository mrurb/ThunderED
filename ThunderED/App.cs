﻿using System;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

using ThunderED.API;
using ThunderED.Classes;
using ThunderED.Helpers;

namespace ThunderED
{
    public class App
    {
        private static Timer _timer;
        private static NamedPipeClientStream pipe;
        private readonly DiscordAPI discordAPI;
        private readonly ESIAPI eSIAPI;
        private readonly ZKillAPI zKillAPI;
        private readonly FleetUpAPI fleetUpAPI;

        public App(DiscordAPI discordAPI , ESIAPI eSIAPI, ZKillAPI zKillAPI, FleetUpAPI fleetUpAPI)
        {
            this.discordAPI = discordAPI;
            this.eSIAPI = eSIAPI;
            this.zKillAPI = zKillAPI;
            this.fleetUpAPI = fleetUpAPI;
        }

        public async Task RunAsync(string[] args, string VERSION)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentUICulture = CultureInfo.InvariantCulture;
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainOnProcessExit;

            ulong replyChannelId = 0;
            if (args.Length > 0)
                ulong.TryParse(args[0], out replyChannelId);

            // var x = string.IsNullOrWhiteSpace("");

            // var ssss = new List<JsonZKill.ZkillOnly>().Count(a => a.killmail_id == 0);

            if (replyChannelId > 0)
                LogHelper.WriteConsole($"Launch after restart");

            //restart logix
            await Task.Factory.StartNew(async () =>
            {
                if (pipe == null)
                {
                    pipe = new NamedPipeClientStream(".", "ThunderED.Restart.Pipe", PipeDirection.In);
                    await pipe.ConnectAsync();
                }
                if (!pipe.IsConnected || pipe.ReadByte() == 0) return;
                await LogHelper.LogInfo("SIGTERM received! Shutdown app...");
                await Shutdown();
            });

            APIHelper.Prepare(discordAPI, eSIAPI, zKillAPI, fleetUpAPI);
            await LogHelper.LogInfo($"ThunderED v{VERSION} is running!").ConfigureAwait(false);
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

                return;
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

                return;
            }

            //initiate core timer
            _timer = new Timer(TickCallback, new AutoResetEvent(true), 100, 100);

            Console.CancelKeyPress += async (sender, e) =>
            {
                e.Cancel = false;
                await Shutdown();
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
                    try
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
                    catch
                    {
                        // ignored
                    }

                    await Task.Delay(10);
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
                if (_confirmClose) return;
            }
        }

        private static async void CurrentDomainOnProcessExit(object sender, EventArgs e)
        {
            await Shutdown();
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

        internal static async Task Shutdown(bool isRestart = false)
        {
            try
            {
                await LogHelper.LogInfo(isRestart ? "Server restart requested..." : $"Server shutdown requested...");
                APIHelper.StopServices();
                IsClosing = true;
                while (!_canClose || !TickManager.AllModulesReadyToClose())
                {
                    await Task.Delay(10);
                }

                await LogHelper.LogInfo(isRestart ? "Server is ready for restart" : "Server shutdown complete");
                Environment.Exit(isRestart ? 1001 : 1002);

            }
            finally
            {
                _confirmClose = true;
            }

            return;
        }

        public static async Task Restart(ulong channelId)
        {
            await Shutdown(true);
            /* try
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
             }*/
        }
    }
}
