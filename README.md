# Babbler

Realtime speech translation web app for rooms, conferences, and events.

## Overview

- Multi-room control with room ID + PIN
- Browser microphone capture from control page
- Live subtitles to display clients through SignalR
- PIN-gated join flow (`join.html` -> `display.html`)
- Monthly free-minute quota
- Optional quota persistence using BitStore

## Tech Stack

- ASP.NET Core minimal API (`net9.0`)
- SignalR hub: `/hubs/translation`
- Static frontend pages in `Babbler.Web/wwwroot`
- Azure Speech SDK (browser recognizer + server token endpoint)

## App Pages

- `/index.html`: control panel, room lifecycle, live feed
- `/join.html`: PIN entry for audience/display devices
- `/display.html?roomId=...`: fullscreen subtitle output

## Local Setup

### 1. Configure local secrets

Set in `Babbler.Web/appsettings.Development.json`:

- `Speech:Key`
- `Speech:Region` (example: `swedencentral`)

Optional BitStore:

- `BitStore:Enabled`
- `BitStore:BaseUrl`
- `BitStore:BucketSlug`
- `BitStore:WriteKey`

### 2. Build and run

```powershell
dotnet build
dotnet run --project Babbler.Web --launch-profile http
```

Default local URL:

- `http://localhost:5091`

### 3. Smoke test

1. Open `http://localhost:5091`
2. Create/select room
3. Click `Start Translation`
4. Accept microphone permission
5. Open join URL, enter PIN, verify subtitles in display

## Environment Variables (Production)

Use Azure App Settings, not committed files:

- `Speech__Key`
- `Speech__Region`
- `SessionLimits__FreeMinutesLimit`
- `BitStore__Enabled`
- `BitStore__BaseUrl`
- `BitStore__BucketSlug`
- `BitStore__WriteKey`

## API Summary

- `GET /api/speech/token`
- `GET /api/rooms`
- `POST /api/rooms`
- `GET /api/rooms/{roomId}/access-info`
- `POST /api/rooms/{roomId}/access/verify`
- `GET /api/rooms/{roomId}/session/status`
- `GET /api/rooms/{roomId}/diag`
- `POST /api/rooms/{roomId}/session/start`
- `POST /api/rooms/{roomId}/session/target`
- `POST /api/rooms/{roomId}/session/stop`

## Deployment

- GitHub Actions: `.github/workflows/deploy-azure-webapp.yml`
- Azure scripts: `scripts/azure/*.ps1`
- Full instructions: `DEPLOYMENT.md`

## Troubleshooting (Common)

### Room starts but no subtitles appear

Check:

1. Room is actually running:
   - `GET /api/rooms/{roomId}/session/status` -> `isRunning=true`
2. Display is connected to same room ID.
3. Control tab stays foreground while speaking.
4. Browser mic permission is granted for site.
5. Source language matches speaker language.

### Works locally but fails in Azure

Check:

1. App Service instance count is `1` (or use Azure SignalR Service for scale-out).
2. WebSockets enabled (recommended for stable realtime).
3. Speech settings are present in App Settings.
4. Hard-refresh browser after deploy (`Ctrl+F5`).

## Notes

- Swedish target language support (`sv`) is intentionally included.
- Do not commit real secrets to `appsettings.json`.
