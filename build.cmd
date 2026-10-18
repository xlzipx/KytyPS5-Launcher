@echo off
rem KytyPS5 Launcher: Release build into dist\ and the release package dist\KytyPS5-Launcher-<version>-Windows.zip.
rem Needs the .NET SDK (any recent one; the program targets .NET Framework 4.8, which Windows 10 and 11 include).
setlocal
cd /d "%~dp0"
if exist dist rmdir /s /q dist
dotnet build KytyLauncher.csproj -c Release -o dist\KytyPS5-Launcher -nologo -v quiet || exit /b 1
for /f "tokens=3 delims=<>" %%v in ('findstr /c:"<Version>" KytyLauncher.csproj') do set "VERSION=%%v"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\KytyPS5-Launcher\*' -DestinationPath 'dist\KytyPS5-Launcher-%VERSION%-Windows.zip' -Force" || exit /b 1
echo Built: dist\KytyPS5-Launcher-%VERSION%-Windows.zip
