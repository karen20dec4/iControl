using System;
using System.Collections.Generic;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// Media and brightness keys for an iMac running Windows under Boot Camp.
//   F11 / F12  brightness down / up   (internal panel, via WMI)
//   F3  / F4   volume down / up       (via the Core Audio endpoint)
//   F1         mute toggle
// Shift plus any of them is a different combination, so it is never captured.
//
// The keys are claimed with RegisterHotKey rather than a WH_KEYBOARD_LL hook.
// A low-level hook runs on the UI thread and Windows drops - or silently
// removes - a hook whose callback overruns LowLevelHooksTimeout, which showed
// up as presses that were seen but never acted on. WM_HOTKEY is queued by the
// OS instead, so nothing is lost when the process is briefly busy.
//
// F12 is the exception: the kernel keeps it reserved for the debugger, so
// RegisterHotKey always refuses it. Any key that cannot be registered falls
// back to a low-level hook whose callback only bumps a counter and signals the
// worker - no file or WMI work on that thread, which is what made the old
// all-hook build lose presses.
//
// Volume goes through Core Audio rather than injected VK_VOLUME_* keys so that
// Windows does not raise its own flyout: both overlays are then drawn here and
// share a single timeout.
//
// Brightness is meant to stay where the user puts it. Boot Camp's tray helper
// (Bootcamp.exe) drives the panel from the ambient light sensor and overrode
// every manual change within seconds, so it is stopped here and the chosen
// level is saved and restored.

static class KeysModule
{
    const int BrightnessStep = 5;
    const float VolumeStep = 0.04f; // 4% per press, close to the macOS feel

    const int IdMute = 1;
    const int IdVolumeDown = 2;
    const int IdVolumeUp = 3;
    const int IdBrightnessDown = 4;
    const int IdBrightnessUp = 5;

    static HotkeyWindow keys;
    static readonly List<uint> hookedKeys = new List<uint>();
    const int SourceHotkey = 1;
    const int SourceHook = 2;
    static volatile int lastSource;

    // A driver or another tool can flood the keys; without a brake the panel
    // ping-pongs across its whole range. Bursts are reported once and dropped.
    const int BurstLimit = 25;
    const int BurstWindowMs = 2000;
    const int BurstPauseMs = 3000;
    static int burstCount;
    static int userActed;
    static int burstStartedAt;
    static int suppressedUntil;
    static IntPtr hookHandle = IntPtr.Zero;
    static KeysNative.LowLevelKeyboardProc hookProc; // kept alive so the GC cannot collect it
    static Osd osd;

    static int pendingBrightness;
    static int pendingVolume;
    static int pendingMute;
    static int pendingRestore;
    static readonly AutoResetEvent work = new AutoResetEvent(false);
    static volatile bool running = true;

    static int currentLevel = -1;
    static byte[] supported;
    static int minLevel = 1;
    static int maxLevel = 100;

    static string levelPath;

    public static void AddMenuItems(ContextMenuStrip menu)
    {
        ToolStripMenuItem brightness = new ToolStripMenuItem("Luminozitate (F11 / F12)");
        brightness.DropDownItems.Add("Test: scade luminozitatea", null, delegate { QueueBrightness(-1); });
        brightness.DropDownItems.Add("Test: creste luminozitatea", null, delegate { QueueBrightness(1); });
        brightness.DropDownItems.Add("Reaplica luminozitatea salvata", null, delegate { QueueRestore(); });
        menu.Items.Add(brightness);

        ToolStripMenuItem volume = new ToolStripMenuItem("Volum (F1 / F3 / F4)");
        volume.DropDownItems.Add("Test: scade volumul", null, delegate { QueueVolume(-1); });
        volume.DropDownItems.Add("Test: creste volumul", null, delegate { QueueVolume(1); });
        volume.DropDownItems.Add("Test: mut / demut", null, delegate { QueueMute(); });
        menu.Items.Add(volume);
    }

