# Native Trickplay for Jellyfin

Native HLS trickplay for AVPlayer-based clients (Apple TV / iOS Jellyfin
clients built on AVKit). Adds proper system scrubbing thumbnails on the
transport bar — no client-side overlay, no custom UI work.

## Why

`AVPlayerViewController` consumes `#EXT-X-I-FRAME-STREAM-INF` declarations
in HLS multivariant playlists for transport-bar scrubbing thumbnails. Jellyfin
doesn't emit them. This plugin generates an I-frame fMP4 per item (one ffmpeg
run, cached forever) and rewrites the HLS playlists Jellyfin already serves
to advertise the I-frame variant.

The client needs zero changes. As long as your client fetches `master.m3u8`
or `main.m3u8` from `/Videos/{id}/...`, it just starts working.

## Install

### Option 1 — Plugin repository (recommended, gets auto-updates)

1. In Jellyfin: **Dashboard → Plugins → Repositories → +**
2. Repository name: `Native Trickplay`
3. Repository URL: `https://raw.githubusercontent.com/HarshLabs/jellyfin-plugin-native-trickplay/main/manifest.json`
4. **Save**, then go to **Catalog**, find **Native Trickplay**, click **Install**
5. Restart Jellyfin (Dashboard → top-right ⚙ → **Restart**)

### Option 2 — Manual

Download the latest `*.zip` from [Releases](../../releases), extract into
`<jellyfin-data>/plugins/`, restart Jellyfin.

## Configuration

After install: **Dashboard → Plugins → Native Trickplay**

- **Generation:** thumbnail height (default 320 px), interval seconds
  (default 1), CRF (default 32), x264 preset (default ultrafast),
  concurrent encodes (default 1, max 8), warmup delay seconds (default
  30, range 5–300), use hardware decoding (follows Jellyfin's global
  hwaccel setting)
- **Cache pruning:** orphan removal (default on), age-based eviction
  (default 90 days, `0` to disable), LRU size cap (`0` = disabled)
- **Auto-resume interrupted encodes** (default on)

The pruner runs daily at 3 AM, the pre-generate task at 4 AM. Both are
adjustable in **Dashboard → Scheduled Tasks**. Run on demand from the
same page.

## How it works (short version)

1. **Encoder** — for each item, one ffmpeg invocation: hardware decode
   (videotoolbox / qsv / vaapi / cuda / d3d11va / drm / rkmpp,
   auto-fallback to software on failure) → `fps=N` thinning → HDR→SDR
   tonemap (Hable, only for HDR sources) → 4:2:0 8-bit downscale →
   libx264 all-IDR Main Level 4.0 (`avc1.4d0028`) → fragmented MP4.
   One I-frame per `IframeIntervalSeconds` of source. Cached under
   `<jellyfin-cache>/native-trickplay/<itemId>/` as `iframe.m4s` +
   `iframe.m3u8` + `.source-mtime` stamp.
2. **HLS injection** — an MVC result filter intercepts `master.m3u8`
   (appends a single `#EXT-X-I-FRAME-STREAM-INF` line) and `main.m3u8`
   (wraps the response as a synthetic master for SDR; passes through
   for HDR/DV so the client can build its own DV-aware master).
3. **Cache-state header** — `iframe.m3u8` responses carry
   `X-Trickplay-Status: ready|encoding`. HDR-aware clients HEAD-probe
   it before declaring the I-frame variant in their synthetic master,
   avoiding tvOS 26's `kCMHTTPLiveStreamingMissingMandatoryKey` (-12642)
   on cold caches.
4. **Triggers** — playback start (deferred warmup), dashboard
   Generate/Regenerate, daily 4 AM pre-gen task, startup auto-resume of
   interrupted encodes. Priority queue: playback High, admin/pre-gen
   Normal, startup-resume Normal — playback always leapfrogs background
   work.

For the deep dive (every trigger, the priority queue, all the failure
modes, all the configuration knobs), read
[`docs/HOW_IT_WORKS.md`](docs/HOW_IT_WORKS.md).

## Compatibility

- **Jellyfin:** 10.11.x (target ABI `10.11.0.0`)
- **Runtime:** .NET 9 (matches Jellyfin 10.11)
- **FFmpeg:** any reasonably recent build (uses `-skip_frame`,
  `+frag_keyframe`, libx264)
- **Clients:** any AVPlayer-based client (Apple TV / iOS / iPadOS /
  visionOS / macOS native players). Browsers / Android / Roku won't
  benefit (they have their own trickplay paths via Jellyfin's existing
  BIF/JPEG sprite trickplay).

## Build from source

Requires the .NET 9 SDK.

```sh
dotnet build Jellyfin.Plugin.NativeTrickplay/Jellyfin.Plugin.NativeTrickplay.csproj -c Release
```

Or use the included `build.sh` to build, package, and drop into a local
Jellyfin install (macOS path baked in — adjust for your OS).

## License

MIT — see [LICENSE](LICENSE).
