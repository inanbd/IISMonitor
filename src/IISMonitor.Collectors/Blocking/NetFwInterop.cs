using System.Runtime.InteropServices;

namespace IISMonitor.Collectors.Blocking.Interop;

// Values from icftypes.h (Microsoft Learn: NET_FW_RULE_DIRECTION, NET_FW_ACTION, NET_FW_PROFILE_TYPE2,
// NET_FW_MODIFY_STATE, NET_FW_IP_PROTOCOL). All are 32-bit C enums, so plain int-backed C# enums marshal correctly.

internal enum NetFwRuleDirection
{
    In = 1,
    Out = 2,
}

internal enum NetFwAction
{
    Block = 0,
    Allow = 1,
}

[Flags]
internal enum NetFwProfileType2
{
    Domain = 0x1,
    Private = 0x2,
    Public = 0x4,
    All = 0x7FFFFFFF,
}

internal enum NetFwModifyState
{
    /// <summary>Adding or changing a rule takes effect in the current profile.</summary>
    Ok = 0,
    /// <summary>The current profile is controlled by Group Policy; local changes will not take effect.</summary>
    GroupPolicyOverride = 1,
    /// <summary>Unsolicited inbound traffic is not allowed on the current profile ("shields up").</summary>
    InboundBlocked = 2,
}

internal static class NetFwIpProtocol
{
    public const int Tcp = 6;
    public const int Udp = 17;
    public const int Any = 256;
}

internal static class NetFwClsid
{
    /// <summary>coclass NetFwPolicy2, ProgID "HNetCfg.FwPolicy2" (FirewallAPI.dll).</summary>
    public static readonly Guid Policy2 = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");

    /// <summary>coclass NetFwRule, ProgID "HNetCfg.FWRule" (FirewallAPI.dll).</summary>
    public static readonly Guid Rule = new("2C5BC43E-3369-4C33-AB0C-BE9469677AF4");
}

// The interfaces are dual (IDispatch-derived). With ComInterfaceType.InterfaceIsDual the runtime puts the
// IDispatch slots first, so the members below must appear exactly in netfw.idl order, with every propget
// immediately followed by its propput. Names do not matter for vtable calls; order and signatures do.

/// <summary>INetFwRule (netfw.h), dispids 1-18. INetFwRule2/3 extend it with dispids 19-25 (not needed here).</summary>
[ComImport]
[Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetFwRule
{
    [DispId(1)]
    string? Name
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(2)]
    string? Description
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(3)]
    string? ApplicationName
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(4)]
    string? ServiceName
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(5)]
    int Protocol { get; set; }

    [DispId(6)]
    string? LocalPorts
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(7)]
    string? RemotePorts
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(8)]
    string? LocalAddresses
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(9)]
    string? RemoteAddresses
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(10)]
    string? IcmpTypesAndCodes
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(11)]
    NetFwRuleDirection Direction { get; set; }

    /// <summary>VARIANT holding a SAFEARRAY of interface names.</summary>
    [DispId(12)]
    object? Interfaces
    {
        [return: MarshalAs(UnmanagedType.Struct)] get;
        [param: MarshalAs(UnmanagedType.Struct)] set;
    }

    [DispId(13)]
    string? InterfaceTypes
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(14)]
    bool Enabled
    {
        [return: MarshalAs(UnmanagedType.VariantBool)] get;
        [param: MarshalAs(UnmanagedType.VariantBool)] set;
    }

    [DispId(15)]
    string? Grouping
    {
        [return: MarshalAs(UnmanagedType.BStr)] get;
        [param: MarshalAs(UnmanagedType.BStr)] set;
    }

    [DispId(16)]
    int Profiles { get; set; }

    [DispId(17)]
    bool EdgeTraversal
    {
        [return: MarshalAs(UnmanagedType.VariantBool)] get;
        [param: MarshalAs(UnmanagedType.VariantBool)] set;
    }

    [DispId(18)]
    NetFwAction Action { get; set; }
}

