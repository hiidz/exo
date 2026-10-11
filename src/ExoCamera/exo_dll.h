// What the COM entry points share with the objects they hand out.
#pragma once

#include <windows.h>

// Every live COM object this DLL created. Each object increments it in its
// constructor and decrements it in its destructor, with Interlocked calls,
// since COM may create and release objects on any thread. DllCanUnloadNow
// refuses while it is above zero: unloading under a live object would run
// freed code inside a process we do not own.
extern LONG g_objects;

// For QueryInterface misses. Logs the first miss for each object and
// interface, and counts the rest: the Frame Server probes the source about
// 370 times a second while streaming, and a line each rolled the log over
// within minutes (A4, 2026-10-11). object is a string literal naming the
// class.
void ExoLogQiMiss(const char* object, REFIID riid);
