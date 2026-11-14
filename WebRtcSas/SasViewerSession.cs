// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Threading.Tasks;
using osWebRtcVoice;
using OMV = OpenMetaverse;

namespace osWebRtcSas
{
    /// <summary>
    /// One viewer voice connection as OpenSim's region module tracks it (in its static session registry), plus what
    /// SpatialAudioServer gave us for it: the room, the participant and the session token for signalling. The tokens
    /// stay in the simulator; the viewer only ever sees <see cref="ViewerSessionID"/>.
    /// </summary>
    public sealed class SasViewerSession : IVoiceViewerSession
    {
        private readonly SasVoiceService _service;

        public SasViewerSession(SasVoiceService service, OMV.UUID regionId, OMV.UUID agentId)
        {
            _service = service;
            VoiceService = service;
            RegionId = regionId;
            AgentId = agentId;
            ViewerSessionID = OMV.UUID.Random().ToString();
        }

        // IVoiceViewerSession
        public string ViewerSessionID { get; set; }
        public IWebRtcVoiceService VoiceService { get; set; }
        public string VoiceServiceSessionId { get; set; } = string.Empty;
        public OMV.UUID RegionId { get; set; }
        public OMV.UUID AgentId { get; set; }
        public IVoiceViewerSession.VFlags Flags { get; set; }

        /// <summary>Serialises provision, signalling and shutdown for this connection.</summary>
        public object Lock { get; } = new();

        /// <summary>SpatialAudioServer room external id (region:… or parcel:…), once connected.</summary>
        public string? RoomExternalId { get; set; }

        public Guid? ParticipantId { get; set; }

        /// <summary>Session token from the offer, for ICE candidates.</summary>
        public string? SessionToken { get; set; }

        /// <summary>Leaves the room on the server (called when the viewer logs out or OpenSim drops the session).</summary>
        public Task Shutdown()
        {
            VoiceViewerSession.RemoveViewerSession(ViewerSessionID);
            _service.Disconnect(this, "shutdown");
            return Task.CompletedTask;
        }

        public override string ToString() =>
            $"viewer_session={ViewerSessionID} agent={AgentId} region={RegionId} room={RoomExternalId ?? "-"} participant={ParticipantId?.ToString() ?? "-"}";
    }
}
