using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using SD = System.Drawing;

namespace ClaudeUsageTray;

/// <summary>
/// El ícono de la bandeja (barra de menús en macOS) y todo lo que coordina: consultas, sesiones,
/// actividad, avisos, sonidos y configuración. Es la misma lógica que TrayApp en Windows.
/// </summary>
sealed class TrayController
{
    readonly App _app;
    readonly TrayIcon _tray = new();
    readonly PopupWindow _popup = new();
    readonly DispatcherTimer _timer = new();
    readonly DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly SessionMonitor _monitor = new();
    readonly ActivityTracker _activity = new();
    readonly RefreshAnimator _refresh = new();
    readonly Dictionary<RefreshFrame, WindowIcon> _refreshIcons = new();
    readonly AppSettings _settings;
    readonly Dictionary<string, UsageSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    readonly NoticeDetector _detector = new();
    readonly ToastStack _toasts = new();
    readonly Sounds _sounds = new();
    readonly ChibiDirector _director = new();

    readonly BubbleWindow _bubble = new();
    MascotWindow? _mascotWindow;
    Theme _theme = Theme.Light;
    LocalTotals? _local;
    DateTime? _updated;
    bool _busy, _scanning, _counting;
    Action? _pendingApply;
    IReadOnlyList<SessionInfo>? _lastSessions;
    int _sampleIndex, _noticesShown;

    UsageView View => _popup.View;
    static string ExePath => Environment.ProcessPath ?? "";

    public TrayController(App app, bool showOnStart, bool sessionsView, bool activityView, bool demo, bool autoSetup)
    {
        _app = app;
        _settings = SettingsStore.Load();
        _director.Settings = _settings;
        if (SettingsStore.DetectNew(_settings)) SettingsStore.Save(_settings);
        _settings.Selected ??= _settings.Accounts.FirstOrDefault()?.ConfigDir;
        Sounds.EnsureDefaults();

        Theme.SelectedPalette = _settings.Palette;
        Mascots.Choose(_settings, Mascots.ChoiceOf(_settings)); // deja en orden lo guardado por versiones anteriores
        Mascots.Selected = _settings.Mascot;
        _refresh.Style = RefreshSprite.Resolve(_settings.Animation, _settings.Mascot);
        ApplyTheme();
        app.ActualThemeVariantChanged += (_, _) => ApplyTheme();

        // Menú del ícono: corto; lo demás está en la vista Ajustes (engranaje del pie).
        var menu = new NativeMenu();
        void Item(string header, Action action)
        {
            var item = new NativeMenuItem(header);
            item.Click += (_, _) => action();
            menu.Add(item);
        }
        Item("Abrir", ShowForm);
        Item("Actualizar", () => _ = RefreshAsync());
        Item("Nueva sesión…", () => _ = NewSessionAsync(null));
        menu.Add(new NativeMenuItemSeparator());
        Item("Ajustes…", () => { ShowForm(); View.ShowSettings(); });
        menu.Add(new NativeMenuItemSeparator());
        Item("Salir", Quit);

        _tray.Menu = menu;
        _tray.ToolTipText = "Uso de Claude";
        _tray.Icon = Gfx.PercentIcon(null, _theme.Tertiary);
        _tray.Clicked += (_, _) => Toggle();
        _tray.IsVisible = true;
        TrayIcon.SetIcons(app, new TrayIcons { _tray });

        View.RefreshRequested += () => _ = RefreshAsync();
        View.AddRequested += () => _ = AddAccountAsync();
        View.RemoveRequested += a => _ = RemoveAccountAsync(a);
        View.RenameRequested += a => _ = RenameAccountAsync(a);
        View.SelectedChanged += account =>
        {
            _settings.Selected = account.ConfigDir;
            SettingsStore.Save(_settings);
            UpdateTray();
        };
        View.SetPinned(_settings.PinnedViews);
        View.PinnedChanged += views =>
        {
            _settings.PinnedViews = views.ToList();
            SettingsStore.Save(_settings);
        };
        View.SettingRequested += action => _ = OnSettingAsync(action);
        View.NewSessionRequested += session => _ = NewSessionAsync(session);
        PushSettings();
        _toasts.Clicked += OpenNotice;
        _bubble.Clicked += OpenNotice;
        _toasts.Shown += () => _noticesShown++;

        // Sesiones: cada 1,5 s con la ventana abierta y cada 3 s cerrada (solo con notificaciones).
        _sessionTimer.Tick += (_, _) => _ = ScanSessionsAsync();
        _sessionTimer.Start();
        _popup.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            _sessionTimer.Interval = TimeSpan.FromMilliseconds(_popup.IsVisible ? 1500 : 3000);
            if (_popup.IsVisible) _ = ScanSessionsAsync();
        };

