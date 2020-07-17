using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Reflection;
using System.Linq;
using NLog;
using IrcMessageSharp;
using System.IO;
using System.Threading;
using System.Collections.Concurrent;

using Timer = System.Timers.Timer;
using System.Diagnostics;

namespace DiscordIrcBridge
{
    public class IrcServer : IDisposable
    {
        private Dictionary<string, IrcCommandHandler> preAuthCommands = new Dictionary<string, IrcCommandHandler>();
        private Dictionary<string, IrcCommandHandler> preCapCommands = new Dictionary<string, IrcCommandHandler>();
        private Dictionary<string, IrcCommandHandler> postCapCommands = new Dictionary<string, IrcCommandHandler>();

        private Logger logger = LogManager.GetLogger("IrcServer");
        private TcpClient client;

        private bool running = false;
        private ConcurrentQueue<string> messageQueue = new ConcurrentQueue<string>();
        private ConcurrentQueue<string> priorityMessageQueue = new ConcurrentQueue<string>();

        private Timer timeoutTimer;
        private bool missedPing = false;

        private readonly TcpListener listener;

        private Config config;

        public delegate void IrcCommandHandler(IrcMessage message);


        public IrcServer(IPAddress address, Config config)
        {
            if (string.IsNullOrWhiteSpace(config.Hostname))
            {
                config.Hostname = "irc.discord.com";
            }
            this.config = config;
            Hostname = config.Hostname;
            listener = new TcpListener(address, config.Port);
            listener.Server.SendBufferSize = 8092;
        }

        public void RegisterCommands(object commandHandler)
        {
            var methods = commandHandler.GetType().GetMethods()
                    .Where(m => m.GetCustomAttributes(typeof(IrcCommandAttribute), false).Length > 0)
                    .ToList();

            foreach (var m in methods)
            {
                var a = m.GetCustomAttribute(typeof(IrcCommandAttribute)) as IrcCommandAttribute;
                if (a.PreAuth)
                {
                    if (preAuthCommands.ContainsKey(a.Command))
                        logger.Warn($"PreAuth command '{a.Command}' already exists! Overwriting!");

                    var d = Delegate.CreateDelegate(typeof(IrcCommandHandler), commandHandler, m) as IrcCommandHandler;
                    if (d != null)
                    {
                        preAuthCommands[a.Command] = d;
                        logger.Debug($"Registered PreAuth command '{a.Command}' from '{m.DeclaringType.FullName}'");
                    }
                }

                if (a.PostAuth)
                {
                    if (preCapCommands.ContainsKey(a.Command))
                        logger.Warn($"PreCap command '{a.Command}' already exists! Overwriting!");

                    var d = Delegate.CreateDelegate(typeof(IrcCommandHandler), commandHandler, m) as IrcCommandHandler;
                    if (d != null)
                    {
                        preCapCommands[a.Command] = d;
                        logger.Debug($"Registered PreCap command '{a.Command}' from '{m.DeclaringType.FullName}'");
                    }
                }

                if (a.PostCaps)
                {
                    if (postCapCommands.ContainsKey(a.Command))
                        logger.Warn($"PostCap command '{a.Command}' already exists! Overwriting!");

                    var d = Delegate.CreateDelegate(typeof(IrcCommandHandler), commandHandler, m) as IrcCommandHandler;
                    if (d != null)
                    {
                        postCapCommands[a.Command] = d;
                        logger.Debug($"Registered PostCap command '{a.Command}' from '{m.DeclaringType.FullName}'");
                    }
                }
            }
        }

