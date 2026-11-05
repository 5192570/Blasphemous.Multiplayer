# Next session handoff — efficient enemy wiring

## Start here

PR [#4](https://github.com/5192570/Blasphemous.Multiplayer/pull/4) was merged **into `main`** at merge commit `fddc5af74fc0cb9cd9ae530aced94aaa0860b057` (head `6b48648`). The merged code, not the former `enemy-sync-handler-refactor` branch or the old tier proposal, is the baseline. Start the next session from the latest `main` with this handoff available; check the actual branch and repository instructions before working.

**Next focus:** plan how to wire additional ordinary enemy types efficiently, grouping candidates by shared building blocks and prefab variants. The user cannot conveniently reach many rooms because of progression gates. A debug-only multiplayer test spawner is a promising option, but its design and feasibility have **not** been established. Investigate and plan before implementing. Do things one by one and discuss consequential changes with the user.

Suggested opening: “Continue from `docs/HANDOFF_NEXT_SESSION.md`. Read the current `main` code and investigate building-block families and a multiplayer-safe way to access test enemies. Do not assume a new tier framework or mass registration.”

## Completed and validated

- Registration table gates supported enemy types: `Fool`, `WheelCarrier`, and `WaxCrawler`. Discovery attaches an enemy-specific handler plus `EnemySync`; unsupported types are skipped. The existing table is not a capability registry.
- Snapshot processing and diagnostics performance changes were tested in both scene-host directions. Unchanged snapshots use a heartbeat; spawn identities are cached and identity retries run only while unresolved. The user reported no noticeable follower slowdown.
- MudCrawler is a prefab variant using the `WaxCrawler` entity class. `MudCrawler`, `WaxCrawler`, and `SewerCrawler` exports reference the same `WaxCrawlerBT` graph; this does **not** establish that every variant was tested. P1/P2 emergence, repeated cycles and submerging with P1 watching or away were user-validated for MudCrawler. P2 attacking it was previously untested; don't infer a damage test from the presentation verdict.
- `EnemyRemotePerception` shares puppet/sensor plumbing. Wax-specific wake policy still calls `Rise()` in its adapter; WheelCarrier's separate vision patch was not migrated. No protocol or authority changes were needed; protocol version remains 15.
- Crawler offscreen freezing was traced to prefab Animator `CullCompletely`: the authority animator stopped progressing while the host camera was away. `EnemySync` now sets registered enemies' main `Enemy.Animator` to `AlwaysAnimate`, retrying initialization and handling a changed reference. The user tested the generic change and reported Wax, Fool, and WheelCarrier working identically. Offscreen animation cost is a possible future consideration, not a measured regression.
- WheelCarrier's observer finisher previously always appeared on the left. `6b48648` uses vanilla `Execution.FlipAnimation` on the presentation clone with the owner's reported side, even when the clone is reused. The user confirmed P2's execution appears from the correct side on P1. This is presentation only; actual execution sync and mirroring are functional code, not a debug hack.
- Enemy-specific diagnostics remain available but default off via switches in `EnemyDiagnostics.cs`: Wax, WheelCarrier state, WheelCarrier execution/visual watch, Penitent visibility, and tier probe. The user validated quiet logs without loss of functionality. Enemy HP labels are **on by default and should remain on** as a visual aid. Generic sync/death/damage logs are separate.

## Next-phase milestones — proposed, not yet authorized for implementation

1. **Read-only building-block inventory.** Classify ordinary-enemy candidates and variants by actual enemy/behavior classes, movement mechanism (`NPCInputs`, direct transform, physics/path, etc.), animator/controller, sensors/behavior tree, damage/death hooks and obvious `NotImplementedException` traps. Mark evidence source and unknowns; avoid claiming an entire family is validated. Classes alone do not show every prefab or scene configuration.
2. **Investigate test access.** Compare a debug warp to real rooms with a command-driven test spawner in an existing room. The user cannot freely roam/teleport because progression is gated. A dedicated room is optional, not required. Neither design has been chosen.
3. **If the user chooses a spawner, propose a minimal proof of concept first:** one already-supported enemy and an explicit disposable test setup; both clients must have deterministic matching spawn identities, scene/authority context and safe cleanup. Verify prefab discovery/loading for enemies not present in the current scene, initialization, cache and generation/revision behavior, persistence/save isolation, joining and authority handoffs before expanding. A local `Instantiate` is possible but does not automatically create matching multiplayer identities. Existing `EnemySpawnPoint.CreateEnemy()` uses a configured prefab and initializes spawner state; a synthetic spawn point requires careful lifecycle design. Do not claim this already works or that it requires no protocol changes.
4. **Group and prototype wiring by demonstrated shared blocks.** Favor small opt-in helpers and thin adapters where real enemies prove common behavior. An enemy family may share an entity class while its prefab variants differ. Register and validate incrementally rather than mass-registering a group on class names alone. Establish tests for representative variants and then test differences.
5. **Validate movement and HP alongside safe lifecycle behavior.** Today registering a handler enables damage/death transport too; the handler contract includes authority, hit reactions and death presentation. If the user wants a genuine movement/HP-only stage before death handling is ready, first propose the explicit gate/contract change and agree on safe semantics. Do not silently enable incomplete death or disable gameplay. Death **presentation remains per-enemy/per-adapter**, never generalized.

T4 bosses and scripted enemies remain out of scope. No speculative tier hierarchy or capability registry without a demonstrated need. Keep the scene-host, per-spawned-instance tracking model; avoid a broad sync redesign.

## Open issues / boundaries

- **WheelCarrier blink:** user sees an apparent one-frame stagger/blink after death on P1. Enemy death still synchronizes. It is low priority; source object and cause are unknown, and whether the side fix affected it was not reported. Do not assume it was fixed. Investigate separately using the existing opt-in probe or frame-by-frame evidence; do not guess at death lifecycle behavior.
- The user explicitly dropped HP-label drift while Wax submerges; it is a debug visual aid. Do not investigate that drift.
- Wax graph wakes through a Penitent-tagged hearing-sensor `TriggerStay` and calls `LookAtTarget` then `Rise()`. The helper's direct Wax wake policy currently gates on visual sensing; heard-but-not-seen parity is not established. Don't change it speculatively.
- Wax animator state has `Appear → Crawling` and `Crawling → Disappear` on `HIDE`; `Disappear` has no outgoing transition. Follower uses direct `Animator.Play("Disappear", 0, 0f)` rather than `Hide()`; stale-trigger changes alone did not solve the actual offscreen-culling issue.

## Current architecture and files on `main`

- `Blasphemous.Multiplayer.Client/ProgressSync/EnemySyncManager.cs` — registration table and attachment gate.
- `EnemySync.cs` — generic scene authority, identity, snapshots/interpolation, target selection and main animator culling policy.
- `EnemySyncHandler.cs` — adapter contract, including explicit authority, snapshot, hit and death hooks.
- `FoolEnemySyncHandler.cs`, `WheelCarrierEnemySyncHandler.cs`, `WaxCrawlerEnemySyncHandler.cs` — enemy-specific translations and presentation.
- `EnemyRemotePerception.cs` — per-handler remote-puppet sensor tests, sight/hearing field drive, sensor-list mirroring and cleanup.
- `EnemyDeathSyncManager.cs` — spawn identity, generation/revision, damage/death transport; be cautious with lifecycle changes.
- `EnemyDiagnostics.cs`, `EnemyTierProbe.cs`, `WheelCarrierVisualProbe.cs`, `Players/PenitentVisibilityProbe.cs` — opt-in diagnostics. `WheelCarrierVisualProbe.DescribeClip` is also used functionally for presentation cleanup and must not be disabled with the visual logger.
- `docs/WAX_PERCEPTION_HANDOFF.md`, `docs/WHEELCARRIER_EXECUTION_CHAIN.md`, `docs/WHEELCARRIER_RIPOSTE_DESYNC.md` — historical detail; current code and this handoff supersede old proposed architectures.

## Collaboration, testing and tooling

- The user relays messages to/from CGPT in chat; do not use the PR for relay unless asked. User asked to document actions/decisions on the PR for the completed phase; follow current instructions for future documentation. Both reviewers share one account, so do not infer authorship from the GitHub account alone.
- User prefers one step at a time, without overcomplication. Don't label a feature tested unless the user has actually validated the relevant case.
- No .NET compiler was available in the previous sandbox; tree-sitter C# parsing and `git diff --check` are syntax/static checks, **not a build**. The user builds/tests in game. Check current environment rather than assuming.
- Decompiled reference: `EKQRCalamity/Assemblies-Unity-Games` on GitHub, `Blasphemous/` directory. Fetch from GitHub/codeload or `gh api`; `/tmp` does not persist reliably. Verify game API names before coding.
- `[SharedDiagMerged]` entries relay the same events: deduplicate by session/source sequence and segment by session/epoch before counting. Logs may be trimmed and snapshot diagnostics gated; missing lines are not proof of absence.
- Avoid simultaneous edits to the same file (prior parallel edits lost work). Post Markdown PR bodies from a file to prevent shell backtick substitution.
- Do not reintroduce timeout-based mirror auto-recovery, call native `Parry`/`Execution`/`Alive` on a non-owner, patch the `Execution.OnUse` iterator, or trigger every animator declaring `USED`. See the WheelCarrier execution-chain document for why.
