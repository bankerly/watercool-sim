#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
lc_sim.py - 虚拟水冷坞（让机械革命控制中心认为"水冷已连接"）

逆向结论（详见 README.md）：
  CCU.WinUI 自己并不扫蓝牙；它订阅本机 MQTT broker(127.0.0.1:13688) 的
      BT_LC/Status   <- 蓝牙水冷坞状态(JSON)
      LCHWOC/Status  <- 水冷硬件超频(HWOC)能力/开关状态(JSON)
  真实状态由后台服务「机械革命控制中心 Service」负责发布（它去连真水冷坞）。
  本程序冒充这个发布方：持续发布 connected=true 的状态；一旦监听到真实服务
  上报的"未连接"状态，立即重新压制回去（watchdog）。

用法：
  python lc_sim.py                # 常驻，Ctrl+C 退出
  python lc_sim.py --once         # 只发布一次状态后退出
  python lc_sim.py --dry-run      # 只打印将要发布的 JSON，不联网
  python lc_sim.py --interval 2   # 定时重发间隔(秒)，默认 1.0
  python lc_sim.py --no-suppress  # 不压制真实服务的上报（仅观察效果）
"""

import argparse
import json
import sys
import threading
import time

import gcu_broker
from mqtt_min import MqttClient

# ---- broker 参数（DataService/MQTTDataService.ClientConnection + 运行期探测）----
DEF_HOST = "127.0.0.1"
DEF_PORT = 13688
DEF_SLOT = 6      # UWPClient_6 / UWPClient_User_6 / UWPClient_Pwd888881772688_6
                  # 官方界面占用编号 5，别抢；gcu_broker 会在 6 起自动找空位

TOPIC_CTRL = "BT_LC/Control"
TOPIC_STATUS = "BT_LC/Status"
TOPIC_HWOC_CTRL = "LCHWOC/Control"
TOPIC_HWOC_STATUS = "LCHWOC/Status"

SELF_TAG = "lc_sim"                # 用来识别自己发的消息（避免被自己的回声触发循环压制）

# UI 里泵/风扇档位 -> 占空比的映射（LiquidCoolingSettingViewModel）
PUMP_DUTY = {0: 45, 1: 60, 2: 90}
FAN_DUTY = {0: 40, 1: 50, 2: 60, 3: 90}


def log(msg):
    print("[%s] %s" % (time.strftime("%H:%M:%S"), msg), flush=True)


class FakeDock(object):
    """模拟一台已连接的水冷坞。字段名与界面模型 WinUIGallery.Models.Fan_BT_LC 一一对应。"""

    def __init__(self, mac, fw, auto_reconnect_delay=3.0):
        self.mac = mac
        self.fw = fw                      # 必须 >= 22 字符，否则界面解析会抛异常
        self.device_list = [mac]
        self.auto_reconnect_delay = auto_reconnect_delay

        self.connected = True
        self.connect_string = "Connected"  # 必须正好是 "Connected" 才会置 IsLiquidCoolingConnected=true
        self.fan_duty = 50
        self.pump_duty = 60
        self.lc_fan_ctrl = 1
        self.lc_pump_ctrl = 1
        self.r, self.g, self.b = 255, 0, 0
        self.led_mode = 0                  # 0 常亮 / 1 呼吸 / 2 幻彩
        self.fanled_mode = 0               # 0 关 / 4 流转 / 5 彩虹
        self.input_water = False
        self.update_fw = False
        self.meter_normal = True
        self.lc_action = True              # true 才允许界面上的连接/断开按钮生效
        self.auto_connect = True

    # ---- 报文组装（键名与真实服务发布的一模一样，2026-10-09 抓包校准）----
    def status(self):
        return {
            "src": SELF_TAG,
            "connected": bool(self.connected),      # 只有这个键是精确小写（界面 dynamic 按小写取）
            "ConnectString": self.connect_string,   # 必须 "Connected" 才置 IsLiquidCoolingConnected
            "PumpDuty": self.pump_duty,
            "FanDuty": self.fan_duty,
            "LCLED_R": self.r,
            "LCLED_G": self.g,
            "LCLED_B": self.b,
            "LCLED_Mode": self.led_mode,
            "LCFanLED_Mode": self.fanled_mode,
            "LC_action": bool(self.lc_action),
            "LC_CoolingAuto": False,
            "LC_InputWater": bool(self.input_water),
            "LC_PumpCtrl": str(self.lc_pump_ctrl),
            "LC_FanCtrl": str(self.lc_fan_ctrl),
            "LC_UpdateFW": bool(self.update_fw),
            "DevFWVersion": self.fw,                # 界面会 Substring(0,22)，必须 >= 23 字符
            "DevMACString": self.mac,
            "DeviceMacList": list(self.device_list) or [""],
            "LC_MeterNormal": bool(self.meter_normal),
            "AutoConnect": bool(self.auto_connect),
        }

    def hwoc(self):
        # 键名与真实服务一致；界面用 (bool)val["Enable"/"Connected"/"Support"] 精确同名取值。
        return {
            "src": SELF_TAG,
            "Enable": bool(self.connected),
            "CoreFreqOffset": 0,
            "MemFreqOffset": 0,
            "Connected": bool(self.connected),
            "Support": True,
        }

    # ---- 处理界面发来的控制指令（BT_LC/Control）----
    def handle_control(self, payload):
        action = payload.get("Action") or payload.get("action") or ""
        action = str(action)
        if action == "GETSTATUS":
            return "GETSTATUS -> 上报状态"
        if action == "Connect":
            self.connected = True
            self.connect_string = "Connected"
            self.lc_action = True
            return "Connect -> 已连接"
        if action == "Disconnect":
            self.connected = False
            self.connect_string = "Disconnected"
            return "Disconnect -> 已断开(%.1fs 后自动重连)" % self.auto_reconnect_delay
        if action == "LC_PumpCtrl":
            idx = int(payload.get("PumpCtrl", 1) or 1)
            self.lc_pump_ctrl = idx
            self.pump_duty = PUMP_DUTY.get(idx, self.pump_duty)
            return "LC_PumpCtrl -> 档位 %d, 占空比 %d%%" % (idx, self.pump_duty)
        if action == "LC_FanCtrl":
            idx = int(payload.get("FanCtrl", 1) or 1)
            self.lc_fan_ctrl = idx
            self.fan_duty = FAN_DUTY.get(idx, self.fan_duty)
            return "LC_FanCtrl -> 档位 %d, 占空比 %d%%" % (idx, self.fan_duty)
        if action == "LEDControl":
            try:
                self.r = int(payload.get("LCLED_R", self.r))
                self.g = int(payload.get("LCLED_G", self.g))
                self.b = int(payload.get("LCLED_B", self.b))
            except Exception:
                pass
            return "LEDControl -> RGB(%d,%d,%d)" % (self.r, self.g, self.b)
        if action in ("LEDBreathing", "LEDColorful"):
            self.led_mode = int(payload.get("LedMode", 0) or 0)
            return "%s -> lcLed_Mode=%d" % (action, self.led_mode)
        if action == "FanLEDEffect":
            self.fanled_mode = int(payload.get("LedMode", 0) or 0)
            return "FanLEDEffect -> lcFanLED_Mode=%d" % self.fanled_mode
        if action == "InputWater":
            self.input_water = True
            return "InputWater -> 注水模式(模拟 5s)"
        if action == "DeviceMacSetting":
            self.mac = str(payload.get("DeviceMac") or self.mac)
            if self.mac not in self.device_list:
                self.device_list.append(self.mac)
            return "DeviceMacSetting -> %s" % self.mac
        if action == "ClearDevMAC":
            self.mac = ""
            self.device_list = []
            return "ClearDevMAC -> 已清空配对记录"
        if action == "Open_DFU_Folder":
            return "Open_DFU_Folder -> 忽略（模拟设备无 DFU）"
        return "未知 Action=%r，忽略" % action


def dump(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def ci(obj, name):
    """大小写不敏感取值（真实服务混用 PascalCase 和小写键）。"""
    for k in obj:
        if str(k).lower() == name.lower():
            return obj[k]
    return None


def main(argv=None):
    ap = argparse.ArgumentParser(description="虚拟水冷坞 / 模拟水冷已连接")
    ap.add_argument("--host", default=DEF_HOST)
    ap.add_argument("--port", type=int, default=DEF_PORT)
    ap.add_argument("--slot", type=int, default=DEF_SLOT,
                    help="UWPClient_<N> 编号（官方界面固定用 5，别抢；默认 6，被占会自动往后找）")
    ap.add_argument("--client-id", default=None, help="手动指定 clientId（用于非官方 broker）")
    ap.add_argument("--user", default=None, help="手动指定用户名（配合 --client-id）")
    ap.add_argument("--password", default=None, help="手动指定密码（配合 --client-id）")
    ap.add_argument("--mac", default="5C:8A:3B:00:1A:2F", help="伪装的水冷坞蓝牙 MAC")
    ap.add_argument("--fw", default="CoolingSystem LCT21001-SIM-v1.0.0", help="固件版本字符串(>=22 字符)")
    ap.add_argument("--interval", type=float, default=1.0, help="定时重发状态的间隔(秒)")
    ap.add_argument("--reconnect-delay", type=float, default=3.0,
                    help="界面点\"断开\"后自动重连的等待秒数")
    ap.add_argument("--once", action="store_true", help="发布一次状态后退出")
    ap.add_argument("--dry-run", action="store_true", help="只打印报文，不连网络")
    ap.add_argument("--no-suppress", action="store_true", help="不压制真实服务的上报")
    ap.add_argument("--offline", action="store_true",
                    help="反向操作：发布一次\u300c未连接\u300d状态并退出（清掉 broker 上残留的 retain）")
    ap.add_argument("--verbose", action="store_true", help="打印收到的所有消息")
    args = ap.parse_args(argv)

    if len(args.fw) < 23:
        log("警告：--fw 太短(%d 字符)，界面解析固件版本时会抛异常，建议 >=23 字符" % len(args.fw))

    dock = FakeDock(args.mac, args.fw, args.reconnect_delay)
    if args.offline:
        dock.connected = False
        dock.connect_string = "Disconnected"
        args.once = True

    if args.dry_run:
        log("BT_LC/Status  =" + dump(dock.status()))
        log("LCHWOC/Status =" + dump(dock.hwoc()))
        return 0

    stats = {"suppressed": 0, "sent": 0, "ctrl": 0}

    def publish_all(reason=""):
        try:
            client.publish(TOPIC_STATUS, dock.status(), retain=True)
            client.publish(TOPIC_HWOC_STATUS, dock.hwoc(), retain=True)
            stats["sent"] += 2
            if reason and args.verbose:
                log("publish (%s)" % reason)
        except Exception as exc:
            log("publish 失败: %r" % (exc,))

    def on_message(topic, payload, retain):
        try:
            text = payload.decode("utf-8", "replace")
            try:
                obj = json.loads(text)
            except Exception:
                obj = None
        except Exception:
            obj = None
        if args.verbose:
            log("< %s %s%s" % (topic, text[:400], " (retain)" if retain else ""))

        if topic == TOPIC_CTRL and isinstance(obj, dict):
            if str(obj.get("src")) == SELF_TAG:
                return
            stats["ctrl"] += 1
            log("控制指令: " + dock.handle_control(obj))
            publish_all("响应控制指令")
            if dock.connected is False:
                threading.Timer(dock.auto_reconnect_delay, _auto_reconnect).start()
            return

        if topic == TOPIC_HWOC_CTRL and isinstance(obj, dict):
            if str(obj.get("src")) == SELF_TAG:
                return
            log("HWOC 控制指令: %s" % dump(obj))
            publish_all("响应 HWOC 指令")
            return

        if topic in (TOPIC_STATUS, TOPIC_HWOC_STATUS) and isinstance(obj, dict):
            if str(obj.get("src")) == SELF_TAG:
                return                      # 自己的回声
            if args.no_suppress:
                return
            bad = False
            if topic == TOPIC_STATUS:
                bad = (ci(obj, "connected") is not True) or (str(ci(obj, "connectString")) != "Connected")
            else:
                bad = not (ci(obj, "Enable") and ci(obj, "Connected") and ci(obj, "Support"))
            if bad:
                stats["suppressed"] += 1
                log("检测到真实服务上报\"未连接\"状态 -> 立即压制(第 %d 次): %s" % (stats["suppressed"], dump(obj)))
                publish_all("压制真实上报")
            return

    def _auto_reconnect():
        if not dock.connected:
            dock.connected = True
            dock.connect_string = "Connected"
            log("自动重连: 水冷坞已重新连接")
            publish_all("自动重连")

    manual = bool(args.client_id or args.user or args.password)
    if manual:
        client = MqttClient(host=args.host, port=args.port,
                            client_id=args.client_id or ("UWPClient_%d" % args.slot),
                            username=args.user, password=args.password,
                            keepalive=30, on_message=on_message, log=log)
        try:
            client.connect()
        except Exception as exc:
            log("连不上 MQTT broker %s:%d -> %r" % (args.host, args.port, exc))
            return 2
        slot = args.slot
    else:
        try:
            client, slot = gcu_broker.connect_auto(args.host, args.port, prefer=args.slot,
                                                  on_message=on_message, log=log)
        except Exception as exc:
            log("连不上 MQTT broker %s:%d -> %r" % (args.host, args.port, exc))
            log("请确认「机械革命控制中心 Service / GCUBridge」正在运行（13688 端口是它监听的）")
            log("GCUBridge 只接受编号一致的 UWPClient_<N> + UWPClient_User_<N> + UWPClient_Pwd888881772688_<N>")
            return 2

    client.subscribe([(TOPIC_CTRL, 0), (TOPIC_HWOC_CTRL, 0), (TOPIC_STATUS, 0), (TOPIC_HWOC_STATUS, 0)])
    time.sleep(0.2)
    publish_all("启动初始化")
    log("虚拟水冷坞已上线: clientId=UWPClient_%d  MAC=%s 固件=%s" % (slot, dock.mac, dock.fw))
    log("BT_LC/Status  = " + dump(dock.status()))
    log("LCHWOC/Status = " + dump(dock.hwoc()))

    if args.once:
        client.close()
        log("--once 模式：已发布一次，退出")
        return 0

    def heartbeat():
        while True:
            time.sleep(max(0.2, args.interval))
            publish_all("")

    threading.Thread(target=heartbeat, daemon=True).start()

    try:
        client.loop(auto_reconnect=True, reconnect_delay=3.0)
    except KeyboardInterrupt:
        log("用户中断")
    finally:
        try:
            client.close()
        except Exception:
            pass
        log("退出。累计发布 %d 条，压制真实上报 %d 次，处理控制指令 %d 条"
            % (stats["sent"], stats["suppressed"], stats["ctrl"]))
    return 0


if __name__ == "__main__":
    sys.exit(main())
