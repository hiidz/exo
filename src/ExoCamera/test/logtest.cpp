// Hammers the logger from several threads so a torn line would show, and
// writes past 4MB so the rollover runs. Lines have a fixed shape, so a
// malformed one is found by grep.
#include "../exo_log.h"

#include <windows.h>
#include <stdio.h>

static const int kThreads = 8;
static const int kLinesPerThread = 10000;

static DWORD WINAPI Worker(LPVOID param)
{
    int id = (int)(INT_PTR)param;
    for (int i = 0; i < kLinesPerThread; i++) {
        ExoLog("thread=%d line=%05d payload=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", id, i);
    }
    return 0;
}

int main()
{
    HANDLE threads[kThreads];
    for (int i = 0; i < kThreads; i++) {
        threads[i] = CreateThread(NULL, 0, Worker, (LPVOID)(INT_PTR)i, 0, NULL);
        if (threads[i] == NULL) {
            printf("CreateThread %d failed: %lu\n", i, GetLastError());
            return 1;
        }
    }
    WaitForMultipleObjects(kThreads, threads, TRUE, INFINITE);
    for (int i = 0; i < kThreads; i++) {
        CloseHandle(threads[i]);
    }
    ExoLogClose();
    printf("wrote %d lines from %d threads, pid=%lu\n",
           kThreads * kLinesPerThread, kThreads, GetCurrentProcessId());
    return 0;
}
