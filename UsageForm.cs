using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Ventana emergente sobre la bandeja, dibujada a mano. Dos vistas: «Uso» (límites por cuenta) y
/// «Sesiones» (sesiones abiertas de Claude Code con su estado y progreso).
/// </summary>
sealed class UsageForm : Form
{
    public event Action? RefreshRequested;
    public event Action? AddRequested;
    public event Action<Account>? SelectedChanged;
    public event Action<Account>? RemoveRequested;
    public event Action<Account>? RenameRequested;
    public event Action? ThemeChanged;
    public event Action? NotificationsToggled;
    public event Action<SessionInfo>? NewSessionRequested;

    /// <summary>Mientras hay un diálogo abierto, perder el foco no cierra la ventana.</summary>
    public bool HoldOpen { get; set; }
    public DateTime LastHidden { get; private set; }
    public Theme Theme => _t;

    public bool NotificationsOn
    {
        get => _notificationsOn;
        set { _notificationsOn = value; Invalidate(); }
    }

    enum View { Usage, Sessions, Activity }
    enum HitKind { Refresh, Account, Add, View, Session, IdleToggle, Day, Bell }
    sealed record Hit(string Key, Rectangle Bounds, HitKind Kind, Account? Account = null, SessionInfo? Session = null, View View = View.Usage, DateOnly Day = default);

    /// <summary>Semanas completas del cuadro de actividad, más la actual.</summary>
    const int HeatmapWeeks = 26;

