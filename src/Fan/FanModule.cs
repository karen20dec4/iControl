using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// Control ventilator iMac 27" 2017 (iMac18,3) in Windows 10 prin Apple SMC (\\.\MacHALDriver).
//
// Curba configurata:
//   - Idle (CPU <= 60 C si GPU <= 65 C):
//       F0Mn = 800 RPM, FS! = 1 (manual), F0Tg = 900 RPM (turatie fizica ~940-950 RPM).
//   - Sarcina (CPU > 60 C sau GPU > 65 C):
//       Rampa liniara progresiva de la 940 RPM pana la 2700 RPM
//       (CPU: 60 C -> 80 C, GPU: 65 C -> 82 C), cu medie mobila pe ~12 secunde (12 citiri x 1s).
//   - Fail-safe:
//       La oprire / shutdown / restart / sleep / eroare senzori, readuce SMC pe modul
//       automat din fabrica (FS! = 0, F0Mn = 1200 RPM).
//   - Afisaj in taskbar, in stanga zonei de notificare: "950rpm | 52°CPU | 48°GPU"
//       (temperaturi galbene >= 75 C, rosii >= 85 C; click dreapta = meniul aplicatiei).

sealed class ShutdownWindow : NativeWindow
{
    const int WM_QUERYENDSESSION = 0x0011;
    const int WM_ENDSESSION = 0x0016;

    readonly Action onShutdown;

    public ShutdownWindow(Action onShutdownAction)
    {
        onShutdown = onShutdownAction;
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_QUERYENDSESSION || m.Msg == WM_ENDSESSION)
        {
            if (onShutdown != null) onShutdown();
        }
        base.WndProc(ref m);
    }
}

static class FanModule
{
    const int PollIntervalMs = 1000;
    const int MovingAvgWindow = 12; // 12 x 1s = 12 secunde medie mobila (fara fluctuatii la varfuri Turbo Boost)

    const int FactoryMinRpm = 1200;
    const int CustomMinRpm = 800;
    const int IdleTargetRpm = 900;
    const int RampStartRpm = 940;
    const int MaxRpm = 2700;

    const double CpuIdleMaxC = 60.0;
    const double CpuFullLoadC = 80.0;

    const double GpuIdleMaxC = 65.0;
    const double GpuFullLoadC = 82.0;

    const double LabelWarnC = 75.0;
    const double LabelHotC = 85.0;

    static readonly string[] CpuTempKeys = new string[] { "TC0c", "TC1c", "TC2c", "TC3c", "TCXc", "TC0P" };
    static readonly string[] GpuTempKeys = new string[] { "TG0D", "TG0E", "TG0F", "TG0P" };

    static SmcDevice smc;
    static string statusPath;

    static volatile bool running = true;
    static volatile bool manualCurveEnabled = true;
    static volatile bool suspended = false;
    static int failSafeRestored = 0;

    static NotifyIcon tray;
    static ToolStripMenuItem statusItem;
    static ToolStripMenuItem curveModeItem;
    static ToolStripMenuItem autoModeItem;
    static ShutdownWindow shutdownWin;
    static TaskbarLabel taskbarLabel;
    static System.Windows.Forms.Timer labelTimer;

    // Ultimele citiri instantanee, afisate in taskbar (NaN = senzor indisponibil).
    static readonly object readingsLock = new object();
    static double lastRpm = double.NaN;
    static double lastCpu = double.NaN;
    static double lastGpu = double.NaN;

    static readonly Queue<double> cpuSamples = new Queue<double>();
    static readonly Queue<double> gpuSamples = new Queue<double>();
    static int lastAppliedTarget = -1;
    static int consecutiveSensorErrors = 0;

    // Comanda "--auto": readuce SMC pe modul automat si iese.
    public static void RestoreAutoOnce()
    {
        Init();
        RestoreSmcAuto("comanda --auto");
        smc.Dispose();
    }

    // Comanda "--status": o singura citire SMC, in status.txt si la consola.
    public static void WriteStatusOnce()
    {
        Init();
        WriteStatusSnapshot();
        smc.Dispose();
    }

    static void Init()
    {
        statusPath = Path.Combine(AppLog.Dir, "status.txt");
        smc = new SmcDevice();
    }

    public static void AddMenuItems(ContextMenuStrip menu)
    {
        statusItem = new ToolStripMenuItem("Ventilator: initializare...");
        statusItem.Enabled = false;
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());

