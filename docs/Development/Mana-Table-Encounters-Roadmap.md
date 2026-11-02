# Cooperative encounters and pod circuits: initial v1

Planning date: 1 November 2026. The player approved this milestone roadmap.
M1 is in progress: cooperative enrollment, team rules, durable completion and
six-seat presentation are implemented in the source candidate and undergoing
journey checks. Content quantities remain targets to validate through playtesting.
The published 0.2.334 build contains the earlier team/outcome foundation; it does
not expose this cooperative lobby. This roadmap is not a release announcement.

## What feature complete means

A player can form a party of one to four humans, choose an expedition or a pod
circuit, play through its conclusion, and resume saved progress between matches.
Solo uses the same systems, with optional AI teammates in cooperative encounters.

The initial content target is deliberately bounded:

- Cooperative expedition: three battles, including a final boss, branching
  encounter choices, one noncombat deck-tuning event, and rewards between battles.
  Save at each encounter boundary; measure actual session length before adding
  more battles. Normal matches begin with fresh match resources.
- Pod circuit: eight participants, including one to four humans and AI filling
  the remaining places; three rounds with two four-player pods per round,
  shuffled pairings, seat rotation, standings, and a final result.
- At least eight qualified AI decks across six strategic families: combat/tokens,
  counters, spells, artifacts, sacrifice, and recursion. Six standard encounter
  configurations and two distinct boss configurations supply expedition variety.
- Cooperative parties occupy up to four allied seats, including any AI allies,
  and face one or two AI enemies. The initial encounter budget is six total seats.
  Party size and total match size are different capabilities.
- The first cooperative ruleset uses individual turns, separately owned life,
  mana and cards, and team victory. Player elimination preserves spectator access
  until the team encounter finishes. Exact rules must be visible before Ready.

Deck qualification and encounter balance apply to exact content versions.
Imported legal decks remain usable; v1 does not promise equally competent AI
play with every possible deck. These content targets are acceptance criteria
to refine through playtesting, not claims about current balance.

## Milestones

| Milestone | Playable result | Completion gate |
| --- | --- | --- |
| M1: Cooperative match foundation | Up to four friends face an AI side, finish the encounter, and return together to the same lobby with progress saved. | Actual team rules, ally-aware AI, authoritative results, readable larger tables, and a four-human journey work together. |
| M2: Opponents worth playing | A varied roster uses its resources and plays sensibly against both parties and individual opponents. | Qualified deck versions, focused decision evidence, bounded real-engine campaigns, and human play review. |
| M3: Randomized pod circuit | Solo or friends enter a complete three-round event with new pods, standings, and a conclusion. | Every pod has a real outcome, all participants are accounted for, and reconnect/restart preserves the event. |
| M4: Complete expedition | Solo or friends choose a route, earn upgrades, face a boss, and win or lose a saved run. | A complete branching three-battle run works with isolated deck progression and synchronized party choices. |
| M5: v1 release readiness | Players can discover, understand, finish, and return to both modes reliably. | Recovery, compatibility, performance, complete human journeys, and exact-candidate release checks pass. |

### M1: Cooperative match foundation

Keep this a vertical slice with one encounter definition and no reward economy.
The implementation work is:

1. Introduce persistent participant and party identities, distinct from transient
   lobby slots, match seats, teams, and controllers. Human/AI control is explicit.
2. Define an engine-independent match specification: seats, teams, pinned decks,
   format, turn/resource rules, and required capabilities. Validate it before any
   client becomes committed to starting; unsupported rules never silently change.
3. Project actual ally/enemy relationships into snapshots, action evaluation and
   AI objectives. Rules-authorized target domains remain authoritative. Benefits
   to allies and discretionary harm to allies need different valuation.
4. Replace presentation-string result inference with an authoritative completion
   record containing encounter/match identity, winners, teams and end reason.
5. Commit the completion and next checkpoint once, then return the intact roster
   to its lobby. Persist a recoverable provider completion receipt before retiring
   the match, so a crash before run acceptance cannot lose its recorded outcome.
   Failure without such a receipt preserves an explicit unresolved attempt.
6. Extend seating and presentation for the supported total seat count. Keep cards,
   zones, ownership, tapping, priority and eliminated spectators legible; preserve
   the existing seated camera and physical card motion.

Gate: solo, two-human and four-human real-engine journeys; include a four-human
party versus two AI seats. Check team victory/defeat, host concession while the
match continues, guest reconnect, private information, duplicate completion and
recovery between receipt persistence and checkpoint commit, and return-to-lobby
behavior. Human review covers the exact candidate and whole
journey. Existing four-seat free-for-all checks alone cannot pass this gate.

