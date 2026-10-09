#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
gcu_broker.py - GCUBridge (机械革命控制中心 Service) 的 MQTT 连接规则

运行时探测结论（2026-10-09 实测本机 GCUBridge.exe 监听 127.0.0.1:13688）：
    clientId = UWPClient_<N>
    username = UWPClient_User_<N>
    password = UWPClient_Pwd888881772688_<N>      三者编号必须一致，否则：
        - 编号不一致 / 其它 clientId  -> CONNACK 2 (IdentifierRejected)
        - 编号一致但密码错            -> CONNACK 4 (BadUserNameOrPassword)
    实测 N = 1..12 全部可用；官方界面固定用 N = 5（UWPClient_5）。
    协议：MQTT 3.1.1（protocol name "MQTT", level 4）。

因此第三方程序要挂上这个 broker，必须换一个不冲突的编号（默认 6），
否则用 5 会把官方界面的连接顶掉。
"""

from mqtt_min import MqttClient

CLIENT_FMT = "UWPClient_%d"
USER_FMT = "UWPClient_User_%d"
PASS_FMT = "UWPClient_Pwd888881772688_%d"
OFFICIAL_SLOT = 5


def credentials(slot: int):
    return (CLIENT_FMT % slot, USER_FMT % slot, PASS_FMT % slot)


def make_client(host, port, slot, on_message=None, log=print, keepalive=30):
    cid, user, pwd = credentials(slot)
    return MqttClient(host=host, port=port, client_id=cid, username=user, password=pwd,
                      keepalive=keepalive, on_message=on_message, log=log)


def candidate_slots(prefer=6, count=15):
    """优先用 prefer，然后往后找空位，最后才试官方 5 号（会把界面踢下线）。"""
    order = []
    for n in range(prefer, prefer + count):
        if n != OFFICIAL_SLOT and n >= 1:
            order.append(n)
    for n in range(1, OFFICIAL_SLOT):
        order.append(n)
    order.append(OFFICIAL_SLOT)
    return order


def connect_auto(host, port, prefer=6, on_message=None, log=print, keepalive=30):
    """返回 (client, slot)。全部失败则抛最后一个异常。"""
    last = None
    tried = []
    for slot in candidate_slots(prefer):
        c = make_client(host, port, slot, on_message=on_message, log=log, keepalive=keepalive)
        try:
            c.connect()
            if tried:
                log("[gcu] 编号 %d 被拒(%s)，改用 %d" % (tried[-1][0], tried[-1][1], slot))
            if slot == OFFICIAL_SLOT:
                log("[gcu] 警告：正在使用官方界面的编号 5，界面连接会被顶掉并自动重连")
            return c, slot
        except Exception as exc:
            last = exc
            tried.append((slot, str(exc)))
            try:
                c.close()
            except Exception:
                pass
    raise last if last else RuntimeError("no slot available")
