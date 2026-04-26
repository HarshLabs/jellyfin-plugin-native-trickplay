# How Native Trickplay Works

A complete guide to the cache, the encoder pipeline, and every trigger that
moves an item from "uncached" to "cached" — so you can predict what the
plugin will do before you click anything.

> **TL;DR** — Each video item gets one ffmpeg encode (one-time cost; ~1–6
> minutes depending on resolution/codec) that produces a small `iframe.m4s`
> + `iframe.m3u8` pair. Once cached, scrubbing on Apple TV is instant
> forever. The plugin self-heals from interruptions, deduplicates concurrent
> requests, prioritizes user-triggered work over background work, and
> survives Jellyfin restarts cleanly.

---

## 1. The cache, plainly

Each item gets a directory under the cache root:

```
<cache root>/native-trickplay/<itemId>/
├── iframe.m4s          ← the encoded I-frames as a single fMP4 file
├── iframe.m3u8         ← the HLS I-frame playlist (byte-range references into iframe.m4s)
└── .source-mtime       ← stamp:  "<source mtime ticks>:<encoder tag>"
```

A cache entry is **valid** if and only if all three files exist AND the
`.source-mtime` ticks match the source video's current mtime AND the
encoder tag matches the version this build produces. Anything else — file
missing, source modified, encoder upgraded — and the entry is invalid;
it gets re-encoded the next time something requests it.

The cache root is whatever you set in **Cache directory** (Generation card
on the dashboard). Empty = Jellyfin's default cache path
(`~/.local/share/jellyfin/cache/native-trickplay/` on Linux, similar on
macOS/Windows).

---

## 2. Two ffmpegs, side by side

It's easy to confuse them. **Don't.**

| | Jellyfin's playback ffmpeg | Plugin's iframe encoder |
|---|---|---|
| Owned by | Jellyfin core | This plugin |
| Triggered by | AVPlayer fetching `main.m3u8` | `PlaybackWarmupService`, dashboard, etc. |
| What it does | Stream-copy or transcode source → fMP4 segments → AVPlayer | Decode source, tonemap (if HDR), downscale, all-IDR encode → cached `iframe.m4s` |
| Hits source video | Yes | Yes (read-only, separate handle) |
| Affects playback | Is playback | Never |

Both processes can read the same source file simultaneously without
interference. The plugin's encoder writes only to its own cache directory.

---

## 3. The plugin encoder pipeline

For every item the plugin encodes, ONE ffmpeg invocation runs:

```
source video (mkv/mp4/mov/...)
    │
    ▼
  DECODE  ── -hwaccel videotoolbox / qsv / vaapi / cuda / d3d11va / drm / rkmpp ──►  (HARDWARE)
    │       (most hwaccels auto-transfer frames to system memory by default,
    │        so the CPU filter chain reads them directly. QSV is the
    │        outlier — it leaves frames in qsv-format on the GPU, so the
    │        plugin prepends `hwdownload,format=<sw_format>` for QSV only,
    │        where <sw_format> is nv12 for 8-bit / p010le for 10-bit,
    │        chosen by an ffprobe pass on stream metadata)
    ▼
  fps=N            (N = 1 / IframeIntervalSeconds — uniform thumbnail density
    │              regardless of the source's GOP layout)
    ▼
  zscale + Hable tonemap     (HDR sources only: BT.2020 PQ → BT.709 SDR via
    │                         linear-light intermediate; AVPlayer rejects
    │                         iframe variants whose VUI claims HDR)
    ▼
  scale=-2:<height>, format=yuv420p     (resize to your IframeWidth setting,
    │                                    8-bit chroma-subsampled output)
    ▼
  ENCODE  ── libx264 -preset <preset> -crf <crf>                                 (CPU)
    │       -profile:v main -level:v 4.0
    │       -x264-params keyint=1:scenecut=0:open-gop=0:colorprim=bt709:...
    │
    │       Always CPU. Hardware H.264 encoders silently drop or break on
    │       keyint=1, but the iframe variant requires every frame to be IDR.
    │       libx264 is the only encoder that honors all-IDR cleanly and
    │       produces a bitstream whose VUI matches the declared `avc1.4d0028`
    │       Main 4.0 codec string AVPlayer expects.
    ▼
  fragmented MP4   (-movflags +frag_keyframe+empty_moov+default_base_moof)
    │              one moof+mdat fragment per I-frame so the playlist can
    │              address each frame with EXT-X-BYTERANGE
    ▼
  iframe.m4s.tmp on disk
    │
    ▼
  Mp4BoxScanner walks the file → records (offset, size) for every fragment
    │
    ▼
  ProbePtsDeltasAsync (ffprobe pass) → per-frame durations for #EXTINF
    │
    ▼
  BuildPlaylist → atomic File.Move(.tmp → final) → write .source-mtime stamp
                  (until both succeed, the cache is "incomplete" and
                   StartupResumeService will re-queue the item on next boot)
```

