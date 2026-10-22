namespace ClaudeUsageTray;

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _tray = new();
    readonly UsageForm _form = new();
    readonly System.Windows.Forms.Timer _timer = new();
    readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 1500 };
    readonly SessionMonitor _monitor = new();
    readonly ActivityTracker _activity = new();
    readonly RefreshAnimator _refresh = new();
    readonly Dictionary<RefreshFrame, Icon> _refreshIcons = new();
    readonly AppSettings _settings;
    readonly Dictionary<string, UsageSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    readonly ContextMenuStrip _menu = new();
    readonly ContextMenuStrip _packMenu = new();
    readonly ContextMenuStrip _paletteMenu = new();
    readonly ContextMenuStrip _animationMenu = new();
    readonly ContextMenuStrip _mascotMenu = new();
    readonly NoticeDetector _detector = new();
    readonly ToastManager _toasts = new();
    readonly Sounds _sounds = new();
    readonly ChibiDirector _director = new();

    MascotWindow? _mascotWindow;
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
        // La ventana se creó con la paleta por defecto: toma la elegida.
        Theme.SelectedPalette = _settings.Palette;
        _form.ApplyTheme();
        Mascots.Selected = _settings.Mascot;
        _refresh.Style = RefreshSprite.Resolve(_settings.Animation, _settings.Mascot);

        // El menú del ícono queda corto: lo demás está en la vista Ajustes (botón del pie).
        var menu = _menu;
        menu.Items.Add("Abrir", null, (_, _) => ShowForm());
        menu.Items.Add("Actualizar", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Nueva sesión…", null, (_, _) => NewSession(null));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Ajustes…", null, (_, _) => { ShowForm(); _form.ShowSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Salir", null, (_, _) => Quit());
        foreach (var (id, label) in Sounds.Packs)
            _packMenu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => SetSoundPack(id)) { Tag = id });
        foreach (var (id, label, _, _) in Theme.Palettes)
            _paletteMenu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => SetPalette(id)) { Tag = id });
        foreach (var (id, label) in RefreshSprite.Styles)
            _animationMenu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => SetAnimation(id)) { Tag = id });
        foreach (var (id, label) in Mascots.All)
            _mascotMenu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => SetMascot(id)) { Tag = id });
        foreach (var choices in new[] { _packMenu, _paletteMenu, _animationMenu, _mascotMenu })
            choices.Closed += (_, _) => _form.HoldOpen = false;
        ApplyMenus();

        _tray.ContextMenuStrip = menu;
        _tray.Text = "Uso de Claude";
        SetIcon(null, _form.Theme.Tertiary);
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
        _tray.Visible = true;

        _form.RefreshRequested += async () => await RefreshAsync();
        _form.AddRequested += AddAccount;
        _form.RemoveRequested += RemoveAccount;
        _form.RenameRequested += RenameAccount;
        _form.NewSessionRequested += NewSession;
        _form.ThemeChanged += () => { ApplyMenus(); ClearRefreshIcons(); UpdateTray(); };
        _form.SetPinned(_settings.PinnedViews);
        _form.PinnedChanged += views =>
        {
            _settings.PinnedViews = views.ToList();
            SettingsStore.Save(_settings);
        };
        _form.SettingRequested += OnSetting;
        PushSettings();
        _form.SelectedChanged += account =>
        {
            _settings.Selected = account.ConfigDir;
            SettingsStore.Save(_settings);
            UpdateTray();
        };

        // Las sesiones se leen cada 1,5 s con la ventana abierta y cada 3 s cerrada, solo si hay
        // notificaciones encendidas.
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
        // La animación (la mascota u otro estilo) ocupa el ícono y la hora del pie mientras se
        // actualiza; el estado nuevo se muestra cuando termina.
        _refresh.FrameChanged += ShowRefreshFrame;
        _refresh.Completed += () =>
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
            UpdateMascotWindow();
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
            _refresh.Finish(); // al terminar la animación llama a Render
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
        if (_scanning || (!_form.Visible && !_settings.Notifications && _mascotWindow is null)) return;
        _scanning = true;
        try
        {
            var accounts = _settings.Accounts.ToList();
            var sessions = await Task.Run(() => _monitor.Scan(accounts));
            _form.SetSessions(sessions);
            _director.SetSessions(sessions);
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
        _director.Notice(notice.Kind);
        if (_settings.Sound) _sounds.Play(notice.Kind, _settings.SoundPack);
    }

    /// <summary>Lo que la vista Ajustes muestra: se manda de nuevo cada vez que algo cambia.</summary>
    void PushSettings()
    {
        var pack = Sounds.Packs.FirstOrDefault(p => p.Id == _settings.SoundPack).Label ?? _settings.SoundPack;
        _form.SetSettingsState(new UsageForm.SettingsState(
            _settings.Notifications, _settings.Sound, pack, Theme.PaletteLabel(_settings.Palette),
            RefreshSprite.Label(_settings.Animation), Mascots.Label(_settings.Mascot),
            Mascots.IsChibi ? _settings.DesktopMascot : null, StartupRegistration.IsEnabled, _settings.Accounts.Count));
    }

    void OnSetting(string action)
    {
        switch (action)
        {
            case "notify": ToggleNotifications(); break;
            case "sound": ToggleSound(); break;
            case "pack": ShowChoiceMenu(_packMenu, _settings.SoundPack); break;
            case "palette": ShowChoiceMenu(_paletteMenu, _settings.Palette); break;
            case "anim": ShowChoiceMenu(_animationMenu, _settings.Animation); break;
            case "mascot": ShowChoiceMenu(_mascotMenu, _settings.Mascot); break;
            case "desk": ToggleDesktopMascot(); break;
            case "startup": ToggleStartup(); break;
            case "sample": ShowNextSample(); break;
            case "sounds": OpenSoundsFolder(); break;
            case "add": AddAccount(); break;
            case "setup": ShowSetup(); break;
            case "diag": CopyDiagnostics(); break;
            case "unsetup":
                _form.HoldOpen = true;
                try { RemoveSetup(); }
                finally { _form.HoldOpen = false; }
                break;
        }
        PushSettings();
    }

    /// <summary>La lista de una opción de Ajustes (estilo de sonido, color, animación), con la elegida marcada.</summary>
    void ShowChoiceMenu(ContextMenuStrip choices, string selected)
    {
        foreach (ToolStripMenuItem item in choices.Items) item.Checked = (string?)item.Tag == selected;
        _form.HoldOpen = true;
        choices.Show(Cursor.Position);
    }

    /// <summary>Colores del tema en todos los menús; el de la paleta, con un punto de cada color en el modo actual.</summary>
    void ApplyMenus()
    {
        var theme = _form.Theme;
        foreach (var menu in new[] { _menu, _packMenu, _paletteMenu, _animationMenu, _mascotMenu }) Menus.Apply(menu, theme);
        _paletteMenu.ShowImageMargin = true;
        foreach (ToolStripMenuItem item in _paletteMenu.Items)
        {
            var palette = Theme.Palettes.First(p => p.Id == (string?)item.Tag);
            item.Image?.Dispose();
            item.Image = Draw.Swatch(theme.Dark ? palette.Dark : palette.Light, 16);
        }
    }

    void SetPalette(string palette)
    {
        _settings.Palette = palette;
        SettingsStore.Save(_settings);
        Theme.SelectedPalette = palette;
        _form.ApplyTheme(); // repinta la ventana; ThemeChanged rehace los menús, el ícono y la animación
        PushSettings();
    }

    void SetAnimation(string style)
    {
        _settings.Animation = style;
        SettingsStore.Save(_settings);
        _refresh.Style = RefreshSprite.Resolve(style, _settings.Mascot);
        ClearRefreshIcons();
        PushSettings();
        _ = RefreshAsync(); // muestra de la animación elegida, como la del estilo de sonido
    }

    void SetMascot(string mascot)
    {
        _settings.Mascot = mascot;
        SettingsStore.Save(_settings);
        Mascots.Selected = mascot; // los avisos la leen al dibujarse
        _refresh.Style = RefreshSprite.Resolve(_settings.Animation, mascot);
        ClearRefreshIcons();
        PushSettings();
        UpdateMascotWindow();
        _ = RefreshAsync(); // muestra de la mascota en la animación al actualizar
    }

    void ToggleDesktopMascot()
    {
        _settings.DesktopMascot = !_settings.DesktopMascot;
        SettingsStore.Save(_settings);
        UpdateMascotWindow();
    }

    /// <summary>El chibi del escritorio está si se eligió el chibi y no se ocultó.</summary>
    void UpdateMascotWindow()
    {
        var wanted = Mascots.IsChibi && _settings.DesktopMascot;
        if (wanted == _mascotWindow is not null) return;
        if (!wanted)
        {
            _mascotWindow!.Close();
            _mascotWindow.Dispose();
            _mascotWindow = null;
            return;
        }
        Point? saved = _settings.DesktopMascotX is { } x && _settings.DesktopMascotY is { } y ? new Point(x, y) : null;
        _mascotWindow = new MascotWindow(_director, saved);
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

    /// <summary>El cuadro actual de la animación en el ícono y en el pie de la ventana.</summary>
    void ShowRefreshFrame()
    {
        if (_refresh.Frame is { } frame)
        {
            if (!_refreshIcons.TryGetValue(frame, out var icon))
                _refreshIcons[frame] = icon = RefreshArt.Icon(_refresh.Style, frame, _form.Theme.Accent);
            _tray.Icon = icon;
        }
        _form.SetRefreshFrame(_refresh.Frame, _refresh.Style);
    }

    /// <summary>Con otro estilo u otra paleta, los íconos de la animación se vuelven a dibujar.</summary>
    void ClearRefreshIcons()
    {
        var old = _refreshIcons.Values.ToList();
        _refreshIcons.Clear();
        ShowRefreshFrame(); // si está animando, el ícono pasa ya al nuevo antes de soltar el viejo
        foreach (var icon in old) icon.Dispose();
    }

    void ToggleSound()
    {
        _settings.Sound = !_settings.Sound;
        SettingsStore.Save(_settings);
        if (_settings.Sound) _sounds.Play(NoticeKind.Done, _settings.SoundPack, force: true); // para oír cómo suena
    }

    void SetSoundPack(string pack)
    {
        _settings.SoundPack = pack;
        SettingsStore.Save(_settings);
        PushSettings();
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
        PushSettings();
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
        PushSettings(); // la cantidad de cuentas puede haber cambiado
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
        if (_refresh.Running) { } // el ícono es de la animación hasta que termine
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

    /// <summary>Desde una sesión, propone su cuenta y su carpeta; el resto, lo último elegido.</summary>
    void NewSession(SessionInfo? from)
    {
        var initial = _settings.Launch;
        if (from is not null)
            initial = new LaunchOptions
            {
                ConfigDir = from.Account.ConfigDir, Folder = from.Cwd,
                Model = initial.Model, Effort = initial.Effort, Advisor = initial.Advisor,
            };
        var recent = (_lastSessions ?? []).Select(s => s.Cwd).Prepend(initial.Folder ?? "");

        _form.HoldOpen = true;
        try
        {
            using var dialog = new NewSessionForm(_settings.Accounts, initial, recent, _form.Theme);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            _settings.Launch = dialog.Result;
            SettingsStore.Save(_settings);
            Launcher.Start(dialog.Result);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir la sesión: {ex.Message}", "Uso de Claude", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _form.HoldOpen = false;
        }
    }

    void ToggleStartup()
    {
        StartupRegistration.Set(!StartupRegistration.IsEnabled);
        PushSettings();
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
        PushSettings();
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
        PushSettings();
        MessageBox.Show(string.Join(Environment.NewLine, lines), "Uso de Claude", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    void Quit()
    {
        _timer.Stop();
        _sessionTimer.Stop();
        _toasts.CloseAll();
        _mascotWindow?.Dispose();
        _sounds.Dispose();
        _refresh.Dispose();
        foreach (var icon in _refreshIcons.Values) icon.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _form.Dispose();
        _icon?.Dispose();
        ExitThread();
    }
}
