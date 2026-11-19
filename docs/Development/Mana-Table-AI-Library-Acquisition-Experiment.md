# Basic land acquisition experiment

## Problem and hypothesis

The development runner retained an actual turn-22 decision from a four-player
Commander game in which v6 selected zero of 28 offered basic lands, with a legal
range of zero to two. Its proposed empty `ChooseOptions` and accepted receipt
remain in the subsequent captured history. The original capture stays unchanged.
The choice's purpose was Unknown, so generic choice counting discarded positive
card estimates instead of acquiring the offered resources.

The hypothesis is that a candidate using mechanically certified own-library
acquisition context will select the offered basic lands and actually receive
them, while preserving v6 behavior for unsupported choices. The primary endpoint
is acceptable selection on identical eligible captured decisions. Actual rules
continuations provide a separate consequence check. This experiment supports a
bounded acquisition improvement; Commander playing strength requires a larger
natural-game experiment.

## Frozen treatments

Both variants use the same provider, disclosed choices, seed, original history,
plan and allowance. Both enable full attachment evaluation. The baseline factory
is `casual-commander.v6`; it ignores the new optional acquisition facts. The
candidate is `casual-commander.v7.library-acquisition.v1`. Its acquisition option
is explicit, with a distinct policy descriptor. Generic Unknown choices and
confirmation handling retain their existing behavior.

## Supported mechanics

The rules controller captures a scoped offer from the actual zone-change choice
call, active ability and offered cards. The scope retires in `finally`; the native
projection binds it to the current owned choice and publication. It contains
actor, library owner, recipient, stage, current destination, known absent extra
costs and bounded planned destinations. Selection indices remain the entire
authority to answer the choice. No card occurrence IDs, action handles, catalog
queries or hidden library order are added.

All three players must be the acting viewer. Supported stages are direct
Library to Hand/Battlefield, a selected two-card Library bundle followed by one
mandatory remembered land to Battlefield and the other to Hand, and the matching
one-card distribution step. Recognition uses actual operation types and parameter
constraints, with at most eight nodes. Card names, prompt labels and Oracle text
do not establish mechanics. Unknown conditions, extra costs, foreign ownership,
exile, arbitrary Library-to-Library movement and unsupported chains yield no
context. Destinations describe intended movement, not guaranteed resolution.

The candidate considers current disclosed basic land faces only, using existing
value/tie ordering and a bounded slice of the shared work allowance. It selects
up to the offered maximum. If it cannot satisfy a required minimum, ordinary
mandatory handling remains responsible for the answer. Payment, hand reduction,
sacrifice and discard contexts cannot become acquisition opportunities.

## Independent grading and controls

The `acquire-basic-lands` evaluator derives eligibility from the original current
frame and typed context. Expected count is the smaller of the legal maximum and
the current disclosed basic-land choice count; it must be positive and satisfy
the offered minimum. A pass requires a Ready, admissible `ChooseOptions` with
exactly that many distinct current basic-land indices. Empty baseline answers,
too few/many choices, nonbasic or foreign indices and stale scope fail. Candidate
budget exhaustion does not remove original eligibility. Missing context and
unsupported opportunities remain visible as ungraded/incompatible; separate
negative controls use admissibility and unchanged-behavior assertions.

| Family | Required evidence |
| --- | --- |
| Direct acquisition | Actual offered basic land reaches the actual own Hand/Battlefield after provider execution |
| Staged acquisition | Two actual selected lands reach one Battlefield and one Hand under the same real resolution |
| Identity independence | Renamed sources and prompts preserve mechanically equivalent behavior |
| Unsupported/cost contexts | Foreign library/recipient, exile, top-library ordering, payment, nonbasic-only, missing/unknown facts and altered chains receive no invented acquisition credit |
| Current authority | Stale input, changed choices, foreign indices and injected identities are refused |
| Limits and compatibility | Low work retains failure visibility; v6 stays frozen and attachment/required-answer checks remain intact |

New measured captures must come from the real provider with the new context.
Adding annotations to the historical replay would not make them original
provider evidence. Synthetic frames are explicitly unit controls. Preserve exact
original replay seeds and allowances. Family weights prevent renamed copies from
inflating the primary endpoint. A/A controls precede A/B interpretation.

## Review and acceptance

Provider/native contracts, candidate policy and independent evaluator use separate
expert branches. The lead reviews scope, current authority, visibility, bounded
work and causal evidence before integration. Success requires a replay correction,
verified actual land acquisition, no change to unsupported/required-answer safety,
and passing relevant existing checks. A favorable heuristic score or accepted
command alone cannot establish acquisition. Natural games retain the full locked
32-cell schedule, first attempts, failures and unresolved outcomes. V1 reports
are descriptive and make no general strength or confidence-interval claim.

## First development measurement

At source `3b7877cbecafbf02c71e0296cef61dde321bc807`, the locked four-case
owned replay comparison completed all eight jobs: baseline 0/4 useful selections,
candidate 4/4, four corrections and zero regressions, incomplete pairs or invalid
evidence. The two staged cases share one family; all three family rates likewise
changed from zero to one. Candidate A/A completed eight jobs with identical
proposals and 4/4 passes per arm. The separate five-case attachment comparison
completed ten jobs with 5/5 per arm and zero regressions. No natural-game strength
conclusion follows from these development cases.

Actual native continuation needs separate evidence of resolved movement. Public
Battlefield events supply exact acquired identities. Library-to-Hand activity
deliberately omits private identities, so a test-only bounded fixture audit records
committed engine movement and bridges it only to existing IDs in the already
published owned Hand. Exact source execution, owners, zones and identity sets
must match. The audit is not policy input or production telemetry. First attempts
that incorrectly expected a public Hand identity remain retained as failed
verification attempts; later checks use this independent private proof rather
than weakening acquisition to a count comparison.
