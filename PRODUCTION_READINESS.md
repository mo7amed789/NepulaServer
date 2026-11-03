# Production readiness

## Secure configuration

- API authentication is intentionally disabled in the current LAN build. Pairing can still issue a bearer token for client compatibility, but API and SignalR routes do not enforce that token or the pairing header.
- Treat port `Server:Port` as an unauthenticated LAN service. Restrict it with Windows Firewall to trusted networks and do not expose it directly to the internet.
- `Auth:JwtSecret` is intentionally empty in the checked-in settings. On first start the server creates a random signing secret at `C:\ProgramData\Tanzeel\jwt.secret`; this does not protect routes while authentication middleware remains disabled.
- For managed deployments, provide `Auth__JwtSecret` through a secret store or environment variable. It must be at least 32 characters.
- Do not copy `C:\ProgramData\Tanzeel\jwt.secret`, `pairing.json`, `device_id.txt`, `cookies.txt`, or the `Logs` directory into source control or a public artifact.

## Reverse proxy

Forwarded headers are ignored unless the proxy IP is explicitly configured:

```json
{
  "ForwardedHeaders": {
    "KnownProxies": ["127.0.0.1"]
  }
}
```

Use HTTPS at the proxy and restrict the server port to only the proxy or trusted LAN clients that need it. The current build does **not** require pairing or bearer authentication for non-loopback API requests. Re-enable `UseAuthentication`, `UseAuthorization`, endpoint authorization, and pairing validation before describing an internet-facing deployment as authenticated.

## Licensing

The current repository does not contain a real license verifier. Release activation therefore fails closed instead of accepting arbitrary text. Connect `LicenseService` to the licensing API or the bundled public-key verification flow before offering paid production activation.

Only `Keys\public.pem` may be copied to build and publish output. `Keys\private.pem`, `private_key.pem`, and generated `device_id.txt` files are explicitly excluded and deleted from reused publish directories. If a release containing a private key was distributed, revoke or retire that key in the licensing service, generate a new signing key pair outside the repository, deploy only the new public key, and keep the new private key in an access-controlled signing service or secret store.

## Build and publish

```powershell
dotnet restore NebulaServer.csproj
dotnet build NebulaServer.csproj -c Release --no-restore
dotnet publish NebulaServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o C:\Deploy\NebulaServer
```

Smoke-test `/api/health`, `/api/system/ready`, `/app`, image URL validation, playlist range downloads, and a pairing request after installation. Confirm the configured `Server:Port` appears in pairing and QR payloads. Inspect the final publish directory and verify that it contains neither a private PEM key nor `device_id.txt`. Runtime state is stored under `C:\ProgramData\Tanzeel` so upgrades do not overwrite it.
