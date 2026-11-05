using System.IO;
using System.Text.Json;
using NexysView.Core.Arinc664;
using NexysView.Core.Bus;

namespace NexysNet.App.Services;

/// <summary>NexysNet application model: the shared chart/file engine (<see cref="EngineBase"/>, nexys-core) plus the ARINC 664 project
/// (.nxn = the project JSON with protocol arinc664; .nxb files of that protocol open too), the network side (<see cref="AfdxService"/>)
/// and the rail (START / PROJECT / ANALYZE / SIMULATE / ICD / INTEROP / SETTINGS = Mode).</summary>
public sealed class NetEngine : EngineBase
{
    public const string ProjectExt = ".nxn";
    public BusProject Project { get; private set; } = NewAfdxProject("New project");
    public bool Dirty { get; set; }
    public List<string> Recent { get; } = new();
    public static readonly string[] Rails = { "START", "PROJECT", "ANALYZE", "SIMULATE", "ICD", "INTEROP", "SETTINGS" };
    public AfdxService Afdx { get; }
    public override string BusModeName => "ANALYZE";

    public NetEngine()
    {
        Mode = "START"; Afdx = new AfdxService(this);
        Icd.SetHub(Project.Icd, "(project)");
        Icd.HubChanged += hub =>
        {
            if (!ReferenceEquals(hub, Project.Icd)) { Project.SetIcd(hub); Dirty = true; }
            EnsureAfdx();
            SetStatus(hub.IsAfdx ? $"ICD {hub.Source}: {hub.Vls!.Count} virtual links, {hub.Vls.Sum(v => v.Ports.Sum(p => p.Params.Count))} parameters" : $"ICD {hub.Source}: no virtual links (an ARINC 664 ICD has vl_id / bag_ms / lmax columns)");
            Touch();
        };
    }

    static BusProject NewAfdxProject(string name) { var p = new BusProject { Name = name, Protocol = "arinc664", Afdx = new AfdxProjectSettings() }; p.Icd.Schema = "1.1"; p.Icd.Vls ??= new(); return p; }
    /// <summary>Every project of this application is an ARINC 664 project (settings + schema 1.1 hub part).</summary>
    void EnsureAfdx() { var p = Project; p.Protocol = "arinc664"; p.Afdx ??= new AfdxProjectSettings(); p.Icd.Schema = "1.1"; p.Icd.Vls ??= new(); }

    // ------------------------------------------------------------------ project
    /// <summary>New project (the protocol argument is accepted for compatibility with the shared self-tests and ignored: always ARINC 664).</summary>
    public void NewProject(string? name, string protocol = "arinc664")
    {
        StopAll();
        Project = NewAfdxProject(string.IsNullOrWhiteSpace(name) ? "New project" : name!.Trim());
        Icd.SetHub(Project.Icd, "(project)"); Dirty = false; Mode = "PROJECT"; SetStatus($"Project '{Project.Name}' created — pick the network, load an ICD, then SIMULATE or ANALYZE"); Touch();
    }
    public void StopAll() => Afdx.Stop();
    public void OpenProject(string path)
    {
        var p = BusProject.Load(path);
        if (!p.IsAfdx) { SetStatus($"{Path.GetFileName(path)} is not an ARINC 664 project — open it in NexysBus"); SetLed("error"); Touch(); return; }
        StopAll();
        Project = p; EnsureAfdx(); Icd.SetHub(Project.Icd, path); AddRecent(path); Dirty = false; Mode = "PROJECT";
        SetStatus($"Project {Path.GetFileName(path)}: {Project.Icd.Vls?.Count ?? 0} virtual links, network {Project.Device}"); Touch();
    }
    public string SaveProject(string? path)
    {
        path ??= Project.Path; if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("no project path");
        Project.Save(path); AddRecent(path); Dirty = false; SetStatus("Saved " + path); Touch(); return path;
    }
    void AddRecent(string path) { Recent.Remove(path); Recent.Insert(0, path); while (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1); }

