// ARINC 664 (AFDX) pages of NexysNet, used when the project's protocol is arinc664:
// ICD (virtual links · ports · parameters, validation, import/export), SIMULATE (VL table, sender run, BAG accuracy, error injection,
// virtual-network faults), ANALYZE (VL tree, virtualized frame list with filters, decode, per-VL check table, trend on the shared chart),
// INTEROP (peer → NexysNet automatic checklist, NexysNet → peer table, report export).
import { bridge } from '@core/bridge';
import { PlotView } from '@core/plot';
import { esc, fmtInt, h } from '@core/util';

const BAGS = [1, 2, 4, 8, 16, 32, 64, 128];
const TYPES = ['u8', 'i8', 'u16', 'i16', 'u32', 'i32', 'u64', 'i64', 'f32', 'f64'];
const KINDS = ['eu', 'sine', 'ramp', 'const'];
const kbps = (bps: number) => bps >= 1e6 ? `${(bps / 1e6).toFixed(2)} Mbit/s` : `${(bps / 1e3).toFixed(1)} kbit/s`;
const num = (v: any, d = 3) => v == null || !isFinite(v) ? '—' : Number(v).toLocaleString('en-US', { maximumFractionDigits: d });
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

// ============================================================================ ICD
export class AfdxIcdPage {
  el = h(`<div class="afdx-icd"><section class="card vls"><div class="card-h"><span class="t">VIRTUAL LINKS</span><span class="m" data-m></span><div class="acts"><button class="pill h24" data-a="import">IMPORT…</button><button class="pill h24" data-a="export">EXPORT CSV…</button><button class="pill h24" data-a="dummy" title="Built-in sample ICD (6 VLs)">SAMPLE</button><button class="pill h24" data-a="addVl">+ VL</button></div></div><div class="scroll"><table class="frame-tbl"><thead><tr><th>VL ID</th><th>NAME</th><th>BAG ms</th><th>LMAX</th><th>NET</th><th>PORTS</th><th>PARAMS</th><th>BANDWIDTH</th><th></th></tr></thead><tbody></tbody></table></div></section>
    <section class="card ports"><div class="card-h"><span class="t">PORTS · PARAMETERS</span><span class="m" data-pm></span></div><div class="scroll" data-ports></div></section>
    <section class="card val"><div class="card-h"><span class="t">VALIDATION</span><span class="m" data-vm>hub schema 1.1</span></div><div class="scroll" data-val></div></section></div>`);
  private key = ''; private icd: any = null; sel = -1;
  constructor() {
    this.el.addEventListener('click', e => {
      const t = e.target as HTMLElement; const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; const vlRow = t.closest('tr[data-vl]') as HTMLElement | null;
      if (a === 'import') bridge.call('afdx.icd.import'); else if (a === 'export') bridge.call('afdx.icd.exportCsv'); else if (a === 'dummy') bridge.call('afdx.icd.dummy');
      else if (a === 'addVl') bridge.call<number>('afdx.vl.add').then(id => { this.sel = id; });
      else if (a === 'rmVl' && vlRow) bridge.call('afdx.vl.remove', { vl: +vlRow.dataset.vl! });
      else if (a === 'addParam') { const p = t.closest('[data-port]') as HTMLElement; bridge.call('afdx.param.add', { vl: this.sel, port: p.dataset.port }); }
      else if (a === 'rmParam') { const p = t.closest('[data-port]') as HTMLElement; const r = t.closest('tr[data-idx]') as HTMLElement; bridge.call('afdx.param.remove', { vl: this.sel, port: p.dataset.port, idx: +r.dataset.idx! }); }
      else if (vlRow && !(t as HTMLInputElement).matches('input,select')) { this.sel = +vlRow.dataset.vl!; this.render(); }
    });
    this.el.addEventListener('change', e => {
      const t = e.target as HTMLInputElement; const f = t.dataset.f; if (!f) return;
      const vlRow = t.closest('tr[data-vl]') as HTMLElement | null; const port = (t.closest('[data-port]') as HTMLElement | null)?.dataset.port; const idx = (t.closest('tr[data-idx]') as HTMLElement | null)?.dataset.idx;
      const numeric = ['vl_id', 'bag_ms', 'lmax', 'size', 'sub_vl', 'udp_dst', 'udp_src', 'offset', 'scale', 'offset_val'].includes(f); const value = numeric ? parseFloat(t.value) || 0 : t.value;
      if (vlRow && !port) { if (f === 'vl_id') this.sel = +t.value; bridge.call('afdx.vl.set', { vl: +vlRow.dataset.vl!, field: f, value }); }
      else if (port) bridge.call('afdx.param.set', { vl: this.sel, port, idx: idx == null ? -1 : +idx, field: f, value });
    });
  }
  async setState(S: any) { const k = JSON.stringify([S.afdx?.icd, S.afdx?.vlList]); if (k === this.key) return; this.key = k; this.icd = await bridge.call('afdx.icd'); if (this.sel < 0 || !this.icd.vls.some((v: any) => v.vl_id === this.sel)) this.sel = this.icd.vls[0]?.vl_id ?? -1; this.render(); }
  render() {
    const d = this.icd; if (!d) return; const vls: any[] = d.vls ?? [];
    (this.el.querySelector('[data-m]') as HTMLElement).textContent = `${esc(d.source || 'no ICD')} · ${vls.length} VLs`;
    this.el.querySelector('.vls tbody')!.innerHTML = vls.map(v => `<tr data-vl="${v.vl_id}" class="${v.vl_id === this.sel ? 'sel' : ''}"><td><input class="cell" data-f="vl_id" value="${v.vl_id}"></td><td><input class="cell w" data-f="name" value="${esc(v.name)}"></td><td><select class="cell" data-f="bag_ms">${BAGS.map(b => `<option value="${b}"${b === v.bag_ms ? ' selected' : ''}>${b}</option>`).join('')}${BAGS.includes(v.bag_ms) ? '' : `<option selected>${v.bag_ms}</option>`}</select></td><td><input class="cell" data-f="lmax" value="${v.lmax}"></td><td><select class="cell" data-f="network">${['AB', 'A', 'B'].map(n => `<option${n === v.network ? ' selected' : ''}>${n}</option>`).join('')}</select></td><td>${v.ports.length}</td><td>${v.ports.reduce((a: number, p: any) => a + p.params.length, 0)}</td><td class="dim">${kbps((v.lmax + 20) * 8 / (v.bag_ms / 1000))}</td><td><button class="pill h24" data-a="rmVl" title="Remove VL">✕</button></td></tr>`).join('') || `<tr><td colspan="9" class="muted11">No virtual links — IMPORT an ICD (CSV / XLSX: one row per parameter, columns vl_id · bag_ms · lmax · network · port · udp_dst · size · param · offset · type …), SAMPLE, or + VL.</td></tr>`;
    const v = vls.find(x => x.vl_id === this.sel);
    (this.el.querySelector('[data-pm]') as HTMLElement).textContent = v ? `VL ${v.vl_id} · ${v.ports.length} ports` : '';
    (this.el.querySelector('[data-ports]') as HTMLElement).innerHTML = v ? v.ports.map((p: any) => `<div class="afdx-port" data-port="${esc(p.name)}"><div class="row2"><input class="in" data-f="name" value="${esc(p.name)}" title="Port name"><span class="k">SUB-VL</span><input class="in w40" data-f="sub_vl" value="${p.sub_vl}"><select class="sel h24" data-f="port_type">${['sampling', 'queuing'].map(x => `<option${x === p.port_type ? ' selected' : ''}>${x}</option>`).join('')}</select><span class="k">DST</span><input class="in w110" data-f="ip_dst" value="${esc(p.ip_dst)}"><span>:</span><input class="in w60" data-f="udp_dst" value="${p.udp_dst}"><span class="k">SRC</span><input class="in w60" data-f="udp_src" value="${p.udp_src}"><span class="k">SIZE</span><input class="in w60" data-f="size" value="${p.size}"><span class="muted11">B</span></div>
      <table class="rt-tbl"><thead><tr><th>PARAMETER</th><th>OFFSET</th><th>TYPE</th><th>ENDIAN</th><th>SCALE</th><th>OFFSET VAL</th><th>UNIT</th><th></th></tr></thead><tbody>${p.params.map((q: any, i: number) => `<tr data-idx="${i}"><td><input class="cell w" data-f="name" value="${esc(q.name)}"></td><td><input class="cell" data-f="offset" value="${q.offset}"></td><td><select class="cell" data-f="type">${TYPES.map(x => `<option${x === q.type ? ' selected' : ''}>${x}</option>`).join('')}</select></td><td><select class="cell" data-f="endian">${['big', 'little'].map(x => `<option${x === q.endian ? ' selected' : ''}>${x}</option>`).join('')}</select></td><td><input class="cell" data-f="scale" value="${q.scale}"></td><td><input class="cell" data-f="offset_val" value="${q.offset_val}"></td><td><input class="cell" data-f="unit" value="${esc(q.unit)}"></td><td><button class="pill h24" data-a="rmParam">✕</button></td></tr>`).join('')}</tbody></table><div class="row2"><button class="pill h24" data-a="addParam">+ PARAMETER</button></div></div>`).join('') : '<div class="muted11 pad">Pick a VL.</div>';
    const val: string[] = d.validation ?? [];
    (this.el.querySelector('[data-vm]') as HTMLElement).textContent = `hub schema 1.1 · ${val.filter(x => x.startsWith('error')).length} errors · ${val.filter(x => x.startsWith('warning')).length} warnings`;
    (this.el.querySelector('[data-val]') as HTMLElement).innerHTML = val.length ? val.map(x => `<div class="vline${x.startsWith('error') ? ' errv' : ''}">${esc(x)}</div>`).join('') : '<div class="muted11 pad">No findings: VL ids unique, BAG 2^k ms, Lmax 64–1518, payloads inside Lmax, parameters inside their payload without overlap.</div>';
  }
  testApi() {
    const self = this; const fire = async (sel: string, v: string) => { const el = self.el.querySelector(sel) as HTMLInputElement; if (!el) throw new Error('no ' + sel); el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); await sleep(200); return true; };
    return { rows: () => self.el.querySelectorAll('.vls tr[data-vl]').length, select: async (vl: number) => { (self.el.querySelector(`.vls tr[data-vl="${vl}"] td:nth-child(6)`) as HTMLElement).click(); await sleep(100); return self.sel; },
      setVl: (vl: number, f: string, v: string) => fire(`.vls tr[data-vl="${vl}"] [data-f="${f}"]`, v), setParam: (port: string, idx: number, f: string, v: string) => fire(`[data-port="${port}"] tr[data-idx="${idx}"] [data-f="${f}"]`, v),
      validation: () => Array.from(self.el.querySelectorAll('[data-val] .vline')).map(x => x.textContent), ports: () => self.el.querySelectorAll('.afdx-port').length };
  }
}

