// Start screen: new project / open project / open capture / recent projects.
import { bridge } from '@core/bridge';
import { I } from '@core/icons';
import { esc, h } from '@core/util';

export class StartPage {
  el = h(`<section class="start"><div class="start-in"></div></section>`);
  private last = '';
  constructor() {
    this.el.addEventListener('click', e => {
      const t = e.target as HTMLElement; const p = (t.closest('[data-path]') as HTMLElement | null)?.dataset.path; if (p) { bridge.call('project.openPath', { path: p }); return; }
      const a = (t.closest('[data-a]') as HTMLElement | null)?.dataset.a; if (!a) return;
      if (a === 'newProject') bridge.call('project.new', { name: 'New project' });
      else if (a === 'openProject') bridge.call('project.open');
      else if (a === 'openCapture') bridge.call('afdx.open');
    });
  }
  setState(S: any) {
    const recent: any[] = S.recent ?? []; const pcap = (S.devices ?? []).some((d: any) => d.id === 'network' && d.available);
    const key = JSON.stringify([recent, pcap, S.app?.versionText]); if (key === this.last) return; this.last = key;
    const note = pcap ? 'Packet-capture library found — pick the Ethernet ports of network A and B in PROJECT.' : 'Ethernet ports need the packet-capture library (Npcap, installed separately). The virtual network works without it.';
    this.el.querySelector('.start-in')!.innerHTML = `<h1>${esc(S.app?.name || 'NexysNet')}<small>ARINC 664 (AFDX) network capture, checks and transmit · interoperability checklist and report · ICD of virtual links — on the virtual network right now, on Ethernet ports where the packet-capture library is installed.</small></h1>
      <div class="actions"><button class="pill h32 primary" data-a="newProject" title="ARINC 664 project">${I.file(15)}NEW PROJECT</button><button class="pill h32" data-a="openProject">${I.folder(15)}OPEN PROJECT…</button><button class="pill h32" data-a="openCapture">${I.folder(15)}OPEN CAPTURE…</button><div class="note">${note}</div></div>
      <div class="recent"><div class="lblh">RECENT PROJECTS</div>${recent.length ? recent.map(r => `<button data-path="${esc(r.path)}" title="${esc(r.path)}"><b>${esc(r.name)}</b><span>${esc(r.path)}</span></button>`).join('') : '<div class="faint11">None yet — start with NEW PROJECT and SAVE it as an .nxn file.</div>'}</div>`;
  }
}
