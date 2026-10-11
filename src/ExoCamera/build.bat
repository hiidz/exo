@echo off
REM vcvars must run in the same shell as cl, because it sets INCLUDE and LIB.
REM Run from PowerShell or cmd, not Git Bash (spikes/STATUS.md).
call "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 (echo VCVARS FAILED & exit /b 1)

cd /d "%~dp0"
if not exist out mkdir out

echo --- logtest.exe ---
cl /nologo /EHsc /W4 /WX /Fo:out\ /Fe:out\logtest.exe test\logtest.cpp exo_log.cpp
if errorlevel 1 exit /b 1

echo --- exocam.dll ---
cl /nologo /EHsc /W4 /WX /LD /Fo:out\ /Fe:out\exocam.dll exo_dll.cpp exo_log.cpp exo_activate.cpp exo_source.cpp exo_stream.cpp exo_link.cpp exo_card.cpp exo_format.cpp ^
   /link /DEF:exocam.def advapi32.lib ole32.lib mfplat.lib mfuuid.lib ws2_32.lib
if errorlevel 1 exit /b 1

echo --- dlltest.exe ---
cl /nologo /EHsc /W4 /WX /Fo:out\ /Fe:out\dlltest.exe test\dlltest.cpp /link ole32.lib mfplat.lib mfuuid.lib
if errorlevel 1 exit /b 1

echo --- fmttest.exe ---
cl /nologo /EHsc /W4 /WX /Fo:out\ /Fe:out\fmttest.exe test\fmttest.cpp exo_format.cpp ^
   /link ole32.lib mfplat.lib mfuuid.lib
if errorlevel 1 exit /b 1

echo --- regcam.exe ---
cl /nologo /EHsc /W4 /WX /Fo:out\ /Fe:out\regcam.exe test\regcam.cpp ^
   /link ole32.lib mfplat.lib mfuuid.lib mfsensorgroup.lib
if errorlevel 1 exit /b 1

echo --- build ok ---
