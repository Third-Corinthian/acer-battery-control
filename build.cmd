@echo off
rem Build Acer Battery Control with the C# compiler included in Windows (.NET Framework 4.x).
rem Run from the folder containing this file. No installation required.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe not found: .NET Framework 4.x required
  exit /b 1
)
set REFS=/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.ServiceProcess.dll
set OPTS=/nologo /codepage:65001 /optimize+ /platform:anycpu
"%CSC%" %OPTS% /target:winexe /win32icon:src\app.ico /win32manifest:src\app.manifest /out:AcerBatteryControl.exe %REFS% src\*.cs || goto :err
"%CSC%" %OPTS% /target:exe /define:CLI /out:acerbat.exe %REFS% src\*.cs || goto :err
copy /y src\app.config AcerBatteryControl.exe.config >nul || goto :err
echo.
echo Built AcerBatteryControl.exe and acerbat.exe
exit /b 0
:err
echo Build failed
exit /b 1
