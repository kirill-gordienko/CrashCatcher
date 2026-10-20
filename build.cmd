@echo off
chcp 65001 >nul
rem Сборка CrashCatcher.exe встроенным компилятором .NET Framework 4.x — ничего устанавливать не нужно.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:anycpu /optimize+ /unsafe /out:CrashCatcher.exe /win32icon:assets\app.ico /win32manifest:assets\app.manifest /resource:assets\app.ico,CrashCatcher.app.ico /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Runtime.Serialization.dll /r:System.Xml.dll /recurse:src\*.cs
if errorlevel 1 (
  echo.
  echo Сборка не удалась.
  pause
  exit /b 1
)
echo Готово: CrashCatcher.exe
pause
