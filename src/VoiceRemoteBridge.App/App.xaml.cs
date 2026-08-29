using System.Drawing;
using System.Windows;
using Microsoft.Win32;
using VoiceRemoteBridge.Windows;
using Forms = System.Windows.Forms;

namespace VoiceRemoteBridge.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? singleInstance;
    private Forms.NotifyIcon? trayIcon;
    private MainWindow? mainWindow;
    private bool exiting;
    private volatile bool systemEventsSubscribed;

    protected override void OnStartup(StartupEventArgs eventArgs)
    {
        StartupDiagnostics.Record("App.OnStartup entered");
        base.OnStartup(eventArgs);
        StartupDiagnostics.Record("App.OnStartup base completed");
        if (eventArgs.Args.Contains("--ui-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            RunUiSmokeTest();
            return;
        }

        bool startInBackground = eventArgs.Args.Contains(
            StartupCommandBuilder.BackgroundArgument,
            StringComparer.OrdinalIgnoreCase);

        singleInstance = new SingleInstanceCoordinator();
        StartupDiagnostics.Record($"Single instance created primary={singleInstance.IsPrimary}");
        if (!singleInstance.IsPrimary)
        {
            if (!startInBackground)
            {
                singleInstance.SignalPrimary();
            }

            Shutdown(0);
            return;
        }

        singleInstance.ShowRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        singleInstance.StartListening();
        StartupDiagnostics.Record("Single instance listener started");
        mainWindow = new MainWindow();
        StartupDiagnostics.Record("MainWindow constructed");
        mainWindow.HideRequested += (_, _) => mainWindow.Hide();
        mainWindow.ExitRequested += (_, _) => ExitApplication();
        if (startInBackground)
        {
            mainWindow.ShowActivated = false;
            mainWindow.ShowInTaskbar = false;
            mainWindow.WindowState = WindowState.Minimized;
        }

        mainWindow.Show();
        if (startInBackground)
        {
            mainWindow.Hide();
        }

        StartupDiagnostics.Record("MainWindow shown");
        ConfigureTrayIcon();
        StartupDiagnostics.Record("Tray icon configured");
        _ = Task.Run(SubscribeSystemEvents);
        if (eventArgs.Args.Contains("--startup-lifecycle-smoke-test", StringComparer.OrdinalIgnoreCase))
        {
            _ = Dispatcher.InvokeAsync(RunStartupLifecycleSmokeAsync);
        }
    }

    private void RunUiSmokeTest()
    {
        try
        {
            MainWindow window = new() { AllowClose = true };
            window.Measure(new System.Windows.Size(900, 650));
            window.Arrange(new System.Windows.Rect(0, 0, 900, 650));
            window.UpdateLayout();
            window.Close();
            Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Shutdown(1);
        }
    }

    private async Task RunStartupLifecycleSmokeAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500));
            if (mainWindow is null || !mainWindow.IsLoaded)
            {
                throw new InvalidOperationException("MainWindow did not reach the Loaded state.");
            }

            StartupDiagnostics.Record("Startup lifecycle smoke reached loaded window");
            await mainWindow.ShutdownAsync();
            mainWindow.AllowClose = true;
            mainWindow.Close();
            Shutdown(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs eventArgs)
    {
        if (systemEventsSubscribed)
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            SystemEvents.SessionSwitch -= SystemEvents_SessionSwitch;
        }
        trayIcon?.Dispose();
        singleInstance?.Dispose();
        base.OnExit(eventArgs);
    }

    private void SubscribeSystemEvents()
    {
        try
        {
            SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;
            SystemEvents.SessionSwitch += SystemEvents_SessionSwitch;
            systemEventsSubscribed = true;
        }
        catch (InvalidOperationException exception)
        {
            Dispatcher.BeginInvoke(() => mainWindow?.ReportSystemEventSubscriptionFailure(exception.Message));
        }
    }

    private void SystemEvents_PowerModeChanged(object sender, PowerModeChangedEventArgs eventArgs)
    {
        if (eventArgs.Mode == PowerModes.Suspend)
        {
            Dispatcher.BeginInvoke(() => mainWindow?.PauseForSession("Windows 即将休眠。"));
        }
        else if (eventArgs.Mode == PowerModes.Resume)
        {
            Dispatcher.BeginInvoke(() => mainWindow?.ResumeAfterSession("Windows 已从休眠恢复。"));
        }
    }

    private void SystemEvents_SessionSwitch(object sender, SessionSwitchEventArgs eventArgs)
    {
        if (eventArgs.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
        {
            Dispatcher.BeginInvoke(() => mainWindow?.PauseForSession($"Windows 会话不可用：{eventArgs.Reason}。"));
        }
        else if (eventArgs.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            Dispatcher.BeginInvoke(() => mainWindow?.ResumeAfterSession($"Windows 会话恢复：{eventArgs.Reason}。"));
        }
    }

    private void ConfigureTrayIcon()
    {
        trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Voice Remote Bridge",
            Visible = true
        };
        Forms.ContextMenuStrip menu = new();
        menu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(ShowMainWindow));
        menu.Items.Add("紧急停止", null, (_, _) => Dispatcher.Invoke(() => mainWindow?.EmergencyStop()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        trayIcon.ContextMenuStrip = menu;
        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        if (mainWindow is null)
        {
            return;
        }

        mainWindow.ShowInTaskbar = true;
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
    }

    private async void ExitApplication()
    {
        if (exiting)
        {
            return;
        }

        exiting = true;
        trayIcon?.Dispose();
        if (mainWindow is not null)
        {
            await mainWindow.ShutdownAsync();
            mainWindow.AllowClose = true;
            mainWindow.Close();
        }

        Shutdown(0);
    }
}
