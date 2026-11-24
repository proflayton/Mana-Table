# Unpriced uncertainty in opponent-decided effects

Separate proposed experiment. Controlled real-provider fixtures reproduce a mandatory choice in which v6 at 12 life selects an unquantified life-loss branch and is eliminated by the actual 28-life loss. The alternative lets an opponent draw one card. This design records the missing facts and bounded next experiment; it does not claim an implemented policy fix.

The actual card script is `forge-gui/res/cardsfolder/p/palantir_of_orthanc.txt`:
its GenericChoice offers controller Draw1 or controller MillX followed by the
chosen opponent losing Y life. X reads the source influence counters; Y is the
mana-value sum of remembered milled cards. The existing Forge AILogic hint is
not a native policy capability and must not become one.

Ignored diagnostic cases use real MatchTable/MatchSession/RulesScenario APIs.
The fixture helper is a renamed copy of RulesScenario, changing only registered
starting life before the match starts. Synthetic private deck setup is separate
from every native policy input. Every deciding owned frame asserts that ALL
players' library face arrays are empty. No user live-game action was taken.

There are eight actual owned frames: starting life40 or12, at successively
observed influence1,2,3,4. Default v6, diagnostic seed17, empty authored history,
10s/4096work, proposes current index1 in every frame. First-three Draw choices
are explicitly **setup overrides**, retaining starting life for the fourth case;
they are not reported as AI choices or a natural campaign.

At influence4 the exact native v6 proposal is dispatched through the actual
provider: at40life it loses28 and remains at12; at12life it loses28 and is
eliminated at-16. These are actual same-execution public life/resolution facts,
not forecasts or whole-match outcomes. The private controlled library contents
explain the test consequence but were never in the acting policy input.

`evidence-summary.json` retains paths, raw hashes, current source counters,
graphs, proposals, dispatch overrides and actual consequences. The high case's
attempt0004 later failed on an unrelated cleanup-helper assumption; its first
trace already proves life change and resolution, so it was not rerun. Low
attempt0005 printed RESOLVED/COMPLETED without a Java exception; the tool's
PowerShell wrapper reported exit1 alongside catalog warnings. Treat these as
retained diagnostic facts, not a formally green suite. Attempts1-3 and their
launcher/prompt/stdio failures remain retained.

## Exact semantic and policy cause

At both life totals and all four counter counts:

| Alternative | Root/amount | Recipient | Consequence |
| --- | --- | --- | --- |
| index0 Draw | graphKnown; DrawKnown; amount1, AmountKnowledgeKnown | Known Actor0, the controller/opponent | complete modeled value -1.95 for decider1 |
| index1 Mill | graphUnknown; MillUnknown; amountnull, AmountKnowledgeUnknown | Known Actor0 | sequence to LifeLossUnknown |
| linked LifeLoss | amountnull, AmountKnowledgeUnknown | **Unknown binding, no entities** | sequence to Known Cleanup |

The source physical card is public and has Influence1..4 in `Card.Counters`.
The graph does not bind Mill's variable amount to that public counter. More
critically, it does not establish that the linked LifeLoss affects the decider.
The native consumer cannot infer that recipient from an English choice label,
the card name, an AILogic tag, the acting seat, or a previous observed life loss.

`ActionSemantics.effectAmount` admits literal quantities only. Its Unknown
result for X/Y is honest. `ActionOperationProjection.targetBinding`, for
ParentTarget, looks up a parent operation ID; the already chosen targeting
parent is outside this branch projection, so the recipient remains Unknown.
Do not fix that by copying an arbitrary ancestor graph or inventing target IDs.

`CasualActionAssessment.Evaluate` correctly returns Incomplete/operation-knowledge
for this branch. It does not implement a general Mill model either.
`CasualCommanderPolicy.ChoiceScore` then scores a keyless, mana-less incomplete
option as Tie(seed,index)*.1. At diagnostic seed17, actual evaluation gives:

