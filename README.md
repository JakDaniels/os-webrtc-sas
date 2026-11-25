# os-webrtc-sas

Addon module for [OpenSimulator](http://opensimulator.org/) that provides WebRTC voice for current viewers
(Firestorm, Second Life viewer) using **SpatialAudioServer**, an MCU audio server with spatial mixing.

https://github.com/JakDaniels/SpatialAudioServer

It is a back end for OpenSim's own WebRTC voice region module (`OpenSim/Addons/os-webrtc-janus`), (written by MisterBlue here: https://github.com/Misterblue/os-webrtc-janus) and then folded into the OpenSim main codebase. It serves the viewer's voice capabilities and checks estate and parcel voice permissions. Where that add-on talks to a Janus gateway, this one talks to SpatialAudioServer's HTTP API:

| Viewer / OpenSim | SpatialAudioServer |
|---|---|
| voice in a region or a parcel using the estate channel | room `region:{regionId}` |
| a parcel with its own voice channel | room `parcel:{regionId}:{parcel GlobalID}` |
| the avatar | participant, `externalId` = avatar UUID, name and position (global coordinates) |
| the viewer's SDP offer | `POST /signal/{participant}/offer` (`trickle:false`: the answer carries every server candidate) |
| the viewer's ICE candidates | `POST /signal/{participant}/ice` |
| the avatar moving | `POST /api/rooms/by-external/{room}/poses` (every avatar in the room at once) |
| logout, leaving the region | participant removed |
| a group or P2P call (`multiagent`) | non-spatial room `call:{grid}:{IM session id}`, deleted when the call ends |

The join and session tokens stay in the simulator; viewers only see a viewer session id. Their audio goes straight to
SpatialAudioServer over WebRTC (UDP).

Rooms are spatial and `tethered`: the simulator sends avatar positions to the server a few times a second
(`PositionUpdateMilliseconds`), and the viewer reports its own avatar and camera positions over WebRTC, which the server
uses as long as they stay near the simulator's (so nobody can move their ears across the region).

People in the room who are not avatars (web browsers, bots) appear in viewers' voice lists as e.g. "Simon (web)":
their names are registered with the simulator (`RegisterGuestNames`, `GuestLastName`).

They can also be shown in-world as **NPCs** (`WebUserAvatars`): `optional` for those that ask (SpatialAudioServer's web
client has a "Create an NPC in-world" tick box, its load tool `--npc`), `always` for everyone (nobody listens in the
region unseen). The NPC has the voice-list name and copies the look of the avatars in `NpcAppearancesFrom` in turn
(names, avatar UUIDs, or the asset UUIDs of appearance notecards saved with `osAgentSaveAppearance`, which also store
the baked textures; without any it is a cloud); it stands where the participant is in the room and turns with its Facing control, and
viewers show its white dot when it talks. If the NPC is moved in-world (pushed, or by a script) by more than 25 cm, its
voice moves with it. It appears only while the participant's position is inside the room's
region, and goes when the participant leaves. NPC ids are stable while the simulator runs (from a start-up secret, the room and the name), so a bot that rejoins
under the same name, e.g. after a server restart, keeps its entry in viewers' voice lists; removed NPCs and guests are
reported to the server, so viewers that missed the leave drop them.

The module tells each room its `WebUserAvatars` setting, so the web client knows whether to offer the tick box. Needs
OpenSim's NPC module (`[NPC] Enabled`).

### Group and P2P calls

OpenSim's own `WebRtcVoiceRegionModule` cannot start these calls yet: it answers the viewer's `call` request without
the voice channel and never rings a P2P callee (a core patch for OpenSim is written up in
[`core-patches/`](core-patches/)). So this add-on includes a region module, `SasCallsModule`, that answers `ChatSessionRequest`
in its place (`Calls = true`): `call` gets the channel (the IM session id) if the avatar may use it (a member of the
group; for P2P, the session must be theirs with another avatar) and, for P2P, rings the other avatar; a decline
(or the 30 s ring running out) is passed back to the caller, whose viewer then ends the call. The
channel is checked again when the viewer joins. A callee on another simulator is not rung yet (they can call back).
Ad-hoc conferences are refused, as by OpenSim. On an OpenSim whose core handles calls
(`WebRtcVoiceRegionModule.MultiAgentCallsVersion`), the add-on leaves `ChatSessionRequest` to it.

Things to know when trying calls:

- **Group calls start muted.** The viewer turns the microphone off on joining a group (or ad-hoc) call; unmute with
  Speak. P2P calls start unmuted. Group members are not rung: they join with Join Call in the group's IM window.
- **Groups on a SQLite standalone.** OpenSim's groups (V2) store only in MySQL/MariaDB or PostgreSQL, but they can
  use one alone while everything else stays on SQLite:

  ```ini
  [Groups]
      Enabled = true
      Module = "Groups Module V2"
      StorageProvider = OpenSim.Data.MySQL.dll
      ConnectionString = "Data Source=localhost;Database=opensim_groups;User ID=opensim;Password=***;Old Guids=true;SslMode=None;"
      ServicesConnectorModule = "Groups Local Service Connector"
      LocalService = local
      MessagingEnabled = true
      MessagingModule = "Groups Messaging Module V2"
      MessageOnlineUsersOnly = true   ; without it, V2 group messaging (and so group IM) switches itself off
  ```

  and in `config-include/storage/SQLiteStandalone.ini` give `[GridUserService]` its provider,
  `StorageProvider = "OpenSim.Data.SQLite.dll"`: the groups service also reads grid users (online status), and with
  no provider there it pairs SQLite's `griduser.db` connection string with the MySQL driver and stops the simulator
  at start-up ("Could not find a storage interface for the given users module"). For MariaDB, OpenSim's usual
  settings are `max_allowed_packet = 128M` and no strict mode (`sql_mode = NO_ENGINE_SUBSTITUTION`); the groups
  tables declare their own character set.

**Status:** region and parcel voice with positions, neighbouring regions, per-person mute and volume, web users as
NPCs, group and P2P calls (callees on the same simulator). Not yet: ringing across simulators, ad-hoc conferences.

## Building

Clone into an OpenSim source tree's `addon-modules` folder and build OpenSim as usual (.NET 8 SDK):

```bash
cd opensim/addon-modules
git clone git@github.com:JakDaniels/os-webrtc-sas.git
cd ..
./runprebuild.sh
dotnet build -c Release OpenSim.sln          # or: dotnet build -c Release addon-modules/os-webrtc-sas/WebRtcSas/WebRtcSas.csproj
```

This builds `bin/WebRtcSas.dll`.

## Configuring

```bash
mkdir -p bin/config
cp addon-modules/os-webrtc-sas/os-webrtc-sas.ini.example bin/config/os-webrtc-sas.ini
```

Set `ServerURI` and `APIKey` in `[SasWebRtcVoice]` (create a key on the server with
`SpatialAudioServer apikey create <label>`). Voice must also be allowed in the estate and parcel settings.

**The simulator must give viewers STUN servers.** On OpenSim grids, WebRTC viewers take their STUN servers from
the region's simulator features (`stun-servers`), which OpenSim fills from `StunServers`. A region that sets none
gives viewers an empty list, and their voice never starts: the viewer cannot create its WebRTC connection (its log
says `ICE server parsing failed: Empty uri`), so it never sends an offer, the simulator log shows only `logout`
requests every few seconds, and no room is made on the server. OpenSim's `OpenSim.ini.example` sets it in
`[Startup]`, but an `OpenSim.ini` written another way (a grid's own, such as OSGrid's) may not have it; the example
file sets it too:

```ini
[Startup]
    StunServers = stun.l.google.com:19302, stun.cloudflare.com:3478
```

`host:port`, comma-separated (OpenSim adds `stun:`). OpenSim also reads it from `[Const]`, `[GridInfo]` and
`[SimulatorFeatures]` (the last of these that has it wins). Viewers use these to find their own public address; SpatialAudioServer
does not need them.

### How voice sounds: per simulator, per region

`[SasWebRtcVoice]` holds the defaults for every region on the simulator; a region overrides the three sound
settings in its own section of `bin/Regions/Regions.ini` (OpenSim passes keys it does not know to modules) under the
same name with `Sas` in front. Everything the module sends to SpatialAudioServer's rooms:

| `[SasWebRtcVoice]` | Per region (`Regions.ini`) | SAS room setting | Changes an existing room? |
|---|---|---|---|
| `SpatialProfile`: the distance curve, `outdoor` (physical fall-off from 2 m, the server's default), `meeting` (SL's Vivox curve: full volume to 10 m, silent from 35 m), or another of the server's `Media:Spatial:Profiles` | `SasSpatialProfile` | `spatialProfile` | yes, when the room is next empty (see below) |
| `Engine`: the mix engine, `matrix` (the server's default for spatial rooms), `matrix-shared`, `pairchains`, `acoustic` (needs the acousticmultimixer plugin on the server) | `SasEngine` | `engine` | yes, when the room is next empty (see below) |
| `Acoustics`: the kind of space (early reflections and reverb), `outdoor` (none), `tiny-room`, `small-room`, `big-room`, `hall`, `long-hall`, `cave`, `cathedral`; heard only with the `acoustic` engine | `SasAcoustics` | `acoustics` | yes, when a viewer first joins it after the simulator starts |
| `WebUserAvatars` (`off`, `optional`, `always`) | — | `inWorldAvatars` | yes, on every join |
| `MaxParticipantsPerRoom` (0: the server's default) | — | `maxParticipants` | no |
| (fixed) | — | `spatial: true`, `clientPose: tethered` | no |

The module's other settings stay in the simulator: `PositionUpdateMilliseconds` (how often avatar positions are
sent), `Calls`, `RegisterGuestNames` / `GuestLastName`, `NpcAppearancesFrom`, and the connection (`ServerURI`,
`APIKey`, `AcceptSelfSignedCertificate`, `RequestTimeoutSeconds`).

```ini
[Great Hall]
RegionUUID = ...
Location = 1000,1000
SasEngine = acoustic
SasAcoustics = cathedral
```

Empty or missing: the simulator's setting; `default`: the server's default. A parcel with its own voice channel
sounds like its region. Group and P2P calls are not spatial and use none of these.

- **Changing them later:** the configured values are sent to an existing room when a viewer first joins it after
  the simulator starts (so they also undo a change made on the server meanwhile, unless left empty). Acoustics change
  at once. The engine and the distance curve change when nobody is in the room: at once if it is empty, otherwise
  the server keeps the running mix until the last person leaves (people in it are not disturbed), and the room shows
  what is still in use as `inUse`.
- **A setting the server does not have** (a typo, or `acoustic` on a server without the plugin) is logged, and the
  room is made with the server's defaults, so voice still works. `sas status` lists the server's engines and
  acoustics.
- The simulator log shows each room's engine, curve and acoustics the first time a viewer joins it.

Set only in SpatialAudioServer's own configuration, for all rooms (its `docs/configuration.md`): the fade before the
cut-off (`Media:Spatial:Taper`), how close the room sound starts to fall away (`Media:Spatial:NearDistance`), how far
viewers' own positions may stray from the simulator's (`Media:Spatial:SpeakerTether`, `ListenerTether`), the
distance curves themselves (`Media:Spatial:Profiles`), and the engine used when a region sets none
(`Media:SpatialEngine`).

### Parcels and the region's voice channel

Rooms stay on the server until they are deleted (only their media stops while nobody is in them). When a region
starts, the module deletes the rooms of its parcels that no longer exist, e.g. after joining parcels
(`DeleteOldParcelRooms = true`, the default); rooms with people in them are left until next time. A parcel that only
switched to the region's channel keeps its room, and so its settings.


A parcel either shares the region's voice channel (room `region:{regionId}`) or has its own (room
`parcel:{regionId}:{GlobalID}`), set by **Restrict Voice to this parcel** in the viewer's Parcel Details, Sound tab
(the parcel flag `UseEstateVoiceChan` is the opposite of that box). Unlike Second Life, **OpenSim's default parcel has
the box ticked**: a new region's single parcel has a voice room of its own, and parcels subdivided from it copy that.
Untick it on every parcel that should share the region's channel. On the simulator console, `land show <local id>`
lists a parcel's flags: `AllowVoiceChat` (voice on) and `UseEstateVoiceChan` (the region's channel).

