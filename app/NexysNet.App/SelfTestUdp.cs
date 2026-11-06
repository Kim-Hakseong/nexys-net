using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NexysView.Core.Arinc664;

namespace NexysNet.App;

/// <summary>
/// --udp --out dir: the "UDP (Windows sockets)" device — no packet-capture library, no driver, no administrator rights (run it with
/// NEXYS_NO_PCAP=1 to prove it on a PC that has the library). A stand-in for a VI (plain .NET UDP sockets in this process) sends the
/// example ICD's datagrams (VL 20 → 224.224.0.20:30080, 64 B big endian: u16 counter, f32 altitude, i16 temperature × 0.1) while
/// ANALYZE › LISTEN runs on the UDP device: every datagram must arrive as a VL 20 frame, decode to the sent values, no SN findings.
/// Then 127.0.0.1:30080 (unicast to this PC) the same way, and the other direction: SIMULATE › START on the UDP device while the VI
/// stand-in's own socket (joined to the group) counts the datagrams NexysNet sends.
/// </summary>
public static partial class SelfTest
{
    const string UdpIcd = "vl_id,vl_name,bag_ms,lmax,network,sub_vl,port,port_type,ip_src,ip_dst,udp_src,udp_dst,size,param,offset,type,endian,scale,offset_val,unit\n" +
        "20,VI_DEMO,8,111,A,1,VI_DEMO_P1,sampling,192.168.0.10,224.224.0.20,30080,30080,64,COUNTER,0,u16,big,1,0,\n" +
        "20,VI_DEMO,8,111,A,1,VI_DEMO_P1,sampling,192.168.0.10,224.224.0.20,30080,30080,64,ALTITUDE,2,f32,big,1,0,m\n" +
        "20,VI_DEMO,8,111,A,1,VI_DEMO_P1,sampling,192.168.0.10,224.224.0.20,30080,30080,64,TEMPERATURE,6,i16,big,0.1,0,degC\n";

