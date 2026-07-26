using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Snipwhiz.Core.Hotkeys;

/// <summary>
/// Owns a message-only window and the RegisterHotKey registrations against it.
/// Registration failure is never fatal: the tray menu always offers every action.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint VkPrintScreen = 0x2C;

    private readonly MessageOnlyWindow _window;
    private readonly List<HotkeyId> _registered = [];

    public event Action<HotkeyId>? Pressed;

    public HotkeyService()
    {
        _window = new MessageOnlyWindow(id => Pressed?.Invoke(id));
    }

    /// <returns>False when another process holds the chord. Callers must not treat this as fatal.</returns>
    public bool TryRegister(HotkeyId id, uint modifiers, uint virtualKey)
    {
        // MOD_NOREPEAT stops auto-repeat firing a capture storm on a held key.
        const uint ModNoRepeat = 0x4000;
        if (!PInvoke.RegisterHotKey(_window.Handle, (int)id, (HOT_KEY_MODIFIERS)(modifiers | ModNoRepeat), virtualKey))
            return false;

        _registered.Add(id);
        return true;
    }

    public void Unregister(HotkeyId id)
    {
        if (_registered.Remove(id)) PInvoke.UnregisterHotKey(_window.Handle, (int)id);
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToArray()) PInvoke.UnregisterHotKey(_window.Handle, (int)id);
        _registered.Clear();
        _window.Dispose();
    }

    /// <summary>A HWND_MESSAGE window exists only to receive WM_HOTKEY.</summary>
    private sealed class MessageOnlyWindow : IDisposable
    {
        private const int WmHotkey = 0x0312;
        private readonly System.Windows.Forms.NativeWindow _native;

        public HWND Handle { get; }

        public MessageOnlyWindow(Action<HotkeyId> onPressed)
        {
            _native = new Sink(onPressed);
            ((Sink)_native).CreateHandle(new System.Windows.Forms.CreateParams
            {
                Parent = new IntPtr(-3),          // HWND_MESSAGE
            });
            Handle = (HWND)_native.Handle;
        }

        public void Dispose() => ((Sink)_native).DestroyHandle();

        private sealed class Sink(Action<HotkeyId> onPressed) : System.Windows.Forms.NativeWindow
        {
            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                if (m.Msg == WmHotkey) onPressed((HotkeyId)m.WParam.ToInt32());
                base.WndProc(ref m);
            }
        }
    }
}
