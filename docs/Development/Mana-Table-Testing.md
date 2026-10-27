# Testing Mana Table

Mana Table has one client: the Windows MonoGame app. Use the smallest check that
covers the change. Reuse the existing Java and native harnesses; do not add a
second client or rules simulator to test the shipping app.

Run commands from the repository root. Native development requires the .NET 9
SDK; engine work requires JDK 17+ and Maven 3.8.1+. Node is only for dependency-free
documentation and CI tools. See [native setup](../../mana-native/README.md) for
a complete engine/runtime/resource bundle.

## Canonical interaction coverage

The existing graphics-free `Mana.Client.Tests` runner also checks the `Mana.AI`
framework. Scripted policies exercise all fifteen typed decision families and
the same contract permissions, plus source-offer membership, observation/offer
revocation, budgets, late completions, per-seat bounded knowledge and one-shot
host receipts. Missing strategic capabilities are explicit; these checks do not
demonstrate a production bot or enjoyable behavior. The assembly dependency check
keeps AI independent of Forge, rendering and the updater.

The existing `--smoke-headless-engine` process runs a short scripted AI-framework
continuation through real starting-player selection, keeping a hand and playing
an exact offered land route before its normal application journey. It verifies
the later authoritative battlefield rather than treating command acceptance as
resolution. It reuses the same initialized JVM; fixture setup passes are explicit
human test-driver commands, not AI passes inferred from incomplete discovery.
No additional CI job or separate rules simulator is introduced.

After that journey, the same JVM runs four complete `CasualCommanderPolicy`
seats against different Commander decks, including a saved Toothy/Pir partner
deck. No scripted driver answers a bot. A separate public spectator records
the changing board and stack in `whole-seat-ai-public.jsonl`; private decision
histories live separately in `whole-seat-ai.json`. The focused standalone option
is `--smoke-whole-seat-ai` with the same engine and disposable-profile arguments.
The same JVM then runs one diverse campaign match with a 16-global-turn limit
and a 35-second progress cutoff. Its shared singleton deck manifests contain ordinary spells and
lands for Rhys, Pir/Toothy, Balmor and Traxos, rather than land-heavy isolation
decks. `--smoke-ai-campaign` runs two seat rotations, each with a 32-turn limit
and a 45-second progress cutoff, in one engine process. Time cutoffs are checked
between steps; a pending RPC or teardown can extend elapsed time. Reaching a
natural outcome or the requested turn boundary completes the continuation. A
time cutoff is inconclusive and cannot pass that gate using earlier development
or accepted-command counts; it also does not prove a policy loop. A mechanical
failure stops subsequent rotations and records them as unrun. The campaign records deck/policy/engine
provenance, actual public state, missing mechanic coverage, and separate review
findings. Review findings are not automatically strategy errors: a forced
own-land return is legitimate. Policy seeds do not control Forge's rules RNG.

## AI release evaluation

The short campaign checks progress. AI release evidence also needs completed
games, controlled outcomes and recorded human review. Keep these gates separate:

| Gate | Required evidence | Insufficient evidence |
| --- | --- | --- |
| Provider and rules conformance | Exact-source Java/native checks of real current decisions, costs, allowed resources, visibility and subsequent rules outcomes | A plausible-looking frame, accepted command or a policy's own prediction |
| Policy and tactical continuations | Controlled production-policy journeys, including positive X investment through actual payment and outcome; deterministic funding/tactical regressions | A manually supplied X value alone, an offline proposal alone, or a rare draw absent from a campaign |
| Whole-game completion | Four different Commander decks in four cyclic seat rotations, each reaching a verified engine outcome before cleanup | Earlier battlefield development, a turn count, a forced concede, or a stopped simulation |
| State, authority and lifecycle integrity | Exact-board public comparisons, private-window and fault checks, plus fresh-RPC rejection of consumed scopes for every exercised seat | Cached command receipts, retained historical disclosure, or a client-only rejection before the request reaches Java |
| Human gameplay review | Recorded review of the exact candidate and findings, with reviewer, artifact identity and disposition | Pending review, passing automation, win rate, or equal damage/target distributions |

Run the manual long profile with immutable test and production artifacts in a
new private profile:

```powershell
dotnet '<immutable-harness>/Mana.Table.Tests.dll' --evaluate-ai-release --engine-resources '<candidate-package>/engine' --engine-jar '<test-fixture-wrapper.jar>' --production-engine-jar '<shipping-engine.jar>' --source-revision '<full-40-character-commit>' --source-manifest '<ci-engine-manifest.json>' --profile '<fresh-private-profile>'
```

`--engine-resources` must identify the `engine` directory of the exact candidate's
release package. The runner checks its `build.json` source revision, release
marker and engine hash, and compares the packaged `Mana.AI`, `Mana.Planning`, `Mana.Contracts`
and `Mana.Forge` assemblies with the loaded harness dependencies. It records
the actual rules-resource path and build-metadata hash. Retain the verified
package/source receipt which binds the complete runtime archive. An older
installed resource bundle or a preview assembled from external resources does
not establish exact-candidate release evidence. The packaged headless CI lane
uses these same provenance checks.

The wrapper is a test-only launcher for controlled positions using the exact
production engine. It is checked against the shipping JAR and never supplies a
shipping scenario API. The existing process initializes one JVM for the eight
controlled native goldens and subsequent games. The release campaign uses four
cyclic seat rotations, each bounded by 160 global turns and 180 seconds. This is
at most twelve minutes of nominal game budget, separate from fast CI. Limits are
checked between completed steps; an in-flight request and cleanup can extend
wall-clock time. These are test limits, unrelated to a player's decision timer.
Neither ceiling passes the natural-outcome gate. A legitimate engine-reported
draw can pass; the driver does not invent individual loss reasons which the
provider omits. It records the public state and outcome before its only
`StopSimulation` cleanup call. Forced cleanup, aborts and automation faults cannot
manufacture a finish.

The controlled journeys cover funded/unfunded X, funded/short consuming sources,
optional effect-cost decline/payment, flexible-output payment, and mana
contribution completion. The flexible-output journey
starts with Temple of Epiphany and blue-only sources: the normal policy must
execute its exact red output, shrink Balmor's actual bill from blue/red to blue,
then pay blue and observe the same source on the stack and resolved battlefield.
Immediate spending can consume produced mana before the next publication, so a
transient floating red pool is not required. The fixture supplies only declared
pregame permanents; no test driver answers the policy's choices.
The contribution journey casts Kasla through actual convoke with six selected
creatures and a seventh unused creature. It requires positive then exact-empty
progress, current Done, no further contribution offer from the unused creature,
and the same public source resolving as 5/4. Real Java checks also cover undo,
positive bills with incompatible candidates, projection purity and a nested
multicolor choice observed while its UI callback waits for the answer.

