# WheelCarrier: complete call surface and the execution chain

Source of truth: the shipped assemblies (`Gameplay.GameControllers.Enemies.WheelCarrier*`,
`Tools.Level`, `Gameplay.GameControllers.Combat`, `Gameplay.GameControllers.AnimationBehaviours.*`).
Line numbers refer to the decompiled files.

This document answers four questions that the current PR cannot answer from memory:

1. which WheelCarrier functions exist and which of them a non-owning client may run,
2. the exact call chain `riposte → executionable state → prompt → E/Y → execution → death`,
3. whether the WheelCarrier has an execution animation of its own (it does not),
4. what minimum context a remote observer needs to reproduce the presentation.

Two-player status this is written against: host-side report application is fixed (`c8cb926`),
so the remaining gap is presentation only — P1 keeps the parry pose during P2's
executionable state, and plays the ordinary death animation when P2 finishes the execution.

---

## 1. The three animator-driven state exits (read this first)

Every "stuck enemy" bug in this file class comes from one of these three timers, and each of
them is a *native* recovery path that a mirror does not have and must not inherit.

| Exit | Owner | When | Effect |
| --- | --- | --- | --- |
| `ParryBehaviour.OnStateUpdate` (`AnimationBehaviours.Enemies.Stunt/ParryBehaviour.cs:26`) | the enemy's own `Parry` animator state | `RemainTimeParried` = **1 s** after `Parry()`, unless `Core.Logic.Penitent.IsOnExecution` | `EnemyBehaviour.Alive()` |
| `StuntBehaviour.OnStateUpdate` (`AnimationBehaviours.Enemies.Stunt/StuntBehaviour.cs:26`) | the **execution prefab** animator | `Enemy.StuntTime` = **5 s** (`Entities/Enemy.cs:68`) after the prefab spawns | `EnemyBehaviour.Alive()`, then `Destroy(prefab)` |
| `Execution.OnUse` coroutine (`Tools.Level.Interactables/Execution.cs:150`) | the execution prefab, on the owner's E/Y | when the clip's `INTERACTION_END` animation event sets `Consumed` | `Destroy(enemy)`, `Destroy(prefab)`, restore the owner's Penitent |

So the executionable state (our `state = 2`) is **not** open-ended: on the owner it expires
after `StuntTime` (5 s, per-enemy serialized field, `Enemy.cs:68`) and the enemy silently
returns to `Alive()` with `CurrentLife = Life.Base / 2` (`WheelCarrierBehaviour.cs:179-190`).
That is what "until P2's executionable state ends" in the test report actually is.

---

## 2. Function inventory

`W` = `WheelCarrier`, `B` = `WheelCarrierBehaviour`, `I` = `WheelCarrierAnimatorInyector`,
`A` = `WheelCarrierAttack`/`WheelCarrierWeapon`, `E` = `EntityExecution`, `X` = `Execution` prefab.

### 2.1 Entry points that start a state

| Function | Where | Triggered by | Already synced? |
| --- | --- | --- | --- |
| `WheelCarrier.Parry()` → `B.Parry()` | `WheelCarrier.cs:123` | `PenitentSword.SuccessParryChance()` (`Penitent.Attack/PenitentSword.cs:264`) when the player's blade meets a parriable enemy hit | yes — postfix → `state = 1` |
| `WheelCarrier.Damage(Hit)` → `Enemy.Execution(hit)` → `W.GetStun()` → `B.Execution()` | `WheelCarrier.cs:100,129`; `Entities/Enemy.cs:162` | the riposte attack inside the riposte window (`Penitent.ActiveRiposte.IsTriggeredRiposte`), or `DamageType.Stunt` / `OptionalStunt` hits | yes — postfix → `state = 2` |
| `B.Alive()` | `WheelCarrierBehaviour.cs:179` | the two timers above, or `StuntBehaviour` | yes — postfix → `state = 0`, and suppressed on mirrors (`WheelCarrierRemoteParryRecoveryPatch`) |
| `X.OnUse()` (E/Y) | `Execution.cs:150` | `Interactable.OnUpdate` → `Use()` when the local Penitent presses the interaction button in range | **no** — only the death it causes is reported |

### 2.2 What `B.Execution()` actually does (the whole of it)

