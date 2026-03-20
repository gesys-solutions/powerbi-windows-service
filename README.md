# PBI Bridge API — ASP.NET Core .NET 8 Windows Service

ADR-002: Centralized multi-client Power BI migration service.

## Architecture
- VM Linux (agent) + shared Windows VM (PBI Bridge API)
- Port 8090 internal VNet only — **NEVER exposed publicly** (DA-012)
- X-API-Key authentication on all routes except `GET /health` (DA-013)
- Client isolation by `client_id` — one client never sees another's jobs (DA-014)
- `tableau2pbi` invoked via subprocess — logic never re-implemented (DA-015)
- Auto-cleanup of jobs older than 48h (DA-016)
- `ADMIN_API_KEY` in environment variable — never hardcoded (DA-017)

---

## Prerequisites

| Requirement | Version | Notes |
|---|---|---|
| Windows Server | 2019+ | Windows 10/11 also supported |
| .NET 8 Runtime | 8.x | [Download](https://dotnet.microsoft.com/download/dotnet/8.0) |
| Python | 3.10+ | Required for `tableau2pbi` subprocess |
| NSSM | Optional | Preferred for production — [nssm.cc](https://nssm.cc/). Falls back to `sc.exe`. |

---

## Quick Install

Run as Administrator in PowerShell:

```powershell
# Minimal install (port 8090, install to C:\Services\PbiBridgeApi\)
.\scripts\install.ps1 -AdminApiKey "your-secret-admin-key"

# Custom install directory
.\scripts\install.ps1 -AdminApiKey "your-secret-admin-key" -InstallDir "D:\Services\PbiBridgeApi\"
```

The script will:
1. Verify .NET 8 is installed
2. Publish the app (`dotnet publish --configuration Release --runtime win-x64`)
3. Create the Windows Service named `PbiBridgeApi` (auto-start, LocalSystem account)
4. Set `ADMIN_API_KEY` as a service environment variable
5. Start the service
6. Verify `GET http://localhost:8090/health` returns 200

---

## Update

```powershell
# Update to latest code (fetches from current working directory)
.\scripts\update.ps1

# With custom install dir
.\scripts\update.ps1 -InstallDir "D:\Services\PbiBridgeApi\"
```

---

## Uninstall

```powershell
# Stop and remove service (keeps files)
.\scripts\uninstall.ps1

# Stop, remove service, and delete deploy files
.\scripts\uninstall.ps1 -RemoveFiles -InstallDir "D:\Services\PbiBridgeApi\"
```

---

## Environment Variables

| Variable | Required | Description |
|---|---|---|
| `ADMIN_API_KEY` | **Yes** | Admin key for managing client API keys (register/revoke) |
| `ASPNETCORE_ENVIRONMENT` | No | Default: `Production` |

**Security**: The `ADMIN_API_KEY` is stored as a service environment variable (via NSSM or Windows registry). It is **never** hardcoded in code or config files.

---

## API Endpoints

### Health (no auth required)
```
GET http://localhost:8090/health
→ 200 {"status":"ok","version":"1.0.0"}
```

### Client API Key Management (ADMIN_API_KEY required)
```
POST   /admin/keys          Register a new client API key
DELETE /admin/keys/{key}    Revoke a client API key
GET    /admin/keys          List active client keys
```

### Conversion (client API key required)
```
POST /v1/migrate            Start a Tableau → Power BI migration job
GET  /v1/status/{jobId}     Get job status/result
```

---

## Troubleshooting

### Service doesn't start
1. Open **Event Viewer** → Windows Logs → Application → filter by Source `PbiBridgeApi`
2. Ensure .NET 8 Runtime is installed: `dotnet --version`
3. Ensure `ADMIN_API_KEY` is set — if missing, the app refuses to start
4. Check port 8090 is not already in use: `netstat -an | findstr 8090`

### Health check fails after install
- Wait 5–10 seconds — the service may still be initializing
- Check Windows Firewall if accessing from another VM (port 8090 must be open on the VNet interface)
- Run `Get-Service PbiBridgeApi` — verify status is `Running`

### NSSM vs sc.exe
- **NSSM** (preferred): better restart behavior, stdout/stderr logging, env var support
- **sc.exe** (fallback): built-in Windows, env vars set via registry, no automatic restart on crash

### Python subprocess errors
- Ensure Python 3.10+ is installed and in the system PATH for the `LocalSystem` account
- `tableau2pbi` must be installed: `pip install tableau2pbi` (or see `tableau2pbi` repo)
- Test: open a CMD window as SYSTEM and run `python --version`

### Updating ADMIN_API_KEY
Re-run `install.ps1` with the new key, or manually update via NSSM:
```powershell
nssm set PbiBridgeApi AppEnvironmentExtra "ADMIN_API_KEY=new-key" "ASPNETCORE_ENVIRONMENT=Production"
Restart-Service PbiBridgeApi
```

---

## Stories
| Story | Description | Status |
|---|---|---|
| S2.1 | Bootstrap repo + ASP.NET Core .NET 8 structure + `GET /health` | ✅ Done |
| S2.2 | X-API-Key auth middleware (per-client, ADMIN_API_KEY) | ✅ Done |
| S2.3 | JobManager isolation by `client_id` + 48h cleanup | ✅ Done |
| S2.4 | ConversionController `POST /v1/migrate` + subprocess `tableau2pbi` | ✅ Done |
| S2.5 | Windows Service install scripts (NSSM/sc.exe) + this README | ✅ Done |

---

## Rules (DA-012 to DA-017)
| Rule | Description |
|---|---|
| DA-012 | Port 8090 ONLY — never change |
| DA-013 | X-API-Key required on all routes except `GET /health` |
| DA-014 | Strict isolation by `client_id` — clients never see each other's jobs |
| DA-015 | `tableau2pbi` via subprocess — do NOT re-implement the logic |
| DA-016 | Auto-cleanup jobs older than 48h |
| DA-017 | `ADMIN_API_KEY` via env var — never hardcode |
