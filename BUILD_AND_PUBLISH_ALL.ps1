#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PosProject = Join-Path $Root "..\AzumahPOS\AzumahPOS.csproj"
$ApiProject = Join-Path $PSScriptRoot "AzumahCloud.Api.csproj"
$PosPublish = Join-Path $Root "..\AzumahPOS\publish"
$ApiPublish = Join-Path $PSScriptRoot "publish"

Write-Host "Building Azumah POS..." -ForegroundColor Cyan
dotnet publish $PosProject -c Release -r win-x64 --self-contained true -o $PosPublish

Write-Host "Installing/updating LAN API..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "INSTALL_LAN_API.ps1")

Write-Host ""
Write-Host "Build and LAN deployment completed." -ForegroundColor Green
Write-Host "POS publish folder: $PosPublish"
Write-Host "LAN API publish folder: $ApiPublish"
