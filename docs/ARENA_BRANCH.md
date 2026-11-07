# Arena branch: WheelCarrier execution and enemy-tier probe

**Branch:** `arena/a83f9785-blasphemous-multiplayer`  
**Snapshot:** older investigation branch; not the current integration baseline.

## Intended scope

This branch records the WheelCarrier execution-mirroring work and diagnostic investigation that preceded the enemy-sync handler refactor. Its latest commits refine the enemy-tier probe so it distinguishes prefab variants that share one behaviour type (notably Tutorial and Regular WheelCarriers).

The branch history also includes the execution presentation work: mirroring the owner's execution sequence, clone rendering, executor-avatar visibility, cleanup, and supporting diagnostics.

## Status and use

- The branch's work was subsequently incorporated into the PR #3/mainline history and then continued on later branches.
- Treat this branch as a historical snapshot for reviewing the original execution/probe changes, not as the branch to continue enemy-sync development from.
- For current code, use `main`; for the later WaxCrawler/shared-perception work, see `arena/b57a79f7-blasphemous-multiplayer`.
- The variant-aware tier probe is diagnostic inventory only. It does not itself register or synchronize additional enemies.

## Key files

- `Blasphemous.Multiplayer.Client/ProgressSync/WheelCarrierEnemySyncHandler.cs`
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyTierProbe.cs`
- `docs/WHEELCARRIER_EXECUTION_CHAIN.md`
- `docs/WHEELCARRIER_RIPOSTE_DESYNC.md`
