# Gameplay reliability design and implementation

Started 16 November 2026. This program turns the code review into five expert
designs, technical-lead review, isolated implementations, and integrated checks.
The baseline is `0eaa9f70abe3a40f92342352a20dc805a0ea730a` (public 0.2.469).
The existing live-update candidate is a separate input, not an approved merge.

## Ownership and integration

Each specialist owns one worktree and branch. The technical lead owns Git
commits, integration, review decisions, and shared guide changes. Existing
working directories and their uncommitted changes are outside this program.
All integration starts on `integration/gameplay-reliability-20261116`, based on
`origin/master`. Public `master` remains subject to its required PR and CI gates.

| Workstream | Specialist branch | Design |
| --- | --- | --- |
| Artwork residency and graphics resource lifetime | `feature/artwork-residency-20261116` | [Artwork residency](Mana-Table-Artwork-Residency-Design.md) |
| Rules-process diagnostic liveness | `fix/engine-diagnostics-liveness-20261116` | [Engine diagnostics](Mana-Table-Engine-Diagnostics-Design.md) |
| AI capability failures and content qualification | `feature/ai-reliability-spec-20261116` | [AI reliability](Mana-Table-AI-Reliability-Design.md) |
| Presentation state ownership and frame costs | `refactor/presentation-ownership-20261116` | [Presentation ownership](Mana-Table-Presentation-Ownership-Design.md) |
| Live-update delivery and complete product qualification | `design/live-update-delivery-20261116` | [Live-update delivery](Mana-Table-Live-Update-Delivery-Design.md) |

## Design review contract

Every design must identify the observed problem and its source, distinguish
reproduced defects from unmeasured risks, and account for existing safeguards.
It specifies owners, state transitions, exact lifetime invalidation, compatibility,
failure behavior, file boundaries, and acceptance criteria. The implementation
plan must produce complete reviewable behavior with the existing test owners.
New infrastructure needs a concrete consumer and decisive test.

Technical-lead review checks the design against the actual code and related
workstreams. Revisions and their disposition are recorded below. An accepted
design authorizes only its explicit execution scope. Implementation receives a
separate review of behavior, failure sequences, dependencies, and integration.

Rules authority, hidden-information filtering, current command scopes, and
uncertain-command protection remain required. An AI capability gap cannot be
fixed by inventing a pass, replaying an uncertain command, or fabricating a
result. Texture eviction cannot dispose an image still owned by a displayed
frame. Diagnostics must not control gameplay liveness. Presentation extraction
preserves existing gestures and visible results.

## Qualification

Start with focused existing regressions, including negative failure sequences
and a usable normal control. Run the full affected client/native suites on the
integrated source when relevant. Java, packaged-process, GPU, and real-engine
journeys are required where the changed boundary makes them meaningful. Record
the exact source, command, first failure, final outcome, and evidence limits.
Do not count fake-provider checks as rules proof or agent inspection as human
gameplay approval.

Source changes, automated checks, human review, production signing, and public
release qualification are separate states. A missing human or publisher input
remains explicit; it does not prevent completing independent authorized work.

## Review and execution record

| Workstream | Design review | Execution | Integration |
| --- | --- | --- | --- |
| Artwork | Revised design accepted; stable-graphics cross-review resolved | Implementing residency, frame leases, admission and GPU checks | Pending |
| Diagnostics | Revised design accepted | Implementing bounded drain, isolated writer, retirement and child-pipe checks | Pending |
| AI | Revised design accepted | Implementing Phase A provenance; B requires its actual-provider reproduction | Pending |
| Presentation | Revised history-owner design accepted | P1 history ownership and bounded actual I/O approved | Pending |
| Live updates | Revised delivery design accepted | Mainline U1 retained update owner approved; isolated U2 keeps its own gates | Pending |

All five specialist assignments begin design-only. Production changes begin
after the five designs and their cross-stream boundaries have been reviewed.

### Baseline evidence

On the untouched baseline, the complete graphics-free client runner passed
5,556 checks. The Release native test executable built successfully; its
existing audio-test nullability warning and NuGet vulnerability-feed warning
were retained. The ordinary `--smoke-presentation` journey passed 1,757 checks.
These establish a local baseline, not gameplay or public release approval.
The existing real-GPU card-reading journey also passed 120 checks at each of
1000 by 740 and 1540 by 980, including history, source inspection and return.
Its captures are the before-state for the ownership/resource changes.

### Initial technical-lead decisions

- Preserve intentional terminal handling for uncertain AI execution and invalid
  contracts. Retain typed failure provenance first. Any stale-policy-failure
  confirmation must earn its implementation through a failing held-result
  reproduction and cannot renew a causal decision's budgets.
- Artwork eviction must protect exact paintable frames, bound GPU payload and
  metadata, and permit an evicted face to reload. Equal-frame lease swaps cannot
  trigger repeated pressure-denied downloads. Existing readable fallback under
  fully pinned pressure is accepted as the resource policy, with visual review
  still required for the candidate.
- Diagnostic storage and its disposal stay on one worker, independent of stderr
  draining. One bounded retirement budget covers all optional diagnostic work;
  an abandoned worker cannot update a replacement owner or spawn unbounded
  successors. Avoid unused public status APIs.
- Presentation extraction must transfer an actual mutable lifetime into a
  cohesive owner. It preserves current geometry, gestures and rules dispatch.
- The unfinished 277-file update branch is an isolated design input. Do not
  import it wholesale as a dependency of the reliability fixes. Its complete
  A-to-B product witness and stable graphics/resource boundary need explicit
  cross-stream review.

### Combined design acceptance

All five designs received source review and a specialist revision on 16 November.
The graphics/update cross-review locates shared drawing/resource contracts in
the candidate's stable Graphics closure and keeps leases free of private Game
objects. Mainline fixes remain independent of that candidate.

The implementation boundary is the five approved first slices in the table.
AI recovery changes, the three later mechanic verifiers and qualified roster,
later presentation extraction, and the authenticated live-loading product are
explicit subsequent gates in their full designs. This first integration does
not claim those later milestones, production signing, or human acceptance.

Workers may add the narrow existing-runner registrations needed to execute their
own regressions. The lead reconciles those registrations and the small shared
application/window construction seams during review. Canonical guide updates
and all Git integration remain lead-owned.

### Existing remote evidence to keep separate

The earlier draft PR 136 checks used merge-test source `4c4fc887700a13d0c6a42b634a705e8014b2a626`.
Its [mixed-game job](https://github.com/proflayton/Mana-Table/actions/runs/42791391189/job/129746934430)
ended inconclusive because not every requested natural rotation reached an outcome
within its existing limits. Its [Java 21 job](https://github.com/proflayton/Mana-Table/actions/runs/42791391123/job/129745432555)
failed `TerminalProjectionTest.aRealLosingBlockPreventsAnOtherwiseFatalCommanderHit`
while awaiting an exact staged blocker assignment. These precede this program;
neither is attributed to a new implementation without final-source evidence.
Their retained failures do not justify relaxing checks, extending budgets or
claiming complete gameplay qualification from the passing local baseline suites.
