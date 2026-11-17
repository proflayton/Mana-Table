# AI failure handling and qualified gameplay design

Status: design accepted by the technical lead, 16 November 2026. Source baseline:
`0eaa9f70abe3a40f92342352a20dc805a0ea730a` (the 0.2.469 source).
This design does not certify a deck, complete encounters M1/M2, or approve a
release. Implementation and exact-candidate human gameplay review are pending.

The goal is to finish complicated Commander games reliably while preserving
each seat's knowledge and every accepted rules action. Phase A independently
retains precise failure causes with unchanged stopping behavior. Phase B may
confirm that a pre-dispatch failure still belongs to the current input only
after a failing held-result reproduction establishes that race through the
shipping host/provider ownership path. Continuing
an unsupported current input, retrying an uncertain command, and substituting
another controller are outside this slice.

## Findings and decisions

The shipping composition creates `AiTableHost` with `CasualCommanderPolicy`
(`casual-commander.v5`). Forge supplies rules and current legal input domains;
its strategic controller is not the fallback for these automated seats. The
policy handles all fifteen decision families, uses explicit work/time allowances,
and submits through the same authoritative input controllers as human seats.

`AiTableHost.StepCoreAsync` currently maps `Unavailable`, `Invalid`,
`BudgetExceeded`, and `NeedsRecovery` to `FaultAsync`. The provider sends
`automatedFault`; `MatchSession.automationFault()` stops the match. This is an
intentional integrity boundary covered by `AiHostRegression`, not an accidental
exception handler that can safely be replaced with a pass. A required selection
can lack any supported continuation; its enabled completion button does not
establish a legal or meaningful alternative. A dispatched action's lost receipt
can mean that its costs already changed the rules state.

Two narrower problems remain:

1. Failure attribution loses useful structure. The seat view retains a valid
   unavailable-capability code in `Detail`, while the host records only
   `unavailable`. The history records `missing-capability`. Several baseline
   work-exhaustion paths use `Unavailable("work-budget", ...)`, which further
   obscures coverage gaps versus computation limits. Policy exception messages
   are deliberately withheld because they may contain private information.
2. A policy evaluates a captured input asynchronously. Its unsuccessful result
   can arrive after a legitimate concession, input retirement, elimination, or
   table completion outside that captured frame. The host does not independently
   confirm current input ownership before reporting that pre-dispatch failure.
   This is a source-derived race to reproduce with a held policy result; it is
   not established as the cause of a reported natural-game interruption.

Retain terminal handling for invalid policy contracts, malformed observations,
unknown execution, and accepted commands that fail to publish progress. Permit
bounded confirmation only for a policy's unavailable continuation or exhausted
budget when no command handoff occurred for that evaluation. A still-current
failure remains terminal. Fixing strategic capability requires policy/provider
work and evidence; observing the same input again supplies neither.

## Authority and invariants

- The rules provider owns game/seat membership, decisions, legal offers,
  visibility, receipt admission and terminal outcomes. No new provider mutation
  or inferred result is introduced.
- `AiSeatSession` remains the sole owner of frame chronology, evaluation
  cancellation, causal episode allowances, history and one-shot submission.
  The host does not reset or reconstruct a seat to recover a policy failure.
- `AiTableHost` owns one serialized provider lane, pending failure confirmation,
  priority withdrawal and final fault publication. Confirmation calls use its
  normal complete discovery operation, not a second concurrent reader.
- A failure descriptor is evidence. It contains no executable command, card
  face, text supplied by a policy exception, or permission to retry.
- Failed evaluations never spend resources through a fallback. Unknown
  execution never enters the pre-dispatch confirmation path.
- An unchanged input, higher metadata revision, changed pacing flag or completed
  control receipt cannot renew time/work, authorize another evaluation, or prove
  retirement of the failed decision.