    public static void Start()
    {
        levelPath = Path.Combine(AppLog.Dir, "level.txt");

        osd = new Osd();
        osd.CreateControl();
        IntPtr unused = osd.Handle; // realise the handle so BeginInvoke works

        try
        {
            ReadHardware();
            Log("start: luminozitate=" + currentLevel + " min=" + minLevel + " max=" + maxLevel);
        }
        catch (Exception ex)
        {
            Log("nu pot citi luminozitatea: " + ex.Message);
        }

        Thread worker = new Thread(WorkerLoop);
        worker.IsBackground = true;
        worker.Start();

        StopAppleAutoBrightness();

        int settleTicks = 0;
        System.Windows.Forms.Timer settle = new System.Windows.Forms.Timer();
        settle.Interval = 3000;
        settle.Tick += delegate
        {
            settleTicks++;
            if (userActed != 0 || settleTicks > 8)
            {
                settle.Stop();
                return;
            }
            if (settleTicks == 1 || settleTicks == 3 || settleTicks == 7) QueueRestore();
        };
        settle.Start();
        System.Windows.Forms.Timer guard = new System.Windows.Forms.Timer();
        guard.Interval = 20000;
        guard.Tick += delegate
        {
            if (StopAppleAutoBrightness()) QueueRestore();
        };
        guard.Start();

        keys = new HotkeyWindow();
        keys.Pressed += OnHotkey;
        Claim(IdMute, KeysNative.VK_F1, "F1 mut");
        Claim(IdVolumeDown, KeysNative.VK_F3, "F3 volum -");
        Claim(IdVolumeUp, KeysNative.VK_F4, "F4 volum +");
        Claim(IdBrightnessDown, KeysNative.VK_F11, "F11 luminozitate -");
        Claim(IdBrightnessUp, KeysNative.VK_F12, "F12 luminozitate +");
        InstallFallbackHook();

        Application.ApplicationExit += delegate
        {
            running = false;
            work.Set();
            for (int id = IdMute; id <= IdBrightnessUp; id++)
                KeysNative.UnregisterHotKey(keys.Handle, id);
            if (hookHandle != IntPtr.Zero) KeysNative.UnhookWindowsHookEx(hookHandle);
        };
    }

    static void Claim(int id, uint vk, string name)
    {
        if (KeysNative.RegisterHotKey(keys.Handle, id, KeysNative.MOD_NONE, vk))
        {
            Log("tasta inregistrata: " + name);
            return;
        }

        int err = Marshal.GetLastWin32Error();
        string why = err == 1409
            ? "rezervata de sistem sau de alt program"
            : "cod de eroare " + err;
        hookedKeys.Add(vk);
        Log(name + ": RegisterHotKey refuzat (" + why + "), trec pe hook");
    }

    static void InstallFallbackHook()
    {
        if (hookedKeys.Count == 0) return;

        hookProc = HookCallback;
        hookHandle = KeysNative.SetWindowsHookEx(KeysNative.WH_KEYBOARD_LL, hookProc, KeysNative.GetModuleHandle(null), 0);
        Log(hookHandle == IntPtr.Zero
            ? "hook de rezerva a esuat, cod " + Marshal.GetLastWin32Error()
            : "hook de rezerva activ pentru " + hookedKeys.Count + " tasta(e)");
    }

    // Deliberately minimal: a counter, an event, and nothing that can block.
    static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            bool down = msg == KeysNative.WM_KEYDOWN || msg == KeysNative.WM_SYSKEYDOWN;
            bool up = msg == KeysNative.WM_KEYUP || msg == KeysNative.WM_SYSKEYUP;

