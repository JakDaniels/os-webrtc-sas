# OpenSim core patches

Changes to OpenSim itself that this add-on would rather not have to work around, written up for OpenSim's issue
tracker (Mantis). The add-on does not need them: it carries its own workaround, and steps aside when the running
OpenSim has the fix, so it works on OpenSim versions with or without them.

| Patch | Problem | Add-on workaround | How the add-on detects the fix |
|---|---|---|---|
| [0001](0001-webrtc-voice-group-and-p2p-calls.md) | WebRTC group and P2P voice calls cannot start | `SasCallsModule` replaces `ChatSessionRequest` | `WebRtcVoiceRegionModule.MultiAgentCallsVersion` exists |
