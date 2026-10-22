"""GUI 冒烟测试：启动 exe，抓窗口文字 + 截图，然后关掉进程。

用法： python tools/verify_gui.py <exe路径> [输出png]
"""
import ctypes
import ctypes.wintypes as wt
import struct
import subprocess
import sys
import time
import zlib
from pathlib import Path

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

user32.GetWindowTextW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetClassNameW.argtypes = [wt.HWND, wt.LPWSTR, ctypes.c_int]
user32.GetWindowRect.argtypes = [wt.HWND, ctypes.POINTER(wt.RECT)]
user32.PrintWindow.argtypes = [wt.HWND, wt.HDC, wt.UINT]
user32.GetWindowThreadProcessId.argtypes = [wt.HWND, ctypes.POINTER(wt.DWORD)]


def text_of(hwnd):
    buf = ctypes.create_unicode_buffer(1024)
    user32.GetWindowTextW(hwnd, buf, 1024)
    return buf.value


def class_of(hwnd):
    buf = ctypes.create_unicode_buffer(256)
    user32.GetClassNameW(hwnd, buf, 256)
    return buf.value


def pid_of(hwnd):
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def top_windows(pid):
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(hwnd, _):
        if pid_of(hwnd) == pid:
            out.append(hwnd)
        return True

    user32.EnumWindows(cb, 0)
    return out


def child_texts(hwnd):
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def cb(child, _):
        t = text_of(child)
        r = wt.RECT()
        user32.GetWindowRect(child, ctypes.byref(r))
        geo = f"[{r.left},{r.top} {r.right - r.left}x{r.bottom - r.top}]"
        out.append(f"{class_of(child)} {geo}: {t}")
        return True

    user32.EnumChildWindows(hwnd, cb, 0)
    return out


def grab(hwnd, path, from_screen=False, inflate=30):
    user32.SetForegroundWindow(hwnd)
    time.sleep(1.2)
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))  # 位置可能刚被居中过，量第二次
    time.sleep(0.2)
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    w = rect.right - rect.left + inflate * 2
    h = rect.bottom - rect.top + inflate * 2
    if w <= 0 or h <= 0:
        return None

    if from_screen:
        hdc = user32.GetDC(0)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
        old = gdi32.SelectObject(mem, bmp)
        gdi32.BitBlt(mem, 0, 0, w, h, hdc, rect.left - inflate, rect.top - inflate, 0x00CC0020)
    else:
        hdc = user32.GetWindowDC(hwnd)
        mem = gdi32.CreateCompatibleDC(hdc)
        bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
        old = gdi32.SelectObject(mem, bmp)
        user32.PrintWindow(hwnd, mem, 2)  # PW_RENDERFULLCONTENT

    class BMIH(ctypes.Structure):
        _fields_ = [
            ("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
            ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
            ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
            ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD),
        ]

    bi = BMIH(ctypes.sizeof(BMIH), w, -h, 1, 32, 0, 0, 0, 0, 0, 0)
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    gdi32.SelectObject(mem, old)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)

    raw = bytearray()
    src = buf.raw
    for y in range(h):
        row = src[y * w * 4:(y + 1) * w * 4]
        px = bytearray(w * 4)
        px[0::4] = row[2::4]
        px[1::4] = row[1::4]
        px[2::4] = row[0::4]
        px[3::4] = b"\xff" * w
        raw += b"\x00" + bytes(px)

    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(raw), 6))
        + chunk(b"IEND", b"")
    )
    Path(path).write_bytes(png)
    return w, h


def hotkey_probe(tag):
    """试着占用 Ctrl+Alt+D：被别人占了就说明托盘程序确实注册上了。"""
    MOD_ALT, MOD_CONTROL, MOD_NOREPEAT = 0x0001, 0x0002, 0x4000
    ctypes.set_last_error(0)
    ok = user32.RegisterHotKey(None, 1, MOD_ALT | MOD_CONTROL | MOD_NOREPEAT, ord("D"))
    err = ctypes.get_last_error()
    if ok:
        user32.UnregisterHotKey(None, 1)
    print(f"hotkey[{tag}] ctrl+alt+D free={bool(ok)} err={err}")
    return bool(ok)


def main():
    # 先声明 DPI 感知，否则 Win32 返回的坐标和截屏会被系统按缩放比例糊弄
    try:
        ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
    except Exception:
        try:
            user32.SetProcessDPIAware()
        except Exception:
            pass

    exe = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else "gui.png"
    proc = subprocess.Popen([exe])
    time.sleep(3.0)
    hotkey_probe("app running")

    wins = top_windows(proc.pid)
    print(f"pid = {proc.pid}, top-level windows = {len(wins)}")
    shot_done = False
    for hwnd in wins:
        cls, title = class_of(hwnd), text_of(hwnd)
        print(f"  hwnd=0x{hwnd:X} class={cls} title={title!r} visible={bool(user32.IsWindowVisible(hwnd))}")
        for line in child_texts(hwnd):
            print("     child -> " + line.replace("\n", "\\n"))
        if user32.IsWindowVisible(hwnd) and title and not shot_done:
            print("  screenshot:", grab(hwnd, out, from_screen=True))
            shot_done = True

    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()
    print("terminated =", proc.returncode is not None)
    time.sleep(1.0)
    hotkey_probe("app exited")


if __name__ == "__main__":
    main()
