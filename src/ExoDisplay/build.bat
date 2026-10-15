@echo off
REM ExoDisplay, x64 Release, with the WDK from NuGet restored into packages\.
REM The output is x64\Release\ExoDisplay\: the DLL, INF and catalog, signed
REM with the WDK's per-user test certificate. install.ps1 installs it.
REM A release signs with Exo's release certificate instead: TestCertificate
REM set in the environment, which MSBuild reads as a property, as
REM src\ExoSetup\release.ps1 does. Rebuild, so a switch
REM between the two certificates signs again.
REM
REM 64-bit MSBuild: the WDK's tasks pick their DLLs by the process's bitness,
REM and this WDK has no x86 ones. No parentheses anywhere in the path: the
REM WDK escapes them and then cannot find its own tasks.
set MSB="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

cd /d "%~dp0"
%MSB% ExoDisplay.vcxproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir=%~dp0 -nologo -v:minimal -nodeReuse:false
if errorlevel 1 exit /b 1
%MSB% ExoDisplay.vcxproj -t:Rebuild -p:Configuration=Release -p:Platform=x64 -p:SolutionDir=%~dp0 -nologo -v:minimal -nodeReuse:false
if errorlevel 1 exit /b 1

REM exodevice.exe, the installer's helper for the root device (exodevice.cpp).
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 (echo VCVARS FAILED & exit /b 1)
cl /nologo /EHsc /W4 /WX /Fo:x64\Release\ /Fe:x64\Release\exodevice.exe exodevice.cpp /link setupapi.lib newdev.lib advapi32.lib
if errorlevel 1 exit /b 1
echo --- build ok ---
