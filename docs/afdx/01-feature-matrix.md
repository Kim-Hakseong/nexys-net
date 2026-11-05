# ARINC 664 (AFDX) — 기능 매트릭스와 정답지 코퍼스 (RUN-AFDX1 A1, RUN-AFDX2 갱신)

작성 2026-11-05. 코퍼스 생성기 `work/afdx/gen_corpus.py`(stdlib만), 산출 `work/afdx/corpus/`(pcapng 5 + truth JSON 5 + ICD CSV 5). 저장소 밖(개발 PC 전용).

## 1. 스펙 근거 — 확정(공개 자료) / UNVERIFIED

| 항목 | 내용 | 등급 | 구현 위치 |
|---|---|---|---|
| VL 목적지 MAC | `03:00:00:00:vv:vv` (하위 16비트 = VL id) | 확정(공개) | `AfdxCapture.Index`, `AfdxFrame.Build` |
| SN 트레일러 | UDP 페이로드 뒤 1바이트, 프레임 마지막 바이트(FCS 앞). 0 = 리셋 후 첫 프레임, 이후 1→255→1 순환 | 확정(공개) | `AfdxAnalysis.Run` 무결성 |
| BAG | 1·2·4·…·128 ms (2^k) | 확정(공개) | `AfdxIcd.IsPow2Bag`, 검증 |
| 프레임 크기 | 64…1518 B (FCS 포함), UDP 페이로드 17…1471 B, 17 B 미만은 패딩 | 확정(공개) | `AfdxIcd.Overhead`=47, `AfdxFrame.Build` |
| A/B 이중화 · RM | 같은 프레임이 A·B 두 네트워크로, 수신 측은 VL·SN이 같은 사본 중 먼저 온 유효 프레임 채택("first valid wins") | 확정(공개) | `AfdxAnalysis` RM |
| 포트 종류 | sampling / queuing | 확정(공개) | ICD `port_type` |
| Jmax 0.5 ms | 송신 지터 상한(BAG 검사 허용치) | 확정(공개 수치), 상대 장비 설정은 UNVERIFIED | `AfdxCheckOptions.JmaxMs` |
| 출발지 MAC 인터페이스 id | `02:00:00:ee:ee:(id<<5)`, id 1 = A, 2 = B | **UNVERIFIED**(상대 장비가 실제로 세팅하는지) | 네트워크 판정 2순위 |
| 무결성 검사 허용 범위 | 이 구현: 정확히 +1만 정상, +2…+128 = 그만큼 손실(프레임은 채택), 그 외 역순·중복(폐기). 표준의 "PSN+1 또는 PSN+2 허용" 모드와의 일치 여부 | **UNVERIFIED** | `AfdxAnalysis` |
| IP 주소 체계 | 목적지 224.224.vv.vv(멀티캐스트), 출발지 10.x.x.x | **UNVERIFIED**(생성기·송신기 기본값일 뿐, 판정에 쓰지 않음) | `AfdxFrame`, ICD `ip_dst` |
| 스큐 한계 | 10 ms 기본값(측정만, 판정 항목은 설정값) | **UNVERIFIED**(실 ES는 VL별 SkewMax) | `SkewMaxMs` |
| RM 짝 창 | 20 ms(이 안에 다른 네트워크 사본이 없으면 누락) | 구현 선택 | `PairWindowMs` |
| 큐잉 포트 IP 단편화·재조립 | 단편 데이터 = ⌊(Lmax − 4 − 14 − 20 − 1) / 8⌋ × 8 B, UDP 헤더는 첫 단편, 단편마다 SN 1개, 메시지 ≤ 8192 B(구현 상한). 재조립은 채택 프레임을 VL별 FIFO로(다른 IP id가 오면 열린 메시지 닫힘) | 구현(RUN-AFDX2) · 코퍼스 c06 진실 일치 · **tshark 재조립 수와 일치**(VL 500 134 · 501 49 · 502 100 · 503 50). 상대 장비의 단편 크기·메시지 상한은 **UNVERIFIED** | `AfdxFrame.BuildFragment`, `AfdxAnalysis.AfterAccept` |
| 서브 VL 송신 순서 | 슬롯마다 다음 서브 VL(오름차순) 큐에서 1 프레임, 빈 큐는 그 서브 VL의 다음 포트 메시지로 채움. 순서 깨짐 = 같은 서브 VL 연속 + 직전 한 바퀴 안에 다른 서브 VL 있음 | 구현(RUN-AFDX2) · 코퍼스 c07 진실 일치. 상대 ES의 서브 VL 스케줄 방식은 **UNVERIFIED**(유휴 서브 VL 판단은 휴리스틱) | `AfdxSender.Refill`, `SubVlStats` |
| VL 식별 폴백 | 목적지 IP 224.224.x.x → VL x.x, ICD 포트 ip_dst:udp_dst → VL, UDP 목적지 포트 → VL(맵), SN 바이트 유무 선택 | 구현(RUN-AFDX2, 프로젝트 설정 `vlFromIp`·`vlFromIcd`·`udpVls`·`udpSn`) · 코퍼스 c08. 팀 프로그램이 실제로 어떤 MAC/IP를 쓰는지는 **UNVERIFIED**(RUN-AFDX3) | `AfdxVlRule` |

