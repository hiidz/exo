#!/bin/sh
# PROJECT_SPEC SC-11, an hour of both. The S21 as camera feeding Chrome, the
# TB375FC as display with moving content on the EXO monitor, one hub for N seconds.
# Shaped like linkrun.sh: hub, live logcat from a device time, timestats,
# then a second hub that sets no reverse to collect. Adds the Chrome page
# (campage.ps1) and a once-a-minute heat and battery sample per phone.
# Usage: sc11run.sh <seconds> <tag>
#
# Every adb call uses the hub's own adb.exe and runs only while a hub owns
# 5037 (DECISIONS 5.8). The sampler stops 60s before --seconds runs out, so
# it cannot race the hub's exit and leave a server the next hub refuses.
export MSYS_NO_PATHCONV=1
S=$1; TAG=$2
CAM=PHONESERIAL; DISP=TABLETSERIAL
T=C:/Dev/Exo/src/ExoPhone/testdata/sc11/$TAG; mkdir -p $T
BIN=C:/Dev/Exo/src/ExoHub/bin/Release/net10.0-windows
HUB=$BIN/ExoHub.exe; ADB=$BIN/adb/adb.exe
CHROME="C:/Program Files/Google/Chrome/Application/chrome.exe"
FILES=/storage/emulated/0/Android/data/exo.phone/files
say() { echo "$(date +%H:%M:%S) $*" | tee -a $T/run.txt; }

# Starts the hub with the given arguments, logging to $1, and returns once
# its own adb server answers and the device list is held.
hub() {
    LOG=$1; shift
    "$HUB" "$@" > $LOG 2>&1 &
    HUBPID=$!
    until grep -q "holding host:track-devices" $LOG 2>/dev/null; do
        kill -0 $HUBPID 2>/dev/null || { say "hub exited:"; grep REFUSED $LOG; exit 1; }
        # A foreign adb on 5037 no longer ends the hub; it runs on without.
        grep -q "adb REFUSED" $LOG && { say "hub refused adb:"; grep REFUSED $LOG; kill $HUBPID; exit 1; }
        sleep 0.2
    done
}

# Raw dumpsys battery and thermalservice for each phone, appended.
sample() {
    for d in $CAM $DISP; do
        { echo "=== $(date +%Y-%m-%dT%H:%M:%S) $d"
          "$ADB" -s $d shell dumpsys battery
          "$ADB" -s $d shell dumpsys thermalservice; } >> $T/thermal-$d.txt 2>&1
    done
}

powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/campage.ps1 \
    -Log "$(cygpath -w $T/page.log)" -Seconds $((S + 300)) > $T/campage.txt 2>&1 &
PAGEPID=$!
say "hub start, --seconds $S"
hub $T/hub.log --seconds $S
"$ADB" -s $CAM shell am start -S -n exo.phone/.MainActivity --es role camera >/dev/null
"$ADB" -s $DISP shell am start -S -n exo.phone/.MainActivity --es role display \
    --ei rate 30 --es orientation landscape --es size full >/dev/null
# The EXO monitor exists once the phone connects; the content goes on it then.
until grep -q "vdisp EXO0001 on the desktop" $T/hub.log; do sleep 0.2; done
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/vddcontent.ps1 -Seconds $((S + 60)) > $T/content.log 2>&1 &
HUB0=$(date +%s)
say "hub holds the device list"

# Live logcat per phone, from 3s before now (linkrun.sh says why).
for d in $CAM $DISP; do
    eval START_$d=$(( $("$ADB" -s $d wait-for-device shell date +%s | tr -d '\r') - 3 ))
    eval "\"\$ADB\" -s $d logcat -T \$START_$d.000 -s ExoPhone:V AndroidRuntime:E" > $T/logcat-$d-live.txt 2>/dev/null &
    eval LOGPID_$d=$!
done
# The live connection writes its CSV only when it ends, so old ones can go.
"$ADB" -s $DISP shell "rm -f $FILES/*-link-*.csv"
"$ADB" -s $DISP shell dumpsys SurfaceFlinger --timestats -disable -clear >/dev/null
"$ADB" -s $DISP shell dumpsys SurfaceFlinger --timestats -enable >/dev/null

# Chrome once the camera is registered and the S21 streams to the hub.
for i in $(seq 1 120); do
    grep -q "vcam Start" $T/hub.log && grep -q "role camera" $T/hub.log && break
    sleep 0.5
done
say "chrome opened"
"$CHROME" --profile-directory="Profile 1" --new-window --window-position=0,0 --window-size=1280,900 \
    http://localhost:8765/ > /dev/null 2>&1 &

# Heat and battery once a minute, until 60s before --seconds runs out.
(
    while [ $(( $(date +%s) - HUB0 )) -lt $((S - 60)) ] && kill -0 $HUBPID 2>/dev/null; do
        sample
        sleep 60
    done
) &
SAMPLERPID=$!

wait $HUBPID
say "hub exited: $(tail -1 $T/hub.log)"
wait $SAMPLERPID
sleep 2
kill $LOGPID_PHONESERIAL $LOGPID_TABLETSERIAL 2>/dev/null

# Collection, through a hub that sets no reverse, so neither app reconnects.
# The tablet writes its CSV after the connection ends; with ~100k rows that
# takes a while, so wait for it.
hub $T/collect.log --no-reverse --seconds 180
say "collect hub up"
sample
for i in $(seq 1 60); do
    "$ADB" -s $DISP logcat -d -T $START_TABLETSERIAL.000 -s ExoPhone:V | grep -q "wrote .*-link-" && break
    sleep 2
done
"$ADB" -s $DISP logcat -d -T $START_TABLETSERIAL.000 -s ExoPhone:V | grep "wrote " | tee -a $T/run.txt
for f in $("$ADB" -s $DISP shell "ls $FILES/ | grep -- -link-" | tr -d '\r'); do
    "$ADB" -s $DISP pull $FILES/$f $T/$f > /dev/null && say "pulled $f $(wc -c < $T/$f) bytes"
done
"$ADB" -s $DISP shell dumpsys SurfaceFlinger --timestats -dump > $T/ts.txt
"$ADB" -s $DISP shell dumpsys SurfaceFlinger --timestats -disable >/dev/null
for d in $CAM $DISP; do
    eval "\"\$ADB\" -s $d logcat -d -T \$START_$d.000 -s ExoPhone:V AndroidRuntime:E" > $T/logcat-$d-tail.txt
    grep -h -v '^-' $T/logcat-$d-live.txt $T/logcat-$d-tail.txt | sort -u > $T/logcat-$d.txt
    rm $T/logcat-$d-live.txt $T/logcat-$d-tail.txt
done
say "collected; waiting for the collect hub to stop"
wait $HUBPID
kill $PAGEPID 2>/dev/null
say "done"
