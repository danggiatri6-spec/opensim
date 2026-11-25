/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 */

using System;
using NUnit.Framework;
using OpenSim.Region.OptionalModules.World.NPC.AIFriend;

namespace OpenSim.Region.OptionalModules.World.NPC.Tests
{
    [TestFixture]
    public class AiFriendDomainTests
    {
        [Test]
        public void CharacterMustDiscloseAiIdentity()
        {
            AiCharacterDefinition character = new AiCharacterDefinition();
            character.DisplayName = "Mira";
            character.Status = AiCharacterStatus.Active;
            character.Validate();

            character.IsAiDisclosureEnabled = false;
            Assert.Throws<ArgumentException>(() => character.Validate());
        }

        [Test]
        public void RelationshipCannotBeTransitionedByAnotherUser()
        {
            Guid owner = Guid.NewGuid();
            AiFriendRelationship relationship = new AiFriendRelationship
            {
                UserId = owner,
                CharacterId = Guid.NewGuid(),
                State = AiFriendRelationshipState.Met
            };

            Assert.Throws<UnauthorizedAccessException>(() =>
                AiFriendRelationshipRules.Transition(
                    relationship, Guid.NewGuid(), AiFriendRelationshipState.Requested, DateTime.UtcNow));
            Assert.AreEqual(AiFriendRelationshipState.Met, relationship.State);
        }

        [Test]
        public void FriendshipRequiresExplicitRequestAndAcceptance()
        {
            AiFriendRelationship relationship = new AiFriendRelationship
            {
                UserId = Guid.NewGuid(),
                CharacterId = Guid.NewGuid(),
                State = AiFriendRelationshipState.Met
            };

            Assert.Throws<InvalidOperationException>(() =>
                AiFriendRelationshipRules.Transition(
                    relationship, relationship.UserId, AiFriendRelationshipState.Friends, DateTime.UtcNow));

            AiFriendRelationshipRules.Transition(
                relationship, relationship.UserId, AiFriendRelationshipState.Requested, DateTime.UtcNow);
            AiFriendRelationshipRules.Transition(
                relationship, relationship.UserId, AiFriendRelationshipState.Friends, DateTime.UtcNow);

            Assert.AreEqual(AiFriendRelationshipState.Friends, relationship.State);
            Assert.IsTrue(relationship.FriendsSinceUtc.HasValue);
        }

        [Test]
        public void BlockingDisablesProactiveCapabilities()
        {
            AiFriendRelationship relationship = new AiFriendRelationship
            {
                UserId = Guid.NewGuid(),
                CharacterId = Guid.NewGuid(),
                State = AiFriendRelationshipState.Friends,
                AllowProactiveGreeting = true,
                AllowFollow = true,
                AllowVoice = true
            };

            AiFriendRelationshipRules.Transition(
                relationship, relationship.UserId, AiFriendRelationshipState.Blocked, DateTime.UtcNow);

            Assert.IsFalse(relationship.AllowProactiveGreeting);
            Assert.IsFalse(relationship.AllowFollow);
            Assert.IsFalse(relationship.AllowVoice);
        }

        [Test]
        public void MemoryMustBeRelationshipScopedAndBounded()
        {
            AiFriendMemory memory = new AiFriendMemory
            {
                RelationshipId = Guid.NewGuid(),
                Content = new string('x', AiFriendLimits.MaxMemoryLength + 1)
            };

            Assert.Throws<ArgumentException>(() => memory.Validate());
        }

        [Test]
        public void RelationshipRejectsNonUtcTimestamps()
        {
            AiFriendRelationship relationship = new AiFriendRelationship
            {
                UserId = Guid.NewGuid(),
                CharacterId = Guid.NewGuid(),
                FirstMetUtc = DateTime.Now
            };

            Assert.Throws<ArgumentException>(() => relationship.Validate());
        }
    }
}
