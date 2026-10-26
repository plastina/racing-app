@echo off
rem Le o nome do controlador RGB das memorias (ENE em 0x77, SMBus principal).
rem Faz apenas a escrita de "posicionamento do cursor" de leitura (registradores 0x1000-0x100F), sem mudar cor.
rem Pede administrador e salva a saida em ram-id-resultado.txt.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --ram-id > "%~dp0ram-id-resultado.txt" 2>&1
type "%~dp0ram-id-resultado.txt"
echo.
echo Resultado salvo em ram-id-resultado.txt. Pode fechar esta janela.
pause