Output is **always SDR H.264 Main Level 4.0** (`avc1.4d0028`,
`VIDEO-RANGE=SDR`) per Apple HLS Authoring Spec §6.16. RESOLUTION is
computed from your IframeWidth × source aspect ratio.

---

## 4. Every trigger that produces a cache entry

There are seven places an encode can start. They all funnel through
`IframeAssetCache.GetOrCreateAsync(itemId, isPriority)`, which shares one
de-duplication map and one priority-aware slot manager — so concurrent
requests for the same item run only once, and high-priority requests
leapfrog background work.

### 4.1 — User starts playback (deferred, configurable)

**Trigger:** `ISessionManager.PlaybackStart` event.
**Service:** `PlaybackWarmupService`

1. Event fires when a client reports playback start.
2. The service waits **`WarmupDelaySeconds`** (default 30, range 5–300, set
   on the dashboard) before doing anything. The defer prevents the heavy
   decode+tonemap encoder from racing Jellyfin's main playback ffmpeg
   startup window — observed empirically on macOS/videotoolbox to
   sometimes inhibit Jellyfin's TranscodeManager from firing if the
   plugin's ffmpeg starts in the same ~1 s as the playback ffmpeg's
   probe phase.
3. After the delay, re-checks the cache. Already valid → skip.
4. Otherwise, calls `_cache.Warmup(itemId, isPriority: true)` which
   queues an encode at **High** priority.

> The 30 s default is conservative. On Apple Silicon / NVMe, 10–15 s is
> safe and gives faster queue feedback. Slow disks / NAS deployments
> should keep 30 s.

### 4.2 — Dashboard Generate buttons

**Trigger:** `POST /Plugins/NativeTrickplay/Generate` or `/GenerateLibrary`.
**Service:** `IframeAdminController`

Per-row Generate, "Generate selected", or "Generate all in library" all
hit a controller endpoint with a list of item IDs and call
`_cache.Warmup(id, isPriority: false)` immediately. **No 30 s defer**
(the user explicitly asked for this) and **Normal** priority (will yield
to playback-triggered High encodes).

Optimistic UI: row badges flip to ⏸ queued, the shimmer bar starts. When
ffmpeg starts running on each one, the badge transitions to
`▶ encoding · NN%` (live) and the meta line shows `XX MiB · Ym Zs`
ticking up.

### 4.3 — User scrubs an uncached item before warmup completes

**Trigger:** AVPlayer fetches `iframe.m3u8` for an item that's not cached.
**Service:** `IframeHlsController`

1. AVPlayer parses the master playlist, sees the I-FRAME-STREAM-INF, and
   fetches `/Videos/{id}/iframe.m3u8`.
2. Cache miss. Controller returns **HTTP 200 + an empty I-frames-only
   playlist** (`#EXT-X-I-FRAMES-ONLY` + `#EXT-X-ENDLIST`, zero segments)
   with response header `X-Trickplay-Status: encoding`. AVPlayer parses
   it, sees zero segments, marks the variant unusable for this session,
   and proceeds with primary playback unaffected.
