# Presentation ownership and frame composition

Status: design accepted by the technical lead, 16 November 2026. Baseline:
`0eaa9f70abe3a40f92342352a20dc805a0ea730a`, public 0.2.469. No production
change or performance improvement is claimed by this design.

The first implementation extracts the match-history reader's complete local
lifetime from `TableApplication`. Later work separates measured composition
plans and other local readers as their independent ownership can be demonstrated.
Existing presentation and command owners remain in place. The objective is to
make feature interactions easier to reason about while preserving current play.

## Findings and scope

`TableApplication` spans 120 partial files and about 17,000 nonblank lines. That
count identifies the size of one shared mutable namespace; it does not establish
poor frame time, a reproduced bug, or a need to replace the client architecture.
The relevant concentration is that one class can alter deck navigation, history,
inspection, command pacing, transient geometry and overlay exclusion directly.

Important boundaries already exist:

- `ClientSession` owns command serialization, recovery, accepted observations
  and current action authority.
- `DecisionSession` owns payload-specific drafts and invalidates them by the
  actual game/viewer/decision and selection shape.
- `MatchPresentationLifecycle` owns card motion, announcements and public effect
  feedback. It resets across audiences and rebases restoration feedback.
- `InteractionFrame` publishes a detached ordered hit collection; `PresentationFrame`
  binds it to the completed drawing and displayed card poses.
- `DeckInteractionState`, the deck workspace, source-action session, history
  read model and public-presence session already have narrower responsibilities.

Those are assets to preserve. Moving each partial into a new wrapper with access
to the entire application would leave the current mutation surface unchanged.

The first slice is deliberately a reader, not a rewrite of gameplay composition.
History has a coherent local state machine and existing production-path tests.
Its navigation, archive requests, request generations, detail cache and export
consent can be owned independently without changing rules, cards or gestures.

## Behavior to preserve

The player opens History from the current table, reads a recorded action,
navigates previous/next/newest, and may inspect its currently disclosed source.
Saved matches use the existing archive list. Opening an archive changes reading
material, not the current game. The existing report dialog captures the player's
destination and public/own-private export choice.

Reading never submits a game command. A historical source ID cannot authorize
inspection of a concealed face or become a current action handle. A source that
ceases to be disclosed returns the reader from its card inspector to History.
The table keeps progressing under its existing pacing and input-exclusion rules.

