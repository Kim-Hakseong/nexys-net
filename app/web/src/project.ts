// PROJECT rail: name / file / capture folder · network (virtual, shared virtual, Ethernet ports A / B, card slot) · checks · ICD summary.
import { bridge } from '@core/bridge';
import { esc, h } from '@core/util';

export class ProjectPage {
  el = h(`<div class="project"></div>`);
  private key = '';
  private ports: any = null;
  constructor() {
    this.el.addEventListener('click', async e => {
      const t = e.target as HTMLElement; const dev = (t.closest('[data-dev]') as HTMLElement | null); if (dev && !dev.classList.contains('off')) { bridge.call('project.set', { field: 'device', value: dev.dataset.dev }); return; }
      const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
      if (a === 'save') bridge.call('project.save'); else if (a === 'saveAs') bridge.call('project.save', { as: true }); else if (a === 'close') bridge.call('project.close');
      else if (a === 'pickCapture') bridge.call('project.pickCaptureDir'); else if (a === 'icd664') bridge.call('rail', { name: 'ICD' }); else if (a === 'simulate') bridge.call('rail', { name: 'SIMULATE' }); else if (a === 'interop') bridge.call('rail', { name: 'INTEROP' });
    });
    this.el.addEventListener('change', e => {
      const t = e.target as HTMLInputElement; if (t.dataset.nic) { bridge.call('afdx.set', { field: t.dataset.nic, value: t.value }); return; } const f = t.dataset.f; if (!f) return;
      bridge.call('project.set', { field: f, value: t.value });
    });
  }
  setState(S: any) {
    const p = S.project ?? {}; const devs: any[] = S.devices ?? []; const key = JSON.stringify([p, devs, S.afdx?.settings, S.afdx?.icd, this.ports]); if (key === this.key) return; this.key = key;
    const a = S.afdx ?? {}; const st = a.settings ?? {}; const icd = a.icd ?? {};
    if (!this.ports) bridge.call('afdx.ports').then(r => { this.ports = r; this.key = ''; this.setState(S); });
    const ports: any[] = this.ports?.ports ?? [];
    const row = (k: string, body: string) => `<div class="row2"><span class="k">${k}</span>${body}</div>`;
    const inp = (f: string, v: any, cls = 'in', extra = '') => `<input class="${cls}" data-f="${f}" value="${esc(v ?? '')}"${extra}>`;
    const nicSel = (f: string, v: string) => `<select class="sel" data-nic="${f}"${ports.length ? '' : ' disabled'}><option value="">— not used —</option>${ports.map(x => `<option value="${esc(x.id)}"${x.id === v ? ' selected' : ''}>${esc(x.name)}</option>`).join('')}</select>`;
    this.el.innerHTML = `
      <section class="card"><div class="card-h"><span class="t">PROJECT</span><span class="m">${esc(p.path ? p.path.split(/[\\/]/).pop() : 'unsaved')}</span></div><div class="body-p">
        ${row('NAME', inp('name', p.name))}
        ${row('PROTOCOL', `<span class="muted11">ARINC 664 part 7 (AFDX)</span>`)}
        ${row('FILE', `<span class="mono muted11" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${esc(p.path || '(not saved yet)')}</span><span class="d"><button class="pill h24" data-a="save">SAVE</button> <button class="pill h24" data-a="saveAs">SAVE AS…</button> <button class="pill h24" data-a="close">CLOSE</button></span>`)}
        ${row('CAPTURE FOLDER', inp('captureDir', p.captureDir, 'in', ' placeholder="%LOCALAPPDATA%\\NexysNet\\captures"') + `<button class="pill h24" data-a="pickCapture">PICK…</button>`)}
      </div></section>
      <section class="card"><div class="card-h"><span class="t">DEVICE</span><span class="m">${devs.filter(d => d.available).length} available</span></div><div class="body-p"><div class="dev-list">
        ${devs.map(d => `<div class="dev${d.id === p.device ? ' on' : ''}${d.available ? '' : ' off'}" data-dev="${esc(d.id)}"><span class="ic">${d.kind === 'virtual' ? '▣' : '▢'}</span><div><div class="nm">${esc(d.name)}</div><div class="dt">${esc(d.detail)}</div></div><span class="chip">${d.id === p.device ? 'SELECTED' : d.available ? 'AVAILABLE' : 'UNAVAILABLE'}</span></div>`).join('')}
      </div></div></section>
      <section class="card"><div class="card-h"><span class="t">NETWORK</span><span class="m">${this.ports?.available ? 'packet-capture library: ' + esc(this.ports.library) : 'no packet-capture library — virtual network only'}</span></div><div class="body-p">
        ${row('NETWORK A', nicSel('nicA', st.nicA ?? '') + `<span class="d">Ethernet port of network A</span>`)}
        ${row('NETWORK B', nicSel('nicB', st.nicB ?? '') + `<span class="d">Port of network B (the same port = frames told apart by source MAC)</span>`)}
        ${row('CHECKS', `<span class="muted11">Jmax ${st.jmaxMs ?? 0.5} ms · skew limit ${st.skewMaxMs ?? 10} ms · pair window ${st.pairWindowMs ?? 20} ms</span><span class="d"><button class="pill h24" data-a="interop">INTEROP →</button></span>`)}
      </div></section>
      <section class="card"><div class="card-h"><span class="t">ICD · VIRTUAL LINKS</span><span class="m">${esc(icd.source || 'no ICD')}</span></div><div class="body-p">
        <div class="stat-row"><div><div class="k">VLs</div><div class="v">${icd.vls ?? 0}</div></div><div><div class="k">PORTS</div><div class="v">${icd.ports ?? 0}</div></div><div><div class="k">PARAMS</div><div class="v">${icd.params ?? 0}</div></div><div><div class="k">ERRORS</div><div class="v${icd.errors ? ' errv' : ''}">${icd.errors ?? 0}</div></div></div>
        ${row('', `<button class="pill h24 primary" data-a="icd664">ICD →</button><button class="pill h24" data-a="simulate">SIMULATE →</button>`)}
      </div></section>`;
  }
  testApi() {
    const self = this;
    return {
      async setField(f: string, v: string) { const inp = self.el.querySelector(`[data-f="${f}"]`) as HTMLInputElement; if (!inp) throw new Error('no field ' + f); inp.value = v; inp.dispatchEvent(new Event('change', { bubbles: true })); await new Promise(r => setTimeout(r, 150)); return true; },
      devices: () => Array.from(self.el.querySelectorAll<HTMLElement>('[data-dev]')).map(d => ({ id: d.dataset.dev, on: d.classList.contains('on'), off: d.classList.contains('off') })),
    };
  }
}
