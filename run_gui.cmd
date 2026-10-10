@echo off
cd /d "%~dp0"
dotnet build "SeedSearchGui\SeedSearchGui.csproj" -c Release --nologo
if errorlevel 1 exit /b 1
start "" "SeedSearchGui\bin\Release\net10.0-windows\SeedSearchGui.exe"
