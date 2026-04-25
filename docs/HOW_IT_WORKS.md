# How Native Trickplay Works

A complete guide to the cache, the encoder pipeline, and every trigger that
moves an item from "uncached" to "cached" — so you can predict what the
plugin will do before you click anything.

> **TL;DR** — Each video item gets one ffmpeg encode (one-time cost, ~1–6 min
> depending on resolution/codec) that produces a small `iframe.m4s` +
> `iframe.m3u8` pair. Once cached, scrubbing on Apple TV is instant forever.
> The plugin self-heals from interruptions, deduplicates concurrent requests,
> and survives Jellyfin restarts cleanly.

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
`.source-mtime` stamp's ticks match the source video file's current mtime AND
the encoder tag matches the version this build produces. Anything else — file
missing, source modified, encoder upgraded — the entry is invalid and gets
re-encoded the next time it's needed.

The cache root is whatever you set in **Cache directory** (Generation card on
the dashboard). Empty = Jellyfin's default cache path
(`~/.local/share/jellyfin/cache/native-trickplay/` on Linux, similar locations
on macOS/Windows).

The full Cache Status panel (top of the dashboard) and the per-library
coverage bars are computed by walking this directory.

---

## 2. What gets encoded — the pipeline

For every item, ONE ffmpeg invocation produces all the trickplay frames at
once. The pipeline:

```
source video (mkv/mp4/mov/...)
    │
    ▼
ffmpeg input  ─[hwaccel videotoolbox / qsv / vaapi / cuda / d3d11va]─►
    │
    ▼
fps=N filter  (N = 1 / IframeIntervalSeconds)
    │  thins frames to one per N seconds of source — this is what
    │  guarantees uniform thumbnail density regardless of the source's
    │  GOP layout
    ▼
zscale + Hable tonemap  (HDR sources only)
    │  BT.2020 PQ → BT.709 SDR for a clean H.264 output bitstream
    │  whose VUI tags AVPlayer accepts
    ▼
scale=-2:<height>, format=yuv420p
    │  resize to your configured Thumbnail height, 4:2:0 8-bit
    ▼
libx264 -profile:v main -level:v 4.0
        -x264-params keyint=1:scenecut=0:open-gop=0:colorprim=bt709:...
    │  every frame becomes an IDR — no inter-prediction, no GOPs
    ▼
fragmented MP4  (-movflags +frag_keyframe+empty_moov+default_base_moof)
    │  one moof+mdat fragment per I-frame so the playlist can address
    │  each frame with EXT-X-BYTERANGE
    ▼
iframe.m4s.tmp on disk
    │
    ▼
plugin's box scanner walks the file, builds iframe.m3u8 with byte ranges
    │
    ▼
File.Move(iframe.m4s.tmp → iframe.m4s)  +  write .source-mtime stamp
                       (atomic — until both succeed, the cache is "incomplete")
```

The output is always **SDR H.264 Main Level 4.0** even for HDR sources. Apple's
HLS Authoring Spec §6.16 mandates SDR trick play streams; AVPlayer silently
rejects HDR-only I-frame variants. The Hable tonemap converts HDR colors
correctly so the thumbnails don't look washed out.

Codec strings on the wire match the bitstream exactly:
- `avc1.4d0028` — H.264 Main Level 4.0, no constraint flags
- VIDEO-RANGE=SDR
- RESOLUTION computed from your IframeWidth × source aspect ratio

---

## 3. Every trigger that produces a cache entry

There are **six** places an encode can start. They all converge on the same
`IframeAssetCache.GetOrCreateAsync(itemId)` method, so they share the
de-duplication and concurrency machinery.

### 3.1 — User starts playback (deferred 30s)

**Trigger:** Jellyfin's `ISessionManager.PlaybackStart` event fires.

**Service:** `PlaybackWarmupService` (hosted service)

