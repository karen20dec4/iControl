using System;
using System.Runtime.InteropServices;

// Core Audio, declared down to the few methods actually used. The unused
// entries are placeholders that keep the vtable slots lined up.
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float levelDb, Guid eventContext);
    int SetMasterVolumeLevelScalar(float level, Guid eventContext);
    int GetMasterVolumeLevel(out float levelDb);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channel, float levelDb, Guid eventContext);
    int SetChannelVolumeLevelScalar(uint channel, float level, Guid eventContext);
    int GetChannelVolumeLevel(uint channel, out float levelDb);
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid eventContext);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
}

[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr collection);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
class MMDeviceEnumerator { }

static class Audio
{
    const int ERender = 0;      // eRender
    const int EMultimedia = 1;  // eMultimedia
    const int CLSCTX_ALL = 23;

    // Re-resolved on every change, so switching the default output device works.
    static IAudioEndpointVolume Endpoint()
    {
        IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
        IMMDevice device;
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out device));
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object instance;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out instance));
        return (IAudioEndpointVolume)instance;
    }

    // Returns the resulting level in percent. Raising the volume clears mute.
    public static int Nudge(float delta)
    {
        IAudioEndpointVolume v = Endpoint();
        float level;
        Marshal.ThrowExceptionForHR(v.GetMasterVolumeLevelScalar(out level));
        level += delta;
        if (level < 0f) level = 0f;
        if (level > 1f) level = 1f;
        Marshal.ThrowExceptionForHR(v.SetMasterVolumeLevelScalar(level, Guid.Empty));

        bool muted;
        if (delta > 0f && v.GetMute(out muted) == 0 && muted)
            v.SetMute(false, Guid.Empty);

        return (int)Math.Round(level * 100f);
    }

    // Returns the new mute state, plus the level sitting behind it.
    public static bool ToggleMute(out int percent)
    {
        IAudioEndpointVolume v = Endpoint();
        bool muted;
        Marshal.ThrowExceptionForHR(v.GetMute(out muted));
        bool target = !muted;
        Marshal.ThrowExceptionForHR(v.SetMute(target, Guid.Empty));

        float level;
        v.GetMasterVolumeLevelScalar(out level);
        percent = (int)Math.Round(level * 100f);
        return target;
    }
}
