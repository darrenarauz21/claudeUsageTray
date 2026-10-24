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
    readonly ContextMenuStrip _mascotMenu = new();
    readonly NoticeDetector _detector = new();
    readonly ToastManager _toasts = new();
    readonly Sounds _sounds = new();
    readonly ChibiDirector _director = new();

    readonly BubbleForm _bubble = new();
    readonly ChatThread _chat = new();
    MascotWindow? _mascotWindow;
    ChatBubble? _chatBubble;
    CancellationTokenSource? _asking;
    TaskProposal? _pendingTask;
    IslandForm? _island;
    CancellationTokenSource? _islandAsking, _analyzing;
    string? _islandStreaming;
    /// <summary>El modelo de sus sesiones, con nombre: de la última respuesta de la isla o, antes, de la sesión más reciente.</summary>
    string? _defaultModel;
    string? _lastAnalysis, _lastAnalysisFile;
    ApprovalServer? _approvals;
    Hotkeys? _hotkeys;
    /// <summary>La tecla Copilot y la de hablar grabada: la app se las queda (KeyHook).</summary>
    KeyHook? _keyHook;
    VoiceInput? _voice;
    /// <summary>Con qué tecla se empezó a hablar, si no fue la de mantener (para el «envía con…»).</summary>
    string? _voiceTrigger;
    ReadAloud? _readAloud;
    /// <summary>A quién se le habla (se decide al empezar a escuchar), si se usó el micrófono de la isla y si hay una escucha en curso.</summary>
    bool _voiceToIsland, _micFromIsland, _voiceActive;
    CancellationTokenSource? _modelDownload;
    int? _modelPercent;
    string? _modelError;
    /// <summary>La descarga de la voz de Claupet (Supertonic), como la del modelo de voz.</summary>
    CancellationTokenSource? _speechDownload;
    int? _speechPercent;
    string? _speechError;
    ActivitySummary? _activitySummary;
    /// <summary>«Siempre en esta sesión»: sesión, herramienta y comando que ya no preguntan.</summary>
    readonly HashSet<string> _alwaysAllow = new();
    LocalTotals? _local;
    DateTime? _updated;
    bool _busy, _scanning, _counting;
    Icon? _icon;
    Action? _pendingApply;
    IReadOnlyList<SessionInfo>? _lastSessions;
    int _sampleIndex, _noticesShown;

    public TrayApp(bool showOnStart, bool sessionsView = false, bool activityView = false, bool settingsView = false, bool demo = false, bool autoSetup = true, bool preview = false, string? demoDrop = null, string? demoVoice = null)
    {
        _form.StayOpen = preview;
        _settings = SettingsStore.Load();
        _director.Settings = _settings;
        if (SettingsStore.DetectNew(_settings)) SettingsStore.Save(_settings);
        _settings.Selected ??= _settings.Accounts.FirstOrDefault()?.ConfigDir;
        Sounds.EnsureDefaults();
        // La ventana se creó con la paleta por defecto: toma la elegida.
        Theme.SelectedPalette = _settings.Palette;
        _form.ApplyTheme();
        Mascots.Choose(_settings, Mascots.ChoiceOf(_settings)); // deja en orden lo guardado por versiones anteriores
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
        foreach (var (id, label) in Mascots.Choices)
            _mascotMenu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => SetMascot(id)) { Tag = id });
        foreach (var choices in new[] { _packMenu, _paletteMenu, _mascotMenu })
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
        if (Enum.TryParse<UsageForm.SettingsTab>(_settings.SettingsTab, out var tab)) _form.SetSettingsTab(tab);
        _form.SettingsTabChanged += t =>
        {
            _settings.SettingsTab = t.ToString();
            SettingsStore.Save(_settings);
        };
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
        _bubble.Clicked += OpenNotice;
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
            if (showOnStart || settingsView) ShowForm();
            if (settingsView) _form.ShowSettings();
            if (demo) ShowSamples();
            UpdateMascotWindow();
            UpdateIsland();
            UpdateHotkeys();
            UpdateVoice();
            UpdateKeyHook();
            if (demoDrop is not null) _ = DemoDropAsync(demoDrop);
            if (demoVoice is not null && _voice is { } voice) _ = Task.Delay(1500).ContinueWith(_ => voice.DemoAsync(demoVoice),
                TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
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
            _activitySummary = summary;
            MaybeShowRecap(false);
        }
        finally
        {
            _counting = false;
        }
    }

    async Task ScanSessionsAsync()
    {
        if (_scanning || (!_form.Visible && !_settings.Notifications && _mascotWindow is null && _island is null)) return;
        _scanning = true;
        try
        {
            var accounts = _settings.Accounts.ToList();
            var sessions = await Task.Run(() => _monitor.Scan(accounts));
            _form.SetSessions(sessions);
            _director.SetSessions(sessions);
            _lastSessions = sessions;
            _island?.SetSessions(sessions);
            if (_defaultModel is null && sessions.Where(s => s.Model is not null).OrderByDescending(s => s.TurnEndedAt ?? s.StatusAt).FirstOrDefault() is { } recent)
            {
                _defaultModel = Format.Model(recent.Model!);
                PushIslandModel();
            }
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
        if (_mascotWindow is { } mascot && _settings.Speak && _chatBubble?.Visible != true) _bubble.Say(notice, mascot.Chibi, mascot.Bounds);
        if (_settings.Sound && _island?.Muted != true) _sounds.Play(notice.Kind, _settings.SoundPack);
    }

    /// <summary>Lo que la vista Ajustes muestra: se manda de nuevo cada vez que algo cambia.</summary>
    void PushSettings()
    {
        var s = _settings;
        _form.SetSettingsState(new UsageForm.SettingsState
        {
            Notifications = s.Notifications,
            Sound = s.Sound,
            SoundPack = Sounds.Packs.FirstOrDefault(p => p.Id == s.SoundPack).Label ?? s.SoundPack,
            Palette = Theme.PaletteLabel(s.Palette),
            Mascot = Mascots.Label(Mascots.ChoiceOf(s)),
            MascotSize = MascotSizes.Label(s.MascotSize),
            DesktopMascot = s.DesktopMascot,
            Speak = s.Speak,
            FlowGestures = s.FlowGestures,
            DanceToMusic = s.DanceToMusic,
            NoticeGestures = s.NoticeGestures,
            GreetOnAppear = s.GreetOnAppear,
            AngryOnClicks = s.AngryOnClicks,
            SleepWithoutSessions = s.SleepWithoutSessions,
            Chat = s.Chat,
            MascotModel = AssistantModels.Label(s.MascotModel),
            AssistantAccount = AssistantAccountLabel(),
            TaskFolder = TaskFolderLabel(),
            AnalyzeDrops = s.AnalyzeDrops,
            Mic = s.Mic,
            MicKey = MicKeys.Label(s.MicKey),
            ChatKey = KeyNames.Label(s.ChatKey),
            CopilotKey = s.CopilotKey,
            VoiceStatus = VoiceStatusText().Status,
            VoiceAction = VoiceStatusText().Action,
            ReadAloud = s.ReadAloud,
            ReadAloudVoice = ClaupetVoices.Label(s.ReadAloudVoice),
            SpeechStatus = SpeechStatusText().Status,
            SpeechAction = SpeechStatusText().Action,
            Island = s.Island,
            IslandModel = AssistantModels.Label(s.IslandModel),
            IslandHover = s.IslandHover,
            IslandWhileBusy = s.IslandWhileBusy,
            WeeklyRecap = s.WeeklyRecap,
            Approvals = ApprovalsText(),
            StartWithWindows = StartupRegistration.IsEnabled,
            Accounts = s.Accounts.Count,
        });
    }

    string AssistantAccountLabel() =>
        _settings.AssistantAccount is { } dir && _settings.Accounts.FirstOrDefault(a => Account.SamePath(a.ConfigDir, dir)) is { } account
            ? account.Label : "La elegida en Uso";

    string TaskFolderLabel()
    {
        var folder = _settings.TaskFolder ?? _settings.Launch.Folder;
        return string.IsNullOrEmpty(folder) ? "Elegir…" : Path.GetFileName(folder.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : folder;
    }

    /// <summary>La fila del modelo de voz en Ajustes: qué dice y qué enlace lleva.</summary>
    (string Status, string Action) VoiceStatusText() =>
        VoiceModel.Ready ? ("Modelo de voz: listo, en este equipo", "")
        : _modelDownload is not null ? ($"Descargando el modelo de voz… {_modelPercent ?? 0} %", "Cancelar")
        : _modelError is not null ? ($"No se pudo descargar ({_modelError})", "Reintentar")
        : ("Modelo de voz (488 MB, una sola vez)", "Descargar");

    /// <summary>Baja el modelo de voz (retoma lo que haya quedado de antes) y muestra el avance en Ajustes.</summary>
    async Task DownloadVoiceModelAsync()
    {
        if (_modelDownload is not null || VoiceModel.Ready) return;
        var cancel = _modelDownload = new CancellationTokenSource();
        _modelError = null;
        _modelPercent = 0;
        PushSettings();
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (_modelDownload != cancel || percent == _modelPercent) return;
            _modelPercent = percent;
            PushSettings();
        });
        try { await VoiceModel.DownloadAsync(progress, cancel.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            _modelError = ex is IOException ? ex.Message.TrimEnd('.') : "el servidor no respondió";
        }
        finally
        {
            _modelDownload = null;
            _modelPercent = null;
            cancel.Dispose();
            PushSettings();
        }
    }

    /// <summary>La fila de la voz de Claupet en Ajustes; vacía si no hace falta (ya está, o se eligió la de Windows).</summary>
    (string Status, string Action) SpeechStatusText() =>
        !ClaupetVoices.IsClaupet(_settings.ReadAloudVoice) || ClaupetVoiceModel.Ready ? ("", "")
        : _speechDownload is not null ? ($"Descargando la voz… {_speechPercent ?? 0} %", "Cancelar")
        : _speechError is not null ? ($"No se pudo descargar ({_speechError})", "Reintentar")
        : ($"Voz de Claupet ({ClaupetVoiceModel.Size / 1_000_000} MB, una sola vez)", "Descargar");

    /// <summary>Baja la voz de Claupet (retoma lo que haya quedado de antes) y muestra el avance en Ajustes. Mientras tanto lee la de Windows.</summary>
    async Task DownloadSpeechAsync()
    {
        if (_speechDownload is not null || ClaupetVoiceModel.Ready) return;
        var cancel = _speechDownload = new CancellationTokenSource();
        _speechError = null;
        _speechPercent = 0;
        PushSettings();
        var progress = new Progress<double>(p =>
        {
            var percent = (int)(p * 100);
            if (_speechDownload != cancel || percent == _speechPercent) return;
            _speechPercent = percent;
            PushSettings();
        });
        try { await ClaupetVoiceModel.DownloadAsync(progress, cancel.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
            _speechError = ex is IOException ? ex.Message.TrimEnd('.') : "el servidor no respondió";
        }
        finally
        {
            _speechDownload = null;
            _speechPercent = null;
            cancel.Dispose();
            PushSettings();
        }
    }

    /// <summary>El hook de permisos y preguntas en la isla: listo si está en todas las carpetas de Claude Code.</summary>
    string ApprovalsText()
    {
        var dirs = SetupDirs().ToList();
        return dirs.Count > 0 && dirs.All(d => HookInstaller.ApprovalsStatus(d, ExePath) == HookInstaller.State.Installed) ? "Listo" : "Configurar…";
    }

    /// <summary>Agrega (o quita) el hook PermissionRequest en cada carpeta de Claude Code, con respaldo, después de preguntar.</summary>
    void SetupApprovals()
    {
        var dirs = SetupDirs().ToList();
        if (dirs.Count == 0) return;
        string Name(string dir) => dir.Replace(Account.Home, "~");
        _form.HoldOpen = true;
        try
        {
            var lines = new List<string>();
            if (dirs.All(d => HookInstaller.ApprovalsStatus(d, ExePath) == HookInstaller.State.Installed))
            {
                if (MessageBox.Show("Los permisos y las preguntas ya se responden en la isla. ¿Quitar ese hook de Claude Code?",
                        "Uso de Claude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var dir in dirs)
                {
                    try { HookInstaller.UninstallApprovals(dir); lines.Add($"{Name(dir)}: quitado."); }
                    catch (Exception ex) { lines.Add($"{Name(dir)}: no se pudo ({ex.Message})."); }
                }
            }
            else
            {
                var list = string.Join(Environment.NewLine, dirs.Select(d => "• " + Name(d)));
                if (MessageBox.Show(
                        "Se agrega el hook PermissionRequest al settings.json de Claude Code (antes se guarda un respaldo) en:"
                        + Environment.NewLine + Environment.NewLine + list + Environment.NewLine + Environment.NewLine
                        + "Así los permisos y las preguntas de Claude Code se responden en la isla. Si la app está cerrada o nadie "
                        + "responde en 110 s, Claude Code pregunta en la terminal como siempre. ¿Continuar?",
                        "Uso de Claude", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                foreach (var dir in dirs)
                {
                    try
                    {
                        var backup = HookInstaller.InstallApprovals(dir, ExePath);
                        lines.Add($"{Name(dir)}: listo{(backup is null ? "" : " (con respaldo)")}.");
                    }
                    catch (Exception ex) { lines.Add($"{Name(dir)}: no se pudo ({ex.Message})."); }
                }
                lines.Add("Funciona en las sesiones de Claude Code que se abran desde ahora.");
            }
            MessageBox.Show(string.Join(Environment.NewLine, lines), "Uso de Claude", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { _form.HoldOpen = false; }
    }

    /// <summary>
    /// Un permiso o una pregunta de Claude Code llegó por el hook: la isla muestra su tarjeta y espera.
    /// Lo que la persona elige vuelve a Claude Code; sin respuesta en 105 s, la tarjeta se va y pregunta la terminal.
    /// </summary>
    async Task<string?> OnApprovalRequested(ApprovalRequest request)
    {
        var key = $"{request.SessionId}|{request.Tool}|{request.Target}";
        if (!request.IsQuestion && _alwaysAllow.Contains(key)) return Approvals.Allow();
        if (_island is not { } island) return null;
        var title = (_lastSessions ?? []).FirstOrDefault(s => s.SessionId == request.SessionId)?.DisplayTitle
            ?? Path.GetFileName(request.Cwd.TrimEnd('\\', '/'));
        if (_mascotWindow is not null && !_carrying) _director.Gesture(ChibiMove.Think, 3000);
        var asked = island.AskApproval(request, title);
        var finished = await Task.WhenAny(asked, Task.Delay(TimeSpan.FromSeconds(Approvals.DecisionSeconds - 5)));
        if (finished != asked)
        {
            island.DropApproval();
            return null;
        }
        var answer = await asked;
        switch (answer?.Choice)
        {
            case IslandForm.ApprovalChoice.Allow: return Approvals.Allow();
            case IslandForm.ApprovalChoice.Always:
                _alwaysAllow.Add(key);
                return Approvals.Allow();
            case IslandForm.ApprovalChoice.Deny: return Approvals.Deny();
            case IslandForm.ApprovalChoice.Answered when answer.Picks is { } picks: return Approvals.Answer(request, picks);
            default: return null;
        }
    }

    void OnSetting(string action)
    {
        switch (action)
        {
            case "notify": ToggleNotifications(); break;
            case "sound": ToggleSound(); break;
            case "pack": ShowChoiceMenu(_packMenu, _settings.SoundPack); break;
            case "palette": ShowChoiceMenu(_paletteMenu, _settings.Palette); break;
            case "mascot": ShowChoiceMenu(_mascotMenu, Mascots.ChoiceOf(_settings)); break;
            case "size": ShowOptions(MascotSizes.Choices, _settings.MascotSize, SetMascotSize); break;
            case "desk": ToggleDesktopMascot(); break;
            case "speak": ToggleSpeak(); break;
            case "flowgest": Toggle(s => s.FlowGestures = !s.FlowGestures); break;
            case "music": Toggle(s => s.DanceToMusic = !s.DanceToMusic); break;
            case "noticegest": Toggle(s => s.NoticeGestures = !s.NoticeGestures); break;
            case "greet": Toggle(s => s.GreetOnAppear = !s.GreetOnAppear); break;
            case "angry": Toggle(s => s.AngryOnClicks = !s.AngryOnClicks); break;
            case "sleep":
                Toggle(s => s.SleepWithoutSessions = !s.SleepWithoutSessions);
                _ = ScanSessionsAsync(); // para que se duerma o se despierte ya
                break;
            case "chat": Toggle(s => s.Chat = !s.Chat); break;
            case "mmodel": ShowOptions(AssistantModels.Choices, _settings.MascotModel, m => Change(s => s.MascotModel = m)); break;
            case "account": ShowOptions(AssistantAccountChoices(), _settings.AssistantAccount ?? "", a => Change(s => s.AssistantAccount = a.Length == 0 ? null : a)); break;
            case "folder": ShowOptions(TaskFolderChoices(), _settings.TaskFolder ?? "", PickTaskFolder); break;
            case "drops": Toggle(s => s.AnalyzeDrops = !s.AnalyzeDrops); break;
            case "mic":
                Toggle(s => s.Mic = !s.Mic);
                if (_settings.Mic) _ = DownloadVoiceModelAsync(); // se descarga una vez, al encenderlo
                break;
            case "mickey":
                ShowOptions(KeyChoices(MicKeys.Choices, _settings.MicKey, "Grabar otra tecla…"), _settings.MicKey, k =>
                {
                    // Después de que se cierre el menú: la ventanita de grabar es modal.
                    if (k == RecordChoice) _form.BeginInvoke(() => RecordKey(talk: true, combo => Change(s => s.MicKey = combo)));
                    else Change(s => s.MicKey = k);
                });
                break;
            case "chatkey":
                ShowOptions(KeyChoices([(KeyNames.DefaultChat, KeyNames.Label(KeyNames.DefaultChat))], _settings.ChatKey, "Grabar otra combinación…"),
                    _settings.ChatKey, k =>
                    {
                        if (k == RecordChoice) _form.BeginInvoke(() => RecordKey(talk: false, SetChatKey));
                        else SetChatKey(k);
                    });
                break;
            case "copilot": Toggle(s => s.CopilotKey = !s.CopilotKey); break;
            case "voice":
                if (_modelDownload is { } download) download.Cancel();
                else _ = DownloadVoiceModelAsync();
                break;
            case "readaloud":
                Toggle(s => s.ReadAloud = !s.ReadAloud);
                if (!_settings.ReadAloud) _readAloud?.Stop();
                else if (ClaupetVoices.IsClaupet(_settings.ReadAloudVoice)) _ = DownloadSpeechAsync(); // se descarga una vez
                break;
            case "readvoice":
                ShowOptions(ClaupetVoices.Choices, _settings.ReadAloudVoice, v =>
                {
                    Change(s => s.ReadAloudVoice = v);
                    if (ClaupetVoices.IsClaupet(v)) _ = DownloadSpeechAsync();
                });
                break;
            case "speech":
                if (_speechDownload is { } speech) speech.Cancel();
                else _ = DownloadSpeechAsync();
                break;
            case "island": Toggle(s => s.Island = !s.Island); break;
            case "imodel": ShowOptions(AssistantModels.IslandChoices, _settings.IslandModel ?? "", m => Change(s => s.IslandModel = m.Length == 0 ? null : m)); break;
            case "hover": Toggle(s => s.IslandHover = !s.IslandHover); break;
            case "busy": Toggle(s => s.IslandWhileBusy = !s.IslandWhileBusy); break;
            case "recap": Toggle(s => s.WeeklyRecap = !s.WeeklyRecap); break;
            case "approvals": SetupApprovals(); break;
            case "shortcuts": ShowShortcuts(); break;
            case "recapnow": MaybeShowRecap(true); break;
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

    /// <summary>Cambia un ajuste, lo guarda y avisa a quien dependa de él.</summary>
    void Change(Action<AppSettings> change)
    {
        change(_settings);
        SettingsStore.Save(_settings);
        SettingsChanged();
        PushSettings();
    }

    void Toggle(Action<AppSettings> flip) => Change(flip);

    /// <summary>Lo que depende de los ajustes del asistente, la isla y la mascota se pone al día.</summary>
    void SettingsChanged()
    {
        UpdateIsland();
        UpdateHotkeys();
        UpdateVoice();
        UpdateKeyHook();
        Music.Watch(_settings.DesktopMascot && _settings.DanceToMusic);
    }

    // ── Voz ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>El micrófono: con «Micrófono» encendido y alguien a quien hablarle (el chat de la mascota o la isla).</summary>
    void UpdateVoice()
    {
        if (!_settings.Mic || !(_settings.Chat || _settings.Island))
        {
            _voice?.Dispose();
            _voice = null;
            if (_voiceActive) OnVoiceCanceled(); // se apagó mientras escuchaba
        }
        else if (_voice is null)
        {
            var voice = _voice = new VoiceInput();
            voice.Started += OnVoiceStarted;
            voice.Level += level =>
            {
                if (_voiceToIsland) _island?.SetVoiceLevel(level);
                else _chatBubble?.SetLevel(level);
            };
            voice.Partial += text =>
            {
                if (_voiceToIsland) _island?.SetHeard(text);
                else _chatBubble?.SetHeard(text);
            };
            voice.Converting += () =>
            {
                if (_voiceToIsland) _island?.ConvertingVoice();
                else _chatBubble?.Converting();
            };
            voice.Heard += OnVoiceHeard;
            voice.Canceled += OnVoiceCanceled;
            voice.NeedsModel += () => VoiceNote(_modelDownload is not null
                ? $"El modelo de voz se está descargando ({_modelPercent ?? 0} %)."
                : "Falta el modelo de voz: se descarga en Ajustes › Asistente.");
            voice.Failed += VoiceNote;
        }
        // Una grabada (F13, Win+H…) la vigila KeyHook; la de mantener, VoiceInput (y si no se entiende, Ctrl derecho).
        if (_voice is not null)
            _voice.Key = MicKeys.IsHold(_settings.MicKey) && Enum.TryParse<Keys>(_settings.MicKey, out var key) ? key
                : KeyCombo.TryParse(_settings.MicKey, out _) ? Keys.None : Keys.RControlKey;
        if (_island is { } island) island.MicEnabled = _voice is not null;
    }

    /// <summary>
    /// Las teclas que la app se queda: la Copilot (con el chat o la isla) y la de hablar, si se grabó una
    /// combinación. La Copilot habla si hay micrófono; si no, abre el chat.
    /// </summary>
    void UpdateKeyHook()
    {
        var combos = new List<KeyCombo>();
        if (_settings.CopilotKey && (_settings.Chat || _settings.Island)) combos.Add(KeyCombo.Copilot);
        if (_voice is not null && !MicKeys.IsHold(_settings.MicKey) && KeyCombo.TryParse(_settings.MicKey, out var talk) && !combos.Contains(talk))
            combos.Add(talk);
        if (combos.Count == 0)
        {
            _keyHook?.Dispose();
            _keyHook = null;
            return;
        }
        if (_keyHook is null)
        {
            _keyHook = new KeyHook();
            _keyHook.Changed += OnHookKey;
        }
        _keyHook.Combos = combos;
    }

    void OnHookKey(KeyCombo combo, bool down)
    {
        if (_voice is { } voice)
        {
            _voiceTrigger = combo == KeyCombo.Copilot ? "Copilot" : combo.Label;
            if (down) voice.ComboDown();
            else voice.ComboUp();
        }
        else if (down) FocusChat(); // la Copilot, sin micrófono
    }

    /// <summary>A quién se le habla: a la isla si está abierta, si se usó su micrófono o si no hay mascota con chat; si no, a la mascota.</summary>
    bool VoiceToIsland() =>
        _island is { } island && (_micFromIsland || island.IsOpen || !(_settings.Chat && _mascotWindow is not null));

    void OnVoiceStarted()
    {
        if (_voice is not { } voice) return;
        _readAloud?.Stop();
        _voiceToIsland = VoiceToIsland();
        _micFromIsland = false;
        var hint = voice.Hint(_voiceTrigger ?? MicKeys.Label(_settings.MicKey));
        _voiceTrigger = null;
        if (_voiceToIsland && _island is { } island)
        {
            island.Listen(hint, island.IsOpen ? null : Screen.FromPoint(Cursor.Position));
            if (_mascotWindow is not null && !_carrying) _director.SetMood(ChibiMove.Listen); // mira a la isla mientras le hablan
        }
        else if (_settings.Chat && _mascotWindow is { } mascot)
        {
            _bubble.Hide();
            _asking?.Cancel();
            _pendingTask = null;
            ChatBubbleFor().Listen(mascot.Bounds, hint);
            _director.SetMood(ChibiMove.Hear);
        }
        else
        {
            voice.Cancel();
            return;
        }
        _voiceActive = true;
    }

    void OnVoiceHeard(string text)
    {
        _voiceActive = false;
        if (_voiceToIsland)
        {
            if (_island is not { } island) return;
            if (_mascotWindow is not null && !_carrying) _director.SetMood(null);
            island.StopListening(text.Length == 0 ? "No se entendió nada. Pruebe otra vez, más cerca del micrófono." : null);
            if (text.Length > 0) _ = AskIslandAsync(text);
            return;
        }
        if (_mascotWindow is not { } mascot || _chatBubble is not { } bubble) return;
        if (text.Length > 0)
        {
            _ = AskMascotAsync(text);
            return;
        }
        bubble.Say(mascot.Bounds, "No te entendí… ¿me lo repites? (._.)");
        _director.SetMood(null);
    }

    void OnVoiceCanceled()
    {
        _voiceActive = false;
        if (_voiceToIsland)
        {
            _island?.StopListening();
            if (_mascotWindow is not null && !_carrying) _director.SetMood(null);
        }
        else if (_chatBubble is { Current: ChatBubble.Mode.Listening } bubble) bubble.Close(dismissed: true);
    }

    /// <summary>Un aviso corto de la voz (falta el modelo, no hay micrófono…) donde se le iba a hablar.</summary>
    void VoiceNote(string message)
    {
        var toIsland = _voiceActive ? _voiceToIsland : VoiceToIsland();
        _voiceActive = _micFromIsland = false;
        _voiceTrigger = null;
        if (toIsland && _island is { } island)
        {
            island.ShowPage(IslandForm.Page.Chat, island.IsOpen ? null : Screen.FromPoint(Cursor.Position));
            island.StopListening(message);
        }
        else if (_mascotWindow is { } mascot)
        {
            ChatBubbleFor().Say(mascot.Bounds, message + " (._.)");
            _director.SetMood(null);
        }
    }

    /// <summary>Lee la respuesta en voz alta, si se pidió (Ajustes › Asistente › Voz).</summary>
    void ReadAloudIfOn(string text)
    {
        if (_settings.ReadAloud && text.Length > 0) (_readAloud ??= new ReadAloud()).Say(text, _settings.ReadAloudVoice);
    }

    /// <summary>
    /// Lee lo que se mostró: si es la respuesta de Claude, con sus risas o suspiros (Spoken); si no (la
    /// acción falló), tal cual.
    /// </summary>
    void ReadAloudIfOn(AssistantResult result, string shown) =>
        ReadAloudIfOn(shown == result.Text && result.Spoken is { } spoken ? spoken : shown);

    /// <summary>La respuesta se leerá con la voz de Claupet: Claude puede ponerle risas o suspiros.</summary>
    bool Expressive => _settings.ReadAloud && ClaupetVoices.IsClaupet(_settings.ReadAloudVoice);

    /// <summary>
    /// Los lunes (o la primera vez que la app corre en la semana), el resumen de la semana pasada en la
    /// isla, una sola vez. now: mostrarlo ya (desde Ajustes), aunque ya se haya visto.
    /// </summary>
    void MaybeShowRecap(bool now)
    {
        if (_island is not { } island || _activitySummary is not { } summary) return;
        var thisMonday = WeekRecap.MondayOf(DateOnly.FromDateTime(DateTime.Today));
        if (!now && (!_settings.WeeklyRecap || _settings.RecapShown == thisMonday)) return;
        var recap = WeekRecap.For(summary, thisMonday.AddDays(-7)) ?? (now ? WeekRecap.For(summary, thisMonday) : null);
        if (recap is null) return;
        if (!now)
        {
            _settings.RecapShown = thisMonday;
            SettingsStore.Save(_settings);
        }
        island.ShowWeek(recap);
    }

    /// <summary>Los atajos de teclado: con la isla o el chat encendidos.</summary>
    void UpdateHotkeys()
    {
        if (!_settings.Island && !_settings.Chat)
        {
            _hotkeys?.Dispose();
            _hotkeys = null;
            return;
        }
        if (_hotkeys is not null) return;
        _hotkeys = new Hotkeys();
        _hotkeys.Add(KeyCombo.TryParse(_settings.ChatKey, out var chat) ? chat : new KeyCombo(Keys.Space, Ctrl: true, Alt: true),
            "Abrir el chat", FocusChat);
        _hotkeys.Add(Keys.A, "Ir al permiso o la pregunta pendiente", () => _island?.ShowPending());
        _hotkeys.Add(Keys.T, "Traer la terminal de la sesión", () =>
        {
            var session = (_lastSessions ?? []).Where(s => s.State is SessionState.Waiting or SessionState.Working).FirstOrDefault()
                ?? (_lastSessions ?? []).OrderByDescending(s => s.TurnEndedAt ?? s.StatusAt).FirstOrDefault();
            if (session is not null) Terminals.Focus(session.Pid);
        });
        _hotkeys.Add(Keys.N, "Abrir o plegar la isla", () => _island?.ToggleOpen());
        _hotkeys.Add(Keys.S, "Silenciar o volver a sonar", () => _island?.ToggleMute());
    }

    /// <summary>Otro atajo del chat: los atajos se vuelven a registrar.</summary>
    void SetChatKey(string combo)
    {
        _hotkeys?.Dispose();
        _hotkeys = null;
        Change(s => s.ChatKey = combo);
    }

    /// <summary>El atajo del chat (y la tecla Copilot sin micrófono): el campo de la isla o la nubecita de la mascota.</summary>
    void FocusChat()
    {
        // Para escribir hay que estar al frente, y Windows no siempre deja a una app de fondo.
        if (((Control?)_island ?? _mascotWindow) is { } window) Foreground.Take(window.Handle);
        if (_island is { } island) island.FocusChat();
        else if (_settings.Chat && _mascotWindow is not null) OpenChat();
    }

    /// <summary>«Grabar…»: espera la tecla o la combinación y, si se eligió una, la guarda.</summary>
    void RecordKey(bool talk, Action<string> save)
    {
        _form.HoldOpen = true;
        try
        {
            if (KeyRecorder.Record(_form.Theme, talk, talk ? "Tecla para hablar" : "Atajo del chat") is { } combo) save(combo);
        }
        finally { _form.HoldOpen = false; }
    }

    /// <summary>Las opciones de una tecla: las de siempre, la grabada (si es otra) y «Grabar…».</summary>
    static IEnumerable<(string Id, string Label)> KeyChoices(IEnumerable<(string Id, string Label)> usual, string current, string record)
    {
        var list = usual.ToList();
        if (!list.Any(c => c.Id == current) && current.Length > 0) list.Add((current, KeyNames.Label(current)));
        list.Add((RecordChoice, record));
        return list;
    }

    const string RecordChoice = "•grabar";

    void ShowShortcuts()
    {
        var lines = (_hotkeys?.List ?? []).Select(s => $"{s.Keys}: {s.What}{(s.Registered ? "" : " (en uso por otra app)")}").ToList();
        if (_settings.Mic)
            lines.Add(MicKeys.IsHold(_settings.MicKey)
                ? $"Mantener {MicKeys.Label(_settings.MicKey)}: hablarle a la mascota o a la isla"
                : $"{MicKeys.Label(_settings.MicKey)}: hablarle (un toque, o mantenerla)");
        if (_keyHook?.Combos.Contains(KeyCombo.Copilot) == true && !(_settings.Mic && _settings.MicKey == KeyNames.Copilot))
            lines.Add(_voice is not null ? "Tecla Copilot: hablarle (un toque, o mantenerla)" : "Tecla Copilot: abrir el chat");
        if (lines.Count == 0) lines.Add("Los atajos funcionan con la isla o el chat encendidos.");
        _form.HoldOpen = true;
        try { MessageBox.Show(string.Join(Environment.NewLine, lines), "Atajos de teclado", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { _form.HoldOpen = false; }
    }

    // ── La isla ─────────────────────────────────────────────────────────────────────────────

    /// <summary>La isla está si se pidió en Ajustes; se le pasan el acento, la mascota, el modelo, el uso y las sesiones.</summary>
    void UpdateIsland()
    {
        if (!_settings.Island)
        {
            _approvals?.Dispose();
            _approvals = null;
            if (_island is { } old)
            {
                _island = null;
                old.DropApproval();
                old.Hide();
                old.Dispose();
            }
            return;
        }
        _approvals ??= new ApprovalServer(OnApprovalRequested);
        if (_island is null)
        {
            var island = _island = new IslandForm();
            island.SessionClicked += s => { _form.FocusSession(s.SessionId); ShowForm(); };
            island.TerminalClicked += s => Terminals.Focus(s.Pid);
            island.NewSessionClicked += () => NewSession(null);
            island.SettingsClicked += () => { ShowForm(); _form.ShowSettings(); };
            island.ModelClicked += () => ShowOptions(AssistantModels.IslandChoices, _settings.IslandModel ?? "", m =>
            {
                Change(s => s.IslandModel = m.Length == 0 ? null : m);
                PushIslandModel();
            });
            island.NewChatClicked += () =>
            {
                _islandAsking?.Cancel();
                _chat.Clear();
                PushIslandChat(false);
            };
            island.Asked += text => _ = AskIslandAsync(text);
            island.FilesDropped += files => _ = AnalyzeAsync(files[0]);
            island.CancelClicked += () =>
            {
                _islandAsking?.Cancel();
                _analyzing?.Cancel();
            };
            island.ResultAction += OnResultAction;
            island.OpenFileClicked += OpenInEditor;
            island.MicClicked += () =>
            {
                _micFromIsland = !(_voice?.Listening ?? false);
                _voice?.Toggle();
            };
            island.MicEnabled = _voice is not null;
            _ = ScanSessionsAsync();
        }
        _island.WhileBusy = _settings.IslandWhileBusy;
        _island.OpenOnHover = _settings.IslandHover;
        _island.SetAccent(Theme.ForDark(true).Accent);
        _island.SetMascot(Mascots.IsChibi);
        PushIslandModel();
        PushIslandUsage();
        PushIslandChat(_islandAsking is { IsCancellationRequested: false } && _islandStreaming is not null);
        if (_lastSessions is { } sessions) _island.SetSessions(sessions);
    }

    void PushIslandModel()
    {
        var label = _settings.IslandModel is { } model
            ? AssistantModels.Label(model)
            : _defaultModel ?? AssistantModels.Label(null);
        var account = AssistantConfigDir is { } dir ? _settings.Accounts.FirstOrDefault(a => Account.SamePath(a.ConfigDir, dir))?.Label ?? "" : "";
        _island?.SetModel(label, account);
    }

    void PushIslandUsage()
    {
        if (_island is null) return;
        var selected = _settings.Accounts.FirstOrDefault(a => _settings.Selected is not null && Account.SamePath(a.ConfigDir, _settings.Selected))
            ?? _settings.Accounts.FirstOrDefault();
        UsageSnapshot? snap = null;
        if (selected is not null) _snapshots.TryGetValue(selected.ConfigDir, out snap);
        _island.SetUsage(snap?.Session?.Percent, snap?.Weekly?.Percent);
    }

    void PushIslandChat(bool asking) => _island?.SetChat(_chat.Turns.ToList(), _islandStreaming, asking);

    /// <summary>Le pregunta a Claude desde la isla, con el modelo de sus sesiones (o el elegido).</summary>
    async Task AskIslandAsync(string text)
    {
        _islandAsking?.Cancel();
        var cancel = _islandAsking = new CancellationTokenSource();
        var history = _chat.Turns.ToList();
        _chat.Add(new ChatTurn(true, text));
        _islandStreaming = "";
        PushIslandChat(true);
        var result = await Assistant.AskAsync(new AssistantRequest
        {
            Prompt = text,
            Voice = AssistantVoice.Island,
            Model = _settings.IslandModel,
            ConfigDir = AssistantConfigDir,
            Context = AssistantContext(),
            History = history,
            Chibi = Mascots.IsChibi,
            Expressive = Expressive,
        }, piece =>
        {
            if (cancel.IsCancellationRequested) return;
            _islandStreaming += piece;
            PushIslandChat(true);
        }, cancel: cancel.Token);
        _islandStreaming = null;
        if (cancel.IsCancellationRequested)
        {
            PushIslandChat(false);
            return;
        }
        if (result.Model is { } used && _settings.IslandModel is null)
        {
            _defaultModel = Format.Model(used);
            PushIslandModel();
        }
        if (result.Failed) _chat.Add(new ChatTurn(false, $"{result.Error} (._.)"));
        else if (result.Task is { } task)
        {
            _chat.Add(new ChatTurn(false, result.Text.Length > 0 ? result.Text : "Eso es una tarea para Claude Code."));
            OfferTask(task);
        }
        else
        {
            var answer = await RunActionAsync(result);
            if (cancel.IsCancellationRequested) return;
            _chat.Add(new ChatTurn(false, answer));
            ReadAloudIfOn(result, answer);
        }
        PushIslandChat(false);
    }

    /// <summary>
    /// Si la respuesta pide una acción en el equipo, la hace: devuelve lo que dijo Claude o, si la
    /// acción falló, lo que pasó.
    /// </summary>
    async Task<string> RunActionAsync(AssistantResult result)
    {
        if (result.Action is not { } action) return result.Text;
        var problem = await SystemActions.RunAsync(action, SetPalette);
        return problem is null ? (result.Text.Length > 0 ? result.Text : "¡Listo! (^_^)") : $"{problem} (._.)";
    }

    /// <summary>El nombre de la carpeta donde se abriría una tarea, para la nubecita.</summary>
    static string FolderLabel(string? folder) => folder is null ? "una carpeta" : Path.GetFileName(folder.TrimEnd('\\', '/'));

    /// <summary>Una tarea pedida en la isla (o un archivo que hay que encargar): la mascota la ofrece en su nubecita; sin mascota, se pregunta aquí.</summary>
    void OfferTask(TaskProposal task)
    {
        if (_mascotWindow is { } mascot)
        {
            _pendingTask = task;
            ChatBubbleFor().Say(mascot.Bounds, "");
            _chatBubble!.Finish("", island: false, task.Prompt, FolderLabel(task.Folder ?? TaskFolderPath));
            _director.SetMood(ChibiMove.Think);
            return;
        }
        var folder = task.Folder ?? TaskFolderPath;
        var where = folder is null ? "una carpeta que elija" : $"«{FolderLabel(folder)}»";
        if (MessageBox.Show($"¿Abro Claude Code en {where} con esta tarea?\n\n«{task.Prompt}»", "Uso de Claude",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (folder is null)
        {
            using var dialog = new FolderBrowserDialog { Description = "Carpeta donde se abre la sesión.", UseDescriptionForTitle = true };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            folder = dialog.SelectedPath;
            Change(s => s.TaskFolder = folder);
        }
        try
        {
            Launcher.Start(new LaunchOptions
            {
                ConfigDir = AssistantConfigDir, Folder = folder, Prompt = task.Prompt,
                Model = _settings.Launch.Model, Effort = _settings.Launch.Effort, Advisor = _settings.Launch.Advisor,
            });
        }
        catch (Exception ex) { MessageBox.Show($"No se pudo abrir la sesión: {ex.Message}", "Uso de Claude"); }
    }

    /// <summary>
    /// Analiza un archivo (o una carpeta) con el modelo de la isla: la isla muestra los pasos reales y el
    /// resumen mientras se escribe; la mascota cuenta lo esencial en su nubecita.
    /// </summary>
    async Task AnalyzeAsync(string file)
    {
        _analyzing?.Cancel();
        var cancel = _analyzing = new CancellationTokenSource();
        var island = _island;
        island?.BeginAnalysis(file);
        if (!_carrying && _mascotWindow is not null) _director.SetMood(ChibiMove.Listen); // soltado en la isla: la mascota mira mientras lee
        var result = await Assistant.AskAsync(new AssistantRequest
        {
            Prompt = "",
            Voice = AssistantVoice.Island,
            Model = _settings.IslandModel,
            ConfigDir = AssistantConfigDir,
            File = file,
            Chibi = Mascots.IsChibi,
        }, piece => island?.AppendAnalysis(piece), stage => island?.SetAnalysisStage(stage), cancel.Token);
        if (cancel.IsCancellationRequested)
        {
            island?.FinishAnalysis("", "Cancelado.", null);
            _director.SetMood(null);
            return;
        }
        island?.FinishAnalysis(result.Text, result.Error, result.Model is { } m ? Format.Model(m) : null);
        if (result.Model is { } used && _settings.IslandModel is null)
        {
            _defaultModel = Format.Model(used);
            PushIslandModel();
        }
        _lastAnalysis = result.Failed ? null : result.Text;
        _lastAnalysisFile = file;
        // No lo pudo abrir (un Excel, por ejemplo): se lo encarga a una sesión en la carpeta del archivo.
        if (result.Task is { } task)
        {
            OfferTask(task);
            return;
        }
        if (_mascotWindow is { } mascot)
        {
            var line = result.Failed ? $"{result.Error} (._.)" : island is null ? result.Text : result.Short ?? result.Text;
            ChatBubbleFor().Say(mascot.Bounds, line);
            _director.SetMood(result.Failed ? null : ChibiMove.Talk);
            if (result.Failed) _director.Gesture(ChibiMove.Dizzy, 1500);
        }
    }

    bool _carrying;

    /// <summary>--soltar: como si soltaran ese archivo sobre la mascota (para probar la coreografía).</summary>
    async Task DemoDropAsync(string file)
    {
        await Task.Delay(2500);
        if (_mascotWindow is null) return;
        _director.SetMood(ChibiMove.Catch);
        await Task.Delay(900);
        await CarryAsync(Path.GetFullPath(file));
    }

    /// <summary>
    /// Le soltaron un archivo encima: lo atrapa en una caja y, con la isla, camina según dónde esté
    /// (ThrowPlan), la lanza a la isla, mira mientras Claude lee, cuenta lo esencial y vuelve a su
    /// lugar. Sin la isla, no camina: piensa con la caja en las manos y cuenta el resumen.
    /// </summary>
    async Task CarryAsync(string file)
    {
        if (_mascotWindow is not { } mascot || _carrying) return;
        if (!_settings.AnalyzeDrops)
        {
            _director.SetMood(null);
            ChatBubbleFor().Say(mascot.Bounds, "Tengo apagado lo de mirar archivos (Ajustes › Asistente). (._.)");
            return;
        }
        _carrying = true;
        mascot.Busy = true;
        _chatBubble?.Close(dismissed: false);
        _bubble.Hide();
        var home = mascot.Location;
        try
        {
            _director.SetMood(ChibiMove.Carry);
            await Task.Delay(650);
            if (_island is not { } island)
            {
                await AnalyzeAsync(file);
                return;
            }

            var screen = Screen.FromControl(mascot);
            var scale = mascot.DeviceDpi / 96f;
            var plan = ThrowPlan.Make(mascot.Bounds, screen.WorkingArea, island.MouthArea(screen), mascot.Unit, scale);
            island.Receive(screen);
            if (plan.WalkMs > 0)
            {
                _director.SetMood(plan.Left ? ChibiMove.CarryLeft : ChibiMove.CarryRight);
                await mascot.WalkToAsync(plan.WalkToX, plan.WalkMs);
            }

            // Toma impulso, salta y la lanza: la caja sale de sus manos cuando suelta.
            island.OpenMouth();
            _director.SetMood(ChibiMove.Throw);
            await Task.Delay(ChibiSprite.ThrowRelease);
            // La caja sale de las manos del chibi (a la altura del pecho) o de arriba de la cabeza de la clásica.
            var (bx, by) = ChibiSprite.BoxInHands;
            var hands = mascot.Chibi
                ? new Point(mascot.Left + (bx + 7) * mascot.Unit, mascot.Top + (by + 2) * mascot.Unit)
                : new Point(mascot.Left + mascot.Width / 2, mascot.Top + 3 * mascot.Unit);
            using (var box = new BoxFlight(mascot.Chibi ? mascot.Unit : Math.Max(1, mascot.Unit * 2 / 3)))
                await box.FlyAsync(hands, island.Mouth, plan.FlightMs, island, scale);
            island.Swallow();
            var analysis = AnalyzeAsync(file);
            await Task.Delay(Math.Max(0, ChibiSprite.Length(ChibiMove.Throw) - ChibiSprite.ThrowRelease - plan.FlightMs));
            if (!analysis.IsCompleted) _director.SetMood(ChibiMove.Listen); // mira hacia arriba mientras lee
            await analysis;

            // Cuenta lo esencial y, cuando termina de decirlo, vuelve caminando a su lugar.
            var waited = 0;
            while (_chatBubble?.Talking == true && waited < 8000) { await Task.Delay(100); waited += 100; }
            if (mascot.IsDisposed) return;
            if (mascot.Left != home.X)
            {
                _director.SetMood(home.X > mascot.Left ? ChibiMove.WalkRight : ChibiMove.WalkLeft);
                await mascot.WalkToAsync(home.X, Math.Max(400, plan.WalkMs));
            }
            _director.SetMood(null);
        }
        finally
        {
            _carrying = false;
            if (!mascot.IsDisposed) mascot.Busy = false;
        }
    }

    /// <summary>Abre un archivo en VS Code; si no está instalado, con el programa que Windows tenga para él.</summary>
    static void OpenInEditor(string file)
    {
        try
        {
            var code = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(d => Path.Combine(d.Trim('"'), "code.cmd")).FirstOrDefault(File.Exists);
            var start = code is not null
                ? new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") { ArgumentList = { "/d", "/c", code, "-g", file }, CreateNoWindow = true, UseShellExecute = false }
                : new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true };
            using var _ = System.Diagnostics.Process.Start(start);
        }
        catch (Exception) { }
    }

    void OnResultAction(string action)
    {
        if (_lastAnalysis is not { } analysis || _lastAnalysisFile is not { } file) return;
        switch (action)
        {
            case "ask":
                _chat.Add(new ChatTurn(false, $"(Resumen de {Path.GetFileName(file)}) {analysis}"));
                PushIslandChat(false);
                _island?.ShowPage(IslandForm.Page.Chat);
                break;
            case "copy":
                try { Clipboard.SetText(analysis); } catch (Exception) { }
                break;
            case "session":
                try
                {
                    var folder = Directory.Exists(file) ? file : Path.GetDirectoryName(file);
                    Launcher.Start(new LaunchOptions
                    {
                        ConfigDir = AssistantConfigDir, Folder = folder,
                        Prompt = $"Revisa «{file}». Ya lo resumí así: {analysis.ReplaceLineEndings(" ")} Dime qué ves y pregúntame qué hacer con él.",
                        Model = _settings.Launch.Model, Effort = _settings.Launch.Effort, Advisor = _settings.Launch.Advisor,
                    });
                }
                catch (Exception ex) { MessageBox.Show($"No se pudo abrir la sesión: {ex.Message}", "Uso de Claude"); }
                break;
        }
    }

    /// <summary>Una lista armada al momento (cuentas, carpetas…), con la elegida marcada; elegir llama a pick con su Id.</summary>
    void ShowOptions(IEnumerable<(string Id, string Label)> options, string selected, Action<string> pick, Action? onClosed = null)
    {
        var menu = new ContextMenuStrip();
        foreach (var (id, label) in options)
        {
            var value = id;
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => pick(value)) { Checked = id == selected });
        }
        Menus.Apply(menu, _form.Theme);
        menu.Closed += (_, _) =>
        {
            _form.HoldOpen = false;
            onClosed?.Invoke();
            _form.BeginInvoke(menu.Dispose); // después del clic, que todavía usa el menú
        };
        _form.HoldOpen = true;
        menu.Show(Cursor.Position);
    }

    // ── Chat con la mascota ─────────────────────────────────────────────────────────────────

    /// <summary>La cuenta que responde: la elegida en Ajustes o, si no, la de Uso.</summary>
    string? AssistantConfigDir => _settings.AssistantAccount ?? _settings.Selected;

    /// <summary>Dónde se abren las tareas: la carpeta elegida o la última de «Nueva sesión».</summary>
    string? TaskFolderPath =>
        new[] { _settings.TaskFolder, _settings.Launch.Folder }.FirstOrDefault(f => !string.IsNullOrEmpty(f) && Directory.Exists(f));

    /// <summary>El campo para escribirle, en la nubecita, con la mascota escuchando.</summary>
    void OpenChat()
    {
        if (_mascotWindow is not { } mascot) return;
        _bubble.Hide();
        _asking?.Cancel(); // si estaba respondiendo, deja de esperar esa respuesta
        _pendingTask = null;
        ChatBubbleFor().AskInput(mascot.Bounds, AssistantModels.Label(_settings.MascotModel));
        _director.SetMood(ChibiMove.Listen);
    }

    ChatBubble ChatBubbleFor()
    {
        if (_chatBubble is { IsDisposed: false } existing) return existing;
        var bubble = new ChatBubble();
        bubble.Sent += text => _ = AskMascotAsync(text);
        bubble.ModelClicked += () =>
        {
            bubble.HoldOpen = true;
            ShowOptions(AssistantModels.Choices, _settings.MascotModel, m =>
            {
                Change(s => s.MascotModel = m);
                bubble.SetModel(AssistantModels.Label(m));
            }, onClosed: () =>
            {
                bubble.HoldOpen = false;
                if (bubble.Visible) bubble.Activate();
            });
        };
        bubble.TaskChosen += OnTaskChosen;
        bubble.IslandClicked += () =>
        {
            if (_island is { } island && _mascotWindow is { } mascot) island.ShowPage(IslandForm.Page.Chat, Screen.FromControl(mascot));
        };
        bubble.DoneTalking += () => _director.SetMood(_pendingTask is null ? null : ChibiMove.Think);
        bubble.Dismissed += () =>
        {
            _asking?.Cancel();
            _pendingTask = null;
            _director.SetMood(null);
        };
        return _chatBubble = bubble;
    }

    /// <summary>Le pregunta a Claude (con el modelo de la mascota) y va mostrando la respuesta en la nubecita.</summary>
    async Task AskMascotAsync(string text)
    {
        if (_mascotWindow is not { } mascot || _chatBubble is not { } bubble) return;
        _asking?.Cancel();
        var cancel = _asking = new CancellationTokenSource();
        bubble.Think(mascot.Bounds, text);
        _director.SetMood(ChibiMove.Think);
        var request = new AssistantRequest
        {
            Prompt = text,
            Voice = AssistantVoice.Mascot,
            Model = _settings.MascotModel,
            ConfigDir = AssistantConfigDir,
            Context = AssistantContext(),
            History = _chat.Turns.ToList(),
            Chibi = Mascots.IsChibi,
            Expressive = Expressive,
        };
        _chat.Add(new ChatTurn(true, text));
        var result = await Assistant.AskAsync(request, piece =>
        {
            if (cancel.IsCancellationRequested) return;
            bubble.Append(piece);
            _director.SetMood(ChibiMove.Talk);
        }, cancel: cancel.Token);
        if (cancel.IsCancellationRequested || bubble.IsDisposed) return;

        if (result.Failed)
        {
            bubble.Finish($"{result.Error} (._.)", island: false, error: true);
            _director.SetMood(null);
            _director.Gesture(ChibiMove.Dizzy, 1500);
            return;
        }
        if (result.Task is { } task)
        {
            _chat.Add(new ChatTurn(false, $"(Propuse abrir una sesión de Claude Code con: {task.Prompt})"));
            _pendingTask = task;
            bubble.Finish("", _settings.Island, task.Prompt, FolderLabel(TaskFolderPath));
            _director.SetMood(ChibiMove.Think);
            return;
        }
        var answer = await RunActionAsync(result);
        if (cancel.IsCancellationRequested || bubble.IsDisposed) return;
        _chat.Add(new ChatTurn(false, answer));
        bubble.Finish(answer, _settings.Island);
        ReadAloudIfOn(result, answer);
    }

    /// <summary>La tarea que propuso: se abre en su carpeta (la del archivo, si vino de uno), en la de las tareas o en otra que se elija.</summary>
    void OnTaskChosen(bool here)
    {
        if (_pendingTask is not { } task || _mascotWindow is not { } mascot || _chatBubble is not { } bubble) return;
        var folder = here ? task.Folder ?? TaskFolderPath : null;
        if (folder is null)
        {
            bubble.HoldOpen = true;
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Carpeta donde se abre la sesión de Claude Code con la tarea.",
                    UseDescriptionForTitle = true,
                    InitialDirectory = TaskFolderPath ?? Account.Home,
                };
                if (dialog.ShowDialog() != DialogResult.OK) return;
                folder = dialog.SelectedPath;
            }
            finally { bubble.HoldOpen = false; }
            Change(s => s.TaskFolder = folder);
        }
        _pendingTask = null;
        try
        {
            Launcher.Start(new LaunchOptions
            {
                ConfigDir = AssistantConfigDir, Folder = folder, Prompt = task.Prompt,
                Model = _settings.Launch.Model, Effort = _settings.Launch.Effort, Advisor = _settings.Launch.Advisor,
            });
            bubble.Say(mascot.Bounds, "¡Va! Abrí la sesión. Bailo mientras trabaja y te aviso al terminar \\(^o^)/");
            _director.SetMood(null);
            _director.Gesture(ChibiMove.Cheer, 1200);
        }
        catch (Exception ex)
        {
            bubble.Finish($"No pude abrir la sesión: {ex.Message} (._.)", island: false, error: true);
            _director.SetMood(null);
        }
    }

    /// <summary>
    /// Lo que la mascota sabe del momento, para responder sobre el uso y las sesiones: la hora, el uso
    /// de cada cuenta y las sesiones abiertas (título, cuenta, carpeta y estado). Sin correos ni tokens.
    /// </summary>
    string AssistantContext()
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Ahora: {DateTime.Now:dddd d 'de' MMMM, HH:mm}.");
        foreach (var account in _settings.Accounts)
        {
            if (!_snapshots.TryGetValue(account.ConfigDir, out var snap) || snap.Limits.Count == 0) continue;
            _profiles.TryGetValue(account.ConfigDir, out var profile);
            var limits = snap.Limits.Select(l => $"{l.Label} al {l.Percent:0} %" + (Format.Reset(l.ResetsAt) is { Length: > 0 } r ? $" ({r.ToLowerInvariant()})" : ""));
            text.AppendLine($"Cuenta «{account.Label}»{(profile?.Plan is { Length: > 0 } plan ? $" (plan {plan})" : "")}: {string.Join("; ", limits)}.");
        }
        var sessions = (_lastSessions ?? []).Where(s => s.State != SessionState.Idle).Take(8).ToList();
        if (sessions.Count == 0) text.AppendLine("No hay sesiones de Claude Code activas.");
        foreach (var s in sessions)
        {
            var state = s.State switch
            {
                SessionState.Working => "trabajando" + (s.Action is { } a ? $" ({a})" : "") + (s.TodoTotal > 0 ? $", {s.TodoDone} de {s.TodoTotal} tareas" : ""),
                SessionState.Waiting => s.AwaitingAnswer ? "esperando su respuesta" : "esperando su permiso",
                SessionState.Failed => "falló: " + (s.ErrorText ?? "error de la API"),
                _ => "terminó " + Format.Ago(s.TurnEndedAt ?? s.StatusAt),
            };
            text.AppendLine($"Sesión «{s.DisplayTitle}» ({s.Account.Label}, carpeta {Path.GetFileName(s.Cwd.TrimEnd('\\', '/'))}): {state}.");
        }
        return text.ToString().TrimEnd();
    }

    void SetMascotSize(string size)
    {
        Change(s => s.MascotSize = size);
        _mascotWindow?.SetSize(MascotSizes.Unit(size, _mascotWindow.Chibi, _mascotWindow.DeviceDpi / 96f));
    }

    IEnumerable<(string Id, string Label)> AssistantAccountChoices() =>
        _settings.Accounts.Select(a => (a.ConfigDir, a.Label)).Prepend(("", "La elegida en Uso"));

    /// <summary>Las carpetas de las sesiones abiertas y la última de «Nueva sesión», y elegir otra.</summary>
    IEnumerable<(string Id, string Label)> TaskFolderChoices()
    {
        var folders = (_lastSessions ?? []).Select(s => s.Cwd)
            .Prepend(_settings.Launch.Folder ?? "").Prepend(_settings.TaskFolder ?? "")
            .Where(f => f.Length > 0 && Directory.Exists(f))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8);
        return folders.Select(f => (f, f.Replace(Account.Home, "~"))).Append(("…", "Elegir otra carpeta…"));
    }

    void PickTaskFolder(string folder)
    {
        if (folder == "…")
        {
            _form.HoldOpen = true;
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Carpeta donde se abren las tareas que le pida a la mascota.",
                    UseDescriptionForTitle = true,
                    InitialDirectory = _settings.TaskFolder ?? _settings.Launch.Folder ?? Account.Home,
                };
                if (dialog.ShowDialog(_form) != DialogResult.OK) return;
                folder = dialog.SelectedPath;
            }
            finally { _form.HoldOpen = false; }
        }
        Change(s => s.TaskFolder = folder);
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
        foreach (var menu in new[] { _menu, _packMenu, _paletteMenu, _mascotMenu }) Menus.Apply(menu, theme);
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
        _island?.SetAccent(Theme.ForDark(true).Accent);
        PushSettings();
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
        ClearRefreshIcons();
        PushSettings();
        UpdateMascotWindow();
        _island?.SetMascot(Mascots.IsChibi);
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
            _chatBubble?.Close(dismissed: true);
            current.Close();
            current.Dispose();
            _mascotWindow = null;
        }
        if (!wanted || _mascotWindow is not null) return;
        Point? saved = _settings.DesktopMascotX is { } x && _settings.DesktopMascotY is { } y ? new Point(x, y) : null;
        _mascotWindow = new MascotWindow(_director, saved, Mascots.IsChibi, () => _form.Theme.Accent, _settings.MascotSize);
        _mascotWindow.LocationChanged += (_, _) =>
        {
            if (_mascotWindow is not { } moved) return;
            if (_bubble.Visible) _bubble.Follow(moved.Bounds);
            if (_chatBubble?.Visible == true) _chatBubble.Follow(moved.Bounds);
        };
        _mascotWindow.Poked += () =>
        {
            if (_settings.Chat && _director.Pokes == 1) OpenChat(); // el clic del saludo abre el chat
        };
        _mascotWindow.FileHover += over =>
        {
            if (_carrying) return;
            _director.SetMood(over ? ChibiMove.Catch : null);
        };
        _mascotWindow.FilesDropped += files => _ = CarryAsync(files[0]);
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
        PushIslandUsage();
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
        _approvals?.Dispose();
        _hotkeys?.Dispose();
        _keyHook?.Dispose();
        _modelDownload?.Cancel();
        _speechDownload?.Cancel();
        _voice?.Dispose();
        _readAloud?.Dispose();
        _island?.Dispose();
        _bubble.Dispose();
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
