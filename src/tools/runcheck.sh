#!/bin/sh
# Checks a hub run against the stall and frame rate criteria first written
# for SC-11 (spike 4 FINDINGS, Notes added 2026-10-13). One line per
# criterion: PASS, FAIL, or NOTE when there is nothing to judge.
# Usage: runcheck.sh <run dir>
# Reads hub.log, and if present the display CSV (<run>-link-N.csv),
# page.log from campage.html, and logcat-<serial>.txt with the camera
# role's per-second lines. Parts a run lacks are skipped.
#
# Limits: the thresholds (29fps, 145 frames per 5s, 500ms) are this
# script's, not PROJECT_SPEC's. The first window after a connect is
# skipped, since it always reads about 27fps. Chrome's 5s timer jitters, so
# the page is judged on its fps field, not its frame count. Only a run with
# a single connection per role reads correctly; a replug restarts windows.
D=${1:?usage: runcheck.sh <run dir>}
cd "$D" || exit 1
[ -f hub.log ] || { echo "no hub.log in $D"; exit 1; }
verdict() { [ "$1" = 0 ] && echo "PASS  $2" || echo "FAIL  $2"; }

echo "== $(grep -m1 '^totals' hub.log | cut -d' ' -f1-2)"

if grep -q "camera last 5s:" hub.log; then
    grep -E "link [^ ]+ camera last 5s" hub.log | awk '{t=$1+0; fps=$9+0; g=$(NF-2)+0; n++
        if (n>1) { if (min=="" || fps<min) min=fps; if (fps<29) low++; if (t-pt>mx) mx=t-pt }
        gaps+=g; pt=t; last=fps }
        END { bad = (low>0) + (gaps>0) + (mx>10)
              printf "%d camera phone to hub: %d windows, min %.1ffps after the first, %d under 29, %d seq gaps, longest between windows %.2fs, last %.1ffps\n", bad, n, min, low, gaps, mx, last }' |
        { read b rest; verdict $b "$rest"; }
    grep "camera decode last 5s" hub.log | awk '{n++; if ($6!=$8) ne++; disc+=$10}
        END { printf "%d hub decode: %d windows, %d with in != out, %d discarded\n", (ne>0)+(disc>0), n, ne, disc }' |
        { read b rest; verdict $b "$rest"; }
    if grep -qE "camera pid [0-9]+ \(.*\) last 5s" hub.log; then
        grep -E "camera pid [0-9]+ \(.*\) last 5s" hub.log | awk '{n++; f=$8+0; s+=$10
            if (n>1) { if (min=="" || f<min) min=f; if (f<145) low++ } }
            END { printf "%d hub to camera consumers: %d windows, min %d frames after the first, %d under 145, %d superseded\n", (low>0)+(s>0), n, min, low, s }' |
            { read b rest; verdict $b "$rest"; }
    else
        echo "NOTE  no camera consumer connected"
    fi
fi

for f in logcat-*.txt; do
    [ -f "$f" ] && grep -q "camera last second" "$f" || continue
    grep "camera last second" "$f" | awk -v f="$f" '{for (i=1; i<=NF; i++) { if ($i=="second:") fr=$(i+1)+0; if ($i=="dropped") d=$(i+1)+0 }
        n++; if (n>2) { if (min=="" || fr<min) min=fr; if (fr<29) low++ }; dr+=d }
        END { printf "%d phone camera (%s): %d seconds, min %d frames after warm-up, %d under 29, %d dropped\n", (low>0)+(dr>0), f, n, min, low, dr }' |
        { read b rest; verdict $b "$rest"; }
done

if [ -f page.log ]; then
    # The page that last opened the camera; others are left over from earlier runs.
    # Pages from before 2026-10-13 carry no ID; then every line counts.
    if grep -q '"ev":"open"' page.log; then
        P=$(grep '"ev":"open"' page.log | tail -1 | sed -n 's/.*"page":"\([a-z0-9]*\)".*/"page":"\1"/p')
        grep -F "$P" page.log | grep '"state":"live"' | sed 's/.*"fps":\([0-9.]*\).*/\1/' |
            awk '{n++; if (n>1) { if (min=="" || $1<min) min=$1; if ($1<29) low++ } }
            END { printf "%d chrome page: %d live windows, min %.2ffps after the first, %d under 29\n", (low>0)+(n<2), n, min, low }' |
            { read b rest; verdict $b "$rest"; }
        early=$(grep -F "$P" page.log | grep -cE '"ev":"(error|mute)"')
        verdict $((early > 0)) "chrome page: $early error or mute events"
    else
        echo "NOTE  page.log has no page that opened the camera"
    fi
fi

# A closed line within 5s of the end is the normal stop.
end=$(awk '/^totals/{print $2+0}' hub.log)
faults=$(grep -cE "REFUSED|SERVER DIED|tracking ended|STALLED|has failed|STOPPED|LOST #|RESEND .*heartbeat|write failed|IDLE sent|DEPARTED" hub.log)
early=$(grep -E "closed" hub.log | awk -v e="${end:-0}" '$1+0 < e-5' | wc -l)
verdict $((faults + early)) "hub fault lines: $faults, connections closed before the end: $early"
grep -E "REFUSED|SERVER DIED|tracking ended|STALLED|has failed|STOPPED|LOST #|RESEND .*heartbeat|write failed|IDLE sent|DEPARTED" hub.log | head -5 | sed 's/^/      /'

csv=$(ls *-link-*.csv 2>/dev/null | grep -vE -- '-(cursor|pongs)\.csv$' | head -1)
if [ -n "$csv" ]; then
    awk -F, 'NR>1 && $14>0 { n++; t=$14/1e9; if (n==1) t0=t; w=int((t-t0)/5); W[w]++; if (w>mw) mw=w
        if (pt) { g=(t-pt)*1000; if (g>gm) gm=g; if (g>100) g1++; if (g>500) g5++ }
        pt=t; if (ps!="" && $8-ps>1) sg+=$8-ps-1; ps=$8; q[n]=$6 }
        END { if (n<2) { printf "1 display: %d frames decoded\n", n; exit }
              for (i=1; i<mw; i++) { c=W[i]+0; if (min=="" || c<min) min=c; if (c<145) low++ }
              printf "%d display stalls: %d outputs, %.2ffps over %.1fs, %d gaps over 500ms, longest %.1fms, %d seq gaps\n", (g5>0)+(sg>0), n, (n-1)/(t-t0), t-t0, g5, gm, sg
              printf "%d display falls: %d full 5s windows, min %d, %d under 145, last full window %d; %d gaps over 100ms\n", (low>0), mw-1, min, low, W[mw-1], g1
              asort(q); printf "N display decode queue to output: median %.2fms, p95 %.2f, max %.2f\n", q[int(n/2)], q[int(n*0.95)], q[n] }' "$csv" |
        while read b rest; do
            if [ "$b" = N ]; then echo "NOTE  $rest"; else verdict $b "$rest"; fi
        done
else
    echo "NOTE  no display CSV"
fi