```csharp
base.Execution();                                    // EnemyBehaviour.cs:339 → Entity.IsStunt = true
isExecuted = true;
GotParry = true;                                     // AI freeze + IS_PARRIED stays true
StopMovement();                                      // WALK = false, HorizontalInput = 0
gameObject.layer = LayerMask.NameToLayer("Default"); // sword no longer hits it
SpriteRenderer.enabled = false;                      // ← enemy is NOT drawn after this point
Core.Logic.Penitent.Audio.PlaySimpleHitToEnemy();     // owner-local audio
EntExecution.InstantiateExecution();                 // spawns the Execution prefab
Attack.gameObject.SetActive(false);
EntExecution.enabled = true;
```

There is **no animator call and no execution clip on the WheelCarrier**. `AnimatorInyector.OnUpdate`
(`WheelCarrierAnimatorInyector.cs:16`) then keeps the enemy's `IS_PARRIED` bool true, but the
renderer is off, so nothing of the enemy is visible while the state lasts.

Consequences for the mirror, which currently diverge:

* P1 shows the parry pose — the owner shows **no enemy at all** plus the prefab.
* ~~P1 keeps `layer = Enemy`, so P1's own sword can still hit the executionable enemy and roll
  `WheelCarrier.Damage` → `Enemy.Execution(hit)` locally.~~ Closed: `ApplyExecutionParity()`
  mirrors the `Default` layer and `IsStunt` for state 2, and `RestoreAlivePresentation` undoes
  both. P1 now matches the owner's collision behaviour, while staying visible (the sprite is
  still shown on purpose; only the prefab work would change that).
* P1 never disables `SpriteRenderer`; if we ever do, we must re-enable it in
  `RestoreAlivePresentation` (it already does).
* `base.Execution()` (`EnemyBehaviour.cs:339`) sets `Entity.IsStunt = true`, and `base.Alive()`
  clears it. `IsStunt` gates `ContactDamage` (`Entities/ContactDamage.cs:59`) and
  `EntityDisplacement` (`Entities/EntityDisplacement.cs:69`), so before this was mirrored a
  parried/executing enemy still dealt contact damage and could still be shoved on the observer.
  Closed for state 2 by `ApplyExecutionParity()`; state 1 deliberately keeps the native parry
  behaviour (`EnemyBehaviour.Parry()` never sets the flag, so the flag stays false).

### 2.3 Locomotion, AI and cosmetics (owning client only)

| Function | Note |
| --- | --- |
| `B.Update()` (`:51`) | the whole AI: chase / attack / idle. Early-returns on `IsDead() \|\| GotParry`, which is why the mirror sets `GotParry` |
| `B.Chase`, `B.Idle`, `B.StopMovement`, `B.LookAtTarget` | driven by `B.Update`; the mirror only calls `StopMovement` |
| `B.Attack()` (`:144`) | cooldown + `I.Attack()`; synced as an event via `WheelCarrierAttackSyncPatch` |
| `I.AttackAnimationEvent()` (`:81`) | animator event → `A.CurrentWeaponAttack()` → the actual hostile hit |
| `A.CurrentWeaponAttack`, `WheelCarrierWeapon.Attack` | hit generation; owner only |
| `B.Death()` (`:126`) | `StopMovement` + `I.Death()` (`DEATH` trigger) — the *ordinary* death presentation |
| `I.PlayDeathAnimation()` / `Audio.PlayDeath()` | death audio, played by the death clip |
| `I.DisposeEnemy()` (`:71`) | `gameObject.SetActive(false)` — an **animation event on the death clip**; anything that replays that clip also hides the object |
| `B.StartVulnerablePeriod` / `I.SetVulnerableTrue` | `VulnerablePeriodTrait` toggles `Enemy.IsVulnerable` for N seconds; cosmetic-leaning, not synced |
| `B.ResetCoolDown` / `I.ResetCoolDownAttack` | attack cooldown reset (animator event) |
| `B.ReadSpawnerConfig`, `B.Wander`, `B.Chase(Transform)` | spawn config and `NotImplementedException` stubs |
| `WheelCarrierAttackBehaviour` (`AnimationBehaviours.Enemies.CrossCrawler/…:1`) | sets `IsAttacking` on the attack state; `LookAtTarget` on exit |
| `Audio.PlayStepLeft/Right`, `PlayAttack`, `StopAttack` | cosmetics |

