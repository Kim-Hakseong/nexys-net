using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NexysView.Core.Arinc664;
using NexysView.Core.Bus;
using NexysView.Core.Data;
using NexysView.Core.Mil1553;

namespace NexysNet.App.Services;

/// <summary>
/// ARINC 664 (AFDX) side of NexysNet, created only for an arinc664 project: the network device (virtual A/B loopback, or Ethernet ports
/// through the packet-capture library when it is installed), the transmit scheduler, the live capture (pcapng on stop), the checks
/// (<see cref="AfdxAnalysis"/>), the trend channels (shared chart engine) and the interoperability checklist / report.
/// </summary>
public sealed class AfdxService
{
    readonly NetEngine _e;
    public AfdxService(NetEngine e) { _e = e; }
    BusProject P => _e.Project;
    AfdxProjectSettings S => P.Afdx ??= new AfdxProjectSettings();
    IcdHub Icd => P.Icd;
    List<IcdVl> Vls => Icd.Vls ??= new List<IcdVl>();

    // ------------------------------------------------------------------ capture + analysis
    public AfdxCapture? Capture { get; private set; }
    public AfdxAnalysis? Analysis { get; private set; }
    int _analysedVersion = -1, _analysedCount = -1; object? _analysedIcd;
    public string CaptureName => Capture == null ? "" : Capture.Path.Length > 0 ? Path.GetFileName(Capture.Path) : "live capture";
    int[]? _view; string _filterKey = "";
    public string LastCapturePath { get; private set; } = "";

    public object OpenCapture(string path)
    {
        var sw = Stopwatch.StartNew(); Capture = AfdxCapture.Open(path, null, S.VlRule(Icd)); double openMs = sw.Elapsed.TotalMilliseconds;
        Reanalyse(true); _e.Mode = "ANALYZE";
        _e.SetStatus($"Capture {Path.GetFileName(path)}: {Capture.Count:N0} frames, {Analysis!.Vls.Count} VLs, {Analysis.TotalErrors:N0} check findings ({openMs:F0} ms read + {Analysis.ElapsedMs:F0} ms checks)"); _e.Touch();
        return new { frames = Capture.Count, vls = Analysis.Vls.Count, errors = Analysis.TotalErrors, openMs, checkMs = Analysis.ElapsedMs };
    }

    /// <summary>Re-run the checks when the capture grew, the ICD changed or <paramref name="force"/>.</summary>
    public AfdxAnalysis? Reanalyse(bool force = false)
    {
        if (Capture == null) return null;
        if (!force && Analysis != null && _analysedCount == Capture.Count && _analysedVersion == Capture.Version && ReferenceEquals(_analysedIcd, Icd)) return Analysis;
        var rule = S.VlRule(Icd); if (rule.Key != Capture.Rule.Key) Capture.Reclassify(rule);
        Analysis = new AfdxAnalysis(Capture, Icd.IsAfdx ? Icd : null, S.CheckOptions()); _analysedCount = Capture.Count; _analysedVersion = Capture.Version; _analysedIcd = Icd; _view = null; _filterKey = "";
        RebuildChannels(); return Analysis;
    }

    // ------------------------------------------------------------------ frame list (filter: vl, network, errors only, kind, text)
    int _fVl = -1, _fNet = -1; bool _fErr; string _fKind = "", _fText = "";
    public object SetFilter(JsonElement r)
    {
        _fVl = Bridge.I(r, "vl", -1); _fNet = Bridge.I(r, "net", -1); _fErr = Bridge.B(r, "errors"); _fKind = Bridge.S(r, "kind"); _fText = Bridge.S(r, "text").Trim(); _view = null; _filterKey = "";
        return new { total = View().Length };
    }
    int[] View()
    {
        var a = Reanalyse(); if (a == null) return Array.Empty<int>();
        string key = $"{_fVl}|{_fNet}|{_fErr}|{_fKind}|{_fText}|{Capture!.Count}";
        if (_view != null && key == _filterKey) return _view;
        var c = Capture; var l = new List<int>(c.Count); uint ipText = _fText.Count(ch => ch == '.') == 3 ? AfdxFrame.ParseIp(_fText) : 0; int.TryParse(_fText, out int num);
        for (int i = 0; i < c.Count; i++)
        {
            if (_fVl >= 0 && c.Vl(i) != _fVl) continue; if (_fNet >= 0 && c.Network(i) != _fNet) continue;
            if (_fErr && (a.Flags[i] & ~(FrameFlag.RmDuplicate | FrameFlag.NotInIcd)) == 0) continue;
            if (_fKind == "afdx" && c.Kind(i) != FrameKind.Afdx) continue; if (_fKind == "other" && c.Kind(i) == FrameKind.Afdx) continue;
            if (_fText.Length > 0 && !(ipText != 0 && c.IpDst(i) == ipText) && !(num > 0 && (c.UdpDst(i) == num || c.Vl(i) == num)) && !Text(i).Contains(_fText, StringComparison.OrdinalIgnoreCase)) continue;
            l.Add(i);
        }
        _view = l.ToArray(); _filterKey = key; return _view;
    }
    string Text(int i) { var p = Analysis?.PortOf(i); return (p?.Name ?? "") + " " + (Analysis?.IcdOf(Capture!.Vl(i))?.Name ?? ""); }

    public object Frames(int from, int count)
    {
        var v = View(); var c = Capture; var a = Analysis; if (c == null || a == null) return new { total = 0, rows = Array.Empty<object>() };
        long t0 = c.FirstNs; var rows = new List<object>(); from = Math.Max(0, from);
        for (int k = from; k < Math.Min(v.Length, from + Math.Clamp(count, 1, 2000)); k++)
        {
            int i = v[k]; var fl = a.Flags[i]; var port = a.PortOf(i);
            rows.Add(new { row = k, i, no = i + 1, t = (c.TimeNs(i) - t0) / 1e9, net = c.Network(i) == 0 ? "A" : "B", kind = c.Kind(i).ToString().ToUpperInvariant(), vl = c.Vl(i), sn = c.Vl(i) >= 0 && c.HasSn(i) ? c.Sn(i) : -1, frag = c.IsFragment(i) ? $"{c.FragOffset(i)}{(c.MoreFragments(i) ? "+" : "")}" : "", from = c.VlFrom(i) == VlSource.Mac || c.Vl(i) < 0 ? "" : c.VlFrom(i).ToString().ToUpperInvariant(), len = c.WireLength(i), dst = c.Kind(i) == FrameKind.Afdx || c.Kind(i) == FrameKind.OtherIp ? $"{AfdxCapture.Ip(c.IpDst(i))}:{c.UdpDst(i)}" : "", port = port?.Name ?? "", flags = FlagText(fl), err = (fl & ~(FrameFlag.RmDuplicate | FrameFlag.NotInIcd)) != 0, dup = (fl & FrameFlag.RmDuplicate) != 0 });
        }
        return new { total = v.Length, all = c.Count, rows };
    }
    static string FlagText(FrameFlag f)
    {
        var l = new List<string>(); if (f.HasFlag(FrameFlag.SnGap)) l.Add("SN GAP"); if (f.HasFlag(FrameFlag.SnReversed)) l.Add("SN REV"); if (f.HasFlag(FrameFlag.SnReset)) l.Add("SN RESET");
        if (f.HasFlag(FrameFlag.BagViolation)) l.Add("BAG"); if (f.HasFlag(FrameFlag.LmaxViolation)) l.Add("LMAX"); if (f.HasFlag(FrameFlag.RmDuplicate)) l.Add("RM DUP"); if (f.HasFlag(FrameFlag.NotInIcd)) l.Add("NOT IN ICD");
        if (f.HasFlag(FrameFlag.Reassembly)) l.Add("REASM"); if (f.HasFlag(FrameFlag.SubVlOrder)) l.Add("SUB-VL ORDER");
        return string.Join(" · ", l);
    }

