# AGENTS.md

Repository guidance for coding agents.

## Scope

Applies to the full repository.

## Architecture Guardrails

- Solution: `Babbler.sln`
- Main app: `Babbler.Web`
- Runtime: `.NET 9`
- Frontend: static HTML/JS in `Babbler.Web/wwwroot`
- Realtime transport: SignalR hub `/hubs/translation`
- Speech recognition happens in browser (Azure Speech JS SDK), not server

Do not move microphone capture into backend.

## Product Behaviors That Must Not Break

- Multi-room model (room ID + PIN)
- PIN-gated display access (`/join.html` -> `/display.html`)
- Room-scoped SignalR updates
- Room start/stop + target language switching
- Monthly free-minute usage enforcement
- Swedish support (`sv`) in supported targets

## Change Discipline

- Keep fixes minimal and reversible.
- Avoid adding automatic restart/fallback loops unless explicitly requested.
- If diagnostics are added, keep them low-noise and easy to remove.
- If user asks to revert behavior, prefer a focused revert commit.

## Secrets and Config

- Never commit real keys.
- Production secrets must come from App Settings:
  - `Speech__Key`
  - `Speech__Region`
  - `BitStore__*` (if enabled)
- `appsettings.json` must stay safe for source control.

## Local Workflow

```powershell
dotnet build
dotnet run --project Babbler.Web --launch-profile http
```

Local default URL: `http://localhost:5091`

## Deployment Artifacts

- `DEPLOYMENT.md`
- `.github/workflows/deploy-azure-webapp.yml`
- `scripts/azure/provision-appservice.ps1`
- `scripts/azure/deploy-webapp.ps1`

If deployment assumptions change, update all related docs/scripts in the same PR.

## Validation Checklist

- `dotnet build` succeeds
- Room create/select/start/stop works
- `index.html` requests mic permission on start
- `join.html` PIN gate works
- `display.html` receives `translationUpdate` events
- No regressions in room-scoped behavior
