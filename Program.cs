using Discord.WebSocket;
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
            if (!File.Exists("./token.txt"))
            {
                LogManager.GetLogger("IrcBridge").Fatal("token.txt not found!");
                LogManager.GetLogger("IrcBridge").Fatal("Please create a file named token.txt and populate it with your bot token.");
                return;
            }
            var token = File.ReadAllText("token.txt");

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

            var server = new IrcServer(IPAddress.Any, 6667);
            var translator = new IrcDiscordTranslator(dClient, server);

            server.RegisterCommands(translator);
            server.Start();
        }
    }
}
