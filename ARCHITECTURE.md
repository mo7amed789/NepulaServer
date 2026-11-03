# NebulaServer architecture

The application uses ASP.NET Core 10 for HTTP and SignalR, a Photino/WebView2 desktop shell, SQLite for job persistence, and a Python/yt-dlp/FFmpeg media pipeline. This refactor retains those boundaries and API routes.

## Ownership

| Component | Responsibility |
| --- | --- |
| `Program.cs` | Process configuration, single instance, host assembly and desktop/headless lifetime |
| `Extensions/WebServiceCollectionExtensions.cs` | HTTP infrastructure: token configuration, compression, forwarded headers, request limits |
| `Extensions/ServiceCollectionExtensions.cs` | Application dependency registration |
| `Web/EndpointRouteBuilderExtensions.cs` | Browser pages, pairing/setup endpoints, cache validators |
| `Desktop/DesktopApplication.cs` | STA window, tray menu, Windows integration |
| `Services/Core/CoreLifecycleService.cs` | Wait for host readiness, recover jobs, start/observe/stop core workers |
| `Services/JobQueueManager.cs` | Fixed asynchronous channel workers, job transitions, periodic cleanup |
| `Services/Jobs/JobStore.cs` | SQLite schema migration, durable writes, indexed job reads |
| `Services/Dashboard/DashboardResourceMonitor.cs` | Shared synchronized OS telemetry sampling |
| `Python/nebula_engine.py` | Download and processing orchestration, existing process protocol |
| `Python/nebula/probe.py` | Bounded file-change-aware FFprobe cache |
| `Python/nebula/encoding.py` | Encoder capability benchmark and quality targets |

HTTP listening starts before job recovery. The host's ApplicationStarted event replaces polling the application's own HTTP endpoint. The lifecycle service observes worker failure, and shutdown awaits worker cancellation and child-process cleanup. Media concurrency remains configurable through `Performance:MaxConcurrentDownloads`.

## Quality and performance policy

- Copy compatible H.264/AAC streams whenever possible. Each stream type is considered independently, so converting audio does not re-encode compatible video and converting video preserves compatible audio.
- Fast mode uses the same numerical video quality target (19) and converted AAC bitrate (192 kbps) as the normal path. Faster presets can produce larger files. Different encoders' quality scales are not equivalent; re-encoding remains lossy.
- User-selected download resolution, audio bitrate and data saver settings remain effective.
- Benchmark working hardware encoders before selecting them; retry failed hardware conversion with software encoding. A short synthetic benchmark is not a guarantee of the fastest encoder for every source.
- Cache file probes by executable, full path, size and timestamps; retain at most 256 entries and never cache failures.
- SQLite uses WAL and `synchronous=FULL`. Do not reduce durability merely to improve a benchmark. Back up a live WAL database through SQLite's backup mechanism, or stop the application before copying its state.
- OS telemetry is sampled at 500 ms (process) and 5 seconds (drive/IP). Job state remains live. Hidden browser tabs suspend status polling and resume immediately on visibility.

## Development and verification

```powershell
dotnet build NebulaServer.csproj -c Release --no-restore
dotnet run --project Tools/QueueChecks/QueueChecks.csproj -c Release
dotnet run --project Tools/LifecycleChecks/LifecycleChecks.csproj -c Release
.\Python\python.exe Tools/test_media_engine.py
node Tools/test-dashboard-web.mjs
node Tools/validate-web.mjs
.\Python\python.exe Tools/test-server-smoke.py
```

The server smoke test launches the Release executable on a temporary loopback port with an isolated database and terminates only that test process afterward. Graceful lifecycle and queue shutdown are checked separately. `--headless` runs the HTTP host without the desktop window or startup-registration repair. `TANZEEL_DATA_DIR` selects an alternate state directory; normal installations continue using `C:\ProgramData\Tanzeel`. Alternate state directories get an independent single-instance mutex.

After changing HTML/CSS/JS, regenerate the served minified assets with `Tools/minify-web.ps1`. New application Python modules under `Python/nebula` are included in normal build output and publish output. The complete Python runtime is copied at publish time. Temporary verification directories, Android sources, and test harness sources are excluded from the server's default source discovery.

## Technology decisions

.NET 10 is an active LTS platform, so the current framework is an appropriate base; this work does not replace it with another web framework. See [Microsoft's support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). Shared resource sampling, smaller queries, asynchronous workers and fewer blocking operations follow [ASP.NET Core performance guidance](https://learn.microsoft.com/aspnet/core/fundamentals/best-practices).

SQLite fits a local desktop server and avoids adding a separate database service. Channels provide explicit asynchronous producer/consumer coordination using the existing runtime; see [Microsoft's channel documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels). FFmpeg stream copy avoids decoding and encoding and preserves the copied stream's quality; see [FFmpeg's documentation](https://ffmpeg.org/ffmpeg-all.html). Encoder changes or a new native engine should be justified by representative workload benchmarks and quality measurements, rather than assumed to improve performance.
