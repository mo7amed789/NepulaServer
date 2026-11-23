# Tanzeel native desktop redesign

The desktop uses Windows Forms controls and the existing media services directly. No embedded browser is required. The six-page Tanzeel Enterprise Redesign brief supplied on November 2, 2026 is the visual reference; its sample jobs and statistics are not production data.

Implemented: IBM Plex Sans/Mono bundled privately with their OFL license, dark navigation, breadcrumbs, responsive cards, guided source/format flow, metadata quality choices, live size/format summary, queue actions with disabled states, status badges and progress bars, media thumbnails, image selection gallery, runtime/queue overview, pairing QR, settings toggles and section links.

Thumbnails are loaded asynchronously with four concurrent requests, a 20-second timeout, a 10 MB transfer limit, public-address validation, and cancellation when controls are replaced or disposed. Older requests cannot replace a newer selection. Unsupported image formats show a fallback without blocking downloads. Get media info retries a failed thumbnail.

Run `dotnet run --project Tools/DesktopChecks -c Release -- <render-directory>` for native layout, option validation, retention and live HTTPS thumbnail checks. This check uses a separate data directory and renders both 1180px and 960px windows. It needs internet access for the real thumbnail check. Run `Python/python.exe Tools/test-server-smoke.py` for the existing server smoke checks.

Launch `bin/Release/net10.0-windows/win-x64/NebulaServer.exe`. Keep the output folder intact. A development build resolves the Python runtime from this repository; use `dotnet publish -c Release` to package the bundled runtime for another computer.

The word Enterprise describes the requested redesign. This change does not certify the application for organizational deployment, add SSO, or change the existing LAN authentication policy.