### 2.4 `EntityExecution` (`Gameplay.GameControllers.Combat/EntityExecution.cs`)

| Function | Note |
| --- | --- |
| `InstantiateExecution()` (`:35`) | `Core.Audio.PlaySfx(ExecutionAwareness)`, `Instantiate(ExecutionPrefab)` at the enemy's position, sets `Execution.ExecutedEntity = owner`, flips the `Interactable`-tagged sprite towards the *local* Penitent |
| `DestroyExecution()` + coroutine (`:46`) | after `SafeTimeThreshold + 0.1`, applies a 100-damage `Hit` to the prefab's `Execution` — the **interrupt** path |

`ExecutionPrefab` is a **public field** and `WheelCarrier.EntExecution` a public property, so an
observer can reach the prefab without instantiating the interaction. This is the only supported
hook the game offers for a faithful remote presentation.

---

## 3. The execution chain, step by step

```
P2 (owner)                                    P1 (observer, today)
──────────────────────────────────────────────  ────────────────────────────────
PenitentSword.SuccessParryChance
  └─ Enemy.Parry()            → report 1
       └─ B.Parry(): GotParry = true,
          Animator.Play("Parry")                  mirror: GotParry = true, Play("Parry")
          ParryBehaviour: 1 s timer → Alive()      suppressed by patch
          (skipped while Penitent.IsOnExecution)

riposte attack (Attack button)
  └─ W.Damage(hit)
       └─ Enemy.Execution(hit) == true   → report 2
            └─ W.GetStun(hit) → B.Execution()
                 GotParry = true
                 layer = Default
                 SpriteRenderer.enabled = false      mirror: keeps sprite + Enemy layer
                 Attack.SetActive(false)             mirror: does this
                 EntExecution.InstantiateExecution()
                   → Execution prefab + prompt       mirror: nothing
                 StuntBehaviour: 5 s timer
                   → Alive() + Destroy(prefab)       mirror: none (authority state 0 does it)

E/Y  (a *separate* later input)
  └─ Execution.OnUpdate → Use()
       └─ OnUse():
            Penitent.HSpeed = 0, SetBlocker("EXECUTION")
            FlipAnimation(playerDirection)
            interactableAnimator.SetActive(false)
            ShowPlayer(false)      → P2's own sprite + DamageArea off, physics off
            ExecutedEntity.KillEntity()      ← DEATH HAPPENS HERE, BEFORE THE ANIMATION
            interactorAnimator.SetTrigger("USED")   ← the execution clip starts
            while (!Consumed) yield          (INTERACTION_END event ends it)
            Penitent.position = rootMotion point
            Destroy(enemy.gameObject); Destroy(prefab)
            ShowPlayer(true); SetBlocker("EXECUTION", false)
                                                 mirror: receives the death
                                                    → Behaviour.Death()  ← ordinary DEATH clip
```

Two properties of that chain matter for synchronisation more than anything else:

* **the kill precedes the animation.** `KillEntity()` (`Entity.cs:148` → `Kill()` `:312` →
  `KillInstanteneously()` `:317`) runs *before* `SetTrigger("USED")`. A received death therefore
  means "the execution is starting", not "the execution is over". The animator trigger is what
  the observer is missing, and the death message it already receives is a valid *trigger point*
  for it.
* **the enemy never plays an animation of its own during an execution.** `KillInstanteneously`
  plays nothing, and the object is destroyed at the end of the clip. The `DEATH` trigger the
  mirror plays (`Behaviour.Death()` → `I.Death()`) is the *interrupted*-execution presentation
  (`Execution.Damage()` `:279` restores the sprite and plays `DEATH`), not the completed one.

---

## 4. Answers to the four open questions

**1. Which Penitent method starts the executing player's animation?** None. The Penitent's
renderer is *hidden* (`Interactable.ShowPlayer` `Tools.Level/Interactable.cs:492` →
`Penitent.SpriteRenderer.enabled = false`, `Penitent.DamageArea.enabled = false`, plus
`Penitent.Physics.EnablePhysics(false)` in `Execution.ShowPlayer`). The animation belongs to the
prefab: `interactorAnimator.SetTrigger("USED")` (`Execution.cs:150`).

