#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
mqtt_min.py - 纯标准库实现的极简 MQTT 3.1.1 客户端（CONNECT/SUBSCRIBE/PUBLISH/PING）。
不依赖 paho-mqtt，方便在任何装了 Python 的机器上直接跑。
只实现本任务需要的部分：QoS0/1 订阅、QoS0 发布(可带 retain)、自动重连。
"""

import socket
import struct
import threading
import time


def _encode_len(n: int) -> bytes:
    out = bytearray()
    while True:
        b = n % 128
        n //= 128
        if n:
            b |= 0x80
        out.append(b)
        if not n:
            return bytes(out)


def _encode_str(s) -> bytes:
    if isinstance(s, str):
        s = s.encode("utf-8")
    return struct.pack(">H", len(s)) + s


class MqttClient(object):
    def __init__(self, host="127.0.0.1", port=13688, client_id="py_client",
                 username=None, password=None, keepalive=30,
                 on_message=None, on_connect=None, log=print):
        self.host = host
        self.port = port
        self.client_id = client_id
        self.username = username
        self.password = password
        self.keepalive = keepalive
        self.on_message = on_message      # fn(topic:str, payload:bytes, retain:bool)
        self.on_connect = on_connect      # fn() -> None，重连成功后回调（用于重新订阅）
        self.log = log
        self._sock = None
        self._buf = b""
        self._send_lock = threading.Lock()
        self._pid = 0
        self._stop = threading.Event()
        self.subscriptions = []           # [(topic, qos)]

    # ---------------- 底层收发 ----------------
    def _send_raw(self, data: bytes):
        with self._send_lock:
            if self._sock is None:
                raise OSError("socket closed")
            self._sock.sendall(data)

    def _recv_exact(self, n: int) -> bytes:
        while len(self._buf) < n:
            chunk = self._sock.recv(4096)
            if not chunk:
                raise OSError("connection closed by broker")
            self._buf += chunk
        out, self._buf = self._buf[:n], self._buf[n:]
        return out

    def _next_packet(self, timeout=1.0):
        """返回 (cmd:int, flags:int, body:bytes)；超时返回 None。"""
        self._sock.settimeout(timeout)
        try:
            first = self._recv_exact(1)[0]
        except socket.timeout:
            return None
        mult, length = 1, 0
        while True:
            b = self._recv_exact(1)[0]
            length += (b & 127) * mult
            if not (b & 0x80):
                break
            mult *= 128
        body = self._recv_exact(length) if length else b""
        return (first >> 4, first & 0x0F, body)

    # ---------------- 协议动作 ----------------
    def connect(self):
        self._buf = b""
        self._sock = socket.create_connection((self.host, self.port), timeout=5.0)
        self._sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        flags = 0x02  # clean session
        payload = _encode_str(self.client_id)
        if self.username is not None:
            flags |= 0x80
            payload += _encode_str(self.username)
        if self.password is not None:
            flags |= 0x40
            payload += _encode_str(self.password)
        vh = _encode_str("MQTT") + bytes([4, flags]) + struct.pack(">H", self.keepalive)
        body = vh + payload
        self._send_raw(bytes([0x10]) + _encode_len(len(body)) + body)
        deadline = time.time() + 5.0
        while time.time() < deadline:
            pkt = self._next_packet(timeout=max(0.2, deadline - time.time()))
            if pkt is None:
                continue
            cmd, _flags, data = pkt
            if cmd == 2:  # CONNACK
                code = data[1] if len(data) > 1 else 255
                if code != 0:
                    raise OSError("CONNACK refused, return code = %d" % code)
                self.log("[mqtt] connected to %s:%d as %s" % (self.host, self.port, self.client_id))
                return True
        raise OSError("CONNACK timeout")

    def subscribe(self, topics):
        """topics: 字符串或 [(topic, qos), ...]"""
        if isinstance(topics, str):
            topics = [topics]
        pairs = []
        for t in topics:
            pairs.append((t, 0) if isinstance(t, str) else (t[0], t[1]))
        self.subscriptions = pairs
        self._pid = (self._pid + 1) & 0xFFFF or 1
        body = struct.pack(">H", self._pid)
        for topic, qos in pairs:
            body += _encode_str(topic) + bytes([qos])
        self._send_raw(bytes([0x82]) + _encode_len(len(body)) + body)
        self.log("[mqtt] SUBSCRIBE -> " + ", ".join(t for t, _ in pairs))

    def publish(self, topic, payload, retain=False, qos=0):
        if isinstance(payload, dict):
            import json
            payload = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
        if isinstance(payload, str):
            payload = payload.encode("utf-8")
        body = _encode_str(topic)
        if qos > 0:
            self._pid = (self._pid + 1) & 0xFFFF or 1
            body += struct.pack(">H", self._pid)
        body += payload
        first = 0x30 | (qos << 1) | (1 if retain else 0)
        self._send_raw(bytes([first]) + _encode_len(len(body)) + body)

    def _handle(self, cmd, flags, body):
        if cmd == 3:  # PUBLISH
            qos = (flags >> 1) & 0x03
            retain = bool(flags & 0x01)
            tlen = struct.unpack(">H", body[0:2])[0]
            topic = body[2:2 + tlen].decode("utf-8", "replace")
            idx = 2 + tlen
            if qos > 0:
                pid = body[idx:idx + 2]
                idx += 2
                if qos == 1:
                    self._send_raw(bytes([0x40, 0x02]) + pid)
            payload = body[idx:]
            if self.on_message:
                try:
                    self.on_message(topic, payload, retain)
                except Exception as exc:  # 回调异常不能打断连接
                    self.log("[mqtt] on_message error: %r" % (exc,))
        elif cmd == 4:  # PUBACK
            pass
        elif cmd == 9:  # SUBACK
            pass
        elif cmd == 13:  # PINGRESP
            pass

    def loop(self, auto_reconnect=True, reconnect_delay=3.0):
        """阻塞读循环 + 心跳 + 断线重连。"""
        last_ping = time.time()
        while not self._stop.is_set():
            try:
                pkt = self._next_packet(timeout=0.5)
                if pkt is not None:
                    self._handle(*pkt)
                now = time.time()
                if now - last_ping >= max(5, self.keepalive // 2):
                    self._send_raw(b"\xC0\x00")   # PINGREQ
                    last_ping = now
            except socket.timeout:
                continue
            except Exception as exc:
                if not auto_reconnect:
                    self.log("[mqtt] loop aborted: %r" % (exc,))
                    return
                self.log("[mqtt] connection lost (%r), reconnecting in %.1fs" % (exc, reconnect_delay))
                self.close()
                time.sleep(reconnect_delay)
                try:
                    self.connect()
                    if self.subscriptions:
                        self.subscribe(self.subscriptions)
                    if self.on_connect:
                        self.on_connect()
                    last_ping = time.time()
                except Exception as exc2:
                    self.log("[mqtt] reconnect failed: %r" % (exc2,))
                    time.sleep(reconnect_delay)

    def stop(self):
        self._stop.set()

    def close(self):
        try:
            if self._sock:
                self._sock.close()
        except Exception:
            pass
        self._sock = None
        self._buf = b""
