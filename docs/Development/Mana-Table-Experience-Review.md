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
| `card-reading.v1` | Hand slots remain available; click to read, switch cards and collapse; compact required selection |
| `ordered-choice.v1` | Visible position numbers, same-name occurrences, selection order and confirmation |
| `mana-payment.v1` | Unspent colored mana, payment instructions and pool transitions |
| `combat-allocation.v1` | Attacker/blocker identity, current P/T, marked damage and assignment thresholds, including anonymous cards |
| `public-state.v1` | Qualified/repeated abilities, generic player resources, day/night and revocation of visible information |
| `stack-priority.v1` | Named stack order, controller/targets, whose response is pending, local response and resolution |
| `trigger-search.v1` | Passing priority hands off to another responder, then required recipient/library choices and the searched card in hand |

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
`-window.png` counterparts and `replay-capture.json` with actual timing and
public state/actor stamps. The labels are scheduled observation points, not
animation acceptance assertions. `--focus-player 0` opens the local battlefield
focus view; also review the ordinary whole table. Do not combine `--capture`
and `--capture-replay`.

Review both the design surface and actual window output at `1000x740` and
`1540x980`, then use the interactive window. Check normal and larger text,
ordinary and reduced motion, and realistic art alongside fallback surfaces.
Keyframes cannot establish readable motion timing or comfortable pointer use.

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

## Planned turn-orientation comparison

Gentle turn-follow is part of the presentation refactor's exploration, with
production defaults unchanged. This comparison is not implemented yet. Build the
shared board-view drawing/input boundary described in
[Architecture](Mana-Table-Architecture.md#planned-experiment-orienting-a-multiplayer-turn)
before adding a lab control or capture mode.

Compare **stationary turn emphasis** with **optional gentle turn-follow** over
the same authored four-player journey. Start with translation only, preserving
zoom, seat orientation and all fixed controls. A turn boundary may move the board;
ordinary response windows do not. Add a versioned canonical turn-orientation case
when the actual presentation hook exists. Existing stack/search and crowded
counter cases do not contain that complete journey and must not be relabeled as
proof of turn-follow behavior.

| Battlefield | Stationary emphasis | Gentle follow enabled |
| --- | --- | --- |
| Ordinary Commander table | Play the complete journey | Replay the same states and actions |
| Very crowded table | Play the complete journey | Review stationary fallback and retention of the other seats |

The journey includes a turn handoff, an off-turn response, combat involving
multiple defenders, a required cross-seat target choice, and opening/closing a
card reader and the focused battlefield workspace. Also begin a handoff while
the pointer is held or a choice is active: the move must be skipped, without
catching up after the interaction. Return to **Whole table** and verify following
stays paused until explicitly resumed. Review reconnect, extra turns, elimination
and reduced motion with the same policy; the next player's identity always comes
from the current public engine state.

Observe whether people notice the relevant seat, distinguish whose turn it is
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
