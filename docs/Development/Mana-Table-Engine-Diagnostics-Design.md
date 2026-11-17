# Engine diagnostics without gameplay backpressure

Design accepted by the technical lead, 16 November 2026. Implementation and
integrated qualification are pending.
The baseline is `0eaa9f70abe3a40f92342352a20dc805a0ea730a`.

The rules process must remain able to write stderr when diagnostic storage is
unavailable or slow. Give each process a continuous, bounded stderr drain and
isolate optional file output behind one bounded queue. Logging never supplies
rules authority, changes a command receipt, or authorizes a retry.

## Observed problem and existing protections

[`ForgeEngine.InitializeAsync`](../../mana-native/Mana.Forge/ForgeEngine.cs)
currently gives `EngineProcess.Diagnostics` one task which opens `engine.log`,
reads stderr with `ReadLineAsync`, writes each line, and flushes after every line.
Sink construction, writing, flushing, and disposal can throw. Construction and
each flush can also block independently of the rules protocol.

The concrete failure chain is:

1. The log cannot open, or a later write/flush fails or stops completing.
2. That task exits or waits before its next stderr read.
3. The child continues writing stderr until its redirected pipe fills.
4. The child can block in a diagnostic write and stop answering rules requests.

An arbitrarily long unterminated line also makes `ReadLineAsync` an unbounded
allocation route. An asynchronous task alone does not isolate a slow sink.
These are source-established failure paths; no reported player's freeze has been
attributed to them, and pipe saturation has not yet been reproduced for this
candidate.

Existing boundaries should remain intact:

- The stdout reader is separate and owns protocol decoding and receipts.
- Each process retains its own readiness signal, command scope, and reader tasks.
- `Fail(owner, error)` rejects pending requests only when that owner is current.
- Request readiness, write gate, write, flush, and reply share one 45-second
  transport deadline. Uncertain execution is not replayed.
- Process shutdown first closes input, waits three seconds, then kills and waits
  up to three more seconds when necessary. `Stopped` makes this path idempotent.
- A startup exception is preserved even when cleanup also fails.

The current stderr task has no recovery catch, no queue or retained-byte bound,
and no sink-time bound. `StopProcessAsync` awaits it after the process exits, so a
blocked sink can also hold shutdown indefinitely.

## Scope and invariants

This change is internal provider resilience. The player uses the existing game,
startup, and exit paths; it adds no controls, game rules, messages, or new wire
fields. Existing diagnostic files remain private support evidence and may contain
hidden card information.

1. A file operation never runs on or is awaited by the stderr drain.
2. Stderr is consumed in fixed-size character chunks, including without newlines.
3. Queued output and private in-memory tail have fixed capacities. Overflow loses
   diagnostic output explicitly, without blocking the child or changing play.
4. Exactly one worker owns sink construction, writes, flushes, and disposal. This
   includes synchronous work performed before an asynchronous method returns.
5. Sink failure disables that sink for its process. It never calls `Fail`, changes
   readiness, mutates command scopes, or completes a pending protocol request.
6. An old process's task, failure, or late sink completion can only update that
   diagnostic owner. It cannot retire or replace a newer process.
7. Diagnostic retirement has one 500 ms budget after the existing process-stop
   path. It does not create separate per-stage waits or extend command deadlines.
8. A noncooperative sink can leave at most one abandoned file worker per
   `ForgeEngine` instance. Subsequent owners still drain stderr but suppress file
   workers for the remainder of that engine instance's lifetime.

No exception-swallowing change may hide actual protocol failure or uncertain
execution. The stdout and command-write paths keep their existing behavior.

## Ownership and proposed code surface

Add a provider-internal `EngineDiagnostics` owner in
`Mana.Forge/EngineDiagnostics.cs`; `EngineProcess` owns one instance. A small
engine-instance logging gate remembers only whether a writer was abandoned.
There is no process-global/static gate, cross-profile singleton, logging
framework, or new project.

| Owner | Owns | Cannot do |
| --- | --- | --- |
| `EngineProcess` | Stderr reader, process identity and its diagnostic owner | Transfer its diagnostic callbacks to another process |
| `EngineDiagnostics` drain | Fixed read buffer, tail, bounded pending ring, counters and completion | Open or await the file sink; interpret output as protocol |
| File worker | One sink from construction through disposal, one write buffer | Read process pipes or call provider failure/recovery code |
| `ForgeEngine` logging gate | A monotonic `fileLoggingAbandoned` flag for this instance | Reset itself from a late task, stop a process, or disable another engine instance |
| Internal evidence reader | Immutable numeric/status snapshot | Retrieve raw private tail through a game snapshot or use status as an action handle |

