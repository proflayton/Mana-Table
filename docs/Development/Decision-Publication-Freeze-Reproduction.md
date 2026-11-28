# Reproducing the 0.2.510 decision freeze

Investigated against release `mana-native-v0.2.510`, commit
`3c80a76af14611e3b243a8df5443411a7a7096b9`.

## Observed failure

The actual local application stalled with one human and the Blood Rites,
Veloci-Ramp-Tor and Cinder Commander background policies. Captures include an
Icetill Explorer mill trigger on turn 17, an Aesi landfall trigger on turn 27 at
ordinary pacing, and priority during an AI's own draw step. The rules thread was
waiting for `InputPassPriority`; the owning AI's observation instead contained
`status: resolving` with no decision. There was no engine exception or AI fault.

A traced read had the same active input in both InputQueue and InputProxy, no
queued command, and a source generation newer than its committed publication.
The event stack was:

```text
MatchSession.decisionObservationValues
SourceActions.discover
Card.getSourceActionAbilities
SpellAbility.canPlay / GameActionUtil.getOptionalCostValues
GameAction.checkStaticAbilities(false)
StaticEffect.remove / StaticAbilityContinuous.applyContinuousAbility
Player.removeMaxLandPlays / Player.addMaxLandPlays
GameEventPlayerStatsChanged
```

Alternate-face checks temporarily rebuild continuous effects, then restore
them. Player-stat notifications ignored the existing `runEvents=false` scope.
With extra land plays present, those inspections invalidated their own source
authority. The next read hid the invalidated decision without scheduling a new
publication, leaving Forge waiting for an invisible answer. Republishing alone
was insufficient because the next alternate-face query emitted the same events.

## Fix boundaries

Player facts derived from continuous effects honor the event scope during both
the speculative pass and restoration. The scope is local to the executing thread
and restores in `finally`; ordinary live notifications remain enabled. This
includes land plays, player keywords and voting effects.

A superseded decision read schedules a full publication on the existing input
owner. It rechecks the exact pending input and InputProxy sequence before
publishing, without answering the input. Old source actions remain invalid.
This also recovers genuine intervening rules changes independently of the false
event fix.

## Deterministic checks

Run from the repository root, using the normal Maven environment:

```powershell
mvn -pl forge-api -am '-Dtest=DecisionPublicationRecoveryTest,IcetillExplorerRegressionTest' '-Dsurefire.failIfNoSpecifiedTests=false' test
```

`DecisionPublicationRecoveryTest` covers:

- An event before or during a human or automated decision read; the same waiting
  decision becomes usable again, stale actions are rejected, and a fresh mana
  action executes.
- Actual Bala Ged Recovery and Springheart Nantuko source inspections with Aesi,
  Elven Chorus, Oracle of Mul Daya or Fastbond. Inspection emits no transient
  player changes; ordinary continuous-effect updates still emit notifications.
- Player keywords, additional votes, optional votes and villainous choices under
  the same alternate-face inspection.

All 12 isolated recovery and producer cases fail against the untouched 0.2.510
engine: four retain `resolving` instead of restoring the decision, and eight
report 4–16 transient player-stat events from an inspection. The direct
alternate-face cases isolate the producer without requiring a stalled game to
reach its timeout.

`IcetillExplorerRegressionTest` resolves a real land play and mill with Elven
Chorus exposing Mossborn Hydra. It checks the library, graveyard and new visible
top-card identity for all audiences, including a large battlefield and two
eliminated seats.

## Complete local-game journey

Build `mana-native/Mana.Table.Tests`, then run:

```powershell
dotnet <test-output>/Mana.Table.Tests.dll --reproduce-solo-freeze --engine-resources <engine-bundle> --engine-jar <immutable-engine-copy.jar> --profile <unique-evidence-directory>
```

The runner uses the actual TableApplication update, polling, Auto, presentation
and source-action paths. A script supplies the human's independently admitted
choices; three ordinary background AI policies supply the opponents' choices.
It confirms the actual no-land warning when needed. A game must reach an engine
outcome; 45 seconds without public rules progress fails the run and captures the
human state, raw AI observations, continuation owners, recent receipts and rules
thread dump where available.
The runner also has a 12-minute total time budget. Reaching that budget before
an outcome is recorded separately from a 45-second loss of rules progress.

