using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.Core.Arinc664;
using NexysView.Core.Bus;
using NexysView.Core.Mil1553;

namespace NexysNet.App;

/// <summary>
/// --afdx [--corpus dir] [--frames 1000000]: the ARINC 664 path on the real UI — new 664 project → ICD import → VL edit (+ validation)
/// → virtual send (batch and real time) → ANALYZE (list, filter, decode, trend) → INTEROP (checklist, peer table, report) — plus the
/// truth corpus (every item of every truth JSON), error detection, 1,000 VL-seconds without loss, BAG accuracy of the real-time sender
/// and the 1M-frame performance gates. The shell offers the ARINC 664 rails only (net_shell_664_only).
/// </summary>
public static partial class SelfTest
{
    static async Task Afdx(MainWindow w)
    {
        var e = w.Engine; string dir = Path.Combine(OutDir, "data"); Directory.CreateDirectory(dir); var sw = Stopwatch.StartNew();
        string corpus = Arg("--corpus", "");
        if (Array.IndexOf(Args, "--rtonly") >= 0) { await RtGate(w); return; }
        if (Array.IndexOf(Args, "--perfonly") >= 0) { e.NewProject("perf", "arinc664"); await PerfGate(w, dir); return; }
        // ---------------------------------------------------------------- 0. the application shell is the ARINC 664 one only (RUN-SPLIT): six rails, no bus-simulator text
        string shell = (await JS(w, "await nx.test.rail('START'); return Array.from(document.querySelectorAll('.rail [data-rail]')).map(b => b.dataset.rail).join(' ') + '|' + document.querySelector('.start').textContent;")).GetString() ?? "";
        string railNames = shell.Split('|')[0]; string startText = shell.Length > railNames.Length ? shell[(railNames.Length + 1)..] : "";
        bool foreign = System.Text.RegularExpressions.Regex.IsMatch(startText, "15" + "53|MIL" + "-STD|remote " + "terminal", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        GB("net_shell_664_only", railNames == "PROJECT ANALYZE SIMULATE ICD INTEROP SETTINGS" && !foreign, $"rails '{railNames}', other-protocol text on START {foreign}");

        // ---------------------------------------------------------------- 1. new ARINC 664 project from START
        await JS(w, "await nx.test.rail('START'); await nx.test.clickSel('.start [data-a=newProject]'); return true;"); await Idle(w, 300);
        GB("new_664_project", e.Project.IsAfdx && e.Project.Afdx != null, $"protocol {e.Project.Protocol}, rail {e.Mode}");

        // ---------------------------------------------------------------- 2. ICD import (CSV, one row per parameter)
        string icdCsv = corpus.Length > 0 && File.Exists(Path.Combine(corpus, "c02_vl8_icd.csv")) ? Path.Combine(corpus, "c02_vl8_icd.csv") : Path.Combine(dir, "afdx_icd.csv");
        if (!File.Exists(icdCsv)) File.WriteAllText(icdCsv, AfdxIcd.ToCsv(AfdxIcd.Dummy()));
        e.Icd.LoadIcd(icdCsv); await Idle(w, 300);
        await JS(w, "await nx.test.rail('ICD'); return true;"); await Idle(w, 400);
        int nVl = e.Project.Icd.Vls?.Count ?? 0; int rows = (await JS(w, "return nx.test.afdx().icd.rows();")).GetInt32();
        GB("icd_import", nVl > 0 && rows == nVl && e.Project.Icd.Schema == "1.1", $"{Path.GetFileName(icdCsv)}: {nVl} VLs, {e.Project.Icd.Vls!.Sum(v => v.Ports.Sum(p => p.Params.Count))} parameters, ICD rail rows {rows}, schema {e.Project.Icd.Schema}");
        await Shot(w, "afdx_icd");
        // ---------------------------------------------------------------- 3. VL edit through the table + validation
        var v0 = e.Project.Icd.Vls![0]; int id0 = v0.VlId;
        await JS(w, $"await nx.test.afdx().icd.setVl({id0}, 'lmax', '40'); return true;"); await Idle(w, 300);
        var val = (await JS(w, "return nx.test.afdx().icd.validation();")).EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        bool sawErr = val.Any(x => x.Contains("Lmax 40"));
        await JS(w, $"await nx.test.afdx().icd.setVl({id0}, 'lmax', '1518'); await nx.test.afdx().icd.setVl({id0}, 'bag_ms', '16'); return true;"); await Idle(w, 300);
        GB("vl_edit_validation", sawErr && v0.Lmax == 1518 && v0.BagMs == 16 && AfdxIcd.Validate(e.Project.Icd).All(x => !x.StartsWith("error")), $"Lmax 40 → '{val.FirstOrDefault(x => x.Contains("Lmax")) ?? "no error"}'; restored Lmax {v0.Lmax}, BAG edited to {v0.BagMs} ms");
        e.SaveProject(Path.Combine(dir, "afdx.nxn")); var back = BusProject.Load(Path.Combine(dir, "afdx.nxn"));
        GB("project_roundtrip", back.IsAfdx && back.Icd.Vls?.Count == nVl && back.Icd.Vls[0].BagMs == 16, $"afdx.nxn: protocol {back.Protocol}, {back.Icd.Vls?.Count} VLs, VL {id0} BAG {back.Icd.Vls?[0].BagMs}");

        // ---------------------------------------------------------------- 4. virtual send: 10 s batch from the SIMULATE rail
        await JS(w, "await nx.test.rail('SIMULATE'); return true;"); await Idle(w, 300);
        await JS(w, "await nx.test.afdx().sim.click('batch'); return true;"); await Idle(w, 600);
        var cap = e.Afdx.Capture!; var an = e.Afdx.Analysis!; var st = e.Afdx.RunDto(); long sent = JsonSerializer.SerializeToElement(st).GetProperty("sent").GetInt64();
        GB("batch_send_receive", cap.Count == sent && sent > 0 && an.TotalErrors == 0, $"10 s batch: sent {sent:N0}, received {cap.Count:N0}, check findings {an.TotalErrors}");
        var bagOk = an.Vls.Values.All(v => Math.Abs(v.MeanIntervalMs(0) + v.MeanIntervalMs(1) - (v.Frames[0] > 0 && v.Frames[1] > 0 ? 2 : 1) * v.BagMs) < 1e-6);
        GB("batch_bag_exact", bagOk, "virtual clock: every VL's mean interval = BAG exactly");
        await Shot(w, "afdx_simulate");

        // ---------------------------------------------------------------- 5. ANALYZE: list, filter, decode, trend
        await JS(w, "await nx.test.rail('ANALYZE'); return true;"); await Idle(w, 800);
        int total = (await JS(w, "return nx.test.afdx().an.total();")).GetInt32();
        int vlF = (await JS(w, $"return await nx.test.afdx().an.filter({{ vl: {id0} }});")).GetInt32();
        long vlFrames = an.Vls[id0].Frames[0] + an.Vls[id0].Frames[1];
        string dec = (await JS(w, "return await nx.test.afdx().an.selectRow(3);")).GetString() ?? ""; int nPar = (await JS(w, "return nx.test.afdx().an.decodedParams();")).GetInt32();
        int chart = (await JS(w, "return await nx.test.afdx().an.trendFirst();")).GetInt32(); await Idle(w, 500);
        var ch = e.Charts.FirstOrDefault(c => c.Id == chart);
        GB("analyze_list_filter", total == cap.Count && vlF == vlFrames, $"list {total:N0} = capture {cap.Count:N0}; VL {id0} filter {vlF} = {vlFrames}");
        GB("analyze_decode_trend", nPar > 0 && ch != null && ch.Model.Curves.Count == 1 && ch.Model.Curves[0].Channel.Length > 0, $"decoded parameters {nPar}; trend chart {chart} curves {ch?.Model.Curves.Count} samples {ch?.Model.Curves.FirstOrDefault()?.Channel.Length}");
        await Shot(w, "afdx_analyze");
        int errOnly = (await JS(w, "return await nx.test.afdx().an.filter({ errors: true });")).GetInt32();
        // inject sender faults, batch again: the checks must report exactly what was injected
        var se = e.Project.Afdx!.Errors; se.SnSkipEvery = 100; se.DropBEvery = 240; se.EarlyEvery = 300; se.OversizeEvery = 400;
        e.Afdx.RunFor(2); var a2 = e.Afdx.Analysis!; var snd = e.Afdx.LastSender!;
        int detected = 0, expectedItems = 0; var lines = new List<string>();
        foreach (var vs in a2.Vls.Values)
        {
            // expected counts from the injection rules, frame by frame (r = regular frame number of the VL, 1-based)
            var tv = snd.Vls.First(x => x.Vl.VlId == vs.Vl); var vl = tv.Vl; long reg = tv.Regular; bool hasA = vl.Network != "B", hasB = vl.Network != "A";
            long[] gaps = new long[2], bagv = new long[2]; long lmaxv = 0, missB = 0;
            for (long r = 1; r <= reg; r++)
            {
                bool skip = r % 100 == 0, dropB = hasB && r % 240 == 0, early = r % 300 == 0, over = r % 400 == 0; double tMs = 1 + (r - 1) * vl.BagMs;
                for (int n = 0; n < 2; n++)
                {
                    if (n == 0 && !hasA || n == 1 && !hasB) continue; bool dropped = n == 1 && dropB;
                    if (skip) gaps[n]++; if (dropped) gaps[n]++;
                    if (early && !dropped) bagv[n]++; if (over && !dropped) lmaxv++;
                }
                if (dropB && hasA && 2000 - tMs > 25) missB++;
            }
            void Ex(string what, long want, long got) { expectedItems++; if (want == got) detected++; else lines.Add($"VL {vs.Vl} {what}: injected {want} found {got}"); }
            Ex("sn gaps A", gaps[0], vs.SnGaps[0]); Ex("sn gaps B", gaps[1], vs.SnGaps[1]);
            Ex("bag violations A", bagv[0], vs.BagViolations[0]); Ex("bag violations B", bagv[1], vs.BagViolations[1]);
            Ex("lmax violations", lmaxv, vs.LmaxViolations);
            if (vl.Network == "AB") Ex("missing B", missB, vs.Missing[1]);
        }
        GB("sender_faults_detected", detected == expectedItems, $"{detected}/{expectedItems} injected-fault counts found exactly" + (lines.Count > 0 ? ": " + string.Join("; ", lines.Take(6)) : ""));
        se.SnSkipEvery = se.DropBEvery = se.EarlyEvery = se.OversizeEvery = 0;
        GB("errors_only_clean_run", errOnly == 0, $"ERRORS ONLY on the clean batch: {errOnly} frames");

        // ---------------------------------------------------------------- 6. INTEROP
        string c04 = corpus.Length > 0 ? Path.Combine(corpus, "c04_errors.pcapng") : "";
        if (File.Exists(c04))
        {
            e.Icd.LoadIcd(Path.Combine(corpus, "c04_errors_icd.csv")); e.Afdx.OpenCapture(c04);
            await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 400);
            var checks = (await JS(w, "return nx.test.afdx().iop.checks();")).EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("verdict").GetString()!);
            bool want = checks.GetValueOrDefault("sn") == "FAIL" && checks.GetValueOrDefault("bag") == "FAIL" && checks.GetValueOrDefault("lmax") == "FAIL" && checks.GetValueOrDefault("ab") == "FAIL" && checks.GetValueOrDefault("vl_seen") == "PASS";
            GB("interop_faulty_capture", want, "c04_errors: " + string.Join(" ", checks.Select(kv => $"{kv.Key}={kv.Value}")));
            await Shot(w, "afdx_interop_faults");
            e.Icd.LoadIcd(Path.Combine(corpus, "c02_vl8_icd.csv")); e.Afdx.OpenCapture(Path.Combine(corpus, "c02_vl8.pcapng"));
            await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 400);
            var c2 = (await JS(w, "return nx.test.afdx().iop.checks();")).EnumerateArray().Select(x => (id: x.GetProperty("id").GetString()!, v: x.GetProperty("verdict").GetString()!)).ToList();
            GB("interop_clean_capture", c2.All(x => x.v != "FAIL") && c2.Count(x => x.v == "PASS") >= 8, "c02_vl8: " + string.Join(" ", c2.Select(x => $"{x.id}={x.v}")));
        }
        else L("interop corpus checks skipped (no --corpus)");
        // NexysNet → peer: run, then type what the "peer" reported
        e.Afdx.RunFor(1); await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 300);
        var tx = e.Afdx.TxTable(); var r0 = tx[0]; var r1 = tx.Count > 1 ? tx[1] : tx[0];
        await JS(w, $"await nx.test.afdx().iop.setPeer({r0.Vl}, 'rxA', '{r0.SentA}'); await nx.test.afdx().iop.setPeer({r0.Vl}, 'rxB', '{r0.SentB}'); await nx.test.afdx().iop.setPeer({r1.Vl}, 'rxA', '{Math.Max(0, r1.SentA - 3)}'); return true;"); await Idle(w, 300);
        var tx2 = e.Afdx.TxTable();
        GB("interop_peer_table", tx2[0].Verdict == "PASS" && (tx.Count < 2 || tx2[1].Verdict == "FAIL"), $"VL {r0.Vl} peer = sent → {tx2[0].Verdict}; VL {r1.Vl} peer 3 short → {(tx.Count > 1 ? tx2[1].Verdict : "-")}");
        e.Project.Afdx!.PeerName = "self-test peer";
        string rep = e.Afdx.ExportReport(Path.Combine(OutDir, "interop-report.md")); string rtext = File.ReadAllText(rep);
        GB("interop_report", rtext.Contains("## 1. Peer → NexysNet") && rtext.Contains("## 2. NexysNet → peer") && rtext.Contains("| " + r0.Vl + " |"), $"{rep} {rtext.Length} chars");
        await Shot(w, "afdx_interop");

        // ---------------------------------------------------------------- 7. truth corpus + error detection
        if (corpus.Length > 0 && Directory.Exists(corpus))
        {
            int okAll = 0, totAll = 0, errOk = 0, errTot = 0; var notes = new List<string>();
            foreach (var f in Directory.EnumerateFiles(corpus, "*.pcapng").OrderBy(x => x))
            {
                var (ok, tot, diffs, eok, etot) = CompareTruth(f); okAll += ok; totAll += tot; errOk += eok; errTot += etot;
                L($"truth {Path.GetFileName(f)}: {ok}/{tot}" + (diffs.Count > 0 ? " — " + string.Join("; ", diffs.Take(5)) : "")); notes.Add($"{Path.GetFileNameWithoutExtension(f)} {ok}/{tot}");
            }
            GB("truth_corpus", okAll == totAll && totAll > 0, $"{okAll}/{totAll} truth items ({string.Join(", ", notes)})");
            GB("error_detection", errOk == errTot && errTot > 0, $"{errOk}/{errTot} injected-error items detected with the exact count");
        }

        // ---------------------------------------------------------------- 7b. RUN-AFDX2: VL fallback, reassembly, sub-VLs, evidence frames, peer CSV, Wireshark
        await AfdxRun2(w, corpus, dir);

        // ---------------------------------------------------------------- 8. 1,000 VL-seconds on the virtual network without loss
        {
            var vls = Enumerable.Range(0, 100).Select(i => new IcdVl { VlId = 2000 + i, Name = $"L{i}", BagMs = 1 << (i % 6), Network = "AB", Lmax = 200, Ports = { new IcdAfdxPort { Name = $"L{i}", UdpDst = 40000 + i, Size = 100, Params = { new IcdAfdxParam { Name = $"L{i}_CNT", Type = "u16" } } } } }).ToList();
            var dev = new VirtualNetworkDevice { ManualClock = true }; dev.Open(); dev.Start(); var txs = new AfdxSender(dev, vls); var cp = new AfdxCapture(1 << 24);
            var t0 = Stopwatch.StartNew(); txs.RunBatch(dev, 10, (n, t, f) => cp.Add(n, t, f)); double ms = t0.Elapsed.TotalMilliseconds;
            var aa = new AfdxAnalysis(cp, new IcdHub { Vls = vls }); long s = txs.Vls.Sum(v => v.Sent[0] + v.Sent[1]);
            GB("virtual_1000_vl_seconds", s == cp.Count && aa.TotalErrors == 0 && aa.Vls.Values.All(v => v.Missing[0] + v.Missing[1] == 0), $"100 VLs × 10 s: sent {s:N0}, received {cp.Count:N0}, lost {s - cp.Count}, findings {aa.TotalErrors} ({ms:F0} ms)");
        }

        // ---------------------------------------------------------------- 9. real-time sender accuracy (1 ms BAG) on the virtual network
        await RtGate(w);

        // ---------------------------------------------------------------- 10. 1M-frame performance
        await PerfGate(w, dir);
        L($"afdx self-test {sw.Elapsed.TotalSeconds:F1} s");
    }

    /// <summary>Real-time sender accuracy, 1 ms BAG + 15 other VLs on the virtual network. --rtmode hires|sleep|spin (wait strategy),
    /// --rtseconds s, --minimized (window minimized during the run: the case where Windows 11 ignores a hidden process's timer request).</summary>
    static async Task RtGate(MainWindow w)
    {
        string mode = Arg("--rtmode", "sleep"); bool minimized = Array.IndexOf(Args, "--minimized") >= 0;
        if (minimized) { w.WindowState = System.Windows.WindowState.Minimized; await Task.Delay(500); }
        try
        {
            var vls = new List<IcdVl> { new() { VlId = 3000, Name = "RT1", BagMs = 1, Network = "AB", Lmax = 200, Ports = { new IcdAfdxPort { Name = "RT1", UdpDst = 41000, Size = 64 } } } };
            for (int i = 1; i < 16; i++) vls.Add(new IcdVl { VlId = 3000 + i, Name = $"RT{i + 1}", BagMs = 1 << (i % 5), Network = "AB", Lmax = 300, Ports = { new IcdAfdxPort { Name = $"RT{i + 1}", UdpDst = 41000 + i, Size = 200 } } });
            double secs = double.TryParse(Arg("--rtseconds", "5"), System.Globalization.CultureInfo.InvariantCulture, out var rs) ? rs : 5;
            var dev = new VirtualNetworkDevice { Faults = new NetFaults { DelayUs = 20, SkewBUs = 50 } }; dev.Open(); dev.Start(); var txr = new AfdxSender(dev, vls) { WaitMode = mode }; var cp = new AfdxCapture(1 << 24);
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2); var pause0 = GC.GetTotalPauseDuration();
            txr.Start(); var t0 = Stopwatch.StartNew();
            while (t0.Elapsed.TotalSeconds < secs) { await Task.Delay(50); dev.Poll((n, t, f) => cp.Add(n, t, f)); }
            txr.Stop(); await Task.Delay(50); dev.Poll((n, t, f) => cp.Add(n, t, f));
            var dev1 = new List<double>(); long last = long.MinValue;
            for (int i = 0; i < cp.Count; i++) { if (cp.Vl(i) != 3000 || cp.Network(i) != 0) continue; long t = cp.TimeNs(i); if (last != long.MinValue) dev1.Add(Math.Abs((t - last) / 1e6 - 1.0)); last = t; }
            dev1.Sort(); double P(double q) => dev1.Count == 0 ? double.NaN : dev1[Math.Min(dev1.Count - 1, (int)(q * dev1.Count))];
            var t1 = txr.Vls[0]; var aa = new AfdxAnalysis(cp, new IcdHub { Vls = vls });
            G("rt_bag1_interval_dev_p50_ms", P(0.5), "ms"); G("rt_bag1_interval_dev_p99_ms", P(0.99), "ms", "≤ 0.5"); G("rt_bag1_interval_dev_max_ms", dev1.Count > 0 ? dev1[^1] : double.NaN, "ms");
            G("rt_bag1_late_sigma_us", t1.LateStdUs, "µs"); G("rt_bag1_late_max_us", t1.LateMaxUs, "µs"); G("rt_bag1_schedule_slips", t1.Slips, "slips"); G("rt_all_bag_violations", aa.Vls.Values.Sum(v => v.BagViolations[0] + v.BagViolations[1]), "intervals", "0");
            G("rt_slips_per_minute", txr.Vls.Sum(v => v.Slips) * 60.0 / secs, "slips/min"); L($"rt mode {mode}, minimized {minimized}, slips all VLs {txr.Vls.Sum(v => v.Slips)}"); G("rt_gc_pause_ms", (GC.GetTotalPauseDuration() - pause0).TotalMilliseconds, "ms"); L($"rt GC gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}");
            GB("rt_bag1_jitter_gate", P(0.99) <= 0.5 && aa.Vls.Values.Sum(v => v.BagViolations[0] + v.BagViolations[1]) == 0, $"1 ms BAG for {secs:G} s with 15 other VLs: |interval − 1 ms| p50 {P(0.5):F4} · p99 {P(0.99):F4} · max {(dev1.Count > 0 ? dev1[^1] : 0):F4} ms over {dev1.Count:N0} intervals; BAG violations (interval < BAG − 0.5 ms) VL 3000 {aa.Vls[3000].BagViolations[0]}, all VLs {aa.Vls.Values.Sum(v => v.BagViolations[0] + v.BagViolations[1])}: " + string.Join("; ", aa.Vls.Values.Where(v => v.BagViolations[0] + v.BagViolations[1] > 0).Take(6).Select(v => $"VL {v.Vl} BAG {v.BagMs} A {v.BagViolations[0]} B {v.BagViolations[1]} min {v.IntervalMinMs[0]:F3}/{v.IntervalMinMs[1]:F3} ms slips {txr.Vls.First(x => x.Vl.VlId == v.Vl).Slips}")));
                }
        finally { if (minimized) w.WindowState = System.Windows.WindowState.Normal; }
    }

    /// <summary>Open + check + filter + page performance on a generated capture of --frames frames (default 1M; 10M ≈ 1.3 GB pcapng).</summary>
    static async Task PerfGate(MainWindow w, string dir)
    {
        var e = w.Engine;
        {
            int target = int.TryParse(Arg("--frames", "1000000"), out var tf) ? tf : 1_000_000;
            var vls = Enumerable.Range(0, 100).Select(i => new IcdVl { VlId = 4000 + i, Name = $"P{i}", BagMs = 1, Network = "AB", Lmax = 200, Ports = { new IcdAfdxPort { Name = $"P{i}", UdpDst = 42000 + i, Size = 64, Params = { new IcdAfdxParam { Name = $"P{i}_CNT", Type = "u16" }, new IcdAfdxParam { Name = $"P{i}_SIN", Offset = 2, Type = "f32" } } } } }).ToList();
            var dev = new VirtualNetworkDevice { ManualClock = true }; dev.Open(); dev.Start(); var txp = new AfdxSender(dev, vls); var big = new AfdxCapture(1 << 26);
            txp.RunBatch(dev, target / 200 / 1000.0 + 0.0005, (n, t, f) => big.Add(n, t, f));
            string path = Path.Combine(dir, target >= 5_000_000 ? "afdx_big.pcapng" : "afdx_1m.pcapng"); big.Save(path); long fileMb = new FileInfo(path).Length >> 20; big = null!; GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long peak0 = Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
            var hubP = new IcdHub { Source = "perf", Schema = "1.1", Vls = vls };
            e.Project.Icd.Vls = hubP.Vls; e.Project.Icd.Source = "perf"; long ws0 = WsMb();
            var t0 = Stopwatch.StartNew(); var o = JsonSerializer.SerializeToElement(e.Afdx.OpenCapture(path)); double openMs = t0.Elapsed.TotalMilliseconds; long ws1 = WsMb();
            int frames = o.GetProperty("frames").GetInt32();
            G("perf_frames", frames, "frames"); G("perf_open_and_check_ms", openMs, "ms", "≤ 5000"); G("perf_file_mb", fileMb, "MB"); G("perf_ws_delta_mb", ws1 - ws0, "MB");
            t0.Restart(); var fr = JsonSerializer.SerializeToElement(e.Afdx.SetFilter(JsonSerializer.SerializeToElement(new { vl = 4050 }))); double filterMs = t0.Elapsed.TotalMilliseconds;
            t0.Restart(); e.Afdx.SetFilter(JsonSerializer.SerializeToElement(new { errors = true })); double errMs = t0.Elapsed.TotalMilliseconds;
            e.Afdx.SetFilter(JsonSerializer.SerializeToElement(new { })); t0.Restart(); for (int k = 0; k < 20; k++) e.Afdx.Frames(k * 50_000, 200); double pageMs = t0.Elapsed.TotalMilliseconds / 20;
            t0.Restart(); e.Afdx.Stats(); double statMs = t0.Elapsed.TotalMilliseconds;
            G("perf_filter_vl_ms", filterMs, "ms", "≤ 1000"); G("perf_filter_errors_ms", errMs, "ms", "≤ 1000"); G("perf_page_200_ms", pageMs, "ms", "≤ 50"); G("perf_stats_ms", statMs, "ms");
            await JS(w, "await nx.test.rail('ANALYZE'); return true;"); await Idle(w, 1500);
            t0.Restart(); int shown = (await JS(w, "const a = nx.test.afdx().an; await a.filter({}); return a.rowsShown();")).GetInt32(); double uiMs = t0.Elapsed.TotalMilliseconds;
            G("perf_ui_first_page_ms", uiMs, "ms"); G("perf_peak_ws_mb", Process.GetCurrentProcess().PeakWorkingSet64 >> 20, "MB"); G("perf_ws_after_open_mb", WsMb(), "MB"); L($"perf: peak working set while generating {peak0} MB");
            GB("perf_1m_gates", frames >= target * 0.99 && openMs <= 5000 && filterMs <= 1000 && errMs <= 1000 && pageMs <= 50 && shown > 0, $"{frames:N0} frames: open+checks {openMs:F0} ms, VL filter {filterMs:F0} ms ({fr.GetProperty("total").GetInt32():N0}), errors filter {errMs:F0} ms, page {pageMs:F2} ms, stats {statMs:F0} ms, UI rows {shown} in {uiMs:F0} ms");
            await Shot(w, "afdx_analyze_1m");
                }
    }

    static async Task AfdxRun2(MainWindow w, string corpus, string dir)
    {
        var e = w.Engine; var ic = System.Globalization.CultureInfo.InvariantCulture;
        string c06 = Path.Combine(corpus, "c06_frag.pcapng"), c08 = Path.Combine(corpus, "c08_ipvl.pcapng");
        if (corpus.Length > 0 && File.Exists(c08))
        {
            // VL recognition fallback through the ANALYZE bar: MAC only → 1 VL; + IP 224.224.x.x + UDP map 5000=900 → 3 VLs
            e.Icd.LoadIcd(Path.Combine(corpus, "c08_ipvl_icd.csv")); var s0 = e.Project.Afdx!; s0.VlFromIp = false; s0.UdpVls.Clear(); s0.VlFromIcd = false; s0.UdpSn = false;
            e.Afdx.OpenCapture(c08); await JS(w, "await nx.test.rail('ANALYZE'); return true;"); await Idle(w, 600);
            int std = e.Afdx.Analysis!.Vls.Count;
            await JS(w, "const a = nx.test.afdx().an; await a.setRule('vlFromIp', true); await a.setRule('udpVls', '5000=900'); return true;"); await Idle(w, 600);
            var an = e.Afdx.Analysis!; var v7 = an.Vls.GetValueOrDefault(7); var v900 = an.Vls.GetValueOrDefault(900);
            string dec = (await JS(w, "await nx.test.afdx().an.filter({ vl: 7 }); return await nx.test.afdx().an.selectRow(0);")).GetString() ?? "";
            GB("vl_fallback", std == 1 && an.Vls.Count == 3 && v7?.ByIp == 125 && v900?.ByUdp == 100 && v900.SnGaps[0] == 0 && dec.Contains("destination IP"), $"c08: MAC only {std} VL; + IP 224.224.x.x + UDP map 5000=900 → {an.Vls.Count} VLs (VL 7 {v7?.ByIp} frames by IP, VL 900 {v900?.ByUdp} by UDP without SN, gaps {v900?.SnGaps[0]}); decode shows the source: {dec.Contains("destination IP")}");
            await Shot(w, "afdx_vl_fallback");
            await JS(w, "const a = nx.test.afdx().an; await a.setRule('vlFromIp', false); await a.setRule('udpVls', ''); await a.filter({}); return true;");
            GB("vl_fallback_off", e.Afdx.Analysis!.Vls.Count == 1, $"rules off again: {e.Afdx.Analysis!.Vls.Count} VL");
        }
        if (corpus.Length > 0 && File.Exists(c06))
        {
            // reassembly + sub-VLs in ANALYZE, INTEROP evidence frame → ANALYZE
            e.Icd.LoadIcd(Path.Combine(corpus, "c06_frag_icd.csv")); e.Afdx.OpenCapture(c06);
            await JS(w, "await nx.test.rail('ANALYZE'); return true;"); await Idle(w, 800);
            int qrows = (await JS(w, "return nx.test.afdx().an.queuingRows();")).GetInt32(); var a6 = e.Afdx.Analysis!;
            GB("analyze_queuing_table", qrows == 4 && a6.Vls[501].MsgIncomplete == 2 && a6.Vls[502].MsgReordered == 1, $"queuing / sub-VL rows {qrows}; VL 501 incomplete {a6.Vls[501].MsgIncomplete}, VL 502 out of order {a6.Vls[502].MsgReordered}, VL 500 complete {a6.Vls[500].MsgComplete}");
            await JS(w, "await nx.test.afdx().an.selectRow(0); return await nx.test.afdx().an.showQueuing();"); await Idle(w, 300);
            await Shot(w, "afdx_reassembly");
            await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 400);
            var chk = (await JS(w, "return nx.test.afdx().iop.checks();")).EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => (v: x.GetProperty("verdict").GetString()!, f: x.GetProperty("frame").GetInt32()));
            var ra = chk.GetValueOrDefault("reassembly"); int clicked = (await JS(w, "return await nx.test.afdx().iop.clickFrame('reassembly');")).GetInt32(); await Idle(w, 600);
            int selected = (await JS(w, "return nx.test.afdx().an.selectedFrame();")).GetInt32(); string decTxt = (await JS(w, "return nx.test.afdx().an.decodeText();")).GetString() ?? "";
            bool flagged = ra.f > 0 && (e.Afdx.Analysis!.Flags[ra.f - 1] & FrameFlag.Reassembly) != 0;
            GB("interop_evidence_frame", ra.v == "FAIL" && flagged && clicked == ra.f && selected == ra.f - 1 && decTxt.Contains("IP FRAGMENT") && e.Mode == "ANALYZE", $"reassembly {ra.v}, evidence frame {ra.f} (flagged {flagged}) → link opened ANALYZE on frame index {selected}, decode shows the fragment: {decTxt.Contains("IP FRAGMENT")}");
            await Shot(w, "afdx_evidence_frame");
            string rep = e.Afdx.ExportReport(Path.Combine(OutDir, "interop-report-c06.md")); string rt = File.ReadAllText(rep);
            GB("interop_report_evidence", rt.Contains($"[frame {ra.f}](#frame-{ra.f})") && rt.Contains($"id=\"frame-{ra.f}\"") && rt.Contains("frame.number =="), $"{rep}: evidence link + anchor for frame {ra.f}");
        }
        // sender: queuing-port message larger than one frame → IP fragments; a dropped fragment every 5th message → incomplete
        {
            var vls = new List<IcdVl> { new() { VlId = 700, Name = "Q", BagMs = 2, Network = "AB", Lmax = 1518, Ports = { new IcdAfdxPort { Name = "Q1", SubVl = 1, PortType = "queuing", UdpDst = 37000, Size = 4000, Params = { new IcdAfdxParam { Name = "Q1_CNT", Type = "u16" } } }, new IcdAfdxPort { Name = "S2", SubVl = 2, UdpDst = 37001, Size = 64 } } },
                                         new() { VlId = 701, Name = "R", BagMs = 1, Network = "A", Lmax = 200, Ports = Enumerable.Range(1, 4).Select(k => new IcdAfdxPort { Name = $"R{k}", SubVl = k, UdpDst = 37010 + k, Size = 100 }).ToList() } };
            var dev = new VirtualNetworkDevice { ManualClock = true }; dev.Open(); dev.Start(); var tx = new AfdxSender(dev, vls) { Errors = new AfdxErrorInjection { FragDropEvery = 5 } }; var cp = new AfdxCapture(1 << 22);
            tx.RunBatch(dev, 3, (n, t, f) => cp.Add(n, t, f)); var aa = new AfdxAnalysis(cp, new IcdHub { Vls = vls }); var q = aa.Vls[700]; var r = aa.Vls[701];
            long other = q.SnGaps[0] + q.SnGaps[1] + q.BagViolations[0] + q.BagViolations[1] + q.Missing[0] + q.Missing[1] + q.LmaxViolations;
            GB("sender_fragments", q.MsgIncomplete == tx.Vls[0].FragDropped && q.MsgIncomplete > 0 && other == 0 && aa.MessagesDone.Where(m => m.vl == 700).All(m => m.bytes == 4000) && r.SubVlBreaks == 0 && r.SubVls.Count == 4 && r.SubVls.Values.Max(x => x.Frames) - r.SubVls.Values.Min(x => x.Frames) <= 1,
                $"VL 700: {tx.Vls[0].FragMessages} messages of 4000 B as {AfdxIcd.Fragments(4000, 1518)} fragments, dropped {tx.Vls[0].FragDropped} → incomplete {q.MsgIncomplete}, complete {q.MsgComplete} (4000 B each), other findings {other}; VL 701 4 sub-VLs round robin: frames {string.Join("/", r.SubVls.Values.Select(x => x.Frames))}, order breaks {r.SubVlBreaks}");
        }
        // peer report from a CSV (instead of typing it in)
        {
            e.Icd.LoadIcd(corpus.Length > 0 && File.Exists(Path.Combine(corpus, "c02_vl8_icd.csv")) ? Path.Combine(corpus, "c02_vl8_icd.csv") : Path.Combine(dir, "afdx_icd.csv"));
            e.Afdx.RunFor(1); var t = e.Afdx.TxTable(); string csv = Path.Combine(dir, "peer.csv");
            File.WriteAllText(csv, "VL;Frames A;Frames B;Errors;Note\n" + string.Join("\n", t.Select((x, k) => $"{x.Vl};{(k == 1 ? x.SentA - 2 : x.SentA)};{x.SentB};0;csv row {k}")) + "\n");
            int n = e.Afdx.ImportPeerCsv(csv); var t2 = e.Afdx.TxTable();
            await JS(w, "await nx.test.rail('INTEROP'); await nx.test.afdx().iop.reload(); return true;"); await Idle(w, 300);
            int uiPass = (await JS(w, "return nx.test.afdx().iop.tx().filter(r => r.verdict === 'PASS').length;")).GetInt32();
            GB("peer_csv_import", n == t.Count && t2.Count(x => x.Verdict == "FAIL") == 1 && t2[1].Verdict == "FAIL" && uiPass == t.Count - 1, $"{n} rows from a ';' CSV with other column names; verdicts {string.Join(" ", t2.Select(x => x.Verdict))}; UI PASS rows {uiPass}");
        }
        // Wireshark cross-check: what tshark reported (work\afdx\tshark_truth.py) = what NexysNet reads
        string tsDir = Arg("--tshark", "");
        if (tsDir.Length > 0 && Directory.Exists(tsDir))
        {
            int ok = 0, tot = 0; var diffs = new List<string>(); var notes = new List<string>();
            foreach (var f in Directory.EnumerateFiles(tsDir, "*.tshark.json").OrderBy(x => x))
            {
                var t = JsonDocument.Parse(File.ReadAllText(f)).RootElement; string cp = t.GetProperty("path").GetString()!; if (!File.Exists(cp)) { diffs.Add("missing " + cp); continue; }
                var cap = AfdxCapture.Open(cp); var a = new AfdxAnalysis(cap, null); int o0 = ok, t0 = tot;
                void Eq(string what, long want, long got) { tot++; if (want == got) ok++; else diffs.Add($"{Path.GetFileName(cp)} {what}: Wireshark {want} NexysNet {got}"); }
                Eq("frames", t.GetProperty("frames").GetInt64(), cap.Count); Eq("ARP", t.GetProperty("arp").GetInt64(), a.Arp); Eq("IPv4", t.GetProperty("ipv4").GetInt64(), a.Ipv4); Eq("ICMP", t.GetProperty("icmp").GetInt64(), a.Icmp);
                Eq("UDP", t.GetProperty("udp_proto_field").GetInt64(), a.Udp); Eq("TCP", t.GetProperty("tcp_proto_field").GetInt64(), a.Tcp);
                Eq("VLs", t.GetProperty("vls").EnumerateObject().Count(), a.Vls.Count);
                foreach (var v in t.GetProperty("vls").EnumerateObject())
                {
                    int vl = int.Parse(v.Name); var st = a.Vls.GetValueOrDefault(vl);
                    Eq($"VL {vl} frames A", v.Value.GetProperty("a").GetInt64(), st?.Frames[0] ?? -1); Eq($"VL {vl} frames B", v.Value.GetProperty("b").GetInt64(), st?.Frames[1] ?? -1);
                    long fa = Enumerable.Range(0, cap.Count).Count(i => cap.Vl(i) == vl && cap.Network(i) == 0 && cap.IsFragment(i));
                    Eq($"VL {vl} fragments A", v.Value.GetProperty("fragments_a").GetInt64(), fa);
                    if (fa > 0) Eq($"VL {vl} reassembled", v.Value.GetProperty("reassembled_a").GetInt64(), st?.MsgComplete ?? -1);
                }
                notes.Add($"{Path.GetFileNameWithoutExtension(cp)} {ok - o0}/{tot - t0}");
            }
            GB("wireshark_cross", ok == tot && tot > 0, $"{ok}/{tot} items equal to tshark ({string.Join(", ", notes)})" + (diffs.Count > 0 ? ": " + string.Join("; ", diffs.Take(8)) : ""));
        }
        else L("wireshark cross-check skipped (no --tshark dir)");
        // Wireshark profile + dissector for the c02 ICD, checked with tshark when it is installed (not shipped, not required)
        if (corpus.Length > 0 && File.Exists(Path.Combine(corpus, "c02_vl8.pcapng")))
        {
            e.Icd.LoadIcd(Path.Combine(corpus, "c02_vl8_icd.csv")); var hub = e.Project.Icd;
            string wdir = Path.Combine(OutDir, "wireshark"); if (Directory.Exists(wdir)) Directory.Delete(wdir, true);
            var res = JsonSerializer.SerializeToElement(e.Afdx.Dispatch("afdx.wireshark.exportTo", JsonSerializer.SerializeToElement(new { dir = wdir }), out var rr) ? rr : null);
            bool files = File.Exists(Path.Combine(wdir, "plugins", "nexysnet_afdx.lua")) && File.Exists(Path.Combine(wdir, "profiles", Services.WiresharkProfile.ProfileName, "preferences")) && File.Exists(Path.Combine(wdir, "profiles", Services.WiresharkProfile.ProfileName, "colorfilters"));
            string tshark = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Wireshark", "tshark.exe");
            if (!File.Exists(tshark)) GB("wireshark_profile", files, "profile files written; tshark not installed — dissector not exercised");
            else
            {
                string pc = Path.Combine(corpus, "c02_vl8.pcapng"); var cap = AfdxCapture.Open(pc); var a = new AfdxAnalysis(cap, hub);
                var prms = hub.Vls!.SelectMany(v => v.Ports.Where(p => p.UdpDst > 0).SelectMany(p => p.Params.Select(q => q.Name))).ToList();
                var args = new List<string> { "-C", Services.WiresharkProfile.ProfileName, "-r", pc, "-n", "-T", "fields", "-E", "separator=|", "-E", "occurrence=f", "-e", "frame.number", "-e", "nexysafdx.vl", "-e", "nexysafdx.sn", "-e", "nexysafdx.net", "-e", "nexysafdx.vl_name" };
                for (int k = 0; k < prms.Count; k++) { args.Add("-e"); args.Add($"nexysafdx.p{k}"); }
                var (outp, err, code) = RunTool(tshark, args, wdir);
                int rowsN = 0, vlOk = 0, decN = 0, decOk = 0; var bad = new List<string>();
                foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var c = line.TrimEnd('\r').Split('|'); if (!int.TryParse(c[0], out int no)) continue; int i = no - 1; rowsN++;
                    bool same = c[1] == cap.Vl(i).ToString(ic) && c[2] == cap.Sn(i).ToString(ic) && c[3] == (cap.Network(i) == 0 ? "A" : "B"); if (same) vlOk++; else if (bad.Count < 4) bad.Add($"frame {no}: tshark {c[1]}/{c[2]}/{c[3]} NexysNet {cap.Vl(i)}/{cap.Sn(i)}/{cap.Network(i)}");
                    if (no > 2000) continue;
                    var dec = a.Decode(i).ToDictionary(x => x.name, x => x.eu);
                    for (int k = 0; k < prms.Count; k++)
                    {
                        if (5 + k >= c.Length || c[5 + k].Length == 0 || !dec.TryGetValue(prms[k], out double eu)) continue; decN++;
                        if (double.TryParse(c[5 + k], System.Globalization.NumberStyles.Float, ic, out double tv) && Math.Abs(tv - eu) <= 1e-6 * Math.Max(1, Math.Abs(eu))) decOk++; else if (bad.Count < 8) bad.Add($"frame {no} {prms[k]}: tshark {c[5 + k]} NexysNet {eu}");
                    }
                }
                var (cols, _, _) = RunTool(tshark, new List<string> { "-C", Services.WiresharkProfile.ProfileName, "-r", pc, "-n", "-c", "3" }, wdir);
                var (aCnt, _, _) = RunTool(tshark, new List<string> { "-C", Services.WiresharkProfile.ProfileName, "-r", pc, "-n", "-Y", "nexysafdx.net == \"A\"", "-T", "fields", "-e", "frame.number" }, wdir);
                long nA = aCnt.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, wantA = Enumerable.Range(0, cap.Count).Count(i => cap.Vl(i) >= 0 && cap.Network(i) == 0);
                GB("wireshark_profile", files && code == 0 && rowsN == cap.Count && vlOk == rowsN && decN > 0 && decOk == decN && cols.Contains("VL_10") && nA == wantA,
                    $"tshark with the exported profile + dissector on c02: VL/SN/network {vlOk}/{rowsN} frames equal, parameters {decOk}/{decN} equal (first 2000 frames), VL-name column in the summary line {cols.Contains("VL_10")}, colour-rule filter net == A {nA}/{wantA}" + (bad.Count > 0 ? ": " + string.Join("; ", bad) : "") + (err.Length > 0 ? " stderr: " + err[..Math.Min(300, err.Length)] : ""));
            }
        }
    }

    static (string stdout, string stderr, int code) RunTool(string exe, List<string> args, string configDir)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WIRESHARK_CONFIG_DIR"] = configDir;
        using var p = Process.Start(psi)!; var o = p.StandardOutput.ReadToEndAsync(); var er = p.StandardError.ReadToEndAsync(); p.WaitForExit(120_000);
        return (o.Result, er.Result, p.HasExited ? p.ExitCode : -1);
    }

    static (int ok, int total, List<string> diffs, int errOk, int errTotal) CompareTruth(string pcap) { var r = AfdxTruth.Compare(pcap); return (r.Ok, r.Total, r.Diffs, r.ErrOk, r.ErrTotal); }
}
