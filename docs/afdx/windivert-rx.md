# 설치 없는 수신 전용 모드 — WinDivert로 AFDX를 받을 수 있는가 (RUN-AFDX2 백로그 조사, 2026-11-05)

코드 변경 없음. 결론은 **실측 전 UNVERIFIED**이고, 이 문서는 실측 절차와 판정 기준을 정합니다.

## 0. 질문
Npcap은 사용자가 따로 설치해야 합니다(동봉·재배포 금지). LinkTap처럼 **WinDivert**(LGPL-3.0 / GPL-2.0 이중 라이선스, DLL + 서명된 커널 드라이버 `WinDivert64.sys`를 앱 폴더에 함께 두고 처음 열 때 드라이버를 올리는 방식)를 동봉하면, 별도 설치 없이 **UDP 224.224.x.x를 IP 계층에서 받는 수신 전용 모드**가 되는가?

## 1. 구조상 사실 (확정 = 공개 문서·설계로 판단 가능)
| # | 사실 | 결과 |
|---|---|---|
| F1 | WinDivert는 WFP(Windows Filtering Platform) 콜아웃으로 **IP 패킷**을 봅니다(NETWORK 계층: 이더넷 헤더 없음, `WINDIVERT_ADDRESS`에 인터페이스 번호 `IfIdx`·`SubIfIdx`, 방향, 타임스탬프(QPC)). | 목적지 MAC `03:00:00:00:vv:vv`를 볼 수 없습니다 → VL은 **목적지 IP 224.224.x.x 폴백**(RUN-AFDX2에서 구현한 `VL FROM › + IP`)으로만 알 수 있습니다. 네트워크 A/B는 MAC 인터페이스 id가 없으므로 **NIC(IfIdx)로만** 나눕니다(포트 2개 필요). |
| F2 | SN 트레일러는 IP 데이터그램 **뒤**(IP total length 밖)에 있습니다. | IP 계층에서는 이더넷 트레일러가 잘려 **SN을 못 봅니다**(UNVERIFIED: WFP가 넘겨주는 버퍼에 트레일러가 남는지 — 남지 않는다고 가정). → 무결성(SN) 검사 · 이중화(RM, VL·SN 짝)를 할 수 없고, 수 · 간격(BAG) · Lmax(IP 길이 + 이더넷 오버헤드 추정) · 디코드만 가능. |
| F3 | NIC 하드웨어 멀티캐스트 필터: NIC는 **자기 MAC, 브로드캐스트, 등록된 멀티캐스트 MAC**만 위로 올립니다(무차별 모드 · all-multicast가 아니면). | AFDX 목적지 MAC `03:00:00:00:xx:xx`는 IP 멀티캐스트 대응 MAC(`01:00:5e:60:xx:xx`)이 **아닙니다**. 소켓으로 224.224.x.x 그룹에 가입해도 등록되는 것은 `01:00:5e:…`라 **표준 AFDX 프레임은 NIC에서 버려질 가능성이 큽니다**. WinDivert는 NDIS 위(WFP)라 NIC를 무차별 모드로 바꿀 수 없습니다(Npcap은 NDIS 필터라 가능). |
| F4 | 프레임이 NIC를 통과해도 Windows TCP/IP는 목적지 MAC이 자기 것 · 브로드캐스트 · 가입한 멀티캐스트가 아니면 IP 처리 전에 버립니다(일반적인 수신 경로; UNVERIFIED for Windows 11 26H2). | WFP 인바운드 IP 계층에 도달하지 못하면 WinDivert도 못 봅니다. |
| F5 | 팀 프로그램이 표준 AFDX MAC 대신 IP 멀티캐스트 MAC(`01:00:5e:60:xx:xx`)으로 보내는 경우(RUN-AFDX2 c08 시나리오) | 소켓 그룹 가입(IP_ADD_MEMBERSHIP 224.224.x.x)을 함께 하면 NIC 필터를 통과하고 IP 계층까지 올라옵니다 → WinDivert(또는 그냥 UDP 소켓)로 받을 수 있을 가능성이 큽니다. |
| F6 | 라이선스 | LGPL-3.0: 수정하지 않은 `WinDivert.dll`/`.sys`를 별도 파일로 동봉하고 고지·소스 위치를 알리면 됩니다(동적 링크). 드라이버는 저자 서명. 관리자 권한이 있어야 드라이버가 올라갑니다 → "설치 없음"이지 "권한 없음"은 아님. |

