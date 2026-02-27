# Babbler

Real-time translation web app for conferences and events.

## What It Does

- Creates multiple translation rooms with unique room IDs and PINs
- Lets a speaker run translation from the control page (`index.html`)
- Uses browser microphone capture (permission prompt on start)
- Streams translated text live to connected display clients
- Protects fullscreen display with a PIN join flow
- Tracks monthly free-minute usage and can persist usage to BitStore

## Stack

- ASP.NET Core (`net9.0`) minimal API + SignalR
- Static frontend (`wwwroot/*.html`)
- Azure Speech Service (browser SDK + short-lived server token endpoint)
- Optional BitStore usage persistence

## Pages

- `/index.html`: control panel for rooms, start/stop, and live feed
- `/join.html`: mobile/remote PIN entry for a room
- `/display.html?roomId=...`: fullscreen display output (PIN-gated)

## Quick Start (Local)

### 1. Configure secrets

Set these in `Babbler.Web/appsettings.Development.json` for local testing:

- `Speech:Key`
- `Speech:Region` (for example `swedencentral`)

Optional BitStore settings:

- `BitStore:Enabled`
- `BitStore:BaseUrl`
- `BitStore:BucketSlug`
- `BitStore:WriteKey`

### 2. Build and run

```powershell
dotnet build
dotnet run --project Babbler.Web --urls http://localhost:5091
```

Open:

- http://localhost:5091

### 3. Start translating

1. Open `index.html`
2. Create/select a room
3. Click `Start Translation`
4. Allow browser microphone permission
5. Open join URL on another device and enter PIN to show fullscreen output

## Key API Endpoints

- `GET /api/speech/token`
- `GET /api/rooms`
- `POST /api/rooms`
- `GET /api/rooms/{roomId}/access-info`
- `POST /api/rooms/{roomId}/access/verify`
- `GET /api/rooms/{roomId}/session/status`
- `POST /api/rooms/{roomId}/session/start`
- `POST /api/rooms/{roomId}/session/target`
- `POST /api/rooms/{roomId}/session/stop`

## Deployment

Deployment automation is included:

- GitHub Actions workflow: `.github/workflows/deploy-azure-webapp.yml`
- Azure scripts: `scripts/azure/*.ps1`
- Full guide: `DEPLOYMENT.md`

## Important Notes

- Browser microphone permission requires HTTPS in production.
- Do not commit real keys into `appsettings.json`.
- App Service free plan is `F1`.
