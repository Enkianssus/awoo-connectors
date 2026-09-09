using System.Runtime.InteropServices;

namespace QQMusicControlPoc;

/// <summary>
/// An STA-owned isolated client: one write OR two distinct fixed status reads.
/// A returned call is not a playback acknowledgment.
/// </summary>
internal sealed class QQMusicWebApiClient : IDisposable
{
    private static readonly Guid ClassId = new("05CB0B5A-57FA-4067-B405-E1ACCA3035DF");
    private static readonly Guid ClientId = new("EEBC7E3F-B437-42A7-9C16-6978660A2E5C");
    private static readonly Guid FactoryId = new("00000001-0000-0000-C000-000000000046");
    private readonly nint module;
    private readonly nint borrowedClientInterface;
    private readonly uint ownerNativeThreadId;
    private readonly int ownerThreadId;
    private IClassFactory? factory;
    private IQmApiCli? client;
    private readonly QQMusicWebApiBudget budget = new();

    private QQMusicWebApiClient(nint module, IClassFactory factory, IQmApiCli client,
        nint borrowedClientInterface)
    {
        this.module = module;
        this.factory = factory;
        this.client = client;
        this.borrowedClientInterface = borrowedClientInterface;
        ownerThreadId = Environment.CurrentManagedThreadId;
        ownerNativeThreadId = GetCurrentThreadId();
    }

    public static QQMusicWebApiClient Open(string apiPath)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X86 ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("requires-x86-sta");
        if (!Path.IsPathFullyQualified(apiPath))
            throw new InvalidOperationException("api-path-not-absolute");

        // The host validates and holds this file open against writes/replacement.
        // Never explicitly unload it: native workers and RCWs can outlive a call.
        var module = NativeLibrary.Load(apiPath, typeof(QQMusicWebApiClient).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        IClassFactory? factory = null;
        try
        {
            var getClass = Marshal.GetDelegateForFunctionPointer<DllGetClassObjectDelegate>(
                NativeLibrary.GetExport(module, "DllGetClassObject"));
            var classId = ClassId;
            var factoryId = FactoryId;
            Marshal.ThrowExceptionForHR(getClass(ref classId, ref factoryId, out var factoryPointer));
            if (factoryPointer == 0) throw new COMException("factory-not-returned");
            try { factory = (IClassFactory)Marshal.GetObjectForIUnknown(factoryPointer); }
            finally { Marshal.Release(factoryPointer); }

            var clientId = ClientId;
            Marshal.ThrowExceptionForHR(factory.CreateInstance(0, ref clientId, out var clientPointer));
            if (clientPointer == 0) throw new COMException("client-not-returned");
            IQmApiCli client;
            try { client = (IQmApiCli)Marshal.GetObjectForIUnknown(clientPointer); }
            finally { Marshal.Release(clientPointer); }
            // Preserve only the original CreateInstance interface address. The
            // RCW owns its lifetime; this borrowed value is comparison-only.
            return new(module, factory, client, clientPointer);
        }
        catch
        {
            if (factory is not null && Marshal.IsComObject(factory))
                Marshal.FinalReleaseComObject(factory);
            throw;
        }
    }

    public void SendOnce(string command)
    {
        ObjectDisposedException.ThrowIf(client is null, this);
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("requires-owning-sta");
        budget.ReserveWrite();
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        // Do not retry even if COM throws: the native asynchronous queue may
        // already contain the command. Do not use WebPerform2 (two dispatches).
        client!.WebPerform(command);
    }

    internal QQMusicWebDrainIdentity CaptureDrainIdentity(string validatedApiSha256)
    {
        ObjectDisposedException.ThrowIf(client is null, this);
        if (Environment.CurrentManagedThreadId != ownerThreadId
            || GetCurrentThreadId() != ownerNativeThreadId)
            throw new InvalidOperationException("requires-owning-sta");
        return new(validatedApiSha256, module, borrowedClientInterface,
            checked((uint)Environment.ProcessId), ownerNativeThreadId);
    }

    internal string? QueryCurrentOnce()
    {
        ObjectDisposedException.ThrowIf(client is null, this);
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("requires-owning-sta");
        budget.ReserveCurrent();
        return client!.WebPerform3(QQMusicWebStatusProtocol.QueryCurrentXml);
    }

    internal string? QueryQueueSettingOnce()
    {
        ObjectDisposedException.ThrowIf(client is null, this);
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("requires-owning-sta");
        budget.ReserveQueueSetting();
        return client!.WebPerform3(QQMusicWebStatusProtocol.QueryQueueSettingXml);
    }

    void IDisposable.Dispose()
    {
        if (Environment.CurrentManagedThreadId != ownerThreadId)
            throw new InvalidOperationException("requires-owning-sta");
        if (client is not null && Marshal.IsComObject(client))
            Marshal.FinalReleaseComObject(client);
        client = null;
        if (factory is not null && Marshal.IsComObject(factory))
            Marshal.FinalReleaseComObject(factory);
        factory = null;
        GC.KeepAlive(module); // Process exit, not NativeLibrary.Free, owns unloading.
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DllGetClassObjectDelegate(ref Guid classId, ref Guid interfaceId, out nint instance);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IClassFactory
    {
        [PreserveSig] int CreateInstance(nint outer, ref Guid riid, out nint instance);
        [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }

    // Preserve every original dual-interface slot, but expose only SendOnce
    // from this wrapper. No arbitrary command surface is exposed by the host.
    [ComImport, Guid("EEBC7E3F-B437-42A7-9C16-6978660A2E5C"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IQmApiCli
    {
        void WebPerform([MarshalAs(UnmanagedType.BStr)] string command);
        ulong GetQQUin();
        void PutShareFriendList([MarshalAs(UnmanagedType.BStr)] string friendList);
        [return: MarshalAs(UnmanagedType.BStr)] string GetShareFriendList([MarshalAs(UnmanagedType.BStr)] string key);
        void WebPerform2([MarshalAs(UnmanagedType.BStr)] string command, [MarshalAs(UnmanagedType.BStr)] string secondCommand);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDldCacheInfo([MarshalAs(UnmanagedType.BStr)] string key);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDldConfig();
        [return: MarshalAs(UnmanagedType.BStr)] string GetDldFolderInfo();
        [return: MarshalAs(UnmanagedType.BStr)] string SelectDldFolder();
        int ConfigureDownload();
        void Drag([MarshalAs(UnmanagedType.BStr)] string information);
        uint GetVersion();
        [return: MarshalAs(UnmanagedType.BStr)] string? WebPerform3([MarshalAs(UnmanagedType.BStr)] string command);
        void FakeAsyncXmlCmd(int identifier, [MarshalAs(UnmanagedType.BStr)] string command);
    }
}