3. The controller does **not** itself trigger Warmup — that path
   races Jellyfin's main playback ffmpeg startup. Warmup is left to
   `PlaybackWarmupService` which is already scheduled with the
   configurable defer.

> Why not 503 + Retry-After (older versions)? AVPlayer treats 503 on a
> variant playlist referenced by the master as "wait until ready",
> stalling primary playback for the full encode duration (~6 min on a 4K
> HDR title). Why not 404? AVPlayer's tvOS 26 `SUPPLEMENTAL-CODECS`
> validator (used by HDR/DV synthetic masters) is strict and rejects
> 404 with `kCMHTTPLiveStreamingMissingMandatoryKey` (-12642), aborting
> the master parse entirely. The empty-stub + `X-Trickplay-Status`
> header design is the only response shape that works for both SDR
> (server-side wrap) and HDR (client-side wrap) paths.

### 4.4 — Cache hit (the fast path you'll hit most often)

If `iframe.m3u8` is requested and the cache is valid, the controller
returns the cached playlist with `X-Trickplay-Status: ready`. AVPlayer
fetches `iframe.m4s` byte ranges and trickplay just works. ~1 ms server
overhead.

HDR-aware clients (e.g., JellyseerTV) HEAD-probe iframe.m3u8 before
building their synthetic master and only declare the I-frame variant
when the probe returns `X-Trickplay-Status: ready`. Cold cache → no
declaration → primary playback isn't affected, encode still runs in the
background, next session gets trickplay.

### 4.5 — Pre-generate scheduled task (4 AM daily by default)

**Trigger:** Daily timer.
**Service:** `PreGenerateIframeAssetsTask`

1. Enumerates every video item in the library
   (`Movie`, `Episode`, `MusicVideo`, `Video`).
2. Skips items already cached (`TryGetCached` < 1 ms each).
3. For uncached items, calls `_cache.GetOrCreateAsync(id)` synchronously
   at **Normal** priority — the global slot manager controls how many
   ffmpegs run at once.
4. Logs final tally:
   `pre-gen done: N/M processed, X encoded, Y already-cached, Z failed`.

Run this once on a fresh library to skip the per-item cold-cache wait
on first scrub.

### 4.6 — Plugin startup auto-resume (30 s after Jellyfin boot)

**Trigger:** Plugin loads after a Jellyfin restart.
**Service:** `StartupResumeService`

1. Waits 30 s so Jellyfin's own boot work and any active playback's
   segment ffmpeg get a head start.
2. Walks the cache root for directories containing `iframe.m4s.tmp` but
   missing either `iframe.m4s` or `.source-mtime` — signs of an encode
   that didn't complete.
3. Verifies the item still exists in the library; if not, leaves the
   orphan dir for the pruner.
4. Calls `_cache.Warmup(itemId, isPriority: false)` to re-queue.

Catches: plugin upgrades, Jellyfin crashes (OOM, panic), power failures,
`kill -9`. Toggle on Generation card → **Resume interrupted encodes**
(default on).

### 4.7 — Library scan adds a new item (opt-in)

**Trigger:** `ILibraryManager.ItemAdded` event fires when Jellyfin's
scanner imports a new video.
**Service:** `LibraryAddListener`
**Gate:** `EncodeOnLibraryAdd` config flag (default **off**).

When the flag is on, every newly-added video item gets a
`_cache.Warmup(id, isPriority: false)` call — Normal priority, so it
yields to playback-triggered High encodes. Already-cached items are
skipped (rare on add, possible if the file was re-imported).

This mirrors Jellyfin's own "Extract trickplay images during the
library scan" toggle but for the native-HLS path. Default off because a
fresh library import can fire thousands of ItemAdded events; the daily
4 AM pre-generate task already handles uncached items overnight, so
opting out just means new items get cached overnight instead of
immediately.

