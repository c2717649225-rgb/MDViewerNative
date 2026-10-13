@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Task Publish %*
exit /b %errorlevel%