**2. Does the WheelCarrier have a distinct execution animation?** No. It has a *hidden* sprite
and a disabled attack object. The "executionable" visual is entirely the prefab
(`EntExecution.ExecutionPrefab`), and the only enemy-side signal is `IS_PARRIED`.

**3. Which events/coroutines coordinate it?**

| Event | Source | Receiver | Effect |
| --- | --- | --- | --- |
| `USED` trigger | `Execution.OnUse` | `interactorAnimator` | starts the execution clip |
| `INTERACTION_START` | clip event → `AnimatorEvent.LaunchEvent` | `Interactable.OnEventLaunched` `:390` | `BeingUsed = true`, `SetBlocker("INTERACTABLE")` |
| `INTERACTION_END` | clip event | same | `BeingUsed = false`, `InteractionEnd()` → `Consumed = true` → coroutine finishes; `AddFervourBonus()` on the local Penitent |
| `DoSlowMotion` / `StopSlowMotion` / `ZoomIn` / `ZoomOut` / `CamShake` / `Rumble` / sound events | clip events | `ExecutionAnimationEvents` | level `TimeScale`, `ProCamera2D` zoom/pan, camera shake, FMOD, controller rumble |

**4. What minimum context does a remote observer need?** The prefab instance itself (it carries
the enemy-side visual *and* the executor visual), its world position (the enemy's), and the side
to face — `Execution.FlipAnimation(base.PlayerDirection)` `:255` and
`EntityExecution.InstantiateExecution` both derive it from the *local* Penitent's position, which
on an observer is the wrong player. Everything else (`ExecutedEntity`) exists only to feed
`DamageArea.SetOwner`, `TriggerAwareness`, `StuntBehaviour` and `OnUse`.

---

## 5. Why the three obvious remote implementations are wrong

1. **"Replay the animation on the Penitent."** There is no Penitent animation to replay; the
   Penitent is hidden and a prefab-owned duplicate is animated. Playing anything on the
   observer's Penitent animates the *wrong character*, and the observer's Penitent is not even in
   the owner's execution position.

2. **"Call `WheelCarrierBehaviour.Execution()` on the observer."** That is the pre-`4ff5a45` bug
   with extra steps: it spawns a second `Execution` interactable bound to the *observer's*
   Penitent, hides the observer's sprite, disables its attack, flips its layer, and hands the
   enemy's recovery to an interaction the observer never asked for. `StuntBehaviour` would then
   expire the state on the observer's clock (5 s), not the owner's.

3. **"Instantiate the prefab and call `Use()`/`OnUse()` on the observer."** The prefab's `OnUse`
   is not presentation code; it is the finishing move. It would:
   * hide the observer's own Penitent and disable its damage area and physics (`ShowPlayer`),
   * block the observer's input for the whole clip (`SetBlocker("EXECUTION")`),
   * teleport the observer's Penitent every frame (`PlayerReposition`, `OnUpdate`),
   * call `ExecutedEntity.KillEntity()` — a **second death commit** on a mirror that may still be
     inside `state = 2` (which is exactly our `OwnsUnattributedDeath` predicate, so the observer
     would report a duplicate death),
   * grant the observer fervour (`AddFervourBonus`), move its camera (`CameraZoomIn`), change its
     level timescale (`DoSlowmotion` → `TimeScale = 0.25`) and rumble its controller.

So the assumption "replay the player animation, or call the native execution remotely" is
falsified by the source, not by taste. Any faithful observer path has to *subtract* from the
prefab: keep the animator, drop the interaction and the player coupling.

---

## 6. Smallest presentation-only path (implemented)

The prefab is self-contained enough to be replayed if we disable exactly the parts that reach
for the local Penitent. Sketch:

1. **On entering `state = 2`** (report from the owner):
   * instantiate `EntExecution.ExecutionPrefab` at the enemy's position, oriented by the
     owner's side (needs to travel with the state/execution event),
   * set `Execution.ExecutedEntity` — `StuntBehaviour.OnStateEnter` dereferences it, so leaving it
     null throws every frame,
   * neutralise the interaction *before* `Start` runs: keep the object inactive for the frame it
     is spawned in (`Start` is deferred, `Awake` is not), then drop the `Execution` and
     `ExecutionAnimationEvents` components or gate them behind a marker component,
   * hide the enemy sprite / disable the attack object / switch to the `Default` layer to match
     `B.Execution()`, but keep the enemy object alive — it still owns the authority state and
     the death report,
   * suppress `StuntBehaviour` expiry on the clone (it calls `Alive()` on the real enemy and
     destroys the prefab), because the authority already sends `state = 0` if the owner never
     executes.
2. **On execution start** (new event, or the received death while mirroring `state = 2`):
   `interactorAnimator.SetTrigger("USED")`. Nothing else.
3. **On the authoritative death**: destroy the clone. No `Behaviour.Death()`, no death audio —
   the owner plays neither for a completed execution.

Things that must be verified against a real prefab before writing this (assets are not in the
decompilation):

* does `ExecutionPrefab` draw the executed enemy itself, or does it expect
  `ExecutedEntity.SpriteRenderer` to stay on? (`Execution.Damage` re-enables it, which hints the
  prefab does *not* own the enemy visual — in that case the sprite should stay enabled on the
  observer and only the interaction is dropped);
* is `AnimatorEvent` on the `interactorAnimator` object, and does disabling the component stop
  `LaunchEvent`? (Animator events call methods regardless of `enabled`, so a Harmony prefix on
  `Interactable.OnEventLaunched` keyed on a clone marker is the safe answer);
* how long the clip runs, so the clone can be cleaned up if a death never arrives.

---

## 7. Protocol shape this implies

| Signal | Needed for | Status |
| --- | --- | --- |
| `state` 0/1/2 + owner | parry pose, executionable state, state end | exists |
| authoritative death | removal of the enemy | exists |
| `EnemyExecutionStarted` (scene/spawn/generation/epoch + side) | starting the clip on the observer at the same moment as the owner, and *not* playing `DEATH` | added (`NetworkType.EnemyExecutionStarted = 25`) |
| owner disconnect / authority change / scene change | dropping the clone with the mirror | exists |

The execution-start event is deliberately separate from the death report: it is *presentation*,
while the death stays the authoritative commit, and the two must be idempotent independently
(a disconnect between them must leave neither a live clone nor a double kill). Deriving the start
from "death while mirroring state 2" works for the common case but cannot distinguish a completed
execution from an interrupted one (`Execution.Damage()` also kills, and correctly plays `DEATH`).

### 6.1 What the implementation does

Owner side: `WheelCarrierExecutionStartPatch` (prefix on `Interactable.Use`) reports the moment the
prompt is consumed. `Use()` is patched instead of `Execution.OnUse` because `OnUse` is an iterator -
Harmony patches its `MoveNext`, so a prefix there would fire on every `yield` of the
`while (!Consumed)` loop. `Use()` runs once, on the frame of the button press, which is also a frame
before the owner's `KillEntity` packet goes out. The payload carries the side the executing player
stands on (`Execution.PlayerDirection`), because only the owner can answer that.

Observer side: `ApplyRemoteExecutionStart` instantiates `EntExecution.ExecutionPrefab` at the enemy's
position, then subtracts everything that reaches for the local player:

| Neutralised | Why |
| --- | --- |
| `Execution` component (disabled before `Start` runs) | `OnStart`/`OnUpdate`/`OnUse` bind the local Penitent, block input and call `KillEntity()` |
| `ExecutionAnimationEvents` (disabled) | local slow motion, camera zoom, rumble, execution sound |
| `Interactable.OnEventLaunched` (Harmony prefix) | `INTERACTION_START/END` would block the observer's input and pay them fervour |
| every `Collider2D` in the clone | no sensor, prompt or damage collision with the observer |
| `StuntBehaviour.OnStateUpdate` (Harmony prefix) | it would call `Alive()` on the real enemy after `StuntTime` and report `state = 0` |

Then `USED` is triggered on every animator in the clone that declares that trigger, the real enemy's
sprite is hidden (parity with `Execution()`), and the clone is destroyed when its clip reports a
finished state, with a 20 s leak guard. On the authoritative death the handler skips
`Behaviour.Death()`/`PlayDeath()` - the owner plays no death clip for a completed execution - and the
death simply removes the enemy.

If anything is missing (no prefab, no `USED` trigger, an exception while instantiating) the code logs
a warning and falls back to the previous behaviour, so an observer without a clone still gets the
ordinary death presentation rather than nothing.

The first clone also logs the prefab's hierarchy once (`Execution prefab shape ...`), which is what
answers the three asset questions at the end of this section without an editor.