### 4.8 — Stale-encoder cleanup (prune phase 0)

`PruneTrickplayCacheTask` Phase 0 evicts cache directories whose
`.source-mtime` stamp doesn't match the encoder tag the current build
expects. We bump the encoder tag whenever the produced output materially
changes (e.g., HDR tonemap added, level declaration fixed). After
eviction, those items will re-encode the next time something triggers
them — usually the next pre-gen run.

---

## 5. Concurrency: priority queue

```
GetOrCreateAsync(id, isPriority)
        │
        ▼
  _inflight: ConcurrentDictionary<itemId, Lazy<Task<IframeAsset>>>
  (de-duplication — same item requested N times → ONE ffmpeg)
        │
        ▼
  GenerateAsync ─► AcquireSlotAsync(priority, id, ct)
                   │
                   ▼
                Slot manager (replaces a plain semaphore)
                  • slotsAvailable counter, capped by MaxConcurrentGenerations
                  • waitQueue: priority desc → sequence asc
                  • PromoteToPriority(id) bumps an already-queued waiter
                    to High in place when a playback request races a
                    pre-gen / admin request for the same item
                   │
                   ▼
                RunFfmpegAsync (with hwaccel if configured)
                   │
                   ▼
                Mp4BoxScanner → ProbePtsDeltasAsync → BuildPlaylist
                   │
                   ▼
                atomic File.Move + stamp write
```

**Three priority levels:**
- **High (100)** — playback-triggered (`PlaybackWarmupService`)
- **Normal (50)** — admin Generate, pre-gen scheduled task,
  StartupResumeService
- **Low (10)** — reserved

A user clicking Play on item #150 of a 200-item bulk encode gets that
item promoted to the front of the queue instead of waiting behind 149
others. **Non-preemptive** — an encode that's already running stays put;
priority only steers the next slot assignment when one frees up.

`MaxConcurrentGenerations` (Generation card, default 1, max 8) caps how
many ffmpegs run in parallel. Changing it requires a Jellyfin restart —
the slot count is created at plugin load.

**Process lifetime:** ffmpeg uses `IHostApplicationLifetime.ApplicationStopping`
as its cancellation token, NOT the HTTP request token. AVPlayer
disconnecting or the dashboard closing doesn't kill anything; only a
Jellyfin shutdown does. Killed mid-encode → leaves a `.tmp` →
StartupResumeService picks it up next boot.

---

## 6. Hardware acceleration

The encoder reads Jellyfin's global **Dashboard → Playback → Transcoding
→ Hardware acceleration** setting and adds the right `-hwaccel` flag.
The plugin's own **Use hardware decoding** toggle is a second gate — off
forces software regardless.

| Jellyfin setting | What the plugin adds |
|---|---|
| videotoolbox (macOS) | `-hwaccel videotoolbox -hwaccel_flags +low_priority` |
| qsv (Intel) | `-hwaccel qsv [-qsv_device …]` |
| vaapi (Linux open driver) | `-hwaccel vaapi [-vaapi_device …]` |
| nvenc (NVIDIA encoder) | `-hwaccel cuda -threads 1` |
| amf on Windows | `-hwaccel d3d11va -threads 2` |
| amf on Linux | `-hwaccel vaapi [-vaapi_device …]` |
| v4l2m2m (Pi, etc.) | `-hwaccel drm` |
| rkmpp (Rockchip) | `-hwaccel rkmpp` |
| amf on macOS / unknown | software-only |

**`hwdownload` is QSV-only.** Of the supported hwaccels, only QSV
needs an explicit `hwdownload,format=<sw_format>` at the head of the
filter chain. VideoToolbox / VAAPI / CUDA / D3D11VA / DRM / RKMPP all
auto-transfer decoded frames to system memory by default (when no
`-hwaccel_output_format` is set), so the CPU `fps` filter reads them
directly. QSV's decoder is the outlier — it leaves frames in qsv-format
on the GPU, and ffmpeg's auto-inserted `auto_scale` can't bridge them,
producing `Impossible to convert between the formats supported by the
filter 'Parsed_fps_0' and the filter 'auto_scale_0'`. For QSV only, the
plugin adds `hwdownload,format=<probed>` to bridge.

