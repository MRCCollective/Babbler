# Babbler Deployment (GitHub + Azure App Service)

## 1. Create and push repo to MRC Collective GitHub

Run from the repo root:

```powershell
git init
git add .
git commit -m "Initial Babbler app with Azure deploy workflow"
git branch -M main
git remote add origin https://github.com/<MRC_ORG>/<REPO_NAME>.git
git push -u origin main
```

Replace:
- `<MRC_ORG>` with your org/user in GitHub (for example `MRCCollective`)
- `<REPO_NAME>` with your repo name (for example `babbler`)

## 2. Provision Azure App Service

Install Azure CLI first if needed:
- https://learn.microsoft.com/cli/azure/install-azure-cli

Login:

```powershell
az login
```

Run provisioning script:

```powershell
.\scripts\azure\provision-appservice.ps1 `
  -ResourceGroupName "babbler-rg" `
  -WebAppName "babbler-<unique-name>" `
  -Location "swedencentral" `
  -Sku "F1" `
  -SpeechKey "<azure-speech-key>" `
  -SpeechRegion "swedencentral" `
  -FreeMinutesLimit 300 `
  -BitStoreEnabled $true `
  -BitStoreBaseUrl "https://bitstorehome.azurewebsites.net" `
  -BitStoreBucketSlug "<bitstore-bucket>" `
  -BitStoreWriteKey "<bitstore-write-key>" `
  -ExportPublishProfilePath ".\publish-profile.xml"
```

## 3. Configure GitHub Actions secrets/variables

In your GitHub repo:

- Repository variable:
  - `AZURE_WEBAPP_NAME` = `babbler-<unique-name>`
- Repository secret:
  - `AZURE_WEBAPP_PUBLISH_PROFILE` = full contents of `publish-profile.xml`

## 4. Deploy

Any push to `main` triggers deploy automatically via:
- `.github/workflows/deploy-azure-webapp.yml`

Manual deploy from local machine is also available:

```powershell
.\scripts\azure\deploy-webapp.ps1 `
  -ResourceGroupName "babbler-rg" `
  -WebAppName "babbler-<unique-name>"
```

## 5. Important runtime notes

- Browser microphone permission requires HTTPS in production.
- Configure secrets in Azure App Settings, not in committed `appsettings.json`.
- `F1` is the free App Service plan name.
