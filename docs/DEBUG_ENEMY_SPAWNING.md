# Debug enemy spawning

## Status and scope

The current implementation is a **single-player debug prototype** on PR [#6](https://github.com/5192570/Blasphemous.Multiplayer/pull/6). It supports the ordinary Fool only. WheelCarrier spawning and multiplayer coordination are not implemented in this phase.

The commands use the installed game's real prefab and FMOD catalog assets. They do not bundle extracted assets, load donor scenes, or parse `sharedassets*.assets` / `resources.assets` directly.

## Commands

| Command | Behavior |
| --- | --- |
| `spawn help` | Lists the available spawn commands. |
| `spawn probe` | Reports loaded enemy-prefab sources and retained cache references without spawning or modifying anything. |
| `spawn fool -relative center` | Spawns a Fool at the world position corresponding to the center of the current game camera view. |
| `spawn fool -scene x y` | Spawns a Fool at explicit scene/world coordinates. Z is set to `0`. |

Coordinates use invariant numeric format: use `.` as the decimal separator and do not include thousands separators. Invalid, NaN, and infinite coordinates are rejected.

A validated example in `D01Z01S03` is:

```text
spawn fool -scene -560 12
```

The room's authored Fools were logged at approximately `(-553, 11.8)` and `(-555, 11.8)`. The example is slightly left of them on the same upper platform.

Debug spawning requires an active gameplay scene and refuses to run while connected to a multiplayer server. The existing enemy discovery may still attach the mod's normal HP/sync components to the new entity, but disconnected `EnemySync` resolves to local authority. The debug spawner does not attach or coordinate those components itself.

## Implementation decisions

### Exact prefab selection

`fool` means prefab **`Fool`** with enemy ID **`EV11`**. Selection checks both values and does not select the first C# `Fool` component match. This excludes `FoolDemake` (`EV_DMK02`), which uses the same component class.

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

Before instantiation, the debug wrapper now finds the first loaded `Core.Audio.EnemiesAudioCatalogs` entry whose owner type is exactly `Fool`, matching native `AudioLoader` selection. It registers that catalog and its associated catalog through the native, idempotent `Core.Audio.RegisterCatalog` API. It does not substitute sound paths or create replacement audio events.

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

These checks establish a working Fool-only offline sandbox. They are not evidence of multiplayer spawning, WheelCarrier instantiation, every possible Fool sound, or exhaustive terrain/placement safety.

## Placement limitations

Camera center is only a coordinate source. It can be midair or inside terrain; it does not reproduce an authored spawn point's configuration. No arbitrary camera offset, ground snap, or terrain correction is currently applied. In the validated camera-center run, ordinary enemy gravity moved the Fool onto valid ground.

Explicit coordinates have the same limitation. Use known safe positions, such as coordinates near an authored enemy on suitable terrain, and avoid placing the debug instance directly on top of another enemy.

## Files

- `Blasphemous.Multiplayer.Client/SpawnCommand.cs` — console parsing, camera-center conversion, and command help.
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyPrefabCatalog.cs` — exact prefab/ID resolution and validated reference cache.
- `Blasphemous.Multiplayer.Client/ProgressSync/DebugEnemySpawner.cs` — offline guard, direct lifecycle wrapper, and native Fool audio registration.
- `Blasphemous.Multiplayer.Client/ProgressSync/EnemyPrefabProbe.cs` — read-only asset/cache diagnostics.

## Next phase

The next proposed implementation is WheelCarrier (`WheelCarrier`, `EV10`) using the same narrow offline pattern, followed by its own movement, combat, damage, death, cleanup, audio, transition, and Prie Dieu checks. Do not infer WheelCarrier runtime success from the prefab probe alone.

Multiplayer coordination remains a separate future design task. Current scene spawn-point identity is not sufficient to make independent local debug instances match across clients.
