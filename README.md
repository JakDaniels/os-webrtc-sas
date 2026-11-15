# os-webrtc-sas

Addon module for [OpenSimulator](http://opensimulator.org/) that provides WebRTC voice for current viewers
(Firestorm, Second Life viewer) using **SpatialAudioServer**, an MCU audio server with spatial mixing.

It is a back end for OpenSim's own WebRTC voice region module (`OpenSim/Addons/os-webrtc-janus`), which serves the
viewer's voice capabilities and checks estate and parcel voice permissions. Where that add-on talks to a Janus
gateway, this one talks to SpatialAudioServer's HTTP API:

| Viewer / OpenSim | SpatialAudioServer |
|---|---|
| voice in a region or a parcel using the estate channel | room `region:{regionId}` |
| a parcel with its own voice channel | room `parcel:{regionId}:{parcel GlobalID}` |
| the avatar | participant, `externalId` = avatar UUID, name and position (global coordinates) |
| the viewer's SDP offer | `POST /signal/{participant}/offer` (`trickle:false`: the answer carries every server candidate) |
| the viewer's ICE candidates | `POST /signal/{participant}/ice` |
| the avatar moving | `POST /api/rooms/by-external/{room}/poses` (every avatar in the room at once) |
| logout, leaving the region | participant removed |

The join and session tokens stay in the simulator; viewers only see a viewer session id. Their audio goes straight to
SpatialAudioServer over WebRTC (UDP).

Rooms are spatial and `tethered`: the simulator sends avatar positions to the server a few times a second
(`PositionUpdateMilliseconds`), and the viewer reports its own avatar and camera positions over WebRTC, which the server
uses as long as they stay near the simulator's (so nobody can move their ears across the region).

People in the room who are not avatars (web browsers, bots) appear in viewers' voice lists as e.g. "Simon (web)":
their names are registered with the simulator (`RegisterGuestNames`, `GuestLastName`).

They can also be shown in-world as **NPCs** (`WebUserAvatars`): `optional` for those that ask (SpatialAudioServer's web
client has a "Create an NPC in-world" tick box, its load tool `--npc`), `always` for everyone (nobody listens in the
region unseen). The NPC has the default appearance and the voice-list name, stands where the participant is in the
room and turns with its Facing control; viewers show its white dot when it talks. It appears only while the
participant's position is inside the room's region, and goes when the participant leaves. The module tells each room
its setting, so the web client knows whether to offer the tick box. Needs OpenSim's NPC module (`[NPC] Enabled`).

**Status:** region and parcel voice with positions, neighbouring regions, per-person mute and volume, web users as
NPCs. Not yet: group, ad-hoc and P2P calls.

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

- `sas status`: the server's version and load, and this simulator's voice sessions.
- `sas rooms`: the rooms on the server.

## Licence

Mozilla Public License 2.0 (see `LICENSE`).
