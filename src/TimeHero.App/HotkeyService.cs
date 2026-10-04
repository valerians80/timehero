using System.Windows.Interop;

namespace TimeHero.App;

/// <summary>Scorciatoia globale (finestra "message-only" nascosta).</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x7701;
    private readonly HwndSource _source;

    public bool Registered { get; }
    public event Action? Pressed;

    public HotkeyService(uint modifiers, uint virtualKey)
    {
        // HWND_MESSAGE = -3: finestra senza UI che riceve solo messaggi
        _source = new HwndSource(new HwndSourceParameters("TimeHeroHotkey") { ParentWindow = new IntPtr(-3) });
        _source.AddHook(WndProc);
        Registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId,
            modifiers | NativeMethods.MOD_NOREPEAT, virtualKey);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Registered) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _source.Dispose();
    }
}