`--freeze-fast` removes the AI thinking delay and accelerates presentation time.
Omit it to retain the production 2.4-second AI cadence. `--freeze-seed <integer>`
controls the policies, not Forge's deck shuffle; full-game runs are repeatable
journeys rather than deterministic deal replays. Keep each JVM's engine JAR at
an immutable path while it runs.

## Validation record

| Check | Result |
| --- | --- |
| Untouched release, 12 isolated reproductions | 12 failures at the expected assertions |
| Fixed engine, focused authority and rules suite | 63 passed, zero failures |
| Native diagnostic runner build | Passed; one existing AudioSettingsSmoke nullability warning |
| Fixed engine, accelerated complete local games | Five natural outcomes, turns 27, 33, 34, 40 and 42 |
| Final candidate, ordinary AI pacing | Advanced through turn 33 for 12 minutes; stopped at the runner's total time budget, with no rules-progress stall |

The captured release failures and the fixed candidate's complete-game results
are recorded in `F:\ForgeButBetter\.tools\freeze-0510-results.json`, with raw
evidence in the referenced profile directories. The normal-paced journey's
record is `solo-freeze-reviewed-normal` in that workspace.
Human UX review of the initial publication fix is not applicable: it changes
rules observation and input recovery without changing a player control.

## Bounded-progress follow-up

The production player session and AI host now share a monotonic automatic-work
budget and negotiated input-reconciliation control. See the
[bounded progress contract](Mana-Table-Architecture.md#bounded-match-progress-and-input-recovery)
and [regression inventory](Mana-Table-Testing.md#match-liveness-and-bounded-recovery).
An expired budget permits one bounded repair, followed by a fresh owned read;
failed repair becomes an explicit interruption instead of a silent wait. No
repair can pass priority, choose an option, concede or replay an uncertain action.

A later actual four-player run reproduced another stall on turn 30, `MAIN1`,
with Blood Rites AI holding a published priority input. Its rich frame reported
an available response but continuation metadata still said `awaiting-observation`.
The human had acknowledged activity 617; the AI's acknowledgement remained at
615 with board 1785 while its current frame was board 1790. The rules thread was
waiting for the actual input. The local evidence is retained in
`F:\ForgeButBetter\.tools\liveness-natural-game-final`.

That candidate incorrectly exempted the hold merely because a human was present.
The replacement uses engine-reported `matchReadingWaits` for actual exhausted
human delivery credit. An observation-held bot acknowledges a newer board even
below its ordinary activity batch threshold. Returning credit also refreshes
every seat's continuation metadata, so the real response becomes visible without
requiring another rules action. An unchanged board receives no repeated ACK, and
an actual human reading wait still cannot be bypassed with a manual AI pass.

The two isolated regressions fail before these changes: one retains the unowned
hold indefinitely; the other skips the required current-board acknowledgement.
They pass after the fix. Java controls also exercise actual exhausted human
credit and its retirement across audiences. The monitor ignores revision churn
and reissued publication scopes for the same engine input.

Human review of the follow-up's recovery text is **pending**. It reuses the
existing recovery dock and controls; automation does not establish UX approval.

### Follow-up local validation, 2026-11-28

| Check | Result |
| --- | --- |
| Java 17 recovery, publication, source authority, priority and Icetill suite | 84 tests passed, zero failures |
| Full graphics-free client inventory | 6,258 checks passed, including 29 focused liveness checks |
| Headless native presentation inventory | 9,768 checks passed |
| Packaged JVM recovery with controlled player/AI observation loss | All seven journey checks passed; actual Java publication loss is tested separately |
| Final packaged engine, accelerated four-player games | Two natural outcomes, turns 27 and 36; 322 and 561 human commands; no AI fault or player recovery |
| Native harness build and maintained documentation links | Passed; one existing AudioSettingsSmoke nullability warning |

Final engine SHA-256:
`2CB9FD7B2139B6D19357319C8B5E6A5D10062E826931D5591736F054D051F808`.
The immutable JAR is `F:\ForgeButBetter\.tools\liveness-engine-reading-20261128.jar`.
Raw final profiles are `liveness-real-recovery-complete`,
`liveness-natural-game-reading-01`, `liveness-natural-game-reading-02`, and
`liveness-presentation-reading` under that workspace's `.tools` directory.
The two failing pre-fix controls are retained as
`liveness-observation-control-baseline.log` and
`liveness-observation-ack-baseline.log`. The earlier failed natural game remains
part of the evidence; it is not counted as a passing run.

These are local candidate checks. Java 21 and source-bound remote CI have not
been run for this follow-up; no package has been released.
