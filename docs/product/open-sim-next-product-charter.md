# OpenSim-next Product Charter

## Vision

**An Open Virtual World for Living, Creating and Connecting.**

OpenSim-next aims to provide a modern, open platform for persistent virtual life. It should preserve the social and creative qualities that made Second Life meaningful while replacing avoidable technical limitations with an adaptable, accessible architecture.

The objective is not to win a graphics feature checklist. The objective is to make a virtual world where people can establish an identity, build relationships, create and own content, form communities, and want to return over many years.

## Strategic principle

> Do not modernize the technology at the expense of the soul of the virtual world.

A technically impressive viewer is not enough. Long-term value comes from persistent identities, social relationships, user-created places and objects, community culture, and continuity across upgrades.

## Six modernization pillars

### 1. Modern rendering architecture

- Introduce a renderer abstraction independent of protocol and authoritative simulation.
- Evaluate Vulkan and other viable backends through measured proof-of-concept work; do not mandate one graphics API before compatibility and hardware testing.
- Retain a low-cost rendering path and user-selectable quality presets.
- Make graphics quality affect presentation only, never authoritative world rules.
- Migrate incrementally and keep a compatibility path until functional and performance criteria are met.

### 2. Modern avatar system

- Preserve existing avatar identity, appearance, clothing, attachments, and animation compatibility as a migration requirement.
- Introduce modern skeleton, rigging, facial expression, and animation capabilities progressively.
- Keep avatar identity separate from any single mesh, skeleton version, or renderer.
- Provide conversion and fallback paths so legacy users are not stranded by upgrades.

### 3. Modern creation and building system

- Support glTF/GLB and PBR as preferred workflows for new 3D content.
- Provide practical Blender import/export workflows and asset validation.
- Retain support for legacy primitives, mesh, textures, and existing inventory assets.
- Add versioning, conversion, caching, and provenance so assets remain maintainable.
- Make AI-assisted creation produce editable assets rather than opaque, non-editable results.

### 4. Human-centered social system

- Treat communication and relationships as core platform capabilities, not secondary game features.
- Improve chat, voice integration, speech-to-text, translation, events, groups, and community discovery.
- Preserve user control over presence, privacy, blocking, and communication preferences.
- AI may assist communication, but must not impersonate a human participant or silently alter a user's social relationships.

### 5. AI-assisted creation and operations

AI should amplify human creativity and reduce operational friction. Candidate capabilities include:

- Natural-language building plans and editable scene drafts.
- Material, texture, and object variation assistance.
- Script drafting, explanation, and debugging support.
- Region administration, diagnostics, and moderation assistance.
- Translation, accessibility, and discovery assistance.

AI-generated content must be reviewable before publication. Users retain control over publishing, permissions, and asset ownership. AI must not silently modify authoritative world state, user property, or social relationships.

### 6. Cross-platform access

- PC remains the first full-featured 3D experience.
- A mobile companion should initially focus on messaging, friends, notifications, maps, events, and lightweight account or land management.
- Mobile 3D capabilities can expand after the PC platform and shared service contracts are stable.
- Accounts, identity, permissions, inventory references, and social relationships should be consistent across clients.

## Three foundations beneath the six pillars

### A. Persistent identity and relationships

Accounts, avatar identity, friends, groups, communication preferences, and user history should survive viewer upgrades and client changes. Identity must not be coupled to one client implementation.

### B. User assets and creative rights

The platform must preserve asset references, permissions, ownership metadata, backups, and format-version information. Compatibility and migration are first-class requirements, not afterthoughts.

### C. Community trust and governance

A long-lived virtual society needs privacy controls, abuse reporting, blocking, land and group governance, moderation workflows, and transparent administrative powers. AI can assist authorized human operators but should not become an unaccountable authority.

## Product and architecture constraints

1. **Compatibility before replacement.** Preserve OpenSim protocol and content compatibility while new systems are introduced behind adapters.
2. **Centralized first, extensible later.** Start with centrally managed grid identity, land, permissions, and global services. Keep region runtime deployable across multiple instances. Defer user-hosted or federated regions until the central experience is stable.
3. **PC first, mobile companion next.** Do not split early engineering effort across multiple full clients.
4. **Low-end access is a product requirement.** Graphics settings may reduce visual fidelity, but must not exclude users from core social and creative functions.
5. **Modular migration, not a big-bang rewrite.** Each replacement needs tests, performance evidence, and a rollback path.
6. **Human agency.** AI assists users; it does not own their identity, assets, decisions, or relationships.
7. **Open and traceable engineering.** Track upstream ancestry, third-party licenses, asset formats, protocol assumptions, and compatibility decisions.

## Delivery sequence

### Foundation — make the existing world reliable

- Establish reproducible server builds and focused automated tests.
- Audit login, grid, region, asset, inventory, presence, UDP, HTTP capabilities, scripts, and persistence.
- Preserve protocol and legacy content behavior.
- Establish the PC viewer compatibility baseline and module boundaries.
- Keep the initial centralized grid model.

### Modern experience — upgrade without breaking continuity

- Introduce renderer interfaces and measured renderer prototypes.
- Modernize asset pipelines and add glTF/GLB plus PBR support.
- Evolve avatar rigging, animation, and expression support.
- Improve social UX and accessibility.
- Add graphics presets and performance diagnostics.

### Platform evolution — expand creation and access

- Add AI-assisted building, scripting, translation, and operations tools with human review.
- Deliver mobile companion features using shared service APIs.
- Expand community discovery and governance tools.
- Evaluate federation or independently hosted regions only after identity, permissions, assets, and trust mechanisms are mature.

## Success criteria

OpenSim-next should measure more than frame rate and visual quality. Useful indicators include:

- Successful login and region entry across supported client configurations.
- Compatibility of existing avatars, inventory, objects, and scripts.
- Time and effort required for a user to create and publish a place or object.
- Reliability of identity, asset delivery, persistence, and backups.
- Quality and usability of social communication and accessibility tools.
- Performance on low-, mid-, and high-range hardware.
- User retention, community activity, and the continuity of user-created content.

## Long-term design question

Are we building a virtual world that merely looks better than Second Life, or one that is better suited to long-term human life?

OpenSim-next chooses the second goal. Visual modernization matters, but it is a means to a more persistent, accessible, creative, and human-centered virtual society.
