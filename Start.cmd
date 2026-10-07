@echo off
setlocal
if not exist "%~dp0dist\DeskTodo\DeskTodo.exe" (
  echo Build the app first: powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Publish
  pause
  exit /b 1
)
start "" "%~dp0dist\DeskTodo\DeskTodo.exe"
