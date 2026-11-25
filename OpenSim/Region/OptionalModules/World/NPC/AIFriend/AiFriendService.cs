/*
 * OpenSim-next AI Friend application service and SQLite data adapter.
 */
using System;
using System.Collections.Generic;
using OpenSim.Data;

namespace OpenSim.Region.OptionalModules.World.NPC.AIFriend
{
    /// <summary>
    /// Application boundary. Callers must pass the authenticated principal ID;
    /// model-generated text or action proposals are never an identity source.
    /// </summary>
    public sealed class AiFriendService
    {
        private readonly IAiFriendData _data;

        public AiFriendService(IAiFriendData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public AiCharacterDefinition GetCharacter(Guid characterId)
        {
            AiFriendCharacterData row = _data.GetCharacter(characterId);
            return row == null ? null : FromData(row);
        }

        public bool SaveCharacter(AiCharacterDefinition character)
        {
            if (character == null) throw new ArgumentNullException(nameof(character));
            character.Validate();
            DateTime now = DateTime.UtcNow;
            AiFriendCharacterData row = new AiFriendCharacterData
            {
                CharacterId = character.CharacterId,
                DisplayName = character.DisplayName,
                ProfileDescription = character.ProfileDescription,
                PersonaVersion = character.PersonaVersion,
                AppearanceAssetId = character.AppearanceAssetId,
                VoiceProfileId = character.VoiceProfileId,
                PublisherId = character.PublisherId,
                Status = (int)character.Status,
                CreatedUtc = now,
                UpdatedUtc = now
            };
            return _data.SaveCharacter(row);
        }

        public AiFriendRelationship GetRelationship(Guid authenticatedUserId, Guid characterId)
        {
            RequireUser(authenticatedUserId);
            AiFriendRelationshipData row = _data.GetRelationship(authenticatedUserId, characterId);
            return row == null ? null : FromData(row);
        }

        /// <summary>Creates the initial "Met" relationship for the authenticated user.</summary>
        public AiFriendRelationship Meet(Guid authenticatedUserId, Guid characterId)
        {
            RequireUser(authenticatedUserId);
            AiFriendCharacterData character = _data.GetCharacter(characterId);
            if (character == null || character.Status != (int)AiCharacterStatus.Active)
                throw new InvalidOperationException("The AI character is not active.");

            AiFriendRelationship relationship = new AiFriendRelationship
            {
                UserId = authenticatedUserId,
                CharacterId = characterId,
                State = AiFriendRelationshipState.Met
            };
            relationship.Validate();
            AiFriendRelationshipData row = ToData(relationship);
            if (!_data.CreateRelationship(row, authenticatedUserId))
                throw new InvalidOperationException("The relationship already exists or could not be created.");
            return relationship;
        }

        public AiFriendRelationship RequestFriendship(Guid authenticatedUserId, Guid characterId)
        {
            return Transition(authenticatedUserId, characterId, AiFriendRelationshipState.Requested);
        }

        public AiFriendRelationship AcceptFriendship(Guid authenticatedUserId, Guid characterId)
        {
            return Transition(authenticatedUserId, characterId, AiFriendRelationshipState.Friends);
        }

        public AiFriendRelationship Pause(Guid authenticatedUserId, Guid characterId)
        {
            return Transition(authenticatedUserId, characterId, AiFriendRelationshipState.Paused);
        }

        public AiFriendRelationship Unfriend(Guid authenticatedUserId, Guid characterId)
        {
            return Transition(authenticatedUserId, characterId, AiFriendRelationshipState.Unfriended);
        }

        public AiFriendRelationship Block(Guid authenticatedUserId, Guid characterId)
        {
            return Transition(authenticatedUserId, characterId, AiFriendRelationshipState.Blocked);
        }

        public IList<AiFriendMemory> GetMemories(Guid authenticatedUserId, Guid characterId, int limit)
        {
            AiFriendRelationship relationship = GetRequiredRelationship(authenticatedUserId, characterId);
            List<AiFriendMemoryData> rows = _data.GetMemories(authenticatedUserId, relationship.RelationshipId, limit);
            List<AiFriendMemory> memories = new List<AiFriendMemory>();
            foreach (AiFriendMemoryData row in rows) memories.Add(FromData(row));
            return memories;
        }

        public bool AddMemory(Guid authenticatedUserId, Guid characterId, AiFriendMemory memory)
        {
            if (memory == null) throw new ArgumentNullException(nameof(memory));
            AiFriendRelationship relationship = GetRequiredRelationship(authenticatedUserId, characterId);
            if (memory.RelationshipId != relationship.RelationshipId)
                throw new UnauthorizedAccessException("Memory belongs to a different relationship.");
            memory.Validate();
            return _data.AddMemory(ToData(memory), authenticatedUserId);
        }

        public bool DeleteMemory(Guid authenticatedUserId, Guid characterId, Guid memoryId)
        {
            AiFriendRelationship relationship = GetRequiredRelationship(authenticatedUserId, characterId);
            return _data.DeleteMemory(authenticatedUserId, relationship.RelationshipId, memoryId);
        }

        private AiFriendRelationship Transition(Guid authenticatedUserId, Guid characterId, AiFriendRelationshipState next)
        {
            RequireUser(authenticatedUserId);
            AiFriendRelationship current = GetRequiredRelationship(authenticatedUserId, characterId);
            long expectedVersion = current.Version;
            AiFriendRelationshipRules.Transition(current, authenticatedUserId, next, DateTime.UtcNow);
            if (!_data.UpdateRelationship(ToData(current), authenticatedUserId, expectedVersion))
                throw new InvalidOperationException("The relationship changed concurrently; reload and retry.");
            return current;
        }

        private AiFriendRelationship GetRequiredRelationship(Guid userId, Guid characterId)
        {
            AiFriendRelationship relationship = GetRelationship(userId, characterId);
            if (relationship == null) throw new InvalidOperationException("No relationship exists for this user and character.");
            return relationship;
        }

        private static void RequireUser(Guid userId)
        {
            if (userId == Guid.Empty) throw new UnauthorizedAccessException("An authenticated user is required.");
        }

        private static AiCharacterDefinition FromData(AiFriendCharacterData row)
        {
            return new AiCharacterDefinition
            {
                CharacterId = row.CharacterId,
                DisplayName = row.DisplayName,
                ProfileDescription = row.ProfileDescription,
                PersonaVersion = row.PersonaVersion,
                AppearanceAssetId = row.AppearanceAssetId,
                VoiceProfileId = row.VoiceProfileId,
                PublisherId = row.PublisherId,
                Status = (AiCharacterStatus)row.Status,
                IsAiDisclosureEnabled = true
            };
        }

        private static AiFriendRelationship FromData(AiFriendRelationshipData row)
        {
            return new AiFriendRelationship
            {
                RelationshipId = row.RelationshipId,
                UserId = row.UserId,
                CharacterId = row.CharacterId,
                State = (AiFriendRelationshipState)row.State,
                RelationshipLevel = row.RelationshipLevel,
                FirstMetUtc = row.FirstMetUtc,
                FriendsSinceUtc = row.FriendsSinceUtc,
                LastInteractionUtc = row.LastInteractionUtc,
                AllowProactiveGreeting = row.AllowProactiveGreeting,
                AllowFollow = row.AllowFollow,
                AllowVoice = row.AllowVoice,
                Version = row.Version
            };
        }

        private static AiFriendMemory FromData(AiFriendMemoryData row)
        {
            return new AiFriendMemory
            {
                MemoryId = row.MemoryId,
                RelationshipId = row.RelationshipId,
                MemoryType = (AiFriendMemoryType)row.MemoryType,
                Content = row.Content,
                Confidence = row.Confidence,
                SourceConversationId = row.SourceConversationId,
                CreatedUtc = row.CreatedUtc,
                LastConfirmedUtc = row.LastConfirmedUtc
            };
        }

        private static AiFriendRelationshipData ToData(AiFriendRelationship value)
        {
            value.Validate();
            return new AiFriendRelationshipData
            {
                RelationshipId = value.RelationshipId,
                UserId = value.UserId,
                CharacterId = value.CharacterId,
                State = (int)value.State,
                RelationshipLevel = value.RelationshipLevel,
                FirstMetUtc = value.FirstMetUtc,
                FriendsSinceUtc = value.FriendsSinceUtc,
                LastInteractionUtc = value.LastInteractionUtc,
                AllowProactiveGreeting = value.AllowProactiveGreeting,
                AllowFollow = value.AllowFollow,
                AllowVoice = value.AllowVoice,
                Version = value.Version
            };
        }

        private static AiFriendMemoryData ToData(AiFriendMemory value)
        {
            return new AiFriendMemoryData
            {
                MemoryId = value.MemoryId,
                RelationshipId = value.RelationshipId,
                MemoryType = (int)value.MemoryType,
                Content = value.Content,
                Confidence = value.Confidence,
                SourceConversationId = value.SourceConversationId,
                CreatedUtc = value.CreatedUtc,
                LastConfirmedUtc = value.LastConfirmedUtc
            };
        }
    }
}
