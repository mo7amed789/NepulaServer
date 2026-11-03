# Performance and structure review — 2026-11-02

## Follow-up: YouTube post-image CDN links

Direct image extraction recognizes extensionless `yt<number>.ggpht.com` and `yt<number>.googleusercontent.com` URLs. Recognized size/crop suffixes are replaced with `=s0`, with fallback to the supplied URL if unavailable. Response MIME is checked, HEAD can fall back to GET headers, and the existing safe outbound handler and image signature/size validation remain in use. Main-page image links open the image workspace with their URL queued.

The user's supplied URL passed live `/api/images/analyze` and `/api/images/download` checks: the resulting JPEG was **3071 × 4095**, **1,609,727 bytes**. Release build, 13 extractor checks, frontend validation, route/queue checks and live HTTP smoke checks passed. This adds direct CDN image-link support; extracting every image from arbitrary YouTube community-post page URLs is not established by this change. Originals are backed up in `Temp/youtube-images-backup`.

## Follow-up: faster trimmed downloads

Fast Mode now keeps yt-dlp's forced frame-accurate cut processing enabled. The local trim uses input-after-seek decoding and re-encodes only the selected interval; Fast Mode uses the ultrafast CRF 19 preset while normal mode uses veryfast CRF 19. Both modes honor the requested first and final timestamps. This costs some CPU compared with packet copy, but avoids leaking frames around keyframes.

Download results identify whether a range was already applied. Full-file fallbacks and playlist items now apply the requested trim before conversion or AI processing; already-cut single downloads are not trimmed again. MP3 trims are re-encoded because MP3 frame-copy cuts cannot be sample accurate.

The expanded Python suite checks both Fast Mode and normal mode against the requested duration. A local 640x360 fixture measured about **0.67 s** for Fast Mode accurate trimming and **0.70 s** for normal accurate trimming. This measures local processing, not internet download speed; external provider behavior remains untested. Pre-change files are in `Temp/trim-precision-backup`.

## Implemented findings

| Finding | Change | Verification |
| --- | --- | --- |
| Single-job requests loaded all history and deserialized every row | Primary-key `GetById` lookup in job endpoints | Missing-ID, migration, persistence and benchmark checks |
| Every save repeated schema work and replaced the whole row | Startup migrations, UPSERT, history index, WAL with FULL synchronization | Legacy database and reopen tests |
| Detached queue tasks were not awaited during shutdown | Fixed channel workers, cancellation synchronization, observed pipe readers, periodic idle cleanup | Real child-process concurrency/cancellation/shutdown checks |
| Completed playlist files were hashed twice | Reuse a single SHA-256 result | Stable item ID, hash, title and length checks |
| Media validation, codec inspection and duration launched separate probes | Shared bounded file-aware probe cache | Actual process call counts, retry and invalidation checks |
| Fast mode reduced video and AAC quality settings | Preserve quality targets; copy compatible streams independently | Real FFmpeg packet hash comparisons and encoding policy checks |
| Failed conversions could silently return incompatible input | Fail explicitly; retain hardware-to-software fallback | Invalid input and failure-path tests |
| Dashboard clients repeated process/drive/network queries | Shared synchronized resource sampling and brief IP cache | 1,000 concurrent telemetry reads |
| Hidden tabs polled and slow requests could overlap | Parallel, single-flight, visibility-aware polling with timeouts | Offline browser-script behavior checks |
| Gallery downloads were sequential | Three bounded workers; preserve original blobs and ordering | Concurrency, duplicate URL and partial failure checks |
| Page routes read and allocated complete HTML on every request | Physical-file results with ETag/Last-Modified | Live conditional GET returns empty 304; gzip matches source |
| Startup, endpoints and native UI shared one large file | Separate composition, web endpoints, desktop integration and hosted lifecycle | Release build, live smoke and lifecycle failure/shutdown checks |

## Measurements

These are local synthetic measurements, not total application/download speed claims. Timing varies with machine load, media, filesystem, network and cache state.

- 100 individual job lookups across 501 fixture rows: full-history scan **607.0 ms**, indexed lookup **6.5 ms**. Allocations: **122,251,992 → 417,600 bytes**.
- Three consumers of media probe information: **205.0 → 67.2 ms**, with subprocess count **3 → 1**.
- 10,000 telemetry reads in a separate microbenchmark: repeated OS queries **17,680.73 ms**, shared sampling **3.66 ms**. This intentionally measures cache reuse within the sampling interval.
- Live `/app`: **41,231 bytes**, gzip **16,845 bytes**. Live `/images`: **15,999 bytes**, gzip **7,303 bytes**. Both passed ETag revalidation and source-content checks.

## Validation and limits

- Release build: zero warnings and errors.
- NuGet direct/transitive vulnerability audit: no known vulnerable packages reported by the configured sources on this date; this is not a full security audit.
- 15 queue/store regression checks, 11 Python/FFmpeg tests, four lifecycle scenarios, frontend behavior and minified-asset validation passed.
- Live isolated HTTP host: health, recovery, dashboard, version, pages, static assets, gzip, ETags and Python module copies passed.
- Exact packet preservation was checked for copied video/audio streams. This does not establish perceptual equivalence between different hardware encoders or make lossy re-encoding lossless.
- No external website download, actual AI model inference, complete publish/installer, Android rebuild or interactive Photino/tray QA was performed in this pass. Native window behavior was preserved by extraction, but still needs an interactive check on the target desktop.
- Runtime launch checks use temporary state; the user's existing database and downloads were not migrated or changed by testing. The WAL migration occurs when the updated application first opens its normal database.
- Existing LAN authentication behavior is preserved. This review does not make the LAN build suitable for public internet exposure.

## Recovery and further work

Local Git metadata is not a usable checkout (`git status` fails). Edited originals are saved under `Temp/refactor-backup`; new files are listed in the architecture map and verification commands. No commit, deployment or installer replacement was made.

Further measurement should use representative large playlists, 1080p/4K sources, slow disks, and simultaneous clients before increasing concurrency. Pagination for very large history lists, cross-job encoder-benchmark reuse and more granular job orchestration extraction remain sensible follow-up candidates; they were not needed for the verified bottlenecks addressed here.