**Bit depth is probed up front** (`ProbeSourceVideo` runs ffprobe to
read `bits_per_raw_sample` and `pix_fmt`) so the QSV `hwdownload`
format matches the hwframe's actual `sw_format`: `nv12` for 8-bit
sources, `p010le` for 10-bit. Using an alternation like `nv12|p010le`
does NOT work — ffmpeg picks the first option and errors out if it
isn't in the device's `valid_sw_formats` list. The same probe also
detects HDR (PQ / HLG transfer characteristics) to decide whether the
filter chain needs the zscale + Hable tonemap stage.

> **Historical note.** Plugin versions v1.1.28–v1.1.32 also added
> `hwdownload` for VAAPI / CUDA / D3D11VA based on a single QSV bug
> report. That over-generalization actually broke hardware decode for
> those three hwaccels: their decoders had already auto-downloaded
> frames to CPU memory, and the extra `hwdownload` filter caused
> `auto_scale` to fail in the opposite direction. The plugin's
> software-decode fallback caught the error and the encode still
> completed, but the GPU was bypassed. v1.1.33.0 reverted to the
> v1.1.25 behavior (bare `-hwaccel <type>`) for those three; QSV
> still gets the bridge.

**Auto-fallback to software decode** if the hwaccel handoff fails with a
recognized error pattern (`Failed to transfer data to output frame`,
`Cannot use AVHWFramesContext`, `Invalid output format … for hwframe
download`, `Impossible to convert between the formats`, …). The plugin
retries the same encode with `-hwaccel` removed. The user sees a `[WRN]`
line with the tail of the ffmpeg stderr; the encode still completes.

**Encoder side is always CPU (libx264).** Hardware H.264 encoders don't
honor `keyint=1` reliably and the iframe variant requires every frame
to be IDR. At 480p / 1 fps / CRF 30, libx264 is cheap (~2× realtime per
encode on a Mac mini M2).

---

## 7. Pruning: how the cache stays bounded

`PruneTrickplayCacheTask` runs daily at 3 AM (configurable). Four phases,
each gated independently:

1. **Phase 0 — Stale / corrupt** (always on): evicts cache dirs where
   `TryGetCached` returns null. Catches old-encoder-tag entries and
   half-written `.tmp` orphans for items that no longer exist.
2. **Phase 1 — Orphans** (`PruneOrphans`, default on): evicts cache dirs
   whose item is no longer in the Jellyfin library.
3. **Phase 2 — Age-based** (`MaxAgeDays`, default 90, `0` disables):
   evicts entries unaccessed for N days.
4. **Phase 3 — Size cap** (`MaxCacheGigabytes`, default `0` / disabled):
   evicts least-recently-used entries until total size is under the cap.

`TryEvict` is in-flight aware — it skips items currently being encoded
(`_inflight` dictionary check) so eviction can never race regeneration.
The dictionary is cleaned up on every encode return path (success,
failure, fast-path cache hit), so an item that finished encoding earlier
in the day is properly evictable.

---

## 8. The HLS injection layer

Two endpoints are intercepted; everything else passes through untouched:

- **`GET /Videos/{id}/master.m3u8`** — Jellyfin's multi-variant playlist.
  Plugin appends a single `#EXT-X-I-FRAME-STREAM-INF` line pointing at
  `iframe.m3u8`, leaving every other variant untouched.
- **`GET /Videos/{id}/main.m3u8`** — Jellyfin's single-variant playlist
  (used for stream-copy direct-stream scenarios). For SDR, plugin wraps
  the response as a synthetic master with one STREAM-INF (the original
  media playlist) + one I-FRAME-STREAM-INF.

