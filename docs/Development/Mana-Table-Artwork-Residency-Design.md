# Artwork residency and frame lifetime design

Status: design accepted and mainline implementation locally validated for
integration, 16 November 2026. This design starts from
`0eaa9f70abe3a40f92342352a20dc805a0ea730a`; the execution record below identifies
completed controls and remaining Host, telemetry and release gates.
It covers window-owned card artwork, including its upload queue and
bookkeeping; it does not change game rules, card disclosure, gestures or layout.

The intended result is bounded artwork memory during prolonged deck browsing
and successive games, while every published frame keeps valid textures through
painting. Existing readable card fallbacks remain available when an image cannot
be admitted. No currently displayed image is repeatedly evicted and reloaded to
make room for another currently displayed image.

## Observed behavior and limits of the finding

`Mana.Game/CardArt.cs` bounds current visible requests at 512, anticipated
requests at 160, each accepted encoded image at 4 MiB, and the pending upload
queue at sixteen items. Its byte check occurs before a load: the current 8 MiB
queue threshold can be exceeded by one accepted image. These are encoded-byte
and scheduling bounds, not GPU residency bounds.

Every successful upload enters `textures`; nothing removes a texture until
`CardArt.Dispose`. The `uploaded`, `warmed`, and `retryAfter` collections likewise
retain historical keys for the window lifetime. `TableWindow` owns one CardArt
through deck browsing, matches and returns to the lobby. There is therefore no
code bound on accumulated GPU textures or historical metadata. No measured
out-of-memory failure or session-duration threshold is claimed by this audit.

`TextureFiltering.Load` first creates an original texture, then copies it into a
`SurfaceFormat.Color` mipmapped render target. The destination, every mip level,
and the temporary original must be considered separately. A 4 MiB compressed
file does not establish its pixel dimensions or decoded allocation.

The follow-up source audit also found that MagicArtSource used unbounded
GetByteArrayAsync and ReadAllBytesAsync, and LocalArtSource used unbounded
ReadAllBytesAsync. Rejecting an oversized byte array after those calls cannot
bound their source allocation. The accepted execution extension therefore adds
one shared 4 MiB contract constant and small private streamed readers in those
two existing owners, without a new project or download framework. Remote declared
lengths and open-file lengths are checked first; streamed or growing contents
are still limited to the cap plus a one-byte over-limit witness. Failed,
cancelled or incomplete network bodies never publish cache entries. The existing
atomic temporary-file publication remains intact.

ResponseHeadersRead requires an explicit total twelve-second network deadline
across headers and all body reads. It must not restart when headers finish or a
read begins. An optional injected TimeProvider supplies deterministic deadline
tests; this changes no game or action protocol. Existing request serialization,
150 ms remote pacing and caller cancellation remain in force.

`DrawingFrame` commands contain raw texture references. `TableWindow.Update`
uploads artwork before composing the next application frame; `Draw` paints the
latest completed frame. A visible-request list alone cannot prove that an older
published frame no longer needs its textures. This is the decisive lifetime
constraint for eviction.

## Scope and acceptance contract

1. The logical GPU payload of resident card artwork never exceeds its configured
   byte or texture-count limit. Uploads reserve temporary source and destination
   payload before allocation. Invalid dimensions cannot trigger a speculative
   large GPU decode.
2. A resident texture referenced by any live frame lease cannot be disposed.
   Leases are acquired when a frame becomes paintable, before its predecessor
   retires. No eviction occurs during composition or an open drawing batch.
3. Current demand larger than capacity uses a stable admitted subset plus the
   existing fallback. It never creates an unbounded all-visible exception.
4. Eviction removes the upload/resident marker that would otherwise suppress a
   later reload. Warm-on-disk knowledge never means resident-on-GPU knowledge.
5. Request, retry, warm-cache and lease bookkeeping have explicit bounds.
6. Only the device owner creates or disposes GPU resources. Workers load bytes
   and publish bounded results. No artwork completion can change action scope,
   disclose a revoked card or satisfy readiness before successful GPU admission.
7. Existing inspection, alternate-face, exact-printing and current-seat privacy
   behavior remains unchanged. In ordinary states that fit the budget, artwork
   remains visually continuous.

