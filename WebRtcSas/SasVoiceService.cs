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
    /// Region and parcel voice (<c>channel_type: local</c>), and group and P2P calls (<c>multiagent</c>, with
    /// <see cref="SasCallsModule"/>: a non-spatial room per call, removed when the last person leaves); ad-hoc
    /// conferences are refused. Configuration: <c>[SasWebRtcVoice]</c>, see os-webrtc-sas.ini.example.
    /// </remarks>
    public class SasVoiceService : IWebRtcVoiceService
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC VOICE]";
        private static int _consoleRegistered;

        /// <summary>Rooms let the viewer report its own avatar and camera positions, near the simulator's (see the server's PLAN.md).</summary>
        private const string ClientPose = "tethered";

        /// <summary>An avatar that has not moved is still sent this often, so a missed update cannot stick.</summary>
        private static readonly TimeSpan PoseRefresh = TimeSpan.FromSeconds(5);

        private readonly SasClient? _client;
        private readonly int _maxParticipants;

        /// <summary>Group and P2P calls (multiagent channels) are taken.</summary>
        private readonly bool _calls;

        /// <summary>The distance curve for new rooms (the server's Media:Spatial:Profiles); null = the server's default.</summary>
        private readonly string? _spatialProfile;
        private readonly bool _messageDetails;
        private readonly System.Threading.Timer? _poseTimer;
        private readonly System.Threading.Timer? _nameTimer;
        private readonly string _guestLastName = "(web)";
        private readonly bool _registerGuestNames;
        private int _registeringNames;

        /// <summary>Web and bot participants shown in-world as NPCs (WebUserAvatars).</summary>
        private readonly SasWebUserAvatars? _webUserAvatars;
        private readonly string _webUserAvatarsMode = SasWebUserAvatars.Off;

        /// <summary>Rooms told this simulator's WebUserAvatars setting.</summary>
        private readonly ConcurrentDictionary<string, bool> _avatarSettingSent = new();

        /// <summary>Participant id → the name registered for it with OpenSim (browsers and bots) and its room.</summary>
        private readonly ConcurrentDictionary<UUID, (string Name, string Room)> _guestNames = new();
        private int _sendingPoses;
        private DateTime _poseErrorLoggedAt;
        private readonly ConcurrentDictionary<string, bool> _roomsChecked = new();

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
            _calls = sas.GetBoolean("Calls", true);
            _spatialProfile = sas.GetString("SpatialProfile", string.Empty).Trim() is { Length: > 0 } profile ? profile : null;
            _messageDetails = sas.GetBoolean("MessageDetails", false);
            _client = new SasClient(serverUri, apiKey, TimeSpan.FromSeconds(sas.GetInt("RequestTimeoutSeconds", 10)),
                sas.GetBoolean("AcceptSelfSignedCertificate", false));

            try
            {
                // Short: the simulator's start-up waits for this.
                var status = _client.Status(TimeSpan.FromSeconds(3));
                Log.Info($"{LogHeader} Using SpatialAudioServer {(string?)status["version"]} at {serverUri}");
            }
            catch (SasException e)
            {
                // Not fatal: the server may start after the simulator. Each request reports its own failure.
                Log.Warn($"{LogHeader} SpatialAudioServer at {serverUri} is not answering yet: {e.Message}");
            }
            RegisterConsoleCommands();

            // Names for participants that are not avatars (web browsers, bots), so viewers can show them; and, if
            // enabled, NPCs for them in-world. Both from one poll of the rooms.
            _guestLastName = sas.GetString("GuestLastName", "(web)");
            _registerGuestNames = sas.GetBoolean("RegisterGuestNames", true);
            var avatars = sas.GetString("WebUserAvatars", SasWebUserAvatars.Off);
            if (SasWebUserAvatars.Parse(avatars) is { } mode)
            {
                _webUserAvatarsMode = mode;
            }
            else
            {
                Log.Warn($"{LogHeader} WebUserAvatars = {avatars} is not off, optional or always: using off");
            }
            if (_webUserAvatarsMode != SasWebUserAvatars.Off)
            {
                var looks = sas.GetString("NpcAppearancesFrom", string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                _webUserAvatars = new SasWebUserAvatars(_client, _webUserAvatarsMode, _guestLastName, looks);
                Log.Info($"{LogHeader} Web users are shown in-world as NPCs: {_webUserAvatarsMode}");
            }
            if (_registerGuestNames || _webUserAvatars is not null)
            {
                _nameTimer = new System.Threading.Timer(_ => RegisterGuestNames(), null, 1000, 1000);
            }

            // Avatar positions as the simulator sees them, for every avatar with a voice session.
            var every = sas.GetInt("PositionUpdateMilliseconds", 250);
            if (every > 0)
            {
                every = Math.Max(every, 100);
                _poseTimer = new System.Threading.Timer(_ => SendPoses(), null, every, every);
            }
        }

        // ------------------------------------------------------------------------------------- IWebRtcVoiceService

        public IVoiceViewerSession? CreateViewerSession(OSDMap request, UUID agentId, UUID regionId)
        {
            if (_client is null)
            {
                return null;
            }
            var channelType = request.TryGetString("channel_type", out var type) ? type : string.Empty;
            if (channelType != "local" && !(channelType == "multiagent" && _calls))
            {
                Log.Info($"{LogHeader} {channelType} voice is not supported{(channelType == "multiagent" ? " (Calls = false)" : "")}; agent {agentId}");
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
                    var isCall = request.TryGetString("channel_type", out var channelType) && channelType == "multiagent";
                    string roomId, roomName;
                    if (isCall)
                    {
                        // The channel the "call" request handed out (an IM session id); checked again, so it cannot be
                        // used without being allowed it.
                        var channelText = request.TryGetString("channel", out var c) ? c : request.TryGetString("channel_id", out c) ? c : string.Empty;
                        if (!UUID.TryParse(channelText, out var channel) || SasCalls.Access(scene, agentId, channel) is not { } call)
                        {
                            Log.Info($"{LogHeader} Agent {agentId} may not join call channel '{channelText}'");
                            return Failed("not allowed in that call");
                        }
                        roomId = SasCalls.RoomFor(request.TryGetString("gridhash", out var grid) ? grid : string.Empty, channel);
                        roomName = call.Kind == CallKind.Group ? $"Group call: {call.Name}" : "P2P call";
                    }
                    else
                    {
                        (roomId, roomName) = RoomFor(scene, request);
                    }
                    var (displayName, pose) = AvatarOf(scene, agentId);
                    if (isCall)
                    {
                        pose = null; // calls are not spatial
                    }

                    // Moving to another room (e.g. into a parcel with its own channel) on the same connection: leave the old one.
                    if (session.ParticipantId is { } old && session.RoomExternalId is { } oldRoom && oldRoom != roomId)
                    {
                        _client.RemoveParticipant(oldRoom, old);
                    }

                    var room = isCall
                        ? _client.EnsureRoom(roomId, roomName, spatial: false, "none", _maxParticipants, SasWebUserAvatars.Off, null)
                        : _client.EnsureRoom(roomId, roomName, spatial: true, ClientPose, _maxParticipants, _webUserAvatarsMode, _spatialProfile);
                    if (!string.Equals((string?)room["inWorldAvatars"], _webUserAvatarsMode, StringComparison.OrdinalIgnoreCase))
                    {
                        _client.UpdateRoom(roomId, _webUserAvatarsMode);
                    }
                    _avatarSettingSent[roomId] = true;
                    if (!isCall && (string?)room["clientPose"] is { } roomPose && !string.Equals(roomPose, ClientPose, StringComparison.OrdinalIgnoreCase)
                        && _roomsChecked.TryAdd(roomId, true))
                    {
                        Log.Warn($"{LogHeader} Room {roomId} on the server is clientPose '{roomPose}', not '{ClientPose}' (it was made " +
                            "before this version): viewers' own positions are ignored there. Delete it on the server to have it made again.");
                    }
                    var (participantId, joinToken) = _client.AddParticipant(roomId, displayName, agentId.ToString(), pose);
                    var (answer, sessionToken) = _client.Offer(participantId, joinToken, offer);

                    if (session.RoomExternalId is { } previous && session.ParticipantId is not null && SasCalls.ChannelOf(previous) is { } left)
                    {
                        SasCalls.Left(left, agentId);
                    }
                    if (SasCalls.ChannelOf(roomId) is { } joined)
                    {
                        SasCalls.Joined(joined, agentId);
                    }
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

        // ------------------------------------------------------------------------------------- positions

        /// <summary>
        /// Timer: sends the simulator's position for every avatar with a voice session that has moved or turned (or not
        /// been sent for <see cref="PoseRefresh"/>), one request per room. Skipped while the previous round is running.
        /// </summary>
        private void SendPoses()
        {
            if (_client is null || System.Threading.Interlocked.Exchange(ref _sendingPoses, 1) != 0)
            {
                return;
            }
            try
            {
                var now = DateTime.UtcNow;
                var byRoom = new Dictionary<string, List<(SasViewerSession Session, SasPose Pose)>>();
                foreach (var session in _sessions.Values)
                {
                    if (session.ParticipantId is null || session.RoomExternalId is not { } room || SasCalls.IsCallRoom(room)
                        || !SceneManager.Instance.TryGetScene(session.RegionId, out var scene)
                        || AvatarOf(scene, session.AgentId).Pose is not { } pose)
                    {
                        continue;
                    }
                    if (session.SentPose is { } sent && !Moved(sent, pose) && now - session.SentAt < PoseRefresh)
                    {
                        continue;
                    }
                    if (!byRoom.TryGetValue(room, out var list))
                    {
                        byRoom[room] = list = [];
                    }
                    list.Add((session, pose));
                }
                foreach (var (room, list) in byRoom)
                {
                    try
                    {
                        var unknown = _client.UpdatePoses(room, list.Select(e => (e.Session.AgentId.ToString(), e.Pose)).ToList());
                        foreach (var (session, pose) in list)
                        {
                            session.SentPose = pose;
                            session.SentAt = now;
                        }
                        if (unknown.Count > 0 && _messageDetails)
                        {
                            Log.Debug($"{LogHeader} Positions for avatars the server no longer has in {room}: {string.Join(", ", unknown)}");
                        }
                    }
                    catch (SasException e)
                    {
                        if (now - _poseErrorLoggedAt > TimeSpan.FromSeconds(30))
                        {
                            _poseErrorLoggedAt = now;
                            Log.Warn($"{LogHeader} Could not send avatar positions for {room} (logged at most every 30 s): {e.Message}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"{LogHeader} Sending avatar positions failed: {e}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _sendingPoses, 0);
            }
        }

        // ------------------------------------------------------------------------------------- guest names

        /// <summary>
        /// Timer: SpatialAudioServer tells viewers about participants that are not avatars (web browsers, bots) under
        /// their participant id, and a viewer asks its simulator for the name of every id in its voice list. So their
        /// names are registered with OpenSim's user management ("Simon (web)"), for every room this simulator has
        /// voice sessions in. The server announces them a couple of seconds after they connect, which is enough for
        /// this one-second poll. Names are removed again once the participant has gone (or this simulator has no
        /// voice sessions left in its room); viewers keep their own copy for an hour.
        /// </summary>
        private void RegisterGuestNames()
        {
            if (_client is null || System.Threading.Interlocked.Exchange(ref _registeringNames, 1) != 0)
            {
                return;
            }
            try
            {
                var rooms = _sessions.Values.Where(s => s.ParticipantId is not null).Select(s => s.RoomExternalId)
                    .OfType<string>().ToHashSet();
                if (_webUserAvatars is not null)
                {
                    // Web users may join a region's room while no viewer here is in it.
                    foreach (var scene in SceneManager.Instance.Scenes)
                    {
                        rooms.Add($"region:{scene.RegionInfo.RegionID}");
                    }
                }
                var seen = new HashSet<UUID>();
                var seenParticipants = new HashSet<Guid>();
                var polled = new HashSet<string>();
                var read = new List<(string Room, System.Text.Json.Nodes.JsonArray Participants)>();
                foreach (var room in rooms)
                {
                    System.Text.Json.Nodes.JsonArray participants;
                    try
                    {
                        participants = _client.Participants(room);
                        polled.Add(room);
                    }
                    catch (SasException)
                    {
                        continue; // not on the server (yet), or unreachable: the position updates report that
                    }
                    if (!_avatarSettingSent.ContainsKey(room))
                    {
                        try
                        {
                            _client.UpdateRoom(room, _webUserAvatarsMode);
                            _avatarSettingSent[room] = true;
                        }
                        catch (SasException e)
                        {
                            Log.Warn($"{LogHeader} Could not set WebUserAvatars on room {room}: {e.Message}");
                        }
                    }
                    read.Add((room, participants));
                    SasWebUserAvatars.AddIds(participants, seenParticipants);
                    if (!_registerGuestNames)
                    {
                        continue;
                    }
                    foreach (var p in participants.OfType<System.Text.Json.Nodes.JsonObject>())
                    {
                        var id = (string?)p["id"];
                        var name = ((string?)p["displayName"])?.Trim();
                        if (id is null || string.IsNullOrEmpty(name) || UUID.TryParse((string?)p["externalId"] ?? string.Empty, out _)
                            || !UUID.TryParse(id, out var uuid))
                        {
                            continue;
                        }
                        seen.Add(uuid);
                        if (_guestNames.TryGetValue(uuid, out var known) && known.Name == name)
                        {
                            continue;
                        }
                        foreach (var scene in SceneManager.Instance.Scenes)
                        {
                            scene.RequestModuleInterface<IUserManagement>()?.AddSystemUser(uuid, name, _guestLastName);
                        }
                        _guestNames[uuid] = (name, room);
                        if (_messageDetails)
                        {
                            Log.Debug($"{LogHeader} Registered the name '{name} {_guestLastName}' for participant {id} in {room}");
                        }
                    }
                }

                // NPCs of participants that left go first, so a rejoining participant can have its NPC id back.
                if (_webUserAvatars is { } avatars)
                {
                    avatars.Sweep(polled, rooms, seenParticipants);
                    foreach (var (room, participants) in read)
                    {
                        avatars.Update(room, participants);
                    }
                }

                // Gone from a room we could read, or from a room we no longer have sessions in.
                foreach (var (uuid, entry) in _guestNames)
                {
                    if ((polled.Contains(entry.Room) && !seen.Contains(uuid)) || !rooms.Contains(entry.Room))
                    {
                        foreach (var scene in SceneManager.Instance.Scenes)
                        {
                            scene.RequestModuleInterface<IUserManagement>()?.RemoveUser(uuid);
                        }
                        _guestNames.TryRemove(uuid, out _);
                        if (polled.Contains(entry.Room))
                        {
                            // Viewers that missed its leave (the server restarted) drop it from their lists.
                            ReportGone(entry.Room, uuid.Guid);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"{LogHeader} Registering guest names failed: {e}");
            }
            finally
            {
                System.Threading.Volatile.Write(ref _registeringNames, 0);
            }
        }

        private void ReportGone(string room, Guid key)
        {
            try
            {
                _client?.ViewersGone(room, [key]);
            }
            catch (SasException e)
            {
                Log.Debug($"{LogHeader} Could not report {key} gone from {room}: {e.Message}");
            }
        }

        /// <summary>More than 5 cm, or about 2° of turn.</summary>
        private static bool Moved(SasPose a, SasPose b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            var dot = Math.Abs(a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw);
            return dx * dx + dy * dy + dz * dz > 0.05 * 0.05 || dot < 0.99985;
        }

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
                if (SasCalls.ChannelOf(room) is { } channel)
                {
                    SasCalls.Left(channel, session.AgentId);
                }
                try
                {
                    _client.RemoveParticipant(room, participantId);
                    Log.Info($"{LogHeader} Left voice room {room} ({reason}): agent {session.AgentId}");
                    if (SasCalls.IsCallRoom(room) && _client.Participants(room).Count == 0)
                    {
                        _client.DeleteRoom(room); // the call is over
                        Log.Info($"{LogHeader} Call {room} ended");
                    }
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
