using System;
using System.Windows.Forms;

// Message-only window that owns the hotkeys. It is deliberately separate from
// the overlay: the overlay changes properties that can force a handle to be
// recreated, which would orphan every registration made against it.
sealed class HotkeyWindow : NativeWindow
{
    public event Action<int> Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == KeysNative.WM_HOTKEY)
        {
            Action<int> handler = Pressed;
            if (handler != null) handler(m.WParam.ToInt32());
        }
        base.WndProc(ref m);
    }
}