Logical payload accounting excludes driver allocation granularity, driver
caches, the window framebuffer, typography, arena assets and process RSS. Those
remain measured soak metrics. This design promises an auditable payload bound,
not a universal bound on total graphics-driver memory.

## Ownership and data

Keep one window-owned CardArt; do not introduce a process-global cache or another
art source. Add a small internal `ArtworkResidency` policy with no graphics-device
calls. It owns byte/count accounting, LRU order, pin counts and admission tickets.
CardArt remains the sole owner of concrete textures and the existing worker.

| Value | Owner | Lifetime |
| --- | --- | --- |
| Artwork request identity | Current source and ArtRequest.Key | Exact printing and disclosed face; never an occurrence or action handle |
| Demand sets | CardArt | Current visible/anticipated/foreground request generations |
| Resident record | CardArt plus residency policy | Successful committed upload through eviction/disposal |
| Admission ticket | Device-thread upload attempt | Reservation through commit or failure; exactly one terminal effect |
| Frame lease | Window or explicit temporary painter | Frame publication through final permitted paint |
| Encoded completion | Byte worker then device-thread consumer | One bounded queued result; never evidence of GPU readiness |
| Warm/retry history | Bounded CardArt metadata | Recent keys only, independent of disk-cache contents |

A resident record has key, Texture2D, accounted bytes, monotonically increasing
last-use ordinal and pin count. A reverse texture-to-key map lets frame leases
pin only resources owned by this cache; arena and other device assets retain
their existing owners. Use ordinal keys and deterministic key order to break
LRU ties. Use checked long arithmetic for all byte calculations.

The existing independent `uploaded` set should become derived state: a key is
resident or has one queued/in-flight upload. It must not remain a historical
"uploaded at some point" fact. Keep disk-warm and negative-retry knowledge in a
bounded metadata store, or integrate those fields into a bounded entry table.
There is no second persistent cache schema.

## Budgets and image admission

Initial defaults are engineering starting limits, not measured optimal values:

| Limit | Proposed default | Rationale |
| --- | --- | --- |
| Resident logical GPU payload | 224 MiB | Finite consumer-PC allocation; accommodates roughly 130 normal 488 by 680 RGBA mip chains |
| Eviction low-water mark | 192 MiB | Hysteresis avoids a full eviction scan for every newly admitted face |
| Accounted upload peak | 256 MiB | Includes resident payload plus temporary source and destination of one serialized upload |
| Resident texture count | 1,024 | Bounds objects and metadata even for tiny test or local images |
| Accepted encoded image | 4 MiB | Preserve the existing per-image envelope |
| Queued encoded payload | 8 MiB | Strict queue accounting; an additional in-flight source result is at most 4 MiB |
| Queued uploads | 16 | Preserve existing backpressure |
| Decoded dimensions | Each side at most 4,096; at most 4,194,304 pixels | At most 16 MiB for the temporary RGBA source before the mip destination |
| Historical metadata | 4,096 entries | More than all bounded active requests/residents, with finite recent history |

Expose limits through an internal immutable constructor option for focused
checks. Do not add player settings in this slice. The defaults must remain named
constants with evidence recorded when tuning them.

The exact input inventory in the reviewed source is:

- `MagicArtSource` requests Scryfall `format=image&version=normal` and caches the
  returned bytes using a `.jpg` name. The implementation expects normal JPEG
  artwork but does not validate the returned format. Cache-file extensions are
  not content validation.
- `CardReadingArtFixture` emits a static 488 by 680 PNG with alpha; it may return
  cached JPEG bytes instead. Its PNG can therefore pass through a `.jpg` fixture
  cache path. Preflight must recognize bytes, not the extension.
- `LocalArtSource` passes manifest-selected bytes through without a format
  allowlist. Its present acceptance is whatever the MonoGame decoder supports;
  the project has no narrower local-format guarantee to preserve silently.
- The bounded contract introduced here accepts static PNG and single-frame
  baseline/progressive JPEG for both remote/cache and local inputs. GIF, BMP,
  WebP, TIFF, TGA, PSD, HDR, animated PNG, JPEG variants with deferred dimensions,
  and unknown inputs use the existing readable fallback. This local-art
  narrowing must be stated in the native development guide. No maintained
  artwork fixture found in this source requires those formats.

