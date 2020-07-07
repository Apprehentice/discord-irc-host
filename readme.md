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

## Channel Types ##

The IRC server distinguishes voice and text channels by their channel prefix. Text channels have the # prefix while voice channels have the & prefix.

### Channel Modes ###

Each channel type has its own set of modes, however prefix modes are universal. The host uses prefix modes to denote members with specific permissions:

| Prefix | Mode | Permission |
|--------|------|------------|
|~|q|Owner|
|&|a|Administrator|
|@|o|Manage Channels|
|%|h|Kick Members|
|+|v|Send Messages|

### Text Channels ###

Text channels behave as much like IRC channels as possible. Incoming messages are split into lines and sent over to the client. Each message is sent with the tags `discord.com/bot` (if the author is a bot) and `msgid`. The first line of a multiline message will contain the actual message ID. Each subsequent line will have the 0-indexed line number attached to the ID (`<message_id>-<line_number>`.)

Text channels have the following modes available to them:

| Mode | Requires Parameter | Function |
|------|--------------------|----------|
|Z|When setting|Slow Mode Interval in Seconds|
|X|No|NSFW|

Text channels also receive messages about reactions via the `+discord.com/react-add` and `+discord.com/react-remove` tags along with the `+reply` tag.

Message deletes are done via the `+discord.com/delete` tag. Its value is the target message ID.

See [replies.md](..BLOB/master/replies.md) for details about the EDITMSG message.

### Voice Channels ###

Voice Channels do not send or receive PRIVMSGs, but they do relay information about who is connected to a voice channel and what their voice state is.

Voice channels have the following modes available to them:

| Mode | Requires Parameter | Function |
|------|--------------------|----------|
|B|Always|Bitrate (between 8000 and 96000) **(Cannot be unset)**|
|l|When setting|User limit|

Voice state is transmitted via MSGTAG with the following tags:

```
discord.com/muted
discord.com/self-muted
discord.com/deafened
discord.com/self-deafened
discord.com/streaming
```