요약: **표준 AFDX(03:00:00:00 MAC)는 WinDivert 단독으로는 거의 확실히 못 받습니다**(F3·F4). 받을 수 있는 경우는 (a) 프레임이 IP 멀티캐스트 MAC으로 오는 비표준 프로그램(F5), (b) 다른 무언가(예: 같은 PC의 Npcap 캡처)가 NIC를 무차별 모드로 둔 경우(그러면 Npcap이 이미 있는 셈). 그리고 받더라도 SN이 없어 무결성·이중화 검사는 불가(F2).

## 2. 실측 절차 (벤치: 송신 장비 1 + Windows PC 1, 유선 포트)
준비: 송신 측은 NexysBus SIMULATE(다른 PC, 유선) 또는 상대 장비. 수신 PC에는 Npcap을 **설치하지 않은 상태**와 **설치한 상태** 둘 다.

| 단계 | 설정 | 측정 | 기록 |
|---|---|---|---|
| W-1 기준 | 수신 PC에 Npcap 설치, Wireshark로 30 s 캡처 | VL별 프레임 수 N_ref(A/B) | `ref.pcapng` |
| W-2 표준 MAC, 그룹 가입 없음 | Npcap 제거(또는 NPF 서비스 중지), WinDivert `--sniff` 필터 `inbound and udp and ip.DstAddr >= 224.224.0.0 and ip.DstAddr <= 224.224.255.255` | 받은 패킷 수 N_wd, IfIdx 분포 | N_wd / N_ref |
| W-3 표준 MAC + 그룹 가입 | W-2 + 같은 프로세스에서 UDP 소켓으로 224.224.x.x(ICD의 모든 ip_dst) 가입 | N_wd | 가입이 NIC 필터에 영향 있는지 |
| W-4 무차별 모드 | W-2 + NIC 고급 설정의 "Promiscuous" 류 옵션(있는 경우) 또는 Npcap 캡처를 동시에 실행 | N_wd | NIC 필터가 원인인지 확인 |
| W-5 IP 멀티캐스트 MAC | 송신 측이 `01:00:5e:60:xx:xx`로 보내도록(NexysBus 송신기 옵션 추가 필요, 또는 상대 프로그램) + 그룹 가입 | N_wd, 간격 | 비표준 프로그램 대응 가능성 |
| W-6 트레일러 | W-4 또는 W-5에서 받은 패킷 버퍼의 길이 = IP total length인지, 그 뒤 1바이트가 오는지 | SN 가용 여부 | F2 확정/불일치 |
| W-7 시각 | W-5에서 `WINDIVERT_ADDRESS.Timestamp`(QPC)로 |간격 − BAG| p50/p99/max vs W-1(Npcap 드라이버 시각) | 편차 | 타이밍 검사 가능 여부 |

## 3. 판정 기준
| 판정 | 조건 |
|---|---|
| **채택(수신 전용 모드로 구현)** | W-2 또는 W-3에서 N_wd = N_ref(VL별, 차 0) **그리고** W-7 p99 ≤ 0.5 ms. → "설치 없는 수신 전용" 모드: VL = IP 폴백, A/B = NIC, SN·RM 검사는 W-6이 "가용"일 때만. |
| **조건부(비표준 프로그램 전용)** | W-2·W-3은 0이고 W-5만 N_wd = N_ref. → 팀 프로그램이 IP 멀티캐스트 MAC을 쓸 때만 쓰는 옵션으로, 가이드에 "표준 AFDX는 Npcap 필요" 명시. |
| **기각** | W-2·W-3·W-5 모두 N_wd < N_ref(또는 W-4에서만 받음). → Npcap 감지 방식 유지(현행), WinDivert 동봉 안 함. |

어느 경우든 **송신은 범위 밖**입니다(WinDivert 주입은 IP 패킷 단위라 AFDX MAC · SN 트레일러를 만들 수 없음).

## 4. 지금 판단 (UNVERIFIED, 근거 F1–F5)
표준 AFDX 벤치에서는 **기각** 쪽일 가능성이 높습니다. 실측은 RUN-AFDX3(LIG 연동 벤치, 유선 포트) W-1…W-7로 1회 합니다. 그 전에는 코드 변경 없음.
