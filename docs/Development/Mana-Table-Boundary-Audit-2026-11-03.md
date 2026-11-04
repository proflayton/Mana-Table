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
