@echo off
rem Builds VMVolumeSync.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK, Visual Studio or downloads needed.
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo .NET Framework 4.x C# compiler not found.
    exit /b 1
)

pushd "%~dp0"
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
    /win32manifest:src\app.manifest ^
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll ^
    /out:VMVolumeSync.exe src\VMVolumeSync.cs
set "RESULT=%ERRORLEVEL%"
popd

if "%RESULT%"=="0" echo Built %~dp0VMVolumeSync.exe
exit /b %RESULT%