    const string GlyphRefresh = "", GlyphAdd = "", GlyphCheck = "", GlyphClose = "", GlyphBell = "", GlyphBellOff = "", GlyphChevronDown = "", GlyphChevronUp = "";
    // El asterisco animado de Claude Code: va y vuelve.
    static readonly string[] SpinnerFrames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];
    const TextFormatFlags Line = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    readonly float _scale;
    readonly List<Hit> _hits = new();
    readonly Dictionary<string, Rectangle> _rowBounds = new();
    readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    readonly ContextMenuStrip _accountMenu = new();
    readonly ContextMenuStrip _sessionMenu = new();
    readonly System.Windows.Forms.Timer _animation = new() { Interval = 50 };
    readonly Font _title, _body, _bodyStrong, _small, _glyph, _glyphSmall, _spinner;

    Theme _t = Theme.Current;
    View _view = View.Usage;
    IReadOnlyList<Account> _accounts = Array.Empty<Account>();
    IReadOnlyDictionary<string, UsageSnapshot> _snapshots = new Dictionary<string, UsageSnapshot>();
    IReadOnlyDictionary<string, Profile> _profiles = new Dictionary<string, Profile>();
    IReadOnlyList<SessionInfo> _sessions = Array.Empty<SessionInfo>();
    ActivitySummary? _activity;
    MascotFrame? _mascot;
    LocalTotals? _local;
    DateTime? _updated;
    string? _selected;
    bool _busy, _showIdle, _notificationsOn = true;
    string? _hoverKey;
    Account? _menuAccount;
    SessionInfo? _menuSession;

    int S(int value) => (int)Math.Round(value * _scale);

    public UsageForm()
    {
        _scale = DeviceDpi / 96f;
        _title = Fonts.Make("Georgia", 13f, "Cambria");
        _body = Fonts.Make("Segoe UI Variable Text", 9.75f, "Segoe UI");
        _bodyStrong = Fonts.Make("Segoe UI Variable Text Semibold", 9.75f, "Segoe UI Semibold");
        _small = Fonts.Make("Segoe UI Variable Text", 8.75f, "Segoe UI");
        _glyph = Fonts.Make("Segoe Fluent Icons", 10.5f, "Segoe MDL2 Assets");
        _glyphSmall = Fonts.Make("Segoe Fluent Icons", 9f, "Segoe MDL2 Assets");
        _spinner = Fonts.Make("Segoe UI Symbol", 11f, "Segoe UI");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        ClientSize = new Size(S(360), S(300));

        _accountMenu.Items.Add("Renombrar", null, (_, _) => { if (_menuAccount is { } a) RenameRequested?.Invoke(a); });
        _accountMenu.Items.Add("Abrir carpeta", null, (_, _) => { if (_menuAccount is { } a) OpenFolder(a.ConfigDir); });
        _accountMenu.Items.Add(new ToolStripSeparator());
        _accountMenu.Items.Add("Quitar", null, (_, _) => { if (_menuAccount is { } a) RemoveRequested?.Invoke(a); });

        _sessionMenu.Items.Add("Nueva sesión en esta carpeta…", null, (_, _) => { if (_menuSession is { } s) NewSessionRequested?.Invoke(s); });
        _sessionMenu.Items.Add("Abrir carpeta del proyecto", null, (_, _) => { if (_menuSession is { } s) OpenFolder(s.Cwd); });
        _sessionMenu.Items.Add("Copiar ID de la sesión", null, (_, _) => { if (_menuSession is { } s) Clipboard.SetText(s.SessionId); });
        Menus.Apply(_accountMenu, _t);
        Menus.Apply(_sessionMenu, _t);

        _animation.Tick += (_, _) => { if (Visible) Invalidate(); };

        Deactivate += (_, _) => { if (!HoldOpen) Hide(); };
        VisibleChanged += (_, _) =>
        {
            if (!Visible) LastHidden = DateTime.Now;
            UpdateAnimation();
        };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.Apply(Handle, _t);
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        var next = Theme.Current;
        if (next.Dark == _t.Dark) return;
        _t = next;
        if (IsHandleCreated) Dwm.Apply(Handle, _t);
        Menus.Apply(_accountMenu, _t);
        Menus.Apply(_sessionMenu, _t);
        Invalidate();
        ThemeChanged?.Invoke();
    }

    public void ShowNear()
    {
        Reposition();
        Show();
        Activate();
    }

    void Reposition()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Right - Width - S(12), area.Bottom - Height - S(12));
    }

    public void SetBusy(bool busy)
    {
        _busy = busy;
        Invalidate();
    }

    public void Render(IReadOnlyList<Account> accounts, string? selectedDir,
        IReadOnlyDictionary<string, UsageSnapshot> snapshots, IReadOnlyDictionary<string, Profile> profiles,
        LocalTotals? local, DateTime? updated)
    {
        _accounts = accounts.ToList();
        _selected = selectedDir;
        _snapshots = snapshots;
        _profiles = profiles;
        _local = local;
        _updated = updated;
        Relayout();
    }

    public void ShowSessions() => _view = View.Sessions;
    public void ShowActivity() => _view = View.Activity;

    /// <summary>Abre la vista de sesiones con esa sesión desplegada (al hacer clic en un aviso).</summary>
    public void FocusSession(string sessionId)
    {
        _view = View.Sessions;
        _expanded.Add(sessionId);
        if (_sessions.Any(s => s.SessionId == sessionId && s.State == SessionState.Idle)) _showIdle = true;
        Relayout();
    }

    public void FocusAccount(string configDir)
    {
        _view = View.Usage;
        _selected = configDir;
        Relayout();
    }

    public void SetMascot(MascotFrame? frame)
    {
        _mascot = frame;
        if (Visible) Invalidate();
    }

    public void SetActivity(ActivitySummary activity)
    {
        _activity = activity;
        if (_view == View.Activity) Relayout();
    }

    public void SetSessions(IReadOnlyList<SessionInfo> sessions)
    {
        _sessions = sessions;
        _expanded.IntersectWith(sessions.Select(s => s.SessionId));
        UpdateAnimation();
        Relayout();
    }

    /// <summary>La animación solo corre con la ventana abierta y algo en marcha.</summary>
    void UpdateAnimation()
    {
        var active = Visible && _sessions.Any(s => s.State is SessionState.Working or SessionState.Waiting);
        if (active && !_animation.Enabled) _animation.Start();
        else if (!active && _animation.Enabled) _animation.Stop();
    }

    void Relayout()
    {
        using (var g = CreateGraphics())
        {
            var height = Compose(g, draw: false);
            if (height != ClientSize.Height)
            {
                ClientSize = new Size(ClientSize.Width, height);
                if (Visible) Reposition();
            }
        }
        Invalidate();
    }

    Account? SelectedAccount =>
        _accounts.FirstOrDefault(a => _selected is not null && Account.SamePath(a.ConfigDir, _selected)) ?? _accounts.FirstOrDefault();

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Compose(e.Graphics, draw: true);
    }

    /// <summary>Un solo recorrido sirve para medir la altura y para dibujar.</summary>
    int Compose(Graphics g, bool draw)
    {
        _hits.Clear();
        if (draw) g.Clear(_t.Background);
        var y = ComposeHeader(g, draw, S(16));
        y = _view switch
        {
            View.Usage => ComposeUsage(g, draw, y),
            View.Sessions => ComposeSessions(g, draw, y),
            _ => ComposeActivity(g, draw, y),
        };
        return ComposeFooter(g, draw, y);
    }

    int Pad => S(20);
    int Inner => ClientSize.Width - 2 * Pad;

    // ── Encabezado: «Uso» / «Sesiones» y actualizar ─────────────────────────────────────────

    int ComposeHeader(Graphics g, bool draw, int y)
    {
        var height = TextRenderer.MeasureText(g, "Sesiones", _title, Size.Empty, Line).Height;
        var x = Pad;
        foreach (var (view, label) in new[] { (View.Usage, "Uso"), (View.Sessions, "Sesiones"), (View.Activity, "Actividad") })
        {
            var width = TextRenderer.MeasureText(g, label, _title, Size.Empty, Line).Width;
            var badge = view == View.Sessions ? BadgeWidth(g) : 0;
            var key = "view:" + view;
            AddHit(key, new Rectangle(x - S(4), y - S(4), width + badge + S(8), height + S(8)), HitKind.View, view: view);
            if (draw)
            {
                var color = _view == view ? _t.Text : _hoverKey == key ? _t.Secondary : _t.Tertiary;
                Write(g, label, _title, color, new Rectangle(x, y, width, height));
                if (badge > 0) DrawBadge(g, x + width, y, height);
            }
            x += width + badge + S(14);
        }

        var refresh = new Rectangle(ClientSize.Width - Pad - S(24), y + (height - S(30)) / 2, S(30), S(30));
        AddHit("refresh", refresh, HitKind.Refresh);
        var bell = new Rectangle(refresh.X - S(32), refresh.Y, S(30), S(30));
        AddHit("bell", bell, HitKind.Bell);
        if (draw)
        {
            if (_hoverKey == "refresh" && !_busy) Draw.Fill(g, _t.Hover, refresh, S(8));
            Write(g, GlyphRefresh, _glyph, _busy ? _t.Tertiary : _t.Secondary, refresh,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (_hoverKey == "bell") Draw.Fill(g, _t.Hover, bell, S(8));
            Write(g, _notificationsOn ? GlyphBell : GlyphBellOff, _glyph, _notificationsOn ? _t.Secondary : _t.Tertiary, bell,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return y + height + S(16);
    }

    int BadgeWidth(Graphics g)
    {
        var count = _sessions.Count(s => s.State is SessionState.Working or SessionState.Waiting);
        return count == 0 ? 0 : S(8) + S(14) + S(2) + TextRenderer.MeasureText(g, count.ToString(), _small, Size.Empty, Line).Width;
    }

    void DrawBadge(Graphics g, int x, int y, int height)
    {
        var working = _sessions.Count(s => s.State == SessionState.Working);
        var waiting = _sessions.Count(s => s.State == SessionState.Waiting);
        var box = new Rectangle(x + S(8), y, S(14), height);
        if (working > 0) DrawSpinner(g, box);
        else DrawPulse(g, box, _t.Warn);
        var count = (working + waiting).ToString();
        Write(g, count, _small, _t.Tertiary, new Rectangle(box.Right + S(2), y, S(30), height), TextFormatFlags.VerticalCenter);
    }

    // ── Vista «Uso» ─────────────────────────────────────────────────────────────────────────

    int ComposeUsage(Graphics g, bool draw, int y)
    {
        var width = ClientSize.Width;
        var pillHeight = S(30);
        var gap = S(6);
        var x = Pad;
        var selected = SelectedAccount;

        foreach (var account in _accounts)
        {
            _snapshots.TryGetValue(account.ConfigDir, out var snap);
            var percent = snap?.Session is { } s ? $"{s.Percent:0} %" : "";
            var labelWidth = TextRenderer.MeasureText(g, account.Label, _body, Size.Empty, Line).Width;
            var percentWidth = percent.Length == 0 ? 0 : S(6) + TextRenderer.MeasureText(g, percent, _small, Size.Empty, Line).Width;
            var pillWidth = S(12) + labelWidth + percentWidth + S(12);
            if (x + pillWidth > width - Pad && x > Pad) { x = Pad; y += pillHeight + gap; }

            var pill = new Rectangle(x, y, pillWidth, pillHeight);
            var key = "acct:" + account.ConfigDir;
            AddHit(key, pill, HitKind.Account, account);
            if (draw)
            {
                var isSelected = account == selected;
                if (isSelected)
                {
                    Draw.Fill(g, _t.Raised, pill, S(8));
                    Draw.Outline(g, _t.Border, pill, S(8));
                }
                else if (_hoverKey == key)
                {
                    Draw.Fill(g, _t.Hover, pill, S(8));
                }
                Write(g, account.Label, _body, isSelected ? _t.Text : _t.Secondary,
                    new Rectangle(pill.X + S(12), pill.Y, labelWidth, pillHeight), TextFormatFlags.VerticalCenter);
                if (percent.Length > 0)
                    Write(g, percent, _small, snap!.Error is null ? _t.Tertiary : _t.Danger,
                        new Rectangle(pill.X + S(12) + labelWidth + S(6), pill.Y, percentWidth, pillHeight), TextFormatFlags.VerticalCenter);
            }
            x += pillWidth + gap;
        }
        if (x + pillHeight > width - Pad && x > Pad) { x = Pad; y += pillHeight + gap; }
        var add = new Rectangle(x, y, pillHeight, pillHeight);
        AddHit("add", add, HitKind.Add);
        if (draw)
        {
            if (_hoverKey == "add") Draw.Fill(g, _t.Hover, add, S(8));
            Write(g, GlyphAdd, _glyphSmall, _t.Secondary, add, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        y += pillHeight + S(20);

        var bodyHeight = _body.Height;
        var smallHeight = _small.Height;
        if (selected is null)
        {
            if (draw) Write(g, "Agregue una cuenta con +", _body, _t.Secondary, new Rectangle(Pad, y, Inner, bodyHeight));
            return y + bodyHeight + S(20);
        }

        _profiles.TryGetValue(selected.ConfigDir, out var profile);
        _snapshots.TryGetValue(selected.ConfigDir, out var snapshot);

        var plan = profile?.Plan ?? "";
        var planWidth = plan.Length == 0 ? 0 : TextRenderer.MeasureText(g, plan, _small, Size.Empty, Line).Width;
        if (draw)
        {
            Write(g, profile?.Email ?? selected.ConfigDir, _small, _t.Secondary,
                new Rectangle(Pad, y, Inner - planWidth - S(12), smallHeight), TextFormatFlags.EndEllipsis);
            if (plan.Length > 0)
                Write(g, plan, _small, _t.Tertiary, new Rectangle(width - Pad - planWidth, y, planWidth, smallHeight));
        }
        y += smallHeight + S(18);

        if (snapshot is null)
        {
            if (draw) Write(g, "Consultando…", _body, _t.Tertiary, new Rectangle(Pad, y, Inner, bodyHeight));
            return y + bodyHeight + S(20);
        }

        if (snapshot.Error is not null)
        {
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            var h = TextRenderer.MeasureText(g, snapshot.Error, _small, new Size(Inner, 0), flags).Height;
            if (draw) Write(g, snapshot.Error, _small, _t.Danger, new Rectangle(Pad, y, Inner, h), flags);
            y += h + S(16);
        }

        foreach (var limit in snapshot.Limits)
        {
            if (draw)
            {
                Write(g, limit.Label, _body, _t.Text, new Rectangle(Pad, y, Inner - S(60), bodyHeight));
                Write(g, $"{limit.Percent:0} %", _bodyStrong, _t.Text, new Rectangle(width - Pad - S(60), y, S(60), bodyHeight), TextFormatFlags.Right);
            }
            y += bodyHeight + S(8);

            var bar = new RectangleF(Pad, y, Inner, S(6));
            if (draw)
            {
                Draw.Fill(g, _t.Track, bar, bar.Height / 2);
                var fill = (float)(bar.Width * Math.Clamp(limit.Percent, 0, 100) / 100);
                if (fill > 0)
                    Draw.Fill(g, _t.For(limit.Percent, limit.Severity), new RectangleF(bar.X, bar.Y, Math.Max(fill, bar.Height), bar.Height), bar.Height / 2);
            }
            y += S(6) + S(8);

            var reset = Format.Reset(limit.ResetsAt);
            if (draw && reset.Length > 0) Write(g, reset, _small, _t.Tertiary, new Rectangle(Pad, y, Inner, smallHeight));
            y += smallHeight + S(20);
        }
        return y;
    }

    // ── Vista «Sesiones» ────────────────────────────────────────────────────────────────────

    int ComposeSessions(Graphics g, bool draw, int y)
    {
        _rowBounds.Keys.Where(k => !_sessions.Any(s => "sess:" + s.SessionId == k)).ToList().ForEach(k => _rowBounds.Remove(k));

        if (_sessions.Count == 0)
        {
            if (draw) Write(g, "No hay sesiones abiertas.", _body, _t.Tertiary, new Rectangle(Pad, y, Inner, _body.Height));
            return y + _body.Height + S(24);
        }

        var active = _sessions.Where(s => s.State != SessionState.Idle).ToList();
        var idle = _sessions.Where(s => s.State == SessionState.Idle).ToList();

        foreach (var session in active)
            y = ComposeSession(g, draw, session, y);

        if (idle.Count > 0)
        {
            if (active.Count > 0) y += S(2);
            var height = _small.Height + S(12);
            var toggle = new Rectangle(Pad - S(10), y, Inner + S(20), height);
            AddHit("idle", toggle, HitKind.IdleToggle);
            if (draw)
            {
                if (_hoverKey == "idle") Draw.Fill(g, _t.Hover, toggle, S(8));
                Write(g, $"Inactivas ({idle.Count})", _small, _t.Tertiary, new Rectangle(Pad, y, Inner, height), TextFormatFlags.VerticalCenter);
                Write(g, _showIdle ? GlyphChevronUp : GlyphChevronDown, _glyphSmall, _t.Tertiary,
                    new Rectangle(Pad + Inner - S(16), y, S(16), height), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            y += height + S(8);
            if (_showIdle)
                foreach (var session in idle)
                    y = ComposeSession(g, draw, session, y);
        }
        return y + S(8);
    }

    int ComposeSession(Graphics g, bool draw, SessionInfo s, int y)
    {
        var key = "sess:" + s.SessionId;
        var expanded = _expanded.Contains(s.SessionId);
        var top = y + S(10);
        var bodyHeight = _body.Height;
        var smallHeight = _small.Height;
        var textX = Pad + S(26);
        var textWidth = Inner - S(26);

        // El fondo usa el tamaño medido en el recorrido anterior (Relayout siempre mide antes de pintar).
        if (draw && _rowBounds.TryGetValue(key, out var previous))
        {
            if (expanded)
            {
                Draw.Fill(g, _hoverKey == key ? _t.Hover : _t.Raised, previous, S(10));
                Draw.Outline(g, _t.Border, previous, S(10));
            }
            else if (_hoverKey == key)
            {
                Draw.Fill(g, _t.Hover, previous, S(10));
            }
        }

        y = top;
        var tag = s.Account.Label;
        var tagWidth = TextRenderer.MeasureText(g, tag, _small, Size.Empty, Line).Width;
        if (draw)
        {
            DrawStateIcon(g, s.State, new Rectangle(Pad, y, S(16), bodyHeight));
            Write(g, s.DisplayTitle, _body, _t.Text, new Rectangle(textX, y, textWidth - tagWidth - S(10), bodyHeight), TextFormatFlags.EndEllipsis);
            Write(g, tag, _small, _t.Tertiary, new Rectangle(Pad + Inner - tagWidth, y, tagWidth, bodyHeight), TextFormatFlags.VerticalCenter);
        }
        y += bodyHeight + S(3);

        var (status, color) = StatusLine(s);
        if (draw) Write(g, status, _small, color, new Rectangle(textX, y, textWidth, smallHeight), TextFormatFlags.EndEllipsis);
        y += smallHeight;

        if (s.State == SessionState.Working)
        {
            y += S(9);
            if (draw) DrawProgress(g, new RectangleF(textX, y, textWidth, S(3)), s);
            y += S(3) + S(7);
            if (draw) Write(g, ProgressLine(s), _small, _t.Tertiary, new Rectangle(textX, y, textWidth, smallHeight), TextFormatFlags.EndEllipsis);
            y += smallHeight;
        }

        if (expanded)
        {
            y += S(12);
            var labelWidth = S(84);
            foreach (var (label, value, lines) in Details(s))
            {
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis;
                var measured = TextRenderer.MeasureText(g, value, _small, new Size(textWidth - labelWidth, 0), flags).Height;
                var height = Math.Min(measured, smallHeight * lines);
                if (draw)
                {
                    Write(g, label, _small, _t.Tertiary, new Rectangle(textX, y, labelWidth, smallHeight));
                    TextRenderer.DrawText(g, value, _small, new Rectangle(textX + labelWidth, y, textWidth - labelWidth, height), _t.Secondary, flags);
                }
                y += height + S(5);
            }
            y -= S(5);
        }

        var bounds = new Rectangle(Pad - S(10), top - S(10), Inner + S(20), y - top + S(20));
        _rowBounds[key] = bounds;
        AddHit(key, bounds, HitKind.Session, session: s);
        return bounds.Bottom + S(4);
    }

    (string Text, Color Color) StatusLine(SessionInfo s) => s.State switch
    {
        SessionState.Working => (s.Action ?? "Pensando…", _t.Secondary),
        SessionState.Waiting => (s.AwaitingAnswer ? "Esperando su respuesta" : "Necesita su atención", _t.Warn),
        SessionState.Failed => ("Falló · " + (s.ErrorText ?? "error de la API"), _t.Danger),
        SessionState.Done => ("Terminó " + Format.Ago(s.TurnEndedAt ?? s.StatusAt)
            + (s.LastTurn is { } d ? $" · {Format.Duration(d)}" : ""), _t.Success),
        _ => ("Inactiva · " + Format.Ago(s.TurnEndedAt ?? s.StatusAt), _t.Tertiary),
    };

    static string ProgressLine(SessionInfo s)
    {
        var parts = new List<string>();
        if (s.TodoTotal > 0) parts.Add($"{s.TodoDone} de {s.TodoTotal} tareas");
        parts.Add(s.Steps == 1 ? "1 paso" : $"{s.Steps} pasos");
        if (s.TurnStartedAt is { } start) parts.Add(Format.Duration(DateTimeOffset.Now - start));
        return string.Join(" · ", parts);
    }

    static IEnumerable<(string Label, string Value, int Lines)> Details(SessionInfo s)
    {
        if (!string.IsNullOrWhiteSpace(s.LastPrompt))
            yield return ("Pedido", s.LastPrompt.ReplaceLineEndings(" "), 3);
        if (s.Cwd.Length > 0)
            yield return ("Carpeta", s.Cwd.Replace(Account.Home, "~"), 1);
        if (Format.ModelLine(s) is { Length: > 0 } model)
            yield return ("Modelo", model, 1);
        var turns = s.Turns == 1 ? "1 interacción" : $"{s.Turns} interacciones";
        var started = s.StartedAt is { } at ? $" · desde {at.LocalDateTime:t}" : "";
        yield return ("Actividad", turns + started, 1);
    }

    // ── Animaciones ─────────────────────────────────────────────────────────────────────────

    void DrawStateIcon(Graphics g, SessionState state, Rectangle box)
    {
        switch (state)
        {
            case SessionState.Working:
                DrawSpinner(g, box);
                break;
            case SessionState.Waiting:
                DrawPulse(g, box, _t.Warn);
                break;
            case SessionState.Failed:
                Write(g, GlyphClose, _glyphSmall, _t.Danger, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
            case SessionState.Done:
                Write(g, GlyphCheck, _glyphSmall, _t.Success, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                break;
            default:
                var r = S(3);
                using (var brush = new SolidBrush(_t.Tertiary))
                    g.FillEllipse(brush, box.X + box.Width / 2f - r, box.Y + box.Height / 2f - r, 2 * r, 2 * r);
                break;
        }
    }

    void DrawSpinner(Graphics g, Rectangle box)
    {
        var frame = SpinnerFrames[(int)(Environment.TickCount64 / 110 % SpinnerFrames.Length)];
        Write(g, frame, _spinner, _t.Accent, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    void DrawPulse(Graphics g, Rectangle box, Color color)
    {
        var phase = Environment.TickCount64 % 1400 / 1400.0;
        var wave = 0.5 + 0.5 * Math.Sin(phase * 2 * Math.PI);
        var halo = S(4) + (float)(S(3) * wave);
        var r = S(4);
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        using (var brush = new SolidBrush(Color.FromArgb((int)(70 * (1 - wave)) + 20, color)))
            g.FillEllipse(brush, cx - halo, cy - halo, 2 * halo, 2 * halo);
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
    }

    /// <summary>Con lista de tareas, barra de avance; sin ella, un destello que recorre la barra.</summary>
    void DrawProgress(Graphics g, RectangleF bar, SessionInfo s)
    {
        var radius = bar.Height / 2;
        Draw.Fill(g, _t.Track, bar, radius);
        if (s.TodoTotal > 0)
        {
            var fill = bar.Width * s.TodoDone / s.TodoTotal;
            if (fill > 0) Draw.Fill(g, _t.Accent, new RectangleF(bar.X, bar.Y, Math.Max(fill, bar.Height), bar.Height), radius);
            return;
        }

        var segment = bar.Width * 0.28f;
        var phase = Environment.TickCount64 % 1600 / 1600f;
        var x = bar.X - segment + (bar.Width + segment) * phase;
        using var clip = Draw.Rounded(bar, radius);
        var state = g.Save();
        g.SetClip(clip);
        using (var brush = new LinearGradientBrush(new RectangleF(x - 1, bar.Y, segment + 2, bar.Height),
                   Color.FromArgb(0, _t.Accent), _t.Accent, LinearGradientMode.Horizontal))
        {
            brush.SetBlendTriangularShape(0.5f);
            g.FillRectangle(brush, x, bar.Y, segment, bar.Height);
        }
        g.Restore(state);
    }

    // ── Vista «Actividad»: mapa de calor de 6 meses ─────────────────────────────────────────

    int ComposeActivity(Graphics g, bool draw, int y)
    {
        var smallHeight = _small.Height;
        if (_activity is null)
        {
            if (draw) Write(g, "Calculando…", _body, _t.Tertiary, new Rectangle(Pad, y, Inner, _body.Height));
            return y + _body.Height + S(24);
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var start = monday.AddDays(-7 * HeatmapWeeks);
        var columns = HeatmapWeeks + 1;
        var labelWidth = S(28);
        var gap = S(3);
        var cell = (Inner - labelWidth - gap * (columns - 1)) / columns;
        var gridX = Pad + Inner - (cell * columns + gap * (columns - 1));

        // Meses: se rotula la columna en la que empieza cada mes.
        var lastLabelRight = int.MinValue;
        for (var c = 0; c < columns; c++)
        {
            var sunday = start.AddDays(7 * c + 6);
            if (c > 0 && sunday.Month == start.AddDays(7 * (c - 1) + 6).Month) continue;
            var x = gridX + c * (cell + gap);
            var text = sunday.ToString("MMM").TrimEnd('.');
            var width = TextRenderer.MeasureText(g, text, _small, Size.Empty, Line).Width;
            if (x < lastLabelRight + S(6)) continue;
            if (draw) Write(g, text, _small, _t.Tertiary, new Rectangle(x, y, width, smallHeight));
            lastLabelRight = x + width;
        }
        y += smallHeight + S(6);

        // Umbrales por cuartiles de los días con actividad, como el cuadro de GitHub.
        var values = Enumerable.Range(0, columns * 7).Select(i => start.AddDays(i))
            .Where(d => d <= today).Select(d => _activity.Get(d)?.Tokens ?? 0).Where(v => v > 0).OrderBy(v => v).ToList();
        long Quartile(double q) => values.Count == 0 ? 0 : values[(int)Math.Min(values.Count - 1, Math.Floor(q * values.Count))];
        var (q1, q2, q3) = (Quartile(0.25), Quartile(0.5), Quartile(0.75));
        int Level(long v) => v <= 0 ? 0 : v <= q1 ? 1 : v <= q2 ? 2 : v <= q3 ? 3 : 4;

        string[] dayLabels = ["lun", "", "mié", "", "vie", "", ""];
        for (var r = 0; r < 7; r++)
            if (draw && dayLabels[r].Length > 0)
                Write(g, dayLabels[r], _small, _t.Tertiary, new Rectangle(Pad, y + r * (cell + gap), labelWidth, cell), TextFormatFlags.VerticalCenter);

        DateOnly? hovered = null;
        for (var c = 0; c < columns; c++)
        {
            for (var r = 0; r < 7; r++)
            {
                var day = start.AddDays(7 * c + r);
                if (day > today) break;
                var rect = new Rectangle(gridX + c * (cell + gap), y + r * (cell + gap), cell, cell);
                var key = "day:" + day.DayNumber;
                AddHit(key, Rectangle.Inflate(rect, (gap + 1) / 2, (gap + 1) / 2), HitKind.Day, day: day);
                if (_hoverKey == key) hovered = day;
                if (!draw) continue;
                Draw.Fill(g, HeatColor(Level(_activity.Get(day)?.Tokens ?? 0)), rect, S(2));
                if (_hoverKey == key || day == today) Draw.Outline(g, _hoverKey == key ? _t.Text : _t.Tertiary, rect, S(2));
            }
        }
        y += 7 * cell + 6 * gap + S(12);

        // Detalle del día bajo el mouse y leyenda.
        var legendBox = S(9);
        var less = TextRenderer.MeasureText(g, "Menos", _small, Size.Empty, Line).Width;
        var more = TextRenderer.MeasureText(g, "Más", _small, Size.Empty, Line).Width;
        var legendWidth = less + S(5) + 5 * (legendBox + S(3)) + S(2) + more;
        if (draw)
        {
            string detail;
            if (hovered is { } d)
            {
                var record = _activity.Get(d);
                detail = $"{d:ddd d MMM}".Replace(".", "") + (record is null || record.Tokens == 0
                    ? " · sin actividad"
                    : $" · {Format.Tokens(record.Tokens)} tokens · {record.Responses} respuestas");
            }
            else
            {
                detail = _activity.FirstDay is { } first && first > start ? $"Desde el {first:d 'de' MMMM}" : "Últimos 6 meses";
            }
            Write(g, detail, _small, hovered is null ? _t.Tertiary : _t.Secondary,
                new Rectangle(Pad, y, Inner - legendWidth - S(10), smallHeight), TextFormatFlags.EndEllipsis);

            var x = Pad + Inner - legendWidth;
            Write(g, "Menos", _small, _t.Tertiary, new Rectangle(x, y, less, smallHeight));
            x += less + S(5);
            for (var level = 0; level <= 4; level++)
            {
                Draw.Fill(g, HeatColor(level), new Rectangle(x, y + (smallHeight - legendBox) / 2, legendBox, legendBox), S(2));
                x += legendBox + S(3);
            }
            Write(g, "Más", _small, _t.Tertiary, new Rectangle(x + S(2), y, more, smallHeight));
        }
        y += smallHeight + S(22);

        // Cifras del período, en dos columnas.
        var stats = ActivityStats.Compute(_activity, start, today);
        static string Days(int n) => n == 1 ? "1 día" : $"{n} días";
        var items = new (string Value, string Label)[]
        {
            (Format.Tokens(stats.Tokens), "tokens"),
            (stats.ActiveDays.ToString(), "días activos"),
            (Days(stats.CurrentStreak), "racha actual"),
            (Days(stats.LongestStreak), "racha más larga"),
            (stats.BusiestDay is { } busiest ? $"{busiest:d MMM}".Replace(".", "") : "—", "día más activo"),
            (stats.FavoriteModel ?? "—", "modelo favorito"),
        };
        var columnWidth = Inner / 2;
        var rowHeight = _bodyStrong.Height + S(2) + smallHeight + S(14);
        for (var i = 0; i < items.Length && draw; i++)
        {
            var x = Pad + i % 2 * columnWidth;
            var top = y + i / 2 * rowHeight;
            Write(g, items[i].Value, _bodyStrong, _t.Text, new Rectangle(x, top, columnWidth - S(8), _bodyStrong.Height), TextFormatFlags.EndEllipsis);
            Write(g, items[i].Label, _small, _t.Tertiary, new Rectangle(x, top + _bodyStrong.Height + S(2), columnWidth - S(8), smallHeight));
        }
        return y + (items.Length + 1) / 2 * rowHeight + S(6);
    }

    Color HeatColor(int level) => level switch
    {
        0 => _t.Track,
        1 => Blend(_t.Background, _t.Accent, 0.3),
        2 => Blend(_t.Background, _t.Accent, 0.52),
        3 => Blend(_t.Background, _t.Accent, 0.76),
        _ => _t.Accent,
    };

    static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
        (int)(from.R + (to.R - from.R) * amount),
        (int)(from.G + (to.G - from.G) * amount),
        (int)(from.B + (to.B - from.B) * amount));

    // ── Pie ─────────────────────────────────────────────────────────────────────────────────

    int ComposeFooter(Graphics g, bool draw, int y)
    {
        var smallHeight = _small.Height;
        if (draw)
        {
            using var pen = new Pen(_t.Border);
            g.DrawLine(pen, Pad, y, ClientSize.Width - Pad, y);
        }
        y += S(14);
        if (draw)
        {
            // Mientras se actualiza, la mascota ocupa el lugar de la hora.
            var unit = Math.Max(1, (int)Math.Round(smallHeight / 8f));
            var when = _mascot is not null ? "" : _busy ? "Actualizando…" : _updated is { } u ? $"{u:t}" : "";
            var whenWidth = _mascot is not null ? Mascot.Columns * unit : TextRenderer.MeasureText(g, when, _small, Size.Empty, Line).Width;
            Write(g, Format.Local(_local), _small, _t.Tertiary, new Rectangle(Pad, y, Inner - whenWidth - S(12), smallHeight), TextFormatFlags.EndEllipsis);
            if (_mascot is { } frame)
                Mascot.Draw(g, ClientSize.Width - Pad - whenWidth, y + (smallHeight - Mascot.Rows * unit) / 2, unit, frame);
            else
                Write(g, when, _small, _t.Tertiary, new Rectangle(ClientSize.Width - Pad - whenWidth, y, whenWidth, smallHeight));
        }
        return y + smallHeight + S(16);
    }

    // ── Interacción ─────────────────────────────────────────────────────────────────────────

    void AddHit(string key, Rectangle bounds, HitKind kind, Account? account = null, SessionInfo? session = null, View view = View.Usage, DateOnly day = default) =>
        _hits.Add(new Hit(key, bounds, kind, account, session, view, day));

    void Write(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags extra = TextFormatFlags.Default) =>
        TextRenderer.DrawText(g, text, font, bounds, color, Line | extra);

    Hit? HitAt(Point p) => _hits.LastOrDefault(h => h.Bounds.Contains(p));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        Cursor = hit is null || hit.Kind == HitKind.Day ? Cursors.Default : Cursors.Hand;
        if (hit?.Key == _hoverKey) return;
        _hoverKey = hit?.Key;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey is null) return;
        _hoverKey = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (HitAt(e.Location) is not { } hit) return;

        if (e.Button == MouseButtons.Right)
        {
            if (hit.Account is { } account)
            {
                _menuAccount = account;
                _accountMenu.Show(this, e.Location);
            }
            else if (hit.Session is { } session)
            {
                _menuSession = session;
                _sessionMenu.Show(this, e.Location);
            }
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        switch (hit.Kind)
        {
            case HitKind.Refresh when !_busy:
                RefreshRequested?.Invoke();
                break;
            case HitKind.Add:
                AddRequested?.Invoke();
                break;
            case HitKind.Bell:
                NotificationsToggled?.Invoke();
                break;
            case HitKind.View:
                _view = hit.View;
                Relayout();
                break;
            case HitKind.IdleToggle:
                _showIdle = !_showIdle;
                Relayout();
                break;
            case HitKind.Session when hit.Session is { } session:
                if (!_expanded.Remove(session.SessionId)) _expanded.Add(session.SessionId);
                Relayout();
                break;
            case HitKind.Account when hit.Account is { } account:
                _selected = account.ConfigDir;
                SelectedChanged?.Invoke(account);
                Relayout();
                break;
        }
    }

    static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _animation.Dispose();
            _title.Dispose(); _body.Dispose(); _bodyStrong.Dispose(); _small.Dispose();
            _glyph.Dispose(); _glyphSmall.Dispose(); _spinner.Dispose();
            _accountMenu.Dispose(); _sessionMenu.Dispose();
        }
        base.Dispose(disposing);
    }
}
