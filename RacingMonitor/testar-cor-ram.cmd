@echo off
rem Primeiro teste de ESCRITA nas memorias: deixa as cores do efeito (0x8160-0x8177) VERMELHAS por 5 s
rem e restaura as cores originais. Nao escreve em mais nada. Pede administrador e salva em ram-escrita-resultado.txt.

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"
"%~dp0bin\Release\net8.0-windows\win-x64\RacingMonitor.exe" --leds --ram-write-test > "%~dp0ram-escrita-resultado.txt" 2>&1
type "%~dp0ram-escrita-resultado.txt"
echo.
echo Resultado salvo em ram-escrita-resultado.txt. Pode fechar esta janela.
pause
