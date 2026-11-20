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

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System;
using NUnit.Framework;
using OpenMetaverse.Packets;

namespace OpenSim.Region.ClientStack.LindenUDP.Tests
{
    /// <summary>
    /// Characterization tests for per-client packet handler registration.
    ///
    /// LLClientView's constructor requires a fully initialized Scene, UDP server,
    /// UDP client, and authenticated session. These tests isolate registration
    /// by creating an uninitialized instance and explicitly initializing only
    /// the handler dictionary touched by AddLocalPacketHandler.
    /// </summary>
    [TestFixture]
    public class PacketHandlerTests
    {
        private static LLClientView CreateRegistrationOnlyClient()
        {
            LLClientView client = (LLClientView)RuntimeHelpers.GetUninitializedObject(typeof(LLClientView));

            FieldInfo handlersField = typeof(LLClientView).GetField(
                "m_packetHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(handlersField, Is.Not.Null, "LLClientView handler dictionary field must exist");

            handlersField.SetValue(client, new Dictionary<PacketType, LLClientView.PacketProcessor>());

            return client;
        }

        [Test]
        public void FirstLocalPacketHandlerRegistrationSucceeds()
        {
            LLClientView client = CreateRegistrationOnlyClient();

            bool registered = client.AddLocalPacketHandler(PacketType.AgentUpdate, packet => { });

            Assert.That(registered, Is.True);
        }

        [Test]
        public void DuplicateLocalPacketHandlerRegistrationIsRejected()
        {
            LLClientView client = CreateRegistrationOnlyClient();
            PacketMethod firstHandler = packet => { };
            PacketMethod secondHandler = packet => { };

            Assert.That(client.AddLocalPacketHandler(PacketType.AgentUpdate, firstHandler), Is.True);
            Assert.That(client.AddLocalPacketHandler(PacketType.AgentUpdate, secondHandler), Is.False);

            FieldInfo handlersField = typeof(LLClientView).GetField(
                "m_packetHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var handlers = (Dictionary<PacketType, LLClientView.PacketProcessor>)handlersField.GetValue(client);

            Assert.That(handlers[PacketType.AgentUpdate].method, Is.SameAs(firstHandler),
                "A duplicate registration must not replace the original handler");
        }

        [Test]
        public void LocalPacketHandlerDefaultsToAsynchronous()
        {
            LLClientView client = CreateRegistrationOnlyClient();

            Assert.That(client.AddLocalPacketHandler(PacketType.AgentUpdate, packet => { }), Is.True);

            FieldInfo handlersField = typeof(LLClientView).GetField(
                "m_packetHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var handlers = (Dictionary<PacketType, LLClientView.PacketProcessor>)handlersField.GetValue(client);

            Assert.That(handlers[PacketType.AgentUpdate].Async, Is.True);
        }

        [Test]
        public void LocalPacketHandlerCanBeRegisteredAsSynchronous()
        {
            LLClientView client = CreateRegistrationOnlyClient();

            Assert.That(client.AddLocalPacketHandler(PacketType.AgentUpdate, packet => { }, false), Is.True);

            FieldInfo handlersField = typeof(LLClientView).GetField(
                "m_packetHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var handlers = (Dictionary<PacketType, LLClientView.PacketProcessor>)handlersField.GetValue(client);

            Assert.That(handlers[PacketType.AgentUpdate].Async, Is.False);
        }

        private static bool InvokeProcessPacketMethod(LLClientView client, Packet packet)
        {
            MethodInfo method = typeof(LLClientView).GetMethod(
                "ProcessPacketMethod",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(method, Is.Not.Null, "ProcessPacketMethod must exist");
            return (bool)method.Invoke(client, new object[] { packet });
        }

        [Test]
        public void StaticPacketHandlerTakesPrecedenceOverLocalHandler()
        {
            LLClientView client = CreateRegistrationOnlyClient();
            bool localHandlerInvoked = false;

            Assert.That(
                client.AddLocalPacketHandler(
                    PacketType.VelocityInterpolateOn,
                    packet => localHandlerInvoked = true,
                    false),
                Is.True);

            bool handled = InvokeProcessPacketMethod(client, new VelocityInterpolateOnPacket());

            FieldInfo velocityField = typeof(LLClientView).GetField(
                "m_VelocityInterpolate",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(handled, Is.True);
            Assert.That(localHandlerInvoked, Is.False,
                "The static handler must win when both handler tables contain the packet type");
            Assert.That(velocityField.GetValue(client), Is.True,
                "The static VelocityInterpolateOn handler should have executed");
        }

        [Test]
        public void UnknownPacketWithoutRegisteredHandlerReturnsFalse()
        {
            LLClientView client = CreateRegistrationOnlyClient();

            Assert.That(
                InvokeProcessPacketMethod(client, new AgentFoleyPacket()),
                Is.False);
        }

        [Test]
        public void GenericPacketHandlerNormalizesMethodAndRejectsDuplicates()
        {
            LLClientView client = CreateRegistrationOnlyClient();
            GenericMessage firstHandler = (sender, method, parameters) => { };
            GenericMessage secondHandler = (sender, method, parameters) => { };

            Assert.That(client.AddGenericPacketHandler("  HELLO  ", firstHandler), Is.True);
            Assert.That(client.AddGenericPacketHandler("hello", secondHandler), Is.False);

            FieldInfo handlersField = typeof(LLClientView).GetField(
                "m_genericPacketHandlers",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var handlers = (Dictionary<string, GenericMessage>)handlersField.GetValue(client);

            Assert.That(handlers.ContainsKey("hello"), Is.True);
            Assert.That(handlers["hello"], Is.SameAs(firstHandler));
        }
    }
}
