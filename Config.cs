﻿namespace DiscordIrcBridge
{
    public class Config
    {
        public ulong? BannedRole;
        public bool AtMentions = true;
        public bool ConvertMentionsFromDiscord = true;
        public int Port = 6667;
        public string Hostname = "irc.discord.com";
    }
}