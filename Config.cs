using System.Collections.Generic;

namespace DiscordIrcBridge
{
    public class Config
    {
        public ulong? BannedRole;
        public bool AtMentions = true;
        public bool ConvertMentionsFromDiscord = true;
        public bool FakeKick = false;
        public int Port = 6667;
        public string Hostname = "irc.discord.com";
        public int OutgoingMessageLimit = 25;
        public Dictionary<ulong, string> RoleTags = new Dictionary<ulong, string>();
        public bool PreserveBans = true;
        public int NamesPerEntry = 20;
        public int ClientTimeout = 300;
        public int ThreadDelay = 25;
        public int QueueLockTime = 1000;
        public bool ShowOfflineUsers = false;
    }
}
