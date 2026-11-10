# State and interaction boundary audit: 3 November 2026

This audit starts from release 0.2.372 (`5779eed1`). It follows recent
regressions through Java publication, client acceptance, native composition and
their release tests. It is a focused audit of those seams, not a review of every
Forge card script or proof that every imported deck works.

Existing project dependency guards, immutable decision captures, serialized
command dispatch and scoped interaction frames provide useful boundaries.
The weaknesses are inside those layers: values with different lifetimes can
still share an owner or cache. Adding another interface around the same mutable
state would leave those failure sequences possible.

## Contracts to enforce

| Value | Owner and lifetime | Required invariant | Regression gate |
| --- | --- | --- | --- |
| Published board and decision facts | Java's committed decision capture | A source-action query cannot describe a live source version under an older published revision. Query work must revalidate its capture before installing a catalog. | Real rules event changes source state without publishing; cold and retained catalogs must reject until a fresh publication. |
| Accepted viewer snapshot | Client session audience generation | A command or observation captured for a retired audience cannot replace the current audience, even within the same game and revision. An active old yield grant must finish revocation before handoff commits. | Hold the old command response, accept a handoff, then release the old response; exercise the grant-revocation barrier separately. |
| Required HUD reservations | Current native composition | Required controls cannot consume reader/payment rectangles produced by a previous composition. | Parent source action becomes a child mana choice; first-exposed hit survives pointer-only frames and the original click. |
| Source-owned choice geometry | Current source, reader and bill owners | The same measured rectangles determine paint and input; current turn controls also reserve their actual footprint. | Original mana-payment journey plus stack-present payment and frozen-time recomposition. |
| Test execution coverage | Native CI inventory | Critical small and large card-state render cases both run in ordinary PR/master checks. Full inventory availability alone does not establish release coverage. | Existing inventory accounting and release wiring checks. |

## Changes in this candidate

The native reservation owner now captures scene geometry explicitly before HUD
placement. It no longer uses a lazy getter keyed by scene reference and time, or
reads prior source-reader, battlefield-state or mana-painter output during that
capture. Later mana choices consume current reader, bill and turn-control
reservations after their owners have measured them. Retained card motion remains
a separate presentation input; this does not claim that every card pose is a
detached current-frame layout plan.

Client audience replacement and Java source-query publication are covered by
held-response and real-input regressions. The same game ID does not make an old
audience response current; the same input object does not make a changed source
version part of an older publication. Existing legal handoffs and ordinary
source actions must remain usable after the fence.
An explicit handoff returns false while old finite-yield consent awaits its
negative acknowledgement. The original audience and connection retain that
cleanup; an explicit retry can commit the replacement after acknowledgement.
Transport responses cannot implicitly perform an audience handoff, and stale
rejected lobby cohorts cannot turn into recovery failures for the current table.

Tests reuse the existing graphics-free client, Java input/state, headless
presentation and four native UI shards. The larger card-state case is added to
an existing shard with headroom. There is no new campaign, service, test lane or
increased interaction deadline. First failures and candidate results must be
recorded separately in the PR; these authored checks do not supply human UX
approval.

## Follow-up sequence audit from 0.2.375

The next pass composes lifecycle changes while transport work is held. Thirty
direct/Auto/poll receipt and failure cases include viewer ABA, chained games,
reset followed by a game, explicit null followed by a game and closing after a
handoff. Eight finite-yield cases combine clearing/resetting with a prospective
grant or held revocation, followed by continuation or closing. The oracle checks
the final audience, late-failure isolation, exact negative capability and absence
of duplicate dispatch. These are bounded controlled sequences, not a model of
all rules interactions.

Two released-source failures required ownership beyond the visible replica.
`Accept(null)` must retire the action epoch and stop continuation before clearing
state. Pending negative consent must survive that empty replica and block
foreign binding until the original owner's acknowledgement arrives. Rendering
no match does not prove that old asynchronous work or consent ended.

Three bootstrap cases hold a poll started with no bound audience, explicitly
accept a game, then return another game, null or an error from the old poll.
Those callbacks must be inert once another source binds the replica. Initial
binding does not advance the global epoch: queued initial and subsequent frames
from the same admitted ingress retain their existing ordered-prefix contract.

Real Java input probes exposed another failure class: an unoffered read could
rebind a live ability's activating player, and a retained source action could then
be admitted without producing the intended tap and mana. The candidate separates
detached source-action queries from ordinary invocation. Query alternatives
retain their query purpose and original activation lineage; the current input
admits and materializes an invocation at execution. Legacy execution enumeration
remains separate. Purity and once-per-turn limits need real engine tests together;
a board-only before/after comparison cannot establish this boundary.

