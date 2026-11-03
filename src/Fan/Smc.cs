using System;
using System.Runtime.InteropServices;
using System.Text;

// Acces la Apple SMC prin driverul Boot Camp (\\.\MacHALDriver), fara drivere terte.
// Cheile folosite: F0Ac (turatie reala), F0Tg (tinta), F0Mn/F0Mx (min/max), FS! (mod manual),
// TC*c / TG** (temperaturi CPU / GPU, format sp78).

static class SmcNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;

    public const uint IOCTL_SMC_READ = 0x9C402454;
    public const uint IOCTL_SMC_WRITE = 0x9C402458;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        byte[] lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}

sealed class SmcDevice : IDisposable
{
    IntPtr handle = new IntPtr(-1);
    readonly object sync = new object();

    public bool IsOpen
    {
        get { return handle != IntPtr.Zero && handle != new IntPtr(-1); }
    }

    public bool EnsureOpen()
    {
        lock (sync)
        {
            if (IsOpen) return true;
            handle = SmcNative.CreateFileW(
                @"\\.\MacHALDriver",
                SmcNative.GENERIC_READ | SmcNative.GENERIC_WRITE,
                SmcNative.FILE_SHARE_READ | SmcNative.FILE_SHARE_WRITE,
                IntPtr.Zero,
                SmcNative.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            return IsOpen;
        }
    }

    public byte[] ReadKey(string key, int size)
    {
        lock (sync)
        {
            if (!EnsureOpen()) return null;
            byte[] inBuf = new byte[5];
            byte[] keyBytes = Encoding.ASCII.GetBytes(key);
            Array.Copy(keyBytes, 0, inBuf, 0, Math.Min(4, keyBytes.Length));
            inBuf[4] = 0;

            byte[] outBuf = new byte[size];
            int returned;
            bool ok = SmcNative.DeviceIoControl(
                handle,
                SmcNative.IOCTL_SMC_READ,
                inBuf,
                inBuf.Length,
                outBuf,
                outBuf.Length,
                out returned,
                IntPtr.Zero);
            if (!ok || returned != size) return null;
            return outBuf;
        }
    }

    public bool WriteKey(string key, byte[] data)
    {
        lock (sync)
        {
            if (!EnsureOpen()) return false;
            byte[] inBuf = new byte[5 + data.Length];
            byte[] keyBytes = Encoding.ASCII.GetBytes(key);
            Array.Copy(keyBytes, 0, inBuf, 0, Math.Min(4, keyBytes.Length));
            inBuf[4] = 0;
            Array.Copy(data, 0, inBuf, 5, data.Length);

            int returned;
            return SmcNative.DeviceIoControl(
                handle,
                SmcNative.IOCTL_SMC_WRITE,
                inBuf,
                inBuf.Length,
                null,
                0,
                out returned,
                IntPtr.Zero);
        }
    }

    public double? ReadFpe2(string key)
    {
        byte[] raw = ReadKey(key, 2);
        if (raw == null) return null;
        int val = (raw[0] << 8) | raw[1];
        return val / 4.0;
    }

    public bool WriteFpe2(string key, int rpm)
    {
        int rawVal = Math.Max(0, Math.Min(0xFFFF, rpm * 4));
        byte[] data = new byte[] { (byte)((rawVal >> 8) & 0xFF), (byte)(rawVal & 0xFF) };
        return WriteKey(key, data);
    }

    public int? ReadUi16(string key)
    {
        byte[] raw = ReadKey(key, 2);
        if (raw == null) return null;
        return (raw[0] << 8) | raw[1];
    }

    public bool WriteUi16(string key, int val)
    {
        byte[] data = new byte[] { (byte)((val >> 8) & 0xFF), (byte)(val & 0xFF) };
        return WriteKey(key, data);
    }

    public double? ReadSp78(string key)
    {
        byte[] raw = ReadKey(key, 2);
        if (raw == null) return null;
        short signedVal = (short)((raw[0] << 8) | raw[1]);
        return signedVal / 256.0;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (IsOpen)
            {
                SmcNative.CloseHandle(handle);
                handle = new IntPtr(-1);
            }
        }
    }
}