Before `Texture2D.FromStream`, validate dimensions from bounded encoded bytes.
A bounded PNG IHDR/JPEG SOF reader inspects metadata but does not replace the
decoder. For PNG, require one first IHDR of length thirteen, legal bounded
dimensions, well-bounded chunk lengths and a terminating IEND; reject duplicate
IHDR, animation control, conflicting metadata or unexpected trailing image data.
For JPEG, parse bounded marker segments and entropy escaping through EOI; accept
one SOF0 or SOF2 with explicit nonzero dimensions. Reject another frame header,
DNL/deferred height, unsupported SOF variants, conflicting dimensions, truncated
segments, or appended image data. JPEG thumbnails inside an explicitly bounded
APP segment are metadata, not another top-level frame. Never scan arbitrary
payload bytes for a convenient first dimension marker and ignore later headers.
The actual decoder dimensions must still match this descriptor. Unsupported,
ambiguous or excessive input is rejected before GPU allocation, without an
unrestricted decoder fallback.

For a level with dimensions w,h, the Color payload is checked(w * h * 4). Sum
all levels, halving each dimension with a minimum of one until both reach one.
Use the actual returned Width, Height, Format and LevelCount to validate the
committed destination footprint. Mismatched decoder dimensions or an unexpected
format dispose the new resource and abort its reservation. Source bytes may be
rejected independently of whether the file is present in the disk cache.

Admission reserves both the predicted destination and temporary original before
decode. Evict eligible unpinned entries as needed. Refuse admission if the final
resident limit, count limit or temporary peak still cannot be honored. The
original is disposed by TextureFiltering's existing scoped lifetime before the
reservation commits. Any exception releases the reservation and owned temporary
resources exactly once.

A driver can allocate internal decode buffers and alignment beyond these logical
payloads. The soak records observed working set and GPU memory separately; it
must not label the calculated payload as measured physical VRAM.

## Frame publication and retirement

Add a read-only, deduplicated `DrawingFrame.TextureResources` inventory, computed
from the detached draw commands. The five current texture-bearing variants are
Image, Sprite, Surface, Portrait and ProjectedEllipse. Keep discovery centralized
and cover every texture-bearing command in the renderer checks. The inventory
contains references, not disposal authority.

Add `CardArt.PinFrame(DrawingFrame)`, returning an idempotent device-thread lease.
It pins owned resident entries for the inventory. Disposed owned textures are a
contract failure; foreign assets are ignored. A caller retaining a frame for
later painting must acquire its lease at retention time, not attempt to revive
an already retired frame afterward.

The TableWindow sequence is:

1. At Update start, the preceding published frame's lease is still held. Upload
   at most the existing two images, evicting only unpinned entries.
2. Compose the application. Get returns resident handles and records use, or
   queues demand and returns null. Get never evicts or disposes a texture.
3. Acquire the new completed frame's lease, then swap the window's frame/lease
   pair and release the preceding lease. No trimming occurs inside this swap.
4. Draw that exact pair. Every SpriteBatch using its textures completes before
   the lease can retire. Temporary probe/capture painting obtains a scoped lease
   too. Multiple Updates without Draw simply replace unused completed pairs.
5. The next Update may reclaim newly unpinned history. Window disposal retires
   all owned leases before disposing CardArt and the graphics device.

Do not hold frames forever to preserve artwork. The shipping window has one
published lease; temporary captures have scoped additional leases. A separate
quota permits at most four outstanding optional capture leases. Required frame
publication does not consume that optional quota. Fixed owner slots bound the
mandatory leases: one published frame, one prepared candidate and one retained
checkpoint where supported, plus the transient successor acquired during a swap.
Owners replace their slot rather than append a frame history. On public master
only the published slot is needed. Headless frames with no artwork need no GPU
lease.

Use `TryPinCapture` for optional painting. Quota exhaustion returns a structured
capture refusal and no lease; it must not throw through live Update or Draw.
The window/probe capture coordinator catches the refusal, completes that capture
request as unavailable, records bounded diagnostic evidence and continues painting
the retained live frame. A test runner may report the missing artifact as a test
failure after this safe handoff. A leaked optional capture can retain a bounded
set of bytes and deny further captures, but cannot block the next mandatory
frame publication or terminate a game. Runtime assertions bind device ownership
on the first GPU Update so existing graphics-free scheduling checks can construct
and drive CardArt without a graphics thread.

