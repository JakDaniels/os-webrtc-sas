# OpenSim core patches

Changes to OpenSim itself that this add-on would rather not have to work around, written up for OpenSim's issue
tracker (Mantis). The add-on does not need them: it carries its own workaround, and steps aside when the running
OpenSim has the fix, so it works on OpenSim versions with or without them.

| Patch | Problem | Mantis | Add-on workaround | How the add-on detects the fix |
|---|---|---|---|---|
| [0001](0001-webrtc-voice-group-and-p2p-calls.md) | WebRTC group and P2P voice calls cannot start | [0009265](http://opensimulator.org/mantis/view.php?id=9265) | `SasCallsModule` replaces `ChatSessionRequest` | `WebRtcVoiceRegionModule.MultiAgentCallsVersion` exists |
| [0002](0002-ubode-npc-rest-position.md) | ubODE throws a new NPC towards the region's corner about a second after it appears | not filed yet | `WebUserAvatars` puts an NPC back if it moves in its first 5 s | not needed: with the fix nothing moves, so the workaround never acts |