- Same-root child inputs and a later return to that root retain the existing
  allowance, seed and active-time accounting. A new independently admitted
  causal episode receives an allowance only through the existing session rules.
- Diagnostics remain host-private. Failure to write evidence cannot change
  disposition, suppress withdrawal, or manufacture a successful game outcome.

## Failure decision table

| Observed condition | Dispatch knowledge | Disposition |
| --- | --- | --- |
| Valid `Unavailable` capability result | No handoff for this evaluation | One bounded current-input confirmation; retire the old failure only on the independent facts below; otherwise stop |
| Exhausted work or active evaluation time | No handoff for this evaluation | Same confirmation; spent allowance remains spent; a still-current input stops |
| Invalid capability identifier, invalid proposal/plan, policy exception | No accepted command from this result | Stop with structured contract/policy cause; no capability recovery or arbitrary legal answer |
| Engine explicitly rejects a dispatched command | Known rejection | Preserve existing one fresh observation path; an unchanged rejected stamp stops; never replay the command |
| Execute timeout, cancellation after dispatch, I/O error or lost reply | Unknown execution | Preserve `NeedsRecovery` and stop; no resubmission or pre-dispatch confirmation |
| Accepted command lacks fresh admitted progress for 45 seconds | Known accepted receipt, unknown progress | Stop once with `accepted-no-progress`; retain the accepted receipt |
| `EngineObservationSupersededException` | No observation authority | Preserve normal bounded backoff; do not evaluate or clear a pending failure from this exception |
| Normally ignored regressed observation | No new admitted chronology | Keep existing ignore behavior; retain pending failure and its original deadline; no evaluation, budget renewal or newly introduced integrity fault |
| Invalid/wrong-audience observation or ordinary provider failure | Untrusted/missing observation | Preserve the existing provider/integrity fault path; never reinterpret it as input retirement |
| Explicit host disposal/caller cancellation | Work owner retired | Cancel waiting and retire local work; late evaluation has no authority; preserve any unknown dispatched receipt |

No current-input failure becomes a rules loss, concession, draw, expedition
result or circuit score. The engine's stopped-match presentation stays as it is.

## Typed failure provenance

Add a small immutable failure descriptor in `Mana.AI`, reused by session history
and host diagnostics. Proposed fields are:

| Field | Meaning |
| --- | --- |
| `Kind` | Closed enum: capability, work limit, time limit, invalid output, policy exception, execution uncertainty, or no progress |
| `Code` | Validated existing bounded machine code, at most 96 ASCII letters/digits/hyphens |
| `Stamp` / `Task` / `EpisodeId` | Exact evaluated seat input, task family and existing causal budget identity |
| `RemainingWork` / `Elapsed` | Existing session accounting captured at result admission, never new limits |

`AiSeatView` and `AiDecisionRecord` receive an optional descriptor as additive
properties. Older replay JSON without it remains readable and explicitly lacks
typed provenance. `AiHostFault` receives the same optional property; its existing
top-level reason and the Java `ReportAutomationFaultAsync` reason stay compatible.
Do not send a private descriptor or exception through the multiplayer state.

Assign the descriptor inside the session while its frame and allowance are
owned. Validate codes once before retaining them. Keep `Detail` as existing
display/debug text, not a string to parse for control decisions. In particular,
do not infer recovery eligibility from an exception message or capability name.
Work exhaustion must be corroborated by the owning budget's accounting; local
optimization limits with an already valid answer remain the existing bounded
strategy behavior. The baseline's explicit whole-budget exits should return a
typed budget result (or a session-owned budget classification), not relabel an
arbitrary third-party capability string as computation exhaustion.
The `TakeCommand` handoff-budget path also creates this descriptor when a
previously ready proposal loses its allowance before submission. The host must
inspect that resulting state instead of treating a null handoff as progress.

