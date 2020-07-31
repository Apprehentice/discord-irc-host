using Discord;
using Discord.Net;
using Discord.Rest;
using Discord.WebSocket;
using IrcMessageSharp;
using Newtonsoft.Json;
using NLog;
using NLog.LayoutRenderers.Wrappers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordIrcBridge
{
    public partial class IrcDiscordTranslator
    {
        DiscordSocketClient client;
        DiscordRestClient restClient;
        IGuild guild;
        IrcServer server;

        HandshakeFlags handshakeStatus = HandshakeFlags.None;

        [Flags]
        private enum HandshakeFlags
        {
            None = 0,
            User = 1 << 1,
            Nick = 1 << 2,
            Pass = 1 << 3,
            Caps = 1 << 4,
            All = ~0
        }

        private bool capLocked = false;
        private string nick;

        private Mutex dmMutex;
        private bool handleDms;

        private Dictionary<ulong, IrcChannel> joinedChannels = new Dictionary<ulong, IrcChannel>();
        private Dictionary<string, ulong> nickLookupDict = new Dictionary<string, ulong>();
        private Capabilities currentCapabilities = new Capabilities();

        private Dictionary<ulong, EmbedBuilder> currentEmbeds = new Dictionary<ulong, EmbedBuilder>();

        private List<ulong> bans = new List<ulong>();

        private string userModes = "";

        private Config config;

        private Logger logger = LogManager.GetLogger("Bridge");

        public IrcDiscordTranslator(DiscordSocketClient client, IrcServer server, Config config)
        {
            this.client = client;
            this.server = server;
            this.config = config;

            var token = File.ReadAllText("./token.txt");
            restClient = new DiscordRestClient();
            restClient.LoginAsync(TokenType.Bot, token);

            dmMutex = new Mutex(true, token, out handleDms);

            client.MessageReceived += Client_MessageReceived;
            client.ChannelUpdated += Client_ChannelUpdated;
            client.RoleUpdated += Client_RoleUpdated;
            client.GuildMemberUpdated += Client_GuildMemberUpdated;
            client.UserVoiceStateUpdated += Client_UserVoiceStateUpdated;

            client.UserJoined += Client_UserJoined;
            client.UserLeft += Client_UserLeft;
            client.UserBanned += Client_UserBanned;
            client.UserUnbanned += Client_UserUnbanned;

            client.ReactionAdded += Client_ReactionAdded;
            client.ReactionRemoved += Client_ReactionRemoved;

            client.MessageDeleted += Client_MessageDeleted;
            client.MessageUpdated += Client_MessageUpdated;

            client.UserIsTyping += Client_UserIsTyping;
        }

        private async Task Client_UserIsTyping(SocketUser user, ISocketMessageChannel channel)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            var guildChannel = channel as IGuildChannel;
            if (guildChannel == null)
                return;

            var nick = getNickById(user.Id);
            server.EnqueueMessage($"@+typing=active :{nick}!{user.Id}@discord.com TAGMSG #{guildChannel.GetIrcSafeName()}");
        }

        private async Task Client_MessageUpdated(Cacheable<IMessage, ulong> oldMsg, SocketMessage newMsg, ISocketMessageChannel channel)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (!oldMsg.HasValue || oldMsg.Value.Content == newMsg.Content)
                return;

            var textChannel = newMsg.Channel as ITextChannel;
            if (textChannel == null)
                return;

            var guildUser = await textChannel.Guild.GetUserAsync(newMsg.Author.Id);

            if (!joinedChannels.ContainsKey(channel.Id))
                return;

            var isPrivateMessage = (newMsg.Channel as IPrivateChannel) != null;
            if (!handleDms && isPrivateMessage)
                return;

            string target;
            if (isPrivateMessage)
            {
                target = nick;
            }
            else
            {
                target = "#" + newMsg.Channel.Name;
            }

            string userNick;
            string userUser;
            if (!newMsg.Author.IsWebhook)
            {
                userNick = getNickById(newMsg.Author.Id);
                userUser = newMsg.Author.Id.ToString();
            }
            else
            {
                userNick = newMsg.Author.GetIrcSafeName();
                userUser = "webhook";
            }

            var tagsList = new Dictionary<string, string>();
            if (currentCapabilities.message_tags)
            {
                if (newMsg.Author.IsBot)
                    tagsList["discord.com/bot"] = "";


                if (guildUser != null)
                {
                    foreach (var tag in config.RoleTags)
                    {
                        if (!guildUser.RoleIds.Contains(tag.Key))
                            continue;

                        if (string.IsNullOrWhiteSpace(tag.Value))
                            return;

                        tagsList[tagEscape(tag.Value)] = "";
                    }
                }

                tagsList["discord.com/user"] = newMsg.Author.Id.ToString();
            }

            var isAction = !newMsg.Content.Contains('\n') && Regex.IsMatch(newMsg.Content, @"_.*_");

            var lines = newMsg.Content.Split('\n');
            var lineNumber = 1;
            foreach (var line in lines)
            {
                if (currentCapabilities.message_tags)
                {
                    if (lines.Length > 1)
                        tagsList["+reply"] = newMsg.Id.ToString() + $"-{lineNumber++}";
                    else
                        tagsList["+reply"] = newMsg.Id.ToString();
                }

                string tags = "@";
                foreach (var tag in tagsList)
                {
                    var val = string.IsNullOrWhiteSpace(tag.Value) ? "" : $"={tag.Value}";
                    tags += tagEscape($"{tag.Key}{val}") + ";";
                }

                var msgContent = parseDiscordMentions(line.Replace("\r", ""));
                if (isAction)
                {
                    var actionMatch = Regex.Match(msgContent, @"_(?<content>.*)_");
                    msgContent = $"ACTION {actionMatch.Groups["content"].Value}";
                }
                server.EnqueueMessage($"{tags} :{userNick}!{userUser}@discord.com EDITMSG {target} :{msgContent}");
            }
        }

        private async Task Client_MessageDeleted(Cacheable<IMessage, ulong> message, ISocketMessageChannel channel)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (!joinedChannels.ContainsKey(channel.Id))
                return;

            var ircChan = joinedChannels[channel.Id];
            server.EnqueueMessage($"@+discord.com/delete={message.Id} :{server.Hostname} TAGMSG #{ircChan.Name}");
        }

        private async Task Client_ReactionRemoved(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (!joinedChannels.ContainsKey(channel.Id)
                || !reaction.User.IsSpecified)
                return;

            var ircChan = joinedChannels[channel.Id];
            var guildUser = reaction.User.Value as IGuildUser;

            if (guildUser == null)
                return;

            server.EnqueueMessage($"@{(guildUser.IsBot ? "discord.com/bot;" : "")}discord.com/user={guildUser.Id};+reply={message.Id};+discord.com/react-remove={reaction.Emote.Name} :{guildUser.GetIrcSafeName()}!{guildUser.Id}@discord.com TAGMSG #{ircChan.Name}");
        }

        private async Task Client_ReactionAdded(Cacheable<IUserMessage, ulong> message, ISocketMessageChannel channel, SocketReaction reaction)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (!joinedChannels.ContainsKey(channel.Id)
                || !reaction.User.IsSpecified)
                return;

            var ircChan = joinedChannels[channel.Id];
            var guildUser = reaction.User.Value as IGuildUser;

            if (guildUser == null)
                return;

            server.EnqueueMessage($"@{(guildUser.IsBot ? "discord.com/bot;" : "")}discord.com/user={guildUser.Id};+reply={message.Id};+discord.com/react-add={reaction.Emote.Name} :{guildUser.GetIrcSafeName()}!{guildUser.Id}@discord.com TAGMSG #{ircChan.Name}");
        }

        private async Task Client_UserUnbanned(SocketUser user, SocketGuild guild)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (config.BannedRole.HasValue)
                return;

            foreach (var c in joinedChannels)
            {
                var chan = guild.GetChannel(c.Key) as ITextChannel;
                if (chan == null)
                    continue;

                server.EnqueueMessage($":{server.Hostname} MODE #{c.Value.IrcName} -b *!{user.Id}@*");
            }
        }

        private async Task Client_UserBanned(SocketUser user, SocketGuild guild)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (config.BannedRole.HasValue)
                return;

            foreach (var c in joinedChannels)
            {
                var chan = guild.GetChannel(c.Key) as ITextChannel;
                if (chan == null)
                    continue;

                server.EnqueueMessage($":{server.Hostname} MODE #{c.Value.IrcName} +b *!{user.Id}@*");
            }
        }

        private async Task Client_UserLeft(SocketGuildUser user)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            server.EnqueueMessage($"{(user.IsBot ? "@discord.com/bot " : "")}:{getNickById(user.Id)}!{user.Id}@discord.com QUIT");
        }

        private async Task Client_UserJoined(SocketGuildUser user)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (bans.Contains(user.Id) && config.BannedRole.HasValue)
            {
                await user.AddRoleAsync(guild.GetRole(config.BannedRole.Value));
            }

            foreach (var c in joinedChannels)
            {
                var chan = await guild.GetChannelAsync(c.Key) as ITextChannel;
                if (chan == null)
                    continue;

                if (!user.GetPermissions(chan).ViewChannel)
                    continue;

                server.EnqueueMessage($"{(user.IsBot ? "@discord.com/bot " : "")}:{getNickById(user.Id)}!{user.Id}@discord.com JOIN #{c.Value.IrcName}");

                if (user.GuildPermissions.Administrator)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +a {getNickById(user.Id)}");
                }

                if (user.GuildPermissions.ManageChannels)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +o {getNickById(user.Id)}");
                }

                if (user.GuildPermissions.KickMembers)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +h {getNickById(user.Id)}");
                }

                if (user.GetPermissions(chan).SendMessages)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +v {getNickById(user.Id)}");
                }
            }
        }

        private async Task Client_UserVoiceStateUpdated(SocketUser user, SocketVoiceState oldState, SocketVoiceState newState)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            var guildUser = user as IGuildUser;
            if (guildUser == null)
                return;

            if (newState.VoiceChannel != null
                && joinedChannels.ContainsKey(newState.VoiceChannel.Id))
            {
                if ((oldState.IsMuted || oldState.IsSelfMuted)
                    && (!newState.IsMuted && !newState.IsSelfMuted))
                {
                    server.EnqueueMessage($":{server.Hostname} MODE &{newState.VoiceChannel.Id} +v {guildUser.GetIrcSafeName()}");
                }
                else if ((!oldState.IsMuted && !oldState.IsSelfMuted)
                    && (newState.IsMuted || newState.IsSelfMuted))
                {
                    server.EnqueueMessage($":{server.Hostname} MODE &{newState.VoiceChannel.Id} -v {guildUser.GetIrcSafeName()}");
                }

                if (currentCapabilities.message_tags)
                {
                    server.EnqueueMessage($"@discord.com/voice-state;{(guildUser.IsBot ? "discord.com/bot;" : "")}{(newState.IsMuted ? "discord.com/muted;" : "")}{(newState.IsSelfMuted ? "discord.com/self-muted;" : "")}{(newState.IsDeafened ? "discord.com/deafened;" : "")}{(newState.IsSelfDeafened ? "discord.com/self-deafened;" : "")}{(newState.IsStreaming ? "discord.com/streaming;" : "")} :{guildUser.GetIrcSafeName()}!{guildUser.Id}@discord.com TAGMSG &{newState.VoiceChannel.Id}");
                }
            }

            if (oldState.VoiceChannel != newState.VoiceChannel)
            {
                if (oldState.VoiceChannel != null && joinedChannels.ContainsKey(oldState.VoiceChannel.Id))
                {
                    server.EnqueueMessage($"{(guildUser.IsBot ? "@discord.com/bot " : "")}:{guildUser.GetIrcSafeName()}!{guildUser.Id}@discord.com PART &{oldState.VoiceChannel.Id} :Disconnected");
                }

                if (newState.VoiceChannel != null
                    && joinedChannels.ContainsKey(newState.VoiceChannel.Id))
                {
                    server.EnqueueMessage($":{guildUser.GetIrcSafeName()}!{guildUser.Id}@discord.com JOIN &{newState.VoiceChannel.Id}");
                    if (guildUser.GetPermissions(newState.VoiceChannel).Speak
                        && !newState.IsMuted && !newState.IsSelfMuted)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE &{newState.VoiceChannel.Id} +v {guildUser.GetIrcSafeName()}");
                    }
                }
            }
        }

        private async Task Client_GuildMemberUpdated(SocketGuildUser oldGuildUser, SocketGuildUser newGuildUser)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            if (oldGuildUser.Roles.Count != newGuildUser.Roles.Count)
            {
                var gainedRoles = newGuildUser.Roles.Except(oldGuildUser.Roles);
                var lostRoles = oldGuildUser.Roles.Except(newGuildUser.Roles);

                foreach (var gained in gainedRoles)
                {
                    server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_ADD {newGuildUser.Id} * :{gained.Id}");
                }

                foreach (var lost in lostRoles)
                {
                    server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_REMOVE {newGuildUser.Id} * :{lost.Id}");
                }

                if (config.BannedRole.HasValue)
                {
                    if (gainedRoles.Any(r => r.Id == config.BannedRole.Value))
                    {
                        if (!bans.Contains(newGuildUser.Id))
                            bans.Add(newGuildUser.Id);

                        File.WriteAllText("./bans.json", JsonConvert.SerializeObject(bans));
                    }
                    else if (lostRoles.Any(r => r.Id == config.BannedRole.Value))
                    {
                        bans.Remove(newGuildUser.Id);
                        File.WriteAllText("./bans.json", JsonConvert.SerializeObject(bans));
                    }
                }

                foreach (var c in joinedChannels)
                {
                    var chan = await guild.GetChannelAsync(c.Value.Id) as IGuildChannel;
                    if (chan == null)
                        continue;

                    var isVoice = (chan as IVoiceChannel) != null;
                    var prefix = isVoice ? "&" : "#";

                    if (oldGuildUser.GuildPermissions.Administrator && !newGuildUser.GuildPermissions.Administrator)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} -a {getNickById(newGuildUser.Id)}");
                    }
                    else if (!oldGuildUser.GuildPermissions.Administrator && newGuildUser.GuildPermissions.Administrator)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} +a {getNickById(newGuildUser.Id)}");
                    }

                    if (oldGuildUser.GuildPermissions.ManageChannels && !newGuildUser.GuildPermissions.ManageChannels)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} -o {getNickById(newGuildUser.Id)}");
                    }
                    else if (!oldGuildUser.GuildPermissions.ManageChannels && newGuildUser.GuildPermissions.ManageChannels)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} +o {getNickById(newGuildUser.Id)}");
                    }

                    if (oldGuildUser.GuildPermissions.KickMembers && !newGuildUser.GuildPermissions.KickMembers)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} -h {getNickById(newGuildUser.Id)}");
                    }
                    else if (!oldGuildUser.GuildPermissions.KickMembers && newGuildUser.GuildPermissions.KickMembers)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} +h {getNickById(newGuildUser.Id)}");
                    }

                    if (!isVoice && oldGuildUser.GetPermissions(chan).SendMessages && !newGuildUser.GetPermissions(chan).SendMessages)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} -v {getNickById(newGuildUser.Id)}");
                    }
                    else if (!isVoice && !oldGuildUser.GetPermissions(chan).SendMessages && newGuildUser.GetPermissions(chan).SendMessages)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE {prefix}{chan.GetIrcSafeName()} +v {getNickById(newGuildUser.Id)}");
                    }

                    if (config.BannedRole.HasValue)
                    {
                        if (gainedRoles.Any(r => r.Id == config.BannedRole.Value))
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +b *!{newGuildUser.Id}@*");
                        }
                        else if (lostRoles.Any(r => r.Id == config.BannedRole.Value))
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} -b *!{newGuildUser.Id}@*");
                        }
                    }

                    if (!newGuildUser.GetPermissions(chan).ViewChannel)
                        server.EnqueueMessage($":{getNickById(newGuildUser.Id)}!{newGuildUser.Id}@discord.com PART #{chan.GetIrcSafeName()} :Permissions changed");
                }
            }

            if (oldGuildUser.Nickname != newGuildUser.Nickname)
            {
                if (nickLookupDict.Any(n => n.Value == newGuildUser.Id))
                {
                    var key = nickLookupDict.Where(n => n.Value == newGuildUser.Id).First().Key;
                    nickLookupDict.Remove(key);

                    string discriminator = "";
                    if ((newGuildUser.GetIrcSafeName() == nick && newGuildUser.Id != client.CurrentUser.Id)
                        || nickLookupDict.ContainsKey(newGuildUser.GetIrcSafeName()))
                    {
                        discriminator = "|" + newGuildUser.Discriminator;
                    }

                    nickLookupDict[newGuildUser.GetIrcSafeName() + discriminator] = newGuildUser.Id;

                    server.EnqueueMessage($":{key}!{newGuildUser.Id}@discord.com NICK {newGuildUser.GetIrcSafeName() + discriminator}");
                }
            }
        }

        private async Task Client_RoleUpdated(SocketRole oldRole, SocketRole newRole)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            foreach (var c in joinedChannels)
            {
                var chan = await guild.GetTextChannelAsync(c.Value.Id);
                await foreach (var ul in chan.GetUsersAsync())
                {
                    foreach (var u in ul)
                    {
                        if (!u.RoleIds.Contains(newRole.Id))
                            continue;

                        if (oldRole.Permissions.Administrator && !newRole.Permissions.Administrator
                            && !u.GuildPermissions.Administrator)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} -a {getNickById(u.Id)}");
                        }
                        else if (!oldRole.Permissions.Administrator && newRole.Permissions.Administrator
                            && u.GuildPermissions.Administrator)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} +a {getNickById(u.Id)}");
                        }

                        if (oldRole.Permissions.ManageChannels && !newRole.Permissions.ManageChannels
                            && !u.GuildPermissions.ManageChannels)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} -o {getNickById(u.Id)}");
                        }
                        else if (!oldRole.Permissions.ManageChannels && newRole.Permissions.ManageChannels
                            && u.GuildPermissions.ManageChannels)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} +o {getNickById(u.Id)}");
                        }

                        if (oldRole.Permissions.KickMembers && !newRole.Permissions.KickMembers
                            && !u.GuildPermissions.KickMembers)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} -h {getNickById(u.Id)}");
                        }
                        else if (!oldRole.Permissions.KickMembers && newRole.Permissions.KickMembers
                            && u.GuildPermissions.KickMembers)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} +h {getNickById(u.Id)}");
                        }

                        if (oldRole.Permissions.SendMessages && !newRole.Permissions.SendMessages
                            && !u.GetPermissions(chan).SendMessages)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} -v {getNickById(u.Id)}");
                        }
                        else if (!oldRole.Permissions.SendMessages && newRole.Permissions.SendMessages
                            && u.GetPermissions(chan).SendMessages)
                        {
                            server.EnqueueMessage($":{server.Hostname} MODE {chan.GetIrcSafeName()} +v {getNickById(u.Id)}");
                        }
                    }
                }
            }
        }

        private async Task Client_ChannelUpdated(SocketChannel oldChannel, SocketChannel newChannel)
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            var oldGuildChannel = oldChannel as IGuildChannel;
            var newGuildChannel = newChannel as IGuildChannel;
            if (newGuildChannel == null)
                return;

            if ((newGuildChannel as ITextChannel) != null)
            {
                var oldTextChannel = oldChannel as ITextChannel;
                var newTextChannel = newChannel as ITextChannel;
                if (oldTextChannel.SlowModeInterval != newTextChannel.SlowModeInterval)
                {
                    if (newTextChannel.SlowModeInterval == 0)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} -Z");
                    }
                    else
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} +Z {newTextChannel.SlowModeInterval}");
                    }
                }

                if (oldTextChannel.IsNsfw != newTextChannel.IsNsfw)
                {
                    var operation = newTextChannel.IsNsfw ? "+" : "-";
                    server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} {operation}X");
                }

                if (oldTextChannel.Topic != newTextChannel.Topic)
                {
                    string topicStr;
                    if (newTextChannel.Topic != null)
                    {
                        topicStr = newTextChannel.Topic.Replace("\r", "").Replace("\n", " | ");
                    }
                    else
                    {
                        topicStr = "";
                    }

                    server.EnqueueMessage($":{server.Hostname} TOPIC #{newGuildChannel.GetIrcSafeName()} :{topicStr}");
                }

                var joined = newChannel.Users.Except(oldChannel.Users).ToList();
                var parted = oldChannel.Users.Except(newChannel.Users).ToList();
                var commonUsers = newChannel.Users.Intersect(oldChannel.Users);

                foreach (var j in joined)
                {
                    var guildUser = j as IGuildUser;
                    server.EnqueueMessage($":{getNickById(guildUser.Id)}!{guildUser.Id}@discord.com JOIN #{newGuildChannel.GetIrcSafeName()}");

                    var modes = "";
                    if (guildUser.Id == guild.OwnerId)
                    {
                        modes += "q";
                    }

                    if (guildUser.GuildPermissions.Administrator)
                    {
                        modes += "a";
                    }

                    if (guildUser.GuildPermissions.ManageChannels)
                    {
                        modes += "o";
                    }

                    if (guildUser.GuildPermissions.KickMembers)
                    {
                        modes += "h";
                    }

                    if (guildUser.GetPermissions(newGuildChannel).Has(ChannelPermission.SendMessages))
                    {
                        modes += "v";
                    }

                    if (modes.Length > 0)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} +{modes} {getNickById(j.Id)}");
                    }
                }

                foreach (var p in parted)
                {
                    var guildUser = p as IGuildUser;
                    server.EnqueueMessage($":{getNickById(guildUser.Id)}!{guildUser.Id}@discord.com PART #{newGuildChannel.GetIrcSafeName()} :Permissions changed");
                }

                foreach (var u in commonUsers)
                {
                    var guildUser = u as IGuildUser;

                    if (guildUser.GetPermissions(newGuildChannel).Has(ChannelPermission.SendMessages)
                        && !guildUser.GetPermissions(oldGuildChannel).Has(ChannelPermission.SendMessages))
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} +v {getNickById(guildUser.Id)}");
                    }
                    else if (!guildUser.GetPermissions(newGuildChannel).Has(ChannelPermission.SendMessages)
                        && guildUser.GetPermissions(oldGuildChannel).Has(ChannelPermission.SendMessages))
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} -v {getNickById(guildUser.Id)}");
                    }
                }

                if (oldTextChannel.Name != newTextChannel.Name)
                {
                    server.EnqueueMessage($":{server.Hostname} KICK #{oldTextChannel.GetIrcSafeName()} {nick} :Channel name changed");
                    joinedChannels[newTextChannel.Id].Name = newTextChannel.Name;

                    var newJoinMessage = IrcMessage.Parse($":{nick}!{client.CurrentUser.Id}@discord.com JOIN #{newTextChannel.GetIrcSafeName()}");
                    JoinHandler(newJoinMessage);
                }
            }
            else if ((newGuildChannel as IVoiceChannel) != null)
            {
                var oldVoiceChannel = oldChannel as IVoiceChannel;
                var newVoiceChannel = newChannel as IVoiceChannel;

                if (oldVoiceChannel.Bitrate != newVoiceChannel.Bitrate)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} +N {newVoiceChannel.Bitrate}");
                }

                if (oldVoiceChannel.UserLimit != newVoiceChannel.UserLimit)
                {
                    if (newVoiceChannel.UserLimit.HasValue)
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} +l {newVoiceChannel.UserLimit}");
                    }
                    else
                    {
                        server.EnqueueMessage($":{server.Hostname} MODE #{newGuildChannel.GetIrcSafeName()} -l");
                    }
                }
            }
        }

        private async Task Client_MessageReceived(SocketMessage socketMessage) // async to avoid `return Task.CompletedTask`
        {
            if (server.CurrentStage != AuthStages.CapsNegotiated)
                return;

            var message = socketMessage as IUserMessage;
            if (message == null)
                return;

            if (message.Source == MessageSource.System)
                return;

            if (message.Author.Id == client.CurrentUser.Id)
                return;

            var isPrivateMessage = (message.Channel as IPrivateChannel) != null;
            if (!handleDms && isPrivateMessage)
                return;

            var guildUser = message.Author as IGuildUser;

            string target;
            if (isPrivateMessage)
            {
                target = nick;
            }
            else
            {
                target = "#" + message.Channel.Name;
            }

            string userNick;
            string userUser;
            if (!message.Author.IsWebhook)
            {
                userNick = getNickById(message.Author.Id);
                userUser = message.Author.Id.ToString();
            }
            else
            {
                userNick = message.Author.GetIrcSafeName();
                userUser = "webhook";
            }

            var tagsList = new Dictionary<string, string>();
            if (currentCapabilities.message_tags)
            {
                if (message.Author.IsBot)
                    tagsList["discord.com/bot"] = "";


                if (guildUser != null)
                {
                    foreach (var tag in config.RoleTags)
                    {
                        if (!guildUser.RoleIds.Contains(tag.Key))
                            continue;

                        if (string.IsNullOrWhiteSpace(tag.Value))
                            return;

                        tagsList[tagEscape(tag.Value)] = "";
                    }
                }

                tagsList["discord.com/user"] = message.Author.Id.ToString();
            }

            var isAction = !message.Content.Contains('\n') && Regex.IsMatch(message.Content, @"^_.*_");

            var lines = message.Content.Split('\n');
            var lineNumber = 1;
            foreach (var line in lines)
            {
                if (currentCapabilities.message_tags)
                {
                    if (lines.Length > 1)
                        tagsList["msgid"] = message.Id.ToString() + $"-{lineNumber++}";
                    else
                        tagsList["msgid"] = message.Id.ToString();
                }

                string tags = "@";
                foreach (var tag in tagsList)
                {
                    var val = string.IsNullOrWhiteSpace(tag.Value) ? "" : $"={tag.Value}";
                    tags += tagEscape($"{tag.Key}{val}") + ";";
                }

                var msgContent = parseDiscordMentions(line.Replace("\r", ""));
                if (isAction)
                {
                    var actionMatch = Regex.Match(msgContent, @"_(?<content>.*)_");
                    msgContent = $"ACTION {actionMatch.Groups["content"].Value}";
                }
                server.EnqueueMessage($"{tags} :{userNick}!{userUser}@discord.com PRIVMSG {target} :{msgContent}");
            }
        }

        [IrcCommand("CAP", postCaps: false)]
        public void CapHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
                return;

            switch (message.Params[0])
            {
                case "LS":
                    server.EnqueueMessage($":{server.Hostname} CAP * LS :message-tags away-notify multi-prefix");
                    capLocked = true;
                    break;
                case "REQ":
                    if (message.Params.Count < 2)
                        return;

                    var ack = "";
                    var nak = "";
                    for (var i = 1; i < message.Params.Count; i++)
                    {
                        if (message.Params[i] == "message-tags"
                            || message.Params[i] == "multi-prefix")
                        {
                            ack += message.Params[i] + " ";
                        }
                        else
                        {
                            nak += message.Params[i] + " ";
                        }

                        if (message.Params[i] == "message-tags")
                            currentCapabilities.message_tags = true;
                        if (message.Params[i] == "multi-prefix")
                            currentCapabilities.multi_prefix = true;
                    }

                    if (!string.IsNullOrWhiteSpace(ack))
                        server.EnqueueMessage($":{server.Hostname} CAP * ACK :{ack}");

                    if (!string.IsNullOrWhiteSpace(nak))
                        server.EnqueueMessage($":{server.Hostname} CAP * NAK :{nak}");

                    break;
                case "END":
                    capLocked = false;
                    checkHandshakeStatus();
                    break;
                default:
                    break;
            }
        }

        [IrcCommand("PASS", postAuth: false, postCaps: false)]
        public async void PassHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
                return;

            ulong.TryParse(message.Params[0], out ulong guildId);
            var guilds = client.Guilds;
            guild = guilds.Where(g => g.Id == guildId).FirstOrDefault();
            if (guild == null)
            {
                server.Stop();
                return;
            }

            if (config.BannedRole.HasValue && config.PreserveBans)
            {
                if (File.Exists("./bans.json"))
                {
                    try
                    {
                        bans = JsonConvert.DeserializeObject<List<ulong>>(File.ReadAllText("./bans.json"));
                    }
                    catch (JsonException e)
                    {
                        logger.Warn($"Couldn't load bans: {e.Message}");
                    }
                    catch (IOException e)
                    {
                        logger.Warn($"Couldn't load bans: {e.Message}");
                    }
                }

                var banRole = guild.Roles.Where(r => r.Id == config.BannedRole).FirstOrDefault();
                if (banRole != null)
                {
                    foreach (var u in await guild.GetUsersAsync())
                    {
                        if (!u.RoleIds.Contains(banRole.Id))
                        {
                            if (bans.Contains(u.Id))
                                bans.Remove(u.Id);

                            continue;
                        }

                        if (!bans.Contains(banRole.Id))
                            bans.Add(u.Id);
                    }
                }
            }

            handshakeStatus |= HandshakeFlags.Pass;
            checkHandshakeStatus();
        }

        [IrcCommand("USER", postAuth: false, postCaps: false)]
        public void UserHandler(IrcMessage message)
        {
            handshakeStatus |= HandshakeFlags.User;
        }

        [IrcCommand("NICK")]
        public async void NickHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
                return;

            if (!handshakeStatus.HasFlag(HandshakeFlags.Nick))
            {
                nick = client.CurrentUser.Username.Replace(' ', '_').Replace(':', '_');

                server.EnqueueMessage($":{message.Params[0]} NICK {nick}");

                handshakeStatus |= HandshakeFlags.Nick;
                checkHandshakeStatus();
            }
            else
            {
                var self = await guild.GetUserAsync(client.CurrentUser.Id);
                await self.ModifyAsync(u =>
                {
                    u.Nickname = message.Params[0];
                });
            }
        }

        [IrcCommand("MOTD")]
        public void MotdHandler(IrcMessage message)
        {
            sendMotd();
        }

        [IrcCommand("LUSERS")]
        public void LusersHandler(IrcMessage message)
        {
            sendLusers();
        }

        [IrcCommand("PING")]
        public void PingHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
                return;

            server.PriorityEnqueueMessage($":{server.Hostname} PONG {nick} :{message.Params[0]}");
            server.Ping();
        }

        [IrcCommand("PONG")]
        public void PongHandler(IrcMessage message)
        {
            server.Ping();
        }

        [IrcCommand("JOIN", preAuth: false, postAuth: false)]
        public async void JoinHandler(IrcMessage message)
        {
            foreach (var param in message.Params[0].Split(','))
            {
                if (string.IsNullOrWhiteSpace(param))
                    continue;

                var isVoice = param.StartsWith('&');
                var chanName = param.Substring(1);

                ulong chanId;
                IGuildChannel chan;
                if (isVoice)
                {
                    if (!ulong.TryParse(chanName, out chanId))
                    {
                        server.EnqueueMessage($":{server.Hostname} 403 {nick} {param} :Unknown channel");
                        return;
                    }
                    chan = await guild.GetChannelAsync(chanId);
                    if (chan == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} 403 {nick} {param} :Unknown channel");
                        return;
                    }
                }
                else
                {
                    if (!param.StartsWith('#'))
                    {
                        server.EnqueueMessage($":{server.Hostname} 479 {nick} {param} :Unknown channel");
                        return;
                    }

                    chan = (await guild.GetChannelsAsync()).Where(c => c.Name == chanName).FirstOrDefault();
                    if (chan == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} 403 {nick} {param} :Unknown channel");
                        return;
                    }

                    chanId = chan.Id;
                }

                string topicStr;
                if (isVoice)
                {
                    topicStr = chan.Name;
                }
                else
                {
                    var textChan = (chan as ITextChannel);
                    if (textChan.Topic != null)
                    {
                        topicStr = textChan.Topic.Replace("\r", "").Replace("\n", " | ");
                    }
                    else
                    {
                        topicStr = "";
                    }
                }

                joinedChannels[chan.Id] = new IrcChannel(chanId, chanName, isVoice);
                server.EnqueueMessage($":{nick}!{client.CurrentUser.Id}@discord.com JOIN {param}");
                server.EnqueueMessage($":{server.Hostname} 332 {nick} {param} :{topicStr}");
                server.EnqueueMessage($":{server.Hostname} 333 {nick} {param} {server.Hostname} {(Int32)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds}");
                await sendNames(chan);

                var user = await guild.GetUserAsync(client.CurrentUser.Id);
                if (user.GuildPermissions.Administrator)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +a {getNickById(user.Id)}");
                }

                if (user.GuildPermissions.ManageChannels)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +o {getNickById(user.Id)}");
                }

                if (user.GuildPermissions.KickMembers)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +h {getNickById(user.Id)}");
                }

                if (user.GetPermissions(chan).SendMessages)
                {
                    server.EnqueueMessage($":{server.Hostname} MODE #{chan.GetIrcSafeName()} +v {getNickById(user.Id)}");
                }
            }
        }

        [IrcCommand("PART", preAuth: false, postAuth: false)]
        public void PartHandler(IrcMessage message)
        {
            if (joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
            {
                var id = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
                joinedChannels.Remove(id);
                server.EnqueueMessage($":{nick}!{client.CurrentUser.Id}@discord.com PART {message.Params[0]}");
            }
        }

        [IrcCommand("WHOIS", preAuth: false, postAuth: false)]
        public async void WhoisHandler(IrcMessage message)
        {
            var target = await findUserByIrcName(message.Params[0]);
            if (target == null)
            {
                server.EnqueueMessage($":{server.Hostname} 401 {nick} {message.Params[0]} :No such nick");
                return;
            }

            server.EnqueueMessage($":{server.Hostname} 311 {nick} {message.Params[0]} {target.Id} discord.com * :{target.Username}#{target.Discriminator}");
            server.EnqueueMessage($":{server.Hostname} 312 {nick} {message.Params[0]} {server.Hostname} :The Truman Show");
            server.EnqueueMessage($":{server.Hostname} 317 {nick} {message.Params[0]} {(target.Status != UserStatus.Online ? "180" : "0")} {(Int32)(target.CreatedAt.Subtract(new DateTime(1970, 1, 1))).TotalSeconds} :{Enum.GetName(typeof(UserStatus), target.Status)}");
            server.EnqueueMessage($":{server.Hostname} 318 {nick} {message.Params[0]} :End of /WHOIS list");
        }

        [IrcCommand("PRIVMSG", preAuth: false, postAuth: false)]
        public async void PrivmsgHandler(IrcMessage message)
        {
            var isChannel = message.Params[0].StartsWith('#');
            var isVoice = message.Params[0].StartsWith('&');

            if (isVoice)
            {
                server.EnqueueMessage($":{server.Hostname} 404 {nick} {message.Params[0]} :Cannot send to voice channels");
                return;
            }

            if (isChannel)
            {
                var chanName = message.Params[0].Substring(1);
                if (!joinedChannels.Any(c => c.Value.IrcName == chanName))
                {
                    server.EnqueueMessage($":{server.Hostname} 442 {nick} {message.Params[0]} :Not on channel");
                    return;
                }

                var chan = await guild.GetChannelAsync(joinedChannels.Where(c => c.Value.IrcName == chanName).First().Key) as ITextChannel;
                var currentUser = await guild.GetUserAsync(client.CurrentUser.Id);

                if (!currentUser.GetPermissions(chan).Has(ChannelPermission.SendMessages))
                {
                    server.EnqueueMessage($":{server.Hostname} 404 {nick} {message.Params[0]} :SendMessages permission required");
                    return;
                }

                var msgStr = message.Params[1];
                var actionMatch = Regex.Match(message.Params[1], @"^ACTION (?<content>.*)$");
                if (actionMatch.Success)
                {
                    msgStr = "_" + actionMatch.Groups["content"].Value + "_";
                }

                msgStr = await parseIrcMentions(msgStr);
                await chan.SendMessageAsync(msgStr.Substring(0, Math.Min(msgStr.Length, 2000)));
            }
            else
            {
                var target = await findUserByIrcName(message.Params[0]);
                if (target == null)
                    return;

                var msgStr = await parseIrcMentions(message.Params[1]);
                await target.SendMessageAsync(msgStr.Substring(0, Math.Min(msgStr.Length, 2000)));
            }
        }

        [IrcCommand("QUIT")]
        public void QuitHandler(IrcMessage message)
        {
            server.Stop();
        }

        [IrcCommand("MODE", preAuth: false, postAuth: false)]
        #region MODE
        public async void ModeHandler(IrcMessage message)
        {
            if (!message.Params[0].StartsWith('#')
                && !message.Params[0].StartsWith('&'))
            {
                if (message.Params.Count > 1 && message.Params[1].Length > 1
                    && (message.Params[1].StartsWith('+') || message.Params[1].StartsWith('-')))
                {
                    server.EnqueueMessage($":{server.Hostname} 501 {nick} :User modes are unsupported");
                    server.EnqueueMessage($":{nick} MODE {nick} {message.Params[1]}");
                    if (message.Params[1].StartsWith('+'))
                    {
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            if (userModes.Contains(message.Params[1][i]))
                                continue;

                            userModes += message.Params[1][i];
                        }
                    }
                    else if (message.Params[1].StartsWith('-'))
                    {
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            userModes = userModes.Replace(message.Params[1][i].ToString(), "");
                        }
                    }
                    return;
                }
                else
                {
                    server.EnqueueMessage($":{server.Hostname} 221 {nick} +{userModes}");
                    return;
                }
            }
            else if (message.Params[0].StartsWith('#'))
            {
                var self = await guild.GetUserAsync(client.CurrentUser.Id);
                var chan = (await guild.GetChannelsAsync()).Where(c => c.Name == message.Params[0].Substring(1)).FirstOrDefault();

                if (chan == null)
                {
                    server.EnqueueMessage($":{server.Hostname} 403 {nick} {message.Params[0]} :No such channel");
                    return;
                }

                if (message.Params.Count > 1 && message.Params[1].Length > 1
                    && (message.Params[1].StartsWith('+') || message.Params[1].StartsWith('-')))
                {
                    if (message.Params[1].StartsWith('+'))
                    {
                        var appliedModes = "";
                        var appliedParams = new List<string>();
                        var paramIndex = 2;
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            switch (message.Params[1][i])
                            {
                                case 'b':
                                    if (message.Params.Count >= paramIndex + 1)
                                    {
                                        if (config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.ManageRoles)
                                            || (!config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.BanMembers)))
                                        {
                                            server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                        }
                                        else
                                        {
                                            var matches = Regex.Match(message.Params[paramIndex++], @".*!(?<id>\d+)?@.*");
                                            if (matches.Success)
                                            {
                                                ulong.TryParse(matches.Groups["id"].Value, out ulong userId);
                                                if (config.BannedRole.HasValue)
                                                {
                                                    var role = guild.GetRole(config.BannedRole.Value);
                                                    if (role != null)
                                                    {
                                                        var user = await guild.GetUserAsync(userId);

                                                        if (user != null)
                                                        {
                                                            try
                                                            {
                                                                await user.AddRoleAsync(role);
                                                                //appliedModes += message.Params[1][i];
                                                                //appliedParams.Add($"*!{userId}@*");
                                                            }
                                                            catch (HttpException) { }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        server.EnqueueMessage($":{server.Hostname} 400 {nick} MODE +b :Could not find ban role");
                                                    }
                                                }
                                                else
                                                {
                                                    var user = await guild.GetUserAsync(userId);

                                                    if (user != null)
                                                    {
                                                        appliedModes += message.Params[1][i];
                                                        appliedParams.Add($"*!{userId}@*");
                                                        await user.BanAsync();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (config.BannedRole.HasValue)
                                        {
                                            var role = guild.GetRole(config.BannedRole.Value);
                                            if (role != null)
                                            {
                                                var bans = (await guild.GetUsersAsync()).Where(u => u.RoleIds.Contains(role.Id));
                                                foreach (var ban in bans)
                                                {
                                                    server.EnqueueMessage($":{server.Hostname} 367 {nick} {message.Params[0]} *!{ban.Id}@*");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            var bans = await guild.GetBansAsync();
                                            foreach (var ban in bans)
                                            {
                                                server.EnqueueMessage($":{server.Hostname} 367 {nick} {message.Params[0]} *!{ban.User.Id}@*");
                                            }
                                        }
                                        server.EnqueueMessage($":{server.Hostname} 368 {nick} {message.Params[0]} :End of channel ban list");
                                    }
                                    break;
                                case 'Z':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        if (paramIndex <= message.Params.Count)
                                        {
                                            if (int.TryParse(message.Params[paramIndex++], out int slowModeInterval))
                                            {
                                                await (chan as ITextChannel).ModifyAsync(c => c.SlowModeInterval = slowModeInterval);
                                                appliedModes += appliedModes += message.Params[1][i];
                                                appliedParams.Add(slowModeInterval.ToString());
                                            }
                                        }
                                    }
                                    break;
                                case 'X':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        await (chan as ITextChannel).ModifyAsync(c => c.IsNsfw = true);
                                        appliedModes += appliedModes += message.Params[1][i];
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }
                        var appliedParamsStr = "";
                        if (appliedParams.Count > 0)
                        {
                            appliedParamsStr = " " + appliedParams.Aggregate((c, n) => $"{c} {n}");
                        }

                        if (!string.IsNullOrWhiteSpace(appliedModes))
                        {
                            server.EnqueueMessage($":{nick} MODE {message.Params[0]} +{appliedModes}{appliedParamsStr}");
                        }
                    }
                    else if (message.Params[1].StartsWith('-'))
                    {
                        var appliedModes = "";
                        var appliedParams = new List<string>();
                        var paramIndex = 2;
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            switch (message.Params[1][i])
                            {
                                case 'b':
                                    if (message.Params.Count >= paramIndex)
                                    {
                                        if (config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.ManageRoles)
                                            || (!config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.BanMembers)))
                                        {
                                            server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                        }
                                        else
                                        {
                                            var matches = Regex.Match(message.Params[paramIndex++], @".*!(?<id>\d+)@.*");
                                            if (matches.Success)
                                            {
                                                ulong.TryParse(matches.Groups["id"].Value, out ulong userId);
                                                if (config.BannedRole.HasValue)
                                                {
                                                    var role = guild.GetRole(config.BannedRole.Value);
                                                    if (role != null)
                                                    {
                                                        var user = await guild.GetUserAsync(userId);

                                                        if (user != null)
                                                        {
                                                            try
                                                            {
                                                                await user.RemoveRoleAsync(role);
                                                                //appliedModes += message.Params[1][i];
                                                                //appliedParams.Add($"*!{userId}@*");
                                                            }
                                                            catch (HttpException) { }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        server.EnqueueMessage($":{server.Hostname} 400 {nick} MODE +b :Could not find ban role");
                                                    }
                                                }
                                                else
                                                {
                                                    await guild.RemoveBanAsync(userId);
                                                    appliedModes += message.Params[1][i];
                                                    appliedParams.Add($"*!{userId}@*");
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case 'Z':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        if (paramIndex <= message.Params.Count)
                                        {
                                            await (chan as ITextChannel).ModifyAsync(c => c.SlowModeInterval = 0);
                                            appliedModes += appliedModes += message.Params[1][i];
                                        }
                                    }
                                    break;
                                case 'X':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        await (chan as ITextChannel).ModifyAsync(c => c.IsNsfw = false);
                                        appliedModes += appliedModes += message.Params[1][i];
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }

                        var appliedParamsStr = "";
                        if (appliedParams.Count > 0)
                        {
                            appliedParamsStr = " " + appliedParams.Aggregate((c, n) => $"{c} {n}");
                        }

                        if (!string.IsNullOrWhiteSpace(appliedModes))
                        {
                            server.EnqueueMessage($":{nick} MODE {message.Params[0]} -{appliedModes}{appliedParamsStr}");
                        }
                    }
                    return;
                }
                else
                {
                    var isSlow = (chan as ITextChannel).SlowModeInterval > 0;
                    var isNsfw = (chan as ITextChannel).IsNsfw;
                    var modeStr = (isSlow || isNsfw) ? $"+{(isSlow ? "Z" : "")}{(isNsfw ? "X" : "")}" : "";
                    var paramStr = $"{(isSlow ? " " + (chan as ITextChannel).SlowModeInterval.ToString() : "")}";
                    server.EnqueueMessage($":{server.Hostname} 324 {nick} {message.Params[0]} {modeStr}{paramStr}");
                    return;
                }
            }
            else if (message.Params[0].StartsWith('&'))
            {
                var self = await guild.GetUserAsync(client.CurrentUser.Id);
                ulong.TryParse(message.Params[0].Substring(1), out ulong chanId);
                var chan = await guild.GetVoiceChannelAsync(chanId);

                if (chan == null)
                {
                    server.EnqueueMessage($":{server.Hostname} 403 {nick} {message.Params[0]} :No such channel");
                    return;
                }

                if (message.Params.Count > 1 && message.Params[1].Length > 1
                    && (message.Params[1].StartsWith('+') || message.Params[1].StartsWith('-')))
                {
                    if (message.Params[1].StartsWith('+'))
                    {
                        var appliedModes = "";
                        var appliedParams = new List<string>();
                        var paramIndex = 2;
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            switch (message.Params[1][i])
                            {
                                case 'b':
                                    if (message.Params.Count >= paramIndex + 1)
                                    {
                                        if (config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.ManageRoles)
                                            || (!config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.BanMembers)))
                                        {
                                            server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                        }
                                        else
                                        {
                                            var matches = Regex.Match(message.Params[paramIndex++], @".*!(?<id>\d+)@.*");
                                            if (matches.Success)
                                            {
                                                ulong.TryParse(matches.Groups["id"].Value, out ulong userId);
                                                if (config.BannedRole.HasValue)
                                                {
                                                    var role = guild.GetRole(config.BannedRole.Value);
                                                    if (role != null)
                                                    {
                                                        var user = await guild.GetUserAsync(userId);

                                                        if (user != null)
                                                        {
                                                            try
                                                            {
                                                                await user.AddRoleAsync(role);
                                                                appliedModes += message.Params[1][i];
                                                                appliedParams.Add($"*!{userId}@*");
                                                            }
                                                            catch (HttpException) { }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        server.EnqueueMessage($":{server.Hostname} 400 {nick} MODE +b :Could not find ban role");
                                                    }
                                                }
                                                else
                                                {
                                                    var user = await guild.GetUserAsync(userId);

                                                    if (user != null)
                                                    {
                                                        appliedModes += message.Params[1][i];
                                                        appliedParams.Add($"*!{userId}@*");
                                                        await user.BanAsync();
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        if (config.BannedRole.HasValue)
                                        {
                                            var role = guild.GetRole(config.BannedRole.Value);
                                            if (role != null)
                                            {
                                                var bans = (await guild.GetUsersAsync()).Where(u => u.RoleIds.Contains(role.Id));
                                                foreach (var ban in bans)
                                                {
                                                    server.EnqueueMessage($":{server.Hostname} 367 {nick} {message.Params[0]} *!{ban.Id}@*");
                                                }
                                            }
                                        }
                                        else
                                        {
                                            var bans = await guild.GetBansAsync();
                                            foreach (var ban in bans)
                                            {
                                                server.EnqueueMessage($":{server.Hostname} 367 {nick} {message.Params[0]} *!{ban.User.Id}@*");
                                            }
                                        }
                                        server.EnqueueMessage($":{server.Hostname} 368 {nick} {message.Params[0]} :End of channel ban list");
                                    }
                                    break;
                                case 'B':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        if (paramIndex <= message.Params.Count)
                                        {
                                            if (int.TryParse(message.Params[paramIndex++], out int bitrate)
                                                && bitrate >= 8000
                                                && bitrate <= 96000)
                                            {
                                                await (chan as IVoiceChannel).ModifyAsync(c => c.Bitrate = bitrate);
                                                appliedModes += appliedModes += message.Params[1][i];
                                                appliedParams.Add(bitrate.ToString());
                                            }
                                        }
                                    }
                                    break;
                                case 'l':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        if (paramIndex <= message.Params.Count)
                                        {
                                            if (int.TryParse(message.Params[paramIndex++], out int userLimit)
                                                && userLimit > 0)
                                            {
                                                await (chan as IVoiceChannel).ModifyAsync(c => c.UserLimit = userLimit);
                                                appliedModes += appliedModes += message.Params[1][i];
                                                appliedParams.Add(userLimit.ToString());
                                            }
                                        }
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }
                        var appliedParamsStr = "";
                        if (appliedParams.Count > 0)
                        {
                            appliedParamsStr = " " + appliedParams.Aggregate((c, n) => $"{c} {n}");
                        }

                        if (!string.IsNullOrWhiteSpace(appliedModes))
                        {
                            server.EnqueueMessage($":{nick} MODE {message.Params[0]} +{appliedModes}{appliedParamsStr}");
                        }
                    }
                    else if (message.Params[1].StartsWith('-'))
                    {
                        var appliedModes = "";
                        var appliedParams = new List<string>();
                        var paramIndex = 2;
                        for (var i = 1; i < message.Params[1].Length; i++)
                        {
                            switch (message.Params[1][i])
                            {
                                case 'b':
                                    if (message.Params.Count >= paramIndex)
                                    {
                                        if (config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.ManageRoles)
                                            || (!config.BannedRole.HasValue && !self.GuildPermissions.Has(GuildPermission.BanMembers)))
                                        {
                                            server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                        }
                                        else
                                        {
                                            var matches = Regex.Match(message.Params[paramIndex++], @".*!(?<id>\d+)@.*");
                                            if (matches.Success)
                                            {
                                                ulong.TryParse(matches.Groups["id"].Value, out ulong userId);
                                                if (config.BannedRole.HasValue)
                                                {
                                                    var role = guild.GetRole(config.BannedRole.Value);
                                                    if (role != null)
                                                    {
                                                        var user = await guild.GetUserAsync(userId);

                                                        if (user != null)
                                                        {
                                                            try
                                                            {
                                                                await user.RemoveRoleAsync(role);
                                                                appliedModes += message.Params[1][i];
                                                                appliedParams.Add($"*!{userId}@*");
                                                            }
                                                            catch (HttpException) { }
                                                        }
                                                    }
                                                    else
                                                    {
                                                        server.EnqueueMessage($":{server.Hostname} 400 {nick} MODE +b :Could not find ban role");
                                                    }
                                                }
                                                else
                                                {
                                                    await guild.RemoveBanAsync(userId);
                                                    appliedModes += message.Params[1][i];
                                                    appliedParams.Add($"*!{userId}@*");
                                                }
                                            }
                                        }
                                    }
                                    break;
                                case 'l':
                                    if (!self.GuildPermissions.Has(GuildPermission.ManageChannels))
                                    {
                                        server.EnqueueMessage($":{server.Hostname} 482 {nick} MODE :Insufficient privileges");
                                    }
                                    else
                                    {
                                        await (chan as IVoiceChannel).ModifyAsync(c => c.UserLimit = null);
                                        appliedModes += appliedModes += message.Params[1][i];
                                    }
                                    break;
                                default:
                                    break;
                            }
                        }

                        var appliedParamsStr = "";
                        if (appliedParams.Count > 0)
                        {
                            appliedParamsStr = " " + appliedParams.Aggregate((c, n) => $"{c} {n}");
                        }

                        if (!string.IsNullOrWhiteSpace(appliedModes))
                        {
                            server.EnqueueMessage($":{nick} MODE {message.Params[0]} -{appliedModes}{appliedParamsStr}");
                        }
                    }
                    return;
                }
                else
                {
                    var bitrate = (chan as IVoiceChannel).Bitrate;
                    var userLimit = (chan as IVoiceChannel).UserLimit;
                    var modeStr = $"+B{(userLimit.HasValue ? "l" : "")} {bitrate}{(userLimit.HasValue ? " " + userLimit.Value : "")}";
                    server.EnqueueMessage($":{server.Hostname} 324 {nick} {message.Params[0]} {modeStr}");
                    return;
                }
            }
        }
        #endregion

        [IrcCommand("USERHOST", preAuth: false, postAuth: false)]
        public async void UserhostHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
            {
                server.EnqueueMessage($":{server.Hostname} 461 {nick} USERHOST :Need more params");
                return;
            }

            var replies = new List<string>();
            foreach (var nick in message.Params)
            {
                var user = await findUserByIrcName(nick);
                if (user != null)
                {
                    var isAdmin = user.GuildPermissions.Administrator;
                    replies.Add($"{nick}{(isAdmin ? "*" : "")}={user.Id}@discord.com");
                }
            }

            var replyStr = replies.Aggregate((c, n) => $"{c} {n}");
            server.EnqueueMessage($":{server.Hostname} 302 {nick} :{replyStr}");
        }

        [IrcCommand("TOPIC", preAuth: false, postAuth: false)]
        public async void TopicHandler(IrcMessage message)
        {
            var self = await guild.GetUserAsync(client.CurrentUser.Id);
            if (!self.GuildPermissions.ManageChannels)
            {
                server.EnqueueMessage($":{server.Hostname} 482 {nick} TOPIC :Insufficient privileges");
                return;
            }

            if (!joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
            {
                server.EnqueueMessage($":{server.Hostname} 403 {nick} {message.Params[0]} :No such channel");
                return;
            }

            var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
            var chan = await guild.GetTextChannelAsync(chanId);
            await chan.ModifyAsync(c => c.Topic = message.Params[1]);
        }

        [IrcCommand("KICK", preAuth: false, postAuth: false)]
        public async void KickHandler(IrcMessage message)
        {
            if (message.Params.Count < 2)
                return;

            var target = await findUserByIrcName(message.Params[1]);
            var self = await guild.GetUserAsync(client.CurrentUser.Id);

            var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
            var voiceChan = await guild.GetVoiceChannelAsync(chanId);
            if (voiceChan != null && self.GuildPermissions.MoveMembers)
            {
                await target.ModifyAsync(u => u.Channel = null);
                return;
            }

            var reason = message.Params.Count > 2 ? message.Params[2] : "";
            if (!self.GuildPermissions.KickMembers)
            {
                server.EnqueueMessage($":{server.Hostname} 482 {nick} KICK :Insufficient privileges");
                return;
            }

            if (!config.FakeKick)
            {
                try
                {
                    await target.KickAsync(reason);
                }
                catch (HttpException) { }
            }
            else
            {
                server.EnqueueMessage($":{nick}!{self.Id}@discord.com KICK {message.Params[1]} {message.Params[2]} :{reason}");
                server.EnqueueMessage($":{message.Params[2]} JOIN {message.Params[1]}");
            }
        }

        [IrcCommand("ROLE", preAuth: false, postAuth: false)]
        public async void RoleHandler(IrcMessage message)
        {
            var self = await guild.GetUserAsync(client.CurrentUser.Id);
            if (!self.GuildPermissions.ManageRoles)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL * * :Insufficient permissions");
                return;
            }

            if (message.Params.Count == 0)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL * * :Not enough parameters");
                return;
            }

            IGuildUser guildUser;
            IRole role;
            ulong userId;
            ulong roleId;
            switch (message.Params[0])
            {
                case "LS":
                    if (message.Params.Count > 1)
                    {
                        if (!ulong.TryParse(message.Params[1], out userId))
                        {
                            server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {message.Params[1]} LS :Invalid user ID");
                            break;
                        }

                        guildUser = await guild.GetUserAsync(userId);
                        if (guildUser == null)
                        {
                            server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} LS :User not found");
                            break;
                        }

                        var roleList = new List<string>();
                        foreach (var r in guildUser.RoleIds)
                        {
                            roleList.Add(r.ToString());
                            if (roleList.Count == 15)
                            {
                                server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_LS_USERENTRY {userId} LS :{roleList.Aggregate((c, n) => $"{c} {n}")}");
                                roleList.Clear();
                            }
                        }
                        if (roleList.Count > 0)
                        {
                            server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_LS_USERENTRY {userId} LS :{roleList.Aggregate((c, n) => $"{c} {n}")}");
                        }
                        server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_LS_USEREND {userId} LS :End of ROLE LS list");
                    }
                    else
                    {
                        foreach (var r in guild.Roles)
                        {
                            server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_LS_ENTRY * LS :{r.Name}={r.Id}");
                        }
                        server.EnqueueMessage($":{server.Hostname} NOTE ROLE ROLE_LS_END * LS :End of ROLE LS list");
                    }
                    break;
                case "ADD":
                    if (message.Params.Count == 1)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL * ADD :Not enough parameters");
                        break;
                    }

                    if (!ulong.TryParse(message.Params[1], out userId))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {message.Params[1]} ADD :Invalid user ID");
                        break;
                    }

                    if (!ulong.TryParse(message.Params[2], out roleId))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {message.Params[1]} ADD :Invalid role ID");
                        break;
                    }

                    guildUser = await guild.GetUserAsync(userId);
                    if (guildUser == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} ADD :User not found");
                        break;
                    }

                    role = guild.GetRole(roleId);
                    if (role == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} ADD :Role not found");
                        break;
                    }

                    try
                    {
                        await guildUser.AddRoleAsync(role);
                    }
                    catch (HttpException e)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} ADD :{e.Message}");
                    }
                    break;
                case "REMOVE":
                    if (message.Params.Count == 1)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL * REMOVE :Not enough parameters");
                        break;
                    }

                    if (!ulong.TryParse(message.Params[1], out userId))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {message.Params[1]} REMOVE :Invalid user ID");
                        break;
                    }

                    if (!ulong.TryParse(message.Params[2], out roleId))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {message.Params[1]} REMOVE :Invalid role ID");
                        break;
                    }

                    guildUser = await guild.GetUserAsync(userId);
                    if (guildUser == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} REMOVE :User not found");
                        break;
                    }

                    role = guild.GetRole(roleId);
                    if (role == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} REMOVE :Role not found");
                        break;
                    }

                    try
                    {
                        await guildUser.RemoveRoleAsync(role);
                    }
                    catch (HttpException e)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL ROLE ROLE_FAIL {userId} REMOVE :{e.Message}");
                    }
                    break;
                default:
                    break;
            }
        }

        [IrcCommand("TAGMSG", preAuth: false, postAuth: false)]
        public async void TagmsgHandler(IrcMessage message)
        {
            if (message.Tags.ContainsKey("+reply") && message.Tags.ContainsKey("+discord.com/react-add"))
            {
                if (!ulong.TryParse(message.Tags["+reply"], out ulong msgId))
                    return;

                if (joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
                {
                    var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
                    var chan = await guild.GetChannelAsync(chanId) as ITextChannel;
                    if (chan == null)
                        return;

                    var target = await chan.GetMessageAsync(msgId);
                    if (target == null)
                        return;

                    IEmote emote;
                    var guildEmote = guild.Emotes.Where(e => e.Name == message.Tags["+discord.com/react-add"]).FirstOrDefault();
                    if (guildEmote == null)
                    {
                        if (!NeoSmart.Unicode.Emoji.IsEmoji(message.Tags["+discord.com/react-add"]))
                            return;

                        emote = new Emoji(message.Tags["+discord.com/react-add"]);
                    }
                    else
                    {
                        emote = guildEmote;
                    }

                    try
                    {
                        await target.AddReactionAsync(emote);
                    }
                    catch (HttpException) { }
                }
            }

            if (message.Tags.ContainsKey("+reply") && message.Tags.ContainsKey("+discord.com/react-remove"))
            {
                ulong targetId = client.CurrentUser.Id;
                if (message.Tags.ContainsKey("+discord.com/user"))
                {
                    if (!ulong.TryParse(message.Tags["+discord.com/user"], out targetId))
                        return;
                }

                if (!ulong.TryParse(message.Tags["+reply"], out ulong msgId))
                    return;

                if (joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
                {
                    var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
                    var chan = await guild.GetChannelAsync(chanId) as ITextChannel;
                    if (chan == null)
                        return;

                    var target = await chan.GetMessageAsync(msgId);
                    if (target == null)
                        return;

                    IEmote emote;
                    var guildEmote = guild.Emotes.Where(e => e.Name == message.Tags["+discord.com/react-remove"]).FirstOrDefault();
                    if (guildEmote == null)
                    {
                        if (!NeoSmart.Unicode.Emoji.IsEmoji(message.Tags["+discord.com/react-remove"]))
                            return;

                        emote = new Emoji(message.Tags["+discord.com/react-remove"]);
                    }
                    else
                    {
                        emote = guildEmote;
                    }

                    if (target.Author.Id != client.CurrentUser.Id
                        && !(await guild.GetUserAsync(client.CurrentUser.Id)).GuildPermissions.ManageMessages)
                        return;

                    try
                    {
                        await target.RemoveReactionAsync(emote, targetId);
                    }
                    catch (Discord.Net.HttpException)
                    {
                        LogManager.GetLogger("Bridge").Error($"HttpException thrown while reacting with emote: {emote.Name}");
                    }
                }
            }

            if (message.Tags.ContainsKey("+discord.com/delete"))
            {
                if (!(await guild.GetUserAsync(client.CurrentUser.Id)).GuildPermissions.ManageMessages)
                    return;

                if (!ulong.TryParse(message.Tags["+discord.com/delete"], out ulong msgId))
                    return;

                if (joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
                {
                    var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
                    var chan = await guild.GetChannelAsync(chanId) as ITextChannel;
                    if (chan == null)
                        return;

                    var target = await chan.GetMessageAsync(msgId);
                    if (target == null)
                        return;

                    await chan.DeleteMessageAsync(msgId);
                }
            }
        }

        [IrcCommand("EDITMSG", preAuth: false, postAuth: false)]
        public async void EditmsgHandler(IrcMessage message)
        {
            if (!message.Tags.ContainsKey("+reply"))
                return;

            if (!ulong.TryParse(message.Tags["+reply"], out ulong msgId))
                return;

            var isChannel = message.Params[0].StartsWith('#');
            var isVoice = message.Params[0].StartsWith('&');

            if (isVoice)
            {
                server.EnqueueMessage($":{server.Hostname} 404 {nick} {message.Params[0]} :Cannot send to voice channels");
                return;
            }

            if (isChannel)
            {
                var chanName = message.Params[0].Substring(1);
                if (!joinedChannels.Any(c => c.Value.IrcName == chanName))
                {
                    server.EnqueueMessage($":{server.Hostname} 442 {nick} {message.Params[0]} :Not on channel");
                    return;
                }

                var chan = await guild.GetChannelAsync(joinedChannels.Where(c => c.Value.IrcName == chanName).First().Key) as ITextChannel;
                var currentUser = await guild.GetUserAsync(client.CurrentUser.Id);

                if (!currentUser.GetPermissions(chan).Has(ChannelPermission.SendMessages))
                {
                    server.EnqueueMessage($":{server.Hostname} 404 {nick} {message.Params[0]} :SendMessages permission required");
                    return;
                }

                var msg = await chan.GetMessageAsync(msgId) as IUserMessage;
                if (msg == null || msg.Author.Id != client.CurrentUser.Id)
                    return;

                await msg.ModifyAsync(async m => m.Content = (await parseIrcMentions(message.Params[1])).Substring(0, Math.Min(message.Params[1].Length, 2000)));
            }
        }

        [IrcCommand("LIST", preAuth: false, postAuth: false)]
        public async void ListHandler(IrcMessage message)
        {
            var chans = new List<string>();
            if (message.Params.Count > 1)
            {
                chans = message.Params[1].Split(',').ToList();
            }

            foreach (var c in await guild.GetChannelsAsync())
            {
                var isVoice = (c as IVoiceChannel) != null;
                var isText = (c as ITextChannel) != null;

                if (!isVoice && !isText)
                    continue;

                if (chans.Count > 0)
                {
                    if ((isVoice && !chans.Any(ic => ic.Substring(1) == c.Id.ToString()))
                        || !chans.Any(ic => ic.Substring(1) == c.GetIrcSafeName()))
                        continue;
                }
                string topic = "";
                if (isVoice)
                {
                    topic = c.Name;
                }
                else
                {
                    var textChan = c as ITextChannel;
                    if (textChan.Topic != null)
                        topic = textChan.Topic.Replace("\n", " | ").Replace("\r", "");
                }
                server.EnqueueMessage($":{server.Hostname} 322 {nick} {(isVoice ? "&" : "#")}{c.GetIrcSafeName()} 0 :{topic}");
            }
            server.EnqueueMessage($":{server.Hostname} 323 {nick} :End of /LIST");
        }

        [IrcCommand("SETNICK", preAuth: false, postAuth: false)]
        public async void SetnickHandler(IrcMessage message)
        {
            var self = await guild.GetUserAsync(client.CurrentUser.Id);
            if (!self.GuildPermissions.ManageNicknames)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL SETNICK SETNICK_FAIL * * :Insufficient permissions");
                return;
            }

            if (message.Params.Count < 2)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL SETNICK SETNICK_FAIL * * :Not enough parameters");
                return;
            }

            if (!ulong.TryParse(message.Params[0], out ulong userId))
            {
                server.EnqueueMessage($":{server.Hostname} FAIL SETNICK SETNICK_FAIL {message.Params[1]} * :Invalid user ID");
                return;
            }

            var guildUser = await guild.GetUserAsync(userId);
            if (guildUser == null)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL SETNICK SETNICK_FAIL {userId} * :No such user");
                return;
            }

            try
            {
                await guildUser.ModifyAsync(u => u.Nickname = message.Params[1]);
                server.EnqueueMessage($":{server.Hostname} NOTE SETNICK SETNICK_SUCCESS {userId} * :{message.Params[1]}");
            }
            catch (HttpException e)
            {
                server.EnqueueMessage($":{server.Hostname} FAIL SETNICK SETNICK_FAIL {userId} * :{e.Message}");
            }
        }

        [IrcCommand("WHO", preAuth: false, postAuth: false)]
        public async void WhoHandler(IrcMessage message)
        {
            if (message.Params.Count == 0
                || message.Params[0] == "0"
                || message.Params[0] == "*")
            {
                // List all users
                foreach (var u in await guild.GetUsersAsync())
                {
                    foreach (var chan in joinedChannels)
                    {
                        var guildChan = await guild.GetChannelAsync(chan.Key);
                        if (!u.GetPermissions(guildChan).Has(ChannelPermission.ReadMessages))
                            continue;

                        string prefix = "";
                        if (u.Id == guild.OwnerId)
                        {
                            prefix += "~";
                        }

                        if (u.GuildPermissions.Has(GuildPermission.Administrator))
                        {
                            prefix += "&";
                        }

                        if (u.GuildPermissions.Has(GuildPermission.ManageChannels))
                        {
                            prefix += "@";
                        }

                        if (u.GuildPermissions.Has(GuildPermission.KickMembers))
                        {
                            prefix += "%";
                        }

                        if (u.GetPermissions(guildChan).Has(ChannelPermission.SendMessages))
                        {
                            prefix += "+";
                        }

                        server.EnqueueMessage($":{server.Hostname} 352 {nick} {chan.Value.IrcName} {u.Id} discord.com discord.com {getNickById(u.Id)} G{prefix} :0 {u.Username}#{u.Discriminator}");
                    }
                }
                server.EnqueueMessage($":{server.Hostname} 315 * :End of WHO list");
            }
            else
            {
                // Find the specified user
                var matches = Regex.Match(message.Params[0], @"(?<nick>[^!]+)(?:!(?<id>[^@]+)@.*)?");
                if (!matches.Success)
                {
                    server.EnqueueMessage($":{server.Hostname} 315 {message.Params[0]} :End of WHO list");
                    return;
                }

                IGuildUser u = null;
                if (matches.Groups["id"].Success)
                {
                    if (!ulong.TryParse(matches.Groups["id"].Value, out ulong id))
                    {
                        server.EnqueueMessage($":{server.Hostname} 315 {message.Params[0]} :End of WHO list");
                        return;
                    }

                    u = await guild.GetUserAsync(id);
                    if (u == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} 315 {message.Params[0]} :End of WHO list");
                        return;
                    }
                }
                else if (matches.Groups["nick"].Success)
                {
                    u = await findUserByIrcName(matches.Groups["nick"].Value);
                    if (u == null)
                    {
                        server.EnqueueMessage($":{server.Hostname} 315 {message.Params[0]} :End of WHO list");
                        return;
                    }
                }

                foreach (var chan in joinedChannels)
                {
                    var guildChan = await guild.GetChannelAsync(chan.Key);
                    if (!u.GetPermissions(guildChan).Has(ChannelPermission.ReadMessages))
                        continue;

                    string prefix = "";
                    if (u.Id == guild.OwnerId)
                    {
                        prefix += "~";
                    }

                    if (u.GuildPermissions.Has(GuildPermission.Administrator))
                    {
                        prefix += "&";
                    }

                    if (u.GuildPermissions.Has(GuildPermission.ManageChannels))
                    {
                        prefix += "@";
                    }

                    if (u.GuildPermissions.Has(GuildPermission.KickMembers))
                    {
                        prefix += "%";
                    }

                    if (u.GetPermissions(guildChan).Has(ChannelPermission.SendMessages))
                    {
                        prefix += "+";
                    }

                    server.EnqueueMessage($":{server.Hostname} 352 {nick} {chan.Value.IrcName} {u.Id} discord.com discord.com {getNickById(u.Id)} G{prefix} :0 {u.Username}#{u.Discriminator}");
                }
                server.EnqueueMessage($":{server.Hostname} 315 {message.Params[0]} :End of WHO list");
            }
        }

        [IrcCommand("NAMES", preAuth: false, postAuth: false)]
        public async void NamesHandler(IrcMessage message)
        {
            if (message.Params.Count == 0)
                return;

            if (!joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
            {
                server.EnqueueMessage($":{server.Hostname} 403 {nick} {message.Params[0]} :No such channel");
                return;
            }

            var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
            var chan = await guild.GetTextChannelAsync(chanId);

            await sendNames(chan as IGuildChannel);
        }

        [IrcCommand("EMBED", preAuth: false, postAuth: false)]
        public async void EmbedHandler(IrcMessage message)
        {
            if (message.Params.Count < 2)
            {
                // FAIL <command> <identifier> <context> <subcommand>
                server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL * * :Not enough parameters");
                return;
            }

            if (!joinedChannels.Any(c => c.Value.IrcName == message.Params[0].Substring(1)))
            {
                server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} * :Unknown channel");
                return;
            }

            var chanId = joinedChannels.Where(c => c.Value.IrcName == message.Params[0].Substring(1)).First().Key;
            var chan = await guild.GetTextChannelAsync(chanId);

            switch (message.Params[1])
            {
                case "AUTHOR":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    if (message.Params.Count == 3)
                    {
                        var author = await findUserByIrcName(message.Params[2]);
                        if (author == null)
                        {
                            server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :No such user");
                            return;
                        }

                        currentEmbeds[chanId].WithAuthor(author);
                    }
                    else
                    {
                        var authorName = message.Params[2];
                        var authorIconUrl = message.Params.Count > 3 ? message.Params[3] : null;
                        var authorUrl = message.Params.Count > 4 ? message.Params[4] : null;
                        currentEmbeds[chanId].WithAuthor(authorName, authorIconUrl, authorUrl);
                    }
                    break;
                case "COLOR":
                    if (message.Params.Count < 5)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    if (!int.TryParse(message.Params[2], out int red)
                        || !int.TryParse(message.Params[3], out int green)
                        || !int.TryParse(message.Params[4], out int blue))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Invalid parameters");
                        return;
                    }

                    currentEmbeds[chanId].WithColor(red, green, blue);
                    break;
                case "DESCRIPTION":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    currentEmbeds[chanId].WithDescription(message.Params[2]);
                    break;
                case "FOOTER":
                    if (message.Params.Count < 4)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    var footer = message.Params[3];
                    var footerIconUrl = message.Params[2] != "*" ? message.Params[2] : null;
                    currentEmbeds[chanId].WithFooter(footer, footerIconUrl);
                    break;
                case "IMAGE":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    currentEmbeds[chanId].WithImageUrl(message.Params[2]);
                    break;
                case "THUMBNAIL":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    currentEmbeds[chanId].WithThumbnailUrl(message.Params[2]);
                    break;
                case "TIMESTAMP":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    if (!double.TryParse(message.Params[2], out double utime))
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Invalid parameters");
                        return;
                    }

                    var timestamp = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddSeconds(utime).ToLocalTime();

                    currentEmbeds[chanId].WithTimestamp(new DateTimeOffset(timestamp));
                    break;
                case "TITLE":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    currentEmbeds[chanId].WithTitle(message.Params[2]);
                    break;
                case "URL":
                    if (message.Params.Count < 3)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    currentEmbeds[chanId].WithUrl(message.Params[2]);
                    break;
                case "FIELD":
                    if (message.Params.Count < 4)
                    {
                        server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Not enough parameters");
                        return;
                    }

                    if (!currentEmbeds.ContainsKey(chanId))
                    {
                        currentEmbeds[chanId] = new EmbedBuilder();
                    }

                    var field = message.Params[2];
                    var value = message.Params[3];
                    var inline = message.Params.Count > 4;
                    currentEmbeds[chanId].AddField(field, value, inline);
                    break;
                case "END":
                    if (currentEmbeds.ContainsKey(chanId))
                    {
                        var body = message.Params.Count > 2 ? message.Params[2] : null;
                        await chan.SendMessageAsync(body, embed: currentEmbeds[chanId].Build());
                        server.EnqueueMessage($":{nick} PRIVMSG {message.Params[0]} :{body}");
                        currentEmbeds.Remove(chanId);
                    }
                    break;
                default:
                    server.EnqueueMessage($":{server.Hostname} FAIL EMBED EMBED_FAIL {message.Params[0]} {message.Params[1]} :Unknown command");
                    break;
            }
        }

        private void checkHandshakeStatus()
        {
            var rfcAuth = HandshakeFlags.Nick | HandshakeFlags.Pass | HandshakeFlags.User;
            if (handshakeStatus == rfcAuth && !capLocked)
            {
                handshakeStatus |= HandshakeFlags.Caps;
                server.CurrentStage = AuthStages.CapsNegotiated;
                server.EnqueueMessage($":{server.Hostname} 001 {nick} :Welcome to the Discord Network, {nick}");
                server.EnqueueMessage($":{server.Hostname} 002 {nick} :Your host is discord-irc-bridge, running version 1.0");
                server.EnqueueMessage($":{server.Hostname} 003 {nick} :This server was created last Thursday");
                server.EnqueueMessage($":{server.Hostname} 004 {nick} discord-irc-bridge 1.0 * X Zb");
                server.EnqueueMessage($":{server.Hostname} 005 {nick} PREFIX=(qaohv)~&@%+ STATUSMSG=~&@%+ MODES=1 CHANMODES=b,,ZBl,X LINELEN=4096 :are supported by this server");
                sendLusers();
                sendMotd();
            }
            else if (handshakeStatus == rfcAuth)
            {
                server.CurrentStage = AuthStages.Authenticated;
            }
        }

        private void sendMotd()
        {
            server.EnqueueMessage($":{server.Hostname} 375 {nick} :- discord-irc-bridge Message of the day - ");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :██████╗ ██╗███████╗ ██████╗ ██████╗ ██████╗ ██████╗ ");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :██╔══██╗██║██╔════╝██╔════╝██╔═══██╗██╔══██╗██╔══██╗");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :██║  ██║██║███████╗██║     ██║   ██║██████╔╝██║  ██║");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :██║  ██║██║╚════██║██║     ██║   ██║██╔══██╗██║  ██║");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :██████╔╝██║███████║╚██████╗╚██████╔╝██║  ██║██████╔╝");
            server.EnqueueMessage($":{server.Hostname} 372 {nick} :╚═════╝ ╚═╝╚══════╝ ╚═════╝ ╚═════╝ ╚═╝  ╚═╝╚═════╝ ");
            server.EnqueueMessage($":{server.Hostname} 376 {nick} :End of /MOTD command.");
        }

        private void sendLusers()
        {
            server.EnqueueMessage($":{server.Hostname} 251 {nick} :There are 1 users and 0 invisible on 1 servers");
        }

        private async Task sendNames(IGuildChannel chan)
        {
            bool sentList = false;
            var chanPrefix = (chan as IVoiceChannel) != null ? "&" : "#";
            if (joinedChannels.ContainsKey(chan.Id))
            {
                var prefix = "";
                var names = new List<string>();
                names.Add(nick);

                await foreach (var ul in chan.GetUsersAsync())
                {
                    foreach (var u in ul)
                    {
                        prefix = "";
                        if (u.Id == guild.OwnerId)
                        {
                            if (currentCapabilities.multi_prefix)
                                prefix += "~";
                            else
                                prefix = "~";
                        }

                        if (u.GuildPermissions.Has(GuildPermission.Administrator))
                        {
                            if (currentCapabilities.multi_prefix)
                                prefix += "&";
                            else if (prefix.Length == 0)
                                prefix = "&";
                        }
                        
                        if (u.GuildPermissions.Has(GuildPermission.ManageChannels))
                        {
                            if (currentCapabilities.multi_prefix)
                                prefix += "@";
                            else if (prefix.Length == 0)
                                prefix = "@";
                        }
                        
                        if (u.GuildPermissions.Has(GuildPermission.KickMembers))
                        {
                            if (currentCapabilities.multi_prefix)
                                prefix += "%";
                            else if (prefix.Length == 0)
                                prefix = "%";
                        }
                        
                        if (u.GetPermissions(chan).Has(ChannelPermission.SendMessages))
                        {
                            if (currentCapabilities.multi_prefix)
                                prefix += "+";
                            else if (prefix.Length == 0)
                                prefix = "+";
                        }

                        string discr = "";
                        if ((u.GetIrcSafeName() == nick && u.Id != client.CurrentUser.Id)
                            || nickLookupDict.Any(n => n.Key == u.GetIrcSafeName() && n.Value != u.Id))
                        {
                            discr = "|" + u.Discriminator;
                        }

                        names.Add($"{prefix + u.GetIrcSafeName() + discr}!{u.Id}@discord.com");
                        nickLookupDict[u.GetIrcSafeName() + discr] = u.Id;

                        if (names.Count >= config.NamesPerEntry)
                        {
                            server.EnqueueMessage($":{server.Hostname} 353 {nick} {chanPrefix}{chan.GetIrcSafeName()} :{names.Aggregate((s, n) => s + " " + n)}");
                            names.Clear();
                            sentList = true;
                        }
                    }
                }

                if (names.Count > 0)
                {
                    server.EnqueueMessage($":{server.Hostname} 353 {nick} {chanPrefix}{chan.GetIrcSafeName()} :{names.Aggregate((s, n) => s + " " + n)}");
                    sentList = true;
                }
            }

            if (sentList)
                server.EnqueueMessage($":{server.Hostname} 366 {nick} {chanPrefix}{chan.GetIrcSafeName()} :End of /NAMES list");
        }

        private async Task<IGuildUser> findUserByIrcName(string name)
        {
            if (nickLookupDict.ContainsKey(name))
                return await guild.GetUserAsync(nickLookupDict[name]);

            var users = await guild.GetUsersAsync();
            foreach (var u in users)
            {
                if (u.GetIrcSafeName() == name)
                    return u;
            }

            return null;
        }

        private string getNickById(ulong id)
        {
            var userQuery = nickLookupDict.Where(kv => kv.Value == id);

            if (userQuery.Any())
                return userQuery.First().Key;

            IUser user = client.GetUser(id);
            if (user == null)
            {
                user = restClient.GetUserAsync(id).GetAwaiter().GetResult();
                if (user == null)
                    return $"user_{id}";
            }

            string discriminator = "";
            if ((user.GetIrcSafeName() == nick && user.Id != client.CurrentUser.Id)
                || nickLookupDict.Any(n => n.Key == user.GetIrcSafeName() && n.Value != user.Id))
            {
                discriminator = "|" + user.Discriminator;
            }

            nickLookupDict[user.GetIrcSafeName() + discriminator] = user.Id;

            return user.GetIrcSafeName() + discriminator;
        }

        private string tagEscape(string input)
        {
            return input.Replace(":", @"\:").Replace(" ", @"\s").Replace(@"\", @"\\").Replace("\r", @"\r").Replace("\n", @"\n");
        }

        private string tagUnescape(string input)
        {
            return input.Replace(@"\:", ":").Replace(@"\s", " ").Replace(@"\\", @"\").Replace(@"\r", "\r").Replace(@"\n", "\n");
        }

        private async Task<string> parseIrcMentions(string input)
        {
            if (config.AtMentions)
            {
                input = Regex.Replace(input, @"(?<![\w])@(?!@)(?<nick>[^\s:$%,.;!?]+)", m =>
                {
                    var nick = m.Groups["nick"].Value;
                    if (!nickLookupDict.ContainsKey(nick))
                        return nick;

                    return $"<@{nickLookupDict[nick]}>";
                });
            }
            else
            {
                foreach (var ul in await guild.GetUsersAsync())
                {
                    var nick = getNickById(ul.Id);
                    input = Regex.Replace(input, @$"\b{Regex.Escape(nick)}\b", $@"<@{ul.Id}>");
                }
            }

            foreach (var ch in await guild.GetChannelsAsync())
            {
                if ((ch as IVoiceChannel) != null
                    || (ch as ICategoryChannel) != null)
                    continue;

                var chanName = ch.GetIrcSafeName();
                input = Regex.Replace(input, $@"(?<![\w])#(?!#){Regex.Escape(chanName)}\b", @$"<#{ch.Id}>");
            }

            return input;
        }

        private string parseDiscordMentions(string input)
        {
            if (config.ConvertMentionsFromDiscord)
            {
                input = Regex.Replace(input, @"<@!?(?<id>\d+)>", m =>
                {
                    if (ulong.TryParse(m.Groups["id"].Value, out ulong id))
                    {
                        return getNickById(id);
                    }
                    return m.Value;
                });

                input = Regex.Replace(input, @"<#!?(?<id>\d+)>", m =>
                {
                    if (ulong.TryParse(m.Groups["id"].Value, out ulong id))
                    {
                        var chan = guild.GetChannelAsync(id).GetAwaiter().GetResult();
                        if (chan == null)
                            return m.Value;

                        var isVoice = (chan as IVoiceChannel) != null;

                        return (isVoice ? "&" : "#") + chan.GetIrcSafeName();
                    }
                    return m.Value;
                });
            }
            return input;
        }

        private class Capabilities
        {
            public bool message_tags = false;
            public bool multi_prefix = false;
        }
    }
}