// ============================================================================ SIMULATE
export class AfdxSimPage {
  el = h(`<div class="afdx-sim"><section class="card vltab"><div class="card-h"><span class="t">VIRTUAL LINKS · TRANSMIT</span><span class="m" data-m></span></div><div class="scroll"><table class="frame-tbl"><thead><tr><th>ON</th><th>VL</th><th>NAME</th><th>BAG ms</th><th>NET</th><th>LMAX</th><th>PORTS</th><th>EXPECTED</th><th>SOURCE</th><th>PERIOD s</th></tr></thead><tbody></tbody></table></div></section>
    <section class="card run"><div class="card-h"><span class="t">RUN</span><span class="m" data-rm></span></div><div class="ctl"></div><div class="stat4"></div><div class="errs" data-errs></div><div class="errs" data-faults></div><div class="scroll"><table class="rt-tbl"><thead><tr><th>VL</th><th>SENT A</th><th>SENT B</th><th>BAG</th><th>MEAN ms</th><th>MIN ms</th><th>MAX ms</th><th>JITTER σ µs</th><th>MAX |Δ| µs</th></tr></thead><tbody data-tx></tbody></table></div><div class="cap"></div></section></div>`);
  private key = ''; private runKey = ''; private S: any = null;
  constructor() {
    this.el.addEventListener('click', e => {
      const a = ((e.target as HTMLElement).closest('[data-a]') as HTMLElement | null)?.dataset.a;
      if (a === 'start') bridge.call('afdx.start'); else if (a === 'stop') bridge.call('afdx.stop'); else if (a === 'batch') bridge.call('afdx.runFor', { seconds: 10 }); else if (a === 'analyze') bridge.call('rail', { name: 'ANALYZE' });
    });
    this.el.addEventListener('change', e => {
      const t = e.target as HTMLInputElement; const f = t.dataset.f; if (!f) return; const vl = (t.closest('tr[data-vl]') as HTMLElement | null)?.dataset.vl;
      if (t.dataset.set) { bridge.call('afdx.set', { field: f, value: parseFloat(t.value) || 0 }); return; }
      if (vl == null) return;
      bridge.call('afdx.vl.set', { vl: +vl, field: f, value: f === 'enabled' ? t.checked : f === 'source.kind' ? t.value : parseFloat(t.value) || 0 });
    });
    setInterval(() => { if (this.el.isConnected && this.S?.afdx?.run?.running) bridge.call('afdx.status').then(r => { if (r) this.renderRun(r); }); }, 250);
  }
  setState(S: any) {
    this.S = S; const a = S.afdx ?? {}; const key = JSON.stringify([a.vlList, a.settings]);
    if (key !== this.key) { this.key = key; this.renderTable(a); }
    this.renderRun(a.run ?? {});
  }
  private renderTable(a: any) {
    const vls: any[] = a.vlList ?? []; const on = vls.filter(v => v.enabled);
    (this.el.querySelector('[data-m]') as HTMLElement).textContent = `${on.length} of ${vls.length} VLs · expected ${kbps(on.reduce((s, v) => s + v.expectedBps * (v.network === 'AB' ? 2 : 1), 0))} on the wire (Lmax + 20 B every BAG, A and B)`;
    this.el.querySelector('.vltab tbody')!.innerHTML = vls.map(v => `<tr data-vl="${v.vl}" class="${v.enabled ? '' : 'off'}"><td><input type="checkbox" data-f="enabled"${v.enabled ? ' checked' : ''}></td><td>${v.vl}</td><td>${esc(v.name)}</td><td>${v.bagMs}</td><td>${v.network}</td><td>${v.lmax}</td><td>${v.ports}${v.subVls > 1 ? ` <span class="dim">· ${v.subVls} sub-VL</span>` : ''}</td><td class="dim">${kbps(v.expectedBps)}</td><td><select class="cell" data-f="source.kind">${KINDS.map(k => `<option${v.source.kind === k ? ' selected' : ''}>${k}</option>`).join('')}</select></td><td><input class="cell" data-f="source.periodS" value="${v.source.periodS}"></td></tr>`).join('') || `<tr><td colspan="10" class="muted11">No virtual links — load an ARINC 664 ICD in the ICD rail.</td></tr>`;
    const er = a.settings?.errors ?? {}, nf = a.settings?.faults ?? {};
    const inp = (f: string, v: any, label: string) => `<span>${label}</span><input class="in" data-set="1" data-f="${f}" value="${v ?? 0}">`;
    (this.el.querySelector('[data-errs]') as HTMLElement).innerHTML = `<span class="k">SENDER FAULTS · every N frames of a VL</span>${inp('err.snSkip', er.snSkipEvery, 'SN skip')}${inp('err.dropA', er.dropAEvery, 'drop A')}${inp('err.dropB', er.dropBEvery, 'drop B')}${inp('err.oversize', er.oversizeEvery, 'over Lmax')}${inp('err.early', er.earlyEvery, 'early (BAG)')}${inp('err.fragDrop', er.fragDropEvery, 'fragment drop (msgs)')}<span class="k">0 = off</span>`;
    (this.el.querySelector('[data-faults]') as HTMLElement).innerHTML = `<span class="k">VIRTUAL NETWORK</span>${inp('net.delayUs', nf.delayUs, 'delay µs')}${inp('net.jitterUs', nf.jitterUs, 'jitter µs')}${inp('net.skewBUs', nf.skewBUs, 'B skew µs')}${inp('net.lossEvery', nf.lossEvery, 'loss every')}${inp('net.dupEvery', nf.dupEvery, 'dup every')}`;
  }
  private renderRun(r: any) {
    const running = !!r.running; const ctl = this.el.querySelector('.ctl')!; const key = JSON.stringify([running, r.device, r.lastCapture]);
    if (key !== this.runKey) { this.runKey = key; (this.el.querySelector('[data-rm]') as HTMLElement).textContent = r.device ?? ''; ctl.innerHTML = `<button class="pill h32 primary" data-a="start"${running ? ' disabled' : ''}>▶ START</button><button class="pill h32" data-a="stop"${running ? '' : ' disabled'}>■ STOP</button><button class="pill h24" data-a="batch" title="10 s of traffic at once on the virtual network's clock"${running ? ' disabled' : ''}>10 s BATCH</button><button class="pill h24" data-a="analyze">ANALYZE →</button><span class="elapsed" data-el>00:00.0</span>`; }
    const t = r.elapsedS ?? 0; (ctl.querySelector('[data-el]') as HTMLElement).textContent = `${String(Math.floor(t / 60)).padStart(2, '0')}:${(t % 60).toFixed(1).padStart(4, '0')}`;
    const stat = (k: string, v: string) => `<div><div class="k">${k}</div><div class="v">${v}</div></div>`;
    this.el.querySelector('.stat4')!.innerHTML = stat('SENT', `<b>${fmtInt(r.sent ?? 0)}</b>`) + stat('RECEIVED', `<b>${fmtInt(r.received ?? 0)}</b>`) + stat('A / B', `<b>${fmtInt(r.sentA ?? 0)}</b><span>/ ${fmtInt(r.sentB ?? 0)}</span>`) + stat('EXPECTED', `<b>${((r.expectedBps ?? 0) / 1e6).toFixed(2)}</b><span>Mbit/s</span>`);
    (this.el.querySelector('[data-tx]') as HTMLElement).innerHTML = (r.vls ?? []).map((v: any) => `<tr><td>${v.vl}</td><td>${fmtInt(v.sentA)}</td><td>${fmtInt(v.sentB)}</td><td>${v.bagMs}</td><td>${num(v.meanMs)}</td><td>${num(v.minMs)}</td><td>${num(v.maxMs)}</td><td>${num(v.lateStdUs, 1)}</td><td>${num(v.lateMaxUs, 1)}</td></tr>`).join('');
    (this.el.querySelector('.cap') as HTMLElement).textContent = r.error ? 'error: ' + r.error : running ? 'receiving → ANALYZE shows the capture · STOP writes a .pcapng' : r.lastCapture ? `last capture: ${r.lastCapture}` : `captures go to ${r.captureDir ?? ''}`;
  }
  testApi() {
    const self = this; const fire = async (sel: string, v: string) => { const el = self.el.querySelector(sel) as HTMLInputElement; if (!el) throw new Error('no ' + sel); if (el.type === 'checkbox') el.checked = v === 'true'; else el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); await sleep(200); return true; };
    return { rows: () => self.el.querySelectorAll('.vltab tr[data-vl]').length, setVl: (vl: number, f: string, v: string) => fire(`.vltab tr[data-vl="${vl}"] [data-f="${f}"]`, v), setFault: (f: string, v: string) => fire(`[data-set][data-f="${f}"]`, v), click: async (a: string) => { (self.el.querySelector(`[data-a="${a}"]`) as HTMLElement).click(); await sleep(150); return true; }, txRows: () => self.el.querySelectorAll('[data-tx] tr').length };
  }
}

