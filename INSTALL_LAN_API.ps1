#Requires -RunAsAdministrator
$ErrorActionPreference = "Stop"

$ApiProject = Join-Path $PSScriptRoot "AzumahCloud.Api.csproj"
$PublishDir = Join-Path $PSScriptRoot "publish"
$ServiceName = "AzumahCloudLAN"
$Port = 5088
$DatabasePath = Join-Path $env:LOCALAPPDATA "AzumahEnterprise\azumah.db"

if (-not (Test-Path $DatabasePath)) {
    throw "The Main PC POS database was not found at $DatabasePath. Start AzumahPOS once and run this installer again."
}

$code = Read-Host "Enter the LAN Access Code (minimum 8 characters)"
if ([string]::IsNullOrWhiteSpace($code) -or $code.Length -lt 8) {
    throw "LAN Access Code must contain at least 8 characters."
}

Write-Host "Publishing Azumah LAN API..." -ForegroundColor Cyan
dotnet publish $ApiProject -c Release -r win-x64 --self-contained true -o $PublishDir

$configPath = Join-Path $PublishDir "appsettings.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json
$CloudDatabasePath = Join-Path $PublishDir "azumah-cloud.db"
$config.ConnectionStrings.Default = "Data Source=$CloudDatabasePath"
$config.Jwt.SigningKey = [guid]::NewGuid().ToString("N") + [guid]::NewGuid().ToString("N")
$config.LanAccess.AccessCode = $code
$config.LanDatabase.Path = $DatabasePath
$config.Kestrel.Endpoints.Http.Url = "http://0.0.0.0:$Port"
$config | ConvertTo-Json -Depth 10 | Set-Content $configPath -Encoding UTF8

$exe = Join-Path $PublishDir "AzumahCloud.Api.exe"
if (-not (Test-Path $exe)) { throw "Published API executable was not created: $exe" }

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

sc.exe create $ServiceName binPath= "`"$exe`"" start= auto DisplayName= "Azumah Offline LAN Inventory API" | Out-Null
sc.exe description $ServiceName "Shared offline inventory API for Azumah POS Main PC and restocking PC." | Out-Null

if (-not (Get-NetFirewallRule -DisplayName "Azumah LAN Inventory 5088" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName "Azumah LAN Inventory 5088" -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private | Out-Null
}

Start-Service $ServiceName

Write-Host ""
Write-Host "Azumah LAN API installed successfully." -ForegroundColor Green
Write-Host "Service: $ServiceName"
Write-Host "Port: $Port"
Write-Host "Database: $DatabasePath"
Write-Host ""
Write-Host "On PC-2, open: http://MAIN-PC-IP:$Port/lan.html" -ForegroundColor Yellow
Write-Host "Keep the LAN access code private."