* Draw: Estimated, -1.9500000000000002.
* Mill/life loss: Incomplete, zero estimate placeholder; fallback0.050279235839843756.

`Choices` sorts completed **positive** estimates first, then numeric scores.
Thus the incomplete branch beats a fully known modest disadvantage. It is not
simulating a risk and deciding to accept it; the risk has disappeared from the
comparison. For this precise negative-draw/incomplete-keyless shape, changing
the seed cannot fix that ordering. Other shapes, including an opponent drawing
from an empty library, must retain their distinct current terminal behavior.

The same ChoiceScore fallback exists in the live 0eaa source. The controlled
runtime is explicitly default v6 from the frozen diagnostic binaries; this is
not a claim that the user's installed binary is that newer assembly.

## First execution slice: trustworthy facts, unchanged strategy

Phase A is independently deliverable. It repairs narrowly qualified structural
evidence while preserving Unknown outcome amounts and current policy behavior.

1. **Resolved ParentTarget recipient.** In the actual current owned choice
   publication, resolve the selected parent target through Forge's existing
   object references. An unambiguous current player may be projected as Known
   Explicit player recipient even when its targeting operation is outside this
   branch. Bind to the real parent ability/current pending choice, not any
   same-source ability or arbitrary remembered player. If the target is absent,
   ambiguous, secret or not provably related to this branch, retain Unknown.
   Reuse ordinary game/player roster admission. This does not grant a new command.
2. **Closed dynamic quantity evidence.** Add optional mechanically typed amount
   dependency facts, with two initially supported operators:
   `PublicSourceCounterSnapshot(sourceCardId,counterType,observedCount)` for
   the exposed Mill operation, and
   `SumManaValuesOfRememberedMill(inputOperationId)` for its linked LifeLoss.
   Operator spelling is provisional contract design, not an accepted wire schema.
   The first records a current public observation; the second describes a
   dependency, **not** a value, average, probability or upper bound.
3. Qualify from actual ability parameters/SVar definitions and active references:
   Mill RememberMilled=True; a mechanically exact current-source counter
   expression; linked LoseLife depending on those remembered milled cards;
   deterministic ordinary sequence and cleanup; no paid, conditional, repeat,
   replacement-description, additional-choice or unrelated remembered-input path.
   Require evidence that the aggregation input is exactly this mill, excluding
   preexisting or modified remembered objects. Do not expose those objects.
4. Keep operation/graph Knowledge Unknown and AmountKnowledge Unknown whenever
   the eventual loss is unknown. A known dependency and recipient do not make
   an uncertain numerical effect a complete known forecast. Existing literal
   quantities and older replays retain their meanings.

Native admission cross-checks every optional operator against the current graph
node kind, referenced operation ID, actual public source identity/counter,
player recipient, and immutable owned publication. Reject conflicting evidence;
do not silently promote it. Missing optional facts remain readable and Unknown.
No new enum value on the legacy effect wire, target/source occurrence, pass,
concession, reveal privilege or resolver is needed.

Likely producer files: ActionOperationProjection.java, DecisionSemantics.java,
ActionSemantics.java if a separate bounded quantity extractor is appropriate,
and ChoicePublicationTest/owned real-rule tests. Native files: immutable operation
contracts, Forge mapper/admission, shared wire schema and round-trip regressions.
The first slice should not modify ChoiceScore, generic confirmations, library
acquisition or fallback policy behavior.

## Separate policy experiment after fact review

The new candidate must distinguish a complete utility estimate from an
unpriced uncertainty; a zero placeholder must not masquerade as a free outcome.
It should inspect only certified current own-life-loss dependency evidence and
current legal alternatives. Its first scope is a mandatory opponent-decided
choice with a fully modeled nonterminal fixed opponent draw versus the qualified
uncertain own-life-loss branch. No general Unknown-choice reordering, card-name
exception, English text recognition or permanent "always draw" rule.

