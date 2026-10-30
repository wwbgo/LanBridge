using LanBridge.App;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

    var calls = 0;
    var exits = 0;
    var errors = new List<string>();
    var lifecycle = new AppLifecycle();

    var result = await lifecycle.ShutdownAndExitAsync(
        _ => { calls++; return Task.CompletedTask; },
        () => exits++,
        errors.Add);

    Check(result == LifecycleExitResult.Exited, "Successful shutdown must report Exited.");
    Check(calls == 1, "Successful shutdown must call backend shutdown once.");
    Check(exits == 1, "UI close must happen only after backend shutdown acknowledgement.");
    Check(errors.Count == 0, "Successful shutdown must not show an error.");
    Console.WriteLine("PASS lifecycle exits only after shutdown acknowledgement");

    calls = 0;
    exits = 0;
    errors.Clear();
    lifecycle = new AppLifecycle();
    result = await lifecycle.ShutdownAndExitAsync(
        _ => { calls++; return Task.FromException(new InvalidOperationException("服务管理器拒绝停止。")); },
        () => exits++,
        errors.Add);

    Check(result == LifecycleExitResult.Failed, "Failed shutdown must report Failed.");
    Check(calls == 1, "Failed shutdown must attempt the backend once.");
    Check(exits == 0, "Failed shutdown must keep the UI alive.");
    Check(errors.Count == 1 && errors[0].Contains("服务管理器拒绝停止", StringComparison.Ordinal), "Failure must be shown meaningfully.");
    Check(!lifecycle.IsExitRequested, "Failed shutdown must allow a later retry.");
    Console.WriteLine("PASS failed shutdown keeps tray/UI alive and permits retry");

    lifecycle = new AppLifecycle();
    var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var first = lifecycle.ShutdownAndExitAsync(
        async _ => { firstEntered.SetResult(); await release.Task; },
        () => { },
        _ => { });
    await firstEntered.Task;
    result = await lifecycle.ShutdownAndExitAsync(_ => Task.CompletedTask, () => { }, _ => { });
    Check(result == LifecycleExitResult.AlreadyInProgress, "Concurrent exit requests must not start a second shutdown.");
    release.SetResult();
    Check(await first == LifecycleExitResult.Exited, "The first exit request must finish normally.");
    Console.WriteLine("PASS concurrent exit requests are serialized");

    var appSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LanBridge.App", "App.xaml.cs"));
    Check(appSource.Contains("TrayIcon", StringComparison.Ordinal), "App must own the tray lifetime.");
    Check(!appSource.Contains("Environment.Exit", StringComparison.Ordinal), "App must not force-exit before shutdown acknowledgement.");
    var windowSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LanBridge.App", "MainWindow.cs"));
    Check(windowSource.Contains("AppWindow.Closing", StringComparison.Ordinal), "MainWindow must cancel close and hide to tray.");
    Check(windowSource.Contains("ShutdownAndExitAsync", StringComparison.Ordinal), "MainWindow must use the lifecycle shutdown seam.");
    Check(windowSource.Contains("_exitLifecycle.IsExitRequested", StringComparison.Ordinal), "MainWindow must suppress reconnect during exit.");
    var traySource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LanBridge.App", "TrayIcon.cs"));
    Check(traySource.Contains("Shell_NotifyIcon", StringComparison.Ordinal), "Tray must use Shell_NotifyIcon.");
    Check(traySource.Contains("TaskbarCreated", StringComparison.Ordinal), "Tray must recover after Explorer restart.");
    Check(traySource.Contains("NimDelete", StringComparison.Ordinal), "Tray must remove its icon on disposal.");
    Check(traySource.Contains("private const string TrayWindowClassName = \"LanBridge.NotificationAreaWindow\"", StringComparison.Ordinal), "Tray message window class must remain stable for native smoke automation.");
    Check(traySource.Contains("private readonly uint _callbackMessage = WmApp + 0x31", StringComparison.Ordinal), "Tray callback must use the documented WM_APP offset.");
    Check(traySource.Contains("private const int IdOpen = 1001", StringComparison.Ordinal) && traySource.Contains("private const int IdExit = 1002", StringComparison.Ordinal), "Tray menu command IDs must remain stable.");
    Check(traySource.Contains("打开主界面", StringComparison.Ordinal) && traySource.Contains("退出 LanBridge", StringComparison.Ordinal), "Tray menu must expose accessible Chinese commands.");
    Console.WriteLine("PASS source harness covers stable tray HWND class, callback and accessible menu IDs");