## 2. 검사 규칙(코퍼스 진실과 분석기가 같은 정의를 독립 구현)

- **무결성**(VL·네트워크별): 첫 프레임 기준. SN 0 = 리셋(+1). 그 외 `delta = (sn − 기대값) mod 255`(기대값 = 이전이 0 또는 255면 1, 아니면 이전+1): 0 정상, 1…127 = 그만큼 손실(SN GAP), 그 외 = 역순·중복(SN REV, 폐기, 이전값 유지).
- **BAG**: 같은 네트워크 연속 프레임 간격 < BAG − Jmax → 위반(BAG VIOL). 무효 프레임 포함 모든 수신 프레임 기준.
- **Lmax**: 와이어 길이(캡처 길이 + FCS 4) > Lmax → 위반(네트워크별 프레임마다).
- **RM**: VL·SN 짝. 먼저 온 유효 사본 = 채택, 다른 네트워크의 같은 SN = 중복(RM DUP), 그 시간차 = 스큐. 먼저 온 사본이 무결성 실패면 늦게 온 유효 사본이 채택. 이중화 VL(ICD AB, ICD 없으면 양쪽에서 본 VL)에서 짝이 20 ms 안에 안 오면 그 네트워크 누락(MISSING).
- **진실 계산**: 생성기가 네트워크별 수열·시각에서 위 규칙으로 계산하고, 주입 설계값과 `assert`로 대조(설계와 규칙이 다르면 생성 실패). 스왑 주입 VL은 스큐 진실 생략(스왑된 SN은 한 BAG 떨어진 사본끼리 짝이 됨 — 분석기 단위 테스트가 4300 µs로 고정).

## 3. 코퍼스

| 파일 | 프레임 | VL | BAG ms | 네트워크 | ARP/ICMP | 스큐 µs | 주입 오류 |
|---|---|---|---|---|---|---|---|
| c06_frag (RUN-AFDX2) | 3,112 | 4 | 2·4·8·4 | AB 3 · A 1 | 0/0 | 200 … 400 | 큐잉 메시지 3000·5000·2000·1500 B(Lmax 600 VL 포함), 가운데 단편 누락 2(VL 501), 단편 역순 1(VL 502) |
| c07_subvl (RUN-AFDX2) | 5,000 | 2 | 1·2 | AB · A | 0/0 | 150 | 서브 VL 4개 라운드로빈 순서 깨짐 3(VL 600), 서브 VL 3개 정상(VL 601) |
| c08_ipvl (RUN-AFDX2) | 475 | 3 | 4·8·(10) | A | 0/0 | — | 표준 VL 10 · IP 멀티캐스트 MAC + 224.224.0.7(VL 7) · 유니캐스트 UDP 포트 5000(SN 없음 → 맵으로 VL 900). 진실의 `vl_rule`로 열어 대조 |
| c01_vl1 | 2,000 | 1 | 2 | AB 1 | 0/0 | 100 | — |
| c02_vl8 | 19,920 | 8 | 1·2·4·8·16·32·64·128 | AB 8 | 0/0 | 100 … 10,000 | — (서브 VL 1~4, 페이로드 5(→17 패딩)·17·33·64·200·512·1000·1471, sampling/queuing 교대) |
| c03_vl64 | 21,169 | 64 | 4 … 128 | AB 32 · A 16 · B 16 | 20/30 | 200 … 810 | — |
| c04_errors | 18,508 | 8 | 2·4·8·16 | AB 8 | 4/4 | 300 | SN 건너뜀 2 · A만 SN 스왑 1 · BAG 위반(0.1 ms 뒤 추가) 1 · Lmax 초과 1 · B 누락 2 · A 누락 1 · SN 리셋 1 · A 중복 1 |
| c05_wrap | 10,512 | 3 | 1·2 | AB · A · B | 6/6 | 9,000 | — (BAG 1 ms × 3 s: SN 255→1 순환 11회 이상) |

