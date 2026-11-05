# Server-owned priority continuation: proposed next slice

**Design proposal, not implemented or released.** The aim is to let two people
and two bots play without repeatedly confirming already agreed passes. Every
actual priority window and required choice remains owned by the rules engine.

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

Ordinary human Auto and quiet continuation still live in the client and send
per-prompt commands. Java supplies verified `canAutoPass` permission and the
`QuietYieldBaseline` token; ignored activated abilities are already seat-owned
server preferences. These are different facts from the player's standing intent.

The current source adds a controller-owned scan generation and an input/resource
lease for automatic passes. An intervening rules event invalidates absence
evidence, including when the same input is republished. Queued dispatch rechecks
the lease and holds the unanswered input if it has expired. This safety boundary
is implemented; registering standing Auto intent remains the proposed next step.

`TimedAiHostPacing` is a separate presentation owner. The fast full-game campaign
does not test its timing. The real 110-trigger fixture now uses the shipping
2.4-second pacer, retaining real AI evaluations and all resource, consent and
terminal-receipt assertions. This controlled journey verifies the captured queue;
it does not establish the cause of the user's live delay. A slow archive-arrival interval
alone cannot establish which of these owners caused a delay.

## One consent owner, existing rules dispatch

Extend the existing per-seat server consent boundary rather than introducing a
second priority engine. An immutable continuation instruction needs the match,
connection generation, consent identity and replacement epoch, explicit policy,
and saved phase stops. Quiet continuation also needs its original engine token;
finite continuation keeps its existing exact member witness and separate optional
decline permission. Registration requires current owned authority. Historical
snapshots, a visual ID, or another seat's available actions cannot register it.

Publish only that audience's accepted mode, reason and progress. The server may
know other seats' private legal options; this must not expose them to a player.
An old host without the negotiated capability keeps the existing client path.
There must never be two active dispatch owners during migration.

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

Keep verified absence, an available response and unknown availability distinct.
The current false `canAutoPass` value alone does not explain which of the latter
two applies. A replacement rules provider must supply that evidence or report
unknown; card highlighting cannot fill the gap.

No-response Auto, quiet consent and a finite queue retain their distinct scopes.
The applicable policy must hold or retire for a phase stop, outside action,
required choice, changed terms, newly available response, lost evidence or
connection retirement. It cannot silently replace a quiet token or recapture a
new trigger group. Optional payment answers require their own certified offer;
passing policy alone cannot answer them.

**Take control is a negative command callable without a current priority prompt.**
Its acknowledgement fences later dispatch from that consent. It cannot undo a
pass already committed before the acknowledgement. Replacement is idempotent;
late callbacks and an old connection cannot revive or cancel a replacement
instruction. Reconnection begins held until a fresh instruction is admitted.

Bots keep their separate policy owner and private observation. Externally
automated controllers skip the human availability scan, so human `canAutoPass`
cannot authorize bot shortcuts. A bot may propose a real response; optimize its
observation, evaluation and announcement timing without replacing it with an
always-pass controller.

## Required gates before enabling the new path

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
