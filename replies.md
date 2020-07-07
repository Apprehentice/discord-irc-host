Discord Standard Replies
========================

Commands which interface directly with Discord (not through IRC conventions) 
send replies via the [Standard Replies Extension](https://ircv3.net/specs/extensions/standard-replies). This extension is designed under the assumption that only one client is connected to the IRC server.

## ROLE Command ##

The ROLE command is used to change and get information about a guild's roles. 
It has a few subcommands to facilitate these actions. The client needs the 
Manage Roles permission in order to use this command. A FAIL reply will be sent 
should any of the commands fail to complete.

#### ROLE_FAIL ####

This reply indicates that the role command failed for some reason.

```
FAIL ROLE ROLE_FAIL <context> <subcommand> :<reason>
```

### LS ###

```
ROLE LS [<id>]
```

If `id` is given, the LS subcommand lists a user's roles. Otherwise, it lists all 
available roles on the server.

#### ROLE_LS_ENTRY ####

Represents a role on the given user

```
NOTE ROLE ROLE_LS_ENTRY {<id>|*} LS :*{<role_name>=<role_ud>}
```

#### ROLE_LS_END ###

Signals when the list of roles is completed

```
NOTE ROLE ROLE_LS_END <id> LS :End of ROLES LS list
```

### ADD ###

Adds a role to a user

```
ROLE ADD <user_id> <role_id>
```

#### ROLE_ADD ####

Confirms that a role was added to the target user

```
NOTE ROLE ROLE_ADD <user_id> * :<role_id>
```

### REMOVE ###

Removes a role from a user

```
ROLE REMOVE <user_id> <role_id>
```

#### ROLE_REMOVE ####

Confirms that a role was removed from the target user

```
NOTE ROLE ROLE_ADD <user_id> * :<role_id>
```

## SETNICK ##

The SETNICK command allows an IRC user to change the nickname of a Discord user.

```
SETNICK <user_id> :<new_nickname>
```

### SETNICK_SUCCESS ###

Signifies success in setting a user's nick. The client should not, however, assume that the request was fulfilled. Instead, the client should wait for a NICK message from the server.

```
NOTE SETNICK SETNICK_SUCCESS <user_id> * :<new_nick>
```

### SETNICK_FAIL ###

Denotes a failure to set the target's nick. This is most likely a privilege issue, but may present itself as an HTTP error.

```
FAIL SETNICK SETNICK_FAIL {<user_id>|*} * :Reason
```