namespace ClaudeUsageTray;

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray = new();
    readonly UsageForm _form = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 1500 };
    readonly SessionMonitor _monitor = new();
    readonly ActivityTracker _activity = new();
    readonly MascotAnimator _mascot = new();
    readonly Dictionary<MascotFrame, Icon> _mascotIcons = new();
    readonly AppSettings _settings;
    readonly Dictionary<string, UsageSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ToolStripMenuItem _startup;
    readonly ContextMenuStrip _menu = new();
    readonly ToolStripMenuItem _notifyItem;
    readonly NoticeDetector _detector = new();
    readonly ToastManager _toasts = new();
    readonly Sounds _sounds = new();
    readonly ToolStripMenuItem _soundItem;
    readonly ToolStripMenuItem _styleItem;

    LocalTotals? _local;
    DateTime? _updated;
    bool _busy, _scanning, _counting;
    Icon? _icon;
    Action? _pendingApply;
    IReadOnlyList<SessionInfo>? _lastSessions;
    int _sampleIndex, _noticesShown;

    public TrayApp(bool showOnStart, bool sessionsView = false, bool activityView = false, bool demo = false, bool autoSetup = true)
    {
        _settings = SettingsStore.Load();
        if (SettingsStore.DetectNew(_settings)) SettingsStore.Save(_settings);
        _settings.Selected ??= _settings.Accounts.FirstOrDefault()?.ConfigDir;
        Sounds.EnsureDefaults();

        var menu = _menu;
        menu.Items.Add("Abrir", null, (_, _) => ShowForm());
        menu.Items.Add("Actualizar", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Agregar cuenta…", null, (_, _) => AddAccount());
        menu.Items.Add(new ToolStripSeparator());
        _startup = new ToolStripMenuItem("Iniciar con Windows", null, (_, _) => ToggleStartup()) { Checked = StartupRegistration.IsEnabled };
        menu.Items.Add(_startup);
        _notifyItem = new ToolStripMenuItem("Notificaciones", null, (_, _) => ToggleNotifications()) { Checked = _settings.Notifications };
        menu.Items.Add(_notifyItem);
        _soundItem = new ToolStripMenuItem("Sonido", null, (_, _) => ToggleSound()) { Checked = _settings.Sound };
        menu.Items.Add(_soundItem);
        var style = new ToolStripMenuItem("Estilo de sonido");
        foreach (var (id, label) in Sounds.Packs)
            style.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetSoundPack(id)) { Tag = id, Checked = _settings.SoundPack == id });
        menu.Items.Add(style);
        _styleItem = style;
        menu.Items.Add("Abrir carpeta de sonidos", null, (_, _) => OpenSoundsFolder());
        menu.Items.Add("Ver un aviso de ejemplo", null, (_, _) => ShowNextSample());
        menu.Items.Add("Copiar diagnóstico", null, (_, _) => CopyDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configurar…", null, (_, _) => ShowSetup());
        menu.Items.Add("Quitar configuración…", null, (_, _) => RemoveSetup());
        menu.Items.Add("Salir", null, (_, _) => Quit());
        Menus.Apply(menu, _form.Theme);

        _tray.ContextMenuStrip = menu;
        _tray.Text = "Uso de Claude";
        SetIcon(null, _form.Theme.Tertiary);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
        _tray.Visible = true;

        _form.RefreshRequested += async () => await RefreshAsync();
        _form.AddRequested += AddAccount;
        _form.RemoveRequested += RemoveAccount;
        _form.RenameRequested += RenameAccount;
        _form.ThemeChanged += () => { Menus.Apply(_menu, _form.Theme); UpdateTray(); };
        _form.SelectedChanged += account =>
        {
            _settings.Selected = account.ConfigDir;
            SettingsStore.Save(_settings);
            UpdateTray();
        };

        // Las sesiones se leen cada 1,5 s con la ventana abierta y cada 3 s cerrada, solo si hay
        // notificaciones encendidas.
        _form.NotificationsOn = _settings.Notifications;
        _form.NotificationsToggled += ToggleNotifications;
        _toasts.Clicked += OpenNotice;
        _toasts.Shown += () => _noticesShown++;
        _sessionTimer.Interval = 3000;
        _sessionTimer.Tick += async (_, _) => await ScanSessionsAsync();
        _sessionTimer.Start();
        _form.VisibleChanged += async (_, _) =>
        {
            _sessionTimer.Interval = _form.Visible ? 1500 : 3000;
            if (_form.Visible) await ScanSessionsAsync();
        };
        // La mascota ocupa el ícono y la hora del pie mientras se actualiza; el estado nuevo se
        // muestra cuando termina.
        _mascot.FrameChanged += () =>
        {
            if (_mascot.Frame is { } frame)
            {
                if (!_mascotIcons.TryGetValue(frame, out var icon)) _mascotIcons[frame] = icon = Mascot.Icon(frame);
                _tray.Icon = icon;
            }
            _form.SetMascot(_mascot.Frame);
        };
        _mascot.Completed += () =>
        {
            _pendingApply?.Invoke();
            _pendingApply = null;
            Render();
            var usage = _detector.Usage(_settings.Accounts, _snapshots);
            if (_settings.Notifications) usage.ForEach(ShowNotice);
        };

        if (sessionsView) _form.ShowSessions();
        if (activityView) _form.ShowActivity();

        _timer.Interval = Math.Max(1, _settings.RefreshMinutes) * 60_000;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();

        // La primera consulta, ya con el bucle de mensajes corriendo.
        var first = new System.Windows.Forms.Timer { Interval = 100 };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            first.Dispose();
            Render();
            if (showOnStart) ShowForm();
            if (demo) ShowSamples();
            // Primera vez, o la app cambió de lugar y el hook apunta a la ruta vieja.
            if (autoSetup && NeedsSetup()) ShowSetup();
            await RefreshAsync();
        };
        first.Start();
    }

    async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        _form.SetBusy(true);
        _mascot.Start();
        try
        {
            if (SettingsStore.DetectNew(_settings)) SettingsStore.Save(_settings);
            var accounts = _settings.Accounts.ToList();

            var results = await Task.WhenAll(accounts.Select(a => UsageClient.FetchAsync(a)));
            var profiles = await Task.Run(() => accounts.Select(UsageClient.ReadProfile).ToList());
            var local = await Task.Run(() => LocalUsage.ScanToday(accounts.Select(a => a.ConfigDir)));
            var updated = DateTime.Now;

            // Los datos nuevos se aplican cuando la mascota termina, no antes.
            _pendingApply = () =>
            {
                for (var i = 0; i < accounts.Count; i++)
                {
                    var dir = accounts[i].ConfigDir;
                    var fresh = results[i];
                    // Si la consulta falla, se siguen mostrando los últimos datos buenos con el aviso.
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
            _form.SetBusy(false);
            _mascot.Finish(); // al terminar la animación llama a Render
        }
    }

    /// <summary>Corre con cada actualización, aunque la ventana esté cerrada, para que el historial no pierda días.</summary>
    async Task CountActivityAsync(List<Account> accounts)
    {
        if (_counting) return;
        _counting = true;
        try
        {
            var summary = await Task.Run(() => _activity.Scan(accounts.Select(a => a.ConfigDir)));
            _form.SetActivity(summary);
        }
        finally
        {
            _counting = false;
        }
    }

    async Task ScanSessionsAsync()
    {
        if (_scanning || (!_form.Visible && !_settings.Notifications)) return;
        _scanning = true;
        try
        {
            var accounts = _settings.Accounts.ToList();
            var sessions = await Task.Run(() => _monitor.Scan(accounts));
            _form.SetSessions(sessions);
            _lastSessions = sessions;
            if (_settings.Notifications)
            {
                _detector.Sessions(sessions).ForEach(ShowNotice);
                _detector.HookEvents().ForEach(ShowNotice);
            }
        }
        finally
        {
            _scanning = false;
        }
    }

    void ShowNotice(Notice notice)
    {
        _toasts.Show(notice);
        if (_settings.Sound) _sounds.Play(notice.Kind, _settings.SoundPack);
    }

    void ToggleSound()
    {
        _settings.Sound = !_settings.Sound;
        SettingsStore.Save(_settings);
        _soundItem.Checked = _settings.Sound;
        if (_settings.Sound) _sounds.Play(NoticeKind.Done, _settings.SoundPack, force: true); // para oír cómo suena
    }

    void SetSoundPack(string pack)
    {
        _settings.SoundPack = pack;
        SettingsStore.Save(_settings);
        foreach (ToolStripMenuItem item in _styleItem.DropDownItems) item.Checked = (string?)item.Tag == pack;
        _sounds.Play(NoticeKind.Done, pack, force: true); // muestra del estilo elegido
    }

    static void OpenSoundsFolder()
    {
        Sounds.EnsureDefaults();
        System.Diagnostics.Process.Start("explorer.exe", Sounds.Folder);
    }

    void ToggleNotifications()
    {
        _settings.Notifications = !_settings.Notifications;
        SettingsStore.Save(_settings);
        _notifyItem.Checked = _settings.Notifications;
        _form.NotificationsOn = _settings.Notifications;
        if (_settings.Notifications) _detector.Reset(); // sin avisos de lo que pasó mientras estaban apagadas
        else _toasts.CloseAll();
    }

    /// <summary>Un clic en un aviso abre la ventana en esa sesión o en esa cuenta.</summary>
    void OpenNotice(Notice notice)
    {
        if (notice.SessionId is { } id) _form.FocusSession(id);
        else if (notice.AccountDir is { } dir)
        {
            _settings.Selected = dir;
            _form.FocusAccount(dir);
        }
        ShowForm();
    }

    /// <summary>Cada clic en el menú muestra el siguiente ejemplo, en orden y de uno en uno.</summary>
    void ShowNextSample()
    {
        var samples = NoticeDetector.Samples().ToList();
        ShowNotice(samples[_sampleIndex % samples.Count]);
        _sampleIndex++;
    }

    void CopyDiagnostics()
    {
        var report = Diagnostics.Report(_settings, _lastSessions, _noticesShown);
        try
        {
            Clipboard.SetText(report);
            _tray.ShowBalloonTip(3000, "Uso de Claude", "Diagnóstico copiado al portapapeles.", ToolTipIcon.None);
        }
        catch (Exception)
        {
            MessageBox.Show(report, "Diagnóstico de Uso de Claude");
        }
    }

    /// <summary>Todos los ejemplos seguidos (solo --demo, para probar).</summary>
    void ShowSamples()
    {
        var queue = new Queue<Notice>(NoticeDetector.Samples());
        var timer = new System.Windows.Forms.Timer { Interval = 450 };
        timer.Tick += (_, _) =>
        {
            if (queue.Count == 0) { timer.Stop(); timer.Dispose(); return; }
            ShowNotice(queue.Dequeue());
        };
        timer.Start();
    }

    void Render()
    {
        UpdateTray();
        _form.Render(_settings.Accounts, _settings.Selected, _snapshots, _profiles, _local, _updated);
    }

    void UpdateTray()
    {
        var selected = _settings.Accounts.FirstOrDefault(a => _settings.Selected is not null && Account.SamePath(a.ConfigDir, _settings.Selected))
            ?? _settings.Accounts.FirstOrDefault();
        UsageSnapshot? snap = null;
        if (selected is not null) _snapshots.TryGetValue(selected.ConfigDir, out snap);
        var session = snap?.Session;

        var theme = _form.Theme;
        if (_mascot.Running) { } // el ícono es de la mascota hasta que termine
        else if (session is null) SetIcon(null, theme.Tertiary);
        else SetIcon(session.Percent, snap!.Error is null ? theme.For(session.Percent, session.Severity) : theme.Tertiary);

        var lines = _settings.Accounts.Select(a =>
        {
            _snapshots.TryGetValue(a.ConfigDir, out var s);
            if (s?.Session is null) return $"{a.Label}  —";
            var weekly = s.Weekly is null ? "" : $" · {s.Weekly.Percent:0} % semana";
            return $"{a.Label}  {s.Session.Percent:0} % sesión{weekly}";
        });
        var text = string.Join("\n", lines.Prepend("Uso de Claude"));
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    void SetIcon(double? percent, Color color)
    {
        var previous = _icon;
        _icon = Draw.TrayIcon(percent, color);
        _tray.Icon = _icon;
        previous?.Dispose();
    }

    void Toggle()
    {
        if (_form.Visible) { _form.Hide(); return; }
        // El mismo clic que quitó el foco ya la cerró: no volver a abrirla.
        if ((DateTime.Now - _form.LastHidden).TotalMilliseconds < 300) return;
        ShowForm();
    }

    void ShowForm()
    {
        Render(); // recalcula los «se reinicia en…»
        _form.ShowNear();
        if (_updated is null || DateTime.Now - _updated > TimeSpan.FromMinutes(1))
            _ = RefreshAsync();
    }

    void AddAccount()
    {
        _form.HoldOpen = true;
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Elija la carpeta de configuración de Claude Code de la cuenta (la de CLAUDE_CONFIG_DIR, con .credentials.json).",
                UseDescriptionForTitle = true,
                InitialDirectory = Account.Home,
            };
            if (dialog.ShowDialog(_form) != DialogResult.OK) return;
            var dir = dialog.SelectedPath;

            if (_settings.Accounts.FirstOrDefault(a => Account.SamePath(a.ConfigDir, dir)) is { } existing)
            {
                _settings.Selected = existing.ConfigDir;
            }
            else
            {
                if (!File.Exists(Path.Combine(dir, ".credentials.json"))
                    && MessageBox.Show(_form, "Esa carpeta no tiene .credentials.json todavía. ¿La agrego de todas formas?",
                        "Uso de Claude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                _settings.Ignored.RemoveAll(i => Account.SamePath(i, dir));
                _settings.Accounts.Add(new Account { Label = Account.LabelFor(dir), ConfigDir = dir });
                _settings.Selected = dir;
            }
            SettingsStore.Save(_settings);
        }
        finally
        {
            _form.HoldOpen = false;
        }
        ShowForm();
        _ = RefreshAsync();
    }

    void RemoveAccount(Account account)
    {
        _form.HoldOpen = true;
        try
        {
            if (MessageBox.Show(_form, $"¿Quitar «{account.Label}» de la lista? No se borra nada de la carpeta.",
                    "Uso de Claude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
        }
        finally
        {
            _form.HoldOpen = false;
        }

        _settings.Accounts.Remove(account);
        _settings.Ignored.Add(account.ConfigDir);
        _snapshots.Remove(account.ConfigDir);
        _profiles.Remove(account.ConfigDir);
        if (_settings.Selected is not null && Account.SamePath(_settings.Selected, account.ConfigDir))
            _settings.Selected = _settings.Accounts.FirstOrDefault()?.ConfigDir;
        SettingsStore.Save(_settings);
        Render();
        _form.Activate();
    }

    void RenameAccount(Account account)
    {
        _form.HoldOpen = true;
        string? name;
        try { name = Prompt.Ask(_form, "Renombrar cuenta", "Nombre de la pestaña:", account.Label); }
        finally { _form.HoldOpen = false; }
        if (name is null) return;
        account.Label = name;
        SettingsStore.Save(_settings);
        Render();
        _form.Activate();
    }

    void ToggleStartup()
    {
        StartupRegistration.Set(!StartupRegistration.IsEnabled);
        _startup.Checked = StartupRegistration.IsEnabled;
    }

    static string ExePath => Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>Carpetas de Claude Code donde puede ir el hook: las cuentas y las ~/.claude* que existan.</summary>
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

    void ShowSetup()
    {
        _form.Hide();
        using var setup = new SetupForm(_settings, ExePath, _form.Theme, SetupDirs());
        var accepted = setup.ShowDialog() == DialogResult.OK;
        _settings.SetupDone = true; // «Ahora no» tampoco vuelve a preguntar, salvo que la app cambie de lugar
        SettingsStore.Save(_settings);
        if (!accepted) return;

        var lines = setup.ApplyHooks(ExePath);
        try
        {
            StartupRegistration.Set(setup.StartWithWindows);
            lines.Add(setup.StartWithWindows ? "Iniciará con Windows." : "No iniciará con Windows.");
        }
        catch (Exception ex) { lines.Add($"Inicio con Windows: no se pudo cambiar ({ex.Message})."); }
        _startup.Checked = StartupRegistration.IsEnabled;
        if (setup.NotificationsOn != _settings.Notifications) ToggleNotifications();
        lines.Add(_settings.Notifications ? "Notificaciones encendidas." : "Notificaciones apagadas.");
        if (lines.Any(l => l.Contains("hook")))
            lines.Add("Los avisos de permisos funcionan en las sesiones de Claude Code que se abran desde ahora.");
        MessageBox.Show(string.Join(Environment.NewLine, lines), "Uso de Claude", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void RemoveSetup()
    {
        var answer = MessageBox.Show(
            "Se quitará el hook de avisos de permisos de cada settings.json (con respaldo) y el inicio con Windows. "
            + "La app sigue funcionando hasta que la cierre. ¿Continuar?",
            "Uso de Claude", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        var lines = new List<string>();
        foreach (var dir in SetupDirs())
        {
            var state = HookInstaller.Status(dir, ExePath);
            if (state is not (HookInstaller.State.Installed or HookInstaller.State.Outdated)) continue;
            var name = dir.Replace(Account.Home, "~");
            try { HookInstaller.Uninstall(dir); lines.Add($"{name}: hook quitado."); }
            catch (Exception ex) { lines.Add($"{name}: no se pudo quitar ({ex.Message})."); }
        }
        try { StartupRegistration.Set(false); lines.Add("Ya no inicia con Windows."); }
        catch (Exception ex) { lines.Add($"Inicio con Windows: no se pudo quitar ({ex.Message})."); }
        _startup.Checked = StartupRegistration.IsEnabled;
        MessageBox.Show(string.Join(Environment.NewLine, lines), "Uso de Claude", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Quit()
    {
        _timer.Stop();
        _sessionTimer.Stop();
        _toasts.CloseAll();
        _sounds.Dispose();
        _mascot.Dispose();
        foreach (var icon in _mascotIcons.Values) icon.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _form.Dispose();
        _icon?.Dispose();
        ExitThread();
    }
}
