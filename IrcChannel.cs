using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordIrcBridge
{
    public class IrcChannel
    {
        public IrcChannel(ulong id, string name, bool isVoice)
        {
            Id = id;
            Name = name;
            IsVoice = isVoice;
        }

        public ulong Id;
        public string Name;
        public bool IsVoice;
        public string IrcName => IsVoice ? Id.ToString() : Name;
        //public List<ulong> VoicedUsers = new List<ulong>();
    }
}