/// <summary>INetFwRules (netfw.h).</summary>
[ComImport]
[Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetFwRules
{
    [DispId(1)]
    int Count { get; }

    [DispId(2)]
    void Add([MarshalAs(UnmanagedType.Interface)] INetFwRule rule);

    /// <summary>No effect if no rule has this name.</summary>
    [DispId(3)]
    void Remove([MarshalAs(UnmanagedType.BStr)] string name);

    /// <summary>Throws with HRESULT 0x80070002 (FileNotFoundException in .NET) when no rule has this name.</summary>
    [DispId(4)]
    [return: MarshalAs(UnmanagedType.Interface)]
    INetFwRule Item([MarshalAs(UnmanagedType.BStr)] string name);

    /// <summary>_NewEnum: an IUnknown that implements IEnumVARIANT.</summary>
    [DispId(-4)]
    object NewEnum
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] get;
    }
}

/// <summary>INetFwPolicy2 (netfw.h), dispids 1-15. Indexed properties are declared as get_/put_ methods.</summary>
[ComImport]
[Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
[InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface INetFwPolicy2
{
    /// <summary>Bitmask of NET_FW_PROFILE_TYPE2 values that are active right now.</summary>
    [DispId(1)]
    int CurrentProfileTypes { get; }

    [DispId(2)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_FirewallEnabled(NetFwProfileType2 profileType);

    [DispId(2)]
    void put_FirewallEnabled(NetFwProfileType2 profileType, [MarshalAs(UnmanagedType.VariantBool)] bool enabled);

    [DispId(3)]
    [return: MarshalAs(UnmanagedType.Struct)]
    object? get_ExcludedInterfaces(NetFwProfileType2 profileType);

    [DispId(3)]
    void put_ExcludedInterfaces(NetFwProfileType2 profileType, [MarshalAs(UnmanagedType.Struct)] object? interfaces);

    [DispId(4)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_BlockAllInboundTraffic(NetFwProfileType2 profileType);

    [DispId(4)]
    void put_BlockAllInboundTraffic(NetFwProfileType2 profileType, [MarshalAs(UnmanagedType.VariantBool)] bool block);

    [DispId(5)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_NotificationsDisabled(NetFwProfileType2 profileType);

    [DispId(5)]
    void put_NotificationsDisabled(NetFwProfileType2 profileType, [MarshalAs(UnmanagedType.VariantBool)] bool disabled);

    [DispId(6)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_UnicastResponsesToMulticastBroadcastDisabled(NetFwProfileType2 profileType);

    [DispId(6)]
    void put_UnicastResponsesToMulticastBroadcastDisabled(NetFwProfileType2 profileType, [MarshalAs(UnmanagedType.VariantBool)] bool disabled);

    [DispId(7)]
    INetFwRules Rules
    {
        [return: MarshalAs(UnmanagedType.Interface)] get;
    }

    /// <summary>INetFwServiceRestriction; declared as IUnknown because we never use it (the slot must exist).</summary>
    [DispId(8)]
    object ServiceRestriction
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] get;
    }

    [DispId(9)]
    void EnableRuleGroup(int profileTypesBitmask, [MarshalAs(UnmanagedType.BStr)] string group, [MarshalAs(UnmanagedType.VariantBool)] bool enable);

    [DispId(10)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool IsRuleGroupEnabled(int profileTypesBitmask, [MarshalAs(UnmanagedType.BStr)] string group);

    /// <summary>Resets the whole local firewall policy. The slot must be declared; never call it.</summary>
    [DispId(11)]
    void RestoreLocalFirewallDefaults();

    [DispId(12)]
    NetFwAction get_DefaultInboundAction(NetFwProfileType2 profileType);

    [DispId(12)]
    void put_DefaultInboundAction(NetFwProfileType2 profileType, NetFwAction action);

    [DispId(13)]
    NetFwAction get_DefaultOutboundAction(NetFwProfileType2 profileType);

    [DispId(13)]
    void put_DefaultOutboundAction(NetFwProfileType2 profileType, NetFwAction action);

    [DispId(14)]
    [return: MarshalAs(UnmanagedType.VariantBool)]
    bool get_IsRuleGroupCurrentlyEnabled([MarshalAs(UnmanagedType.BStr)] string group);

    [DispId(15)]
    NetFwModifyState LocalPolicyModifyState { get; }
}