        public async void Start()
        {
            listener.Start();
            logger.Info($"An IRC server is listening on {((IPEndPoint)listener.LocalEndpoint).Address}:{((IPEndPoint)listener.LocalEndpoint).Port}...");
            client = listener.AcceptTcpClient();

            logger.Info($"Client connected! Starting message loop!");

            timeoutTimer = new Timer(12000)
            {
                AutoReset = false
            };

            timeoutTimer.Elapsed += (s, e) =>
            {
                PriorityEnqueueMessage($"PING :{Hostname}");
                if (!missedPing)
                {
                    timeoutTimer.Stop();
                    timeoutTimer.Start();
                    missedPing = true;
                }
                else
                {
                    logger.Fatal("Client timed out");
                    Stop();
                }
            };
            timeoutTimer.Start();

            running = true;
            using (var stream = client.GetStream())
            {
                byte[] buffer = new byte[4096];
                int pos = 0;
                string line;
                while (client.Connected && running)
                {
                    while (priorityMessageQueue.Count > 0)
                    {
                        try
                        {
                            if (priorityMessageQueue.TryDequeue(out string l))
                            {
                                logger.Trace($"tx: {l}");
                                stream.Write(Encoding.UTF8.GetBytes(l + "\r\n"));
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error($"Exception ({e.GetType().FullName}) thrown while reading from client: {e.Message}");
                            Stop();
                        }
                    }

                    var outMsgCount = 0;
                    while (messageQueue.Count > 0 && outMsgCount <= config.OutgoingMessageLimit)
                    {
                        try
                        {
                            if (messageQueue.TryDequeue(out string l))
                            {
                                logger.Trace($"tx: {l}");
                                stream.Write(Encoding.UTF8.GetBytes(l + "\r\n"));
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Error($"Exception ({e.GetType().FullName}) thrown while reading from client: {e.Message}");
                            Stop();
                        }
                        outMsgCount++;
                    }

                    while (stream.DataAvailable)
                    {
                        try
                        {
                            stream.Read(buffer, Math.Min(pos++, buffer.Length - 1), 1);
                        }
                        catch (Exception e)
                        {
                            logger.Error($"Exception ({e.GetType().FullName}) thrown while reading from client: {e.Message}");
                            Stop();
                        }

                        var current = Encoding.UTF8.GetString(buffer, 0, pos - 1);

                        if (pos > 1
                            && buffer[pos - 1] == '\n')
                        {
                            line = Encoding.UTF8.GetString(buffer, 0, pos - 1).Replace("\r", "").Replace("\n", "");
                            if (IrcMessage.TryParse(line, out IrcMessage message) && !string.IsNullOrWhiteSpace(line))
                            {
                                logger.Trace($"rx: {line}");
                                switch (CurrentStage)
                                {
                                    case AuthStages.PreAuthentication:
                                        if (preAuthCommands.ContainsKey(message.Command))
                                            preAuthCommands[message.Command](message);
                                        else
                                            EnqueueMessage($"421 {message.Command} :Unknown command");
                                        break;
                                    case AuthStages.Authenticated:
                                        if (preCapCommands.ContainsKey(message.Command))
                                            preCapCommands[message.Command](message);
                                        else
                                            EnqueueMessage($"421 {message.Command} :Unknown command");
                                        break;
                                    case AuthStages.CapsNegotiated:
                                    case AuthStages.All:
                                        if (postCapCommands.ContainsKey(message.Command))
                                            postCapCommands[message.Command](message);
                                        else
                                            EnqueueMessage($"421 {message.Command} :Unknown command");
                                        break;
                                    default:
                                        EnqueueMessage($"421 {message.Command} :Unknown command");
                                        break;
                                }
                            }

                            buffer = new byte[4096];
                            pos = 0;
                        }
                    }

                    Thread.Sleep(25);
                }

                logger.Info("Message loop ended. Shutting down.");
            }
        }

        public void EnqueueMessage(string message)
        {
            logger.Trace($"q: {message}");
            messageQueue.Enqueue(message);
        }

        public void PriorityEnqueueMessage(string message)
        {
            logger.Trace($"q: {message}");
            priorityMessageQueue.Enqueue(message);
        }

        public void Stop()
        {
            logger.Info("IRC Server stopping...");
            running = false;
            Dispose();
        }

        public void Ping()
        {
            missedPing = false;
            timeoutTimer.Stop();
            timeoutTimer.Start();
        }

        public void Dispose()
        {
            listener.Stop();
        }

        public string Hostname { get; private set; }
        public AuthStages CurrentStage { get; set; } = AuthStages.PreAuthentication;
    }
}