Private host evidence includes the descriptor and whether confirmation found
`still-current`, `input-retired`, `seat-retired`, or `confirmation-unavailable`.
Keep those outcomes distinct from rules execution receipts. Use existing
diagnostic owners and bounded seat history, not a new telemetry service or
another complete snapshot store. The provider's existing private last-observation
capture remains evidence only and may contain hands; it must not become a public
release asset.

## Bounded pre-dispatch confirmation

This is Phase B, not part of the independently deliverable provenance change.
First add a controlled held-policy-result journey through the shipping
`AiTableHost` and actual `ForgeEngine`/Java owned observation route. While that
result is held, use an existing legitimate game action or lifecycle transition
to retire its input; release a valid unavailable result and retain the actual
erroneous `automatedFault` trace on the unchanged baseline. The relevant
state/decision must come from the real provider, not a fake port assigning a
new string. Hold an actual baseline policy failure on that captured input;
do not manufacture an unavailable capability that the production policy would
never return there. A fake-provider test then supplies cheap exhaustive timing controls.
If the real reproduction does not fail as hypothesized, Phase B is deferred;
Phase A can still be reviewed, tested and merged without a behavior change.

Each game may retain at most one `PendingPolicyFailure`. It contains the failed
seat, descriptor, exact decision ID, task, original observation watermark,
episode identity and the monotonic start time. It holds no proposal or command.
Create it only after an admitted `Unavailable`/`BudgetExceeded` result and before
`TakeCommand`; assert that this evaluation owns no submission.

On creation, stop new policy evaluations and host dispatches for that game in
the remaining batch. Withdraw its existing server priority consent through the
normal negative-control path. A withdrawal receipt is not confirmation: it can
only stop further authorized automatic work. Withdrawal uncertainty remains an
integrity fault. Other games, if ever supplied by the provider, retain their own
owners; there is no global policy fallback or unbounded per-seat pending queue.

The next ordinary serialized host discovery obtains a complete fresh batch.
Before any policy work for the affected game, validate it through the same
audience, state, execution and chronology checks used by ordinary admission.
Then classify the pending failure:

1. A successful complete membership batch no longer contains that exact
   game/seat, or its independently admitted state is finished/eliminated:
   retire that failure and let ordinary lifecycle retirement proceed. Absence
   from a partial, failed or superseded read does not count.
2. A valid current frame owns a different decision ID: retire the failed input's
   result, admit the new frame through the existing session, and let the normal
   next step evaluate it with existing causal accounting. Do not submit anything
   from the earlier batch or immediately chain a command from a control receipt.
3. The exact failed decision ID still belongs to the same game and seat: stop
   once using its original structured cause, even if board/offer/metadata
   revisions advanced. The first slice deliberately does not treat same-input
   enrichment as proof that a previously unsupported continuation is solved.
4. An ordinary ignored regressed read remains ignored and supplies no retirement
   witness. A malformed/foreign read preserves its existing integrity disposition.
   A superseded read supplies no facts and retains the pending entry. A valid
   transient busy/resolving frame with no decision also retains the pending
   entry: a temporarily absent prompt does not independently prove that the old
   decision cannot return. Only a later admitted different input or actual
   seat/game retirement discharges it.

Confirmation never reevaluates the failed frame. Its waiting bound is the
existing 45-second observation timeout, measured from the first failure and
never refreshed by polls, pacing, control acknowledgements or superseded reads.
Successful confirmation usually uses one further discovery. Repeated superseded
discovery may continue only until that fixed bound. On expiry, retain the
original cause plus `confirmation-unavailable` and stop through the existing
fault path. Do not increase the 30-second active decision allowance, 4,096 work
units, provider timeout, or campaign ceilings to hide a failure.

Check that fixed deadline before and after every confirmation await, including
priority withdrawal and the superseded-read catch. Link the remaining portion
of the original deadline into those waits; starting a provider's normal
45-second timeout must not extend the combined confirmation lifetime. Caller
cancellation/disposal retains its ordinary cancellation disposition. A lost
negative-control receipt remains unknown control authority, rather than proof
that cancellation successfully withdrew consent.

