# Builds the plugin and writes dist/JellyfinRequests_<version>.zip, ready to unzip into
# Jellyfin's plugins folder (<config>/plugins/JellyfinRequests_<version>/).
param(
    [string]$Version = "1.0.0.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src/Jellyfin.Plugin.Requests"
$out = Join-Path $root "dist"
$stage = Join-Path $out "JellyfinRequests_$Version"

dotnet publish $project -c Release -o (Join-Path $out "publish") -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $out "publish/Jellyfin.Plugin.Requests.dll") $stage

$meta = [ordered]@{
    category    = "General"
    changelog   = ""
    description = "A themed requests page backed by Seerr, Radarr and Sonarr, with automatic cleanup of removed content."
    guid        = "5b0f3a52-8f1e-4d0c-9a77-2f6f7c1e9b41"
    name        = "Jellyfin Requests"
    overview    = "Requests page for Seerr, grouped by user, with live download progress."
    owner       = "jellyfin-requests-plugin"
    targetAbi   = "12.0.0.0"
    timestamp   = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    version     = $Version
    status      = "Active"
    autoUpdate  = $false
    assemblies  = @()
}
# Jellyfin rejects a UTF-8 BOM, which Windows PowerShell's Set-Content -Encoding utf8 adds.
[IO.File]::WriteAllText((Join-Path $stage "meta.json"), ($meta | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

$zip = Join-Path $out "JellyfinRequests_$Version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Write-Host "Wrote $zip"
