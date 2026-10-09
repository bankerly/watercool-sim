#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lc_watch.py - 抓包/观测工具：连上 ControlCenterX 的本地 MQTT broker，把水冷相关主题
的原始报文全部打印出来，用来确认：
  1) broker 是否可达（服务是否在跑）；
  2) 真实服务上报的 BT_LC/Status 长什么样（真实的字段名/取值）；
  3) 界面点按钮时发出的 BT_LC/Control、LCHWOC/Control 内容。

用法：
  python lc_watch.py
  python lc_watch.py --all          # 订阅全部已知主题（会刷屏）
"""

import argparse
import json
import sys
import time

import gcu_broker
from mqtt_min import MqttClient

DEFAULT_TOPICS = [
    "BT_LC/Control", "BT_LC/Status",
    "LCHWOC/Control", "LCHWOC/Status",
    "Settings/DeviceSwitchItemStatus",
]

ALL_TOPICS = [
    "GPUDevice/Status", "GPUDeviceItem/Status", "Settings/DeviceSwitchItemStatus",
    "System/CpuInfo", "System/MemoryInfo", "System/GpuInfo", "System/NetworkInfo",
    "System/BatteryInfo", "System/DiskInfo", "System/StaticsData", "System/FanErrorInfo",
    "System/HardwareInfo", "System/FanInfo", "System/HwFuelGauge", "System/BatteryProtection",
    "Setting/Status", "Fan/Status", "Fan/Table", "WhisperMode/Status",
    "MyRgbLightbar/Status", "HidLightbar/Status", "HidLightbar_Logo/Status",
    "HidLightbar_Hinge/Status", "HidLightbar_Sync/Status", "Keyboard/Status", "Keyboard/Ctrl",
    "KeyboardSingleZone/Status", "Customize/Info", "Customize/SupportInfo", "Languages/Info",
    "OSD/Status", "Display/Status", "TouchPadWorkArea/Status", "GameProfile/Status",
    "BT_LC/Control", "BT_LC/Status", "LCHWOC/Control", "LCHWOC/Status",
    "Languages/AutoDetectStatus", "OTA/CCU6Control",
]


def main(argv=None):
    ap = argparse.ArgumentParser(description="观测 ControlCenterX 水冷相关 MQTT 报文")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=13688)
    ap.add_argument("--slot", type=int, default=6,
                    help="UWPClient_<N> 编号；官方界面用 5，观测时默认用 6")
    ap.add_argument("--user", default=None)
    ap.add_argument("--password", default=None)
    ap.add_argument("--client-id", default=None)
    ap.add_argument("--all", action="store_true", help="订阅全部已知主题")
    ap.add_argument("--raw", action="store_true", help="不格式化 JSON")
    args = ap.parse_args(argv)

    topics = ALL_TOPICS if args.all else DEFAULT_TOPICS

    def log(msg):
        print(msg, flush=True)

    def on_message(topic, payload, retain):
        text = payload.decode("utf-8", "replace")
        ts = time.strftime("%H:%M:%S")
        if not args.raw:
            try:
                text = json.dumps(json.loads(text), ensure_ascii=False, indent=2)
            except Exception:
                pass
        print("---- %s  %s%s ----" % (ts, topic, " [retain]" if retain else ""), flush=True)
        print(text, flush=True)

    try:
        if args.client_id or args.user or args.password:
            client = MqttClient(host=args.host, port=args.port, client_id=args.client_id,
                                username=args.user, password=args.password,
                                on_message=on_message, log=log)
            client.connect()
        else:
            client, slot = gcu_broker.connect_auto(args.host, args.port, prefer=args.slot,
                                                  on_message=on_message, log=log)
            print("已用 clientId=UWPClient_%d 接入" % slot)
    except Exception as exc:
        print("连不上 %s:%d -> %r" % (args.host, args.port, exc))
        print("提示：「机械革命控制中心 Service / GCUBridge」没在跑时，本机 13688 端口是空的；")
        print("      它在跑但拒绝连接，说明 clientId/用户名/密码的编号没对齐。")
        return 2
    client.subscribe([(t, 0) for t in topics])
    print("已订阅 %d 个主题，Ctrl+C 退出" % len(topics))
    try:
        client.loop(auto_reconnect=True)
    except KeyboardInterrupt:
        pass
    finally:
        client.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
