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
this revision replaces them with `Mirror apply (presentation only)` /
`Mirror still unconfirmed` / `Suppressed native parry recovery`, which are listed at
the end of this section. A mirror log line never means the observer performed the
native interaction — the message says so explicitly.)

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
[WheelCarrierStateDiag] Mirror apply (presentation only) ... before=0, state=1, owner=Host (scene authority)
[WheelCarrierStateDiag] Suppressed native parry recovery on mirrored state          (repeated while parried)
[WheelCarrierStateDiag] Mirror apply (presentation only) ... before=1, state=2, owner=Host (scene authority)
[EnemyDeathSync]        Applied remote death ...     (or: Reporting unattributed lethal hit -> Authority committed unattributed death)
[WheelCarrierStateDiag] Clearing special state ... mirrored=True, reason=remote death
```

`Mirror still unconfirmed` is diagnostic only and is expected on the host, whose mirror
has no periodic confirmation; it never changes presentation.

## 5. What this branch changes

The rule the rewrite enforces: **only the client whose Penitent performed the
parry/riposte runs the native transition methods; every other client runs a reversible
presentation mirror, and the mirror is ended only by an authoritative condition.**

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
  from a mirror; a live locally-owned state ignores authority echoes entirely, so the
  authority can no longer cancel a riposte in progress; a local transition that just
  ended keeps a 1 s `LocalOwnershipGrace`; execution never regresses to parry.
* **No mirror is ended by a timer.** A mirror ends on: the owner's `state = 0`, an
  authoritative/remote death, the mirror owner leaving the session, a scene-authority
  change, or losing the connection. Elapsed time only emits a diagnostic.
* `ReassertMirroredSpecialState()` restores the mirror after a local hit cleared
  `GotParry` (D4), and no longer reports a bogus `state = 0`.
* Remote attack replays are ignored while a special state is active.

`ProgressSync/EnemyDeathSyncManager.cs`

* `ReportUnattributedLocalDeath()` + a postfix on `Entity.KillInstanteneously`: the
  riposte finisher (`Execution.OnUse -> KillEntity`) never opens a damage context, so it
  is now committed by the authority or reported as a lethal damage report by followers
  (D3). The hook is gated on `EnemySyncHandler.OwnsUnattributedDeath`, which is only
  true for a WheelCarrier that is being executed.
* Report ids are deterministic per enemy incarnation, so the existing hit-id dedupe
  collapses repeated kills of the same corpse.
* Authority death commits stay idempotent via the existing generation check.

`ProgressSync/EnemySyncHandler.cs` / `EnemySync.cs`

* `OnSyncUpdate()` — per-frame validation hook (mirror owner presence, diagnostics).
* `OnAuthorityContextChanged(reason)` — fired on authority epoch changes; drops
  presentation copied from the previous authority.
* `ApplySpecialState(state, authorityName)` — snapshots now tell the handler whose
  state they carry.

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

## 7. Review follow-up (timeout and death hook)

### 7.1 The timeout was removed

The previous revision restored the alive presentation after 8 s of silence. That was
wrong for the reason raised in review: **elapsed time does not prove the owner
abandoned the execution**, and the host-side mirror is exactly the client that has no
periodic confirmation. It also produced the churn that was described: the host would
expire, publish `specialState = 0`, the follow-up `state = 2` snapshots would be
ignored for the grace window and then re-applied, so the presentation could oscillate
and the authority could briefly tell the owner to stand down.

What replaced it:

| Situation | Recovery |
|---|---|
| Owner finishes and reports `0` | report / snapshot `0` (normal path) |
| Enemy dies | `ApplyDeath` / `ApplyAuthoritativeDamageReaction(dead)` clears the mirror |
| Mirror owner (follower) leaves | Presence is established by the report itself (a routed report can only come from a player the server already announced), so `OnSyncUpdate` can drop the mirror on its first poll after the owner disappears — even if it never observed the owner connected |
| Scene authority handed over / epoch bumped | `EnemySync` → `OnAuthorityContextChanged` |
| Connection lost / client becomes authority | `EnemySync` → `SetAuthority(true)` → mirror dropped |
| Scene change | new enemy instance, new handler |
| None of the above, and no confirmation for 10 s | **diagnostic warning only** |

Reliable transport is the justification for not needing a timer: the client uses
`SimpleTcpClient` (`NetworkManager` field `_client`, `SimpleTCP`), so state reports are
ordered and reliable while the connection is up. A state update is therefore only
"lost" when the session itself breaks, which is covered by the disconnect and
authority-change paths above.

Answers to the specific questions:

1. *What was 8 s meant to recover from?* A hypothetically lost `state = 0` report. With
   TCP plus an explicit death path that case no longer exists as a silent failure.
2. *Can a legitimate execution last longer?* Yes. `Enemy.StuntTime` defaults to 5 s and
   `Execution.OnUse` waits for the interactable's `Consumed` flag, so the visible
   execution can exceed 8 s depending on the prefab and the player's distance. The
   timeout could cut a live interaction.
3. *State-2 snapshot right after expiry?* With the old code that was a real oscillation
   (expire → 1 s grace → re-apply). With no expiry the question disappears.
4. *Can a timeout distinguish a lost update from a long execution?* No — not without an
   authoritative signal, which is exactly what it lacked.
5. *Diagnostic-only instead?* That is what it is now.

### 7.2 Death hook: evidence for the lifecycle assumptions

Call chain from the riposte finisher:

```
Execution.OnUse()                     Tools.Level.Interactables/Execution.cs:150
  -> ExecutedEntity.KillEntity()      :161
     -> Entity.KillEntity()           Gameplay.GameControllers.Entities/Entity.cs:148
        -> Entity.Kill()              :312
           -> Entity.Damage(CurrentLife + Defense)   :270
              -> Stats.Life.Current <= 0
                 -> Entity.KillInstanteneously()    :317
