# Site profile (`--site`) — NexysNet

`NexysNet.exe --site --profile <file.json> --out <dir>` runs the ARINC 664 bench of the profile's `afdx` section: N0 packet-capture library and ports → N1 receive only → N2 transmit (only when `txVls` lists VLs) → N3 long receive run (`n3Minutes` > 0). Everything that belongs to a bench lives in the JSON file, never in the code. Example: `site-profiles/example-afdx.json`.

| Field | Type | Meaning |
|---|---|---|
| `name`, `description`, `notes` | string | Carried into the log / report. |
| `afdx.icd` | path | ARINC 664 ICD (CSV / XLSX: one row per parameter, columns `vl_id`, `bag_ms`, `lmax`, `network`, `port`, `udp_dst`, `size`, `param`, `offset`, `type`, `endian`, `scale`, `offset_val`, `unit`; or a hub JSON with `vls`), relative to the profile. |
| `afdx.nicA`, `afdx.nicB` | string | Port of network A / B: the packet-capture device name, or a part of its description as the port list in the log shows it. The same port twice = one port for both networks (frames assigned by the source-MAC interface id). |
| `afdx.listenSeconds` | number | N1 receive-only capture (default 30). Nothing is transmitted in N1. |
| `afdx.txVls` | int list | N2: VL ids NexysNet transmits for `sendSeconds` (empty = N2 skipped, nothing is ever sent). |
| `afdx.expectVls` | int list | VLs the peer must send (N1 gate); empty = every ICD VL not in `txVls`. |
| `afdx.peerName` | string | Written into the report. |
| `afdx.jmaxMs`, `afdx.skewMaxMs` | number | Check settings (defaults 0.5 and 10). |
| `afdx.expectPeer` | bool | `false` = a bench without a peer (one port looped back to itself): N1 passes on any received traffic, N2 also checks that every own frame comes back on the capture (`afdx_site_loopback`). Default `true`. |
| `afdx.n3Minutes` | number | N3 long receive run in minutes (0 = skipped). |
| `afdx.n3Send` | bool | N3 also transmits `txVls` (own traffic to check for losses when no peer sends). |

Steps and gates: **N0** packet-capture library present (`afdx_site_library`; without it the steps are skipped — the library is installed separately and never shipped), ports and roles (`afdx_site_ports`), ICD loaded (`afdx_site_icd`). **N1** capture for `listenSeconds` (`afdx_site_listen`: frames received, every expected VL seen; the interoperability checklist is written to the log). **N2** transmit `txVls` while capturing (`afdx_site_send`: frames handed to the port, accepted, send errors 0 — a driver that refuses raw frames, e.g. a Wi-Fi adapter, shows its message here; `afdx_site_loopback` with `expectPeer: false`: own frames seen back = handed to the port, no SN gap; `afdx_site_send_bag`: |interval − BAG| p50 / p99 / max on the capture's timestamps and intervals < BAG − Jmax = 0). **N3** (`n3Minutes` > 0) long receive (`afdx_site_long`: frames in every minute, SN gaps + reversed 0, own frames sent = seen when `n3Send`, working set per minute). Files: `afdx_listen.pcapng`, `afdx_send.pcapng`, `afdx_long.pcapng`, `interop-report.md` (checklist of the N1 capture + the NexysNet → peer table), `afdx_summary.json`.
