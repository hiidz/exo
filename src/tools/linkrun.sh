#!/bin/sh
# One live run over the link (DECISIONS 5.7). Moving content on the VDD,
# the hub for N seconds, the app connected by hand, SurfaceFlinger
# timestats around it, then the phone's CSVs pulled and LinkTimes run on
# them. Needs the hub built in Release and ExoPhone installed.
# Usage: linkrun.sh <seconds> <tag>
export MSYS_NO_PATHCONV=1
S=$1; TAG=$2
# Output is local evidence, gitignored with the rest of testdata.
T=C:/Dev/Exo/src/ExoPhone/testdata/link/$TAG; mkdir -p $T
SERIAL=$(adb get-serialno | tr -d "[:space:]")
HUB=C:/Dev/Exo/src/ExoHub/bin/Release/net10.0-windows/ExoHub.exe
JAVA="C:/Program Files/Android/Android Studio/jbr/bin/java"
FILES=/storage/emulated/0/Android/data/exo.phone/files

adb shell am force-stop exo.phone
adb shell "rm -f $FILES/*-link-*.csv"
adb reverse localabstract:exo tcp:27183 >/dev/null
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Dev/Exo/src/tools/vddcontent.ps1 -Seconds $((S + 15)) > $T/content.log 2>&1 &
sleep 3
adb logcat -c
adb shell dumpsys SurfaceFlinger --timestats -disable -clear >/dev/null
adb shell dumpsys SurfaceFlinger --timestats -enable >/dev/null
"$HUB" --seconds $S > $T/hub.log 2>&1 &
HUBPID=$!
sleep 1
adb shell am start -S -n exo.phone/.MainActivity --es serial $SERIAL >/dev/null
wait $HUBPID; sleep 2
adb shell dumpsys SurfaceFlinger --timestats -dump > $T/ts.txt
adb shell dumpsys SurfaceFlinger --timestats -disable >/dev/null
adb logcat -d -s ExoPhone:V AndroidRuntime:E > $T/logcat.txt
adb shell am force-stop exo.phone
for f in $(adb shell "ls $FILES" | tr -d '\r' | grep -- '-link-'); do adb pull $FILES/$f $T/ >/dev/null; done
grep -E "ended|done|ms n=" $T/logcat.txt | cut -c 32-
grep -E "sender|encoded->|write blocked" $T/hub.log
for c in $T/*-link-[0-9].csv; do
    "$JAVA" C:/Dev/Exo/src/tools/LinkTimes.java $c ${c%.csv}-pongs.csv | grep -v outlier
done