The viewer sends its audio to SpatialAudioServer directly, so the server's address and UDP ports must be reachable
from viewers, not only from the simulator.

### Setting a voice room's sound from in-world

The module adds script functions. They are OpenSim functions registered by a module, so they need
`[OSSL] AllowMODFunctions = true` (on in OpenSim's default `config-include/osslDefaultEnable.ini`), and
`ScriptFunctions = true` in `[SasWebRtcVoice]` (the default):

| Function | Does |
|---|---|
| `string sasSetRoomParameters(string notecard)` | sets the room from a notecard in the object; `""`, or why not |
| `string sasGetRoomParameters(string notecard)` | saves the room's settings to a notecard in the object (replacing one of that name); `""`, or why not |
| `string sasSetRoomParametersText(string text)` | as `sasSetRoomParameters`, from text in the same format |
| `string sasGetRoomParametersText()` | the room's settings as text; `""` on failure (the reason goes to the debug channel) |

They act on the voice room of the parcel the object stands on: the parcel's own room, or the region's if the parcel
uses the region's channel. The notecard is `key = value` lines (`#` or `//` start a comment):

```
room = parcel
acoustics = cathedral
```

- `room` (required): `parcel` or `region`, which room the settings are for. If a parcel is switched to the
  region's channel, its notecard (`room = parcel`) is refused instead of changing the whole region.
- `acoustics`: as in the table above.
- `engine`, `distance-curve`: as in the table above (`distance-curve = default`: the server's). They change when
  nobody is in the room: at once if it is empty, else when the last person leaves; until then the getter adds a
  comment saying what is still in use.
- An unknown key, a repeated key or a bad value refuses the whole set.

**Who:** a parcel's own room may be set by an object whose owner owns the parcel (or is deeded to the parcel's
group), or by an estate owner or manager; the region's room only by estate owners and managers. Anyone may read.
Worn objects cannot set a room.

**One object controls a room.** The first object to set a room controls it (it is recorded with the room on the
server, so it survives restarts). While it is still in the region, standing in that room, other objects are refused,
and the refusal names it and says where it is. Delete it (or take it) and the next object to set the room takes over.
A room a script has set keeps its settings: the configuration's `Acoustics`, `Engine` and `SpatialProfile` (and
their `Sas…` overrides) no longer apply to it while its controlling object is there. Refusals are also logged by the simulator.

