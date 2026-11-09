# Debug enemy spawning

## Status and scope

The current implementation is a **single-player debug prototype** on PR [#6](https://github.com/5192570/Blasphemous.Multiplayer/pull/6). It has explicit targets for the ordinary Fool and WheelCarrier. Fool spawning and the core WheelCarrier direct-spawn lifecycle are user/log validated. Multiplayer coordination is not implemented in this phase.

The commands use the installed game's real prefab and FMOD catalog assets. They do not bundle extracted assets, load donor scenes, or parse `sharedassets*.assets` / `resources.assets` directly.

## Commands

| Command | Behavior |
| --- | --- |
| `spawn help` | Lists the available spawn commands. |
| `spawn probe` | Reports loaded enemy-prefab sources and retained cache references without spawning or modifying anything. |
| `spawn fool -relative center` | Spawns a Fool at the world position corresponding to the center of the current game camera view. |
| `spawn fool -scene x y` | Spawns a Fool at explicit scene/world coordinates. Z is set to `0`. |
| `spawn wheelcarrier -relative center` | Spawns a WheelCarrier at the world position corresponding to the center of the current game camera view. |
| `spawn wheelcarrier -scene x y` | Spawns a WheelCarrier at explicit scene/world coordinates. Z is set to `0`. |

Coordinates use invariant numeric format: use `.` as the decimal separator and do not include thousands separators. Invalid, NaN, and infinite coordinates are rejected.

A validated example in `D01Z01S03` is:

```text
spawn fool -scene -560 12
```

The room's authored Fools were logged at approximately `(-553, 11.8)` and `(-555, 11.8)`. The example is slightly left of them on the same upper platform.

Debug spawning requires an active gameplay scene and refuses to run while connected to a multiplayer server. The existing enemy discovery may still attach the mod's normal HP/sync components to the new entity, but disconnected `EnemySync` resolves to local authority. The debug spawner does not attach or coordinate those components itself.

## Implementation decisions

### Exact prefab selection

Each target checks both an exact prefab name and enemy ID rather than selecting the first matching C# component:

| Command | Prefab | Enemy ID |
| --- | --- | --- |
| `fool` | `Fool` | `EV11` |
| `wheelcarrier` | `WheelCarrier` | `EV10` |

This excludes `FoolDemake` (`EV_DMK02`), which uses the same component class as the ordinary Fool. The direct lifecycle and audio wrapper uses the base `Enemy` type and the selected prefab's runtime enemy type; target-specific code only supplies the exact prefab name and enemy ID.

Prefab resolution uses, in order:

1. A previously validated prefab reference, if still alive and still matching the exact prefab/ID.
2. Loaded `PreloadReferenceScriptableAsset.enemyPrefabs` entries.
3. Loaded non-scene `Enemy` prefab assets.
4. An active authored spawn point's selected prefab as a final loaded-asset fallback.

An unavailable explicitly registered prefab produces a clear error. There is no donor-scene or speculative raw-file fallback.

The runtime probe established that the normal Fool and WheelCarrier prefabs were already loaded at MainMenu. During gameplay, `PreloadAsset` contained 71 enemy prefabs with no null entries. Fool and WheelCarrier were available in `D01Z02S01` despite that room having no active enemy spawn points. Cached prefab/catalog references retained their instance IDs across the tested transitions without missing-reference warnings.

### Direct lifecycle wrapper

The spawner directly instantiates the selected prefab, parents it to `Core.Logic.CurrentLevelConfig.transform`, enables its enemy behavior, and applies the current level's `EnemyStatsImporter` before Unity `Start`.

It deliberately does **not**:

- create an `EnemySpawnPoint`;
- assign an authored `SpawningId`;
- add or remove consumed spawners;
- subscribe to spawn-point death/respawn handling; or
- invoke scene respawn methods.

This keeps debug instances outside authored spawn persistence while preserving ordinary scene spawning and respawning. The resulting enemy is scene-local: it dies with its room and does not respawn at Prie Dieu. Its prefab remains cached, so another instance can be spawned after a transition.

Native damage, death, reward, achievement, bestiary, and event effects are not suppressed. Debug progression damage is acceptable for this sandbox; no disposable-save isolation system is part of the implementation.

### Native audio registration

Direct prefab instantiation alone initially produced errors for `FoolFootStep` and `FoolDeath`. The cause was not a missing audio component on the prefab.

The game's scene `AudioLoader` registers enemy audio catalogs only for enemy types referenced by that scene's authored spawn points. `D01Z02S01` has no active spawn points, so its normal loader had no reason to register Fool audio.

Before instantiation, the debug wrapper finds the exact-ID `Enemy` on the selected prefab and uses its runtime class to select the first matching loaded `Core.Audio.EnemiesAudioCatalogs` entry, matching native `AudioLoader` selection. It registers that catalog and its associated catalog through the native, idempotent `Core.Audio.RegisterCatalog` API. It does not substitute sound paths or create replacement audio events. The generic path selected one catalog for each validated Fool and WheelCarrier spawn. Neither target produced missing-catalog errors in the successful WheelCarrier validation log.

## User-validated runtime behavior

| Check | Result |
| --- | --- |
| Read-only probe | Passed at MainMenu, in `D01Z02S01`, and after entering `D01Z01S03`. |
| `spawn fool -relative center` | Passed in `D01Z02S01`; the Fool spawned at `(-517.0, 15.3, 0.0)` and fell onto valid ground. |
| Movement and combat | The log showed movement/footstep attempts, damage to the player, and damage to the Fool. |
| Damage and death | The player killed the debug Fool and the native `ENTITY_DEAD` event fired. |
| Native SFX | Initially missing; after exact Fool catalog registration, the user confirmed that SFX works. Footstep and death catalog lookups were specifically identified in the prior failing log. |
| Prie Dieu | The debug Fool did not respawn. Ordinary Prie Dieu respawn processing still ran. |
| Room transition and repeated spawn | The debug Fool did not survive the transition, as intended, and another could be spawned afterward. |
| Authored spawning | Authored enemies spawned normally on subsequent room loads. |
| `spawn fool -scene x y` | The user confirmed that explicit scene coordinates work, including the documented `D01Z01S03` example. |
| Generic Fool regression | After the WheelCarrier/generalization change, Fool spawned with `enemyType=Fool`, `audioCatalogs=1`, took damage, and fired `ENTITY_DEAD`. |
| WheelCarrier initialization | In `D01Z02S01`, exact cached prefab `36706` spawned with `enemyType=WheelCarrier` and `audioCatalogs=1`; no initialization or missing-catalog errors occurred. |
| WheelCarrier grounding, movement, and combat | It spawned at `(-524.0, 15.3, 0.0)` and later attacked at ground-level Y=11 with changed X positions. Two native attack hooks and parry events were logged. |
| WheelCarrier execution and death | The native `ACT_WheelCarrierExecution(Clone)` interaction started and finished, `ENTITY_DEAD` fired, and the native execution achievement flag progressed. |
| WheelCarrier Prie Dieu | The executed debug WheelCarrier did not respawn; normal Prie Dieu respawn processing ran. |
| WheelCarrier transition and repeated spawn | A second debug WheelCarrier spawned at `(-539.4, 13.2, 0.0)` in `D01Z01S03`. It did not survive the following room transition, while authored enemies spawned normally on subsequent loads. |

The log does not record which placement syntax produced the second WheelCarrier spawn, so a separate `spawn wheelcarrier -scene x y` test is not claimed. Offline attack-sync warnings report that an unresolved debug spawn identity cannot send network attacks; the native local attacks still fired, and these warnings are not local initialization failures. Native achievement progression remains enabled as designed.

These checks establish working Fool and WheelCarrier offline sandbox targets. They are not evidence of multiplayer spawning, every possible enemy sound, every WheelCarrier placement form, or exhaustive terrain/placement safety.

## Placement limitations

Camera center is only a coordinate source. It can be midair or inside terrain; it does not reproduce an authored spawn point's configuration. No arbitrary camera offset, ground snap, or terrain correction is currently applied. In the validated camera-center run, ordinary enemy gravity moved the Fool onto valid ground.

Explicit coordinates have the same limitation. Use known safe positions, such as coordinates near an authored enemy on suitable terrain, and avoid placing the debug instance directly on top of another enemy.

## Files

- `Blasphemous.Multiplayer.Client/SpawnCommand.cs` — console parsing, camera-center conversion, and command help.
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyPrefabCatalog.cs` — exact prefab/ID resolution and validated reference cache.
- `Blasphemous.Multiplayer.Client/ProgressSync/DebugEnemySpawner.cs` — explicit target prefab/ID pairs, offline guard, generic direct lifecycle wrapper, and native audio registration by runtime enemy type.
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyPrefabProbe.cs` — read-only asset/cache diagnostics.

## Next phase

WheelCarrier (`WheelCarrier`, `EV10`) and the post-refactor Fool regression have passed the documented runtime checks. A distinct WheelCarrier `-scene x y` invocation can still be recorded if desired, although both targets share the same coordinate parser.

Add further non-boss enemies one at a time, with exact prefab/ID selection and focused runtime checks. A target being non-boss or using the generic wrapper does not establish that it is self-contained.

Multiplayer coordination remains a separate future design task. Current scene spawn-point identity is not sufficient to make independent local debug instances match across clients.
