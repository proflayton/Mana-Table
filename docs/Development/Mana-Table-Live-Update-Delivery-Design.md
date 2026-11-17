# Update ownership and live update delivery design

Status: U1 design accepted and implemented as a locally verified source candidate,
16 November 2026; technical-lead code review found no blocking issue. Integration,
required PR checks and public release qualification remain pending. U2 remains
design-only and gated. Mainline baseline:
`0eaa9f70abe3a40f92342352a20dc805a0ea730a`. Candidate input:
`c79263b887ae2ee93217fdaeb739b0f6b386fa74`, the existing live-update design and
implementation branch. This specification refines that branch's M1 through M6
plan. It does not replace its component architecture or announce live updates.

The first independently mergeable result is a retained owner for the existing
restart updater. The isolated candidate then uses that same owner for one complete
authenticated A-to-B product journey. Neither result establishes production trust,
arbitrary live-game boundary support, or release readiness by itself.

## Source audit and decisions

| Boundary | Mainline 0.2.469 | Candidate c79263b | Required disposition |
| --- | --- | --- | --- |
| Update workflow | `AppUpdateSession` owns work, but `TableApplication` constructs/closes it and supplies `session.Post` and private Game callbacks | Still Game-owned; passive checkpoint copies settled update state and `UpdatesPort` scopes provider calls | Retain one operation owner outside the replaceable Game; views borrow revocable command/read authority |
| Product apply path | `Install` verifies, invokes the composition's restart callback, then requests exit | Actual Updates handlers still take the restart path | Preserve mainline restart behavior; connect the isolated minor path to Host only after the complete M1 witness |
| Host admission | No live loader | `WindowUpdateCoordinator` stages and issues one-use admission, but Program does not compose it into the update gesture | Evolve this owner; do not add another coordinator |
| Completion/persistence | Managed full/delta updater, explicit restart | `CompleteAdmissionAsync` still calls `RetainForNormalExitAsync`; admission and updater retain exit-installer state | Remove this minor-path coupling; durable component selection replaces it |
| Verified code | Trusted installed package and updater transport | Checked manifests, closures, returned bytes, host/runtime preparations and leases | Preserve content checks; add publisher-authenticated store reopening rather than trusting cached JSON |
| Cold startup | Installed build | `OpenApplication` still opens installed/development baseline | Select authenticated desired generation before constructing Game/runtime; retain trusted baseline recovery |
| Runtime isolation | Concrete composition in `Mana.Table` | Host and shared Runtime still reference ordinary `Mana.Client`; component recipe classifies Client as shared | M1 may keep exact unchanged Client bytes; M2 must move ordinary Client behavior into the private table cohort |
| Product evidence | Installed restart-update journeys | Distinct-image swaps, repeated mounts, real-provider catalog work, controlled updater admission | Reuse as supporting evidence; none proves the actual complete live update gesture, changed runtime behavior and offline relaunch |

The revised candidate design already rejects applying a minor installer at normal
exit. Code still supporting that route is unfinished migration, not a second
approved persistence option. Do not import the entire candidate into mainline to
make the ownership extraction compile. Mainline has no `Mana.Host`, `Mana.Runtime`
or `Mana.Graphics` dependency.

## Mainline execution slice U1

U1 moves the existing `AppUpdateSession` lifetime and completion dispatch to
window composition, without changing installation format, release discovery,
download consent, or the explicit **Restart to update** behavior. It removes
update work from the rules-session completion queue. It also supplies an explicit
view-revocation boundary required by a future presentation replacement.

