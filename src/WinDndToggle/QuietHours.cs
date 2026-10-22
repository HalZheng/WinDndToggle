using System;
using System.Runtime.InteropServices;

namespace WinDndToggle;

internal enum DndState
{
    Off,
    PriorityOnly,
    AlarmsOnly,
    Unknown,
}

/// <summary>
/// 通过 Windows 未公开的 QuietHoursSettings COM 服务读写「勿扰 / 专注助手」状态。
///
///   CLSID {F53321FA-34F8-4B7F-B9A3-361877CB94CF}  CLSID_QuietHoursSettings（本地服务，out-of-proc）
///   IID   {6BFF4732-81EC-4FFB-AE67-B6C1BC29631F}  IQuietHoursSettings
///
/// 接口是 IUnknown 派生的普通 COM 接口，方法在 vtable 槽 3/4：
///   槽 3  get_UserSelectedProfile([out] LPWSTR*)  返回值由 CoTaskMemAlloc 分配，必须 CoTaskMemFree
///   槽 4  put_UserSelectedProfile([in] LPCWSTR)   直接传 UTF-16 缓冲区
///
/// 坑（踩过了）：
///   - 这个对象同时也 QI 得到 IInspectable，但方法不在槽 6/7，别按 WinRT 接口调；
///   - 返回值是裸 LPWSTR，不是 BSTR、不是 HSTRING，按 BSTR 释放会把堆写坏（0xC0000374）。
/// </summary>
internal static class QuietHours
{
    private static readonly Guid Clsid = new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");
    private static readonly Guid Iid = new("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F");

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const int COINIT_APARTMENTTHREADED = 0x2;

    private const int SlotRelease = 2;
    private const int SlotGetProfile = 3;
    private const int SlotPutProfile = 4;

    private const string ProfileOff = "Microsoft.QuietHoursProfile.Unrestricted";
    private const string ProfilePriority = "Microsoft.QuietHoursProfile.PriorityOnly";
    private const string ProfileAlarms = "Microsoft.QuietHoursProfile.AlarmsOnly";

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coinit);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr p);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetProfileFn(IntPtr self, out IntPtr profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int PutProfileFn(IntPtr self, IntPtr profile);

    private static bool _comReady;

    /// <summary>UI 线程是 STA，这里只是确保 COM 初始化过；重复调用无害。</summary>
    public static void EnsureCom()
    {
        if (_comReady)
        {
            return;
        }

        int hr = CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        // S_OK / S_FALSE / RPC_E_CHANGED_MODE 都可以继续
        _comReady = hr >= 0 || (uint)hr == 0x80010106;
    }

    private static T Slot<T>(IntPtr self, int index) where T : Delegate
    {
        IntPtr vtbl = Marshal.ReadIntPtr(self);
        IntPtr fn = Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }

    private static bool TryCreate(out IntPtr svc, out string error)
    {
        svc = IntPtr.Zero;
        error = "";

        EnsureCom();

        Guid clsid = Clsid;
        Guid iid = Iid;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out svc);
        if (hr == 0 && svc != IntPtr.Zero)
        {
            return true;
        }

        svc = IntPtr.Zero;
        error = (uint)hr switch
        {
            0x80040154 => "这台系统上没有 QuietHoursSettings 这个 COM 接口（REGDB_E_CLASSNOTREG），无法读写勿扰状态。",
            0x80080005 => "QuietHoursSettings 的本地服务启动失败（CO_E_SERVER_EXEC_FAILURE）。",
            _ => $"无法连接 QuietHoursSettings 服务（HRESULT 0x{hr:X8}）。",
        };
        return false;
    }

    private static void Release(IntPtr svc)
    {
        if (svc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            Slot<ReleaseFn>(svc, SlotRelease)(svc);
        }
        catch
        {
            // 释放失败不该影响调用方
        }
    }

    public static bool TryRead(out DndState state, out string error)
    {
        state = DndState.Unknown;

        if (!TryReadRaw(out string profile, out error))
        {
            return false;
        }

        state = FromProfile(profile);
        return true;
    }

    /// <summary>读原始 profile 字符串，排查时有用。</summary>
    public static bool TryReadRaw(out string profile, out string error)
    {
        profile = "";
        error = "";

        if (!TryCreate(out IntPtr svc, out error))
        {
            return false;
        }

        try
        {
            int hr = Slot<GetProfileFn>(svc, SlotGetProfile)(svc, out IntPtr ptr);
            if (hr != 0)
            {
                error = $"读取勿扰状态失败（HRESULT 0x{hr:X8}）。";
                return false;
            }

            if (ptr == IntPtr.Zero)
            {
                error = "读取勿扰状态返回空值。";
                return false;
            }

            try
            {
                profile = Marshal.PtrToStringUni(ptr) ?? "";
                return true;
            }
            finally
            {
                CoTaskMemFree(ptr);
            }
        }
        catch (Exception ex)
        {
            error = $"读取勿扰状态时出错：{ex.GetType().Name} {ex.Message}";
            return false;
        }
        finally
        {
            Release(svc);
        }
    }

    public static bool TrySet(DndState target, out string error)
    {
        error = "";

        string profile = target switch
        {
            DndState.Off => ProfileOff,
            DndState.PriorityOnly => ProfilePriority,
            DndState.AlarmsOnly => ProfileAlarms,
            _ => ProfileOff,
        };

        if (!TryCreate(out IntPtr svc, out error))
        {
            return false;
        }

        IntPtr buffer = Marshal.StringToHGlobalUni(profile);
        try
        {
            int hr = Slot<PutProfileFn>(svc, SlotPutProfile)(svc, buffer);
            if (hr != 0)
            {
                error = $"写入勿扰状态失败（HRESULT 0x{hr:X8}）。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"写入勿扰状态时出错：{ex.GetType().Name} {ex.Message}";
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Release(svc);
        }
    }

    /// <summary>和任务栏那个铃铛一样：开着就关，关着就开成「仅优先事项」。</summary>
    public static bool TryToggle(out DndState state, out string error)
    {
        state = DndState.Unknown;

        if (!TryRead(out DndState current, out error))
        {
            return false;
        }

        DndState target = current == DndState.Off ? DndState.PriorityOnly : DndState.Off;
        if (!TrySet(target, out error))
        {
            return false;
        }

        state = target;
        return true;
    }

    private static DndState FromProfile(string profile) => profile switch
    {
        ProfileOff => DndState.Off,
        ProfilePriority => DndState.PriorityOnly,
        ProfileAlarms => DndState.AlarmsOnly,
        _ => DndState.Unknown,
    };

    /// <summary>给人看的短描述，CLI 用。</summary>
    public static string Describe(string profile) => FromProfile(profile) switch
    {
        DndState.Off => "off",
        DndState.PriorityOnly => "on (priority only)",
        DndState.AlarmsOnly => "on (alarms only)",
        _ => "unknown",
    };
}
