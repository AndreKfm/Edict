@echo off
rem Starts Edict on Windows (a double click is enough): play.ps1 fetches the game data, builds and starts the game.
rem Arguments go to the game, e.g. play.cmd -listen 4
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0play.ps1" %*
if errorlevel 1 pause
