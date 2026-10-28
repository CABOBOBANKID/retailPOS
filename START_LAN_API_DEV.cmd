@echo off
setlocal
cd /d "%~dp0"
echo Starting Azumah Offline LAN Inventory API...
echo Enter a private LAN access code of at least 12 characters.
set /p AZUMAH_LAN_CODE=LAN access code: 
if "%AZUMAH_LAN_CODE%"=="" (
  echo An access code is required.
  pause
  exit /b 1
)
set "LanAccess__Enabled=true"
set "LanAccess__AccessCode=%AZUMAH_LAN_CODE%"
dotnet run --project "%~dp0AzumahCloud.Api.csproj" --urls "http://0.0.0.0:5088"
