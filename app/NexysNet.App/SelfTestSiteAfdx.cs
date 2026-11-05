using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.Core.Arinc664;
using NexysView.Core.Mil1553;

namespace NexysNet.App;

/// <summary>
/// --site with a profile that has an "afdx" section (docs/site-profile.md): N0 packet-capture library and ports (roles A / B),
/// N1 receive-only capture of the peer for listenSeconds → checks + interop checklist, N2 (txVls given) transmit those VLs for
/// sendSeconds while capturing — own frames seen back, sequence numbers and the BAG measured on the capture's timestamps — and N3
/// (n3Minutes &gt; 0) a long receive run: frames per minute, sequence-number losses, memory growth. Writes afdx_listen.pcapng,
/// afdx_send.pcapng, afdx_long.pcapng, interop-report.md and afdx_summary.json into --out. Nothing is transmitted unless the profile
/// lists txVls. expectPeer false = a bench without a peer (one port looped back to itself).
/// </summary>
public static partial class SelfTest
{
    static async Task<bool> SiteAfdx(MainWindow w, SiteProfile prof)
    {
        var a = prof.Afdx!; var e = w.Engine; var sum = new Dictionary<string, object?>();
        // ---- N0 library, ports, roles
        bool lib = Pcap.Available;
        GB("afdx_site_library", lib, lib ? Pcap.LibraryPath : "no packet-capture library on this PC (" + Pcap.LastError + ") — install Npcap separately, then run again; the AFDX steps are skipped");
        if (!lib) return false;
        var ports = Pcap.Ports(); sum["ports"] = ports;
        L("ports: " + string.Join(" | ", ports.Select(p => $"{p.Name} [{p.Id}]")));
        string? Find(string want) => want.Length == 0 ? null : ports.FirstOrDefault(p => p.Id == want || p.Name == want || p.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || want.Length > 4 && p.Id.Contains(want, StringComparison.OrdinalIgnoreCase))?.Id ?? want;
        var roles = NetworkRoles.Assign(ports, Find(a.NicA), Find(a.NicB));
        string PortName(string? id) => id == null ? "-" : ports.FirstOrDefault(p => p.Id == id)?.Name ?? id;
        GB("afdx_site_ports", roles.NicA != null || roles.NicB != null, $"A {PortName(roles.NicA)} · B {PortName(roles.NicB)}" + (roles.Notes.Count > 0 ? " · " + string.Join(" · ", roles.Notes) : ""));
        if (roles.NicA == null && roles.NicB == null) return false;
        sum["roles"] = new { a = PortName(roles.NicA), b = PortName(roles.NicB), roles.SharedPort };
        // project for the checks / report
        e.NewProject(prof.Name.Length > 0 ? prof.Name : "site", "arinc664");
        if (a.Icd.Length > 0 && File.Exists(a.Icd)) e.Icd.LoadIcd(a.Icd);
        var s = e.Project.Afdx!; s.NicA = roles.NicA ?? ""; s.NicB = roles.NicB ?? ""; s.JmaxMs = a.JmaxMs; s.SkewMaxMs = a.SkewMaxMs; s.PeerName = a.PeerName;
        var icdVls = e.Project.Icd.Vls ?? new(); GB("afdx_site_icd", icdVls.Count > 0, $"{a.Icd}: {icdVls.Count} VLs");
        // ---- N1 receive only
        var cap = await CaptureFor(new PcapNetworkDevice(roles.NicA, roles.NicB), null, a.ListenSeconds);
        string p1 = Path.Combine(OutDir, "afdx_listen.pcapng"); cap.Save(p1); e.Afdx.OpenCapture(p1);
        var an = e.Afdx.Analysis!; var expect = !a.ExpectPeer ? new List<int>() : a.ExpectVls.Count > 0 ? a.ExpectVls : icdVls.Select(v => v.VlId).Where(id => !a.TxVls.Contains(id)).ToList();
        var missing = expect.Where(id => !an.Vls.ContainsKey(id)).ToList();
        GB("afdx_site_listen", cap.Count > 0 && missing.Count == 0, $"{a.ListenSeconds:G} s: {cap.Count:N0} frames (IPv4 {an.Ipv4:N0}: UDP {an.Udp:N0} · TCP {an.Tcp:N0}; ARP {an.Arp} · ICMP {an.Icmp} · other {an.Other}), {an.Vls.Count} VLs, findings {an.TotalErrors}; " + (a.ExpectPeer ? $"expected VLs not received: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}" : "no peer expected (loopback bench)"));
        var checks = e.Afdx.PeerChecks(); foreach (var c in checks) L($"N1 {c.Id} {c.Verdict}: {c.Detail}");
        sum["listen"] = new { frames = cap.Count, ipv4 = an.Ipv4, udp = an.Udp, tcp = an.Tcp, arp = an.Arp, vls = an.Vls.Count, findings = an.TotalErrors, checks };
        // ---- N2 transmit (only the listed VLs) while capturing
        if (a.TxVls.Count > 0)
        {
            var tx = icdVls.Where(v => a.TxVls.Contains(v.VlId)).ToList();
            var dev = new PcapNetworkDevice(roles.NicA, roles.NicB); var snd = new AfdxSender(dev, tx, s.SourcesById());
            var cap2 = await CaptureFor(dev, snd, a.SendSeconds);
            string p2 = Path.Combine(OutDir, "afdx_send.pcapng"); cap2.Save(p2);
            long sent = snd.Vls.Sum(v => v.Sent[0] + v.Sent[1]), att = snd.Vls.Sum(v => v.Attempted[0] + v.Attempted[1]); var own = new AfdxAnalysis(cap2, new IcdHub { Vls = tx }, s.CheckOptions());
            long seen = tx.Sum(v => own.Vls.TryGetValue(v.VlId, out var st) ? st.Frames[0] + st.Frames[1] : 0);
            long gaps = own.Vls.Values.Where(v => a.TxVls.Contains(v.Vl)).Sum(v => v.SnGaps[0] + v.SnGaps[1] + v.SnReversed[0] + v.SnReversed[1]);
            long bagV = own.Vls.Values.Where(v => a.TxVls.Contains(v.Vl)).Sum(v => v.BagViolations[0] + v.BagViolations[1]);
            var (p50, p99, max, n) = BagDev(cap2, tx);
            GB("afdx_site_send", sent > 0 && dev.Counters.SendErrors == 0, $"{a.SendSeconds:G} s: {att:N0} frames handed to the port on {tx.Count} VLs, accepted {sent:N0}, send errors {dev.Counters.SendErrors}" + (dev.LastSendError.Length > 0 ? $" — the port's driver: '{dev.LastSendError}' (nothing went on the wire)" : ""));
            if (!a.ExpectPeer) GB("afdx_site_loopback", seen == att && gaps == 0, $"own frames seen back on the capture {seen:N0} of {att:N0} handed to the port, SN gaps/reversed {gaps}" + (sent == 0 && seen > 0 ? " (looped back inside the packet-capture library, not from the wire)" : ""));
            G("afdx_site_send_bag_dev_p50_ms", p50, "ms"); G("afdx_site_send_bag_dev_p99_ms", p99, "ms", "≤ 0.5"); G("afdx_site_send_bag_dev_max_ms", max, "ms"); G("afdx_site_send_bag_violations", bagV, "intervals", "0");
            GB("afdx_site_send_bag", n > 0 && bagV == 0, FormattableString.Invariant($"capture timestamps of the own VLs: |interval − BAG| p50 {p50:F3} · p99 {p99:F3} · max {max:F3} ms over {n:N0} intervals; intervals < BAG − Jmax {bagV}; sender slips {snd.Vls.Sum(v => v.Slips)}"));
            sum["send"] = new { attempted = att, sent, sendErrors = dev.Counters.SendErrors, portError = dev.LastSendError, seen, gaps, bagViolations = bagV, p50, p99, max, vls = snd.Vls.Select(v => new { vl = v.Vl.VlId, sentA = v.Sent[0], sentB = v.Sent[1], firstSnA = v.FirstSn[0], lastSnA = v.LastSn[0], meanMs = v.IntMeanMs, lateStdUs = v.LateStdUs, slips = v.Slips }).ToList() };
        }
        // ---- N3 long receive run
        if (a.N3Minutes > 0)
        {
            var tx = a.N3Send ? icdVls.Where(v => a.TxVls.Contains(v.VlId)).ToList() : new List<IcdVl>();
            var dev = new PcapNetworkDevice(roles.NicA, roles.NicB); var snd = tx.Count > 0 ? new AfdxSender(dev, tx, s.SourcesById()) : null;
            GC.Collect(); GC.WaitForPendingFinalizers(); long ws0 = WsMb(); var wsPerMin = new List<long>();
            var cap3 = await CaptureFor(dev, snd, a.N3Minutes * 60, () => wsPerMin.Add(WsMb()));
            GC.Collect(); GC.WaitForPendingFinalizers(); long ws1 = WsMb();
            string p3 = Path.Combine(OutDir, "afdx_long.pcapng"); cap3.Save(p3);
            var a3 = new AfdxAnalysis(cap3, e.Project.Icd.IsAfdx ? e.Project.Icd : null, s.CheckOptions());
            long t0 = cap3.FirstNs; var perMin = Enumerable.Range(0, cap3.Count).GroupBy(i => (int)((cap3.TimeNs(i) - t0) / 60_000_000_000L)).OrderBy(g => g.Key)
                .Select(g => (min: g.Key, frames: g.Count(), vl: g.Count(i => cap3.Vl(i) >= 0))).ToList();
            foreach (var m in perMin) L($"N3 min {m.min}: {m.frames:N0} frames, {m.vl:N0} VL frames");
            long loss = a3.Vls.Values.Sum(v => v.SnGaps[0] + v.SnGaps[1] + v.SnReversed[0] + v.SnReversed[1]);
            long sentN3 = snd?.Vls.Sum(v => v.Attempted[0] + v.Attempted[1]) ?? 0, ownN3 = tx.Sum(v => a3.Vls.TryGetValue(v.VlId, out var st) ? st.Frames[0] + st.Frames[1] : 0);
            double growthPerMin = wsPerMin.Count > 2 ? (wsPerMin[^1] - wsPerMin[1]) / (double)Math.Max(1, wsPerMin.Count - 2) : 0;
            G("afdx_site_n3_frames", cap3.Count, "frames"); G("afdx_site_n3_ws_growth_mb_per_min", growthPerMin, "MB/min"); G("afdx_site_n3_sn_losses", loss, "frames", "0");
            bool everyMinute = perMin.Count >= (int)a.N3Minutes && perMin.All(m => m.frames > 0);
            GB("afdx_site_long", everyMinute && loss == 0 && (snd == null || ownN3 == sentN3), $"{a.N3Minutes:G} min: {cap3.Count:N0} frames in {perMin.Count} minutes (min {perMin.Min(m => m.frames):N0} / max {perMin.Max(m => m.frames):N0} per minute), VLs {a3.Vls.Count}, SN gaps + reversed {loss}" +
                (snd != null ? $", own frames sent {sentN3:N0} / seen {ownN3:N0}, sender slips {snd.Vls.Sum(v => v.Slips)}" : "") + $"; working set {ws0} → {ws1} MB (capture buffer included; per minute {string.Join(" ", wsPerMin)} MB)");
            sum["long"] = new { minutes = a.N3Minutes, frames = cap3.Count, perMinute = perMin.Select(m => new { m.min, m.frames, m.vl }).ToList(), snLoss = loss, sent = sentN3, ownSeen = ownN3, ws0, ws1, wsPerMin };
        }
        e.Afdx.OpenCapture(p1);
        string rep = e.Afdx.ExportReport(Path.Combine(OutDir, "interop-report.md")); L("report " + rep);
        File.WriteAllText(Path.Combine(OutDir, "afdx_summary.json"), JsonSerializer.Serialize(sum, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    /// <summary>|interval − BAG| of each VL's frames (its first network) on a capture's timestamps: p50, p99, max (ms) and the interval count.</summary>
    static (double p50, double p99, double max, int n) BagDev(AfdxCapture cap, List<IcdVl> vls)
    {
        var d = new List<double>();
        foreach (var v in vls)
        {
            int net = v.Network == "B" ? 1 : 0; long last = long.MinValue;
            for (int i = 0; i < cap.Count; i++) { if (cap.Vl(i) != v.VlId || cap.Network(i) != net) continue; long t = cap.TimeNs(i); if (last != long.MinValue) d.Add(Math.Abs((t - last) / 1e6 - v.BagMs)); last = t; }
        }
        if (d.Count == 0) return (double.NaN, double.NaN, double.NaN, 0);
        d.Sort(); return (d[d.Count / 2], d[Math.Min(d.Count - 1, (int)(0.99 * d.Count))], d[^1], d.Count);
    }

    static async Task<AfdxCapture> CaptureFor(INetworkDevice dev, AfdxSender? snd, double seconds, Action? everyMinute = null)
    {
        var cap = new AfdxCapture(1 << 24); dev.Open(); dev.Start(); snd?.Start(); var sw = Stopwatch.StartNew(); int minute = 0;
        try
        {
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(50); dev.Poll((n, t, f) => cap.Add(n, t, f));
                if (everyMinute != null && (int)(sw.Elapsed.TotalSeconds / 60) > minute) { minute++; everyMinute(); }
            }
        }
        finally { snd?.Stop(); await Task.Delay(100); dev.Poll((n, t, f) => cap.Add(n, t, f)); dev.Dispose(); }
        cap.EnsureSorted();
        return cap;
    }
}
