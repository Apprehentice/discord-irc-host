using Discord.WebSocket;
using Newtonsoft.Json;
using NLog;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordIrcBridge
{
    class Program
    {
        static void Main(string[] args)
        {
            MainAsync().GetAwaiter().GetResult();
        }

        static async Task MainAsync()
        {
            var logger = LogManager.GetLogger("IrcHost");
            if (!File.Exists("./token.txt"))
            {
                logger.Fatal("token.txt not found!");
                logger.Fatal("Please create a file named token.txt and populate it with your bot token.");
                return;
            }
            var token = File.ReadAllText("token.txt");

            Config config;
            if (File.Exists("./config.json"))
            {
                config = JsonConvert.DeserializeObject<Config>(File.ReadAllText("./config.json"));
            }
            else
            {
                config = new Config();
                File.WriteAllText("./config.json", JsonConvert.SerializeObject(config));
            }

            if (string.IsNullOrWhiteSpace(config.Hostname))
            {
                logger.Fatal("Invalid hostname");
                return;
            }

            var dClient = new DiscordSocketClient(new DiscordSocketConfig
            {
                LogLevel = Discord.LogSeverity.Debug,
                MessageCacheSize = 128,
                AlwaysDownloadUsers = true
            });
            await dClient.LoginAsync(Discord.TokenType.Bot, token);
            await dClient.StartAsync();

            var readyEvent = new ManualResetEvent(false);
            dClient.Ready += async () => // Async to avoid ugly `return Task.CompletedTask`
            {
                readyEvent.Set();
            };

            readyEvent.WaitOne();

            var server = new IrcServer(IPAddress.Any, config);
            var translator = new IrcDiscordTranslator(dClient, server, config);

            server.RegisterCommands(translator);

            try
            {
                server.Start();
            }
            catch (Exception e)
            {
                logger.Fatal($"An uncaught exception was thrown in the IRC server\n\t{e.GetType().FullName}:\n\t\t{e.Message}");
            }
        }
    }
}
