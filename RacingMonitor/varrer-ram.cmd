@echo off
rem Varredura SOMENTE LEITURA dos controladores RGB das memorias (enderecos 0x70-0x77 do SMBus principal).
rem Nao toca na EEPROM dos pentes (0x50-0x57). Pede administrador e salva a saida em ram-resultado.txt.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --scan-ram > "%~dp0ram-resultado.txt" 2>&1
type "%~dp0ram-resultado.txt"
echo.
echo Resultado salvo em ram-resultado.txt. Pode fechar esta janela.
pause
