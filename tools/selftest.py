#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
selftest.py - 离线自检：本机起一个极简 MQTT broker，把 lc_sim 接上去，
验证 1) 虚拟水冷坞会发布 connected=true 状态；2) 会响应 GETSTATUS；
3) 会压制真实服务上报的"未连接"；4) 泵档位指令生效。
不需要安装机械革命控制中心，纯协议层验证。

用法：python selftest.py
"""

import json
import socket
import struct
import sys
import threading
import time

import lc_sim
from mqtt_min import MqttClient, _encode_len, _encode_str

PORT = 13689
RESULT = []


# ----------------------------- 极简 MQTT broker -----------------------------
class MiniBroker(object):
    def __init__(self, port):
        self.port = port
        self.clients = []            # [(sock, subs:set)]
        self.retained = {}           # topic -> payload bytes
        self.lock = threading.Lock()
        self.srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(("127.0.0.1", port))
        self.srv.listen(8)
        self.srv.settimeout(1.0)

    def start(self):
        threading.Thread(target=self._accept_loop, daemon=True).start()

    def _accept_loop(self):
        while True:
            try:
                c, _ = self.srv.accept()
            except socket.timeout:
                continue
            except OSError:
                return
            with self.lock:
                self.clients.append([c, set()])
            threading.Thread(target=self._client_loop, args=(c,), daemon=True).start()

    @staticmethod
    def _read_pkt(c):
        hdr = c.recv(1)
        if not hdr:
            return None
        mult, length = 1, 0
        while True:
            b = c.recv(1)[0]
            length += (b & 127) * mult
            if not (b & 0x80):
                break
            mult *= 128
        body = b""
        while len(body) < length:
            chunk = c.recv(length - len(body))
            if not chunk:
                break
            body += chunk
        return (hdr[0] >> 4, hdr[0] & 0x0F, body)

    def _deliver(self, topic, payload, retain=False):
        out = []
        with self.lock:
            if retain:
                self.retained[topic] = payload
            for sock, subs in self.clients:
                if topic in subs:
                    out.append(sock)
        for sock in out:
            try:
                body = _encode_str(topic) + payload
                sock.sendall(bytes([0x30]) + _encode_len(len(body)) + body)
            except Exception:
                pass

    def _client_loop(self, c):
        sock = None
        try:
            while True:
                pkt = self._read_pkt(c)
                if pkt is None:
                    return
                cmd, flags, body = pkt
                if cmd == 1:      # CONNECT
                    try:
                        c.sendall(b"\x20\x02\x00\x00")
                    except Exception:
                        return
                elif cmd == 8:    # SUBSCRIBE
                    pid = body[0:2]
                    idx, granted = 2, b""
                    while idx < len(body):
                        tl = struct.unpack(">H", body[idx:idx + 2])[0]
                        topic = body[idx + 2:idx + 2 + tl].decode()
                        idx += 2 + tl + 1
                        granted += b"\x00"
                        with self.lock:
                            for entry in self.clients:
                                if entry[0] is c:
                                    entry[1].add(topic)
                        sock = c
                    c.sendall(bytes([0x90]) + _encode_len(2 + len(granted)) + pid + granted)
                    with self.lock:
                        retained = [(t, p) for t, p in self.retained.items()]
                    for t, p in retained:
                        with self.lock:
                            mine = any(e[0] is c and t in e[1] for e in self.clients)
                        if mine:
                            bb = _encode_str(t) + p
                            c.sendall(bytes([0x31]) + _encode_len(len(bb)) + bb)
                elif cmd == 3:    # PUBLISH
                    qos = (flags >> 1) & 3
                    retain = bool(flags & 1)
                    tl = struct.unpack(">H", body[0:2])[0]
                    topic = body[2:2 + tl].decode()
                    idx = 2 + tl
                    if qos > 0:
                        pid = body[idx:idx + 2]
                        idx += 2
                        if qos == 1:
                            c.sendall(b"\x40\x02" + pid)
                        elif qos == 2:
                            c.sendall(b"\x50\x02" + pid)   # PUBREC
                    self._deliver(topic, body[idx:], retain)
                elif cmd == 12:   # PINGREQ
                    c.sendall(b"\xD0\x00")
                elif cmd == 14:   # DISCONNECT
                    return
        except Exception:
            return
        finally:
            with self.lock:
                self.clients = [e for e in self.clients if e[0] is not c]
            try:
                c.close()
            except Exception:
                pass


# ------------------------------- 自检用例 -----------------------------------
def main():
    broker = MiniBroker(PORT)
    broker.start()
    time.sleep(0.3)
    print("[selftest] 本地测试 broker 已在 127.0.0.1:%d 启动" % PORT)

    # 后台跑虚拟水冷坞，指向测试 broker
    sim_args = ["--host", "127.0.0.1", "--port", str(PORT), "--interval", "0.5"]
    threading.Thread(target=lc_sim.main, args=(sim_args,), daemon=True).start()
    time.sleep(0.8)

    got = []          # [(topic, payload dict)]
    def on_message(topic, payload, retain):
        try:
            got.append((topic, json.loads(payload.decode("utf-8", "replace"))))
        except Exception:
            got.append((topic, {}))

    test = MqttClient(host="127.0.0.1", port=PORT, client_id="selftest_client",
                      on_message=on_message, log=lambda m: None)
    test.connect()
    test.subscribe([("BT_LC/Status", 0), ("LCHWOC/Status", 0)])
    threading.Thread(target=test.loop, kwargs={"auto_reconnect": False}, daemon=True).start()
    time.sleep(1.2)

    def wait_for(pred, timeout=3.0):
        end = time.time() + timeout
        while time.time() < end:
            for t, obj in list(got):
                if pred(t, obj):
                    return obj
            time.sleep(0.05)
        return None

    # 1) 启动后应主动发布 connected=true
    st = wait_for(lambda t, o: t == "BT_LC/Status" and o.get("connected") is True and o.get("src") == "lc_sim")
    RESULT.append(("启动即上报水冷已连接 (BT_LC/Status connected=true)", bool(st)))

    # 2) LCHWOC/Status 三项都为真
    hw = wait_for(lambda t, o: t == "LCHWOC/Status" and o.get("Enable") and o.get("Connected") and o.get("Support"))
    RESULT.append(("HWOC 能力/开关状态均为真 (LCHWOC/Status)", bool(hw)))

    # 3) 界面发 GETSTATUS，虚拟设备应立刻回状态
    got.clear()
    test.publish("BT_LC/Control", {"Action": "GETSTATUS"})
    st = wait_for(lambda t, o: t == "BT_LC/Status" and o.get("connected") is True)
    RESULT.append(("响应界面握手 GETSTATUS -> 立即回状态", bool(st)))

    # 4) 泵档位指令
    got.clear()
    test.publish("BT_LC/Control", {"Action": "LC_PumpCtrl", "PumpCtrl": "2"})
    st = wait_for(lambda t, o: t == "BT_LC/Status" and o.get("PumpDuty") == 90)
    RESULT.append(("LC_PumpCtrl 档位2 -> PumpDuty=90%", bool(st)))

    # 5) 压制真实服务上报的"未连接"
    got.clear()
    test.publish("BT_LC/Status", {"connected": False, "connectString": "Disconnected"})
    st = wait_for(lambda t, o: t == "BT_LC/Status" and o.get("src") == "lc_sim" and o.get("connected") is True)
    RESULT.append(("真实服务上报未连接 -> 立即压制回 connected=true", bool(st)))

    # 6) 收到的报文能被界面模型正确反序列化（字段完备性）
    st = wait_for(lambda t, o: t == "BT_LC/Status" and o.get("src") == "lc_sim")
    need = ["connected", "ConnectString", "PumpDuty", "FanDuty", "DevMACString", "DevFWVersion",
            "DeviceMacList", "LCLED_Mode", "LCFanLED_Mode", "LC_action", "LC_InputWater",
            "LC_PumpCtrl", "LC_FanCtrl", "LC_MeterNormal", "AutoConnect"]
    missing = [k for k in need if st is None or k not in st]
    RESULT.append(("状态报文包含界面 Fan_BT_LC 所需的全部字段", not missing))

    print("")
    ok = True
    for name, passed in RESULT:
        print("  [%s] %s" % ("PASS" if passed else "FAIL", name))
        ok = ok and passed
    print("")
    print("总结果: %s" % ("全部通过" if ok else "存在失败项"))
    if missing:
        print("缺失字段: %s" % missing)
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
