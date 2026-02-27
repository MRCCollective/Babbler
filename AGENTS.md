# AGENTS.md

Guidance for coding agents working in this repository.

## Scope

Applies to the entire repo.

## Project Overview

- Solution: `Babbler.sln`
- Web project: `Babbler.Web`
- Runtime: `.NET 9`
- Frontend: static HTML in `Babbler.Web/wwwroot`
- Realtime transport: SignalR hub at `/hubs/translation`

## Core Product Behavior (Do Not Break)

- Multi-room support with room IDs and PINs
- PIN-gated access to `/display.html`
- Room-specific SignalR groups
- Browser-side microphone capture from `index.html`
- Server-side speech token issuance via `/api/speech/token`
- Monthly free-minute quota enforcement

Do not reintroduce server microphone capture in backend services.

## Language/Translation Rules

- Keep Swedish support (`sv`) in target languages
- Preserve existing source language options unless explicitly changed
- Target language switching must stay room-scoped

## Security and Secrets

- Never commit real secrets in `appsettings.json` or `appsettings.Development.json`
- Use Azure App Settings for production secrets:
  - `Speech__Key`
  - `Speech__Region`
  - `BitStore__*` if enabled
- Keep token endpoint short-lived and server-issued

## Local Commands

```powershell
dotnet build
dotnet run --project Babbler.Web --urls http://localhost:5091
```

## Deployment Files

- `DEPLOYMENT.md`
- `scripts/azure/provision-appservice.ps1`
- `scripts/azure/deploy-webapp.ps1`
- `.github/workflows/deploy-azure-webapp.yml`

If deployment behavior changes, update these files together.

## Validation Checklist for Changes

- `dotnet build` succeeds
- Room create/start/stop endpoints still work
- `index.html` can start translation and trigger mic permission prompt
- `join.html` PIN verification still gates `display.html`
- SignalR updates still arrive in display clients