Preserve the public `ForgeEngine(ForgePaths, TimeProvider?)` construction path.
Add an internal construction seam for a diagnostic sink factory and retirement
clock, exposed to `Mana.Table.Tests` through `InternalsVisibleTo`. Production
composition uses the ordinary file sink. Tests inject sink behavior only;
the production process, stderr drain, stdout codec, and request handling remain
the subject under test.

Keep `EngineDiagnosticStatus`, the capture method, sink injection and clock
injection internal. No existing host-support consumer requires a new public API;
friend access for `Mana.Table.Tests` is sufficient for this slice. A future
support consumer must be wired in the same change that exposes a supported
surface, rather than adding an unused public property now.

The bounded immutable status reports process identity, read/written/
dropped character counts, queue high water, sink stage and closed failure category,
EOF/read-failure status, logging suppression, and retirement abandonment. It
contains no stderr text, request arguments, exception stack, or card identity.
The private tail has a separate internal capture method for focused checks;
it is not added to `ICardEngine`, match contracts, or public multiplayer state.

Status capture never waits for I/O and copies only scalar values under a short
lock. At retirement, freeze one terminal scalar status, replacing any earlier
retired scalar record. That record retains no owner, task, exception, reader,
queue, raw tail or callback. Clear the retired drain's tail after its final
capture; do not store a tail in the engine-instance status history.

After disposal, status reads return the frozen terminal record and private tail
reads return no text. They do not restart work, wait for a worker, reopen a sink,
or refresh from a late completion. Replacing the current process cannot erase
the engine-instance abandonment fact. A captured A status remains A evidence
after B becomes current and never becomes an alias for B's status.

The sink worker captures a writer-only state: its pending ring, write buffer,
cancellation state and bounded sink-stage evidence. It does not capture
`ForgeEngine`, `EngineProcess`, the pipe reader, the drain task, or the private
tail. After an abandonment there is at most one such retained writer state per
engine instance, plus the current drain owner and one frozen retired scalar
record. Suppressed replacement owners create no sink worker or pending file
ring; normally retired owners are released rather than accumulated in a list.
The conservative character-payload ceiling for current plus abandoned diagnostic
state is two ordinary owner allocations, 272 KiB, plus stream/task/object
overhead; the separated/suppressed shapes use less. This bound concerns retention
caused by file logging. The real child-pipe exit/handle-close tests must also prove
that the supported framework reader retires; an indefinitely retained pipe read
is a failed acceptance gate, not an assumed reclaimed resource.

## Continuous drain and bounded storage

Use `Process.StandardError.ReadAsync(Memory<char>, cancellation)` with a
2,048-character buffer. Set stderr encoding consistently with the Java UTF-8
launch contract. Do not build lines or concatenate all output before admission.

For each nonempty read:

1. Append characters to a 32,768-character circular private tail.
2. Under the same short diagnostic lock, assign a monotonic chunk sequence and
   copy the read into a fixed 16-slot ring of 2,048-character slots.
3. If the ring is full, overwrite its oldest queued chunk and increment dropped
   chunk/character counters. Never wait for a consumer.
4. Wake the single file worker with a coalescing work signal and continue reading.

The worker copies one queued chunk into its own fixed buffer under the lock,
then performs all sink operations after releasing that lock. A producer never
waits on a lock held across sink work. Ring storage, read buffer, write buffer,
and tail retain 69,632 characters, or 136 KiB of character payload per owner.
Task, object and ordinary stream-reader overhead are additional; a memory test
asserts bounded retained payload and queue occupancy, rather than a brittle exact
whole-process allocation count.

On ring overflow, the writer emits a bounded omission marker before the next
available chunk. Markers describe omitted diagnostic characters and never claim
that gameplay events were lost. Output can start mid-line after an omission.
Character decoding stays in `StreamReader`, so an input chunk boundary does not
split a decoded UTF-8 sequence into invalid bytes. An oversized line is simply
several bounded chunks.

Queue slots are allocated once. After a sink is disabled or file logging is
suppressed, the drain updates only its tail and counters and does not keep
offering output to an unused queue. The first sink failure is represented by a
closed stage/category; any private exception summary is capped at 256 characters.
There is no per-read exception accumulation or retry loop.

## File worker and failure behavior

Start the complete sink lifecycle inside one `Task.Run` worker. Merely wrapping
`WriteAsync` is insufficient because opening a file, a custom sink method, or
disposal can block synchronously. Do not create a new task for each chunk.

Keep `engine.log` and append behavior for compatibility. Open with read sharing
but no second writer sharing. The sink writes bounded chunks, flushes after a
drained batch, and flushes on ordinary EOF/retirement. It does not flush for every
small stderr line. Each process session writes a bounded process-identifying
header before its output.

