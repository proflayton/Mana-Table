# Review the Mana Table experience

Use the Experience Lab to reproduce a presentation problem immediately, then
review the corresponding live-game behavior. Automated checks establish scoped
command safety, state projection, privacy and geometry invariants. They do not
approve the UX. A count of passing checks, represented cards or captured frames
does not establish that a person can understand or use the screen.

## Design rules learned from player feedback

1. **Design the journey before its controls.** Identify the player's intent,
   source, target, gesture and visible result. Moving the same generic Activate
   button from a dialog to a corner or over the artwork does not fix its purpose.
2. **Use the right detail for the task.** Battlefield faces prioritize identity,
   art and current characteristics; inspection preserves a recognizable full card
   with complete rules and authoritative live state.
   Keep the battlefield context. Reading is noncommitting; hand play uses the card itself.
   Concrete engine actions belong beside their source, with quieter alternatives
   for routine gestures. A live card face can integrate changed characteristics;
   replacing it with a cropped illustration, generic prose panel and rules pages
   loses its identity.
3. **Show state and causality where they happen.** Connect source, pending effect,
   affected objects and settled state. Current stats, counters, resources and
   gained abilities must remain inspectable after an animation, with their sources
   where the engine reports them. Repeated labels and a paragraph of
   changes cannot substitute for that relationship.
   Public-zone inspection also needs the relevant casting route, its cost and
   conditions, and reliable recent zone history. A public capability is distinct
   from a current legal action, especially when inspecting an opponent's card.
4. **Keep asynchronous work from disrupting interaction.** Preload authorized
   likely artwork; prioritize visible and inspected cards. A background observation
   should not flicker, disable or move an unchanged control. Retain local intent
   and revalidate its exact engine permission before commitment.
5. **Let the engine describe the game.** Use authoritative current characteristics,
   action handles and decision constraints. A list intended for auto-pass is not
   a complete action catalog. Simplify presentation without inventing availability,
   skipping required choices or hardcoding a mechanic's maximum.
6. **Treat rejection as design evidence.** Revisit the interaction and information
   hierarchy before polishing its container. Review the actual entry, gesture,
   interruption and result with realistic art and the relevant crowded state.
   Use existing canonical cases and focused checks; record human feedback and its
   scope separately from automated safety results.
7. **Separate seating from attention.** At multiplayer tables, keep controller
   territories and reading direction stable. Turn ownership, priority and required
   decisions are different facts. Direct attention to the relevant source,
   recipients and responder without rearranging everyone's battlefield.
8. **Reduce repeated interaction through explicit, bounded intent.** A burst of
   equivalent choices should support assigning them together, followed by an
   interruptible continuation. Keep every underlying trigger, target restriction,
   response window and effect in the engine. Grouping presentation does not make
   separate effects simultaneous or authorize choosing for another player.

### Accepted hand-play direction: 21 October 2026

The user accepted the linked Adagia preview after reviewing the intact card
reader and then the drag interaction: click to read, drag from the hand or enlarged
face, a compact held card, and a quiet Play land fallback. The hand-play source
was committed as `ab0affd0715`; the reviewed application SHA-256 is
`E261E4540CDC610D5A67057A2F0C207A9E021959E2D5BE4F5A540D64FDC7863D`.
The scripted preview defaults to a `1200x800` window with genuine cached artwork.
Its authored land result and separate real-Forge checks provide different evidence.

This acceptance covers that reader/hand-play direction. Crowded multiplayer
clarity, other ability decisions, artwork latency and quiet-priority behavior
retain their own review status. It does not establish release publication.

## MTGA references reviewed: 21 October 2026

The user's six Arena screenshots make the battlefield/inspection hierarchy concrete:

| Visible evidence | Mana Table design implication |
| --- | --- |
| Battlefield faces emphasize artwork, a narrow name strip and current P/T; full rules are absent at this scale | Give battlefield, inspection and pending effects distinct presentations of the same occurrence. Preserve full card identity and rules when reading. |
| The first local Pridemate is 3/3 beside a small counter badge showing 1; the second is 7/6 beside a badge showing 4 | Make authoritative effective stats primary. A counter quantity is supporting state, not a formula for current P/T. |
| Related cards sit behind the second Pridemate, with small icons on its frame | Keep attachments visibly associated with their host; retain inspectable identity, counter types and ability details. The still does not establish every icon/effect's meaning. |
| The third screenshot's inspected Pridemate displays 9/8 in its own stat box, six +1/+1 counters, and gained Lifelink/Ward {2}; the detail blocks identify Sheltered by Ghosts as their source | Inspection should explain effective state and provenance while remaining recognizably the card. Keep these details available on demand, rather than displaying every explanation on the battlefield. |
| Creature rows, rotated lands, the hand and the turn cue occupy distinct places | Establish a stable spatial hierarchy before adding labels. Test an equivalent hierarchy across four seats rather than copying two opposing halves. |
| The fourth screenshot keeps Exemplar of Light recognizable as a full card, with a Flying explanation adjacent to it and the battlefield continuing behind | Treat the card and its contextual explanation as one inspection area. Its placement must preserve the current source, eligible targets, hand and decision; four seats make this a layout concern, not just a tooltip concern. |
| The fifth screenshot inspects an opponent's Tidings of War with adjacent Cost Modified, entered-the-graveyard-this-turn, Flashback and Amass explanations; the card's rules include a different result when cast from a graveyard | Make public-zone capabilities and casting context discoverable. Keep printed cost, route-specific cost, conditional effect text, recent history and current legal permission distinct. The still does not establish that the opponent can cast it at that moment. |
| The sixth screenshot presents three Ajani Resolute abilities as distinct faces with shared source artwork, individual effects and prominent loyalty costs of 0, -4 and -10; two have bright outlines and one is muted | A real ability choice can use readable game objects and engine-reported availability. Preserve exact costs, source identity and the continuing battlefield; a chosen ability begins its actual payment/target sequence. Do not infer a disabled reason from styling alone. |