There is no new layout or player control in U1. The existing Updates entry,
Check, Download, Cancel and Restart gestures keep their meanings. The current
game, connected table, unsettled work, unsaved deck, draft and capture blockers
remain authoritative. This is an ownership change, not an opportunity to restyle
the update screen. Any later new apply presentation must follow the
[shared game-reference and journey rules](Mana-Table-Experience-Review.md#design-rules-learned-from-player-feedback).

### U1 ownership and API

Evolve `AppUpdateSession`; do not wrap it in a competing workflow/controller.
It remains the sole owner of phase, request generation, provider task,
cancellation, prepared identity, preferences and terminal results.

| Value | Owner | Lifetime and authority |
| --- | --- | --- |
| `AppUpdateSession` | `TableWindow` composition; candidate later uses its stable Host | From window construction through actual update-task settlement at shutdown |
| Current view binding | Session issues one `AppUpdateViewBinding` | One admitted presentation role; explicit retirement clears current identity |
| Update view | Session | Immutable operation publication; replacing the view cannot restore copied Busy/task state |
| Local update-modal state | `TableApplication` | Open/closed state and modal generation; never provider/task ownership |
| Install context | Current Game supplies a neutral immutable value | Fresh at explicit request and again at final handoff; not retained as a callback into Game |
| Provider service and restart handoff | Existing Program composition | Disposed only after the retained session has joined actual work |

`AppUpdateSession` borrows `IAppUpdates`; it never disposes or transfers ownership
of that service. Program remains its sole disposal owner through the existing
update-lifetime scope. Window teardown closes/joins its retained session before
returning to Program's provider disposal. Repeated window/session close calls
join the same close task and cannot dispose the provider a second time. Enabled
test fixtures keep the same order: retire their view, close the retained session,
then let the fixture's existing service owner dispose the provider.

Proposed concrete API names are `AppUpdateSession.AttachView()`, an idempotent
binding `Dispose`, binding commands `Check`, `Download`, `CancelDownload`,
`Install(context)`, `ToggleStartupCheck`, and immutable `View`/`Build` access.
Mutators on the session become internal owner operations rather than a public
bypass around binding admission. A binding contains only its owner and opaque
identity. Each command checks reference identity against the current binding;
an old A binding cannot act after A-to-B-to-A, even if all visible values match.
Returned IDs and display generations are diagnostic/UI scopes, not capabilities.
Only composition issues bindings. Attach refuses while another binding is
current; a passive/prepared view receives no mutating role. Candidate adoption
uses its existing `PresentationViewBinding` commit to retire A and admit B in
one owner-lane transition. The update facade does not create a second competing
definition of which presentation is active.

Remove the constructor's borrowed `Action<Action> post`. Worker work queues
terminal results to a session-owned mailbox; the window owner drains it. Keep
one terminal completion and one coalesced latest progress value per operation,
with operation generation attached. A fast progress producer cannot build an
unbounded UI queue. Workers capture the update owner and immutable inputs, never
the Game, its rules session, a view binding's private callback, or a graphics type.

Progress-only publication preserves the existing semantic `AppUpdateView.Generation`.
Updating received/total bytes or the fallback-progress display within an unchanged
phase does not invalidate a still-current Cancel control or its published hit.
The coalesced slot carries the request generation, so late progress remains inert
after cancellation, replacement or close. A phase transition, release/prepared
identity change, command-availability change or role retirement does invalidate
the applicable control scope. In particular, a progress sample that changes
Downloading to Preparing is a phase publication, not merely another byte sample.
Role identity remains a separate required part of hit/command validity. Add both
unchanged-control and stale-phase/prepared/role negative controls; do not make
every progress report advance scope to simplify the queue implementation.

Replace retained `installBlocker` and `installed` Game delegates with a neutral
`AppUpdateInstallContext` supplied by the active window update and a composition-
owned successful-handoff action. The context carries the existing blocker text
and automatic-check eligibility; it grants no installation authority by itself.
The install delegate continues to be the existing verified restart handoff.
Success calls the same window's Exit action, not a retired Game's event.

### U1 pump and handoff order

`TableWindow.Update` performs the existing application update, reads that current
application's immutable install/automatic-check context, then pumps the retained
session exactly once with the current binding and context. The application stops
calling `AppUpdateSession.Tick` or draining update completions. Its update handlers
only submit through its binding. Headless drivers use the same order explicitly.
No other class pumps this owner. A window without a current view pumps with a
blocked/no-view context so cleanup and downloads can settle while install and
automatic discovery remain inadmissible.

This order matters: the application first admits its rules/lobby/save completions
and current input. A verification callback then sees the resulting blockers.
The owner never installs based on the previous frame's safe context. Moving the
pump after composition can expose an update status on the next frame. Old hits
must be rejected when the role, modal or semantic publication changed during
that interval; byte-only progress leaves otherwise unchanged hits usable.
This bounded ordering difference must be covered in the actual driver checks,
not hidden by pumping twice to make a fixture pass.

An explicit Install captures its current binding and prepared-release identity,
checks the supplied live context, and starts normal verification. At completion,
the owner rechecks the same prepared identity, request generation, current role,
and the fresh window-supplied blocker before invoking the installer. A blocked
handoff returns to Ready and still requires another explicit Install. If the
originating view retired during verification, retain the verified Ready result
but revoke the uncommitted restart intent; the new view must request Install.
Once an installer was actually launched, retirement cannot retract that effect.

Retiring a view does not cancel accepted discovery, download or preparation.
A newly attached view reads the same live operation and may cancel a download
using its fresh binding. It does not reconstruct a session from a checkpoint.
Changing role advances role identity, not the in-flight operation generation;
valid terminal download results still settle once in the original owner.

### U1 shutdown and failures

Window shutdown first closes admission and retires its binding, then requests
provider cancellation and joins the real task. It does not wait for a completion
requiring another Game update. Queued terminal/progress entries become inert and
are cleared after the task settles. `CloseAsync` is idempotent and concurrent
callers share one close task; a synchronous host adapter may join that task during
normal teardown. The UI no longer closes the workflow from `TableApplication.Dispose`.

Actual task settlement and mailbox settlement are separate facts. The provider
task settles only after its work and owned cleanup return; enqueueing a terminal
result does not apply it to the owner. Ordinary Pump applies that result once.
Close does not require its application: it fences admission/role first, cancels,
awaits the real task directly, and discards queued or late owner effects before
disposing session-owned cancellation/mailbox resources. A worker never waits for
a Game callback, owner-mailbox acknowledgement or phase publication before its
actual task can finish. A Closed view means command admission ended; completion
of the shared close task establishes that actual work has settled.

Owner completion application releases its operation bookkeeping in `finally`.
Provider, cancellation-notification and host callback exceptions cannot leave a
reservation that teardown must pump to release. Catch/record applicable handoff
errors at that boundary while preserving already known effects: if the installer
launched and the composition's exit notification throws, `InstallerLaunched`
remains true and no automatic second handoff is permitted. Close still joins
the real task and retires its owned state even if a previously queued completion
would throw. These controls must run with the Game already disposed and with no
further Game/session/window Pump calls.

An update provider must honor cancellation and finish its own package/patcher
cleanup. A provider that ignores cancellation retains ownership until it really
returns; U1 must not turn a timeout into permission to dispose active resources
or exit the CLR. Controlled tests hold and then release such a provider to prove
the close join. Transport/SDK liveness limits remain with their existing owner;
this refactor adds no separate abandon-and-restart policy. The existing indefinite
join when SDK/provider work truly never cooperates is a preserved limitation,
not a new bounded-shutdown guarantee. A held-then-released test establishes the
ownership/join behavior, not a bound for arbitrary noncooperative work.

Check/download/verification/preference/location failure keeps its existing
classification and safe phase. Old view commands fail admission without starting
a provider operation. Old operation results cannot clear a newer operation's
state. Closing during a provider error cannot raise a callback into retired Game.
The operation is independent of rules commands, match recovery and action scopes.

### U1 files and verification

| File area | Work |
| --- | --- |
| `Mana.Client/AppUpdateSession.cs` | Retained mailbox, current-view authority, explicit context, one pump and real-task close |
| `Mana.Client/AppUpdateViewBinding.cs` (new if clarity requires) | Small revocable facade; no second workflow |
| `Mana.Game/TableApplication.Updates.cs` | Borrow binding, expose immutable current install context, preserve existing modal/gesture behavior |
| `Mana.Game/TableApplication.cs` | Constructor/disposal plumbing only; no update task ownership |
| `Mana.Game/TableWindow.cs` | Construct/pump/close retained owner; narrow composition edits reconciled with artwork work |
| Existing update client/native regressions and `PresentationRun` | Move fixture ownership to driver, retain all original safety assertions |

An application constructed without an updater receives a disabled immutable
view/port, not a hidden Game-owned asynchronous session. Test factories that
enable updates own their retained session explicitly and close it after the
application, using the same pump/context order as the shipping window.

Run the existing full client suite and native update checks. Add held-operation
sequences to those owners: A starts download, A retires, B reads its progress,
late A commands do nothing, the original download settles once, B may install
only through fresh verified context; view ABA; no view; duplicate Pump protection
by the shipping call graph; cancellation followed by late progress; old completion
after a new operation; close while a provider temporarily ignores cancellation;
verification finishes while a match/save becomes active; retirement before and
after the actual restart callback. Include progress-only unchanged-hit validity,
phase/prepared/role stale-hit refusal, queued completion exceptions, cancellation
callback exceptions and service-disposal accounting. Include normal successful
current-view controls and prove session close finishes after provider release
without applying a mailbox completion or calling any Game pump.

Use the actual `TableWindow`/headless driver ordering to establish blockers and
scope rejection. Controlled providers establish lifecycle behavior, not a real
installer. Reuse the existing managed installed-update journey for actual restart
handoff and prepared-package continuity. New checks must enter existing CI lanes.
U1 acceptance requires no live-loader/Host dependency, no remaining Game/session
completion callback in the workflow, and unchanged download/restart consent.

## Isolated live-update slice U2

U2 executes on an isolated descendant of the existing candidate after U1 is
ported and reviewed there. Its boundary is one complete packaged A-to-B path:
the real Updates command, authenticated stored B, same-window B presentation,
unchanged running A match, a new table exercising B runtime behavior, and an
offline relaunch choosing B. These are one acceptance result. Do not relabel
component helper tests, a direct Host swap, or an expected failing checklist as
completion of this slice.

### Exact A/B artifacts and product behavior

The candidate's `Mana.Component.Tools`, `ci/component-build.props`, package
manifest, `Mana.LiveUpdate.Tests`, and `Mana.Runtime.Tests` supply the existing
build/check owners. Freeze the complete integration source and pinned toolchain
once, reuse exact host/shared bytes for A and B, and record all source/component
hashes. Build both actual production presentation/runtime entrypoints. Do not
make distinct packages by changing MVIDs, labels, version attributes or test plugins.

The already-authored actual UI behavior pair is the Auto ability list: A closes
after one preference change and B keeps the list open for a second fresh change.
The candidate's `ReleaseSwapJourney` exercises that real code pair, while its
current provider/host setup bypasses the full updater and does not prove U2.
Rebuild the pair from recorded source variants with only that approved product
change, then run it through the production update port and real match. The lead
must confirm that behavior is the selected reviewed change before freezing A/B.

The runtime behavior choice remains unresolved for implementation admission.
The published commander-defense correction in PR 135 is a concrete candidate:
record the A policy source before that correction and B after it, prove the
same offered real-engine lethal commander block fails A and succeeds B, and
verify those source variants compile against the same frozen shared contract.
Do not silently choose it, invent a new strategy, or count a changed diagnostic
reason as changed gameplay. If binary/source compatibility or a deterministic
real-engine witness fails, return to design review with the evidence. U2 cannot
pass until a specific behavior and implementable A/B recipe are accepted.

Fixture packages use a fixture-only bootstrap identity and pinned fixture key.
Release/platform metadata must satisfy the actual parser; do not create a fake
production channel to bypass it. Fixture feeds are not public updater releases.
No test-key path may be reachable from a production bootstrap.

### Repository and durable selection

Extend existing `VerifiedComponentGeneration`/checked package ownership with a
small repository/store owner. A store is keyed by package, channel, platform and
exact host closure, outside installation and gameplay profile. Its authenticated
release capability binds bounded exact descriptor bytes, ECDSA P-256/SHA-256
signature, monotonic sequence, source, manifest inventory, entry identities,
shared closure, ABIs and storage compatibility. Verify the pinned publisher key
before granting parsed metadata execution authority, then continue existing
actual-returned-byte/file checks. Strong names and self-provided hashes do not
replace publisher authentication.

Publish only a complete verified generation: stage to a fresh child, enforce
existing path/inventory/expanded-size limits, flush files, and same-volume rename
under a short mutation gate. Existing named directories are revalidated. No
published or installed generation is overwritten. Network/hash/Host work stays
outside the mutation gate. A real generation lease covers preparation, loading,
active presentation, selected runtime and provider children. Cleanup must prove
exclusive ownership against new acquisition before tombstoning/deleting history;
until the child/orphan witness exists, cleanup stays disabled rather than guessing.

The durable desired record contains schema, host/package identity, descriptor
hash, sequence, revision, desired and prior healthy identities, and launch-trial
state. It contains no UI-supplied executable path. Commit via expected-revision
compare-and-swap with a retained recoverable prior record and tested Windows
flush/replace ordering. A stale B cannot overwrite accepted C. A directory name,
JSON checksum or previous online check does not authorize execution after exit.

Before constructing Game/runtime, cold startup authenticates the selected signed
descriptor, verifies complete content and compatibility, acquires leases and
records a trial. Mark healthy only after usable presentation and required runtime
initialization. A pre-health failure falls back to compatible prior healthy code
or trusted bundled baseline with its cause retained. Post-health failures do not
automatically blame B. Offline relaunch needs no reauthentication network call.
No code recovery rolls back gameplay or replays a command.

### Retained operation, Host and runtime effects

Port U1's retained owner; expand the existing `WindowUpdateCoordinator` rather
than copying U1 tasks back into Game or introducing another coordinator. Stable
shared command/read values distinguish Check, Prepare, Apply, Cancel and
RetryMissingEffects, with current role/revision and exact offered identity.
The UI invokes neither loaders nor install callbacks for a minor update.

The owner serializes preparation and worker completion admission. Host pumps
its effects on the window lane. Capture host epoch plus view/runtime identities
and revisions, prepare verified runtime files without starting another profile
writer, wait for a supported boundary, freeze/drain the original role, prepare
passive B, and recheck the exact publication/viewer/decision/viewport before a
single synchronous transfer. Adopt B's prepared frame before retiring A callbacks.
Keep the current table's Client/connection/receipts/AI/JVM exactly owned by A.

Prepare B runtime before selecting it. Select it before admitting the first new
table after the current cohort is explicitly retired, not only after some later
return event. Keep an existing remote lobby's cohort and wire capabilities. U2
does not change cross-version multiplayer policy; M5 must qualify that separately.

Replace `TryRetainForNormalExit`, `RetainForNormalExitAsync`, `retainedForExit`,
and minor-path completion's installer coupling with independent observed effects:
presentation committed, runtime selected, durable desired revision committed,
and retirement outcome. A completed B presentation with a failed durable write
is session-only B, not rollback to A or full success. A fresh scoped operation
may retry missing effects after checking current bases; it cannot reapply an
already committed transfer or overwrite newer C. Minor shutdown launches no installer.

### Graphics and presentation compatibility

U1's mainline window wiring is separate from artwork's frame/resource pairing.
Reconcile that file sequentially; neither stream owns the other's lifetime logic.
For U2, port `DrawingFrame.TextureResources`, draw commands and device resource
contracts to their candidate location in exact shared `Mana.Graphics`. Keep
CardArt in stable Host and its device thread. Do not duplicate frame definitions
in private `Mana.Renderer` or leak private Game objects through pin leases.

`TableHostWindow` must retain an exact `RuntimeFrame` and resource lease pair
after Host update/adoption and paint that pair; its current read-at-Draw path is
insufficient for an independently retained frame. Acquire B pins before releasing
A; include passive/prepared/temporary capture frames, with the graphics design's
finite lease budget. Abort and retirement release pins on the device lane.
Before collectible-context retirement, clear private frame/callback references;
stable pins may retain only stable drawing/resource objects. Qualify A-to-B under
residency pressure, a held capture, and aborted passive preparation. No eviction
or unload step may dispose a texture still referenced by a permitted paint.

The separate presentation-owner extraction ports as private presentation state.
It must not move window devices, update operations, command lanes, or current
table policy into a replaceable view. Extraction alone does not change the frozen
shared ABI; port/review the resulting boundary before rebuilding A/B.

### U2 implementation and failure gates

| Order | Exact implementation area | Decisive witness |
| --- | --- | --- |
| 1 | Frozen real source variants; existing component producer/runner | Actual UI and runtime A/B behavior, exact stable closure, no version-only distinction |
| 2 | `Mana.Client/Updates` checked store/authentication/desired record; existing verifier reuse | Signed stored B reopens in a cold offline process; wrong-key/content and interrupted-write controls reject safely |
| 3 | U1 port, `WindowUpdateCoordinator`, shared update port, `Program` composition | Original actual update operation survives view replacement; old roles cannot issue commands or callbacks |
| 4 | `PresentationHost`, `RuntimeGenerationCoordinator`, startup selection | Same-window B, same A match/JVM/receipt lane; first fresh table runs B; truthful independent effects |
| 5 | Existing `Mana.LiveUpdate.Tests` and `Mana.Runtime.Tests` product runners | Drive actual update gesture, continue A, exercise B table, normal exit and offline relaunch B using the same artifact set |
| 6 | Failure controls in those same owners | Cancel/precommit failure, store failure after adoption, revision race, held receipt and frame retirement preserve their actual owners |

| Failure cut | Required outcome |
| --- | --- |
| Signature/content/staging failure | No candidate execution; A remains usable |
| Unsupported interaction boundary | A continues; defer this update attempt without a restart demand |
| Cancellation/exception before known transfer | Retire B and restore A input once; retain original admitted receipts |
| Exception with unknown transfer outcome | Do not resume both views; retain ownership/evidence and use explicit recovery |
| B active, runtime selection fails | Report only committed view effect; current A table remains untouched |
| B active/runtime selected, desired write fails | Report session-only effects; offline launch keeps the valid older durable choice |
| Desired record interrupted or B fails pre-health | Authenticated prior compatible record/baseline, no unchecked pointer execution |
| Another window accepts C | Late B cannot rewrite C or revoke another window's A lease |
| Parent dies with live JVM | Child profile/file claims still exclude unsafe writers/cleanup |
| Shutdown with download/patcher active | Join actual cleanup; no Game pump dependency or implicit minor installer |

Use existing case deadlines and record first failures. Do not extend timeouts,
retry an unchanged failed candidate, or inflate supporting assertion counts to
replace the product witness. Preparation latency, input responsiveness, actual
resource residency and complete game progression are separate measurements.

## Later milestones and public release

M2 completes ordinary Client isolation with an exercised real Client fix delivered
to the next table while A continues. Exact contracts, not display versions,
determine compatibility. A routine Client behavior change requiring restart fails
this gate. Preserve orphan-profile and provider-file lifetime evidence.

M3 provisions the production signer in a protected release environment, pins its
public key in an explicit bootstrap release, uses exact reviewed stable baseline
bytes, signs final gated inventory and verifies public readback. No production
private key, key-management account or release-environment approval is assumed
available. Those are explicit operator prerequisites. Fixture proof is not public
trust. Key rotation remains an explicit bootstrap/major update.

M4 adds each supported live interaction only with its preservation journey:
Auto, captured resolution, required choice, payment/target/combat drafts,
history, delayed receipts and mass triggers. Unsupported drafts defer; new local
controls/layout need a concrete game precedent and human review under the shared
rules. M5 qualifies repeated updates, multiple windows, crash cuts, resource
retirement, major-update regression, a host/friend/two-AI complete game and next
table. M6 requires exact-head draft PR checks, reviewed human journey, protected
merge, exact-master checks and managed/portable public artifact verification.

Mainline U1 and the designs can merge independently after their relevant checks.
The unfinished live candidate stays isolated until its accepted end-to-end scope
and dependency changes have been reviewed. The lead records source, artifacts,
first failures, accepted changes and remaining gates in the program handoff.

## Technical lead review resolution 16 November 2026

The technical lead accepted U1's direction and requested the following design
clarifications before execution. This revision records their disposition;
U1 is accepted as the first execution scope after the combined ownership review.
U2 production implementation retains its separate admission gates.

| Review request | Resolution in this revision |
| --- | --- |
| Byte progress must not make unchanged controls stale | Semantic Generation is unchanged for progress-only fields; phase, prepared identity and role changes still revoke the relevant scopes, with paired positive/negative input checks |
| Distinguish actual task completion from owner-result application | Close fences and joins the provider task directly, then discards mailbox effects; no Game or owner pump is needed to finish teardown |
| Callback failures must not strand teardown | Completion bookkeeping releases in finally; cancellation/host callback failures retain known effects and cannot create another required mailbox acknowledgement or installer retry |
| Preserve provider ownership and prevent double disposal | Program alone disposes the borrowed provider after window/session close; repeated session close shares its task and never disposes the service |
| Do not claim bounded shutdown for noncooperative SDK work | The existing indefinite actual-work join is explicit; held-then-released controls prove ownership without claiming a universal deadline |
| Keep U2 behavior selection and execution gated | The actual Auto-panel A/B pair is an accepted design input; runtime behavior/recipe remains unresolved for admission, and no U2 production implementation is authorized by this revision |

## Evidence and technical references

### U1 execution record 16 November 2026

The isolated U1 implementation starts from the program's accepted-design commit
`4e67e04da98b01a8e5973d46bb8d6c527d48a373`, whose production baseline remains
0.2.469. The technical lead reviewed the owner, binding, composition and held-
operation source and found no blocking issue before integration.

`AppUpdateSession` now owns one terminal-result slot and one coalesced progress
slot, plus a passive asynchronous queued-work notification. `AppUpdateViewBinding`
supplies revocable current-view authority; `AppUpdateInstallContext` supplies fresh
neutral blockers. `TableWindow` owns construction, one post-application pump and
actual-task-joining close. Game owns only its borrowed binding and local modal.
Program's provider ownership and the explicit restart path remain unchanged.
The candidate loader/store/signing implementation was not imported.

The baseline held-download experiment failed the retained-view contract: closing
the original Game-owned workflow requested cancellation and the replacement
workflow had no download state. That failure is retained separately from an
initial proof-fixture compilation failure (a missing ValidateAsync stub), which
was corrected before the behavior experiment. A later native test compilation
failure used a read-only scenario State initializer; the test was corrected to
the existing Present method. No failed behavior assertion was weakened.

| Local validation | Result and scope |
| --- | --- |
| `Mana.Client.Tests --app-update-checks` | 52 passed: ordinary workflow, progress/phase scopes, held result, view replacement/ABA, cancelled waiter isolation, fresh restart consent, pump-independent close, callback failure and borrowed-service disposal |
| Full `Mana.Client.Tests` | 5,580 passed on the U1 source |
| Native `--smoke-headless-presentation --presentation-group updates-owner` | 24 passed across 1000 by 740 and 1540 by 980: actual application input/composition, old hit retirement, progress-stable Cancel press/release and Game disposal with a retained operation |
| Native `--smoke-presentation` | 1,781 passed on the final production source |
| Full native `--smoke-headless-presentation` | 8,791 passed on the final production source |
| Offscreen native `--smoke-updates` | 31 passed at each supported size; final-source repetition is recorded in the integration handoff |
| Agent image inspection | Existing small download/progress and large Ready layouts remain readable, with the established Cancel and Restart to update controls |

These runs use controlled update providers. They establish ownership, input,
geometry and failure behavior, not a new real installed-SDK handoff, Magic rules
result, production publisher signature or human UX approval. Required exact-head
CI, the existing installed-update release lane, and integration verification still
apply. Truly noncooperative SDK work retains the documented indefinite join.
U2's runtime A/B behavior decision, production trust and complete live journey
remain unimplemented.

Extend existing suites: `Mana.Client.Tests` for role/operation/store policy,
`Mana.Table.Tests` for production input and Host authority, `Mana.Runtime.Tests`
for actual provider/profile/next-table lifetime, `Mana.LiveUpdate.Tests` for the
physical same-window product flow, and installed-update lifecycle runners for
the unchanged major/restart path. Existing CI lane names and checks stay required.
Record source/closure/descriptor hashes, fixture/production trust identity,
window/process/table/connection identity, commands/receipts, selection revision,
measured limits and first failure. Agent inspection is separate from human review.

- [Assembly load contexts](https://learn.microsoft.com/en-us/dotnet/core/dependency-loading/understanding-assemblyloadcontext) explain shared identity and per-context assembly versions; this design's allowlisted closure is a stricter application contract.
- [Collectible unloadability](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability) is cooperative and depends on stacks and retained references; a loader is not a preemptive cancellation or crash-containment mechanism.
- [Velopack integration](https://docs.velopack.io/integrating/overview) supplies the existing startup/apply integration; U2 retains its transport while reserving installer/apply behavior for explicit major updates.
- [Architecture](Mana-Table-Architecture.md), [testing](Mana-Table-Testing.md), and [release guidance](Mana-Table-Releases.md) remain the maintained integration and qualification entrypoints.
