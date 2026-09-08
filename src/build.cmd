@echo off
rem ---------------------------------------------------------------------------
rem Builds the unified executable using the C# compiler that ships with
rem Windows. No Visual Studio, .NET SDK, or internet access required.
rem
rem   dist\GPU Mode Switch.exe  -> both modes in one app (v1.2.0, D10)
rem
rem The retired v1.x pair (Go Time.exe / Eco Mode.exe, built with the
rem MODE_STANDARD / MODE_ECO defines) is gone - the mode is chosen at
rem runtime (cards, --gotime/--eco flags or the session tray).
rem Every .cs file under src\ is compiled into the one target.
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

rem WindowsBase lives in the WPF subdirectory of the framework folder
set "WPFDIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"
if not exist "%WPFDIR%\WindowsBase.dll" set "WPFDIR=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\WPF"

rem UI Automation assemblies live in the GAC (not the framework directory)
set "UIAC_DIR="
set "UIAT_DIR="
for /f "delims=" %%i in ('dir /b /ad "%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient" 2^>nul') do set "UIAC_DIR=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\%%i"
for /f "delims=" %%i in ('dir /b /ad "%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes" 2^>nul') do set "UIAT_DIR=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\%%i"

%CSC% /nologo /target:winexe /platform:anycpu /optimize+ ^
    /win32manifest:src\app.manifest /win32icon:src\gotime.ico ^
    /res:src\gotime-256.png,GpuModeSwitch.appicon.png ^
    /res:src\gotime-256.png,GpuModeSwitch.gotime.png ^
    /res:src\ecomode-256.png,GpuModeSwitch.ecomode.png ^
    /r:System.dll /r:"%WPFDIR%\WindowsBase.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.ServiceProcess.dll /r:System.Web.Extensions.dll ^
    /r:"%UIAC_DIR%\UIAutomationClient.dll" /r:"%UIAT_DIR%\UIAutomationTypes.dll" ^
    /out:"dist\GPU Mode Switch.exe" src\*.cs
if errorlevel 1 exit /b 1

echo.
echo Build OK:
dir /b dist