    public object Frame(int i)
    {
        var c = Capture; var a = Analysis; if (c == null || a == null || i < 0 || i >= c.Count) return new { };
        var f = c.Frame(i); var port = a.PortOf(i); var vl = a.IcdOf(c.Vl(i));
        var hex = new StringBuilder(); for (int k = 0; k < f.Length; k += 16) hex.Append($"{k:x4}  ").Append(string.Join(" ", f.Slice(k, Math.Min(16, f.Length - k)).ToArray().Select(b => b.ToString("x2")))).Append('\n');
        return new
        {
            i, t = (c.TimeNs(i) - c.FirstNs) / 1e9, abs = DateTime.UnixEpoch.AddTicks(c.TimeNs(i) / 100).ToString("yyyy-MM-dd HH:mm:ss.fffffff"), net = c.Network(i) == 0 ? "A" : "B", kind = c.Kind(i).ToString().ToUpperInvariant(),
            dstMac = AfdxCapture.Mac(f, 0), srcMac = AfdxCapture.Mac(f, 6), vl = c.Vl(i), sn = c.Sn(i), wire = c.WireLength(i), captured = c.CapturedLength(i), lmax = vl?.Lmax ?? 0, bag = vl?.BagMs ?? 0,
            ipDst = AfdxCapture.Ip(c.IpDst(i)), udpSrc = c.UdpSrc(i), udpDst = c.UdpDst(i), payload = c.Payload(i).Length, no = i + 1, ipId = c.IpId(i), fragment = c.IsFragment(i), fragOffset = c.FragOffset(i), moreFragments = c.MoreFragments(i), vlFrom = c.VlFrom(i).ToString().ToUpperInvariant(), hasSn = c.HasSn(i), port = port?.Name ?? "", portType = port?.PortType ?? "", subVl = port?.SubVl ?? 0, vlName = vl?.Name ?? "",
            flags = FlagText(a.Flags[i]), @params = a.Decode(i).Select(x => new { name = x.name, eu = double.IsFinite(x.eu) ? x.eu : (double?)null, unit = x.unit, hex = x.hex, channel = ChannelId(c.Vl(i), x.name) }).ToList(), hex = hex.ToString(),
        };
    }

    public object Stats()
    {
        var a = Reanalyse(); if (a == null) return new { has = false };
        long span = a.SpanNs; double dur = span / 1e9;
        return new
        {
            has = true, capture = CaptureName, frames = Capture!.Count, afdx = Capture.Count - a.Arp - a.Icmp - a.OtherIp - a.Other, ipv4 = a.Ipv4, udp = a.Udp, tcp = a.Tcp, rule = Capture.Rule.IsDefault ? "MAC" : "MAC + " + string.Join(" + ", new[] { Capture.Rule.IpFallback ? "IP 224.224.x.x" : "", Capture.Rule.UdpPorts.Count + Capture.Rule.UdpEndpoints.Count > 0 ? "UDP map" : "" }.Where(x => x.Length > 0)), arp = a.Arp, icmp = a.Icmp, otherIp = a.OtherIp, other = a.Other, durationS = dur, errors = a.TotalErrors, checkMs = a.ElapsedMs,
            bandwidthBps = a.Vls.Values.Sum(v => v.BandwidthBps(span)), expectedBps = a.Vls.Values.Where(v => v.InIcd).Sum(v => v.ExpectedBps * (v.Network == "AB" ? 1 : 1)),
            vls = a.Vls.Values.OrderBy(v => v.Vl).Select(v => VlRow(v, span)).ToList(),
            notInCapture = (Icd.Vls ?? new()).Where(x => !a.Vls.ContainsKey(x.VlId)).Select(x => x.VlId).ToList(),
        };
    }
    static object VlRow(VlStats v, long span) => new
    {
        vl = v.Vl, name = v.Name, inIcd = v.InIcd, network = v.Network, bagMs = v.BagMs, bagFromIcd = v.BagFromIcd, lmax = v.Lmax, framesA = v.Frames[0], framesB = v.Frames[1], accepted = v.Accepted,
        snGapsA = v.SnGaps[0], snGapsB = v.SnGaps[1], snRevA = v.SnReversed[0], snRevB = v.SnReversed[1], resets = v.SnResets[0] + v.SnResets[1], bagViolA = v.BagViolations[0], bagViolB = v.BagViolations[1], lmaxViol = v.LmaxViolations,
        missingA = v.Missing[0], missingB = v.Missing[1], skewMeanUs = v.SkewMeanUs, skewMaxUs = v.SkewMaxUs, pairs = v.Pairs, skewViol = v.SkewViolations,
        meanMsA = v.MeanIntervalMs(0), meanMsB = v.MeanIntervalMs(1), minMsA = v.Intervals[0] > 0 ? v.IntervalMinMs[0] : 0, minMsB = v.Intervals[1] > 0 ? v.IntervalMinMs[1] : 0, jitterMsA = v.JitterMs(0), jitterMsB = v.JitterMs(1),
        maxWire = v.MaxWire, bandwidthBps = v.BandwidthBps(span), expectedBps = v.ExpectedBps, errors = v.Errors,
        fragments = v.Fragments, messages = v.Messages, msgComplete = v.MsgComplete, msgIncomplete = v.MsgIncomplete, msgReordered = v.MsgReordered, msgTruncated = v.MsgTruncated, subVlBreaks = v.SubVlBreaks, byIp = v.ByIp, byUdp = v.ByUdp,
        subVls = v.SubVls.Values.Select(x => new { sub = x.SubVl, frames = x.Frames, meanMs = x.MeanIntervalMs, maxMs = x.IntMaxMs, meanBags = v.BagMs > 0 ? x.MeanIntervalMs / v.BagMs : 0 }).ToList(),
    };

