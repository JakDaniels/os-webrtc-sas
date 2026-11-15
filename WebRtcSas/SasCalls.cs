// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

[assembly: Addin("WebRtcSas", "1.0")]
[assembly: AddinDependency("OpenSim.Region.Framework", OpenSim.VersionInfo.VersionNumber)]

namespace osWebRtcSas
{
    /// <summary>What a call's channel is: a group's voice call, or a two-person (P2P) call.</summary>
    internal enum CallKind
    {
        Group,
        P2P,
    }

    /// <summary>Who may use a call channel (group and P2P voice): checked when the channel is handed out and again on joining.</summary>
    internal static class SasCalls
    {
        /// <summary>The viewer's value for a P2P invitation (EMultiAgentChatSessionType::P2P_CHAT_SESSION).</summary>
        public const int P2PInvitation = 2;

        /// <summary>
        /// Whether <paramref name="agentId"/> may use the call channel <paramref name="channel"/> (an IM session id), and
        /// what it is: a group's call (the session id is the group's id; the agent must be a member), or a P2P call (the
        /// session id is the two agents' ids XORed; XORed with the agent's own id it must give a real avatar, so a third
        /// person who computes A⊕B gets nothing). Null if neither.
        /// </summary>
        public static (CallKind Kind, string Name, UUID Other)? Access(Scene scene, UUID agentId, UUID channel)
        {
            var groups = scene.RequestModuleInterface<IGroupsModule>();
            if (groups?.GetMembershipData(channel, agentId) is { } membership)
            {
                return (CallKind.Group, membership.GroupName ?? "Group call", UUID.Zero);
            }
            var other = new UUID(channel.ulonga ^ agentId.ulonga, channel.ulongb ^ agentId.ulongb);
            if (other == agentId || other.IsZero())
            {
                return null;
            }
            if (IsAvatar(scene, other) is { } name)
            {
                return (CallKind.P2P, name, other);
            }
            return null;
        }

        /// <summary>The avatar's name if <paramref name="id"/> is an account on this grid or an avatar here (a visitor).</summary>
        private static string? IsAvatar(Scene scene, UUID id)
        {
            foreach (var s in SceneManager.Instance.Scenes)
            {
                if (s.TryGetScenePresence(id, out ScenePresence presence) && !presence.IsNPC)
                {
                    return presence.Name;
                }
            }
            var account = scene.UserAccountService?.GetUserAccount(scene.RegionInfo.ScopeID, id);
            return account is null ? null : $"{account.FirstName} {account.LastName}";
        }

        /// <summary>The room for a call: one per channel and grid.</summary>
        public static string RoomFor(string gridHash, UUID channel) =>
            $"call:{(string.IsNullOrEmpty(gridHash) ? "local" : gridHash)}:{channel}";

        public static bool IsCallRoom(string? room) => room?.StartsWith("call:", StringComparison.Ordinal) == true;

        /// <summary>The channel of a call room ("call:{grid}:{channel}").</summary>
        public static UUID? ChannelOf(string? room) =>
            IsCallRoom(room) && UUID.TryParse(room![(room!.LastIndexOf(':') + 1)..], out var channel) ? channel : null;

        /// <summary>Who is in which call on this simulator (filled by the voice service as viewers join and leave).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<(UUID Channel, UUID Agent), int> InCall = new();

        public static void Joined(UUID channel, UUID agent) => InCall.AddOrUpdate((channel, agent), 1, (_, n) => n + 1);

        public static void Left(UUID channel, UUID agent)
        {
            if (InCall.AddOrUpdate((channel, agent), 0, (_, n) => n - 1) <= 0)
            {
                InCall.TryRemove((channel, agent), out _);
            }
        }

        public static bool IsInCall(UUID channel, UUID agent) => InCall.ContainsKey((channel, agent));
    }