| Failure | Required behavior |
| --- | --- |
| Open throws | Record sink-open failure, disable file output, continue draining and retaining tail |
| Write or flush throws | Record the original sink stage, disable file output, discard pending file queue, continue draining |
| Dispose throws | Record cleanup failure without replacing an earlier sink failure or a rules error |
| Open/write/flush/dispose never completes | Drain continues independently; retirement stops waiting at its one budget and suppresses future file workers for this engine instance |
| Queue fills behind a slow sink | Overwrite oldest queued chunks, account for loss, preserve recent private tail, and continue protocol work |
| Stderr reaches EOF | Complete the pending queue and let the worker drain/flush within retirement budget |
| Stderr read fails | Retain a closed read-failure status and end that drain; do not convert stderr I/O into a rules receipt or game result |

After any sink failure, cleanup also stays on that same worker. A blocked dispose
must not migrate back into process shutdown. Worker faults are observed at their
owner boundary even when retirement has stopped awaiting completion.

Log rotation is outside the first slice. It is not needed to remove sink
backpressure, and implementing a disk-retention policy would broaden the change.
The append file can still grow during long healthy sessions; that is a separate
support-storage decision. The drain remains safe if that growth eventually
causes a storage failure. Tests use bounded authored output and isolated profiles.

## Startup, exit and replacement

Start the drain immediately after creating `EngineProcess` and before awaiting
the stdout ready event. Starting or opening a file sink never delays readiness.
If the engine-instance logging gate is already suppressed, create only the drain
and fixed private tail; mark the new owner's file status as suppressed.

Run diagnostic retirement in the existing process-stop path's `finally`, including
when waiting for or killing the process fails; preserve the original process
exception. Retire diagnostics using one 500 ms budget covering drain completion,
queue completion, sink flush and disposal. Prefer stderr EOF so final crash output can reach the
tail and queue. On expiry, cancel remaining diagnostic work, discard pending
queued file output, publish abandonment status, and return without awaiting a
noncooperative sink. The worker remains the sole sink owner until its own eventual
completion; the shutdown caller does not race a second dispose against it.

The 500 ms allowance is a shutdown grace for optional support output, not a game
latency budget. It allows normal local buffered output to settle while adding at
most half a second after the existing three-plus-three-second process-stop
bounds. Capture the retirement start/deadline once. Every awaited drain/sink phase
uses its remaining allowance from that same deadline; switching from pipe EOF to
file flushing does not allocate another timer or reset the allowance. Use a
deterministic retirement clock in lifecycle tests and a generous outer test
watchdog only to detect a hung test. Do not stack a new 500 ms wait for each phase.

`EngineDiagnosticsChecks.SharedRetirementBudgetCoversDrainAndSink` is the decisive
clock/gate control. Start retirement with the drain and sink gates both held,
advance 300 virtual milliseconds, then let the controlled reader return EOF.
Leave the sink held and ignoring cancellation. Retirement must remain pending
at 499 total virtual milliseconds and finish at 500, with one deadline retired
and no residual timer. A per-stage implementation would still wait until 800 ms
and must fail this check. Run a paired ordinary control which releases both
gates before the deadline and observes final output, successful cleanup and no
abandonment. The controlled reader validates the retirement budget; the separate
real child/pipe controls validate actual stream completion.

A synchronous blocked in-process call cannot be forcibly unwound safely by
cancellation. The bounded guarantee is that the provider stops waiting and does
not start more file workers after one abandonment. That worker may retain its
one bounded writer state and sink until the OS/callee returns or the application exits.
Its late completion cannot clear suppression or update a replacement owner's
status. With exclusive writer sharing, a lingering old file handle also cannot
interleave another owner's log writes. Pipe draining and stdout protocol remain
available when new file logging is suppressed.

## Implementation ownership

| File | Change |
| --- | --- |
| `Mana.Forge/EngineDiagnostics.cs` | Fixed drain/tail/queue, single sink worker, status and bounded retirement |
| `Mana.Forge/EngineDiagnosticSink.cs` | Small internal sink contract, ordinary append sink and factory seam; may stay in the diagnostics file if clearer |
| `Mana.Forge/AssemblyInfo.cs` | Friend access for the existing native test assembly only |
| `Mana.Forge/ForgeEngine.cs` | Process-owned construction, internal scalar evidence, replacement suppression and teardown integration |
| `Mana.Table.Tests/EngineDiagnosticsChecks.cs` | Controlled sink, oversized-line, bounded storage and retirement checks |
| `Mana.Table.Tests/ForgeDiagnosticsProtocolProbe.cs` | Actual child stderr saturation, stdout replies, replacement and normal exit witnesses |
| `Mana.Table.Tests/Program.cs` | Recognize the child probe marker before graphics initialization |
| `Mana.Table.Tests/HeadlessPresentationSmoke.cs` | Run new checks in the existing configured-simulation and full headless inventory |
| Architecture/testing guides | Describe diagnostic ownership, limits, command and evidence scope; coordinated by tech lead if other workstreams edit them |

