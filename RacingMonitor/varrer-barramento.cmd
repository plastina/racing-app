@echo off
rem Varredura SOMENTE LEITURA do SMBus auxiliar (0x0B20): lista quais enderecos respondem.
rem Pede administrador, roda e salva a saida em scan-resultado.txt nesta pasta.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --scan > "%~dp0scan-resultado.txt" 2>&1
type "%~dp0scan-resultado.txt"
echo.
echo Resultado salvo em scan-resultado.txt. Pode fechar esta janela.
pause
