#!/bin/sh
# What has to be clear before a tool starts its hub: no
# hub already running (Start with Windows puts one in the tray), nothing on
# 5037 (the hub refuses a server it did not start, and a stray one outlives
# a run), and no screensaver (the EXO monitor goes grey and touch fails).
# Prints each problem and exits 1, or exits 0 silently.
# Usage: sh preflight.sh || exit 1
export MSYS_NO_PATHCONV=1
bad=0

running() { tasklist /FI "IMAGENAME eq $1" /FO CSV /NH | grep -qi "\"$1\""; }

if running ExoHub.exe; then
    echo "preflight: a hub is running; quit it from the tray first"; bad=1
fi
# Get-Process misses the screensaver; tasklist shows it.
if running scrnsave.scr; then
    echo "preflight: the screensaver is running; end it"; bad=1
fi
for pid in $(netstat -ano -p TCP | grep LISTENING | grep -E ':5037[[:space:]]' | awk '{print $NF}' | sort -u); do
    path=$(powershell -NoProfile -Command "(Get-Process -Id $pid).Path" 2>/dev/null | tr -d '\r')
    echo "preflight: 5037 is held by PID $pid ($path); end it by PID, never kill-server"; bad=1
done
exit $bad