Also reviewed sampled frames from [LegenVD's earthbending/landfall match](https://www.youtube.com/watch?v=38QyBm7unec)
(24 November 2025). Full video retrieval returned HTTP 403; public storyboard
samples cover roughly 6:20–13:15 at about ten-second intervals, at 320x180 per
sample. Times below are approximate. These establish visible layout and overlap,
not continuous motion, exact gesture timing or hard-to-read effect semantics.

| Sample | Observation | Apply or avoid |
| --- | --- | --- |
| [About 7:48](https://www.youtube.com/watch?v=38QyBm7unec&t=468s) | An enlarged original face sits beside the continuing hand and battlefield | Preserve identity and context during inspection. |
| [About 8:48](https://www.youtube.com/watch?v=38QyBm7unec&t=528s) | Trigger ordering presents the actual cards between explicit Last/First endpoints | Show order spatially, with its direction clear. A dedicated ordering workspace does not justify a dialog for every activation. |
| [About 10:57](https://www.youtube.com/watch?v=38QyBm7unec&t=657s) | Battlefield/portrait emphasis coexists with pending cards at the side | Direct attention to affected objects while retaining the pending source. |
| [About 11:27](https://www.youtube.com/watch?v=38QyBm7unec&t=687s) | Large links, outlined cards and an enlarged face compete across the board | Show relevant relationships selectively; multiplying every connector across four seats would amplify clutter. |

Wizards' [Sylvan Library design account](https://magic.wizards.com/en/news/mtg-arena/dev-diary-sylvan-library)
and its three UI images show eligibility on actual hand cards, a **Pay 8 Life**
commit action, and a separate ordering step when needed. Our inference: name the
actual command/consequence and preserve source context. Compress repetitive
decisions only through engine-authorized equivalence; their sequential fallback
handles interactions that change what the player can learn or choose.

The official [Smart Priority guide](https://magic.wizards.com/en/news/mtg-arena/announcements-october-27-2025#a-quick-guide-to-smart-priority-for-powered-cube)
documents automatic skips and card-specific exceptions. Our inference: learn
from the pacing goal, while making our yield intent visible and revocable.
Current responder, active turn and required decision ownership need independent
representation at a multiplayer table.

Next comparisons should reuse `crowded-counters.v1` for board hierarchy and
selective cause/recipient feedback, `stack-priority.v1` for response ownership,
`card-reading.v1` for authoritative stats/abilities across board and inspection,
and `ordered-choice.v1`/`trigger-search.v1` for meaningful choices. Compare actual
screens and complete journeys with people. These references propose design
directions; they neither implement nor accept new Mana Table behavior.

The currently accepted reader retains its original image and places effective
state alongside it. State-aware inspection is a follow-up design question, not
a reason to restore the rejected generic rules panel. Missing modifier provenance
requires an engine projection; the client must not guess it from printed text.

### Public-zone capabilities and contextual inspection

The opponent-graveyard reference extends the same design beyond battlefield
stats. The player's intent is to understand a public card that may matter again.
A quiet cue on its public zone leads to that exact card's inspection; reading
sends no game command. Preserve its full face and explain the relevant ability,
casting route, known cost/conditions and reliably reported recent zone transition
beside it. Keep that zone associated with the correct seat at a four-player table.

Use a generic engine projection for public abilities and casting context across
zones. A printed mana cost, an alternative cost, a conditional result and legal
availability now are different facts. A route-sensitive spell can have a different
result from its ordinary cast; retain that conditional rule without presenting
it as an already resolved outcome. Show effective costs or modifier sources only
when the engine can authoritatively report them; some costs remain dependent on
choices. The viewer's private action catalog cannot describe an opponent's options.

Keep the normal table quiet: expose concise public-zone cues, with explanations
on inspection rather than a permanent rules panel for every graveyard. Relevant
zone history must come from durable, scoped engine facts, not an assumption that
the retained activity tail is complete. Clear stale card details on visibility
revocation. A face-down or otherwise private occurrence never acquires a visible
identity, cost or ability merely because another card uses a familiar mechanic.
This is a design requirement; generic casting-route/cost and recent-zone
inspection have not been implemented by recording this reference.

### Ability choices as readable game objects

The Ajani reference supports a deliberate choice between concrete abilities:
each face shares the source identity/artwork, states one effect, and makes its
own cost easy to compare. Selecting a face starts that exact engine ability;
subsequent payments and targets retain their normal rules. Keep cancellation
and access to the battlefield available. A source with one concrete action
retains its direct route instead of adding an unnecessary choice step.

This requires a richer engine projection than today's source-action Id/Kind/Text.
Forge has typed cost parts and availability, but the current catalog filters out
unavailable abilities and combines costs/effects in its display text. The next
projection needs separate engine-formatted cost and effect information, ordered
option identity and availability. Only legal current options receive action
handles. Preserve zero/variable/modified costs and unknown availability reasons;
the UI must not split a string at a colon or invent why an option is disabled.
This ability-choice presentation is a follow-up to the table study, not a feature
implemented by adding this reference.

### Four-player direction: stable table, selective attention

**Native lab prototype; human review pending.** The
[spatial study](images/four-player-attention-study.svg) separates controller
territories from the current interaction. Its shapes illustrate relationships,
not final card styling or proof that crowded boards are readable. The sketch
omits the hand, zone piles and full-card inspector; fitting those while retaining
the action context is part of the native comparison below.

The source now includes an explicit **Table study** mode in the Experience Lab:

```powershell
& 'C:\candidate\ManaTable.exe' --experience-lab four-player-table.v1 --table-study
```

The header toggles the study for that same snapshot. It uses the production card,
presentation and input pipeline over authored projections. The four checkpoints
include ordinary boards, only one crowded opponent, a cross-seat Ballista ability
and its damage result. The hand, public command/zone piles, attached equipment and
counter-bearing creatures are present together. The study is not the default
layout for normal games or a released feature.

`SharedTableStudy` arranges upright scan faces independently within fixed role
lanes. A pending stack or another controller's density does not participate in
packing. Battlefield faces prioritize real artwork, a narrow name, current P/T,
small counters and state glyphs. Every occurrence remains represented; differing
public states remain separate. Full inspection retains the battlefield anchor,
keeps the original card readable, overlays authoritative P/T and attaches typed
counter/damage/state chips below it. The pending effect uses a reserved area and
can occupy its remaining space beside an opened reader. Hand play retains its
existing scoped drag behavior.

Focused geometry, privacy and pointer checks protect these mechanics. Actual
small/large-window review remains separate. The busy far seat and the extreme
480-permanent overview still become too small for comfortable card recognition.
They need a better density/focus design; retaining the occurrences does not settle
that issue. The prototype still uses the existing letterboxed game surface.

Start with a shared table: the viewer below, with other seats clockwise at the
left, top and right. Reconsider the current territory dimensions. Give every
seat viewer-upright battlefield faces with identity, art and authoritative P/T;
put creatures toward the shared action area and resources/support farther out.
These are spatial relationships, not permanent role headings or four framed
miniature screens. Place cards by current controller; retain original ownership
in inspection. Cross-controller attachments still need their controller cue.

The alternative of three opponent bands above one wide local field deserves a
visual comparison, especially in a larger window. At a small window, its narrow
opponent strips compete with full-card inspection and the pending-effect area.
The shared-table study is the first prototype, not an accepted final layout.

Reserve action/inspection space before arranging fields. The default
`BattlefieldLayoutContext` switches the whole table to bounded packing when any
seat exceeds seven public-state groups, and its conditional stack/source
reservation intersects a battlefield territory. This can move unrelated cards
when a new effect appears. Layout stability requires changing that composition,
not merely repainting smaller card faces. The study bypasses that global packing.
Priority eligibility or an identical
snapshot should not repack the table. P/T, counters or attachments can correctly
split a copy group; preserve unrelated anchors while updating that membership.

Plan against the actual viewport and text scale. The current fixed 1600x900
game surface leaves unused height in a 1000x740 window while squeezing all four
fields into the remaining band. A new composition should use that available
space, with drawing, hit testing and inspection sharing the same geometry.

The first complete journeys to prototype are:

| Player intent | Source, target and gesture | Engine transition and visible result |
| --- | --- | --- |
| Read another player's modified creature | Click that exact public occurrence; inspect a counter or ability beside its full face | No game command. The source position stays identifiable; inspection shows current P/T, typed counters, gained abilities and any engine-projected modifier sources. The reading area stays fixed while used. |
| Follow an off-turn response | Inspect the actual source or target of the pending effect | Reading does not pass priority. Turn owner, pending-effect controller and current responder retain distinct cues. Relevant occurrences highlight individually; the entire effect container does not behave as one card. |
| Understand combat against two players | Select an attacker or defender and inspect its actual related blockers | Keep all seats present. Emphasize that combat relationship and its authoritative stats; do not permanently draw every possible connector across the table. |
| Understand Toph followed by Felidar Retreat | Follow the reported source, changed land, then affected creatures | Brief source-to-result emphasis settles into current card state. A mass effect emphasizes its recipients together; it does not produce 82 simultaneous arrows or persistent explanatory panels. |
| Read a very crowded seat | Deliberately inspect or focus the relevant region and exact occurrence | The proposed focus/zoom preserves a way back to the same whole-table location and keeps other seats locatable. Distinct state and eligible occurrences remain reachable; no hidden overflow page. This navigation has not been implemented. |

Use a quiet persistent turn accent and a separate responder/decision cue, rather
than moving the camera each time priority changes. Keep the hand anchored.
Automatic turn-follow remains an optional experiment; never move the view during
reading, dragging, targeting or a required choice. Pending characteristics stay
pending: for example, a targeted 1/1 does not display Giant Growth's 4/4 result
before resolution. Inspection and all compact views resolve the same current
authorized occurrence without turning visual identity into an action handle.

Compare the same ordinary table, one heavily crowded opponent, two defenders,
an open full-card/ability explanation and the 480-permanent case at both small
and large windows. Existing ordinary/crowded/priority canonical cases provide a
starting point; `four-player-table.v1` adds the asymmetric checkpoint. Review the
actual gesture through its result with people; fitting every occurrence is not
evidence that they can identify it or follow the action.

### Repeated targeted triggers: 37 draws

The user reported 37 draws causing 37 one-creature targeting interactions and
repeated manual resolution. This is a generic interaction requirement; the card
name is not needed to define the design. This section specifies a required
canonical journey for the refactor, not an implemented shortcut or loadable lab
case.

Two independent intents are needed:

- **Assign targets together.** Present the equivalent trigger group beside its
  source. Choosing a creature can assign it to the group with an explicit
  same-target action; distributing assignments remains available on the actual
  eligible creatures. Show pending assignment quantities on those creatures.
  Those quantities count triggers, not promised counters, damage or final P/T.
- **Continue through this group.** After assignments, let the player yield their
  own priority for the captured group. Keep Take control available, show the
  remaining group and update actual card state as effects resolve. Do not require
  another click or a full animation pause for each equivalent resolution.

Normal targeted triggers choose their targets while entering the stack, before
resolution. Other players still receive their response opportunities between
resolutions; a local continuation cannot resolve on their behalf. These are
separate stages under [rules 115.1d, 603.3d and 117.4](https://media.wizards.com/2026/downloads/MagicCompRules%2020260925.pdf).

The engine must identify the finite group and supported choice equivalence,
including controller, source occurrence, ability/mode and targeting constraints.
Each target assignment uses current legal candidates and the current prompt.
Never reuse an old card key, infer equivalence from matching names/text, or
automatically choose a replacement when a target becomes unavailable. Required
payments, optional decisions, changed constraints, an intervening action, a new
trigger outside the group, cancellation or uncertain connection return control.
Expected effects from the group can continue; stopping on every counter change
would recreate the original problem. New triggers never silently extend the
player's authorization.

Current Pass for now only covers empty-stack windows. Ordinary Auto requires
the engine's no-response permission. Neither covers this intent. Forge's existing
stack yields and optional-trigger acceptance provide useful primitives but do
not batch target selections. Forge already skips some target prompts when a
mandatory trigger has only one legal card, so repeated Resolve clicks must be
distinguished from actual repeated target choices. The native boundary still needs group identity,
current assignment permissions and bounded continuation; exposing a broad
persistent yield preference is not the specified behavior.

The canonical journey must include all choices aimed at one creature, choices
split between creatures, target loss, an opponent response during resolution,
a new mandatory decision and new triggers created by a group member. Review how
people assign, inspect, interrupt and understand the resulting state on a crowded
four-player table. Separate the mechanical preservation of 37 triggers from
human review of the grouped presentation and pacing.

## Open a canonical case

From the repository root on Windows:

```powershell
.\experience-lab.cmd
.\experience-lab.cmd -Scenario trigger-search.v1
```

The launcher builds the current native app and opens an interactive window. To
review a specific compiled candidate without rebuilding it:

```powershell
.\experience-lab.cmd -ApplicationPath 'C:\candidate\ManaTable.exe' -Scenario crowded-counters.v1
```

`-NoBuild` uses the existing Debug output. Record which candidate/source revision
you reviewed; a stale binary is evidence about that binary only.

The lab starts without Forge, a lobby, a network request or real deck writes.
Each launch creates a fresh profile under `%TEMP%\ManaTableExperienceLab`.
It reads already-cached public artwork from `%LOCALAPPDATA%\ManaTableNative\art`
and otherwise uses the normal text/surface fallbacks. An optional `--art-cache`
on the executable selects a different read-only cache. Missing art is useful
fallback coverage; also review realistic art before accepting a visual change.

The header labels every case **Scripted snapshot**. Next advances an authored
public continuation; Previous and Reset replay the baseline in a fresh scope.
Scenarios opens the case gallery. These controls do not send a game action for
another seat. Known selection/confirmation actions demonstrate the authored
interaction; unsupported commands explain their limitation. This is the actual
native renderer and input pipeline with controlled state, not a rules simulator.

| Scenario ID | Review focus |
| --- | --- |
| `four-player-table.v1` | Ordinary and asymmetric four-seat fields, a seven-card local hand, commanders, public zones, current stats/counters/attachments and a cross-seat ability/result; optional `--table-study` comparison |
| `crowded-counters.v1` | 480 permanents, Toph earthbend 2, then one Felidar Retreat effect with 82 recipients; source, changed land and complete affected set |
| `card-reading.v1` | Hand slots, reading/collapse and required selection; current P/T/counters in card and ability-source previews despite an older embedded source face |
| `ability-choice.v1` | Select the Elf, read its concrete mana action without changing the prompt, then explicitly activate and see the authored tapped source and green mana; no extra one-option chooser |
| `ability-targeting.v1` | Walking Ballista's distinct costs/effects, card/player targets, cancellation, paid counter cost, pending stack, another player's response window, and authored result |
| `ordered-choice.v1` | Visible position numbers, same-name occurrences, selection order and confirmation |
| `mana-payment.v1` | Unspent colored mana, payment instructions and pool transitions |
| `combat-allocation.v1` | Attacker/blocker identity, current P/T, marked damage and assignment thresholds, including anonymous cards |
| `public-state.v1` | Qualified/repeated abilities, generic player resources, day/night and revocation of visible information |
| `stack-priority.v1` | Named stack order, controller/targets, whose response is pending, local response and resolution |
| `trigger-search.v1` | Passing priority hands off to another responder, then required recipient/library choices and the searched card in hand |
| `turn-orientation.v1` | Ordinary four-player turn sequence; Follow turn currently exposes insufficient travel space and remains stationary |
| `turn-orientation-crowded.v1` | The same authored turn sequence with the crowded battlefield; stationary fallback and table context |

Keep IDs versioned when changing an authored case's meaning. The factories live
in `Mana.Scenarios` and feed both the lab and checks. Reuse them instead of adding
a second 480-card builder or a different imitation of the same choice flow.

## Capture a still or a transition

The compiled executable accepts a zero-based stage index and actual window size:

```powershell
& 'C:\candidate\ManaTable.exe' --experience-lab stack-priority.v1 --stage 1 --size 1000x740 --capture 'C:\review\opponent-priority.png'
```

`--capture` saves a settled design-surface still and exits after the capture
frames. Starting directly at a later stage does not show how the user reached
it. For causal feedback and motion, use interactive Next or a replay capture:

```powershell
& 'C:\candidate\ManaTable.exe' --experience-lab crowded-counters.v1 --stage 2 --size 1000x740 --capture-replay 'C:\review\crowd-transition'
```

Replay capture starts at stage 0 and advances to the requested final stage.
It saves baseline and source/travel/landing/settled keyframes, their physical
`-window.png` counterparts and a `.frame.json` for each keyframe, copied from the
completed production `PresentationFrame`. The frame records public card values,
display/hit/text geometry and the effective view offset/enabled mode.
`replay-capture.json` records timing, `Turn`/`ActivePlayerId` separately from
`PriorityPlayerId`/`DecisionOwnerId`, plus view offset and limited-space status.
These records tie each screen to its actual state and composed frame; they make
no usability assertion. Keyframe labels are scheduled observation points, not
animation acceptance assertions. `--focus-player 0` opens the local battlefield
focus view; also review the ordinary whole table. Do not combine `--capture`
and `--capture-replay`.

Review both the design surface and actual window output at `1000x740` and
`1540x980`, then use the interactive window. Check normal and larger text,
ordinary and reduced motion, and realistic art alongside fallback surfaces.
Keyframes cannot establish readable motion timing or comfortable pointer use.

For counter and interaction-state review, `card-reading.v1` stage 2 shows a 3/3
creature with two +1/+1 counters, plus lands/support with multiple counter types.
Stage 3 shows the same creature at 6/6 with those same two counters and an ability
chooser whose embedded source face still says 1/1. The preview must use the exact
current public occurrence; the original choice handles remain unchanged. These
authored values demonstrate that current P/T must not be calculated from counters.

```powershell
& 'C:\candidate\ManaTable.exe' --experience-lab card-reading.v1 --stage 2 --size 1000x740 --capture 'C:\review\current-counters.png'
& 'C:\candidate\ManaTable.exe' --experience-lab card-reading.v1 --stage 3 --size 1000x740 --capture 'C:\review\ability-current-source.png'
```

Also advance between those stages interactively and inspect the creature. Review
the on-card stats strip, dice/count marker and its additional-types ellipsis, then
find the full counter names in the reader. In `crowded-counters.v1`, follow the
source spark to the actual counter-marker pulse without losing the current value.
Dense cards use connected thumbnail/name metadata and one combined stats row;
redundant role and hero counts have been removed. These changes are implemented,
but human clarity and comfort have **not been accepted**. The crowded whole-table
layout remains an open design problem.

## Gameplay interaction audit: 21 October 2026

This audit found repeated instructions and controls competing with the current
decision. The table records source changes; candidate verification is recorded
once below. Neither source inspection nor automated checks record human UX
acceptance.

| Finding | Scoped change or remaining work | Source status |
| --- | --- | --- |
| Constant card-reading instructions, activation badges and an empty mana caption repeated information already conveyed by the board | Removed the repeated board hints, per-card activation captions and zero-pool caption; meaningful resources, legal actions and decision consequences remain | Implemented |
| Opening a card creates a separate Play card / Close card control column; the replacement edge tab was easy to miss | Moving that same control over the illustration did not improve the interaction model | Both placement iterations rejected by human review |
| A primary button's permanent accent can look like hover; custom text choices have separate painting paths | The shared Controls helper separates resting importance or selection from hover and pressed feedback across buttons, policies, text choices and phase stops; card hover, selection and availability have distinct treatment | Implemented |
| The allocation recipient's visible Inspect caption lay mostly outside its clickable art | The existing inspect-only target now includes the exact art or painted caption; assignment amounts and commands are unchanged | Implemented |
| Every combat row repeated Back to table without locating that attacker | Removed the row copies; the panel exit and actual assign, remove and recall actions remain | Implemented |
| Text-choice source art looked like other inspectable card art but had no interaction | A currently visible source preview now has an inspect-only target and hover feedback; original option indexes and action handles remain unchanged | Implemented |
| Choice cards repeated SELECT / SELECTED / VIEW ONLY captions alongside their frames and task constraints | Replaced repeated captions with selection marks; the shortened ordered-choice task keeps counts and order, with each selected position still numbered | Implemented |
| Small drags inside an enlarged hand card could be mistaken for a play, while requiring an exit from its full face made deliberate drags awkward | Require deliberate movement, then shrink the held card above the pointer. Permit legal drops inside the former reader area after leaving the original hand slot; Escape or right-click cancels | Accepted for the Adagia hand-play preview, 21 October |
| A narrative panel and duplicate drop lane competed with the dragged card | Use subtle corner marks from the actual legal-drop geometry and light the held card on a legal drop; retain combat aim | Accepted for the Adagia hand-play preview, 21 October |
| A boxed Play land shortcut competed with reading and dragging | Keep the available secondary shortcut as quiet text with hover feedback; clicking the card still reads | Accepted for the Adagia hand-play preview, 21 October |
| The hand cursor also appeared over input-shield panel backgrounds | Shield-only battlefield, recipient and public-outcome panel backgrounds no longer advertise an action | Implemented |
| Error or notice banners could cover the reader's former top action tab | Keep the full original face visible, with authorized actions and effective state beside it | Reworked after the reconstructed reader was rejected; human review pending |
| A single text ability option opened a gallery; the smaller dialog still obscured the game's purpose | Attach engine-reported actions to the actual source occurrence on the table. Keep the board visible, choose the action directly, and show its resulting state transition | Reworked after negative human feedback; review pending |
| A pending ability repeated its source card, source-name row, Details button and stack count while its actual effect was not visible | Show its actual effect, controller, targets and order together; hover source/target references to read their cards, with exact-effect inspection only for overflow and secondary summaries | Reworked after screenshot rejection; human review pending |
| Reconstructed art/rules panels and continuation pages obscured the actual card | Restore the complete original face; show concrete actions together beside it, with wheel scrolling only for rare overflow | Intact reader accepted in the Adagia preview; other source-action contexts remain open |
| Card images appeared only after the player opened them | Preload exposed faces and warm the selected local deck; prioritize the card being read and discard obsolete speculative work | Implemented; human review pending |
| Pass for now flickered during background observations | Keep its input state stable while polling; arming records intent and revalidates the original token after observation | Implemented; human review pending |
| Card gestures vary between the board reader, zone inspection, choices and allocation details | Review these contexts together before changing the common gesture policy; a hand cursor alone does not explain whether a click reads, selects or acts | Open |

Candidate verification: the Release build completed with no warnings or errors;
presentation/headless checks and the card-reading, card-states, allocation and
priority GPU journeys passed. An agent inspected actual `1000x740` cached-art
captures of the bottom tab at rest, hovered and pressed, plus ordered choices,
mana, the allocation caption journey and the priority walkthrough. Local evidence
is under `.tools/render-refactor-integration-20261021`, including
`interaction-audit-source-preview.png`, `interaction-audit-candidate-allocation`
and `interaction-audit-candidate-priority`. Modal exits now measure their label
width, including View battlefield at 130% text. These are mechanical results and
agent visual inspection, **not human UX acceptance**. The subsequent compact
dialog also passed mechanical checks, but the human reviewer could not identify
its purpose or what it asked them to do. That design is **not accepted**; making
the window smaller did not establish a coherent game interaction.

Context belongs in the game UI. Keep the source card as the visual anchor,
present its concrete actions there, and show their consequences through the
reported tap, mana, target or other state changes. Debug fixture explanations
belong in lab chrome, not the decision itself. Internal checks should cover the
entry through the result; a developer snapshot starting halfway through a choice
is not a complete product experience or an implicit task for the reviewer.
Broader gesture consistency remains open.

The inline candidate builds without warnings or errors. Existing presentation,
headless presentation, card-state, hand-placement and action-feedback checks
pass. The input journey starts at priority, opens the source's abilities and
chooses its original scoped option without a separate confirmation. Its authored
continuation shows the tapped Elf and one green mana; this is not a Forge rules
simulation. GPU captures under `inline-actions-r3-*` retain the actual source and
remove the false stack copy. These checks establish input and rendering behavior,
not human acceptance of the experience.

The edge tab was subsequently found too easy to miss. That rejected candidate put
the entry over the selected card's illustration, then replaces it with the actual
engine-reported ability cost/effect when the complete options fit there. Longer
choices keep the adjacent layout. Missing-art faces reserve that same action
space above their rules; names and current stats remain outside it. This change
was rejected by human review: it put the same generic control over the artwork
without designing a better interaction.
The Release build and existing presentation, headless presentation, card-reading
and action-feedback checks pass. Cached-art captures under `on-card-actions-*`
show the entry at rest, hovered and pressed, and the ability over Llanowar Elves'
illustration. These are implementation checks and agent inspection.

## Card interaction decision after the rejected placement changes

The design question is what selecting a card lets a player understand and do.
Moving an Abilities button changes neither its meaning nor the unnecessary
navigation it adds. The proposed interaction is:

| Player intent | Visible behavior |
| --- | --- |
| Read or select a card | Promote the exact current occurrence into a stable, readable card. Keep the illustration clear; show current stats and counters with the card. Reading alone must not pay a cost or begin an activation. |
| Use a card | Show concrete engine-reported cost/effect actions beside its complete face. One ability gives one action; multiple abilities or casting alternatives use the same structure. Passive text remains text. No generic Abilities step. |
| Complete an action | Keep the chosen source identifiable while the engine requests modes, targets or payment in its actual order. Highlight legal battlefield targets and relevant resources in place. A short current task can accompany those objects. |
| Back out | Offer cancellation only where the engine permits it. Preserve already-committed costs; do not imply a universal undo. Before commitment, selection and inspection leave game state unchanged. |
| Understand the result | Return attention to the affected card, target or resource. Show the actual tap, movement, damage, counter or mana change; retain the source relationship through the transition. |

The printed face remains intact. A rectangle at a percentage of a card image is
not a mapping from its printed rules to a live action. The engine supplies action
identity and presentation data for explicit cost/effect controls beside the face;
unrelated printed text must not appear clickable.
Availability and unavailable reasons must be engine-reported, never inferred
from Oracle prose. Unknown availability must remain unknown.

The audit found a data boundary preventing an honest direct presentation. The
auto-pass `PriorityAbilities` list excludes mana abilities and is not a complete
catalog of card actions (`AvailableActions.scanAll`, `PriorityAbilityPass`).
Concrete play choices are projected later by `MatchSession.chooseAbility`.
Selecting a card needs a noncommitting, prompt-scoped projection of its real
actions before a separate explicit action can invoke one. Both priority and
mana-payment contexts need this contract, with stale-source validation and the
viewer's existing visibility limits. The new candidate implements that optional
source-action boundary and presents it beside the selected card's complete face. Its
engine-owned handles stay separate from Auto-pass policies.

This follows the useful separation described by Wizards' rules/client teams:
the engine supplies finalized actions and decision constraints, while the client
chooses how to present each interaction. See [On Whiteboards, Naps, and Living
Breakthrough](https://magic.wizards.com/en/news/mtg-arena/on-whiteboards-naps-and-living-breakthrough).

The rejected Elf fixture was misleading: it forced a one-option `playAbility`
choice even though the adapter normally returns that sole playable ability
immediately unless it explicitly requires a prompt (`MatchSession`'s
`getAbilityToPlay` dispatch). Its authored tap/green-mana continuation and the
on-card rectangle checks do not demonstrate a coherent real-game experience.
The corrected Elf case invokes its concrete source action directly; unsupported
hand cards in the reading case no longer advertise Cast/Play. Authored lab results
remain distinct from actual Forge outcomes.

The candidate review covers the same grammar across a simple mana source and a
real multi-ability source with distinct costs and effects, including a targeted
action: select, read the action, choose it, target, back out where permitted,
retry, and observe the authoritative result. Include an unavailable state,
graveyard casting and a crowded source location. Use actual engine continuations
for commitment and cancellation; keep visual review separate from command
assertions. The existing fake second-option labels and generic scripted result
are insufficient. No additional CI suite is introduced for this review.

The current candidate keeps the complete original card image at 410x574. Concrete
actions and effective P/T, damage and typed counters sit alongside it without
reconstructing its illustration or rules. Actions have no previous/next pages;
rare overflow uses wheel scrolling. The intact reader and hand-play direction were
accepted for the Adagia preview above; other action contexts remain open. Source reads are coalesced and
bound to the current source/decision/revision. Explicit selection, cost/target
continuations and cancellation keep their engine authority. Actual target-input
semantics prevent a mixed card/player prompt from being labeled Choose a player,
and keep the permitted Cancel available.

The earlier reconstructed reader's agent GPU review used genuine art at `1000x740`. Evidence under
`source-actions-review-harness/elf-1000-r2` and `ballista-1000-r2` records the full
authored physical-pointer journeys, state/frame JSON and candidate assembly
hash. The single source shows tap and green mana; the multi-ability source covers
cancel, retry, card/player aiming, paid cost, stack, another responder and the
authored result. These are observed rendering/input behaviors. Human acceptance
is still open.

The final native build completes with no warnings. Focused Java 17/21 checks and
the real Forge/native walkthrough pass: querying current actions does not change
the pending decision, and selecting the concrete action plays a land and casts a
commander. The existing hand-placement walkthrough also passes with a valid empty
source-action list, preserving Gaea's Touch's placement permission, and records a
graveyard reader with its concrete flashback cost and effect. These checks prove
the exercised protocol and interaction paths, not human clarity or every card's
rules. Evidence is in `source-actions-final-real-engine`,
`source-actions-final-hand-placement` and `source-actions-validation.json` under
the same local integration artifact directory.

Later screenshot feedback rejected the pending-stack presentation: the source
card, repeated source name, Details button and Stack count competed while the
actual pending effect was absent. The current iteration uses one combined
surface for each real stack item. Its main row shows the reported effect text,
a quiet source thumbnail, controller and targets with arrows; subsequent items
keep their exact queue order. Source and target references retain genuine
engine-permitted stack selection. Long effects remain available
in inspection. Payment and recipient source context retain their separate paths.
This records the implementation and rejection evidence; human acceptance of the
new stack presentation remains open.

The native build and the existing presentation and priority-flow checks pass.
The latter keeps exact order, read-only inspection and the scoped pass → required
recipient → library search journey, and now checks the effect text before
inspection rather than requiring the redundant Stack button. GPU review uses
`pending-effect-final-priority-gpu` at `1000x740` with 130% text and the same
Ballista input journey in `pending-effect-final-ballista-review`. The effect
surface fits its measured content, keeps the real source on the battlefield and
shows the reported target; a pending effect is not called resolved. These
artifacts remain agent review, not human acceptance.

Follow-up human feedback found the combined effect clearer, but called `You can
respond` awkward after initiating your own action and found both connection
directions hidden. The band now reports `Your priority` or the named holder.
A gold arrow connects the current public source to the effect; red arrows leave
the effect for its reported targets. Dedicated ports route around the full
panel/header footprint. Agent review includes the nearby Bears target above the
header (`priority-connections-near-card.png`) and the existing player-target
journey (`priority-connections-player-review`), alongside the existing priority
and presentation checks. Arrow counts are not evidence of legibility, and human
acceptance of these changes is still open.

Further feedback rejected hover fill on the whole effect box and crossing
connections. The box now has no hit or hover state. Its source art/name and each
target name are card references: hovering one highlights the actual battlefield
face when present and shows a full read-only card with current stats and counters beside the
panel. The preview stays in place, shields input beneath it and has no actions;
the canonical card and its input region do not move. Player references highlight
the actual portrait. Auto pauses during card-reference reading. Overflow and
secondary-effect summaries retain exact-item inspection only.

Same-side connection ports now follow the endpoints' vertical order. A nearby
card gets a feasible edge port while preserving that order, correcting the Bears
case without relaxing the panel clearance. Current agent capture evidence is
under `card-reference-final-bears-review` and `card-reference-final-player-review`.
Both physical-pointer GPU journeys complete, including source and target hovers,
the unchanged pending effect, priority handoff and the authored result. An agent
inspected the actual body/source/card-target/player-target captures. The native
build and existing presentation/priority checks pass; these mechanical results
do not establish visual clarity. Human acceptance remains open.

## Existing regression walkthrough

Use one `1000x740` interactive window for the short review: `card-reading.v1`
stages 0 and 3, `ordered-choice.v1`, `stack-priority.v1` stage 2, then
`combat-allocation.v1`. Hover, click, deselect and close each surface; also click
the allocation Inspect caption itself. `trigger-search.v1` stage 2 checks that
the actual consequence and named recipients survive the removal of repeated
instructions. Review graveyard/exile inspection in the existing hand-placement
journey or a real game; the lab's reading case does not demonstrate flashback.
Keep current stats, counters, nonempty mana, costs, choice constraints and pending
effect information throughout this cleanup.

## Human acceptance

Before marking an experience change ready, inspect actual screens and transitions
and perform the relevant gestures yourself. Ask a reviewer who did not implement
the change to explain what is happening without a developer narration:

Agent inspection of actual captures can identify visual problems and guide
iterations. It cannot stand in for a human playtest or record human acceptance.

- Who can act now, and which control will do what? Passing priority must not
  imply that a trigger has already resolved or that a required choice is skipped.
- Which source caused this change, what changed, and which occurrences were
  affected? In the crowded case, follow Toph to the animated land, then Felidar
  Retreat to its recipients. Find an unrelated card and inspect it too.
- Can the reviewer find the current stats, counters, abilities and unspent mana
  without opening unrelated panels? Are focus/hover, selection, legal action,
  ownership and causal highlighting distinguishable?
- Can they read the whole decision, switch adjacent hand cards, select duplicate
  names, order choices and assign damage without the reader blocking the next
  control? Try the small physical window and larger text.
- Does information disappear when its permission is revoked? An anonymous public
  permanent may show current characteristics; a later hidden hand must not retain
  its old face, label, artwork, interaction or source link.

The required trust case is `trigger-search.v1`: pass the first priority window,
observe the other responder, choose the recipient when that choice becomes local,
then choose an actual library card and verify it appears in hand. Review the
same handoff in a real Forge game; a scripted continuation proves neither trigger
rules nor actual library searching.

In a real game with an available instant/activation, review **Pass for now** on
an opponent's empty-stack priority. Quiet windows may pass, but a new action,
turn or required choice must return control. Activate a mana source, respond to
a spell, and try Hold/Take control and inspection. Hover and arm it during a
background poll: its appearance should remain steady and passing must wait for
fresh-state validation. The feature retains one
engine-issued token, never silently adopts a new one, never consumes search or
payment choices, and is not a persistent F6/end-turn setting. Existing Auto has
its separate conservative availability permission. A command already submitted
before cancellation must finish reconciliation before a new command can run.

Record the exact binary/source revision, scenario/stages, viewport, art source,
text/motion settings, captures and manual actions. Write what the reviewer could
or could not identify, concrete clipping/overlap or unexpected motion, and the
remaining live-game steps. Distinguish **not reviewed** from **reviewed with
issues** and **accepted by the human reviewer**. Passing automation alone cannot
fill the last category.

See [Testing](Mana-Table-Testing.md) for command/state regressions and CI ownership,
and [Architecture](Mana-Table-Architecture.md#native-client-boundaries) for the
shared composition/input boundary. This workflow documents the review required;
it makes no claim that current visual quality has completed that review.

## Experimental turn-orientation comparison

The current source has a lab-only **Follow turn** toggle and shared drawing/input
view boundary described in
[Architecture](Mana-Table-Architecture.md#experimental-multiplayer-turn-orientation).
The current ordinary layout cannot safely accommodate the requested shift around
fixed controls, and the crowded layout uses stationary fallback too. **Neither
case currently demonstrates meaningful camera movement.** The lab is a diagnostic
for this constraint. A stable layout with reserved travel space is needed before
the intended human comparison, which has **not been reviewed or accepted**.
Production has no follow setting or changed default. The implemented boundary
permits battlefield translation with fixed zoom, seat orientation and anchored
hand/player/zone/decision controls; response ownership alone cannot request it.

Capture the same candidate stationary and with follow requests enabled to review
the current limitation:

```powershell
& 'C:\candidate\ManaTable.exe' --experience-lab turn-orientation.v1 --size 1000x740 --capture-replay 'C:\review\turn-stationary'
& 'C:\candidate\ManaTable.exe' --experience-lab turn-orientation.v1 --follow-turn --size 1000x740 --capture-replay 'C:\review\turn-follow'
```

Repeat with `turn-orientation-crowded.v1` and separate output directories. Omit
`--capture-replay` for manual interaction. **Next** advances the authored state;
**Previous** and **Reset** replay through the selected stage in a fresh scope.
**Scenarios** opens the paged gallery. The header's **Whole table** restores the
stationary view and turns following off; **Follow turn** explicitly enables
requests again for a subsequent eligible boundary. **Follow: limited space**
explains the guarded ordinary view; **Follow: full table** identifies the crowded
fallback. An enabled toggle does not mean movement occurred. These controls send
no engine commands.

| Battlefield | Stationary emphasis | Follow requests enabled: current finding |
| --- | --- | --- |
| Ordinary Commander table | Play the complete journey | Stationary: requested path overlaps fixed controls |
| Very crowded table | Play the complete journey | Stationary: dense-table fallback |

Both authored journeys include a turn handoff, an off-turn response, combat with
two defenders, a required cross-seat choice, quiet priority and an extra turn for
the same owner. They do not simulate those Magic rules. During manual review,
open/close a card reader and the focused battlefield workspace, and hold or move
the pointer across a handoff. Interrupted or suppressed movement must not catch up
after the interaction. Review reduced motion and the crowded case's stationary
fallback. Reconnect and elimination need separate live-game review; these cases
do not cover them.

Once a layout admits useful movement, compare the same journeys and observe
whether people notice the relevant seat, distinguish whose turn it is
from who can respond, retain their sense of where everyone sits, and reach their
intended cards without chasing them. Record any disrupted reading, hidden
combat/target context or surprising movement. Use realistic art and the actual
small window as well as the larger view. A preference for stationary emphasis is
a useful outcome; keeping camera movement is not the goal of the comparison.

Mechanical checks can protect transform/hit alignment, fixed-control bounds,
scope, privacy and cancellation of movement. They cannot accept comfort, clarity
or orientation. Agent inspection can surface concerns; acceptance still requires
people playing through the comparison. The whole-table crowded layout remains
an open design problem regardless of the camera experiment's outcome.