Clear the pending entry on confirmed retirement, final fault, actual game
retirement or host disposal. Table/viewer ABA cannot resurrect it. A new prompt
within the same root may itself be out of budget and enter confirmation; it
receives no new allowance from this feature. Existing no-progress tracking for
earlier accepted commands is not erased by a pre-dispatch policy failure.

This path protects an already retired input; it cannot repair a current missing
capability. It does not turn a previously tolerated stale observation into a
fatal provider error.

### Complete membership and cleanup ordering

`IAutomatedSeatProvider` returns an array, not a paged delta or a membership
certificate. Its existing host already treats successful returned membership as
the complete set for retiring sessions. Document and preserve that required
provider meaning; the array type alone is insufficient proof for a new provider.
No new completeness flag is inferred from a count, a missing decision, or source
action catalog coverage.

The shipping path supplies the required meaning concretely:

- `DesktopEngine.ownedAutomatedSeats()` selects the simulation, connected hosted
  table, or local table. `MatchTable.automatedSeats()` copies all registered bot
  sessions; eliminated seats are not filtered from this membership list.
- `automatedState` maps every selected session to `automatedObservation()` and
  returns only the completed list. An exception while building any row fails
  the request; it does not return the completed prefix. Each row independently
  fences its decision/source ownership in `decisionObservationValues()`.
- `ForgeEngine.ObserveAutomatedSeatsAsync` captures the process/world generation,
  decodes the entire array, rejects duplicate/foreign rows, and rechecks owner,
  generation and cancellation before replacing scopes and audience leases. A
  changed world or stale row fails/supersedes discovery rather than installing
  a partial membership snapshot.

This establishes complete membership for one accepted discovery, not a promise
that all sequentially projected seats have the same board revision. Keep that
distinction in tests. Test providers must return complete membership snapshots
under the same semantics. If another provider supplies partial/delta rows, it
already violates session retirement assumptions; adapt its contract before
enabling absence-based confirmation. Phase B must not add a provider type check
or silently accept missing rows as an empty result.

Resolve pending failure membership and validate any matching row before the
existing `RetirePriorityOwnersAsync(active)` and absent-session disposal loop.
For a present matching row, use the existing session's admission/ignore behavior
before calling a change a retirement. Do not dispose and reconstruct a seat to
obtain a favorable watermark. Its exact allowance, retained plan, submitted
receipt state and private history stay owned until the ordinary lifecycle
retires that session. For actual absence, first retain the immutable pending
descriptor/disposition with no command authority, then run ordinary revocation
and disposal, then remove the pending entry. If revocation fails, retain the
original descriptor as diagnostic cause and preserve the existing unknown-control
fault; do not turn failed cleanup into a successful recovery. No extra snapshot
copy or action capability survives actual seat disposal.

Observation of a regressed present row neither advances the watermark nor
disposes its session, clears failure evidence, resets the deadline, or grants
another evaluation. Deadline checks still run while ignored/superseded reads
continue. Normal same-root and restoration budget tests must keep their current
results, including when the pending failure passes through a null presentation.

## Qualification and content policy

Reuse `AiCampaignDecks`, `AiCampaignQualificationTracker`, the causal mechanic
reader, `AiReleaseEvaluation` and `MixedGameCampaign`. Existing code already pins
source deck hashes, workshop semantic versions, committed assignment handles,
actual slot/player/team bindings, configurations and runtime identity. It also
separates `not-instrumented`, `not-observed`, `inconclusive` and observed failures.
A second manifest registry or card-name strategy implementation is unnecessary.

Keep three separate claims: a legal deck can be selected, a policy can finish
sampled games, and an exact deck version is qualified for a supported mode.
Current beta selection stays available. No deck gains a qualified badge or
shipping-catalog promotion from this work. The existing formal roster still has
zero qualified lists. The first four campaign candidates and four LCI precons
remain candidates rather than eight qualified decks or balanced opponents.

