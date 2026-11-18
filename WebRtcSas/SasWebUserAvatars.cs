// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using log4net;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace osWebRtcSas
{
    /// <summary>
    /// Shows web and bot participants of this simulator's voice rooms in-world as NPCs (<c>WebUserAvatars</c>):
    /// <c>optional</c> for those that ask (the web client's tick box, the load tool's <c>--npc</c>), <c>always</c> for
    /// all. The participant's pose on the server drives the NPC (where the web client or load tool put it, and its
    /// facing), and an NPC moved in-world (a push, a script) moves the participant's position on the server in turn; the
    /// NPC is tied to the participant on the server (<c>avatarId</c>), so viewers list and show the NPC.
    /// Called from the voice service's room poll, once a second.
    /// </summary>
    /// <remarks>
    /// <para>The NPC is named "{display name} {GuestLastName}", registered with user management for viewers' name
    /// lookups, and looks like the avatars in <c>NpcAppearancesFrom</c> in turn (the default "cloud" without them). It
    /// appears only when the participant's position is inside the room's region. NPCs are not kept across simulator
    /// restarts: a participant still in the room gets a new one.</para>
    /// <para>NPC ids are stable while the simulator runs: derived from a secret made at start-up, the room, the name
    /// and a slot number, so a participant that joins again under the same name (a bot after a server restart) gets
    /// its old id, and viewers keep one list entry for it; the secret keeps the ids from being guessed. (They are
    /// version 3, name-based UUIDs, so they cannot equal an account's random version 4 UUID.) A removed NPC is
    /// reported to the server, so viewers that missed the leave drop it.</para>
    /// <para>Looks (<c>NpcAppearancesFrom</c>, used in turn): an avatar's name or UUID copies that avatar, its current
    /// look while it is in a region here, else its stored appearance (whose baked textures may have expired: a cloud);
    /// a notecard's asset UUID loads an appearance saved with <c>osAgentSaveAppearance</c>, which also stores the
    /// baked textures, so it keeps working.</para>
    /// </remarks>
    internal sealed class SasWebUserAvatars
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC VOICE]";

        public const string Off = "off";
        public const string Optional = "optional";
        public const string Always = "always";

        private readonly SasClient _client;
        private readonly string _lastName;
        private readonly string[] _appearancesFrom;
        private int _nextAppearance;

        /// <summary>Mixed into NPC ids so they cannot be worked out from the room and name.</summary>
        private readonly string _secret = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        /// <summary>Appearances loaded from notecards, by asset id (they do not change).</summary>
        private readonly ConcurrentDictionary<UUID, OpenMetaverse.StructuredData.OSDMap> _notecards = new();

        /// <summary>Participant id → its NPC.</summary>
        private readonly ConcurrentDictionary<Guid, Npc> _npcs = new();

        /// <summary>Participants whose position is outside the room's region (logged once).</summary>
        private readonly ConcurrentDictionary<Guid, bool> _outside = new();

        private bool _npcModuleMissingLogged;

        /// <param name="appearancesFrom">Avatar names ("First Last") or UUIDs whose looks NPCs copy, in turn.</param>
        public SasWebUserAvatars(SasClient client, string mode, string lastName, IEnumerable<string> appearancesFrom)
        {
            _client = client;
            Mode = mode;
            _lastName = lastName;
            _appearancesFrom = System.Linq.Enumerable.ToArray(appearancesFrom);
        }

        /// <summary>The ids of the web and bot participants in a room listing (not SL avatars).</summary>
        public static void AddIds(JsonArray participants, HashSet<Guid> into)
        {
            foreach (var p in participants)
            {
                if (p is JsonObject participant && Guid.TryParse((string?)participant["id"], out var id)
                    && !UUID.TryParse((string?)participant["externalId"] ?? string.Empty, out _))
                {
                    into.Add(id);
                }
            }
        }

        /// <summary><see cref="Off"/>, <see cref="Optional"/> or <see cref="Always"/>.</summary>
        public string Mode { get; }

        /// <summary>The ini value, or null if it is not one of the three.</summary>
        public static string? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "off" or "false" => Off,
            "optional" => Optional,
            "always" or "true" => Always,
            _ => null,
        };

        /// <summary>Brings the room's NPCs in line with its participants (as just read from the server).</summary>
        public void Update(string room, JsonArray participants)
        {
            var scene = SceneOf(room);
            var dragged = new List<(Guid, SasPose)>();
            foreach (var p in participants)
            {
                if (p is not JsonObject participant || !Guid.TryParse((string?)participant["id"], out var id)
                    || UUID.TryParse((string?)participant["externalId"] ?? string.Empty, out _))
                {
                    continue; // an SL avatar (its viewer's own body), or not readable
                }
                var wanted = Mode == Always || (Mode == Optional && (bool?)participant["inWorldAvatar"] == true);
                Guid.TryParse((string?)participant["avatarId"], out var avatarId);
                if (!wanted || scene is null)
                {
                    if (Remove(id) is { } removed)
                    {
                        Tie(room, id, Guid.Empty); // still in the room: viewers know it by its participant id again
                        Gone(room, removed);
                    }
                    continue;
                }
                var pose = PoseOf(participant);
                if (_npcs.TryGetValue(id, out var npc) && npc.Scene.TryGetScenePresence(npc.Id, out ScenePresence presence))
                {
                    if (avatarId != npc.Id.Guid)
                    {
                        Tie(room, id, npc.Id.Guid); // the server lost it (e.g. restarted with the participant re-added)
                    }
                    if (pose is { } moved && (npc.Pose is not { } last || Moved(last, moved)) && Local(npc.Scene, moved) is { } local)
                    {
                        presence.Teleport(local);
                        presence.Rotation = new Quaternion((float)moved.Qx, (float)moved.Qy, (float)moved.Qz, (float)moved.Qw);
                        npc.Pose = moved;
                    }
                    else if (npc.Pose is { } placed && Dragged(npc.Scene, presence, placed) is { } there)
                    {
                        // Moved in-world (a push, a script) while its voice stayed put: the voice follows the NPC.
                        dragged.Add((id, there));
                        npc.Pose = there;
                    }
                    continue;
                }
                _npcs.TryRemove(id, out _); // gone from the scene (removed in-world, region restarted): make another
                Create(room, scene, id, ((string?)participant["displayName"])?.Trim() ?? "Guest", pose);
            }
            if (dragged.Count > 0)
            {
                try
                {
                    _client.UpdatePositions(room, dragged);
                }
                catch (SasException e)
                {
                    Log.Warn($"{LogHeader} Could not send the positions of NPCs moved in-world in {room}: {e.Message}");
                }
            }
        }

        /// <summary>An NPC further than this from where it was put has been moved in-world.</summary>
        private const double DragDistance = 0.25;

        /// <summary>
        /// The NPC's global position (with the placed orientation), if it is more than <see cref="DragDistance"/> from
        /// where it was put.
        /// </summary>
        private static SasPose? Dragged(Scene scene, ScenePresence presence, SasPose placed)
        {
            var p = presence.AbsolutePosition;
            double x = scene.RegionInfo.WorldLocX + p.X, y = scene.RegionInfo.WorldLocY + p.Y, z = p.Z;
            double dx = x - placed.X, dy = y - placed.Y, dz = z - placed.Z;
            return dx * dx + dy * dy + dz * dz > DragDistance * DragDistance ? placed with { X = x, Y = y, Z = z } : null;
        }

        /// <summary>Removes NPCs whose participant left a room that was read, or whose room is no longer polled.</summary>
        public void Sweep(HashSet<string> polled, ICollection<string> rooms, HashSet<Guid> seen)
        {
            foreach (var (id, npc) in _npcs)
            {
                var left = polled.Contains(npc.Room) && !seen.Contains(id);
                if ((left || !rooms.Contains(npc.Room)) && Remove(id) is { } removed && left)
                {
                    Gone(npc.Room, removed);
                }
            }
            foreach (var id in _outside.Keys)
            {
                if (!seen.Contains(id))
                {
                    _outside.TryRemove(id, out _);
                }
            }
        }

        private void Create(string room, Scene scene, Guid id, string name, SasPose? pose)
        {
            if (pose is not { } p || Local(scene, p) is not { } local)
            {
                if (_outside.TryAdd(id, true))
                {
                    Log.Info($"{LogHeader} No NPC yet for {name} ({id}) in {room}: its position is not inside {scene.RegionInfo.RegionName}");
                }
                return;
            }
            var npcs = scene.RequestModuleInterface<INPCModule>();
            if (npcs is null)
            {
                if (!_npcModuleMissingLogged)
                {
                    _npcModuleMissingLogged = true;
                    Log.Warn($"{LogHeader} WebUserAvatars = {Mode}, but NPCs are disabled ([NPC] Enabled = false): no NPCs for web users");
                }
                return;
            }
            var wantedId = StableId(room, name);
            var npcId = npcs.CreateNPC(name, _lastName, local, wantedId, UUID.Zero, string.Empty, UUID.Zero, false, scene, NextAppearance(scene));
            if (npcId == UUID.Zero)
            {
                Log.Warn($"{LogHeader} Could not create an NPC for {name} ({id}) in {scene.RegionInfo.RegionName}");
                return;
            }
            if (scene.TryGetScenePresence(npcId, out ScenePresence presence))
            {
                presence.Rotation = new Quaternion((float)p.Qx, (float)p.Qy, (float)p.Qz, (float)p.Qw);
            }
            foreach (var s in SceneManager.Instance.Scenes)
            {
                s.RequestModuleInterface<IUserManagement>()?.AddSystemUser(npcId, name, _lastName);
            }
            _npcs[id] = new Npc(npcId, scene, room) { Pose = p };
            _outside.TryRemove(id, out _);
            Log.Info($"{LogHeader} NPC {name} {_lastName} ({npcId}) for participant {id} in {scene.RegionInfo.RegionName} at {local}");
            Tie(room, id, npcId.Guid);
        }

        private void Tie(string room, Guid participantId, Guid npcId)
        {
            try
            {
                _client.SetAvatarId(room, participantId, npcId);
            }
            catch (SasException e)
            {
                Log.Warn($"{LogHeader} Could not tie participant {participantId} to its NPC {npcId}: {e.Message}");
            }
        }

        /// <summary>Tells the server the NPC has gone, so viewers that missed it (a server restart) drop it.</summary>
        private void Gone(string room, UUID npcId)
        {
            try
            {
                _client.ViewersGone(room, [npcId.Guid]);
            }
            catch (SasException e)
            {
                Log.Debug($"{LogHeader} Could not report NPC {npcId} gone from {room}: {e.Message}");
            }
        }

        /// <summary>
        /// The NPC id for a participant called <paramref name="name"/> in <paramref name="room"/>: the same each time,
        /// unless another participant of that name has it now (then the next slot's).
        /// </summary>
        private UUID StableId(string room, string name)
        {
            for (var slot = 0; ; slot++)
            {
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"os-webrtc-sas npc|{_secret}|{room}|{name}|{slot}"));
                hash[6] = (byte)((hash[6] & 0x0F) | 0x30); // a version 3 (name-based) UUID
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
                var id = new UUID(hash, 0);
                var taken = false;
                foreach (var npc in _npcs.Values)
                {
                    taken |= npc.Id == id;
                }
                if (!taken)
                {
                    return id;
                }
            }
        }

        /// <summary>The next look from NpcAppearancesFrom (round robin): the avatar's own if it is in a region here, else
        /// its stored appearance; the default (a cloud) without one.</summary>
        private AvatarAppearance NextAppearance(Scene scene)
        {
            if (_appearancesFrom.Length == 0)
            {
                return new AvatarAppearance();
            }
            var from = _appearancesFrom[(int)((uint)System.Threading.Interlocked.Increment(ref _nextAppearance) - 1) % _appearancesFrom.Length];
            UUID user;
            if (UUID.TryParse(from, out user) && Notecard(scene, user) is { } saved)
            {
                var appearance = new AvatarAppearance();
                appearance.Unpack(saved);
                return appearance;
            }
            if (!UUID.TryParse(from, out user))
            {
                var names = from.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var account = names.Length == 2 ? scene.UserAccountService?.GetUserAccount(scene.RegionInfo.ScopeID, names[0], names[1]) : null;
                if (account is null)
                {
                    Log.Warn($"{LogHeader} NpcAppearancesFrom: no avatar called '{from}': using the default appearance");
                    return new AvatarAppearance();
                }
                user = account.PrincipalID;
            }
            foreach (var s in SceneManager.Instance.Scenes)
            {
                if (s.TryGetScenePresence(user, out ScenePresence presence) && !presence.IsChildAgent && !presence.IsNPC)
                {
                    return new AvatarAppearance(presence.Appearance, true);
                }
            }
            if (scene.AvatarService?.GetAppearance(user) is { } stored)
            {
                return stored;
            }
            Log.Warn($"{LogHeader} NpcAppearancesFrom: no appearance stored for '{from}': using the default appearance");
            return new AvatarAppearance();
        }

        /// <summary>The appearance saved in the notecard with this asset id (osAgentSaveAppearance), or null if it is not one.</summary>
        private OpenMetaverse.StructuredData.OSDMap? Notecard(Scene scene, UUID assetId)
        {
            if (_notecards.TryGetValue(assetId, out var cached))
            {
                return cached;
            }
            var asset = scene.AssetService?.Get(assetId.ToString());
            if (asset is null || asset.Type != (sbyte)AssetType.Notecard)
            {
                return null; // an avatar's UUID
            }
            try
            {
                var text = string.Join("\n", SLUtil.ParseNotecardToArray(asset.Data));
                if (OpenMetaverse.StructuredData.OSDParser.DeserializeLLSDXml(text) is OpenMetaverse.StructuredData.OSDMap map)
                {
                    _notecards[assetId] = map;
                    return map;
                }
            }
            catch (Exception e)
            {
                Log.Warn($"{LogHeader} NpcAppearancesFrom: notecard {assetId} is not a saved appearance ({e.Message})");
                return null;
            }
            Log.Warn($"{LogHeader} NpcAppearancesFrom: notecard {assetId} is not a saved appearance");
            return null;
        }

        /// <summary>Removes the participant's NPC, if it has one; returns its id.</summary>
        private UUID? Remove(Guid participantId)
        {
            if (!_npcs.TryRemove(participantId, out var npc))
            {
                return null;
            }
            npc.Scene.RequestModuleInterface<INPCModule>()?.DeleteNPC(npc.Id, npc.Scene);
            foreach (var s in SceneManager.Instance.Scenes)
            {
                s.RequestModuleInterface<IUserManagement>()?.RemoveUser(npc.Id);
            }
            Log.Info($"{LogHeader} Removed NPC {npc.Id} of participant {participantId} from {npc.Scene.RegionInfo.RegionName}");
            return npc.Id;
        }

        /// <summary>The scene of a room's region ("region:{id}" or "parcel:{id}:{parcel}"), if it is on this simulator.</summary>
        private static Scene? SceneOf(string room)
        {
            var parts = room.Split(':');
            return parts.Length >= 2 && UUID.TryParse(parts[1], out var region) && SceneManager.Instance.TryGetScene(region, out var scene)
                ? scene
                : null;
        }

        /// <summary>A global position in the scene's coordinates, if it is inside the region.</summary>
        private static Vector3? Local(Scene scene, SasPose pose)
        {
            var x = pose.X - scene.RegionInfo.WorldLocX;
            var y = pose.Y - scene.RegionInfo.WorldLocY;
            return x >= 0 && y >= 0 && x < scene.RegionInfo.RegionSizeX && y < scene.RegionInfo.RegionSizeY
                ? new Vector3((float)x, (float)y, (float)pose.Z)
                : null;
        }

        /// <summary>Where the participant is and which way it faces (its listener facing, which the web client turns).</summary>
        private static SasPose? PoseOf(JsonObject participant)
        {
            if (participant["position"] is not JsonObject position)
            {
                return null;
            }
            var q = participant["listenerOrientation"] as JsonObject ?? participant["orientation"] as JsonObject;
            return new SasPose((double?)position["x"] ?? 0, (double?)position["y"] ?? 0, (double?)position["z"] ?? 0,
                (double?)q?["x"] ?? 0, (double?)q?["y"] ?? 0, (double?)q?["z"] ?? 0, (double?)q?["w"] ?? 1);
        }

        /// <summary>More than 5 cm, or about 2° of turn.</summary>
        private static bool Moved(SasPose a, SasPose b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            var dot = Math.Abs(a.Qx * b.Qx + a.Qy * b.Qy + a.Qz * b.Qz + a.Qw * b.Qw);
            return dx * dx + dy * dy + dz * dz > 0.05 * 0.05 || dot < 0.99985;
        }

        private sealed class Npc(UUID id, Scene scene, string room)
        {
            public UUID Id { get; } = id;
            public Scene Scene { get; } = scene;
            public string Room { get; } = room;
            public SasPose? Pose { get; set; }
        }
    }
}
