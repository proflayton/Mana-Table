# Review the Mana Table experience

Use the Experience Lab to reproduce a presentation problem immediately, then
review the corresponding live-game behavior. Automated checks establish scoped
command safety, state projection, privacy and geometry invariants. They do not
approve the UX. A count of passing checks, represented cards or captured frames
does not establish that a person can understand or use the screen.

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
| A primary button's permanent accent can look like hover; custom stack rows and text choices have separate painting paths | The shared Controls helper separates resting importance or selection from hover and pressed feedback across buttons, stack rows, policies, text choices and phase stops; card hover, selection and availability have distinct treatment | Implemented |
| The allocation recipient's visible Inspect caption lay mostly outside its clickable art | The existing inspect-only target now includes the exact art or painted caption; assignment amounts and commands are unchanged | Implemented |
| Every combat row repeated Back to table without locating that attacker | Removed the row copies; the panel exit and actual assign, remove and recall actions remain | Implemented |
| Text-choice source art looked like other inspectable card art but had no interaction | A currently visible source preview now has an inspect-only target and hover feedback; original option indexes and action handles remain unchanged | Implemented |
| Choice cards repeated SELECT / SELECTED / VIEW ONLY captions alongside their frames and task constraints | Replaced repeated captions with selection marks; the shortened ordered-choice task keeps counts and order, with each selected position still numbered | Implemented |
| Small drags inside an enlarged hand card could be mistaken for a play | A drag begun with a pinned hand reader must leave both its captured reader face and original hand slot before a legal drop can commit | Implemented |
| A narrative panel followed each drag | Removed the generic drag prose; the held card, legal-drop outline and combat aim remain | Implemented |
| The hand cursor also appeared over input-shield panel backgrounds | Shield-only battlefield, recipient and public-outcome panel backgrounds no longer advertise an action | Implemented |
| Error or notice banners could cover the reader's former top action tab | The selected-card action now sits within its illustration, away from the top banner band | Implemented |
| A single text ability option opened a gallery; the smaller dialog still obscured the game's purpose | Attach engine-reported actions to the actual source occurrence on the table. Keep the board visible, choose the action directly, and show its resulting state transition | Reworked after negative human feedback; review pending |
| Long ability text needs readable context through continuation pages | Complete text is preserved, but its continuation still needs a visual review of action identity and cost | Open |
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

The edge tab was subsequently found too easy to miss. The current candidate puts
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
| Use a card | Show concrete engine-reported cost/effect actions within its rules area. One ability gives one action; multiple abilities or casting alternatives use the same structure. Passive text remains text. No generic Abilities step. |
| Complete an action | Keep the chosen source identifiable while the engine requests modes, targets or payment in its actual order. Highlight legal battlefield targets and relevant resources in place. A short current task can accompany those objects. |
| Back out | Offer cancellation only where the engine permits it. Preserve already-committed costs; do not imply a universal undo. Before commitment, selection and inspection leave game state unchanged. |
| Understand the result | Return attention to the affected card, target or resource. Show the actual tap, movement, damage, counter or mana change; retain the source relationship through the transition. |

The rules area must be composed deliberately. A rectangle at a percentage of a
card image is not a mapping from its printed rules to a live action. The engine
must supply action identity and presentation data. When it cannot identify the
corresponding rules segment, explicit action text belongs in a distinct portion
of the card reading surface; unrelated printed text must not appear clickable.
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
source-action boundary and uses it in the selected card's rules area. Its
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

The current candidate keeps the illustration clear and typesets concrete actions
with the card's public rules, current counters and stats. Its height follows
measured content; exact repeated Oracle/action lines display once while the full
original rules remain available for inspection. Source reads are coalesced and
bound to the current source/decision/revision. Explicit selection, cost/target
continuations and cancellation keep their engine authority. Actual target-input
semantics prevent a mixed card/player prompt from being labeled Choose a player,
and keep the permitted Cancel available.

Agent GPU review used genuine art at `1000x740`. Evidence under
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
a spell, and try Hold/Take control and inspection. The feature retains one
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
