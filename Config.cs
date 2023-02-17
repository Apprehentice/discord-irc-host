using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DiscordIrcBridge
{
    public class Config
    {
        public enum BanModes
        {
            Ban,
            Role,
            Timeout
        }

        public ulong? BannedRole;
        public ulong? DefaultTimeoutDuration = 600;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public BanModes BanMode = BanModes.Timeout;
        public bool AtMentions = true;
        public bool ConvertMentionsFromDiscord = true;
        public bool FakeKick = false;
        public int Port = 6667;
        public string Hostname = "irc.discord.com";
        public int OutgoingMessageLimit = 25;
        public Dictionary<ulong, string> RoleTags = new();
        public bool PreserveBans = true;
        public int NamesPerEntry = 20;
        public int ClientTimeout = 300;
        public int ThreadDelay = 25;
        public int QueueLockTime = 1000;
        public bool ShowOfflineUsers = true;
        public List<string> NamesWhitelist = new();
    }
}