```

`KillInstanteneously()` body (Entity.cs:317-336) in order:

1. `Status.Dead = true;`
2. `Stats.Life.Current = 0f;`
3. `Entity.Death`, `OnDeath`, `OnEntityDeath` events
4. `Core.Events.LaunchEvent(...)` twice

So at the postfix: `Status.Dead` is **guaranteed true** (assigned on the first line).

`activeInHierarchy`: the only subscriber that matters here is
`EnemySpawnPoint.OnDeath += OnEntityDie` (Tools.Level.Layout/EnemySpawnPoint.cs:159),
and `OnEntityDie` (l.192-197) only unregisters events, sets `HasEnemySpawned = false`
and calls `EnemySpawner.AddConsumedSpawner` (which stores a position in a list). It does
not deactivate or destroy the enemy. `WheelCarrierAnimatorInyector.DisposeEnemy()` can
deactivate the object, but it is an animator-event method and cannot run before the
animator advances. The `Destroy` in `Execution.OnUse` happens later in the coroutine
(`while (!Consumed)` first), and Unity defers `Destroy` to end of frame anyway.

Because the check was not load-bearing and could silently break a future flow that
deactivates on death, **the `activeInHierarchy` requirement was removed**; the method
now relies on `Status.Dead` plus the narrowed handler gate. `TryFindSpawnPoint` matches
on `EnemySpawnPoint.SpawnedEnemy == enemy`, which survives `OnEntityDie`, so identity
resolution still works.

### 7.3 Duplicates and remote-induced reports

`ReportUnattributedLocalDeath` bails out when:

| Condition | Prevents |
|---|---|
| `!(entity is Enemy)` | non-enemy entities |
| `!enemy.Status.Dead` | a `KillInstanteneously` that was suppressed by the predicted-death prefix and then resumed |
| `applyingRemoteDeath` / `applyingRemoteDamage` | deaths caused by applying a remote authoritative state (`ApplyNativeRemoteDeath`, `ApplyDamageReportToEnemy`) |
| `!handler.OwnsUnattributedDeath` | every enemy outside a WheelCarrier execution — the Fool and ordinary WheelCarrier deaths keep the previous behaviour |
| `activeLocalHitContext.Enemy == enemy` | deaths already reported by `CompleteLocalHit` |
| generation mismatch | late reports for a previous incarnation |
| not connected / no authority | offline and pre-authority states |

Duplicates for the *same* death: the report id is deterministic
(`unattributed-death:<scene>:<spawn>:<generation>`), so a second `KillEntity` (for
example `Execution.Damage` re-killing the corpse) produces the same key and is dropped
by `processedHitIds` in `ApplyAuthoritativeDamageReport`. If two reports still raced,
the authority's generation bump rejects the second one. The previous random GUID was
**not** sufficient for this, which is why it was replaced.

On the authority side, `CommitAuthorityDeath` bumps the generation, so a repeat call
returns early in `CompleteLocalHit` and in a second `ReportUnattributedLocalDeath`.

### 7.4 Runtime finding: the ownership grace swallowed the report (fixed)

First two-player test of this branch (P1 host, P2 follower) showed the host receiving
P2's `state = 2` report and then logging:

```
[WheelCarrierStateDiag] Ignored stale authority state during local ownership grace ...
    localState=0, requested=2, age=0.000
```

`age=0.000` is the whole story. `ApplyRemoteWheelCarrierStateReport` calls
`EnemySyncManager.Attach(wheelCarrier)` before applying the state, and
`EnemySyncManager.Attach` calls `handler.Attach(enemy)` → `OnAttach()` **unconditionally**,
even when the handler was already attached. `OnAttach` set
`_specialStateChangedAt = Time.unscaledTime`, so the state it was about to apply was
always 0 ms old and always inside the 1 s grace window. That applied to the `state = 1`
report as well, so the host never mirrored the parry at all: the enemy stayed alive, kept
chasing and attacking, and every later transition was swallowed the same way.

Two changes:

1. `OnAttach()` is now idempotent — it returns immediately when the handler is already
   attached to the same enemy instance, and a fresh handler initialises its timestamps to
   `float.NegativeInfinity` so it never starts inside a grace window.
2. The grace was narrowed: it may only ignore an echo of *the state this client just
   left* (`_lastLeftLocalState`). Any other transition — in particular another player's
   parry or execution arriving in the same frame as a local change — is applied. The
   window's original purpose (a snapshot that crossed our own report) is preserved.

`EnemySyncManager.Attach` is intentionally left untouched: making the handler idempotent
fixes the whole class of "re-attach resets runtime state" bugs, including for the Fool
handler and any future handler.
