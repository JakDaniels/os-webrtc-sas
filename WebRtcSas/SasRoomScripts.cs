// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcSas
{
    /// <summary>
    /// Script functions for a voice room's sound, set from in-world: <c>sasSetRoomParameters(notecard)</c>,
    /// <c>sasGetRoomParameters(notecard)</c>, <c>sasSetRoomParametersText(text)</c>, <c>sasGetRoomParametersText()</c>.
    /// They act on the voice room of the parcel the object stands on (the parcel's own, else the region's). Needs
    /// <c>[OSSL] AllowMODFunctions = true</c> (OpenSim's switch for functions registered by modules) and
    /// <c>ScriptFunctions = true</c> (the default) in <c>[SasWebRtcVoice]</c>.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SasRoomScriptsModule")]
    public class SasRoomScriptsModule : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC SCRIPTS]";
        private bool _enabled;

        public string Name => "SasRoomScriptsModule";

        public Type? ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var voice = source.Configs["WebRtcVoice"];
            var sas = source.Configs["SasWebRtcVoice"];
            _enabled = voice is not null && voice.GetBoolean("Enabled", false) && sas is not null
                && voice.GetString("SpatialVoiceService", string.Empty).Contains("WebRtcSas", StringComparison.OrdinalIgnoreCase)
                && sas.GetBoolean("ScriptFunctions", true);
        }

        public void PostInitialise()
        {
        }

        public void AddRegion(Scene scene)
        {
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!_enabled)
            {
                return;
            }
            if (scene.RequestModuleInterface<IScriptModuleComms>() is not { } comms)
            {
                Log.Warn($"{LogHeader} No script module comms in {scene.RegionInfo.RegionName}: the sas… script functions are not available");
                return;
            }
            comms.RegisterScriptInvocations(this);
            Log.Info($"{LogHeader} Script functions sasSetRoomParameters, sasGetRoomParameters (and …Text) registered in " +
                $"{scene.RegionInfo.RegionName} (scripts need [OSSL] AllowMODFunctions = true)");
        }

        public void Close()
        {
        }

        // ------------------------------------------------------------------------------------------ script functions

        /// <summary>Sets the room's parameters from a notecard in the object. "" on success, else the reason.</summary>
        [ScriptInvocation]
        public string sasSetRoomParameters(UUID hostID, UUID scriptID, string notecard)
        {
            if (Find(hostID) is not var (scene, part))
            {
                return "the object was not found";
            }
            if (ReadNotecard(scene, part, notecard) is not { } text)
            {
                return $"no notecard '{notecard}' in this object";
            }
            return SasRoomScripts.Set(scene, part, text, $"notecard '{notecard}'");
        }

        /// <summary>Sets the room's parameters from text in the notecard format. "" on success, else the reason.</summary>
        [ScriptInvocation]
        public string sasSetRoomParametersText(UUID hostID, UUID scriptID, string text) =>
            Find(hostID) is var (scene, part) ? SasRoomScripts.Set(scene, part, text, "text") : "the object was not found";

        /// <summary>Saves the room's parameters to a notecard in the object (replacing one of that name). "" or the reason.</summary>
        [ScriptInvocation]
        public string sasGetRoomParameters(UUID hostID, UUID scriptID, string notecard)
        {
            if (Find(hostID) is not var (scene, part))
            {
                return "the object was not found";
            }
            if (string.IsNullOrWhiteSpace(notecard))
            {
                return "a notecard name is needed";
            }
            var (text, error) = SasRoomScripts.Get(scene, part);
            if (error is not null)
            {
                return error;
            }
            return WriteNotecard(scene, part, notecard.Trim(), text!);
        }

        /// <summary>The room's parameters in the notecard format; "" on failure (the reason goes to the debug channel).</summary>
        [ScriptInvocation]
        public string sasGetRoomParametersText(UUID hostID, UUID scriptID)
        {
            if (Find(hostID) is not var (scene, part))
            {
                return string.Empty;
            }
            var (text, error) = SasRoomScripts.Get(scene, part);
            if (error is not null)
            {
                var message = Util.UTF8.GetBytes($"sasGetRoomParametersText: {error}");
                scene.SimChat(message, ChatTypeEnum.DebugChannel, 0x7FFFFFFF, part.AbsolutePosition, part.Name, part.UUID, false);
                return string.Empty;
            }
            return text!;
        }

        // ------------------------------------------------------------------------------------------------ notecards

        private static (Scene, SceneObjectPart)? Find(UUID hostID)
        {
            foreach (var scene in SceneManager.Instance.Scenes)
            {
                if (scene.GetSceneObjectPart(hostID) is { } part)
                {
                    return (scene, part);
                }
            }
            return null;
        }

        private static string? ReadNotecard(Scene scene, SceneObjectPart part, string name)
        {
            var item = part.Inventory.GetInventoryItems(name).FirstOrDefault(i => i.InvType == (int)InventoryType.Notecard);
            if (item is null || scene.AssetService.Get(item.AssetID.ToString()) is not { Data: { } data })
            {
                return null;
            }
            return string.Join("\n", SLUtil.ParseNotecardToArray(data));
        }

        /// <summary>Stores a notecard in the object, replacing a notecard of that name (as osMakeNotecard does).</summary>
        private static string WriteNotecard(Scene scene, SceneObjectPart part, string name, string text)
        {
            if (part.Inventory.GetInventoryItems(name).Any(i => i.InvType != (int)InventoryType.Notecard))
            {
                return $"'{name}' is already something other than a notecard in this object";
            }
            var body = Util.UTF8.GetBytes(text);
            var head = Util.UTF8.GetBytes(
                $"Linden text version 2\n{{\nLLEmbeddedItems version 1\n{{\ncount 0\n}}\nText length {body.Length}\n");
            var asset = new AssetBase(UUID.Random(), name, (sbyte)AssetType.Notecard, part.OwnerID.ToString())
            {
                Description = "Voice room settings (sasGetRoomParameters)",
                Data = [.. head, .. body, .. Util.UTF8.GetBytes("}")],
            };
            scene.AssetService.Store(asset);
            var item = new TaskInventoryItem
            {
                ParentID = part.UUID,
                CreationDate = (uint)Util.UnixTimeSinceEpoch(),
                Name = name,
                Description = asset.Description,
                Type = (int)AssetType.Notecard,
                InvType = (int)InventoryType.Notecard,
                OwnerID = part.OwnerID,
                CreatorID = part.OwnerID,
                BasePermissions = (uint)OpenSim.Framework.PermissionMask.All | (uint)OpenSim.Framework.PermissionMask.Export,
                CurrentPermissions = (uint)OpenSim.Framework.PermissionMask.All | (uint)OpenSim.Framework.PermissionMask.Export,
                NextPermissions = (uint)OpenSim.Framework.PermissionMask.All,
                GroupID = part.GroupID,
                AssetID = asset.FullID,
            };
            item.ResetIDs(part.UUID);
            part.Inventory.AddInventoryItemExclusive(item, false);
            part.ParentGroup.InvalidateDeepEffectivePerms();
            return string.Empty;
        }
    }

    /// <summary>
    /// What the script functions do: read and set the sound of the voice room an object stands in, guarded so that one
    /// object controls a room (kept in the room's metadata on the server) and a notecard says which room it is for.
    /// </summary>
    internal static class SasRoomScripts
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC SCRIPTS]";
        private const string ControllerKey = "controller";

        /// <summary>One set at a time, so two objects cannot both pass the controller check.</summary>
        private static readonly object SetLock = new();

        /// <summary>The room's own settings; any other key is one of its acoustic parameters, which the server checks.</summary>
        private static readonly string[] Keys = ["room", "acoustics", "engine", "distance-curve"];

        public static string Set(Scene scene, SceneObjectPart part, string text, string source)
        {
            var result = TrySet(scene, part, text);
            var what = $"{part.Name} ({part.UUID}, owner {OwnerName(scene, part.OwnerID)}) in {scene.RegionInfo.RegionName}, {source}";
            if (result.Length > 0)
            {
                Log.Info($"{LogHeader} Refused room parameters from {what}: {result}");
            }
            return result;
        }

        private static string TrySet(Scene scene, SceneObjectPart part, string text)
        {
            if (SasVoiceService.Current is not { } service)
            {
                return "SpatialAudioServer voice is not configured on this simulator";
            }
            if (part.ParentGroup.IsAttachment)
            {
                return "the object must be rezzed in-world, not worn";
            }
            if (Parse(text) is var (values, error) && error is not null)
            {
                return error;
            }
            if (!values.TryGetValue("room", out var wantedRoom))
            {
                return "the 'room = parcel' or 'room = region' line is missing: it says which voice room these settings are for";
            }
            if (wantedRoom is not ("parcel" or "region"))
            {
                return $"room = {wantedRoom}: it must be parcel or region";
            }

            var (roomId, roomName, parcelRoom) = RoomOf(scene, part);
            if (wantedRoom == "parcel" && !parcelRoom)
            {
                return "this parcel uses the region's voice channel, and these settings are for a parcel's own room (room = parcel)";
            }
            if (wantedRoom == "region" && parcelRoom)
            {
                return "this parcel has its own voice room, and these settings are for the region's room (room = region)";
            }
            var estate = scene.RegionInfo.EstateSettings.IsEstateManagerOrOwner(part.OwnerID);
            if (!parcelRoom && !estate)
            {
                return "only estate owners and managers may set the region's voice room";
            }
            if (parcelRoom && !estate && scene.LandChannel.GetLandObject(part.AbsolutePosition.X, part.AbsolutePosition.Y)?.LandData.OwnerID != part.OwnerID)
            {
                return "the object's owner must own the parcel (or be an estate owner or manager)";
            }

            lock (SetLock)
            {
                JsonObject room;
                try
                {
                    room = service.EnsureSpatialRoom(roomId, roomName, scene);
                }
                catch (SasException e)
                {
                    return $"SpatialAudioServer: {e.Message}";
                }
                if (ControllerOf(room, roomId) is { } controller && controller.Object != part.UUID)
                {
                    return $"this voice room is set by '{controller.Name}' (owner {controller.Owner}) at {controller.Where}: " +
                        "only that object may change it while it is there";
                }
                var position = part.AbsolutePosition;
                var changes = new JsonObject
                {
                    ["metadata"] = new JsonObject
                    {
                        [ControllerKey] = new JsonObject
                        {
                            ["object"] = part.UUID.ToString(),
                            ["name"] = part.Name,
                            ["owner"] = OwnerName(scene, part.OwnerID),
                            ["region"] = scene.RegionInfo.RegionName,
                            ["position"] = $"{position.X:F0},{position.Y:F0},{position.Z:F0}",
                            ["set"] = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture),
                        },
                    },
                };
                if (values.TryGetValue("acoustics", out var acoustics))
                {
                    changes["acoustics"] = acoustics;
                }
                // The server switches the room's media to these when the room is next empty (at once if it is).
                if (values.TryGetValue("engine", out var engine))
                {
                    changes["engine"] = engine;
                }
                if (values.TryGetValue("distance-curve", out var curve))
                {
                    changes["spatialProfile"] = curve;
                }
                // Acoustic parameters by their notecard keys (the element's property names, which the server accepts):
                // no value, or "default", goes back to the preset's (or the server's default).
                var parameters = new JsonObject();
                foreach (var (key, value) in values.Where(v => !Keys.Contains(v.Key)))
                {
                    parameters[key] = value is "" or "default" ? null : value;
                }
                if (parameters.Count > 0)
                {
                    changes["acousticParameters"] = parameters;
                }
                try
                {
                    room = service.Client.UpdateRoom(roomId, changes);
                }
                catch (SasException e)
                {
                    return $"SpatialAudioServer: {e.Message}";
                }
                service.ScriptSet(roomId);
                Log.Info($"{LogHeader} Room {roomName}: acoustics {(string?)room["acoustics"] ?? "outdoor"}, engine {(string?)room["engine"]}, " +
                    $"distance curve {(string?)room["spatialProfile"] ?? "default"}{Waiting(room)}" +
                    (parameters.Count > 0 ? $", acoustic parameters {(room["acousticParameters"]?.ToJsonString() ?? "none")}" : "") +
                    $", set by {part.Name} ({part.UUID}, owner {OwnerName(scene, part.OwnerID)})");
                return string.Empty;
            }
        }

        /// <summary>The room's settings in the notecard format, or the reason they could not be read.</summary>
        public static (string? Text, string? Error) Get(Scene scene, SceneObjectPart part)
        {
            if (SasVoiceService.Current is not { } service)
            {
                return (null, "SpatialAudioServer voice is not configured on this simulator");
            }
            var (roomId, roomName, parcelRoom) = RoomOf(scene, part);
            JsonObject room;
            try
            {
                room = service.EnsureSpatialRoom(roomId, roomName, scene);
            }
            catch (SasException e)
            {
                return (null, $"SpatialAudioServer: {e.Message}");
            }
            var text = new StringBuilder()
                .Append("# Voice room \"").Append(roomName).Append("\", saved ")
                .Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(" UTC.\n")
                .Append("# room: which voice room these settings are for (parcel: the parcel's own; region: the region's).\n")
                .Append("room = ").Append(parcelRoom ? "parcel" : "region").Append('\n')
                .Append("acoustics = ").Append((string?)room["acoustics"] ?? "outdoor").Append('\n')
                .Append("# The engine and distance curve change when nobody is in the room (at once if it is empty).\n")
                .Append("engine = ").Append((string?)room["engine"]).Append('\n')
                .Append("distance-curve = ").Append((string?)room["spatialProfile"] ?? "default").Append('\n');
            if (room["inUse"] is JsonObject inUse)
            {
                text.Append("# Still in use until the room is empty: engine ").Append((string?)inUse["engine"])
                    .Append(", distance-curve ").Append((string?)inUse["spatialProfile"] ?? "default").Append(".\n");
            }
            if (ControllerOf(room, roomId) is { } controller)
            {
                text.Append("# Set in-world by '").Append(controller.Name).Append("' (owner ").Append(controller.Owner)
                    .Append(") at ").Append(controller.Where).Append(".\n");
            }
            try
            {
                AppendParameters(text, service.Client.Acoustics(roomId));
            }
            catch (SasException e)
            {
                // A server from before acoustic parameters: the room's settings above are still right.
                text.Append("# No acoustic parameters from SpatialAudioServer: ").Append(e.Message).Append('\n');
            }
            return (text.ToString(), null);
        }

        private static readonly (string Group, string Title)[] Groups =
        [
            ("dimensions", "Room dimensions: with width, depth and height they give the reflections and reverb instead of the preset."),
            ("reflections", "Early reflections."),
            ("reverb", "Reverb."),
            ("distance", "Distance and direction (overriding the distance curve)."),
        ];

        /// <summary>
        /// Every acoustic parameter: those the room sets as lines, the rest commented with the value it has now (from
        /// the preset, the room's size or the server's defaults), so a builder can see what to change.
        /// </summary>
        private static void AppendParameters(StringBuilder text, JsonObject acoustics)
        {
            var set = acoustics["acousticParameters"] as JsonObject;
            var values = acoustics["values"] as JsonObject;
            var derived = (values?["derived"] as JsonArray)?.Select(n => (string?)n).ToHashSet() ?? [];
            text.Append("# Acoustic parameters, heard with the acoustic engine. Lines without # are set here; the # lines show\n")
                .Append("# what the room has now. Remove # to set one; write it with no value (e.g. \"reverb-decay =\") to go back.\n");
            if (values is null)
            {
                text.Append("# (The server cannot show the values in effect: the acousticmultimixer plugin is not installed.)\n");
            }
            foreach (var (group, title) in Groups)
            {
                text.Append("# ").Append(title).Append('\n');
                foreach (var parameter in (acoustics["parameters"] as JsonArray ?? []).OfType<JsonObject>().Where(p => (string?)p["group"] == group))
                {
                    var key = (string)parameter["key"]!;
                    var property = (string)parameter["property"]!;
                    var unit = (string?)parameter["unit"] is { Length: > 0 } u ? $"{u}; " : "";
                    if (set?[key] is { } value)
                    {
                        text.Append(property).Append(" = ").Append(Format(value)).Append('\n');
                    }
                    else
                    {
                        var now = values?[key] is { } v ? Format(v) : "";
                        var from = derived.Contains(key) ? "from the room's size" : now.Length == 0 ? "none" : "now";
                        text.Append("# ").Append(property).Append(" = ").Append(now).Append("    (").Append(unit).Append(from).Append(")\n");
                    }
                }
            }
        }

        private static string Format(JsonNode value) => value switch
        {
            JsonArray list => string.Join(", ", list.Select(v => v is null ? "" : Format(v))),
            JsonValue v when v.TryGetValue<double>(out var d) => d.ToString("0.####", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };

        /// <summary>
        /// The object controlling a room, if the room's metadata names one that is still in the region and still
        /// stands in that room; null when there is none (so the next object to set the room takes it over).
        /// </summary>
        public static (UUID Object, string Name, string Owner, string Where)? ControllerOf(JsonObject room, string roomId)
        {
            if (room["metadata"]?[ControllerKey] is not JsonObject controller
                || !UUID.TryParse((string?)controller["object"], out var id))
            {
                return null;
            }
            foreach (var scene in SceneManager.Instance.Scenes)
            {
                if (scene.GetSceneObjectPart(id) is { } part && !part.ParentGroup.IsDeleted && !part.ParentGroup.IsAttachment
                    && string.Equals(RoomOf(scene, part).ExternalId, roomId, StringComparison.OrdinalIgnoreCase))
                {
                    var p = part.AbsolutePosition;
                    var parcel = scene.LandChannel.GetLandObject(p.X, p.Y)?.LandData.Name;
                    return (id, part.Name, OwnerName(scene, part.OwnerID),
                        $"{scene.RegionInfo.RegionName} {p.X:F0},{p.Y:F0},{p.Z:F0}{(parcel is null ? "" : $" (parcel '{parcel}')")}");
                }
            }
            return null;
        }

        private static string Waiting(JsonObject room) =>
            room["inUse"] is JsonObject inUse
                ? $" (still using {(string?)inUse["engine"]}, {(string?)inUse["spatialProfile"] ?? "default"} until the room is empty)"
                : string.Empty;

        private static (string ExternalId, string Name, bool Parcel) RoomOf(Scene scene, SceneObjectPart part)
        {
            var p = part.ParentGroup.AbsolutePosition;
            return SasVoiceService.RoomForParcel(scene, scene.LandChannel?.GetLandObject(p.X, p.Y));
        }

        /// <summary>
        /// The notecard format: "key = value" lines; blank lines and lines starting # or // are ignored. Any unknown
        /// key, repeated key or line without "=" refuses the whole text.
        /// </summary>
        internal static (Dictionary<string, string> Values, string? Error) Parse(string text)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lines = text.Replace("\r", string.Empty).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                var equals = line.IndexOf('=');
                if (equals <= 0)
                {
                    return (values, $"line {i + 1}: '{line}' is not 'key = value'");
                }
                var key = line[..equals].Trim().ToLowerInvariant();
                var value = line[(equals + 1)..].Trim().ToLowerInvariant();
                if (!Keys.Contains(key) && !IsParameterKey(key))
                {
                    return (values, $"line {i + 1}: '{key}' is not a key (the room's: {string.Join(", ", Keys)}; or an acoustic parameter)");
                }
                // No value: an acoustic parameter goes back to the preset's; the room's own keys need one.
                if (value.Length == 0 && Keys.Contains(key))
                {
                    return (values, $"line {i + 1}: {key} has no value");
                }
                if (!values.TryAdd(key, value))
                {
                    return (values, $"line {i + 1}: {key} is set twice");
                }
            }
            return (values, null);
        }

        /// <summary>Looks like an acoustic parameter's key (e.g. reverb-decay); the server says whether it is one.</summary>
        private static bool IsParameterKey(string key) => key.Length is > 0 and <= 40 && key.All(c => c is >= 'a' and <= 'z' or '-');

        private static string OwnerName(Scene scene, UUID id) =>
            scene.RequestModuleInterface<IUserManagement>()?.GetUserName(id) is { Length: > 0 } name ? name : id.ToString();
    }
}