    /// <summary>
    /// Group and P2P voice calls for viewers: answers the viewer's <c>ChatSessionRequest</c> capability in place of
    /// OpenSim's WebRtcVoiceRegionModule, which (up to the version this was written against) answers the viewer's
    /// <c>call</c> request without the voice channel and never rings a P2P callee, so those calls cannot start. See
    /// <c>core-patches/0001</c> in this repo for the matching core change; once a core with it is running
    /// (it has <c>WebRtcVoiceRegionModule.MultiAgentCallsVersion</c>), this module stays out of the way.
    /// </summary>
    /// <remarks>
    /// <c>call</c> gets <c>voice_credentials</c> (the channel is the IM session id) after <see cref="SasCalls.Access"/>,
    /// and for a P2P call rings the other person with a <c>ChatterBoxInvitation</c> carrying the channel, unless they
    /// are already in the call (then they are the one who called) and if they are on this simulator (callees elsewhere:
    /// not yet; they can call back). A viewer whose IM window with the callee is already open sends only <c>call</c>,
    /// a new one <c>start p2p voice</c> first, so ringing belongs to <c>call</c>. Every other method is answered as the
    /// core module answers it. The handler is registered once the region is ready, after every module's own, so it
    /// replaces the core one (a later registration of a capability replaces an earlier one).
    /// </remarks>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SasCallsModule")]
    public class SasCallsModule : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC CALLS]";
        private static readonly byte[] UndefAnswer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
        private static readonly byte[] EmptyArrayAnswer = Util.UTF8.GetBytes("<llsd><array /></llsd>");

        private bool _enabled;
        private bool _messageDetails;
        private readonly HashSet<UUID> _hooked = [];

        public string Name => "SasCallsModule";

        public Type? ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var voice = source.Configs["WebRtcVoice"];
            var sas = source.Configs["SasWebRtcVoice"];
            _enabled = voice is not null && voice.GetBoolean("Enabled", false) && sas is not null
                && voice.GetString("NonSpatialVoiceService", string.Empty).Contains("WebRtcSas", StringComparison.OrdinalIgnoreCase)
                && sas.GetBoolean("Calls", true);
            _messageDetails = sas?.GetBoolean("MessageDetails", false) ?? false;
        }

        public void PostInitialise()
        {
            if (!_enabled)
            {
                return;
            }
            // A core that handles calls itself says so; then it is left to it.
            var core = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("osWebRtcVoice.WebRtcVoiceRegionModule", throwOnError: false))
                .FirstOrDefault(t => t is not null);
            if (core?.GetField("MultiAgentCallsVersion", BindingFlags.Public | BindingFlags.Static) is { } marker)
            {
                _enabled = false;
                Log.Info($"{LogHeader} OpenSim's WebRtcVoiceRegionModule handles group and P2P calls (version {marker.GetValue(null)}): not replacing it");
                return;
            }
            Log.Info($"{LogHeader} Group and P2P voice calls enabled (in place of WebRtcVoiceRegionModule's ChatSessionRequest)");
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
            lock (_hooked)
            {
                _hooked.Remove(scene.RegionInfo.RegionID);
            }
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled)
            {
                return;
            }
            // After every module's RegionLoaded (and so after the core module's OnRegisterCaps), before logins.
            scene.EventManager.OnRegionReadyStatusChange += _ => Hook(scene);
            if (scene.Ready)
            {
                Hook(scene);
            }
        }

        public void Close()
        {
        }

        private void Hook(Scene scene)
        {
            lock (_hooked)
            {
                if (!_hooked.Add(scene.RegionInfo.RegionID))
                {
                    return;
                }
            }
            scene.EventManager.OnRegisterCaps += (agentId, caps) =>
                caps.RegisterSimpleHandler("ChatSessionRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (request, response) => ChatSessionRequest(request, response, agentId, scene)));
        }

        private void ChatSessionRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentId, Scene scene)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            if (!scene.TryGetScenePresence(agentId, out ScenePresence caller) || caller.IsDeleted)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            OSDMap? body = null;
            try
            {
                if (request.InputStream.Length > 0)
                {
                    using Stream input = request.InputStream;
                    body = OSDParser.DeserializeLLSDXml(input) as OSDMap;
                }
            }
            catch (Exception e)
            {
                Log.Debug($"{LogHeader} ChatSessionRequest from {agentId}: unreadable body ({e.Message})");
            }
            if (body is null)
            {
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }
            if (_messageDetails)
            {
                Log.Debug($"{LogHeader} ChatSessionRequest from {caller.Name}: {body}");
            }
            if (!body.TryGetString("method", out var method) || !body.TryGetUUID("session-id", out var sessionId))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }
            if (body.TryGetOSDMap("alt_params", out var alt)
                && (alt.TryGetString("voice_server_type", out var type) || alt.TryGetString("preferred_voice_server_type", out type))
                && !string.IsNullOrEmpty(type) && !type.Equals("webrtc", StringComparison.OrdinalIgnoreCase))
            {
                response.RawBuffer = UndefAnswer;
                response.StatusCode = (int)HttpStatusCode.OK;
                return;
            }

            switch (method.ToLowerInvariant())
            {
                case "call":
                    Call(response, scene, caller, sessionId);
                    break;

                case "start p2p voice":
                    StartP2P(response, scene, caller, sessionId, body);
                    break;

                // The callee declined (or let the 30 s incoming-call dialog run out): the caller is told.
                case "decline p2p voice":
                    response.StatusCode = (int)HttpStatusCode.OK;
                    Declined(caller, sessionId);
                    break;

                // As the core module: methods that do not gate the session.
                case "decline invitation":
                case "accept invitation":
                case "invite":
                case "mute update":
                case "session update":
                    response.StatusCode = (int)HttpStatusCode.OK;
                    break;

                case "fetch history":
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.RawBuffer = EmptyArrayAnswer;
                    break;

                // As the core module: ad-hoc conferences are not supported; fail fast rather than time out.
                case "start conference":
                    scene.RequestModuleInterface<IEventQueue>()?.ChatterBoxSessionStartReply(
                        sessionId, string.Empty, 1, false, true, sessionId, false, "no_ability_error", agentId);
                    response.StatusCode = (int)HttpStatusCode.OK;
                    break;

                default:
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    break;
            }
        }

        /// <summary>The viewer asks for the session's voice channel (starting or joining a group or P2P call).</summary>
        private void Call(IOSHttpResponse response, Scene scene, ScenePresence caller, UUID sessionId)
        {
            if (SasCalls.Access(scene, caller.UUID, sessionId) is not { } call)
            {
                Log.Info($"{LogHeader} {caller.Name} may not call on session {sessionId} (not a member of that group, nor a P2P session of theirs)");
                response.StatusCode = (int)HttpStatusCode.Forbidden; // the viewer says "voice not allowed"
                return;
            }
            Log.Info($"{LogHeader} {caller.Name} joins the {(call.Kind == CallKind.Group ? $"group call of {call.Name}" : $"P2P call with {call.Name}")}");
            response.RawBuffer = OSDParser.SerializeLLSDXmlToBytes(new OSDMap { { "voice_credentials", Channel(sessionId) } });
            response.StatusCode = (int)HttpStatusCode.OK;
            if (call.Kind == CallKind.P2P && !SasCalls.IsInCall(sessionId, call.Other))
            {
                Ring(caller, call.Other, sessionId);
            }
        }

        /// <summary>
        /// The caller starts a P2P call: as the core module, the session (the two ids XORed, which the viewer also files
        /// IMs under) is confirmed to the caller; then the callee is rung, if they are on this simulator.
        /// </summary>
        private void StartP2P(IOSHttpResponse response, Scene scene, ScenePresence caller, UUID requestSessionId, OSDMap body)
        {
            if (!body.TryGetUUID("params", out var otherId))
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }
            var sessionId = new UUID(otherId.ulonga ^ caller.UUID.ulonga, otherId.ulongb ^ caller.UUID.ulongb);
            var queue = scene.RequestModuleInterface<IEventQueue>();
            if (queue is null)
            {
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return;
            }
            queue.ChatterBoxSessionStartReply(sessionId, caller.Name, SasCalls.P2PInvitation, false, true, requestSessionId, true, string.Empty,
                caller.UUID);
            response.StatusCode = (int)HttpStatusCode.OK;
            // The viewer follows this with "call", which rings the other person.
        }

        /// <summary>
        /// The callee declined a P2P call: the caller's viewer is sent the session's agent list with the callee unable to
        /// voice chat, which it shows as "declined" and ends the call (LLIMMgr::processAgentListUpdates). Only for
        /// callers on this simulator, like ringing.
        /// </summary>
        private static void Declined(ScenePresence callee, UUID sessionId)
        {
            var callerId = new UUID(sessionId.ulonga ^ callee.UUID.ulonga, sessionId.ulongb ^ callee.UUID.ulongb);
            foreach (var s in SceneManager.Instance.Scenes)
            {
                if (s.TryGetScenePresence(callerId, out ScenePresence caller) && !caller.IsChildAgent && !caller.IsNPC)
                {
                    s.RequestModuleInterface<IEventQueue>()?.ChatterBoxSessionAgentListUpdates(sessionId, callerId,
                        [new GroupChatListAgentUpdateData(callee.UUID, true) { canVoice = false }]);
                    Log.Info($"{LogHeader} {callee.Name} declined the call from {caller.Name}");
                    return;
                }
            }
            Log.Info($"{LogHeader} {callee.Name} declined a call from {callerId}, who is not on this simulator: not told");
        }

        /// <summary>Rings the other person of a P2P call, if they are on this simulator.</summary>
        private static void Ring(ScenePresence caller, UUID otherId, UUID sessionId)
        {
            foreach (var s in SceneManager.Instance.Scenes)
            {
                if (s.TryGetScenePresence(otherId, out ScenePresence callee) && !callee.IsChildAgent && !callee.IsNPC)
                {
                    var invitation = new OSDMap
                    {
                        { "message", "ChatterBoxInvitation" },
                        {
                            "body", new OSDMap
                            {
                                { "session_id", sessionId },
                                { "session_name", caller.Name },
                                { "from_id", caller.UUID },
                                { "from_name", caller.Name },
                                { "voice", Channel(sessionId, SasCalls.P2PInvitation) },
                            }
                        },
                    };
                    s.RequestModuleInterface<IEventQueue>()?.Enqueue((OSD)invitation, otherId);
                    Log.Info($"{LogHeader} {caller.Name} calls {callee.Name}");
                    return;
                }
            }
            Log.Info($"{LogHeader} {caller.Name} calls {otherId}, who is not on this simulator: not rung (calls across simulators are not supported yet)");
        }

        /// <summary>The voice channel as the viewer takes it (voice_credentials, or a voice invitation with its type).</summary>
        private static OSDMap Channel(UUID sessionId, int? invitationType = null)
        {
            var channel = new OSDMap
            {
                { "channel_uri", sessionId.ToString() },
                { "channel_credentials", string.Empty },
                { "voice_server_type", "webrtc" },
            };
            if (invitationType is { } t)
            {
                channel["invitation_type"] = t;
            }
            return channel;
        }
    }
}