The current source candidate adds capability-checked party setup, joint setup and
tempo consent, actual teams, frozen enrollment/deck assignments, durable receipt
and checkpoint replay, same-lobby return, and a continuous six-seat table to the
earlier team/outcome foundation. The automated gates include actual one-, two-
and four-human provider journeys, native UI/wire checks and a real native adapter
journey. An authoritative durable started/unresolved-attempt record is still
missing: a process crash before the first terminal receipt cannot distinguish
that attempt from not played. This is remaining M1 work, independently of M4's
saved-party selector. Exact-candidate CI, visual review and a complete human
cooperative journey also remain required before M1 acceptance. Neither roadmap
approval nor a passing boundary test marks M1 complete. Persistent journal replay
does not yet
provide a cross-application party/run selector or saved live matches.

### M2: Opponents worth playing

Build AI/content qualification alongside M1; finish it before promoting either
full mode. Expand the current policy and real-engine campaign, rather than
building another simulator or card-name strategy implementations.

Separate versioned deck lists, encounter composition, policy behavior and
difficulty. Each qualified deck records its strategic family, power band,
mechanics, known limitations and evidence. Boss identity must come from an
intentional deck/objective and supported rules, with any resource advantage
disclosed. Difficulty never grants unauthorized hidden information.

Prioritize ally-aware objectives, payment and resource planning, useful sequencing,
removal that changes an outcome, combat, and avoiding repeated unproductive
routes. Existing same-turn plans and social pressure assessment are foundations
to extend, not missing systems to recreate.

Gate: eight qualified lists and the encounter content target, tactical regressions
for observed gaps, varied authoritative games, and observed mixed human/AI play.
Legal completion, win rate and even target distribution do not establish enjoyable
opponents. Timeouts remain inconclusive and retain diagnostic evidence.

### M3: Randomized pod circuit

Implement a circuit scheduler over the shared match/result boundary. Cooperative
scheduling keeps the party together; circuit scheduling can place friends in
different pods within the same event. Show that distinction before joining.

Pin the roster and deck versions for the event. Use a recorded schedule seed to
shuffle pods, rotate seats and minimize repeat opponents where possible. Repeats
cannot always be avoided. The seed reproduces definitions and pairings, not
Forge's card shuffles or complete gameplay.

Host both pods as actual matches, including AI-only pods, with bounded aggregate
AI/process work. This adds a multi-match hosting capability: independent match
identities, command routing, lifecycle and authorized subscriptions. Returning
one pod to the event must not close or redirect another. Clients subscribe only
to their authorized match. Publish round
progress while other pods finish; advance after all pod outcomes are settled.
Checkpoint each accepted pod result. If the host restarts during a partial
round, preserve completed pods and disclose a new attempt for each unfinished
pod; do not replay completed pods or award their points twice.
Define scoring before starting: wins earn three points, rules-drawn matches one,
losses zero; equal scores may share final placement. Elimination order and damage
do not earn points. An interruption or timeout is not a scored draw.
An earlier eliminated player must not receive draw points merely because the
remaining match later draws. Supply explicit individual result facts/policy
before implementing those awards; the initial global outcome is insufficient.

Gate: a complete eight-participant, three-round event, including humans assigned
to different pods, an AI-only pod, elimination, a rules draw, pending completion,
and restart at a completed-round checkpoint. Test assignment invariants cheaply
across many schedule seeds, then a bounded real-engine event sample.
Also restart a partial round with one completed pod and one unfinished pod.

### M4: Complete expedition

Build a cooperative run scheduler over the same match and completion boundaries.
The party chooses between encounter alternatives, keeps its deck progress, and
reaches a boss or a clearly presented run defeat. The first run is three battles;
the noncombat event adds a deck-tuning choice without another full match.

Use one run authority for route proposals, votes, readiness and reward claims.
Record a deterministic tie policy before the run; the proposing leader breaks
tied route votes. Each player controls their own upgrade choice. Disconnects
must lead to an explicit wait/rejoin/leave decision, not silently choose for them.

Snapshot starting decks into the run, reusing existing deck version identities.
Rejoining preserves the original participant's slot. Adding new participants or
changing commanders during an active run is outside initial v1.
Offer small, inspectable card-swap rewards that preserve format legality, deck
size, singleton rules and commander color identity, including partner decks.
Skipping a reward remains valid. Commit a reward and its new deck version once;
the original saved deck remains intact. Persistent card collection/metaprogression
and arbitrary rule-changing relics are outside initial v1.

Checkpoint after match completion, route selection, and reward commitment. A
restart restores pending between-match choices without duplicating rewards. A
loss ends the run; an interrupted match resumes from its pre-match checkpoint
as a disclosed new attempt, with no invented result or earned reward.

