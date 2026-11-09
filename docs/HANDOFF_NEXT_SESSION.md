# Next session handoff — efficient enemy wiring

## Start here

PR [#4](https://github.com/5192570/Blasphemous.Multiplayer/pull/4) was merged **into `main`** at merge commit `fddc5af74fc0cb9cd9ae530aced94aaa0860b057` (head `6b48648`). The merged code, not the former `enemy-sync-handler-refactor` branch or the old tier proposal, is the baseline. Start the next session from the latest `main` with this handoff available; check the actual branch and repository instructions before working.

**Current work:** PR [#6](https://github.com/5192570/Blasphemous.Multiplayer/pull/6) contains a user-validated Fool offline debug spawner, a WheelCarrier target using the new generic lifecycle/audio wrapper, and a read-only prefab probe. WheelCarrier has not yet been runtime-validated. See [`docs/DEBUG_ENEMY_SPAWNING.md`](DEBUG_ENEMY_SPAWNING.md) for commands, implementation constraints, exact test results, and placement limitations. The PR remains open; do not describe it as merged.

**Next focus:** validate WheelCarrier independently and rerun one Fool smoke test after the generic refactor. Then add further non-boss enemies one at a time after user confirmation. Multiplayer coordination is a separate future design task. Proceed one step at a time and discuss consequential changes with the user.

Suggested opening: “Continue from `docs/HANDOFF_NEXT_SESSION.md` and `docs/DEBUG_ENEMY_SPAWNING.md`. Fool offline spawning is user-validated on PR #6; WheelCarrier is implemented through the generic wrapper and awaits runtime validation.”

## Completed and validated

- The Fool-only offline debug spawner supports `spawn fool -relative center` and `spawn fool -scene x y`. The user validated both forms, native gravity/movement/combat/damage/death, working SFX after native catalog registration, room-local cleanup, repeated spawning after transitions, and no debug-enemy Prie Dieu respawn. Authored room spawning continued normally. See the debug-spawning document for the exact evidence and remaining limitations.
- Prefab selection is exact (`Fool` / `EV11`) and excludes `FoolDemake`; real loaded prefabs come from the validated cache/preload catalog/loaded assets. No donor scenes, bundled game assets, or raw SerializedFile loader are involved.
- Registration table gates supported enemy types: `Fool`, `WheelCarrier`, and `WaxCrawler`. Discovery attaches an enemy-specific handler plus `EnemySync`; unsupported types are skipped. The existing table is not a capability registry.
- Snapshot processing and diagnostics performance changes were tested in both scene-host directions. Unchanged snapshots use a heartbeat; spawn identities are cached and identity retries run only while unresolved. The user reported no noticeable follower slowdown.
- MudCrawler is a prefab variant using the `WaxCrawler` entity class. `MudCrawler`, `WaxCrawler`, and `SewerCrawler` exports reference the same `WaxCrawlerBT` graph; this does **not** establish that every variant was tested. P1/P2 emergence, repeated cycles and submerging with P1 watching or away were user-validated for MudCrawler. P2 attacking it was previously untested; don't infer a damage test from the presentation verdict.
- `EnemyRemotePerception` shares puppet/sensor plumbing. Wax-specific wake policy still calls `Rise()` in its adapter; WheelCarrier's separate vision patch was not migrated. No protocol or authority changes were needed; protocol version remains 15.
- Crawler offscreen freezing was traced to prefab Animator `CullCompletely`: the authority animator stopped progressing while the host camera was away. `EnemySync` now sets registered enemies' main `Enemy.Animator` to `AlwaysAnimate`, retrying initialization and handling a changed reference. The user tested the generic change and reported Wax, Fool, and WheelCarrier working identically. Offscreen animation cost is a possible future consideration, not a measured regression.
- WheelCarrier's observer finisher previously always appeared on the left. `6b48648` uses vanilla `Execution.FlipAnimation` on the presentation clone with the owner's reported side, even when the clone is reused. The user confirmed P2's execution appears from the correct side on P1. This is presentation only; actual execution sync and mirroring are functional code, not a debug hack.
- Enemy-specific diagnostics remain available but default off via switches in `EnemyDiagnostics.cs`: Wax, WheelCarrier state, WheelCarrier execution/visual watch, Penitent visibility, and tier probe. The user validated quiet logs without loss of functionality. Enemy HP labels are **on by default and should remain on** as a visual aid. Generic sync/death/damage logs are separate.

## Next-phase milestones — proposed, not yet authorized for implementation

1. **Validate WheelCarrier and the generic wrapper.** Test `WheelCarrier` / `EV10` placement, movement/combat, execution, damage/death, audio, cleanup, transitions, Prie Dieu, and ordinary authored spawning independently. Rerun one Fool smoke test after the refactor. A loaded prefab and default `canWalk=true` do not prove full runtime behavior or authored per-spawn configuration. After validation, add further non-boss enemies one at a time with exact prefab/ID selection and focused checks; do not create a broad enemy-registration framework.
2. **Multiplayer follow-up.** Resolve camera-relative placement on the typing client and send the resulting world position, enemy choice and scene to the scene host, rather than converting separately using each camera. Investigate host-authorized coordination and matching identities on both clients. Current sync relies on scene spawn-point identity: matching positions or local `Instantiate` alone are insufficient. A targeted identity or protocol change may be necessary; don't assume either approach is already proven. Start with both players present in the same scene and one Fool, then explicitly address cleanup, scene transitions, late joins, authority handoffs, cache and generation/revision behavior before expanding. Test movement, HP, attacks and death on both sides.
3. **Read-only building-block inventory and incremental wiring.** Classify ordinary-enemy candidates and variants by actual enemy/behavior classes, movement mechanism (`NPCInputs`, direct transform, physics/path, etc.), animator/controller, sensors/behavior tree, damage/death hooks and prefab/scene dependencies. Distinguish evidence from hypotheses and note unknowns. Favor small opt-in helpers and thin adapters where real enemies prove common behavior; test representative variants rather than mass-registering by class name. The user can research environmental edge cases and place those enemies where needed.
4. **Validate movement and HP alongside safe lifecycle behavior.** Today registering a handler enables damage/death transport too; the handler contract includes authority, hit reactions and death presentation. If the user wants a genuine movement/HP-only stage before death handling is ready, first propose the explicit gate/contract change and agree on safe semantics. Do not silently enable incomplete death or disable gameplay. Death **presentation remains per-enemy/per-adapter**, never generalized.

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

PR #6 additionally contains `SpawnCommand.cs`, `ProgressSync/EnemyPrefabCatalog.cs`, `ProgressSync/DebugEnemySpawner.cs`, and `ProgressSync/EnemyPrefabProbe.cs`. These debug-spawning files are not yet on `main`; see the debug-spawning document rather than treating the historical file list above as a description of PR #6.

## Collaboration, testing and tooling

- The user relays messages to/from CGPT in chat; do not use the PR for relay unless asked. User asked to document actions/decisions on the PR for the completed phase; follow current instructions for future documentation. Both reviewers share one account, so do not infer authorship from the GitHub account alone.
- User prefers one step at a time, without overcomplication. Don't label a feature tested unless the user has actually validated the relevant case.
- No .NET compiler was available in the previous sandbox; tree-sitter C# parsing and `git diff --check` are syntax/static checks, **not a build**. The user builds/tests in game. Check current environment rather than assuming.
- Decompiled reference: `EKQRCalamity/Assemblies-Unity-Games` on GitHub, `Blasphemous/` directory. Fetch from GitHub/codeload or `gh api`; `/tmp` does not persist reliably. Verify game API names before coding.
- The user may create a separate private GitHub repository for extracted game prefab references, accessible through their connected account. It has not yet been created or provided; ask for its location when ready. Keep extracted game assets out of the public multiplayer repository. Text exports are useful for inspection but are not necessarily runtime-loadable Unity prefabs; prefer retrieving actual prefabs from the installed game.
- A search of The Cutting Room Floor found no documented original Blasphemous debug room; this is not proof that none exists. `BrandenEK/Blasphemous.DebugMod` documents scene/position display, hitbox viewer, noclip and free camera, but no spawner in its README; multiplayer compatibility and save effects are unverified.
- `[SharedDiagMerged]` entries relay the same events: deduplicate by session/source sequence and segment by session/epoch before counting. Logs may be trimmed and snapshot diagnostics gated; missing lines are not proof of absence.
- Avoid simultaneous edits to the same file (prior parallel edits lost work). Post Markdown PR bodies from a file to prevent shell backtick substitution.
- Do not reintroduce timeout-based mirror auto-recovery, call native `Parry`/`Execution`/`Alive` on a non-owner, patch the `Execution.OnUse` iterator, or trigger every animator declaring `USED`. See the WheelCarrier execution-chain document for why.
