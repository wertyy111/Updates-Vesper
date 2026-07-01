using System.Runtime.InteropServices;

namespace VesperNet.Service;

internal sealed class WintunNative : IDisposable
{
    private readonly nint _libraryHandle;
    private readonly WintunCreateAdapterDelegate _createAdapter;
    private readonly WintunOpenAdapterDelegate _openAdapter;
    private readonly WintunCloseAdapterDelegate _closeAdapter;
    private readonly WintunGetRunningDriverVersionDelegate _getRunningDriverVersion;
    private readonly WintunStartSessionDelegate _startSession;
    private readonly WintunEndSessionDelegate _endSession;
    private readonly WintunReceivePacketDelegate _receivePacket;
    private readonly WintunReleaseReceivePacketDelegate _releaseReceivePacket;
    private readonly WintunAllocateSendPacketDelegate _allocateSendPacket;
    private readonly WintunSendPacketDelegate _sendPacket;
    private readonly WintunGetReadWaitEventDelegate _getReadWaitEvent;

    private WintunNative(
        nint libraryHandle,
        WintunCreateAdapterDelegate createAdapter,
        WintunOpenAdapterDelegate openAdapter,
        WintunCloseAdapterDelegate closeAdapter,
        WintunGetRunningDriverVersionDelegate getRunningDriverVersion,
        WintunStartSessionDelegate startSession,
        WintunEndSessionDelegate endSession,
        WintunReceivePacketDelegate receivePacket,
        WintunReleaseReceivePacketDelegate releaseReceivePacket,
        WintunAllocateSendPacketDelegate allocateSendPacket,
        WintunSendPacketDelegate sendPacket,
        WintunGetReadWaitEventDelegate getReadWaitEvent)
    {
        _libraryHandle = libraryHandle;
        _createAdapter = createAdapter;
        _openAdapter = openAdapter;
        _closeAdapter = closeAdapter;
        _getRunningDriverVersion = getRunningDriverVersion;
        _startSession = startSession;
        _endSession = endSession;
        _receivePacket = receivePacket;
        _releaseReceivePacket = releaseReceivePacket;
        _allocateSendPacket = allocateSendPacket;
        _sendPacket = sendPacket;
        _getReadWaitEvent = getReadWaitEvent;
    }

    public static WintunNative Load(string libraryPath)
    {
        var handle = NativeLibrary.Load(libraryPath);
        return new WintunNative(
            handle,
            GetDelegate<WintunCreateAdapterDelegate>(handle, "WintunCreateAdapter"),
            GetDelegate<WintunOpenAdapterDelegate>(handle, "WintunOpenAdapter"),
            GetDelegate<WintunCloseAdapterDelegate>(handle, "WintunCloseAdapter"),
            GetDelegate<WintunGetRunningDriverVersionDelegate>(handle, "WintunGetRunningDriverVersion"),
            GetDelegate<WintunStartSessionDelegate>(handle, "WintunStartSession"),
            GetDelegate<WintunEndSessionDelegate>(handle, "WintunEndSession"),
            GetDelegate<WintunReceivePacketDelegate>(handle, "WintunReceivePacket"),
            GetDelegate<WintunReleaseReceivePacketDelegate>(handle, "WintunReleaseReceivePacket"),
            GetDelegate<WintunAllocateSendPacketDelegate>(handle, "WintunAllocateSendPacket"),
            GetDelegate<WintunSendPacketDelegate>(handle, "WintunSendPacket"),
            GetDelegate<WintunGetReadWaitEventDelegate>(handle, "WintunGetReadWaitEvent"));
    }

    public nint OpenAdapter(string name)
    {
        return _openAdapter(name);
    }

    public nint CreateAdapter(string name, string tunnelType, Guid requestedGuid)
    {
        return _createAdapter(name, tunnelType, ref requestedGuid);
    }

    public void CloseAdapter(nint adapterHandle)
    {
        if (adapterHandle != nint.Zero)
        {
            _closeAdapter(adapterHandle);
        }
    }

    public uint GetRunningDriverVersion()
    {
        return _getRunningDriverVersion();
    }

    public nint StartSession(nint adapterHandle, uint capacity)
    {
        return _startSession(adapterHandle, capacity);
    }

    public void EndSession(nint sessionHandle)
    {
        if (sessionHandle != nint.Zero)
        {
            _endSession(sessionHandle);
        }
    }

    public nint ReceivePacket(nint sessionHandle, out uint packetSize)
    {
        return _receivePacket(sessionHandle, out packetSize);
    }

    public void ReleaseReceivePacket(nint sessionHandle, nint packetPointer)
    {
        if (sessionHandle != nint.Zero && packetPointer != nint.Zero)
        {
            _releaseReceivePacket(sessionHandle, packetPointer);
        }
    }

    public nint AllocateSendPacket(nint sessionHandle, uint packetSize)
    {
        return _allocateSendPacket(sessionHandle, packetSize);
    }

    public void SendPacket(nint sessionHandle, nint packetPointer)
    {
        if (sessionHandle != nint.Zero && packetPointer != nint.Zero)
        {
            _sendPacket(sessionHandle, packetPointer);
        }
    }

    public nint GetReadWaitEvent(nint sessionHandle)
    {
        return _getReadWaitEvent(sessionHandle);
    }

    public void Dispose()
    {
        if (_libraryHandle != nint.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
        }
    }

    private static T GetDelegate<T>(nint libraryHandle, string exportName) where T : Delegate
    {
        var export = NativeLibrary.GetExport(libraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate nint WintunCreateAdapterDelegate(string name, string tunnelType, ref Guid requestedGuid);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode, SetLastError = true)]
    private delegate nint WintunOpenAdapterDelegate(string name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate void WintunCloseAdapterDelegate(nint adapter);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate uint WintunGetRunningDriverVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate nint WintunStartSessionDelegate(nint adapter, uint capacity);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate void WintunEndSessionDelegate(nint session);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate nint WintunReceivePacketDelegate(nint session, out uint packetSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate void WintunReleaseReceivePacketDelegate(nint session, nint packet);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate nint WintunAllocateSendPacketDelegate(nint session, uint packetSize);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate void WintunSendPacketDelegate(nint session, nint packet);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate nint WintunGetReadWaitEventDelegate(nint session);
}