Gate: complete solo and four-human runs through a boss, plus run defeat, skipped
reward, partner-deck upgrade, simultaneous party commands, and restart during a
pending reward. Review pacing, route clarity and reward usefulness with people.

### M5: v1 release readiness

Finish the full journey: discover a mode, form a party, understand the encounter,
play, read the result and its causes, make the next choice, save/leave, resume,
and finish. Introductions, reward cards and results must use the same game
language as the table, with full card reading and clear team ownership.

Exercise supported party sizes and maximum match size through concessions,
elimination, disconnected guests, host application exit, engine failure, stale
commands and repeated network delivery. Explain interrupted work and its recovery
without corrupting completed progress. Define save-schema compatibility and
content-version handling; missing content cannot silently substitute a new deck.
Measure match duration, AI decision time, all-pod resource use, state publication,
and crowded-scene rendering. Set release budgets from recorded M1/M2 evidence;
do not raise timeouts merely to hide a failed gate.

Gate: exact-candidate human approval for a solo expedition, friends' expedition
and multi-pod circuit; focused and existing required automated checks; exact-master
publisher checks and public download verification. Keep candidate identity and
automated evidence separate from the recorded scope of human approval.

## Ownership and invariants across every milestone

| Owner | Authority |
| --- | --- |
| Party/run domain | Membership, encounter schedule, choices, progress, deck references, and rewards. |
| Run host | Validates member commands against the current revision, commits transitions, persists checkpoints, and publishes viewer-authorized projections. |
| Match provider | Rules capabilities, legal actions, visibility, teams, live state, and match completion facts. |
| AI policy | Chooses for its seat using only authorized observations and explicit objectives. |
| Client/presentation | Local navigation, reading and animation; submits current intents and displays authoritative projections. |

An accepted result belongs to one exact encounter attempt and match. Accepting
it twice has one effect. Terminal receipts, result acceptance, reward delivery
and progression use durable, recoverable commits; a persisted receipt can finish
its pending acceptance after restart. Clients never infer a win from labels,
animations or a disconnected seat. New matches receive new action authority;
restored run state cannot restore expired action handles.

Saving between encounters is part of v1. Restoring an unfinished live rules game,
automatic host migration, genuine shared-turn Two-Headed Giant, four-versus-four
tables, an arbitrary encounter scripting language, public matchmaking, and a
second rules-engine implementation are later work. The boundaries must permit
future implementations without pretending those capabilities exist today.

M1 precedes the mode work. M2 runs in parallel with M1 and remains a promotion
gate. M3 and M4 reuse that foundation and can develop independently after it is
stable; M5 integrates both. Each milestone ends in a playable reviewable build.

## Design evidence and intended adaptation

- The player's supplied MTG Arena references and the
  [shared experience rules](Mana-Table-Experience-Review.md#design-rules-learned-from-player-feedback)
  establish seat-facing cards, source-to-stack-to-battlefield motion, camera
  movement through one table, and noncommitting inspection. Preserve those
  gestures as the cooperative table grows. Arena's two-sided arrangement does
  not demonstrate a readable six-seat table; maximum-size review is required.
- Mega Crit's [Slay the Spire 2 developer page](https://www.megacrit.com/press-kits/slay-the-spire-2/)
  documents deck development, varied encounters, relics and cooperative play.
  A branching route, readable reward selection, and returning to the party's
  route after battle are proposed Mana Table adaptations, not UI behavior
  established by that page. Before implementing those surfaces, record a concrete
  gameplay reference for their spatial arrangement, gesture and transition, plus
  the complete source/target/intent/visible-result journey. Commander match length
  and deck legality limit direct transplantation of roguelike pacing and rewards.
- Wizards' [Archenemy design account](https://magic.wizards.com/en/news/announcements/evolving-archenemy)
  supplies the cooperative-party-versus-boss precedent. Our bosses use supported
  encounter definitions; this does not promise official Archenemy scheme rules.
- Wizards' [Two-Headed Giant rules](https://magic.wizards.com/en/formats/two-headed-giant)
  describe shared life and turns. Our initial individual-turn cooperative format
  must be named and explained separately. The source audit found unfinished
  shared-turn rules support, so there is no cosmetic 2HG toggle in this plan.

Implementation must follow the [architecture](Mana-Table-Architecture.md),
[AI framework](Mana-Table-AI.md), [testing](Mana-Table-Testing.md), and
[release](Mana-Table-Releases.md) guides. Reuse cheap exact-decision replays and
focused continuations first, then bounded same-JVM campaigns and full lifecycle
journeys. Expand supported coverage by mechanic and boundary risk, rather than
trying to enumerate every possible card interaction.
