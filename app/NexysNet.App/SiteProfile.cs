using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexysNet.App;

/// <summary>
/// Site profile for the <c>--site</c> self-test: everything that belongs to one ARINC 664 bench (ports of network A / B, the ICD, the
/// receive and transmit durations, whether a peer is on the network) lives in a JSON file next to the measurement results, never in
/// the code. Schema: docs/site-profile.md; example: site-profiles/example-afdx.json.
/// </summary>
public sealed class SiteProfile
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>Free notes (wiring, who owns the peer) — carried into the report only.</summary>
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";
    /// <summary>ARINC 664 bench: ports, ICD, receive / transmit durations — see docs/site-profile.md "afdx".</summary>
    [JsonPropertyName("afdx")] public SiteAfdx? Afdx { get; set; }

    public sealed class SiteAfdx
    {
        /// <summary>ARINC 664 ICD (CSV / XLSX / hub JSON), relative to the profile file.</summary>
        [JsonPropertyName("icd")] public string Icd { get; set; } = "";
        /// <summary>Packet-capture port of network A / B (device name or description as the port list shows it).</summary>
        [JsonPropertyName("nicA")] public string NicA { get; set; } = "";
        [JsonPropertyName("nicB")] public string NicB { get; set; } = "";
        /// <summary>Receive-only capture (N1), seconds.</summary>
        [JsonPropertyName("listenSeconds")] public double ListenSeconds { get; set; } = 30;
        /// <summary>N2: also transmit these VL ids (empty = no transmission) for sendSeconds.</summary>
        [JsonPropertyName("txVls")] public List<int> TxVls { get; set; } = new();
        [JsonPropertyName("sendSeconds")] public double SendSeconds { get; set; } = 30;
        /// <summary>VL ids the peer is expected to send (N1 gate); empty = every ICD VL not in txVls.</summary>
        [JsonPropertyName("expectVls")] public List<int> ExpectVls { get; set; } = new();
        [JsonPropertyName("peerName")] public string PeerName { get; set; } = "";
        [JsonPropertyName("jmaxMs")] public double JmaxMs { get; set; } = 0.5;
        [JsonPropertyName("skewMaxMs")] public double SkewMaxMs { get; set; } = 10;
        /// <summary>false = no peer on the network (a loopback bench): N1 passes on any received traffic, N2 expects every own frame back.</summary>
        [JsonPropertyName("expectPeer")] public bool ExpectPeer { get; set; } = true;
        /// <summary>N3: long receive run (minutes, 0 = skip) — frames per minute, sequence-number losses, memory growth.</summary>
        [JsonPropertyName("n3Minutes")] public double N3Minutes { get; set; }
        /// <summary>N3: also send txVls during the long run (own traffic to check for losses when no peer sends).</summary>
        [JsonPropertyName("n3Send")] public bool N3Send { get; set; }
    }
    [JsonIgnore] public string Path { get; set; } = "";

    public static SiteProfile Load(string path)
    {
        var p = JsonSerializer.Deserialize<SiteProfile>(File.ReadAllText(path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? throw new InvalidDataException("empty profile");
        p.Path = System.IO.Path.GetFullPath(path);
        if (p.Afdx != null && p.Afdx.Icd.Length > 0 && !System.IO.Path.IsPathRooted(p.Afdx.Icd)) p.Afdx.Icd = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(p.Path) ?? "", p.Afdx.Icd);
        return p;
    }
}
