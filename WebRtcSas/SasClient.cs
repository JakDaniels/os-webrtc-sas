// os-webrtc-sas: WebRTC voice for OpenSimulator using SpatialAudioServer.
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.

#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace osWebRtcSas
{
    /// <summary>A position and rotation in SpatialAudioServer's form (global coordinates, metres; quaternion).</summary>
    public readonly record struct SasPose(double X, double Y, double Z, double Qx, double Qy, double Qz, double Qw);

    /// <summary>A failed request to SpatialAudioServer, with its HTTP status (0 when the server could not be reached).</summary>
    public sealed class SasException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>
    /// Blocking client for SpatialAudioServer's control API (API key) and signalling endpoints (join and session
    /// tokens). OpenSim's capability handlers are synchronous, so this uses HttpClient.Send rather than blocking on
    /// async calls (which can deadlock or starve a busy thread pool).
    /// </summary>
    public sealed class SasClient : IDisposable
    {
        private readonly HttpClient _http;

        public SasClient(Uri server, string apiKey, TimeSpan timeout, bool acceptSelfSigned)
        {
            var handler = new HttpClientHandler();
            if (acceptSelfSigned)
            {
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            _http = new HttpClient(handler) { BaseAddress = server, Timeout = timeout };
            _http.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            Server = server;
        }

        public Uri Server { get; }

        /// <summary>GET /api/status, giving up after <paramref name="timeout"/> if that is shorter than the usual timeout.</summary>
        public JsonObject Status(TimeSpan? timeout = null) => Send(HttpMethod.Get, "api/status", null, null, timeout)!;

        /// <summary>GET /api/rooms: every room on the server.</summary>
        public JsonArray Rooms() => SendArray(HttpMethod.Get, "api/rooms");

        /// <summary>GET /api/rooms/by-external/{room}/participants: everyone in the room.</summary>
        public JsonArray Participants(string roomExternalId) =>
            SendArray(HttpMethod.Get, $"api/rooms/by-external/{Escape(roomExternalId)}/participants");

        /// <summary>
        /// Gets or creates the room with this external id (PUT /api/rooms/by-external/{id}). The settings only apply
        /// when it is created; the reply shows the room as it is.
        /// </summary>
        public JsonObject EnsureRoom(string externalId, string name, bool spatial, string clientPose, int maxParticipants, string inWorldAvatars,
            string? spatialProfile)
        {
            var body = new JsonObject
            {
                ["name"] = name, ["spatial"] = spatial, ["clientPose"] = clientPose, ["inWorldAvatars"] = inWorldAvatars,
            };
            if (spatialProfile is not null)
            {
                body["spatialProfile"] = spatialProfile;
            }
            if (maxParticipants > 0)
            {
                body["maxParticipants"] = maxParticipants;
            }
            return Send(HttpMethod.Put, $"api/rooms/by-external/{Escape(externalId)}", body, null)!;
        }

        /// <summary>Tells the server these viewer keys (NPCs, guests) have gone, so viewers that missed it drop them.</summary>
        public void ViewersGone(string roomExternalId, IReadOnlyList<Guid> keys)
        {
            var list = new JsonArray();
            foreach (var key in keys)
            {
                list.Add(key.ToString());
            }
            Send(HttpMethod.Post, $"api/rooms/by-external/{Escape(roomExternalId)}/viewers-gone", new JsonObject { ["keys"] = list }, null);
        }

        /// <summary>Sets whether this simulator shows the room's web users in-world (PATCH /api/rooms/by-external/{id}).</summary>
        public JsonObject UpdateRoom(string externalId, string inWorldAvatars) =>
            Send(HttpMethod.Patch, $"api/rooms/by-external/{Escape(externalId)}", new JsonObject { ["inWorldAvatars"] = inWorldAvatars }, null)!;

        /// <summary>Ties a participant to the NPC made for it (PATCH …/participants/{id} avatarId); viewers then know it by the NPC.</summary>
        public void SetAvatarId(string roomExternalId, Guid participantId, Guid avatarId) =>
            Send(HttpMethod.Patch, $"api/rooms/by-external/{Escape(roomExternalId)}/participants/{participantId}",
                new JsonObject { ["avatarId"] = avatarId.ToString() }, null);

        /// <summary>Adds (or, for the same avatar, replaces) a participant. Returns its id and one-time join token.</summary>
        public (Guid ParticipantId, string JoinToken) AddParticipant(string roomExternalId, string displayName, string agentId, SasPose? pose)
        {
            var body = new JsonObject { ["displayName"] = displayName, ["externalId"] = agentId };
            if (pose is { } p)
            {
                body["position"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
                body["orientation"] = new JsonObject { ["x"] = p.Qx, ["y"] = p.Qy, ["z"] = p.Qz, ["w"] = p.Qw };
            }
            var result = Send(HttpMethod.Post, $"api/rooms/by-external/{Escape(roomExternalId)}/participants", body, null)!;
            return (Guid.Parse((string)result["participantId"]!), (string)result["joinToken"]!);
        }

        /// <summary>
        /// Sets many avatars' poses in one room at once (POST /api/rooms/by-external/{room}/poses, by avatar UUID).
        /// Returns the avatar ids the server does not have in that room.
        /// </summary>
        public IReadOnlyList<string> UpdatePoses(string roomExternalId, IReadOnlyList<(string AgentId, SasPose Pose)> poses)
        {
            var list = new JsonArray();
            foreach (var (agentId, p) in poses)
            {
                list.Add(new JsonObject
                {
                    ["externalId"] = agentId,
                    ["position"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z },
                    ["orientation"] = new JsonObject { ["x"] = p.Qx, ["y"] = p.Qy, ["z"] = p.Qz, ["w"] = p.Qw },
                });
            }
            var result = Send(HttpMethod.Post, $"api/rooms/by-external/{Escape(roomExternalId)}/poses", new JsonObject { ["poses"] = list }, null);
            var unknown = new List<string>();
            if (result?["unknown"] is JsonArray ids)
            {
                foreach (var id in ids)
                {
                    if ((string?)id is { } s)
                    {
                        unknown.Add(s);
                    }
                }
            }
            return unknown;
        }

        /// <summary>
        /// Sets web or bot participants' positions in one room, keeping their orientations (the poses endpoint, by
        /// participant id): where their NPCs were moved in-world. Participants the server no longer has are ignored.
        /// </summary>
        public void UpdatePositions(string roomExternalId, IReadOnlyList<(Guid ParticipantId, SasPose Pose)> positions)
        {
            var list = new JsonArray();
            foreach (var (participantId, p) in positions)
            {
                list.Add(new JsonObject
                {
                    ["participantId"] = participantId.ToString(),
                    ["position"] = new JsonObject { ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z },
                });
            }
            Send(HttpMethod.Post, $"api/rooms/by-external/{Escape(roomExternalId)}/poses", new JsonObject { ["poses"] = list }, null);
        }

        /// <summary>Deletes a room (404 is not an error).</summary>
        public void DeleteRoom(string externalId)
        {
            try
            {
                Send(HttpMethod.Delete, $"api/rooms/by-external/{Escape(externalId)}", null, null);
            }
            catch (SasException e) when (e.Status == (int)HttpStatusCode.NotFound)
            {
            }
        }

        /// <summary>Removes a participant; true if it was there (404 is not an error: it may have gone already).</summary>
        public bool RemoveParticipant(string roomExternalId, Guid participantId)
        {
            try
            {
                Send(HttpMethod.Delete, $"api/rooms/by-external/{Escape(roomExternalId)}/participants/{participantId}", null, null);
                return true;
            }
            catch (SasException e) when (e.Status == (int)HttpStatusCode.NotFound)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends the viewer's SDP offer with the join token and returns the answer, which carries all of the server's
        /// ICE candidates (trickle:false: a viewer has no way to receive more), and the session token for later calls.
        /// </summary>
        public (string AnswerSdp, string SessionToken) Offer(Guid participantId, string joinToken, string offerSdp)
        {
            var result = Send(HttpMethod.Post, $"signal/{participantId}/offer",
                new JsonObject { ["sdp"] = offerSdp, ["trickle"] = false }, joinToken)!;
            return ((string)result["sdp"]!, (string)result["sessionToken"]!);
        }

        /// <summary>Forwards the viewer's ICE candidates (and/or the end of them).</summary>
        public void SendCandidates(Guid participantId, string sessionToken, IReadOnlyList<(string Candidate, uint MLineIndex)> candidates, bool completed)
        {
            var list = new JsonArray();
            foreach (var (candidate, index) in candidates)
            {
                list.Add(new JsonObject { ["candidate"] = candidate, ["sdpMLineIndex"] = index });
            }
            var body = new JsonObject { ["candidates"] = list };
            if (completed)
            {
                body["completed"] = true;
            }
            Send(HttpMethod.Post, $"signal/{participantId}/ice", body, sessionToken);
        }

        public void Dispose() => _http.Dispose();

        private static string Escape(string segment) => Uri.EscapeDataString(segment);

        private JsonArray SendArray(HttpMethod method, string path) =>
            JsonNode.Parse(SendText(method, path, null, null)) as JsonArray ?? new JsonArray();

        /// <summary>Sends a request and returns the JSON object reply (null for an empty one). Throws <see cref="SasException"/>.</summary>
        private JsonObject? Send(HttpMethod method, string path, JsonObject? body, string? bearer, TimeSpan? timeout = null)
        {
            var text = SendText(method, path, body, bearer, timeout);
            return text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject;
        }

        private string SendText(HttpMethod method, string path, JsonObject? body, string? bearer, TimeSpan? timeout = null)
        {
            using var cancel = timeout is { } limit ? new CancellationTokenSource(limit) : null;
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            }
            if (bearer is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }

            HttpResponseMessage response;
            try
            {
                response = _http.Send(request, cancel?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancel?.IsCancellationRequested == true)
            {
                throw new SasException(0, $"{method} {path}: no answer within {timeout!.Value.TotalSeconds:0.#} s");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                throw new SasException(0, $"{method} {path}: server not reachable ({e.Message})");
            }

            using (response)
            {
                using var reader = new System.IO.StreamReader(response.Content.ReadAsStream());
                var text = reader.ReadToEnd();
                if (!response.IsSuccessStatusCode)
                {
                    throw new SasException((int)response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {ProblemText(text)}");
                }
                return text;
            }
        }

        /// <summary>The detail (or title) of an RFC 9457 problem reply, or the text itself.</summary>
        private static string ProblemText(string text)
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject problem)
                {
                    return (string?)problem["detail"] ?? (string?)problem["title"] ?? text;
                }
            }
            catch (JsonException)
            {
            }
            return text;
        }
    }
}
