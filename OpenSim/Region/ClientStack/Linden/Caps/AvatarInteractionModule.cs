/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// Optional per-agent capability for avatar interaction preferences,
    /// invitations and consent. Legacy viewers do not request this capability.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AvatarInteractionModule")]
    public class AvatarInteractionModule : INonSharedRegionModule
    {
        private const int InvitationLifetimeSeconds = 30;
        private const int MaxPendingInvitations = 128;
        private const int MaxInvitesPerWindow = 5;
        private const int InviteRateWindowSeconds = 10;
        private const int MaxTrackedInviteSenders = 1024;
        private const int InteractionSessionLifetimeSeconds = 120;
        private const int MaxActiveSessions = 128;

        private sealed class InteractionInvitation
        {
            public UUID Id;
            public UUID Requester;
            public UUID Target;
            public string Kind;
            public DateTime ExpiresUtc;
        }

        private sealed class InteractionSession
        {
            public UUID Id;
            public UUID RequestId;
            public UUID Requester;
            public UUID Target;
            public string Kind;
            public DateTime ExpiresUtc;
        }

        private readonly object m_invitationLock = new object();
        private readonly Dictionary<UUID, InteractionInvitation> m_invitations =
            new Dictionary<UUID, InteractionInvitation>();
        private readonly Dictionary<UUID, Queue<DateTime>> m_inviteAttempts =
            new Dictionary<UUID, Queue<DateTime>>();
        private readonly Dictionary<UUID, InteractionSession> m_sessions =
            new Dictionary<UUID, InteractionSession>();
        private Scene m_scene;

        public string Name => "AvatarInteractionModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source) { }

        public void AddRegion(Scene scene)
        {
            m_scene = scene;
            scene.RegisterModuleInterface<AvatarInteractionModule>(this);
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            if (m_scene == scene)
            {
                lock (m_invitationLock)
                {
                    m_invitations.Clear();
                    m_inviteAttempts.Clear();
                    m_sessions.Clear();
                }
                scene.UnregisterModuleInterface<AvatarInteractionModule>(this);
                m_scene = null;
            }
        }

        public void RegionLoaded(Scene scene)
        {
            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void PostInitialise() { }
        public void Close() { }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            if (m_scene == null)
                return;

            string capPath = "/" + UUID.Random().ToString();
            Scene registeredScene = m_scene;
            caps.RegisterSimpleHandler("AvatarInteraction",
                new SimpleStreamHandler(capPath, (request, response) =>
                    HandleRequest(registeredScene, agentID, request, response)));
        }

        private static void HandleRequest(Scene scene, UUID agentID,
            IOSHttpRequest request, IOSHttpResponse response)
        {
            response.ContentType = "application/llsd+xml";

            if (request.HttpMethod != "POST")
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            OSDMap input;
            try
            {
                input = OSDParser.DeserializeLLSDXml(request.InputStream) as OSDMap;
                if (input == null)
                {
                    WriteReply(response, HttpStatusCode.BadRequest, "invalid_request");
                    return;
                }
            }
            catch
            {
                WriteReply(response, HttpStatusCode.BadRequest, "invalid_llsd");
                return;
            }

            if (!scene.TryGetScenePresence(agentID, out ScenePresence self) ||
                self == null || self.IsChildAgent ||
                self.LifecycleState != ScenePresenceState.Running)
            {
                WriteReply(response, HttpStatusCode.Conflict, "presence_unavailable");
                return;
            }

            string action = input.ContainsKey("action") ? input["action"].AsString() : string.Empty;
            OSDMap output = new OSDMap();

            if (action == "set_policy")
            {
                string policy = input.ContainsKey("policy") ? input["policy"].AsString() : string.Empty;
                switch (policy)
                {
                    case "off":
                        self.InteractionPolicy = AvatarInteractionPolicy.Off;
                        break;
                    case "friends":
                        self.InteractionPolicy = AvatarInteractionPolicy.Friends;
                        break;
                    case "ask":
                        self.InteractionPolicy = AvatarInteractionPolicy.Ask;
                        break;
                    case "everyone":
                        self.InteractionPolicy = AvatarInteractionPolicy.Everyone;
                        break;
                    default:
                        WriteReply(response, HttpStatusCode.BadRequest, "invalid_policy");
                        return;
                }

                output["ok"] = OSD.FromBoolean(true);
                output["policy"] = OSD.FromString(policy);
                output["status"] = OSD.FromString("saved_for_this_presence");
            }
            else if (action == "check")
            {
                if (!TryGetTarget(scene, input, agentID, out ScenePresence target, response))
                    return;

                bool eligible = target.CanReceiveAvatarInteractionFrom(self);
                output["ok"] = OSD.FromBoolean(true);
                output["eligible"] = OSD.FromBoolean(eligible);
                output["status"] = OSD.FromString(eligible ? "eligible" : "not_permitted_or_out_of_range");
            }
            else if (action == "invite")
            {
                HandleInvite(scene, self, input, response);
                return;
            }
            else if (action == "accept" || action == "reject" || action == "cancel")
            {
                HandleInvitationDecision(scene, self, input, action, response);
                return;
            }
            else if (action == "end")
            {
                HandleSessionEnd(scene, self, input, response);
                return;
            }
            else
            {
                WriteReply(response, HttpStatusCode.BadRequest, "unknown_action");
                return;
            }

            WriteReply(response, HttpStatusCode.OK, output);
        }

        private static bool TryGetTarget(Scene scene, OSDMap input, UUID agentID,
            out ScenePresence target, IOSHttpResponse response)
        {
            target = null;
            string targetText = input.ContainsKey("target") ? input["target"].AsString() : string.Empty;
            if (!UUID.TryParse(targetText, out UUID targetID) || targetID == agentID)
            {
                WriteReply(response, HttpStatusCode.BadRequest, "invalid_target");
                return false;
            }

            if (!scene.TryGetScenePresence(targetID, out target) || target == null)
            {
                WriteReply(response, HttpStatusCode.NotFound, "target_not_present");
                return false;
            }

            return true;
        }

        private static void HandleInvite(Scene scene, ScenePresence requester,
            OSDMap input, IOSHttpResponse response)
        {
            if (!TryGetTarget(scene, input, requester.UUID, out ScenePresence target, response))
                return;

            string kind = input.ContainsKey("interaction") ? input["interaction"].AsString() : string.Empty;
            if (kind != "handshake" && kind != "high_five" && kind != "shoulder_tap")
            {
                WriteReply(response, HttpStatusCode.BadRequest, "unsupported_interaction");
                return;
            }

            if (!target.CanReceiveAvatarInteractionFrom(requester))
            {
                WriteReply(response, HttpStatusCode.Forbidden, "not_permitted_or_out_of_range");
                return;
            }

            AvatarInteractionModule module = scene.RequestModuleInterface<AvatarInteractionModule>();
            if (module == null)
            {
                WriteReply(response, HttpStatusCode.ServiceUnavailable, "interaction_module_unavailable");
                return;
            }

            InteractionInvitation invitation = new InteractionInvitation
            {
                Id = UUID.Random(),
                Requester = requester.UUID,
                Target = target.UUID,
                Kind = kind,
                ExpiresUtc = DateTime.UtcNow.AddSeconds(InvitationLifetimeSeconds)
            };

            lock (module.m_invitationLock)
            {
                module.PruneExpiredInvitations();
                if (!module.TryConsumeInviteAttempt(requester.UUID, DateTime.UtcNow))
                {
                    WriteReply(response, (HttpStatusCode)429, "invite_rate_limited");
                    return;
                }

                if (module.m_invitations.Count >= MaxPendingInvitations)
                {
                    WriteReply(response, HttpStatusCode.ServiceUnavailable, "invitation_capacity_reached");
                    return;
                }

                foreach (InteractionInvitation pending in module.m_invitations.Values)
                {
                    if (pending.Target == target.UUID)
                    {
                        WriteReply(response, HttpStatusCode.Conflict, "target_has_pending_invitation");
                        return;
                    }
                }

                module.m_invitations[invitation.Id] = invitation;
            }

            OSDMap body = new OSDMap();
            body["request_id"] = OSD.FromUUID(invitation.Id);
            body["requester"] = OSD.FromUUID(requester.UUID);
            body["requester_name"] = OSD.FromString(requester.Name);
            body["interaction"] = OSD.FromString(kind);
            body["expires_in"] = OSD.FromInteger(InvitationLifetimeSeconds);

            if (!module.EnqueueInteractionEvent(target.UUID, "AvatarInteractionInvite", body))
            {
                lock (module.m_invitationLock)
                    module.m_invitations.Remove(invitation.Id);
                WriteReply(response, HttpStatusCode.ServiceUnavailable, "target_event_queue_unavailable");
                return;
            }

            OSDMap output = new OSDMap();
            output["ok"] = OSD.FromBoolean(true);
            output["request_id"] = OSD.FromUUID(invitation.Id);
            output["status"] = OSD.FromString("invitation_sent");
            WriteReply(response, HttpStatusCode.OK, output);
        }

        private static void HandleInvitationDecision(Scene scene, ScenePresence actor,
            OSDMap input, string action, IOSHttpResponse response)
        {
            string requestText = input.ContainsKey("request_id") ? input["request_id"].AsString() : string.Empty;
            if (!UUID.TryParse(requestText, out UUID requestID))
            {
                WriteReply(response, HttpStatusCode.BadRequest, "invalid_request_id");
                return;
            }

            AvatarInteractionModule module = scene.RequestModuleInterface<AvatarInteractionModule>();
            if (module == null)
            {
                WriteReply(response, HttpStatusCode.ServiceUnavailable, "interaction_module_unavailable");
                return;
            }

            InteractionInvitation invitation;
            lock (module.m_invitationLock)
            {
                module.PruneExpiredInvitations();
                if (!module.m_invitations.TryGetValue(requestID, out invitation))
                {
                    WriteReply(response, HttpStatusCode.NotFound, "invitation_expired_or_unknown");
                    return;
                }

                bool actorIsTarget = actor.UUID == invitation.Target;
                bool actorIsRequester = actor.UUID == invitation.Requester;
                if ((action == "accept" || action == "reject") && !actorIsTarget ||
                    action == "cancel" && !actorIsRequester)
                {
                    WriteReply(response, HttpStatusCode.Forbidden, "not_invitation_participant");
                    return;
                }

                // A rejection or cancellation consumes the invitation immediately.
                // Acceptance is committed only after presence, range and session-capacity
                // checks succeed, so a transient conflict does not destroy a valid invite.
                if (action != "accept")
                    module.m_invitations.Remove(requestID);
            }

            UUID sessionID = UUID.Zero;
            if (action == "accept")
            {
                if (!scene.TryGetScenePresence(invitation.Requester, out ScenePresence requester) ||
                    requester == null || !actor.CanReceiveAvatarInteractionFrom(requester))
                {
                    lock (module.m_invitationLock)
                        module.m_invitations.Remove(requestID);

                    module.EnqueueInteractionEvent(invitation.Requester, "AvatarInteractionEnded",
                        BuildRequestBody(invitation, "expired", "requester_unavailable_or_out_of_range"));
                    WriteReply(response, HttpStatusCode.Conflict, "requester_unavailable_or_out_of_range");
                    return;
                }

                InteractionSession session = new InteractionSession
                {
                    Id = UUID.Random(),
                    RequestId = invitation.Id,
                    Requester = invitation.Requester,
                    Target = invitation.Target,
                    Kind = invitation.Kind,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(InteractionSessionLifetimeSeconds)
                };

                lock (module.m_invitationLock)
                {
                    // Re-check under the lock: another accept/cancel may have consumed
                    // this invitation while the presence/range checks were running.
                    if (!module.m_invitations.ContainsKey(requestID))
                    {
                        WriteReply(response, HttpStatusCode.Conflict, "invitation_already_consumed");
                        return;
                    }

                    module.PruneExpiredSessions(DateTime.UtcNow);
                    if (module.m_sessions.Count >= MaxActiveSessions ||
                        module.HasActiveSession(invitation.Requester) ||
                        module.HasActiveSession(invitation.Target))
                    {
                        WriteReply(response, HttpStatusCode.Conflict, "participant_already_in_interaction");
                        return;
                    }

                    module.m_invitations.Remove(requestID);
                    module.m_sessions[session.Id] = session;
                }

                sessionID = session.Id;
                OSDMap accepted = BuildSessionBody(session, "accepted", string.Empty);
                module.EnqueueInteractionEvent(invitation.Requester, "AvatarInteractionAccepted", accepted);
                module.EnqueueInteractionEvent(invitation.Target, "AvatarInteractionAccepted", accepted);
            }
            else
            {
                string status = action == "reject" ? "rejected" : "cancelled";
                UUID notify = action == "reject" ? invitation.Requester : invitation.Target;
                module.EnqueueInteractionEvent(notify, "AvatarInteractionEnded",
                    BuildRequestBody(invitation, status, string.Empty));
            }

            OSDMap output = new OSDMap();
            output["ok"] = OSD.FromBoolean(true);
            output["status"] = OSD.FromString(action == "accept" ? "accepted" :
                action == "reject" ? "rejected" : "cancelled");
            if (action == "accept")
                output["session_id"] = OSD.FromUUID(sessionID);
            WriteReply(response, HttpStatusCode.OK, output);
        }

        private static void HandleSessionEnd(Scene scene, ScenePresence actor,
            OSDMap input, IOSHttpResponse response)
        {
            string sessionText = input.ContainsKey("session_id") ? input["session_id"].AsString() : string.Empty;
            if (!UUID.TryParse(sessionText, out UUID sessionID))
            {
                WriteReply(response, HttpStatusCode.BadRequest, "invalid_session_id");
                return;
            }

            AvatarInteractionModule module = scene.RequestModuleInterface<AvatarInteractionModule>();
            if (module == null)
            {
                WriteReply(response, HttpStatusCode.ServiceUnavailable, "interaction_module_unavailable");
                return;
            }

            InteractionSession session;
            lock (module.m_invitationLock)
            {
                module.PruneExpiredSessions(DateTime.UtcNow);
                if (!module.m_sessions.TryGetValue(sessionID, out session))
                {
                    WriteReply(response, HttpStatusCode.NotFound, "session_not_found_or_expired");
                    return;
                }

                if (actor.UUID != session.Requester && actor.UUID != session.Target)
                {
                    WriteReply(response, HttpStatusCode.Forbidden, "not_interaction_participant");
                    return;
                }

                module.m_sessions.Remove(sessionID);
            }

            OSDMap ended = BuildSessionBody(session, "ended", "participant_requested_end");
            module.EnqueueInteractionEvent(session.Requester, "AvatarInteractionEnded", ended);
            module.EnqueueInteractionEvent(session.Target, "AvatarInteractionEnded", ended);

            OSDMap output = new OSDMap();
            output["ok"] = OSD.FromBoolean(true);
            output["status"] = OSD.FromString("ended");
            output["session_id"] = OSD.FromUUID(sessionID);
            WriteReply(response, HttpStatusCode.OK, output);
        }

        private static OSDMap BuildSessionBody(InteractionSession session,
            string status, string reason)
        {
            OSDMap body = new OSDMap();
            body["request_id"] = OSD.FromUUID(session.RequestId);
            body["session_id"] = OSD.FromUUID(session.Id);
            body["requester"] = OSD.FromUUID(session.Requester);
            body["target"] = OSD.FromUUID(session.Target);
            body["interaction"] = OSD.FromString(session.Kind);
            body["status"] = OSD.FromString(status);
            body["reason"] = OSD.FromString(reason);
            body["expires_in"] = OSD.FromInteger(InteractionSessionLifetimeSeconds);
            return body;
        }

        private bool HasActiveSession(UUID participantID)
        {
            foreach (InteractionSession session in m_sessions.Values)
            {
                if (session.Requester == participantID || session.Target == participantID)
                    return true;
            }
            return false;
        }

        // Called only while m_invitationLock is held. Expiry is lazy: expired
        // sessions are pruned when the next participant action reaches this region.
        private void PruneExpiredSessions(DateTime now)
        {
            List<UUID> expired = new List<UUID>();
            foreach (KeyValuePair<UUID, InteractionSession> pair in m_sessions)
            {
                if (pair.Value.ExpiresUtc <= now)
                    expired.Add(pair.Key);
            }

            foreach (UUID id in expired)
            {
                InteractionSession session = m_sessions[id];
                m_sessions.Remove(id);
                OSDMap ended = BuildSessionBody(session, "expired", "session_timeout");
                EnqueueInteractionEvent(session.Requester, "AvatarInteractionEnded", ended);
                EnqueueInteractionEvent(session.Target, "AvatarInteractionEnded", ended);
            }
        }

        private static OSDMap BuildRequestBody(InteractionInvitation invitation,
            string status, string reason)
        {
            OSDMap body = new OSDMap();
            body["request_id"] = OSD.FromUUID(invitation.Id);
            body["requester"] = OSD.FromUUID(invitation.Requester);
            body["target"] = OSD.FromUUID(invitation.Target);
            body["interaction"] = OSD.FromString(invitation.Kind);
            body["status"] = OSD.FromString(status);
            body["reason"] = OSD.FromString(reason);
            return body;
        }

        private bool EnqueueInteractionEvent(UUID agentID, string eventName, OSDMap body)
        {
            IEventQueue eventQueue = m_scene?.RequestModuleInterface<IEventQueue>();
            if (eventQueue == null)
                return false;

            OSDMap message = new OSDMap();
            message["message"] = OSD.FromString(eventName);
            message["body"] = body;
            return eventQueue.Enqueue(message, agentID);
        }

        // Called only while m_invitationLock is held. Uses a rolling window so
        // bursts are bounded without penalising users for unrelated interactions.
        private bool TryConsumeInviteAttempt(UUID requesterID, DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-InviteRateWindowSeconds);

            if (!m_inviteAttempts.TryGetValue(requesterID, out Queue<DateTime> attempts))
            {
                if (m_inviteAttempts.Count >= MaxTrackedInviteSenders)
                {
                    // Remove idle senders before refusing to grow this per-region table.
                    List<UUID> idleSenders = new List<UUID>();
                    foreach (KeyValuePair<UUID, Queue<DateTime>> pair in m_inviteAttempts)
                    {
                        while (pair.Value.Count > 0 && pair.Value.Peek() <= cutoff)
                            pair.Value.Dequeue();
                        if (pair.Value.Count == 0)
                            idleSenders.Add(pair.Key);
                    }

                    foreach (UUID idleSender in idleSenders)
                        m_inviteAttempts.Remove(idleSender);

                    if (m_inviteAttempts.Count >= MaxTrackedInviteSenders)
                        return false;
                }

                attempts = new Queue<DateTime>();
                m_inviteAttempts[requesterID] = attempts;
            }

            while (attempts.Count > 0 && attempts.Peek() <= cutoff)
                attempts.Dequeue();

            if (attempts.Count >= MaxInvitesPerWindow)
                return false;

            attempts.Enqueue(now);
            return true;
        }

        private void PruneExpiredInvitations()
        {
            DateTime now = DateTime.UtcNow;
            List<UUID> expired = new List<UUID>();
            foreach (KeyValuePair<UUID, InteractionInvitation> pair in m_invitations)
            {
                if (pair.Value.ExpiresUtc <= now)
                    expired.Add(pair.Key);
            }

            foreach (UUID id in expired)
                m_invitations.Remove(id);
        }

        private static void WriteReply(IOSHttpResponse response, HttpStatusCode status, string reason)
        {
            OSDMap output = new OSDMap();
            output["ok"] = OSD.FromBoolean(false);
            output["status"] = OSD.FromString(reason);
            WriteReply(response, status, output);
        }

        private static void WriteReply(IOSHttpResponse response, HttpStatusCode status, OSDMap output)
        {
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(output);
            response.ContentType = "application/llsd+xml";
            response.StatusCode = (int)status;
        }
    }
}