// ============================================================================ ANALYZE
const ROW_H = 22;
export class AfdxAnalyzePage {
  el = h(`<div class="afdx-an"><section class="card tree"><div class="card-h"><span class="t">VIRTUAL LINKS</span><span class="m" data-tm></span></div><div class="scroll" data-tree></div></section>
    <section class="card list"><div class="card-h"><span class="t">FRAMES</span><span class="m" data-lm></span><div class="acts"><button class="pill h24" data-a="listen" title="Receive only on the project's network (nothing is sent); the list fills live">● LISTEN</button><button class="pill h24" data-a="stopListen" title="Stop and write the capture (.pcapng)">■ STOP</button><button class="pill h24" data-a="openCap">OPEN CAPTURE…</button></div></div>
      <div class="afdx-filters"><span class="k">VL</span><input class="in w60" data-flt="vl" placeholder="all"><select class="sel h24" data-flt="net"><option value="-1">A + B</option><option value="0">A</option><option value="1">B</option></select><select class="sel h24" data-flt="kind"><option value="">all frames</option><option value="afdx">AFDX</option><option value="other">other traffic</option></select><label class="chk"><input type="checkbox" data-flt="errors"> ERRORS ONLY</label><input class="in w140" data-flt="text" placeholder="port · IP · UDP port"></div>
      <div class="afdx-filters" data-rule><span class="k">VL FROM</span><span class="muted11">MAC 03:00:00:00</span><label class="chk" title="Destination IP 224.224.x.x → VL x.x when the MAC is not an AFDX VL MAC"><input type="checkbox" data-rule="vlFromIp"> + IP 224.224.x.x</label><label class="chk" title="Plain UDP to an ICD port's destination (IP:port) → that VL"><input type="checkbox" data-rule="vlFromIcd"> + ICD UDP ports</label><span class="k">UDP MAP</span><input class="in w140" data-rule="udpVls" placeholder="5000=900, 5001=901" title="UDP destination port = VL id (traffic outside the ICD)"><label class="chk" title="Mapped UDP frames end with an AFDX sequence-number byte"><input type="checkbox" data-rule="udpSn"> SN byte</label></div>
      <div class="afdx-head"><span>#</span><span>TIME s</span><span>NET</span><span>KIND</span><span>VL</span><span>SN</span><span>LEN</span><span>DESTINATION</span><span>PORT</span><span>CHECKS</span></div>
      <div class="afdx-scroll"><div class="sp"></div><div class="rows"></div></div><div class="afdx-empty muted11">Open a capture (.pcapng / .pcap, interfaces A and B) or run SIMULATE.</div></section>
    <section class="card dec"><div class="card-h"><span class="t">FRAME</span><span class="m" data-dm></span></div><div class="scroll" data-dec><div class="muted11 pad">Select a frame.</div></div></section>
    <section class="card stats"><div class="card-h"><span class="t">CHECKS PER VL</span><span class="m" data-sm></span></div><div class="scroll" data-stats></div></section>
    <section class="card trend"><div class="card-h"><span class="t">TREND</span><span class="m" data-pm></span></div><div class="afdx-plot"><div class="muted11 pad">Click a parameter (▸ trend) in the frame or the VL tree.</div></div></section></div>`);
  private S: any = null; private key = ''; private total = 0; private rows = new Map<number, any>(); private loading = new Set<number>(); sel = -1; private selRow = -1;
  private pv: PlotView | null = null; chartId = -1; private openVl = new Set<number>(); private stats: any = null;
  private scroll: HTMLElement; private rowsEl: HTMLElement; private sp: HTMLElement;
  constructor() {
    this.scroll = this.el.querySelector('.afdx-scroll')!; this.rowsEl = this.el.querySelector('.rows')!; this.sp = this.el.querySelector('.sp')!;
    this.scroll.addEventListener('scroll', () => this.paint());
    this.rowsEl.addEventListener('click', e => { const r = (e.target as HTMLElement).closest('[data-row]') as HTMLElement | null; if (r) this.select(+r.dataset.row!); });
    this.el.querySelector('.afdx-filters')!.addEventListener('change', () => this.applyFilter());
    this.el.querySelector('[data-rule]')!.addEventListener('change', e => { const t = e.target as HTMLInputElement; const f = t.dataset.rule; if (!f) return; bridge.call('afdx.set', { field: f, value: t.type === 'checkbox' ? t.checked : t.value }).then(() => bridge.call('afdx.reanalyse')).then(() => this.reload()); });
    this.el.addEventListener('click', e => {
      const t = e.target as HTMLElement; const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (a === 'openCap') { bridge.call('afdx.open'); return; } if (a === 'listen') { bridge.call('afdx.listen'); return; } if (a === 'stopListen') { bridge.call('afdx.stop'); return; }
      const tr = t.closest('[data-trend]') as HTMLElement | null; if (tr) { this.trend(+tr.dataset.vl!, tr.dataset.trend!); return; }
      const vn = t.closest('[data-tvl]') as HTMLElement | null; if (vn) { const v = +vn.dataset.tvl!; if (this.openVl.has(v)) this.openVl.delete(v); else this.openVl.add(v); (this.el.querySelector('[data-flt="vl"]') as HTMLInputElement).value = String(v); this.applyFilter(); this.renderTree(); return; }
      const sr = t.closest('tr[data-svl]') as HTMLElement | null; if (sr) { (this.el.querySelector('[data-flt="vl"]') as HTMLInputElement).value = sr.dataset.svl!; this.applyFilter(); }
    });
    this.el.addEventListener('keydown', e => { if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); this.select(Math.max(0, Math.min(this.total - 1, this.selRow + (e.key === 'ArrowDown' ? 1 : -1)))); } });
    this.el.tabIndex = 0;
  }
  setState(S: any) {
    this.S = S; const c = S.afdx?.capture ?? {}; const key = JSON.stringify([c, S.afdx?.icd]);
    const st = S.afdx?.settings ?? {}; for (const f of ['vlFromIp', 'vlFromIcd', 'udpSn']) { const el = this.el.querySelector(`[data-rule="${f}"]`) as HTMLInputElement; if (el) el.checked = !!st[f]; }
    const um = this.el.querySelector('[data-rule="udpVls"]') as HTMLInputElement; if (um && document.activeElement !== um) um.value = st.udpVls ?? '';
    if (key !== this.key) { const capChanged = JSON.stringify([this.lastCap?.path, this.lastCap?.name]) !== JSON.stringify([c.path, c.name]); this.key = key; this.lastCap = c; if (capChanged) this.clearFrame(); this.reload(); }
    const ch = S.charts?.find((x: any) => x.id === this.chartId);
    if (ch) { const host = this.el.querySelector('.afdx-plot') as HTMLElement; if (!this.pv) { this.pv = new PlotView(ch, S.plot); host.innerHTML = ''; host.append(this.pv.el); } this.pv.update(ch, S.plot); }
  }
  private lastCap: any = null;
  /** A different capture: forget the selected frame and the trend of the previous one. */
  private clearFrame() {
    this.sel = -1; this.selRow = -1; (this.el.querySelector('[data-dm]') as HTMLElement).textContent = ''; (this.el.querySelector('[data-dec]') as HTMLElement).innerHTML = '<div class="muted11 pad">Select a frame.</div>';
    this.chartId = -1; this.pv = null; (this.el.querySelector('.afdx-plot') as HTMLElement).innerHTML = '<div class="muted11 pad">Click a parameter (▸ trend) in the frame or the VL tree.</div>'; (this.el.querySelector('[data-pm]') as HTMLElement).textContent = '';
  }
  async reload() {
    this.rows.clear(); this.loading.clear();
    const st = await bridge.call<any>('afdx.stats'); this.stats = st;
    const r = await bridge.call<any>('afdx.frames', { from: 0, count: 1 }); this.total = r?.total ?? 0;
    (this.el.querySelector('.afdx-empty') as HTMLElement).style.display = (r?.all ?? 0) > 0 ? 'none' : '';
    (this.el.querySelector('[data-lm]') as HTMLElement).textContent = st?.has ? `${esc(st.capture)} · ${fmtInt(this.total)} of ${fmtInt(r.all)} frames · ${st.durationS.toFixed(2)} s` : '';
    this.sp.style.height = `${Math.min(this.total * ROW_H, 6_000_000)}px`; this.paint(); this.renderTree(); this.renderStats();
  }
  private async applyFilter() {
    const g = (k: string) => this.el.querySelector(`[data-flt="${k}"]`) as HTMLInputElement;
    const vl = g('vl').value.trim(); await bridge.call('afdx.filter', { vl: vl === '' ? -1 : parseInt(vl, 10), net: parseInt(g('net').value, 10), kind: g('kind').value, errors: g('errors').checked, text: g('text').value });
    this.selRow = -1; this.scroll.scrollTop = 0; await this.reload();
  }
  private paint() {
    const top = Math.floor(this.scroll.scrollTop / ROW_H), n = Math.ceil(this.scroll.clientHeight / ROW_H) + 2; const want = Array.from({ length: n }, (_, k) => top + k).filter(i => i < this.total);
    const miss = want.filter(i => !this.rows.has(i)); if (miss.length) { const from = miss[0]; if (!this.loading.has(from)) { this.loading.add(from); bridge.call<any>('afdx.frames', { from, count: 200 }).then(r => { this.loading.delete(from); for (const x of r?.rows ?? []) this.rows.set(x.row, x); this.paint(); }); } }
    this.rowsEl.style.transform = `translateY(${top * ROW_H}px)`;
    this.rowsEl.innerHTML = want.map(i => { const x = this.rows.get(i); if (!x) return `<div class="afdx-row"><span>${i + 1}</span></div>`; return `<div class="afdx-row${x.err ? ' err' : ''}${x.dup ? ' dup' : ''}${i === this.selRow ? ' sel' : ''}" data-row="${i}"><span>${x.i + 1}</span><span>${x.t.toFixed(6)}</span><span>${x.net}</span><span${x.frag ? ` title="IP fragment, offset ${x.frag.replace('+', ' (more follow)')}"` : ''}>${x.frag ? 'FRAG' : x.kind}</span><span>${x.vl >= 0 ? x.vl : ''}${x.from ? ` <span class="dim" title="VL recognised by the ${x.from} fallback">${x.from}</span>` : ''}</span><span>${x.sn >= 0 ? x.sn : ''}</span><span>${x.len}</span><span>${esc(x.dst)}</span><span>${esc(x.port)}</span><span class="${x.err ? 'errv' : 'dim'}">${esc(x.flags)}</span></div>`; }).join('');
  }
  /** Show frame index i of the capture: clear the filters (row = frame index) and select it. */
  async goto(i: number) {
    for (const k of ['vl', 'text']) (this.el.querySelector(`[data-flt="${k}"]`) as HTMLInputElement).value = '';
    (this.el.querySelector('[data-flt="net"]') as HTMLSelectElement).value = '-1'; (this.el.querySelector('[data-flt="kind"]') as HTMLSelectElement).value = ''; (this.el.querySelector('[data-flt="errors"]') as HTMLInputElement).checked = false;
    await this.applyFilter(); await this.select(i);
  }
  async select(row: number) {
    this.selRow = row; let x = this.rows.get(row); if (!x) { const r = await bridge.call<any>('afdx.frames', { from: row, count: 1 }); x = r?.rows?.[0]; } if (!x) return; this.sel = x.i;
    const top = row * ROW_H; if (top < this.scroll.scrollTop) this.scroll.scrollTop = top; else if (top + ROW_H > this.scroll.scrollTop + this.scroll.clientHeight) this.scroll.scrollTop = top + ROW_H - this.scroll.clientHeight;
    this.paint(); const d = await bridge.call<any>('afdx.frame', { i: x.i }); this.renderDecode(d);
  }
  private renderDecode(d: any) {
    (this.el.querySelector('[data-dm]') as HTMLElement).textContent = d ? `#${d.i + 1} · network ${d.net}` : '';
    const kv = (k: string, v: any) => `<tr><td class="dim">${k}</td><td>${esc(v)}</td></tr>`;
    (this.el.querySelector('[data-dec]') as HTMLElement).innerHTML = `<table class="rt-tbl afdx-kv">${kv('TIME', `${d.t.toFixed(6)} s · ${d.abs}`)}${kv('KIND', d.kind)}${d.vl >= 0 ? kv('VL', `${d.vl}${d.vlName ? ' · ' + d.vlName : ''}${d.bag ? ` · BAG ${d.bag} ms · Lmax ${d.lmax}` : ''}${d.vlFrom !== 'MAC' ? ` · from ${d.vlFrom === 'IP' ? 'destination IP' : 'UDP map'}` : ''}`) + kv('SN', d.hasSn ? d.sn : '— (mapped UDP, no sequence number)') : ''}${d.fragment ? kv('IP FRAGMENT', `id ${d.ipId} · offset ${d.fragOffset} B${d.moreFragments ? ' · more follow' : ' · last'}`) : ''}${kv('MAC', `${d.srcMac} → ${d.dstMac}`)}${d.ipDst !== '0.0.0.0' ? kv('UDP', `${d.udpSrc} → ${d.ipDst}:${d.udpDst}`) : ''}${kv('LENGTH', `${d.wire} B on the wire (captured ${d.captured}) · payload ${d.payload} B`)}${d.port ? kv('PORT', `${d.port} · ${d.portType} · sub-VL ${d.subVl}`) : ''}${d.flags ? `<tr><td class="dim">CHECKS</td><td class="errv">${esc(d.flags)}</td></tr>` : ''}</table>
      ${d.params?.length ? `<table class="rt-tbl"><thead><tr><th>PARAMETER</th><th>HEX</th><th>EU</th><th></th></tr></thead><tbody>${d.params.map((p: any) => `<tr data-trend="${esc(p.name)}" data-vl="${d.vl}" title="Plot the trend"><td>${esc(p.name)}</td><td class="mono dim">${p.hex}</td><td>${num(p.eu, 6)} <span class="dim">${esc(p.unit)}</span></td><td class="dim">▸ trend</td></tr>`).join('')}</tbody></table>` : ''}
      <pre class="afdx-hex">${esc(d.hex)}</pre>`;
  }
  private renderTree() {
    const st = this.stats; const vls: any[] = st?.vls ?? []; (this.el.querySelector('[data-tm]') as HTMLElement).textContent = st?.has ? `${vls.length} · other ${fmtInt(st.arp + st.icmp + st.otherIp + st.other)}` : '';
    const icd: any[] = this.S?.afdx?.vlList ?? [];
    (this.el.querySelector('[data-tree]') as HTMLElement).innerHTML = vls.map(v => `<div class="tnode" data-tvl="${v.vl}"><span class="n">VL ${v.vl} <span class="dim">${esc(v.inIcd ? v.name : 'not in ICD')}</span></span><span class="c">${fmtInt(v.accepted)}${v.errors ? ` <em class="errv">· ${fmtInt(v.errors)}</em>` : ''}</span></div>` + (this.openVl.has(v.vl) ? this.paramNodes(v.vl, icd) : '')).join('') + (st?.notInCapture?.length ? `<div class="tnode grp"><span class="n dim">In the ICD, not received: ${st.notInCapture.join(', ')}</span></div>` : '');
  }
  private paramNodes(vl: number, icd: any[]) { const v = this.S?.afdx?.vlList ? icd.find(x => x.vl === vl) : null; if (!v) return ''; return (this.icdCache?.vls ?? []).filter((x: any) => x.vl_id === vl).flatMap((x: any) => x.ports.flatMap((p: any) => p.params.map((q: any) => `<div class="tnode word" data-trend="${esc(q.name)}" data-vl="${vl}" title="Plot the trend"><span class="n">${esc(q.name)}</span><span class="c dim">${esc(q.unit)} ▸</span></div>`))).join(''); }
  private icdCache: any = null;
  private renderStats() {
    const st = this.stats; if (!st?.has) { (this.el.querySelector('[data-stats]') as HTMLElement).innerHTML = ''; return; }
    bridge.call('afdx.icd').then(i => { this.icdCache = i; this.renderTree(); });
    (this.el.querySelector('[data-sm]') as HTMLElement).textContent = `${fmtInt(st.errors)} findings · ${kbps(st.bandwidthBps)} measured · checks ${st.checkMs.toFixed(0)} ms`;
    const c = (v: number) => v ? `<b class="errv">${fmtInt(v)}</b>` : '0';
    (this.el.querySelector('[data-stats]') as HTMLElement).innerHTML = `<table class="rt-tbl"><thead><tr><th>VL</th><th>NET</th><th>BAG</th><th>FRAMES A/B</th><th>ACCEPTED</th><th>SN GAP A/B</th><th>SN REV A/B</th><th>BAG VIOL A/B</th><th>LMAX</th><th>MISSING A/B</th><th>SKEW µs mean/max</th><th>INTERVAL ms A/B</th><th>JITTER ms</th><th>BANDWIDTH</th></tr></thead><tbody>${st.vls.map((v: any) => `<tr data-svl="${v.vl}" title="Show this VL's frames"><td>${v.vl}</td><td>${v.network}</td><td>${v.bagMs}${v.bagFromIcd ? '' : '<span class="dim">*</span>'}</td><td>${fmtInt(v.framesA)} / ${fmtInt(v.framesB)}</td><td>${fmtInt(v.accepted)}</td><td>${c(v.snGapsA)} / ${c(v.snGapsB)}</td><td>${c(v.snRevA)} / ${c(v.snRevB)}</td><td>${c(v.bagViolA)} / ${c(v.bagViolB)}</td><td>${c(v.lmaxViol)}</td><td>${c(v.missingA)} / ${c(v.missingB)}</td><td>${num(v.skewMeanUs, 1)} / ${num(v.skewMaxUs, 1)}</td><td>${num(v.meanMsA)} / ${num(v.meanMsB)}</td><td>${num(Math.max(v.jitterMsA, v.jitterMsB), 4)}</td><td class="dim">${kbps(v.bandwidthBps)}</td></tr>`).join('')}</tbody></table><div class="muted11 pad">* BAG inferred from the capture (no ICD entry). VL from: ${esc(st.rule ?? 'MAC')}. Other traffic: ARP ${st.arp} · ICMP ${st.icmp} · other IP ${st.otherIp} · other ${st.other} · IPv4 ${fmtInt(st.ipv4 ?? 0)} (UDP ${fmtInt(st.udp ?? 0)} · TCP ${fmtInt(st.tcp ?? 0)}).</div>${this.queuing(st)}`;
  }
  /** Queuing ports (IP-fragmented messages) and sub-VLs: one row per VL that has either. */
  private queuing(st: any) {
    const l = (st.vls ?? []).filter((v: any) => v.fragments > 0 || (v.subVls?.length ?? 0) > 1); if (!l.length) return '';
    const c = (v: number) => v ? `<b class="errv">${fmtInt(v)}</b>` : '0';
    return `<table class="rt-tbl" data-qtab><thead><tr><th>VL</th><th>FRAGMENTS</th><th>MESSAGES OK</th><th>INCOMPLETE</th><th>OUT OF ORDER</th><th>CUT BY CAPTURE</th><th>SUB-VL ORDER BREAKS</th><th>SUB-VL · FRAMES · MEAN INTERVAL (BAGs)</th></tr></thead><tbody>${l.map((v: any) => `<tr data-svl="${v.vl}"><td>${v.vl}</td><td>${fmtInt(v.fragments)}</td><td>${fmtInt(v.msgComplete)}</td><td>${c(v.msgIncomplete)}</td><td>${c(v.msgReordered)}</td><td class="dim">${fmtInt(v.msgTruncated)}</td><td>${c(v.subVlBreaks)}</td><td class="dim">${(v.subVls ?? []).map((x: any) => `${x.sub}: ${fmtInt(x.frames)} · ${num(x.meanMs)} ms (${num(x.meanBags, 2)})`).join(' &nbsp; ')}</td></tr>`).join('')}</tbody></table>`;
  }
  async trend(vl: number, param: string) {
    const ch = await bridge.call<string | null>('afdx.trend', { vl, param }); if (!ch) return;
    this.chartId = await bridge.call<number>('trend.chart', { clear: true });
    await bridge.call('addCurves', { chart: this.chartId, channels: [ch], zone: 'plot' }); (this.el.querySelector('[data-pm]') as HTMLElement).textContent = `VL ${vl} · ${param}`;
  }
  testApi() {
    const self = this;
    return {
      total: () => self.total, stats: () => self.stats, rowsShown: () => self.rowsEl.querySelectorAll('[data-row]').length, chartId: () => self.chartId,
      async filter(f: { vl?: number; net?: number; errors?: boolean; kind?: string; text?: string }) { const g = (k: string) => self.el.querySelector(`[data-flt="${k}"]`) as HTMLInputElement; g('vl').value = f.vl == null || f.vl < 0 ? '' : String(f.vl); g('net').value = String(f.net ?? -1); g('kind').value = f.kind ?? ''; g('errors').checked = !!f.errors; g('text').value = f.text ?? ''; g('vl').dispatchEvent(new Event('change', { bubbles: true })); for (let k = 0; k < 50; k++) { await sleep(40); if (self.rowsEl.querySelector('[data-row]') || self.total === 0) break; } return self.total; },
      async selectRow(r: number) { await self.select(r); await sleep(150); return (self.el.querySelector('[data-dec]') as HTMLElement).innerText; },
      async trendFirst() { const t = self.el.querySelector('[data-dec] [data-trend]') as HTMLElement | null; if (!t) throw new Error('no decoded parameter'); t.click(); for (let k = 0; k < 40 && self.chartId < 0; k++) await sleep(50); await sleep(300); return self.chartId; },
      decodedParams: () => self.el.querySelectorAll('[data-dec] [data-trend]').length,
      click: async (a: string) => { (self.el.querySelector(`[data-a="${a}"]`) as HTMLElement).click(); await sleep(300); return true; },
      showQueuing: async () => { const t = self.el.querySelector('[data-qtab]') as HTMLElement | null; t?.scrollIntoView({ block: 'end' }); await sleep(150); return !!t; },
      decodeText: () => (self.el.querySelector('[data-dec]') as HTMLElement).innerText, queuingRows: () => self.el.querySelectorAll('[data-qtab] tbody tr').length, selectedFrame: () => self.sel,
      async setRule(f: string, v: string | boolean) { const el = self.el.querySelector(`[data-rule="${f}"]`) as HTMLInputElement; if (typeof v === 'boolean') el.checked = v; else el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); await sleep(600); return self.stats?.vls?.length ?? 0; },
    };
  }
}

