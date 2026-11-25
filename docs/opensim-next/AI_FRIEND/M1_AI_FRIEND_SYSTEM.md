# OpenSim-next AI Friend System — M1 Architecture

Status: Initial implementation specification
Target repository: `danggiatri6-spec/opensim`
Target branch: `opensim-next-dev`

## 1. Product definition

An AI Friend is a persistent, embodied NPC that a user can meet in the world and choose to befriend. It is not merely a help bot, scripted greeter, or generic ambient population.

The friendship is a continuing relationship between one authenticated user and one NPC identity. The NPC has a stable public identity and persona; each user's private conversations, memories, preferences, and relationship history are stored in a separate user-NPC relationship partition.

A user may meet an NPC without becoming friends. Friendship is established through an explicit in-world interaction/acceptance flow. The user can later pause, mute, unfriend, or delete the relationship data, subject to clearly explained retention rules.

AI identity must be transparent: the Viewer and NPC profile identify the character as AI-controlled. The NPC must not claim to be a human user.

## 2. Non-negotiable architectural boundaries

- Add the feature as an independent AI Friend service/module; do not put LLM calls, memory retrieval, or blocking network operations on simulator/Scene threads.
- Preserve existing OpenSim Region, Scene, Avatar, inventory, asset, chat, animation, LSL and OSSL behavior.
- Reuse existing NPC/avatar representation and movement/animation interfaces where possible; do not create a second incompatible avatar protocol.
- Keep AI provider integration behind an interface. No vendor-specific model SDK may leak into domain models.
- Treat model output as untrusted content. It may produce bounded dialogue and validated action proposals only; it must never execute arbitrary C#, scripts, console commands, or direct Scene mutations.
- Keep credentials in a separate encrypted secret store. Never serialize API keys into NPC profiles, relationship memories, project manifests, logs, or Viewer responses.
- Support provider failure and offline fallback: an AI Friend must not destabilize a Region if an LLM, speech service, or memory store is unavailable.

## 3. Domain model

### AiCharacter
Stable NPC identity shared across encounters:
- CharacterId (UUID)
- DisplayName
- ProfileDescription
- PersonaDefinitionVersion
- AvatarAppearanceReference
- VoiceProfileReference (optional)
- HomeLocation / permitted world scope
- OwnerOrPublisherId
- IsAiDisclosureEnabled (must be true)
- Status (Draft, Active, Disabled, Archived)
- CreatedUtc, UpdatedUtc

Persona definition is versioned and bounded. It describes tone, interests, background, conversational boundaries, and behavior preferences. It must not contain user-specific private memories.

### UserCharacterRelationship
One relationship per authenticated user and character:
- RelationshipId (UUID)
- UserId
- CharacterId
- State (Met, Requested, Friends, Paused, Unfriended, Blocked)
- RelationshipLevel (bounded, non-deceptive progression signal; not a claim of sentience)
- FirstMetUtc, FriendsSinceUtc, LastInteractionUtc
- UserPreferences (opt-in controls such as proactive greeting, follow, voice)
- CurrentMemoryVersion

Unique key: (UserId, CharacterId).

### ConversationRecord
- ConversationId
- RelationshipId
- StartedUtc, EndedUtc
- Channel (NearbyText, DirectText, VoiceTranscript, Activity)
- Message records with speaker, timestamp, content, and retention metadata

### RelationshipMemory
Private to a single RelationshipId:
- MemoryId
- RelationshipId
- MemoryType (Preference, ImportantFact, SharedExperience, OngoingTopic, Boundary)
- Content
- Confidence
- SourceConversationId / SourceActivityId
- CreatedUtc, LastConfirmedUtc
- Visibility (PrivateToRelationship)
- Expiry / deletion metadata

Do not infer sensitive personal attributes. Memory extraction is conservative, explainable, user-controllable, and supports correction/deletion.

### SharedExperience
A structured record of an activity shared by this user and this character:
- ExperienceId
- RelationshipId
- ActivityType
- World/Location reference
- StartedUtc, EndedUtc
- Summary
- UserVisibility / retention setting

## 4. Runtime architecture

Use an asynchronous boundary:

Viewer / existing Avatar protocol
    -> OpenSim NPC adapter (spawn, appearance, chat, movement, animation)
    -> AI Friend Coordinator (relationship state, permissions, context assembly)
    -> Conversation Orchestrator
         -> IAIModelProvider
         -> IRelationshipMemoryStore
         -> IConversationStore
         -> IWorldActionGateway
    -> validated response / action proposal
    -> NPC adapter executes permitted action on simulator thread