        curveModeItem = new ToolStripMenuItem("Curba silentioasa (~940 - 2700 RPM)");
        curveModeItem.Checked = true;
        curveModeItem.Click += delegate { SetMode(true); };
        menu.Items.Add(curveModeItem);

        autoModeItem = new ToolStripMenuItem("Automat Apple SMC (1200 - 2700 RPM)");
        autoModeItem.Checked = false;
        autoModeItem.Click += delegate { SetMode(false); };
        menu.Items.Add(autoModeItem);
    }

    public static void Start(NotifyIcon trayIcon)
    {
        Init();
        tray = trayIcon;

        AppDomain.CurrentDomain.ProcessExit += delegate { RestoreSmcAuto("ProcessExit"); };
        AppDomain.CurrentDomain.UnhandledException += delegate { RestoreSmcAuto("UnhandledException"); };
        SystemEvents.SessionEnding += delegate { RestoreSmcAuto("SessionEnding"); };
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        shutdownWin = new ShutdownWindow(delegate { RestoreSmcAuto("WM_ENDSESSION"); });

        taskbarLabel = new TaskbarLabel(tray.ContextMenuStrip);
        labelTimer = new System.Windows.Forms.Timer();
        labelTimer.Interval = PollIntervalMs;
        labelTimer.Tick += delegate
        {
            try { taskbarLabel.SetText(BuildLabel()); }
            catch (Exception ex) { Log("Eroare afisaj taskbar: " + ex.Message); }
        };
        labelTimer.Start();

        Log("Pornire control ventilator: curba idle 900 RPM (~940 real), rampa 60-80C CPU / 65-82C GPU -> 2700 RPM (medie 12s)");

        Thread worker = new Thread(ControlLoop);
        worker.IsBackground = true;
        worker.Start();

        Application.ApplicationExit += delegate
        {
            running = false;
            RestoreSmcAuto("ApplicationExit");
            labelTimer.Stop();
            try { taskbarLabel.Dispose(); } catch { }
            smc.Dispose();
        };
    }

    static void SetMode(bool useCustomCurve)
    {
        manualCurveEnabled = useCustomCurve;
        lastAppliedTarget = -1;
        Interlocked.Exchange(ref failSafeRestored, 0);
        if (curveModeItem != null) curveModeItem.Checked = useCustomCurve;
        if (autoModeItem != null) autoModeItem.Checked = !useCustomCurve;

        if (!useCustomCurve)
        {
            RestoreSmcAuto("Comutare manuala pe Automat SMC");
        }
        else
        {
            Log("Comutare pe curba silentioasa (~940-2700 RPM)");
        }
    }

    static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            suspended = true;
            RestoreSmcAuto("Suspend/Sleep");
        }
        else if (e.Mode == PowerModes.Resume)
        {
            lock (cpuSamples)
            {
                cpuSamples.Clear();
                gpuSamples.Clear();
            }
            lastAppliedTarget = -1;
            Interlocked.Exchange(ref failSafeRestored, 0);
            suspended = false;
            Log("Resume din Sleep: reluare control ventilator");
        }
    }

    static void ControlLoop()
    {
        while (running)
        {
            try
            {
                if (!suspended)
                {
                    StepControl();
                }
            }
            catch (Exception ex)
            {
                Log("Eroare in bucla de control: " + ex.Message);
            }

            Thread.Sleep(PollIntervalMs);
        }
    }

    static void StepControl()
    {
        double cpuMax = ReadMaxTemp(CpuTempKeys);
        double gpuMax = ReadMaxTemp(GpuTempKeys);
        double? actualRpm = smc.ReadFpe2("F0Ac");

        lock (readingsLock)
        {
            lastRpm = actualRpm ?? double.NaN;
            lastCpu = cpuMax;
            lastGpu = gpuMax;
        }

        if (double.IsNaN(cpuMax) || double.IsNaN(gpuMax))
        {
            consecutiveSensorErrors++;
            if (consecutiveSensorErrors >= 3)
            {
                RestoreSmcAuto("Senzori indisponibili (fail-safe)");
            }
            return;
        }
        consecutiveSensorErrors = 0;

        double avgCpu;
        double avgGpu;
        lock (cpuSamples)
        {
            EnqueueSample(cpuSamples, cpuMax);
            EnqueueSample(gpuSamples, gpuMax);
            avgCpu = Average(cpuSamples);
            avgGpu = Average(gpuSamples);
        }

        if (!manualCurveEnabled)
        {
            UpdateUi(actualRpm ?? 0, FactoryMinRpm, avgCpu, avgGpu, "Automat SMC");
            return;
        }

        int targetRpm = ComputeTargetRpm(avgCpu, avgGpu);

        int? curMin = null;
        double? curMinFpe = smc.ReadFpe2("F0Mn");
        if (curMinFpe.HasValue) curMin = (int)Math.Round(curMinFpe.Value);

        int? curManual = smc.ReadUi16("FS! ");
        double? curTargetFpe = smc.ReadFpe2("F0Tg");
        int curTarget = curTargetFpe.HasValue ? (int)Math.Round(curTargetFpe.Value) : -1;

        if (!curMin.HasValue || curMin.Value != CustomMinRpm)
        {
            smc.WriteFpe2("F0Mn", CustomMinRpm);
        }

        if (!curManual.HasValue || curManual.Value != 1)
        {
            smc.WriteUi16("FS! ", 1);
        }

        bool needWriteTarget = false;
        if (lastAppliedTarget < 0 || Math.Abs(curTarget - targetRpm) >= 15)
        {
            needWriteTarget = true;
        }
        else if ((targetRpm == IdleTargetRpm || targetRpm == MaxRpm) && curTarget != targetRpm)
        {
            needWriteTarget = true;
        }

        if (needWriteTarget)
        {
            if (smc.WriteFpe2("F0Tg", targetRpm))
            {
                if (lastAppliedTarget < 0 || Math.Abs(lastAppliedTarget - targetRpm) >= 50 ||
                    (targetRpm == IdleTargetRpm && lastAppliedTarget != IdleTargetRpm))
                {
                    Log(string.Format("Tinta F0Tg={0} RPM | Real={1:F0} RPM | CPU={2:F1}C | GPU={3:F1}C",
                        targetRpm, actualRpm ?? 0, avgCpu, avgGpu));
                }
                lastAppliedTarget = targetRpm;
            }
        }

        string modeLabel = targetRpm <= IdleTargetRpm ? "Idle silentios" : "Rampa activa";
        UpdateUi(actualRpm ?? 0, targetRpm, avgCpu, avgGpu, modeLabel);
    }

    static int ComputeTargetRpm(double cpuC, double gpuC)
    {
        if (cpuC <= CpuIdleMaxC && gpuC <= GpuIdleMaxC)
        {
            return IdleTargetRpm;
        }

        double cpuRatio = 0.0;
        if (cpuC > CpuIdleMaxC)
        {
            cpuRatio = (cpuC - CpuIdleMaxC) / (CpuFullLoadC - CpuIdleMaxC);
        }

        double gpuRatio = 0.0;
        if (gpuC > GpuIdleMaxC)
        {
            gpuRatio = (gpuC - GpuIdleMaxC) / (GpuFullLoadC - GpuIdleMaxC);
        }

        double ratio = Math.Max(cpuRatio, gpuRatio);
        if (ratio < 0.0) ratio = 0.0;
        if (ratio > 1.0) ratio = 1.0;

        int rpm = (int)Math.Round(RampStartRpm + ratio * (MaxRpm - RampStartRpm));
        if (rpm < RampStartRpm) rpm = RampStartRpm;
        if (rpm > MaxRpm) rpm = MaxRpm;
        return rpm;
    }

    static double ReadMaxTemp(string[] keys)
    {
        double max = double.NaN;
        for (int i = 0; i < keys.Length; i++)
        {
            double? t = smc.ReadSp78(keys[i]);
            if (t.HasValue && t.Value > 5.0 && t.Value < 120.0)
            {
                if (double.IsNaN(max) || t.Value > max)
                {
                    max = t.Value;
                }
            }
        }
        return max;
    }

    static void EnqueueSample(Queue<double> q, double val)
    {
        if (q.Count == 0)
        {
            double seed = Math.Min(val, 50.0);
            for (int i = 0; i < MovingAvgWindow - 1; i++)
            {
                q.Enqueue(seed);
            }
        }
        q.Enqueue(val);
        while (q.Count > MovingAvgWindow)
        {
            q.Dequeue();
        }
    }

    static double Average(Queue<double> q)
    {
        if (q.Count == 0) return 0.0;
        double sum = 0.0;
        foreach (double v in q) sum += v;
        return sum / q.Count;
    }

    static void UpdateUi(double actualRpm, int targetRpm, double cpuC, double gpuC, string mode)
    {
        string tip = string.Format("iMac Fan: {0:F0} RPM (tinta {1})\nCPU: {2:F1} °C | GPU: {3:F1} °C\n{4}",
            actualRpm, targetRpm, cpuC, gpuC, mode);
        if (tip.Length > 63)
        {
            tip = string.Format("Fan:{0:F0} RPM | CPU:{1:F0}C GPU:{2:F0}C ({3})",
                actualRpm, cpuC, gpuC, mode);
            if (tip.Length > 63) tip = tip.Substring(0, 63);
        }

        string menuLine = string.Format("Ventilator: {0:F0} RPM (tinta {1}) | CPU {2:F1}°C | GPU {3:F1}°C",
            actualRpm, targetRpm, cpuC, gpuC);
        string statusLine = string.Format("Ventilator: {0:F0} RPM (tinta {1}) | CPU {2:F1} C | GPU {3:F1} C",
            actualRpm, targetRpm, cpuC, gpuC);

        try
        {
            if (tray != null) tray.Text = tip;
            if (statusItem != null && tray.ContextMenuStrip != null && tray.ContextMenuStrip.IsHandleCreated)
            {
                tray.ContextMenuStrip.BeginInvoke((MethodInvoker)delegate
                {
                    statusItem.Text = menuLine;
                });
            }
            else if (statusItem != null)
            {
                statusItem.Text = menuLine;
            }

            File.WriteAllText(statusPath, statusLine + Environment.NewLine + "Mod: " + mode + Environment.NewLine);
        }
        catch { }
    }

    static TaskbarLabel.Segment[] BuildLabel()
    {
        double rpm, cpu, gpu;
        lock (readingsLock)
        {
            rpm = lastRpm;
            cpu = lastCpu;
            gpu = lastGpu;
        }

        Color text = SystemUsesLightTheme() ? Color.Black : Color.White;
        string sep = " | ";
        return new TaskbarLabel.Segment[]
        {
            new TaskbarLabel.Segment((double.IsNaN(rpm) ? "--" : rpm.ToString("F0")) + "rpm", text),
            new TaskbarLabel.Segment(sep, text),
            new TaskbarLabel.Segment(FormatTemp(cpu) + "°CPU", TempColor(cpu, text)),
            new TaskbarLabel.Segment(sep, text),
            new TaskbarLabel.Segment(FormatTemp(gpu) + "°GPU", TempColor(gpu, text))
        };
    }

    static string FormatTemp(double c)
    {
        return double.IsNaN(c) ? "--" : Math.Round(c).ToString("F0");
    }

    static Color TempColor(double c, Color normal)
    {
        if (double.IsNaN(c)) return normal;
        if (c >= LabelHotC) return Color.FromArgb(255, 90, 90);
        if (c >= LabelWarnC) return Color.FromArgb(255, 200, 0);
        return normal;
    }

    static bool SystemUsesLightTheme()
    {
        try
        {
            object v = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 0);
            return v is int && (int)v == 1;
        }
        catch { return false; }
    }

    static void WriteStatusSnapshot()
    {
        double cpuMax = ReadMaxTemp(CpuTempKeys);
        double gpuMax = ReadMaxTemp(GpuTempKeys);
        double? f0Ac = smc.ReadFpe2("F0Ac");
        double? f0Mn = smc.ReadFpe2("F0Mn");
        double? f0Mx = smc.ReadFpe2("F0Mx");
        double? f0Tg = smc.ReadFpe2("F0Tg");
        int? fs = smc.ReadUi16("FS! ");

        string info = string.Format(
            "F0Ac={0:F0} RPM | F0Tg={1:F0} RPM | F0Mn={2:F0} RPM | F0Mx={3:F0} RPM | FS!={4} | CPU={5:F1}C | GPU={6:F1}C",
            f0Ac ?? -1, f0Tg ?? -1, f0Mn ?? -1, f0Mx ?? -1, fs ?? -1, cpuMax, gpuMax);
        try { File.WriteAllText(statusPath, info + Environment.NewLine); } catch { }
        try { Console.WriteLine(info); } catch { }
    }

    static void RestoreSmcAuto(string reason)
    {
        if (Interlocked.Exchange(ref failSafeRestored, 1) != 0 && reason != "Comutare manuala pe Automat SMC")
        {
            return;
        }
        try
        {
            smc.WriteFpe2("F0Mn", FactoryMinRpm);
            smc.WriteFpe2("F0Tg", FactoryMinRpm);
            smc.WriteUi16("FS! ", 0);
            Log("Fail-safe aplicat (FS!=0, F0Mn=1200): " + reason);
        }
        catch (Exception ex)
        {
            Log("Eroare la fail-safe: " + ex.Message);
        }
    }

    static void Log(string line)
    {
        AppLog.Write("fan", line);
    }
}