// ============================================================================ INTEROP
export class AfdxInteropPage {
  el = h(`<div class="afdx-iop"><section class="card peer"><div class="card-h"><span class="t">PEER → NEXYSBUS · AUTOMATIC</span><span class="m" data-cm></span><div class="acts"><button class="pill h24" data-a="openCap">OPEN CAPTURE…</button><button class="pill h24" data-a="recheck">RE-CHECK</button><button class="pill h24" data-a="wireshark" title="Wireshark profile + dissector for this ICD (VL / SN / network columns, A/B colours, parameter decode)">WIRESHARK PROFILE…</button><button class="pill h24 primary" data-a="export">EXPORT REPORT…</button></div></div>
      <div class="errs" data-set></div><div class="scroll"><table class="frame-tbl"><thead><tr><th>#</th><th>CHECK</th><th>VERDICT</th><th>DETAIL</th><th>EVIDENCE</th></tr></thead><tbody data-checks></tbody></table></div></section>
    <section class="card tx"><div class="card-h"><span class="t">NEXYSBUS → PEER · REPORTED BY THE PEER</span><span class="m" data-tm></span><div class="acts"><button class="pill h24" data-a="peerCsv" title="CSV, one row per VL: vl, rx_a, rx_b, errors, note">IMPORT PEER CSV…</button></div></div><div class="errs"><span class="k">PEER</span><input class="in w200" data-peer="name" placeholder="peer equipment / tool"></div><div class="scroll"><table class="rt-tbl"><thead><tr><th>VL</th><th>NAME</th><th>NET</th><th>SENT A</th><th>SENT B</th><th>INTERVAL ms</th><th>PEER RX A</th><th>PEER RX B</th><th>PEER ERRORS</th><th>NOTE</th><th>VERDICT</th></tr></thead><tbody data-tx></tbody></table></div></section></div>`);
  private key = ''; data: any = null;
  constructor() {
    this.el.addEventListener('click', e => {
      const t = e.target as HTMLElement; const fr = t.closest('[data-frame]') as HTMLElement | null; if (fr) { window.dispatchEvent(new CustomEvent('afdx-goto', { detail: +fr.dataset.frame! - 1 })); return; }
      const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (a === 'openCap') bridge.call('afdx.open'); else if (a === 'recheck') bridge.call('afdx.reanalyse').then(() => this.load()); else if (a === 'export') bridge.call('afdx.interop.export');
      else if (a === 'peerCsv') bridge.call('afdx.peer.importCsv').then(() => this.load()); else if (a === 'wireshark') bridge.call('afdx.wireshark.export');
    });
    this.el.addEventListener('change', e => {
      const t = e.target as HTMLInputElement;
      if (t.dataset.peer === 'name') { bridge.call('afdx.set', { field: 'peerName', value: t.value }); return; }
      if (t.dataset.set) { bridge.call('afdx.set', { field: t.dataset.f, value: parseFloat(t.value) || 0 }).then(() => this.load()); return; }
      const vl = (t.closest('tr[data-vl]') as HTMLElement | null)?.dataset.vl; if (vl == null || !t.dataset.f) return;
      bridge.call('afdx.peer.set', { vl: +vl, field: t.dataset.f, value: t.dataset.f === 'note' ? t.value : t.value.trim() === '' ? null : parseInt(t.value, 10) }).then(() => this.load());
    });
  }
  setState(S: any) { const k = JSON.stringify([S.afdx?.capture, S.afdx?.run?.lastCapture, S.afdx?.settings]); if (k === this.key) return; this.key = k; const st = S.afdx?.settings ?? {}; (this.el.querySelector('[data-set]') as HTMLElement).innerHTML = `<span class="k">CHECK SETTINGS</span><span>Jmax ms</span><input class="in" data-set="1" data-f="jmaxMs" value="${st.jmaxMs ?? 0.5}"><span>skew limit ms</span><input class="in" data-set="1" data-f="skewMaxMs" value="${st.skewMaxMs ?? 10}"><span>pair window ms</span><input class="in" data-set="1" data-f="pairWindowMs" value="${st.pairWindowMs ?? 20}">`; (this.el.querySelector('[data-peer="name"]') as HTMLInputElement).value = st.peerName ?? ''; this.load(); }
  async load() {
    const d = await bridge.call<any>('afdx.interop'); this.data = d; if (!d) return;
    (this.el.querySelector('[data-cm]') as HTMLElement).textContent = `${esc(d.capture || 'no capture')} · ${d.checks.filter((c: any) => c.verdict === 'PASS').length} PASS · ${d.checks.filter((c: any) => c.verdict === 'FAIL').length} FAIL`;
    (this.el.querySelector('[data-checks]') as HTMLElement).innerHTML = d.checks.map((c: any, i: number) => `<tr data-check="${c.id}"><td>${i + 1}</td><td>${esc(c.check)}</td><td class="${c.verdict === 'FAIL' ? 'errv' : c.verdict === 'PASS' ? '' : 'dim'}"><b>${c.verdict}</b></td><td class="dim wrap">${esc(c.detail)}</td><td>${c.frame > 0 ? `<a class="lnk" data-frame="${c.frame}" title="Show this frame in ANALYZE (Wireshark: frame.number == ${c.frame})">frame ${c.frame}</a>` : ''}</td></tr>`).join('');
    const inp = (f: string, v: any, w = 'w60') => `<input class="cell ${w}" data-f="${f}" value="${v ?? ''}">`;
    (this.el.querySelector('[data-tm]') as HTMLElement).textContent = `${d.tx.length} VLs · ${d.tx.filter((r: any) => r.verdict === 'PASS').length} PASS · ${d.tx.filter((r: any) => r.verdict === 'FAIL').length} FAIL`;
    (this.el.querySelector('[data-tx]') as HTMLElement).innerHTML = d.tx.map((r: any) => `<tr data-vl="${r.vl}" class="${r.enabled ? '' : 'off'}"><td>${r.vl}</td><td>${esc(r.name)}</td><td>${r.network}</td><td>${fmtInt(r.sentA)}</td><td>${fmtInt(r.sentB)}</td><td>${num(r.bagMeasMs)}</td><td>${inp('rxA', r.rxA)}</td><td>${inp('rxB', r.rxB)}</td><td>${inp('errors', r.errors)}</td><td>${inp('note', esc(r.note), 'w')}</td><td class="${r.verdict === 'FAIL' ? 'errv' : ''}"><b>${r.verdict}</b></td></tr>`).join('');
  }
  testApi() {
    const self = this;
    return { checks: () => (self.data?.checks ?? []).map((c: any) => ({ id: c.id, verdict: c.verdict, detail: c.detail, frame: c.frame })),
      async clickFrame(id: string) { const a = self.el.querySelector(`tr[data-check="${id}"] [data-frame]`) as HTMLElement | null; if (!a) return -1; a.click(); await sleep(900); return +a.dataset.frame!; }, tx: () => self.data?.tx ?? [], reload: async () => { await self.load(); return self.data?.checks?.length ?? 0; },
      async setPeer(vl: number, f: string, v: string) { const el = self.el.querySelector(`tr[data-vl="${vl}"] [data-f="${f}"]`) as HTMLInputElement; if (!el) throw new Error('no peer cell'); el.value = v; el.dispatchEvent(new Event('change', { bubbles: true })); await sleep(300); return true; } };
  }
}
