using System;
using System.Collections.Generic;
using System.Text;

namespace DiscordIrcBridge
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    public class IrcCommandAttribute : Attribute
    {
        public IrcCommandAttribute(string command, bool preAuth = true, bool postAuth = true, bool postCaps = true)
        {
            Command = command;
            PreAuth = preAuth;
            PostAuth = postAuth;
            PostCaps = postCaps;
        }

        public string Command;
        public bool PreAuth;
        public bool PostAuth;
        public bool PostCaps;
    }

    [Flags]
    public enum AuthStages
    {
        None = 0,
        PreAuthentication = 1 << 1,
        Authenticated = 1 << 2,
        CapsNegotiated = 1 << 3,
        PreCaps = PreAuthentication | Authenticated,
        All = ~0
    }
}