CPU frame/batch retirement is explicit. GPU destruction uses MonoGame's supported
device-thread disposal path after batches are flushed; no per-frame GPU idle
wait is introduced. The rendered lifetime regression is required on DesktopGL.

## Eviction, pressure and scheduling

Eviction order is least recently used among unpinned residents. Get hits refresh
last use. Pending encoded items do not confer residency. Successful admission
under pressure evicts toward the low-water mark when possible, then admits the
candidate; pinned entries may prevent reaching that mark but never permit
exceeding the hard cap. Neither a preload list nor an enlarged-reader name grants
an override of the byte cap.

When the current drawing demand is larger than capacity, preserve already
admitted frame-pinned faces and leave additional faces on the established
readable fallback. Do not rotate visible faces through the cache each frame.
This prioritizes visual stability over pretending every requested image can fit.
A newly requested reader can temporarily use its complete textual fallback when
all capacity belongs to pinned frames. Whether foreground reservation or lower
resolution is desirable requires a separate measured product decision; neither
is silently added in this resource-lifetime fix.

Record a pressure-denied key against a capacity generation. Advance that
generation only when the effective admission opportunity changes: resident
bytes/count change, a key crosses pin count zero to one or one to zero (changing
the eligible victim set), a limit changes, or that request's validated footprint
changes. Ordinary LRU touches, frame numbers, observation revisions, new arrays
with equal demand, and lease-count changes entirely above zero do not advance it.
Acquiring B before releasing A for an identical texture set produces pin counts
one to two to one and therefore leaves capacity generation unchanged. That
equal-frame swap cannot reload denied bytes on every Update.

On a genuine generation change, re-evaluate feasibility from the descriptor
before asking the byte source again; unchanged all-pinned capacity can remain
denied without a disk read. A request with a changed footprint has a new admission
attempt, not permission to exceed capacity. Do not convert pressure into the
existing 30-second source-failure backoff. A demand that retires can be forgotten.

If queued bytes cannot fit the queue cap, keep at most the one already bounded
in-flight result until capacity is available; its size remains separately
accounted. No second source request starts behind that retained completion.
Discard obsolete queued results before decode when neither current demand nor
a retained frame needs the key. Foreground/newly read requests retain the current
priority over speculative warming. Freeing a queue slot wakes the worker through
the existing owner, without starting duplicate workers.

Readiness counts only admitted resident images as Ready. A current image denied
by a hard resource limit is Unavailable for the current capacity generation and
uses its readable fallback, so a crowded opening cannot wait indefinitely for
an impossible all-resident state. Source failure, pressure denial, queued bytes
and actual residency remain separate internal facts. A later successful admission
can make the same current face Ready. Existing opening-budget and disclosure
rules remain unchanged.

Eviction clears resident/uploaded membership atomically. A later authorized Get
can reload from the existing disk source. Pruning warm metadata may cause a
future disk read; it must not delete disk files or cause a new network request
when the source already has the file. Retain active retry entries; prune inactive
history deterministically. Current demand, residents, queued work, in-flight
work and frame pins are never removed merely to satisfy a historical metadata
LRU limit. Their independent bounds must fit within the configured entry bound.

## Failure and privacy behavior

A malformed or oversized image receives the existing readable fallback and a
bounded negative-cache entry. A GPU failure aborts its admission, disposes any
newly created target, and preserves the last valid published frame. Keep the
original diagnostic cause in bounded developer evidence; do not repeatedly retry
a device failure from every paint call. Device-loss policy outside artwork is
unchanged and must not be disguised as corrupt artwork.

All texture maps and policy transitions synchronize with readiness/worker state,
but only the bound device thread touches Texture2D creation/disposal. A late
worker result after disposal cannot enqueue or install an image. Dispose is
idempotent, cancels loading, clears encoded payload and releases all resident
resources. It must not synchronously wait for an art source that ignores
cancellation on the window thread.

Owner shutdown explicitly revokes the bounded live lease registry before any
resident texture is disposed. Each revoked lease clears its owner, DrawingFrame
and key-array references, and its Drawing becomes unavailable. Delayed external
lease.Dispose is inert. Capture and mandatory counters return to zero during
owner close, even when a capture caller has not released its handle. Mandatory
leases have four fixed-role slots, independent of the four optional capture
slots; capture exhaustion cannot consume publication capacity. The window and
temporary painters use only an active lease's retained drawing. Once the owner
closes, a retained handle grants no further permitted paint.

