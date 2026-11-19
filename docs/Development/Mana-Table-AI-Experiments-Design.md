# Measuring Mana Table AI experiments

**Status:** Reviewed design with a first implemented development runner. V1
supports compatible registered variants, exact replay comparisons, isolated
natural-game batches and descriptive private reports. Confirmation-grade
statistics, a human-review UI and the later capabilities below remain future work.

**Objective:** Stronger casual Commander play, as selected by the project owner.
Measure purposeful decisions, tactical errors, robustness, game results and
decision cost separately. Human enjoyment remains a reviewed product outcome.

**Baseline source inspected:** `9d06b04a7e886e6a602ab4ced03a6714a9436879`.
Build on the [AI framework](Mana-Table-AI.md),
[reliability design](Mana-Table-AI-Reliability-Design.md) and
[existing evidence gates](Mana-Table-Testing.md#shared-attachment-capability-checks).

## Running the first implementation

Use the [runnable specs and instructions](../../mana-native/experiments/README.md)
after building the Windows test harness. The registry exposes full v6
and its distinctly versioned attachment-evaluation ablation. It is a closed
experimental seam, not a new player preference or arbitrary historical DLL loader.

Plan writes a strict spec copy, exact seed strings, artifact hashes and the full
randomized schedule. Run reserves immutable first attempts, checks their input
closure before launch, and verifies atomic result/artifact receipts. Analyze
recomputes deterministic reports from retained evidence with the locked analyzer;
the original engine and driver need not remain installed for that analysis.
V1 supports sequential execution and optional `--max-jobs` execution chunks.
An incomplete chunk retains every unrun cell in its denominator.

V1 reports family-weighted paired grades, corrections/regressions, natural
outcome bounds, faults, missing measurements and scoped wall-time/work samples.
Coverage and causal-consequence fields remain explicitly unavailable where no
verifier exists. Reports have no confidence intervals or general strength claim.
The example captures are known development regressions, not an unseen holdout.

Application/input files and runtime launchers are hash-pinned. The actual worker
records its selected .NET framework, architecture and OS; shared runtime libraries
and the OS are version-observed rather than a hermetic image. Source metadata is
an assertion unless independently bound by clean-build/CI receipts. Final-source
release checks remain separate.

Root review required corrections before integration: measurement callbacks now
enqueue bounded immutable records and perform no serialization/disk I/O; the
owner drains outside policy evaluation and command-receipt callbacks. A later
sink failure cannot change a known accepted acknowledgment. Native admission
parses and hashes the same lock bytes, claims attempts exclusively, and accounts
for result and artifact-manifest bytes inside the same capture quota. Coordinator
failures preserve uncertain ownership rather than launching replacement games.

## Decisions

1. Use four complementary evaluation layers: exact decision comparisons,
   controlled rules continuations, natural games and blinded human review.
2. Each experiment freezes a baseline, candidate, hypothesis, primary endpoint,
   limits, data versions, assignments and analysis before evaluation.
3. Begin with compatible policy variants in one build and existing production
   decision/host/provider paths. A separate coordinator owns experimental batches.
4. For natural A/B play, change one focal seat against three frozen references.
   Balance deck and physical seat; randomize execution order.
5. Preserve every scheduled job, attempt, failure and unresolved outcome.
   Completion-only results cannot be the headline strength estimate.
6. Report a scorecard and disagreements, rather than one weighted AI score.
   Experiment evidence does not replace required release qualification.

## Baseline seams and initial gaps

This table records the starting point inspected for the design. V1 adds separate
experiment owners while retaining the existing qualification runners.

| Existing owner | Reuse | Current gap |
| --- | --- | --- |
| `Mana.AI/AiDecisionReplay.cs` | Exact entitled frame, history, plan/version, seed and remaining allowance; production session and scope validation | Current command selects one hardcoded policy; no A/B corpus evaluator |
| `Mana.AI/AiTableHost.cs` | Existing `Func<int, IAiPolicy>` per-player factory and whole-seat execution | Experiment composition must bind registered variants to accepted player IDs |
| Configured simulation ports | Authoritative four-deck setup and slot-to-player receipts | Batch scheduling, fixed opponents and experimental assignments |
| `AiCampaignDiagnostics` and qualification evidence | Proposed/accepted commands, ordered observations, causal witnesses and findings | Comparative metrics, independent grades and some explicit coverage counters |
| `AiCampaignOutcomeProof` | Typed terminal outcome captured before cleanup | Immutable per-attempt ledger and unresolved-outcome analysis |
| `AiReleaseEvaluation.VerifyRuntime` | Source/artifact identity checks | Development experiment identity without claiming packaged release qualification |

`AiEngineCampaign` and `MixedGameCampaign` currently construct
`CasualCommanderPolicy` directly. The existing four mixed-game rotations use two
scripted clients and two AI seats. They establish bounded completion and
integration properties; the earlier three-of-four and later four-of-four
completion results are not an A/B playing-strength experiment.

The experiment coordinator must not turn the release campaign into an unbounded
research loop. Existing release budgets, assertions and CI triggers stay intact.
V1 can use the Windows `Mana.Table.Tests` harness. A graphics-free cross-platform
runner is a later extraction, not an initial compatibility promise.

## Evaluation layers

| Layer | Unit and method | Supported conclusion |
| --- | --- | --- |
| Frozen decisions | A and B receive the same original frame/history/plan, seed and allowance | Which local decisions improve or regress on this corpus |
| Controlled continuations | Actual policy proposal, actual provider submission, actual rules consequence | A declared mechanic or tactical opportunity is handled correctly |
| Natural games | One focal policy against a frozen reference population, balanced by deck/seat | Outcome and robustness differences for this benchmark distribution |
| Human review | Blinded, sampled sequences with a versioned rubric | Perceived competence, clarity and casual-play quality on reviewed sequences |

A replay is an off-policy local counterfactual: the candidate might never reach
the source policy's state, initiate its cast or retain its plan. It is not a game
value estimator. Capture fresh observations from both live arms to inspect this
distribution difference. Do not inject new semantics into an old capture and
call it the same input.

An offline proposal has no execution receipt. An accepted command is not an
observed consequence. A verifier must join the actual source, execution,
recipient and subsequent rules facts before granting consequence credit.
Existing manually targeted Java journeys and native offline proposal parity
remain useful separate evidence; they do not become live policy dispatch merely
because their target keys match.

## Experiment types and variant identity

**Policy experiment:** Provider, resources, adapter, contracts, environment and
granted budgets are fixed. Only the declared policy implementation/settings vary.
Examples: attachment valuation ablation, removal timing or a bounded planner.

**System experiment:** Provider semantics, adapter or several runtime components
also change. The report attributes results to the complete treatment. This is
the correct classification for incompatible old/new semantic pipelines.

V1 uses a closed code registry: `factoryId`, versioned descriptor and a typed,
validated settings object. Reject unknown keys, duplicate keys, nonfinite values
and unsupported variants. Hash canonical settings using a versioned serializer.
Do not use arbitrary type names, executable expressions or silent default-policy
substitution. The variant identity includes settings, code and dependency hashes.

A constructor/factory seam for immutable settings is required; current hardcoded
weights are not already configurable. A fresh policy instance and independent
seat history/plan are constructed for every seat/game. Bind physical slots using
the accepted configured-simulation receipt before starting the host. Never assume
that slots 0–3 are actual player IDs 0–3.

Initially register A and B in one compatible build. Retaining a baseline may mean
freezing a versioned implementation with golden behavior checks. A v5 policy
ported to current contracts must be named as a port; it is not the old binary.
Arbitrary historical `Mana.AI.dll` files cannot simply be loaded beside current
contracts, particularly across the internal Attach/DecisionSource additions.
Cross-build policy worker IPC needs separately reviewed serialization, budget,
plan-transition and cancellation semantics.

## Benchmark sets and independent grading

Maintain three versioned sets:

- **Development:** Known regressions and exploration cases, including Rancor.
  Use these freely for tuning and debugging.
- **Confirmation:** Frozen unseen opportunities and deck families. Choose the
  candidate on development data before opening these results.
- **Safety:** Must-pass authority, privacy, required-answer and scoped-command
  properties. Every experiment runs these regardless of its tactical hypothesis.

A case contains an immutable original replay or a real fixture recipe, provenance,
task/mechanic/deck-family tags, origin game/episode, expected acceptable behaviors,
an independent evaluator ID/version and explicit grading coverage. Expectations
can define a set of acceptable targets or actions; they need not name one
arbitrary optimal move. Predicates use typed facts and known permissions, not
card-name/prose interpretation or the candidate's own estimated utility.

Automated grades are `pass`, `fail`, `ungraded` or `incompatible`. Strategic
ambiguity stays ungraded or goes to review. Reason codes and heuristic campaign
findings are diagnostic labels, not a ground-truth blunder judge. Preflight
rejects a declared arm that cannot consume a corpus's contract; incompatible
frames must not silently vanish from one arm's denominator.

Freeze grading eligibility from the original case independently of either arm's
proposal. The primary paired comparison uses the same planned eligible cases in
both arms. A missing response, unavailable required answer or failed grade in
one arm cannot be dropped to improve that arm's rate. Report its reason and
mark the affected primary comparison incomplete until the declared handling
applies. Neither arm may redefine eligibility after seeing its own action.

Group related captures, renamed cards, seed variations and the same originating
game/interaction family together when splitting data and estimating uncertainty.
Ten renamed copies are robustness checks of one situation, not ten independent
strategic discoveries. A holdout failure promoted into a regression spends that
holdout; version and replace it for future confirmation.

## Scorecard and denominators

| Dimension | Measures | Required interpretation |
| --- | --- | --- |
| Tactical judgment | Acceptable choices and serious-error rate on independently graded opportunities; A-fail/B-pass versus A-pass/B-fail | Show graded, ungraded and incompatible counts; classify severity with evidence |
| Natural strength | Focal win share, strict sole-win rate and natural draw rate by pod/deck/seat | Preserve intended benchmark weights and unresolved-outcome bounds |
| Reliability | Policy faults, invalid proposals, unavailable required decisions, rejections, cutoffs and evidence loss | Counts per eligible episode and per started game; preserve failure attribution |
| Coverage | Relevant opportunities, modeled mechanics, assessed candidates, bounded-search limits, unassessed cancellations | Missing telemetry is unknown, not zero; full-game opportunity mix can change between arms |
| Cost | Active policy time/work, end-to-end response time, engine/transport waits and process resources | Separate task, episode, cold/warm state, capture mode and concurrency |
| Casual quality | Blind sequence ratings and reviewer agreement | Purposeful play, response to threats, avoidable errors and enjoyment remain distinct |

The primary endpoint must be named before the run. For the first feature
experiment, use independently graded acceptable-decision rate on held-out tactical
opportunities, with serious-error avoidance, natural outcomes, coverage,
reliability and latency as secondary evidence/guardrails. Include both useful
action opportunities and harmful-action avoidance; freeze family weights and
grade the declared local objective without claiming a globally optimal move.
A strength study can instead name focal natural
win share as primary. The analyzer cannot choose whichever endpoint improved.

Do not reward raw cast count, mana expenditure, low cancellation frequency or
equal pressure on all opponents. A forced self-target can be correct; an
optional cancellation can be prudent. Conversely, refusing every unfamiliar
card can reduce visible blunders while making the bot useless. Include positive
opportunities, development and natural outcomes alongside caution metrics.

For live human review, preselect random/stratified sequences independent of the
variant's result; retain inclusion probabilities. Present A/B under shuffled
blind labels, with the actor's entitled information at the decision boundary.
Reviewers first grade the decision without future draws/outcomes, then may grade
the later sequence separately. Retain reviewer identity, uncertainty and
disagreement. Hidden opponent truth cannot retroactively justify a choice.

## Natural A/B schedule

V1 defaults to four-player free-for-all Commander, with one focal A/B seat and
three instances from a frozen reference policy roster. The reference policy does
not switch when the focal treatment switches. Policies receive no opponent
variant labels or privileged deck contents. Own-deck knowledge and disclosures
retain their normal visibility rules.

For one fixed pod of four decks, cross focal deck with physical seat:

```text
4 focal decks × 4 physical seats × 2 treatments = 32 games per balanced block
                                                   16 A games + 16 B games
```

With decks D[0..3], focal deck d and focal slot s, set rotation
`r = (d - s + 4) % 4` and place `D[(j+r)%4]` in slot j. This rotates the complete
pod, places each focal deck in every seat, and holds opponents' deck versions
fixed within each A/B setup cell. This is cyclic seat coverage, not all six
possible relative opponent orderings.

Freeze the randomized/interleaved execution order before starting. Randomize A/B
order within each setup cell. Counterbalance machines/concurrency identically;
never put all A trials on one machine and B on another. Record actual starting
player independently: physical seat rotation does not prove equal starting
player exposure when setup cannot control it.

Repeat whole balanced blocks and add predeclared pods for broader studies. The
first 32-game block is a feasibility pilot measuring cost, completion, coverage,
artifact volume and descriptive heterogeneity. It cannot estimate empirical
between-block variance for the planned interval method; repeated independent
blocks are needed for that estimate and confirmation sizing. It does not
establish a small win-rate gain.
The existing four campaign decks are development data; later confirmation needs
additional, independently chosen deck/interaction families and frozen weights.

All-A/all-B tables, 2A/2B leagues and cooperative team games answer different
questions and need separate experiment types. They are not pooled into this
focal-seat estimate. Elo or exploitability is not a default scalar for this
four-player, opponent-population-dependent objective.

## Randomness and repeatability

There are separate planning, policy, provider, identity and timing sources.
Current configured simulation exposes no production rules-seed control.
`forge.util.MyRandom.setRandom` exists but changes a JVM-global provider.
`MatchIdentities` also creates fresh UUIDs, while policy tie-breaking hashes
occurrence identities. Equal policy seeds therefore do not guarantee equal
shuffles or even equal tie-breaking across newly created worlds.

V1 supports exact same-frame decision comparisons and randomized, blocked
independent natural games. It makes no identical-shuffle paired-game claim.
The locked schedule derives policy seeds from stable logical assignments using
a versioned algorithm, independent of arm, queue order and results. Keep exact
per-seat and per-episode derivations in the evidence.

NEW manifest seed fields use signed decimal strings checked against the .NET
64-bit range. Existing replay integers retain their original schema and bytes;
Node must not parse/rewrite them through lossy JavaScript numbers. Planning and
bootstrap seeds are separate from policy seeds.

Future test-only setup can seed the rules provider before creating one isolated
game, with an audit of identities, other RNGs, clocks and scheduling. First label
that capability `engine-seeded`. Stronger repeatability needs a declared scope,
repeated normalized event/outcome comparisons and controls. Different policies
can consume the same random stream differently. Do not weaken live identity
revocation or reveal hidden ordering to make a benchmark repeatable.

## Outcomes, missing results and analysis

For a verified natural FFA terminal outcome, define focal score S as `1/k` when
the typed Win outcome lists the focal player among k winners; otherwise S is zero.
A verified Draw scores zero under this explicit v1 win-share convention and is
also reported separately. `MatchOutcome` Draw contains no winner list; never
infer draw participants, placement or elimination causes from life totals/prose.
Team experiments later score the focal team once using typed winning-team facts.

The strength estimand is candidate minus baseline expected score, standardized
to the frozen pod/deck/seat distribution. Also show per-stratum results and strict
sole-win rate. Four seats' results and thousands of decisions in one game are
not independent game samples. Repeated evaluations of one frame are not new
strategic situations.

Persist distinct statuses: not launched, setup failure, natural terminal,
time cutoff, turn cutoff, policy failure, provider/transport failure, operator
stop and evidence-integrity failure. Keep causal attribution separate from
terminal status: a completed result can coexist with a reliability finding.
A cutoff is unresolved, not a draw. A policy fault is adverse reliability
evidence, not an invented rules defeat. Never concede to manufacture completion.

Report scheduled, started, naturally completed and analyzable counts by arm and
stratum. Completed-only win share is descriptive and can be selection biased.
For each arm/stratum, with observed terminal-score sum O, unresolved count U
and fixed denominator N, show the missing-outcome bounds:

```text
lower = O / N                         upper = (O + U) / N
candidate-minus-baseline bounds = [lowerB - upperA, upperB - lowerA]
```

Calculate both planned-job bounds (including unstarted jobs as unresolved) and
started-job bounds; then apply the prespecified stratum weights. This is a
worst-case missing-result range, not a confidence interval. Show sampling
uncertainty separately. Missingness must not improve the headline denominator.

The initial analyzer uses a fixed sample/stopping plan and a versioned,
stratified complete-block bootstrap for natural-game differences. Resample
independent pod-repetition blocks while preserving their A/B cells; report the
unit, weights, method and analysis seed. Do not bootstrap thousands of decisions
as independent games. A single 32-game pilot block supports descriptive results,
not an empirical between-block interval. The implementation must validate finite-
sample coverage and a minimum usable block count with synthetic known effects;
until that method gate passes, confirmation-grade intervals are unavailable.
Replay comparisons similarly preserve origin-game/mechanic-family clusters and
pair A/B on the same cases.

V1 withholds a confirmation-grade strength interval whenever any primary planned
job lacks a verified terminal outcome or required evidence. Report the missing-
outcome bounds and completion/failure counts instead; do not attach a completed-
only confidence interval to the intended full-schedule estimand. Estimating
sampling uncertainty for bound endpoints is a later, separately validated method.

Sample size and practical regression margins belong to each confirmation plan.
For scale only, assuming independent Bernoulli arms near 25%, approximately
`1.96² × (0.25×0.75 + 0.25×0.75) / 0.05² = 576.24` games per arm gives a
normal-approximation five-percentage-point interval half-width on the difference.
This is not a power calculation or a Commander sample-size guarantee; draws,
blocking, heterogeneity and missing results change it. Use pilot estimates and
the desired precision to budget a later study.

Freeze sample size, primary endpoint, minimum meaningful effect, regression
margins, exclusions, interval method and endpoint hierarchy before confirmation.
Progress dashboards remain descriptive. No repeated significance peeking,
optional extension until favorable or silent removal of bad games. Search many
variants on development data, then confirm the chosen one on fresh holdout data;
simultaneous confirmatory comparisons need a declared multiplicity method.
Future sequential testing needs a separate reviewed valid stopping method.

## Runtime and evidence ownership

One natural-game worker owns one native process, one Java process and a fresh
profile. This avoids cross-game JVM-global state and provides a process cleanup
boundary. Resource concurrency and capture mode are frozen inputs. `jobs=1` is
the reference timing mode; parallel throughput results form a separate timing
class. No production profile or live player table is used.

Reuse the production policy/session/host and configured simulation ports. A
collector can read private test evidence but cannot pass observer knowledge into
policy requests, discover extra actions or mutate rules. Experiments grant the
same declared decision time/work to both arms; larger budgets are a distinct
resource treatment. Game limits remain explicit experimental settings and do
not alter existing release settings.

Current `AiDecisionRecord.Elapsed` is cumulative within an episode. Do not sum
all refresh/submission records as independent CPU time. Record evaluation-attempt
spans plus unique episode totals and starting/remaining work; use real causal
root IDs where present, and label legacy input/combat fallback grouping. Report
active policy p50/p95/p99 by task and episode separately from host latency,
engine/transport wait, pacing, startup and cleanup. Cold/JIT/model warmup and
steady-state timing have separate declared samples.

V1 does not promise exact per-decision CPU or allocation attribution. Whole-worker
CPU/peak memory can be sampled with the method recorded; per-decision profiling
needs an appropriate profiler, particularly across asynchronous thread changes.
Within replay timing runs, alternate A/B order, fix warmup and repetitions, and
separate instrumentation modes. Logs and startup cannot masquerade as strategy
computation. Full-game timing reflects each arm's different workload mix.

Metric queues, per-game traces, private replays, critical-failure reserve and
total disk have explicit finite caps in the lock. Critical failures are retained
before optional samples. Routine capture uses a declared stratified/reservoir
rule and records omission counts/probabilities. A full or failed sink must not
block gameplay indefinitely. Lost evidence makes affected metrics unavailable;
it is never zero errors. If required outcome/authority evidence cannot be
committed, stop that worker with an evidence-integrity result and retain the
partial record. Safe cancellation/retirement still cannot undo an already sent
rules command.

Resume launches only never-launched scheduled jobs by default. An interrupted
game is an interrupted attempt, not a saved game. Any allowed infrastructure
replacement has a predeclared reason and new immutable attempt ID; retain the
original, link the replacement and disclose its denominator treatment. V1 uses
first attempts for primary summaries. Replacements are diagnostic, so no favorable
rerun can erase a loss, cutoff or fault.

Exactly one coordinator holds an exclusive lifetime-owned lock for an experiment.
It durably reserves a job/attempt with an ownership token before spawning its
worker. A crash between reservation, process start and receipt commit leaves
`reserved/launch-unknown`, not an unstarted job eligible for automatic relaunch.
Reconcile the reservation, worker identity and immutable receipts first. Orphaned
ownership needs explicit recovery; a reused PID or expired timestamp alone does
not prove that a worker stopped. Concurrent run/resume commands fail cleanly
rather than duplicating a scheduled cell.

## Record design and V1 interfaces

The record table describes the full target design. Implemented V1 accepts the
strict subset in `ai-experiment-contract.cjs` and the runnable examples; it does
not accept unsupported confirmation settings or claim those analyses exist.

| Record | Required contents |
| --- | --- |
| `experiment-spec/1` | Hypothesis, treatment scope, stage, variants/settings, primary/secondary endpoints, grading rubric, strata/weights, sample/stopping plan, exclusions, practical margins, limits and privacy/capture policy |
| `experiment-lock/1` | Spec bytes/hash, source and artifact dependency hashes, deck/case/reference versions, provider capabilities, environment, randomization algorithm/seeds, full planned schedule and analyzer identity |
| `attempt/1` | Job/attempt/variant IDs, actual seat bindings, capabilities, timestamps, setup receipt, limits, stop reason, outcome proof, fault/evidence status and artifact hashes |
| `decision-measure/1` | Game/seat/variant, scope stamp, episode/task, exact seed, budgets/spans, typed proposal, coverage counters, receipt/execution links, grading/evaluator provenance |
| `experiment-report/1` | All denominators, weighted effects and uncertainty, missing-result bounds, disagreements, stratified metrics, resource costs, review state, exclusions and evidence links |

Unknown and absent metrics remain distinct from zero. Schemas reject unknown
required versions and nonfinite/out-of-range data. Validators impose bounded
input sizes/depth, resolve only locked local artifacts, and do not evaluate text.
Analysis consumes retained data without calling the engine or policy.

Implemented Windows harness commands:

```text
Mana.Table.Tests --ai-experiment-plan <spec.json> --profile <fresh-directory>
Mana.Table.Tests --ai-experiment-run <locked-directory> --jobs 1
Mana.Table.Tests --ai-experiment-analyze <locked-directory>
```

The coordinator alone invokes the implemented internal worker entry:

```text
Mana.Table.Tests --ai-experiment-worker <lock.json> --job-id <job>
  --attempt-directory <reserved-directory> --reservation-token <token>
```

The worker verifies the lock, reservation, exact artifacts and assignments before
creating a table. Its atomic `result.json` plus `artifacts.json` receipt is the
outcome protocol. Exit 0 means a complete committed worker record, which may
describe a cutoff or policy failure; it does not mean the AI passed. Exit 2 means
preflight refusal before a game, and exit 3 execution/evidence failure with an
error record where possible. A killed worker or missing receipt is interrupted/
unknown regardless of exit status. The coordinator uses typed results to stop
on critical safety findings and the analyzer preserves every attempt.

The planner validates compatibility, hashes resources and emits the lock and
schedule before any trial. It estimates required trial count/disk and records
budget decisions. The coordinator uses repository CommonJS tooling for process
orchestration and analysis, leaving .NET policy evaluation and Java rules intact.
Experiment settings cannot enter ordinary shipping matches implicitly.

```text
experiment/
  spec.json
  lock.json
  schedule.jsonl
  ledger.jsonl
  jobs/<job>/attempt-0001/
    manifest.json
    result.json
    artifacts.json
    private-decisions.jsonl.gz
    pre-cleanup-public-state.json
    replay-cases/
  analysis/<analysis-id>/
    summary.json
    report.md
```

Attempt results commit atomically after evidence hashes are checked; the
coordinator appends receipts to the ledger. An uncertain write is reconciled,
never blindly replayed. Later analyses receive new identities and retain earlier
reports; the original locked analysis remains the confirmatory result. Detailed
reports/captures are host-private. A separately generated public aggregate must
exclude private deck/hand data, faces, action handles and revealing labels.

The report begins with the hypothesis and allowed conclusion: tactical improvement,
strength improvement, regression, inconclusive or experiment invalid. It shows
A/B disagreements, links to exact replay/continuation evidence, coverage gaps,
resource tradeoffs and human review. A mock report must say it is illustrative;
it cannot present invented measurements as an experiment result.

## Implementation sequence and acceptance

| Slice | Files and owners | Exit evidence |
| --- | --- | --- |
| 1. Exact comparisons | New `AiExperiment*` helpers/CLI in `Mana.Table.Tests`; strict contracts/orchestration under `scripts/mana-table`; small registered policy factory/settings seam in `Mana.AI` | Same-input A/B report, exact large seeds, independent grades, immutable identity, A/A control and deliberately bad-policy positive control |
| 2. Natural pilot | Separate experiment game worker/coordinator reusing configured simulation, host and outcome proof | Complete balanced 32-game schedule, validated actual-player assignments, fixed references, first-attempt accounting, cutoff/fault/retry controls |
| 3. Measured coverage and review | Bounded explicit policy metrics, independent mechanic verifiers, private review queue and comparative report | No grading from policy score/reason strings; no double-counted episodes; sampled review and omitted-evidence accounting |
| 4. Confirmation and scaling | Frozen additional pods/cases, validated analysis and a sized plan | Prespecified primary result/intervals, practical margins, leakage controls and auditable conclusions |
| Later capabilities | Test-only deterministic setup, isolated cross-build policy ABI or external models, cross-platform extraction | Separate repeatability, visibility, budget/cancellation and artifact-compatibility qualification |

The lead reviews each slice before assigning isolated execution branches. Shared
contracts/registry are frozen first. Specialists can then own the replay/evaluator,
natural worker, and analysis/evidence paths with explicit integration boundaries.
The first implementation covers the V1 paths described above. Later statistical,
coverage, review and runtime capabilities retain their separate exit criteria.

Required regressions include wrong artifact/settings/version refusal; preservation
of exact 64-bit seeds; slot/player mismatch; no cross-seat knowledge; stale/illegal
proposal rejection; required-answer failures; nonrenewing episode budgets; correct
typed draws and shared wins; pre-cleanup outcome capture; evidence sink failure;
first-attempt retention; safe interrupted resume; all-scheduled/started bounds;
no completed-only selection; concurrent-coordinator refusal; crashes between
reservation, spawn and receipt; A/A agreement on fixed frames with sufficient budget;
A/B label swaps changing only comparison direction; and reproducible analysis
without an engine. New tests join existing CI owners; long research batches are
explicit developer/manual jobs and cannot replace required release checks.

Natural A/A controls verify identical artifact/config identities, balanced
assignments, budgets, lifecycle and accounting. They do not require equal outcomes
or tie choices across independent newly created games.

## First experiment and decision rules

The first component experiment uses these explicit compatible variants in one
artifact set:

- **A, experimental control:** current v6 with attachment evaluation disabled
  through a newly implemented, explicit experimental ablation setting.
- **B, candidate:** full current v6 attachment evaluation.

Keep the provider facts, cancellation/mandatory-answer safety and granted budgets
identical. The ablation changes the evaluator's use of attachment facts; it does
not edit observations or claim to reproduce shipped v5. This is an experimental
control for measuring one component, not a proposed weaker shipping policy.
The question is whether direct attachment understanding reduces independently
graded target mistakes while preserving useful supported actions. Pure mistake
avoidance alone could tie if A cancels everything, so include positive appropriate-
action opportunities in the primary acceptable-decision rubric and report that
stratum explicitly. Keep the old captured v5 Rancor case as known regression
evidence with its original bytes. Incompatible old/new pipeline comparisons are
separate system experiments.

1. Run the safety set, paired development opportunities and blind confirmation
   cases. Include beneficial/harmful attachments, duplicate keywords, known
   harmful targets, unsupported modifiers, cancellation and mandatory answers.
2. Execute short actual-policy rules continuations for changed supported choices.
   Retain proposed, accepted and causally observed stages independently.
3. Run A/A worker controls, then the 32-game balanced A/B pilot with one frozen
   development pod. Review all unresolved games and sampled A/B disagreements.
4. Publish feasibility and tactical results. Use measured runtime and repeated
   pilot blocks' variance to choose a larger, separately frozen confirmation
   plan and additional pods.

Initial engineering criteria are concrete: no newly observed privacy, authority,
current-scope or mandatory-answer failure; no new failure in the frozen must-pass
serious-error corpus; unchanged granted budgets; and at least one preregistered
tactical correction with an independently expected real consequence. These
criteria justify a bounded behavioral fix, not a claim of general strength.

A confirmatory quality/strength claim additionally needs its named primary
endpoint to meet the frozen practical/statistical rule, with secondary guardrails
assessed at their predeclared margins. An imprecise result is inconclusive,
not evidence of equivalence. Universal win-rate or latency margins are not
invented here: those product tradeoffs must be set before the relevant study.
Human casual-play acceptance and existing release permissions remain separate.

## Review decisions and methodology references

Architecture review accepted the existing per-seat factory and rejected a host
redesign, arbitrary historical DLL loading and live-identity stabilization.
Statistics review accepted the focal 32-game pilot and rejected four-game
strength claims, completed-only comparisons, synthetic draw participants and
universal invented regression margins. The lead reconciled outcome scoring with
the actual `MatchOutcome` contract and separated engineering acceptance from
confirmation of playing-strength improvement.

Controlling deck/seat factors while randomizing execution order follows the
blocking principle in the [NIST experimental-design handbook](https://www.itl.nist.gov/div898/handbook/pri/section3/pri332.htm).
The proposal to show uncertainty and distributions rather than rankings from a
few point estimates is informed by [Agarwal et al., Deep Reinforcement Learning
at the Edge of the Statistical Precipice](https://arxiv.org/abs/2108.13264).
Our unit definitions, missing-outcome handling and casual Commander scorecard
are project design choices; those references do not establish a ready-made
four-player Magic metric.