**Flow:**
1. Event fires when a client starts playing an item.
2. The service waits **30 seconds** before doing anything. This delay exists
   so the heavy iframe encoder doesn't race Jellyfin's stream-copy ffmpeg
   startup for the first segment — racing the two ffmpegs caused intermittent
   client-side `-1008 resource unavailable` errors on uncached HDR titles.
3. After the delay, the service checks if the cache is already valid for the
   item. If yes — skip entirely (no work).
4. If not, calls `_cache.Warmup(itemId)` which queues an encode.

**Why this matters:** by the time you reach for the scrubber on the Apple TV
(typically 30s+ into watching), the cache is either already there or being
built. The first segment of the actual video plays without competing for
encoder resources.

### 3.2 — User clicks Generate / Generate selected / Generate all in library

**Trigger:** `POST /Plugins/NativeTrickplay/Generate` or `/GenerateLibrary`
endpoints, fired from the dashboard.

**Service:** `IframeAdminController`

**Flow:**
1. Per-row Generate button, or "Generate selected" with the row checkboxes,
   or "Generate all in library" with the dropdown — all hit a controller
   endpoint with a list of item IDs.
2. The controller calls `_cache.Warmup(itemId)` for each ID immediately
   (no 30s delay — the user explicitly asked for this).
3. Toast confirms: "N items queued for generation."
4. Optimistic UI: row badges flip to ⏸ queued, the shimmer bar starts, the
   row's checkbox locks. When ffmpeg actually starts on each one, the badge
   transitions to ▶ encoding (pulsing blue) and the meta line under it shows
   live `XX MiB · Ym Zs` ticking up.

**Idempotency:** if you click Generate on an item that's already cached,
TryGetCached hits in `< 1 ms` and the warmup is a no-op. If you click on an
item that's already in flight, the in-flight Lazy task is reused — no
duplicate ffmpeg.

### 3.3 — User scrubs an uncached item before warmup completes

**Trigger:** AVPlayer fetches `iframe.m3u8` for an item that has no valid
cache yet.

**Service:** `IframeHlsController`

**Flow:**
1. AVPlayer parses the master playlist, sees the I-FRAME-STREAM-INF, and
   fetches `/Videos/{id}/iframe.m3u8`.
2. Controller calls `TryGetCached(id)` — returns null because the cache
   isn't ready yet.
3. Controller calls `_cache.Warmup(id)` (synchronous — kicks the encode if
   not already in flight) and returns **HTTP 503 + Retry-After: 15**.
4. AVPlayer politely waits 15 seconds and retries.
5. When the encode completes, the cache is populated and the next retry
   gets HTTP 200 with the playlist. AVPlayer then begins fetching the
   `iframe.m4s` byte ranges normally.

**Why 503 + Retry-After:** AVPlayer would otherwise time out after ~30s and
mark the I-frame variant permanently broken for the playback session. The
503 keeps it polling indefinitely. The encode itself uses
`IHostApplicationLifetime.ApplicationStopping` for its cancellation token,
NOT the HTTP request's token — so AVPlayer disconnecting doesn't kill ffmpeg.

### 3.4 — Pre-generate scheduled task (4 AM daily by default)

**Trigger:** The scheduled task fires at 4 AM (configurable in
Dashboard → Scheduled Tasks → Pre-generate Native Trickplay I-frames).

**Service:** `PreGenerateIframeAssetsTask`

**Flow:**
1. Task enumerates every video item in the library
   (`Movie`, `Episode`, `MusicVideo`, `Video`).
2. For each, calls `TryGetCached(id)` — if cached, count as "already done"
   and move on.
3. If not cached, calls `_cache.GetOrCreateAsync(id).WaitAsync(ct)` — runs
   the encode synchronously, one item at a time (respecting the global
   concurrency semaphore so multiple encodes can run in parallel if you
   raised that setting).
4. Logs final tally: `pre-gen done: N/M processed, X encoded, Y already-cached, Z failed`.

**Why this exists:** for a fresh library, hitting Play and waiting through
a 503 polling loop on every uncached item would be annoying. Running this
once overnight makes every subsequent first-scrub instant.