pcapng: 인터페이스 2개(IDB 이름 "A"·"B", if_tsresol 9 = ns, if_fcslen 0), EPB. 페이로드 파라미터: `_CNT` u16 BE @0(프레임 카운터), `_SIN` f32 BE @2(45·sin(k/20)), `_RAMP` i32 LE @6(scale 0.01, offset −5, 단위 m). 진실 JSON: 파일 프레임 수·ARP·ICMP, VL별 네트워크별 프레임·SN GAP·REV·RESET·BAG 위반·누락, Lmax 위반, 채택 수, 스큐, 처음 3개 채택 프레임의 디코드 EU.

## 4. 결과(NexysBus 0.4.0-beta.1 개발 빌드)

| 관문 | 결과 | 근거 |
|---|---|---|
| 진실 항목 전부 | **2039 / 2039** (c01 28 · c02 197 · c03 1539 · c04 201 · c05 74) | `--afdx --corpus` gate `truth_corpus`, core 테스트 `CorpusMatchesTruth` 5/5 |
| 오류 검출(0이 아닌 주입 항목, 정확한 개수) | **15 / 15** | gate `error_detection` |
| 송신기 오류 주입 → 분석기 검출 | **48 / 48** 카운트 일치 | gate `sender_faults_detected` |
| Wireshark 상호 열기 | 사용자 확인: c02를 Wireshark가 정상으로 염(VL MAC 03:00:00:00:00:65 · IP 224.224.0.101 · UDP). RUN-AFDX2: tshark 4.6.9로 코퍼스 8개 + 사용자 실캡처 real1 교차 대조 **340 / 340**(프레임 · ARP · IPv4 · ICMP · UDP · TCP · VL별 A/B · 단편 · 재조립 수). NexysBus가 쓴 pcapng(batch · listen · send · 1M)를 capinfos가 오류 없이 읽음 | gate `wireshark_cross`, B16-1 종결 |
| 진실 항목 전부(RUN-AFDX2, 코퍼스 8개) | **2259 / 2259** (c06 107 · c07 61 · c08 52 추가), 오류 검출 **18 / 18** | gate `truth_corpus` · `error_detection`, core 테스트 8/8 |

## 5. 도구 부재 기록
- scapy 없음(패키지 설치 금지) → 자체 pcapng 라이터(Python `PcapNgWriter`, C# `PcapNgWriter`). 두 구현은 독립(같은 블록 정의만 공유).
- (RUN-AFDX1 시점) tshark·Wireshark 없음 → 교차검증 미실시. **RUN-AFDX2에서 해소**: Wireshark 4.6.9 + Npcap 1.88이 설치되어 `work/afdx/tshark_truth.py`로 교차 대조. 받아서 할 것: Wireshark 설치 PC에서 `tshark -r c02_vl8.pcapng -T fields -e frame.interface_id -e eth.dst -e udp.dstport | sort | uniq -c`와 NexysBus ANALYZE VL별 프레임 수 대조, NexysBus가 쓴 `*_batch.pcapng`를 Wireshark로 열어 오류 없이 열리는지.
- (RUN-AFDX1 시점) Npcap 없음 → 실제 NIC 경로 미검증. **RUN-AFDX2**: Npcap 1.88로 포트 열거 · 무차별 수신(Wi-Fi 실트래픽) · 시각 · 송신 오류 표시 · 라이브러리 내부 루프백 확인. Wi-Fi 드라이버는 원시 프레임 송신을 거절(`PacketSendPacket failed (31)`) → 선로 송신은 유선 포트 벤치(RUN-AFDX3).
