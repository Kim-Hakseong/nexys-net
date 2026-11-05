using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NexysView.Core.Arinc664;
using NexysView.Core.Mil1553;

namespace NexysNet.App;

/// <summary>
/// --interop [--transport local|nic] [--nic "port name"] [--seconds 20] [--icd file]: two NexysNet instances on one PC.
/// This instance is B (ANALYZE › LISTEN, receive only). It starts instance A as a second process (--interop-tx: SIMULATE › START on the
/// same project), then compares what A sent with what B received: per VL and network the frame count and the first / last sequence
/// number (N/N), sequence-number losses (0), the BAG measured on B's receive clock. B writes the per-VL counts as a peer CSV, A imports
/// it into its INTEROP table (NexysNet → peer) and exports its report; B exports the peer → NexysNet report. Both reports together:
/// interop-sample.md. Transport "local" = shared virtual network (loopback multicast, nothing leaves the PC), "nic" = one Ethernet /
/// Wi-Fi port through the packet-capture library (A sends, B captures on the same port).
/// </summary>
public static partial class SelfTest
{
    static async Task Interop(MainWindow w)
    {
        var e = w.Engine; var ic = CultureInfo.InvariantCulture; string transport = Arg("--transport", "local"); double secs = double.Parse(Arg("--seconds", "20"), ic);
        string dirA = Path.Combine(OutDir, "A"); Directory.CreateDirectory(dirA); string peerCsv = Path.Combine(OutDir, "peer-report.csv"); if (File.Exists(peerCsv)) File.Delete(peerCsv);
        foreach (var f in new[] { "tx.json", "interop-report-A.md" }) if (File.Exists(Path.Combine(dirA, f))) File.Delete(Path.Combine(dirA, f));
        // ---- project (shared by A and B): ICD + device
        e.NewProject("interop", "arinc664");
        string icd = Arg("--icd", ""); if (icd.Length == 0) { icd = Path.Combine(OutDir, "interop_icd.csv"); File.WriteAllText(icd, AfdxIcd.ToCsv(new IcdHub { Schema = "1.1", Source = "interop", Vls = InteropVls(transport == "nic") })); }
        e.Icd.LoadIcd(icd); var s = e.Project.Afdx!;
        string nicName = "";
        if (transport == "nic")
        {
            if (!Pcap.Available) { GB("interop_transport", false, "nic transport: no packet-capture library (" + Pcap.LastError + ")"); return; }
            var ports = Pcap.Ports(); string want = Arg("--nic", ""); var port = ports.FirstOrDefault(p => p.Id == want || p.Name.Contains(want, StringComparison.OrdinalIgnoreCase) || want.Length > 4 && p.Id.Contains(want, StringComparison.OrdinalIgnoreCase));
            if (port == null || want.Length == 0) { GB("interop_transport", false, $"nic transport: port '{want}' not found among {string.Join(" | ", ports.Select(p => p.Name))}"); return; }
            s.NicA = s.NicB = port.Id; nicName = port.Name; e.Project.Device = "network";
        }
        else e.Project.Device = "local";
        string nxb = Path.Combine(OutDir, "interop_A.nxn"); e.SaveProject(nxb);
        var vls = e.Project.Icd.Vls!; L($"interop: transport {transport} {nicName}, {vls.Count} VLs, {secs:G} s, project {nxb}");
        // ---- B listens (ANALYZE › LISTEN on the real UI)
        await JS(w, "await nx.test.rail('ANALYZE'); await nx.test.afdx().an.click('listen'); return true;"); await Idle(w, 300);
        GB("interop_b_listening", e.Afdx.Running && e.Afdx.Listening, $"B: device {e.Project.Device}, listening {e.Afdx.Listening}, error '{e.Afdx.LastError}'");
        if (!e.Afdx.Running) return;
        // ---- A: a second NexysNet process, SIMULATE › START for secs
        string exe = Environment.ProcessPath!; var sw = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[] { "--interop-tx", "--out", dirA, "--project", nxb, "--seconds", secs.ToString(ic), "--peercsv", peerCsv }) psi.ArgumentList.Add(a);
        using var child = Process.Start(psi)!;
        string txJson = Path.Combine(dirA, "tx.json");
        while (!File.Exists(txJson) && !child.HasExited && sw.Elapsed.TotalSeconds < secs + 180) { await Task.Delay(200); }
        await Task.Delay(800);   // the last frames in flight
        GB("interop_a_ran", File.Exists(txJson), $"A (pid {child.Id}) {(File.Exists(txJson) ? "sent and stopped" : "did not report")} after {sw.Elapsed.TotalSeconds:F1} s");
        await JS(w, "await nx.test.afdx().an.click('stopListen'); return true;"); await Idle(w, 500);
        if (!File.Exists(txJson)) return;
        var tx = JsonDocument.Parse(File.ReadAllText(txJson)).RootElement; var cap = e.Afdx.Capture!; var an = e.Afdx.Analysis!;
        // ---- compare per VL / network: count + first / last SN
        var first = new Dictionary<(int, int), int>(); var last = new Dictionary<(int, int), int>();
        for (int i = 0; i < cap.Count; i++) { int vl = cap.Vl(i); if (vl < 0) continue; var k = (vl, cap.Network(i)); if (!first.ContainsKey(k)) first[k] = cap.Sn(i); last[k] = cap.Sn(i); }
        int ok = 0, tot = 0; var diffs = new List<string>(); var rows = new StringBuilder();
        long sentAll = 0, rxAll = 0, refused = 0;
        foreach (var v in tx.GetProperty("vls").EnumerateArray())
        {
            int vl = v.GetProperty("vl").GetInt32(); an.Vls.TryGetValue(vl, out var st);
            for (int n = 0; n < 2; n++)
            {
                long sent = v.GetProperty(n == 0 ? "sentA" : "sentB").GetInt64(), att = v.GetProperty(n == 0 ? "attemptedA" : "attemptedB").GetInt64(); if (att == 0 && (st?.Frames[n] ?? 0) == 0) continue;
                if (att > sent) { refused += att - sent; sent = att; }   // the port driver refused the frames but the capture library looped them back to B
                long got = st?.Frames[n] ?? 0; int f0 = v.GetProperty(n == 0 ? "firstSnA" : "firstSnB").GetInt32(), f1 = v.GetProperty(n == 0 ? "lastSnA" : "lastSnB").GetInt32();
                int g0 = first.GetValueOrDefault((vl, n), -1), g1 = last.GetValueOrDefault((vl, n), -1); tot++; sentAll += sent; rxAll += got;
                bool good = sent == got && f0 == g0 && f1 == g1; if (good) ok++; else diffs.Add($"VL {vl} {(n == 0 ? "A" : "B")}: sent {sent} SN {f0}..{f1}, received {got} SN {g0}..{g1}");
                rows.Append($"| {vl} | {(n == 0 ? "A" : "B")} | {sent} | {got} | {f0}..{f1} | {g0}..{g1} | {(good ? "PASS" : "FAIL")} |\n");
            }
        }
        string refusedNote = refused == 0 ? "" : $" — NOTE: the port refused {refused:N0} of them (send error '{tx.GetProperty("error").GetString()}'); the packet-capture library still looped every frame back to B, so this run checks NexysNet ↔ NexysNet through the library, not the wire";
        G("interop_port_send_errors", refused, "frames");
        GB("interop_vl_sn_match", ok == tot && tot > 0, $"{ok}/{tot} VL·network rows: frames sent = received and first/last SN equal (sent {sentAll:N0}, received {rxAll:N0}){refusedNote}" + (diffs.Count > 0 ? ": " + string.Join("; ", diffs.Take(6)) : ""));
        long gaps = an.Vls.Values.Sum(x => x.SnGaps[0] + x.SnGaps[1]), rev = an.Vls.Values.Sum(x => x.SnReversed[0] + x.SnReversed[1]), miss = an.Vls.Values.Sum(x => x.Missing[0] + x.Missing[1]);
        GB("interop_sn_missing_zero", gaps + rev + miss == 0, $"SN gaps {gaps} · reversed {rev} · missing on one network {miss}");
        var frag = an.Vls.Values.Where(x => x.Fragments > 0).ToList();
        if (frag.Count > 0) GB("interop_reassembly", frag.All(x => x.MsgIncomplete + x.MsgReordered == 0 && x.MsgComplete > 0), string.Join("; ", frag.Select(x => $"VL {x.Vl}: {x.MsgComplete} messages reassembled from {x.Fragments} fragments, incomplete {x.MsgIncomplete}, cut {x.MsgTruncated}")));
        // BAG measured on B's receive clock
        var dev = new List<double>(); long bagViol = 0; var perVl = new List<string>();
        foreach (var v in vls)
        {
            long lastT = long.MinValue; var d1 = new List<double>();
            for (int i = 0; i < cap.Count; i++) { if (cap.Vl(i) != v.VlId || cap.Network(i) != (v.Network == "B" ? 1 : 0)) continue; long t = cap.TimeNs(i); if (lastT != long.MinValue) d1.Add(Math.Abs((t - lastT) / 1e6 - v.BagMs)); lastT = t; }
            dev.AddRange(d1); if (an.Vls.TryGetValue(v.VlId, out var st)) { bagViol += st.BagViolations[0] + st.BagViolations[1]; perVl.Add(FormattableString.Invariant($"VL {v.VlId} BAG {v.BagMs} ms: mean {st.MeanIntervalMs(v.Network == "B" ? 1 : 0):F3} ms, min {st.IntervalMinMs[v.Network == "B" ? 1 : 0]:F3} ms")); }
        }
        dev.Sort(); double P(double q) => dev.Count == 0 ? double.NaN : dev[Math.Min(dev.Count - 1, (int)(q * dev.Count))];
        G("interop_bag_dev_p50_ms", P(0.5), "ms"); G("interop_bag_dev_p99_ms", P(0.99), "ms"); G("interop_bag_dev_max_ms", dev.Count > 0 ? dev[^1] : double.NaN, "ms"); G("interop_bag_violations", bagViol, "intervals");
        GB("interop_bag_measured", dev.Count > 0, FormattableString.Invariant($"|interval − BAG| on B's receive clock over {dev.Count:N0} intervals: p50 {P(0.5):F3} · p99 {P(0.99):F3} · max {(dev.Count > 0 ? dev[^1] : 0):F3} ms; intervals < BAG − Jmax: {bagViol}. ") + string.Join("; ", perVl));
        // ---- peer CSV for A (what B received), then A's report
        var csv = new StringBuilder("vl,rx_a,rx_b,errors,note\n");
        foreach (var v in vls) { an.Vls.TryGetValue(v.VlId, out var st); csv.Append(FormattableString.Invariant($"{v.VlId},{st?.Frames[0] ?? 0},{st?.Frames[1] ?? 0},{st?.Errors ?? 0},received by instance B\n")); }
        File.WriteAllText(peerCsv + ".tmp", csv.ToString()); File.Move(peerCsv + ".tmp", peerCsv, true);
        sw.Restart(); while (!child.HasExited && sw.Elapsed.TotalSeconds < 120) await Task.Delay(200);
        string repA = Path.Combine(dirA, "interop-report-A.md"); string textA = File.Exists(repA) ? File.ReadAllText(repA) : "";
        int passA = textA.Split('\n').Count(l => l.StartsWith("| ") && l.Contains("| PASS |")), failA = textA.Split('\n').Count(l => l.StartsWith("| ") && l.Contains("| FAIL |"));
        GB("interop_a_peer_table", passA > 0 && failA == 0, $"A imported B's peer CSV: NexysNet → peer table {passA} PASS · {failA} FAIL (child exit {(child.HasExited ? child.ExitCode : -1)})");
        e.Project.Afdx!.PeerName = "NexysNet instance A (SIMULATE, pid " + child.Id + ")";
        string repB = e.Afdx.ExportReport(Path.Combine(OutDir, "interop-report-B.md")); string textB = File.ReadAllText(repB);
        var checks = e.Afdx.PeerChecks();
        GB("interop_b_checks", checks.All(c => c.Verdict != "FAIL"), string.Join(" ", checks.Select(c => $"{c.Id}={c.Verdict}")));
        // ---- interop-sample.md
        var md = new StringBuilder();
        md.Append($"# NexysNet interoperability sample — two instances on one PC\n\nGenerated by `NexysNet --interop --transport {transport}{(nicName.Length > 0 ? $" --nic \"{nicName}\"" : "")} --seconds {secs.ToString(ic)}` on {DateTime.Now:yyyy-MM-dd HH:mm}.\n\n");
        md.Append($"- Instance A: SIMULATE › START ({vls.Count} VLs, {secs.ToString(ic)} s), then imported B's per-VL counts as the peer report (INTEROP › IMPORT PEER CSV).\n- Instance B: ANALYZE › LISTEN (receive only) on the same {(transport == "nic" ? "network port '" + nicName + "' (packet-capture library)" : "shared virtual network (loopback multicast, nothing leaves the PC)")}; its capture: `{Path.GetFileName(cap.Path)}`.\n\n");
        md.Append($"## Sent by A vs received by B (per VL and network)\n\n| VL | Net | Sent | Received | SN sent | SN received | Verdict |\n|---|---|---|---|---|---|---|\n{rows}\n");
        if (refused > 0) md.Append($"> **Not on the wire.** The port's driver refused every raw frame ({refused:N0} send errors: `{tx.GetProperty("error").GetString()}`); the packet-capture library loops injected frames back to its other capture handles, so B still received them. This sample therefore shows NexysNet ↔ NexysNet through the library on one port, not a transmission over the network.\n\n");
        md.Append(FormattableString.Invariant($"Result: {ok}/{tot} rows equal · SN gaps {gaps} · reversed {rev} · missing {miss} · BAG on B's clock |interval − BAG| p50 {P(0.5):F3} / p99 {P(0.99):F3} / max {(dev.Count > 0 ? dev[^1] : 0):F3} ms · intervals < BAG − Jmax {bagViol}.\n\n"));
        md.Append("---\n\n## Report of instance B (peer → NexysNet)\n\n").Append(Demote(textB)).Append("\n---\n\n## Report of instance A (NexysNet → peer)\n\n").Append(Demote(textA));
        File.WriteAllText(Path.Combine(OutDir, "interop-sample.md"), md.ToString(), new UTF8Encoding(false));
        L("interop sample " + Path.Combine(OutDir, "interop-sample.md"));
        static string Demote(string t) => string.Join("\n", t.Split('\n').Select(l => l.StartsWith("#") ? "##" + l : l));
    }

    /// <summary>Instance A of --interop: open the project, SIMULATE › START for --seconds, STOP, write tx.json; wait for B's peer CSV,
    /// import it (INTEROP NexysNet → peer) and export interop-report-A.md.</summary>
    static async Task InteropTx(MainWindow w)
    {
        var e = w.Engine; var ic = CultureInfo.InvariantCulture; double secs = double.Parse(Arg("--seconds", "20"), ic); string peerCsv = Arg("--peercsv", "");
        e.OpenAny(Arg("--project")); await Idle(w, 500);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2); var pause0 = GC.GetTotalPauseDuration();
        await JS(w, "await nx.test.rail('SIMULATE'); await nx.test.afdx().sim.click('start'); return true;"); await Idle(w, 200);
        GB("tx_started", e.Afdx.Running, $"A: device {e.Project.Device}, error '{e.Afdx.LastError}'");
        var sw = Stopwatch.StartNew(); while (sw.Elapsed.TotalSeconds < secs && e.Afdx.Running) await Task.Delay(100);
        await JS(w, "await nx.test.afdx().sim.click('stop'); return true;"); await Idle(w, 300);
        var run = e.Afdx.RunDto(); File.WriteAllText(Path.Combine(OutDir, "tx.json.tmp"), JsonSerializer.Serialize(run)); File.Move(Path.Combine(OutDir, "tx.json.tmp"), Path.Combine(OutDir, "tx.json"), true);
        var tx = e.Afdx.LastSender; GB("tx_sent", tx != null && tx.Vls.Sum(v => v.Attempted[0] + v.Attempted[1]) > 0, $"A handed {tx?.Vls.Sum(v => v.Attempted[0] + v.Attempted[1]):N0} frames to the device (accepted {tx?.Vls.Sum(v => v.Sent[0] + v.Sent[1]):N0}, error '{e.Afdx.LastError}') on {tx?.Vls.Count(v => v.Enabled)} VLs, slips {tx?.Vls.Sum(v => v.Slips)}, late max {tx?.Vls.Max(v => v.LateMaxUs):F0} µs; GC during the run: gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}, pauses {(GC.GetTotalPauseDuration() - pause0).TotalMilliseconds:F1} ms");
        sw.Restart(); while (peerCsv.Length > 0 && !File.Exists(peerCsv) && sw.Elapsed.TotalSeconds < 120) await Task.Delay(200);
        if (File.Exists(peerCsv))
        {
            int n = e.Afdx.ImportPeerCsv(peerCsv); e.Project.Afdx!.PeerName = "NexysNet instance B (ANALYZE › LISTEN)";
            await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 300);
            var rows = e.Afdx.TxTable(); GB("tx_peer_rows", n > 0 && rows.All(r => r.Verdict == "PASS"), $"peer CSV rows {n}; verdicts " + string.Join(" ", rows.Select(r => $"VL{r.Vl}={r.Verdict}")));
            await Shot(w, "interop_A");
            e.Afdx.ExportReport(Path.Combine(OutDir, "interop-report-A.md"));
        }
        else GB("tx_peer_rows", false, "no peer CSV from B");
    }

    /// <summary>VLs of the interop run: sampling, a queuing port with 3000-byte messages (IP fragments) next to a sampling sub-VL, A-only, B-only.
    /// Over a real port (Wi-Fi / Ethernet shared with other traffic) the fastest BAG is 8 ms.</summary>
    static List<IcdVl> InteropVls(bool gentle)
    {
        IcdAfdxPort Port(string name, int sub, string type, int dst, int size, int vl) => new() { Name = name, SubVl = sub, PortType = type, IpSrc = "10.1.33.1", IpDst = $"224.224.0.{vl}", UdpSrc = 20000 + sub, UdpDst = dst, Size = size, Params = { new IcdAfdxParam { Name = name + "_CNT", Type = "u16" }, new IcdAfdxParam { Name = name + "_SIN", Offset = 2, Type = "f32", Unit = "deg" } } };
        return new List<IcdVl>
        {
            new() { VlId = 101, Name = "VL_101", BagMs = gentle ? 8 : 2, Network = "AB", Lmax = AfdxIcd.Overhead + 64, Ports = { Port("VL101_P1", 1, "sampling", 32101, 64, 101) } },
            new() { VlId = 102, Name = "VL_102", BagMs = 8, Network = "AB", Lmax = 1518, Ports = { Port("VL102_S1", 1, "sampling", 32102, 200, 102), Port("VL102_Q2", 2, "queuing", 32103, 3000, 102) } },
            new() { VlId = 103, Name = "VL_103", BagMs = 32, Network = "A", Lmax = AfdxIcd.Overhead + 1000, Ports = { Port("VL103_P1", 1, "sampling", 32104, 1000, 103) } },
            new() { VlId = 104, Name = "VL_104", BagMs = 128, Network = "B", Lmax = 64, Ports = { Port("VL104_P1", 1, "sampling", 32105, 17, 104) } },
        };
    }
}
