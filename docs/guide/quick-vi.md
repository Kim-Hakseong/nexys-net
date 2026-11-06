# 내 VI로 시험하기 — 실물 664 장비 없이 (빠른 시작)

**이런 경우에 씁니다**: 실물 ARINC 664 장비가 없고, 내 VI(계측 프로그램)가 UDP로 데이터를 보낼 때. VI가 보낸 데이터를 **NexysNet이 받아서 값으로 풀어 보여 주고**, 간격·오류를 검사합니다. PC 한 대로 됩니다.

```
 [   내 VI    ]  ──UDP──▶  (이 PC의 Wi-Fi 또는 이더넷 어댑터)  ──▶  [ NexysNet: 받기 · 값 보기 · 검사 ]
```

준비물: NexysNet(설치형 또는 포터블), **Npcap**(무료, [npcap.com](https://npcap.com)에서 받아 설치 — NexysNet에 들어 있지 않음), 네트워크에 연결된 Wi-Fi 또는 이더넷 하나.

---

## 1단계. ICD 파일 만들기 (VI가 보내는 데이터의 모양)

NexysNet은 **ICD**를 보고 "어느 포트로 오는 데이터의 몇 번째 바이트가 무슨 값인지"를 압니다. 예제 파일 [`examples/vi-example-icd.csv`](examples/vi-example-icd.csv)를 복사해서 내 VI에 맞게 고치세요(Excel로 열어도 됩니다). **파라미터 하나당 한 줄**입니다.

| 열 | 예제 값 | 뜻 — 내 VI에 맞게 |
|---|---|---|
| `vl_id` | 20 | VL 번호(아무 숫자, 0~65535) |
| `vl_name` | VI_DEMO | 이름 |
| `bag_ms` | 8 | VI가 보내는 **주기(ms)**. 1 · 2 · 4 · 8 · 16 · 32 · 64 · 128 중 하나 |
| `lmax` | 111 | 프레임 최대 길이 = **size + 47** (예: 64 + 47) |
| `network` | A | VI가 한 번만 보내면 **A** |
| `ip_dst` | 224.224.0.20 | VI가 보내는 **목적지 IP** |
| `udp_dst` | 30080 | VI가 보내는 **목적지 포트** |
| `size` | 64 | VI가 한 번에 보내는 **바이트 수** |
| `param` | ALTITUDE | 값 이름 |
| `offset` | 2 | 그 값이 시작하는 **바이트 위치**(0부터) |
| `type` | f32 | u8 · i8 · u16 · i16 · u32 · i32 · f32 · f64 … |
| `endian` | big | VI의 Flatten To String · Type Cast 기본은 **big** |
| `scale`, `offset_val`, `unit` | 1, 0, m | 값 = 원시값 × scale + offset_val |

예제가 뜻하는 VI 데이터(64바이트): `0~1` COUNTER(U16) · `2~5` ALTITUDE(SGL) · `6~7` TEMPERATURE(I16, ×0.1 °C) · 나머지 0.

## 2단계. VI 설정 (보내는 쪽)

| VI 함수 (Data Communication › Protocols › UDP 팔레트) | 입력 |
|---|---|
| **UDP Multicast Write-Only Open** (또는 UDP Multicast Open) | multicast address = `224.224.0.20` · port = `30080` · **net address = 이 PC의 Wi-Fi/이더넷 IP**(`String To IP`로 변환, cmd에서 `ipconfig`로 확인) · TTL = 1 |
| **UDP Write** | address = `224.224.0.20`, port = `30080`, data = 값들을 **Flatten To String**(big-endian)으로 이어 붙인 문자열 |
| 루프 | **Wait (ms) = BAG**(예: 8) |

> **net address를 꼭 지정하세요.** 비워 두면 Windows가 엉뚱한 어댑터(VPN · Hyper-V 등)로 내보내서 NexysNet이 못 볼 수 있습니다. 3단계 ③에서 고를 어댑터와 **같은 어댑터의 IP**여야 합니다.

## 3단계. NexysNet에서 받기 (빨간 박스대로)

**① 시작 화면에서 NEW PROJECT**

![1. NEW PROJECT](images/q1_start.png)

**② Ethernet ports (A / B) 클릭 → ③ NETWORK A = VI가 보내는 어댑터**(2단계 net address의 어댑터. 노트북이면 보통 Wi-Fi). NETWORK B는 비워 둡니다.

![2. 장치와 어댑터](images/q2_project.png)

**④ 왼쪽 레일 ICD → IMPORT… → 1단계의 CSV 선택**. VL 한 줄과 오른쪽 파라미터 표(OFFSET · TYPE · ENDIAN)가 VI와 같은지 확인합니다. 다르면 표에서 바로 고쳐도 됩니다.

![3. ICD 가져오기](images/q3_icd.png)

**⑤ 왼쪽 레일 ANALYZE → `+ ICD UDP ports` 체크 · ⑥ `SN byte` 체크 해제 · ⑦ ● LISTEN 누르고, 그다음 VI 실행**

![4. LISTEN](images/q4_listen.png)

**⑧ VI가 보낸 프레임이 실시간으로 쌓이고(VL 칸에 `20 UDP`), ⑨ 행을 누르면 오른쪽에 값(EU)이 보입니다.** VL 칸에 `20`을 넣으면 VI 데이터만 봅니다.

![5. 받기와 값](images/q5_receive.png)

**⑩ 값 옆의 ▸ trend = 그래프 · ⑪ ■ STOP = 받은 것을 캡처 파일(.pcapng)로 저장**(Wireshark로도 열림)

![6. 그래프와 STOP](images/q6_trend_stop.png)

**⑫ CHECKS PER VL — VL별 받은 수 · 평균 간격 · 오류**

![7. 검사](images/q7_checks.png)

- `ACCEPTED` = 받은 개수, `MEAN` 간격이 BAG(예: 8 ms)과 같으면 VI 주기가 맞는 것.
- `BAG VIOL`(빨간 숫자)은 **VI가 BAG − 0.5 ms보다 빨리 보낸 횟수**입니다. Windows에서 도는 VI 루프는 몇 ms씩 흔들리므로 흔히 나옵니다 — 값 확인이 목적이면 무시해도 되고, 주기 정확도를 보려면 이 숫자를 줄이는 게 목표입니다. (그림은 VI 대신 시험용 스크립트로 보낸 결과: 7.5 초에 1,459개, 평균 8.000 ms.)
- 프로젝트는 상단 **SAVE**로 `.nxn`에 저장 — 다음엔 열고 ⑦부터 하면 됩니다.

---

## 안 될 때

| 증상 | 확인 |
|---|---|
| FRAMES에 아무것도 안 쌓임 | ③의 어댑터와 VI의 **net address가 같은 어댑터**인지 · Wi-Fi/이더넷이 **연결된 상태**인지 · Npcap 설치 후 재부팅 · 목록에 `OTHERIP`만 보이면 VI가 다른 목적지로 보내는 중(DESTINATION 칸 확인) |
| 쌓이는데 VL 칸이 비어 있음 | ⑤ `+ ICD UDP ports` 체크 · ICD의 `ip_dst` · `udp_dst`가 VI와 정확히 같은지(DESTINATION 칸과 비교) |
| 값이 이상함(엄청 크거나 NaN) | ICD의 `offset` · `type` · **`endian`** — VI에서 Flatten To String의 byte order를 바꿨다면 little |
| `MISSING A/B`가 계속 늘어남 | ICD `network`가 AB면 B 사본을 기다립니다 → **A**로 |
| `SN GAP`이 보임 | ⑥ `SN byte` 체크 해제 |

## 할 수 있는 것 / 아직 안 되는 것

- ✅ **VI → NexysNet**: 받기 · 값 디코드 · 그래프 · 간격/누락 검사 · 캡처 저장 · INTEROP 보고서(EXPORT REPORT…).
- ⏳ **NexysNet → VI**: 아직 안 됩니다. NexysNet은 실물 AFDX 장비처럼 `03:00:00:00:…` 주소로 보내는데, VI의 UDP Read(일반 UDP 소켓)는 이 프레임을 받지 못합니다. "일반 UDP 멀티캐스트로 보내기" 모드는 다음 버전 계획에 있습니다.
- ℹ️ 같은 PC에서 `127.0.0.1`(localhost)로 보내는 VI는 지금은 받지 못합니다 — 2단계처럼 Wi-Fi/이더넷 어댑터로 보내세요.

VI 없이 이 흐름을 먼저 연습하려면: [`examples/vi_sim_sender.py`](examples/vi_sim_sender.py) — Python이 있으면 `python vi_sim_sender.py <이 PC의 IP> 60`으로 예제 ICD와 같은 데이터를 60초 보냅니다(위 그림들이 이것으로 찍은 것).

더 자세한 화면·검사 규칙은 [자세한 설명서](reference.md).
