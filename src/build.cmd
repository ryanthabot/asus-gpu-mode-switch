@echo off
rem ---------------------------------------------------------------------------
rem Builds both executables using the C# compiler that ships with Windows.
rem No Visual Studio, .NET SDK, or internet access required.
rem
rem   dist\Go Time.exe   -> Standard GPU mode (MSHybrid, dGPU on)
rem   dist\Eco Mode.exe  -> Eco GPU mode      (dGPU powered off)
rem ---------------------------------------------------------------------------
setlocal
cd /d "%~dp0.."

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
    echo ERROR: .NET Framework 4.x C# compiler not found.
    exit /b 1
)

if not exist dist mkdir dist

%CSC% /nologo /target:winexe /platform:anycpu /optimize+ /define:MODE_STANDARD ^
    /win32manifest:src\app.manifest ^
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.ServiceProcess.dll ^
    /out:"dist\Go Time.exe" src\GpuModeSwitch.cs
if errorlevel 1 exit /b 1

%CSC% /nologo /target:winexe /platform:anycpu /optimize+ /define:MODE_ECO ^
    /win32manifest:src\app.manifest ^
    /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.ServiceProcess.dll ^
    /out:"dist\Eco Mode.exe" src\GpuModeSwitch.cs
if errorlevel 1 exit /b 1

echo.
echo Build OK:
dir /b dist
