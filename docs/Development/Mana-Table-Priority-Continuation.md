# Server-owned priority continuation

**Implemented source candidate; release qualification and exact-candidate human
review remain pending.** Two people and two bots can register their passing
intent once. Every actual priority window and required choice remains owned by
the rules engine. This document describes the new source path, not a claim about
the currently installed release.

## Game references and intended experience

Arena's [smart-priority guide](https://magic.wizards.com/en/news/mtg-arena/announcements-october-27-2025)
describes routine windows that its client skips, exceptions that restore a useful
response, and plays that still require a phase stop or full control. The observed
interaction is the player's supplied Arena reference: a phase stop beside the
portrait and the normal response action in one stable location. We adapt the
explicit control and predictable stops. Arena's published exceptions are not a
complete relevance model, and the article does not describe its server design.

MTGO's [September 2025 update](https://www.mtgo.com/news/through-the-omenpaths)
describes yielding everything currently on the stack, with another gesture
needed for later arrivals. That bounded consent is the reference for **Resolve
these**. It does not authorize future choices or a whole turn's unknown effects.

The player journey keeps the existing gestures. **Auto** means continue under
the displayed policy; a saved phase stop or a newly relevant response brings
control back to the player. **Resolve these** names the current captured queue.
**Take control** withdraws continuation and shows its acknowledged state in the
same response area. Reading and grouped progress remain presentation concerns;
neither animation completion nor a renderer's playable-card highlight authorizes
a pass. No additional row of competing passing buttons is proposed.

## What the source already does

`MatchSession.completePublication` schedules finite passes from the completed,
coherent table publication. `FiniteStackYield` retains exact captured execution
members and guards. A queued real `InputPassPriority` is checked again on the
rules state owner before dispatch. The client grants and observes this consent;
it does not have to send a human pass for every captured trigger. Its 350 ms /
32-command worker slice is not a server delay per trigger.

Supported hosts also own ordinary Auto and explicit quiet continuation. Native
code requires both negotiated `priority-continuation-v1` support and the actual
host's `priorityContinuation` frame. A new local adapter connected to an older
TCP host therefore keeps the legacy client path. The session reserves one
dispatcher while a grant or withdrawal is pending; it cannot run a second local
Auto worker alongside the server. Ignored activated abilities remain separate,
seat-owned preferences.

The current source adds a controller-owned scan generation and an input/resource
lease for automatic passes. An intervening rules event invalidates absence
evidence, including when the same input is republished. Queued dispatch rechecks
the lease and holds the unanswered input if it has expired. This safety boundary
is retained by standing Auto; registering intent does not rescan mana or bypass
that input's completed controller scan.

`TimedAiHostPacing` is a separate presentation owner. The fast full-game campaign
does not test its timing. The real 110-trigger fixture now uses the shipping
2.4-second pacer, retaining real AI evaluations and all resource, consent and
terminal-receipt assertions. This controlled journey verifies the captured queue;
it does not establish the cause of the user's live delay. A slow archive-arrival interval
alone cannot establish which of these owners caused a delay.

## One consent owner, existing rules dispatch

The existing per-seat server boundary accepts the closed native
`GrantPriorityContinuation(GameId, DecisionId, Sequence, GrantId, Mode,
PhaseStops, QuietYieldToken)` command. Sequence is monotone within the existing
connection generation; each replacement has a fresh identity. Registration
requires the current owned priority input and a registered observation baseline.
`PhaseStops` contains the existing six own-turn phase keys, not opponent stops.
Quiet retains its original engine token. Finite continuation keeps its exact
member witness and separate optional-decline permission.

`NoResponse` passes only with certified `Absent` availability. `Available` and
`Unknown` hold its accepted intent without consuming the input; ordinary manual
answers and required choices can continue while that intent remains registered.
The prompt's closed `responseAvailability` distinguishes those three results;
legacy `canAutoPass` is true exactly for `Absent`. Explicit `Quiet` retains its
existing unchanged-window behavior, including windows with a response available.
A changed public window revokes Quiet rather than silently replacing its token.

`priorityContinuation` publishes only that audience's sequence high-water,
accepted grant identity/mode/stops, `Idle`, `Active`, `Held` or `Revoked` status,
reason and automatic-pass count. Held standing consent and active finite consent
are mutually exclusive. Switching between them first observes withdrawal.
Another seat's private response availability never enters this frame.

After a coherent publication, queue at most one actual priority operation. At
execution, recheck the same input, consent epoch, stop conditions and verified
engine permission, including the scan/publication generation. A transported
`canAutoPass` flag alone cannot prove that no intervening mutation occurred;
the current source's input and scan leases supply that separate recheck. Consume the current
priority scan; do not launch a new funding query under the publication lock or
rescan all four players on every tick. Other seats acquire their current scan
when they own a window. Current planning can borrow and refund the
live mana pool, and a manual-tap correction window deliberately leaves
availability unverified. Unknown availability means hold.

The closed availability value is produced by the completed controller scan.
A replacement rules provider must supply equivalent evidence or report unknown;
card highlighting cannot fill the gap. Legacy providers may omit the field.

No-response Auto, quiet consent and a finite queue retain their distinct scopes.
The applicable policy must hold or retire for a phase stop, outside action,
required choice, changed terms, newly available response, lost evidence or
connection retirement. It cannot silently replace a quiet token or recapture a
new trigger group. Optional payment answers require their own certified offer;
passing policy alone cannot answer them.

