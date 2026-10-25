using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LogiG304.Standalone
{
    internal sealed class HidEndpoint
    {
        public string Path;
        public ushort UsagePage, Usage, InputLength, OutputLength;
        public override string ToString() { return string.Format("HID {0:X4}:{1:X4}, input={2}, output={3}", UsagePage, Usage, InputLength, OutputLength); }
    }

    internal interface ITransport : IDisposable
    {
        byte[] Exchange(byte device, byte feature, byte function, byte[] parameters);
    }

    // Uses only the inbox Windows HID driver. Never opens the keyboard/mouse input collection.
    internal sealed class HidTransport : ITransport
    {
        readonly FileStream stream;
        readonly HidEndpoint endpoint;
        bool disposed;
        byte software = 9;

        public HidTransport(HidEndpoint endpoint)
        {
            this.endpoint = endpoint;
            SafeFileHandle handle = Native.CreateFile(endpoint.Path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try { stream = new FileStream(handle, FileAccess.ReadWrite, endpoint.InputLength, true); }
            catch { handle.Dispose(); throw; }
        }

        public static List<HidEndpoint> Enumerate()
        {
            var result = new List<HidEndpoint>();
            Guid guid; Native.HidD_GetHidGuid(out guid);
            IntPtr set = Native.SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                for (uint i = 0; ; i++)
                {
                    var info = new Native.InterfaceData(); info.Size = Marshal.SizeOf(info);
                    if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref info))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 259) break;
                        throw new Win32Exception(error);
                    }
                    uint size;
                    Native.SetupDiGetDeviceInterfaceDetail(set, ref info, IntPtr.Zero, 0, out size, IntPtr.Zero);
                    if (size < 8 || size > 65536) continue;
                    IntPtr buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref info, buffer, size, out size, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
                        // A narrow receiver allowlist; never send gaming commands to another Logitech product.
                        if (path == null || path.IndexOf("vid_046d&pid_c53f", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (SafeFileHandle handle = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
                        {
                            if (handle.IsInvalid) continue;
                            IntPtr preparsed;
                            if (!Native.HidD_GetPreparsedData(handle, out preparsed)) continue;
                            try
                            {
                                Native.Caps caps;
                                if (Native.HidP_GetCaps(preparsed, out caps) != 0x110000) continue;
                                if (caps.UsagePage != 0xFF00 || caps.InputLength != 20 || caps.OutputLength != 20) continue;
                                result.Add(new HidEndpoint { Path = path, UsagePage = caps.UsagePage, Usage = caps.Usage, InputLength = caps.InputLength, OutputLength = caps.OutputLength });
                            }
                            finally { Native.HidD_FreePreparsedData(preparsed); }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        public static bool IsReply(byte[] reply, byte device, byte feature, byte address)
        {
            return reply.Length >= 7 && (reply[0] == 0x10 || reply[0] == 0x11) && reply[1] == device &&
                ((reply[2] == feature && reply[3] == address) ||
                 ((reply[2] == 0xFF || reply[2] == 0x8F) && reply[3] == feature && reply[4] == address));
        }

        public byte[] Exchange(byte device, byte feature, byte function, byte[] parameters)
        {
            if (disposed) throw new ObjectDisposedException("HID connection");
            if (function > 15 || parameters.Length > 16) throw new ArgumentException("Invalid HID++ request.");
            software = (byte)(software == 14 ? 9 : software + 1);
            byte address = (byte)((function << 4) | software);
            byte[] request = new byte[endpoint.OutputLength];
            request[0] = 0x11; request[1] = device; request[2] = feature; request[3] = address;
            Array.Copy(parameters, 0, request, 4, parameters.Length);
            try
            {
                var clock = Stopwatch.StartNew();
                IAsyncResult write = stream.BeginWrite(request, 0, request.Length, null, null);
                using (var wait = write.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(1000)) throw new TimeoutException("USB write timed out.");
                    stream.EndWrite(write);
                }
                while (clock.ElapsedMilliseconds < 1800)
                {
                    byte[] response = new byte[endpoint.InputLength];
                    IAsyncResult read = stream.BeginRead(response, 0, response.Length, null, null);
                    int length;
                    using (var wait = read.AsyncWaitHandle)
                    {
                        if (!wait.WaitOne(Math.Max(1, 1800 - (int)clock.ElapsedMilliseconds))) throw new TimeoutException("鼠标没有回应。请移动鼠标唤醒，再刷新。");
                        length = stream.EndRead(read);
                    }
                    if (length == 0) throw new IOException("接收器已断开。");
                    if (length != endpoint.InputLength) throw new IOException("收到不完整的 HID 报告。");
                    if (!IsReply(response, device, feature, address)) continue;
                    if (response[2] == 0xFF || response[2] == 0x8F)
                        throw new ProtocolException(response[5]);
                    byte[] data = new byte[16]; Array.Copy(response, 4, data, 0, Math.Min(16, length - 4));
                    return data;
                }
                throw new TimeoutException("HID++ response timed out.");
            }
            catch (ProtocolException) { throw; }
            catch { Dispose(); throw; }
        }

        public void Dispose() { if (!disposed) { disposed = true; stream.Dispose(); } }
    }

    internal sealed class ProtocolException : IOException
    {
        public readonly byte Code;
        public ProtocolException(byte code) : base("设备拒绝命令，HID++ 错误 0x" + code.ToString("X2")) { Code = code; }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct InterfaceData { public int Size; public Guid ClassGuid; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] internal struct Caps
        {
            public ushort Usage, UsagePage, InputLength, OutputLength, FeatureLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices;
        }
        [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll")] internal static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr data, out Caps caps);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr device, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr device);
        [DllImport("setupapi.dll")] internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
