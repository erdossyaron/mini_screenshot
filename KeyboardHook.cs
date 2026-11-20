using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Screen;

/// <summary>
/// Alacsony szintű billentyűhook a PrtSc gombra (bármilyen módosítóval, pl. Win+PrtSc).
/// Az RDP-kliens (mstsc) saját hookot tesz fel, ami elnyelheti a gombot.
/// Ezért a hookot időnként újra feltesszük: így mindig a lánc elején vagyunk,
/// és teljes képernyős RDP alatt is mi kapjuk meg először a lenyomást.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int VK_SNAPSHOT = 0x2C;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr extra; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private readonly HookProc _proc;   // erős referencia, hogy a GC ne szedje össze
    private readonly Action _onPress;
    private readonly Func<int, bool>? _onOtherKey;
    private readonly System.Windows.Forms.Timer _rehook;
    private IntPtr _handle;
    private long _lastPress;

    /// <param name="onPress">PrtSc lenyomásakor fut.</param>
    /// <param name="onOtherKey">
    /// Minden más billentyű lenyomásakor fut (virtuális kód). Ha true-t ad vissza,
    /// a billentyűt elnyeljük. Így az Esc akkor is működik, ha nem a mi ablakunk az aktív.
    /// </param>
    public KeyboardHook(Action onPress, Func<int, bool>? onOtherKey = null)
    {
        _onPress = onPress;
        _onOtherKey = onOtherKey;
        _proc = Callback;
        Install();
        _rehook = new System.Windows.Forms.Timer { Interval = 3000 };
        _rehook.Tick += (_, _) => Install();
        _rehook.Start();
    }

    private void Install()
    {
        var old = _handle;
        using var mod = Process.GetCurrentProcess().MainModule!;
        _handle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod.ModuleName), 0);
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode == VK_SNAPSHOT)
            {
                int msg = (int)wParam;
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
                {
                    // Nyomva tartásnál ne ismételjen; rehook közbeni dupla hívás ellen is véd
                    long now = Environment.TickCount64;
                    if (now - _lastPress > 300) { _lastPress = now; _onPress(); }
                }
                return (IntPtr)1;   // elnyeljük: se Snipping Tool, se távoli gép ne kapja meg
            }
            if (_onOtherKey != null && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN && _onOtherKey(k.vkCode))
                return (IntPtr)1;
        }
        return CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        _rehook.Dispose();
        if (_handle != IntPtr.Zero) UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
    }
}
