using System.Runtime.InteropServices;
using Serilog;

namespace Spana.Backend.Player;

// Volume for this process's Windows audio sessions. Windows applies it in ~50 ms, while VLC's own volume
// only reaches the speakers after the ~0.6 s of audio it has already queued. Channel volumes are used
// because VLC writes the session's master volume itself.
public static class AppAudio
{
    private static readonly uint Pid = (uint)Environment.ProcessId;
    private static readonly object Gate = new();
    private static Guid _context = Guid.NewGuid();

    // Returns true when at least one session of this process was found and set.
    public static bool Apply(float level)
    {
        lock (Gate)
        {
            try
            {
                bool found = false;
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Check(enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceStateActive, out var devices));
                Check(devices.GetCount(out uint count));
                for (uint d = 0; d < count; d++)
                {
                    Check(devices.Item(d, out var device));
                    var iid = typeof(IAudioSessionManager2).GUID;
                    if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var managerObj) != 0) continue;
                    var manager = (IAudioSessionManager2)managerObj;
                    if (manager.GetSessionEnumerator(out var sessions) != 0) continue;
                    sessions.GetCount(out int n);
                    for (int i = 0; i < n; i++)
                    {
                        if (sessions.GetSession(i, out var control) != 0) continue;
                        var control2 = (IAudioSessionControl2)control;
                        if (control2.GetProcessId(out uint pid) != 0 || pid != Pid) continue;
                        var channels = (IChannelAudioVolume)control;
                        channels.GetChannelCount(out uint chCount);
                        for (uint c = 0; c < chCount; c++) channels.SetChannelVolume(c, level, ref _context);
                        found = true;
                    }
                }
                return found;
            }
            catch (Exception ex)
            {
                Log.Debug("Session volume failed: {Message}", ex.Message);
                return false;
            }
        }
    }

    private static void Check(int hr)
    {
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    private const uint DeviceStateActive = 1;
    private const int ClsCtxAll = 23;

    private enum DataFlow { Render = 0 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr name, ref Guid context);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IChannelAudioVolume
    {
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid context);
    }
}
