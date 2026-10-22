using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Win32;
using Windows.Win32.NetworkManagement.WindowsFilteringPlatform;

namespace ResetExplode;

public sealed class WfpService : IDisposable
{
    public static readonly Guid ProviderKey = new("8C3E2A71-9B4D-4F0E-A6C1-27D94B5E8F10");
    public static readonly Guid SublayerKey = new("D14B6C82-5E3A-4A91-B7F0-3C8E1D92A447");

    private static readonly Guid LayerConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid LayerConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid LayerAcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid LayerAcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid ConditionAppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private static readonly Guid[] Layers =
    [
        LayerConnectV4, LayerConnectV6, LayerAcceptV4, LayerAcceptV6
    ];

    private const uint FwpAlreadyExists = 0x80320009;
    private const uint FwpFilterNotFound = 0x80320003;
    private const uint FwpLayerNotFound = 0x80320004;
    private const uint FwpConditionNotFound = 0x80320002;

    private FWPM_ENGINE_HANDLE _engine;
    private bool _opened;

    public void Open()
    {
        if (_opened) return;
        unsafe
        {
            FWPM_ENGINE_HANDLE engine;
            var err = (uint)PInvoke.FwpmEngineOpen0(
                default,
                PInvoke.RPC_C_AUTHN_WINNT,
                null,
                null,
                &engine);
            Check(err, "FwpmEngineOpen0");
            _engine = engine;
            _opened = true;
        }

        EnsureProvider();
        EnsureSublayer();
    }

    public void EnsureReady()
    {
        Open();
        EnsureProvider();
        EnsureSublayer();
    }

    public void Block(string exePath)
    {
        Open();
        exePath = Path.GetFullPath(exePath);
        if (!File.Exists(exePath))
            throw new FileNotFoundException("Executável não encontrado.", exePath);

        unsafe
        {
            FWP_BYTE_BLOB* appId = null;
            fixed (char* pathPtr = exePath)
            {
                var err = (uint)PInvoke.FwpmGetAppIdFromFileName0(pathPtr, &appId);
                Check(err, "FwpmGetAppIdFromFileName0");
            }

            Exception? wfpError = null;
            var added = 0;
            try
            {
                for (var i = 0; i < Layers.Length; i++)
                {
                    if (AddFilter(exePath, Layers[i], i, appId, allowMissingLayer: i is 1 or 3))
                        added++;
                }
            }
            catch (Exception ex)
            {
                wfpError = ex;
            }
            finally
            {
                PInvoke.FwpmFreeMemory0((void**)&appId);
            }

            var wfOk = TryWindowsFirewall(() => WindowsFirewallRules.Block(exePath));
            ConnectionService.DropTcpConnections(exePath);

            if (added == 0 && !wfOk)
                throw wfpError ?? new InvalidOperationException("Não foi possível criar a regra de bloqueio.");
        }
    }

    public void Unblock(string exePath)
    {
        Open();
        exePath = Path.GetFullPath(exePath);
        unsafe
        {
            for (var i = 0; i < Layers.Length; i++)
            {
                var key = FilterKey(exePath, i);
                var err = (uint)PInvoke.FwpmFilterDeleteByKey0(_engine, &key);
                if (err == FwpFilterNotFound || err == 0)
                    continue;
                Check(err, "FwpmFilterDeleteByKey0");
            }
        }

        TryWindowsFirewall(() => WindowsFirewallRules.Unblock(exePath));
    }

    public HashSet<string> GetBlockedPaths()
    {
        Open();
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        unsafe
        {
            FWPM_FILTER_ENUM_HANDLE enumHandle;
            Check((uint)PInvoke.FwpmFilterCreateEnumHandle0(_engine, null, &enumHandle), "FwpmFilterCreateEnumHandle0");
            try
            {
                while (true)
                {
                    FWPM_FILTER0** entries;
                    uint count;
                    Check((uint)PInvoke.FwpmFilterEnum0(_engine, enumHandle, 200, &entries, &count), "FwpmFilterEnum0");
                    if (count == 0) break;

                    try
                    {
                        for (uint i = 0; i < count; i++)
                        {
                            var filter = entries[i];
                            if (filter is null || filter->providerKey is null) continue;
                            if (*filter->providerKey != ProviderKey) continue;
                            var desc = filter->displayData.description.ToString();
                            if (!string.IsNullOrWhiteSpace(desc))
                                blocked.Add(desc);
                        }
                    }
                    finally
                    {
                        PInvoke.FwpmFreeMemory0((void**)&entries);
                    }
                }
            }
            finally
            {
                PInvoke.FwpmFilterDestroyEnumHandle0(_engine, enumHandle);
            }
        }

        foreach (var path in WindowsFirewallRules.GetBlockedPaths())
            blocked.Add(path);

        return blocked;
    }

    public void UnblockAll()
    {
        foreach (var path in GetBlockedPaths().ToArray())
            Unblock(path);
        TryWindowsFirewall(WindowsFirewallRules.UnblockAll);
    }

    public void Dispose()
    {
        if (!_opened) return;
        PInvoke.FwpmEngineClose0(_engine);
        _opened = false;
    }

