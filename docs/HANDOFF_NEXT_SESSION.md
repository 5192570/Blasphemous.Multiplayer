# Next session handoff — enemy sync handler refactor

Written at the end of the WheelCarrier riposte/execution work, before the refactor branch was started.
It exists because session context does not survive between chats: everything below is either a fact
verified from decompiled vanilla, a decision already agreed with the reviewer, or a tooling quirk of
this sandbox. Read §2 and §4 before touching code; read §10 before running anything.

- **Base to work from:** `enemy-sync-handler-refactor` = `dcf1512` (= `main` after PR #3 merged).
- **PR #3 is merged and closed.** Do not re-open it and do not re-litigate anything in §5.
- **Suggested PR title for the refactor:** *One handler per tier, one thin adapter per enemy.*

---

## 0. How to start the new chat

Paste this so the new session begins with the same footing:

> Continuing the Blasphemous multiplayer enemy-sync work. Base branch `enemy-sync-handler-refactor` (= `main` at
> `dcf1512`, PR #3 merged). Read `docs/HANDOFF_NEXT_SESSION.md` first — it has the verified state, the agreed
> tier/adapter plan, the vanilla facts, the test matrix, and the sandbox tooling notes. First task: step 1 of §4
> (move-only `EnemyAdapter` split) after collecting a `[EnemyTierDiag]` inventory log.

---

## 1. What shipped, and what was verified

PR #3 ("WheelCarrier riposte/execution desync") merged. The commits that matter, oldest first:

| commit | what it did |
|---|---|
| `110792b` | unified the mirrored death presentation (clear `GotParry` first, `IsStunt`/layer restore, then hide/restore sprite + `Death()`, then `ClearSpecialState`) |
| `a84495f` | made the execution clone draw what vanilla draws: only the serialized `interactorAnimator` is triggered, prompt art hidden, `InputNotifier` disabled, clone retired when the clip leaves its state |
| `93b3ec5` | mirrored **executionable** state gets its own presentation (the prefab's `WheelCarrier_stun` loop behind a hidden enemy), clone lifetime split into executionable vs finisher, finisher reuses the same clone, `completedExecution` gate, executor's **remote avatar** hidden for the finisher |
| `e37fea6` | the hidden avatar stays hidden through `ApplySkinTexture` (`SetPresentationVisible` + `presentationHidden`) |
| `332914c` | compile fix: `Penitent` lives in `Gameplay.GameControllers.Penitent`, not in a nested namespace |
| `58a223a` | `EnemyTierProbe` — one diagnostic line per enemy type per scene |
| `2c287d8` | same probe, variant-aware (prefab/animator/execution/life/stunt signature, not behaviour type) |
| `dcf1512` | merge fix: the probe was missing `using UnityEngine;` (it uses `Time` and `GetInstanceID`) |

Tester-confirmed timeline (observer `Astroprof` watching `Cracky`'s WheelCarrier `-20954`, one run):

```
57.023 state-2 report received
57.033 Executionable presentation created … prompt=Interactable Animation, interactor=Left Interactor Animation
58.541 execution report
58.547 Hiding executor avatar owner=Cracky, wasEnabled=True
58.548 Execution presentation started … reused=1.518s        ← one object, both phases
58.556 authoritative death, completedExecution=True, sprite=False
61.797 Execution presentation ended … reason=execution clip ended, aliveFor=3.259
61.799 Restored executor avatar owner=Cracky, reason=execution clip ended
61.898 Execution clone gone
```

Executor side (`Astroprof` on its own `SPAWN (16)` at 75.115): `local Penitent sprite true->false … onExecution=True`
at 75.130, back to `true` at 78.311. Vanilla's local hide path was never touched.

Both original symptoms are gone: the observer enters the executionable state when the **timed attack** lands, not
when the owner presses E/Y, and the executor is not drawn next to the victim during the finisher.

---

## 2. Repo map

| file | lines | role |
|---|---|---|
| `ProgressSync/EnemySync.cs` | 305 | engine: per-scene authority election, snapshot publish/interpolate, identity refresh, commitment. Nothing per-enemy. **Do not change** in step 1. |
| `ProgressSync/EnemySyncManager.cs` | 73 | discovery + hardcoded `is` chains in `Supports()`/`Attach()` + probe hook. This is the registration table target. |
| `ProgressSync/EnemySyncHandler.cs` | 48 | abstract contract (attach, `OnSyncUpdate`, `OnAuthorityContextChanged`, `SpecialState`, `ApplySpecialState`, `OwnsUnattributedDeath`, abstract presentation/hit/death methods). |
| `ProgressSync/FoolEnemySyncHandler.cs` | 72 | **already an adapter in all but name** — pure gameplay translation, no presentation state. |
| `ProgressSync/WheelCarrierEnemySyncHandler.cs` | 1316 | the tier-3 implementation + WheelCarrier specifics + Harmony patches + diagnostics. Target of the split. |
| `ProgressSync/EnemyDeathSyncManager.cs` | 1011 | authoritative death propagation, spawn identity/generation, per-enemy death hooks. **Fragile, leave alone** unless the refactor forces a signature change. |
| `ProgressSync/EnemyTierProbe.cs` | 206 | `[EnemyTierDiag]` inventory probe (diagnostic only). |
| `Players/PenitentVisibilityProbe.cs` | 68 | `[PenitentVisibilityDiag]` local Penitent renderer/damage-area transitions. |
| `ProgressSync/WheelCarrierVisualProbe.cs` | 111 | renderer/animator dumps for the enemy + clone (`[WheelCarrierVisualDiag]`). |
| `docs/WHEELCARRIER_EXECUTION_CHAIN.md` | §1–§14 | the how and why of the execution chain, including §13/§14 for the clone work. |
| `docs/WHEELCARRIER_RIPOSTE_DESYNC.md` | 371 | the original symptom diagnosis (D1–D5) and the review follow-ups. |
| `ProgressSync/WheelCarrierRemotePlayerVisionPatch.cs` | 74 | makes the WheelCarrier see the remote player; per-enemy, stays per-enemy. |

---

## 3. Vanilla facts — do not re-derive these

All paths are relative to the decompiled tree (`/tmp/au/Assemblies-Unity-Games-main/Blasphemous`, see §10).

**The execution chain (one pass, for any executable enemy):**

1. `Tools.Level/Interactable.cs` `Use()` → `Tools.Level.Interactables/Execution.cs` `OnUse()`:
   `interactableAnimator.gameObject.SetActive(false)` (prompt art off) → `ShowPlayer(false)` →
   `ExecutedEntity.KillEntity()` → `interactorAnimator.SetTrigger("USED")` → waits for `Consumed` → destroys.
2. `Interactable.ShowPlayer(bool)` (≈ line 492) is the whole implementation:
   `Core.Logic.Penitent.SpriteRenderer.enabled = show; Core.Logic.Penitent.DamageArea.enabled = show;`
   It only ever touches the **local** Penitent — the remote avatar must be handled by us (`SetPresentationVisible`).
3. `Execution.OnStart` schedules `TriggerAwareness` at 0.2 s and `ShowTutorial` at 0.6 s, so `ExecutedEntity`
   must be set on our clone even though `Execution` is disabled.

**The executable tier is already factored by vanilla:**

- `Gameplay.GameControllers.Enemies.Framework.IA/EnemyBehaviour.cs:327-345` — `Parry()`, `Alive()`, `Execution()` are
  `virtual` on the shared base; the base bodies only set/clear `Entity.IsStunt`.
- **16** enemy behaviours override `Execution()`, and **16/16** call `base.Execution()`, call
  `EntExecution.InstantiateExecution()` once, and implement `Alive()` as the exact inverse
  (layer → `Enemy`, sprite on, `CurrentLife = Stats.Life.Base / 2`, restart behaviour, `EntExecution.enabled = false`).
  List: Acolyte, Bishop, CrossCrawler, Flagellant, FlyingPortrait, Menina, NewFlagellant, Nun, RangedBoomerang,
  AxeRoller, Roller, Runner, ShieldMaiden, ViciousDasher, WalkingTomb, WheelCarrier.
- `EnemyBehaviour.GotParry` (line 42) and `Enemy.IsStunt` (`Enemy.cs:96`) are shared; `Enemy.StuntTime` is a public
  field, default `5f` (`Enemy.cs:68`).
- `StuntBehaviour` (`Gameplay.GameControllers.AnimationBehaviours.Enemies.Stunt`) is **one** class, no subclasses.
  Its `OnStateUpdate` calls `Alive()` + `Destroy` — this is why a mirrored path must never route into it.
- `Gameplay.GameControllers.Combat/EntityExecution.cs` is a `Trait` (`Trait : MonoBehaviour`) on the enemy GameObject,
  exposing `ExecutionPrefab` and `InstantiateExecution()` (which sets `ExecutedEntity` and flips the prompt sprite).
  `enemy.GetComponent<EntityExecution>()` works for all 16 — no need for each enemy's `EntExecution` property.
- Vanilla swaps the enemy to layer `Default` in `Execution()` and back to `Enemy` in `Alive()`. Copy that verbatim;
  do not maintain a parallel mechanism.

**Other facts learned the hard way:**

- `Entity.Stats` is a public **field** initialised inline (`Entity.cs:47`), so it is never null, but `Stats.Life` can be.
- `VariableAttribute` (base of `Life`) has **no public `Base`** — use `CurrentMax`. Vanilla's `Alive()` uses
  `Stats.Life.Base` internally, which is why assuming it from outside fails to compile.
- `Enemy.EnemyAttack()` is an abstract method returning `EnemyAttack : Attack : Trait : MonoBehaviour` → `.enabled` is valid.
- `EntityExecution` is in `Gameplay.GameControllers.Combat`; `Penitent` is `Gameplay.GameControllers.Penitent.Penitent`.
- **71** classes derive from `EnemyBehaviour` in this decompile; **only 16** override `Execution()`. The
  non-executable surface (T1/T2, bosses included) is therefore the large one — see §4.

---

## 4. The agreed plan (CGPT signed off on the direction)

Reviewer's conditions, verbatim in intent: keep the adapter contract **small**, prefer a **variant-aware** probe,
treat T2 as an **experiment with one representative enemy**, test T3 **interruptions**, and add complexity only when
a real enemy demonstrates the need. The full exchange is PR #3 comments `-6642837924` (proposal) and `-6643197779`
(answers).

**Four layers:**

| layer | today | after |
|---|---|---|
| `EnemySyncManager` | hardcoded `is` chains | registration table `enemyType → handler kind + adapter` |
| `EnemySync` | engine | unchanged |
| `EnemySyncHandler` | one implementation per enemy | **tier policy**: `SimpleEnemySyncHandler` (Fool) / `ExecutableEnemySyncHandler` (WheelCarrier + the other 15) |
| `EnemyAdapter` | does not exist (Fool fills the role) | per-enemy translation, ~40–70 lines |

**Minimal adapter contract — only what already differs between Fool and WheelCarrier:**

`IsWalking`, `SetAuthority(bool)`, `SetTarget(GameObject)`, `ApplySnapshot`, `ApplyHit`,
`ApplyDamageReaction(bool dead, byte orientation)`, `ApplyDeath`, plus T3-only
`ApplyParryPresentation`, `HideForExecution`, `RestoreAlive`.

Nothing speculative: no capability registry, no tier-numbered types, no virtuals added "for later".

**Tiers are capability flags, not difficulty:**

- **T1 snapshot** — `Fool`. Generic path only. Done.
- **T2 special non-execution states** — attack windups, flying, teleport, shields. **Undesigned and not uniform.**
  Candidates: `WallEnemyBehaviour` (stationary turret, cheap) and `ExplodingEnemyBehaviour` (fuse — the case where an
  invisible state corrupts the other player's damage prediction). Both are plain `EnemyBehaviour` subclasses
  overriding `Idle/Wander/Chase/Attack/Damage` with **no** `Execution()` override, so they are T2 by construction.
- **T3 executable** — one implementation, the 16 above. Packet 25 (`state`, `side` + scene/spawn/generation/epoch)
  already fits; **no protocol change**. `VERSION = 15` at the time of writing.
- **T4 scripted/boss** — out of scope.

**Extraction order (each step separately reviewable):**

1. `EnemyAdapter` + move Fool and WheelCarrier behind it. **Acceptance: identical diagnostic output** on an
   identical run while `WheelCarrierEnemySyncHandler.cs` shrinks to an adapter. No new enemies, no behaviour change.
2. Replace the `is` chains with the registration table.
3. Add **one** new executable enemy as adapter + table row only. If it needs a change to the generic handler,
   the abstraction is wrong — change the abstraction, not the enemy.
4. Delete the per-enemy handler file.
5. Then T2, with the turret or the fuse.

**Guardrails:**

- The mirrored path never calls `Alive()` (it resets `CurrentLife` and restarts AI — banned for non-owners since
  review `6635844726`), and never routes into `StuntBehaviour.OnStateUpdate`.
- No new timers. The only one in the current code (`ExecutionablePresentationMaxLifetime = 15f`) is a **clone leak
  guard**, not a recovery path, and must stay that way.
- Keep existing diagnostic tags; add `enemy=<Type>` as a field instead of renaming them.
- Do not touch damage reconciliation, state-2 collision protection, authoritative death propagation, or
  `EnemyDeathSyncManager` identity/generation logic.

---

## 5. Invariants from the merge (do not "clean up")

These look redundant and are not:

- **Two events, not one.** The timed attack creates the executionable state (mirror state 2); the owner's E/Y press
  starts the finisher. They arrive separately and the presentation has two lifetimes.
- **`completedExecution` = clone exists *and* finisher started.** A death with an unfired clone is an interrupted
  death and must show the ordinary death clip.
- **`GotParry` is cleared first** in death cleanup, before `Death()`, otherwise `WheelCarrierAnimatorInyector.OnUpdate`
  re-writes `IS_PARRIED` every frame and the death clip can never take over.
- **The enemy's own sprite stays disabled** behind the clone in both phases; after a completed execution it stays
  disabled for the rest of the level (see §8).
- **The prompt art is part of the presentation.** `Interactable Animation` playing `WheelCarrier_stun` *is* the
  executionable look; it is only hidden when the finisher starts.
- **One interactor animator.** The prefab carries `Left` and `Right Interactor Animation`; trigger only the serialized
  `interactorAnimator` (read via `AccessTools.Field(typeof(Tools.Level.Interactable), "interactorAnimator")`).
- **Avatar hiding is tracked implicitly** by `_hiddenExecutionAvatar` being non-null; every clone exit path funnels
  through `DestroyExecutionPresentation`, which is what restores it.

---

## 6. First actions in the next session

1. **Read the merged code, not memory.** In particular `WheelCarrierEnemySyncHandler.cs` on the new base (1316 lines)
   and `EnemyTierProbe.cs` (it now has `using UnityEngine;` — do not "fix" it again).
2. **Probe play-test.** `D01Z01S01` + a couple of neighbouring rooms, grep `[EnemyTierDiag]` only.
   Acceptance: separate `variant=Tutorial` and `variant=Regular` lines for the WheelCarriers, one line per variant,
   plus whatever new types are met. That inventory is the adapter surface's source of truth — do not write the
   adapter from the 16-type list alone.
3. **Step 1 of the split** (§4), with the log-parity acceptance criterion. Keep `WheelCarrierVisualProbe` out of the
   generic handler — it is WheelCarrier-specific instrumentation and may be deleted once the tier work is trusted.
4. Only then the registration table, then the third enemy.

---

## 7. T3 interruption matrix (test cases, not design work)

| case | path that must run |
|---|---|
| scene change while executionable | `TearDownExecutionablePresentation` → `DestroyExecutionPresentation` (no avatar hide to undo) |
| scene change during the finisher | `DestroyExecutionPresentation("sync handler destroyed")` → `RestoreExecutionOwnerAvatar` with the avatar already gone |
| owner disconnect while executionable | mirror owner left → teardown, enemy drawn again, no clone leak |
| owner disconnect during the finisher | avatar restore must not throw on a missing handle |
| authority change (epoch bump) in either phase | `OnAuthorityContextChanged` → presentation retired before the new authority's snapshots apply |
| death mid-finisher | `completedExecution=True`, clone plays out, avatar restored at clip end |
| death mid-executionable | `completedExecution=False`, `"death without a finisher"`, enemy sprite back for the death clip |

Hardest one: **authority change during the finisher** — `_hiddenExecutionAvatar` is per-handler state and must be
cleared exactly once, and there may be no avatar left to restore.

Also still unverified: an execution longer than the observed ~3.3 s (all evidence so far is 3.26 s), and the ordinary
damage/death path after the refactor (must still report `completedExecution=False`).

---

## 8. Known gaps and deliberate leftovers

- The observer keeps the enemy GameObject alive but invisible after a completed execution (sprite off, attack off,
  layer `Enemy`, `dead=True`, animator still cycling `WheelCarrier_idle`). Vanilla only destroys the enemy on the
  owner's side; the clone is the shared presentation. Destroying it would mean touching the spawn registry — left to
  scene unload on purpose.
- The executor's **nametag** still floats at their position during the finisher (body hidden, label not). Cosmetic,
  untouched, needs `OtherPlayerManager` nametag lookup if wanted.
- The post-death renderer watch logs every idle frame change for its bounded window (~8 s). Noise only.
- T2 byte-range for special states is **not reserved** yet: players already use 240–249 for special animations, and
  packet 25 states 1/2 are taken by parry/execution. Decide the range before the first T2 enemy.
- `EnemySyncManager.Supports()` is the single place that decides who is synced; anything added there immediately
  affects death propagation and identity/generation, not just presentation.

---

## 9. Log triage conventions

One file per client, **complete lines** (trimmed columns cost a whole test round once). Regex used in Notepad++:

```
\[(WheelCarrierExecutionDiag|WheelCarrierStateDiag|WheelCarrierVisualDiag|PenitentVisibilityDiag|EnemySyncDiag|EnemyTierDiag)\]
```

Rules that were agreed with the reviewer and tester:

- Never compare `time=` values **across** the two clients; only within one log.
- `[SharedDiagMerged]` lines are the same events relayed through the collector — deduplicate by `session/sourceSeq`
  before counting anything.
- Absence of a line in an excerpt is not proof of absence — the logs contain queue-drop diagnostics; ask for the
  complete log before concluding.
- Retracted evidence: `ExecutionDupeLogOutput.log`, `P2ExecutionDupeLogOutput.log` (they contained duplicate-test
  artefacts). Do not cite them.
- For a first-pass triage, `[EnemyTierDiag]` needs no cross-client correlation at all — it is inventory, not timing.

---

## 10. Sandbox / tooling notes (for me, next session)

- **No `dotnet`, `csc` or `mcs`.** Compilation must be verified by the user. Syntax check with tree-sitter:

  ```bash
  python3 -m venv /tmp/venv && /tmp/venv/bin/pip install -q tree_sitter tree_sitter_c_sharp
  /tmp/venv/bin/python -c "..."   # parse each changed file, walk for ERROR/is_missing nodes, expect 0
  ```

- **Decompiled vanilla** (re-fetch; `/tmp` is wiped between sessions):

  ```bash
  curl -sL https://codeload.github.com/EKQRCalamity/Assemblies-Unity-Games/tar.gz/refs/heads/main -o /tmp/au.tgz
  mkdir -p /tmp/au && tar xzf /tmp/au.tgz -C /tmp/au    # → /tmp/au/Assemblies-Unity-Games-main/Blasphemous
  ```

  `raw.githubusercontent.com` is **blocked**; use the tarball or `https://api.github.com/repos/…/git/blobs/<sha>`.
- **Encoding:** several files start with `EF BB BF`. Reading with `encoding='utf-8'` keeps the BOM as `\ufeff` and
  writing back as `utf-8` preserves it; `utf-8-sig` **adds** one. Read/write bytes if unsure. Never re-save a file
  through a tool that strips it without checking `git diff` for a line-1 change.
- **`xxd` is absent** — use `od -An -tx1`.
- **`gh pr edit` fails** — use `gh api -X PATCH repos/…/pulls/N --input body.json`.
- **Push failures:** if the remote moved, `git reset --mixed origin/<branch>` (keeps edits), then re-commit.
  Never `git checkout --` a file with unmerged work.
- **Unity/API compile traps already hit:** `Stats.Life.Base` does not exist publicly (use `CurrentMax`);
  `Penitent` namespace; missing `using UnityEngine;` in a file that uses `Time`/`GetInstanceID` (this is exactly what
  `dcf1512` fixed). Copy the using block from a neighbouring file in the same folder before writing a new one.
- **Always verify a claimed API before using it** (`grep` the decompiled tree for the member), because a wrong guess
  costs the user a build round-trip.

---

## 11. Dead ends — do not retry

- Timeout-based presentation recovery; `MirrorTimeout` auto-restore; "restore from elapsed time alone".
- Routing a mirrored state into `StuntBehaviour.OnStateUpdate`, or calling `Parry()/Execution()/Alive()` on a non-owner.
- Patching `Execution.OnUse` (iterator) — patch `Tools.Level.Interactable.Use()` instead; and never call `Execution.OnUse`
  on the clone.
- Triggering every animator that declares `USED` (two overlapping bodies).
- Reading JSON config at plugin start / `ApplyBackgroundExecution()` — `runInBackground` uses BepInEx config, bound at
  the earliest callback, separate commit.
- Probe keys without the component kind (the `Animator` entry overwrote the `SpriteRenderer` entry).
- Treating `AnimatorStateInfo.normalizedTime >= 1` as reliable for the interactor animator (it leaves to `clip=null`
  instead) — retirement is gated on the state leaving, guarded by an "entered once" flag.
- Assuming "the parried pose" is acceptable for state 2 — that was the whole bug.
- Reading `sprite=False` as "nothing can be drawn" (it covers only `WheelCarrier.SpriteRenderer.enabled`).
- Global unattributed-death reporting; blanket widening of `LocalOwnershipGrace`; blanket `EnemySyncManager.Attach` guard.

---

## 12. Evidence index

- `docs/WHEELCARRIER_EXECUTION_CHAIN.md` — §1–§12 the chain itself, §13 clone presentation fixes, §14 executionable
  presentation + avatar hiding. Read §13/§14 before changing anything in the clone lifecycle.
- `docs/WHEELCARRIER_RIPOSTE_DESYNC.md` — original diagnosis D1–D5, the review follow-ups (timeout removal, death-hook
  lifecycle, duplicate/remote-induced reports, ownership-grace finding).
- PR #3 comments worth re-reading: `-6635844726` (death cleanup rules), `-6640296831` (config binding),
  `-6641432267`/`-6642127315` (state-2 lifetime evidence, avatar hiding plan), `-6642368198` (the verified timeline),
  `-6642837924` (proposal), `-6643197779` (agreed plan + minimal adapter contract + interruption matrix).
- Tester runs referenced throughout: `-20956` (never executed), `-21232` (execution, pre-`a84495f`), and the final
  verified run summarised in §1.
