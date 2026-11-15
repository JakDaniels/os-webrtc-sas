# WebRTC voice: group and P2P calls cannot start

Patch: [`0001-webrtc-voice-group-and-p2p-calls.patch`](0001-webrtc-voice-group-and-p2p-calls.patch)
(against OpenSim master `9d00a5555225b17d3d1063cb0f3496fb1b6217f9`, 2026-11-07; one file,
`OpenSim/Addons/os-webrtc-janus/WebRtcVoiceRegionModule/WebRtcVoiceRegionModule.cs`; apply with
`git apply -p1`). It is independent of the voice back end (Janus or another `IWebRtcVoiceService`).

## Summary

With WebRTC voice, group voice calls and person-to-person (P2P) calls never connect. The viewer asks the
region for the call's voice channel and gets an empty answer, so it ends the call; and the person called is never
told about the call. Spatial (region and parcel) voice is not affected.

## What the viewer expects

Checked against Firestorm 7.2 (`indra/newview/llvoicechannel.cpp`, `llimview.cpp`, `llvoicewebrtc.cpp`), which
follows the Second Life viewer here.

1. **Getting the channel.** In WebRTC mode a P2P call runs on the same machinery as a group call
   (`LLVoiceChannelGroup` with `mIsP2P`, because the WebRTC client has no separate P2P call interface). To start or
   join a call, the viewer posts to the `ChatSessionRequest` capability:

   ```
   { method: "call", session-id: <IM session id>, alt_params: { preferred_voice_server_type: "webrtc" } }
   ```

   and passes `result["voice_credentials"]` to `setChannelInfo()` (`LLVoiceChannelGroup::voiceCallCapCoro`). It
   expects a map:

   ```
   voice_credentials: { channel_uri: <channel>, channel_credentials: <string>, voice_server_type: "webrtc" }
   ```

   If that is missing, `setChannelInfo()` logs *"Received invalid credentials for channel …"* and calls
   `deactivate()`: the call ends before any voice connection is attempted.

2. **Joining.** With the channel, the viewer provisions a non-spatial connection
   (`LLVoiceWebRTCAdHocConnection::requestVoiceConnection`):

   ```
   { jsep: {offer}, channel_type: "multiagent", channel: <channel_uri>, credentials: <channel_credentials>,
     voice_server_type: "webrtc" }
   ```

   Note the key is **`channel`**. The Janus back end reads `channel_id` (`WebRtcJanusService.cs`), so as it stands
   every call would land in the same Janus room.

3. **Ringing the callee.** The person called learns of a P2P call from an event-queue `ChatterBoxInvitation` whose
   body has a `voice` map (`LLViewerChatterBoxInvitation`, the `voice` branch):

   ```
   { message: "ChatterBoxInvitation",
     body: { session_id, session_name, from_id, from_name,
             voice: { invitation_type: 2 /* P2P_CHAT_SESSION */, channel_uri, channel_credentials, voice_server_type } } }
   ```

   The viewer shows an incoming-call dialog for 30 s and, on accepting, joins with that channel info (it then
   sends `call` itself).

4. **A declined call.** When the callee declines, or its incoming-call dialog runs out after 30 s, the callee's
   viewer posts `decline p2p voice`. The caller's viewer learns of it from a `ChatterBoxSessionAgentListUpdates` event
   for the session listing the callee with `info.can_voice_chat: false` (any transition but `LEAVE`): it then shows
   "P2PCallDeclined" and ends the call (`LLIMMgr::processAgentListUpdates`).

   Which requests a caller sends depends on the IM window: with no window open for the other avatar the viewer
   sends `start p2p voice` and then `call`; with the window already open (e.g. a second attempt) it sends only
   `call`. So ringing has to hang off `call`.

## What OpenSim does now

`WebRtcVoiceRegionModule.ChatSessionRequest` answers `"call"` with a bare `200 OK` (it is in the list of methods
"we do not implement but which do not gate session initialisation"), so step 1 fails. `"start p2p voice"` sends the
caller a `ChatterBoxSessionStartReply` but nothing to the callee, so step 3 never happens; and `"decline p2p voice"`
gets a bare OK, so a caller is never told (step 4) and keeps "calling" until they hang up. Provisioning a
`multiagent` channel (step 2) is passed to the non-spatial voice service without any check of who may use the
channel.

## The change

All in `WebRtcVoiceRegionModule`:

- **`"call"`**: if the agent may use the session's channel (below), answer with `voice_credentials`; the channel is
  the IM session id. Otherwise `403`, which the viewer reports as "voice not allowed". For a P2P session, also ring
  the other party with the `ChatterBoxInvitation` above (if they are a root agent on this simulator), unless they
  are in the call already: then they are the caller, and this `call` is them being answered. Who is in a call comes
  from a small map of viewer session to call channel, filled when a call is provisioned; entries whose viewer
  session has gone from `VoiceViewerSession` are dropped.
- **`"start p2p voice"`**: unchanged (the viewer follows it with `call`, which rings).
- **`"decline p2p voice"`**: tell the caller (if on this simulator) with the agent-list update above.
- **`multiagent` provisioning**: check again that the agent may use the channel, so a viewer cannot join a channel
  it was not given; and copy `channel` to `channel_id`, so back ends that read `channel_id` (os-webrtc-janus) get it.
- **`public const int MultiAgentCallsVersion = 1`**: lets add-ons that worked around this on older versions see
  that the core now handles calls, and step aside.

**Who may use a channel** (`MayUseCallChannel`):

- **Group call**: the session id is the group's id, and the agent must be a member (`IGroupsModule.GetMembershipData`).
- **P2P call**: the session id is the two agents' ids XORed (the viewer's own convention, also used for IM). So the
  channel XORed with the requesting agent's id must give another avatar: an account on the grid, or an avatar
  present on this simulator (a hypergrid visitor). A third person who computes `A ^ B` and asks for that channel
  gets `C ^ A ^ B`, which is no one, and is refused.

`channel_credentials` is left empty: the check is made at the region, both when the channel is handed out and when
it is used, so no secret needs to travel with it.

## Limitations

- **Callees (and callers told of a decline) on another simulator are not reached.** The invitation would have to be passed through the grid (for
  example as an instant message that the callee's simulator turns back into this event), and both simulators would
  need to understand it. The caller still connects. The callee can call back, and that works from either side, since
  both compute the same channel.
- **Ad-hoc conferences** are still refused (`"start conference"` answers `no_ability_error`, as now).
- Group members are not rung when someone starts a group call; they join with the Join Call button in the group
  chat, as for a call already in progress.

## Testing

- Built against OpenSim master (above). The same logic runs, outside core, in this add-on
  (`WebRtcSas/SasCalls.cs`), which replaces `ChatSessionRequest` on versions without this patch. Live tests with
  Firestorm 7.2 on a standalone.
- A group call can be tested with one viewer: start a call from a group's IM window. The provisioning request
  then arrives with `channel_type: "multiagent"` and the group's id as `channel`.
