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

- **Generation:** thumbnail width (default 480px), CRF (quality, default 30)
- **Cache pruning:** orphan removal (default on), age-based eviction
  (default 90 days, 0 to disable), LRU size cap (default disabled)

The pruner runs daily at 3 AM by default. You can run it on demand from
**Dashboard → Scheduled Tasks → Native Trickplay → Prune Native Trickplay
Cache → ▶**.

## How it works

1. **Asset generator** — first time any item is scrubbed, ffmpeg pulls
   keyframes from the source (`-skip_frame nokey`), encodes them as 480p
   H.264 Main 3.0 fMP4 with one fragment per frame, caches to disk under
   `<jellyfin-cache>/native-trickplay/<itemId>/`.
2. **Box scanner** — a tiny C# walker computes byte offsets per fragment so
   the playlist's `#EXT-X-BYTERANGE` lines are correct. (FFmpeg's HLS
   muxer's `iframes_only` flag only adds the tag — it doesn't compute
   correct byte ranges. Don't be misled.)
3. **HLS playlist injector** — an MVC result filter intercepts both
   `master.m3u8` and `main.m3u8` responses. Master playlists get an
   `#EXT-X-I-FRAME-STREAM-INF` line appended. Media playlists (which
   Jellyfin returns for `main.m3u8`) get wrapped in a synthetic master that
   references the inner media playlist (with `?...&skipIframeInjection=1`
   to avoid recursion) plus the I-frame variant. AVPlayer is none the wiser.

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
