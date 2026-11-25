using System.Diagnostics;
using System.Windows;
using FilipBoard.App.Services;
using FilipBoard.App.ViewModels;
using FilipBoard.Core;
using FilipBoard.Data;
using FilipBoard.Hardware;

namespace FilipBoard.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private SqliteStore? _store;
    private FanControlService? _service;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

    public static FanControlService Service { get; private set; } = null!;
    public static SqliteStore Store { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var waitPrev = e.Args.Contains("--wait-prev");
        _mutex = new Mutex(true, @"Local\FilipBoardFanControl.Single", out var created);
        if (!created && !(waitPrev && _mutex.WaitOne(TimeSpan.FromSeconds(8))))
        {
            MessageBox.Show("Filip Board Fan Control is already running (check the system tray).", "Filip Board Fan Control");
            Shutdown(); return;
        }

        DispatcherUnhandledException += (_, ex) => { EmergencyRestore("UI error: " + ex.Exception.Message); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => EmergencyRestore("Unhandled: " + ex.ExceptionObject);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyRestore("process exit", quiet: true);

        try
        {
            _store = new SqliteStore(SqliteStore.DefaultPath());
            var settings = _store.LoadSettings();

            ISensorProvider provider; IFanBackend? backend;
            if (settings.DemoMode || e.Args.Contains("--demo")) { var m = new MockHardware(); provider = m; backend = m; }
            else
            {
                var lhm = new LhmSensorProvider();
                provider = lhm;
                var b = new LhmFanBackend(lhm);
                backend = b.Channels.Count > 0 ? b : null;
            }

            _service = new FanControlService(provider, backend, _store, settings);
            Service = _service; Store = _store;
            _service.HealthEventRaised += OnHealth;
            _service.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to start:\n" + ex.Message, "Filip Board Fan Control", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }

        var vm = new MainViewModel(_service, _store, RestartAsAdmin, ExitApp);
        var pageArg = e.Args.FirstOrDefault(a => a.StartsWith("--page="));
        if (pageArg != null && int.TryParse(pageArg[7..], out var pi) && pi >= 0 && pi < vm.Pages.Count) vm.SelectedPage = vm.Pages[pi];
        _window = new MainWindow { DataContext = vm };
        _window.Closing += (_, ce) =>
        {
            if (!_exiting && _service!.Settings.MinimizeToTray) { ce.Cancel = true; _window.Hide(); }
        };
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized && _service!.Settings.MinimizeToTray) _window.Hide();
        };
        SetupTray();
        if (_service.Settings.StartMinimized && _service.Settings.MinimizeToTray) { /* stay in tray */ }
        else _window.Show();
    }

    private void SetupTray()
    {
        using var bmp = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 59, 78)), 1, 1, 30, 30);
            g.FillEllipse(new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(11, 11, 14)), 12, 12, 8, 8);
        }
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowWindow());
        menu.Items.Add("Exit (restore hardware fan control)", null, (_, _) => ExitApp());
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.FromHandle(bmp.GetHicon()),
            Text = "Filip Board Fan Control",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowWindow();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show(); _window.WindowState = WindowState.Normal; _window.Activate();
    }

    private void OnHealth(HealthEvent e)
    {
        if (e.Level == AlertLevel.Normal) return;
        Dispatcher.BeginInvoke(() => _tray?.ShowBalloonTip(5000, "Filip Board Fan Control", e.Message,
            e.Level == AlertLevel.Critical ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Warning));
    }

    private void EmergencyRestore(string reason, bool quiet = false)
    {
        try { _service?.RestoreHardwareControl(reason); } catch { }
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        try { _service?.Dispose(); } catch { }
        try { _store?.Dispose(); } catch { }
        Shutdown();
    }

    public void RestartAsAdmin()
    {
        var exe = Environment.ProcessPath!;
        try
        {
            Process.Start(new ProcessStartInfo(exe, "--wait-prev") { UseShellExecute = true, Verb = "runas" });
        }
        catch { return; } // user declined UAC
        ExitApp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_exiting) { try { _service?.Dispose(); } catch { } }
        try { _mutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
