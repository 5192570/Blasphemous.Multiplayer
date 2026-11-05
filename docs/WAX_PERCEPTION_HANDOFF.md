# Handoff: WaxCrawler (mudcrawler) does not perceive follower players

**For:** CGPT, continuing while the primary agent is unavailable.
**Branch:** `arena/b57a79f7-blasphemous-multiplayer` → PR #4 (`enemy-sync-handler-refactor`).
**Status at handoff:** presentation sync works; follower perception does not (one fix
attempted and superseded, a second just landed and is **untested**).

## What works (tester-verified)

- Host/follower snapshot sync of position/health for WaxCrawler, Fool, WheelCarrier.
- Mudcrawler emerge + crawl animations mirror on the follower; **submerge now mirrors**
  too (Disappear → hidden at origin), death animation syncs. (Fix: `cd86b14`, adapter
  special-state 0=Hidden/1=Emerged/2=Submerging via `WaxCrawlerEnemySyncHandler`.)
- Follower FPS hitching in enemy-dense scenes is fixed (`69f132b`: spawn-identity
  cache, unchanged-snapshot suppression + 0.5 s heartbeat). Do not reopen the
  capture-on-aggro redesign; the user explicitly froze the sync model.

## The bug

On the scene authority's game, mudcrawlers react **only to the host's own Penitent**.
A follower standing in the mud is ignored: no emerge, no chase. (The follower's copy
is a dormant puppet, correctly waiting for snapshots, so nothing happens on either
screen.) Damage/death from the follower still sync — only perception is missing.

Root cause established earlier: generic target selection already assigns the
nearest player (incl. remote puppets) as `Enemy.Target`
(`EnemySync.UpdateTarget` → `WaxCrawlerEnemySyncHandler.SetTarget`), but the
crawler's wake/chase is gated by `EnemyBehaviour.PlayerSeen`, which vanilla sets
**only** from its visual `CollisionSensor` trigger. Remote-player puppets
(`OtherPlayerScript`) have no sensor-visible collider, so the sensor never trips
for them.

## Attempts so far

1. **`cd86b14` (tested, FAILED):** Harmony prefixes on the `PlayerSeen` property
   getter and `IsPlayerSeen()` (`WaxCrawlerRemotePlayerVisionPatch.cs`) returning
   true when a puppet was inside the sensor collider. Tester: no change.
   Possible reasons: the behaviour tree may not read sight through those two
   accessors, the patch may not have applied, or the overlap test never matched.
   No diagnostics existed to distinguish these.

2. **`88ed1fc` (tested, mechanism worked, assumption falsified):** deleted the
   Harmony patch; the Wax handler drives perception itself on the authority.
   The transition logs fired correctly (`PlayerSeen -> True`,
   `remoteInSensor=True`), yet no emerge — so the field alone is not the gate.

3. **Probe (`1777a73`) + sensor mirroring (current, UNTESTED):** a 1 s
   authority-side probe of all candidate wake inputs, run in both cases,
   gave a clean diff. Failing case (follower in mud): `playerSeen=True`,
   targets = puppet, but `visualColliding=False`, touched=`[<none>]`,
   `hearingColliding=False`, `awake` never true. Working case (host in mud):
   `visualColliding=True`, touched=`[Penitent]`, `hearingColliding=True`,
   `awake=True`, and the tree itself sets `behaviourTarget` to the Penitent's
   `Body` child — it pulls its target from the sensor. Conclusion: the tree
   reads the sensors' live collision bookkeeping. Current fix therefore
   mirrors in-range puppets into the visual and hearing `CollisionSensor`
   private `objectList`s (reflection, adapter-tracked, removed on exit or
   authority loss) and drives `PlayerHeard` like `PlayerSeen`. Note the
   probe's `touched=[...]` reads `entityList`, which can only hold real
   `Entity` objects — it will still show `[<none>]` for puppets after this
   fix; judge by `visualColliding=True` + `awake=True` instead. If it still
   fails with `visualColliding=True`, the remaining suspect is
   `GetTouchedEntities()`/Penitent-identity, which cannot be mirrored with a
   puppet and would need the graph-extraction route or `Rise()` driving.
   Original (now historical) description of attempt 2:
   - `WaxCrawlerEnemySyncHandler.DriveAuthorityPerception()` runs every
     `OnSyncUpdate` while `_isAuthority`. It computes `localSeen` from the
     sensor's own `CollisionSensor.IsColliding()` and `remoteSeen` by testing
     each puppet's feet/body points against the same sensor collider
     (`OverlapPoint`, with a `bounds.Contains` fallback), then writes the result
     into `PlayerSeen`'s backing field (via `AccessTools.PropertySetter`, with a
     `<PlayerSeen>k__BackingField` fallback) — covering every possible reader.
   - `SetTarget` now also assigns `WaxCrawlerBehaviour.Target` (the behaviour's
     own Transform, previously never set by the adapter).
   - Transitions log `[WaxPerceptionDiag] PlayerSeen -> ... (localSensor=...,
     remoteInSensor=...), enemyInstance=..., sensorBounds=...`.