    // ------------------------------------------------------------------ trend channels: one DataFile per analysed capture, a channel per ICD parameter
    DataFile? _df;
    public static string ChannelFile => "AFDX capture";
    static string ChannelId(int vl, string param) => $"{ChannelFile}|VL {vl}|{param}";
    void RebuildChannels()
    {
        if (_df != null) { _e.State.Files.Remove(_df); _df = null; }
        var a = Analysis; if (a == null || !Icd.IsAfdx) { _e.FilesChangedExternally(); return; }
        var df = new DataFile(ChannelFile, "AFDX", new Dictionary<string, object>(), null);
        var byPort = new Dictionary<IcdAfdxPort, List<int>>();
        foreach (int i in a.Accepted) { var p = a.PortOf(i); if (p == null) continue; if (!byPort.TryGetValue(p, out var l)) byPort[p] = l = new List<int>(); l.Add(i); }
        foreach (var v in Icd.Vls!)
        {
            var g = new ChannelGroup($"VL {v.VlId}", new Dictionary<string, object> { ["name"] = v.Name });
            foreach (var port in v.Ports)
            {
                if (!byPort.TryGetValue(port, out var idx) || port.Params.Count == 0) continue; var arr = idx.ToArray();
                var time = new Channel(df.Name, g.Name, port.Name + " time", new TimeSource(Capture!, arr), new Dictionary<string, object>()) { Unit = "s", DataType = "DBL" };
                foreach (var prm in port.Params)
                    g.Channels.Add(new Channel(df.Name, g.Name, prm.Name, new ParamSource(Capture!, arr, prm), new Dictionary<string, object>()) { Unit = prm.Unit, DataType = "DBL", XKind = XKind.Explicit, XChannel = time, XUnit = "s", Description = $"{port.Name} offset {prm.Offset} {prm.Type}" });
            }
            if (g.Channels.Count > 0) df.Groups.Add(g);
        }
        _df = df; _e.State.Files.Add(df); _e.FilesChangedExternally();
    }
    sealed class TimeSource : IValueSource
    {
        readonly AfdxCapture _c; readonly int[] _i; readonly long _t0;
        public TimeSource(AfdxCapture c, int[] i) { _c = c; _i = i; _t0 = c.FirstNs; }
        public long Length => _i.Length;
        public int Read(long first, Span<double> dst) { int n = (int)Math.Max(0, Math.Min(dst.Length, _i.Length - first)); for (int k = 0; k < n; k++) dst[k] = (_c.TimeNs(_i[first + k]) - _t0) / 1e9; if (n < dst.Length) dst[n..].Fill(double.NaN); return n; }
    }
    sealed class ParamSource : IValueSource
    {
        readonly AfdxCapture _c; readonly int[] _i; readonly IcdAfdxParam _p;
        public ParamSource(AfdxCapture c, int[] i, IcdAfdxParam p) { _c = c; _i = i; _p = p; }
        public long Length => _i.Length;
        public int Read(long first, Span<double> dst) { int n = (int)Math.Max(0, Math.Min(dst.Length, _i.Length - first)); for (int k = 0; k < n; k++) dst[k] = _p.Eu(_c.Payload(_i[first + k])); if (n < dst.Length) dst[n..].Fill(double.NaN); return n; }
    }
    public string? TrendChannel(int vl, string param) { var id = ChannelId(vl, param); return _e.Find(id) != null ? id : null; }

    // ------------------------------------------------------------------ run (sender + receiver on the project's network device)
    INetworkDevice? _dev; AfdxSender? _tx; DispatcherTimer? _timer; Stopwatch? _sw;
    public bool Running => _tx?.Running == true;
    public string LastError { get; private set; } = "";
    public double ElapsedS => _sw?.Elapsed.TotalSeconds ?? 0;
    public string CaptureDir => P.CaptureDir.Length > 0 ? P.CaptureDir : Path.Combine(AppPaths.LocalDir, "captures");

    INetworkDevice MakeDevice(bool manual)
    {
        if (P.Device == "virtual") return new VirtualNetworkDevice { ManualClock = manual, Faults = S.Faults };
        if (P.Device == "local") { if (manual) throw new InvalidOperationException("batch mode runs on the virtual network only"); return new LocalNetworkDevice { ReceiveOwn = false }; }   // like a port: own transmissions are not received back
        if (manual) throw new InvalidOperationException("batch mode runs on the virtual network only");
        var roles = NetworkRoles.Assign(Pcap.Ports(), S.NicA, S.NicB); foreach (var n in roles.Notes) Diag.Trace("network roles: " + n);
        if (roles.NicA == null && roles.NicB == null) throw new InvalidOperationException("no network port selected (PROJECT › NETWORK A / B)" + (Pcap.Available ? "" : " — packet-capture library not installed"));
        return new PcapNetworkDevice(roles.NicA, roles.NicB);
    }

