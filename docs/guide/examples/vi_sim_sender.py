# VI 대역(stand-in) — VI 없이 NexysNet 수신을 연습할 때만 씁니다.
# vi-example-icd.csv와 같은 형식으로 UDP를 보냅니다: 목적지 224.224.0.20:30080, 8 ms마다, 64바이트(빅 엔디언)
#   offset 0 COUNTER u16 · offset 2 ALTITUDE f32 [m] · offset 6 TEMPERATURE i16 × 0.1 [degC]
# 사용: python vi_sim_sender.py <이 PC의 Wi-Fi/이더넷 IP> [초]
import math, socket, struct, sys, time

ifip = sys.argv[1]; secs = float(sys.argv[2]) if len(sys.argv) > 2 else 60
s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(ifip))   # VI: UDP Multicast Open의 net address
s.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 1)
s.bind((ifip, 30080))
n = 0; t0 = time.perf_counter()
while time.perf_counter() - t0 < secs:
    alt = 1000 + 500 * math.sin(n * 0.008); temp = 20 + 5 * math.sin(n * 0.002)
    payload = struct.pack('>Hfh', n & 0xFFFF, alt, int(round(temp * 10))) + bytes(56)
    s.sendto(payload, ('224.224.0.20', 30080)); n += 1
    nxt = t0 + n * 0.008
    while time.perf_counter() < nxt: time.sleep(0.0005)
print('sent', n)
