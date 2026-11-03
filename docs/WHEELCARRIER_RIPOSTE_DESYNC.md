# WheelCarrier riposte desync — diagnosis

Symptom (from the report):

* P2 parries and ripostes the WheelCarrier.
* On P1 the WheelCarrier's animation freezes, no parried/execution pose is ever played.
* P1's WheelCarrier still "sees" P2, but its attacks no longer register a hit.
* P2 can still damage/kill its own copy because it behaves like a purely local enemy.

Short version: **the mod replays the parry/execution by calling the game's own
transition methods on the observing client, but those methods are one-shot and
self-terminating only on the client whose Penitent performed the parry.** One of them
self-cancels after ~1 s, the other never terminates at all, and the authority then
broadcasts whichever of the two it ended up in. On top of that, the riposte *kill*
never leaves the client that performed it.

---

## 1. How the enemy actually works

Everything below is from
[`Gameplay.GameControllers.Enemies.WheelCarrier*`](https://github.com/EKQRCalamity/Assemblies-Unity-Games/tree/main/Blasphemous)
in the disassembled project.

| Step | Native code | What it changes | What ends the state |
|---|---|---|---|
| Parried | `WheelCarrierBehaviour.Parry()` (l.155) | `GotParry = true`, `Animator.Play("Parry")` | **`ParryBehaviour.OnStateUpdate`** (Stunt/ParryBehaviour.cs l.26-33): a **1 second timer** that calls `Alive()` unless `Core.Logic.Penitent.IsOnExecution` |
| Executed | `WheelCarrierBehaviour.Execution()` (l.162) | `GotParry = true`, `SpriteRenderer.enabled = false`, `layer = Default`, `Attack.gameObject.SetActive(false)`, `EntExecution.InstantiateExecution()` | **`StuntBehaviour.OnStateUpdate`** on the *spawned `Execution` prefab*, which calls `Alive()` after `Enemy.StuntTime` — and the prefab only animates when the **local Penitent uses it** (`Execution.OnUpdate` → `Use()` when the player stands in range and presses attack) |
| Alive | `WheelCarrierBehaviour.Alive()` (l.179) | restores layer/sprite/attack, `GotParry = false`, `Animator.Play("Idle")`, **`CurrentLife = Stats.Life.Base / 2f`** | – |

Two consequences matter:

1. `WheelCarrierBehaviour.Update()` (l.51-53) returns immediately when `GotParry` is
   true. `GotParry` is *the* frozen flag: no chase, no attack, no vision check.
2. The kill is not a hit. `Execution.OnUse()` (Tools.Level.Interactables/Execution.cs
   l.150-173) calls `ExecutedEntity.KillEntity()` → `Entity.Kill()` →
   `Entity.Damage()` → `KillInstanteneously()`, and only later
   `Object.Destroy(ExecutedEntity.gameObject)`.

Neither recovery mechanism is a state machine the mod can drive: one is an animator
state with a hard-coded 1 s timer, the other is a *player interaction* with an object
that only exists on one client.

## 2. What the mod does today

`WheelCarrierEnemySyncHandler.ApplyRemoteSpecialState()` (previous revision) mapped the
three states onto those native calls on **every** client:

```
state 1 -> WheelCarrier.Behaviour.Parry()
state 2 -> WheelCarrier.Behaviour.Execution()
state 0 -> WheelCarrier.Behaviour.Alive()   (only when the previous state was 2)
```

and the state itself travels as a single byte:

* owner (whichever player parried/riposted) → `ReportWheelCarrierStateChange()`
* follower → `EnemyStateReport` → server → **only the scene host**
* host → applies it, then republishes it inside every `EnemyPosition` snapshot
  (`EnemySync.Update`, `_handler.SpecialState`)
* host that performs the transition itself → no report, snapshot only

## 3. Where it breaks

### D1 (root cause) — `Parry()` on the observer self-cancels after 1 s

The host applies a follower's parry with `Behaviour.Parry()`, which enters the
`"Parry"` animator state. That state's `ParryBehaviour` fires `Alive()` one second
later, because the check is `!Core.Logic.Penitent.IsOnExecution` — *the host's own
Penitent*, who is doing nothing.

So, roughly 1 s after P2 parries:

* the host's WheelCarrier goes back to `Alive()` — `GotParry = false`, `Idle`, **and
  `CurrentLife = Base / 2`**;
* the mod's `WheelCarrierAliveStateSyncPatch` postfix interprets that as a *player*
  transition and reports `state = 0` to the authority as if the owner had finished;
* the resulting snapshot (`specialState = 0`) reaches P2, whose
  `ApplyRemoteSpecialState(0)` calls `Alive()` on the copy that is **mid-riposte**:
  `EntExecution.enabled = false` (the execution prefab is orphaned), sprite/layer
  restored, `Attack` re-enabled, health halved.

That is the "riposte does not show up / the two screens disagree right after the
riposte" part, and it also corrupts health, because `Alive()` rewrites the value the
authority is broadcasting.

### D2 (root cause) — `Execution()` on the observer is a one-way door

If the `state = 2` report arrives **after** that 1 s window, the host applies
`Execution()` to an already-alive enemy. Now the host has:

* `GotParry = true` → `WheelCarrierBehaviour.Update()` returns every frame → **AI
  frozen permanently** (matches "animation freezes");
* `SpriteRenderer.enabled = false` and `layer = Default` → visually gone and not
  hittable by P1's attacks;
* `Attack.gameObject.SetActive(false)` → even if it does resume AI later, its attack
  animation event has no active colliders, so **the attack animation plays but no hit
  is registered** (matches "still detects P2, but attacks register nothing");
* an `Execution` prefab spawned next to P1 that P1 never asked for and that only P1's
  Penitent could ever consume.

If the report arrives *inside* the 1 s window, D1 still fires afterwards and undoes it
half-way, leaving the "frozen pose + running AI + dead attack" mixture.

On a follower the same call is worse: the follower has no authority over the enemy, so
nothing will ever finish the execution either.

### D3 — the riposte kill is never reported

The finisher bypasses `WheelCarrier.Damage`/`EnemyDamageArea.TakeDamage`, which are the
only places `TryCaptureLocalHit` runs. When `Execution.OnUse` kills the enemy there is
no `EnemyDamageContext`, so `CompleteLocalHit` never runs and nothing is sent.

Result: P2's copy dies and is destroyed locally, while the host keeps a ~1 HP copy that
continues to publish snapshots. `generations[]` never advances, so no death is ever
committed and the two scenes permanently disagree about whether the enemy exists
(matches "P2 can still hit and kill its own WheelCarrier").

### D4 — `WheelCarrier.Damage` clears `GotParry` on every hit

`WheelCarrier.cs` l.115-118: any hit that is not an execution sets
`Behaviour.GotParry = false`. On a client that is only *showing* the parry, one stray
hit produces exactly the reported in-between state: the pose stays parried (sprite,
layer and `Attack` are not restored by `Damage`) but AI resumes. The mod's damage
postfix then also reports `state = 0`, cancelling the owner's parry on the authority.

### D5 — `Alive()` is not a presentation-only call

`WheelCarrierBehaviour.Alive()` sets `CurrentLife = Stats.Life.Base / 2f`. Every time
the mod calls it for a remote state transition, synced health is rewritten. If that
call happens on the authority the value is broadcast to everyone.

## 4. Confirming it from your own logs

The previous revision already logs everything needed. Reproduce once with the
diagnostic collector and grep the collected log. (The two message names
`Native apply begin` and `Applied remote state` only exist in the *pre-fix* build —
this revision replaces them with `Mirroring remote state` / `Mirror expired` /
`Suppressed native parry recovery`, which are listed at the end of this section.)

| Observation | Means |
|---|---|
| `[WheelCarrierStateDiag] Native apply begin ... method=Parry` on the **host**, followed ~1 s later by a `[WheelCarrierStateSync]`/`Alive` transition on the host that the *owner never sent* | D1 |
| `[WheelCarrierStateDiag] Native apply begin ... method=Execution` with no `Alive`/`state=0` afterwards | D2 (one-way door) |
| `[WheelCarrierSnapshotDiag] incomingSpecialState=0, localSpecialStateBefore=2` **on the player who riposted** | D1 cancelling the riposte |
| riposte completes but no `[EnemyDeathSync] Authority committed death` and no
  `[EnemyDamageSync] Reporting resolved hit` near the execution | D3 |
| `[EnemySyncDiag] Authority transition ... result=LOCAL` on P1 while `[WheelCarrierStateDiag] Host report rejected ... reason=generation mismatch` on P1 | D3 cascading into the generation counter |

Handy greps:

```bash
grep -n "WheelCarrierStateDiag\|WheelCarrierStateSync\|WheelCarrierSnapshotDiag" client.log
grep -n "EnemyDeathSync\|EnemyDamageSync" client.log
```

After applying this branch, the same reproduction should instead show, on the client
that is *not* performing the riposte:

```
[WheelCarrierStateSync] Mirroring remote state   ... state=1
[WheelCarrierStateDiag] Suppressed native parry recovery on mirrored state   (repeated)
[WheelCarrierStateSync] Mirroring remote state   ... before=1, state=2
[EnemyDeathSync]      Applied remote death ...            (or Reported unattributed lethal hit -> Authority committed unattributed death)
[WheelCarrierStateDiag] Clearing special state ... reason=remote death
```

and no `Mirror expired without an owner update` unless a report really was lost.

## 5. What this branch changes

The rule the rewrite enforces: **only the client whose Penitent performed the
parry/riposte runs the native transition methods; every other client runs a reversible
presentation mirror and lets the authority end it.**

`ProgressSync/WheelCarrierEnemySyncHandler.cs`

* `ApplyRemoteSpecialState()` no longer calls `Parry()`, `Execution()` or `Alive()`.
  It calls `ApplyMirrorPresentation()`, which sets `GotParry`, stops movement, plays the
  parried pose and (for state 2) disables the attack object — nothing is spawned, and
  nothing is bound to the local Penitent.
* `RestoreAlivePresentation()` is the presentation half of `Alive()` **without** the
  `CurrentLife = Base / 2` rewrite (D5).
* `WheelCarrierRemoteParryRecoveryPatch` suppresses `ParryBehaviour.OnStateUpdate`
  while the state is mirrored, so the native 1 s timer cannot cancel a state the owner
  is still playing (D1).
* Ownership: a native transition (`Parry`/`Execution`/`Alive` postfix) takes ownership
  from a mirror; a mirror never takes over from a fresh local transition
  (`LocalOwnershipGrace`), execution never regresses to parry, and every mirror expires
  after `MirrorTimeout` (8 s) with a distinct log line, so a lost packet can no longer
  wedge an enemy forever.
* `ReassertMirroredSpecialState()` restores the mirror after a local hit cleared
  `GotParry` (D4), and no longer reports a bogus `state = 0`.
* Remote attack replays are ignored while a special state is active.

`ProgressSync/EnemyDeathSyncManager.cs`

* `ReportUnattributedLocalDeath()` + a postfix on `Entity.KillInstanteneously`: deaths
  that no captured hit produced (riposte finishers, environment) are now committed by
  the authority or reported as a lethal damage report by followers (D3).
* Authority death commits are idempotent — the existing generation check in
  `CompleteLocalHit` prevents a double commit.

`ProgressSync/EnemySyncHandler.cs` / `EnemySync.cs`

* New `OnSyncUpdate()` hook, called every frame before any authority decision, used for
  the mirror watchdog.

## 6. Still worth watching after this change

* `SetAuthority(false)` leaves the follower's WheelCarrier AI enabled at the component
  level only by `Behaviour.enabled`; `WheelCarrierBehaviour.Update()` ignores
  `EnableBehaviourOnLoad`, so the freeze relies on `GotParry`/component state. If you
  ever want followers to animate again, that is the place to look.
* The parry window on the owner is 1 s; the mod does not attempt to re-time it, so if
  the `state = 1` report is delayed beyond that the owner's own `Alive()` will send
  `state = 0` and the parry simply ends — correct, but worth a log line if it happens
  often.
* A future cleanup would be to give the special-state channel its own monotonic
  sequence per enemy (instead of reusing `EnemyPosition` revisions), which would make
  "out of order state" impossible by construction rather than by grace windows.
