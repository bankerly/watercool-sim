// WaterCoolSim.cs - 机械革命控制中心 · 虚拟水冷坞（GUI 单文件版）
// 纯 .NET Framework 4.x + WinForms，自带 MQTT 3.1.1 客户端，无任何外部依赖。
// 编译：csc /target:winexe /out:WaterCoolSim.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll WaterCoolSim.cs
//
// 原理（逆向结论，详见 README.md）：
//   CCUWinUI 只是本机 MQTT broker(GCUBridge, 127.0.0.1:13688) 的客户端，
//   它把 BT_LC/Status 的 connected + ConnectString=="Connected" 当作"水冷已连接"，
//   把 LCHWOC/Status 的 Enable/Connected/Support 当作"水冷超频可用"。
//   本程序冒充上报方持续发布这两条状态，并在真实服务抢报"未连接"时立刻覆盖回去。
//   退出/停止时自动把这两条覆盖为"未连接"，不留残留。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WaterCoolSim
{
    // ============================================================
    //  极简 MQTT 3.1.1 客户端（CONNECT / SUBSCRIBE / PUBLISH / PING）
    // ============================================================
    public class MqttError : Exception
    {
        public byte Code;
        public MqttError(byte code, string msg) : base(msg) { Code = code; }
    }

    public class MqttMini
    {
        public const string TOPIC_CTRL = "BT_LC/Control";
        public const string TOPIC_STATUS = "BT_LC/Status";
        public const string TOPIC_HWOC_CTRL = "LCHWOC/Control";
        public const string TOPIC_HWOC_STATUS = "LCHWOC/Status";

        private string host;
        private int port;
        private string clientId;
        private string user;
        private string pass;
        private Action<string> log;
        private Socket sock;
        private object sendLock = new object();
        private Thread th;
        private volatile bool stop;
        private string[] subs = new string[0];
        private DateTime lastPing = DateTime.UtcNow;

        public volatile bool Connected;
        public event Action<string, string, bool> OnMessage;   // topic, payload, retain
        public event Action OnReconnected;

        public string ClientId { get { return clientId; } }
        public string Host { get { return host; } }
        public int Port { get { return port; } }

        public MqttMini(string host, int port, string clientId, string user, string pass, Action<string> log)
        {
            this.host = host; this.port = port; this.clientId = clientId;
            this.user = user; this.pass = pass; this.log = log;
        }

        // ---------- 编码 ----------
        private static byte[] EncLen(int n)
        {
            List<byte> o = new List<byte>();
            while (true)
            {
                int b = n % 128; n = n / 128;
                if (n > 0) b = b | 0x80;
                o.Add((byte)b);
                if (n == 0) break;
            }
            return o.ToArray();
        }

        private static byte[] EncStr(string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s == null ? "" : s);
            byte[] r = new byte[2 + b.Length];
            r[0] = (byte)(b.Length >> 8); r[1] = (byte)(b.Length & 0xFF);
            Array.Copy(b, 0, r, 2, b.Length);
            return r;
        }

        private static byte[] Cat(params byte[][] parts)
        {
            int n = 0;
            for (int i = 0; i < parts.Length; i++) if (parts[i] != null) n += parts[i].Length;
            byte[] r = new byte[n]; int p = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null) continue;
                Array.Copy(parts[i], 0, r, p, parts[i].Length); p += parts[i].Length;
            }
            return r;
        }

        // ---------- 连接 ----------
        public void Connect()
        {
            Close();
            Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.Connect(host, port);
            s.NoDelay = true;
            sock = s;

            byte flags = 0x02;      // clean session
            byte[] payload = EncStr(clientId);
            if (user != null) { flags |= 0x80; payload = Cat(payload, EncStr(user)); }
            if (pass != null) { flags |= 0x40; payload = Cat(payload, EncStr(pass)); }

            byte[] vh = Cat(EncStr("MQTT"), new byte[] { 4, flags }, new byte[] { 0, 30 });
            byte[] body = Cat(vh, payload);
            SendRaw(Cat(new byte[] { 0x10 }, EncLen(body.Length), body));

            byte cmd, fl; byte[] resp;
            if (!ReadPacket(5000, out cmd, out fl, out resp)) throw new IOException("CONNACK 超时");
            if (cmd != 2) throw new IOException("未收到 CONNACK（cmd=" + cmd + "）");
            byte code = resp.Length >= 2 ? resp[1] : (byte)255;
            if (code != 0) throw new MqttError(code, ConnackText(code));
            Connected = true;
            log("[mqtt] 已连接 " + host + ":" + port + "  clientId=" + clientId);
        }

        public static string ConnackText(byte code)
        {
            switch (code)
            {
                case 1: return "协议版本不被接受(1)";
                case 2: return "clientId 被拒(2)：GCUBridge 只认 UWPClient_<N> 且用户名/密码编号必须一致";
                case 3: return "服务不可用(3)";
                case 4: return "用户名或密码错误(4)：编号没对齐";
                case 5: return "未授权(5)";
                default: return "CONNACK code=" + code;
            }
        }

        // ---------- 订阅 ----------
        public void Subscribe(string[] topics)
        {
            subs = topics;
            byte[] body = new byte[] { 0, 1 };
            for (int i = 0; i < topics.Length; i++) body = Cat(body, EncStr(topics[i]), new byte[] { 0 });
            SendRaw(Cat(new byte[] { 0x82 }, EncLen(body.Length), body));
            log("[mqtt] 已订阅: " + string.Join(", ", topics));
        }

        // ---------- 发布 ----------
        public void Publish(string topic, string payload, bool retain)
        {
            if (!Connected) throw new IOException("未连接");
            byte[] tb = EncStr(topic);
            byte[] pb = Encoding.UTF8.GetBytes(payload == null ? "" : payload);
            byte[] body = Cat(tb, pb);
            byte[] pkt = Cat(new byte[] { (byte)(0x30 | (retain ? 1 : 0)) }, EncLen(body.Length), body);
            SendRaw(pkt);
        }

        private void SendRaw(byte[] data)
        {
            lock (sendLock)
            {
                if (sock == null) throw new IOException("未连接");
                sock.Send(data);
            }
        }

        // ---------- 读取 ----------
        private bool ReadExact(byte[] buf, int off, int len, int timeoutMs)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (off < len)
            {
                int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0) return false;
                sock.ReceiveTimeout = left;
                int n;
                try { n = sock.Receive(buf, off, len - off, SocketFlags.None); }
                catch (SocketException se)
                {
                    if (se.SocketErrorCode == SocketError.TimedOut) return false;
                    throw;
                }
                if (n <= 0) throw new IOException("broker 关闭了连接");
                off += n;
            }
            return true;
        }

        private bool ReadPacket(int timeoutMs, out byte cmd, out byte flags, out byte[] body)
        {
            cmd = 0; flags = 0; body = new byte[0];
            byte[] one = new byte[1];
            if (!ReadExact(one, 0, 1, timeoutMs)) return false;
            cmd = (byte)(one[0] >> 4);
            flags = (byte)(one[0] & 0x0F);
            int mult = 1, len = 0;
            while (true)
            {
                if (!ReadExact(one, 0, 1, 5000)) throw new IOException("报文长度读取超时");
                len += (one[0] & 127) * mult;
                if ((one[0] & 0x80) == 0) break;
                mult *= 128;
                if (mult > 128 * 128 * 128) throw new IOException("非法长度");
            }
            if (len > 0)
            {
                body = new byte[len];
                if (!ReadExact(body, 0, len, 5000)) throw new IOException("报文体读取超时");
            }
            return true;
        }

        // ---------- 循环 + 自动重连 ----------
        public void StartLoop()
        {
            stop = false;
            th = new Thread(new ThreadStart(Loop));
            th.IsBackground = true;
            th.Start();
        }

        public void StopLoop()
        {
            stop = true;
            if (th != null) { try { th.Join(1500); } catch { } th = null; }
        }

        private void Loop()
        {
            while (!stop)
            {
                try
                {
                    byte cmd, fl; byte[] body;
                    if (ReadPacket(400, out cmd, out fl, out body))
                    {
                        Handle(cmd, fl, body);
                    }
                    else if ((DateTime.UtcNow - lastPing).TotalSeconds >= 15)
                    {
                        SendRaw(new byte[] { 0xC0, 0x00 });
                        lastPing = DateTime.UtcNow;
                    }
                }
                catch (Exception ex)
                {
                    if (stop) return;
                    Connected = false;
                    log("[mqtt] 连接中断：" + ex.Message + "，3 秒后重连");
                    try { Close(); } catch { }
                    for (int i = 0; i < 30 && !stop; i++) Thread.Sleep(100);
                    if (stop) return;
                    try
                    {
                        Connect();
                        if (subs.Length > 0) Subscribe(subs);
                        if (OnReconnected != null) OnReconnected();
                    }
                    catch (Exception ex2)
                    {
                        log("[mqtt] 重连失败：" + ex2.Message);
                    }
                }
            }
        }

        private void Handle(byte cmd, byte flags, byte[] body)
        {
            if (cmd != 3) return;                       // 只关心 PUBLISH
            bool retain = (flags & 0x01) != 0;
            int tl = (body[0] << 8) | body[1];
            string topic = Encoding.UTF8.GetString(body, 2, tl);
            int idx = 2 + tl;
            int qos = (flags >> 1) & 0x03;
            if (qos > 0)
            {
                if (qos == 1)
                {
                    byte[] pid = new byte[] { body[idx], body[idx + 1] };
                    try { SendRaw(Cat(new byte[] { 0x40, 0x02 }, pid)); }
                    catch { }
                }
                idx += 2;
            }
            string payload = Encoding.UTF8.GetString(body, idx, body.Length - idx);
            if (OnMessage != null) OnMessage(topic, payload, retain);
        }

        public void Close()
        {
            lock (sendLock)
            {
                if (sock != null)
                {
                    try { sock.Close(); } catch { }
                    sock = null;
                }
                Connected = false;
            }
        }
    }

    // ============================================================
    //  JSON 取值小工具（只处理本协议用到的简单结构）
    // ============================================================
    public static class JsonMini
    {
        public static string Str(string json, string key)
        {
            if (json == null) return null;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        public static bool Bool(string json, string key)
        {
            if (json == null) return false;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success && m.Groups[1].Value.ToLowerInvariant() == "true";
        }

        public static bool HasKey(string json, string key)
        {
            if (json == null) return false;
            return Regex.IsMatch(json, "\"" + Regex.Escape(key) + "\"\\s*:", RegexOptions.IgnoreCase);
        }

        public static int Int(string json, string key, int def)
        {
            if (json == null) return def;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?(-?\\d+)", RegexOptions.IgnoreCase);
            if (!m.Success) return def;
            int v;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : def;
        }
    }

    // ============================================================
    //  虚拟水冷坞状态机
    // ============================================================
    public class FakeDock
    {
        public const string TAG = "lc_sim";

        public bool ConnectedFlag = true;
        public string ConnectString = "Connected";
        public string Mac = "AA:BB:CC:00:11:22";
        public string Fw = "CoolingSystem LCT21001-SIM-v1.0.0";
        public int PumpDuty = 60;
        public int FanDuty = 50;
        public string PumpCtrl = "1";
        public string FanCtrl = "1";
        public int R = 255, G = 0, B = 0;
        public int LedMode = 0, FanLedMode = 0;
        public bool InputWater = false, UpdateFw = false, MeterNormal = true;
        public bool LcAction = true, AutoConnect = true;
        public double ReconnectDelay = 3.0;

        private static readonly int[] PumpDutyMap = new int[] { 45, 60, 90 };
        private static readonly int[] FanDutyMap = new int[] { 40, 50, 60, 90 };

        private static string Jb(bool v) { return v ? "true" : "false"; }
        private static string N(int v) { return v.ToString(CultureInfo.InvariantCulture); }

        public string BuildStatus()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"src\":\"").Append(TAG).Append("\"");
            sb.Append(",\"connected\":").Append(Jb(ConnectedFlag));
            sb.Append(",\"ConnectString\":\"").Append(ConnectString).Append("\"");
            sb.Append(",\"PumpDuty\":").Append(N(PumpDuty));
            sb.Append(",\"FanDuty\":").Append(N(FanDuty));
            sb.Append(",\"LCLED_R\":").Append(N(R));
            sb.Append(",\"LCLED_G\":").Append(N(G));
            sb.Append(",\"LCLED_B\":").Append(N(B));
            sb.Append(",\"LCLED_Mode\":").Append(N(LedMode));
            sb.Append(",\"LCFanLED_Mode\":").Append(N(FanLedMode));
            sb.Append(",\"LC_action\":").Append(Jb(LcAction));
            sb.Append(",\"LC_CoolingAuto\":false");
            sb.Append(",\"LC_InputWater\":").Append(Jb(InputWater));
            sb.Append(",\"LC_PumpCtrl\":\"").Append(PumpCtrl).Append("\"");
            sb.Append(",\"LC_FanCtrl\":\"").Append(FanCtrl).Append("\"");
            sb.Append(",\"LC_UpdateFW\":").Append(Jb(UpdateFw));
            sb.Append(",\"DevFWVersion\":\"").Append(Fw).Append("\"");
            sb.Append(",\"DevMACString\":\"").Append(Mac).Append("\"");
            sb.Append(",\"DeviceMacList\":[\"").Append(Mac).Append("\"]");
            sb.Append(",\"LC_MeterNormal\":").Append(Jb(MeterNormal));
            sb.Append(",\"AutoConnect\":").Append(Jb(AutoConnect));
            sb.Append("}");
            return sb.ToString();
        }

        public string BuildHwoc()
        {
            return "{\"src\":\"" + TAG + "\",\"Enable\":" + Jb(ConnectedFlag)
                 + ",\"CoreFreqOffset\":0,\"MemFreqOffset\":0"
                 + ",\"Connected\":" + Jb(ConnectedFlag) + ",\"Support\":true}";
        }

        // 清理用：明确的"未连接"状态（覆盖 retain，界面立刻回到未连接）
        public string BuildCleanupStatus()
        {
            return "{\"src\":\"" + TAG + "\",\"connected\":false,\"ConnectString\":\"Disconnected\""
                 + ",\"PumpDuty\":0,\"FanDuty\":0,\"LCLED_R\":0,\"LCLED_G\":0,\"LCLED_B\":0"
                 + ",\"LCLED_Mode\":0,\"LCFanLED_Mode\":0,\"LC_action\":true,\"LC_CoolingAuto\":false"
                 + ",\"LC_InputWater\":false,\"LC_PumpCtrl\":\"1\",\"LC_FanCtrl\":\"1\",\"LC_UpdateFW\":false"
                 + ",\"DevFWVersion\":\"\",\"DevMACString\":\"\",\"DeviceMacList\":[\"\"]"
                 + ",\"LC_MeterNormal\":true,\"AutoConnect\":false}";
        }

        public string BuildCleanupHwoc()
        {
            return "{\"src\":\"" + TAG + "\",\"Enable\":false,\"CoreFreqOffset\":0,\"MemFreqOffset\":0"
                 + ",\"Connected\":false,\"Support\":true}";
        }

        // 处理界面下发的 BT_LC/Control，返回给日志用的一句话
        public string HandleControl(string json)
        {
            string action = JsonMini.Str(json, "Action");
            if (action == null) return "缺 Action，忽略";
            if (action == "GETSTATUS") return "GETSTATUS → 立即上报状态";
            if (action == "Connect")
            {
                ConnectedFlag = true; ConnectString = "Connected"; LcAction = true;
                return "Connect → 已连接";
            }
            if (action == "Disconnect")
            {
                ConnectedFlag = false; ConnectString = "Disconnected";
                return "Disconnect → 已断开（" + ReconnectDelay.ToString("0.#", CultureInfo.InvariantCulture) + " 秒后自动重连）";
            }
            if (action == "LC_PumpCtrl")
            {
                int i = JsonMini.Int(json, "PumpCtrl", 1);
                PumpCtrl = i.ToString(CultureInfo.InvariantCulture);
                if (i >= 0 && i < PumpDutyMap.Length) PumpDuty = PumpDutyMap[i];
                return "LC_PumpCtrl → 档位 " + PumpCtrl + "，占空比 " + PumpDuty + "%";
            }
            if (action == "LC_FanCtrl")
            {
                int i = JsonMini.Int(json, "FanCtrl", 1);
                FanCtrl = i.ToString(CultureInfo.InvariantCulture);
                if (i >= 0 && i < FanDutyMap.Length) FanDuty = FanDutyMap[i];
                return "LC_FanCtrl → 档位 " + FanCtrl + "，占空比 " + FanDuty + "%";
            }
            if (action == "LEDControl")
            {
                R = JsonMini.Int(json, "LCLED_R", R);
                G = JsonMini.Int(json, "LCLED_G", G);
                B = JsonMini.Int(json, "LCLED_B", B);
                return "LEDControl → RGB(" + R + "," + G + "," + B + ")";
            }
            if (action == "LEDBreathing" || action == "LEDColorful")
            {
                LedMode = JsonMini.Int(json, "LedMode", 0);
                return action + " → lcLed_Mode=" + LedMode;
            }
            if (action == "FanLEDEffect")
            {
                FanLedMode = JsonMini.Int(json, "LedMode", 0);
                return "FanLEDEffect → lcFanLED_Mode=" + FanLedMode;
            }
            if (action == "InputWater") { InputWater = true; return "InputWater → 注水模式（模拟）"; }
            if (action == "DeviceMacSetting")
            {
                string m = JsonMini.Str(json, "DeviceMac");
                if (!string.IsNullOrEmpty(m)) Mac = m;
                return "DeviceMacSetting → " + Mac;
            }
            if (action == "ClearDevMAC") { Mac = ""; return "ClearDevMAC → 已清空配对"; }
            if (action == "Open_DFU_Folder") return "Open_DFU_Folder → 忽略（模拟设备无 DFU）";
            return "未知 Action=" + action + "，忽略";
        }
    }

    // ============================================================
    //  模拟器核心（GUI 与命令行共用）
    // ============================================================
    public class SimCore
    {
        private Action<string> log;
        public MqttMini Client;
        public FakeDock Dock = new FakeDock();
        public int Slot = 6;
        public double Interval = 1.0;
        public bool Suppress = true;
        public bool Running;

        public long Sent, Suppressed, Controls;
        public long EchoStatus, EchoHwoc, UiCtrl, BtOff, DevSw;   // 复查用计数
        public bool BtEnabled = true;
        public DateTime ReconnectAt = DateTime.MinValue;

        public string Host = "127.0.0.1";
        public int Port = 13688;

        public SimCore(Action<string> log) { this.log = log; }

        public const string TOPIC_DEVSWS = "Settings/DeviceSwitchItemStatus";

        private static string[] SubscribeTopics()
        {
            return new string[]
            {
                MqttMini.TOPIC_CTRL, MqttMini.TOPIC_HWOC_CTRL,
                MqttMini.TOPIC_STATUS, MqttMini.TOPIC_HWOC_STATUS,
                TOPIC_DEVSWS
            };
        }

        private static string Cred(int slot, string kind)
        {
            if (kind == "c") return "UWPClient_" + slot;
            if (kind == "u") return "UWPClient_User_" + slot;
            return "UWPClient_Pwd888881772688_" + slot;
        }

        // 编号自动回退：6,7,...,20,1..4,5
        public bool ConnectAuto(int prefer)
        {
            List<int> order = new List<int>();
            for (int n = prefer; n <= prefer + 14; n++) if (n >= 1 && n != 5) order.Add(n);
            for (int n = 1; n <= 4; n++) order.Add(n);
            order.Add(5);
            MqttError last = null;
            for (int i = 0; i < order.Count; i++)
            {
                int s = order[i];
                MqttMini c = new MqttMini(Host, Port, Cred(s, "c"), Cred(s, "u"), Cred(s, "p"), log);
                try
                {
                    c.Connect();
                    Client = c;
                    Slot = s;
                    if (s == 5) log("[注意] 正在使用官方界面占用的编号 5，界面会被顶下线并自动重连");
                    return true;
                }
                catch (MqttError e)
                {
                    last = e;
                    try { c.Close(); } catch { }
                    if (e.Code != 2 && e.Code != 4)
                    {
                        log("连接失败：" + e.Message);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    log("连接失败：" + ex.Message + "（确认控制中心/GCUBridge 在运行，13688 有监听）");
                    return false;
                }
            }
            log("所有编号都被拒绝" + (last != null ? "：" + last.Message : ""));
            return false;
        }

        public void AttachHandlers()
        {
            Client.OnMessage += OnMessage;
            Client.OnReconnected += delegate
            {
                log("[mqtt] 重连成功，重发虚拟水冷状态");
                PublishAll("重连后重发");
            };
        }

        public bool Start()
        {
            if (Running) return true;
            if (!ConnectAuto(Slot)) return false;
            AttachHandlers();
            Client.Subscribe(SubscribeTopics());
            Thread.Sleep(200);
            PublishAll("启动初始化");
            Running = true;
            Client.StartLoop();
            log("虚拟水冷坞已上线：clientId=" + Client.ClientId + "  MAC=" + Dock.Mac + "  固件=" + Dock.Fw);
            return true;
        }

        public void PublishAll(string reason)
        {
            try
            {
                Client.Publish(MqttMini.TOPIC_STATUS, Dock.BuildStatus(), true);
                Client.Publish(MqttMini.TOPIC_HWOC_STATUS, Dock.BuildHwoc(), true);
                Sent += 2;
                if (reason != null && reason.Length > 0) log("已发布状态（" + reason + "）");
            }
            catch (Exception ex)
            {
                log("发布失败：" + ex.Message);
            }
        }

        // 自动清理：把两条 retain 覆盖成"未连接"
        public void Cleanup()
        {
            try
            {
                if (Client == null || !Client.Connected) return;
                Client.Publish(MqttMini.TOPIC_STATUS, Dock.BuildCleanupStatus(), true);
                Client.Publish(MqttMini.TOPIC_HWOC_STATUS, Dock.BuildCleanupHwoc(), true);
                log("[自动清理] 已把 BT_LC/Status 与 LCHWOC/Status 覆盖为「未连接」，无残留");
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                log("[自动清理] 失败：" + ex.Message);
            }
        }

        public void Stop(bool doCleanup)
        {
            if (!Running) { if (doCleanup) Cleanup(); return; }
            if (doCleanup) Cleanup();
            Running = false;
            try { Client.StopLoop(); } catch { }
            try { Client.Close(); } catch { }
            log("已停止模拟");
        }

        private void OnMessage(string topic, string payload, bool retain)
        {
            try
            {
                if (topic == MqttMini.TOPIC_CTRL)
                {
                    if (JsonMini.Str(payload, "src") == FakeDock.TAG) return;   // 自己的回声
                    Controls++;
                    UiCtrl++;
                    log("控制指令：" + Dock.HandleControl(payload));
                    PublishAll("响应控制指令");
                    if (!Dock.ConnectedFlag) ReconnectAt = DateTime.UtcNow.AddSeconds(Dock.ReconnectDelay);
                    return;
                }
                if (topic == MqttMini.TOPIC_HWOC_CTRL)
                {
                    if (JsonMini.Str(payload, "src") == FakeDock.TAG) return;
                    log("HWOC 控制指令：" + payload);
                    PublishAll("响应 HWOC 指令");
                    return;
                }
                if (topic == TOPIC_DEVSWS)
                {
                    DevSw++;
                    BtEnabled = JsonMini.Bool(payload, "BTEnable");
                    if (!BtEnabled) BtOff++;
                    return;
                }
                if (topic == MqttMini.TOPIC_STATUS || topic == MqttMini.TOPIC_HWOC_STATUS)
                {
                    if (JsonMini.Str(payload, "src") == FakeDock.TAG)          // 自己的回声 = 发布成功的证据
                    {
                        if (topic == MqttMini.TOPIC_STATUS && JsonMini.Bool(payload, "connected")) EchoStatus++;
                        if (topic == MqttMini.TOPIC_HWOC_STATUS && JsonMini.Bool(payload, "Enable")
                            && JsonMini.Bool(payload, "Connected") && JsonMini.Bool(payload, "Support")) EchoHwoc++;
                        return;
                    }
                    if (!Suppress) return;
                    bool bad;
                    if (topic == MqttMini.TOPIC_STATUS)
                        bad = !JsonMini.Bool(payload, "connected") || JsonMini.Str(payload, "ConnectString") != "Connected";
                    else
                        bad = !(JsonMini.Bool(payload, "Enable") && JsonMini.Bool(payload, "Connected") && JsonMini.Bool(payload, "Support"));
                    if (bad)
                    {
                        Suppressed++;
                        log("发现真实服务上报「未连接」→ 立即压制（第 " + Suppressed + " 次）：" + payload);
                        PublishAll("压制真实上报");
                    }
                }
            }
            catch (Exception ex)
            {
                log("处理报文异常：" + ex.Message);
            }
        }

        public void Tick()
        {
            if (!Running) return;
            if (ReconnectAt != DateTime.MinValue && DateTime.UtcNow >= ReconnectAt)
            {
                ReconnectAt = DateTime.MinValue;
                Dock.ConnectedFlag = true;
                Dock.ConnectString = "Connected";
                log("自动重连：水冷坞已重新连接");
            }
            PublishAll(null);
        }

        // 启动后的复查：确认「发出去的状态确实被 broker 回读」以及「控制中心确实在消费」。
        public bool Recheck(double seconds)
        {
            long e0 = EchoStatus, h0 = EchoHwoc, u0 = UiCtrl, s0 = Suppressed, b0 = BtOff;
            DateTime end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end) Thread.Sleep(100);
            long dEcho = EchoStatus - e0, dHwoc = EchoHwoc - h0, dUi = UiCtrl - u0, dSup = Suppressed - s0, dBt = BtOff - b0;

            int pass = 0, total = 0;
            log("==== 启动复查（观察 " + seconds.ToString("0.#", CultureInfo.InvariantCulture) + " 秒）====");

            // —— 关键项：发布链路必须通 ——
            total++;
            if (dEcho > 0 || EchoStatus > 0) { pass++; log("[OK] broker 已回读 BT_LC/Status：connected=true（发布链路通）"); }
            else log("[!!] 没回读到自己的 BT_LC/Status —— broker 没分发或本连接已被踢掉");

            total++;
            if (dHwoc > 0 || EchoHwoc > 0) { pass++; log("[OK] broker 已回读 LCHWOC/Status：Enable/Connected/Support 全为真"); }
            else log("[!!] 没回读到自己的 LCHWOC/Status");

            log("---- 以下为观察项（不影响上面结论）----");

            if (dUi > 0)
                log("[OK] 控制中心正在消费虚拟水冷坞：观察期内收到 " + dUi + " 条 BT_LC/Control 指令");
            else
                log("[..] 本次观察期内没收到界面指令 —— 这是正常的：界面只有停在「水冷页」时才会每 10 秒发一次 GETSTATUS。"
                    + "想验证就打开控制中心的水冷页，再点「复查」按钮");

            if (DevSw > 0)
                log("[OK] 服务端广播正常：收到 Settings/DeviceSwitchItemStatus（BTEnable=" + (BtEnabled ? "true" : "false") + "），说明 GCUService 在线");
            else
                log("[..] 未收到服务端广播（可能刚连上，稍等再点「复查」）");

            if (dSup > 0) log("[OK] 期间压制真实服务「未连接」上报 " + dSup + " 次（压制生效）");
            else log("[..] 期间真实服务没有抢报「未连接」（暂无干扰）");

            if (dBt > 0 || !BtEnabled)
                log("[!!] 蓝牙总开关处于关闭状态（Settings/DeviceSwitchItemStatus.BTEnable=false），水冷页会显示不可用");

            log("==== 复查结果：关键项 " + pass + "/" + total + (pass == total ? " 通过（虚拟水冷坞已生效）" : " 未通过（看上面 [!!] 行）") + " ====");
            return pass == total;
        }

        // 只读探测：接上 broker、订阅水冷主题、发一次 GETSTATUS、打印真实报文。
        // 用于在陌生机器上先判断"这套东西在这台机器上能不能用"。
        public bool Probe(double seconds)
        {
            bool ok = false;
            try
            {
                if (!ConnectAuto(Slot)) return false;
                Client.OnMessage += delegate(string t, string p, bool r)
                {
                    log("< " + t + (r ? " [retain]" : "") + "  " + p);
                };
                Client.Subscribe(SubscribeTopics());
                Client.StartLoop();
                Thread.Sleep(300);
                log("发送查询 GETSTATUS（只读，与界面行为一致）");
                Client.Publish(MqttMini.TOPIC_CTRL, "{\"Action\":\"GETSTATUS\"}", false);
                Client.Publish(MqttMini.TOPIC_HWOC_CTRL, "{\"Action\":\"GETSTATUS\"}", false);
                DateTime end = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < end) Thread.Sleep(100);
                ok = true;
            }
            catch (Exception ex)
            {
                log("探测失败：" + ex.Message);
            }
            finally
            {
                try { if (Client != null) { Client.StopLoop(); Client.Close(); Client = null; } } catch { }
            }
            return ok;
        }

        // 启动时检查上一次残留：只有当 broker 上的 retain 确实带着本工具的标记
        //（src == "lc_sim"）且仍显示"已连接"时才清理，绝不碰真实设备的状态。
        public bool CleanupStale()
        {
            bool found = false;
            try
            {
                if (!ConnectAuto(Slot)) return false;
                ManualResetEvent got = new ManualResetEvent(false);
                bool[] stale = new bool[1];
                Client.OnMessage += delegate(string t, string p, bool r)
                {
                    if (JsonMini.Str(p, "src") != FakeDock.TAG) return;
                    if (t == MqttMini.TOPIC_STATUS && JsonMini.Bool(p, "connected")) stale[0] = true;
                    if (t == MqttMini.TOPIC_HWOC_STATUS && JsonMini.Bool(p, "Enable")) stale[0] = true;
                    if (stale[0]) got.Set();
                };
                Client.Subscribe(new string[] { MqttMini.TOPIC_STATUS, MqttMini.TOPIC_HWOC_STATUS });
                Client.StartLoop();
                got.WaitOne(1500, false);
                found = stale[0];
                if (found)
                {
                    Cleanup();
                    log("[启动清理] 发现上次模拟留下的「已连接」残留，已覆盖为未连接");
                }
                else
                {
                    log("[启动清理] broker 上没有本工具的残留（真实状态未被动过）");
                }
                Client.StopLoop();
                Client.Close();
                Client = null;
            }
            catch (Exception ex)
            {
                log("[启动清理] 失败：" + ex.Message);
                try { if (Client != null) { Client.Close(); Client = null; } } catch { }
            }
            return found;
        }
    }

    // ============================================================
    //  环境自检：把「这台机器够不够跑」讲清楚
    // ============================================================
    public class EnvReport
    {
        public int Pass;
        public int Total;
        public List<string> Problems = new List<string>();
    }

    // ============================================================
    //  环境自检：把「这台机器够不够跑」讲清楚，并回报需要提醒的问题
    // ============================================================
    public static class SelfCheck
    {
        public static int RegDword(string subkey, string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(subkey, false))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name, def);
                    if (v == null) return def;
                    return Convert.ToInt32(v);
                }
            }
            catch { return def; }
        }

        private static string NetFxName(int rel)
        {
            if (rel <= 0) return null;
            if (rel >= 533320) return "4.8.1 或更高";
            if (rel >= 528040) return "4.8";
            if (rel >= 461808) return "4.7.2";
            if (rel >= 460798) return "4.7";
            if (rel >= 394802) return "4.6.2";
            if (rel >= 393295) return "4.6";
            if (rel >= 378389) return "4.5";
            return "4.x（Release=" + rel + "）";
        }

        public static EnvReport RunEnv(Action<string> log, string host, int port)
        {
            EnvReport rep = new EnvReport();
            int pass = 0;
            log("==== 环境自检 ====");

            rep.Total++;
            try
            {
                log("[OK] 操作系统：" + Environment.OSVersion.VersionString
                    + (Environment.Is64BitOperatingSystem ? "（64 位系统）" : "（32 位系统）"));
                pass++;
            }
            catch { log("[!!] 操作系统信息读取失败"); }

            rep.Total++;
            int rel = RegDword(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0);
            string fx = NetFxName(rel);
            if (fx != null)
            {
                pass++;
                log("[OK] 运行环境：.NET Framework " + fx + "（CLR " + Environment.Version.ToString()
                    + "）。注意：本程序不自带运行时，用的是系统这一份");
            }
            else
            {
                log("[!!] 未检测到 .NET Framework 4.x");
                rep.Problems.Add("未检测到 .NET Framework 4.x：Win7/8.1 需要先安装 .NET Framework 4.8");
            }

            rep.Total++;
            pass++;
            log("[..] 进程位数：" + (Environment.Is64BitProcess ? "64 位" : "32 位") + "（AnyCPU，两者都能跑）");

            rep.Total++;
            pass++;
            try
            {
                System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent();
                bool admin = new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                log(admin
                    ? "[OK] 权限：管理员（已提权）—— 托盘标识登记、写入 HKCU 都不受 UIPI 限制"
                    : "[..] 权限：普通用户 —— 若托盘图标登记不上，多半是这里的权限/安全软件拦截，可右键以管理员身份运行");
            }
            catch { log("[..] 权限检测跳过"); }

            rep.Total++;
            string bridge = @"C:\Program Files\OEM\机械革命控制中心\AiStoneService\GCUBridge.exe";
            bool hasExe = File.Exists(bridge);
            int procCount = 0;
            try { procCount = System.Diagnostics.Process.GetProcessesByName("GCUBridge").Length; } catch { }
            if (hasExe || procCount > 0)
            {
                pass++;
                log("[OK] 控制中心组件：GCUBridge.exe " + (hasExe ? "已安装" : "（默认路径没找到）")
                    + "；可见进程数=" + procCount + "（是否能连上以端口连通为准）");
            }
            else
            {
                log("[!!] 没找到 GCUBridge（控制中心没装或路径不同）");
                rep.Problems.Add("没找到控制中心组件 GCUBridge.exe：没有它就没有本机 MQTT broker，模拟不会生效");
            }

            rep.Total++;
            int lcs = RegDword(@"SOFTWARE\OEM\GamingCenter2\ItemSupport", "LiquidCoolingSupport", -1);
            if (lcs == 1)
            {
                pass++;
                log("[OK] 机型支持水冷：LiquidCoolingSupport=1（界面有水冷入口）");
            }
            else
            {
                log("[!!] 机型水冷门禁未打开（LiquidCoolingSupport=" + lcs + "）");
                rep.Problems.Add("机型水冷页面门禁未打开（LiquidCoolingSupport=" + lcs + "）：界面可能看不到水冷入口，可跑 enable_lc_page.ps1");
            }

            rep.Total++;
            bool tcp = false;
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    IAsyncResult ar = s.BeginConnect(host, port, null, null);
                    tcp = ar.AsyncWaitHandle.WaitOne(1500, false);
                    if (tcp) s.EndConnect(ar);
                }
            }
            catch { tcp = false; }
            if (tcp)
            {
                pass++;
                log("[OK] broker 端口可达：" + host + ":" + port);
            }
            else
            {
                log("[!!] broker 端口不可达：" + host + ":" + port);
                rep.Problems.Add("broker 端口不可达（" + host + ":" + port + "）：控制中心没运行，或端口不同（界面可改「主机/端口」）");
            }

            rep.Total++;
            pass++;
            try
            {
                string t = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WaterCoolSim.wtest");
                File.WriteAllText(t, "ok");
                File.Delete(t);
                log("[OK] 程序目录可写（日志可落盘）");
            }
            catch
            {
                log("[..] 程序目录不可写（不影响核心功能，只是日志不落盘）");
            }

            rep.Pass = pass;
            log("==== 环境自检结果：" + pass + "/" + rep.Total + " 项通过"
                + (rep.Problems.Count > 0 ? "，其中 " + rep.Problems.Count + " 项需要处理" : "") + " ====");
            return rep;
        }
    }

    // ============================================================
    //  M3 对话框基类（无边框 / 圆角 / 可拖动 / ESC / 入场动画）
    // ============================================================
    public class Md3DialogBase : Form
    {
        protected Palette P;
        private Point dragOffset;
        private bool dragging;

        public Md3DialogBase(Palette p, Size size, string title)
        {
            P = p;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = size;
            BackColor = P.SurfaceContainer;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;

            Label t = new Label();
            t.Text = title;
            t.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            t.ForeColor = P.OnSurface;
            t.BackColor = Color.Transparent;
            t.SetBounds(24, 20, size.Width - 48, 30);
            Controls.Add(t);

            MouseDown += delegate(object s, MouseEventArgs e) { dragging = true; dragOffset = new Point(e.X, e.Y); };
            MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging) { Left = Left + e.X - dragOffset.X; Top = Top + e.Y - dragOffset.Y; }
            };
            MouseUp += delegate { dragging = false; };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) OnEscape(); };
            Paint += delegate(object s, PaintEventArgs e)
            {
                Md3.Prep(e.Graphics);
                e.Graphics.Clear(P.SurfaceContainer);
                Md3.DrawRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Md3.RadDlg, P.OutlineVariant, 1f);
            };
            using (GraphicsPath gp = Md3.Round(new Rectangle(0, 0, Width, Height), Md3.RadDlg))
                Region = new Region(gp);
            Shown += delegate { AnimateIn(); };
        }

        protected virtual void OnEscape() { Close(); }

        protected Label MkText(string text, float size, Color color, Rectangle r, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            l.ForeColor = color;
            l.BackColor = Color.Transparent;
            l.SetBounds(r.X, r.Y, r.Width, r.Height);
            Controls.Add(l);
            return l;
        }

        protected Md3Button MkBtn(string text, Md3Kind kind, Rectangle r, EventHandler onClick)
        {
            Md3Button b = new Md3Button();
            b.P = P;
            b.Kind = kind;
            b.Text = text;
            b.Font = new Font("Microsoft YaHei UI", 9.5F);
            b.SetBounds(r.X, r.Y, r.Width, r.Height);
            b.Click += onClick;
            Controls.Add(b);
            return b;
        }

        private void AnimateIn()
        {
            Opacity = 0;
            int y = Top;
            Top = y + 16;
            DateTime t0 = DateTime.UtcNow;
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
            tm.Interval = 16;
            tm.Tick += delegate
            {
                double t = (DateTime.UtcNow - t0).TotalMilliseconds / 240.0;
                if (t >= 1) { Opacity = 1; Top = y; tm.Stop(); tm.Dispose(); return; }
                double e = Anim.OutCubic(t);
                Opacity = e;
                Top = (int)(y + 16 - 16 * e);
            };
            tm.Start();
        }
    }

    // 自检未通过时的提醒框（允许继续）
    public class WarnDialog : Md3DialogBase
    {
        public bool ContinueAnyway = true;

        public WarnDialog(Palette p, List<string> problems)
            : base(p, new Size(580, 260 + Math.Max(48, problems.Count * 44)), "环境自检未通过")
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < problems.Count; i++) sb.Append("· ").Append(problems[i]).Append("\n");
            int h = Math.Max(48, problems.Count * 44);
            MkText(sb.ToString().TrimEnd(), 9F, P.Error, new Rectangle(24, 56, ClientSize.Width - 48, h), false);
            MkText("可以仍然继续使用（模拟可能不生效），也可以先处理上面的问题再启动。",
                9F, P.OnSurfaceVariant, new Rectangle(24, 62 + h, ClientSize.Width - 48, 40), false);
            MkBtn("仍然继续", Md3Kind.Filled, new Rectangle(24, ClientSize.Height - 68, 240, 44),
                delegate { ContinueAnyway = true; Close(); });
            MkBtn("退出程序", Md3Kind.Tonal, new Rectangle(280, ClientSize.Height - 68, 256, 44),
                delegate { ContinueAnyway = false; Close(); });
        }

        protected override void OnEscape() { ContinueAnyway = true; Close(); }
    }

    // 关于
    public class AboutDialog : Md3DialogBase
    {
        public const string Body =
            "让机械革命控制中心认为「水冷已连接」的本地模拟器。\n" +
            "只与本机 GCUBridge(127.0.0.1:13688) 通信；不写驱动、不改官方文件。\n" +
            "\n" +
            "技术栈\n" +
            "· C# / .NET Framework 4.x / WinForms，界面全部自绘\n" +
            "· 单文件 exe，用系统自带 csc.exe 编译（build.cmd）\n" +
            "· MQTT 3.1.1 客户端自行实现（未用 paho / M2Mqtt）\n" +
            "\n" +
            "参考的开源项目 / 规范（规范与算法均自行实现，未打包第三方代码）\n" +
            "· Material Design 3 —— Google，Apache-2.0\n" +
            "      色角色、组件形态、状态层；窗口/对话框/卡片/按钮圆角\n" +
            "      按官方形状令牌（corner-large 16 / extra-large 28 / medium 12 / full）\n" +
            "· material-color-utilities —— Google，Apache-2.0\n" +
            "      HCT(CAM16+L*) / TonalPalette / SchemeTonalSpot 按官方实现\n" +
            "· Monet 动态取色 —— Android 12 起，Apache-2.0\n" +
            "      按壁纸挑主色的打分方式\n" +
            "· easings.net / Robert Penner Easing —— MIT\n" +
            "      OutCubic / InOutCubic / OutBack / OutElastic\n" +
            "· MQTT 3.1.1 —— OASIS 标准\n" +
            "      连接 / 订阅 / 发布 / 心跳报文\n" +
            "· ILSpy —— MIT（仅分析用）\n" +
            "      反编译 CCU.WinUI.dll 定位水冷判定链路\n" +
            "· innoextract —— zlib（仅分析用）\n" +
            "      解析 Inno Setup 安装包\n" +
            "\n" +
            "仅供本机自用与测试。";
        public AboutDialog(Palette p)
            : base(p, new Size(600, 560), "关于 虚拟水冷坞")
        {
            TextBox body = new TextBox();
            body.Multiline = true;
            body.ReadOnly = true;
            body.BorderStyle = BorderStyle.None;
            body.ScrollBars = ScrollBars.Vertical;
            body.Font = new Font("Microsoft YaHei UI", 9F);
            body.BackColor = P.SurfaceContainer;
            body.ForeColor = P.OnSurfaceVariant;
            body.Text = Body.Replace("\n", "\r\n");   // 文本框只认 CRLF
            body.TabStop = false;
            body.SetBounds(24, 56, ClientSize.Width - 52, ClientSize.Height - 150);
            Controls.Add(body);
            Shown += delegate { body.SelectionStart = 0; body.SelectionLength = 0; };   // 不要一进来就整段高亮
            MkText("版本 v4.0 · 单文件 · 零依赖", 8.5F, P.OnSurface,
                new Rectangle(24, ClientSize.Height - 86, 400, 20), false);
            MkBtn("关闭", Md3Kind.Filled, new Rectangle(ClientSize.Width - 144, ClientSize.Height - 64, 120, 44),
                delegate { Close(); });
        }
    }

    // ============================================================
    //  谷歌 Monet 动态取色 + Material 3 色板
    // ============================================================
    public class Palette
    {
        public bool Dark;
        public Color SourceColor;
        public Color Primary, OnPrimary, PrimaryContainer, OnPrimaryContainer;
        public Color Secondary, OnSecondary, SecondaryContainer, OnSecondaryContainer;
        public Color Tertiary, TertiaryContainer;
        public Color Error, OnError, ErrorContainer, OnErrorContainer;
        public Color Surface, OnSurface, SurfaceVariant, OnSurfaceVariant;
        public Color Outline, OutlineVariant;
        public Color SurfaceLow, SurfaceContainer, SurfaceHigh, SurfaceHighest;
        public Color InverseSurface, InverseOnSurface;
        public Color StateHover, StatePress;
    }

    public static class Monet
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int SystemParametersInfo(int uAction, int uParam, StringBuilder lpvParam, int fuWinIni);

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k == null) return false;
                    object v = k.GetValue("AppsUseLightTheme", 1);
                    return Convert.ToInt32(v) == 0;
                }
            }
            catch { return false; }
        }

        public static Bitmap LoadWallpaper()
        {
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                if (SystemParametersInfo(0x0073, sb.Capacity, sb, 0) != 0)
                {
                    string p = sb.ToString();
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Read))
                        using (Image img = Image.FromStream(fs))
                            return new Bitmap(img);
                    }
                }
            }
            catch { }
            try
            {
                string t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                        @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(t))
                {
                    using (FileStream fs = new FileStream(t, FileMode.Open, FileAccess.Read))
                    using (Image img = Image.FromStream(fs))
                        return new Bitmap(img);
                }
            }
            catch { }
            return null;
        }

        public static Color DesktopSolidColor()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                {
                    if (k == null) return Color.Empty;
                    string v = Convert.ToString(k.GetValue("Background", ""));
                    string[] parts = v.Split(' ');
                    if (parts.Length >= 3)
                        return Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]));
                }
            }
            catch { }
            return Color.Empty;
        }

        // 官方链路：壁纸 → Celebi 思路量化 + HCT 打分选主色 → SchemeTonalSpot 生成整套色板
        public static Palette Build(bool dark)
        {
            Color seed = Color.FromArgb(0x67, 0x50, 0xA4);   // Material 基线紫，兜底
            Bitmap w = LoadWallpaper();
            if (w != null) { try { seed = Color.FromArgb(SourceColor.Pick(w)); } finally { w.Dispose(); } }
            else
            {
                Color solid = DesktopSolidColor();
                if (solid != Color.Empty) seed = solid;
            }
            return SourceColor.SchemeFromSeed(seed, dark);
        }

    }

    // ============================================================
    //  Material 3 基础绘制工具
    // ============================================================
    public static class Md3
    {
        // 全局统一圆角：所有控件/窗口/对话框都用同一个半径
        public const int Rad = 12;
        public const int RadWin = Rad;
        public const int RadCard = Rad;
        public const int RadDlg = Rad;
        public const int RadField = Rad;
        public const int RadChip = Rad;
        public const int RadSmall = Rad;

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath gp = new GraphicsPath();
            if (rad <= 0) { gp.AddRectangle(r); return gp; }
            int d = rad * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            using (GraphicsPath gp = Round(r, rad))
            using (SolidBrush br = new SolidBrush(c))
                g.FillPath(br, gp);
        }

        public static void DrawRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            using (GraphicsPath gp = Round(r, rad))
            using (Pen p = new Pen(c, w))
            {
                p.Alignment = System.Drawing.Drawing2D.PenAlignment.Inset;
                g.DrawPath(p, gp);
            }
        }

        public static Color LerpColor(Color a, Color b, double t) { return Anim.LerpColor(a, b, t); }

        public static Color WithAlpha(Color c, double factor)
        {
            double a = c.A * Anim.Clamp01(factor);
            return Color.FromArgb((int)a, c.R, c.G, c.B);
        }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static void Prep(Graphics g)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        }
    }

    public enum Md3Kind { Filled, Tonal, Outlined, Text }

    public class Md3Button : Control, Animator.ITick
    {
        public Md3Kind Kind = Md3Kind.Filled;
        public Palette P;
        private bool hover, press, subscribed;
        private TweenValue layer = new TweenValue();
        private TweenValue scale = new TweenValue();

        public Md3Button()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            layer.Set(0);
            scale.Set(1.0);
        }

        private void Aim()
        {
            double target = (hover || press) ? (press ? 1.0 : 0.6) : 0.0;
            layer.To(target, press ? 80 : 160, 2);                    // 状态层：OutCubic
            scale.To(press ? 0.955 : 1.0, press ? 110 : 260, press ? 1 : 3);  // 按下缩到 95.5%，回弹用 OutBack
            if (!subscribed) { Animator.Add(this); subscribed = true; }
        }

        public bool TickAnim()
        {
            bool a = layer.Tick();
            bool b = scale.Tick();
            Invalidate();
            if (!a && !b && !hover && !press) { subscribed = false; return false; }
            return true;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Aim(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; Aim(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; Aim(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; Aim(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = false; press = false; layer.Set(0); Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Md3.Rad;             // 统一圆角
            if (Math.Abs(scale.Value - 1.0) > 0.002)      // 按下缩放
            {
                int dw = (int)(Width * (1.0 - scale.Value) / 2.0);
                int dh = (int)(Height * (1.0 - scale.Value) / 2.0);
                r = new Rectangle(r.X + dw, r.Y + dh, Math.Max(4, r.Width - dw * 2), Math.Max(4, r.Height - dh * 2));
            }
            Color bg, fg;
            if (!Enabled)
            {
                Md3.FillRound(g, r, rad, P.SurfaceHighest);
                TextRenderer.DrawText(g, Text, Font, r, Md3.Mix(P.OnSurfaceVariant, P.Surface, 0.5),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            switch (Kind)
            {
                case Md3Kind.Tonal: bg = P.SecondaryContainer; fg = P.OnSecondaryContainer; break;
                case Md3Kind.Outlined: bg = Color.Empty; fg = P.Primary; break;
                case Md3Kind.Text: bg = Color.Empty; fg = P.Primary; break;
                default: bg = P.Primary; fg = P.OnPrimary; break;
            }
            if (Kind == Md3Kind.Outlined || Kind == Md3Kind.Text)
            {
                if (layer.Value > 0.005) Md3.FillRound(g, r, rad, Md3.WithAlpha(press ? P.StatePress : P.StateHover, layer.Value));
                if (Kind == Md3Kind.Outlined) Md3.DrawRound(g, r, rad, P.Outline, 1f);
            }
            else
            {
                Md3.FillRound(g, r, rad, bg);
                if (layer.Value > 0.005) Md3.FillRound(g, r, rad, Md3.WithAlpha(press ? P.StatePress : P.StateHover, layer.Value));
            }
            TextRenderer.DrawText(g, Text, Font, r, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public class Md3Switch : Control, Animator.ITick
    {
        public Palette P;
        private bool hover, checkedFlag, subscribed;
        private TweenValue pos = new TweenValue();

        public event EventHandler CheckedChanged;

        public bool Checked
        {
            get { return checkedFlag; }
            set
            {
                if (checkedFlag == value) return;
                checkedFlag = value;
                pos.To(value ? 1.0 : 0.0, 280, 1);
                if (!subscribed) { Animator.Add(this); subscribed = true; }
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public Md3Switch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Size = new Size(52, 32);
            BackColor = Color.Transparent;
            pos.Set(0);
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        public bool TickAnim()
        {
            bool busy = pos.Tick();
            Invalidate();
            if (!busy) { subscribed = false; return false; }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            double t = pos.Value;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            int w = 52, h = 32;
            Rectangle track = new Rectangle((Width - w) / 2, (Height - h) / 2, w - 1, h - 1);
            Color trackColor = Md3.LerpColor(P.SurfaceHighest, P.Primary, t);
            Md3.FillRound(g, track, Md3.RadSmall, trackColor);
            if (t < 0.99) Md3.DrawRound(g, track, Md3.RadSmall, Md3.Mix(P.Outline, P.Primary, t), 1.4f);
            int d = 24;
            int x0 = track.X + 3;
            int x1 = track.Right - d - 3;
            int x = (int)Math.Round(x0 + (x1 - x0) * t);
            Rectangle thumb = new Rectangle(x, track.Y + (track.Height - d) / 2, d - 1, d - 1);
            if (t < 0.5) Md3.FillRound(g, new Rectangle(thumb.X + 1, thumb.Y + 1, d - 3, d - 3), 12, P.SurfaceHigh);
            using (SolidBrush b = new SolidBrush(Md3.LerpColor(P.OutlineVariant, P.OnPrimary, t)))
                g.FillEllipse(b, thumb);
            if (hover)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(40, 128, 128, 128)))
                    g.FillEllipse(b, thumb.X - 4, thumb.Y - 4, d + 7, d + 7);
                using (SolidBrush b = new SolidBrush(Md3.LerpColor(P.OutlineVariant, P.OnPrimary, t)))
                    g.FillEllipse(b, thumb);
            }
        }
    }

    public class Md3Card : Panel
    {
        public Palette P;
        public int Radius = Md3.RadCard;
        public Color Fill = Color.Empty;

        public Md3Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            g.Clear(P.Surface);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Md3.FillRound(g, r, Radius, Fill == Color.Empty ? P.SurfaceLow : Fill);
        }
    }

    public class Md3Field : Control, Animator.ITick
    {
        public Palette P;
        public string Label;
        private TextBox box;
        private bool subscribed;
        private TweenValue focusT = new TweenValue();

        public Md3Field(string label, string initial, Palette p)
        {
            P = p;
            Label = label;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            focusT.Set(0);
            box = new TextBox();
            box.BorderStyle = BorderStyle.None;
            box.Font = new Font("Microsoft YaHei UI", 10F);
            box.BackColor = P.SurfaceHighest;
            box.ForeColor = P.OnSurface;
            box.Text = initial;
            box.Enter += delegate { focusT.To(1.0, 180, 2); if (!subscribed) { Animator.Add(this); subscribed = true; } Invalidate(); };
            box.Leave += delegate { focusT.To(0.0, 200, 2); Invalidate(); };
            Controls.Add(box);
        }

        public string Value
        {
            get { return box.Text; }
            set { box.Text = value; }
        }

        public void ApplyPalette()
        {
            box.BackColor = P.SurfaceHighest;
            box.ForeColor = P.OnSurface;
            Invalidate();
        }

        public bool TickAnim()
        {
            bool busy = focusT.Tick();
            Invalidate();
            if (!busy) { subscribed = false; return false; }
            return true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            box.SetBounds(12, 30, Math.Max(10, Width - 24), 20);
        }

        private Color ParentBg()
        {
            Md3Card card = Parent as Md3Card;
            if (card != null) return card.Fill == Color.Empty ? P.SurfaceLow : card.Fill;
            return P.Surface;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Md3.Prep(g);
            g.Clear(ParentBg());          // 必须跟父卡片同色，否则会出现"穿模"的色块
            using (SolidBrush lb = new SolidBrush(P.OnSurfaceVariant))
                g.DrawString(Label, new Font("Microsoft YaHei UI", 8.5F), lb, new PointF(2, 2));
            Rectangle r = new Rectangle(0, 20, Width - 1, Height - 21);
            Md3.FillRound(g, r, Md3.RadField, P.SurfaceHighest);
            if (focusT.Value > 0.01) Md3.DrawRound(g, r, Md3.RadField, Md3.WithAlpha(P.Primary, focusT.Value), 1.6f);
        }
    }

    public class Md3Chip : Control
    {
        public Palette P;
        public bool Active;

        public Md3Chip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color bg = Active ? P.PrimaryContainer : P.SurfaceHighest;
            Color fg = Active ? P.OnPrimaryContainer : P.OnSurfaceVariant;
            Md3.FillRound(g, r, Md3.RadChip, bg);
            int d = 8;
            int cy = Height / 2;
            using (SolidBrush b = new SolidBrush(Active ? P.Primary : P.Outline))
                g.FillEllipse(b, 14, cy - d / 2, d, d);
            Rectangle tr = new Rectangle(30, 0, Width - 36, Height);
            TextRenderer.DrawText(g, Text, Font, tr, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ============================================================
    //  非线性动画引擎（缓动曲线 + 60fps 驱动）
    // ============================================================
    public static class Anim
    {
        public static double Clamp01(double t) { return t < 0 ? 0 : (t > 1 ? 1 : t); }
        public static double OutCubic(double t) { t = Clamp01(t); return 1 - Math.Pow(1 - t, 3); }
        public static double InOutCubic(double t) { t = Clamp01(t); return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2; }
        public static double OutBack(double t) { t = Clamp01(t); double c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2); }
        public static double OutElastic(double t)
        {
            t = Clamp01(t);
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double c4 = 2 * Math.PI / 3.0;
            return Math.Pow(2, -10 * t) * Math.Sin((t * 10 - 0.75) * c4) + 1;
        }
        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        public static int LerpInt(double a, double b, double t) { return (int)Math.Round(Lerp(a, b, t)); }
        public static Color LerpColor(Color a, Color b, double t)
        {
            t = Clamp01(t);
            return Color.FromArgb(LerpInt(a.A, b.A, t), LerpInt(a.R, b.R, t), LerpInt(a.G, b.G, t), LerpInt(a.B, b.B, t));
        }
    }

    // 一个带缓动的补间值：To(target, ms, ease) → Tick() 逐步逼近，返回是否仍在动
    public class TweenValue
    {
        public double Value;
        private double from, to, durMs;
        private DateTime t0;
        private bool active;
        private int easing;

        public bool Active { get { return active; } }

        public void Set(double v) { Value = v; from = to = v; active = false; }

        public void To(double target, double ms, int ease)
        {
            if (!active && Math.Abs(target - Value) < 0.0008) return;
            from = Value; to = target; durMs = ms < 1 ? 1 : ms; t0 = DateTime.UtcNow; easing = ease; active = true;
        }

        public bool Tick()
        {
            if (!active) return false;
            double t = (DateTime.UtcNow - t0).TotalMilliseconds / durMs;
            if (t >= 1) { Value = to; active = false; return false; }
            double e;
            if (easing == 2) e = Anim.OutCubic(t);
            else if (easing == 3) e = Anim.OutBack(t);
            else if (easing == 4) e = Anim.OutElastic(t);
            else e = Anim.InOutCubic(t);
            Value = from + (to - from) * e;
            return true;
        }
    }

    public class Animator
    {
        public interface ITick { bool TickAnim(); }

        private static readonly List<ITick> items = new List<ITick>();
        private static System.Windows.Forms.Timer timer;

        public static void Add(ITick it)
        {
            if (items.Contains(it)) return;
            items.Add(it);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = 16;
                timer.Tick += delegate { Pump(); };
                timer.Start();
            }
        }

        public static void Remove(ITick it) { items.Remove(it); }

        private static void Pump()
        {
            for (int k = items.Count - 1; k >= 0; k--)
            {
                bool run;
                try { run = items[k].TickAnim(); }
                catch { run = false; }
                if (!run) items.RemoveAt(k);
            }
        }
    }

    // ============================================================
    //  状态指示灯（黄=待机 / 绿=正常 / 红=异常）
    // ============================================================
    public enum LedState { Idle, Running, Error }

    public class StatusLed : Control, Animator.ITick
    {
        public Palette P;
        private LedState state = LedState.Idle;
        private double phase;
        private bool subscribed;
        private TweenValue pop = new TweenValue();

        public StatusLed()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Size = new Size(28, 28);
            pop.Set(1.0);
        }

        public LedState State
        {
            get { return state; }
            set
            {
                if (state == value) return;
                state = value;
                pop.To(1.35, 300, 3);      // OutBack：先弹大再回落
                if (!subscribed) { Animator.Add(this); subscribed = true; }
                Invalidate();
            }
        }

        private Color LedColor()
        {
            if (state == LedState.Running) return Color.FromArgb(0x2E, 0x9E, 0x5B);   // 绿
            if (state == LedState.Error) return Color.FromArgb(0xC6, 0x28, 0x28);     // 红
            return Color.FromArgb(0xE0, 0xA0, 0x1B);                                  // 黄
        }

        public bool TickAnim()
        {
            bool busy = pop.Tick();
            if (!busy && Math.Abs(pop.Value - 1.0) > 0.002) { pop.To(1.0, 340, 1); busy = true; }
            if (state != LedState.Running) { phase += 0.16; busy = true; }
            else phase += 0.05;
            if (phase > 6283) phase = 0;
            Invalidate();
            return busy || state != LedState.Running;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Color c = LedColor();
            double pulse;
            if (state == LedState.Running) pulse = 0.10 + 0.10 * (0.5 - 0.5 * Math.Cos(phase * 0.5));
            else if (state == LedState.Error) pulse = 0.20 + 0.60 * (0.5 - 0.5 * Math.Cos(phase));
            else pulse = 0.12 + 0.40 * (0.5 - 0.5 * Math.Cos(phase * 0.75));
            int cx = Width / 2, cy = Height / 2;
            int r = (int)Math.Max(3, 5.0 * pop.Value);
            using (SolidBrush b = new SolidBrush(c))                     // 扁平：实心点，无光晕
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (Pen ring = new Pen(Color.FromArgb((int)(90 + 120 * pulse), c), 1.4f))
                g.DrawEllipse(ring, cx - r - 3, cy - r - 3, (r + 3) * 2, (r + 3) * 2);
        }
    }

    // ============================================================
    //  设置持久化（HKCU，不需要管理员）+ 开机自启
    // ============================================================
    public static class Settings
    {
        private const string KEY = @"Software\WaterCoolSim";
        private const string RUNKEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUNNAME = "WaterCoolSim";

        public static int Get(string name, int def)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KEY))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name, def);
                    return v == null ? def : Convert.ToInt32(v);
                }
            }
            catch { return def; }
        }

        public static void Set(string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KEY))
                    if (k != null) k.SetValue(name, value, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static bool GetBool(string n, bool d) { return Get(n, d ? 1 : 0) != 0; }
        public static void SetBool(string n, bool v) { Set(n, v ? 1 : 0); }

        public static bool AutoStartEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUNKEY))
                {
                    if (k != null && k.GetValue(RUNNAME) != null) return true;
                }
            }
            catch { }
            try { string c = StartupCmdPath(); return c != null && File.Exists(c); }
            catch { return false; }
        }

        public static string AutoStartCommand()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUNKEY))
                {
                    if (k == null) return null;
                    object v = k.GetValue(RUNNAME);
                    return v == null ? null : Convert.ToString(v);
                }
            }
            catch { return null; }
        }

        public static string LastError = "";

        private static string StartupCmdPath()
        {
            try
            {
                string d = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                if (string.IsNullOrEmpty(d)) return null;
                return Path.Combine(d, "WaterCoolSim.cmd");
            }
            catch { return null; }
        }

        public static bool SetAutoStart(bool on)
        {
            LastError = "";
            bool ok = false;
            string cmd = StartupCmdPath();
            // 首选：HKCU\...\Run（标准做法，任务管理器「启动」里可见）
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RUNKEY))
                {
                    if (k == null) throw new InvalidOperationException("CreateSubKey 返回 null");
                    if (on) k.SetValue(RUNNAME, "\"" + Application.ExecutablePath + "\" --autostart", RegistryValueKind.String);
                    else if (k.GetValue(RUNNAME) != null) k.DeleteValue(RUNNAME, false);
                }
                ok = true;
            }
            catch (Exception ex) { LastError = "Run 键写入失败 · " + ex.GetType().Name + ": " + ex.Message; }

            // 兜底：启动文件夹放一个 .cmd（有些机器的 Run 键被策略/安全软件锁住）
            try
            {
                if (on && !ok && cmd != null)
                {
                    File.WriteAllText(cmd,
                        "@echo off\r\nstart \"\" \"" + Application.ExecutablePath + "\" --autostart\r\n",
                        System.Text.Encoding.Default);
                    LastError += " | 已改用启动文件夹：" + cmd;
                    ok = true;
                }
                else if (!on)
                {
                    if (cmd != null && File.Exists(cmd)) File.Delete(cmd);
                    ok = true;
                }
                else if (on && ok && cmd != null && File.Exists(cmd))
                {
                    File.Delete(cmd);   // 注册表已成功，清掉兜底文件，避免重复启动
                }
            }
            catch (Exception ex2) { LastError += " | 启动文件夹失败：" + ex2.Message; }
            return ok;
        }
    }

    // ============================================================
    //  托盘图标（运行时绘制，无外部资源）
    // ============================================================
    public static class AppIcon
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Make(Color dot)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    Md3.Prep(g);
                    g.Clear(Color.Transparent);
                    // 水珠（与 exe 图标同一造型，缩小后仍可辨认）
                    using (GraphicsPath dp = new GraphicsPath())
                    {
                        dp.AddBezier(16, 3, 16, 3, 6.5f, 15.0f, 6.5f, 20.5f);
                        dp.AddBezier(6.5f, 20.5f, 6.5f, 26.8f, 10.9f, 30.0f, 16f, 30.0f);
                        dp.AddBezier(16f, 30.0f, 21.1f, 30.0f, 25.5f, 26.8f, 25.5f, 20.5f);
                        dp.AddBezier(25.5f, 20.5f, 25.5f, 15.0f, 16f, 3f, 16f, 3f);
                        dp.CloseFigure();
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(255, 0x0D, 0x65, 0x8A)))
                            g.FillPath(b, dp);
                        using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 0xCF, 0xEC, 0xF7)))
                            g.FillPath(b, dp);
                    }
                    using (Pen p = new Pen(Color.FromArgb(255, 0x0D, 0x65, 0x8A), 1.2f))
                    using (GraphicsPath dp2 = new GraphicsPath())
                    {
                        dp2.AddBezier(16, 3, 16, 3, 6.5f, 15.0f, 6.5f, 20.5f);
                        dp2.AddBezier(6.5f, 20.5f, 6.5f, 26.8f, 10.9f, 30.0f, 16f, 30.0f);
                        dp2.AddBezier(16f, 30.0f, 21.1f, 30.0f, 25.5f, 26.8f, 25.5f, 20.5f);
                        dp2.AddBezier(25.5f, 20.5f, 25.5f, 15.0f, 16f, 3f, 16f, 3f);
                        dp2.CloseFigure();
                        g.DrawPath(p, dp2);
                    }
                    // 右下角状态点（绿/黄/红）
                    using (SolidBrush b = new SolidBrush(dot))
                        g.FillEllipse(b, 19, 19, 13, 13);
                    using (Pen p = new Pen(Color.FromArgb(230, 255, 255, 255), 1.6f))
                        g.DrawEllipse(p, 19, 19, 13, 13);
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { DestroyIcon(h); }
            }
        }
    }

    // ============================================================
    //  关闭确认对话框（后台运行 / 直接关闭）
    // ============================================================
    public class CloseDialog : Form
    {
        public enum Choice { None, Tray, Exit }

        public Choice Result = Choice.None;
        public bool Remember;
        public Md3Button TrayButton;
        public Label TrayHint;
        private Palette P;
        private Point dragOffset;
        private bool dragging;

        public CloseDialog(Palette palette)
        {
            P = palette;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(440, 216);
            BackColor = P.SurfaceContainer;
            ShowInTaskbar = false;
            KeyPreview = true;
            DoubleBuffered = true;

            Label title = new Label();
            title.Text = "关闭窗口？";
            title.Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold);
            title.ForeColor = P.OnSurface;
            title.BackColor = Color.Transparent;
            title.SetBounds(24, 22, 380, 30);
            Controls.Add(title);

            Label body = new Label();
            body.Text = "最小化到托盘后台可继续运行（模拟不停）；\n直接关闭会停止模拟并把状态覆盖为「未连接」";
            body.Font = new Font("Microsoft YaHei UI", 9F);
            body.ForeColor = P.OnSurfaceVariant;
            body.BackColor = Color.Transparent;
            body.SetBounds(24, 60, 392, 52);
            Controls.Add(body);

            Md3Check chk = new Md3Check();
            chk.P = P;
            chk.SetBounds(24, 118, 20, 20);
            Controls.Add(chk);

            Label lblRemember = new Label();
            lblRemember.Text = "记住我的选择（下次不再询问）";
            lblRemember.Font = new Font("Microsoft YaHei UI", 8.5F);
            lblRemember.ForeColor = P.OnSurfaceVariant;
            lblRemember.BackColor = Color.Transparent;
            lblRemember.SetBounds(52, 118, 360, 20);
            Controls.Add(lblRemember);

            Md3Button bTray = new Md3Button();
            bTray.P = P;
            bTray.Kind = Md3Kind.Tonal;
            bTray.Text = "后台运行";
        TrayButton = bTray;
            bTray.Font = new Font("Microsoft YaHei UI", 9.5F);
            bTray.SetBounds(24, 150, 180, 44);
            bTray.Click += delegate { Result = Choice.Tray; Remember = chk.Checked; Close(); };
            Controls.Add(bTray);

            Md3Button bExit = new Md3Button();
            bExit.P = P;
            bExit.Kind = Md3Kind.Filled;
            bExit.Text = "直接关闭";
            bExit.Font = new Font("Microsoft YaHei UI", 9.5F);
            bExit.SetBounds(216, 150, 200, 44);
            bExit.Click += delegate { Result = Choice.Exit; Remember = chk.Checked; Close(); };
            Controls.Add(bExit);

            KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { Result = Choice.None; Close(); }
            };
            MouseDown += delegate(object s, MouseEventArgs e) { dragging = true; dragOffset = new Point(e.X, e.Y); };
            MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (dragging) { Left = Left + e.X - dragOffset.X; Top = Top + e.Y - dragOffset.Y; }
            };
            MouseUp += delegate { dragging = false; };
            Paint += delegate(object s, PaintEventArgs e)
            {
                Md3.Prep(e.Graphics);
                e.Graphics.Clear(P.SurfaceContainer);
                Md3.DrawRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Md3.RadDlg, P.OutlineVariant, 1f);
            };
            using (GraphicsPath gp0 = Md3.Round(new Rectangle(0, 0, Width, Height), Md3.RadDlg))
                Region = new Region(gp0);
            Shown += delegate { AnimateIn(); };
        }

        private void AnimateIn()
        {
            Opacity = 0;
            int y = Top;
            Top = y + 16;
            DateTime t0 = DateTime.UtcNow;
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
            tm.Interval = 16;
            tm.Tick += delegate
            {
                double t = (DateTime.UtcNow - t0).TotalMilliseconds / 240.0;
                if (t >= 1) { Opacity = 1; Top = y; tm.Stop(); tm.Dispose(); return; }
                double e = Anim.OutCubic(t);
                Opacity = e;
                Top = (int)(y + 16 - 16 * e);
            };
            tm.Start();
        }
    }

    // ============================================================
    //  自绘标题栏按钮（最小化 / 最大化 / 关闭）
    // ============================================================
    public class CaptionButton : Control, Animator.ITick
    {
        public enum Kind { Minimize, Maximize, Close }

        public Kind Mode = Kind.Close;
        public Palette P;
        private bool hover, press, subscribed, maximized;
        private TweenValue layer = new TweenValue();
        private TweenValue scale = new TweenValue();

        public bool MaximizedGlyph
        {
            get { return maximized; }
            set { if (maximized != value) { maximized = value; Invalidate(); } }
        }

        public CaptionButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(32, 32);
            scale.Set(1.0);
            layer.Set(0);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; layer.To(1.0, 140, 2); Sub(); Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; press = false; layer.To(0.0, 160, 2); scale.To(1.0, 240, 3); Sub(); Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { press = true; scale.To(0.88, 100, 1); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { press = false; scale.To(1.0, 240, 3); Invalidate(); base.OnMouseUp(e); }

        private void Sub() { if (!subscribed) { Animator.Add(this); subscribed = true; } }

        public bool TickAnim()
        {
            bool a = layer.Tick();
            bool b = scale.Tick();
            Invalidate();
            if (!a && !b && !hover && !press) { subscribed = false; return false; }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (Math.Abs(scale.Value - 1.0) > 0.002)
            {
                int dw = (int)(Width * (1.0 - scale.Value) / 2.0);
                int dh = (int)(Height * (1.0 - scale.Value) / 2.0);
                r = new Rectangle(r.X + dw, r.Y + dh, Math.Max(4, r.Width - dw * 2), Math.Max(4, r.Height - dh * 2));
            }
            int rad = Md3.RadSmall;
            if (layer.Value > 0.01)
            {
                Color bg = Mode == Kind.Close ? P.ErrorContainer : P.SurfaceHigh;
                Md3.FillRound(g, r, rad, Md3.WithAlpha(bg, Math.Max(0.3, layer.Value)));
            }
            Color fg = (Mode == Kind.Close && hover) ? P.Error : P.OnSurfaceVariant;
            using (Pen p = new Pen(fg, 1.6f))
            {
                p.StartCap = LineCap.Round;
                p.EndCap = LineCap.Round;
                int cx = Width / 2, cy = Height / 2;
                if (Mode == Kind.Minimize)
                    g.DrawLine(p, cx - 6, cy, cx + 6, cy);
                else if (Mode == Kind.Close)
                {
                    g.DrawLine(p, cx - 5, cy - 5, cx + 5, cy + 5);
                    g.DrawLine(p, cx + 5, cy - 5, cx - 5, cy + 5);
                }
                else if (maximized)
                {
                    g.DrawRectangle(p, cx - 6, cy - 3, 9, 9);
                    g.DrawRectangle(p, cx - 3, cy - 6, 9, 9);
                }
                else
                    g.DrawRectangle(p, cx - 5, cy - 5, 10, 10);
            }
        }
    }

    // ============================================================
    //  M3 复选框
    // ============================================================
    public class Md3Check : Control, Animator.ITick
    {
        public Palette P;
        private bool checkedFlag, subscribed, hover;
        private TweenValue fill = new TweenValue();

        public bool Checked
        {
            get { return checkedFlag; }
            set
            {
                if (checkedFlag == value) return;
                checkedFlag = value;
                fill.To(value ? 1.0 : 0.0, 220, 3);
                if (!subscribed) { Animator.Add(this); subscribed = true; }
                Invalidate();
            }
        }

        public Md3Check()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(22, 22);
            fill.Set(0);
        }

        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        public bool TickAnim()
        {
            bool busy = fill.Tick();
            Invalidate();
            if (!busy) { subscribed = false; return false; }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Rectangle r = new Rectangle(1, 1, Width - 3, Height - 3);
            double v = fill.Value;
            Md3.FillRound(g, r, Md3.Rad, Md3.LerpColor(P.SurfaceHighest, P.Primary, v));
            if (v < 0.98) Md3.DrawRound(g, r, Md3.Rad, hover ? P.OnSurface : P.Outline, 1.6f);
            if (v > 0.15)
            {
                using (Pen p = new Pen(Color.FromArgb((int)(255 * Math.Min(1, v)), 255, 255, 255), 2f))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    int cx = Width / 2, cy = Height / 2;
                    g.DrawLine(p, cx - 5, cy, cx - 1, cy + 4);
                    g.DrawLine(p, cx - 1, cy + 4, cx + 5, cy - 4);
                }
            }
        }
    }

    // ============================================================
    //  主窗口（Material 3 风格，颜色来自谷歌 Monet 取色）
    // ============================================================

    // ============================================================
    //  「本机信息」页：系统 / 硬件 / 控制中心 / 环境自检
    // ============================================================
    public static class MachineInfo
    {
        private static string Q(string wmiClass, string prop)
        {
            try
            {
                using (System.Management.ManagementObjectSearcher s =
                    new System.Management.ManagementObjectSearcher("SELECT " + prop + " FROM " + wmiClass))
                {
                    foreach (System.Management.ManagementBaseObject o in s.Get())
                    {
                        object v = o[prop];
                        if (v != null) return Convert.ToString(v);
                    }
                }
            }
            catch { }
            return "(未知)";
        }

        public static string Collect()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("── 系统 ──");
            sb.AppendLine("操作系统   : " + Environment.OSVersion.VersionString
                + (Environment.Is64BitOperatingSystem ? "  64 位" : "  32 位"));
            int rel = SelfCheck.RegDword(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", 0);
            sb.AppendLine("运行环境   : .NET Framework Release=" + rel + "（CLR " + Environment.Version.ToString() + "）");
            sb.AppendLine("本程序     : " + (Environment.Is64BitProcess ? "64 位进程" : "32 位进程"));
            sb.AppendLine();
            sb.AppendLine("── 硬件 ──");
            sb.AppendLine("机型       : " + Q("Win32_ComputerSystem", "Manufacturer") + " " + Q("Win32_ComputerSystem", "Model"));
            sb.AppendLine("CPU        : " + Q("Win32_Processor", "Name"));
            try
            {
                ulong ram = Convert.ToUInt64(Q("Win32_ComputerSystem", "TotalPhysicalMemory"));
                sb.AppendLine("内存       : " + (ram / 1024 / 1024 / 1024) + " GB");
            }
            catch { sb.AppendLine("内存       : (未知)"); }
            sb.AppendLine("显卡       : " + Q("Win32_VideoController", "Name"));
            sb.AppendLine("主板       : " + Q("Win32_BaseBoard", "Product") + " / BIOS " + Q("Win32_BIOS", "SMBIOSBIOSVersion"));
            sb.AppendLine();
            sb.AppendLine("── 控制中心 / 环境 ──");
            string bridge = @"C:\Program Files\OEM\机械革命控制中心\AiStoneService\GCUBridge.exe";
            sb.AppendLine("GCUBridge  : " + (File.Exists(bridge) ? "已安装（" + bridge + "）" : "默认路径未找到"));
            sb.AppendLine("水冷门禁   : LiquidCoolingSupport="
                + SelfCheck.RegDword(@"SOFTWARE\OEM\GamingCenter2\ItemSupport", "LiquidCoolingSupport", -1)
                + "   AutoMode=" + SelfCheck.RegDword(@"SOFTWARE\OEM\GamingCenter2\ItemSupport", "LiquidCoolingAutoModeSupport", -1));
            sb.AppendLine("BIOS 项目号: " + SelfCheck.RegDword(@"SOFTWARE\OEM\GamingCenter2\ItemSupport", "BIOS_PROJECT_ID", 0));
            sb.AppendLine("托盘图标   : 由本程序运行时绘制，无外部资源");
            sb.AppendLine("开机自启动 : " + (Settings.AutoStartEnabled() ? "已启用（HKCU Run: " + Settings.AutoStartCommand() + "）" : "未启用"));
            sb.AppendLine();
            sb.AppendLine("（下面这段是刚才自动跑的自检结果）");
            return sb.ToString();
        }
    }

    // ============================================================
    //  左侧导航项
    // ============================================================
    public class NavItem : Control, Animator.ITick
    {
        public string Title = "";
        public Palette P;
        public bool Active;
        private bool hover, subscribed;
        private TweenValue sel = new TweenValue();   // 选中胶囊 0..1

        public NavItem()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
            sel.Set(0);
        }

        public void SetActive(bool on)
        {
            if (Active == on) return;
            Active = on;
            sel.To(on ? 1.0 : 0.0, 240, 1);          // InOutCubic
            if (!subscribed) { Animator.Add(this); subscribed = true; }
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        public bool TickAnim()
        {
            bool busy = sel.Tick();
            Invalidate();
            if (!busy) { subscribed = false; return false; }
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (P == null) { base.OnPaint(e); return; }
            Graphics g = e.Graphics;
            Md3.Prep(g);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            if (sel.Value > 0.01)
            {
                Md3.FillRound(g, r, Md3.Rad, Md3.LerpColor(Color.FromArgb(0, 0, 0, 0), P.SecondaryContainer, sel.Value));
                using (SolidBrush b = new SolidBrush(Md3.WithAlpha(P.Primary, sel.Value)))
                    Md3.FillRound(g, new Rectangle(6, Height / 2 - 9, 4, 18), 2, Md3.WithAlpha(P.Primary, sel.Value));
            }
            else if (hover)
            {
                Md3.FillRound(g, r, Md3.Rad, Md3.WithAlpha(P.StateHover, 0.55));
            }
            Color fg = Md3.LerpColor(P.OnSurfaceVariant, P.OnSecondaryContainer, sel.Value);
            Rectangle tr = new Rectangle(22, 0, Width - 28, Height);
            TextRenderer.DrawText(g, Title, Font, tr, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ============================================================
    //  主窗口：左侧导航 + 四个页面
    // ============================================================
    public class MainForm : Form
    {
        private Palette P;
        private CaptionButton btnMin, btnMax, btnClose;
        private NavItem[] nav;
        private Panel pageSim, pageInfo, pageSet, pageAbout;
        private StatusLed led;
        private Md3Chip chip;

        private Md3Button btnStart, btnStop, btnRecheck, btnRefresh;
        private Md3Switch swTray, swAuto, swAutoSim, swCleanStart, swSuppress;
        private Md3Field fHost, fPort, fSlot, fMac, fFw, fInterval;
        private Md3Card cardSet, cardLog;
        private Label lblCardSet, lblLog, lblStats, lblHint;
        private Label lblSw1, lblSw2, lblSw3, lblSw4, lblSw5;
        private TextBox txtLog, txtInfo, txtAbout;
        private System.Windows.Forms.Timer timer;
        private NotifyIcon tray;
        private Icon trayIcon;
        private bool balloonShown;
        private bool trayOk;
        private int trayTries;
        private SimCore core;
        private bool started, exiting;
        private int page;

        public MainForm()
        {
            P = Monet.Build(Monet.IsSystemDarkTheme());
            Text = "虚拟水冷坞 · 机械革命控制中心";
            ClientSize = new Size(980, 700);
            MinimumSize = new Size(840, 600);
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = P.Surface;
            ForeColor = P.OnSurface;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);

            led = new StatusLed();
            led.P = P;
            led.State = LedState.Idle;
            Controls.Add(led);
            chip = new Md3Chip();
            chip.P = P;
            chip.Font = new Font("Microsoft YaHei UI", 9F);
            chip.Text = "未启动";
            Controls.Add(chip);

            btnMin = new CaptionButton();
            btnMin.P = P;
            btnMin.Mode = CaptionButton.Kind.Minimize;
            btnMin.Click += delegate { WindowState = FormWindowState.Minimized; };
            Controls.Add(btnMin);
            btnMax = new CaptionButton();
            btnMax.P = P;
            btnMax.Mode = CaptionButton.Kind.Maximize;
            btnMax.Click += delegate { ToggleMax(); };
            Controls.Add(btnMax);
            btnClose = new CaptionButton();
            btnClose.P = P;
            btnClose.Mode = CaptionButton.Kind.Close;
            btnClose.Click += delegate { Close(); };
            Controls.Add(btnClose);

            // ---- 左侧导航 ----
            string[] titles = new string[] { "模拟水冷", "本机信息", "软件设置", "关于" };
            nav = new NavItem[4];
            for (int i = 0; i < 4; i++)
            {
                NavItem it = new NavItem();
                it.P = P;
                it.Title = titles[i];
                it.Font = new Font("Microsoft YaHei UI", 10F);
                int idx = i;
                it.Click += delegate { SwitchPage(idx); };
                Controls.Add(it);
                nav[i] = it;
            }

            // ---- 页面 1：模拟水冷 ----
            pageSim = MkPage();
            btnStart = MkButton("启动模拟", Md3Kind.Filled, 10F, FontStyle.Bold);
            btnStart.Click += new EventHandler(OnStart);
            pageSim.Controls.Add(btnStart);
            btnStop = MkButton("停止模拟", Md3Kind.Tonal, 10F, FontStyle.Regular);
            btnStop.Enabled = false;
            btnStop.Click += new EventHandler(OnStop);
            pageSim.Controls.Add(btnStop);
            btnRecheck = MkButton("复查", Md3Kind.Outlined, 9F, FontStyle.Regular);
            btnRecheck.Click += new EventHandler(OnRecheck);
            pageSim.Controls.Add(btnRecheck);

            cardLog = new Md3Card();
            cardLog.P = P;
            pageSim.Controls.Add(cardLog);
            lblLog = MkLabel("运行日志", 11F, FontStyle.Bold, P.OnSurface, true);
            cardLog.Controls.Add(lblLog);
            txtLog = MkTextBox(P.SurfaceLow, P.OnSurfaceVariant, new Font("Consolas", 9.5F));
            cardLog.Controls.Add(txtLog);

            // ---- 页面 2：本机信息 ----
            pageInfo = MkPage();
            btnRefresh = MkButton("重新检测", Md3Kind.Outlined, 9F, FontStyle.Regular);
            btnRefresh.Click += delegate { RefreshInfo(); };
            pageInfo.Controls.Add(btnRefresh);
            txtInfo = MkTextBox(P.SurfaceLow, P.OnSurfaceVariant, new Font("Microsoft YaHei UI", 9F));
            pageInfo.Controls.Add(txtInfo);

            // ---- 页面 3：软件设置 ----
            pageSet = MkPage();
            cardSet = new Md3Card();
            cardSet.P = P;
            pageSet.Controls.Add(cardSet);
            lblCardSet = MkLabel("连接参数与开关", 11F, FontStyle.Bold, P.OnSurface, true);
            cardSet.Controls.Add(lblCardSet);

            fHost = new Md3Field("主机", "127.0.0.1", P); cardSet.Controls.Add(fHost);
            fPort = new Md3Field("端口", "13688", P); cardSet.Controls.Add(fPort);
            fSlot = new Md3Field("clientId 编号 N", "6", P); cardSet.Controls.Add(fSlot);
            fInterval = new Md3Field("重发间隔(秒)", "1", P); cardSet.Controls.Add(fInterval);
            fMac = new Md3Field("设备 MAC（伪装值）", "AA:BB:CC:00:11:22", P); cardSet.Controls.Add(fMac);
            fFw = new Md3Field("固件版本（需 >= 23 字符）", "CoolingSystem LCT21001-SIM-v1.0.0", P); cardSet.Controls.Add(fFw);

            swTray = MkSwitch(Settings.GetBool("Tray", true), cardSet);
            lblSw1 = MkLabel("启用托盘图标（关闭窗口可后台运行）", 9F, FontStyle.Regular, P.OnSurfaceVariant, true);
            cardSet.Controls.Add(lblSw1);
            swAuto = MkSwitch(Settings.AutoStartEnabled(), cardSet);
            lblSw2 = MkLabel("开机自启动", 9F, FontStyle.Regular, P.OnSurfaceVariant, true);
            cardSet.Controls.Add(lblSw2);
            swAutoSim = MkSwitch(Settings.GetBool("AutoSim", true), cardSet);
            lblSw3 = MkLabel("自启动时同时开始模拟", 9F, FontStyle.Regular, P.OnSurfaceVariant, true);
            cardSet.Controls.Add(lblSw3);
            swCleanStart = MkSwitch(Settings.GetBool("CleanStart", true), cardSet);
            lblSw4 = MkLabel("启动时清理上次残留", 9F, FontStyle.Regular, P.OnSurfaceVariant, true);
            cardSet.Controls.Add(lblSw4);
            swSuppress = MkSwitch(Settings.GetBool("Suppress", true), cardSet);
            lblSw5 = MkLabel("压制真实服务的「未连接」上报", 9F, FontStyle.Regular, P.OnSurfaceVariant, true);
            cardSet.Controls.Add(lblSw5);

            swTray.CheckedChanged += delegate
            {
                Settings.SetBool("Tray", swTray.Checked);
                Log("[设置] 托盘图标：" + (swTray.Checked ? "启用" : "停用"));
                UpdateTray();
            };
            swAuto.CheckedChanged += delegate
            {
                bool ok = Settings.SetAutoStart(swAuto.Checked);
                Log("[设置] 开机自启动：" + (swAuto.Checked ? (ok ? "已写入 " + Settings.AutoStartCommand() : "写入失败：" + Settings.LastError) : "已移除"));
                RefreshInfo();
            };
            swAutoSim.CheckedChanged += delegate { Settings.SetBool("AutoSim", swAutoSim.Checked); Log("[设置] 自启动时同时开始模拟：" + (swAutoSim.Checked ? "开" : "关")); };
            swCleanStart.CheckedChanged += delegate { Settings.SetBool("CleanStart", swCleanStart.Checked); Log("[设置] 启动时清理上次残留：" + (swCleanStart.Checked ? "开" : "关")); };
            swSuppress.CheckedChanged += delegate { Settings.SetBool("Suppress", swSuppress.Checked); Log("[设置] 压制真实上报：" + (swSuppress.Checked ? "开" : "关")); };

            // ---- 页面 4：关于 ----
            pageAbout = MkPage();
            txtAbout = MkTextBox(P.SurfaceLow, P.OnSurfaceVariant, new Font("Microsoft YaHei UI", 9F));
            txtAbout.Text = AboutDialog.Body.Replace("\n", "\r\n");
            pageAbout.Controls.Add(txtAbout);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += new EventHandler(OnTick);

            FormClosing += new FormClosingEventHandler(OnClosing);
            Shown += delegate
            {
                LayoutForSize();
                if (Program.AutoStartLaunch && Settings.GetBool("Tray", true)) HideToTray(true);
                AnimateIn();
                if (Program.AutoStartLaunch && swAutoSim.Checked)
                {
                    System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
                    once.Interval = 1800;
                    once.Tick += delegate
                    {
                        once.Stop(); once.Dispose();
                        if (!started) { Log("[自启动] 按设置自动开始模拟"); OnStart(this, EventArgs.Empty); }
                    };
                    once.Start();
                }
            };

            // 自启动项自愈：若已启用但指向的不是当前路径，重写一遍
            if (Settings.AutoStartEnabled() && Settings.AutoStartCommand().IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) < 0)
            {
                Settings.SetAutoStart(true);
            }

            SwitchPage(0);
            UpdateTray();
            Log("提示：控制中心界面上的按钮就是给这台虚拟水冷坞用的；本程序只是替它对 broker 说话。");
            Log("左侧可切换：模拟水冷 / 本机信息 / 软件设置 / 关于。启动时会自动做环境自检与连接测试。");
            if (swCleanStart.Checked && !Program.NoStartupCleanup) CleanStaleAsync();
            RunEnvCheckAsync();
            RefreshInfo();
        }

        private Panel MkPage()
        {
            Panel p = new Panel();
            p.BackColor = P.Surface;
            Controls.Add(p);
            return p;
        }

        private TextBox MkTextBox(Color bg, Color fg, Font font)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = bg;
            t.ForeColor = fg;
            t.Font = font;
            return t;
        }

        private Label MkLabel(string text, float size, FontStyle style, Color color, bool onCard)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", size, style);
            l.ForeColor = color;
            l.AutoSize = false;
            l.Tag = onCard ? "card" : "form";
            l.BackColor = onCard ? P.SurfaceLow : Color.Transparent;
            return l;
        }

        private Md3Button MkButton(string text, Md3Kind kind, float size, FontStyle style)
        {
            Md3Button b = new Md3Button();
            b.P = P;
            b.Kind = kind;
            b.Text = text;
            b.Font = new Font("Microsoft YaHei UI", size, style);
            return b;
        }

        private Md3Switch MkSwitch(bool on, Control parent)
        {
            Md3Switch s = new Md3Switch();
            s.P = P;
            s.Checked = on;
            parent.Controls.Add(s);
            return s;
        }

        public void ShowPage(int idx) { SwitchPage(idx); }

        private static bool Elevated
        {
            get
            {
                try
                {
                    return new System.Security.Principal.WindowsPrincipal(
                        System.Security.Principal.WindowsIdentity.GetCurrent())
                        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        // 以管理员身份重启自己（会触发一次系统/安全软件的确认框；仅在你同意后调用）
        public static bool RelaunchElevated()
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                System.Diagnostics.Process.Start(psi);
                return true;
            }
            catch { return false; }
        }

        private void SwitchPage(int idx)
        {
            page = idx;
            for (int i = 0; i < nav.Length; i++) nav[i].SetActive(i == idx);
            pageSim.Visible = idx == 0;
            pageInfo.Visible = idx == 1;
            pageSet.Visible = idx == 2;
            pageAbout.Visible = idx == 3;
            if (idx == 1) RefreshInfo();
            LayoutForSize();
        }

        private void RefreshInfo()
        {
            string rep = "";
            try { rep = MachineInfo.Collect(); } catch (Exception ex) { rep = "采集失败：" + ex.Message + "\r\n"; }
            StringBuilder sb = new StringBuilder(rep);
            try
            {
                EnvReport r = SelfCheck.RunEnv(new Action<string>(delegate(string m) { sb.AppendLine(m); }),
                    fHost.Value.Trim(), PortOf());
                sb.AppendLine();
                sb.AppendLine("── 托盘 / 自启动 状态 ──");
                sb.AppendLine("托盘图标   : " + (tray != null && tray.Visible ? "已在程序中启用" : (swTray.Checked ? "未创建（异常）" : "已按设置关闭")));
                sb.AppendLine("外壳登记   : " + (tray != null && tray.Visible ? TrayShellCheck() : "(未启用托盘)"));
                sb.AppendLine("隐藏到托盘 : " + (trayOk ? "可用" : "不可用（已被程序自动禁用，避免窗口找不回）"));
                sb.AppendLine("自启动注册表: " + (Settings.AutoStartCommand() ?? "(无)"));
                string sc = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                sb.AppendLine("启动文件夹  : " + (File.Exists(Path.Combine(sc, "WaterCoolSim.cmd")) ? "有 WaterCoolSim.cmd（兜底方式生效）" : "无"));
                sb.AppendLine("自启动开关 : " + (swAuto.Checked ? "开" : "关"));
                if (Settings.LastError.Length > 0) sb.AppendLine("最近一次写入: " + Settings.LastError);
            }
            catch (Exception ex) { sb.AppendLine("自检异常：" + ex.Message); }
            txtInfo.Text = sb.ToString().Replace("\n", "\r\n");
        }

        private int PortOf()
        {
            int p;
            if (!int.TryParse(fPort.Value.Trim(), out p)) p = 13688;
            return p;
        }

        // 托盘登记被拦时，问一次「要不要以管理员身份重启」
        private bool askedElevate;
        private void AskElevateForTray()
        {
            if (askedElevate || Elevated) return;
            askedElevate = true;
            int pref = Settings.Get("AskElevate", 0);     // 0=问 1=总是提权 2=不再问
            if (pref == 2) return;
            bool yes = pref == 1;
            if (pref == 0)
            {
                DialogResult dr = MessageBox.Show(
                    "托盘图标没有登记成功（多半是安全软件/权限拦住了）。\n\n" +
                    "要以管理员身份重新启动本程序吗？这样托盘图标就能登记成功。\n" +
                    "（选「否」也能正常用，关闭窗口时会最小化到任务栏；再双击一次 exe 可唤回窗口）",
                    "虚拟水冷坞 · 托盘图标",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                yes = dr == DialogResult.Yes;
            }
            if (!yes) { Log("[托盘] 你选择继续以普通权限运行（托盘图标可能不可用）"); return; }
            Log("[托盘] 按你的选择：以管理员身份重启");
            if (RelaunchElevated()) ReallyExit();
            else Log("[托盘] 提权启动被拒绝或失败，继续普通权限运行");
        }

        // ---------------- 托盘 ----------------
        private void UpdateTray() { UpdateTray(false); }

        private void UpdateTray(bool quiet)
        {
            if (!swTray.Checked)
            {
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
                tray = null;
                try { if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; } } catch { }
                trayOk = false;
                return;
            }
            if (tray == null)
            {
                NotifyIcon ni = new NotifyIcon();
                ni.Icon = AppIcon.Make(Color.FromArgb(0xE0, 0xA0, 0x1B));    // 先给图标，再显示
                ni.Text = "虚拟水冷坞 · 未启动";
                ContextMenuStrip menu = new ContextMenuStrip();
                menu.Items.Add("显示主窗口", null, delegate { ShowFromTray(); });
                menu.Items.Add("启动模拟", null, delegate { ShowFromTray(); OnStart(this, EventArgs.Empty); });
                menu.Items.Add("停止模拟", null, delegate { OnStop(this, EventArgs.Empty); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("关于", null, delegate { ShowFromTray(); SwitchPage(3); });
                menu.Items.Add("重新登记托盘图标", null, delegate { trayTries = 0; UpdateTray(true); Log("[托盘] 手动触发重新登记"); });
                menu.Items.Add("关闭窗口时恢复询问", null, delegate
                {
                    Settings.Set("CloseAction", 0);
                    Log("[设置] 已恢复：下次点 ✕ 重新弹出选择框");
                });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出（停止模拟并清理）", null, delegate { ReallyExit(); });
                ni.ContextMenuStrip = menu;
                ni.DoubleClick += delegate { ShowFromTray(); };
                ni.Visible = true;
                tray = ni;
                trayIcon = ni.Icon;
                if (!quiet) Log("[托盘] 图标已创建（右键可操作，双击回到窗口）");
                try
                {
                    ni.BalloonTipTitle = "虚拟水冷坞已在托盘运行";
                    ni.BalloonTipText = "若任务栏没看到图标，请点任务栏的 ^ 溢出区把它拖出来固定；本程序也会自动尝试设为常显。";
                    ni.ShowBalloonTip(4000);
                }
                catch { }
                VerifyTray();
            }
            UpdateTrayIcon();
        }

        // 自己登记 + 自检 + 失败自动重试 + 尝试提升为任务栏常显
        private void VerifyTray()
        {
            if (tray == null) return;
            trayTries++;
            string check = TrayShellCheck();
            trayOk = check.StartsWith("已登记");
            Log("[托盘] 第 " + trayTries + " 次登记自检：" + check);
            if (trayOk)
            {
                PromoteToTaskbar();
                Log("[托盘] 任务栏常显：" + LastPromote);
                return;
            }
            if (trayTries >= 6)
            {
                Log("[托盘] 连续 " + trayTries + " 次都没登记成功（多为权限/安全软件拦截）。仍按你的选择：点 ✕ 会最小化到托盘；若托盘没图标，再双击一次本程序即可唤回窗口");
                AskElevateForTray();
                return;
            }
            System.Windows.Forms.Timer rt = new System.Windows.Forms.Timer();
            rt.Interval = 2500;
            rt.Tick += delegate
            {
                rt.Stop(); rt.Dispose();
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
                tray = null;
                try { if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; } } catch { }
                UpdateTray(true);
            };
            rt.Start();
        }
        private void UpdateTrayIcon()
        {
            if (tray == null) return;
            Color dot = led.State == LedState.Running ? Color.FromArgb(0x2E, 0x9E, 0x5B)
                      : (led.State == LedState.Error ? Color.FromArgb(0xC6, 0x28, 0x28) : Color.FromArgb(0xE0, 0xA0, 0x1B));
            Icon old = trayIcon;
            trayIcon = AppIcon.Make(dot);
            tray.Icon = trayIcon;
            tray.Text = "虚拟水冷坞 · " + chip.Text;
            if (old != null) old.Dispose();
        }

        private void ShowFromTray()
        {
            ShowInTaskbar = true;
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        private void HideToTray(bool quiet)
        {
            if (tray == null) return;
            Hide();
            ShowInTaskbar = false;
            if (!quiet && !balloonShown)
            {
                balloonShown = true;
                tray.BalloonTipTitle = "虚拟水冷坞仍在后台运行";
                tray.BalloonTipText = "双击托盘图标回到窗口；右键可启动/停止模拟或退出。";
                tray.ShowBalloonTip(2000);
            }
        }

        private void AnimateIn()
        {
            Opacity = 0;
            int y = Top;
            Top = y + 18;
            DateTime t0 = DateTime.UtcNow;
            System.Windows.Forms.Timer tm = new System.Windows.Forms.Timer();
            tm.Interval = 16;
            tm.Tick += delegate
            {
                double t = (DateTime.UtcNow - t0).TotalMilliseconds / 320.0;
                if (t >= 1) { Opacity = 1; Top = y; tm.Stop(); tm.Dispose(); return; }
                double e = Anim.OutCubic(t);
                Opacity = e;
                Top = (int)(y + 18 - 18 * e);
            };
            tm.Start();
        }

        // ---------------- 自绘 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Md3.Prep(g);
            // 统一底色：标题条 / 导航栏 / 内容区都是同一个底色，只留发丝分隔线（不再堆多层色块）
            using (SolidBrush bg = new SolidBrush(P.Surface))
                g.FillRectangle(bg, 0, 0, Width, Height);
            using (Pen ln = new Pen(P.OutlineVariant, 1f))
            {
                g.DrawLine(ln, 0, 40, Width, 40);          // 标题条下边一条发丝线
                g.DrawLine(ln, 176, 40, 176, Height);      // 导航栏右侧一条发丝线
            }
            if (WindowState == FormWindowState.Maximized)
            {
                using (Pen bd = new Pen(P.OutlineVariant, 1f))
                    g.DrawRectangle(bd, 0, 0, Width - 1, Height - 1);
            }
            else
            {
                Md3.DrawRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Md3.RadWin, P.OutlineVariant, 1f);
            }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00C00000;  // 明确去掉 WS_CAPTION：避免失去焦点时系统画出灰色标题条
                cp.Style |= 0x00010000;   // WS_MAXIMIZEBOX
                cp.Style |= 0x00020000;   // WS_MINIMIZEBOX
                cp.Style |= 0x00040000;   // WS_THICKFRAME（配合 WM_NCHITTEST 缩放）
                return cp;
            }
        }

        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCAPTION = 2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct WRECT { public int Left; public int Top; public int Right; public int Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NCCALCSIZEPARAMS { public WRECT rgrc0; public WRECT rgrc1; public WRECT rgrc2; public IntPtr lppos; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out WRECT r);

        // ---- 托盘外壳自检：Shell_NotifyIconGetRect 能查到就说明 explorer 真的登记了图标 ----
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NID { public int cbSize; public IntPtr hWnd; public uint uID; public Guid guidItem; }

        private delegate bool EnumProc(IntPtr h, IntPtr l);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc cb, IntPtr l);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int Shell_NotifyIconGetRect(ref NID id, out WRECT rect);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint RegisterWindowMessage(string msg);

        private static readonly uint WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");

        // Win11：把图标从「溢出区」提升为任务栏常显（HKCU\Control Panel\NotifyIconSettings\<项>\IsPromoted=1）
        public static string LastPromote = "";

        public static bool PromoteToTaskbar()
        {
            try
            {
                string exe = Application.ExecutablePath;
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) { LastPromote = "没有 NotifyIconSettings 键（系统还没为任何图标建过设置项）"; return false; }
                    foreach (string name in root.GetSubKeyNames())
                    {
                        using (RegistryKey k = root.OpenSubKey(name, true))
                        {
                            if (k == null) continue;
                            string p = Convert.ToString(k.GetValue("ExecutablePath"));
                            if (string.IsNullOrEmpty(p)) continue;
                            if (string.Equals(p.Trim('"', ' '), exe, StringComparison.OrdinalIgnoreCase))
                            {
                                object cur = k.GetValue("IsPromoted");
                                if (cur is int && (int)cur == 1) { LastPromote = "已是常显（" + name + "）"; return true; }
                                k.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                LastPromote = "已写入 IsPromoted=1（" + name + "）";
                                return true;
                            }
                        }
                    }
                }
                LastPromote = "还没有本程序的图标设置项（先在托盘登记成功后系统才会建）";
                return false;
            }
            catch (Exception ex) { LastPromote = "写入失败：" + ex.GetType().Name + ": " + ex.Message; return false; }
        }

        public static string TrayShellCheck()
        {
            try
            {
                uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
                System.Collections.Generic.List<IntPtr> wins = new System.Collections.Generic.List<IntPtr>();
                EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == me) wins.Add(h);
                    return true;
                }, IntPtr.Zero);
                foreach (IntPtr h in wins)
                {
                    for (uint id = 1; id <= 8; id++)
                    {
                        NID ni = new NID();
                        ni.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(NID));
                        ni.hWnd = h;
                        ni.uID = id;
                        WRECT r;
                        int hr = Shell_NotifyIconGetRect(ref ni, out r);
                        if (hr == 0)
                            return "已登记（hWnd=0x" + h.ToString("X") + " uID=" + id + "，托盘位置 "
                                + r.Left + "," + r.Top + " 尺寸 " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + "）";
                    }
                }
                return "外壳未登记（图标没有进通知区域）";
            }
            catch (Exception ex) { return "自检异常：" + ex.Message; }
        }

        protected override void WndProc(ref Message m)
        {
            // 无边框窗口：别让系统在激活/失活时重画标题条（否则失焦会冒出一条灰色带）
            if (m.Msg == 0x0085) { m.Result = IntPtr.Zero; return; }        // WM_NCPAINT
            if (m.Msg == 0x0086) { m.Result = (IntPtr)1; return; }          // WM_NCACTIVATE
            if (WM_TASKBARCREATED != 0 && m.Msg == (int)WM_TASKBARCREATED)
            {
                // 资源管理器重启 / 任务栏重建：自己重新登记一次
                Log("[托盘] 收到 TaskbarCreated：自动重新登记图标");
                try { if (tray != null) { tray.Visible = false; tray.Dispose(); } } catch { }
                tray = null;
                try { if (trayIcon != null) { trayIcon.Dispose(); trayIcon = null; } } catch { }
                trayTries = 0;
                UpdateTray(true);
                return;
            }
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero && WindowState != FormWindowState.Maximized)
            {
                WRECT wr;
                if (GetWindowRect(Handle, out wr))
                {
                    NCCALCSIZEPARAMS np = (NCCALCSIZEPARAMS)System.Runtime.InteropServices.Marshal
                        .PtrToStructure(m.LParam, typeof(NCCALCSIZEPARAMS));
                    np.rgrc0 = wr;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(np, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                long lp = (long)m.LParam;
                int sx = unchecked((short)(lp & 0xFFFF));
                int sy = unchecked((short)((lp >> 16) & 0xFFFF));
                Point p = PointToClient(new Point(sx, sy));
                int b = 8;
                bool left = p.X <= b, right = p.X >= ClientSize.Width - b;
                bool top = p.Y <= b, bottom = p.Y >= ClientSize.Height - b;
                int hit = 0;
                if (top && left) hit = HTTOPLEFT;
                else if (top && right) hit = HTTOPRIGHT;
                else if (bottom && left) hit = HTBOTTOMLEFT;
                else if (bottom && right) hit = HTBOTTOMRIGHT;
                else if (left) hit = HTLEFT;
                else if (right) hit = HTRIGHT;
                else if (top) hit = HTTOP;
                else if (bottom) hit = HTBOTTOM;
                else if (p.Y <= 40) hit = HTCAPTION;
                if (hit != 0) { m.Result = (IntPtr)hit; return; }
            }
            base.WndProc(ref m);
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int policy = 1;   // DWMNCRP_DISABLED：DWM 不画非客户区（标题条/边框）
                DwmSetWindowAttribute(Handle, 2, ref policy, 4);
            }
            catch { }
        }

        private void ToggleMax()
        {
            WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
            LayoutForSize();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (nav != null) LayoutForSize(); }
        protected override void OnClientSizeChanged(EventArgs e) { base.OnClientSizeChanged(e); if (nav != null) LayoutForSize(); }

        private void LayoutForSize()
        {
            int W = ClientSize.Width, H = ClientSize.Height;
            if (WindowState == FormWindowState.Maximized) Region = new Region(new Rectangle(0, 0, W, H));
            else
            {
                using (GraphicsPath gpR = Md3.Round(new Rectangle(0, 0, W, H), Md3.RadWin))
                    Region = new Region(gpR);
            }
            if (nav == null) return;

            btnClose.SetBounds(W - 40, 5, 28, 28);
            btnMax.SetBounds(W - 70, 5, 28, 28);
            btnMin.SetBounds(W - 100, 5, 28, 28);
            led.SetBounds(16, 6, 28, 28);
            chip.SetBounds(52, 4, 150, 32);

            for (int i = 0; i < nav.Length; i++)
                nav[i].SetBounds(12, 56 + i * 52, 152, 44);

            int cx = 192, cy = 52, cw = W - cx - 16, ch = H - cy - 40;
            pageSim.SetBounds(cx, cy, cw, ch);
            pageInfo.SetBounds(cx, cy, cw, ch);
            pageSet.SetBounds(cx, cy, cw, ch);
            pageAbout.SetBounds(cx, cy, cw, ch);

            btnStart.SetBounds(0, 0, 132, 42);
            btnStop.SetBounds(140, 0, 132, 42);
            btnRecheck.SetBounds(cw - 100, 2, 100, 38);
            cardLog.SetBounds(0, 56, cw, Math.Max(120, ch - 56));
            lblLog.SetBounds(20, 14, 200, 26);
            txtLog.SetBounds(20, 46, cardLog.Width - 40, Math.Max(60, cardLog.Height - 62));

            btnRefresh.SetBounds(cw - 108, 0, 108, 38);
            txtInfo.SetBounds(0, 46, cw, Math.Max(80, ch - 46));

            cardSet.SetBounds(0, 0, cw, 330);
            lblCardSet.SetBounds(20, 14, 260, 26);
            int left = 20, right = cardSet.Width - 20, avail = right - left, gap = 12;
            fHost.SetBounds(left, 52, (int)(avail * 0.22), 60);
            fPort.SetBounds(fHost.Right + gap, 52, (int)(avail * 0.12), 60);
            fSlot.SetBounds(fPort.Right + gap, 52, (int)(avail * 0.21), 60);
            fInterval.SetBounds(fSlot.Right + gap, 52, Math.Max(96, right - fSlot.Right - gap), 60);
            fMac.SetBounds(left, 124, (int)(avail * 0.32), 60);
            fFw.SetBounds(fMac.Right + gap, 124, Math.Max(200, right - fMac.Right - gap), 60);
            int rowY = 208, dy = 42, colB = left + (int)(avail * 0.6) + 6;
            int wA = colB - (left + 62) - 14;
            int wB = right - (colB + 62) - 6;
            if (wA < 120) wA = 120;
            if (wB < 120) wB = 120;
            swTray.SetBounds(left, rowY, 52, 32);
            lblSw1.SetBounds(left + 62, rowY + 4, wA, 24);
            swAuto.SetBounds(colB, rowY, 52, 32);
            lblSw2.SetBounds(colB + 62, rowY + 4, wB, 24);
            swAutoSim.SetBounds(left, rowY + dy, 52, 32);
            lblSw3.SetBounds(left + 62, rowY + dy + 4, wA, 24);
            swCleanStart.SetBounds(colB, rowY + dy, 52, 32);
            lblSw4.SetBounds(colB + 62, rowY + dy + 4, wB, 24);
            swSuppress.SetBounds(left, rowY + dy * 2, 52, 32);
            lblSw5.SetBounds(left + 62, rowY + dy * 2 + 4, Math.Min(wA + 120, right - (left + 62)), 24);

            txtAbout.SetBounds(0, 0, cw, ch);
            Invalidate();
        }

        // ---------------- 日志 / 状态 ----------------
        private void LogUi(string line) { txtLog.AppendText(line + Environment.NewLine); }

        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            Program.FileLog(line);          // 同时落盘，便于事后核对
            if (txtLog.InvokeRequired) txtLog.BeginInvoke(new Action<string>(LogUi), new object[] { line });
            else LogUi(line);
        }

        private void SetState(string text, LedState st)
        {
            if (chip.InvokeRequired) { chip.BeginInvoke(new Action<string, LedState>(SetState), new object[] { text, st }); return; }
            chip.Text = text;
            chip.Active = (st == LedState.Running);
            chip.Invalidate();
            led.State = st;
            UpdateTrayIcon();
        }

        private void RunEnvCheckAsync()
        {
            string h = fHost.Value.Trim();
            int p = PortOf();
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    EnvReport rep = SelfCheck.RunEnv(new Action<string>(delegate(string m) { Log(m); }), h, p);
                    bool probeOk = false;
                    try
                    {
                        int sl;
                        if (!int.TryParse(fSlot.Value.Trim(), out sl)) sl = 6;
                        Log("---- 自动测试连接（只读，不改动任何状态）----");
                        SimCore pc = new SimCore(new Action<string>(delegate(string m) { Log(m); }));
                        pc.Host = h;
                        pc.Port = p;
                        pc.Slot = sl;
                        probeOk = pc.Probe(2.5);
                        Log(probeOk ? "自动测试连接：通过（broker 可达 / 鉴权通过 / 已订阅）" : "自动测试连接：失败（见上面的报错）");
                    }
                    catch (Exception pex) { Log("自动测试异常：" + pex.Message); }
                    if (!probeOk) rep.Problems.Add("自动测试连接失败：无法接入 broker（控制中心没运行 / 端口不同 / 鉴权编号不通）");
                    if (rep.Problems.Count == 0 || Program.NoStartupCleanup) return;
                    BeginInvoke(new Action(delegate
                    {
                        Log("==== 自检有 " + rep.Problems.Count + " 项需要处理，弹出提醒（可以选择继续）====");
                        using (WarnDialog w = new WarnDialog(P, rep.Problems))
                        {
                            w.ShowDialog(this);
                            if (w.ContinueAnyway) Log("已选择：仍然继续（模拟可能不生效）");
                            else { Log("已选择：退出程序"); ReallyExit(); }
                        }
                    }));
                }
                catch (Exception ex) { Log("环境自检异常：" + ex.Message); }
            });
        }

        private void OnRecheck(object sender, EventArgs e)
        {
            if (core == null || !started) { Log("还没启动模拟，无法复查（先点「启动模拟」）"); return; }
            btnRecheck.Enabled = false;
            Log("—— 手动复查（观察 6 秒）——");
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                bool ok = false;
                try { ok = core.Recheck(6.0); } catch (Exception ex) { Log("复查异常：" + ex.Message); }
                SetState(ok ? ("运行中 · " + core.Client.ClientId) : "运行中（复查未通过）", ok ? LedState.Running : LedState.Error);
                try { BeginInvoke(new Action(delegate { btnRecheck.Enabled = true; })); } catch { }
            });
        }

        private void OnStart(object sender, EventArgs e)
        {
            if (started) return;
            core = new SimCore(Log);
            core.Dock.Mac = fMac.Value.Trim();
            core.Dock.Fw = fFw.Value.Trim();
            core.Suppress = swSuppress.Checked;
            int slot;
            if (!int.TryParse(fSlot.Value.Trim(), out slot)) slot = 6;
            double iv;
            if (!double.TryParse(fInterval.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out iv)) iv = 1.0;
            if (iv < 0.2) iv = 0.2;
            core.Slot = slot;
            core.Interval = iv;
            core.Host = fHost.Value.Trim();
            core.Port = PortOf();
            if (core.Dock.Fw.Length < 23) Log("警告：固件版本字符串太短（<23 字符），界面解析会异常，已按原样发送");

            btnStart.Enabled = false;
            SetState("连接中…", LedState.Idle);
            if (!core.Start())
            {
                SetState("连接失败", LedState.Error);
                Log("启动失败：控制中心没运行，或鉴权编号都不通（可到「本机信息」页看自动检测结果）");
                btnStart.Enabled = true;
                return;
            }
            started = true;
            timer.Interval = (int)(iv * 1000);
            timer.Start();
            SetState("运行中 · " + core.Client.ClientId, LedState.Running);
            btnStop.Enabled = true;
            Log("—— 启动复查：6 秒后自动执行 ——");
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                bool ok = false;
                try { ok = core.Recheck(6.0); } catch (Exception ex) { Log("复查异常：" + ex.Message); }
                SetState(ok ? ("运行中 · " + core.Client.ClientId) : "运行中（复查未通过）", ok ? LedState.Running : LedState.Error);
            });
        }

        private void OnStop(object sender, EventArgs e)
        {
            timer.Stop();
            if (core != null) core.Stop(true);
            started = false;
            btnStart.Enabled = true;
            btnStop.Enabled = false;
            SetState("已停止", LedState.Idle);
            Log("已停止模拟，残留已自动清理（两条状态覆盖为「未连接」）");
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (core != null && started)
            {
                core.Tick();
                if (core.Client != null && !core.Client.Connected) SetState("连接断开 · 重连中", LedState.Error);
                else if (led.State == LedState.Error && core.Client != null && core.Client.Connected)
                    SetState("运行中 · " + core.Client.ClientId, LedState.Running);
            }
        }

        private void CleanStaleAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate(object o)
            {
                try
                {
                    SimCore c2 = new SimCore(new Action<string>(delegate(string m) { Log(m); }));
                    c2.Slot = 8;
                    c2.Host = "127.0.0.1";
                    c2.Port = 13688;
                    if (c2.ConnectAuto(8))
                    {
                        c2.CleanupStale();
                        try { c2.Client.Close(); } catch { }
                    }
                }
                catch (Exception ex) { Log("[启动清理] " + ex.Message); }
            });
        }

        // ---------------- 关闭流程 ----------------
        public void ForceClose()
        {
            exiting = true;
            try { if (core != null && started) { core.Stop(true); started = false; } } catch { }
            Close();
        }

        private void ReallyExit()
        {
            exiting = true;
            timer.Stop();
            if (started && core != null)
            {
                Log("退出：停止模拟并自动清理残留…");
                core.Stop(true);
                started = false;
            }
            if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            Close();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (exiting)
            {
                timer.Stop();
                if (core != null && started) { core.Stop(true); started = false; }
                return;
            }
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                timer.Stop();
                if (core != null && started) { core.Stop(true); started = false; }
                return;
            }
            e.Cancel = true;
            int remembered = Settings.Get("CloseAction", 0);
            if (remembered == 1) { Log("按记住的选择：最小化到托盘"); HideToTray(false); return; }
            if (remembered == 2) { Log("按记住的选择：直接关闭"); ReallyExit(); return; }
            using (CloseDialog dlg = new CloseDialog(P))
            {
                dlg.ShowDialog(this);
                if (dlg.Result == CloseDialog.Choice.Tray)
                {
                    if (dlg.Remember) { Settings.Set("CloseAction", 1); Log("[设置] 已记住：关闭时最小化到托盘"); }
                    Log("已最小化到托盘，模拟继续运行");
                    HideToTray(false);
                    if (!trayOk) Log("提示：系统没把托盘图标登记成功（火绒等安全软件可能拦截）；想叫回窗口就再双击一次本程序。");
                }
                else if (dlg.Result == CloseDialog.Choice.Exit)
                {
                    if (dlg.Remember) { Settings.Set("CloseAction", 2); Log("[设置] 已记住：关闭时直接关闭"); }
                    ReallyExit();
                }
            }
        }
    }

    // ============================================================
    //  程序入口（无参数 = GUI；带参数 = 命令行模式）
    // ============================================================
    public static class Program
    {
        public static bool NoStartupCleanup;   // --guitest 用：自检时不碰任何状态
        public static bool AutoStartLaunch;    // 由 --autostart 拉起（开机自启）
        private static string logFile;

        public static void FileLog(string line)
        {
            try { if (logFile != null) File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); }
            catch { }
        }

        private static void Out(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + msg;
            try { Console.WriteLine(line); } catch { }
            if (logFile != null) { try { File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8); } catch { } }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                try { Console.OutputEncoding = Encoding.UTF8; } catch { }   // 重定向输出时也保证中文不乱码
            }
            logFile = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "WaterCoolSim.log");
            bool autoStart = false;
            for (int i = 0; i < args.Length; i++) if (args[i] == "--autostart") autoStart = true;
            if (args.Length > 0 && !autoStart)
            {
                int rc = RunCli(args);
                Environment.Exit(rc);
                return;
            }
            AutoStartLaunch = autoStart;
            bool created;
            Mutex mtx = new Mutex(true, "WaterCoolSim_SingleInstance", out created);
            if (!created)
            {
                // 已经在运行：把那个（可能已经最小化到托盘的）窗口叫回来
                if (!autoStart) WakeExisting();
                return;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Application.Run(new MainForm()); }
            finally { GC.KeepAlive(mtx); }
        }

        // 已有实例在跑（可能藏在托盘里）：把它的窗口显示出来并置前
        private static void WakeExisting()
        {
            try
            {
                string me = Path.GetFileNameWithoutExtension(Application.ExecutablePath);
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(me))
                {
                    if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id) continue;
                    IntPtr h = p.MainWindowHandle;
                    if (h == IntPtr.Zero)
                    {
                        // 隐藏到托盘的窗口 MainWindowHandle 可能为 0：按标题找
                        h = FindWindow(null, "虚拟水冷坞 · 机械革命控制中心");
                    }
                    if (h != IntPtr.Zero)
                    {
                        ShowWindow(h, 5);          // SW_SHOW
                        ShowWindow(h, 9);          // SW_RESTORE
                        SetForegroundWindow(h);
                        return;
                    }
                }
                MessageBox.Show("虚拟水冷坞已经在运行了（可能藏在托盘里）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("虚拟水冷坞已经在运行了（" + ex.Message + "）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string cls, string win);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr h, int cmd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr h);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        private static bool CaptureWindow(string path, Form frm)
        {
            try
            {
                RECT r;
                if (!GetWindowRect(frm.Handle, out r)) return false;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return false;
                using (Bitmap bmp = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        bool ok = PrintWindow(frm.Handle, hdc, 2);
                        g.ReleaseHdc(hdc);
                        if (!ok) return false;
                    }
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                return true;
            }
            catch (Exception ex) { Out("截图异常：" + ex.Message); return false; }
        }

        private static string Hex(Color c) { return c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        private static string Arg(string[] a, string name, string def)
        {
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return def;
        }

        private static bool Flag(string[] a, string name)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] == name) return true;
            return false;
        }

        private static int RunCli(string[] args)
        {
            string mode = args[0];
            SimCore core = new SimCore(new Action<string>(Out));
            core.Host = Arg(args, "--host", "127.0.0.1");
            int port; if (!int.TryParse(Arg(args, "--port", "13688"), out port)) port = 13688;
            core.Port = port;
            int slot; if (!int.TryParse(Arg(args, "--slot", "6"), out slot)) slot = 6;
            core.Slot = slot;
            double iv; if (!double.TryParse(Arg(args, "--interval", "1"), NumberStyles.Float, CultureInfo.InvariantCulture, out iv)) iv = 1.0;
            core.Interval = iv;
            core.Suppress = !Flag(args, "--no-suppress");
            core.Dock.Mac = Arg(args, "--mac", core.Dock.Mac);
            core.Dock.Fw = Arg(args, "--fw", core.Dock.Fw);

            if (mode == "--cleanup")
            {
                if (!core.ConnectAuto(slot)) return 2;
                core.Cleanup();
                try { core.Client.Close(); } catch { }
                return 0;
            }

            if (mode == "--probe")
            {
                double secs; if (!double.TryParse(Arg(args, "--seconds", "5"), NumberStyles.Float, CultureInfo.InvariantCulture, out secs)) secs = 5;
                return core.Probe(secs) ? 0 : 2;
            }

            if (mode == "--runset")
            {
                string what = Arg(args, "--set", args.Length > 1 ? args[1] : "status");
                if (what == "on")
                {
                    bool ok = Settings.SetAutoStart(true);
                    Out(ok ? "已写入: " + Settings.AutoStartCommand() : "写入失败: " + Settings.LastError);
                    return ok ? 0 : 1;
                }
                if (what == "off")
                {
                    Settings.SetAutoStart(false);
                    Out("已移除。当前值: " + (Settings.AutoStartCommand() ?? "(无)"));
                    return 0;
                }
                Out("当前开机自启动: " + (Settings.AutoStartEnabled() ? Settings.AutoStartCommand() : "(未启用)"));
                return 0;
            }

            if (mode == "--selfcheck")
            {
                SelfCheck.RunEnv(new Action<string>(Out), core.Host, core.Port);   // 结果仅打印
                Out("");
                core.Probe(2.5);
                return 0;
            }

            if (mode == "--sim")
            {
                if (!core.Start()) return 2;
                if (!Flag(args, "--no-recheck"))
                {
                    Thread.Sleep(1500);
                    core.Recheck(6.0);
                }
                Out("模拟运行中，Ctrl+C 结束（结束时会自动清理残留）");
                ManualResetEvent quit = new ManualResetEvent(false);
                Console.CancelKeyPress += delegate(object s, ConsoleCancelEventArgs ce)
                {
                    ce.Cancel = true;
                    quit.Set();
                };
                DateTime next = DateTime.UtcNow;
                while (!quit.WaitOne(100))
                {
                    if (DateTime.UtcNow >= next)
                    {
                        core.Tick();
                        next = DateTime.UtcNow.AddSeconds(core.Interval);
                    }
                }
                core.Stop(!Flag(args, "--no-cleanup"));
                return 0;
            }

            if (mode == "--shot")
            {
                string outPath = Arg(args, "--out", Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ui.png"));
                NoStartupCleanup = true;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                MainForm frm = new MainForm();
                frm.StartPosition = FormStartPosition.Manual;
                frm.Location = new Point(0, 0);
                frm.Show();
                Application.DoEvents();
                Thread.Sleep(900);
                Application.DoEvents();
                bool ok = CaptureWindow(outPath, frm);
                Out(ok ? ("已保存界面截图：" + outPath) : "截图失败");
                try
                {
                    // 逐页截图：模拟水冷 / 本机信息 / 软件设置 / 关于
                    for (int pi = 0; pi < 4; pi++)
                    {
                        frm.ShowPage(pi);
                        Application.DoEvents();
                        Thread.Sleep(350);
                        Application.DoEvents();
                        CaptureWindow(outPath.Replace(".png", ".page" + pi + ".png"), frm);
                    }
                    frm.ShowPage(0);
                    Application.DoEvents();
                    Thread.Sleep(200);
                    Application.DoEvents();
                    // 再抓一张“失去焦点”的样子（用另一个窗体抢焦点）
                    try
                    {
                        Form thief = new Form();
                        thief.Text = "focus thief";
                        thief.SetBounds(0, 0, 320, 200);
                        thief.Show();
                        thief.Activate();
                        thief.BringToFront();
                        Application.DoEvents();
                        Thread.Sleep(500);
                        Application.DoEvents();
                        CaptureWindow(outPath.Replace(".png", ".inactive.png"), frm);
                        thief.Close();
                        thief.Dispose();
                        Application.DoEvents();
                    }
                    catch (Exception ex4) { Out("失焦截图失败：" + ex4.Message); }
                }
                catch (Exception ex3) { Out("逐页截图失败：" + ex3.Message); }
                try
                {
                    frm.Size = new Size(1100, 940);      // 放大窗口，验证自适应重排
                    Application.DoEvents();
                    Thread.Sleep(400);
                    Application.DoEvents();
                    CaptureWindow(outPath.Replace(".png", ".wide.png"), frm);
                    frm.Size = new Size(880, 856);
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();
                }
                catch (Exception ex2) { Out("宽屏截图失败：" + ex2.Message); }
                try
                {
                    Palette pal = Monet.Build(Monet.IsSystemDarkTheme());
                    CloseDialog dlg = new CloseDialog(pal);
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new Point(220, 240);
                    dlg.Show();
                    Application.DoEvents();
                    Thread.Sleep(600);
                    Application.DoEvents();
                    CaptureWindow(outPath.Replace(".png", ".dialog.png"), dlg);
                    dlg.Close();
                    Out("已保存关闭对话框截图：" + outPath.Replace(".png", ".dialog.png"));

                    Palette pal2 = Monet.Build(Monet.IsSystemDarkTheme());
                    AboutDialog ab = new AboutDialog(pal2);
                    ab.StartPosition = FormStartPosition.Manual;
                    ab.Location = new Point(160, 160);
                    ab.Show();
                    Application.DoEvents();
                    Thread.Sleep(600);
                    Application.DoEvents();
                    CaptureWindow(outPath.Replace(".png", ".about.png"), ab);
                    ab.Close();
                    Out("已保存关于对话框截图：" + outPath.Replace(".png", ".about.png"));

                    Palette pal3 = Monet.Build(Monet.IsSystemDarkTheme());
                    List<string> probs = new List<string>();
                    probs.Add("broker 端口不可达（127.0.0.1:13688）：控制中心没运行，或端口不同");
                    probs.Add("没找到控制中心组件 GCUBridge.exe");
                    WarnDialog wd = new WarnDialog(pal3, probs);
                    wd.StartPosition = FormStartPosition.Manual;
                    wd.Location = new Point(180, 200);
                    wd.Show();
                    Application.DoEvents();
                    Thread.Sleep(600);
                    Application.DoEvents();
                    CaptureWindow(outPath.Replace(".png", ".warn.png"), wd);
                    wd.Close();
                    Out("已保存警告对话框截图：" + outPath.Replace(".png", ".warn.png"));
                }
                catch (Exception ex) { Out("对话框截图失败：" + ex.Message); }
                frm.ForceClose();
                return ok ? 0 : 4;
            }

            if (mode == "--guitest")
            {
                try
                {
                    NoStartupCleanup = true;
                    Application.EnableVisualStyles();
                    MainForm frm = new MainForm();
                    frm.CreateControl();
                    Out("GUI OK：窗体与全部控件创建成功，标题=" + frm.Text);
                    frm.Dispose();
                    return 0;
                }
                catch (Exception ex)
                {
                    Out("GUI 初始化失败：" + ex.ToString());
                    return 3;
                }
            }

            Out("用法：");
            Out("  WaterCoolSim.exe                       打开图形界面");
            Out("  WaterCoolSim.exe --sim                 命令行模拟（Ctrl+C 结束，自动清理）");
            Out("  WaterCoolSim.exe --cleanup             只做一次清理（覆盖成未连接）");
            Out("  WaterCoolSim.exe --probe [--seconds N] 只读抓包看真实报文");
            Out("  WaterCoolSim.exe --guitest             自检图形界面（不显示窗口）");
            if (mode == "--palette")
            {
                Color seed = Color.FromArgb(0x67, 0x50, 0xA4);
                if (Flag(args, "--wallpaper"))
                {
                    Bitmap wb = Monet.LoadWallpaper();
                    if (wb != null) { try { seed = Color.FromArgb(SourceColor.Pick(wb)); } finally { wb.Dispose(); } }
                }
                else
                {
                    string hex = Arg(args, "--seed", "");
                    if (hex.Length == 6)
                        seed = Color.FromArgb(Convert.ToInt32(hex.Substring(0, 2), 16),
                                              Convert.ToInt32(hex.Substring(2, 2), 16),
                                              Convert.ToInt32(hex.Substring(4, 2), 16));
                }
                Palette pl = SourceColor.SchemeFromSeed(seed, Flag(args, "--dark"));
                Out("seed=#" + Hex(seed) + "  " + (pl.Dark ? "dark" : "light"));
                Out("primary=#" + Hex(pl.Primary) + " onPrimary=#" + Hex(pl.OnPrimary)
                    + " primaryContainer=#" + Hex(pl.PrimaryContainer) + " onPrimaryContainer=#" + Hex(pl.OnPrimaryContainer));
                Out("secondary=#" + Hex(pl.Secondary) + " secondaryContainer=#" + Hex(pl.SecondaryContainer)
                    + " tertiary=#" + Hex(pl.Tertiary) + " tertiaryContainer=#" + Hex(pl.TertiaryContainer));
                Out("error=#" + Hex(pl.Error) + " errorContainer=#" + Hex(pl.ErrorContainer));
                Out("surface=#" + Hex(pl.Surface) + " onSurface=#" + Hex(pl.OnSurface)
                    + " surfaceVariant=#" + Hex(pl.SurfaceVariant) + " onSurfaceVariant=#" + Hex(pl.OnSurfaceVariant));
                Out("outline=#" + Hex(pl.Outline) + " outlineVariant=#" + Hex(pl.OutlineVariant)
                    + " inverseSurface=#" + Hex(pl.InverseSurface) + " inverseOnSurface=#" + Hex(pl.InverseOnSurface));
                Out("surfaceContainerLowest=#" + Hex(pl.Surface) + " low=#" + Hex(pl.SurfaceLow)
                    + " container=#" + Hex(pl.SurfaceContainer) + " high=#" + Hex(pl.SurfaceHigh)
                    + " highest=#" + Hex(pl.SurfaceHighest));
                return 0;
            }

            Out("  WaterCoolSim.exe --selfcheck           环境自检 + 只读探测 broker");
            Out("  WaterCoolSim.exe --palette [--seed 6750A4 | --wallpaper] [--dark]   打印 M3 色板（校验用）");
            Out("  WaterCoolSim.exe --runset on|off|status                           设置/查看开机自启动（HKCU Run）");
            Out("  WaterCoolSim.exe --shot [--out p.png]  把界面渲染成 PNG（不碰运行状态）");
            Out("可选参数：--host --port --slot --interval --mac --fw --no-suppress --no-cleanup");
            return 1;
        }
    }
}