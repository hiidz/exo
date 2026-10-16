// Per-frame stage times across the link, from one connection's
// two CSVs (Decoder and Link). Relates the clocks from the PONGs, as NTP
// does: theta = hub - phone, fitted as a + b*t over the
// lowest-delay sample in each window.
//
// Usage: java LinkTimes.java <frames.csv> <pongs.csv> [windowSeconds]

import java.nio.file.*;
import java.util.*;

public class LinkTimes {
    public static void main(String[] a) throws Exception {
        double windowS = a.length > 2 ? Double.parseDouble(a[2]) : 10;

        // PONGs, all in microseconds: phone times are nanoTime / 1000.
        List<double[]> p = new ArrayList<>(); // t (phone us), theta, delay
        for (String l : Files.readAllLines(Path.of(a[1])).subList(1, Files.readAllLines(Path.of(a[1])).size())) {
            String[] f = l.split(",");
            double t1 = Long.parseLong(f[0]) / 1e3, t2 = Long.parseLong(f[1]), t3 = Long.parseLong(f[2]),
                    t4 = Long.parseLong(f[3]) / 1e3;
            p.add(new double[] {t4, ((t2 - t1) + (t3 - t4)) / 2, (t4 - t1) - (t3 - t2)});
        }
        double[] delays = p.stream().mapToDouble(x -> x[2]).toArray();
        System.out.printf("pongs %d, delay us %s%n", p.size(), spread(delays));

        // Lowest delay per window.
        double t0 = p.get(0)[0];
        TreeMap<Long, double[]> best = new TreeMap<>();
        for (double[] x : p) {
            long w = (long) ((x[0] - t0) / (windowS * 1e6));
            if (!best.containsKey(w) || x[2] < best.get(w)[2]) best.put(w, x);
        }
        // Least squares theta = A + B * (t - t0).
        double n = best.size(), sx = 0, sy = 0, sxx = 0, sxy = 0, maxHalfDelay = 0;
        for (double[] x : best.values()) {
            double t = x[0] - t0;
            sx += t; sy += x[1]; sxx += t * t; sxy += t * x[1];
            maxHalfDelay = Math.max(maxHalfDelay, x[2] / 2);
        }
        double B = n > 1 ? (n * sxy - sx * sy) / (n * sxx - sx * sx) : 0, A = (sy - B * sx) / n;
        double maxResid = 0;
        for (double[] x : best.values()) maxResid = Math.max(maxResid, Math.abs(x[1] - (A + B * (x[0] - t0))));
        double maxResidAll = 0;
        for (double[] x : p) maxResidAll = Math.max(maxResidAll, Math.abs(x[1] - (A + B * (x[0] - t0))));
        System.out.printf("fit over %d windows of %.0fs: drift %.1f ppm (%.2f ms over the run), "
                        + "residual max %.0f us (all pongs %.0f us), error bound (half delay of fitted samples) %.0f us%n",
                (int) n, windowS, B * 1e6, B * (p.get(p.size() - 1)[0] - t0) / 1e3, maxResid, maxResidAll, maxHalfDelay);

        // Frames.
        List<String> rows = Files.readAllLines(Path.of(a[0]));
        String[] h = rows.get(0).split(",");
        Map<String, Integer> col = new HashMap<>();
        for (int i = 0; i < h.length; i++) col.put(h[i], i);
        List<Double> p2e = new ArrayList<>(), e2s = new ArrayList<>(), s2r = new ArrayList<>(),
                r2q = new ArrayList<>(), q2o = new ArrayList<>(), p2r = new ArrayList<>(), p2o = new ArrayList<>(),
                p2q = new ArrayList<>(),
                s2rKey = new ArrayList<>();
        for (String l : rows.subList(1, rows.size())) {
            String[] f = l.split(",", -1);
            double present = Long.parseLong(f[col.get("present_us")]), encoded = Long.parseLong(f[col.get("encoded_us")]),
                    sent = Long.parseLong(f[col.get("sent_us")]);
            double recv = Long.parseLong(f[col.get("recv_ns")]) / 1e3, queue = Long.parseLong(f[col.get("queue_ns")]) / 1e3;
            long outNs = Long.parseLong(f[col.get("out_ns")]);
            double theta = A + B * (recv - t0);
            p2e.add((encoded - present) / 1e3);
            e2s.add((sent - encoded) / 1e3);
            double transport = (recv - (sent - theta)) / 1e3;
            s2r.add(transport);
            if (f[col.get("idr")].equals("1")) s2rKey.add(transport);
            if (transport > 30 || (queue - recv) / 1e3 > 30)
                System.out.printf("  outlier frame %s seq %s idr %s: transport %.1f ms, received->queued %.1f ms, at %.2fs%n",
                        f[0], f[col.get("seq")], f[col.get("idr")], transport, (queue - recv) / 1e3, (recv - t0) / 1e6);
            r2q.add((queue - recv) / 1e3);
            p2r.add((recv - (present - theta)) / 1e3);
            p2q.add((queue - (present - theta)) / 1e3);
            if (outNs >= 0) {
                q2o.add((outNs / 1e3 - queue) / 1e3);
                p2o.add((outNs / 1e3 - (present - theta)) / 1e3);
            }
        }
        System.out.printf("frames %d%n", rows.size() - 1);
        System.out.printf("  present->encoded   ms %s   (hub clock)%n", spread(p2e));
        System.out.printf("  encoded->sent      ms %s   (hub clock)%n", spread(e2s));
        System.out.printf("  sent->received     ms %s   (across, transport)%n", spread(s2r));
        System.out.printf("    keyframes only   ms %s%n", spread(s2rKey));
        System.out.printf("  received->queued   ms %s   (phone clock)%n", spread(r2q));
        System.out.printf("  queued->output     ms %s   (phone clock)%n", spread(q2o));
        System.out.printf("  present->received  ms %s   (across)%n", spread(p2r));
        System.out.printf("  present->queued    ms %s   (across)%n", spread(p2q));
        System.out.printf("  present->output    ms %s   (across)%n", spread(p2o));
    }

    static String spread(List<Double> v) {
        return spread(v.stream().mapToDouble(Double::doubleValue).toArray());
    }

    static String spread(double[] v) {
        if (v.length == 0) return "n=0";
        double[] s = v.clone();
        Arrays.sort(s);
        return String.format("n=%d min=%.2f median=%.2f p95=%.2f p99=%.2f max=%.2f", s.length, s[0],
                s[s.length / 2], s[(int) (s.length * 0.95)], s[(int) (s.length * 0.99)], s[s.length - 1]);
    }
}
