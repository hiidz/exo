#!/bin/sh
# Recovery from one hub.log: each disturbance and how long its
# service took to come back. runcheck.sh does not fit a run like this: it
# counts every LOST and DEPARTED as a fault.
# Usage: sc10check.sh <run dir>
#
# Display: losses less than 2s apart are one event, since a lock or a
# resolution change gives two or three. An event runs from its first loss
# to its last recovery, so a lock's includes the time the lock screen was up.
# Replug: from the device listed as "device" again to that role's first
# frame. A phone's first arrival in the run is its start, not a replug.
D=${1:?usage: sc10check.sh <run dir>}
awk '
function flush() { if (ev) printf "display event %2d at %8.3fs: %d losses, back at %8.3fs, %6.0fms\n", ev, start, n, end, (end - start) * 1000 }
/ LOST #/ { t = $1 + 0; if (t - last > 2) { flush(); ev++; start = t; n = 0 } n++; last = t }
/ recovered after / { end = $1 + 0 }
/ track .* DEPARTED/ { s = $3; gone[s]++
    if (pend[s]) printf "replug %s #%d: pulled again %.0fms after it returned, before a first frame\n", s, gone[s] - 1, ($1 - back[s]) * 1000
    pend[s] = 0 }
/ track .* -> device$/ { s = $3; if (gone[s]) { back[s] = $1 + 0; pend[s] = 1 } }
/ camera first frame:/ || / first frame sent/ { s = $3
    if (pend[s]) { printf "replug %s #%d: first frame %.0fms after the device returned\n", s, gone[s], ($1 - back[s]) * 1000; pend[s] = 0 } }
END { flush() }
' "$D/hub.log"
# The other service while one was disturbed: every camera window, and how
# many fell short of 29fps or had a sequence gap. A window spanning a camera
# replug is short by design; the hub log has which.
grep -E "link [^ ]+ camera last 5s" "$D/hub.log" | awk '{ n++; f = $9 + 0; g = $(NF-2) + 0; if (f < 29) low++; gaps += g }
    END { printf "camera windows: %d, under 29fps: %d, seq gaps: %d\n", n, low, gaps }'