            if (down || up)
            {
                KeysNative.KBDLLHOOKSTRUCT info = (KeysNative.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KeysNative.KBDLLHOOKSTRUCT));
                if (down
                    && hookedKeys.Contains(info.vkCode)
                    && (KeysNative.GetAsyncKeyState(KeysNative.VK_SHIFT) & 0x8000) == 0)
                {
                    lastSource = SourceHook;
                    Dispatch(info.vkCode);
                    return new IntPtr(1); // swallow the press, let the release pass
                }
            }
        }
        return KeysNative.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    static void Dispatch(uint vk)
    {
        if (vk == KeysNative.VK_F1) QueueMute();
        else if (vk == KeysNative.VK_F3) QueueVolume(-1);
        else if (vk == KeysNative.VK_F4) QueueVolume(1);
        else if (vk == KeysNative.VK_F11) QueueBrightness(-1);
        else if (vk == KeysNative.VK_F12) QueueBrightness(1);
    }

    static void OnHotkey(int id)
    {
        lastSource = SourceHotkey;
        switch (id)
        {
            case IdMute: QueueMute(); break;
            case IdVolumeDown: QueueVolume(-1); break;
            case IdVolumeUp: QueueVolume(1); break;
            case IdBrightnessDown: QueueBrightness(-1); break;
            case IdBrightnessUp: QueueBrightness(1); break;
        }
    }

    static void QueueBrightness(int direction)
    {
        Interlocked.Exchange(ref userActed, 1);
        Interlocked.Add(ref pendingBrightness, direction);
        work.Set();
    }

    static void QueueVolume(int direction)
    {
        Interlocked.Add(ref pendingVolume, direction);
        work.Set();
    }

    static void QueueMute()
    {
        Interlocked.Increment(ref pendingMute);
        work.Set();
    }

    static void QueueRestore()
    {
        Interlocked.Exchange(ref pendingRestore, 1);
        work.Set();
    }

    // Boot Camp's tray helper hunts the backlight from the light sensor, which
    // undoes any manual setting. Nothing but logon starts it again, so a slow
    // guard tick is enough to catch the one that logon brings up.
    static bool StopAppleAutoBrightness()
    {
        bool stopped = false;
        try
        {
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("Bootcamp"))
            {
                try
                {
                    p.Kill();
                    stopped = true;
                    Log("am oprit Bootcamp.exe (luminozitate automata)");
                }
                catch (Exception ex)
                {
                    Log("nu pot opri Bootcamp.exe: " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log("nu pot inspecta procesele: " + ex.Message);
        }
        return stopped;
    }

    static void WorkerLoop()
    {
        RestoreSavedLevel();

        while (running)
        {
            work.WaitOne();
            if (!running) break;

            if (Interlocked.Exchange(ref pendingRestore, 0) != 0) RestoreSavedLevel();

            if (Interlocked.Exchange(ref pendingMute, 0) != 0) ApplyMute();

            int vol = Interlocked.Exchange(ref pendingVolume, 0);
            if (vol != 0) ApplyVolume(vol);

            int delta = Interlocked.Exchange(ref pendingBrightness, 0);
            if (delta != 0) ApplyBrightness(delta);
        }
    }

    // One toggle per burst, so holding F1 down does not flutter the mute state.
    static void ApplyMute()
    {
        try
        {
            int percent;
            bool muted = Audio.ToggleMute(out percent);
            Log("mut = " + muted);
            ShowOsd(muted ? "Sunet mut" : "Volum", muted ? -1 : percent);
        }
        catch (Exception ex)
        {
            Log("eroare la mut: " + ex.Message);
        }
    }

    static void ApplyVolume(int delta)
    {
        try
        {
            int percent = Audio.Nudge(delta * VolumeStep);
            Log("volum = " + percent + "%");
            ShowOsd("Volum", percent);
        }
        catch (Exception ex)
        {
            Log("eroare la volum: " + ex.Message);
        }
    }

    // The panel is read back on every change: adaptive brightness and the Boot
    // Camp service can both move it behind our back, and a stale cached value
    // would make the next step jump.
    static void ApplyBrightness(int delta)
    {
        if (IsFlooding()) return;

        try
        {
            ReadHardware();
            int target = Snap(currentLevel + delta * BrightnessStep);
            SetBrightness((byte)target);
            currentLevel = target;

            int pct = maxLevel > minLevel
                ? (int)Math.Round(100.0 * (currentLevel - minLevel) / (maxLevel - minLevel))
                : currentLevel;
            Log("luminozitate = " + currentLevel + " (" + pct + "%)"
                + (lastSource == SourceHook ? " [hook]" : " [hotkey]"));
            SaveLevel(currentLevel);
            ShowOsd("Luminozitate", pct);
        }
        catch (Exception ex)
        {
            Log("eroare la setarea luminozitatii: " + ex.Message);
            currentLevel = -1;
        }
    }

    static void SaveLevel(int level)
    {
        try { File.WriteAllText(levelPath, level.ToString()); }
        catch (Exception ex) { Log("nu pot salva nivelul: " + ex.Message); }
    }

    // Applied at start-up and whenever the guard has to stop Boot Camp again,
    // so the panel returns to the level the user chose instead of the sensor's.
    static void RestoreSavedLevel()
    {
        try
        {
            if (!File.Exists(levelPath)) return;
            int wanted;
            if (!int.TryParse(File.ReadAllText(levelPath).Trim(), out wanted)) return;

            ReadHardware();
            wanted = Snap(wanted);
            if (wanted == currentLevel) return;

            SetBrightness((byte)wanted);
            currentLevel = wanted;
            Log("luminozitate restaurata la " + wanted);
        }
        catch (Exception ex)
        {
            Log("nu pot restaura luminozitatea: " + ex.Message);
        }
    }

    static bool IsFlooding()
    {
        int now = Environment.TickCount;

        if (suppressedUntil != 0)
        {
            if (now - suppressedUntil < 0) return true;
            suppressedUntil = 0;
            burstCount = 0;
            burstStartedAt = now;
        }

        if (burstStartedAt == 0 || now - burstStartedAt > BurstWindowMs)
        {
            burstStartedAt = now;
            burstCount = 0;
        }

        if (++burstCount <= BurstLimit) return false;

        suppressedUntil = now + BurstPauseMs;
        Log("flux anormal de taste (" + burstCount + " in " + BurstWindowMs + " ms, sursa "
            + (lastSource == SourceHook ? "hook" : "hotkey") + "), ignor " + BurstPauseMs + " ms");
        return true;
    }

    static int Snap(int value)
    {
        if (value < minLevel) value = minLevel;
        if (value > maxLevel) value = maxLevel;
        if (supported == null || supported.Length == 0) return value;

        int best = supported[0];
        int bestDist = Math.Abs(best - value);
        for (int i = 1; i < supported.Length; i++)
        {
            int d = Math.Abs(supported[i] - value);
            if (d < bestDist) { best = supported[i]; bestDist = d; }
        }
        return best;
    }

    static void ShowOsd(string caption, int pct)
    {
        try
        {
            if (osd.IsHandleCreated)
                osd.BeginInvoke((MethodInvoker)delegate { osd.ShowLevel(caption, pct); });
        }
        catch { }
    }

    static void ReadHardware()
    {
        ManagementScope scope = new ManagementScope("root\\wmi");
        using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightness")))
        {
            foreach (ManagementObject o in s.Get())
            {
                currentLevel = Convert.ToInt32(o["CurrentBrightness"]);
                byte[] levels = o["Level"] as byte[];
                if (levels != null && levels.Length > 0)
                {
                    supported = levels;
                    minLevel = levels[0];
                    maxLevel = levels[0];
                    foreach (byte b in levels)
                    {
                        if (b < minLevel) minLevel = b;
                        if (b > maxLevel) maxLevel = b;
                    }
                }
                break;
            }
        }
        if (currentLevel < 0) throw new InvalidOperationException("WmiMonitorBrightness nu a returnat nimic");
    }

    static void SetBrightness(byte level)
    {
        ManagementScope scope = new ManagementScope("root\\wmi");
        using (ManagementObjectSearcher s = new ManagementObjectSearcher(scope, new SelectQuery("WmiMonitorBrightnessMethods")))
        {
            foreach (ManagementObject o in s.Get())
            {
                o.InvokeMethod("WmiSetBrightness", new object[] { (uint)0, level });
                return;
            }
        }
        throw new InvalidOperationException("WmiMonitorBrightnessMethods nu este disponibil");
    }

    static void Log(string line)
    {
        AppLog.Write("keys", line);
    }
}
