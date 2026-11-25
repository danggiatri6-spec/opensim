/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * This file is part of the OpenSim-next AI Friend extension.
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Region.OptionalModules.World.NPC.AIFriend
{
    /// <summary>
    /// Stable identity and public persona for an AI-controlled NPC.
    /// User-specific memories must never be stored on this object.
    /// </summary>
    public sealed class AiCharacterDefinition
    {
        public Guid CharacterId { get; set; }
        public string DisplayName { get; set; }
        public string ProfileDescription { get; set; }
        public int PersonaVersion { get; set; }
        public Guid AppearanceAssetId { get; set; }
        public Guid VoiceProfileId { get; set; }
        public Guid PublisherId { get; set; }
        public bool IsAiDisclosureEnabled { get; set; }
        public AiCharacterStatus Status { get; set; }

        public AiCharacterDefinition()
        {
            CharacterId = Guid.NewGuid();
            DisplayName = String.Empty;
            ProfileDescription = String.Empty;
            PersonaVersion = 1;
            IsAiDisclosureEnabled = true;
            Status = AiCharacterStatus.Draft;
        }

        public void Validate()
        {
            if (CharacterId == Guid.Empty)
                throw new ArgumentException("CharacterId is required.");
            if (String.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > AiFriendLimits.MaxDisplayNameLength)
                throw new ArgumentException("DisplayName must contain 1-64 characters.");
            if (ProfileDescription == null || ProfileDescription.Length > AiFriendLimits.MaxProfileDescriptionLength)
                throw new ArgumentException("ProfileDescription exceeds the allowed length.");
            if (PersonaVersion < 1)
                throw new ArgumentException("PersonaVersion must be positive.");
            if (!IsAiDisclosureEnabled)
                throw new ArgumentException("AI-controlled characters must disclose that they are AI.");
        }
    }

    public enum AiCharacterStatus
    {
        Draft,
        Active,
        Disabled,
        Archived
    }

    public enum AiFriendRelationshipState
    {
        Met,
        Requested,
        Friends,
        Paused,
        Unfriended,
        Blocked
    }

    /// <summary>
    /// Private relationship state for exactly one user and one AI character.
    /// </summary>
    public sealed class AiFriendRelationship
    {
        public Guid RelationshipId { get; set; }
        public Guid UserId { get; set; }
        public Guid CharacterId { get; set; }
        public AiFriendRelationshipState State { get; set; }
        public int RelationshipLevel { get; set; }
        public DateTime FirstMetUtc { get; set; }
        public DateTime? FriendsSinceUtc { get; set; }
        public DateTime? LastInteractionUtc { get; set; }
        public bool AllowProactiveGreeting { get; set; }
        public bool AllowFollow { get; set; }
        public bool AllowVoice { get; set; }
        public long Version { get; set; }

        public AiFriendRelationship()
        {
            RelationshipId = Guid.NewGuid();
            State = AiFriendRelationshipState.Met;
            FirstMetUtc = DateTime.UtcNow;
            Version = 1;
        }

        public void Validate()
        {
            if (RelationshipId == Guid.Empty || UserId == Guid.Empty || CharacterId == Guid.Empty)
                throw new ArgumentException("Relationship, user, and character identifiers are required.");
            if (RelationshipLevel < 0 || RelationshipLevel > 100)
                throw new ArgumentException("RelationshipLevel must be between 0 and 100.");
            if (Version < 1)
                throw new ArgumentException("Version must be positive.");
            if (FirstMetUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("FirstMetUtc must be UTC.");
            if (FriendsSinceUtc.HasValue && FriendsSinceUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("FriendsSinceUtc must be UTC.");
            if (LastInteractionUtc.HasValue && LastInteractionUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("LastInteractionUtc must be UTC.");
        }
    }

    public enum AiFriendMemoryType
    {
        Preference,
        ImportantFact,
        SharedExperience,
        OngoingTopic,
        Boundary
    }

    /// <summary>
    /// A memory scoped to one user-character relationship. Never share across users.
    /// </summary>
    public sealed class AiFriendMemory
    {
        public Guid MemoryId { get; set; }
        public Guid RelationshipId { get; set; }
        public AiFriendMemoryType MemoryType { get; set; }
        public string Content { get; set; }
        public double Confidence { get; set; }
        public Guid SourceConversationId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? LastConfirmedUtc { get; set; }
        public bool IsDeleted { get; set; }

        public AiFriendMemory()
        {
            MemoryId = Guid.NewGuid();
            Content = String.Empty;
            Confidence = 1.0;
            CreatedUtc = DateTime.UtcNow;
        }

        public void Validate()
        {
            if (MemoryId == Guid.Empty || RelationshipId == Guid.Empty)
                throw new ArgumentException("Memory and relationship identifiers are required.");
            if (String.IsNullOrWhiteSpace(Content) || Content.Length > AiFriendLimits.MaxMemoryLength)
                throw new ArgumentException("Memory content must contain 1-2000 characters.");
            if (Double.IsNaN(Confidence) || Double.IsInfinity(Confidence) || Confidence < 0 || Confidence > 1)
                throw new ArgumentException("Confidence must be between 0 and 1.");
            if (CreatedUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("CreatedUtc must be UTC.");
            if (LastConfirmedUtc.HasValue && LastConfirmedUtc.Value.Kind != DateTimeKind.Utc)
                throw new ArgumentException("LastConfirmedUtc must be UTC.");
        }
    }

    public static class AiFriendLimits
    {
        public const int MaxDisplayNameLength = 64;
        public const int MaxProfileDescriptionLength = 1000;
        public const int MaxMemoryLength = 2000;
        public const int MaxConversationMessageLength = 4000;
    }

    /// <summary>
    /// Centralized, explicit relationship state transitions. Callers must
    /// authenticate the user before invoking a transition.
    /// </summary>
    public static class AiFriendRelationshipRules
    {
        public static bool CanTransition(AiFriendRelationshipState current, AiFriendRelationshipState next)
        {
            switch (current)
            {
                case AiFriendRelationshipState.Met:
                    return next == AiFriendRelationshipState.Requested ||
                           next == AiFriendRelationshipState.Blocked;
                case AiFriendRelationshipState.Requested:
                    return next == AiFriendRelationshipState.Friends ||
                           next == AiFriendRelationshipState.Met ||
                           next == AiFriendRelationshipState.Blocked;
                case AiFriendRelationshipState.Friends:
                    return next == AiFriendRelationshipState.Paused ||
                           next == AiFriendRelationshipState.Unfriended ||
                           next == AiFriendRelationshipState.Blocked;
                case AiFriendRelationshipState.Paused:
                    return next == AiFriendRelationshipState.Friends ||
                           next == AiFriendRelationshipState.Unfriended ||
                           next == AiFriendRelationshipState.Blocked;
                case AiFriendRelationshipState.Unfriended:
                    return next == AiFriendRelationshipState.Requested ||
                           next == AiFriendRelationshipState.Blocked;
                case AiFriendRelationshipState.Blocked:
                    return next == AiFriendRelationshipState.Met;
                default:
                    return false;
            }
        }

        public static void Transition(AiFriendRelationship relationship,
            Guid authenticatedUserId, AiFriendRelationshipState next, DateTime nowUtc)
        {
            if (relationship == null)
                throw new ArgumentNullException(nameof(relationship));
            if (authenticatedUserId == Guid.Empty || relationship.UserId != authenticatedUserId)
                throw new UnauthorizedAccessException("The authenticated user does not own this relationship.");
            if (nowUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Transition time must be UTC.", nameof(nowUtc));
            if (!CanTransition(relationship.State, next))
                throw new InvalidOperationException("The requested AI Friend relationship transition is not allowed.");

            relationship.State = next;
            relationship.Version++;
            if (next == AiFriendRelationshipState.Friends && !relationship.FriendsSinceUtc.HasValue)
                relationship.FriendsSinceUtc = nowUtc;
            if (next == AiFriendRelationshipState.Unfriended || next == AiFriendRelationshipState.Blocked)
            {
                relationship.AllowProactiveGreeting = false;
                relationship.AllowFollow = false;
                relationship.AllowVoice = false;
            }
        }
    }

    /// <summary>
    /// Persistence boundary. Implementations must enforce unique (UserId, CharacterId)
    /// and relationship-scoped memory access in the storage layer.
    /// </summary>
    public interface IAiFriendRelationshipStore
    {
        AiFriendRelationship Get(Guid authenticatedUserId, Guid characterId);
        AiFriendRelationship Create(Guid authenticatedUserId, AiFriendRelationship relationship);
        AiFriendRelationship Update(Guid authenticatedUserId, AiFriendRelationship relationship, long expectedVersion);
        IList<AiFriendMemory> GetMemories(Guid authenticatedUserId, Guid relationshipId, int limit);
        AiFriendMemory AddMemory(Guid authenticatedUserId, AiFriendMemory memory);
        bool DeleteMemory(Guid authenticatedUserId, Guid relationshipId, Guid memoryId);
    }
}
