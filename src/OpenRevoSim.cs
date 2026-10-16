using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

// ============================================================================
//  OpenRevo（第三方控制中心）内置模拟：
//  在 open-revo.exe 进程内存里挂钩「水冷状态序列化」入口，
//  每次它要序列化状态前，把状态结构改成「已连接 + 假数据」。
//  只改运行中的内存，不动磁盘文件；关闭开关 / 退出软件即还原。
// ============================================================================
public static class OpenRevoSim
{
    public static bool Attached { get; private set; }
    public static string LastError = "";
    public static int HookCount = 0;

    const int PROCESS_VM_OPERATION = 0x0008;
    const int PROCESS_VM_READ = 0x0010;
    const int PROCESS_VM_WRITE = 0x0020;
    const int PROCESS_QUERY_INFORMATION = 0x0400;
    const int MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    const int PAGE_EXECUTE_READWRITE = 0x40, PAGE_EXECUTE_READ = 0x20, PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, int type, int protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, IntPtr size, int type);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool VirtualProtectEx(IntPtr h, IntPtr addr, IntPtr size, int newProtect, out int oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushInstructionCache(IntPtr h, IntPtr addr, IntPtr size);

    // 挂钩点签名（open-revo 0.8.9 附近版本）：
    //   lea r9,[rbx+0x4c] ; lea rdx,[rip+..] ; lea rcx,[rsp+0x30] ; mov r8d,9 ; call ...
    static readonly byte[] SIG = new byte[]
    {
        0x4C, 0x8D, 0x4B, 0x4C,             // lea r9, [rbx+0x4c]   = "connected" 字段
        0x48, 0x8D, 0x15, 0x00, 0x00, 0x00, 0x00,   // lea rdx, [rip+disp]  (disp 参与比较)
        0x48, 0x8D, 0x4C, 0x24, 0x30,       // lea rcx, [rsp+0x30]
        0x41, 0xB8, 0x09, 0x00, 0x00, 0x00, // mov r8d, 9
        0xE8                                // call rel32
    };
    static readonly bool[] SIG_MASK = new bool[]
    {
        true, true, true, true,
        true, true, true, false, false, false, false,
        true, true, true, true, true,
        true, true, true, true, true, true,
        true
    };

    static IntPtr hProc = IntPtr.Zero;
    static int pid = 0;
    static IntPtr hookAddr = IntPtr.Zero;
    static IntPtr stubAddr = IntPtr.Zero;
    static byte[] original = null;

    // ---------------- 工具 ----------------
    /// <summary>OpenRevo 是否在运行（只探测，不挂钩）</summary>
    public static bool IsRunning()
    {
        Process p = FindRevo();
        return p != null;
    }

    /// <summary>给自检用的描述文本</summary>
    public static string Describe()
    {
        Process p = FindRevo();
        if (p == null) return "未运行";
        string s = "已运行（PID " + p.Id + "）";
        if (Attached) s += "，已挂钩模拟";
        else if (LastError.Length > 0) s += "，未挂钩：" + LastError;
        else s += "，未挂钩";
        return s;
    }

    static Process FindRevo()
    {
        try
        {
            Process[] ps = Process.GetProcessesByName("open-revo");
            if (ps == null || ps.Length == 0) return null;
            return ps[0];
        }
        catch { return null; }
    }

    static bool ReadBytes(IntPtr addr, int n, out byte[] buf)
    {
        buf = new byte[n];
        IntPtr got;
        return ReadProcessMemory(hProc, addr, buf, n, out got);
    }

    static bool WriteBytes(IntPtr addr, byte[] data)
    {
        IntPtr written;
        return WriteProcessMemory(hProc, addr, data, data.Length, out written);
    }

    // ---------------- 查找挂钩点 ----------------
    static IntPtr ScanSignature(IntPtr baseAddr, int scanLen)
    {
        const int CHUNK = 1 << 20;
        for (int off = 0; off < scanLen; off += CHUNK - SIG.Length)
        {
            int n = Math.Min(CHUNK, scanLen - off);
            byte[] buf;
            if (!ReadBytes(new IntPtr(baseAddr.ToInt64() + off), n, out buf)) continue;
            for (int i = 0; i + SIG.Length <= n; i++)
            {
                bool ok = true;
                for (int j = 0; j < SIG.Length; j++)
                {
                    if (!SIG_MASK[j]) continue;
                    if (buf[i + j] != SIG[j]) { ok = false; break; }
                }
                if (ok) return new IntPtr(baseAddr.ToInt64() + off + i);
            }
        }
        return IntPtr.Zero;
    }

    // ---------------- 残留钩子修复 ----------------
    // 我们的挂钩特征：站点被改写成 jmp qword ptr [rip+0]
    static readonly byte[] JMP_PAT = new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };

    /// 从磁盘镜像里找同一段代码的原始字节（用签名匹配，版本无关）
    static bool TryLoadOriginalFromFile(string exePath, out byte[] orig22)
    {
        orig22 = null;
        try
        {
            byte[] img = System.IO.File.ReadAllBytes(exePath);
            int sigOff = -1;
            for (int i = 0; i + SIG.Length <= img.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < SIG.Length; j++)
                {
                    if (!SIG_MASK[j]) continue;
                    if (img[i + j] != SIG[j]) { ok = false; break; }
                }
                if (ok) { sigOff = i; break; }
            }
            if (sigOff < 0) return false;
            orig22 = new byte[22];
            Array.Copy(img, sigOff, orig22, 0, 22);
            // 把签名所在位置换算成 RVA：遍历节表，delta = PointerToRawData - VirtualAddress
            int peOff = BitConverter.ToInt32(img, 0x3C);
            int numSec = BitConverter.ToInt16(img, peOff + 6);
            int optSize = BitConverter.ToInt16(img, peOff + 20);
            int secTab = peOff + 24 + optSize;
            for (int s = 0; s < numSec; s++)
            {
                int so = secTab + s * 40;
                int vsize = BitConverter.ToInt32(img, so + 8);
                int vaddr = BitConverter.ToInt32(img, so + 12);
                int rawSize = BitConverter.ToInt32(img, so + 16);
                int rawPtr = BitConverter.ToInt32(img, so + 20);
                if (rawPtr <= sigOff && sigOff < rawPtr + rawSize)
                {
                    lastRva = sigOff + vaddr - rawPtr;
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    static int lastRva = -1;

    /// 若目标里残留了我们的钩子（上次被强杀），先用磁盘原始字节还原
    public static bool RepairIfStale()
    {
        if (Attached) return false;   // 当前这个钩子是我们自己刚挂的，不是残留，别乱还原
        try
        {
            Process p = FindRevo();
            if (p == null) return false;
            string exePath;
            try { exePath = p.MainModule.FileName; } catch { return false; }
            IntPtr baseAddr = p.MainModule.BaseAddress;
            IntPtr h = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return false;
            try
            {
                byte[] orig;
                if (!TryLoadOriginalFromFile(exePath, out orig) || lastRva < 0) return false;
                IntPtr site = new IntPtr(baseAddr.ToInt64() + lastRva + 4);   // 原签名 +4 = 挂钩点
                byte[] cur;
                cur = new byte[6];
                IntPtr got;
                if (!ReadProcessMemory(h, site, cur, 6, out got)) return false;
                bool isOurs = true;
                for (int i = 0; i < 6; i++) if (cur[i] != JMP_PAT[i]) { isOurs = false; break; }
                if (!isOurs) return false;                                    // 没残留，什么都不用做
                int old;
                VirtualProtectEx(h, site, (IntPtr)18, PAGE_EXECUTE_READWRITE, out old);
                IntPtr w;
                byte[] back = new byte[18];
                Array.Copy(orig, 4, back, 0, 18);
                WriteProcessMemory(h, site, back, 18, out w);
                VirtualProtectEx(h, site, (IntPtr)18, old, out old);
                FlushInstructionCache(h, site, (IntPtr)18);
                return true;
            }
            finally { CloseHandle(h); }
        }
        catch { return false; }
    }

    // ---------------- 安装 ----------------
    public static bool Attach()
    {
        if (Attached) return true;
        LastError = "";
        Process p = FindRevo();
        if (p == null) { LastError = "未检测到 OpenRevo（open-revo.exe 未运行）"; return false; }
        try
        {
            pid = p.Id;
            IntPtr baseAddr = p.MainModule.BaseAddress;
            int modSize = p.MainModule.ModuleMemorySize;
            hProc = OpenProcess(PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_QUERY_INFORMATION, false, pid);
            if (hProc == IntPtr.Zero) { LastError = "打开进程失败（需要管理员权限）：" + Marshal.GetLastWin32Error(); return false; }

            try { RepairIfStale(); } catch { }   // 上次被强杀留下的钩子先还原，否则签名扫不到
            IntPtr sig = ScanSignature(baseAddr, Math.Min(modSize, 32 << 20));
            if (sig == IntPtr.Zero)
            {
                LastError = "没找到挂钩点签名（OpenRevo 版本可能已更新）";
                CleanupHandle();
                return false;
            }
            hookAddr = new IntPtr(sig.ToInt64() + 4);   // 指向 lea rdx,[rip+..]（此时 rbx=状态结构，字段值尚未读走）

            byte[] stay;
            if (!ReadBytes(hookAddr, 22, out stay)) { LastError = "读取目标代码失败"; CleanupHandle(); return false; }
            original = stay;

            IntPtr stub = VirtualAllocEx(hProc, IntPtr.Zero, (IntPtr)256, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
            if (stub == IntPtr.Zero) { LastError = "分配跳板内存失败"; CleanupHandle(); return false; }
            stubAddr = stub;

            // 名字串绝对地址：lea rdx,[rip+disp32] 编码为 48 8D 15 <disp32>，disp32 在第 3 字节
            int disp = BitConverter.ToInt32(stay, 3);
            long nameAddr = hookAddr.ToInt64() + 7 + disp;
            long backAddr = hookAddr.ToInt64() + 18;      // 搬走 18 字节后回到这里

            // 安全检查：该地址必须真的是 "connected" 这个字段名（版本不匹配就别动手，免得把它搞崩）
            byte[] nameCheck;
            if (!ReadBytes(new IntPtr(nameAddr), 9, out nameCheck) || Encoding.ASCII.GetString(nameCheck) != "connected")
            {
                LastError = "挂钩点校验失败（字段名不是 connected，版本可能已更新）";
                CleanupHandle();
                return false;
            }

            List<byte> code = new List<byte>();
            Action<byte[]> A = delegate (byte[] xs) { code.AddRange(xs); };
            Action<long> I64 = delegate (long v) { code.AddRange(BitConverter.GetBytes(v)); };
            A(new byte[] { 0xC6, 0x43, 0x4C, 0x01 });
            A(new byte[] { 0xC6, 0x43, 0x4D, 0x01 });
            A(new byte[] { 0xC6, 0x43, 0x52, 0x01 });
            A(new byte[] { 0xC6, 0x43, 0x53, 0x07 });
            A(new byte[] { 0xC6, 0x43, 0x54, 0x02 });
            A(new byte[] { 0xC6, 0x43, 0x55, 0x32 });
            // mov rdx, imm64
            A(new byte[] { 0x48, 0xBA }); I64(nameAddr);
            // lea rcx, [rsp+0x30]
            A(new byte[] { 0x48, 0x8D, 0x4C, 0x24, 0x30 });
            // mov r8d, 9
            A(new byte[] { 0x41, 0xB8, 0x09, 0x00, 0x00, 0x00 });
            // 剩余 4 字节原始指令：mov r8d 之后是 call rel32（5B），我们只搬 18B 覆盖到 mov r8d 的最后一个字节
            // 因此这里把原 18..21 的 4 字节也搬过来（它们是原指令序列的一部分：mov r8d 的最后 0 字节 + call 的前 4 字节）
            // —— 为避免割裂指令，改为不搬这 4 字节，而是回到 hookAddr+18（原指令边界内），由原代码继续执行。
            // mov rax, imm64 ; jmp rax
            A(new byte[] { 0x48, 0xB8 }); I64(backAddr);
            A(new byte[] { 0xFF, 0xE0 });

            byte[] stubCode = code.ToArray();
            if (!WriteBytes(stub, stubCode)) { LastError = "写入跳板失败"; CleanupHandle(); return false; }

            // 原址写 jmp [rip+0] + 绝对地址 + NOP 填充
            List<byte> patch = new List<byte>();
            patch.AddRange(new byte[] { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 });
            patch.AddRange(BitConverter.GetBytes(stub.ToInt64()));
            while (patch.Count < 18) patch.Add(0x90);

            int old;
            VirtualProtectEx(hProc, hookAddr, (IntPtr)18, PAGE_EXECUTE_READWRITE, out old);
            bool okW = WriteBytes(hookAddr, patch.ToArray());
            VirtualProtectEx(hProc, hookAddr, (IntPtr)18, old, out old);
            FlushInstructionCache(hProc, hookAddr, (IntPtr)18);
            if (!okW) { LastError = "写入挂钩指令失败"; CleanupHandle(); return false; }

            Attached = true;
            HookCount++;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CleanupHandle();
            return false;
        }
    }

    static void CleanupHandle()
    {
        if (hProc != IntPtr.Zero) { try { CloseHandle(hProc); } catch { } hProc = IntPtr.Zero; }
    }

    // ---------------- 卸载（还原内存） ----------------
    public static void Detach()
    {
        try
        {
            if (hProc != IntPtr.Zero && hookAddr != IntPtr.Zero && original != null)
            {
                int old;
                VirtualProtectEx(hProc, hookAddr, (IntPtr)18, PAGE_EXECUTE_READWRITE, out old);
                WriteBytes(hookAddr, original);
                VirtualProtectEx(hProc, hookAddr, (IntPtr)18, old, out old);
                FlushInstructionCache(hProc, hookAddr, (IntPtr)18);
            }
            if (hProc != IntPtr.Zero && stubAddr != IntPtr.Zero)
                VirtualFreeEx(hProc, stubAddr, IntPtr.Zero, MEM_RELEASE);
        }
        catch { }
        finally
        {
            Attached = false;
            stubAddr = IntPtr.Zero;
            hookAddr = IntPtr.Zero;
            original = null;
            CleanupHandle();
        }
    }

    public static string StatusText()
    {
        Process p = FindRevo();
        if (p == null) return "未运行";
        if (Attached) return "已挂钩（模拟已连接）";
        return "运行中，未挂钩" + (LastError.Length > 0 ? "：" + LastError : "");
    }
}
