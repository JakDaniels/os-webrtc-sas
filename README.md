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

**Status:** region and parcel voice, with positions. Not yet: group, ad-hoc and P2P calls; neighbouring regions; the
viewer's participant list and speaking indicators (the server does not write to the viewer's data channel yet).

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

The viewer sends its audio to SpatialAudioServer directly, so the server's address and UDP ports must be reachable
from viewers, not only from the simulator.

## Console

- `sas status`: the server's version and load, and this simulator's voice sessions.
- `sas rooms`: the rooms on the server.

## Licence

Mozilla Public License 2.0 (see `LICENSE`).
