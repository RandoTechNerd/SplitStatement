@echo off
cd /d "%~dp0"
echo Starting Money Split... (close this window to stop it)
dotnet run --project "%~dp0MoneySplit.csproj"
pause