Do not choose numeric risk thresholds from the hidden controlled library or tune
until this reviewed contract exists. Before Phase B execution, freeze a separately
versioned candidate rule, including its risk attitude, public-life/exposure
inputs, every constant and unsupported-case fallback. A conservative low-life
guard is a plausible hypothesis; these observations establish neither the best
health threshold nor an expected mana value. Public observed cards could support
a later explicit uncertain prior, but that is a separate proposal, not authority
to inspect the library or assume a deck list. Policy execution is not yet approved
by this design: the numeric tradeoff requires explicit tech-lead review.

Keep shipping v6, attachment evaluation, and the library-acquisition experiment
independent. Use a distinct closed registry factory/descriptor for this experiment;
do not silently combine it with the library candidate or promote it to default.
Every old replay lacking the new facts must retain its old fallback behavior.

## Frozen acceptance and evaluation cases

**Phase A facts:** actual owned Palantir-style pipeline at life40/12 and
counter1/2/4; same source counter exposed; Known current decider recipient;
unknown future loss value retained; all library arrays empty. Rename source and
labels while retaining mechanical definitions. Negative controls cover another
parent, missing/ambiguous parent target, foreign game/publication/source, a changed
counter/source, hidden source, unrelated remembered cards, interleaved memory
change, unsupported SVar/arithmetic, conditions/repetition/extra costs and stale
operation IDs. Each remains explicitly Unknown or is rejected according to its
existing integrity contract. Native old-wire/replay readability is mandatory.

**Independent useful-action tests for the policy candidate:** freeze exact current
indices and a low-life high-exposure choice where the reviewed policy intent is
to preserve the AI's life by allowing the known nonterminal opponent draw. The
judge must use typed current recipients/option IDs and frozen experiment intent,
not the candidate's numeric score, labels or card names. Include a qualified
known-zero loss/control case that still takes the non-draw alternative, and fully
known bounded alternatives where normal comparative utility remains useful.

**Safety negatives:** missing/unknown recipient never becomes self; no new
capability from option text; costs and required minimum still respected; unknown
or allied relationship not treated as opponent; drawing an opponent's last/empty
library retains current terminal logic; owner/deadline/budget/late result behavior
unchanged; renewed choices use current keys; disabled/absent alternatives cannot
be invented. Unsupported player effects and conditional graphs stay outside the
new risk qualification.

**Real provider continuation:** capture each actual owned frame unchanged, run
native admission/bounded policy once, dispatch the exact proposed current option,
record accepted receipt and same-execution life/draw/mill/resolution facts. The
raw setup must not appear in the policy input. Keep the first attempt and cleanup
failure distinct from the gameplay fact; never retry a result to hide it.

**No-peek controls:** paired private future-library fixtures with identical public
decision data (zero-value vs large-value outcomes) must produce the same policy
decision given the same seed/history. Do not grade a single uncertain decision as
wrong merely because a hidden draw happened to be large. Actual consequences
across a preregistered fixture distribution and natural runs are evidence about
the chosen risk attitude, not proof of a deterministic forecast.

Then run the existing fixed-corpus A/A controls, targeted replay comparison,
predeclared natural FFA blocks with unchanged budgets/roster/denominators, and
human casual-Commander gameplay review. Existing admissible grading only proves
legality; a new usefulness predicate requires its own reviewed rubric. Missing
cases, cutoffs and uncertainty remain in results. Do not infer improvement from
these two controlled consequences or a single natural win/loss.

## Review decisions

* Proven: actual source quantities/recipient are not presently modeled and the
  fallback ordering systematically prices this risk as a near-zero alternative.
* Proven: the isolated current v6 choice can resolve to elimination at12life.
* Required next: review/freeze Phase A typed producer/native contract and exact
  current-parent qualification; then implement its independent acceptance.
* Pending: separately reviewed risk-policy rule/constants, experiment rubric,
  aggregate evaluation and human gameplay approval. No risk-policy production
  edits or threshold tuning were made during this investigation.