The startup harness can now explicitly use `Automated=false` with isolated
services, profile and clock. Its 26 checks load saved Auto off/on through normal
initialization, use physical Hold and finished-table Return, reopen the profile,
and hold the first dispatch to check its fresh scope and lack of replay.
The normal manual harness remains a separate negative control. Return restores
the accepted in-memory preference; reopening independently reloads the profile.
These tests add no production startup policy and do not establish live-table or
human acceptance.

## Remaining risks and next work

The resilience follow-up after 0.2.378 treats each invariant as a release
contract, with a negative sequence and a usable-current-owner control:

| Contract | Existing test owner | Observable result |
| --- | --- | --- |
| A mana color choice retains its captured parent. | Java `SourcePaymentReplacementTest` | A displaced payment cannot authorize its retained required color reply. Restoring its actual parent leaves the choice and cast usable. |
| Display restoration cannot finish a producer. | Java `SourcePaymentReplacementTest` | Closing a producer's cost confirmation and refreshing its parent cannot reopen payment before the producer continuation finishes. |
| Payment cancellation follows rules receipts. | Java `SourcePaymentReplacementTest` | Replacement casts cannot reuse old source offers; reversible mana refunds and irreversible costs follow Forge's actual state. |
| Returning context values do not revive old reads. | Client `ReadAudienceLifecycleRegression` | Old source actions, private history and failure/support state remain retired across viewer ABA, null and reset; fresh reads succeed. |
| Retirement does not replay uncertain execution. | Native `ForgeRequestCancellationProtocolProbe` | The shipping provider deadline ends a withheld reply wait; recovery, Reset and Close keep old results inert and permit only explicit freshly observed commands. |

These cases run in the existing Java, graphics-free client and headless native
lanes. No new test service or rules simulator is involved. A displaced-parent
test restores only its temporary input, then completes the original required
choice and cast. It does not add a player-facing command to cancel a required
child. Legal parent cancellation and resource outcomes are exercised separately.

1. **Query purity extends below published snapshots.** Live ability enumeration
   can assign an activating player while discovering actions. Pool/board purity
   comparisons do not establish that every ability-local field is unchanged.
   The source-action query boundary above addresses its exercised paths; legacy
   static helpers and planners still require separate purity checks. Do not
   generalize its claim to every engine read.
   Cost copies also retain mutable aliases below the ability graph:
   `CostPart.copy()` is shallow, including list/zone-table state in
   `CostPartWithList` and the counter table in `CostPutCounter`. The existing
   `CardZoneTable` copy constructor does not detach every nested collection.
   This is an isolation risk, not a reproduced cause of resource loss. Audit
   those payment containers before claiming whole-cost detachment or rollback.
2. **Funding work needs a shared examination budget.** Source discovery can
   repeat scans for several cards and routes. Current accepted-source/route caps
   do not bound all rejected candidates examined. Measure actual visits and
   stages, then return unknown on budget exhaustion; partial work must never
   become permission to pass or a claim that a cost is unpayable.
3. **Startup fixtures can mask saved preferences.** `PresentationRun` uses the
   automated harness mode, which deliberately starts with Auto disabled. Tests
   that save Auto on must explicitly exercise the shipping startup path before
   claiming saved-preference coverage. The follow-up above adds that path without
   changing the manual harness default.
4. **Observed outcomes remain separate from test counts.** Controlled scripted
   journeys check the invariants they exercise. They do not establish full rules
   coverage, human discovery, readable timing or natural AI game completion.

See the [architecture ownership map](Mana-Table-Architecture.md) and
[test commands and evidence scopes](Mana-Table-Testing.md). Future changes should
state the owner, lifecycle invalidation and observable invariant they strengthen,
and add a failing sequence to the nearest existing journey.

## Multiplayer stabilization execution: 9 November 2026

This is the bounded execution plan for PR #130, not a release announcement.
The product goal is a complicated Commander game with two human players and two
AI seats, from a synchronized lobby through completion. Automated scripted seats
cannot establish human usability. The existing encounter roadmap's M1/M2
acceptance remains separate.

### Pods, work and handoffs

