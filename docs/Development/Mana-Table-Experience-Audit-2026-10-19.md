# Mana Table experience audit

October 19, 2026

Mana Table has a much stronger entrance, builder and table than it did at the start of these sessions. The main remaining gap is understanding: players still have to reconstruct what happened, whose decision is pending, and whether their last action worked. The next work should make those facts visible throughout a complete game and deck experiment.

This audit focuses on Commander players who already know Magic and are learning Mana Table. It reviewed [PR 6](https://github.com/proflayton/Mana-Table/pull/6), which was open at the review checkpoint, plus the released 0.2.37 experience. PR 6 subsequently merged; its battlefield, hand-placement, graveyard-action, allocation and selection improvements were not part of 0.2.37. Separate local equipment work is outside this audit. The findings below record the review checkpoint; the follow-up implementation addresses them.

Findings combine current source, existing captures and the reported play sessions. A code-confirmed failure path is distinguished from a newly reproduced incident. Historical controlled screenshots establish layout and wording where the relevant source is unchanged; they do not establish real-card recognition or readability at the physical window size.

October 20 follow-up: [PR 7](https://github.com/proflayton/Mana-Table/pull/7) merged the recovery, public-state and deck-review improvements addressing these findings. Release 0.2.39 ships the earlier `c11a3094be6c95ba1bddbafbe54aa7ad8fe3a7db` baseline; the merged PR 7 behavior and subsequent equipment/analysis integration require a later package. The findings below remain a record of the audit, rather than a current list of unfixed defects. A merged change, completed candidate checks and a published installer are separate milestones.

## What the recent sessions taught us

1. **A valid action needs an understandable reason.** The Toothy recipient choice and Gaea's Touch interaction show that an engine-correct sequence can still feel wrong. Show the source, purpose and available choice beside the relevant cards or players.
2. **Acknowledgement belongs at the action.** The loading complaint, failed imports and repeated priority passes all expose uncertainty about whether input worked. Distinguish a submitted action, an engine resolution, another player's choice and an interrupted connection.
3. **Persistent facts need persistent presentation.** Creature totals, commander tax, counters and current combat characteristics should remain readable without reconstructing history.
4. **Guidance should preserve attention.** Contextual hints, actionable graveyard piles and selection collapse help players stay with their task. Learning how to click must not remove the explanation of the current decision.
5. **Deck advice needs an observable outcome.** A dismissal should look dismissed. A test should retain the played list and help answer the question it was started to investigate.
6. **Passing tests and understandable play are separate acceptance requirements.** Keep the build gates and engine checks. Add physical-window and real-card walkthroughs for the experience they cannot demonstrate.

## Improvements to preserve

- The intent-based home, staged startup, scoped loading surfaces and startup retry give the app a coherent entrance. Deck saves have inline feedback; host/join and game entry have foreground transitions.
- The builder has live card-linked structural issues, autosave status, Undo/Redo, a real Considering list, versions and a route into a Forge game and back to tuning. Empty-deck copy is guarded. A required builder review wall is unnecessary; explicit multiplayer deck confirmation and readiness serve a different purpose.
- Per-ability passing until the next turn, visible Undo, Hold and Full control address the Arid Mesa frustration while preserving other legal plays and required decisions.
- PR 6 substantially addresses silent battlefield overflow, enlarged selections getting in the way, opaque damage assignment, extra land-placement discovery and actions from owned graveyard/exile/command piles. Preserve every battlefield occurrence and its identity while improving density.
- Commander tax/phasing presentation, stack/source inspection, history, contextual help, reduced motion, sound preferences and explicit update installation already exist. This is a refinement of those systems.

## Findings in priority order

P1 means address in the next reliability and clarity work before adding another major feature. P2 means an important completion gap. Ordering reflects player impact and the recent feedback, rather than measured incidence.

| Priority | Finding | Player consequence | Desired result |
| --- | --- | --- | --- |
| P1 | File reads escape import recovery | An unreadable or disappearing deck file can close the app. | Keep the current screen and deck, explain the read failure, and offer another file or retry. |
| P1 | Multiplayer recovery and exit are incomplete | A stale table looks live; eliminated players and multiplayer error states lack a clear departure route. | Show connection freshness, safe recovery, a watching state and an explicit Leave table action. |
| P1 | Public player and anonymous-card state is incomplete | Poison/energy/experience totals are absent; ordinary face-down cards lose public type and P/T. | Show all engine-authorized public facts while preserving hidden identity. |
| P1 | The legality label exceeds the checks performed | A structurally valid Commander list can say “Checks passed” without the bundled per-card eligibility check. | Separate structural, format and save status; apply the same bundled eligibility rules across the playable list. |
| P1 | Pending actions and decision ownership are conflated | Submitting, resolving, auto-following and waiting for another player share generic waiting text. | Identify the pending action, current chooser/priority owner, source and purpose. |
| P2 | Outcome feedback can misdescribe events | A fizzled spell gets a “JUST RESOLVED” heading; draws are partly inferred from count changes; shuffles lack explicit outcome feedback. | Carry actual outcomes and public draw/shuffle events through to presentation. |
| P2 | Recommendation decisions do not change the actionable view | Keep current/Dismiss append history but leave the recommendation looking undecided. | Show the latest decision for this deck version, with review and reopen controls. |
| P2 | Tests preserve versions without making comparison easy | Earlier tests lack a read-only played-list/diff; aggregate totals cannot answer the first-three-land-drops question. | Compare without restoring; record question-relevant observations and label early-ended tests accurately. |
| P2 | Source conflicts require whole-list decisions | One conflict blocks merging independent local and source changes. | Resolve individual conflicts or clearly preview the scope and baseline effect of each whole-list choice. |
| P2 | Physical readability remains unproven | A dense board may fit while critical labels become too small in a smaller window. | Maintain readable decision text, adjustable detail and reliable occurrence inspection at actual display sizes. |

### Import recovery

`ImportFile` calls `File.ReadAllText` before entering the scoped asynchronous import operation and has no local catch. A read/access exception can reach the outer application handler and end the process. The parser improvements do not cover that earlier failure. This is a source-confirmed path; this audit did not induce a locked-file crash.

Keep the picker/editor state intact, identify the affected file, and separate “could not read the file” from “could not understand these deck lines.” Preserve partial-import diagnostics without silently discarding unmatched cards. A missing or unreadable file must leave the app usable. [Import path][import] · [Application handler][program]

### Multiplayer recovery and elimination

Polling failures retain the last board and prompt while showing a dismissible error line. There is no visible synchronization age. Multiplayer `error` excludes Return and disables Concede; an eliminated viewer gets a badge but remains in the generic following presentation until the whole game finishes.

The transport already supports seat-preserving reconnection and ordered receipts. Expose that safely: “Reconnecting,” the last synchronized state, and a recovery or leave action. A stale prompt must not look current. An eliminated guest should be able to watch or leave without conceding again; a host must understand the consequence of closing the hosted table. “Return to lobby” must remain distinct from leaving an ongoing shared game. [Client failure handling][session] · [Dock and departure controls][dock] · [Lobby return and leave][lobby]

### Public state and decision purpose

Project nonzero public player counters for every seat. History about infect damage cannot replace the current poison total. Ordinary anonymous battlefield cards should retain authorized public characteristics, including modified P/T, damage and public abilities. The allocation-specific projection already distinguishes public anonymous state from hidden identity and provides a useful implementation precedent. [Player snapshot][snapshot] · [Ordinary card projection][anonymous] · [Anonymous allocation state][allocation-public]

Separately present active turn, priority owner and private chooser. Stack controller information currently arrives as a name and is dropped by the native contract; a source name alone cannot distinguish two opponents' identical triggers. A submitted command also needs immediate acknowledgement before the next authoritative state arrives. [Stack contract][stack-contract] · [Decision dock][dock]

Toothy remains a focused discovery/validation case. Trigger narration, source cards and detailed engine target text already exist. The gap is a concise, persistent purpose next to named legal recipients; the exact live wording still needs capture. Keep the legal self/other-player choice and the targeted player's optional search. Never auto-target self or describe the trigger as something the player must activate. [Target guidance][target-guidance]

### Honest outcomes

The event adapter correctly writes “did not resolve” for `hasFizzled`, but still labels its event `resolved`; the prominent native heading always says “JUST RESOLVED.” Carry an explicit outcome so heading and detail agree. Distinguish countering or other causes only when the engine supplies them.

Use actual engine draw/shuffle events for public counts and confirmations. Every library-to-hand move currently has kind `draw`, although its sentence is neutral, and opponent draw cues depend on net count changes. A tutor must not become a draw; a wheel or Brainstorm must not disappear because net hand size hides intermediate events. Repeated polls must not replay effects. [Activity projection][activity] · [Draw inference][presence] · [Resolution heading][outcome]

### Deck trust and the learning loop

Live conformance covers structural rules such as size, copies, identity and commanders. Commander uses a null general card-pool predicate in that path, while analysis uses bundled format eligibility for proposed additions. Apply the bundled rules consistently or label their scope separately. A green structural result must not imply a current online ban-list check; unknown eligibility should remain explicit. [Conformance entry][validation] · [Commander predicate][commander-format] · [Eligibility check][eligibility]

Analysis already exposes estimates, model limits, prerequisites and a curated interaction registry. Preserve that honesty: it does not prove arbitrary combos, infer a human matchup win rate, or understand any free-form deck plan. Keep/Dismiss should visibly update the recommendation's state for its playable fingerprint; earlier decisions should remain historical when the list changes. [Recommendation controls][recommendations]

Results need “View tested deck” and “Compare to current” without restoring the old version. For “Can I make my first three land drops?”, total lands and total turns are insufficient: retain a short per-own-turn observation or explicitly leave the answer to the player's notes. Put “ended early/conceded” prominently on early-ended experiments. The existing small-sample warning and exact-version retention are strengths. [Results and versions][results]

Archidekt support is public import and manual, previewed source refresh. Local changes stay local; it is not continuous or authenticated two-way synchronization. Preserve the safe copy and stale-preview guards. Add per-card conflict resolution, or explain whole-list replacement and source-baseline advancement before confirmation. [Source reconciliation][source-review]

### Density and physical readability

The native client paints to fixed design surfaces and scales the entire result into the viewport. Its screenshot path saves the design surface itself. A capture stored under a small-window test directory can therefore look readable at 1600 pixels while saying little about the actual smaller window. Reduced motion already exists; adjustable text size is absent from the reviewed preferences. [Surface scaling and capture][scaling] · [Text sizing][text]

Keep every permanent rendered. Use stable clusters, readable totals, focused inspection and active-combat emphasis to make occurrences findable. Validate real art, duplicate names, a mixed crowded board, and 50 distinct creatures in actual window captures. Check text, pointer targets and adjacent-card selection at the display's real scale and DPI.

## The presentation standard

Every important interaction should answer five questions: **What changed? Why? Whose choice is this? What can I do? What will my action do next?**

| Moment | Presentation requirement |
| --- | --- |
| Action available | Identify the card/player and the legal action at its location. |
| Action submitted | Acknowledge the intent immediately; preserve context while awaiting the engine. |
| Choice pending | Keep source, purpose, legal recipients and required amount visible. |
| Another player acting | Name the public decision owner without exposing private options. |
| Outcome received | Report the actual engine event and leave lasting public changes visible. |
| Connection uncertain | Mark retained state as stale and offer safe recovery or departure. |

Use the same action gestures across hand, battlefield and owned piles where permissions permit. For effects such as Gaea's Touch, expose availability on the hand card while retaining Forge's actual ability, response window and resolution choice. Guidance must not invent a permission or silently change a rule.

## Reference patterns worth applying

Arena's documented smart-priority exceptions preserve useful interaction windows, while stops and Full control remain available. The lesson for Mana Table is predictable automation with visible scope and expiry; additional card-specific exceptions need careful explanation. [Wizards smart-priority guide](https://magic.wizards.com/en/news/mtg-arena/announcements-october-27-2025)

Blizzard's 2018 effects discussion describes VFX as information and explains how Magnetic previews valid targets and the prospective attachment. Apply that principle to source/target links, persistent effects and outcome cues, with modest emphasis for routine changes. This is a design precedent rather than a claim about today's Hearthstone client. [Hearthstone effects design](https://hearthstone.blizzard.com/en-us/news/22552047/developer-insights-the-art-behind-the-science)

Archidekt documents contextual categories, multiple deck views, visible autosave activity and a playtester accessible from a deck. Mana Table can connect familiar organization tools to its rules-backed testing, version comparison and recorded observations. [Archidekt FAQ](https://archidekt.com/faq)

Microsoft's game text guidelines emphasize physical readability and configurable text. Use them as a design target alongside actual window/DPI checks, including text inside badges and dialogs. [Xbox Accessibility Guideline 101](https://learn.microsoft.com/en-us/xbox/accessibility/xbox-accessibility-guidelines/101)

## Recommended delivery order

1. **Reliability and trust:** contain file/clipboard access errors, add safe disconnected/error/eliminated navigation, complete public-state projection, and align legality labels with actual checks.
2. **Decisions and transitions:** separate submitted/resolving/waiting states; expose chooser and stack attribution; add truthful draw/shuffle/non-resolution outcomes; validate the real Toothy journey.
3. **Deck experiments:** reflect recommendation decisions, inspect/diff tested versions, record question-relevant observations, then improve source conflict resolution.

Physical readability, preserved drafts, scoped input and hidden-information checks belong in every delivery. Retain the passing build gates, and validate the installer from the actual merged source before publishing it.

## Follow-up implementation

The follow-up addresses all ten findings above. The original findings remain a record of the review checkpoint.

| Finding | Implemented behavior |
| --- | --- |
| Import recovery | File and clipboard access failures stay inside the current flow, with the saved deck preserved. |
| Recovery and departure | Retained state is marked stale, submissions pause until reconciliation, and watching, reconnect and host/guest departure have explicit routes. |
| Public state | Nonzero player counters and authorized anonymous-card characteristics survive projection; every counter type is available through paged details. |
| Legality | Structural checks, bundled format eligibility and save state have separate labels; eligibility covers actual playable sections and excludes Considering. |
| Decision ownership | Submission feedback, public chooser/priority roles, stack controller and persistent named recipient choices identify the current action and purpose. |
| Outcome feedback | Explicit resolution outcomes and engine draw/shuffle counts replace guesses; neutral library-to-hand movement is not reported as drawing. |
| Recommendation decisions | Keep, Dismiss and Tested leave the undecided view for the current playable fingerprint; review and Reopen preserve history. |
| Deck experiments | Saved lists and quantity comparisons are read only, early endings are labeled, and free-form questions explicitly require player observations. |
| Source conflicts | Whole-list choices require a separate confirmation with affected quantities and baseline consequences; copying previews the complete source list, including Considering. |
| Readability | Text has 100%, 115% and 130% settings, smaller-window captures include the actual composited viewport, and crowded-board checks locate and inspect a named occurrence among fifty creatures. |

The implementation uses the audit's explicit manual-observation and whole-list-preview alternatives. It does not add an automatic first-three-land-drops measurement, individual conflict resolution, authenticated uploads or continuous synchronization. Bundled eligibility does not claim a live online ban-list check.

The [testing guide](Mana-Table-Testing.md#experience-clarity-and-recovery) identifies the new regression journeys and their evidence. Source review uses a controlled external preview; actual source application and stale guards have separate offline engine coverage. The optional real-art board uses controlled current characteristics. Automated window captures improve the evidence but do not replace a human play session or a broader DPI study.

## Human acceptance journeys

| Journey | Completion condition |
| --- | --- |
| First import | A common community list imports with visible unmatched rows; an unreadable file leaves the app and current deck intact. |
| Deck readiness | Structural, bundled-format and save results are distinguishable; a card banned in the bundled dataset gets a card-linked issue. |
| Toothy and Pir | The trigger's purpose is understandable; self/other recipients and search/decline work without revealing private choices or inventing a shuffle. |
| Scoped passing | Arid Mesa can be ignored until the next turn, visibly undone, and never suppresses another legal play or required choice. |
| Public state | Poison/energy/experience and anonymous modified combat stats remain understandable across four seats and reconnect. |
| Outcome feedback | Resolution and non-resolution differ; a wheel, tutor and actual shuffle receive accurate privacy-safe feedback. |
| Dense combat | At actual window size, locate a named occurrence, inspect it, assign damage across duplicate names/pages, and keep the draft intact. |
| Interrupted or eliminated player | Identify stale state, recover safely or leave, and understand host-departure consequences. |
| Deck experiment | Dismiss advice once, play a saved version, end early or finish, compare to the current list and choose the next change without restoring just to inspect. |

Observe task completion, repeated clicks, incorrect-target attempts, missed state changes and explanations the player has to ask for. Establish a baseline in the next play session; this audit does not invent measured success rates.

## Source references

Links below pin the reviewed gameplay source so findings remain reviewable as the branches change.

[import]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.Setup.cs#L28
[program]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/Program.cs#L37
[session]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Client/ClientSession.cs#L50
[dock]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.Arena.cs#L269
[lobby]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/SyncLobby.java#L139
[snapshot]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/MatchSession.java#L539
[anonymous]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/MatchSession.java#L655
[allocation-public]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/MatchSession.java#L1113
[stack-contract]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Contracts/Engine.cs#L224
[target-guidance]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Magic/ControlGuidance.cs#L69
[activity]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/MatchActivity.java#L100
[presence]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Renderer/TablePresence.cs#L30
[outcome]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.Actions.cs#L59
[validation]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/DeckEditor.java#L118
[commander-format]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-core/src/main/java/forge/deck/DeckFormat.java#L69
[eligibility]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/forge-api/src/main/java/forge/api/DeckAnalysisFormats.java#L18
[recommendations]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.Analysis.cs#L201
[results]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.WorkshopView.cs#L125
[source-review]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.WorkshopView.cs#L283
[scaling]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Table/TableGame.cs#L317
[text]: https://github.com/proflayton/Mana-Table/blob/9cf1eb542bcfd4f92fd3602efc0a2ee9881a6eb6/mana-native/Mana.Renderer/Canvas.cs#L250
