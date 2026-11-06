using System.IO;
using System.Text.Json;
using NexysNet.App.Services;

namespace NexysNet.App;

/// <summary>Command-line automation on the web UI (real DOM events through window.nx.test.*, awaited through the bridge):
/// --afdx (the ARINC 664 user path, truth corpus, real-time sender, 1M/10M frames), --interop (two instances), --interop-tx (the
/// second instance), --site (bench profile with an "afdx" section), --shot. The runner (log, gates, page size, screenshots) is the
/// shared <see cref="SelfTestKit"/> of nexys-core; this class keeps the scenarios.</summary>
public static partial class SelfTest
{
    public static string[] Args { get => SelfTestKit.Args; set => SelfTestKit.Args = value; }
    public static bool Enabled => Args.Length > 0 && Args[0] is "--afdx" or "--interop" or "--interop-tx" or "--site" or "--shot" or "--startmem" or "--udp";
    public static string OutDir { get => SelfTestKit.OutDir; set => SelfTestKit.OutDir = value; }
    static readonly Random Rng = new(7);

    public static void L(string s) => SelfTestKit.L(s);
    static void G(string k, double v, string unit, string? gate = null) => SelfTestKit.G(k, v, unit, gate);
    static void GB(string k, bool ok, string detail) => SelfTestKit.GB(k, ok, detail);
    static string Arg(string name, string dflt = "") => SelfTestKit.Arg(name, dflt);
    static bool Has(string a) => SelfTestKit.Has(a);
    static string J(string s) => SelfTestKit.J(s);
    static long WsMb() => SelfTestKit.WsMb();
    static Task Idle(MainWindow w, int ms = 50) => SelfTestKit.Idle(w, ms);
    static Task<JsonElement> JS(MainWindow w, string body, int timeoutMs = 60000) => w.Bridge!.RunTest(body, timeoutMs);
    static Task Shot(MainWindow w, string name) => SelfTestKit.Shot(w.Bridge!, w.Web, name);
    static string RepoRoot() => SelfTestKit.RepoRoot();

    public static Task RunAsync(MainWindow w) => SelfTestKit.RunAsync(w, w.Web, async () =>
    {
        if (Args[0] == "--afdx") await Afdx(w);
        else if (Args[0] == "--interop") await Interop(w);
        else if (Args[0] == "--interop-tx") await InteropTx(w);
        else if (Args[0] == "--site") await Site(w);
        else if (Args[0] == "--startmem") await StartMem(w);
        else if (Args[0] == "--udp") await Udp(w);
        else if (Args[0] == "--shot")
        {
            await JS(w, $"await nx.test.rail({J(Arg("--rail", "START"))}); return true;");
            foreach (var sel in Arg("--click").Split(';', StringSplitOptions.RemoveEmptyEntries)) { await JS(w, $"await nx.test.clickSel({J(sel)}); return true;"); await Idle(w, int.TryParse(Arg("--wait", "400"), out int ms) ? ms : 400); }
            string js = Arg("--js"); if (js.StartsWith("@")) js = File.ReadAllText(js[1..]);
            if (js.Length > 0) { var r = await JS(w, js, 600000); L("js result: " + r.ToString()); await Idle(w, int.TryParse(Arg("--wait", "400"), out int ms2) ? ms2 : 400); }
            await Shot(w, Arg("--name", "shot"));
        }
    });

    /// <summary>--startmem: working set after start (START rail) and after a new project, the shell's rails — the size record of the split.</summary>
    static async Task StartMem(MainWindow w)
    {
        G("memory_ws_start_mb", WsMb(), "MB");
        G("startup_process_to_ui_ms", (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds - 600, "ms", "<=3000");
        int rails = (await JS(w, "await nx.test.rail('START'); return document.querySelectorAll('.rail [data-rail]').length;")).GetInt32();
        string names = (await JS(w, "return Array.from(document.querySelectorAll('.rail [data-rail]')).map(b => b.dataset.rail).join(' ');")).GetString() ?? "";
        GB("net_rails", rails == 6 && names == "PROJECT ANALYZE SIMULATE ICD INTEROP SETTINGS", $"rails {rails}: {names}");
        await Shot(w, "net_start");
        await JS(w, "await nx.test.clickSel('.start [data-a=newProject]'); return true;"); await Idle(w, 300);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(w, 300);
        G("memory_ws_project_mb", WsMb(), "MB");
        await Shot(w, "net_project");
    }

    /// <summary>--site --profile file.json --out dir: the ARINC 664 bench of the profile's "afdx" section (N0..N3, see SelfTestSiteAfdx.cs).</summary>
    static async Task Site(MainWindow w)
    {
        string profArg = Arg("--profile");
        if (profArg.Length == 0) { GB("site_profile", false, "--profile <file.json> with an \"afdx\" section is required"); return; }
        var prof = SiteProfile.Load(profArg); L($"profile {prof.Name} ({prof.Path}): {prof.Description}"); if (prof.Notes.Length > 0) L("profile notes: " + prof.Notes);
        if (prof.Afdx == null) { GB("site_profile", false, $"{prof.Path}: no \"afdx\" section"); return; }
        await SiteAfdx(w, prof);
    }
}