    static byte[] ViPayload(int n) { var b = new byte[64]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)n); BinaryPrimitives.WriteSingleBigEndian(b.AsSpan(2), 1000f + n); BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(6), (short)(200 + n % 50)); return b; }

    static async Task Udp(MainWindow w)
    {
        var e = w.Engine; string dir = Path.Combine(OutDir, "data"); Directory.CreateDirectory(dir);
        bool noPcap = Environment.GetEnvironmentVariable("NEXYS_NO_PCAP") == "1";
        GB("udp_no_packet_capture_library", !noPcap || !Pcap.Available, noPcap ? $"NEXYS_NO_PCAP=1: packet-capture library available = {Pcap.Available} ({Pcap.LastError})" : $"library present on this PC ({Pcap.Available}); run with NEXYS_NO_PCAP=1 for the no-library proof");
        // ---- project on the UDP device from the PROJECT page (the user path)
        await JS(w, "await nx.test.rail('START'); await nx.test.clickSel('.start [data-a=newProject]'); return true;"); await Idle(w, 300);
        await JS(w, "await nx.test.clickSel('[data-dev=\"udp\"]'); await new Promise(r => setTimeout(r, 800)); return true;"); await Idle(w, 300);
        int adapterOptions = (await JS(w, "return document.querySelectorAll('[data-nic=\"udpIfA\"] option').length;")).GetInt32();
        var devs = JsonSerializer.SerializeToElement(e.Devices()); bool udpAvail = devs.EnumerateArray().Any(d => d.GetProperty("id").GetString() == "udp" && d.GetProperty("available").GetBoolean());
        bool ethAvail = devs.EnumerateArray().Any(d => d.GetProperty("id").GetString() == "network" && d.GetProperty("available").GetBoolean());
        GB("udp_device_selectable", e.Project.Device == "udp" && udpAvail && adapterOptions >= 2, $"device {e.Project.Device}, UDP available {udpAvail}, Ethernet ports available {ethAvail}, NETWORK A choices {adapterOptions - 1} adapters + any");
        await Shot(w, "udp_project");
        string icd = Path.Combine(dir, "vi-example-icd.csv"); File.WriteAllText(icd, UdpIcd); e.Icd.LoadIcd(icd); await Idle(w, 300);

        // ---- 1) VI → NexysNet, multicast 224.224.0.20:30080
        var r1 = await ViToNet(w, IPAddress.Parse("224.224.0.20"), 400);
        GB("udp_vi_to_net_multicast", r1.ok, r1.detail);
        await Shot(w, "udp_vi_receive");
        // ---- 2) VI → NexysNet, unicast 127.0.0.1:30080
        var r2 = await ViToNet(w, IPAddress.Loopback, 200);
        GB("udp_vi_to_net_localhost", r2.ok, r2.detail);

        // ---- 3) NexysNet → VI: SIMULATE › START, the VI stand-in counts what arrives on its own socket
        using var vi = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        vi.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); vi.Bind(new IPEndPoint(IPAddress.Any, 30080)); vi.ReceiveTimeout = 200;
        foreach (var a in UdpNetworkDevice.Adapters()) { try { vi.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(IPAddress.Parse("224.224.0.20"), IPAddress.Parse(a.ip))); } catch (SocketException) { } }
        int viGot = 0, badLen = 0; var counters = new List<int>(); var stopVi = false;
        var viTask = Task.Run(() => { var b = new byte[2048]; while (!stopVi) { try { int n = vi.Receive(b); if (n != 64) badLen++; else { viGot++; counters.Add(BinaryPrimitives.ReadUInt16BigEndian(b)); } } catch (SocketException) { } } });
        await JS(w, "await nx.test.rail('SIMULATE'); await nx.test.afdx().sim.click('start'); return true;"); await Task.Delay(3000);
        await JS(w, "await nx.test.afdx().sim.click('stop'); return true;"); await Task.Delay(600); stopVi = true; await viTask;
        var run = JsonSerializer.SerializeToElement(e.Afdx.RunDto()); long sentA = run.GetProperty("sentA").GetInt64(); long sendErr = run.GetProperty("sendErrors").GetInt64();
        GB("udp_net_to_vi", sentA > 100 && viGot == sentA && badLen == 0 && sendErr == 0, $"SIMULATE 3 s on UDP: NexysNet sent {sentA:N0} datagrams (send errors {sendErr}), the VI stand-in's socket received {viGot:N0} of 64 B (other lengths {badLen})");
        await Shot(w, "udp_simulate");
        G("udp_memory_ws_mb", WsMb(), "MB");
    }

    /// <summary>The VI stand-in sends <paramref name="n"/> datagrams every 2 ms to <paramref name="dst"/>:30080 while ANALYZE › LISTEN runs.</summary>
    static async Task<(bool ok, string detail)> ViToNet(MainWindow w, IPAddress dst, int n)
    {
        var e = w.Engine;
        await JS(w, "await nx.test.rail('ANALYZE'); await nx.test.afdx().an.click('listen'); return true;"); await Task.Delay(600);
        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1); s.MulticastLoopback = true;
            for (int k = 0; k < n; k++) { s.SendTo(ViPayload(k), new IPEndPoint(dst, 30080)); await Task.Delay(2); }
        }
        await Task.Delay(800);
        await JS(w, "await nx.test.afdx().an.click('stopListen'); return true;"); await Task.Delay(800);
        var cap = e.Afdx.Capture!; var an = e.Afdx.Analysis!; var (vlFrames, valuesOk, distinct) = CountVi(cap); long errors = 0;
        if (an.Vls.TryGetValue(20, out var vs)) errors = vs.SnGaps.Sum() + vs.SnReversed.Sum();
        // decode through the UI (the user's view): first VL 20 row → the three parameters with their EU values
        await JS(w, "await nx.test.afdx().an.filter({ vl: 20 }); return true;"); await Idle(w, 300);
        string dec = (await JS(w, "return await nx.test.afdx().an.selectRow(0);")).GetString() ?? "";
        bool decOk = dec.Contains("COUNTER") && dec.Contains("ALTITUDE") && dec.Contains("TEMPERATURE") && dec.Contains("from UDP map");
        await JS(w, "await nx.test.afdx().an.filter({}); return true;");
        bool ok = vlFrames == n && valuesOk == n && distinct == n && errors == 0 && decOk;
        return (ok, $"{dst}:30080 — sent {n}, VL 20 frames {vlFrames}, values equal to the sent ones {valuesOk}, distinct counters {distinct}, SN findings {errors}, decode panel COUNTER/ALTITUDE/TEMPERATURE 'from UDP map' {decOk} (capture total {cap.Count})");
    }

    static (int vlFrames, int valuesOk, int distinct) CountVi(AfdxCapture cap)
    {
        int vlFrames = 0, valuesOk = 0; var seen = new HashSet<int>();
        for (int i = 0; i < cap.Count; i++)
        {
            if (cap.Vl(i) != 20) continue; vlFrames++;
            var p = cap.Frame(i).Slice(42, 64);
            int c = BinaryPrimitives.ReadUInt16BigEndian(p); float alt = BinaryPrimitives.ReadSingleBigEndian(p[2..]); short t = BinaryPrimitives.ReadInt16BigEndian(p[6..]);
            if (alt == 1000f + c && t == 200 + c % 50) valuesOk++; seen.Add(c);
        }
        return (vlFrames, valuesOk, seen.Count);
    }
}