## Test protocol for the fix

Scene `D01Z01S03` (16 mudcrawlers). Host enters first; follower approaches a
hidden crawler **alone** (host far away):

- ✅ Expected: `[WaxPerceptionDiag] PlayerSeen -> True (localSensor=False,
  remoteInSensor=True)` on the host log, crawler emerges and crawls on both games.
- Then follower leaves the mud: `PlayerSeen -> False`, crawler submerges on both.

## If it still fails — decision tree (use the diag log)

- **No `[WaxPerceptionDiag]` lines at all on the host:** the drive never fires —
  check `_isAuthority`/handler attach, or `GetVisualSensor()` throwing/returning
  null (silently swallowed). Add a one-time log there.
- **`remoteInSensor` never becomes True while the follower stands in the mud:**
  geometry problem — compare `sensorBounds` in the log against the puppet's
  position; the sensor may be smaller/offset differently than assumed (it is the
  *detection* area, not the mud patch). Widen the test or use distance to the
  crawler transform vs. sensor bounds extents.
- **`PlayerSeen -> True` logs fire but the crawler still doesn't emerge:** sight
  is no longer the gate. The behaviour tree likely keys off something else —
  prime suspects: distance to `Core.Logic.Penitent` (the *local* player object)
  computed inside tree tasks, or a blackboard target that isn't
  `Entity.Target`/`Behaviour.Target`. Next step: log `Behaviour.Awake`,
  animator state, and `Behaviour.Target` alongside the diag line while the
  follower stands in the mud; if `PlayerSeen=True` + target=puppet + still
  asleep, the tree is reading Penitent directly and the fix becomes driving
  `Rise()` (and suppressing the tree's `Asleep`) from the handler while a
  remote player is in range — treat as a last resort, it fights the tree.
- **Secondary observation (untriaged, probably minor):** when the host loses
  sight (steps out), the follower's crawler plays submerge but its HP label
  keeps moving briefly — consistent with follower interpolation still lerping
  toward the last crawl position while `Disappear` plays, and/or the follower's
  own `Asleep()`→`MoveToOrigin()` racing the snapshot lerp. Re-check after
  perception works; likely cosmetic.

## Constraints (standing user instructions)

- One enemy at a time; adapter + registration-table row is the model. Death
  presentation stays per-adapter. **No sync-model refactor** (no capture-on-aggro,
  no batching) without new evidence.
- Work only on `arena/b57a79f7-blasphemous-multiplayer`; PR #4 targets
  `enemy-sync-handler-refactor`. Document steps on PR #4.
- No .NET toolchain in the Arena sandbox: code is only grammar-parsed there;
  the user builds/tests locally. Ask for build errors if any (first suspects:
  Harmony/reflection names, animator state names).
- Vanilla reference: decompiled sources are fetched per session to
  `/tmp/au/Assemblies-Unity-Games-main/Blasphemous` (does not persist; refetch
  from `EKQRCalamity/Assemblies-Unity-Games` tarball if missing). Key files:
  `Gameplay.GameControllers.Enemies.WaxCrawler.AI/WaxCrawlerBehaviour.cs`,
  `Gameplay.GameControllers.Enemies.Framework.IA/EnemyBehaviour.cs` (PlayerSeen,
  GetVisualSensor, sensor callbacks), `.../WaxCrawlerMeltingBehaviour.cs`.
- Wax traps (do not call; they throw `NotImplementedException`):
  `WaxCrawlerBehaviour.StopMovement()/Damage()/Attack()`,
  `WaxCrawler.EnemyAttack()/EnemyFloorChecker()/EnemyBumper()/EnablePhysics(bool)`.
