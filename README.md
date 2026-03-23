# Power BI Windows Validation Service — ASP.NET Core .NET 8

Service Windows spécialisé **validation only** pour le track PowerBI local-first.
Le moteur principal de conversion appartient au service local Linux. Ce repo n'est **pas** le backend de conversion.

## Rôle canonique

Ce service expose uniquement :
- `GET /health`
- `POST /v1/validate`
- `GET /v1/validation-status/{jobId}`
- `GET /v1/validation-report/{jobId}`
- routes admin `/admin/clients` pour gérer les clés client

## Contrat d'auth

| Surface | Header requis |
|---|---|
| `GET /health` | aucun |
| `/admin/*` | `X-Admin-Key: <ADMIN_API_KEY>` |
| `/v1/*` | `X-API-Key: <client-api-key>` |

`X-Admin-Key` peut aussi être utilisé par un opérateur pour appeler `/v1/*` en diagnostic.

## Contrat de validation

### Health
```http
GET /health
→ 200
{
  "status": "ok",
  "service": "powerbi-windows-validator",
  "role": "validation-only",
  "version": "2.0.0"
}
```

### Lancer une validation
```http
POST /v1/validate
X-API-Key: client-key
Content-Type: application/json

{
  "artifact_path": "C:\\PbiBridge\\workspaces\\acme\\artifacts\\report.pbix",
  "validator": "contract-check",
  "options": {}
}
```

Réponse :
```json
{
  "job_id": "...",
  "validation_status": "queued",
  "message": "Validation job queued."
}
```

### Suivre une validation
```http
GET /v1/validation-status/{jobId}
X-API-Key: client-key
```

Réponse :
```json
{
  "job_id": "...",
  "validation_status": "succeeded",
  "validator": "contract-check",
  "created_at": "2026-03-23T17:00:00Z",
  "started_at": "2026-03-23T17:00:01Z",
  "completed_at": "2026-03-23T17:00:02Z",
  "error": null
}
```

### Récupérer le rapport
```http
GET /v1/validation-report/{jobId}
X-API-Key: client-key
```

Réponse :
```json
{
  "job_id": "...",
  "validation_status": "failed",
  "validator": "contract-check",
  "artifact_path": "C:\\PbiBridge\\workspaces\\acme\\artifacts\\report.pbix",
  "summary": "Validation failed — unsupported artifact shape.",
  "started_at": "2026-03-23T17:00:01Z",
  "completed_at": "2026-03-23T17:00:02Z",
  "error": "Supported validation inputs are .pbix, .pbip, .zip, or a non-empty artifact directory.",
  "fallback_non_blocking": true,
  "conversion_status_impact": "none",
  "checks": [
    {
      "name": "artifact_format",
      "status": "failed",
      "detail": "Unsupported extension '.txt'."
    }
  ]
}
```

## Statuts canoniques

Le service retourne les statuts ADR-003 pour `validation_status` :
- `not_requested`
- `queued`
- `running`
- `succeeded`
- `failed`
- `unavailable`
- `skipped`

En pratique ce repo produit surtout `queued`, `running`, `succeeded`, `failed`, `unavailable`.
`not_requested` et `skipped` restent des états d'intégration légitimes côté hub/workspace.

## Fallback non bloquant

Règle non négociable : **une validation `failed` ou `unavailable` n'annule jamais une conversion déjà réussie ailleurs**.
Le rapport retourne toujours :
- `fallback_non_blocking: true`
- `conversion_status_impact: "none"`

Le cas `unavailable` couvre par exemple :
- runtime Power BI Desktop non disponible ;
- backend MCP indisponible ;
- timeout de validation.

## Validateurs supportés par le contrat actuel

| Validator | Effet |
|---|---|
| `contract-check` | Vérifie la présence et la forme minimale de l'artefact dans le sandbox client |
| `powerbi-desktop` | Retourne `unavailable` tant que le runner Desktop n'est pas branché sur ce runtime |
| `mcp` | Retourne `unavailable` tant que le runner MCP n'est pas branché sur ce runtime |

## Admin clients

### Enregistrer une clé client
```http
POST /admin/clients
X-Admin-Key: <ADMIN_API_KEY>
Content-Type: application/json

{
  "clientId": "acme",
  "apiKey": "acme-key-123"
}
```

### Lister les clients
```http
GET /admin/clients
X-Admin-Key: <ADMIN_API_KEY>
```

### Révoquer une clé client
```http
DELETE /admin/clients/acme
X-Admin-Key: <ADMIN_API_KEY>
```

## Développement local

```bash
export ADMIN_API_KEY=test-admin-key
cd PbiBridgeApi
DOTNET_ENVIRONMENT=Development dotnet run
```

## Build / tests

```bash
dotnet test
dotnet build
```

## Notes d'intégration EPIC-5

- Ce repo reste borné à la validation spécialisée.
- `conversion_status` n'appartient pas à ce repo.
- Le hub EPIC-4 consomme déjà `validation_status` comme dimension séparée et non bloquante.
