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

## Remaining risks and next work

1. **Query purity extends below published snapshots.** Live ability enumeration
   can assign an activating player while discovering actions. Pool/board purity
   comparisons do not establish that every ability-local field is unchanged.
   Introduce a read-only enumeration boundary only after testing which execution
   handles and caches the current inputs require.
2. **Funding work needs a shared examination budget.** Source discovery can
   repeat scans for several cards and routes. Current accepted-source/route caps
   do not bound all rejected candidates examined. Measure actual visits and
   stages, then return unknown on budget exhaustion; partial work must never
   become permission to pass or a claim that a cost is unpayable.
3. **Startup fixtures can mask saved preferences.** `PresentationRun` uses the
   automated harness mode, which deliberately starts with Auto disabled. Tests
   that save Auto on must explicitly exercise the shipping startup path before
   claiming saved-preference coverage.
4. **Observed outcomes remain separate from test counts.** Controlled scripted
   journeys check the invariants they exercise. They do not establish full rules
   coverage, human discovery, readable timing or natural AI game completion.

See the [architecture ownership map](Mana-Table-Architecture.md) and
[test commands and evidence scopes](Mana-Table-Testing.md). Future changes should
state the owner, lifecycle invalidation and observable invariant they strengthen,
and add a failing sequence to the nearest existing journey.