    /// <summary>true while the run only receives (ANALYZE › LISTEN: nothing is sent, the capture fills live).</summary>
    public bool Listening { get; private set; }
    public void Start(bool listenOnly = false)
    {
        if (Running) return; LastError = "";
        try
        {
            if (Vls.Count == 0 && !listenOnly) { _e.SetStatus("Nothing to send: load an ARINC 664 ICD (ICD rail) first"); return; }
            Stop();
            _dev = MakeDevice(false); _dev.Open(); _dev.Start(); Listening = listenOnly;
            _tx = new AfdxSender(_dev, Vls, S.SourcesById(), listenOnly ? Vls.Select(v => v.VlId).ToHashSet() : S.Disabled.ToHashSet()) { Errors = S.Errors, Equipment = (ushort)S.Equipment };
            Capture = new AfdxCapture(1 << 22, S.VlRule(Icd)) { Path = "" }; Analysis = null; _analysedCount = -1; _view = null;
            _tx.Start(); _sw = Stopwatch.StartNew();
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) }; _timer.Tick += (_, _) => Pump(); _timer.Start();
            _e.SetLed("busy"); _e.SetStatus(listenOnly ? $"Listening on {_dev.Name}" : $"Sending {_tx.Vls.Count(v => v.Enabled)} VLs on {_dev.Name}"); _e.Touch();
        }
        catch (Exception ex) { LastError = ex.Message; Diag.Trace("afdx start failed " + ex); _e.SetStatus("Start failed: " + ex.Message); _e.SetLed("error"); _tx?.Dispose(); _dev?.Dispose(); _tx = null; _dev = null; }
    }
    void Pump()
    {
        if (_dev == null || Capture == null) return; var cap = Capture; _dev.Poll((n, t, f) => cap.Add(n, t, f));
        if (_dev is PcapNetworkDevice pd && pd.LastSendError.Length > 0 && LastError.Length == 0) { LastError = "the port refused to send (" + pd.Counters.SendErrors + " frames): " + pd.LastSendError; _e.SetStatus(LastError); } if (_tx?.LastError.Length > 0) { LastError = _tx.LastError; _e.SetStatus("Sender stopped: " + LastError); _e.SetLed("error"); }
    }

    public void Stop()
    {
        _timer?.Stop(); _timer = null;
        if (_tx != null) { _tx.Stop(); Thread.Sleep(30); Pump(); }
        if (_tx != null && Capture != null && Capture.Count > 0)
        {
            try { Directory.CreateDirectory(CaptureDir); var path = Path.Combine(CaptureDir, $"{San(P.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}_afdx.pcapng"); Capture.Save(path); LastCapturePath = path; } catch (Exception ex) { Diag.Trace("afdx save " + ex); }
            Reanalyse(true); _e.SetLed("ok"); _e.SetStatus($"Stopped: {Capture.Count:N0} frames → {Path.GetFileName(LastCapturePath)} · {Analysis?.TotalErrors ?? 0:N0} check findings");
        }
        _dev?.Dispose(); _dev = null; _lastTx = _tx; _tx = null; _e.Touch();
    }
    AfdxSender? _lastTx;
    public AfdxSender? LastSender => _tx ?? _lastTx;
    static string San(string s) => new string(s.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    /// <summary>Batch: <paramref name="seconds"/> of traffic on the virtual network's manual clock, written as a capture and analysed.</summary>
    public object RunFor(double seconds)
    {
        Stop(); var sw = Stopwatch.StartNew();
        var dev = new VirtualNetworkDevice { ManualClock = true, Faults = S.Faults }; dev.Open(); dev.Start();
        var tx = new AfdxSender(dev, Vls, S.SourcesById(), S.Disabled.ToHashSet()) { Errors = S.Errors, Equipment = (ushort)S.Equipment };
        var cap = new AfdxCapture(1 << 22, S.VlRule(Icd)); tx.RunBatch(dev, seconds, (n, t, f) => cap.Add(n, t, f));
        Directory.CreateDirectory(CaptureDir); var path = Path.Combine(CaptureDir, $"{San(P.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}_batch.pcapng"); cap.Save(path); LastCapturePath = path;
        Capture = cap; _lastTx = tx; Reanalyse(true);
        _e.SetStatus($"Batch {seconds:G} s: {tx.Vls.Sum(v => v.Sent[0] + v.Sent[1]):N0} frames sent, {cap.Count:N0} received, {Analysis!.TotalErrors:N0} check findings → {Path.GetFileName(path)}"); _e.Touch();
        return new { sent = tx.Vls.Sum(v => v.Sent[0] + v.Sent[1]), received = cap.Count, dropped = dev.Counters.Dropped, errors = Analysis.TotalErrors, path, ms = sw.Elapsed.TotalMilliseconds };
    }

    public object RunDto()
    {
        var tx = _tx ?? _lastTx; var dev = _dev;
        return new
        {
            running = Running, listening = Running && Listening, elapsedS = ElapsedS, device = P.Device == "virtual" ? "Virtual network (A/B loopback)" : P.Device == "local" ? "Shared virtual network (this PC, other NexysNet instances)" : $"Ethernet A {S.NicA} / B {S.NicB}", error = LastError, lastCapture = LastCapturePath, captureDir = CaptureDir,
            sent = tx?.Vls.Sum(v => v.Sent[0] + v.Sent[1]) ?? 0, sentA = tx?.Vls.Sum(v => v.Sent[0]) ?? 0, sentB = tx?.Vls.Sum(v => v.Sent[1]) ?? 0, sendErrors = tx?.Vls.Sum(v => v.Attempted[0] + v.Attempted[1] - v.Sent[0] - v.Sent[1]) ?? 0, received = dev?.Counters.Received ?? Capture?.Count ?? 0,
            expectedBps = Vls.Where(v => !S.Disabled.Contains(v.VlId)).Sum(v => (v.Lmax + 20) * 8.0 / (v.BagMs / 1000) * (v.Network == "AB" ? 2 : 1)),
            vls = tx?.Vls.Select(v => new { vl = v.Vl.VlId, enabled = v.Enabled, frames = v.Frames, sentA = v.Sent[0], sentB = v.Sent[1], firstSnA = v.FirstSn[0], lastSnA = v.LastSn[0], firstSnB = v.FirstSn[1], lastSnB = v.LastSn[1], attemptedA = v.Attempted[0], attemptedB = v.Attempted[1], messages = v.Messages, fragMessages = v.FragMessages, bagMs = v.Vl.BagMs, meanMs = v.IntMeanMs, minMs = v.IntN > 0 ? v.IntMinMs : 0, maxMs = v.IntMaxMs, lateMeanUs = v.LateMeanUs, lateStdUs = v.LateStdUs, lateMaxUs = v.LateMaxUs, slips = v.Slips }).ToList(),
        };
    }

    // ------------------------------------------------------------------ ICD / settings edits
    public void SetVl(int vl, string field, JsonElement v)
    {
        var x = Vls.FirstOrDefault(q => q.VlId == vl); if (x == null) return;
        switch (field)
        {
            case "vl_id": { int id = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.Parse(v.GetString() ?? "0"); if (!Vls.Any(q => q.VlId == id)) x.VlId = id; break; }
            case "name": x.Name = v.GetString() ?? x.Name; break;
            case "bag_ms": x.BagMs = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.Parse(v.GetString() ?? "8", CultureInfo.InvariantCulture); break;
            case "lmax": x.Lmax = v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.Parse(v.GetString() ?? "1518"); break;
            case "network": { var s = (v.GetString() ?? "AB").ToUpperInvariant(); x.Network = s is "A" or "B" ? s : "AB"; break; }
            case "enabled": { bool on = v.GetBoolean(); S.Disabled.Remove(vl); if (!on) S.Disabled.Add(vl); break; }
            case "source.kind": Src(vl).Kind = v.GetString() is "const" or "ramp" or "sine" or "eu" ? v.GetString()! : "eu"; break;
            case "source.periodS": Src(vl).PeriodS = Math.Max(0.01, v.GetDouble()); break;
            case "source.value": Src(vl).Value = v.GetDouble(); break;
            case "source.amplitude": Src(vl).Amplitude = v.GetDouble(); break;
        }
        Changed();
    }
    BusDataSource Src(int vl) { var k = vl.ToString(CultureInfo.InvariantCulture); if (!S.Sources.TryGetValue(k, out var s)) S.Sources[k] = s = new BusDataSource { Kind = "eu", PeriodS = 2 }; return s; }
    public void SetParam(int vl, string port, int idx, string field, JsonElement v)
    {
        var pt = Vls.FirstOrDefault(q => q.VlId == vl)?.Ports.FirstOrDefault(p => p.Name == port); if (pt == null) return;
        if (idx < 0)
        {
            switch (field) { case "name": pt.Name = v.GetString() ?? pt.Name; break; case "size": pt.Size = Math.Clamp(Num(v), 1, pt.PortType == "queuing" ? AfdxIcd.MaxQueuingMessage : 1471); break; case "sub_vl": pt.SubVl = Math.Clamp(Num(v), 1, 4); break; case "port_type": pt.PortType = v.GetString() == "queuing" ? "queuing" : "sampling"; break; case "udp_dst": pt.UdpDst = Math.Clamp(Num(v), 0, 65535); break; case "udp_src": pt.UdpSrc = Math.Clamp(Num(v), 0, 65535); break; case "ip_dst": pt.IpDst = v.GetString() ?? pt.IpDst; break; case "ip_src": pt.IpSrc = v.GetString() ?? pt.IpSrc; break; }
        }
        else if (idx < pt.Params.Count)
        {
            var q = pt.Params[idx];
            switch (field) { case "name": q.Name = v.GetString() ?? q.Name; break; case "offset": q.Offset = Math.Max(0, Num(v)); break; case "type": q.Type = AfdxIcd.Types.Contains(v.GetString()) ? v.GetString()! : q.Type; break; case "endian": q.Endian = v.GetString() == "little" ? "little" : "big"; break; case "scale": q.Scale = Dbl(v, 1); break; case "offset_val": q.OffsetVal = Dbl(v, 0); break; case "unit": q.Unit = v.GetString() ?? ""; break; }
        }
        Changed();
        static int Num(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetInt32() : int.TryParse(e.GetString(), out int n) ? n : 0;
        static double Dbl(JsonElement e, double d) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : d;
    }
    public int AddVl()
    {
        int id = Vls.Count == 0 ? 1 : Vls.Max(v => v.VlId) + 1;
        Vls.Add(new IcdVl { VlId = id, Name = $"VL_{id}", BagMs = 8, Network = "AB", Lmax = AfdxIcd.Overhead + 64, Ports = { new IcdAfdxPort { Name = $"VL{id}_P1", UdpSrc = 20000, UdpDst = 30000 + id, IpDst = $"224.224.{(id >> 8) & 255}.{id & 255}", Size = 64, Params = { new IcdAfdxParam { Name = $"VL{id}_CNT", Offset = 0, Type = "u16" } } } } });
        Icd.Schema = "1.1"; Changed(); return id;
    }
    public void RemoveVl(int vl) { Vls.RemoveAll(v => v.VlId == vl); Changed(); }
    public void AddParam(int vl, string port)
    {
        var pt = Vls.FirstOrDefault(q => q.VlId == vl)?.Ports.FirstOrDefault(p => p.Name == port); if (pt == null) return;
        int off = pt.Params.Count == 0 ? 0 : pt.Params.Max(p => p.Offset + p.Size); pt.Params.Add(new IcdAfdxParam { Name = $"{pt.Name}_X{pt.Params.Count + 1}", Offset = Math.Min(off, Math.Max(0, pt.Size - 2)), Type = "u16" }); Changed();
    }
    public void RemoveParam(int vl, string port, int idx) { var pt = Vls.FirstOrDefault(q => q.VlId == vl)?.Ports.FirstOrDefault(p => p.Name == port); if (pt != null && idx >= 0 && idx < pt.Params.Count) { pt.Params.RemoveAt(idx); Changed(); } }
    void Changed() { _e.Dirty = true; _analysedIcd = null; _e.Touch(); }

    public void SetSetting(string field, JsonElement v)
    {
        var s = S; double D() => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0; int N() => (int)Math.Max(0, D());
        switch (field)
        {
            case "nicA": s.NicA = v.GetString() ?? ""; break; case "nicB": s.NicB = v.GetString() ?? ""; break;
            case "jmaxMs": s.JmaxMs = Math.Max(0, D()); break; case "skewMaxMs": s.SkewMaxMs = Math.Max(0, D()); break; case "pairWindowMs": s.PairWindowMs = Math.Max(0.1, D()); break;
            case "err.snSkip": s.Errors.SnSkipEvery = N(); break; case "err.dropA": s.Errors.DropAEvery = N(); break; case "err.dropB": s.Errors.DropBEvery = N(); break; case "err.oversize": s.Errors.OversizeEvery = N(); break; case "err.early": s.Errors.EarlyEvery = N(); break;
            case "net.delayUs": s.Faults.DelayUs = Math.Max(0, D()); break; case "net.jitterUs": s.Faults.JitterUs = Math.Max(0, D()); break; case "net.lossEvery": s.Faults.LossEvery = N(); break; case "net.dupEvery": s.Faults.DupEvery = N(); break; case "net.skewBUs": s.Faults.SkewBUs = Math.Max(0, D()); break;
            case "peerName": s.PeerName = v.GetString() ?? ""; break;
            case "err.fragDrop": s.Errors.FragDropEvery = N(); break;
            case "vlFromIp": s.VlFromIp = v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true"; break;
            case "vlFromIcd": s.VlFromIcd = v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true"; break;
            case "udpSn": s.UdpSn = v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.String && v.GetString() == "true"; break;
            case "udpVls": s.UdpVls = ParseUdpMap(v.GetString() ?? ""); break;
        }
        _analysedIcd = null; Changed();
    }
    /// <summary>"5000=900, 5001=901" (UDP destination port = VL id) → mapping list; malformed parts are ignored.</summary>
    public static List<AfdxProjectSettings.UdpVlMap> ParseUdpMap(string text)
    {
        var l = new List<AfdxProjectSettings.UdpVlMap>();
        foreach (var part in text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(new[] { '=', ':', '>' }, StringSplitOptions.RemoveEmptyEntries);
            if (kv.Length == 2 && int.TryParse(kv[0], out int port) && int.TryParse(kv[1], out int vl) && port is > 0 and <= 65535 && vl is >= 0 and <= 65535) l.Add(new() { UdpDst = port, Vl = vl });
        }
        return l;
    }
    static string UdpMapText(List<AfdxProjectSettings.UdpVlMap> l) => string.Join(", ", l.Select(m => $"{m.UdpDst}={m.Vl}"));

    /// <summary>Peer report from a CSV (one row per VL): columns vl | vl_id, rx_a | frames_a | received_a, rx_b | frames_b | received_b,
    /// errors, note (header names matched case-insensitively). Returns the rows taken.</summary>
    public int ImportPeerCsv(string path)
    {
        var rows = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l => l.Split(new[] { ',', ';', '\t' })).ToList(); if (rows.Count < 2) return 0;
        var h = rows[0].Select(x => x.Trim().Trim('"').ToLowerInvariant().Replace(" ", "_")).ToList();
        int Col(params string[] names) { foreach (var n in names) { int k = h.IndexOf(n); if (k >= 0) return k; } return -1; }
        int cVl = Col("vl", "vl_id", "vlid"), cA = Col("rx_a", "rxa", "frames_a", "received_a"), cB = Col("rx_b", "rxb", "frames_b", "received_b"), cE = Col("errors", "error", "errors_seen"), cN = Col("note", "notes", "remark");
        if (cVl < 0) throw new InvalidDataException("peer CSV: no 'vl' column (header: " + string.Join(",", h) + ")");
        int n = 0;
        foreach (var r in rows.Skip(1))
        {
            string F(int c) => c >= 0 && c < r.Length ? r[c].Trim().Trim('"') : ""; if (!int.TryParse(F(cVl), out int vl)) continue;
            var k = vl.ToString(CultureInfo.InvariantCulture); if (!S.Peer.TryGetValue(k, out var pr)) S.Peer[k] = pr = new AfdxProjectSettings.PeerReport();
            long? L(int c) => long.TryParse(F(c), out long x) ? x : null;
            if (cA >= 0) pr.RxA = L(cA); if (cB >= 0) pr.RxB = L(cB); if (cE >= 0) pr.Errors = L(cE); if (cN >= 0) pr.Note = F(cN); n++;
        }
        Changed(); _e.SetStatus($"Peer report: {n} VLs from {Path.GetFileName(path)}"); return n;
    }

    public void SetPeer(int vl, string field, JsonElement v)
    {
        var k = vl.ToString(CultureInfo.InvariantCulture); if (!S.Peer.TryGetValue(k, out var r)) S.Peer[k] = r = new AfdxProjectSettings.PeerReport();
        long? L() => v.ValueKind == JsonValueKind.Number ? v.GetInt64() : long.TryParse(v.GetString(), out long x) ? x : null;
        switch (field) { case "rxA": r.RxA = L(); break; case "rxB": r.RxB = L(); break; case "errors": r.Errors = L(); break; case "note": r.Note = v.GetString() ?? ""; break; }
        Changed();
    }

    public object Ports() => new { available = Pcap.Available, library = Pcap.LibraryPath, error = Pcap.Available ? "" : Pcap.LastError, ports = Pcap.Ports().Select(p => new { id = p.Id, name = p.Name, detail = p.Description, canSend = p.CanSend }).ToList() };

    // ------------------------------------------------------------------ interoperability
    /// <summary>Peer → NexysNet: automatic verdict of the analysed capture against the ICD. Each item PASS / FAIL / N/A with the numbers.</summary>
    public List<InteropItem> PeerChecks()
    {
        var o = new List<InteropItem>(); var a = Reanalyse();
        if (a == null) { o.Add(new("capture", "A capture of the peer's traffic is open", "N/A", "open a .pcapng (Wireshark / dumpcap, interfaces A and B) or run a capture")); return o; }
        var icdVls = Icd.Vls ?? new(); var seen = a.Vls.Values.ToList(); long span = a.SpanNs;
        string V(IEnumerable<int> ids) { var l = ids.ToList(); return l.Count == 0 ? "" : string.Join(", ", l.Take(12)) + (l.Count > 12 ? $" … (+{l.Count - 12})" : ""); }
        o.Add(icdVls.Count == 0 ? new("icd", "ICD virtual links present", "N/A", "no ARINC 664 ICD — checks use measured BAG and both-network presence only") : new("icd", "ICD virtual links present", "PASS", $"{icdVls.Count} VLs in the ICD"));
        if (icdVls.Count > 0)
        {
            var missing = icdVls.Where(v => !a.Vls.ContainsKey(v.VlId) && !S.Disabled.Contains(v.VlId)).Select(v => v.VlId).ToList();
            o.Add(new("vl_seen", "Every ICD VL is received", missing.Count == 0 ? "PASS" : "FAIL", missing.Count == 0 ? $"{icdVls.Count} / {icdVls.Count}" : $"{icdVls.Count - missing.Count} / {icdVls.Count}; not received: VL {V(missing)}"));
            var extra = seen.Where(v => !v.InIcd).Select(v => v.Vl).ToList();
            o.Add(new("vl_unknown", "No VL outside the ICD", extra.Count == 0 ? "PASS" : "FAIL", extra.Count == 0 ? "0" : $"{extra.Count}: VL {V(extra)}"));
        }
        long gaps = seen.Sum(v => v.SnGaps[0] + v.SnGaps[1]), rev = seen.Sum(v => v.SnReversed[0] + v.SnReversed[1]);
        o.Add(new("sn", "Sequence numbers continuous (integrity)", gaps + rev == 0 ? "PASS" : "FAIL", $"gaps {gaps} (VL {V(seen.Where(v => v.SnGaps[0] + v.SnGaps[1] > 0).Select(v => v.Vl))}) · reversed/duplicate {rev}" + (seen.Sum(v => v.SnResets[0] + v.SnResets[1]) > 0 ? $" · resets {seen.Sum(v => v.SnResets[0] + v.SnResets[1])}" : ""), gaps + rev == 0 ? 0 : FirstFlag(FrameFlag.SnGap | FrameFlag.SnReversed)));
        long bag = seen.Sum(v => v.BagViolations[0] + v.BagViolations[1]);
        o.Add(new("bag", $"Frame spacing ≥ BAG − Jmax ({S.JmaxMs:G} ms)", bag == 0 ? "PASS" : "FAIL", $"{bag} intervals short (VL {V(seen.Where(v => v.BagViolations[0] + v.BagViolations[1] > 0).Select(v => v.Vl))})", bag == 0 ? 0 : FirstFlag(FrameFlag.BagViolation)));
        long lmax = seen.Sum(v => v.LmaxViolations);
        o.Add(icdVls.Count == 0 ? new("lmax", "Frames ≤ Lmax", "N/A", "needs the ICD") : new("lmax", "Frames ≤ Lmax", lmax == 0 ? "PASS" : "FAIL", $"{lmax} frames longer (VL {V(seen.Where(v => v.LmaxViolations > 0).Select(v => v.Vl))})", lmax == 0 ? 0 : FirstFlag(FrameFlag.LmaxViolation)));
        var red = seen.Where(v => v.Network == "AB").ToList(); long miss = red.Sum(v => v.Missing[0] + v.Missing[1]);
        o.Add(red.Count == 0 ? new("ab", "Redundant VLs arrive on A and B", "N/A", "no redundant VL") : new("ab", "Redundant VLs arrive on A and B", miss == 0 ? "PASS" : "FAIL", $"{red.Count} redundant VLs · missing A {red.Sum(v => v.Missing[0])} · missing B {red.Sum(v => v.Missing[1])}"));
        var single = seen.Where(v => v.InIcd && v.Network != "AB" && v.Frames[v.Network == "A" ? 1 : 0] > 0).Select(v => v.Vl).ToList();
        if (single.Count > 0) o.Add(new("net", "Single-network VLs only on their network", "FAIL", $"VL {V(single)}"));
        double skMax = red.Count == 0 ? 0 : red.Max(v => v.SkewMaxUs); long skV = red.Sum(v => v.SkewViolations);
        o.Add(red.Count == 0 ? new("skew", $"A/B skew ≤ {S.SkewMaxMs:G} ms", "N/A", "no redundant VL") : new("skew", $"A/B skew ≤ {S.SkewMaxMs:G} ms", skV == 0 ? "PASS" : "FAIL", $"max {skMax / 1000:F3} ms · mean {(red.Sum(v => v.Pairs) > 0 ? red.Sum(v => v.SkewSumUs) / red.Sum(v => v.Pairs) / 1000 : 0):F3} ms · over the limit {skV}"));
        var frag = seen.Where(v => v.Fragments > 0).ToList(); long inc = frag.Sum(v => v.MsgIncomplete), reo = frag.Sum(v => v.MsgReordered);
        o.Add(frag.Count == 0 ? new("reassembly", "Fragmented messages reassemble (queuing ports)", "N/A", "no IP-fragmented message")
            : new("reassembly", "Fragmented messages reassemble (queuing ports)", inc + reo == 0 ? "PASS" : "FAIL", $"{frag.Sum(v => v.MsgComplete)} complete · {inc} incomplete · {reo} out of order · {frag.Sum(v => v.Fragments)} fragments on VL {V(frag.Select(v => v.Vl))}" + (frag.Sum(v => v.MsgTruncated) > 0 ? $" · {frag.Sum(v => v.MsgTruncated)} cut by the capture start/end" : ""), inc + reo == 0 ? 0 : FirstFlag(FrameFlag.Reassembly)));
        var multi = seen.Where(v => v.SubVls.Count > 1).ToList(); long brk = multi.Sum(v => v.SubVlBreaks);
        o.Add(multi.Count == 0 ? new("subvl", "Sub-VLs served in round-robin order", "N/A", "no VL with more than one sub-VL in the ICD")
            : new("subvl", "Sub-VLs served in round-robin order", brk == 0 ? "PASS" : "FAIL", $"{multi.Count} VLs with sub-VLs · order breaks {brk}" + (brk > 0 ? $" (VL {V(multi.Where(v => v.SubVlBreaks > 0).Select(v => v.Vl))})" : ""), brk == 0 ? 0 : FirstFlag(FrameFlag.SubVlOrder)));
        int nan = 0, dec = 0; foreach (int i in a.Accepted.Take(20000)) foreach (var d in a.Decode(i)) { dec++; if (!double.IsFinite(d.eu)) nan++; }
        o.Add(icdVls.Count == 0 ? new("decode", "Parameters decode", "N/A", "needs the ICD") : new("decode", "Parameters decode (finite values)", dec > 0 && nan == 0 ? "PASS" : dec == 0 ? "FAIL" : "FAIL", $"{dec - nan} / {dec} values of the first 20,000 accepted frames"));
        var slow = seen.Where(v => v.BagMs > 0 && v.Intervals[0] + v.Intervals[1] > 0 && Math.Max(v.MeanIntervalMs(0), v.MeanIntervalMs(1)) > v.BagMs * 1.5 + S.JmaxMs).Select(v => v.Vl).ToList();
        o.Add(new("rate", "VLs send close to their BAG (mean interval ≤ 1.5 × BAG)", slow.Count == 0 ? "PASS" : "INFO", slow.Count == 0 ? $"{seen.Count} VLs" : $"slower: VL {V(slow)} (an application may send less often than the BAG allows)"));
        o.Add(new("other", "Other traffic on the networks", "INFO", $"ARP {a.Arp} · ICMP {a.Icmp} · other IP {a.OtherIp} · other {a.Other}"));
        return o;
    }
    /// <summary>One checklist row; <paramref name="Frame"/> = capture frame number (1-based, as Wireshark's No.) of the first frame behind a FAIL, 0 = none.</summary>
    public sealed record InteropItem(string Id, string Check, string Verdict, string Detail, int Frame = 0);
    int FirstFlag(FrameFlag f) { var a = Analysis; if (a == null) return 0; for (int i = 0; i < a.Flags.Length; i++) if ((a.Flags[i] & f) != 0) return i + 1; return 0; }

    /// <summary>NexysNet → peer: what we sent per VL (last run) against what the peer reported.</summary>
    public sealed record TxRow(int Vl, string Name, string Network, double BagMs, bool Enabled, long SentA, long SentB, double BagMeasMs, double JitterUs, long? RxA, long? RxB, long? Errors, string Note, string Verdict);
    public List<TxRow> TxTable()
    {
        var tx = _tx ?? _lastTx; var l = new List<TxRow>();
        foreach (var v in Vls)
        {
            var t = tx?.Vls.FirstOrDefault(x => x.Vl.VlId == v.VlId); var pr = S.Peer.GetValueOrDefault(v.VlId.ToString(CultureInfo.InvariantCulture));
            long sa = t?.Sent[0] ?? 0, sb = t?.Sent[1] ?? 0; string verdict = "—";
            if (pr != null && (pr.RxA != null || pr.RxB != null))
            {
                bool okA = v.Network == "B" || pr.RxA == null || pr.RxA >= sa, okB = v.Network == "A" || pr.RxB == null || pr.RxB >= sb, okE = pr.Errors == null || pr.Errors == 0;
                verdict = okA && okB && okE ? "PASS" : "FAIL";
            }
            l.Add(new TxRow(v.VlId, v.Name, v.Network, v.BagMs, !S.Disabled.Contains(v.VlId), sa, sb, t?.IntMeanMs ?? 0, t?.LateStdUs ?? 0, pr?.RxA, pr?.RxB, pr?.Errors, pr?.Note ?? "", verdict));
        }
        return l;
    }

    public string ReportMarkdown()
    {
        var ic = CultureInfo.InvariantCulture; var sb = new StringBuilder(); var checks = PeerChecks(); var a = Analysis;
        sb.Append($"# Interoperability report — {P.Name}\n\n");
        sb.Append($"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}\n- Tool: {AppInfo.Name} {AppInfo.VersionText}\n- Peer: {(S.PeerName.Length > 0 ? S.PeerName : "(not named)")}\n- ICD: {Icd.Source ?? "(none)"} · {(Icd.Vls ?? new()).Count} VLs (hub schema {Icd.Schema ?? "1.0"})\n");
        sb.Append($"- Capture: {(Capture?.Path.Length > 0 ? Capture.Path : CaptureName)} · {Capture?.Count ?? 0:N0} frames · {(a != null ? a.SpanNs / 1e9 : 0):F2} s\n- Check settings: Jmax {S.JmaxMs.ToString(ic)} ms · skew limit {S.SkewMaxMs.ToString(ic)} ms · pair window {S.PairWindowMs.ToString(ic)} ms\n\n");
        sb.Append("## 1. Peer → NexysNet (automatic)\n\n| # | Check | Verdict | Detail | Evidence |\n|---|---|---|---|---|\n");
        int k = 1; foreach (var c in checks) sb.Append($"| {k++} | {c.Check} | **{c.Verdict}** | {c.Detail.Replace("|", "/")} | {(c.Frame > 0 ? $"[frame {c.Frame}](#frame-{c.Frame}) — Wireshark `frame.number == {c.Frame}`" : "")} |\n");
        sb.Append($"\nResult: {checks.Count(c => c.Verdict == "PASS")} PASS · {checks.Count(c => c.Verdict == "FAIL")} FAIL · {checks.Count(c => c.Verdict is "N/A" or "INFO")} N/A or INFO.\n\n");
        if (a != null)
        {
            sb.Append("### Per VL\n\n| VL | Name | Net | BAG ms | Frames A | Frames B | Accepted | SN gaps A/B | Reversed A/B | BAG viol. A/B | Lmax viol. | Missing A/B | Skew mean / max µs | Mean interval ms A/B |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (var v in a.Vls.Values.OrderBy(v => v.Vl)) sb.Append(FormattableString.Invariant($"| {v.Vl} | {v.Name} | {v.Network} | {v.BagMs:G} | {v.Frames[0]} | {v.Frames[1]} | {v.Accepted} | {v.SnGaps[0]}/{v.SnGaps[1]} | {v.SnReversed[0]}/{v.SnReversed[1]} | {v.BagViolations[0]}/{v.BagViolations[1]} | {v.LmaxViolations} | {v.Missing[0]}/{v.Missing[1]} | {v.SkewMeanUs:F1} / {v.SkewMaxUs:F1} | {v.MeanIntervalMs(0):F3}/{v.MeanIntervalMs(1):F3} |\n"));
            sb.Append('\n');
        }
        sb.Append("## 2. NexysNet → peer (reported by the peer)\n\n| VL | Name | Net | BAG ms | Sent A | Sent B | Measured interval ms | Peer rx A | Peer rx B | Peer errors | Verdict | Note |\n|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var r in TxTable()) sb.Append(FormattableString.Invariant($"| {r.Vl} | {r.Name} | {r.Network} | {r.BagMs:G} | {r.SentA} | {r.SentB} | {r.BagMeasMs:F3} | {r.RxA} | {r.RxB} | {r.Errors} | {r.Verdict} | {r.Note.Replace("|", "/")} |\n"));
        var ev = checks.Where(c => c.Frame > 0).ToList();
        if (ev.Count > 0 && Capture != null)
        {
            sb.Append("\n### Evidence frames\n\n");
            foreach (var c in ev) { int i = c.Frame - 1; sb.Append(FormattableString.Invariant($"<a id=\"frame-{c.Frame}\"></a>- **frame {c.Frame}** ({c.Id}): t = {(Capture.TimeNs(i) - Capture.FirstNs) / 1e9:F6} s · network {(Capture.Network(i) == 0 ? "A" : "B")} · VL {Capture.Vl(i)} · SN {Capture.Sn(i)} · {Capture.WireLength(i)} B · {FlagText(a!.Flags[i])}\n")); }
        }
        sb.Append("\n## 3. Open items (UNVERIFIED until measured with the peer)\n\n- IP address plan (multicast 224.224.x.x destination, unicast sources) and UDP port ranges of the peer\n- Queuing-port fragmentation / reassembly of messages larger than one frame\n- Sub-VL transmit order inside one VL\n- The peer's skew limit and integrity-checking mode\n- Source-MAC interface id (A = 1, B = 2) set by the peer's end system\n");
        return sb.ToString();
    }
    public string ExportReport(string path) { File.WriteAllText(path, ReportMarkdown(), new UTF8Encoding(false)); _e.SetStatus("Interop report → " + path); return path; }

    // ------------------------------------------------------------------ snapshot + bridge
    public object Dto()
    {
        var a = Analysis;
        return new
        {
            icd = new { source = Icd.Source ?? "", vls = Vls.Count, ports = Vls.Sum(v => v.Ports.Count), @params = Vls.Sum(v => v.Ports.Sum(p => p.Params.Count)), errors = AfdxIcd.Validate(Icd).Count(x => x.StartsWith("error")), warnings = AfdxIcd.Validate(Icd).Count(x => x.StartsWith("warning")) },
            vlList = Vls.Select(v => new { vl = v.VlId, name = v.Name, bagMs = v.BagMs, lmax = v.Lmax, network = v.Network, ports = v.Ports.Count, subVls = v.SubVls, @params = v.Ports.Sum(p => p.Params.Count), enabled = !S.Disabled.Contains(v.VlId), expectedBps = (v.Lmax + 20) * 8.0 / (v.BagMs / 1000), source = Src0(v.VlId) }).ToList(),
            settings = new { nicA = S.NicA, nicB = S.NicB, jmaxMs = S.JmaxMs, skewMaxMs = S.SkewMaxMs, pairWindowMs = S.PairWindowMs, errors = S.Errors, faults = S.Faults, peerName = S.PeerName, vlFromIp = S.VlFromIp, vlFromIcd = S.VlFromIcd, udpSn = S.UdpSn, udpVls = UdpMapText(S.UdpVls) },
            run = RunDto(),
            capture = new { name = CaptureName, path = Capture?.Path ?? "", frames = Capture?.Count ?? 0, vls = a?.Vls.Count ?? 0, findings = a?.TotalErrors ?? 0 },
            npcap = Pcap.Available,
        };
    }
    object Src0(int vl) { var s = S.Sources.GetValueOrDefault(vl.ToString(CultureInfo.InvariantCulture)); return new { kind = s?.Kind ?? "eu", periodS = s?.PeriodS ?? 2, value = s?.Value ?? 0, amplitude = s?.Amplitude ?? 100 }; }

    public bool Dispatch(string cmd, JsonElement r, out object? result)
    {
        result = null;
        switch (cmd)
        {
            case "afdx.openPath": result = OpenCapture(Bridge.S(r, "path")); return true;
            case "afdx.filter": result = SetFilter(r); return true;
            case "afdx.frames": result = Frames(Bridge.I(r, "from"), Bridge.I(r, "count", 200)); return true;
            case "afdx.frame": result = Frame(Bridge.I(r, "i")); return true;
            case "afdx.stats": result = Stats(); return true;
            case "afdx.trend": result = TrendChannel(Bridge.I(r, "vl"), Bridge.S(r, "param")); return true;
            case "afdx.vl.set": SetVl(Bridge.I(r, "vl"), Bridge.S(r, "field"), r.GetProperty("value")); return true;
            case "afdx.vl.add": result = AddVl(); return true;
            case "afdx.vl.remove": RemoveVl(Bridge.I(r, "vl")); return true;
            case "afdx.param.set": SetParam(Bridge.I(r, "vl"), Bridge.S(r, "port"), Bridge.I(r, "idx", -1), Bridge.S(r, "field"), r.GetProperty("value")); return true;
            case "afdx.param.add": AddParam(Bridge.I(r, "vl"), Bridge.S(r, "port")); return true;
            case "afdx.param.remove": RemoveParam(Bridge.I(r, "vl"), Bridge.S(r, "port"), Bridge.I(r, "idx")); return true;
            case "afdx.icd": result = new { vls = Vls, validation = AfdxIcd.Validate(Icd), source = Icd.Source ?? "" }; return true;
            case "afdx.icd.dummy": { var d = AfdxIcd.Dummy(); P.Icd.Vls = d.Vls; P.Icd.Schema = "1.1"; P.Icd.Source = d.Source; Changed(); return true; }
            case "afdx.icd.saveCsv": { var path = Bridge.S(r, "path"); File.WriteAllText(path, AfdxIcd.ToCsv(Icd)); result = path; return true; }
            case "afdx.set": SetSetting(Bridge.S(r, "field"), r.GetProperty("value")); return true;
            case "afdx.peer.set": SetPeer(Bridge.I(r, "vl"), Bridge.S(r, "field"), r.GetProperty("value")); return true;
            case "afdx.ports": result = Ports(); return true;
            case "afdx.start": Start(); return true;
            case "afdx.listen": Start(listenOnly: true); return true;
            case "afdx.stop": Stop(); return true;
            case "afdx.runFor": result = RunFor(Bridge.D(r, "seconds", 10)); return true;
            case "afdx.status": result = RunDto(); return true;
            case "afdx.interop": result = new { checks = PeerChecks(), tx = TxTable(), peerName = S.PeerName, capture = CaptureName }; return true;
            case "afdx.interop.exportTo": result = ExportReport(Bridge.S(r, "path")); return true;
            case "afdx.peer.importCsvPath": result = ImportPeerCsv(Bridge.S(r, "path")); return true;
            case "afdx.wireshark.exportTo": result = WiresharkProfile.Write(Bridge.S(r, "dir"), Icd, AppInfo.Name + " " + AppInfo.VersionText); _e.SetStatus("Wireshark profile → " + result); return true;
            case "afdx.reanalyse": Reanalyse(true); _e.Touch(); result = Stats(); return true;
        }
        return false;
    }
}