This slice changes no controls, layout, animation, text, gestures or navigation
policy. The existing [game-derived experience rules](Mana-Table-Experience-Review.md#design-rules-learned-from-player-feedback)
and the `HistorySmoke` card-reading journey remain the visual behavior contract.
No new game interaction precedent is asserted for this internal extraction.

## Ownership after the first slice

| Owner | State and authority | Excluded responsibilities |
| --- | --- | --- |
| `ClientSession.History` | Admitted live history, provider read authority and chronology | Panel navigation and saved-archive UI |
| Application composition | Current game/viewer, global overlay exclusion, current-card visibility lookup, inspector entry, file dialog and lifetime wiring | History selection, pages, read generations or mutable archive results |
| `HistoryPresentationSession` in `Mana.Game/Presentation` | Local surface, selected action, pages, seen cursor, export scope, saved list/document, archive-read/export request lifetime, error text and detail cache | Rules connection, current cards, gameplay commands, graphics devices or archive-service disposal |
| `HistoryDetailsFormatter` in the same folder | Pure text reduction over retained history facts and optional archive summary | Fetching facts, visibility decisions, UI state or persistence |
| Existing archive service/composition | Recording accepted history and marking the match archive closed | Panel focus or rendering |
| Existing history painter | Ordered drawing and hits derived from one captured reader view | Advancing navigation or applying I/O completions while painting |

The archive-recording subscription and the three observed-history provenance
fields used to mark a match closed remain with their existing lifecycle in this
slice. A transient panel owner must not acquire the recorder's lifetime merely
because both use the archive service.

Preserve closure effects exactly: final caught-up history, explicit audience
retirement and application disposal continue to call the same closure owner.
`MatchHistoryArchive.MarkClosed` coalesces its game/viewer closure records; its
method can already be called more than once. The acceptance claim is one
consistent archive outcome without duplicated records or lost incomplete/finished
facts, not a newly invented guarantee of one method invocation. Reader open,
close and archive browsing must not add recorder subscriptions or close a match
archive. A closure-write failure remains a reader-local message and cannot stop
the current table.

## Local state and transitions

Use a closed `HistorySurface` value (`Closed`, `History`, `Inspector`) and an
explicit reading source (`Live`, `SavedList`, `Archive`). The implementation may
reuse existing names, but callers cannot mutate individual session fields.

The owner retains the immutable live reading model most recently supplied by
composition and, when selected, an immutable archive model/document. Its current
model and selected action are derived from those values and the explicit
selection ID. It owns all generation changes. The scope also includes surface,
reading source, audience/archive identity, selected action, pages and export
scope, so an old hit cannot be reused after any relevant local transition.
An unchanged frame must not increment the scope generation.

| Input | Result and invalidation |
| --- | --- |
| Open | Cancel the previous reader request, select the live newest action and cursor, reset detail page and private-report choice, choose existing live/saved opening behavior, enter History |
| Close | Retire the request generation, cancel owned work, release archive/detail references, enter Closed; composition clears its inspector and global overlay |
| Saved matches | Enter SavedList, reset archive page, begin the existing list request |
| This match | Retire archive request, clear archive context, select current live newest action/cursor, reset details, enter live History |
| Open current listed archive | Capture that opaque archive identity and request generation; accept only the result of that request |
| Previous/next/newest | Navigate within the currently selected immutable model, reset details; Newest also advances the seen cursor |
| Detail/archive page | Preserve existing bounds after measurement/list capture; publishing a painter may not independently mutate page state |
| Toggle report scope | Change only the explicit public/own-private consent and invalidate hits carrying the old scope |
| Read source | Return an inspection intent naming the selected history action/source identity; composition independently resolves the currently disclosed card before inspector entry |
| Back from card | Return to History with selection and page retained; composition clears its current-card inspector |
| Audience replacement/null | Retire old reader generation and archive context before the next frame; clear selection and private consent according to existing reconciliation behavior |
| Disposal | Permanent Closed state; cancellation and reference release are idempotent; every later completion is inert |

Composition remains the sole owner of `overlay` and the concrete `inspected`
card. A small synchronous effect result (`Opened`, `Closed`, `InspectSource`,
`ReturnToHistory`, or no effect) tells composition what established shell action
to perform. It contains no callback into the application and no game command.
Opening History must still collapse card reading, clear text focus and dismiss
the notice exactly where the current application does so.

## API shape and data flow

The internal sealed session takes the existing optional `IMatchHistoryArchive`,
the existing owner-post function and an optional clock for controlled deadlines.
It has no reference to `TableApplication`, `ClientSession`, `TableWindow`,
`GraphicsDevice`, or any provider implementation.

Composition supplies admitted live inputs explicitly through an `ObserveLive`
method: game/viewer identity, immutable live model, provider history status/error
and no provider command capability. Recording/caught-up provenance remains with
the existing archive-closure owner. Observation is not permission to query a
provider. The method does not infer another viewer's history from the
same game ID. Null and audience replacement retire reader authority before
new local navigation is accepted.

Semantic methods cover the transitions above. They validate their current scope
when called from a published hit. There are no public setters for selection,
pages, archive data, busy state or generations, and no `Action<TableApplication>`
escape hatch. Existing `HistoryIntent` remains the closed UI intent family;
composition validates its hit scope before routing to the session.

`CaptureView` returns one immutable `HistoryPanelView` for a composition. It
includes current model/selection, source/surface, bounded page values, list and
archive summary, current message/loading state and report consent. The painter
uses that one view for text, enabled state and hit scopes. It does not read a
mixture of mutable session fields partway through painting. Cache the immutable
view until an input changes it instead of allocating one solely because another
frame was requested.

History composition has two passes, measurement and publication:

1. Capture an immutable measurement basis identifying the current reader
   generation, surface/source, model and selected action, report scope, viewport
   and text scale. Measure detail pages and archive-list capacity with the
   existing `DrawList` metrics. Emit no drawing commands or hits in this pass.
2. Call `ReconcilePages` with that exact basis and its measured page counts.
   It refuses a retired basis; otherwise it clamps the pages and returns the
   final immutable view and scope. Build one render plan from that final view
   and the measured pages. Paint text, enabled controls and every hit from this
   plan, including the final page indexes and scope.

Clamping can change the final scope without invalidating page measurements when
all other basis inputs are unchanged. The selected page's text and Previous/More
enabled states must use the clamped final indexes. No painter may fetch a newer
view or change a page while emitting commands. No session pump, archive callback,
dialog or application-state mutation is allowed between final-plan acceptance
and publication. If the basis retires before finalization, discard its measured
plan and prepare from the current owner before publishing; do not combine old
text with new hit authority. Production composition remains on one owner lane,
so concurrent calls into the reader are a contract violation, not a reason to
add a layout retry loop or locks around paint.

The details formatter receives only the selected action, its exact immutable
model and optional archive summary. Its output preserves existing causal text,
unknown coverage wording, payment origins, announced targets and archive date.
The session caches this output against that complete input identity; a matching
action name or ID alone cannot reuse another model's text.

## Archive requests and completion ownership

Each request captures a private lease containing the owner generation, operation
kind, live audience or selected archive identity, and cancellation source. Keep
the existing ten-second reader deadline; starting a later phase does not grant a
new timeout. Logical result authority and actual outstanding work are separate
facts. A cancellation or deadline retires the right to alter the view, but does
not establish that a method ignoring cancellation stopped reading or writing.

The owner has one actual archive/export pipeline slot and at most one pending
read descriptor. Starting a newer read retires the previous logical lease and
replaces that descriptor; it does not start another archive call while the old
pipeline remains outstanding. The descriptor contains only the requested kind,
opaque archive identity, scope and original deadline, not a model, byte buffer,
destination stream or chain of closures. The current slot's completion may wake
dispatch, but the owner independently validates the still-current pending read
and its remaining deadline before starting a new pipeline. Expired pending work
is not dispatched. There is no FIFO queue of obsolete reads.

The actual pipeline invokes the existing archive port with an operation-owned
uncancelled task. Logical cancellation and the ten-second UI deadline are
observed separately. This is deliberate: the current archive's `QueryAsync`
can cancel its wait before an already queued disk read executes, so returning a
cancelled task alone cannot prove physical retirement. Keeping one awaited
uncancelled port operation prevents repeated Close/Open or Retry gestures from
accumulating those reads. The port's normal completion still denotes its own
completed work; this design cannot certify a misbehaving implementation that
returns success while continuing undisclosed I/O.

Expose read-only `HasOutstandingIo` independently of visible `Loading` and
`Surface`. It remains true until the actual pipeline, including an already
started destination write, reaches its `finally` boundary. The completion
signal is published independently of a UI callback being pumped. Generation
retirement never releases the actual slot. Only the owning request's view result
is subject to the scope checks below; freeing the completed slot cannot resurrect
that result. A queued fresh read still requires its own current lease.

Worker callbacks close over the history owner and immutable request values, not
the application. They post an immutable completion to the original
`ClientSession.Post` owner queue. They neither discover a replacement queue nor
rebind to a new presentation. Posting does not grant permission to apply a result.
The session applies it only if it is open, the lease is still current, the
generation matches, and the intended audience/archive still matches. Exceptions
become the existing reader-local message. No completion can close a live game,
overwrite a newer selection, reopen a panel, or change global recovery state.

List results contain only the service's opaque archive summaries. OpenArchive
still requires membership in the current admitted list. Read results use the
existing validated history reducer; this owner does not construct historical
facts, merge audiences or weaken export filtering.

The shell continues to show `SaveFileDialog`. After explicit dialog acceptance,
it passes the captured destination and current request scope to the owner.
Export captures the existing public/own-private scope and selected focus once;
later toggles cannot broaden that request. Preserve the existing prepare-before-
write flow. An export requires the actual pipeline slot to be free; its existing
Save control remains unavailable while that slot is occupied. Validate the
scope again after the dialog returns. Exports are not queued, automatically
retried or replayed. An explicit Save is a fresh request with a fresh destination
confirmation, not permission to repeat a previous write.

Before beginning the destination write, check that the export's logical lease
is still current. Once writing has started, closing the panel can suppress its
local completion but cannot claim that cancellation deleted or rolled back the
report. The actual slot and `HasOutstandingIo` remain owned until this write
settles, even when the visible surface is Closed and Loading is false. Do not
change destination publication semantics as incidental refactoring.

The owner never disposes the shared archive service. Dispose cancels and retires
its own leases, clears retained models and observes eventual task failures.
It clears the pending read descriptor, but does not erase the one actual task's
retirement accounting. It does not synchronously wait on disk work or a callback
that needs the UI pump. A retired pipeline cannot dispatch a successor after
owner disposal. A sink that never completes can retain that one pipeline and
its required operands for the lifetime of the window; no later gestures add
more work. Arbitrary in-process I/O cannot be forcibly unwound safely. Such a
failure may defer history reading and future passive mount indefinitely, while
ordinary game commands, polling and table presentation remain available. This
bound is per application/window owner, not a claim about all independently
constructed windows or an archive service's internal recorder queue.

## Composition and frame contract

`TableApplication` owns update ordering: pump admitted completions, reconcile
accepted game/history inputs, route local input, prepare current geometry, then
publish drawing and hits together. That order remains unchanged. The extraction
does not skip composition when a snapshot revision is unchanged: pointer motion,
animation time, text scale, viewport and overlay state can all change geometry.

The history painter consumes one view, but the current-card resolver remains a
live composition function. It verifies exact game and viewer and uses current
disclosed occurrences. A historical ID contributes no card face. Inspector
revocation still occurs on accepted state changes before new hits are consumed.
History input isolation keeps the established permitted intent set, backdrop,
keyboard escape and noncommitting source reader.

The artwork workstream owns raw texture references, drawing-frame pins and the
window's GPU lifetime. This workstream changes none of `CardArt`, `TableWindow`,
`DrawCommand`, `DrawList`, `DrawingFrame` or texture ownership. No history view
retains a `Texture2D` or copied current `Card` for future use.

## Compatibility with the live-update candidate

The newer candidate already defers passive mount while history I/O is loading
or a History/inspector overlay is active. Its adapter must read the new owner's
surface and `HasOutstandingIo` rather than the removed loose Loading flag.
Closing the panel or expiring a logical request never clears that mount blocker.
No history session, concrete client, cancellation source or mutable archive
document moves into the bootstrap/shared contract or passive checkpoint.
Only neutral immutable checkpoint data could cross a future generation boundary;
reattachment must use the retained owner's current explicit role, never a private
history session or copied cancellation/task object.

An active export must either finish under its original owner or remain a reason
to defer a mount; closing its visible panel is not proof its I/O completed. A
closed retired owner rejects late callbacks. A new presentation initializes a
fresh reader and receives admitted live history through the retained session
ports. Persisted archives remain owned by the existing service. The extraction
creates no dependency on the live-update branch and does not merge that branch.

## First implementation boundary

| Files | Work |
| --- | --- |
| New `Mana.Game/Presentation/HistoryPresentationSession.cs` and small related internal values | Semantic local state machine, request leases, immutable view, detail cache |
| New `Mana.Game/Presentation/HistoryDetailsFormatter.cs` | Existing pure historical text reduction moved with explicit inputs |
| `TableApplication.History.cs` | Thin intent/shell integration and existing archive-recording closure wiring; remove reader state and background closures |
| `TableApplication.HistoryView.cs` | Paint from captured immutable view; remove navigation mutations and detail formatting/cache ownership |
| Root-owned `TableApplication.cs` integration seam | Construct the reader after the owner-post queue exists, replace the old CTS/detail teardown with reader disposal, retain existing update/composition order |
| `TableApplication.Input.cs`, `.Automation.cs`, `.Overlays.cs`, `.MatchIntents.cs`, `.QuickMana.cs`, `.TableChoices.cs`, `.TablePresence.cs` | Replace direct history-flag mutation/read with the narrow read-only surface/current-scope predicates and established transitions |
| New `Mana.Table.Tests/HistoryPresentationSessionChecks.cs` | Controlled request, navigation and lifecycle cases against the real owner |
| Existing `HistorySmoke.cs` | Extend only if needed to cover a missing full source/reader/return journey |

Root owns the shared architecture/testing guides, CI and runner integration.
The updates specialist reserves the application constructor/update/disposal
seams. The history specialist supplies the exact small construction/disposal
patch for root to integrate with those changes and does not edit `TableWindow`
or updater lifetime wiring. Live model observation can stay in the existing
`ReconcileHistory` and `HistoryUpdated` adapters in `TableApplication.History.cs`.
Request one call to `HistoryPresentationSessionChecks.Run` from existing
`PresentationSmoke.Run`, which already runs in the native correctness lane.
No new test executable, framework, public command or CI lane is needed. If the
root prefers existing headless history ownership, call it from
`HistoryAnnouncementPresentationChecks.Run` instead; do not duplicate both runs.

## Verification and acceptance

1. Capture the existing HistorySmoke journey before extraction at 1000 by 740
   and 1540 by 980, 130% text. Retain selected IDs, detail strings, enabled hits,
   input ownership and zero gameplay receipts for history-only input.
2. Exercise the new owner with held list/read/export completions across Close,
   reopen, live-to-archive, archive-to-live, viewer A-to-B-to-A, null and dispose.
   An obsolete success and obsolete failure must both be inert; the current
   request must still complete normally after each negative control.
   Include a provider that ignores cancellation. Let its old actual task finish
   but hold the posted view completion; permit a fresh current reader request
   to succeed, then release that old completion. Neither old success nor old
   failure may restore a model, disclose a previous viewer, or change the current
   message.
   Separately hold the actual uncancelled port task across many Close/Open and
   Retry gestures: at most one pipeline and one pending descriptor exist, no
   second port call dispatches early, and expired pending reads never dispatch.
   Hold an already-started destination write after Close: Loading becomes false,
   `HasOutstandingIo` stays true, passive mount remains ineligible and normal
   gameplay still works. Settle the write without pumping its old view callback
   and verify actual accounting retires without reopening History or replaying.
3. Cover previous/next/newest, seen cursor, expired selected action, page clamp,
   model replacement using the same action ID, private-report toggling and the
   captured export scope. Verify request generation is unchanged on idle frames.
   Measure with an out-of-range page, then reconcile it: displayed text, disabled
   navigation and all published hit scopes must come from the same final view.
   Retire the measurement basis before reconciliation as a negative control;
   no old text/new-scope combination may be published.
4. Reuse the current historical-source concealment and read-without-command
   checks. Confirm all existing card-reading pointer/escape/return assertions
   and text remain unchanged through production composition.
   Keep the accepted-history recorder subscribed once, and verify finish,
   handoff and disposal preserve the same coalesced archive closure facts. A
   failing archive read or closure must leave an ordinary current game command
   available through the existing gameplay owner.
5. Run `--smoke-presentation`, the full headless presentation suite, and the
   existing two-size `card-reading` native UI case. Inspect the retained captures.
   Controlled history data does not establish Magic rules or new human approval.
6. Run the full client suite and maintained-document checks on integrated source.
   Root runs required exact-commit remote checks before PR readiness.

The extraction passes when one owner controls reader state and requests, the
application no longer mutates that state directly, every completion has an
explicit lease, and production journeys preserve observed behavior. File count
or line reduction alone cannot pass it. Human review remains separate; there
is no new UI approval claim for a behavior-preserving refactor.

## Frame cost evidence and later slices

The current headless runner already records aggregate/mean Update milliseconds
and explicitly disclaims GPU frame rate. Preserve that distinction. Add allocation
and percentile reporting only in a focused profiling slice using the existing
runner: synchronous Update wall time and UI-thread allocated bytes after fixed
warmup, with identical snapshots, clock, pointer sequence, window size and build.
Keep network/file work outside the measurement unless the test explicitly names
the complete update path. Record median, p95/p99, allocations/frame, command/hit
counts, runtime and source identity. A GPU frame benchmark is a different witness.

Use quiet four-seat play, six-seat crowded play, active source reading/payment,
large stack/history reading, and a pointer/animation-only sequence. Retain first
failures and compare the same workload before/after. Set future numeric budgets
from those baselines with stated headroom; do not invent a frame-time claim or
flaky machine-time CI gate in this extraction.

| Stage | Deliverable | Gate before proceeding |
| --- | --- | --- |
| P1 | Complete history-reader owner described above | Ownership and held-completion controls plus unchanged real input journey |
| P2 | Current-frame HUD reservation and placement plan | Pure plan over explicit current inputs; one plan supplies paint/hits; original source-to-payment and frozen-time tests remain valid |
| P3 | Separate remaining local reader/overlay lifetimes | Each selected subsystem has independent state/invalidation and a full existing gesture; no universal overlay manager or application-reference escape hatch |
| P4 | Cache immutable facts where measured work warrants it | Key covers audience, authoritative data and relevant preferences; pointer/clock/pose recomposition still runs; measured reduction without changed draw/hit transcript |
| P5 | Smaller application composition surface | Update orders existing owners and publishes their accepted plans; no owner can mutate another feature's private state |

Stop and revise the design if extraction requires access to the entire
`TableApplication`, copying mutable client/provider state, changing established
gestures, retaining concealed cards, or making painting advance request state.
Do not implement all later stages merely to achieve a smaller class.

## Design review resolution

On 16 November 2026, technical-lead review accepted the history-owner direction
and requested explicit accounting for I/O that outlives logical cancellation,
plus one final page/view binding for text and hit authority. This revision adds
the single actual pipeline and replaceable pending-read bound, keeps active
exports accounted after Close, defines `HasOutstandingIo` for future mount
deferral, and specifies the measurement/finalization publication sequence.
Archive recorder closure remains with its existing owner and outcome semantics.
No global game Busy flag, provider authority, automatic export replay or private
session transfer is added.

The technical lead established the baseline with public master `0eaa9f70`:
the existing real-GPU `card-reading` journey, including HistorySmoke, passed
120 checks at 1000 by 740 and 120 checks at 1540 by 980. Matching logs and captures
are retained by the lead for semantic and visual comparison. Those results
describe the pre-extraction source only; they do not validate this design's
implementation or supply human UX approval.