Encoded metrics name stages rather than claim a total CPU-memory limit:
QueuedBytes covers queued completions, HeldBytes the one completion waiting for
queue room, and DeviceUploadBytes the separately retained byte array being
decoded on the device thread. Source-reader MemoryStream capacity/ToArray copies,
read buffers and decoder buffers are additional transient memory. The 8 MiB
queue limit and 4 MiB held-result limit must never be described as a total CPU
bound. Working-set observations remain separate from these counters and from
calculated GPU payload.

Keep ArtPreloadCards' current seat-filtered snapshot and explicit selected-deck
sources. Cached image bytes are not authority to inspect a hidden occurrence.
Revoked faces disappear through current composition/scoping even if their
unreferenced texture awaits LRU reclamation. Diagnostics report aggregate counts
and bytes without card identities or hidden deck contents.

## Implementation ownership

| Files | Change |
| --- | --- |
| `Mana.Game/CardArt.cs` | Bounded resident/queue/metadata state, admission, reload, pressure status and frame leases |
| `Mana.Contracts/Art.cs`, `Mana.Magic/MagicArtSource.cs`, `Mana.Client/LocalArtSource.cs` | Shared encoded-size constant; bounded remote/cache/local reads and one total network deadline |
| New small `Mana.Game/ArtworkResidency.cs` | Graphics-free accounting, reservation, pin and eviction policy |
| `Mana.Renderer/TextureFiltering.cs` and a focused dimension helper if needed | Bounded dimension/footprint description and validated upload contract |
| `Mana.Renderer/Frames/DrawingFrame.cs`, `DrawCommand.cs` | Complete deduplicated texture inventory; no application/provider dependency |
| `Mana.Game/TableWindow.cs` | Exact frame/lease pairing and temporary capture leases |
| Existing artwork/presentation checks plus focused new check files | Policy, worker integration and actual GPU lifetime regressions |
| Architecture and testing guides | Ownership/limits, commands and honest evidence scope |

The tech lead owns changes to shared architecture/testing guides to avoid parallel
editing conflicts. No gameplay policy, protocol, persistent profile schema,
network endpoint or release workflow needs changing for this slice.

The newer live-update candidate moves CardArt and the window into Mana.Host,
PresentationResources into Mana.Runtime, and the graphics primitives physically
into stable `Mana.Graphics` while preserving the `Mana.Renderer` namespace.
Port the change once into those destination owners: the texture inventory and
preflight live with `Mana.Graphics/Frames` and TextureFiltering; residency and
leases live with Host. Do not duplicate implementations in the old and new
projects or introduce a private Game dependency.

After `PresentationHost.Update` has completed any adoption, TableHostWindow
captures the exact `RuntimeFrame` and its mandatory lease as one retained pair.
It acquires B's lease before releasing A's. Draw paints that stored pair; it
must not reread `presentation.Current.ReadFrame()` and accidentally combine a
new generation's frame with an old generation's lease. Passive/prepared candidate
frames and retained checkpoints acquire their own fixed-slot leases as soon as
their drawing can be retained or painted. Abort retires only the candidate's
roles; successful adoption transfers the chosen frame into the published slot
without an unpinned interval. A single active-generation preload list cannot
stand in for those leases.

A lease may retain only stable DrawingFrame/Texture2D values and Host-owned cache
identity. It cannot retain private Game types, PresentedCard, presentation/runtime
instances, callbacks or delegates into a replaceable generation. Capture callers
retain a stable drawing plus its lease, not an old presentation object. Release
private RuntimeFrame or checkpoint containers when their roles retire; retaining
an artwork lease is not permission to root a collectible assembly. Do not merge
the unfinished update branch merely to deliver the public-master resource fix.

## Verification and release gates

Graphics-free checks run through the existing `CardArtPreloadChecks` registration
in `PresentationSmoke`, with accounting cases covering odd dimensions and exact
mip sums, byte/count exhaustion, deterministic LRU, simultaneous frame pins,
reservation failure, duplicate lease disposal and historical metadata bounds.
Exercise the actual CardArt worker for pressure retry, eviction/reload,
source-failure backoff, obsolete completions, duplicate demand and strict queue
backpressure. A policy-only test cannot establish that uploaded membership was
cleared in the actual owner.

