#!/bin/sh
# One live run over the link (DECISIONS 5.7, 5.8). Moving content on the EXO monitor,
# the hub for N seconds, and the app on SERIAL started with the display role,
# SurfaceFlinger timestats around it,
# then the phone's CSVs pulled and LinkTimes run on them. Pull the cable
# during the run for the replug test; the hub log records every cycle.
# Needs the hub built in Release, ExoPhone installed, and 5037 free.
# Usage: linkrun.sh <seconds> <tag> <serial> [moving|static|browser|fast] [relaunches] [fps] [smoothing]
# browser shows content60.html in Edge on the EXO monitor, one image a vsync.
# fast paints only the moving strip, several images a vsync.
# static paints the text once and nothing moves (DECISIONS 5.4). relaunches
# restarts the app that many times, evenly spaced, each a new connection.
# fps is 30 or 60, 30 by default; the display is landscape at full size.
# smoothing is the phone's smoothing buffer in ms, 0 (off, the default),
# 20 or 50; given every run, since the phone keeps the last one.
# HUBARGS in the environment go to the measured hub, such as --bitrate 20
# or --quality 70 for the Wi-Fi rate control runs.
# PHONEARGS go to the app's start, such as "--es via wifi" or "--es via usb";
# the phone keeps that choice for later runs.
#
# Every adb command here runs while a hub owns 5037. Run with no hub, adb
# would start a server of its own, and the hub refuses a server it did not
# start (DECISIONS 5.8). So the collection afterwards goes through a second,
# short hub run that sets no reverse, so the app cannot reconnect, and whose
# server dies with it.
export MSYS_NO_PATHCONV=1
sh C:/Dev/Exo/src/tools/preflight.sh || exit 1
S=$1; TAG=$2; SERIAL=$3; CONTENT=${4:-moving}; RELAUNCH=${5:-0}; RATE=${6:-30}; SMOOTH=${7:-0}
ROLE="--es role display --ei rate $RATE --es orientation landscape --es size full --ei smoothing $SMOOTH $PHONEARGS"
STATIC=; [ "$CONTENT" = static ] && STATIC=-Static
[ "$CONTENT" = fast ] && STATIC=-Fast
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
        # A foreign adb on 5037 no longer ends the hub; it runs on without.
        grep -q "adb REFUSED" $LOG && { echo "hub refused adb:"; grep REFUSED $LOG; kill $HUBPID; exit 1; }
        sleep 0.2
    done
}

hub $T/hub.log --seconds $S $HUBARGS
# An asleep phone never shows the app, so it never connects.
adb -s $SERIAL shell input keyevent KEYCODE_WAKEUP
adb -s $SERIAL shell am start -S -n exo.phone/.MainActivity $ROLE >/dev/null
# The EXO monitor exists once the phone connects; the content goes on it then.
until grep -q "vdisp EXO0001 on the desktop" $T/hub.log; do
    kill -0 $HUBPID 2>/dev/null || { echo "the phone never connected"; exit 1; }
    sleep 0.2
done
if [ "$CONTENT" = browser ]; then
    # Edge on its own profile, so it starts a new process rather than
    # joining a running one; it is ended by that profile after the run.
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" --user-data-dir="$TEMP/exo-edge" \
        --no-first-run --window-position=2560,0 --kiosk file:///C:/Dev/Exo/src/tools/content60.html \
        > $T/content.log 2>&1 &
else
    powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/vddcontent.ps1 -Seconds $S $STATIC > $T/content.log 2>&1 &
fi
# logcat streams for the whole run rather than being cleared and dumped:
# a clear raced the hub's launch, and the tablet's buffer wrapped before the
# dump. -T replays from a device time, so lines logged before the stream
# attaches are kept. 3s before now covers the launch; the previous run's
# lines are older than that.
START=$(( $(adb -s $SERIAL wait-for-device shell date +%s | tr -d '\r') - 3 ))
adb -s $SERIAL logcat -T $START.000 -s ExoPhone:V AndroidRuntime:E > $T/logcat-live.txt 2>/dev/null &
LOGPID=$!
# The app connects once the hub has set its reverse. The live
# connection writes its CSV only when it ends, so old ones can go now.
adb -s $SERIAL shell "rm -f $FILES/*-link-*.csv"
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -disable -clear >/dev/null
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -enable >/dev/null
# Each relaunch is a new connection on whatever the EXO monitor shows. -S kills the
# running app, so the connection it ends leaves no CSV; logcat has its times.
for i in $(seq 1 $RELAUNCH); do
    sleep $((S / (RELAUNCH + 1)))
    adb -s $SERIAL shell input keyevent KEYCODE_WAKEUP
    adb -s $SERIAL shell am start -S -n exo.phone/.MainActivity $ROLE >/dev/null
done
wait $HUBPID; sleep 2
[ "$CONTENT" = browser ] && powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='msedge.exe'\" | Where-Object { \$_.CommandLine -like '*exo-edge*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }"
# The stream ends with the hub's server, possibly before the phone logged
# its end-of-connection stats; the dump below catches that tail.
kill $LOGPID 2>/dev/null; wait $LOGPID 2>/dev/null

hub $T/collect.log --no-reverse --seconds 120
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -dump > $T/ts.txt
adb -s $SERIAL shell dumpsys SurfaceFlinger --timestats -disable >/dev/null
adb -s $SERIAL logcat -d -T $START.000 -s ExoPhone:V AndroidRuntime:E > $T/logcat-tail.txt
# Both start at the same time; every line begins with its timestamp.
grep -h -v '^-' $T/logcat-live.txt $T/logcat-tail.txt | sort -u > $T/logcat.txt
rm $T/logcat-live.txt $T/logcat-tail.txt
adb -s $SERIAL shell am force-stop exo.phone
for f in $(adb -s $SERIAL shell "ls $FILES" | tr -d '\r' | grep -- '-link-'); do adb -s $SERIAL pull $FILES/$f $T/ >/dev/null; done
kill $HUBPID 2>/dev/null; wait $HUBPID 2>/dev/null

grep -E "ended|done|ms n=" $T/logcat.txt | cut -c 32-
grep -E "sender|encoded->|write blocked" $T/hub.log
for c in $T/*-link-[0-9].csv; do
    "$JAVA" C:/Dev/Exo/src/tools/LinkTimes.java $c ${c%.csv}-pongs.csv | grep -v outlier
done