A qualification record must bind:

- Exact deck composition/semantic version and source manifest; format, teams,
  participant count and each actual accepted assignment.
- Shipping engine, policy, planning and harness bytes plus bound resources and
  source candidate; policy seed, known uncontrolled rules randomness and limits.
- Required defining-mechanic witnesses from that deck's accepted action through
  owned costs, execution ancestry and authoritative effect; no other deck's
  generic board tag can satisfy them.
- Natural sampled game outcomes captured before cleanup, mechanical/privacy
  integrity, retained failure/cutoff evidence and structured policy failures.
- Human review of targeting, useful sequencing, pacing, and whole-session
  enjoyment against the exact candidate; explicit known unsupported interactions.

The next coverage work should implement missing defining verifiers before adding
more candidates. At this baseline `legal-owned-combat`, `balmor-spell-buff` and
`modal-multitarget-resolution` are required by manifests but absent from the
supported-verifier set. Even instrumented equipment attachment is not proof of
general equipment strategy. Fix witnessed mechanic/policy gaps, rerun their
cheap exact-decision replay and real continuation, then the bounded natural
sample. An accepted proposal or natural finish alone never marks all witnesses
passed. Qualify each deck/configuration separately, then promote only the scope
actually reviewed; M2's eight-list/six-family target stays a separate gate.

### Phase C verifier order and acceptance

Implement the three missing verifiers in this order. The order follows the
available baseline evidence: combat has current staged assignments and typed
combat damage; Balmor adds triggered ancestry and state deltas; modal multitarget
adds the largest choice/branch/recipient chain. Each is a separate reviewable
change in the existing qualification owners. These are narrow positive witnesses,
not proofs that every combat, trigger, mode or card interaction is supported.

1. **`legal-owned-combat` first.** Bind an accepted `CompleteAttackers` to the
   acting campaign deck, exact `Combat.DeclarationId`, turn, and at least one
   legal staged own attacker/defender pair from the authoritative before frame.
   Require the subsequent actual declaration event for that player and typed
   positive `PublicImpact` combat damage from that exact attacker/controller to
   its chosen opposing defender in the same combat. Use event IDs after the
   captured baseline; do not parse the declaration's prose or invent an
   execution ID for turn-based combat. This first narrow witness may remain
   unobserved when every attack is blocked or deals no damage. Negative controls:
   rejected/uncompleted declaration, another seat/turn/declaration, noncombat
   damage, another physical same-name attacker, changed defender, old activity,
   or no authoritative damage supplies no credit. Controlled positive and
   negative traces must preserve source/recipient identity and cost-free engine
   declaration legality. A real Rhys campaign-deck opportunity must let the
   production policy declare and continue through actual combat; observe the
   damage through the normal owned/public campaign reader. An accepted toggle
   or a controlled unit fixture alone is insufficient.
2. **`balmor-spell-buff` second.** The exact Balmor campaign source script reacts
   to its controller's instant/sorcery cast and grants owned creatures +1/+0 and
   trample until end of turn. Bind an accepted actual cast to a distinct resolved
   Balmor trigger through the existing causal ledger, its exact source/controller,
   and the triggering cast; capture current owned recipient baselines and the
   resulting +1 power/unchanged toughness and trample. Confirm the public state
   for the same occurrences. The controlled real-engine opportunity uses one
   otherwise unmodified non-trample creature, one eligible spell and no competing
   buff, then observes removal at end of turn as a separate lifetime check.
   Negative controls: opponent spell, non-instant/sorcery, another Balmor
   occurrence/controller, unrelated same-poll pump, countered/unresolved trigger,
   wrong recipient, pre-existing trample without a known baseline, or missing
   ancestry grants no witness. If the current projection cannot independently
   bind the trigger to its cast or attribute competing recipient changes, leave
   those cases uncredited and take the exact missing typed fact to the tech
   lead; do not infer causality from a shared budget root or card name. The
   real-engine positive must use the production cast policy and normal reader;
   script text defines the test expectation, never policy strategy.