        // La animación (la mascota u otro estilo) ocupa el ícono y la hora del pie mientras se actualiza.
        _refresh.FrameChanged += ShowRefreshFrame;
        _refresh.Completed += () =>
        {
            _pendingApply?.Invoke();
            _pendingApply = null;
            Render();
            var usage = _detector.Usage(_settings.Accounts, _snapshots);
            if (_settings.Notifications) usage.ForEach(ShowNotice);
        };

        if (sessionsView) View.ShowSessions();
        if (activityView) View.ShowActivity();

        _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, _settings.RefreshMinutes));
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();

        Dispatcher.UIThread.Post(async () =>
        {
            Render();
            if (showOnStart) ShowForm();
            if (demo) ShowSamples();
            UpdateMascotWindow();
            if (autoSetup && NeedsSetup()) await ShowSetupAsync();
            await RefreshAsync();
        });
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        View.SetBusy(true);
        _refresh.Start();
        try
        {
            if (SettingsStore.DetectNew(_settings)) SettingsStore.Save(_settings);
            var accounts = _settings.Accounts.ToList();
            var results = await Task.WhenAll(accounts.Select(a => UsageClient.FetchAsync(a)));
            var profiles = await Task.Run(() => accounts.Select(UsageClient.ReadProfile).ToList());
            var local = await Task.Run(() => LocalUsage.ScanToday(accounts.Select(a => a.ConfigDir)));
            var updated = DateTime.Now;

            // Los datos nuevos se aplican cuando la animación termina, no antes.
            _pendingApply = () =>
            {
                for (var i = 0; i < accounts.Count; i++)
                {
                    var dir = accounts[i].ConfigDir;
                    var fresh = results[i];
                    if (fresh.Error is not null && _snapshots.TryGetValue(dir, out var old) && old.Limits.Count > 0)
                        fresh = old.WithError(fresh.Error);
                    _snapshots[dir] = fresh;
                    _profiles[dir] = profiles[i];
                }
                _local = local;
                _updated = updated;
            };
            _ = CountActivityAsync(accounts);
        }
        finally
        {
            _busy = false;
            View.SetBusy(false);
            _refresh.Finish();
        }
    }

    async Task CountActivityAsync(List<Account> accounts)
    {
        if (_counting) return;
        _counting = true;
        try { View.SetActivity(await Task.Run(() => _activity.Scan(accounts.Select(a => a.ConfigDir)))); }
        finally { _counting = false; }
    }

    async Task ScanSessionsAsync()
    {
        if (_scanning || (!_popup.IsVisible && !_settings.Notifications && _mascotWindow is null)) return;
        _scanning = true;
        try
        {
            var accounts = _settings.Accounts.ToList();
            var sessions = await Task.Run(() => _monitor.Scan(accounts));
            View.SetSessions(sessions);
            _director.SetSessions(sessions);
            _lastSessions = sessions;
            if (_settings.Notifications)
            {
                _detector.Sessions(sessions).ForEach(ShowNotice);
                _detector.HookEvents().ForEach(ShowNotice);
            }
        }
        finally { _scanning = false; }
    }

    void ShowNotice(Notice notice)
    {
        _toasts.Show(notice, _theme);
        _director.Notice(notice.Kind);
        if (_mascotWindow is { } mascot && _settings.Speak) _bubble.Say(notice, mascot.Chibi, mascot.ScreenBounds);
        if (_settings.Sound) _sounds.Play(notice.Kind, _settings.SoundPack);
    }

    /// <summary>Lo que la vista Ajustes muestra: se manda de nuevo cada vez que algo cambia.</summary>
    void PushSettings()
    {
        var s = _settings;
        var pack = Sounds.Packs.FirstOrDefault(p => p.Id == s.SoundPack).Label ?? s.SoundPack;
        var behavior = new UsageView.MascotBehavior(s.FlowGestures, s.DanceToMusic, s.NoticeGestures, s.GreetOnAppear, s.AngryOnClicks, s.SleepWithoutSessions);
        View.SetSettingsState(new UsageView.SettingsState(
                s.Notifications, s.Sound, pack, Theme.PaletteLabel(s.Palette),
                Mascots.Label(Mascots.ChoiceOf(s)), s.DesktopMascot, s.DesktopMascot ? s.Speak : null, s.DesktopMascot ? behavior : null,
                StartupRegistration.IsEnabled, s.Accounts.Count),
            _settings.SoundPack, _settings.Palette, Mascots.ChoiceOf(_settings));
    }

    async Task OnSettingAsync(string action)
    {
        if (action.StartsWith("pack:", StringComparison.Ordinal)) SetSoundPack(action[5..]);
        else if (action.StartsWith("palette:", StringComparison.Ordinal)) SetPalette(action[8..]);
        else if (action.StartsWith("mascot:", StringComparison.Ordinal)) SetMascot(action[7..]);
        else
        {
            switch (action)
            {
                case "notify": ToggleNotifications(); break;
                case "sound": ToggleSound(); break;
                case "startup": ToggleStartup(); break;
                case "desk": ToggleDesktopMascot(); break;
                case "speak": ToggleSpeak(); break;
                case "flowgest": Flip(s => s.FlowGestures = !s.FlowGestures); break;
                case "music": Flip(s => s.DanceToMusic = !s.DanceToMusic); break;
                case "noticegest": Flip(s => s.NoticeGestures = !s.NoticeGestures); break;
                case "greet": Flip(s => s.GreetOnAppear = !s.GreetOnAppear); break;
                case "angry": Flip(s => s.AngryOnClicks = !s.AngryOnClicks); break;
                case "sleep":
                    Flip(s => s.SleepWithoutSessions = !s.SleepWithoutSessions);
                    _ = ScanSessionsAsync(); // para que se duerma o se despierte ya
                    break;
                case "sample": ShowNextSample(); break;
                case "sounds": Sounds.EnsureDefaults(); Gfx.OpenFolder(Sounds.Folder); break;
                case "add": await AddAccountAsync(); break;
                case "setup": await ShowSetupAsync(); break;
                case "diag": await CopyDiagnosticsAsync(); break;
                case "unsetup":
                    _popup.HoldOpen = true;
                    try { await RemoveSetupAsync(); }
                    finally { _popup.HoldOpen = false; }
                    break;
            }
        }
        PushSettings();
    }

    /// <summary>Desde una sesión, propone su cuenta y su carpeta; el resto, lo último elegido.</summary>
    async Task NewSessionAsync(SessionInfo? from)
    {
        var initial = _settings.Launch;
        if (from is not null)
            initial = new LaunchOptions
            {
                ConfigDir = from.Account.ConfigDir, Folder = from.Cwd,
                Model = initial.Model, Effort = initial.Effort, Advisor = initial.Advisor,
            };
        var recent = (_lastSessions ?? []).Select(s => s.Cwd).Prepend(initial.Folder ?? "");

        _popup.HoldOpen = true;
        try
        {
            var dialog = new NewSessionWindow(_settings.Accounts, initial, recent, _theme);
            dialog.Show();
            dialog.Activate();
            if (await dialog.Result is not { } options) return;
            _settings.Launch = options;
            SettingsStore.Save(_settings);
            Launcher.Start(options);
        }
        catch (Exception ex)
        {
            await Dialogs.Info($"No se pudo abrir la sesión: {ex.Message}", _theme);
        }
        finally
        {
            _popup.HoldOpen = false;
        }
    }

    void ToggleSound()
    {
        _settings.Sound = !_settings.Sound;
        SettingsStore.Save(_settings);
        if (_settings.Sound) _sounds.Play(NoticeKind.Done, _settings.SoundPack, force: true);
    }

    void SetSoundPack(string pack)
    {
        _settings.SoundPack = pack;
        SettingsStore.Save(_settings);
        _sounds.Play(NoticeKind.Done, pack, force: true);
    }

    void SetPalette(string palette)
    {
        _settings.Palette = palette;
        SettingsStore.Save(_settings);
        Theme.SelectedPalette = palette;
        ApplyTheme();
    }

    /// <summary>Lo elegido en «Mascota»: la mascota de los avisos y la animación al actualizar.</summary>
    void SetMascot(string choice)
    {
        var wasChibi = _settings.Mascot == Mascots.Chibi;
        Mascots.Choose(_settings, choice);
        if (!wasChibi && _settings.Mascot == Mascots.Chibi) _settings.DesktopMascot = true; // el chibi sale al escritorio
        SettingsStore.Save(_settings);
        Mascots.Selected = _settings.Mascot; // los avisos la leen al dibujarse
        _refresh.Style = RefreshSprite.Resolve(_settings.Animation, _settings.Mascot);
        _refreshIcons.Clear();
        ShowRefreshFrame();
        UpdateMascotWindow();
        _ = RefreshAsync(); // muestra de la animación elegida, como la del estilo de sonido
    }

    void ToggleDesktopMascot()
    {
        _settings.DesktopMascot = !_settings.DesktopMascot;
        SettingsStore.Save(_settings);
        UpdateMascotWindow();
    }

    void ToggleSpeak()
    {
        _settings.Speak = !_settings.Speak;
        SettingsStore.Save(_settings);
        if (!_settings.Speak) _bubble.Hide();
    }

    /// <summary>Un interruptor de la mascota: se guarda y la música se mira solo si hace falta.</summary>
    void Flip(Action<AppSettings> flip)
    {
        flip(_settings);
        SettingsStore.Save(_settings);
        Music.Watch(_settings.DesktopMascot && _settings.DanceToMusic);
    }

    /// <summary>
    /// La mascota del escritorio está si no se ocultó; si cambia de mascota (chibi o clásica), la
    /// ventana se rehace con la nueva.
    /// </summary>
    void UpdateMascotWindow()
    {
        var wanted = _settings.DesktopMascot;
        Music.Watch(wanted && _settings.DanceToMusic); // la música solo se mira si hay mascota que baile
        if (_mascotWindow is { } current && (!wanted || current.Chibi != Mascots.IsChibi))
        {
            _bubble.Hide();
            current.Close();
            _mascotWindow = null;
        }
        if (!wanted || _mascotWindow is not null) return;
        PixelPoint? saved = _settings.DesktopMascotX is { } x && _settings.DesktopMascotY is { } y ? new PixelPoint(x, y) : null;
        _mascotWindow = new MascotWindow(_director, saved, Mascots.IsChibi, () => _theme.Accent);
        _mascotWindow.PositionChanged += (_, _) =>
        {
            if (_bubble.IsVisible && _mascotWindow is { } moved) _bubble.Follow(moved.ScreenBounds);
        };
        _mascotWindow.OpenRequested += ShowForm;
        _mascotWindow.HideRequested += () => { ToggleDesktopMascot(); PushSettings(); };
        _mascotWindow.Moved += at =>
        {
            (_settings.DesktopMascotX, _settings.DesktopMascotY) = (at.X, at.Y);
            SettingsStore.Save(_settings);
        };
        _mascotWindow.Show();
        _ = ScanSessionsAsync(); // para que empiece haciendo lo que corresponde
    }

    /// <summary>Toma el tema del momento (modo del sistema y paleta elegida): ventana, ícono y animación.</summary>
    void ApplyTheme()
    {
        _theme = Theme.ForDark(_app.ActualThemeVariant == ThemeVariant.Dark);
        _popup.ApplyTheme(_theme);
        _refreshIcons.Clear(); // se vuelven a dibujar con el acento nuevo
        ShowRefreshFrame();
        UpdateTray();
    }

    /// <summary>El cuadro actual de la animación en el ícono y en el pie de la ventana.</summary>
    void ShowRefreshFrame()
    {
        if (_refresh.Frame is { } frame)
        {
            if (!_refreshIcons.TryGetValue(frame, out var icon))
                _refreshIcons[frame] = icon = Gfx.RefreshIcon(_refresh.Style, frame, _theme.Accent);
            _tray.Icon = icon;
        }
        View.SetRefreshFrame(_refresh.Frame, _refresh.Style);
    }

    void ToggleNotifications()
    {
        _settings.Notifications = !_settings.Notifications;
        SettingsStore.Save(_settings);
        PushSettings();
        if (_settings.Notifications) _detector.Reset();
        else _toasts.CloseAll();
    }

    void OpenNotice(Notice notice)
    {
        if (notice.SessionId is { } id) View.FocusSession(id);
        else if (notice.AccountDir is { } dir)
        {
            _settings.Selected = dir;
            View.FocusAccount(dir);
        }
        ShowForm();
    }

    void ShowNextSample()
    {
        var samples = NoticeDetector.Samples().ToList();
        ShowNotice(samples[_sampleIndex % samples.Count]);
        _sampleIndex++;
    }

    void ShowSamples()
    {
        var queue = new Queue<Notice>(NoticeDetector.Samples());
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            if (queue.Count == 0) { timer.Stop(); return; }
            ShowNotice(queue.Dequeue());
        };
        timer.Start();
    }

    async Task CopyDiagnosticsAsync()
    {
        var report = Diagnostics.Report(_settings, _lastSessions, _noticesShown);
        try
        {
            if (_popup.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(report);
                await Dialogs.Info("Diagnóstico copiado al portapapeles.", _theme);
                return;
            }
        }
        catch (Exception) { }
        await Dialogs.Info(report, _theme);
    }

    void Render()
    {
        UpdateTray();
        PushSettings(); // la cantidad de cuentas puede haber cambiado
        View.Render(_settings.Accounts, _settings.Selected, _snapshots, _profiles, _local, _updated);
    }

    void UpdateTray()
    {
        var selected = _settings.Accounts.FirstOrDefault(a => _settings.Selected is not null && Account.SamePath(a.ConfigDir, _settings.Selected))
            ?? _settings.Accounts.FirstOrDefault();
        UsageSnapshot? snap = null;
        if (selected is not null) _snapshots.TryGetValue(selected.ConfigDir, out snap);
        var session = snap?.Session;

        if (!_refresh.Running)
        {
            SD.Color color = session is null || snap!.Error is not null ? _theme.Tertiary : _theme.For(session.Percent, session.Severity);
            _tray.Icon = Gfx.PercentIcon(session?.Percent, color);
        }

        var lines = _settings.Accounts.Select(a =>
        {
            _snapshots.TryGetValue(a.ConfigDir, out var s);
            if (s?.Session is null) return $"{a.Label}  —";
            var weekly = s.Weekly is null ? "" : $" · {s.Weekly.Percent:0} % semana";
            return $"{a.Label}  {s.Session.Percent:0} % sesión{weekly}";
        });
        _tray.ToolTipText = string.Join("\n", lines.Prepend("Uso de Claude"));
    }

    void Toggle()
    {
        if (_popup.IsVisible) { _popup.HidePopup(); return; }
        if ((DateTime.Now - _popup.LastHidden).TotalMilliseconds < 300) return;
        ShowForm();
    }

    void ShowForm()
    {
        Render();
        _popup.ShowNear();
        if (_updated is null || DateTime.Now - _updated > TimeSpan.FromMinutes(1)) _ = RefreshAsync();
    }

    async Task AddAccountAsync()
    {
        if (!_popup.IsVisible) ShowForm();
        _popup.HoldOpen = true;
        try
        {
            var start = await _popup.StorageProvider.TryGetFolderFromPathAsync(Account.Home);
            var folders = await _popup.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Carpeta de configuración de Claude Code de la cuenta (la de CLAUDE_CONFIG_DIR)",
                AllowMultiple = false,
                SuggestedStartLocation = start,
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } dir) return;

            if (_settings.Accounts.FirstOrDefault(a => Account.SamePath(a.ConfigDir, dir)) is { } existing)
                _settings.Selected = existing.ConfigDir;
            else
            {
                if (!Credentials.Exists(dir)
                    && !await Dialogs.Confirm("Esa carpeta todavía no tiene credenciales de Claude Code. ¿La agrego de todas formas?", _theme))
                    return;
                _settings.Ignored.RemoveAll(i => Account.SamePath(i, dir));
                _settings.Accounts.Add(new Account { Label = Account.LabelFor(dir), ConfigDir = dir });
                _settings.Selected = dir;
            }
            SettingsStore.Save(_settings);
        }
        finally { _popup.HoldOpen = false; }
        ShowForm();
        _ = RefreshAsync();
    }

    async Task RemoveAccountAsync(Account account)
    {
        _popup.HoldOpen = true;
        try
        {
            if (!await Dialogs.Confirm($"¿Quitar «{account.Label}» de la lista? No se borra nada de la carpeta.", _theme)) return;
        }
        finally { _popup.HoldOpen = false; }
        _settings.Accounts.Remove(account);
        _settings.Ignored.Add(account.ConfigDir);
        _snapshots.Remove(account.ConfigDir);
        _profiles.Remove(account.ConfigDir);
        if (_settings.Selected is not null && Account.SamePath(_settings.Selected, account.ConfigDir))
            _settings.Selected = _settings.Accounts.FirstOrDefault()?.ConfigDir;
        SettingsStore.Save(_settings);
        Render();
    }

    async Task RenameAccountAsync(Account account)
    {
        _popup.HoldOpen = true;
        string? name;
        try { name = await Dialogs.Ask("Renombrar cuenta", "Nombre de la cuenta:", account.Label, _theme); }
        finally { _popup.HoldOpen = false; }
        if (name is null) return;
        account.Label = name;
        SettingsStore.Save(_settings);
        Render();
    }

    void ToggleStartup()
    {
        try { StartupRegistration.Set(!StartupRegistration.IsEnabled); } catch (Exception) { }
        PushSettings();
    }

    IEnumerable<string> SetupDirs()
    {
        var dirs = _settings.Accounts.Select(a => a.ConfigDir).ToList();
        try
        {
            dirs.AddRange(Directory.EnumerateDirectories(Account.Home, ".claude*")
                .Where(d => Directory.Exists(Path.Combine(d, "projects")) || Directory.Exists(Path.Combine(d, "sessions"))
                    || File.Exists(Path.Combine(d, "settings.json"))));
        }
        catch (Exception) { }
        return dirs.Select(Account.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
    }

    bool NeedsSetup() =>
        !_settings.SetupDone || SetupDirs().Any(d => HookInstaller.Status(d, ExePath) == HookInstaller.State.Outdated);

    async Task ShowSetupAsync()
    {
        _popup.HidePopup();
        var setup = new SetupWindow(_settings, ExePath, _theme, SetupDirs());
        setup.Show();
        setup.Activate();
        var accepted = await setup.Result;
        _settings.SetupDone = true;
        SettingsStore.Save(_settings);
        if (!accepted) return;

        var lines = setup.ApplyHooks(ExePath);
        try
        {
            StartupRegistration.Set(setup.StartWithSystem);
            lines.Add(setup.StartWithSystem ? "Iniciará con el sistema." : "No iniciará con el sistema.");
        }
        catch (Exception ex) { lines.Add($"Iniciar con el sistema: no se pudo cambiar ({ex.Message})."); }
        PushSettings();
        if (setup.NotificationsOn != _settings.Notifications) ToggleNotifications();
        lines.Add(_settings.Notifications ? "Notificaciones encendidas." : "Notificaciones apagadas.");
        if (lines.Any(l => l.Contains("hook")))
            lines.Add("Los avisos de permisos funcionan en las sesiones de Claude Code que se abran desde ahora.");
        await Dialogs.Info(string.Join(Environment.NewLine, lines), _theme);
    }

    async Task RemoveSetupAsync()
    {
        if (!await Dialogs.Confirm("Se quitará el hook de avisos de permisos de cada settings.json (con respaldo) y el inicio con el sistema. "
                + "La app sigue funcionando hasta que la cierre. ¿Continuar?", _theme))
            return;
        var lines = new List<string>();
        foreach (var dir in SetupDirs())
        {
            var state = HookInstaller.Status(dir, ExePath);
            if (state is not (HookInstaller.State.Installed or HookInstaller.State.Outdated)) continue;
            var name = dir.Replace(Account.Home, "~");
            try { HookInstaller.Uninstall(dir); lines.Add($"{name}: hook quitado."); }
            catch (Exception ex) { lines.Add($"{name}: no se pudo quitar ({ex.Message})."); }
        }
        try { StartupRegistration.Set(false); lines.Add("Ya no inicia con el sistema."); }
        catch (Exception ex) { lines.Add($"Iniciar con el sistema: no se pudo quitar ({ex.Message})."); }
        PushSettings();
        await Dialogs.Info(string.Join(Environment.NewLine, lines), _theme);
    }

    void Quit()
    {
        _timer.Stop();
        _sessionTimer.Stop();
        _toasts.CloseAll();
        _mascotWindow?.Close();
        _bubble.Close();
        _sounds.Dispose();
        _refresh.Dispose();
        _tray.IsVisible = false;
        _tray.Dispose();
        if (_app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown();
    }
}