**Ready-made script:** [`scripts/sas-room-acoustics.lsl`](scripts/sas-room-acoustics.lsl). Put it in an object
with an `Acoustics` notecard and rez it on the parcel: it applies the notecard when rezzed, when the region starts
and whenever the notecard is edited, and tells its owner the result. The owner touches it, and answers Yes,
to save the room's current settings to the notecard (handy to start one: touch it before adding a notecard).

To try it:

1. Rez a box on the parcel (as its owner, or as an estate manager for the region's room).
2. In the box's Contents, create a new script, replace its text with the script below, and save it. It says "No
   'Acoustics' notecard yet".
3. Touch the box and answer Yes: it saves the room's current settings to a new `Acoustics` notecard.
4. Open the notecard, change `acoustics` (and `engine`, if the room is not on `acoustic` yet), and save it: the box
   applies it and says so.

A notecard as the box saves it (the `#` lines are comments):

```
# Voice room "SAS Test: Big Parcel", saved 2026-11-25 20:10 UTC.
# room: which voice room these settings are for (parcel: the parcel's own; region: the region's).
room = parcel
acoustics = cathedral
# The engine and distance curve change when nobody is in the room (at once if it is empty).
engine = acoustic
distance-curve = default
```

<details>
<summary>The script (<code>scripts/sas-room-acoustics.lsl</code>)</summary>

```lsl
// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
//
// SAS room acoustics: sets how the voice room this object stands in sounds, from the "Acoustics" notecard in the
// object. Applied when rezzed, when the region starts and whenever the notecard changes. The owner touches the
// object and answers Yes to save the room's current settings to the notecard (replacing it), e.g. to start one.
//
// The notecard ("key = value" lines; # or // starts a comment):
//   room = parcel              which voice room: parcel (the parcel's own) or region (the region's)
//   acoustics = hall           outdoor, tiny-room, small-room, big-room, hall, long-hall, cave, cathedral
//   engine = acoustic          the server's mix engine (acoustics are heard only with "acoustic")
//   distance-curve = default   outdoor, meeting, or default (the server's)
// The engine and distance curve change when nobody is in the room (at once if it is empty).
//
// Needs the os-webrtc-sas module, and [OSSL] AllowMODFunctions = true (OpenSim's default). A parcel's own room may be
// set by the parcel owner or an estate owner/manager; the region's room by estate owners/managers only. One object
// controls a room: while it is there, other objects are refused.

string NOTECARD = "Acoustics";
key applied; // the notecard version last applied, so saving it does not apply it again
integer channel; // the Yes/No dialog's
integer listener;

closeDialog()
{
    llListenRemove(listener);
    listener = 0;
    llSetTimerEvent(0);
}

save()
{
    string error = sasGetRoomParameters(NOTECARD);
    if (error == "")
    {
        applied = llGetInventoryKey(NOTECARD);
        llOwnerSay("Saved the voice room's settings to '" + NOTECARD + "'.");
    }
    else
    {
        llOwnerSay("Could not save the voice room's settings: " + error);
    }
}

apply()
{
    if (llGetInventoryType(NOTECARD) != INVENTORY_NOTECARD)
    {
        llOwnerSay("No '" + NOTECARD + "' notecard yet: touch me to save the voice room's current settings to one.");
        return;
    }
    key version = llGetInventoryKey(NOTECARD);
    if (version == applied && version != NULL_KEY)
    {
        return;
    }
    string error = sasSetRoomParameters(NOTECARD);
    if (error == "")
    {
        applied = version;
        llOwnerSay("Voice room settings applied from '" + NOTECARD + "'.");
    }
    else
    {
        llOwnerSay("Voice room settings not applied: " + error);
    }
}

default
{
    state_entry()
    {
        applied = NULL_KEY;
        apply();
    }

    on_rez(integer start)
    {
        llResetScript();
    }

    changed(integer change)
    {
        if (change & CHANGED_REGION_START)
        {
            applied = NULL_KEY;
            apply();
        }
        else if (change & CHANGED_INVENTORY)
        {
            apply();
        }
    }

    touch_start(integer count)
    {
        key owner = llGetOwner();
        if (llDetectedKey(0) != owner)
        {
            return;
        }
        closeDialog();
        channel = -1000000 - (integer)llFrand(1000000000);
        listener = llListen(channel, "", owner, "");
        string question = "Save the voice room's current settings to the '" + NOTECARD + "' notecard?";
        if (llGetInventoryType(NOTECARD) == INVENTORY_NOTECARD)
        {
            question += "\n\nThis replaces the notecard, and anything written in it.";
        }
        llDialog(owner, question, ["Yes", "No"], channel);
        llSetTimerEvent(60); // the question lapses unanswered
    }

    listen(integer ch, string name, key id, string answer)
    {
        closeDialog();
        if (answer == "Yes")
        {
            save();
        }
    }

    timer()
    {
        closeDialog();
    }
}
```

</details>

## Console

- `sas status`: the server's version, load, mix engines and acoustics, and this simulator's voice sessions.
- `sas rooms`: the rooms on the server.

## Supporting the project

If this add-on is useful to you, you can support its development through
[GitHub Sponsors](https://github.com/sponsors/JakDaniels) (also the **Sponsor** button at the top of this page).
Thank you.

## Licence

Mozilla Public License 2.0 (see `LICENSE`).