The AI Friend Coordinator and Conversation Orchestrator run outside the simulator's latency-sensitive update loop. The World Action Gateway is the only route for model-proposed actions to affect the world.

## 5. Bounded world-action contract

M1 should define an allow-listed action vocabulary, not arbitrary commands:
- Speak(text, channel)
- PlayAnimation(animationReference)
- SetExpression(expressionId)
- LookAt(targetId, duration)
- MoveTo(validatedWorldPosition)
- FollowUser(userId, enabled)
- StopCurrentAction()
- InviteToActivity(activityId)

Each action requires schema validation, permission checks, rate limits, world/parcel access checks, and a safe failure result. Movement and animation are delegated to existing OpenSim NPC/avatar facilities. The LLM does not calculate frame-by-frame motion.

## 6. Relationship and privacy rules

- The user explicitly initiates/accepts friendship; proximity alone does not create a friendship.
- A character's general persona is shared; relationship memories are never shared between users.
- A user can inspect, correct, pause, export where supported, and delete their relationship memories.
- The user controls proactive greetings, voice, follow behavior, and whether the NPC may enter private parcels or homes.
- The NPC respects block, mute, unfriending, parcel permissions, region boundaries, and owner policies.
- No unsolicited messages when the user is offline unless the user explicitly enables a supported notification channel.
- Do not simulate human presence or imply that the AI has human feelings, consciousness, or guaranteed availability.

## 7. Performance and cost

- Do not call an LLM for every movement tick, animation frame, or nearby chat packet.
- Use deterministic state machines/behavior trees for locomotion, idle behavior, and simple reactions.
- Call the model for conversation, relationship-aware decisions, and occasional high-level action planning.
- Cache persona and static configuration; bound conversation context and memory retrieval.
- Apply per-user, per-character, per-region and provider-level quotas, timeouts, cancellation, and circuit breakers.
- Keep NPC simulation LOD independent from conversational memory. Distant/inactive NPCs do not need continuous AI inference.

## 8. Provider and service interfaces

Initial interfaces to implement after repository audit:
- IAIModelProvider: asynchronous text generation with cancellation, token/time budgets, and structured response support.
- IRelationshipMemoryStore: get, append, update, and delete relationship-scoped memories.
- IConversationStore: append and retrieve bounded conversation history.
- IAICharacterStore: read/version character identity and persona.
- IUserCharacterRelationshipStore: create/read/update relationship state with ownership checks.
- IWorldActionGateway: validate and dispatch allow-listed actions to the existing NPC adapter.

The interfaces must follow the active branch's target framework, async conventions, dependency injection, logging, and persistence patterns. Do not select a new database or model provider before auditing existing conventions.

## 9. Delivery milestones

### M1 — Domain and service foundation
- Audit active branch project layout, target framework, DI/module registration, NPC interfaces, persistence and test conventions.
- Add domain records, relationship state machine, validation, and service interfaces.
- Add unit tests for relationship ownership/isolation, state transitions, validation, and memory partitioning.
- No LLM calls and no changes to Scene.cs.

### M2 — Text friendship vertical slice
- Add one provider adapter behind IAIModelProvider.
- Implement direct text conversation, bounded context assembly, private relationship memory, user controls, timeouts and fallback.
- Test two users talking to the same character and prove memory isolation.

### M3 — Embodied in-world friend
- Bind conversation responses to existing NPC/avatar chat, expressions, animations and movement.
- Add explicit follow/invite/stop actions and parcel/region permission checks.
- Verify that the AI service cannot block simulator update threads.

### M4 — Voice and shared activities
- Integrate speech input/output and live subtitles through existing OpenSim-next voice/STT work.
- Persist opt-in shared experiences and allow the NPC to join supported games/activities.

## 10. M1 acceptance criteria

- A user can meet an AI character and explicitly establish a friendship.
- A user cannot read or modify another user's relationship or memory.
- Two users can be friends with the same character without cross-user memory leakage.
- A relationship can be paused, blocked, or unfriended.
- Invalid state transitions and oversized memory/content are rejected.
- AI/provider failures do not block or crash simulator update loops.
- Existing login, Region, Scene, Avatar, chat, LSL and OSSL behavior remains unchanged.
