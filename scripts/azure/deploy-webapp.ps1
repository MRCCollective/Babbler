param(
    [Parameter(Mandatory = $true)]
    [string]$ResourceGroupName,

    [Parameter(Mandatory = $true)]
    [string]$WebAppName,

    [string]$ProjectPath = "Babbler.Web/Babbler.Web.csproj",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "Azure CLI (az) is not installed. Install it first: https://learn.microsoft.com/cli/azure/install-azure-cli"
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectFullPath = Join-Path $repoRoot $ProjectPath
if (-not (Test-Path $projectFullPath)) {
    throw "Project file not found: $projectFullPath"
}

$artifactRoot = Join-Path $repoRoot ".artifacts\deploy"
$publishDir = Join-Path $artifactRoot "publish"
$zipPath = Join-Path $artifactRoot "$WebAppName.zip"

if (Test-Path $publishDir) {
    Remove-Item -Path $publishDir -Recurse -Force
}
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

Write-Host "Publishing '$projectFullPath'..."
dotnet publish $projectFullPath -c $Configuration -o $publishDir

Write-Host "Creating deployment zip '$zipPath'..."
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -Force

Write-Host "Deploying to Azure Web App '$WebAppName'..."
az webapp deploy `
    --resource-group $ResourceGroupName `
    --name $WebAppName `
    --src-path $zipPath `
    --type zip `
    --only-show-errors | Out-Null

Write-Host ""
Write-Host "Deployment complete."
Write-Host "Web app URL: https://$WebAppName.azurewebsites.net"
