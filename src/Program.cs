using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

// iControl: utilitar de tava pentru iMac 27" 2017 (iMac18,3) cu Windows 10 in Boot Camp.
//   - Fan\   controlul ventilatorului prin Apple SMC + afisajul "rpm | °CPU | °GPU" din taskbar
//   - Keys\  F1/F3/F4 volum, F11/F12 luminozitate, cu OSD propriu
// Un singur proces, o singura iconita in tava, un singur jurnal (%LOCALAPPDATA%\iControl).
//
// Argumente:
//   --auto / --reset   readuce ventilatorul pe modul automat SMC si iese
//   --status           scrie o citire SMC in status.txt (si la consola) si iese
//   --exit             cere instantei care ruleaza sa se inchida curat (folosit de deploy.ps1)

static class Program
{
    const string ExitEventName = "iControl_Exit";

    [STAThread]
    static void Main(string[] args)
    {
        AppLog.Init();

        if (args != null && args.Length > 0)
        {
            string arg = args[0].Trim().ToLowerInvariant();
            if (arg == "--reset" || arg == "--auto")
            {
                FanModule.RestoreAutoOnce();
                return;
            }
            if (arg == "--status")
            {
                FanModule.WriteStatusOnce();
                return;
            }
            if (arg == "--exit")
            {
                EventWaitHandle ev;
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out ev))
                {
                    ev.Set();
                    ev.Dispose();
                }
                return;
            }
        }

        bool isNew;
        using (Mutex m = new Mutex(true, "iControl_SingleInstance", out isNew))
        {
            if (!isNew) return;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            NotifyIcon tray = new NotifyIcon();
            tray.Icon = SystemIcons.Shield;
            tray.Text = "iControl: initializare...";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            FanModule.AddMenuItems(menu);
            menu.Items.Add(new ToolStripSeparator());
            KeysModule.AddMenuItems(menu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Deschide jurnalul", null, delegate
            {
                try { Process.Start("notepad.exe", AppLog.LogPath); }
                catch { }
            });
            menu.Items.Add("Iesire (ventilatorul revine pe Automat SMC)", null, delegate { Application.Exit(); });
            tray.ContextMenuStrip = menu;

            AppLog.Write("app", "Pornire iControl");
            FanModule.Start(tray);
            KeysModule.Start();

            // Inchidere curata la cererea "iControl.exe --exit" (redeploy fara kill).
            EventWaitHandle exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
            System.Windows.Forms.Timer exitPoll = new System.Windows.Forms.Timer();
            exitPoll.Interval = 500;
            exitPoll.Tick += delegate
            {
                if (exitEvent.WaitOne(0))
                {
                    AppLog.Write("app", "Inchidere ceruta prin --exit");
                    Application.Exit();
                }
            };
            exitPoll.Start();

            Application.ApplicationExit += delegate
            {
                exitPoll.Stop();
                tray.Visible = false;
                AppLog.Write("app", "Oprire iControl");
            };

            Application.Run();
        }
    }
}

// Jurnal comun pentru toate modulele, cu rotatie simpla la 1 MB.
static class AppLog
{
    const long MaxBytes = 1024 * 1024;
    static readonly object sync = new object();

    public static string Dir;
    public static string LogPath;

    public static void Init()
    {
        Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iControl");
        Directory.CreateDirectory(Dir);
        LogPath = Path.Combine(Dir, "log.txt");
    }

    public static void Write(string area, string line)
    {
        lock (sync)
        {
            try
            {
                FileInfo fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = Path.Combine(Dir, "log.old.txt");
                    File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + area + "] " + line + Environment.NewLine);
            }
            catch { }
        }
    }
}