The fixed 16-turn/35-second CI prefix and the two-rotation 32-turn/45-second smoke
remain unchanged. Do not rerun a failed sample until one happens to pass, lower
its target, or enlarge its budget to relabel it green. Preserve the first
failure/cutoff; investigate the actual scopes and progress. A source correction
requires new candidate artifacts and new evidence, with prior results retained.

`ai-release-evaluation.json` reports `automatedReady` separately from
`releaseApproved`. The runner always records human gameplay review as pending
and never approves itself. Review must identify the source commit and SHA256
hashes of the actual production/wrapper engine, policy and test driver. The
required source revision must match the harness's exact informational-version
SHA. The engine artifact manifest also binds that revision to the production
and fixture-launcher hashes and byte counts. CI supplies both arguments;
`--source-manifest` is optional for a locally asserted run, whose weaker
provenance is recorded separately from receipt-verified evidence. The release
package checks still apply. A local source assertion and embedded metadata
alone do not independently prove a clean build; retain the
CI artifact receipt when available. Older captures with predecessor metadata
remain diagnostics, not exact-candidate release proof. A policy seed controls
policy choices only. Forge's rules RNG remains uncontrolled, so these rotations
are not identical-shuffle experiments or reproducible engine replays.

Policy review also holds an accepted zero-X announcement which resolved and
whose same public source was later seen in the graveyard with nonpositive
toughness. That observation requires an explanation of intent; it does not make
all legal X=0 actions wrong or prove which effect caused the card's later state.
Up to four original numeric requests are retained in `private-critical-replays`
even when the ordinary sixteen-case replay budget is full. Mechanical state and
authority results remain separate from these strategic review findings.

Typed match results separate completion, mechanical failures, missing mechanics,
numeric proposal/receipt/outcome facts and payment provenance. An explicit own
optional triggered/effect-cost decline is distinct from abandoning a spell or
activation cost. Unknown provenance remains reviewable. Release-only stale
probes resubmit a previously accepted closed payload with a fresh transport
request after that seat has a different decision. Any acceptance or uncertain
transport result fails the evaluation; the probe supplies no new strategic
answer. All private decisions, scopes, probes and replays remain private test
artifacts and must not be attached to public release downloads.

