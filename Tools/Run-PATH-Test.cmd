@echo off
title MyCompiler PATH Test
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Toolchain-PATH-Test.ps1"
echo.
pause
