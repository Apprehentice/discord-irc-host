using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordIrcBridge
{
    public class IrcAddress
    {
        public string Nick;
        public string User;
        public string Host;

        public override string ToString()
        {
            return $"{Nick}!{User}@{Host}";
        }

        public static bool TryParse(string input, out IrcAddress address)
        {
            var match = Regex.Match(input, @"(?<nick>.*)!(?<user>.*)@(?<host>.*)");
            if (!match.Success)
            {
                address = null;
                return false;
            }

            address = new IrcAddress()
            {
                Nick = match.Groups["nick"].Value,
                User = match.Groups["user"].Value,
                Host = match.Groups["host"].Value
            };
            return true;
        }

        public static bool IsMatch(string input, string pattern)
        {
            var re = "^" + Regex.Escape(pattern).Replace("\\?", ".").Replace("\\*", ".*") + "$";
            return Regex.IsMatch(input, re);
        }

        public static bool IsMatch(IrcAddress input, string pattern) => IsMatch(input.ToString(), pattern);
    }
}
