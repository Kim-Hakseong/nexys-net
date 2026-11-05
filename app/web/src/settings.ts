// SETTINGS rail: the shared frame (appearance, profile) + the project's capture folder.
import { bridge } from '@core/bridge';
import { SettingsFrame } from '@core/shell';
import { esc } from '@core/util';

export const settingsPage = () => new SettingsFrame('NexysNet',
  S => `<section class="card sec"><div class="card-h"><span class="t">PROJECT</span><span class="m">folders</span></div><div class="body-p"><div class="row">Capture folder <input class="in mono" data-f="captureDir" value="${esc(S.project?.captureDir || '')}" placeholder="%LOCALAPPDATA%\\NexysNet\\captures" spellcheck="false"><button class="pill h24" data-a="pickCapture">PICK…</button><span class="d">live captures (pcapng) and SIMULATE runs</span></div></div></section>`,
  S => [S.project?.captureDir],
  d => { if (d.a === 'pickCapture') { bridge.call('project.pickCaptureDir'); return true; } return false; },
  t => { if (t.dataset.f === 'captureDir') bridge.call('project.set', { field: 'captureDir', value: t.value }); });
