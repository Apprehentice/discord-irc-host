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
    }
}
