discord-irc-host
================

A single-client IRC server which connects the client to a Discord guild.

_This application only accepts bot tokens and is not intended to connect users to Discord._

## Usage ##

Create a file called token.txt with your bot's token as its contents and start the application. An IRC server will start listening on port 6667 (configurable). Once an IRC client is connected, it can join channels and begin sending messages.

## Config ##

Configuration is done in config.json. The default configuration is as follows:

```json
{
    "BannedRole": null,
    "AtMentions": true,
    "ConvertMentionsFromDiscord": true,
    "Port": 6667,
    "Hostname": "irc.discord.com"
}
```

### BannedRole ###

If you'd like to use a role for mode +b instead of bans, specify that role's Snowflake ID here.

### AtMentions ###

Whether or not the server should only parse out outgoing mentions preceded by an at sign.

### ConvertMentionsFromDiscord ###

Whether or not mentions should be converted from Discord to their IRC names. If this is set, messages with mentions may be delayed if a user who the Discord client has never seen is mentioned.

### Port ###

The port that the IRC server should listen on

### Hostname ###

The hostname that the IRC server should use in messages going to the IRC client.

## Direct Messages ##

Direct messages are handled by the first instance of the bot. Instances launched thereafter will ignore DMs. If the first instance is destroyed, a new instance will need to be created in order to handle direct messages again.