**HDR/DV is intentionally pass-through.** The JellyseerTV iOS client
builds its own synthetic master client-side via
`AVAssetResourceLoaderDelegate` because tvOS 26's `SUPPLEMENTAL-CODECS`
extension requires DV codec strings the server can't reliably emit.
That client HEAD-probes `iframe.m3u8` and conditionally includes the
I-frame variant based on `X-Trickplay-Status: ready` — so HDR scrubbing
thumbnails work the moment the plugin's cache is hot.

The injector buffers the response into a `MemoryStream`, decides what
to do based on the body content (master vs media playlist), and writes
back either modified or original bytes. **Pass-through is the safe
default** — any ambiguity, the original playlist wins and trickplay
just doesn't appear for that item.

---

## 9. Reading the dashboard

Three live cards reflect different parts of the system:

**Cache Status** (`GET /Plugins/NativeTrickplay/Status`) — KPI tiles
(cached items, bytes on disk, total library items, coverage %),
per-library breakdown with progress bars, refresh button.

**Encoding in progress** (`GET /Plugins/NativeTrickplay/Progress`) —
polls every 2 s when active, every 15 s when idle. Shows running items
first (pinned), then queued in submission order. Each running row
displays six live columns:

| Column | Meaning |
|---|---|
| **Item** | Two-line display. Top line: `Series · S2E12 — Episode title` for episodes (raw episode names like "Two Dragon Emperors" are usually unidentifiable on their own), bare title for movies. Muted second line: source profile + decode hardware path (e.g. `2160p HEVC 10-bit HDR · CUDA`). The source profile is single-pass `ffprobe` data captured during the existing HDR/bit-depth probe — codec, resolution, bit depth, HDR flag combined into one tag. The hardware path reflects the *actual* decoder for this run; on the software-decode retry path it flips to `SW` so an admin can see when hwaccel fell back. |
| **Status** | `▶ running · NN%` (or `⏸ queued`). Percent is the same value as the Progress column, embedded in the badge for at-a-glance scanning. |
| **Elapsed** | Wall-clock time since this item's slot was acquired and ffmpeg actually started. *Not* time since it was originally queued — restamped at queued→running transition so a row that just got promoted from a multi-thousand-item bulk job displays a fresh elapsed counter, not the age of the bulk job. |
| **ETA** | Projected wall-clock seconds until the encode finishes, computed from ffmpeg's reported `speed` multiplier: `(source_duration − source_consumed) / speed`. Shown as `2m 15s · 1.83x` — duration plus the current speed multiplier in muted text underneath, so an admin can see *why* it's slow (e.g. `0.4x` = encoder is the bottleneck). Shows just the speed (no ETA) if `speed` is known but the source duration isn't. |
| **Output** | Size of the partial `.tmp` segment file on disk right now (cheap `FileInfo.Length` per poll). Lets you eyeball whether bytes are actually being written. |
| **Progress** | Percent done. See estimator note below. |

> **How the percent is computed** — primary signal is **time-based**:
> ffmpeg is launched with `-progress pipe:1 -nostats` and the plugin
> parses the `out_time_us=` lines off stdout (microseconds of source
> consumed). Percent = `out_time_us / source_duration_us`. This matches
> the user's mental model ("we're 8 m into a 22 m episode = 36 %") and
> is independent of whether the byte estimate matches actual output.
>
> **Fallback** when the source has no known duration (live recordings,
> damaged metadata): a byte-size estimate `partial_bytes /
> estimated_total_bytes`, where `estimated_total_bytes ≈ frames ×
> 14.4 KB × (width / 480)²`. High-detail content can blow past this
> estimate, so the cap below applies.
>
> **Cap at 99 %** — the displayed percent is `min(99, raw)` regardless
> of which signal feeds it. The encode isn't "done" until ffmpeg exits
> *and* the mp4-box scan + PTS probe + atomic file rename all complete;
> capping prevents the bar from briefly reading 100 % during that
> finalization window.

