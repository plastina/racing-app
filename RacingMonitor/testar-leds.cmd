@echo off
rem Teste SOMENTE LEITURA do controlador de LEDs (LED MCU 0x30, barramento [1] = SMBus auxiliar 0x0B20).
rem Pede administrador, roda o teste e salva a saida em leds-resultado.txt nesta pasta.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --led-bus 1 > "%~dp0leds-resultado.txt" 2>&1
type "%~dp0leds-resultado.txt"
echo.
echo Resultado salvo em leds-resultado.txt. Pode fechar esta janela.
pause
