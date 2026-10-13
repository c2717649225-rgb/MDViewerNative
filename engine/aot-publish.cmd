@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\build.ps1" -Task Publish %*
exit /b %errorlevel%