**Take control withdraws without requiring a current priority prompt.**
`RevokePriorityContinuation(GameId, ThroughSequence)` fences a pending install
as well as an accepted grant. A late withdrawal through N cannot cancel a
replacement N+1; replay of a withdrawn positive instruction cannot reactivate
it. The backend recognizes an exact repeated grant before consulting its retired
original prompt, but the client never blindly retries a positive command after
an uncertain receipt. It reobserves and preserves negative cleanup authority.
Reset, close, audience handoff and match-changing operations retain that cleanup
without restoring the old presentation. A pass committed before the withdrawal
acknowledgement remains committed. Reconnection starts without active consent.

## Reading credit and bounded publication

Every active human peer registers an accepted-frame observation baseline before
the table may elide intermediate priority-only publications. This includes a
full-control player who never enables Auto. The server still executes each real
rules pass and observes its current input lease; it merely avoids rebuilding and
transporting a whole frame for each unchanged priority handoff.

The optional `priorityObservation` frame contains `boardRevision`,
`publicActivitySequence`, acknowledged values for those two cursors,
`registered` and `overflowed`. The closed prompt-free
`AcknowledgePriorityObservation(GameId, BoardRevision, PublicActivitySequence)`
returns credit only for an actual frame from a completed table publication,
committed for all audiences before observer callbacks. An ACK
consumes no input, creates no undo barrier, and is not a gameplay outcome.

Live synchronization retains its existing 8 MiB encoded-frame ceiling. The
4096-entry delivery journals and 64-event credit window bound unattended progress;
they do not guarantee unlimited boards or every possible interaction. Receivers
validate the complete retained union without numeric truncation. Exceeding a
journal's entry bound explicitly holds Auto; the transport's byte ceiling is a
separate limit, especially for unusually large boards or atomic event bursts.

`ClientSession` derives ACKs from frames it accepted, not the provider's decoded
cache or a rejected poll. It registers the initial baseline, then coalesces after
16 public events or an `awaiting-observation` hold. The game explicitly closes
`SetPriorityObservationReady` before its first frame and before every observation
pump, then opens it only after current input, foreground and announcement/reading
gates permit. Graphics-free drivers default to
ready. One metadata task runs without setting user-action `Busy` or `Polling`;
gameplay and negative consent take scheduling precedence, and an epoch/audience
change retires its stale publication. Failed metadata waits for a fresh accepted
observation and bounded backoff. There is no ACK on every unchanged poll.

The server permits at most 64 meaningful public events ahead of a registered
human observer. Its audience-safe delivery journal retains at most 4,096 events,
releasing only acknowledged prefixes. A missing peer baseline disables priority
publication elision, preserving compatibility with older guests. Exhausted
registered-observer credit or overflow explicitly holds automatic progression;
overflow does not claim complete history. Required choices and unrelated new
actions remain visible.
Known captured progress can share a presentation beat, while a new spell or
unrelated announcement still needs its ordinary reading opportunity.

## Native roles and optional presence

Interactive `ClientSession` owns one ordinary command lane and its negative
consent capabilities. `ClientSessionAuthority.ObserveOnly` validates and retains
the complete immutable snapshots for projection replay, including recorded grants,
without acquiring their authority. It cannot run commands, source-action reads,
Auto, finite continuation, observer ACKs or orphan revocations. Replay never
rewrites a real frame to make it look unauthoritative.

Optional `humanPlayerIds` contains actual registered player IDs from the table's
human seat roster; legacy player `human` flags are viewer-relative and cannot
supply it. Presence skips cosmetic exchanges when that known roster contains no
other non-eliminated human. A roster change retires pending cosmetic results,
including solo-to-multiplayer-to-solo transitions. Missing roster metadata keeps
the old optional polling behavior. These cosmetic facts do not authorize a pass.

Bots keep their separate policy owner and private observation. Externally
automated controllers skip the human availability scan, so human `canAutoPass`
cannot authorize bot shortcuts. A bot may propose a real response; optimize its
observation, evaluation and announcement timing without replacing it with an
always-pass controller.

## Required qualification before release

Use the existing quiet, finite, connection and native lifecycle tests. Verify
actual dispatch counts and acknowledged authority, not an equivalent-looking UI.

| Journey | Required result |
| --- | --- |
| Verified empty response, then an affordable instant | Pass the first window; hold at the new response. |
| Incomplete scan or manual-tap correction | No automatic dispatch. |
| Queued pass, then Hold, phase stop or required choice | Recheck refuses any later dispatch covered by the acknowledged withdrawal. |
| Public or mana state changes and returns to its old appearance | Quiet consent remains interrupted; matching final pixels do not restore it. |
| Replacement consent, reconnect or late callback | Old authority cannot affect the replacement. |
| Finite queue with new targets, ordering, outside child or missing evidence | Stop the captured continuation without silently recapturing. |
| 110 real triggers, TCP guest and two actual bots | Exact outcomes, resources and passes remain correct with shipping pacing; no full delay per trigger. |
| Existing Auto/Take control gestures at both supported sizes | A person can start, understand and interrupt the accepted mode; automation alone cannot approve this UX. |

Measure consent-to-dispatch, queued rules execution, provider return, accepted
observation and presentation separately. Keep first failures and fixed semantic
deadlines. A faster result is insufficient if it skipped a response, hid missing
history, lost a decision owner or weakened the test's stopping conditions.