Extend the existing rendered card-reading journey with a deliberately tiny-cache
CardArt instance on the window's actual GraphicsDevice. Paint frame A, retain its
lease, request B/C beyond capacity and prove A still paints with a live texture.
Release A, admit the next face, revisit the evicted face, and verify both real
reload and populated mip levels. Include two Updates before Draw, temporary
capture, decode failure and dispose while a source completion is held. Record
aggregate counters and native pixels, not just the policy's chosen victim.

The existing native UI inventory already runs card reading at 1000 by 740 and
1540 by 980; use those processes without adding a CI lane or timeout. Add the
pure checks to ordinary `--smoke-presentation`. Preserve the existing small and
large rendered card-state journeys and headless privacy/opening checks. Run the
repository documentation checker after guide edits.

A bounded soak uses deterministic local fixture bytes, avoiding network timing:
browse 2,000 unique faces, revisit early pages, and alternate several four-seat
and six-seat layouts. At regular checkpoints record resident/queued/metadata
counts, logical bytes, eviction/reload counts, process working set, supported GPU
memory telemetry, upload cost and frame-time distribution. Resident and metadata
counts must plateau within their limits; every retired resident must be disposed
once. Driver/working-set plateaus are assessed separately and retained as raw
evidence. No numerical FPS promise is made before a measured baseline exists.

## Delivery sequence

1. Freeze the spec and budgets after tech-lead review. Prove a tiny-cache
   eviction/reload failure against the current owner before adding abstractions.
2. Implement accounting, bounded preflight and actual CardArt admission/reload
   together; add the narrow worker regressions.
3. Add frame texture inventories and window/capture leases before enabling
   eviction in shipping composition. Validate real GPU lifetime in the existing
   card-reading runner.
4. Run ordinary native checks and the bounded soak, inspect memory/latency
   evidence, and adjust constants only with recorded rationale. Review both
   normal and deliberate-pressure fallback states visually.
5. Integrate the reviewed source into the tech-lead branch. Required exact-commit
   PR/master checks and human review retain the repository's existing gates.

The slice is complete when the payload/metadata bounds, pinned-frame safety,
eviction/reload, pressure stability and unchanged authorized interaction all
have their named witnesses. Human visual review remains pending until someone
reviews the exact candidate; a passing resource check cannot supply it.

## Tech-lead review resolution on 16 November 2026

The tech lead accepted bounded residency with existing readable fallback under
fully pinned pressure. This revision resolves the requested lifetime details:

| Review point | Required implementation witness |
| --- | --- |
| Equal-frame lease swaps must not retry denied images | Actual CardArt source-read count remains fixed through hundreds of identical completed-frame swaps while a different face is denied; a genuine last-pin release enables one fresh admission attempt |
| Header preflight must be unambiguous | Valid PNG, baseline JPEG and progressive JPEG decode; duplicate/conflicting PNG IHDR, multiple JPEG SOF, DNL, unsupported formats and appended/truncated input are rejected before the instrumented GPU allocator is entered |
| Optional capture pressure must not interrupt play | Hold the capture quota, request another capture and observe structured refusal while current Update/Draw and a fresh ordinary game gesture continue; release a capture and obtain a later successful capture |
| Policy tests must prove actual owner behavior | The real CardArt source counter increments when an evicted face is revisited; the old actual Texture2D is disposed exactly once, the reloaded texture is a different live instance with populated mips, queue/resident markers settle and bytes return to zero on disposal |
| Closing the owner must retire held paint authority first | Hold four mandatory and four capture leases on a real GPU texture, close CardArt, verify all drawing/key/owner references and counters retire, observe one actual texture Disposing event, and prove later lease.Dispose or capture/pin requests cannot restore paint authority |
| Stable graphics and Host port must preserve frame identity | Under A-to-B adoption at capacity, paint the exact retained B RuntimeFrame/lease, keep held A capture pixels valid, and reject any Draw-time reread of the changing Current role |
| Aborted preparation must not retire A | Hold B preparation under pressure, abort it, verify B-only leases release and A still paints/accepts current input with no disposed texture references |
| Delayed captures must not root private code | Retain only stable A drawing plus capture lease across B adoption and private-generation retirement; A capture still paints while the collectible A presentation can retire, then releasing the capture makes its unused textures evictable |

