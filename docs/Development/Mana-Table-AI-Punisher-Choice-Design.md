# Unpriced uncertainty in opponent-decided effects

Separate experiment. Controlled real-provider fixtures reproduce a mandatory choice in which v6 at 12 life selects an unquantified life-loss branch and is eliminated by the actual 28-life loss. The alternative lets an opponent draw one card. The reviewed Phase A source contract below preserves unknown outcomes; its current-source tests and separate policy candidate require execution qualification before any improvement or release claim.

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
result for X/Y is honest. `ActionOperationProjection.targetBinding` originally
used `getParentTargetingCard()` even for player-valued ParentTarget. Forge's
actual player semantics use `getParentTargetingPlayer()`. The already chosen
targeting parent is also outside this branch projection, so merely correcting
that helper does not repair the missing recipient.
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

## Reviewed Phase A: trustworthy facts, unchanged strategy

Phase A has two independent facts. The selected recipient can be repaired even
when the optional quantity dependency declines. Neither changes ChoiceScore,
generic confirmations, library acquisition or shipping v6's fallback.

`PlayerControllerHuman` retains a scoped `EffectChoiceContext` for the actual
blocking callback: owning ability, actual chooser, offered view-to-ability map
and requested cardinality. Nested callbacks restore it in `finally`, including
the controlling-player proxy. `MatchSession.captureChoice` checks this exact
context with its existing pending owner, board publication and source-version
fences. `CurrentEffectChoice` requires identity membership in that owner's real
Choices and an exact sequential descendant; a shared source alone proves
nothing. For player-valued ParentTarget, the nearest selected player ancestor
must be that owner and its one publicly selected current player must be the
actual chooser. The output is existing Known/Explicit recipient grammar with no
parent operation ID. No ancestor graph or legal-target query is added.

The reviewed quantity schema is one optional `rememberedMillLifeLoss` field on
`DecisionOptionSemantics`, rather than a general expression API. Its closed
version-one fields are `version`, `sourceCardId`, `counterType`, `observedCount`
and `lifeLossOperationId`. The option graph root is Mill; exactly two Sequence
edges identify its LifeLoss and terminal Cleanup, with no other nodes or links.
`counterType` is the exact public counter dictionary key (for example
`Influence`), not the uppercase script token. Certification requires this exact
key to be explicitly present in the public dictionary. The ordinary Forge
snapshot omits a zero counter, so that case declines the certificate while its
recipient can remain known; an authored explicitly exposed zero remains valid.
No native mirrored counter-type registry guesses an absent key. Every
descriptor field is required; missing count cannot silently become zero.

`CurrentChoiceMillLifeLossProjection` recognizes actual API/parameter/SVar
mechanics only. It accepts an unadorned SVar identifier resolving with Forge's
ability-first/card-fallback precedence to exact `Count$CardCounters.<enum>`;
Mill must remember its output, followed by exact LoseLife using
`Remembered$CardManaCost`, then Cleanup clearing memory. Closed parameter
allowlists exclude arithmetic, additional effects, conditions, payments,
repetition, optional/secret/random choices and unrelated memory-writing paths.
No name, description, AILogic value, past outcome or deck/library content supplies
capability. The public source is face-up/current on Battlefield, its observed
counter agrees with its published view, and all three nodes have that source and
actor.

Mill appends to the source's remembered objects, so the producer requires an
emptiness-only `hasRemembered()` prerequisite. It never enumerates, counts,
type-checks or reads values from remembered objects. Existing unrelated memory
declines the certificate. Native cannot verify this internal prerequisite from
public state; it trusts the producer and validates only the public source,
counter, exact graph and current owned option. Future replacements and
continuous-state changes remain unmodeled. The certificate describes a nominal
dependency at publication; it does not guarantee future remembered objects or
resolved numeric loss.

Mill/LifeLoss amount and amount knowledge remain null/Unknown and their node
and graph knowledge stay Unknown, including a zero counter. `ActionSemantics`
literal amount behavior remains unchanged. Native
`RememberedMillLifeLossFacts` is shared by the real owned/automated decoders and
authored AI-frame admission. Invalid present evidence rejects; absent/null old
facts remain readable. The closed descriptor converter rejects missing,
duplicate, extra and mistyped fields. Existing older option DTO settings ignore
the additive property. No legacy enum, overall protocol version, occurrence,
action handle, pass/reveal/concession privilege or resolver is added. Semantic
evidence alone cannot prove command ownership; existing current input scopes
continue to do that.

`PunisherChoicePublicationTest` includes an actual end-step/scry/target journey
from the tracked card script and separately controlled owned callback matrices
at life40/12 and influence0/1/2/4. Controlled callback setup is not represented
as natural game play. The native client/wire checks cover roundtrip, missing old
facts, conflict rejection and unchanged v6 proposals. These source-authored
checks must pass the exact integrated head before this slice is qualified.

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
to inspect the library or assume a deck list. The tech lead subsequently reviewed
and froze a separately versioned candidate using a four-life-per-card stress
preference; this is not an expected value or a bound derived from hidden test
decks. Candidate execution/measurement and qualification remain separate from
Phase A facts and default v6 promotion.

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
* Reviewed/frozen: current-owner player binding and the closed version-one
  nominal remembered-mill certificate, including the emptiness-only gate.
* Reviewed/frozen separately: candidate four-life stress preference and exact
  scoped alternatives; it supplies neither a mean nor a guaranteed loss bound.
* Pending: exact-head integrated execution of Phase A acceptance, independent
  policy usefulness/risk evaluation, natural comparisons and human gameplay
  approval. The earlier controlled diagnostics remain diagnostics with their
  original first-attempt and cleanup caveats.
