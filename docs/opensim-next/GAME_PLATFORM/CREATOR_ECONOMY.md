# OpenSim-next Creator Economy — Design Specification

Status: Proposed product and architecture baseline
Branch: `opensim-next-dev`
Scope: Game Platform M1/M2 design; no payment processor or live wallet integration is implied by this document.

## 1. Product principle

Each game creator controls the commercial and reward rules for their own experience. OpenSim-next supplies shared, secure economy infrastructure, transaction records, policy enforcement and creator-facing configuration. The platform must not silently impose one universal price, reward schedule or progression model on every game.

Creator autonomy is bounded by platform-wide safety, consumer-protection, fraud-prevention and applicable legal requirements. The platform may define disclosed service fees, prohibited transaction types, refund handling and technical limits; it must not secretly rewrite creator prices or reward odds.

## 2. Creator-configurable monetization

A creator may choose any combination of the following, and may keep the entire game free:

- Free access.
- Paid entry or paid access to a world, region, event or instance.
- One-time unlocks for maps, chapters, missions, modes or other content.
- Optional consumables and permanent virtual goods.
- Cosmetic goods, clothing, accessories, pets, emotes and effects.
- Memberships or time-limited passes with clearly described benefits.
- Creator-defined bundles and promotional discounts.
- Donations or voluntary support, where supported by the payment provider and applicable policy.

Each offer must have a stable offer ID, display name, description, price, currency, entitlement/grant definition, availability window, purchase limit, refund policy reference and version. A purchase must bind to the offer version shown to the buyer so later creator edits cannot retroactively change an existing transaction.

Creators can change prices and availability prospectively. They cannot revoke already-purchased permanent entitlements except under a disclosed platform safety/refund/legal process.

## 3. Creator-configurable reward systems

Creators can define reward rules for:

- Quests, missions, objectives and story progression.
- Achievements, milestones and skill progression.
- Daily/weekly participation and attendance.
- Match completion, team contribution and ranked results.
- Exploration, discovery, crafting, collection and social activities.
- Seasonal events, creator campaigns and community events.
- Creator-approved promotional codes and grants.

Reward outputs may include game-specific currency, XP, levels, badges, titles, cosmetic items, equipment, pets, access rights, crafting materials and creator-defined entitlements.

Reward rules must support eligibility conditions, cooldowns, per-player and global caps, repeatability, expiration, probability (only where explicitly disclosed), and audit metadata. Random rewards must expose odds before participation or purchase where required by policy or law. Paid random rewards should be disabled by default until a dedicated compliance and disclosure design is approved.

## 4. Separate economy domains

Do not collapse all value into one global balance. Model at least these distinct domains:

1. **External payment** — payment provider transaction in a supported real-world currency.
2. **Platform wallet/credit** — only if the platform later supports it; governed by platform-level rules.
3. **Game currency** — currency scoped to one game/project by default.
4. **Game entitlements** — owned unlocks and rights, not spendable balances.
5. **Creator revenue ledger** — amounts attributable to creator sales, refunds, fees and payable balance.

Game currency is non-transferable between unrelated games by default. Cross-game or persistent-world portability must be an explicit creator opt-in plus platform-approved asset/currency policy. A game must never gain access to a player's external payment credentials or another game's private ledger.

## 5. Creator control and player transparency

Creator Studio should provide a visual Economy panel where creators can:

- Choose free/paid access.
- Create and edit offers, bundles and membership tiers.
- Configure reward triggers, amounts, caps, cooldowns and expiry.
- Preview player-facing price and reward disclosures.
- Review sales, refunds, grants, reward issuance and suspicious activity.
- Export reports and inspect immutable transaction history.
- Pause an offer or reward rule without deleting historical records.

Before purchase or reward claim, the player sees the exact price/currency, contents, duration, renewal terms (if any), restrictions and refund terms. No deceptive UI, hidden recurring charges, misleading scarcity or forced purchase flow.

## 6. Server-authoritative transaction and reward processing

The Viewer and game scripts are untrusted clients. They may request a purchase or reward claim but must never directly set balances, mint currency, mark an item as purchased or declare a payment successful.