    private void EnsureProvider()
    {
        unsafe
        {
            fixed (char* name = "RodrigoTech")
            fixed (char* desc = "Bloqueio de internet por aplicativo")
            {
                var provider = new FWPM_PROVIDER0
                {
                    providerKey = ProviderKey,
                    displayData = new FWPM_DISPLAY_DATA0
                    {
                        name = name,
                        description = desc
                    },
                    flags = 1
                };
                var err = (uint)PInvoke.FwpmProviderAdd0(_engine, &provider, default);
                IgnoreExists(err, "FwpmProviderAdd0");
            }
        }
    }

    private void EnsureSublayer()
    {
        unsafe
        {
            var provider = ProviderKey;
            fixed (char* name = "RodrigoTech")
            fixed (char* desc = "Camada de bloqueio por aplicativo")
            {
                var sublayer = new FWPM_SUBLAYER0
                {
                    subLayerKey = SublayerKey,
                    displayData = new FWPM_DISPLAY_DATA0
                    {
                        name = name,
                        description = desc
                    },
                    flags = 1,
                    providerKey = &provider,
                    weight = 0xFFFF
                };
                var err = (uint)PInvoke.FwpmSubLayerAdd0(_engine, &sublayer, default);
                IgnoreExists(err, "FwpmSubLayerAdd0");
            }
        }
    }

    private unsafe bool AddFilter(string exePath, Guid layer, int layerIndex, FWP_BYTE_BLOB* appId, bool allowMissingLayer)
    {
        var key = FilterKey(exePath, layerIndex);
        var provider = ProviderKey;
        var conditions = stackalloc FWPM_FILTER_CONDITION0[1];

        conditions[0].fieldKey = ConditionAppId;
        conditions[0].matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL;
        conditions[0].conditionValue.type = FWP_DATA_TYPE.FWP_BYTE_BLOB_TYPE;
        conditions[0].conditionValue.Anonymous.byteBlob = appId;

        fixed (char* name = "RodrigoTech block")
        fixed (char* desc = exePath)
        {
            var filter = new NativeFilter
            {
                filterKey = key,
                name = (IntPtr)name,
                description = (IntPtr)desc,
                flags = (uint)FWPM_FILTER_FLAGS.FWPM_FILTER_FLAG_PERSISTENT,
                providerKey = (IntPtr)(&provider),
                layerKey = layer,
                subLayerKey = SublayerKey,
                numFilterConditions = 1,
                filterCondition = (IntPtr)conditions,
                actionType = (uint)FWP_ACTION_TYPE.FWP_ACTION_BLOCK
            };

            ulong id;
            var err = FwpmFilterAddNative((IntPtr)_engine, &filter, IntPtr.Zero, &id);

            if (err == 0 || err == FwpAlreadyExists)
                return true;
            if (allowMissingLayer && err == FwpLayerNotFound)
                return false;
            Check(err, "FwpmFilterAdd0");
            return false;
        }
    }

    [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFilterAdd0", ExactSpelling = true)]
    private static extern unsafe uint FwpmFilterAddNative(IntPtr engineHandle, NativeFilter* filter, IntPtr sd, ulong* id);

    [StructLayout(LayoutKind.Explicit, Size = 200)]
    private struct NativeFilter
    {
        [FieldOffset(0)] public Guid filterKey;
        [FieldOffset(16)] public IntPtr name;
        [FieldOffset(24)] public IntPtr description;
        [FieldOffset(32)] public uint flags;
        [FieldOffset(40)] public IntPtr providerKey;
        [FieldOffset(48)] public uint providerDataSize;
        [FieldOffset(56)] public IntPtr providerDataPtr;
        [FieldOffset(64)] public Guid layerKey;
        [FieldOffset(80)] public Guid subLayerKey;
        [FieldOffset(96)] public uint weightType;
        [FieldOffset(104)] public IntPtr weightValue;
        [FieldOffset(112)] public uint numFilterConditions;
        [FieldOffset(120)] public IntPtr filterCondition;
        [FieldOffset(128)] public uint actionType;
        [FieldOffset(132)] public Guid actionGuid;
        [FieldOffset(152)] public Guid context;
        [FieldOffset(168)] public IntPtr reserved;
        [FieldOffset(176)] public ulong filterId;
        [FieldOffset(184)] public uint effectiveWeightType;
        [FieldOffset(192)] public IntPtr effectiveWeightValue;
    }

    private static bool TryWindowsFirewall(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Guid FilterKey(string exePath, int layerIndex)
    {
        var payload = Encoding.UTF8.GetBytes($"AppFirewall|{exePath.ToUpperInvariant()}|{layerIndex}");
        var hash = SHA256.HashData(payload);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static void IgnoreExists(uint err, string op)
    {
        if (err == 0 || err == FwpAlreadyExists) return;
        Check(err, op);
    }

    private static void Check(uint err, string op)
    {
        if (err == 0) return;
        var detail = err switch
        {
            FwpLayerNotFound => "camada WFP não encontrada",
            FwpConditionNotFound => "condição WFP inválida",
            FwpFilterNotFound => "filtro não encontrado",
            5 => "acesso negado — execute como administrador",
            0x6F7 => "dados inválidos na chamada WFP",
            _ => $"0x{err:X8}"
        };
        throw new InvalidOperationException($"{op} falhou: {detail}");
    }
}
