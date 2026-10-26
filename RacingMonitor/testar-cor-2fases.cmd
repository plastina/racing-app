@echo off
rem Teste de ESCRITA em duas fases: fase 1 deixa o 12V VERMELHO por 5 s, fase 2 deixa o 5V VERDE por 5 s;
rem cada fase restaura a cor original. Pede administrador e salva a saida em escrita-resultado.txt.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --led-write-test > "%~dp0escrita-resultado.txt" 2>&1
type "%~dp0escrita-resultado.txt"
echo.
echo Resultado salvo em escrita-resultado.txt. Pode fechar esta janela.
pause
