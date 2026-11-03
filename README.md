# NepulaServer

NepulaServer is a self-hosted media management backend built with ASP.NET Core. It provides a reliable API and web dashboard for media extraction, download queues, streaming transfers, device pairing, image retrieval, health monitoring, and license-aware deployments.

The project is designed for local-first operation: run it on a Windows server or workstation, connect clients over the local network, and keep downloaded media under your control.

## Highlights

- Media extraction and downloads through a unified API
- Background job queue with progress, snapshots, retries, and transfer events
- YouTube and public web media workflows, with authenticated-source extension points
- Image extraction and cached image delivery for supported media pages
- Browser dashboard, setup flow, pairing page, and server-state endpoints
- Device pairing and local-network client support
- Licensing and activation services for managed deployments
- Recovery, startup, health, and runtime monitoring services
- Optional Android companion client under `NebulaMobileAndroid`
- Python media helpers under `Python/nebula`

## Architecture

NepulaServer is a modular ASP.NET Core application. Controllers expose the HTTP API, services manage jobs and transfers, and the web assets in `Html` provide the browser experience.

```text
Client / Dashboard / Android
            |
       ASP.NET Core API
            |
  Queue + Media + Image Services
            |
   Storage / FFmpeg / Python helpers
```

The main API areas include:

| Area | Routes |
| --- | --- |
| Jobs and transfers | `/api/jobs`, `/api/v1/jobs` |
| Media extraction | `/api/media`, `/api/v1/media` |
| Image extraction | `/api/images` |
| System and health | `/api/system/*`, `/api/health` |
| Dashboard | `/api/dashboard` |
| Pairing | `/api/pairing`, `/pairing` |

## Requirements

- Windows 10 or later for the current desktop-oriented target
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFmpeg and FFprobe available to the runtime when media conversion is required
- Python 3.11 when using the optional Python media helpers
- Android Studio for the companion Android project

## Run locally

Clone the repository and start the server:

```powershell
git clone https://github.com/mo7amed789/NepulaServer.git
cd NepulaServer
dotnet restore
dotnet run --project .\NebulaServer.csproj
```

The application serves its browser experience from `/app`. The root path redirects there automatically.

For a release build:

```powershell
dotnet build .\NebulaServer.csproj --configuration Release
```

Useful local endpoints:

```text
http://localhost:5000/app
http://localhost:5000/api/health
http://localhost:5000/api/system/ready
http://localhost:5000/pairing
```

The port can be changed through configuration or the `Server__Port` environment variable.

## Configuration

Use `appsettings.json` for non-sensitive defaults and environment variables or a local configuration provider for deployment-specific values. Never commit private signing keys, JWT secrets, access tokens, downloaded media, or machine-specific identity files.

Example:

```powershell
$env:Server__Port = "5000"
$env:Server__PublicUrl = "https://example.com"
dotnet run --project .\NebulaServer.csproj
```

Review the startup and licensing services before exposing the API outside a trusted network. Put public deployments behind HTTPS and a properly configured reverse proxy.

## Android companion

The optional Android client is located in `NebulaMobileAndroid`:

```text
Open NebulaMobileAndroid in Android Studio and run the app on an emulator or device.
```

The Android client communicates with the server over the configured local-network address and uses the pairing flow to establish the connection.

## Development

Build the server before opening a pull request:

```powershell
dotnet restore
dotnet build .\NebulaServer.csproj --configuration Release
```

Keep generated folders such as `bin`, `obj`, build caches, Python virtual-environment files, and local Android configuration out of commits. Add focused tests or smoke checks under `Tools` when changing queue, media, image, or lifecycle behavior.

## Security

- Do not commit `Keys/private.pem`, JWT secrets, credentials, or API tokens.
- Restrict the server to trusted interfaces until authentication and HTTPS are configured.
- Validate and limit outbound media URLs and downloaded content.
- Store downloads and runtime state outside the source tree in production.
- Review license and pairing behavior before enabling public access.

## License

No open-source license has been declared in this repository. All rights are reserved unless a separate written license says otherwise.

## Maintainer

Maintained by [Mohamed Jamal](https://github.com/mo7amed789).

