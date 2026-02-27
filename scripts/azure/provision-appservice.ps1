param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [string]$Location = "swedencentral",
    [string]$AppServicePlanName = "",
    [ValidateSet("F1", "B1", "S1")]
    [string]$Sku = "F1",
    [string]$Runtime = "DOTNETCORE|9.0",
    [string]$SubscriptionId = "",

    [string]$SpeechKey = "",
    [string]$SpeechRegion = "swedencentral",
    [int]$FreeMinutesLimit = 300,

    [bool]$BitStoreEnabled = $false,
    [string]$BitStoreBaseUrl = "https://bitstorehome.azurewebsites.net",
    [string]$BitStoreBucketSlug = "",
    [string]$BitStoreWriteKey = "",

    [string]$ExportPublishProfilePath = ""
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) is not installed. Install it first: https://learn.microsoft.com/cli/azure/install-azure-cli"
}

if ([string]::IsNullOrWhiteSpace($AppServicePlanName)) {
    $AppServicePlanName = "$WebAppName-plan"
}

az account show --only-show-errors | Out-Null
if (-not [string]::IsNullOrWhiteSpace($SubscriptionId)) {
    az account set --subscription $SubscriptionId --only-show-errors
}

Write-Host "Creating resource group '$ResourceGroupName' in '$Location'..."
az group create `
    --name $ResourceGroupName `
    --location $Location `
    --only-show-errors | Out-Null

Write-Host "Creating Linux App Service plan '$AppServicePlanName' ($Sku)..."
az appservice plan create `
    --name $AppServicePlanName `
    --resource-group $ResourceGroupName `
    --location $Location `
    --sku $Sku `
    --is-linux `
    --only-show-errors | Out-Null

Write-Host "Creating web app '$WebAppName' ($Runtime)..."
az webapp create `
    --resource-group $ResourceGroupName `
    --plan $AppServicePlanName `
    --name $WebAppName `
    --runtime $Runtime `
    --only-show-errors | Out-Null

$appSettings = @(
    "ASPNETCORE_ENVIRONMENT=Production",
    "Speech__Region=$SpeechRegion",
    "SessionLimits__FreeMinutesLimit=$FreeMinutesLimit",
    "BitStore__Enabled=$($BitStoreEnabled.ToString().ToLowerInvariant())"
)

if (-not [string]::IsNullOrWhiteSpace($SpeechKey)) {
    $appSettings += "Speech__Key=$SpeechKey"
}

if ($BitStoreEnabled) {
    if (-not [string]::IsNullOrWhiteSpace($BitStoreBaseUrl)) {
        $appSettings += "BitStore__BaseUrl=$BitStoreBaseUrl"
    }
    if (-not [string]::IsNullOrWhiteSpace($BitStoreBucketSlug)) {
        $appSettings += "BitStore__BucketSlug=$BitStoreBucketSlug"
    }
    if (-not [string]::IsNullOrWhiteSpace($BitStoreWriteKey)) {
        $appSettings += "BitStore__WriteKey=$BitStoreWriteKey"
    }
}

Write-Host "Applying app settings..."
az webapp config appsettings set `
    --resource-group $ResourceGroupName `
    --name $WebAppName `
    --settings $appSettings `
    --only-show-errors | Out-Null

if (-not [string]::IsNullOrWhiteSpace($ExportPublishProfilePath)) {
    Write-Host "Exporting publishing profile to '$ExportPublishProfilePath'..."
    $publishProfileXml = az webapp deployment list-publishing-profiles `
        --resource-group $ResourceGroupName `
        --name $WebAppName `
        --xml
    $publishProfileXml | Set-Content -Path $ExportPublishProfilePath -NoNewline
}

Write-Host ""
Write-Host "Provisioning complete."
Write-Host "Web app URL: https://$WebAppName.azurewebsites.net"
