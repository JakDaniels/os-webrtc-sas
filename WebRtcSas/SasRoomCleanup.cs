// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace osWebRtcSas
{
    /// <summary>
    /// When a region starts, deletes the server's voice rooms of its parcels that no longer exist (joined into another,
    /// or the region's land reset), if nobody is in them. Rooms of parcels that only switched to the region's channel
    /// are kept, so they keep their settings. <c>DeleteOldParcelRooms = true</c> (the default) in <c>[SasWebRtcVoice]</c>.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "SasRoomCleanupModule")]
    public class SasRoomCleanupModule : ISharedRegionModule
    {
        private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod()!.DeclaringType);
        private const string LogHeader = "[SAS WEBRTC VOICE]";
        private readonly HashSet<Guid> _done = [];
        private bool _enabled;

        public string Name => "SasRoomCleanupModule";

        public Type? ReplaceableInterface => null;

        public void Initialise(IConfigSource source)
        {
            var voice = source.Configs["WebRtcVoice"];
            var sas = source.Configs["SasWebRtcVoice"];
            _enabled = voice is not null && voice.GetBoolean("Enabled", false) && sas is not null
                && voice.GetString("SpatialVoiceService", string.Empty).Contains("WebRtcSas", StringComparison.OrdinalIgnoreCase)
                && sas.GetBoolean("DeleteOldParcelRooms", true);
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
            // Once the region is ready its parcels are loaded.
            scene.EventManager.OnRegionReadyStatusChange += _ => Start(scene);
            if (scene.Ready)
            {
                Start(scene);
            }
        }

        public void Close()
        {
        }

        private void Start(Scene scene)
        {
            lock (_done)
            {
                if (!scene.Ready || !_done.Add(scene.RegionInfo.RegionID.Guid))
                {
                    return;
                }
            }
            Task.Run(() => Clean(scene)); // the server may be slow or down: not on the start-up path
        }

        private static void Clean(Scene scene)
        {
            if (SasVoiceService.Current is not { } service)
            {
                return;
            }
            var parcels = scene.LandChannel?.AllParcels()
                .Select(p => p.LandData.GlobalID.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
            if (parcels.Count == 0)
            {
                return; // land not loaded: deleting now would remove every parcel's room
            }
            var prefix = $"parcel:{scene.RegionInfo.RegionID}:";
            try
            {
                foreach (var room in service.Client.Rooms().OfType<JsonObject>())
                {
                    if ((string?)room["externalId"] is not { } externalId
                        || !externalId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        || parcels.Contains(externalId[prefix.Length..]))
                    {
                        continue;
                    }
                    if ((int?)room["participantCount"] is > 0)
                    {
                        Log.Info($"{LogHeader} Room {(string?)room["name"]} is for a parcel that no longer exists, but people are in it: kept for now");
                        continue;
                    }
                    service.Client.DeleteRoom(externalId);
                    Log.Info($"{LogHeader} Deleted room {(string?)room["name"]} ({externalId}): its parcel no longer exists in {scene.RegionInfo.RegionName}");
                }
            }
            catch (SasException e)
            {
                Log.Warn($"{LogHeader} Could not check {scene.RegionInfo.RegionName}'s parcel rooms on the server: {e.Message}");
            }
        }
    }
}