| Pod | Owned work | Deliverable and exit | Handoff |
| --- | --- | --- | --- |
| 1: Lifecycle contracts | C1: name each owner, authority and invalidation rule. C2: map every changed file to a contract. C3: independently review boundary changes. | The contract below and scope audit are complete. The four-file boundary consolidation passed independent source review. | Pod 2 implements only accepted contract changes; Pod 3 derives observable checks. |
| 2: Boundary implementation | B1: inspect all rich-read callers. B2: move admission, immediate invocation and completion validation into the existing session owner. B3: preserve cache and policy checks after awaits. | B1/B2/B3 are implemented, independently reviewed and covered by the passing normal client/native suites. Real-engine qualification remains pending. | Root freezes the source after Pod 1 review, then admits validation. |
| 3: Validation | V1: bind each contract to existing checks. V2: validate the smallest failing real-engine payment journey. V3: assess exact-candidate campaign and presentation evidence. | V1, dependency reuse and normal client/native suites passed. V2 is next; V3 remains pending. | Root admits a candidate for upload only after the local integration result. |
| Integration: Root | I1: maintain this plan and resolve design/scope decisions. I2: integrate, commit and upload one candidate. I3: review required PR/master checks and the published package. | A short release argument linking each guarantee to its evidence, with explicit remaining limits. | Human review receives the exact playable candidate; findings return to the relevant contract. |

Only root changes Git state, remote PR/release state and the canonical handoff.
Pods own disjoint source files; shared documentation stays with root. A pod
reports a blocker at its boundary instead of expanding into another workstream.

### Lifecycle contract

| Owner | Authority and lifetime | Required outcome | Existing verification owner |
| --- | --- | --- | --- |
| Java `MatchTable` / `MatchSession` | One rules publication and its entire audience cohort. A newer publication retires preparation. | Prepare all audience views before callback-free commit; retain the last coherent cohort when preparation loses ownership. Never restore old handles over newer state. | `PublicationOwnershipTest` and existing publication checks. |
| `ForgeCommandScope` | A read lease binds audience, command-admission generation and anchor. | An obsolete read cannot install or clear newer authority. Bare discovery retirement supplies no action authority; uncertain post-command readback remains recovery, without replay. | `OwnedDecisionObservationProtocolProbe.PublicationAdmission`. |
| `ClientSession` operation lane | A command or quiet acknowledgement owns both its wire exchange and owner-side receipt application. | Quiet work can remain visually non-busy while occupying the connection. Unsent work terminates once; late receipts remain discarded evidence; unknown execution cannot become permission to retry. | `OperationReservationRegression`, diagnostics and priority checks. |
| `ClientSession` read boundary | A settled lane and exact accepted ordinary frame admit one read attempt. | The owner invokes discovery immediately and validates its captured context at completion. Closed Deferred/Retired/Current outcomes replace the caller's optional capture/invoke/check sequence. No asynchronous lock or second replica. | `CurrentReadRegression` and owner-read controls. |
| `ConnectedPlayerDriver` | Validated enrichment plus the current ordinary audience and complete payload. | Cache reuse requires unchanged operation lineage and matching payload; policy/source awaits require revalidation. Identical polls may preserve valid enrichment. | Lineage, equal-reaccept, mismatch-convergence and manual-source controls. |
| Observation round | Two independent ordinary owners. | Both audiences receive service. Incomplete discovery grants no policy, comparison or completed-step credit. Genuine command uncertainty and existing progress deadlines remain blocking. | Shared round controls, mixed campaign, post-concession lobby and consuming-mana journey. |

The latest discovery failure occurred in the scripted ordinary-player driver;
it does not itself establish a shipping AI strategy or mana-rules defect.
Earlier failing runs did not retain every competing command or raw response.
Reproduced boundary defects do not retrospectively identify those missing events.

### Scope and decisions

The audited PR consists of server publication (3 files), provider authority and
diagnostics (7), client lifecycle (10), scripted observation and verification (8),
and supporting workflow/documentation (3). This plan adds the existing audit
document to that scope. No unrelated source change requires a separate split.
The accepted implementation task closes the optional read protocol inside the
existing session owner; it does not introduce another coordinator, game-state
model, command retry, provider interface, wire schema or timer.

AI policy, card-catalog optimization, rendering and new UX features wait for this
candidate's stabilization. A newly found issue enters this change only when it
violates one of the declared contracts; other findings become separate work.

### Ordered acceptance and stop conditions

1. **Contract and scope:** Pods 1/2 agree on the owner and the smallest enforceable
   interface. Root accepts the scope before implementation; Pod 1 reviews the
   resulting diff before the source freezes.
2. **Controlled boundaries:** Run the normal client and native suites once for the
   frozen implementation, including the affected existing negative and positive
   controls. Use focused reruns only when a failure needs investigation. Preserve
   the original failure. Test totals do not replace the named invariant.