    /// <summary>Open whatever was dropped / given on the command line: project, capture, ICD.</summary>
    public void OpenAny(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext is ProjectExt or ".nxb") OpenProject(path);
            else if (ext is ".pcapng" or ".pcap") Afdx.OpenCapture(path);
            else if (ext is ".csv" or ".xlsx" or ".json") { Icd.LoadIcd(path); Mode = "ICD"; Touch(); }
            else SetStatus("Unsupported file: " + Path.GetFileName(path));
        }
        catch (Exception ex) { Diag.Trace("open failed " + ex); SetStatus("Open failed: " + ex.Message); SetLed("error"); }
    }

    public void SetProjectField(string field, JsonElement v)
    {
        var p = Project;
        switch (field)
        {
            case "name": p.Name = v.GetString() ?? p.Name; break;
            case "device": p.Device = v.GetString() ?? "virtual"; break;
            case "captureDir": p.CaptureDir = v.GetString() ?? ""; break;
        }
        Dirty = true; Touch();
    }

    /// <summary>Networks offered in PROJECT: the virtual network always, the shared virtual network of this PC, Ethernet ports when the
    /// packet-capture library is installed, the dedicated card slot.</summary>
    public List<object> Devices()
    {
        bool pcap = Pcap.Available;
        return new List<object>
        {
            new { id = "virtual", kind = "virtual", name = "Virtual network", detail = "A/B loopback in software · delay, loss, duplication and skew injection", available = true },
            new { id = "local", kind = "virtual", name = "Shared virtual network (this PC)", detail = "every NexysNet instance on this PC that picks it sees the frames of the others (loopback multicast, nothing leaves the PC) — e.g. one instance SIMULATEs, another ANALYZEs", available = true },
            new { id = "network", kind = "network", name = "Ethernet ports (A / B)", detail = pcap ? "packet-capture library found — pick the ports for network A and B below" : "needs the packet-capture library (Npcap, installed separately — not shipped with NexysNet)", available = pcap },
            new { id = "afdxcard", kind = "card", name = "AFDX end-system card", detail = "interface reserved for a vendor card adapter — not available in this release", available = false },
        };
    }

    // ------------------------------------------------------------------ bridge commands of this application
    public bool Dispatch(string cmd, JsonElement r, out object? result)
    {
        result = null;
        if (cmd.StartsWith("afdx.", StringComparison.Ordinal)) return Afdx.Dispatch(cmd, r, out result);
        switch (cmd)
        {
            case "rail": { string m = Bridge.S(r, "name", "START").ToUpperInvariant(); Mode = Rails.Contains(m) ? m : "START"; Touch(); return true; }
            case "project.new": NewProject(Bridge.S(r, "name")); return true;
            case "project.openPath": OpenProject(Bridge.S(r, "path")); return true;
            case "project.saveTo": result = SaveProject(Bridge.S(r, "path")); return true;
            case "project.set": SetProjectField(Bridge.S(r, "field"), r.GetProperty("value")); return true;
            case "project.useIcd": if (Icd.Hub != null) { Project.SetIcd(Icd.Hub); EnsureAfdx(); Dirty = true; Touch(); } return true;
            case "project.close": StopAll(); Project = NewAfdxProject("New project"); Icd.SetHub(Project.Icd, "(project)"); Dirty = false; Mode = "START"; Touch(); return true;
        }
        return false;
    }

    protected override void ExtendSnapshot(Dictionary<string, object?> s)
    {
        s["rail"] = Mode; s["recent"] = Recent.Where(File.Exists).Select(p => new { path = p, name = Path.GetFileNameWithoutExtension(p), dir = Path.GetDirectoryName(p) }).ToList();
        s["devices"] = Devices(); s["project"] = ProjectDto(); s["afdx"] = Afdx.Dto();
    }

    object ProjectDto()
    {
        var p = Project;
        return new { name = p.Name, path = p.Path, dirty = Dirty, device = p.Device, protocol = p.Protocol, icd = new { source = p.Icd.Source, vls = p.Icd.Vls?.Count ?? 0 }, captureDir = p.CaptureDir };
    }
}

/// <summary>settings.json under %LOCALAPPDATA%\NexysNet: the shared shell settings (theme, plot, recent projects, window, tour).</summary>
public static class NetStore
{
    public sealed class Dto : ShellSettings { }
    static bool _loading;
    public static void Save(MainWindow w)
    {
        if (_loading || SelfTest.Enabled || Program.Fresh) return;
        var d = new Dto(); ShellStore.Capture(d, w, w.Engine, w.Engine.Recent, w.Engine.TourSeen); ShellStore.Write(d);
    }
    public static void Load(MainWindow w)
    {
        if (Program.Fresh) return;
        var d = ShellStore.Read<Dto>(); if (d == null) return;
        _loading = true;
        try
        {
            var e = w.Engine; ShellStore.ApplyTheme(d, e);
            if (SelfTest.Enabled) return;
            e.Recent.Clear(); e.Recent.AddRange(d.Recent.Where(File.Exists)); e.TourSeen = d.TourSeen; ShellStore.ApplyWindow(d, w);
        }
        finally { _loading = false; }
    }
}
