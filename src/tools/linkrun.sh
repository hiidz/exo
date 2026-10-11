#!/bin/sh
# One live run over the link (DECISIONS 5.7, 5.8). Moving content on the VDD,
# the hub for N seconds holding the display role for SERIAL, so it sets the
# reverse and launches the app itself, SurfaceFlinger timestats around it,
# then the phone's CSVs pulled and LinkTimes run on them. Pull the cable
# during the run for the replug test; the hub log records every cycle.
# Needs the hub built in Release, ExoPhone installed, and 5037 free.
# Usage: linkrun.sh <seconds> <tag> <serial>
#
# Every adb command here runs while a hub owns 5037. Run with no hub, adb
# would start a server of its own, and the hub refuses a server it did not
# start (DECISIONS 5.8). So the collection afterwards goes through a second,
# short hub run with no role, whose server dies with it.
export MSYS_NO_PATHCONV=1
S=$1; TAG=$2; SERIAL=$3
# Output is local evidence, gitignored with the rest of testdata.
T=C:/Dev/Exo/src/ExoPhone/testdata/link/$TAG; mkdir -p $T
HUB=C:/Dev/Exo/src/ExoHub/bin/Release/net10.0-windows/ExoHub.exe
JAVA="C:/Program Files/Android/Android Studio/jbr/bin/java"
FILES=/storage/emulated/0/Android/data/exo.phone/files

# Starts the hub with the given arguments, logging to $1, and returns once
# its own adb server answers and the device list is held.
hub() {
    LOG=$1; shift
    "$HUB" "$@" > $LOG 2>&1 &
    HUBPID=$!
    until grep -q "holding host:track-devices" $LOG 2>/dev/null; do
        kill -0 $HUBPID 2>/dev/null || { echo "hub exited:"; grep REFUSED $LOG; exit 1; }
        sleep 0.2
    done
}

powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/vddcontent.ps1 -Seconds $((S + 15)) > $T/content.log 2>&1 &
sleep 3
hub $T/hub.log --display $SERIAL --seconds $S
# The hub launches the app as soon as the device is listed. The live
# connection writes its CSV only when it ends, so old ones can go now.
adb -s $SERIAL shell "rm -f $FILES/*-link-*.csv"
adb -s $SERIAL logcat -c
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -disable -clear >/dev/null
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -enable >/dev/null
wait $HUBPID; sleep 2

hub $T/collect.log --seconds 30
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -dump > $T/ts.txt
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -disable >/dev/null
adb -s $SERIAL logcat -d -s ExoPhone:V AndroidRuntime:E > $T/logcat.txt
adb -s $SERIAL shell am force-stop exo.phone
for f in $(adb -s $SERIAL shell "ls $FILES" | tr -d '\r' | grep -- '-link-'); do adb -s $SERIAL pull $FILES/$f $T/ >/dev/null; done
kill $HUBPID 2>/dev/null; wait $HUBPID 2>/dev/null

grep -E "ended|done|ms n=" $T/logcat.txt | cut -c 32-
grep -E "sender|encoded->|write blocked" $T/hub.log
for c in $T/*-link-[0-9].csv; do
    "$JAVA" C:/Dev/Exo/src/tools/LinkTimes.java $c ${c%.csv}-pongs.csv | grep -v outlier
done
