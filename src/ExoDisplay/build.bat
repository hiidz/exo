@echo off
REM ExoDisplay, x64 Release, with the WDK from NuGet restored into packages\.
REM The output is x64\Release\ExoDisplay\: the DLL, INF and catalog, signed
REM with the WDK's per-user test certificate. install.ps1 installs it.
REM
REM 64-bit MSBuild: the WDK's tasks pick their DLLs by the process's bitness,
REM and this WDK has no x86 ones. No parentheses anywhere in the path: the
REM WDK escapes them and then cannot find its own tasks.
set MSB="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe"

cd /d "%~dp0"
%MSB% ExoDisplay.vcxproj -t:restore -p:RestorePackagesConfig=true -p:SolutionDir=%~dp0 -nologo -v:minimal -nodeReuse:false
if errorlevel 1 exit /b 1
%MSB% ExoDisplay.vcxproj -p:Configuration=Release -p:Platform=x64 -p:SolutionDir=%~dp0 -nologo -v:minimal -nodeReuse:false
if errorlevel 1 exit /b 1
echo --- build ok ---
