/*
 * OpenSim-next AI Friend persistence contracts.
 * Shared data-layer types; deliberately independent of Region and Viewer assemblies.
 */
using System;
using System.Collections.Generic;

namespace OpenSim.Data
{
    public sealed class AiFriendCharacterData
    {
        public Guid CharacterId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string ProfileDescription { get; set; } = string.Empty;
        public int PersonaVersion { get; set; } = 1;
        public Guid AppearanceAssetId { get; set; }
        public Guid VoiceProfileId { get; set; }
        public Guid PublisherId { get; set; }
        public int Status { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public sealed class AiFriendRelationshipData
    {
        public Guid RelationshipId { get; set; }
        public Guid UserId { get; set; }
        public Guid CharacterId { get; set; }
        public int State { get; set; }
        public int RelationshipLevel { get; set; }
        public DateTime FirstMetUtc { get; set; }
        public DateTime? FriendsSinceUtc { get; set; }
        public DateTime? LastInteractionUtc { get; set; }
        public bool AllowProactiveGreeting { get; set; }
        public bool AllowFollow { get; set; }
        public bool AllowVoice { get; set; }
        public long Version { get; set; }
    }

    public sealed class AiFriendMemoryData
    {
        public Guid MemoryId { get; set; }
        public Guid RelationshipId { get; set; }
        public int MemoryType { get; set; }
        public string Content { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public Guid SourceConversationId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastConfirmedUtc { get; set; }
    }

    /// <summary>
    /// Every private relationship/memory operation is scoped by the authenticated user.
    /// Implementations must not infer the user identity from AI-generated content.
    /// </summary>
    public interface IAiFriendData
    {
        AiFriendCharacterData GetCharacter(Guid characterId);
        bool SaveCharacter(AiFriendCharacterData character);

        AiFriendRelationshipData GetRelationship(Guid authenticatedUserId, Guid characterId);
        bool CreateRelationship(AiFriendRelationshipData relationship, Guid authenticatedUserId);
        bool UpdateRelationship(AiFriendRelationshipData relationship, Guid authenticatedUserId, long expectedVersion);

        bool AddMemory(AiFriendMemoryData memory, Guid authenticatedUserId);
        List<AiFriendMemoryData> GetMemories(Guid authenticatedUserId, Guid relationshipId, int limit);
        bool DeleteMemory(Guid authenticatedUserId, Guid relationshipId, Guid memoryId);
    }
}
