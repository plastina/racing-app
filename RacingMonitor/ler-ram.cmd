@echo off
rem Le (sem mudar nada) os registradores de configuracao, modo e cores do controlador RGB das memorias
rem (ENE AUDA0-E6K5-0101 em 0x77). So faz escritas de "posicionamento do cursor" de leitura.
rem Pede administrador e salva a saida em ram-registradores.txt.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --ram-read > "%~dp0ram-registradores.txt" 2>&1
type "%~dp0ram-registradores.txt"
echo.
echo Resultado salvo em ram-registradores.txt. Pode fechar esta janela.
pause
