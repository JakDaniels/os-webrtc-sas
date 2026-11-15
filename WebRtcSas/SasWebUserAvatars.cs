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
    /// facing); the NPC is tied to the participant on the server (<c>avatarId</c>), so viewers list and show the NPC.
    /// Called from the voice service's room poll, once a second.
    /// </summary>
    /// <remarks>
    /// The NPC gets the default appearance and the name "{display name} {GuestLastName}", registered with user
    /// management for viewers' name lookups. It appears only when the participant's position is inside the room's
    /// region. NPCs are not kept across simulator restarts: a participant still in the room gets a new one.
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

        /// <summary>Participant id → its NPC.</summary>
        private readonly ConcurrentDictionary<Guid, Npc> _npcs = new();

        /// <summary>Participants whose position is outside the room's region (logged once).</summary>
        private readonly ConcurrentDictionary<Guid, bool> _outside = new();

        private bool _npcModuleMissingLogged;

        public SasWebUserAvatars(SasClient client, string mode, string lastName)
        {
            _client = client;
            Mode = mode;
            _lastName = lastName;
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
        public void Update(string room, JsonArray participants, HashSet<Guid> seen)
        {
            var scene = SceneOf(room);
            foreach (var p in participants)
            {
                if (p is not JsonObject participant || !Guid.TryParse((string?)participant["id"], out var id)
                    || UUID.TryParse((string?)participant["externalId"] ?? string.Empty, out _))
                {
                    continue; // an SL avatar (its viewer's own body), or not readable
                }
                seen.Add(id);
                var wanted = Mode == Always || (Mode == Optional && (bool?)participant["inWorldAvatar"] == true);
                if (!wanted || scene is null)
                {
                    Remove(id);
                    continue;
                }
                var pose = PoseOf(participant);
                Guid.TryParse((string?)participant["avatarId"], out var avatarId);
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
                    continue;
                }
                _npcs.TryRemove(id, out _); // gone from the scene (removed in-world, region restarted): make another
                Create(room, scene, id, ((string?)participant["displayName"])?.Trim() ?? "Guest", pose);
            }
        }

        /// <summary>Removes NPCs whose participant left a room that was read, or whose room is no longer polled.</summary>
        public void Sweep(HashSet<string> polled, ICollection<string> rooms, HashSet<Guid> seen)
        {
            foreach (var (id, npc) in _npcs)
            {
                if ((polled.Contains(npc.Room) && !seen.Contains(id)) || !rooms.Contains(npc.Room))
                {
                    Remove(id);
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
            var npcId = npcs.CreateNPC(name, _lastName, local, UUID.Zero, false, scene, new AvatarAppearance());
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

        private void Remove(Guid participantId)
        {
            if (!_npcs.TryRemove(participantId, out var npc))
            {
                return;
            }
            npc.Scene.RequestModuleInterface<INPCModule>()?.DeleteNPC(npc.Id, npc.Scene);
            foreach (var s in SceneManager.Instance.Scenes)
            {
                s.RequestModuleInterface<IUserManagement>()?.RemoveUser(npc.Id);
            }
            Log.Info($"{LogHeader} Removed NPC {npc.Id} of participant {participantId} from {npc.Scene.RegionInfo.RegionName}");
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
