// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using osWebRtcVoice;

namespace osWebRtcSas
{
    /// <summary>
    /// The voice back end for OpenSim's WebRTC region module (OpenSim/Addons/os-webrtc-janus/WebRtcVoiceRegionModule),
    /// loaded through <c>[WebRtcVoice] SpatialVoiceService = WebRtcSas.dll:SasVoiceService</c>. The region module serves
    /// the viewer's voice capabilities and checks estate and parcel permissions; this turns its calls into
    /// SpatialAudioServer requests: a room per region (or per parcel with its own voice channel), the avatar as a
    /// participant, and the viewer's SDP offer and ICE candidates passed through the server's signalling endpoints.
    /// </summary>
    /// <remarks>
    /// First milestone: region and parcel voice (<c>channel_type: local</c>) only; group, ad-hoc and P2P calls
    /// (<c>multiagent</c>) are refused. Configuration: <c>[SasWebRtcVoice]</c>, see os-webrtc-sas.ini.example.
    /// </remarks>
    public class SasVoiceService : IWebRtcVoiceService
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC VOICE]";
        private static int _consoleRegistered;

        private readonly SasClient? _client;
        private readonly int _maxParticipants;
        private readonly bool _messageDetails;

        /// <summary>Our sessions, by viewer session id. OpenSim's own registry forgets them before telling us about some logouts.</summary>
        private readonly ConcurrentDictionary<string, SasViewerSession> _sessions = new();

        public SasVoiceService(IConfigSource config)
        {
            var voice = config.Configs["WebRtcVoice"];
            var sas = config.Configs["SasWebRtcVoice"];
            if (voice is null || !voice.GetBoolean("Enabled", false))
            {
                return;
            }
            if (sas is null)
            {
                Log.Error($"{LogHeader} No [SasWebRtcVoice] section: voice is disabled");
                return;
            }
            var server = sas.GetString("ServerURI", string.Empty);
            var apiKey = sas.GetString("APIKey", string.Empty);
            if (!Uri.TryCreate(server.EndsWith('/') ? server : server + "/", UriKind.Absolute, out var serverUri) || string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Error($"{LogHeader} [SasWebRtcVoice] needs ServerURI (e.g. http://10.0.0.5:5000) and APIKey: voice is disabled");
                return;
            }
            _maxParticipants = sas.GetInt("MaxParticipantsPerRoom", 0);
            _messageDetails = sas.GetBoolean("MessageDetails", false);
            _client = new SasClient(serverUri, apiKey, TimeSpan.FromSeconds(sas.GetInt("RequestTimeoutSeconds", 10)),
                sas.GetBoolean("AcceptSelfSignedCertificate", false));

            try
            {
                var status = _client.Status();
                Log.Info($"{LogHeader} Using SpatialAudioServer {(string?)status["version"]} at {serverUri}");
            }
            catch (SasException e)
            {
                // Not fatal: the server may start after the simulator. Each request reports its own failure.
                Log.Warn($"{LogHeader} SpatialAudioServer at {serverUri} is not answering yet: {e.Message}");
            }
            RegisterConsoleCommands();
        }

        // ------------------------------------------------------------------------------------- IWebRtcVoiceService

        public IVoiceViewerSession? CreateViewerSession(OSDMap request, UUID agentId, UUID regionId)
        {
            if (_client is null)
            {
                return null;
            }
            var channelType = request.TryGetString("channel_type", out var type) ? type : string.Empty;
            if (channelType != "local")
            {
                Log.Info($"{LogHeader} {channelType} voice (group, ad-hoc and P2P calls) is not supported yet; agent {agentId}");
                return null;
            }
            var session = new SasViewerSession(this, regionId, agentId);
            _sessions[session.ViewerSessionID] = session;
            return session;
        }

        public OSDMap ProvisionVoiceAccountRequest(IVoiceViewerSession viewerSession, OSDMap request, UUID agentId, UUID regionId)
        {
            if (viewerSession is not SasViewerSession session || _client is null)
            {
                return Failed("voice service not available");
            }
            if (_messageDetails)
            {
                Log.Debug($"{LogHeader} Provision request {session}: {Redact(request)}");
            }
            if (request.TryGetBool("logout", out var logout) && logout)
            {
                Disconnect(session, "logout");
                return new OSDMap { { "response", "closed" } };
            }
            if (!request.TryGetOSDMap("jsep", out var jsep) || !jsep.TryGetString("type", out var jsepType) || jsepType != "offer"
                || !jsep.TryGetString("sdp", out var offer))
            {
                return Failed("no SDP offer");
            }

            lock (session.Lock)
            {
                try
                {
                    if (!SceneManager.Instance.TryGetScene(regionId, out var scene))
                    {
                        return Failed("region not found");
                    }
                    var (roomId, roomName) = RoomFor(scene, request);
                    var (displayName, pose) = AvatarOf(scene, agentId);

                    // Moving to another room (e.g. into a parcel with its own channel) on the same connection: leave the old one.
                    if (session.ParticipantId is { } old && session.RoomExternalId is { } oldRoom && oldRoom != roomId)
                    {
                        _client.RemoveParticipant(oldRoom, old);
                    }

                    _client.EnsureRoom(roomId, roomName, spatial: true, _maxParticipants);
                    var (participantId, joinToken) = _client.AddParticipant(roomId, displayName, agentId.ToString(), pose);
                    var (answer, sessionToken) = _client.Offer(participantId, joinToken, offer);

                    session.RoomExternalId = roomId;
                    session.ParticipantId = participantId;
                    session.SessionToken = sessionToken;
                    session.VoiceServiceSessionId = participantId.ToString();
                    Log.Info($"{LogHeader} {displayName} joined voice room {roomName} ({session})");

                    return new OSDMap
                    {
                        { "jsep", new OSDMap { { "type", "answer" }, { "sdp", answer } } },
                        { "viewer_session", session.ViewerSessionID },
                    };
                }
                catch (SasException e)
                {
                    Log.Warn($"{LogHeader} Provision failed for agent {agentId}: {e.Message}");
                    return Failed(e.Status == 0 ? "voice server not reachable" : "voice server refused the connection");
                }
            }
        }

        public OSDMap VoiceSignalingRequest(IVoiceViewerSession viewerSession, OSDMap request, UUID agentId, UUID regionId)
        {
            if (viewerSession is not SasViewerSession session || _client is null)
            {
                return Failed("voice service not available");
            }
            var candidates = new List<(string, uint)>();
            var completed = false;
            if (request.TryGetOSDArray("candidates", out var array))
            {
                foreach (var item in array.OfType<OSDMap>())
                {
                    AddCandidate(item, candidates);
                }
            }
            if (request.TryGetOSDMap("candidate", out var single))
            {
                if (single.TryGetBool("completed", out var done) && done)
                {
                    completed = true;
                }
                else
                {
                    AddCandidate(single, candidates);
                }
            }
            if (candidates.Count == 0 && !completed)
            {
                return Failed("no candidates");
            }

            lock (session.Lock)
            {
                if (session.ParticipantId is not { } participantId || session.SessionToken is not { } token)
                {
                    return Failed("not connected");
                }
                try
                {
                    _client.SendCandidates(participantId, token, candidates, completed);
                    return new OSDMap { { "response", "ok" } };
                }
                catch (SasException e)
                {
                    Log.Warn($"{LogHeader} Candidates for {session} not accepted: {e.Message}");
                    return Failed("voice server refused the candidates");
                }
            }
        }

        /// <summary>
        /// Without a session: OpenSim's region module sends a logout with viewer_session 0 when an agent logs out, after
        /// it has already forgotten the agent's sessions. Close every one of ours for that agent in that region.
        /// </summary>
        public OSDMap ProvisionVoiceAccountRequest(OSDMap request, UUID agentId, UUID regionId)
        {
            if (request.TryGetBool("logout", out var logout) && logout)
            {
                foreach (var session in _sessions.Values.Where(s => s.AgentId == agentId && s.RegionId == regionId).ToList())
                {
                    VoiceViewerSession.RemoveViewerSession(session.ViewerSessionID);
                    Disconnect(session, "agent logout");
                }
                return new OSDMap { { "response", "closed" } };
            }
            return Failed("no viewer session");
        }

        public OSDMap VoiceSignalingRequest(OSDMap request, UUID agentId, UUID regionId) => Failed("no viewer session");

        // ------------------------------------------------------------------------------------- helpers

        /// <summary>Leaves the session's room on the server and forgets the session. Safe to call more than once.</summary>
        internal void Disconnect(SasViewerSession session, string reason)
        {
            _sessions.TryRemove(session.ViewerSessionID, out _);
            lock (session.Lock)
            {
                if (session.ParticipantId is not { } participantId || session.RoomExternalId is not { } room || _client is null)
                {
                    return;
                }
                session.ParticipantId = null;
                session.SessionToken = null;
                try
                {
                    _client.RemoveParticipant(room, participantId);
                    Log.Info($"{LogHeader} Left voice room {room} ({reason}): agent {session.AgentId}");
                }
                catch (SasException e)
                {
                    Log.Warn($"{LogHeader} Could not remove participant {participantId} from {room} ({reason}): {e.Message}");
                }
            }
        }

        /// <summary>
        /// The room for a local-voice request: the region's (estate) channel, or a parcel's own channel. OpenSim's region
        /// module has already checked permissions and removed parcel_local_id when the parcel uses the estate channel.
        /// Parcels are keyed by their GlobalID, which survives subdividing and joining (the local id does not).
        /// </summary>
        private static (string ExternalId, string Name) RoomFor(Scene scene, OSDMap request)
        {
            var regionName = scene.RegionInfo.RegionName;
            if (request.TryGetInt("parcel_local_id", out var localId) && scene.LandChannel?.GetLandObject(localId) is ILandObject parcel
                && parcel.LandData is { } land)
            {
                return ($"parcel:{scene.RegionInfo.RegionID}:{land.GlobalID}", $"{regionName}: {land.Name}");
            }
            return ($"region:{scene.RegionInfo.RegionID}", regionName);
        }

        /// <summary>The avatar's name and pose in global coordinates (region origin + position in the region).</summary>
        private static (string Name, SasPose? Pose) AvatarOf(Scene scene, UUID agentId)
        {
            if (!scene.TryGetScenePresence(agentId, out ScenePresence presence))
            {
                return (agentId.ToString(), null);
            }
            var p = presence.AbsolutePosition;
            var q = presence.Rotation;
            var pose = new SasPose(scene.RegionInfo.WorldLocX + p.X, scene.RegionInfo.WorldLocY + p.Y, p.Z, q.X, q.Y, q.Z, q.W);
            return (presence.Name, pose);
        }

        private static void AddCandidate(OSDMap map, List<(string, uint)> into)
        {
            if (map.TryGetString("candidate", out var candidate) && !string.IsNullOrWhiteSpace(candidate))
            {
                var index = map.TryGetValue("sdpMLineIndex", out var i) ? (uint)Math.Max(0, i.AsInteger()) : 0u;
                into.Add((candidate, index));
            }
        }

        private static OSDMap Failed(string error) => new() { { "response", "failed" }, { "error", error } };

        /// <summary>The request for the log, without the SDP (long, and it holds the viewer's addresses).</summary>
        private static string Redact(OSDMap request)
        {
            var copy = new OSDMap();
            foreach (KeyValuePair<string, OSD> entry in request)
            {
                copy[entry.Key] = entry.Key == "jsep" ? OSD.FromString("(sdp)") : entry.Value;
            }
            return OSDParser.SerializeJsonString(copy);
        }

        // ------------------------------------------------------------------------------------- console

        private void RegisterConsoleCommands()
        {
            if (MainConsole.Instance is null || System.Threading.Interlocked.Exchange(ref _consoleRegistered, 1) != 0)
            {
                return;
            }
            MainConsole.Instance.Commands.AddCommand("WebRtcSas", false, "sas status", "sas status",
                "Show SpatialAudioServer's status and this simulator's voice sessions", (_, _) => ShowStatus());
            MainConsole.Instance.Commands.AddCommand("WebRtcSas", false, "sas rooms", "sas rooms",
                "List the rooms on SpatialAudioServer", (_, _) => ShowRooms());
        }

        private void ShowStatus()
        {
            var console = MainConsole.Instance;
            if (_client is null)
            {
                console.Output("SpatialAudioServer voice is not configured.");
                return;
            }
            try
            {
                var status = _client.Status();
                console.Output($"SpatialAudioServer {(string?)status["version"]} at {_client.Server}: " +
                    $"{(int?)status["rooms"]} rooms, {(int?)status["participants"]} participants, GStreamer {(string?)status["gstreamer"]?["version"]}");
            }
            catch (SasException e)
            {
                console.Output($"SpatialAudioServer at {_client.Server}: {e.Message}");
            }
            console.Output($"Voice sessions on this simulator: {_sessions.Count}");
            foreach (var session in _sessions.Values)
            {
                console.Output($"  {session}");
            }
        }

        private void ShowRooms()
        {
            var console = MainConsole.Instance;
            if (_client is null)
            {
                console.Output("SpatialAudioServer voice is not configured.");
                return;
            }
            try
            {
                foreach (var room in _client.Rooms().OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    console.Output($"  {(string?)room["name"],-40} {(string?)room["externalId"],-60} " +
                        $"{(int?)room["participantCount"]} in, {(string?)room["engine"]}{((bool?)room["spatial"] == true ? ", spatial" : "")}");
                }
            }
            catch (SasException e)
            {
                console.Output($"SpatialAudioServer at {_client.Server}: {e.Message}");
            }
        }
    }
}
