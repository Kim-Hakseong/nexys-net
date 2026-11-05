// NexysNet shell: the shared shell of nexys-core (@core/shell: header · rail · page · footer · about · keys · drop · test hooks) with the
// ARINC 664 pages — PROJECT · ANALYZE · SIMULATE · ICD · INTEROP · SETTINGS.
import { bridge } from '@core/bridge';
import { I } from '@core/icons';
import { createShell, ShellState } from '@core/shell';
import { AfdxAnalyzePage, AfdxIcdPage, AfdxInteropPage, AfdxSimPage } from './afdx';
import { help } from './help';
import { ProjectPage } from './project';
import { settingsPage } from './settings';
import { StartPage } from './start';

const start = new StartPage(); const project = new ProjectPage(); const settings = settingsPage();
const icd = new AfdxIcdPage(); const sim = new AfdxSimPage(); const an = new AfdxAnalyzePage(); const iop = new AfdxInteropPage();
window.addEventListener('afdx-goto', async e => { await bridge.call('rail', { name: 'ANALYZE' }); await new Promise(r => setTimeout(r, 300)); await an.goto((e as CustomEvent).detail); });

const pageOf = (rail: string): HTMLElement => rail === 'PROJECT' ? project.el : rail === 'ANALYZE' ? an.el : rail === 'SIMULATE' ? sim.el : rail === 'ICD' ? icd.el : rail === 'INTEROP' ? iop.el : rail === 'SETTINGS' ? settings.el : start.el;
createShell({
  appName: 'NexysNet',
  aboutTitle: 'ARINC 664 (AFDX) network capture, checks, transmit and interoperability',
  aboutBeta: 'Pre-release build: the product name and screens may change; transmission on a wired Ethernet port awaits its first bench measurement. Please report problems through the feedback link below.',
  openTitle: 'Open project / capture / ICD (Ctrl+O)',
  rails: [
    { id: 'PROJECT', label: 'PROJECT', icon: I.file(18), key: '1' }, { id: 'ANALYZE', label: 'ANALYZE', icon: I.table(18), key: '2' }, { id: 'SIMULATE', label: 'SIMULATE', icon: I.lineC(18), key: '3' },
    { id: 'ICD', label: 'ICD', icon: I.list(18), key: '4' }, { id: 'INTEROP', label: 'INTEROP', icon: I.table(18), key: '6' }, { id: 'SETTINGS', label: 'SETTINGS', icon: I.sliders(18), key: '5' }],
  pages: S => [pageOf(S.rail)],
  route: (S: ShellState) => {
    if (S.rail === 'START') start.setState(S); else if (S.rail === 'PROJECT') project.setState(S); else if (S.rail === 'ANALYZE') an.setState(S); else if (S.rail === 'SIMULATE') sim.setState(S);
    else if (S.rail === 'ICD') icd.setState(S); else if (S.rail === 'INTEROP') iop.setState(S); else if (S.rail === 'SETTINGS') settings.setState(S);
  },
  footerText: S => S.project?.name ? `${S.project.name} · ${S.project.icd?.vls ?? 0} virtual links` : 'Open a project or create one',
  footerRight: S => { const r = S.afdx?.run ?? {}; return r.running ? `network running · ${Math.round(r.sent ?? 0).toLocaleString('en-US')} frames sent · ${Math.round(r.received ?? 0).toLocaleString('en-US')} received` : ''; },
  help,
  test: {
    project: () => project.testApi(),
    afdx: () => ({ icd: icd.testApi(), sim: sim.testApi(), an: an.testApi(), iop: iop.testApi() }), afdxBuilt: () => true,
  },
});