The [AI guide](Mana-Table-AI.md#ai-quality-work-before-merge-or-release) explains
the supported scope and research basis. The
[release guide](Mana-Table-Releases.md#ai-candidate-evidence) distinguishes this
candidate evaluation from the required packaging/publication checks.

Private campaign evidence includes compressed decision records and standalone
`AiDecisionReplayCase` JSON for suspicious/failing decisions. Run the native
test driver with `--replay-ai-decision <case.json> --profile <fresh-directory>`
to evaluate the current policy on the same authorized observation, history,
evaluation seed and allowance. This mode needs no engine bundle and cannot
execute a command. Its `ai-decision-replay.json` is private diagnostic output;
an offered proposal does not imply acceptance or resolution. The graphics-free
client suite tests replay roundtrips, scope and knowledge isolation, exhaustive
small mana assignments, and controlled tactical/effect-graph expectations.

Numeric investment checks use typed announcement ownership and read-only
funding facts. They cover fixed and repeated-X costs, physical-source reuse,
partial/unsupported funding, bounded work and current source identity. Java's
`NumericDecisionProjectionTest` exercises real scripts and projection purity;
`AutomatedPaymentJourneyTest` follows an actual variable-cost cast through
manual payment, the stack and its resolved counters. These checks share the
existing runners and engine process paths. Campaign numeric records separately
retain accepted choices, same-source payment/public events and observed
counters/power/toughness; source correlation alone does not prove invocation
ownership or that a chosen value caused every later counter. A bounded campaign
which never draws the relevant card reports missing coverage. Cancellation
replays link earlier same-source context, not an inferred owning cast or trigger.

The same finite-resource and color-assignment oracles exercise the neutral
`Mana.Planning` core directly and through the AI adapter. Native
`ManaFundingWireChecks` decode actual payment/source-preview shapes and reject
foreign resources, invented pool units, injected action handles and malformed
metadata. Explicit current pool applicability remains distinct from legacy
missing knowledge and from a nominal funding witness. Client-session checks
revoke source previews on revision changes and preserve exact command permissions.
Real Java payment journeys exercise manual source use and floating-pool spending,
checking that each publication shrinks the unpaid bill without mutating it during
a source-catalog read. The authored `mana-payment.v1` presentation journey uses
those contracts and ordinary pointer input; it is not a second rules simulator.
Its geometry and interaction assertions support actual rendered review at both
small and large windows, which remains necessary to assess readability.
The real two-color source journey also checks explicit output intent through
priority and payment, invalid/stale intent rejection before mutation, and the
human callback's distinct producer and original paying source. Native
`ManaColorPaymentWireChecks` rejects malformed bill/payer data, action handles
in read-only context and parent payment commands during the color choice.
The authored child continuation retains the bill while ordinary pointer hover
reads the producer and an offered color orb answers only the current child.
`ManaReductionWireChecks` distinguishes unknown progress from exact zero, rejects
injected handles and unrelated prompts, and proves that covered cost metadata
cannot enable disabled Done or parent payment commands. The source-query
integration journey opens the real reader and waits for its current action
generation before establishing its strict repeated-read baseline. Cached state
observation is not a UI-queue barrier; a rejected purity read is retained and
fails that attempt rather than being retried unchanged.
Golden payment and contribution witnesses come from the exact immutable frame
used for each accepted command. A separate outer poll may miss an intermediate
bill already answered by the policy; it is not that command's observation.
Ordered current-cost receipts and independent public stack/battlefield results
remain separate requirements.
The real `PlaneswalkerCombatRegressionTest` follows lethal combat through the
defending permanent's departure and checks every audience's surviving attacker,
blocker relations, absent defender and continued rules progress. Native wire,
presentation and policy checks distinguish explicit absence from the legacy
player fallback, retain blocker links, reject malformed real identities and
avoid player arrows, life-threat estimates or commands for the absent target.
These regressions use the existing Java and headless lanes rather than another
randomized deck, golden fixture or JVM.

On failure, `private-failure-manifest.json` identifies the last completed raw
observation batch, exact seat scopes, stage and original exception before teardown.
If decoding failed before that batch could be accepted, the separate
`private-rejected-observation.json` retains the actual rejected wire batch.
The last completed batch can be valid and must not be mistaken for the failing
input. Both are host-private diagnostics, never policy inputs or capabilities.
The manifest labels
`last-before-mechanical-failure.json` as the last policy-evaluated request, which
can precede a frame-validation failure. The raw batch is diagnostic data without
execution authority; it can reproduce contract validation without replaying a
command. Secondary artifact errors do not replace the original failure.

Campaign disclosure checks compare the bot's private-zone windows with its
current disclosed faces and action permissions. They are consistency checks,
not an independent proof that the provider correctly granted every entitlement.
Real rules-side privacy and revocation scenarios remain necessary.
Current anonymous option faces and explicit read-only disclosures share one
face/multiplicity budget across hidden zones. They do not authorize extra
physical identities, selection flags or source catalogs. A prior reveal is not
permission for the next choice. The mixed-hand Thought-Knot Seer fixture in
`ScryVisibilityTest` checks eligible nonlands separately from a currently
disclosed land, other audiences, and immediate revocation while rules cleanup
is held. Decoder checks keep that readable land outside the selectable indices.
An optional search confirmation may retain one independently disclosed own top
card, with its exact readable face and no selection or source-action authority.
That does not authorize any library contents during an entered search. The
native disclosure regressions cover this distinction and extra-card, foreign-owner
and mismatched-face negatives in the existing headless lane; the real
`PlayAccessPublicationTest` checks continuous top permission before accepting a
search and its suppression during the actual search.

This checks actual development, paid abilities and combat, without treating
progress counters as evidence that the opponents are fun. Java
`AutomatedSeatBoundaryTest`, `SourceActionDomainTest` and
`AutomatedPaymentJourneyTest` cover the controller role, public/private audiences,
complete source discovery and restricted/colored/floating mana. The client runner
also covers the host's no-replay/error handling and stable per-turn route memory.

Optional decision timing runs in existing suites. `DecisionTempoTest` advances
fake monotonic time through presentation, paused processing, simultaneous seats,
grace, warning-only expiry and repeated unattended priority windows without
waiting real minutes. `DecisionTempoIntegrationTest` uses real Forge priority and
opening decisions, checks stale scope/foreign acknowledgement rejection and
normal elimination, and asserts clock reads leave the board revision unchanged.
`LobbyTempoTest` covers host/setup authority, invalid changes, Ready consent and
policy retention. The existing three-player native journey now selects timing
and readies remote peers with the exact policy revision; no extra CI process is
added. Native presentation checks cover neutral clock decoding/interpolation and
the lobby gesture/consent path. `decision-tempo.v1` is the authored visual case
for quiet/urgent clocks, off-turn responses, grace, expiry and resolving. It is
not a rules simulation or human approval of the presentation.

The shared client check runs `InteractionContractMatrix`: named cases cover every
concrete `GameCommand` variant, `DecisionIntent` and `DecisionPurpose`, with a
closed-command/enum coverage guard, fixed-seed permission/visibility transitions, authority rejection and
required continuations. It uses production command builders and `ClientSession`
with a finite scripted authority. Its two-provider alias case verifies that
provider handles cannot be replayed as semantic commands. It is interaction-contract
coverage, not a Magic rules simulator or proof of every possible card combination.
The matrix uses no JVM, graphics device or network; it prints its measured runtime.

Real Forge goldens use the existing `canonical-interaction` TestNG group: paid
casting/payment, private partner search, Toothy's last-known counters and draws,
prepared-spell casting, counter-source cost payment, Convoke assignment versus
ordinary mana, pending versus prevented damage, combat allocation, persistent Speed,
and creature-type land entry with token-producing cards available for ranking. The
partner search retains four recipient variants. These methods already run once
in normal Java CI; grouping them adds no duplicate engine startups. The focused
command selects their classes as well as the group so upstream modules do not
start empty TestNG processes:

```powershell
mvn -pl forge-api -am test '-Dgroups=canonical-interaction' '-Dtest=SourceActionsTest,PartnerChoiceExperienceTest,ToothyLeavesBattlefieldTest,CounterCostExperienceTest,PaymentCandidateExperienceTest,TargetCounterExperienceTest,PendingEffectTest,AllocationProjectionTest,PlayerStatesExperienceTest,MatchFailureTest,ManaAvailabilityExperienceTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

`MatchFailureTest` also exercises engine interruption outside that canonical
group: every seat retains its last synchronized board, has no pending answer and
receives no defeat from cleanup concessions. `DeckTokensTest` covers ownerless
rules prototypes followed by real deck-configured token creation. The native
`--smoke-headless-presentation` lane covers interruption with an old engine's
stale defeat, local card inspection and a genuine completed defeat. These are
failure/authority checks, not approval of the outcome screen's visual design.

`ManaAvailabilityExperienceTest` compares current cues with actual payment using
floating colorless mana, Wastes, mixed mana, explicit colorless requirements and
Victory Chimes' manual recipient choice. It also follows Omnath's landfall mana
from a real pending trigger to production, while `CounterCostExperienceTest`
checks a paid activation's classification. `HeadlessPresentationSmoke.StackArrival`
uses the production application to cover announcement lifetime, effect hover,
current source state, leaving the stack, an already-running clock and provider
classification validation. Its authored observations test presentation and
command safety; the Java journeys supply the rules evidence. Window captures
and human readability review remain separate.
The small-window full card-reading journey also runs in the fast native release
set, sharing the AI-seat shard. It waits for the actual automatic pass after a
visible stack announcement, rather than assuming the earlier shorter animation
duration. The existing headless arrival check separately verifies that unchanged
observations do not restart that announcement.

`StackResolutionRegression` in the existing client suite exercises a frozen
71-entry intent through individual presentation boundaries, ordinary guarded
passes, Take control, inspection pause/resume, required choices, retargeting,
new entries, stale scopes and connection failure. `StackResolutionTest` is the
real Java counterpart: Blue Sun's Zenith draws 35 with Skulker, Toothy and Pir,
creating 70 independent triggers; another real response brings the stack to 71.
It verifies every actual resolution and Pir's resulting counters, response and
immediate-mana interruption, and a required choice between pass acceptance and
execution. These are authority and rules checks, not proof of readability.
`AiHostRegression` also checks fresh authority after a public pacing beat,
divergent viewer-local event sequences and retention of original host failures.
`AutomatedWireFailureChecks` verifies the shipping provider's private diagnostic
quarantine separately from accepted replay frames and public exception text.
`ActionOccurrenceCoherenceTest` also follows an actual warped Haliya into its
simultaneous delayed/draw triggers while a Swords to Plowshares offer has current
target candidates. Every candidate must bind to an entitled current occurrence;
historical portraits cannot rotate its identity. `MatchActivityTest` separately
protects equal-physical-ID historical views, exact event-time movement anchors
and real concealment. The occurrence fixture also rotates a real command-zone
Rhys cast through every seat: its transient pre-stack payment portrait must
share its payment-owner identity after all audience visibility updates, remain
readonly, survive repeated publications and finish through actual mana payment.
The original floating-mana lifecycle checks preserve the same card's visual
identity from hand through payment and stack to battlefield; a missing original
identity must fail rather than be hidden by advancing into a later turn.
The final commander-damage loss regression checks retained final rules-zone
views, shared public IDs and revoked input scopes. The native terminal journey
renders the result and another terminal update with readonly legacy faces;
active public battlefield faces missing their identity still fail the batch gate.
The campaign's
Forge-specific batch check compares same-board public occurrence projections
before policy evaluation, rather than waiting for a policy frame to validate.
Different private hands, game/revision boundaries and historical stack portraits
remain separate. This adds no second rules model or extra random campaign.
The real multi-seat target-continuation regression checks current restrictions
after one target is chosen, selected-card undo, pure queries, a valid subset's
completion and its actual resulting zone changes. `AiTargetContinuationRegression`
checks that existing policy consumes these current facts and that fresh prompt
UUIDs cannot renew a single actual input's work allowance. These protect a
no-progress loop independently of wall-clock campaign timeouts.
The crowded-field window journey also joins the fast release set, sharing the
three-player lobby shard. It uses the production battlefield faces at the former
threshold, mixed states, duplicate tokens, distinct names and the canonical
480-permanent checkpoint. It exercises actual player focus, wheel zoom, card
reading, middle-drag and Home. Its occurrence checks establish retention and
command safety; they do not establish human readability. The former dense-tile
exposure assertions and pixel hunting are removed with that renderer.
The same window journey includes nine lands with mixed tap states and three
fully tapped lands at each seat. Its counts cover physical state, not mana
availability; the presentation checks separately include animated lands,
nonland mana sources and concealed identities. Inspect the actual counted piles
and seat summaries in both window sizes; assertions do not approve their clarity.
The existing cluttered-changes journey also uses the production renderer in
headless and windowed modes. It retains the full source/delta/activation checks
and zooms to the actual activation card before reading it; it no longer demands
480 simultaneously exposed tiny pointer patches or labels on every overview face.

This is a representative real-rules lane alongside enumerated coverage of the
current interaction command variants. Neither replaces visual review of the corresponding
window journeys, nor supplies the complete authoritative Forge oracle needed to
establish another rules provider's equivalence. Record the case, seed, provider
and first differing boundary when diagnosing a failure; use existing scenario
evidence rather than starting a JVM for each generated contract row.

## Choose the relevant check

| Change | Starting point |
| --- | --- |
| Client/session, contracts, persistence or updates | `dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj -c Release` |
| Presentation and scoped input | Build `mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj`, then run the relevant headless mode |
| Java projection, rules or synchronization | `mvn -pl forge-api -am verify`, or the affected Java test selection |
| Actual Forge/native boundary | Native `--smoke-headless-engine`, startup, adapter or multiplayer journey relevant to the change |
| Visible layout, artwork or motion | Relevant real-window journey and human review |
| Repository documentation/CI tools | `node scripts/mana-table/check-docs.cjs`, `node scripts/mana-table/check.cjs`, and affected `scripts/mana-table/tests/*.test.cjs` |

Do not rebuild or repeat unrelated journeys after the relevant checks pass.
A new source or package change, failed check, or unresolved concern can justify
another run. Controlled projections verify presentation and commands; they do
not prove Magic rules. Mechanical results do not establish visual clarity or a
comfortable interaction.

`StateEffectsConcessionTest` covers real concession during participant traversal
and terminal cleanup. Its focused controller regression also checks timestamp
ordering, removal fallback and nullable search overrides through public APIs on
an unstarted table, including bounded concurrent reads while overrides change.

## Native headless checks

Build the separate test executable once:

```powershell
dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release
$driver = 'mana-native/Mana.Table.Tests/bin/Release/net9.0-windows/Mana.Table.Tests.dll'
dotnet $driver --smoke-presentation --profile '<fresh-profile>'
```

The test flags belong to `Mana.Table.Tests`, not the shipped `ManaTable.exe`.
Choose an explicit fresh `--profile` for every independent journey.

`ForgeCommandBoundaryChecks` in `--smoke-presentation` controls two observation
completions without sleeps or a Java process: the displayed replica and provider
command context must retain the newer revision when the older read finishes.
The current command remains valid, the retired prompt remains rejected, and
same-revision refresh, replacement-match revisions and foreign scopes retain
their separate semantics.

The existing `--smoke-headless-presentation` lane includes `TokenArtworkChecks`:
enter Tokens through the production builder, save/reset exact printing choices,
retain the save acknowledgement and card count, reject an old revision's hit,
and switch definitions while an obsolete gallery response is held in flight.
It runs at small and large sizes with enlarged text, without another CI process.
`DeckTokensTest` in the Java API suite covers newest-first exact definitions,
reprint image addressing, save/reopen/duplicate, undo/redo/reset, stale edits,
unchanged playable versions and actual `TokenInfo` creation with two independent
deck choices. These checks do not approve the gallery's visual clarity; inspect
real token images in the game window as well.

- `--smoke-presentation`: presentation, card-reader, scope, focus, geometry and bounded priority-batch regressions over a controlled provider.
- `--smoke-headless-presentation`: production `TableApplication` composition and pointer input over controlled snapshots.
- `--smoke-headless-priority-flow`: authored stack, responder, recipient and library-choice handoffs.
- `--smoke-headless-clutter`: controlled crowded-field representation and reported effect targets.

These modes create no game window or graphics device. They exercise the same
application and typed-frame composition used by the real window. Preserve their
result/trace JSON when diagnosing a failure; submitted drawing data does not
prove that a physical window is clear or correctly clipped.

The presentation lane also runs the existing `AdvanceRegression` cases and writes
`advance-results.json`: required decisions, visible checkpoints, stale/duplicate
observations, bounded waiting, cancellation and retired-session results. They
start no JVM or graphics device. `--smoke-advance` remains available to run those
same cases alone. Client checks cover separate Auto/quiet intent authority and
their shared batch lifecycle; no additional CI job is needed.

The same presentation lane runs `PublicActivityCursorChecks` for join/reset,
repeated observations, bounded-history gaps and shared feedback consumption.
Its production application journey runs in `--smoke-headless-presentation` and
switches the viewed seat within one game, checking that readers and retained
feedback from the previous seat are revoked. Existing board checks cover held
cards and repeated pose preparation across frame-buffer swaps.

`MatchPresentationLifecycleChecks` shares that presentation lane and verifies
baseline, viewer/rematch reset, mana/priority revocation and public departures.
The existing seat-switch application case also checks revocation immediately
after acceptance, before preparing another frame, so stale hit intents cannot
survive until the next paint.

`HeadlessPresentationSmoke.RulesReader` checks the counter lab's opening source
with an ordinary center press/release, an actually empty stack, read-only rules
flips, current counter/P/T updates, and disclosure revocation between press and
release. Geometry and command safety checks do not establish that a player can
discover the action. Native review should use the shipping `ExperienceLabLauncher`
path, ordinary artwork/center clicks, both window sizes and the full payment
continuation. Lab profiles retain `input-trace.jsonl` for physical input diagnosis.

`PresentationReadChecks`, `DecisionSessionChecks` and `DeckInteractionChecks` run
in the same presentation lane. They cover repeated frame reads without advancing
state, publication on Update, revoked or altered intents, prompt/viewer draft
resets, bounded selections/allocations and deck navigation/reader lifetimes.
The decision checks also replace payload families, option bounds, numeric bounds,
initial values, recipient identities and disclosed face sets under the same prompt
ID. Incompatible drafts and private readers must retire; unchanged observations
must preserve the player's edits. `DecisionSession` owns that local state.
The window only reads the prepared frame. Headless timing measures Update,
including frame preparation; reading the cached frame is not a new composition
or an FPS measurement. These checks supply behavior evidence, not UX acceptance.

The priority artwork-handoff case also runs in that lane. It retains the exact
priority prompt while loading owns the screen, then verifies that the same
ability control becomes available without a command when artwork settles.
Its window journey uses deterministic artwork and waits for that handoff rather
than asserting on gameplay controls behind the loading screen.

The shared client runner includes `LatestReadRegression`,
`DeckWorkspaceRegression` and `AppUpdateSessionRegression`. Controlled completion
gates cover coalesced reads, stale failures, deck revision/metadata/fingerprint
admission, delete/restore epochs, explicit review retry, notify-first discovery,
download cancellation races, prepared-release identity, blockers changing during
verification, explicit restart and cancellation cleanup on shutdown. These use
the existing runners and CI jobs; they add no JVM startup or graphics window.

Its decision-contract cases cover all fifteen payload variants through JSON
round trips, deeply immutable subtype graphs, malformed/null/unknown payload
rejection, and closed command coverage. Allocation checks use normalized typed
recipients and the same constraints as command admission. These are finite
interface checks, not coverage of every Magic interaction.

`NumericTextboxProducerTest` calls Forge's real numeric textbox producer and
compares the checked-in `numeric-text-prompt.json` fixture (apart from its UUID).
The native boundary checks decode that same fixture as unbounded integer entry
and preserve its initial value. This runs inside the existing Java/API and native
presentation suites rather than adding a separate process or CI job.
Window journeys for Build/Tune/Test, printings and Updates still exercise the
production application over those owners. Passing lifecycle checks establishes
transition safety, not human visual clarity or approval.

Shared client checks also run `SourceActionSessionRegression`: delayed provider
reads, seat/source/revision revocation, explicit unsupported versus empty results,
timeouts, manual retry and shutdown use one scoped action-query lifecycle. Native
journeys consume semantic `DecisionPurpose` values; Forge prompt class names stay
inside the adapter. The real-engine opening/payment journey verifies that mapping.

Prepared-spell regressions reuse `SourceActionsTest` and the existing native
presentation lane. The real engine creates Emeritus of Ideation's copy in exile,
casts that exact spell through the source, then checks unpreparing and revoked
links. The `prepared-spell.v1` Experience Lab case provides authored four-seat
checkpoints for source reading, the actual spell face, cancellable targeting,
priority, the draw result and preparing again. Its scripted transition checks
are not a rules implementation. Review the visible prepared marker and linked
spell affordance at the small and large window sizes; passing the lifecycle
checks does not establish visual clarity or human acceptance.

The same presentation lane exercises paired-deck editing through the production
app: discover an absent named partner, add it, select the compatible pair, wait
for the persistence acknowledgment, return to Play and reopen. Delayed saves,
rejection/retry, stale responses and library-list failures retain their distinct
states. The real `DeckCommandersTest` checks discovery metadata independently of
the existing legality checks. No extra CI job or JVM per UI case is required.

`ImmutableStateChecks` traverses the entire match graph and every request/result
of the catalog, printing, deck/workshop/analysis, setup and conversation ports,
rejecting writable nested collections/properties. Provider construction buffers
and consumer casts cannot change an accepted revision or queued metadata edit.
It retains preceding match and workshop views while publishing new revisions,
checks JSON array/object round trips and distinguishes omitted metadata fields
from explicit empty clears. It runs in the existing graphics-free client lane.
The native
presentation lane inspects the emitted UI dependency closure and rejects a
provider/host reference or use of the aggregate engine by `Mana.Game`.

The existing `--smoke-headless-engine` journey also verifies the actual Forge
metadata mapping: a plan-only patch preserves roles and Considering categories,
and explicit empty collections clear the overrides without changing playable
deck contents or their version. It reuses that journey's initialized engine
before starting the same Commander game; it adds no separate JVM or CI job.

That same initialized engine also covers explicit setup association: repeated
setup preserves editor revision, undo and an active practice sample; switching
setup reports the requested document and rejects a write for the former owner at
the same numeric revision. `DeckWorkshopTest` covers those transitions with small
offline fixtures, explicit-open practice invalidation and failed/missing setup
switches preserving the current document. The existing presentation lane tests
the production ready-message validator's required `explicit-deck-setup`
capability without another process.

The production headless run includes saved-deck deletion and Undo through the
actual controls, delayed observations, restart recovery and stale-command guards.
The existing `--smoke-deck-experience` window journey covers that same lifecycle
against Forge, including restoring the original saved bytes and preserving an
unrelated open deck. Deletion requires the optional `deck-library` capability.

The existing presentation run includes `DecisionDockPresentationChecks` and the
two-player continuity checks in `SharedTableStudyChecks` and `StudyFocusChecks`.
They cross the old seven-group boundary from both viewer seats and exercise the
shipping live-table entry, required opponent targeting and battlefield inspection.
Use `two-player-table.v1` for immediate visual review of those projected states.
The same run checks explicit pan/zoom and reversal in `BoardViewChecks`, live
counter/P/T updates during movement, Escape, current player targets and stable
canonical occurrences in `StudyFocusChecks`. `DrawingFrameChecks` rejects leaking
clip scopes; `EffectTransitionChecks` protects the camera-mapped family captions
from counter receipts. No additional CI process is introduced. Review actual
motion in `four-player-table.v1` and `crowded-counters.v1`; checks cannot approve it.
The same run also includes the
scoped authored `DecisionFlowLabChecks`. The production headless run includes
`DecisionDockInteractionChecks`: an actual stop-removal gesture clears only that
saved stop, dispatches an authorized Auto pass and leaves another player's
required choice unanswered. Reading a current card pauses dispatch without
introducing a temporary priority surface. Delayed replies, fresh polls, Hold,
rejection and uncertain acknowledgements exercise the real serialized command
path. Quiet-pass controls retain their heading and position through handoffs;
required local targets preempt them immediately. The existing `--smoke-auto-dock`
window journey captures routine Auto, a real response and required blockers.
`decision-flow.v1` supplies the corresponding window-review
sequence; authored replies and passing checks do not supply rules or UX proof.

`optional-decisions.v1` covers real-player library targeting, a sole legal target,
search/decline at another deciding seat, free/paid opening hands, a general
confirmation and the same library choice over 480 permanents. Load stage 7
directly for a settled crowded observation. The existing headless presentation
lane exercises pile gestures, named choices for seats outside the rendered
viewport, stale press/release scopes, peer alternatives, noncommitting source and
hand reading, and actual remaining stack items. `PartnerChoiceExperienceTest`
checks the live target → optional search → private-library journey, absence of
a matching partner, accepted shuffle versus refusal, and Solemn Simulacrum's
optional search without a player picker. These tests do not approve the UX.

`play-access.v1` places source-distinct exile grants, a graveyard flashback card,
an opponent-owned exiled card and an exiled land beside the unchanged hand.
Its authored stages include retained access off priority, grant expiry,
two routes for one card, physical stack/resolution transitions and larger
collections: 37 cards in hand and 60/200 access cards. `PlayAccessPresentationChecks`
and the existing headless presentation lane cover exact source offers,
noncommitting reading, explicit unfold/return and spatial focus/pan/zoom gestures,
withdrawn disclosure and permission between press/release, and normal/reduced
motion. `PlayAccessTest` uses real Forge grants, flashback, library-top land
access and same-turn Plot access. These checks establish the boundary and gesture
behavior; inspect actual artwork at both window sizes before assessing clarity.
Large collections open at play distance. At the deliberate whole-collection
overview the card text is intentionally not a reading surface:
review moving to readable scale, inspecting and playing the first, middle and
last physical occurrences, then returning to the same source packet. A count
of 200 represented cards cannot establish that the journey is understandable.

`CounterCostExperienceTest` follows actual Loch Mare activation through mana,
counter removal and draw, and retains explicit mixed-type/distributed choices,
removal restrictions and cancellation. `PaymentCandidateExperienceTest` uses
actual Convoke assignment and its zero-assignment route to ordinary mana.
`TargetCounterExperienceTest` checks actual Battlegrowth additions, Thrull
Parasite removal and Proliferate subset/empty choices. These distinguish
already-working engine effects from input-publication and native routing faults.
The existing native headless presentation lane also follows an inspected source
through activation/payment and a keyless historical source face: missing physical
candidate permission stays read-only; an authoritative candidate enables only
its exact current `SelectCard`, then an explicit counter-type option. The same
lane exercises legacy focused-battlefield selection and own/opponent counter
targets without turning an ineligible read-only tile into a command.

`CounterCostExperienceTest` also covers a two-counter cost with one counter
allocated at a time, and a Soul Diviner host distinct from its chosen donor.
The exported type-choice debit is one; available quantity accounts for pending
allocation while the permanent's actual counter total stays unchanged until
payment completes. Native decoder/immutable-state checks retain those facts,
reject inconsistent metadata and preserve the original scoped answer.

For the visual journey, launch `--experience-lab counter-payment.v1 --table-study`.
Start at 0 for forced Loch Mare payment, 4 for mixed types, 9 for Soul Diviner,
or 13 for the crowded donor choice. Read the source, activate, pay or select a
donor/type, inspect the pending debit/current stats, then continue to the draw.
Repeat both counter-type branches and each same-name donor. Use actual small
and large windows, enlarged text and reduced/normal motion. These interactive
checkpoints are authored; compare their semantics with real Forge tests and
record human acceptance separately. A passing command path cannot approve the
workspace, its source visibility or its result feedback.

The same canonical case includes actual public piles with mostly unplayable
cards, separate opponent zones, masked exile, a count-only concealed zone and
an empty graveyard. The public-pile journeys open from the real pile, read and
navigate without commands, preserve the exact source offer for an explicit
play, return through current anchors, and withdraw lost disclosures/actions.
Required-task interruption and reopening must preserve the existing decision
draft. Counts, hidden faces and opponent ownership must never manufacture an
action. Use actual-window captures to assess whether the purpose and physical
return are understandable; passing these checks does not settle that question.

The pending-effect journey in `--smoke-headless-presentation` distinguishes a
captured `+3` counter addition/three damage from a current total of twenty, keeps
unknown damage unknown, preserves the context on the stack, and checks read-only
source inspection and exact target-key/prompt submission. These use controlled
snapshots, not the rules engine.

The source-only actual-result checks reuse these modes rather than adding a
separate runner. `ImpactFeedbackChecks` in `--smoke-presentation` covers explicit
damage recipients, damage versus life loss, activity-ID deduplication, public
departures, concealment, anonymous recipients, older payloads and JSON round trips.
`EffectTransitionChecks` also composes the actual application to distinguish a
`+2 Charge` increment from six settled counters and unchanged current 6/7. It
checks receipt collision handling, blocked marker gaps, reduced motion and expiry.
`--smoke-headless-presentation` invokes `ImpactFeedbackChecks.RunPresentation`
for recipient-attached damage/life results, death between observations and the
absence of routine success/public-outcome panels. Grouped-recipient checks cover
uniform per-copy amounts, exact subsets, different amounts, repeated hits,
latest-burst replacement and offscreen focused recipients. These assertions protect
state and drawing relationships; they do not establish visual clarity.

## Real Forge coverage

Java API, rules and synchronization regressions live in
[`forge-api/src/test/java/forge/api`](../../forge-api/src/test/java/forge/api).
The Maven reactor builds the adapter and its required engine modules. For a
focused selection, retain `-Dsurefire.failIfNoSpecifiedTests=false` so dependency
modules without that test do not fail:

```powershell
mvn -pl forge-api -am verify '-Dtest=ToothyLeavesBattlefieldTest,SyncTransportTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

`QuietYieldTest` reproduces explicit passing with an affordable Lightning Bolt
through upkeep, the normal draw and empty combat, including another seat
choosing no attackers. It also checks interruption by draw triggers, effect
draws, land plays, actual combat and queued required choices. The native
`--smoke-headless-presentation` journey clicks the real Pass for now control,
observes other seats without answering them, submits the next same-token pass,
and leaves a new stack response unanswered. Client checks cover older activity
payloads and conservative defaults. These establish command behavior, not human
approval of the presentation.

`PendingEffectTest` exercises All Will Be One with Chandra entering at three
loyalty, then adding one to reach four, and a nonloyalty counter case. Projection
checks cover unknown amounts, known zero, divided totals and concealed sources.
Its prevention regression casts Chandra normally into All Will Be One while an
opponent controls Urza's Armor: the captured trigger and pending stack report
base three, then the real engine reports actual two damage and life `40 → 38`,
including the other seats' public projections. `MatchActivityTest` covers the
typed event payload and visibility boundaries separately. Run this focused
selection when modifying the base/actual contract:

```powershell
mvn -pl forge-api -am verify '-Dtest=PendingEffectTest,MatchActivityTest' '-Dsurefire.failIfNoSpecifiedTests=false'
```

The corresponding native lab cases remain authored; they cannot establish these
rules results or replace this real-engine regression.

Native real-engine journeys use the bundled Java runtime, the actual adapter
protocol, and matching card resources:

```powershell
dotnet $driver --smoke-headless-engine --engine-resources '<package>/engine' --engine-jar '<verified-current.jar>' --profile '<fresh-profile>'
```

`--smoke-headless-engine` operates a real Commander opening, a land play, a paid
command-zone cast and concession through production input without a GPU. Its
same opening now takes two Commander mulligans: the first returns directly to
Keep with seven cards, and the second projects exactly one required bottom card.
It verifies that reading a
bottom-card candidate does not select it, its explicit reader action selects it,
and final confirmation removes it from the kept hand. Headless presentation
checks cover the same noncommitting hand reader for engine inputs and staged
card lists, including deselection and prompt replacement during a press. It
opens the commander's current source actions, drags it into play and activates
a Forest during its actual mana payment. Startup, workshop adapter
and multiplayer modes cover their own boundaries; choose them when affected.
Their entry points are in [Program.cs](../../mana-native/Mana.Table.Tests/Program.cs).

`--smoke-lobby-three-player` stops opening replies when any seat first observes
`MAIN1`, then waits for all three private views to agree on the playing game,
turn, active seat and usable priority owner. A transient phase label alone is
not readiness. Its existing 45-second opening deadline retains the latest views
on failure; it does not pass the first main phase while gathering them.

Use an immutable engine JAR whose source/hash is known. Rebuilding
`forge-api/target/forge-engine.jar` does not refresh an existing package. Do not
replace a JAR or package while another process uses it. A controlled UI fixture
cannot substitute for a real engine regression.

## Experience clarity and recovery

Use [Experience review](Mana-Table-Experience-Review.md) to open the shared,
versioned Experience Lab cases and review actual screens and transitions.
The lab uses authored projections, cached art and an isolated profile. It does
not run Magic rules or mutate real decks.

The source-only `damage-results.v1` case follows a surviving creature's reported
three damage, another damage event with a public departure between observations,
then a pending base-three player target and authored actual-two result at 38 life.
Its final pair makes two Bears take one damage each and merge with an already
damaged third Bear; the shared face must show one per-copy amount and two-of-three
membership rather than an invented two-damage hit.
`counter-damage.v1` retains the separate captured increment versus current loyalty
journey. Review their actual target gesture, pending state, impact and settled
current values at small and large windows, with realistic artwork and reduced
motion. Confirm that increment receipts leave names, counter totals and P/T
uncovered, and that a crowded field does not become overlapping transient labels.
Record graphical and human review for the exact candidate; these new journeys
remain pending human review and release verification until that evidence exists.

The equipment window journey uses the production renderer and `attachments.v1`.
It reads actual exposed edges, checks the host's authoritative P/T and attached
counters, follows re-equipping/detaching, distinguishes a tapped host from its
Equipment, and navigates to a foreign-controlled Aura amid 480 permanents.
It replaces the retired layout's connection-line and badge-pixel assertions;
its real pointer gestures establish interaction behavior, not visual clarity.

The existing headless presentation lane also runs `AttachmentStackChecks` over
the production shared layout. `SharedTableStudyChecks` additionally runs the
`tapped-resources.v1` checkpoints for every viewer, verifying that the same
resource dimensions turn into a different outline without moving their centers
or crossing reserved lanes. The lab case is the visual tap-recognition review.

`AttachmentStackChecks` covers
the production shared layout: exact occurrence/controller retention, exposed
pointer targets, switching and closing readers, counter-bearing Equipment,
seat-facing layouts for every viewer, upright readers from actual left/top/right
pointer gestures, independently tapped faces and rotated attachment edges,
re-equipping/detaching, nested and foreign-controlled attachments, distinct
reference/visual ID domains, malformed links, two-player views and crowded fields.
It does not start another CI process. Use `attachments.v1` for real-window review
at 1000 x 740 and a larger window, including enlarged text, motion/reduced motion
and camera focus. Agent screenshots and automated invariants do not establish
human acceptance of the fan or its small exposed targets.

For a relevant graphical regression, use the same test driver, for example:

```powershell
dotnet $driver --smoke-card-reading --size 1000x740 --profile '<fresh-profile>'
dotnet $driver --smoke-mana-payment --size 1000x740 --profile '<fresh-profile>'
dotnet $driver --smoke-mana-payment --headless --profile '<fresh-profile>'
```

Other focused modes include `--smoke-priority-flow`,
`--smoke-cluttered-changes`, `--smoke-recovery`, `--smoke-state-clarity`,
`--smoke-allocation`, and `--smoke-deck-experience`. Consult the
[native guide](../../mana-native/README.md#focused-verification) and driver rather
than running every mode for every change.

For layout/input changes, inspect the smallest supported physical window and a
larger window, including affected text-size and motion settings. Look at actual
`-window.png` captures and perform the relevant clicks, not only the design
surface. Preserve the observed problem and manual follow-up; test totals,
screenshots, and timing measurements are not human acceptance.

### Capture a real saved deck workspace

The existing native driver also supports `--capture-deck-workspace`. Prepare an
isolated seed profile using the app: import and select a realistic saved deck,
set text size to 130%, and let its authorized artwork populate that profile's
`art` cache. Close the app, then copy that isolated seed to a separate profile
for each window size. Do not point this journey at a player's installed profile.
It requires a saved selected document and a complete real Forge engine bundle;
it does not create the synthetic analysis fixture used by other regressions.

After building the same driver above, run:

```powershell
dotnet $driver --capture-deck-workspace --profile '<isolated-seeded-profile-1000>' --engine-resources '<package-engine-directory>' --size 1000x740
dotnet $driver --capture-deck-workspace --profile '<isolated-seeded-profile-1540>' --engine-resources '<package-engine-directory>' --size 1540x1000
```

The journey follows actual pointer targets through Home, Build, Tune, an engine
relationship (or a card-linked finding when available), full-card reading, Test,
a draw-only sample and Home again. It saves `workspace-*-window.png` native-window
captures, matching design surfaces/state JSON and `workspace-capture-results.json`
in the chosen profile. Missing optional analysis content is not invented. It can
save a practice sample, so use a fresh copy of the seed for another capture.
Its bounded wait and accepted-pointer checks are capture safety only; inspect
the native windows and interact manually before recording human acceptance.

For gesture changes, retain the actual before, held, release and result states.
Exercise cancellation arriving with release, and changes to the exact action key,
permission or visibility during the gesture. Slow background observations must
not flicker controls or retarget a held press or armed intent. Check cold and
warm image caches when image loading affects the journey. Use the relevant
existing harness and record human feedback separately from these assertions.

## Profiles and artifacts

The normal app profile is `%LOCALAPPDATA%\ManaTableNative`. Development and test
runs use `--profile <fresh-directory>`; never use a player's installed profile
or production installation for lifecycle tests.

Retain the exact source revision, package/build metadata and engine hash with
reproduction steps. A dirty candidate also needs the reviewed assembly hash,
base commit and retained diff identity; the base commit alone cannot identify
the tested binary. Useful artifacts include:

- `native-error.log`, `client.log`, and `engine.log` in the selected profile;
- the journey's result, observation or semantic-trace JSON;
- physical-window captures or a recording for visual/input failures;
- Maven Surefire reports and engine decision traces for Java failures.

Keep profiles, caches, packages and test output out of commits. Inspect the first
failure before rerunning, weakening an assertion, or adding a retry.

## Packaged verification

Use a fresh extracted native package or a newly built matching preview, as
described in [Releases](Mana-Table-Releases.md). Check `SOURCE.txt`, `build.json`
and the engine hash before attributing a result to a revision.

Real-engine test modes accept `--engine-resources <package>/engine` and an
optional explicitly verified `--engine-jar`. The production executable accepts
its documented app options, not test harness flags.

Installer and updater lifecycle scripts use separate smoke application
identities and profiles. Follow the [release guide](Mana-Table-Releases.md) for
the applicable installer, managed migration and installed-update checks.
Fixture downloads do not establish real release bandwidth, and a command-line
patch reconstruction does not establish installed restart behavior.

## CI and review

Keep a PR in draft until its final head passes the applicable required checks:

| Check | Responsibility |
| --- | --- |
| `desktop` in [Mana Table](../../.github/workflows/mana-table.yml) | Lightweight repository tools, documentation and CI contracts; the historical check name does not denote a second client |
| `Verify and package Windows x64` in [native Windows release](../../.github/workflows/mana-table-native-release.yml) | Shipping native package and its required client, engine, UI and installer/update evidence |
| `Test with Java 17` and `Test with Java 21` in [Test build](../../.github/workflows/test-build.yaml) | Engine/API reactor verification on both JDKs |
| Applicable [iOS compatibility gate](../../.github/workflows/ios-compat-gate.yml) | Upstream iOS/MobiVM compatibility when affected |

The explicit workflow steps and `mana-native/ci/native-ui.ps1 -ListOnly` define
automatic coverage. A local mode's existence does not mean CI runs it. The
extended inventory remains available for targeted investigation; it is not the
default local checklist.

Within native verification, the installed SDK update lifecycle starts after the
immutable tools artifact is available. It can run alongside packaging. The
product's managed migration lifecycle still waits for both tools and the actual
package and verifies the real Forge-backed application. Both jobs remain required
by the Windows aggregate and upload separate failure evidence. This scheduling
change retains the full lifecycle tests; runner availability still affects time.

`test-native-ci.ps1` also runs `test-source-receipt.ps1`. These cheap fixtures
exercise production rejection of receipt-only input and smoke rejection of
missing, changed or mismatched source, version, build metadata, archive hashes,
sizes and runtime-embedded metadata. Production installers continue using the
actual source ZIP; only isolated smoke identities use its producer-bound receipt.
Both paths run in the existing native checks and installer/migration jobs.

The installer workflow has a read-only PR caller and a master-only staging
caller. The required Windows aggregate expects exactly its authorized caller to
succeed and the other to be skipped; every remaining dependency must succeed.
Repository tests execute that actual aggregate script for both contexts and
exercise release staging/promotion failures through injected API fixtures.
Private drafts do not establish a successful release or bypass final source CI.

The [manual transport benchmark](Mana-Table-Releases.md#measuring-artifact-transfers)
is outside the required aggregate and never runs on ordinary PRs or releases.
Its exact-byte checks and action timings compare wrapped versus raw transport;
they do not replace native or installer tests. Record the benchmark run and
payload identity when reporting results, separately from full-build wall time.

Record the exact tested head and completed workflow links. Earlier-commit
success, an uploaded intermediate engine artifact, or an unpublished release
draft does not establish success for the final source. Native publication checks
the merged master revision and matching artifact identity again. Human review
remains separate from these automated gates.