No Java, rules, UI, packaging, profile schema or multiplayer protocol change is
required. Reconcile the small `ForgeEngine` integration when the separate
live-update branch is later merged; do not import its unfinished lifecycle
architecture as a dependency.

## Verification and completion gates

Tests assert externally observable drain progress and child request replies;
private queue counters support bounds but cannot replace the child-pipe witness.
Use real failure controls rather than creating a test which merely inspects
the chosen implementation's fields.

| Case | Witness and invariant |
| --- | --- |
| Denied sink open | A real file-open failure plus a controlled throwing factory leaves child ready/replies and stderr tail progress usable |
| Write/flush/dispose failure | Separate controlled sinks fail at each stage after known accepted output; all later child replies still arrive, and shutdown preserves any original protocol error |
| Synchronous blocked open | A gated factory blocks before returning its sink; a child writes enough stderr to exceed pipe capacity and still returns a normal stdout reply |
| Slow/noncooperative write | One controlled write ignores cancellation while the child emits at least 2 MiB of stderr; the child completes output and replies, bounded queue loss is explicit, and recent tail reaches the terminal sentinel |
| Unterminated output | At least 2 MiB without a newline is drained with bounded payload, followed by a valid stdout reply; no `ReadLine`-sized allocation is possible |
| Recovered slow worker | Release the gate while the process is still active; remaining output is written in sequence, an omission marker accounts for overflow, and normal exit completes |
| Noncooperative shutdown | Advance the single retirement budget while each sink stage is separately held; provider disposal returns without waiting for sink completion or changing command uncertainty |
| Shared drain/sink deadline | `SharedRetirementBudgetCoversDrainAndSink` spends 300 virtual ms before EOF, retains the blocked sink through 499 ms and retires at 500 total; the paired ordinary control completes before expiry |
| Replacement isolation | Abandon A's sink, initialize B on the same `ForgeEngine`, observe B ready and usable, then release/fail A; B's process, status, command scope and replies remain current, and B opens no file worker |
| Retired/disposed evidence | Repeated status/tail reads after disposal are frozen/empty and inert; A's late sink success/failure cannot alter A's frozen record or B's current evidence, and repeated replacement retains no owner/status history list |
| Instance isolation | A second engine instance with a separate profile still logs normally after the first instance suppresses logging |
| Ordinary exit and startup failure | A clean child drains final text; a failed child preserves its original startup error even when sink cleanup throws |

A denied-open real-filesystem control may use a directory at `engine.log` inside
the disposable profile. Controlled sinks provide repeatable disk-full/write/
flush/dispose behavior without filling a workstation's disk. The child probe
uses the existing self-hosted native test executable mechanism; it supplies
authored protocol replies, not a simulated Magic rules engine. Keep its output
finite, give every gate an outer watchdog, and release gates in test cleanup.

Run the focused configured-simulation group, then the existing full headless
presentation lane and normal required checks for the exact final commit:

```powershell
dotnet run --project mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release -- --smoke-headless-presentation --presentation-group configured-simulation --profile "$env:TEMP/ManaTableDiagnostics-$([Guid]::NewGuid())"
node scripts/mana-table/check-docs.cjs
```

CI already runs the full headless inventory in the Windows native verification
job. Wire new checks into that path; no new lane or relaxed timeout is needed.
Retain the first failing baseline witness and the exact candidate's result.

Implementation proceeds in three reviewable steps: fixed bounded drain/worker and
controlled sink checks; real child-pipe and replacement witnesses; then provider
integration, full inventory and documentation. Completion requires all listed
invariants, failure stages and ordinary controls to pass. A unit-tested queue
alone cannot complete this design. Human UX review is not applicable to the
unchanged player interaction; complete-game reliability and the separate AI
qualification workstream remain independent evidence.

## Tech-lead review resolution

16 November 2026: the tech lead accepted the ownership design subject to these
revisions, now incorporated for cross-stream review:

- All status, sink-injection and retirement-clock surfaces remain internal;
  there is no unused public host-support API.
- Retirement freezes one text-free scalar record and releases raw tail data.
  A blocked file worker retains only bounded writer state. Instance-scoped
  suppression prevents abandoned workers accumulating across replacements;
  current drain plus one abandoned writer plus one scalar record is the maximum
  logging-induced owner set.
- `SharedRetirementBudgetCoversDrainAndSink` names the 300/499/500 ms deterministic
  gate sequence which rejects per-stage timeout resets. Actual child-pipe
  retirement is separately witnessed before claiming a retained-owner bound.
- Disposed and late status reads are inert evidence. A retired worker cannot
  update frozen A evidence, B's current evidence, protocol state or receipts.

The technical lead accepted this revised execution scope after source review.
No production or test implementation has been made by this design revision.
