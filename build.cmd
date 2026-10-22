@echo off
setlocal
cd /d "%~dp0"

echo [1/3] build (net48 + net8)
dotnet build src\WinDndToggle\WinDndToggle.csproj -c Release --nologo || goto :fail

echo [2/3] portable net48 -^> dist\WinDndToggle.exe
if not exist dist mkdir dist
copy /y src\WinDndToggle\bin\Release\net48\WinDndToggle.exe dist\ >nul || goto :fail

echo [3/3] net8 single file -^> dist\net8\WinDndToggle.exe
if exist dist\net8 rmdir /s /q dist\net8
dotnet publish src\WinDndToggle\WinDndToggle.csproj -c Release -f net8.0-windows -r win-x64 ^
  -p:SelfContained=false -p:PublishSingleFile=true -o dist\net8 --nologo || goto :fail

echo.
echo done:
echo   dist\WinDndToggle.exe       portable, no runtime install needed
echo   dist\net8\WinDndToggle.exe  requires .NET 8 Desktop Runtime
exit /b 0

:fail
echo.
echo build failed
exit /b 1
