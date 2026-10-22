using System;
using System.Runtime.InteropServices;

// Windows 未公开的 QuietHoursSettings 服务（勿扰 / 专注助手）
//   CLSID {F53321FA-34F8-4B7F-B9A3-361877CB94CF}  CLSID_QuietHoursSettings
//   IID   {6BFF4732-81EC-4FFB-AE67-B6C1BC29631F}  IQuietHoursSettings
//
// 布局（来自 YASB 的 comtypes 定义，实测可用）：
//   槽 3 = get_UserSelectedProfile([out] LPWSTR*)   —— 字符串由 CoTaskMemAlloc 分配，要 CoTaskMemFree
//   槽 4 = put_UserSelectedProfile([in] LPCWSTR)    —— 直接传 UTF-16 缓冲区
// 注意：这个对象同时实现了 IInspectable，但 IQuietHoursSettings 是 IUnknown 派生，
// 所以方法在槽 3/4，不是槽 6/7。按 BSTR 或 HSTRING 处理都会把堆搞坏。
internal static unsafe class Program
{
    private static readonly Guid Clsid = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");
    private static readonly Guid IidSettings = new("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F");
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private const uint CLSCTX_LOCAL_SERVER = 0x4;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr p);

    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED —— shell 里「勿扰当前 profile」的实时状态
    private const ulong WnfQuietHoursProfile = 0x0D83063EA3BF1C75UL;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(
        ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, byte[] buffer, ref uint bufferSize);

    private static string QueryWnf()
    {
        ulong name = WnfQuietHoursProfile;
        byte[] buf = new byte[512];
        uint size = (uint)buf.Length;
        int st = NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out uint stamp, buf, ref size);
        if (st != 0)
        {
            return $"NTSTATUS=0x{st:X8}";
        }

        string text = System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0');
        string hex = BitConverter.ToString(buf, 0, Math.Min((int)size, 32));
        return $"stamp=0x{stamp:X8} size={size} text='{text}' hex={hex}";
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryInterfaceFn(IntPtr self, ref Guid iid, out IntPtr ppv);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProfileFn(IntPtr self, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutProfileFn(IntPtr self, IntPtr profile);

    private static T Slot<T>(IntPtr self, int index) where T : Delegate
    {
        IntPtr vtbl = *(IntPtr*)self;
        IntPtr fn = *(IntPtr*)(vtbl + index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static void Main(string[] args)
    {
        CoInitializeEx(IntPtr.Zero, 0x2); // COINIT_APARTMENTTHREADED

        Guid clsid = Clsid;
        Guid iid = IidSettings;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out IntPtr svc);
        Console.WriteLine($"CoCreateInstance(IQuietHoursSettings) = 0x{hr:X8}");
        if (hr != 0)
        {
            return;
        }

        Guid insp = IidInspectable;
        int hrQi = Slot<QueryInterfaceFn>(svc, 0)(svc, ref insp, out IntPtr inspPtr);
        Console.WriteLine($"QI IInspectable = 0x{hrQi:X8}（不影响：方法仍在槽 3/4）");

        string current = ReadProfile(svc);
        Console.WriteLine($"profile_now      = {current ?? "(读取失败)"}");
        Console.WriteLine($"wnf_state        = {QueryWnf()}");

        if (args.Length == 0)
        {
            return;
        }

        string want = args[0] switch
        {
            "on" => "Microsoft.QuietHoursProfile.PriorityOnly",
            "off" => "Microsoft.QuietHoursProfile.Unrestricted",
            "alarms" => "Microsoft.QuietHoursProfile.AlarmsOnly",
            _ => args[0],
        };

        IntPtr buf = Marshal.StringToHGlobalUni(want);
        try
        {
            int hrPut = Slot<PutProfileFn>(svc, 4)(svc, buf);
            Console.WriteLine($"put_profile      = 0x{hrPut:X8} ({want})");
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        Console.WriteLine($"profile_after    = {ReadProfile(svc) ?? "(读取失败)"}");
        Console.WriteLine($"wnf_after        = {QueryWnf()}");

        Slot<ReleaseFn>(svc, 2)(svc);
    }

    private static string ReadProfile(IntPtr svc)
    {
        int hr = Slot<GetProfileFn>(svc, 3)(svc, out IntPtr ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
        {
            Console.WriteLine($"  get_profile hr = 0x{hr:X8}");
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            CoTaskMemFree(ptr);
        }
    }
}