Required flow:

1. Server validates player identity, project ownership/configuration, offer version and eligibility.
2. Payment provider confirms payment through a verified server-to-server callback or trusted provider API.
3. Server records an immutable transaction and grants the entitlement exactly once using an idempotency key.
4. Reward engine validates server-observed gameplay facts and rule limits before issuing a reward.
5. Ledger updates and grant records are committed atomically.
6. All administrative adjustments require an authenticated, permission-checked, audited operation.

Never trust client-supplied price, balance, kill count, quest completion, playtime or reward amount. Use idempotency, replay protection, rate limits, fraud monitoring and reconciliation jobs.

## 7. Creator permissions and isolation

- Only project owners and explicitly delegated economy managers may edit offers and reward rules.
- Game scripts may call narrow server APIs to request a validated reward; they cannot execute arbitrary ledger SQL or call payment-provider APIs.
- A game can only inspect its own game-scoped balances and entitlements, subject to player privacy controls.
- Cross-game grants, shared inventories and persistent-world carry-over require explicit allow-lists and policy checks.
- Platform operators need audited, least-privilege support tools; routine access to player balances should not be unrestricted.

## 8. Fees, refunds, payouts and legal boundaries

Creator-set prices and reward rules are distinct from platform service fees, payment-provider fees, taxes, chargebacks and payout schedules. Any platform fee or revenue share must be clearly disclosed before publication and in creator reports; it must not be hidden inside an unexplained balance change.

Real-money payment, stored value, cash-out, creator payouts, subscriptions, taxes, refunds and age-related controls require a dedicated provider, jurisdiction and compliance design before implementation. Do not claim support for cash-out or real-money trading until those flows are actually integrated and approved.

## 9. Suggested data model (additive)

- `GameEconomySettings`: project ID, enabled flags, default game currency, economy policy version.
- `GameCurrencyDefinition`: project ID, currency ID, display metadata, precision, earn/spend rules.
- `GameOffer` and `GameOfferRevision`: offer identity, price, currency, contents, entitlement grants, availability and version.
- `GameRewardRule` and `GameRewardRuleRevision`: trigger, eligibility, reward payload, caps, cooldown, odds disclosure and version.
- `GameWalletAccount`: project ID, player ID, currency ID, current derived balance/version.
- `GameLedgerEntry`: immutable debit/credit/grant/reversal record, source, idempotency key and timestamps.
- `GamePurchaseTransaction`: provider reference, offer revision, amount, currency, state and refund/chargeback linkage.
- `GameEntitlement`: player, project, entitlement, source transaction/reward, state and expiry.
- `CreatorPayoutStatement`: creator, period, gross, refunds, disclosed fees, taxes/adjustments and payable amount.

Balances are derived from or reconciled against immutable ledger entries; do not treat a mutable balance column as the only source of truth.

## 10. Delivery sequence

- **Design now:** agree domain separation, creator autonomy, player disclosure and server-authoritative security.
- **M1:** add economy settings and offer/reward rule schemas to project manifest only after the project manifest versioning contract is stable. No live money movement.
- **M2:** implement free access, game-scoped virtual currency, server-authoritative reward issuance, immutable ledger and creator reports.
- **Later:** integrate payment provider for real-money purchases, refunds and creator payouts after provider and compliance decisions.
- **Later:** consider cross-game entitlements/currency only with explicit creator and player consent plus platform governance.

## 11. Acceptance criteria

- Creator A cannot edit or inspect Creator B's offers, reward rules, wallets or ledger.
- A client cannot forge a purchase, balance, reward, achievement or gameplay result.
- Replayed purchase callbacks and reward requests cannot grant value twice.
- Offer edits do not mutate the historical terms of an already completed purchase.
- Player sees price, contents, duration and recurring terms before purchase.
- Game currency and entitlements are isolated by project unless an explicit portability rule is approved.
- All ledger changes are traceable, reversible through compensating entries, and auditable.
- A creator can publish a fully free game with no monetization enabled.
