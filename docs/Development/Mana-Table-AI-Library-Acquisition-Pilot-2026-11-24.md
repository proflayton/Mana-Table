# Library acquisition measurement, 2026-11-24

**Result: natural pilot stopped on a provider safety failure.** The current-source
development replays confirm the bounded acquisition selection improvement, but
the full-game comparison is incomplete and cannot support a strength or promotion
claim. Shipping remains v6; human casual-Commander review is pending.

The measured source is `fb04a4de1fcc060355c1d893009709016044b1fa`, tree
`a2bd5e01d4926577eb982539a222684b20bee630`. This report's later documentation
commit is not the measured source. A fresh local clean-source release-format
`0.2.0` package, production engine and source-bound harness were built for the
experiment; these are not relabeled public `0.2.504` binaries. The packaged
AI/planning/contracts/Forge assemblies were copied into the harness and their
hashes reconciled. The [aggregate receipt](../../mana-native/experiments/results/library-acquisition-fb04-20261124.json)
records artifact, source/archive, lock and private-evidence digests.

## Frozen design

Five specifications and immutable locks were frozen before experiment outcomes
at `2026-11-24T22:32:11.2676921Z`. Both treatments used the same provider facts,
resources and budgets, full attachment evaluation, and three full-v6 reference
seats. The treatment was the explicit library-acquisition option: full v6 versus
`casual-commander.v7.library-acquisition.v1`. Palantir policy changes were absent.

The natural schedule retained all 32 cells: four focal decks crossed with four
physical seats and two treatments. The frozen pod was Rhys token/equipment,
Pir/Toothy counter/ramp, Balmor spells/multitarget and Traxos artifacts. Planning
seed was decimal string `261027`; policy seed was `2668620592559112008`.
Forge rules randomness was uncontrolled, so these are blocked independent games,
not paired shuffles. Execution was sequential, with 10-second/4096-work decision
allowances, 180-second matches, 160-turn limits, 300-second workers and 16 MiB
captures. The primary natural endpoint was focal win share; no confidence
interval or general-strength conclusion was planned for this single block.

The separate natural A/A control deliberately launched only its first two
scheduled jobs, both full v6, from a complete 32-cell lock. The remaining 30
control cells were intentionally unlaunched. This is a lifecycle/accounting
control, not a completed balanced A/A study; equal independent-game outcomes
were not required.

## Results and retained denominators

| Layer | Planned / executed | Finding |
| --- | --- | --- |
| Candidate replay A/A | 8 / 8 | Four unchanged-input pairs emitted identical commands; 4/4 useful passes in each arm |
| Acquisition replay A/B | 8 / 8 | Baseline 0/4, candidate 4/4; four corrections, no regressions or incomplete pairs |
| Attachment regression A/B | 10 / 10 | Both arms 5/5; no regressions |
| Natural lifecycle A/A | 32 / 2 | Two verified terminals, clean receipts and cleanup; 30 intentionally unlaunched |
| Natural v6/v7 pilot | 32 / 7 | Six verified terminals, one unresolved critical provider failure; 25 unlaunched |

The two staged acquisition captures share one family: the primary macro average
across three declared development families changed from zero to one. These are
known mechanic regressions, not unseen opportunities. Replay establishes useful
selection, not actual land movement or whole-game value. Earlier controlled
continuations remain separately documented in the
[acquisition experiment](Mana-Table-AI-Library-Acquisition-Experiment.md).

The natural pilot's fixed denominator is 16 games per arm. Baseline has three
verified terminals and 13 unresolved planned games; candidate has three verified
terminals, one provider failure and 12 unlaunched games, also 13 unresolved.
Planned baseline win-share bounds are `[0.125, 0.9375]`; candidate bounds are
`[0.0625, 0.875]`; candidate-minus-baseline bounds are `[-0.875, 0.75]`.
These are worst-case missing-outcome bounds, not confidence intervals. The failed
game is neither a draw nor an invented policy loss. Completed-only win rates
cannot replace these denominators.

The framework recorded one critical safety failure, zero invalid evidence,
zero cleanup failures and zero policy failures. Its exit code zero means that
measurement records were committed; the campaign result is **guardrail-failed**.
The failed lock is permanently retired. It was not resumed, retried or replaced
within this study. The failed worker retired without timeout, output overflow,
I/O failure or uncertain ownership; scoped process inspection found no surviving
experiment processes before the runtime allocation was released.

Full-game acquisition exposure and causal-consequence counters are not
instrumented at this source. First-focal-per-task replay samples cannot establish
that no opportunities occurred, nor credit accepted commands as resolved effects.
No acquisition coverage, causal benefit or playing-strength estimate follows
from this stopped pilot.

## Failure and follow-up

The seventh first attempt, focal Rhys at physical seat 3 under the candidate,
stopped at turn 39 in combat damage. Independent spectator and owned viewer 0
shared board revision 3387 and matching execution correlations, but disagreed on
public facts. Viewer 0 had just lost. Its 25 retained public battlefield/graveyard
portraits had no current visual/combat IDs in the owned projection; the spectator
assigned IDs to the same portraits. Two combat blocker references consequently
differed as well. The exact returned pair and bounded differences remain private;
their digest is in the aggregate receipt. This provider defect is not established
as an effect of the candidate policy.

`Game.onPlayerLost` removes in-game membership before the required read-only
face-down-card disclosure completes; `GameAction.checkGameOverCondition` sets the
final outcome afterward. In the last-two-player path, registered-player zones
remain readable during that disclosure. `CurrentCardViews` correctly withholds
current occurrence identities while the departed player is out of the game and
`isGameOver` is still false. `SpectatorObservation` instead allocates identities
without the same current-occurrence qualification. Existing
`TerminalProjectionTest` explicitly requires these historical portraits to remain
readable with empty action/visual/combat IDs during the pending disclosure.

The next source change must reconcile the independent public projection and
out-of-game combat references without manufacturing current capabilities,
weakening the same-board guard, leaking a private original face, or altering
loss/reveal/outcome sequencing. Extend the actual pending-loss-reveal regression
to compare all audiences at one board revision before acknowledgement; retain
current live identities, stale-action refusal and final-board behavior. A reviewed
fix needs a separate branch, new source/binaries and fresh experiment locks.
All seven original attempts remain part of this report.

Validation included 27 coordinator boundary checks and 226 focused native
experiment checks. Two investigation errors were retained: a PowerShell wrapper
initially treated an expected negative-control stderr message as fatal, and an
additional A/A checker initially demanded identical noncanonical reserialization
hashes. Neither caused an experiment retry. The corrected check proved identical
original raw bytes, full parsed requests with every numeric token preserved,
unchanged array order and identical emitted commands. Only Mana dictionary
member ordering differed in derived JSON. Explanation/reason strings were not
recorded by the frozen runner and their equality is not claimed.
