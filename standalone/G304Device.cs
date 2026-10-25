using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LogiG304.Standalone
{
    internal sealed class MouseState
    {
        public string Name, Protocol;
        public int Battery = -1, Dpi, DefaultDpi, DpiMin, DpiMax, DpiStep, RateMs, Mode;
        public int Stage = -1;
        public int[] StageDpis = new int[0];
        public readonly List<int> DpiValues = new List<int>();
        public readonly List<int> RatesMs = new List<int>();
        public readonly List<string> Notes = new List<string>();
        public bool DpiSupported, RateSupported, ModeSupported;
        public string Summary()
        {
            return Name + " | HID++ " + Protocol + Environment.NewLine +
                "DPI: " + (DpiSupported ? Dpi.ToString() : "不可用") + " | 回报率: " + (RateSupported && RateMs > 0 ? (1000 / RateMs) + " Hz" : "不可用") +
                " | 电量: " + (Battery < 0 ? "未知" : Battery + "%（设备估值）") + Environment.NewLine +
                "模式: " + (Mode == 1 ? "板载模式" : Mode == 2 ? "软件模式" : "未知") +
                (Notes.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, Notes.ToArray()));
        }
    }

    internal static class WriteProtection
    {
        // Fail closed if process enumeration fails. Never terminate another program or service.
        public static bool IsGHubRunning()
        {
            foreach (string name in new[] { "lghub", "lghub_agent", "lcore", "LogitechGamingSoftware", "OnboardMemoryManager" })
            {
                Process[] processes = Process.GetProcessesByName(name);
                bool exists = processes.Length > 0;
                foreach (Process process in processes) process.Dispose();
                if (exists) return true;
            }
            return false;
        }
        public static void Check()
        {
            if (IsGHubRunning()) throw new InvalidOperationException("检测到 G HUB / 罗技控制程序正在运行。已阻止写入；请自行退出其托盘程序后刷新。本程序不会替你结束进程。");
        }
    }

    internal sealed class G304Device
    {
        readonly ITransport transport;
        readonly Action writeGuard;
        readonly Dictionary<ushort, byte> features = new Dictionary<ushort, byte>();
        bool identified;
        public G304Device(ITransport transport, Action writeGuard) { this.transport = transport; this.writeGuard = writeGuard; }
        byte[] Call(byte feature, byte function, params byte[] args) { return transport.Exchange(1, feature, function, args); }
        byte Feature(ushort id)
        {
            byte index;
            if (!features.TryGetValue(id, out index))
            {
                index = Call(0, 0, (byte)(id >> 8), (byte)id)[0];
                features.Add(id, index);
            }
            return index;
        }
        static int BE(byte[] bytes, int start) { return (bytes[start] << 8) | bytes[start + 1]; }

        public MouseState Read()
        {
            var state = new MouseState();
            byte[] ping = Call(0, 1, 0, 0, 0xA5);
            if (ping[0] < 2 || ping[2] != 0xA5) throw new IOException("HID++ 设备握手不匹配。");
            state.Protocol = ping[0] + "." + ping[1];
            byte nameFeature = Feature(0x0005);
            if (nameFeature == 0) throw new IOException("无法核实鼠标型号，停止访问。");
            int count = Call(nameFeature, 0)[0];
            if (count < 4 || count > 128) throw new IOException("无效的设备名称长度。");
            byte[] name = new byte[count];
            for (int offset = 0; offset < count; offset += 16)
                Array.Copy(Call(nameFeature, 1, (byte)offset), 0, name, offset, Math.Min(16, count - offset));
            state.Name = Encoding.UTF8.GetString(name).TrimEnd('\0');
            if (state.Name.IndexOf("G304", StringComparison.OrdinalIgnoreCase) < 0 && state.Name.IndexOf("G305", StringComparison.OrdinalIgnoreCase) < 0)
                throw new NotSupportedException("此版本仅支持 G304 / G305，当前鼠标型号不匹配。");
            identified = true;
            ReadOptional(state, "电量", delegate {
                byte feature = Feature(0x1000);
                if (feature != 0) { int value = Call(feature, 0)[0]; if (value <= 100) state.Battery = value; }
            });
            ReadOptional(state, "DPI", delegate {
                byte feature = Feature(0x2201);
                if (feature == 0) return;
                if (Call(feature, 0)[0] != 1) throw new NotSupportedException("不支持此传感器布局。");
                ParseDpiList(Call(feature, 1, 0), state);
                byte[] dpi = Call(feature, 2, 0);
                if (dpi[0] != 0) throw new IOException("传感器索引不匹配。");
                state.Dpi = BE(dpi, 1); state.DefaultDpi = BE(dpi, 3); state.DpiSupported = true;
            });
            ReadOptional(state, "回报率", delegate {
                byte feature = Feature(0x8060);
                if (feature == 0) return;
                int mask = Call(feature, 0)[0];
                // Present only standard G304 rates; bit n represents n+1 milliseconds.
                foreach (int ms in new[] { 1, 2, 4, 8 }) if ((mask & (1 << (ms - 1))) != 0) state.RatesMs.Add(ms);
                state.RateMs = Call(feature, 1)[0];
                state.RateSupported = state.RatesMs.Count > 0 && state.RatesMs.Contains(state.RateMs);
            });
            ReadOptional(state, "板载模式", delegate {
                byte feature = Feature(0x8100);
                if (feature == 0) return;
                state.Mode = Call(feature, 2)[0]; state.ModeSupported = state.Mode == 1 || state.Mode == 2;
                if (state.Mode != 1) return;
                byte[] descriptor = Call(feature, 0);
                if (descriptor[0] != 1 || descriptor[1] != 3) return;
                byte[] profile = Call(feature, 4);
                int sector = BE(profile, 0);
                if (sector < 1 || sector >= descriptor[6]) return;
                byte[] header = Call(feature, 5, profile[0], profile[1], 0, 0);
                state.StageDpis = new int[5];
                for (int i = 0; i < 5; i++) state.StageDpis[i] = header[3 + 2 * i] | (header[4 + 2 * i] << 8);
                state.Stage = Call(feature, 11)[0];
            });
            return state;
        }

        static void ReadOptional(MouseState state, string label, Action read)
        {
            try { read(); }
            catch (ProtocolException e) { state.Notes.Add(label + "暂不可用：" + e.Message); }
            // Transport failure must abort: do not present a stale partial state as connected.
        }

        public static void ParseDpiList(byte[] data, MouseState state)
        {
            if (data.Length < 16 || data[0] != 0) throw new IOException("无效的 DPI 范围回应。");
            for (int i = 1; i + 1 < 16; i += 2)
            {
                int value = BE(data, i);
                if (value == 0) break;
                if (value > 0xE000) state.DpiStep = value - 0xE000;
                else state.DpiValues.Add(value);
            }
            if (state.DpiValues.Count == 0) throw new IOException("设备没有提供 DPI 范围。");
            state.DpiValues.Sort(); state.DpiMin = state.DpiValues[0]; state.DpiMax = state.DpiValues[state.DpiValues.Count - 1];
            if (state.DpiMin < 200 || state.DpiMax > 12000 || state.DpiMax < state.DpiMin)
                throw new NotSupportedException("设备范围超出已验证的 G304 / G305 范围。");
        }

        public static bool ValidDpi(MouseState state, int dpi)
        {
            return state.DpiSupported && dpi >= state.DpiMin && dpi <= state.DpiMax &&
                (state.DpiStep > 0 ? (dpi - state.DpiMin) % state.DpiStep == 0 : state.DpiValues.Contains(dpi));
        }
        void BeforeWrite()
        {
            if (!identified) throw new InvalidOperationException("必须先核实设备。");
            writeGuard();
        }
        public void SetDpi(int value)
        {
            BeforeWrite(); MouseState state = Read();
            if (state.Mode != 2) throw new InvalidOperationException("请先显式切换到软件模式。板载模式使用鼠标中已有的 DPI 档位。");
            if (!ValidDpi(state, value)) throw new ArgumentOutOfRangeException("value", "DPI 不在设备支持的范围或步进中。");
            writeGuard(); byte feature = Feature(0x2201);
            Call(feature, 3, 0, (byte)(value >> 8), (byte)value);
            if (BE(Call(feature, 2, 0), 1) != value) throw new IOException("DPI 回读不一致。请刷新核实；不会自动重试写入。");
        }
        public void SetRate(int milliseconds)
        {
            BeforeWrite(); MouseState state = Read();
            if (state.Mode != 2 || !state.RateSupported || !state.RatesMs.Contains(milliseconds))
                throw new InvalidOperationException("请使用软件模式及设备支持的回报率。");
            writeGuard(); byte feature = Feature(0x8060); Call(feature, 2, (byte)milliseconds);
            if (Call(feature, 1)[0] != milliseconds) throw new IOException("回报率回读不一致。请刷新核实。");
        }
        public void SetMode(int mode)
        {
            BeforeWrite(); MouseState state = Read();
            if (!state.ModeSupported || (mode != 1 && mode != 2)) throw new InvalidOperationException("不支持此模式。");
            if (mode == state.Mode) return;
            writeGuard(); byte feature = Feature(0x8100); Call(feature, 1, (byte)mode);
            if (Call(feature, 2)[0] != mode) throw new IOException("模式回读不一致。请刷新核实。");
        }
        public void SetStage(int index)
        {
            BeforeWrite(); MouseState state = Read();
            if (state.Mode != 1 || index < 0 || index >= state.StageDpis.Length || !ValidDpi(state, state.StageDpis[index]))
                throw new InvalidOperationException("该板载 DPI 档位不可用。");
            writeGuard(); byte feature = Feature(0x8100); Call(feature, 12, (byte)index);
            if (Call(feature, 11)[0] != index) throw new IOException("档位回读不一致。请刷新核实。");
        }
    }
}