3. **Smallest real-engine witness:** Build a source-bound package and harness,
   then run the existing consuming-source mana journey once. It must show the
   actual cast, sacrifice, tap, empty pool and typed payment history, coherent
   ordinary owners, preserved privacy and clean disposal. Keep its 90-second
   total and 45-second active limits.
4. **One fixed candidate:** Require its own PR checks, including applicable iOS;
   then the actual merged master's checks and package verification. Keep the four
   natural games' 180-second/160-turn limits, 110-trigger/four-view workload,
   schema4 admission and all current safety/progress gates. Expected read
   retirement, repeated discovery churn and command uncertainty must be reported
   separately; no gate is silently loosened to make a run green.
5. **Player acceptance:** Review an actual two-human/two-AI journey for passing,
   resources, required choices and readable resolution. Record the exact candidate
   and feedback separately from automation; this remains pending until observed.

Any failed effect assertion, ownership violation, uncertainty, stalled progress
or cleanup failure stops that validation attempt. Classify the failing contract
and inspect the first evidence before changing code. Do not rerun unchanged games,
reroll randomness, expand deadlines, or add broad scaffolding around the failure.
Use supported test/package entrypoints and the already reviewed orchestration;
do not add a new proof wrapper for each result. Consolidating that tooling is a
separate follow-up after this product boundary is stable.

### Follow-up from the actual merged candidate

PR #130 passed its own checks and merged, but the merged master's first natural
campaign stopped one game at its unchanged 180-second limit. Three games ended
naturally; the fourth was still progressing on turn 51. Its retained evidence
reported no engine fault, bare-read supersession or expired command-authority
wait. The release remained blocked. A passing PR does not qualify a later master
run, and a completion cutoff alone does not identify slow strategy or an engine
stall.

The bounded follow-up addresses two independently demonstrated contracts:

| Contract | Defect and required correction | Verification |
| --- | --- | --- |
| Independent observation work | A quiet acknowledgement can defer rich discovery while an ordinary poll is already due. Returning without servicing that poll leaves a zero owner-wait deadline and repeatedly runs the observation loop. Deferred discovery must still service eligible ordinary polling through its existing owner. | The new deterministic control fails the original driver at the due-poll assertion. The one-line correction passes all four new checks and the prior 71 owner-read checks, without sleeps or timer changes. |
| Logical route versus execution | A turn-wide attempted-route guard also suppresses a previously successful tap ability after it untaps and becomes legal again. A submitted or accepted command is insufficient evidence to reopen it. The provider must explicitly correlate the offered stable route with its actual execution; the AI owns the bounded attempt lifecycle. | Require the exact accepted route's committed tap cost, resolved execution with a typed committed effect, subsequent source renewal and a current legal offer. Cancellation, unknown acknowledgement, missing correlation, restoration and incomplete evidence retain the guard. |

The retained game supplies a concrete strategy witness: a legal, payable,
untapped Thermo-Alchemist route was filtered as already attempted in that turn.
Its earlier activation and later untap were present in actual rules history.
That establishes a route-lifetime defect; it does not establish an unanswerable
winning play or explain the entire campaign cutoff. Production behavior must
use typed route/cost/execution facts rather than this card's name.

Pod 2 owns provider correlation and its audience/compatibility checks. Pod 3 owns
native AI history, submission and progress consumption, with regression coverage.
Pod 1 independently reviews both contracts and their evidence. Root integrates
the accepted scope and runs the combined checks before uploading a new candidate.
The existing 4096 logical-route bound, command authority, unknown-execution
recovery, natural-game limits and release gates remain intact.

Route correlation is optional historical metadata, never a command handle.
An older provider or replay without the new evidence preserves the conservative
guard. The existing history schemas 1/2 omit the new live-only route field through
an explicit wire projection; a null value would still break strict older readers.
No matching-application-build policy can be assumed for multiplayer peers.
The first renewal slice covers fully known, repeatable nonmana abilities
whose sole cost is tapping their source. Other costs require their own explicit
resource lifecycle before they can safely reuse this contract. Human multiplayer
acceptance and the encounter milestones remain pending.

The follow-up's first CI attempt caught two further boundary errors before
release. Resolved spell projections could retain route metadata after their
source ID lost admission: face readability and current identity admission are
different facts. The native fence correctly rejected those orphaned routes.
The producer must redact both together. Separately, Java history tests cast the
wire result's records to internal objects; they must decode the actual serialized
projection while retaining their privacy, cursor and behavior assertions.
The original rejected observations and first failures remain evidence. Neither
failure was a natural-game completion cutoff, and neither justifies relaxing
validation or repeating an unchanged game.