These gates require real owner, allocator and GPU evidence alongside pure
accounting checks. The execution record below distinguishes completed mainline
controls from future Host gates; this acceptance table alone establishes no
passing result or human approval.

## Mainline implementation evidence on 16 November 2026

The mainline source slice is implemented on the reviewed program baseline
`4e67e04da98`, with the tech lead responsible for final commit identity and
integration. It is a source candidate, not a published release. The source
review accepted residency/admission, bounded source reads, frame publication and
the explicit owner-close revocation follow-up. Human review remains pending.

| Local command or owner | Recorded result |
| --- | --- |
| `dotnet build mana-native/Mana.Table.Tests/Mana.Table.Tests.csproj -c Release --no-restore` | Passed; the existing audio-test nullability warning and unavailable NuGet advisory-feed warning were retained |
| `dotnet run --project mana-native/Mana.Client.Tests/Mana.Client.Tests.csproj -c Release --no-restore` | 5,565 checks passed, including finite header/chunked/cache/local size bounds, partial/cancelled publication, exact-printing reuse and one total network deadline |
| Native driver `--smoke-presentation --profile <fresh-profile>` | 1,776 checks passed, including strict headers, mip arithmetic, pressure generations, historical metadata, encoded-byte backpressure and held-completion shutdown |
| Native driver `--smoke-card-reading --size 1000x740 --test-offscreen --profile <fresh-profile>` | 155 checks passed, including the actual GPU owner-close ordering control |
| Native driver `--smoke-card-reading --size 1540x980 --test-offscreen --profile <fresh-profile>` | 155 checks passed with the same real decoder, retention, eviction/reload and close controls |
| Repository documentation checker and `git diff --check` | Passed |

The GPU controls decode real PNG, baseline JPEG and progressive JPEG bytes;
exercise each texture-bearing command independently; hold required and optional
frames through pressure; render delayed captures; reload a disposed victim; and
recover from an actual image-decoder failure. A close control holds all four
mandatory and four optional leases on a real texture. At its Disposing event,
every lease is already inactive with no retained drawing/key references and all
lease counters are zero. Later same-thread and cross-thread handle disposal is
inert, and exactly one texture disposal is observed.

The count/lifetime soak loads 2,000 distinct tiny fixture faces and revisits an
early evicted face. Each size records exactly 2,001 source reads, at most 1,024
resident textures, 977 evictions and zero final queued/held/device-upload bytes.
Its final logical mip payload is 348,160 bytes. Tiny configured byte budgets
separately exercise real byte-pressure denial, so this soak does not pretend to
saturate the default 224 MiB budget. Historical warming of 4,300 identities
separately confirms the metadata bound.

First-failure artifacts retain the old owner's failure to evict after 1,025
tiny GPU faces. Intermediate evidence exposed and corrected a dequeue-to-commit
duplicate upload and historical foreground reload churn. The final soak's exact
source-read count guards those corrections. The disposal follow-up retains its
own final `artwork-close-*` build, result, timing and soak evidence alongside the
earlier artifacts; `artwork-client-final.log` records the complete source-bound
client checks. These are evidence labels, not contributor setup directories.

Agent inspection compared the candidate large reader with its baseline and
checked the small readable fallback. No visual regression was observed in those
captures. They use authored fixtures and do not supply human gameplay approval.

### Remaining Host and telemetry gates

The mainline change does not implement or merge the separate
[live-update U2 work](Mana-Table-Live-Update-Delivery-Design.md). Its stable
Mana.Graphics/Host port, exact RuntimeFrame adoption, A-to-B update under
pressure, aborted passive preparation and collectible-generation retirement
remain U2/later compatibility gates. The completed ordinary-frame and delayed
capture controls do not establish those update-generation behaviors.

The soak records raw process working-set samples and elapsed fixture time.
Physical GPU allocation, driver caches, total CPU-memory plateau, render-frame
percentiles, upload-latency budgets and natural four/six-seat game performance
remain unqualified. Calculated mip payload and queued/held/device-upload stage
counts must not be presented as those measurements. Required integrated-source
CI, public package checks and exact-candidate human review retain their existing
program gates.