3. **`modal-multitarget-resolution` third.** Capture one accepted cast from the
   actual Balmor manifest, its owned mode-choice replies, at least two committed
   distinct legal recipient occurrences, the corresponding committed stack
   operation graph, and the resolved effects on those recipients through exact
   execution ancestry. A controlled opportunity can use the manifest's Prismari
   Command with its damage and artifact-destruction modes and two legal distinct
   opposing targets. Preserve actual costs and let production policy choose
   modes/targets; the harness must not answer those choices to force the witness.
   Negative controls: one selected mode/recipient, cancelled/countered action,
   copied or unrelated execution, same-name replacement occurrence, foreign seat,
   unselected-branch graph, unaccepted reply, changed legal target and an unrelated
   nearby effect all fail to credit. Missing resolved branch or target linkage
   remains `not-instrumented`/inconclusive rather than inferred from prompt prose.
   A real normal-provider cast must complete both intended effects and feed the
   existing owned/public reader before the verifier enters the supported set.

For each verifier, register its ID only in the same change that adds a positive
control, independent identity/ancestry negative controls, and the actual-engine
mechanism witness. Preserve bounded evidence capacities, restoration handling,
missing-history status and duplicate-credit behavior. Then run the unchanged
bounded natural campaign; the verifier can be implemented while that exact deck
is still unqualified or its natural defining mechanic remains unobserved. The
publication must retain those separate conclusions. Phase C is not part of A/B's
execution authorization and expands Java projection only after an explicit
tech-lead boundary review of a demonstrated missing fact.

## Implementation ownership and sequence

| Phase | Files and owner | Deliverable and exit |
| --- | --- | --- |
| A: Independently deliver provenance | AI worker: `Mana.AI/AiSeatSession.cs`, `AiPolicy.cs`, `AiSeatHistory.cs`, `AiTableHost.cs`; add a small failure type only if needed; `Mana.Table/AiHostDiagnostics.cs`; related client regressions | Typed capability/work/time/invalid/unknown causes retained privately; unchanged terminal behavior and existing checks pass; mergeable without B or any new recovery behavior |
| B0: Reproduce stale failure | AI worker: existing actual-provider/host journey and controlled timing harness | An unchanged shipping host/provider path incorrectly faults a legitimately retired input after its policy result is held; exact trace and fresh actual decision retained; if absent, stop B and keep A independent |
| B: Confirm retired pre-dispatch failures | Depends on accepted B0; same worker: `AiTableHost.cs` and a focused partial only if it clarifies ownership; existing provider port; host/current-observation regressions | B0 passes with bounded confirmation; old unsent failure retired only by a successful fresh complete read; still-current failure stops once; no replay, budget reset or extra controller |
| C: Complete narrow qualification evidence | Separate follow-up owned by AI/content worker: existing campaign evidence/qualification files and focused real-engine journeys | Each missing verifier gets independent negative controls and actual causal evidence; existing records remain truthful; no automatic deck certification |
| D: Qualify a small reviewed roster | Existing campaign/runtime/package owners plus human review | Exact candidate complete varied mixed games and required per-deck mechanics; review scope and limits recorded; M2 completion claimed only when its entire gate passes |

Do not edit Java rules, wire schemas, lobby controls, deck defaults, layout,
live-update infrastructure, or the encounter scheduler for A/B. If current input
retirement cannot be witnessed using the existing provider contract, report that
boundary to the tech lead before expanding it. Root owns canonical architecture,
testing-guide integration, Git merges and any release claims.

## Verification and release gates

