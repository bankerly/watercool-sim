# 适配第三方控制台 OpenRevo（内置内存挂钩）

> 结论：**OpenRevo 不走 MQTT broker**，它是自己用 WinRT BLE 直连「水冷坞」的（源码模块 `src\driver\ble_cooler.rs`，设备名前缀 `LCTCS_`，数据走 Nordic UART Service）。
> 所以对官方控制中心有效的 broker 模拟对它没用；本程序改为**在它进程内存里挂钩**，让它显示「水冷已连接」。

## 一、协议（逆向所得，存档备查）

| 项 | 值 |
|---|---|
| 设备发现 | BLE 广播，名字前缀 **`LCTCS_`** |
| 服务 | NUS `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` |
| TX（坞→控制台，notify） | `6E400003-…` |
| RX（控制台→坞，write） | `6E400002-…` |
| 握手/超时 | 有握手，8 秒超时 |
| 状态字段 | connected, is_flow_ok, meter_state, pump_voltage, pump_tier, fan_duty, pump_rpm, running, safety_tripped, is_priming, led_on/led_r/led_g/led_b/led_mode, mac_address, strategy, curve_points, pump_start_temp, pump_turbo_temp |

> 注意：**同一台机器上"本机当 BLE 外设"骗不了它**——同一颗蓝牙适配器无法既是中心又连自己的外设（实测 `BluetoothLEDevice.FromBluetoothAddressAsync(本机地址)` 返回 null，本机扫描也扫不到自己的广播）。所以真正的坞必须来自**另一台设备/手机**；同机只能用下面这种内存挂钩的方式模拟。

## 二、内置模拟怎么做的

1. 用签名在 `open-revo.exe` 的 .text 里定位「水冷状态序列化」入口：
   `4C 8D 4B 4C 48 8D 15 ?? ?? ?? ?? 48 8D 4C 24 30 41 B8 09 00 00 00 E8`（即 `lea r9,[rbx+0x4c]` = 读 `connected` 之前的位置）。
2. 在该处写入 14 字节 `jmp qword ptr [rip+0]` + 跳板地址，跳到我们分配的一小段代码。
3. 跳板每次执行时把状态结构（`rbx`）里的字段改成「已连接 + 假数据」，再补回被搬走的 3 条指令并跳回原流程：

| 字段 | 偏移 | 写入 |
|---|---|---|
| `connected` | +0x4C | 1 |
| `is_flow_ok` | +0x4D | 1 |
| `meter_state` | +0x52 | 1 |
| `pump_voltage` | +0x53 | 7 |
| `pump_tier` | +0x54 | 2 |
| `fan_duty` | +0x55 | 50 |

4. **安全检查**：挂钩前会校验该处地址上的字符串确实是 `connected`，版本不对就不动手（避免把控制台搞崩）。

## 三、安全性 / 可逆性

- **只改运行中的内存，不修改任何磁盘文件**；OpenRevo 的 exe 保持原样。
- 关闭开关或退出本程序时会**还原原始指令**。
- 若本程序被强杀（来不及还原），下次启动会检测残留并用**磁盘镜像里的原始字节**自动还原（签名定位，版本无关）。
- 需要管理员权限（OpenRevo 自身以管理员运行，打开它的进程需要同级权限）。

## 四、已知限制

- 挂钩点偏移与具体版本有关；OpenRevo 升级后若签名找不到，本程序会明确提示"版本可能已更新"，不会硬来。
- `pump_rpm`、`mac_address` 目前仍是"未连接时的值"（界面显示的转速/水流为控制台自身的派生态）。
