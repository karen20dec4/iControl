using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Eticheta text incorporata in taskbar, imediat in stanga zonei de notificare (TrayNotifyWnd).
// Este o fereastra copil layered (alpha per pixel) a lui Shell_TrayWnd; lista de butoane
// (MSTaskSwWClass) este ingustata ca sa ii faca loc. Daca explorer.exe reporneste, eticheta
// se reataseaza la noul taskbar la urmatorul refresh.
sealed class TaskbarLabel : NativeWindow, IDisposable
{
    public struct Segment
    {
        public string Text;
        public Color Color;
        public Segment(string text, Color color) { Text = text; Color = color; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
    static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")]
    static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr ho);

    const int WS_CHILD = 0x40000000;
    const int WS_VISIBLE = 0x10000000;
    const int WS_CLIPSIBLINGS = 0x04000000;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x0021;
    const int WM_RBUTTONUP = 0x0205;
    const int MA_NOACTIVATE = 3;
    const int ULW_ALPHA = 2;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_SHOWWINDOW = 0x0040;
    const uint SWP_HIDEWINDOW = 0x0080;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const uint WINEVENT_OUTOFCONTEXT = 0;
    const int OBJID_WINDOW = 0;

    readonly ContextMenuStrip menu;
    readonly WinEventDelegate hookProc;
    IntPtr hook = IntPtr.Zero;
    IntPtr taskbar = IntPtr.Zero;
    IntPtr rebar = IntPtr.Zero;
    IntPtr taskSw = IntPtr.Zero;
    Segment[] segments;
    int labelWidth;
    bool layingOut;

    public TaskbarLabel(ContextMenuStrip contextMenu)
    {
        menu = contextMenu;
        hookProc = OnWinEvent; // referinta pastrata ca delegatul sa nu fie colectat de GC
    }

    public void SetText(Segment[] newSegments)
    {
        segments = newSegments;
        if (!EnsureAttached()) return;
        Render();
        Layout();
    }

    bool EnsureAttached()
    {
        IntPtr tb = FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero)
        {
            Detach();
            return false;
        }

        if (tb != taskbar || Handle == IntPtr.Zero || !IsWindow(Handle))
        {
            Detach();
            taskbar = tb;

            CreateParams cp = new CreateParams();
            cp.Caption = "iMacFanControl Taskbar";
            cp.Parent = tb;
            cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS;
            cp.ExStyle = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            CreateHandle(cp);

            // Explorer re-aranjeaza lista de butoane cand se schimba zona de notificare;
            // reactionam imediat, nu doar la urmatorul refresh.
            uint pid;
            GetWindowThreadProcessId(tb, out pid);
            hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, hookProc, pid, 0, WINEVENT_OUTOFCONTEXT);
        }

        rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        taskSw = rebar != IntPtr.Zero ? FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
        return rebar != IntPtr.Zero && taskSw != IntPtr.Zero;
    }

    void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (idObject != OBJID_WINDOW) return;
        if (hwnd == taskSw || hwnd == rebar) Layout();
    }

    void Render()
    {
        if (segments == null || Handle == IntPtr.Zero) return;

        RECT tbr;
        if (!GetWindowRect(taskbar, out tbr)) return;
        int dpi = (int)GetDpiForWindow(taskbar);
        if (dpi <= 0) dpi = 96;
        int pad = 6 * dpi / 96;
        int height = tbr.Bottom - tbr.Top;
        if (height <= 0) return;

        // Segoe UI 9pt, ca ceasul din taskbar
        using (Font font = new Font("Segoe UI", 9f * dpi / 72f, GraphicsUnit.Pixel))
        using (StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone())
        {
            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

            float[] widths = new float[segments.Length];
            float total = 0f;
            float textHeight = 0f;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                for (int i = 0; i < segments.Length; i++)
                {
                    SizeF sz = pg.MeasureString(segments[i].Text, font, PointF.Empty, fmt);
                    widths[i] = sz.Width;
                    total += sz.Width;
                    textHeight = Math.Max(textHeight, sz.Height);
                }
            }

            int width = (int)Math.Ceiling(total) + 2 * pad;
            labelWidth = width;

            using (Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    // alpha 1: fundal practic invizibil, dar care primeste click-ul
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    float x = pad;
                    float y = (height - textHeight) / 2f;
                    for (int i = 0; i < segments.Length; i++)
                    {
                        using (SolidBrush brush = new SolidBrush(segments[i].Color))
                        {
                            g.DrawString(segments[i].Text, font, brush, x, y, fmt);
                        }
                        x += widths[i];
                    }
                }

                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr memDc = CreateCompatibleDC(screenDc);
                IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr oldBitmap = SelectObject(memDc, hBitmap);
                try
                {
                    SIZE size = new SIZE { Cx = width, Cy = height };
                    POINT src = new POINT();
                    BLENDFUNCTION blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                    UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
                }
                finally
                {
                    SelectObject(memDc, oldBitmap);
                    DeleteObject(hBitmap);
                    DeleteDC(memDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
        }
    }

    void Layout()
    {
        if (layingOut || labelWidth <= 0 || Handle == IntPtr.Zero) return;
        if (!IsWindow(taskbar) || !IsWindow(rebar) || !IsWindow(taskSw)) return;
        layingOut = true;
        try
        {
            RECT tbr, rb, sw;
            GetWindowRect(taskbar, out tbr);
            GetWindowRect(rebar, out rb);
            GetWindowRect(taskSw, out sw);

            // Doar taskbar orizontal (jos/sus); pe verticala eticheta nu are loc.
            if (tbr.Right - tbr.Left <= tbr.Bottom - tbr.Top)
            {
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOACTIVATE | SWP_HIDEWINDOW);
                return;
            }

            int swLeft = sw.Left - rb.Left;
            int swTop = sw.Top - rb.Top;
            int wantedWidth = Math.Max(0, (rb.Right - rb.Left) - swLeft - labelWidth);
            if (sw.Right - sw.Left != wantedWidth)
            {
                MoveWindow(taskSw, swLeft, swTop, wantedWidth, sw.Bottom - sw.Top, true);
            }

            POINT pt = new POINT { X = rb.Right - labelWidth, Y = tbr.Top };
            ScreenToClient(taskbar, ref pt);
            SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y, labelWidth, tbr.Bottom - tbr.Top, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        finally
        {
            layingOut = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        if (m.Msg == WM_RBUTTONUP && menu != null)
        {
            menu.Show(Cursor.Position);
            return;
        }
        base.WndProc(ref m);
    }

    void Detach()
    {
        if (hook != IntPtr.Zero)
        {
            UnhookWinEvent(hook);
            hook = IntPtr.Zero;
        }
        if (Handle != IntPtr.Zero)
        {
            if (IsWindow(Handle)) DestroyHandle();
            else ReleaseHandle();
        }
        taskbar = IntPtr.Zero;
        rebar = IntPtr.Zero;
        taskSw = IntPtr.Zero;
    }

    public void Dispose()
    {
        // Reda listei de butoane latimea completa.
        if (IsWindow(rebar) && IsWindow(taskSw))
        {
            RECT rb, sw;
            GetWindowRect(rebar, out rb);
            GetWindowRect(taskSw, out sw);
            int swLeft = sw.Left - rb.Left;
            MoveWindow(taskSw, swLeft, sw.Top - rb.Top, (rb.Right - rb.Left) - swLeft, sw.Bottom - sw.Top, true);
        }
        Detach();
    }
}
