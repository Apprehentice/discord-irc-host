using Discord;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace DiscordIrcBridge
{
    public static class ExtensionMethods
    {
        public static string GetDisplayName(this IGuildUser user)
        {
            return user.Nickname != null ? user.Nickname : user.Username;
        }

        public static string GetIrcSafeName(this IGuildUser user)
        {
            var newNick = Regex.Replace(user.GetDisplayName(), @"[\s:$%,.;!?]", "_");
            if (!Regex.IsMatch(newNick, @"^[a-zA-Z0-9`|^_{}\[\]\\]"))
                newNick = "_" + newNick;
            return newNick;
        }

        public static string GetIrcSafeName(this IUser user)
        {
            var newNick = Regex.Replace(user.Username, @"[\s:$%,.;!?]", "_");
            if (!Regex.IsMatch(newNick, @"^[a-zA-Z0-9`|^_{}\[\]\\]"))
                newNick = "_" + newNick;

            return newNick;
        }

        public static string GetIrcSafeName(this IGuildChannel chan)
        {
            return (chan as IVoiceChannel) == null ? chan.Name : chan.Id.ToString();
        }

        // https://stackoverflow.com/a/1450889/13275594
        public static IEnumerable<string> GetChunks(this string str, int maxChunkSize)
        {
            for (int i = 0; i < str.Length; i += maxChunkSize)
                yield return str.Substring(i, Math.Min(maxChunkSize, str.Length - i));
        }
    }
}
