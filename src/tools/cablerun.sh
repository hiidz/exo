#!/bin/sh
# The cable-pull test (DECISIONS 5.7). Hub and moving content for N seconds, the
# app connected, and a watcher that logs the device leaving and coming back
# and sets the reverse up again by hand each time it returns.
# Usage: cablerun.sh <seconds> <tag>
export MSYS_NO_PATHCONV=1
S=$1; TAG=$2
# Output is local evidence, gitignored with the rest of testdata.
T=C:/Dev/Exo/src/ExoPhone/testdata/link/$TAG; mkdir -p $T
SERIAL=$(adb get-serialno | tr -d "[:space:]")
HUB=C:/Dev/Exo/src/ExoHub/bin/Release/net10.0-windows/ExoHub.exe
ts() { date +%H:%M:%S.%3N; }

adb shell am force-stop exo.phone
adb reverse localabstract:exo tcp:27183 >/dev/null
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/vddcontent.ps1 -Seconds $((S + 15)) > $T/content.log 2>&1 &
sleep 3
adb logcat -c
"$HUB" --seconds $S > $T/hub.log 2>&1 &
HUBPID=$!
sleep 1
adb shell am start -n exo.phone/.MainActivity --es serial $SERIAL >/dev/null
echo "$(ts) hub started, app launched" | tee -a $T/events.log

while kill -0 $HUBPID 2>/dev/null; do
    timeout 5 adb wait-for-disconnect 2>/dev/null || continue
    echo "$(ts) device gone" | tee -a $T/events.log
    until timeout 5 adb wait-for-device 2>/dev/null; do kill -0 $HUBPID 2>/dev/null || break 2; done
    echo "$(ts) device back: $(adb reverse --list | tr -d '\r' | grep exo || echo 'no reverse')" | tee -a $T/events.log
    adb reverse localabstract:exo tcp:27183 >/dev/null
    echo "$(ts) reverse set again by hand" | tee -a $T/events.log
done
sleep 2
adb logcat -d -s ExoPhone:V AndroidRuntime:E > $T/logcat.txt
echo "$(ts) done" | tee -a $T/events.log
