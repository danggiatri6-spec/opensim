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
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

namespace OpenSim.Region.ClientStack.Linden
{
    /// <summary>
    /// Optional, per-agent capability for desktop avatar interaction preferences
    /// and server-side eligibility checks. Legacy viewers do not request this cap.
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "AvatarInteractionModule")]
    public class AvatarInteractionModule : ISharedRegionModule
    {
        private readonly List<Scene> m_scenes = new();

        public string Name => "AvatarInteractionModule";
        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource source) { }

        public void AddRegion(Scene scene)
        {
            lock (m_scenes)
                m_scenes.Add(scene);
        }

        public void RemoveRegion(Scene scene)
        {
            scene.EventManager.OnRegisterCaps -= RegisterCaps;
            lock (m_scenes)
                m_scenes.Remove(scene);
        }

        public void RegionLoaded(Scene scene)
        {
            scene.EventManager.OnRegisterCaps += RegisterCaps;
        }

        public void PostInitialise() { }
        public void Close() { }

        private void RegisterCaps(UUID agentID, Caps caps)
        {
            Scene scene = null;
            lock (m_scenes)
            {
                foreach (Scene candidate in m_scenes)
                {
                    if (candidate.TryGetScenePresence(agentID, out ScenePresence presence) && presence != null)
                    {
                        scene = candidate;
                        break;
                    }
                }
            }

            if (scene == null)
                return;

            string capPath = "/" + UUID.Random().ToString();
            Scene registeredScene = scene;
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
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }
            }
            catch
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
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
                string targetText = input.ContainsKey("target") ? input["target"].AsString() : string.Empty;
                if (!UUID.TryParse(targetText, out UUID targetID) || targetID == agentID)
                {
                    WriteReply(response, HttpStatusCode.BadRequest, "invalid_target");
                    return;
                }

                if (!scene.TryGetScenePresence(targetID, out ScenePresence target) || target == null)
                {
                    WriteReply(response, HttpStatusCode.NotFound, "target_not_present");
                    return;
                }

                bool eligible = target.CanReceiveAvatarInteractionFrom(self);
                output["ok"] = OSD.FromBoolean(true);
                output["eligible"] = OSD.FromBoolean(eligible);
                output["status"] = OSD.FromString(eligible ? "eligible" : "not_permitted_or_out_of_range");
            }
            else
            {
                WriteReply(response, HttpStatusCode.BadRequest, "unknown_action");
                return;
            }

            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(output);
            response.StatusCode = (int)HttpStatusCode.OK;
        }

        private static void WriteReply(IOSHttpResponse response, HttpStatusCode status, string reason)
        {
            OSDMap output = new OSDMap();
            output["ok"] = OSD.FromBoolean(false);
            output["status"] = OSD.FromString(reason);
            response.RawBuffer = OSDParser.SerializeLLSDXmlBytes(output);
            response.ContentType = "application/llsd+xml";
            response.StatusCode = (int)status;
        }
    }
}