Default cap 10 rows; "Show all (N)" expands to a scrollable list.
Auto-disappears when nothing is in flight.

**Find & select items** (`GET /Plugins/NativeTrickplay/Items`):
- Tokenized search (case + accent insensitive), AND-matched across Name +
  SeriesName. Recognized S/E forms: `S6E18`, `1x05`, `Season 6`,
  `Episode 18`, `S7`, `E18`.
- Live search debounced at 280 ms (no Search button needed).
- Cache filter (All / Cached / Not cached) and Sort order both
  live-refresh.
- Per-row Generate / Regenerate / Evict buttons.
- Bulk Generate selected / Evict selected.
- Reactive row state synced from /Progress every 2 s.

---

## 10. Configuration knobs

All on the dashboard config page; values written to the standard
Jellyfin plugin XML config.

| Setting | Default | Range | Effect | Restart needed? |
|---|---|---|---|---|
| Enabled | true | bool | Master switch. Off = no master-playlist injection, no warmup, no encodes | No |
| Iframe width | 320 | px | Output thumbnail height (sic — width in code, used as scale=-2:N height) | No (new encodes only) |
| Iframe interval | 2.0 s | ≥1 | Seconds between thumbnails (Apple's HLS spec recommends 2-5s; lower = denser scrubbing + bigger cache + 2× encode work). | No (new encodes only) |
| Iframe CRF | 32 | 18–40 | x264 quality (lower = larger files) | No (new encodes only) |
| Iframe preset | ultrafast | x264 preset | Encode speed/quality tradeoff. **`ultrafast` is the right choice** — every output frame is an IDR (no motion estimation work for slower presets to optimize), output is 320p, visual difference vs slower presets is invisible. Slower presets cost 2-10× more CPU per encode for no perceptible benefit on trickplay thumbnails. | No (new encodes only) |
| Use hardware decoding | true | bool | Read Jellyfin's hwaccel setting; off forces software | No |
| Concurrent encodes | 1 | 1–16 | Max parallel ffmpegs. Practical ceiling is your CPU core count divided by *Threads per encode* — pushing past that just thrashes the scheduler. | **Yes** |
| Threads per encode | 1 | 0–32 | Threads each ffmpeg invocation may use. `1` (default) prevents thread oversubscription with multiple Concurrent encodes — without this cap, each ffmpeg auto-detects "all cores", so 4 concurrent on an 8-core box becomes 32-thread contention. Pinning to 1 thread/job typically gives 2-4× better aggregate throughput on bulk encodes. `0` = ffmpeg auto-detect (use only when Concurrent encodes = 1). | No (new encodes only) |
| Warmup delay | 30 s | 5–300 | Seconds after PlaybackStart before encoding | No |
| Resume interrupted encodes | true | bool | StartupResumeService toggle | No |
| Encode on library add | false | bool | Queue an encode when the library scanner imports a new video item (Normal priority) | No |
| Cache directory | (empty) | path | Where iframe assets live; empty = Jellyfin default | No (new encodes only) |
| Prune orphans | true | bool | Pruner Phase 1 | No |
| Max age days | 90 | 0–N | Pruner Phase 2 (`0` disables) | No |
| Max cache GB | 0 | 0–N | Pruner Phase 3 size cap (`0` disables) | No |

---

## 11. What happens when …

| Scenario | Result |
|---|---|
| You upgrade the plugin | New version installs; old keeps running until restart. On restart, in-flight ffmpegs killed; `.tmp` files stay on disk; auto-resume re-queues them ~30 s after boot. |
| Jellyfin crashes (OOM / panic) | Same as upgrade-restart. Auto-resume catches it. |
| Source video file is replaced | Stamp mtime no longer matches → next access invalidates the old cache → re-encode. |
| You change `IframeWidth`, `IframeIntervalSeconds`, `IframeCrf`, `IframePreset` | Existing caches stay valid (encoder tag unchanged). New encodes use new settings. To regenerate everything, run the Pre-generate task or evict + generate. |
| You change `WarmupDelaySeconds` | Takes effect immediately (next PlaybackStart event). |
| You change `MaxConcurrentGenerations` | Takes effect on next plugin load (Jellyfin restart). |
| You change `Cache directory` | Old caches at the old path are orphaned (delete manually). New encodes write to the new path. |
| You disable the plugin | Existing caches preserved on disk. Master-playlist injection stops; AVPlayer falls back to no trickplay. Re-enable to pick up where you left off. |
| You evict an item via the dashboard | Cache directory deleted. Next access re-encodes from scratch. Eviction is in-flight aware: items currently encoding are silently skipped. |
| Source has unusual codec or bit-depth probe is wrong | hwaccel decode fails; plugin retries with software decode and succeeds. One `[WRN]` line in the log; encode completes. |
| Library scan adds 500 new items, **`Encode on library add` off** (default) | They appear in /Items with `cached=false`; not encoded until something triggers them (next 4 AM pre-gen run, manual Generate, or first playback). |
| Library scan adds 500 new items, **`Encode on library add` on** | Each one fires `LibraryAddListener.OnItemAdded` and gets a Normal-priority Warmup. The slot manager processes them in submission order, capped by `MaxConcurrentGenerations`. Playback-triggered encodes still leapfrog them via the priority queue. |
| HDR client (JellyseerTV) plays an uncached HDR item | HEAD-probe sees `X-Trickplay-Status: encoding`; client omits the I-frame variant from its synthetic master; primary playback starts immediately; background encode runs; next play of the same item gets `ready` and trickplay works. |

---

## 12. Where to look in the code

| Behavior | File |
|---|---|
| Encoder pipeline (filter chain, codec args) | `Hls/IframeAssetCache.cs` → `BuildFfmpegArgs` |
| HDR + bit-depth probe | `Hls/IframeAssetCache.cs` → `ProbeSourceVideo` |
| Hardware acceleration mapping | `Hls/IframeAssetCache.cs` → `AppendHwaccelArgs` + `IsLikelyHwaccelFailure` |
| Cache validation logic | `Hls/IframeAssetCache.cs` → `TryGetCached` |
| Concurrency / dedup / priority queue | `Hls/IframeAssetCache.cs` → `GetOrCreateAsync`, `AcquireSlotAsync`, `ReleaseSlot`, `PromoteToPriority` |
| Inflight cleanup (fast-path + finally) | `Hls/IframeAssetCache.cs` → `GenerateAsync` |
| Progress signals (time-based via ffmpeg `-progress`, byte-size fallback) | `Hls/IframeAssetCache.cs` → `RunFfmpegAsync` (out_time_us / speed parser) + `EstimateTotalEncodedBytes` |
| Master-playlist rewriting | `Hls/MasterPlaylistInjector.cs` → `OnResultExecutionAsync` |
| `iframe.m3u8` controller (HEAD probe + cache miss stub + `X-Trickplay-Status` header) | `Api/IframeHlsController.cs` |
| Dashboard HTTP API | `Api/IframeAdminController.cs` |
| Playback-triggered warmup (configurable defer) | `Hls/PlaybackWarmupService.cs` |
| Library-scan-add listener (opt-in) | `Hls/LibraryAddListener.cs` |
| Boot-time recovery of interrupted encodes | `Hls/StartupResumeService.cs` |
| Daily pre-generate task | `Tasks/PreGenerateIframeAssetsTask.cs` |
| Daily prune task (4 phases) | `Tasks/PruneTrickplayCacheTask.cs` |
| All configuration knobs | `Plugin.cs` → `PluginConfiguration` |

The dashboard HTML lives in `Configuration/configPage.html` — single
file with all CSS and JS inline. Everything is scoped under
`#NativeTrickplayConfigPage` so it never leaks into the rest of Jellyfin.
