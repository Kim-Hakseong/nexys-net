using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using NexysNet.App.Services;

namespace NexysNet.App;

/// <summary>One WPF window hosting the WebView2 page (net/web → wwwroot). Start-up, screenshots and settings go through the shared shell
/// (<see cref="AppShell"/>, nexys-core); dialogs stay native; network commands are dispatched by <see cref="NetEngine"/>.</summary>
public partial class MainWindow : Window
{
    public NetEngine Engine { get; } = new();
    public Bridge? Bridge { get; private set; }
    readonly Branding _brand = Branding.Load();
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WhenReady => _ready.Task;
    public string RuntimeVersion { get; private set; } = "";
    public string DisplayName => _brand.displayName;

    public MainWindow()
    {
        InitializeComponent();
        AppShell.Decorate(this, _brand.displayName);
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => { Engine.StopAll(); NetStore.Save(this); };
        Engine.Changed += () => { if (Bridge?.PageReady == true) Dispatcher.BeginInvoke(() => NetStore.Save(this), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };
    }

    async Task InitAsync()
    {
        try
        {
            (Bridge, RuntimeVersion) = await AppShell.StartAsync(Web, Boot, Engine, Dispatcher, HostCommand, Engine.Dispatch, SelfTest.Enabled, Program.Fresh, "NexysNet.exe", "net/web",
                b => { NetStore.Load(this); b.PushSnapshot(); _ready.TrySetResult(); });
        }
        catch (Exception ex) { Diag.Trace("webview init failed " + ex); Boot.Text = AppShell.StartFailedText(ex, "NexysNet.exe"); _ready.TrySetException(ex); }
    }

    /// <summary>Host-only commands (WPF dialogs, files); the shared ones live in <see cref="HostCommands"/>.</summary>
    public async Task<object?> HostCommand(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            case "openDialog":
                {
                    var dlg = new OpenFileDialog { Filter = "Network files|*.nxn;*.nxb;*.pcapng;*.pcap;*.csv;*.xlsx;*.json|NexysNet project|*.nxn;*.nxb|Packet capture|*.pcapng;*.pcap|ICD|*.csv;*.xlsx;*.json|All|*.*", Multiselect = true };
                    if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) Engine.OpenAny(f);
                    return null;
                }
            case "project.open":
                {
                    var dlg = new OpenFileDialog { Title = "Open project", Filter = "NexysNet project|*.nxn;*.nxb|All|*.*" };
                    if (dlg.ShowDialog(this) != true) return null; Engine.OpenProject(dlg.FileName); return dlg.FileName;
                }
            case "project.save":
                {
                    if (Engine.Project.Path.Length > 0 && !Bridge.B(args, "as")) return Engine.SaveProject(null);
                    var dlg = new SaveFileDialog { Title = "Save project", Filter = "NexysNet project|*" + NetEngine.ProjectExt, FileName = Engine.Project.Name + NetEngine.ProjectExt };
                    if (dlg.ShowDialog(this) != true) return null; return Engine.SaveProject(dlg.FileName);
                }
            case "project.pickCaptureDir":
                {
                    var dlg = new OpenFolderDialog { Title = "Capture folder" };
                    if (dlg.ShowDialog(this) != true) return null; Engine.Project.CaptureDir = dlg.FolderName; Engine.Dirty = true; Engine.Touch(); return Engine.Project.CaptureDir;
                }
            case "trend.sendToView":   // the trend channel as CSV (time, value) → Nexys VIEW when installed, else the file stays for a manual open
                {
                    string id = Bridge.S(args, "channel"); var ch = Engine.Find(id); if (ch == null) { Engine.SetStatus("No trend channel"); return null; }
                    string dir = Path.Combine(AppPaths.LocalDir, "trend"); Directory.CreateDirectory(dir);
                    string safe = new string(ch.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()); string path = Path.Combine(dir, $"{safe}_{DateTime.Now:HHmmss}.csv");
                    using (var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true)))
                    {
                        w.WriteLine($"time_s,{safe}" + (ch.Unit.Length > 0 ? $" [{ch.Unit}]" : "")); var buf = new double[4096];
                        for (long i = 0; i < ch.Length; i += buf.Length) { int got = ch.Source.Read(i, buf.AsSpan(0, (int)Math.Min(buf.Length, ch.Length - i))); for (int k = 0; k < got; k++) w.WriteLine($"{ch.XAt(i + k).ToString("R", System.Globalization.CultureInfo.InvariantCulture)},{buf[k].ToString("R", System.Globalization.CultureInfo.InvariantCulture)}"); }
                    }
                    var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "NexysView.exe"), Path.Combine(AppContext.BaseDirectory, "..", "Nexys VIEW", "NexysView.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Nexys VIEW", "NexysView.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Nexys VIEW", "NexysView.exe") };
                    var exe = candidates.FirstOrDefault(File.Exists);
                    if (exe != null && !SelfTest.Enabled) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = true }); Engine.SetStatus($"Sent {ch.Name} to Nexys VIEW ({ch.Length:N0} samples)"); }
                    else Engine.SetStatus((exe == null ? "Nexys VIEW is not installed — " : "") + $"trend written to {path}");
                    return path;
                }
            case "afdx.open":
                {
                    var dlg = new OpenFileDialog { Title = "Open packet capture", Filter = "Packet capture|*.pcapng;*.pcap|All|*.*" };
                    if (dlg.ShowDialog(this) != true) return null; Engine.OpenAny(dlg.FileName); return dlg.FileName;
                }
            case "afdx.interop.export":
                {
                    var dlg = new SaveFileDialog { Title = "Interoperability report", Filter = "Markdown|*.md", FileName = "interop-report.md" };
                    if (dlg.ShowDialog(this) != true) return null; return Engine.Afdx.ExportReport(dlg.FileName);
                }
            case "afdx.peer.importCsv":
                {
                    var dlg = new OpenFileDialog { Title = "Peer report (CSV: vl, rx_a, rx_b, errors, note)", Filter = "CSV|*.csv;*.txt|All|*.*" };
                    if (dlg.ShowDialog(this) != true) return null; return Engine.Afdx.ImportPeerCsv(dlg.FileName);
                }
            case "afdx.wireshark.export":
                {
                    var dlg = new OpenFolderDialog { Title = "Folder for the Wireshark profile (plugins + profiles)" };
                    if (dlg.ShowDialog(this) != true) return null; var dir = Path.Combine(dlg.FolderName, "NexysNet-Wireshark");
                    WiresharkProfile.Write(dir, Engine.Project.Icd, AppInfo.Name + " " + AppInfo.VersionText); Engine.SetStatus("Wireshark profile → " + dir); return dir;
                }
            case "afdx.icd.exportCsv":
                {
                    var dlg = new SaveFileDialog { Title = "ARINC 664 ICD (CSV)", Filter = "CSV|*.csv", FileName = Engine.Project.Name.Replace(' ', '_') + "_afdx_icd.csv" };
                    if (dlg.ShowDialog(this) != true) return null; File.WriteAllText(dlg.FileName, NexysView.Core.Arinc664.AfdxIcd.ToCsv(Engine.Project.Icd)); Engine.SetStatus("ICD → " + dlg.FileName); return dlg.FileName;
                }
            case "afdx.icd.import":
                {
                    var dlg = new OpenFileDialog { Title = "ARINC 664 ICD (CSV / XLSX / hub JSON)", Filter = "ICD|*.csv;*.xlsx;*.json|All|*.*" };
                    if (dlg.ShowDialog(this) != true) return null; Engine.Icd.LoadIcd(dlg.FileName); return dlg.FileName;
                }
            case "capture": { string path = args.GetProperty("path").GetString()!; await CaptureAsync(path); return path; }
            case "quit": Close(); return null;
        }
        var (handled, result) = await HostCommands.TryAsync(this, Engine, cmd, args);
        if (handled) return result;
        throw new NotSupportedException("unknown command " + cmd);
    }

    public Task CaptureAsync(string path) => AppShell.CaptureAsync(Web, path);
}