Extend `AiHostRegression` and `AiObservationSupersededRegression` or a narrowly
named companion invoked by the normal `Mana.Client.Tests` runner. Use controlled
policy/provider gates and a fake monotonic clock; do not wait 45 real seconds.
The oracle is the actual dispatch/fault transcript plus session history and
budget identity, not assertions against the new helper's fields alone.

Required A/B sequences:

- Held unavailable result, then actual new prompt: zero old dispatches/faults;
  only an explicitly fresh scoped proposal can later submit once.
- Same owned prompt with higher metadata/board/offer revisions: one final fault,
  no policy rerun, no invented pass and the original capability retained.
- Same-root nested/replaced input: exact original seed/work object and consumed
  active time survive. Exhausted work/time stays exhausted across confirmation.
- Finished game, eliminated seat, absent seat, replacement game and game/seat ABA:
  old failure cannot fault a new owner or preserve private seat state.
- Invalid answer/plan, malformed identifier and thrown policy: no confirmation
  detour; current terminal contract and private-message suppression remain.
- Unknown receipt and accepted-no-progress: actual receipt classification is
  retained, no extra execute call, no confirmation-to-replay path.
- Repeated superseded reads: no evaluation or fault before the fixed bound;
  expiry faults once; neither supersession nor controls reset the deadline.
- Cancellation/disposal/late reply during confirmation; held negative priority
  receipt; diagnostic sink failure; repeated host pumps after settlement.
- Two seats in one game cannot dispatch from a captured batch after a pending
  failure blocks that game; retirement leaves the next normal current step usable.

Run the normal graphics-free client suite and existing priority/receipt/budget
regressions. They already run in Windows native CI; wire any new cases into that
runner, without adding a competing service or campaign. Use existing native
provider/whole-seat journeys if the provider boundary changes. Reuse the current
natural mixed-game qualification (four rotations, 160 turns/180 seconds each)
and existing real 110-trigger/mana checks for the final integration candidate.
Preserve first failures/cutoffs and exact engine/package provenance.

Human review remains pending. A/B adds no controls, gesture, arrangement,
controller takeover, timeout prompt or new outcome screen: a naturally retired
input continues through the existing table, while a current unsupported input
keeps the existing stopped-match behavior. Existing source-attached reading and
seat/Auto gestures follow the [shared player-feedback rules](Mana-Table-Experience-Review.md#design-rules-learned-from-player-feedback).
Any later manual takeover/retry UI needs its own complete player journey and
observed game precedent before implementation.

Tech lead acceptance requires source review of every terminal and retirement
path, meaningful negative controls, applicable exact-head CI, honest unchanged
qualification counts and no weakening of execution uncertainty. Release remains
subject to the repository's PR/master checks and separate exact-candidate human
review. Automated counts do not establish enjoyable AI or universal card support.

## Tech lead review resolution 16 November 2026

- Phase A is explicitly an independent provenance deliverable with unchanged
  gameplay failure disposition. Phase B is gated by B0's failing actual
  shipping-host/provider held-result journey; fake membership changes alone
  cannot open its execution gate.
- Complete membership is grounded in Java's all-selected-session collection and
  ForgeEngine's all-row decode plus process/world admission. It does not claim
  a shared board revision or treat an interface array as a certificate. Pending
  failure evidence is resolved before ordinary absent-seat cleanup; retained
  sessions preserve their exact budgets and histories.
- Ordinary ignored regressed reads stay ignored. Null/busy presentation does
  not prove failed-input retirement. Malformed/integrity failures retain their
  existing disposition; no stricter provider policy is introduced incidentally.
- Phase C orders combat, Balmor buff, then modal multitarget by the available
  typed evidence and specifies positive, negative and real-engine acceptance for
  each. Required projection facts that are absent remain explicit blockers.
- The technical lead accepts Phase A as the first execution scope. Phase B
  remains gated on its actual-provider reproduction; C and D remain separately
  reviewed qualification work. Production changes wait for the five-design
  ownership review. Human gameplay review remains pending.