### 3.5 — Plugin startup auto-resume (30s after Jellyfin boot)

**Trigger:** Plugin loads after a Jellyfin restart.

**Service:** `StartupResumeService`

**Flow:**
1. Service starts, waits **30 seconds** so Jellyfin's own boot work and
   any active playback's segment ffmpeg get a head start.
2. Walks the cache root looking for directories that contain
   `iframe.m4s.tmp` (a half-written encoder output) but where either
   `iframe.m4s` or `.source-mtime` is missing.
3. For each match, verifies the corresponding library item still exists
   (skips items that have been deleted — those are the pruner's job).
4. Calls `_cache.Warmup(itemId)` to re-queue the encode.

**What this catches:**
- Plugin upgrades (the user clicks Update + Restart while encodes are running)
- Jellyfin crashes (OOM, kernel panic)
- Power failures
- `kill -9` / forceful service stops

The killed ffmpeg leaves its `.tmp` file behind. On next boot, this service
finds the trail and re-runs the encode. Without it, the user would need to
play each affected item manually.

**Toggle:** Generation card → **Resume interrupted encodes** (default on).
Disable if you'd rather restart Jellyfin to abort a runaway bulk encode.

### 3.6 — Stale-encoder cleanup (prune phase 0)

**Trigger:** The prune scheduled task at 3 AM, OR the encoder tag in
`IframeFormat.cs` was bumped in a plugin upgrade (which makes every
existing cache stale).

**Service:** `PruneTrickplayCacheTask` (Phase 0 specifically)

**Flow:** evicts cache directories whose `.source-mtime` stamp doesn't
match the encoder tag the current build expects. After eviction, those
items will re-encode the next time something triggers them.

**This isn't an encode trigger by itself**, but it's a precondition for
re-encodes after an upgrade. We bump the encoder tag whenever the produced
output materially changed (e.g., we added the HDR tonemap, fixed the level
declaration, moved to single-stream output). Bumping invalidates all old
caches in one sweep so users don't have to manually clear anything.

---

## 4. Concurrency: how multiple encodes are handled

```
                         _inflight: ConcurrentDictionary<itemId, Lazy<Task<IframeAsset>>>
                         (de-duplication — same item requested twice → same task)
                                                │
                                                ▼
GetOrCreateAsync ─► GenerateAsync ─► await _globalLimit.WaitAsync(ct)
                                     (semaphore sized by MaxConcurrentGenerations)
                                                │
                                                ▼
                                        RunFfmpegAsync
                                                │
                                                ▼
                                  ffmpeg process (with hwaccel if configured)
                                                │
                                                ▼
                                        Mp4BoxScanner
                                                │
                                                ▼
                                  ProbePtsDeltasAsync (ffprobe pass)
                                                │
                                                ▼
                                  BuildPlaylist + atomic move + stamp write
```

**De-duplication:** Two callers asking for the same item ID share the same
Lazy task — only one ffmpeg runs even if 50 concurrent requests pile up.

**Concurrency limit:** `MaxConcurrentGenerations` (Generation card,
default `1`, max `8`). The semaphore caps how many ffmpeg processes can
run in parallel. With `1`, a queue of 100 items processes serially. With
`4`, four ffmpegs run at once and the rest wait their turn.

> Changing `MaxConcurrentGenerations` requires a Jellyfin restart — the
> semaphore is created at plugin load time.

**Process lifetime:** The ffmpeg cancellation token is the application
lifetime token, not the HTTP request token. Why this matters:
- AVPlayer disconnecting doesn't kill the ffmpeg
- The dashboard tab closing doesn't kill anything
- The encoder finishes whatever it's doing right up until Jellyfin shuts down

**Shutdown:** When Jellyfin stops, `ApplicationStopping` fires, the in-flight
ffmpegs are killed (`proc.Kill(entireProcessTree: true)`), and any partial
.tmp files survive on disk to be picked up by `StartupResumeService` on the
next boot.

---

## 5. Hardware acceleration

The encoder uses Jellyfin's globally-configured hardware acceleration
(Dashboard → Playback → Transcoding → Hardware acceleration). The plugin's
own **Use hardware decoding** toggle is a second gate on top of that.

| Jellyfin setting | What the plugin adds to ffmpeg |
|---|---|
| videotoolbox (macOS) | `-hwaccel videotoolbox -hwaccel_flags +low_priority` |
| qsv (Intel) | `-hwaccel qsv [-qsv_device …]` |
| vaapi (Linux open driver) | `-hwaccel vaapi [-vaapi_device …]` |
| nvenc (NVIDIA encoder) | `-hwaccel cuda -threads 1` |
| amf on Windows | `-hwaccel d3d11va -threads 2` |
| amf on Linux | `-hwaccel vaapi [-vaapi_device …]` |
| amf on macOS / unknown | software-only |

**Auto-fallback:** if hwaccel decode fails with a recognized handoff error
("Failed to transfer data to output frame", "Cannot use AVHWFramesContext",
etc.), the plugin retries the same encode with software decode. The user
sees a `[WRN]` line in the log; the encode still completes.

The encoder side (libx264) is always CPU. Hardware H.264 encoders don't
honor `keyint=1` reliably, and trickplay needs every frame to be IDR.

---

## 6. Pruning: how the cache stays bounded

`PruneTrickplayCacheTask` runs daily at 3 AM (configurable). Four phases,
each gated independently:

1. **Phase 0 — Stale / corrupt** (always on): evicts cache dirs where
   `TryGetCached` returns null. Catches old-encoder-tag entries and
   half-written `.tmp` orphans for items that no longer exist.
2. **Phase 1 — Orphans** (`PruneOrphans`, default on): evicts cache dirs
   whose item is no longer in the Jellyfin library.
3. **Phase 2 — Age-based** (`MaxAgeDays`, default 90, `0` disables):
   evicts cache entries unaccessed for N days.
4. **Phase 3 — Size cap** (`MaxCacheGigabytes`, default 0 / disabled):
   evicts least-recently-used entries until total size is under the cap.

`TryEvict` is in-flight aware — it skips items currently being encoded
(`_inflight` dictionary check) so eviction can never race regeneration.

---

## 7. The HLS injection layer

The plugin doesn't change Jellyfin's main video transcoding at all. It
intercepts only two endpoints:

- `GET /Videos/{id}/master.m3u8` — Jellyfin's multi-variant playlist.
  Plugin appends a single `#EXT-X-I-FRAME-STREAM-INF` line pointing at
  `iframe.m3u8`, leaving every other variant untouched.
- `GET /Videos/{id}/main.m3u8` — Jellyfin's single-variant playlist
  (used for stream-copy direct-stream HDR scenarios). Plugin wraps the
  response as a synthetic master playlist with one STREAM-INF (the
  original media playlist) + one I-FRAME-STREAM-INF.

For HDR/DV content, the server-side wrap is intentionally skipped — the
JellySeerTV iOS client builds its own synthetic master client-side via
`AVAssetResourceLoaderDelegate` so it can declare DV `SUPPLEMENTAL-CODECS`
correctly. The plugin's behavior is defensive: never emit a wrong
STREAM-INF that would break HDR playback.

The injection is via an `IAsyncResultFilter` registered globally; it
buffers the response into a `MemoryStream`, decides what to do based on
the body content (master vs media playlist), and writes back either
modified or original bytes. **Pass-through is the safe default** — any
ambiguity, the original playlist wins and trickplay just doesn't
appear for that item.

---

## 8. Reading the dashboard

The dashboard's three live cards reflect different parts of the system:

**Cache Status** (`GET /Plugins/NativeTrickplay/Status`):
- KPI tiles: cached items, bytes on disk, total library items, coverage %
- Per-library breakdown with progress bars
- Refresh button forces a re-fetch

**Encoding in progress** (`GET /Plugins/NativeTrickplay/Progress`):
- Polls every 2s when active, every 15s when idle
- Shows running items first (pinned to top), then queued in submission order
- Default view caps at 10 rows; "Show all (N)" expands to a scrollable list
- Auto-disappears when nothing is in flight

**Find & select items** (`GET /Plugins/NativeTrickplay/Items`):
- Tokenized search (case + accent insensitive), AND-matched across Name +
  SeriesName. Recognized S/E forms: `S6E18`, `1x05`, `Season 6`, `Episode 18`,
  `S7`, `E18`.
- Live search debounced at 280ms (no Search button needed)
- Cache filter (All / Cached / Not cached) and Sort order both live-refresh
- Per-row Generate / Regenerate / Evict buttons
- Bulk Generate selected / Evict selected
- Reactive row state synced from /Progress every 2s — running items show
  pulsing blue badge with live elapsed time and partial output bytes

---

## 9. What happens when …

| Scenario | Result |
|---|---|
| You upgrade the plugin | New version installs; old version keeps running until restart. On restart, ffmpeg gets killed mid-encode; .tmp files stay on disk. Auto-resume service fires 30s after boot and re-queues them. |
| Jellyfin crashes (OOM / panic) | Same as upgrade-restart. Auto-resume catches it. |
| Source video file is replaced | Stamp mtime no longer matches → next access invalidates the old cache → re-encode. |
| You change `IframeWidth` or `IframeIntervalSeconds` | Existing caches stay valid (encoder tag unchanged). New encodes use new settings. To regenerate everything at the new settings, run the Pre-generate task or evict + generate. |
| You change `MaxConcurrentGenerations` | Takes effect on next plugin load (Jellyfin restart). |
| You change `Cache directory` | Old caches at the old path are orphaned (delete manually if you want disk back). New encodes write to the new path. |
| You disable the plugin | Existing caches are preserved on disk. Master-playlist injection stops; AVPlayer falls back to no trickplay. Re-enable to pick up where you left off. |
| You evict an item via the dashboard | Cache directory deleted. Next access re-encodes from scratch. |
| Source has unusual codec (AV1 in MV) | hwaccel decode fails; plugin retries with software decode and succeeds. One `[WRN]` line in the log, encode completes. |
| Library scan adds 500 new items | They appear in /Items with `cached=false`; they're not encoded until something triggers them (next pre-gen run at 4 AM, manual Generate, or first playback). |

---

## 10. Where to look in the code

If you want to understand a specific behavior:

| You care about | Read this file |
|---|---|
| The encoder pipeline (filter chain, codec args) | `Hls/IframeAssetCache.cs` → `BuildFfmpegArgs` |
| Hardware acceleration mapping | `Hls/IframeAssetCache.cs` → `AppendHwaccelArgs` + `IsLikelyHwaccelFailure` |
| Cache validation logic | `Hls/IframeAssetCache.cs` → `TryGetCached` |
| Concurrency / dedup | `Hls/IframeAssetCache.cs` → `GetOrCreateAsync` (the `_inflight` dictionary) |
| Master-playlist rewriting | `Hls/MasterPlaylistInjector.cs` → `OnResultExecutionAsync` |
| HTTP API for the dashboard | `Api/IframeAdminController.cs` |
| HTTP API for AVPlayer | `Api/IframeHlsController.cs` |
| Playback-triggered warmup (with 30s defer) | `Hls/PlaybackWarmupService.cs` |
| Boot-time recovery of interrupted encodes | `Hls/StartupResumeService.cs` |
| Daily pre-generate task | `Tasks/PreGenerateIframeAssetsTask.cs` |
| Daily prune task (4 phases) | `Tasks/PruneTrickplayCacheTask.cs` |
| All the configurable knobs | `Plugin.cs` → `PluginConfiguration` |

The dashboard HTML lives in `Configuration/configPage.html` — single file
with all CSS and JS inline. Everything is scoped under
`#NativeTrickplayConfigPage` so it never leaks into the rest of Jellyfin.
