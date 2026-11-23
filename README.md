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

### How voice sounds: per simulator, per region

`[SasWebRtcVoice]` holds the defaults for every region on the simulator; a region overrides the three sound
settings in its own section of `bin/Regions/Regions.ini` (OpenSim passes keys it does not know to modules) under the
same name with `Sas` in front. Everything the module sends to SpatialAudioServer's rooms:

| `[SasWebRtcVoice]` | Per region (`Regions.ini`) | SAS room setting | Changes an existing room? |
|---|---|---|---|
| `SpatialProfile`: the distance curve, `outdoor` (physical fall-off from 2 m, the server's default), `meeting` (SL's Vivox curve: full volume to 10 m, silent from 35 m), or another of the server's `Media:Spatial:Profiles` | `SasSpatialProfile` | `spatialProfile` | no: delete the room |
| `Engine`: the mix engine, `matrix` (the server's default for spatial rooms), `matrix-shared`, `pairchains`, `acoustic` (needs the acousticmultimixer plugin on the server) | `SasEngine` | `engine` | no: delete the room |
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

- **The engine and the distance curve are fixed once a room is made**: after changing them, delete the room on the
  server (admin UI, or `DELETE /api/rooms/by-external/region:{regionId}`) while nobody is in it, and the next viewer
  to join makes it again. Until then the simulator log warns that the room differs.
- **Acoustics can change**: the configured value is applied to an existing room when a viewer first joins it after
  the simulator starts. A change made on the server meanwhile (admin UI) is undone then, unless the region and
  simulator leave `Acoustics` empty.
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

A parcel either shares the region's voice channel (room `region:{regionId}`) or has its own (room
`parcel:{regionId}:{GlobalID}`), set by **Restrict Voice to this parcel** in the viewer's Parcel Details, Sound tab
(the parcel flag `UseEstateVoiceChan` is the opposite of that box). Unlike Second Life, **OpenSim's default parcel has
the box ticked**: a new region's single parcel has a voice room of its own, and parcels subdivided from it copy that.
Untick it on every parcel that should share the region's channel. On the simulator console, `land show <local id>`
lists a parcel's flags: `AllowVoiceChat` (voice on) and `UseEstateVoiceChan` (the region's channel).

The viewer sends its audio to SpatialAudioServer directly, so the server's address and UDP ports must be reachable
from viewers, not only from the simulator.

## Console

- `sas status`: the server's version, load, mix engines and acoustics, and this simulator's voice sessions.
- `sas rooms`: the rooms on the server.

## Supporting the project

If this add-on is useful to you, you can support its development through
[GitHub Sponsors](https://github.com/sponsors/JakDaniels) (also the **Sponsor** button at the top of this page).
Thank you.

## Licence

Mozilla Public License 2.0 (see `LICENSE`).
