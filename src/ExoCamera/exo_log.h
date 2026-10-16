// ExoCamera's file logger. This DLL runs inside svchost (LOCAL SERVICE), the
// hub, and possibly camera apps, where a debugger is impractical, so the log
// is the debugger.
//
// One file per process: C:\Windows\Temp\Exo\logs\exo-<exe>-<pid>.log. The
// exe name in the file name records which processes loaded the DLL.
#pragma once

// printf-style. Opens the file on the first call. Never fails loudly: a lost
// line must not take down a host process we do not own.
void ExoLog(const char* fmt, ...);

// Closes the file. Called from DllMain on unload, so that a DLL loaded and
// unloaded many times in one svchost does not leak a handle each time.
void ExoLogClose();
