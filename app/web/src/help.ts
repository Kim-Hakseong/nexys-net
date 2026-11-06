// Help guide (F1 / ? button): sectioned user guide of NexysNet (the modal frame is the shared one of nexys-core).
import { HelpFrame, HelpSection } from '@core/shell';
import { esc } from '@core/util';

const SECTIONS: HelpSection[] = [
  { id: 'start', label: '시작하기', html: app => `
    <h3>NexysNet에 오신 것을 환영합니다!</h3>
    <p>NexysNet은 ARINC 664 part 7(AFDX) 네트워크를 <b>캡처·검사</b>하고, ICD의 가상 링크(VL)를 <b>송신</b>하고, 상대 장비와의 <b>상호운용</b>을 체크리스트·보고서로 정리하는 도구입니다. 가상 네트워크로는 바로, 이더넷 포트로는 패킷 캡처 라이브러리(Npcap, 별도 설치 — NexysNet에 포함되지 않음)가 있는 PC에서 같은 화면으로 동작합니다.</p>
    <h4>5분 경로</h4>
    <ol><li>시작 화면 <b>NEW PROJECT</b> → PROJECT에서 이름·네트워크(Virtual network)를 고릅니다.</li><li><b>ICD</b> 레일에서 VL ICD(CSV·XLSX·허브 JSON)를 가져옵니다.</li><li><b>SIMULATE</b>에서 <b>10 s BATCH</b> 또는 <b>START</b>. 받은 프레임은 ANALYZE에 쌓입니다.</li><li><b>ANALYZE</b>에서 검사 결과·디코드·추이, <b>INTEROP</b>에서 체크리스트와 보고서.</li></ol>
    <p>캡처 파일(<code>.pcapng</code>/<code>.pcap</code>)은 OPEN CAPTURE…나 끌어다 놓기로 바로 엽니다.</p>
    <div class="help-note">문제나 제안은 <a href="#" data-url="${esc(app.issues || '')}">GitHub Issues</a>에 남겨 주세요. 저장소: <a href="#" data-url="${esc(app.repo || '')}">${esc(app.repo || '')}</a></div>` },
  { id: 'project', label: 'PROJECT', html: () => `
    <h3>PROJECT — 네트워크 · 파일</h3>
    <div class="help-card"><b>장치</b><p>Virtual network는 항상 있습니다(A/B 루프백, 지연·지터·B 스큐·손실·중복 주입). <b>UDP (Windows sockets)</b>는 드라이버·관리자 권한 없이 ICD 포트의 UDP를 주고받습니다(VI · PC 프로그램과 시험할 때, Npcap 불필요). <b>Shared virtual network</b>는 이 PC의 다른 NexysNet 창과 프레임을 주고받습니다(한 창 SIMULATE, 다른 창 ANALYZE › LISTEN — 두 인스턴스 상호운용). 이더넷 포트는 패킷 캡처 라이브러리가 있을 때만 NETWORK A/B에서 고릅니다. 전용 AFDX 카드는 자리만 있습니다.</p></div>
    <div class="help-card"><b>파일</b><p>프로젝트는 <code>.nxn</code>(JSON)으로 저장됩니다: 네트워크·ICD·송신 설정·검사 기준이 들어 있습니다. NexysBus 0.4에서 저장한 ARINC 664 프로젝트(<code>.nxb</code>)도 그대로 열립니다.</p></div>` },
  { id: 'icd', label: 'ICD', html: () => `
    <h3>ICD — 가상 링크 (허브 스키마 1.1)</h3>
    <p>VL(vl_id · BAG 1~128 ms의 2^k · Lmax 64~1518 B · 네트워크 A/B/AB) → 포트(sub-VL · sampling/queuing · IP/UDP · 페이로드 크기) → 파라미터(바이트 offset · u8~f64 · endian · scale · offset_val · unit). CSV/XLSX는 파라미터당 1행(열 이름 자동 인식). 표에서 바로 고치면 검증 줄이 error/warning/info를 보여 줍니다. EXPORT CSV로 내보냅니다.</p>` },
  { id: 'simulate', label: 'SIMULATE', html: () => `
    <h3>SIMULATE — 송신</h3>
    <p>VL마다 BAG마다 한 프레임, 서브 VL 라운드로빈(큐잉 포트의 큰 메시지는 IP 단편으로 한 조각씩), 이중화 VL은 A·B 동시, SN 자동(시작 0, 이후 1~255). RUN 표의 MEAN/MIN/MAX는 실제 송신 간격, JITTER는 예정 시각 대비 편차입니다.</p>
    <p><b>SENDER FAULTS</b>: SN 건너뜀 · A/B 누락 · Lmax 초과 · BAG 위반(0.1 ms 뒤 추가 프레임) · 단편 누락. 10 s BATCH는 가상 시계라 프레임 수가 정확히 ⌈10 s / BAG⌉입니다.</p>` },
  { id: 'analyze', label: 'ANALYZE', html: () => `
    <h3>ANALYZE — 캡처와 검사</h3>
    <p>무결성: SN 0 = 리셋, 다음 = 이전+1(1~255 순환), 1~127 앞서면 그만큼 손실, 그 외 역순·중복(폐기). BAG: 같은 네트워크 간격 &lt; BAG − Jmax(0.5 ms). Lmax: FCS 포함 길이 &gt; Lmax. RM: VL·SN이 같은 A/B 사본 중 먼저 온 유효 프레임 채택, 다른 쪽은 중복이며 그 지연이 스큐, 짝이 창(20 ms) 안에 없으면 그 네트워크 누락. REASM: 단편 메시지 조각 누락·역순. SUB-VL ORDER: 서브 VL 라운드로빈 순서 깨짐.</p>
    <p><b>VL FROM</b>: 표준 MAC이 아닌 프로그램은 + IP 224.224.x.x · + ICD UDP ports · UDP MAP(포트=VL)으로 VL을 알아냅니다. <b>● LISTEN</b>: 받기만. 행을 고르면 디코드, 파라미터의 ▸ trend로 추이 차트(SEND TO VIEW로 Nexys VIEW에 CSV 전달).</p>` },
  { id: 'interop', label: 'INTEROP', html: () => `
    <h3>INTEROP — 상호운용 체크리스트</h3>
    <div class="help-card"><b>상대 → NexysNet</b><p>캡처를 열면 VL 수신 · ICD 밖 VL · SN · BAG · Lmax · A/B · 스큐 · 디코드 항목이 PASS/FAIL로 판정됩니다. FAIL의 EVIDENCE 링크 = 근거 프레임(ANALYZE로 이동, Wireshark No.와 같음).</p></div>
    <div class="help-card"><b>NexysNet → 상대</b><p>보낸 프레임 수 옆에 상대가 보고한 수신 수를 적거나 IMPORT PEER CSV…로 가져오면 판정합니다.</p></div>
    <div class="help-card"><b>보고서 · Wireshark</b><p>EXPORT REPORT…로 interop-report.md. WIRESHARK PROFILE…: 이 ICD의 Wireshark 열·색·파라미터 디코드(Lua 디섹터 + 프로필). TCP 링크 관찰은 LinkTap으로.</p></div>` },
  { id: 'keys', label: '단축키', html: () => `
    <h3>키보드 단축키</h3>
    <table class="help-keys">${[['PROJECT / ANALYZE / SIMULATE / ICD / SETTINGS / INTEROP', 'Ctrl + 1 · 2 · 3 · 4 · 5 · 6'], ['열기 (프로젝트 · 캡처 · ICD)', 'Ctrl + O'], ['프로젝트 저장', 'Ctrl + S'], ['테마 / 플롯 밝기', 'Ctrl + T / Ctrl + P'], ['도움말 / 닫기', 'F1 / Esc']].map(([a, b]) => `<tr><td>${a}</td><td><kbd>${b}</kbd></td></tr>`).join('')}</table>` },
  { id: 'tips', label: '활용 팁', html: () => `
    <h3>활용 팁</h3>
    <div class="help-card"><b>두 인스턴스</b><p>NexysNet 창 두 개를 띄우고 둘 다 Shared virtual network를 고른 뒤, 한쪽은 SIMULATE › START, 다른 쪽은 ANALYZE › LISTEN. 상대 장비 없이 송수신·보고서 흐름을 연습할 수 있습니다.</p></div>
    <div class="help-card"><b>Wi-Fi 포트</b><p>무선 어댑터 드라이버는 원시 이더넷 프레임 송신을 거절하는 경우가 많습니다(RUN 표에 송신 오류로 표시). 송신 시험은 유선 포트로 하세요.</p></div>
    <div class="help-card"><b>파일 드롭</b><p>.nxn · .pcapng/.pcap · ICD(.csv/.xlsx/.json)를 창 어디에나 끌어다 놓으면 알맞은 레일로 열립니다.</p></div>` },
];

export const help = new HelpFrame(SECTIONS, 'NexysNet');
