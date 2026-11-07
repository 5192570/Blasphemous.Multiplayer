# Arena branch: enemy sync handlers and WaxCrawler perception

**Branch:** `arena/b57a79f7-blasphemous-multiplayer`  
**Snapshot:** completed enemy-sync integration branch; its latest code was merged into `main` through PR #4.

## Intended scope

This branch contains the enemy-sync implementation and follow-up fixes for the supported Fool, WheelCarrier, and WaxCrawler/MudCrawler enemies. The work includes shared remote-player perception plumbing extracted from the WaxCrawler adapter, WaxCrawler follower presentation/perception fixes, snapshot-processing performance improvements, and WheelCarrier execution presentation orientation.

The branch also contains the P1/P2 diagnostic logs uploaded during the WaxCrawler/offscreen-animation investigation.

## Status and use

- The completed implementation is in `main`; do not treat this branch as a separate unfinished feature line.
- Keep this branch as a historical review point for the PR #4 work and its diagnostic evidence.
- Continue new development from current `main`, not from this hash-named branch.
- The uploaded logs are evidence for the recorded test runs, not proof of any behavior beyond what was explicitly tested.

## Key files

- `Blasphemous.Multiplayer.Client/ProgressSync/EnemySync.cs`
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyRemotePerception.cs`
- `Blasphemous.Multiplayer.Client/ProgressSync/WaxCrawlerEnemySyncHandler.cs`
- `Blasphemous.Multiplayer.Client/ProgressSync/WheelCarrierEnemySyncHandler.cs`
- `docs/P1afterprefabLogOutput.log`
- `docs/P2afterprefabLogOutput.log`
- `docs/HANDOFF_NEXT_SESSION.md`
