# PBI Bridge API — ASP.NET Core .NET 8 Windows Service

ADR-002: Centralized multi-client Power BI migration service.

## Architecture
- VM Linux (agent) + shared Windows VM (PBI Bridge API)
- Port 8090 internal VNet only — NEVER exposed publicly
- X-API-Key authentication on all routes except GET /health

## Quick Start

### Requirements
- .NET 8 SDK
- Windows Server (for production deployment)

### Build
```bash
dotnet build PbiBridgeApi/PbiBridgeApi.csproj
```

### Run (development)
```bash
dotnet run --project PbiBridgeApi/PbiBridgeApi.csproj
# API available at http://localhost:8090
```

### Health check
```bash
curl http://localhost:8090/health
# {"status":"ok","version":"1.0.0"}
```

### Install as Windows Service
```powershell
# See scripts/ directory (added in S2.5)
.\scripts\install.ps1
```

## Rules (DA-012 to DA-017)
| Rule | Description |
|------|-------------|
| DA-012 | Port 8090 ONLY — never change |
| DA-013 | X-API-Key required on all routes except GET /health |
| DA-014 | Strict isolation by client_id |
| DA-015 | tableau2pbi via subprocess — do NOT re-implement |
| DA-016 | Auto-cleanup jobs > 48h |
| DA-017 | ADMIN_API_KEY via env var — never hardcode |

## Stories
- S2.1: Bootstrap (this PR)
- S2.2: X-API-Key auth middleware
- S2.3: JobManager isolation + 48h cleanup
- S2.4: ConversionController + subprocess tableau2pbi
- S2.5: Windows Service install scripts
