using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

/// <summary>Selector de vistas con pin y vista «Ajustes».</summary>
sealed partial class UsageForm
{
    const string GlyphSettings = "", GlyphChevronRight = "", GlyphBack = "";

    static readonly View[] Selectable = [View.Usage, View.Sessions, View.Activity, View.Flows];

    /// <summary>Lo que muestra «Ajustes»; lo manda TrayApp cada vez que cambia algo. Los textos son los valores ya listos para mostrar.</summary>
    public sealed record SettingsState
    {
        public bool Notifications { get; init; } = true;
        public bool Sound { get; init; } = true;
        public string SoundPack { get; init; } = "";
        public string Palette { get; init; } = "";
        public string Mascot { get; init; } = "";
        public string MascotSize { get; init; } = "";
        public bool DesktopMascot { get; init; }
        public bool Speak { get; init; }
        public bool FlowGestures { get; init; }
        public bool DanceToMusic { get; init; }
        public bool NoticeGestures { get; init; }
        public bool GreetOnAppear { get; init; }
        public bool AngryOnClicks { get; init; }
        public bool SleepWithoutSessions { get; init; }
        public bool Chat { get; init; }
        public string MascotModel { get; init; } = "";
        public string AssistantAccount { get; init; } = "";
        public string TaskFolder { get; init; } = "";
        public bool AnalyzeDrops { get; init; }
        public bool Mic { get; init; }
        public string MicKey { get; init; } = "";
        public string ChatKey { get; init; } = "";
        public bool CopilotKey { get; init; }
        /// <summary>El modelo de voz: lo que dice la fila y su enlace (descargar, el avance o listo).</summary>
        public string VoiceStatus { get; init; } = "";
        public string VoiceAction { get; init; } = "";
        public bool ReadAloud { get; init; }
        /// <summary>La voz que lee y, si es de Claupet y falta su modelo, lo que dice su fila y su enlace (vacío: sin fila).</summary>
        public string ReadAloudVoice { get; init; } = "";
        public string SpeechStatus { get; init; } = "";
        public string SpeechAction { get; init; } = "";
        public bool Island { get; init; }
        public string IslandModel { get; init; } = "";
        public bool IslandHover { get; init; }
        public bool IslandWhileBusy { get; init; }
        public bool WeeklyRecap { get; init; }
        /// <summary>El hook de permisos y preguntas: «Configurar…» o «Listo».</summary>
        public string Approvals { get; init; } = "";
        public bool StartWithWindows { get; init; }
        public int Accounts { get; init; }
    }

    /// <summary>Las pestañas de Ajustes, en orden.</summary>
    public enum SettingsTab { Avisos, Apariencia, Asistente, Isla, Sistema }

    /// <summary>Se cambió de pestaña en Ajustes (para recordarla).</summary>
    public event Action<SettingsTab>? SettingsTabChanged;

    readonly List<View> _pinned = [View.Usage, View.Sessions, View.Activity];
    bool _viewMenuOpen;
    /// <summary>Vista que se quiere fijar: el próximo pin de una fija la reemplaza.</summary>
    View? _replacing;
    View _beforeSettings = View.Usage;
    Rectangle _selectorBounds;
    SettingsState _settingsState = new();
    SettingsTab _settingsTab;
    /// <summary>Desde qué pestaña se desliza el indicador y cuándo empezó.</summary>
    SettingsTab _tabFrom;
    long _tabAt;

    static string ViewLabel(View view) => view switch
    {
        View.Usage => "Uso",
        View.Sessions => "Sesiones",
        View.Activity => "Actividad",
        View.Flows => "Flujos",
        _ => "Ajustes",
    };

    /// <summary>Tres vistas distintas y elegibles; si no, las de siempre.</summary>
    public void SetPinned(IEnumerable<string> names)
    {
        var views = names.Select(n => Enum.TryParse<View>(n, out var v) ? v : (View?)null).ToList();
        var valid = views.Count == 3 && views.All(v => v is { } x && Selectable.Contains(x)) && views.Distinct().Count() == 3;
        _pinned.Clear();
        _pinned.AddRange(valid ? views.Select(v => v!.Value) : [View.Usage, View.Sessions, View.Activity]);
        if (IsHandleCreated) Relayout();
    }

    public void SetSettingsState(SettingsState state)
    {
        var old = _settingsState;
        _settingsState = state;
        if (_view != View.Settings) return;
        // Las filas que aparecen o se van (al encender el chat, la isla, el micrófono…) entran con una transición.
        if (old.Chat != state.Chat || old.Mic != state.Mic || old.Island != state.Island || old.DesktopMascot != state.DesktopMascot || old.DanceToMusic != state.DanceToMusic
            || old.ReadAloud != state.ReadAloud || old.SpeechStatus.Length > 0 != state.SpeechStatus.Length > 0)
        {
            BeginTransition(Slide.Fade);
            Relayout();
        }
        else Invalidate();
    }

    /// <summary>La pestaña de Ajustes con la que se abre (la última usada).</summary>
    public void SetSettingsTab(SettingsTab tab) => _settingsTab = _tabFrom = tab;

    void SelectSettingsTab(SettingsTab tab)
    {
        if (tab == _settingsTab) return;
        BeginTransition(tab > _settingsTab ? Slide.FromRight : Slide.FromLeft);
        _tabFrom = _settingsTab;
        _tabAt = AnimationsEnabled ? Environment.TickCount64 : 0;
        _settingsTab = tab;
        StartMotion();
        Relayout();
        SettingsTabChanged?.Invoke(tab);
    }

    void OpenView(View view)
    {
        if (view != _view || _viewMenuOpen) BeginTransition(view == _view ? Slide.Fade : SlideFor(_view, view));
        _view = view;
        _viewMenuOpen = false;
        _replacing = null;
        Relayout();
    }

    void CloseViewMenu()
    {
        BeginTransition(Slide.Fade);
        _viewMenuOpen = false;
        _replacing = null;
        Relayout();
    }

    /// <summary>Siempre hay 3 fijas: el pin de una libre la prepara y el pin de una fija hace el cambio.</summary>
    void TogglePin(View view)
    {
        if (!_pinned.Contains(view))
            _replacing = _replacing == view ? null : view;
        else if (_replacing is { } incoming)
        {
            _pinned[_pinned.IndexOf(view)] = incoming;
            _replacing = null;
            PinnedChanged?.Invoke(_pinned.Select(v => v.ToString()).ToList());
        }
        Relayout();
    }

    // ── Selector de la cabecera ─────────────────────────────────────────────────────────────

    /// <summary>Botón con el pin, a la izquierda de «actualizar». Si la vista abierta no es fija, lleva su nombre cuando cabe.</summary>
    void ComposeViewSelector(Graphics g, bool draw, int right, int top, int tabsRight)
    {
        var height = S(30);
        var extra = !_pinned.Contains(_view);
        var label = ViewLabel(_view);
        var labelWidth = TextRenderer.MeasureText(g, label, _body, Size.Empty, Line).Width;
        var compact = S(10) + S(16) + S(4) + S(12) + S(8);
        var width = extra && right - (compact + S(6) + labelWidth) >= tabsRight + S(10) ? compact + S(6) + labelWidth : compact;
        var showLabel = width > compact;
        _selectorBounds = new Rectangle(right - width, top, width, height);
        AddHit("viewmenu", _selectorBounds, HitKind.ViewMenu);
        if (!draw) return;

        var highlight = extra || _viewMenuOpen;
        if (_viewMenuOpen) Draw.Fill(g, _t.Raised, _selectorBounds, S(8));
        else if (_hoverKey == "viewmenu") Draw.Fill(g, _t.Hover, _selectorBounds, S(8));
        Draw.Outline(g, highlight ? _t.Accent : _t.Border, _selectorBounds, S(8));

        var x = _selectorBounds.X + S(10);
        DrawPin(g, new RectangleF(x, top + (height - S(16)) / 2f, S(16), S(16)), extra ? _t.Accent : _t.Secondary, filled: false);
        x += S(16);
        if (showLabel)
        {
            Write(g, label, _body, _t.Text, new Rectangle(x + S(6), top, labelWidth, height), TextFormatFlags.VerticalCenter);
            x += S(6) + labelWidth;
        }
        Write(g, GlyphChevronDown, _glyphSmall, _t.Secondary, new Rectangle(x + S(4), top, S(12), height),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>La lista de vistas bajo el selector. Devuelve su borde inferior.</summary>
    int ComposeViewMenu(Graphics g, bool draw)
    {
        var width = S(250);
        var x = Math.Min(_selectorBounds.Right, ClientSize.Width - S(10)) - width;
        var top = _selectorBounds.Bottom + S(6);
        var rowHeight = S(34);
        var smallHeight = _small.Height;
        var hintFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var hint = _replacing is { } incoming
            ? $"Pulse el pin de la fija que cambia por {ViewLabel(incoming)}."
            : "Siempre hay 3 pestañas fijas. Para fijar otra, pulse su pin y luego el de la que cambia.";
        var hintHeight = TextRenderer.MeasureText(g, hint, _small, new Size(width - S(24), 0), hintFlags).Height;
        var height = S(8) + smallHeight + S(8) + Selectable.Length * rowHeight + S(9) + hintHeight + S(12);
        var panel = new Rectangle(x, top, width, height);
        AddHit("viewmenu-panel", panel, HitKind.MenuPanel);

        if (draw)
        {
            Draw.Fill(g, _t.Raised, panel, S(10));
            Draw.Outline(g, _t.Border, panel, S(10));
            Write(g, "Clic: abrir · Pin: fijar como pestaña", _small, _t.Tertiary, new Rectangle(x + S(12), top + S(8), width - S(24), smallHeight));
        }

        var y = top + S(8) + smallHeight + S(8);
        foreach (var view in Selectable)
        {
            var row = new Rectangle(x + S(6), y, width - S(12), rowHeight);
            var pin = new Rectangle(row.Right - S(32), row.Y + (rowHeight - S(30)) / 2, S(30), S(30));
            var rowKey = "vrow:" + view;
            var pinKey = "vpin:" + view;
            AddHit(rowKey, row, HitKind.ViewRow, view: view);
            AddHit(pinKey, pin, HitKind.ViewPin, view: view);
            if (draw)
            {
                var pinned = _pinned.Contains(view);
                if (_replacing == view) Draw.Fill(g, Blend(_t.Raised, _t.Warn, 0.18), row, S(8));
                else if (_view == view) Draw.Fill(g, _t.Hover, row, S(8));
                else if (_hoverKey == rowKey) Draw.Fill(g, Blend(_t.Raised, _t.Hover, 0.6), row, S(8));
                var text = ViewLabel(view);
                var textWidth = TextRenderer.MeasureText(g, text, _body, Size.Empty, Line).Width;
                Write(g, text, _body, _t.Text, new Rectangle(row.X + S(10), row.Y, textWidth, rowHeight), TextFormatFlags.VerticalCenter);
                if (_view == view)
                    Write(g, "abierta", _small, _t.Tertiary, new Rectangle(row.X + S(10) + textWidth + S(8), row.Y, S(80), rowHeight), TextFormatFlags.VerticalCenter);
                if (_hoverKey == pinKey) Draw.Fill(g, _t.Hover, pin, S(8));
                var color = pinned ? (_replacing is null ? _t.Accent : _t.Warn) : _replacing == view ? _t.Warn : _t.Tertiary;
                DrawPin(g, new RectangleF(pin.X + S(7), pin.Y + S(7), S(16), S(16)), color, filled: pinned);
            }
            y += rowHeight;
        }

        y += S(4);
        if (draw)
        {
            using (var pen = new Pen(_t.Border)) g.DrawLine(pen, x + S(10), y, x + width - S(10), y);
            TextRenderer.DrawText(g, hint, _small, new Rectangle(x + S(12), y + S(5), width - S(24), hintHeight),
                _replacing is null ? _t.Tertiary : _t.Warn, hintFlags);
        }
        return panel.Bottom;
    }

    /// <summary>Una tachuela inclinada 30°, dibujada para poder rellenarla cuando la vista está fija.</summary>
    void DrawPin(Graphics g, RectangleF box, Color color, bool filled)
    {
        using var body = new GraphicsPath();
        body.AddPolygon(new PointF[] { new(8.5f, 2.5f), new(15.5f, 2.5f), new(15.5f, 5.5f), new(14.8f, 5.5f), new(16f, 12.5f),
            new(18.5f, 13.5f), new(18.5f, 16f), new(5.5f, 16f), new(5.5f, 13.5f), new(8f, 12.5f), new(9.2f, 5.5f), new(8.5f, 5.5f) });
        using var needle = new GraphicsPath();
        needle.AddLine(12f, 16f, 12f, 22f);

        using var matrix = new Matrix();
        matrix.Translate(box.X + box.Width / 2, box.Y + box.Height / 2);
        matrix.Rotate(30);
        matrix.Scale(box.Width / 24f, box.Height / 24f);
        matrix.Translate(-12, -12);
        body.Transform(matrix);
        needle.Transform(matrix);

        using var pen = new Pen(color, Math.Max(1.2f, box.Width / 11f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        if (filled)
            using (var brush = new SolidBrush(color)) g.FillPath(brush, body);
        g.DrawPath(pen, body);
        g.DrawPath(pen, needle);
    }

    // ── Vista «Ajustes» ─────────────────────────────────────────────────────────────────────

    int ComposeSettingsHeader(Graphics g, bool draw, int y)
    {
        var height = TextRenderer.MeasureText(g, "Ajustes", _title, Size.Empty, Line).Height;
        var close = new Rectangle(ClientSize.Width - Pad - S(24), y + (height - S(30)) / 2, S(30), S(30));
        AddHit("settings-close", close, HitKind.CloseSettings);
        if (draw)
        {
            Write(g, "Ajustes", _title, _t.Text, new Rectangle(Pad, y, Inner - S(40), height));
            if (_hoverKey == "settings-close") Draw.Fill(g, _t.Hover, close, S(8));
            Write(g, GlyphClose, _glyphSmall, _t.Secondary, close, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return y + height + S(16);
    }

    static readonly SettingsTab[] AllTabs = Enum.GetValues<SettingsTab>();

    static string TabLabel(SettingsTab tab) => tab switch
    {
        SettingsTab.Avisos => "Avisos",
        SettingsTab.Apariencia => "Apariencia",
        SettingsTab.Asistente => "Asistente",
        SettingsTab.Isla => "Isla",
        _ => "Sistema",
    };

    Font? _smallStrong;
    Font SmallStrong => _smallStrong ??= Fonts.Make("Segoe UI Variable Text Semibold", 8.75f, "Segoe UI Semibold");

    /// <summary>
    /// Las pestañas: una barra con el indicador que se desliza bajo la elegida. Cada pestaña mide lo
    /// que su nombre más una parte igual del espacio que sobra.
    /// </summary>
    int ComposeSettingsTabs(Graphics g, bool draw, int y)
    {
        var bar = new Rectangle(Pad, y, Inner, S(32));
        var widths = AllTabs.Select(t => TextRenderer.MeasureText(g, TabLabel(t), SmallStrong, Size.Empty, Line).Width).ToArray();
        var extra = (bar.Width - S(8) - widths.Sum()) / (float)AllTabs.Length;
        var cells = new RectangleF[AllTabs.Length];
        var x = (float)bar.X + S(4);
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = new RectangleF(x, bar.Y + S(4), widths[i] + extra, bar.Height - S(8));
            x += cells[i].Width;
        }
        if (draw)
        {
            Draw.Fill(g, _t.Hover, bar, S(9));
            var k = (float)Smooth(Math.Clamp((Environment.TickCount64 - _tabAt) / (double)IndicatorMs, 0, 1));
            var a = cells[(int)_tabFrom];
            var b = cells[(int)_settingsTab];
            var pill = new RectangleF(a.X + (b.X - a.X) * k, b.Y, a.Width + (b.Width - a.Width) * k, b.Height);
            Draw.Fill(g, _t.Raised, pill, S(6));
            Draw.Outline(g, _t.Border, pill, S(6));
        }
        for (var i = 0; i < cells.Length; i++)
        {
            var tab = AllTabs[i];
            var cell = Rectangle.Round(cells[i]);
            AddHit("stab:" + tab, cell, HitKind.SettingsTab);
            if (!draw) continue;
            var selected = tab == _settingsTab;
            var color = selected || _hoverKey == "stab:" + tab ? _t.Text : _t.Secondary;
            Write(g, TabLabel(tab), selected ? SmallStrong : _small, color, cell, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return bar.Bottom + S(12);
    }

    /// <summary>
    /// Todas las pestañas ocupan lo que la más alta (con lo que esté encendido): al cambiar de pestaña
    /// solo se desliza el contenido y el cuadro no cambia de tamaño. Las demás se miden sin dejar hits.
    /// </summary>
    int ComposeSettings(Graphics g, bool draw, int y)
    {
        y = ComposeSettingsTabs(g, draw, y);
        var bottom = ComposeSettingsTab(g, draw, _settingsTab, y);
        var hits = _hits.Count;
        foreach (var tab in AllTabs)
            if (tab != _settingsTab) bottom = Math.Max(bottom, ComposeSettingsTab(g, false, tab, y));
        _hits.RemoveRange(hits, _hits.Count - hits);
        return bottom + S(14);
    }

    int ComposeSettingsTab(Graphics g, bool draw, SettingsTab tab, int y)
    {
        var s = _settingsState;
        switch (tab)
        {
            case SettingsTab.Avisos:
                y = SwitchRow(g, draw, "Notificaciones", "notify", s.Notifications, y);
                y = SwitchRow(g, draw, "Sonido", "sound", s.Sound, y);
                y = ChoiceRow(g, draw, "Estilo de sonido", "pack", s.SoundPack, y);
                y = LinksRow(g, draw, y, ("Ver un aviso de ejemplo", "sample"), ("Abrir carpeta de sonidos", "sounds"));
                break;
            case SettingsTab.Apariencia:
                y = ChoiceRow(g, draw, "Color", "palette", s.Palette, y, swatch: _t.Accent);
                y = ChoiceRow(g, draw, "Mascota", "mascot", s.Mascot, y);
                y = ChoiceRow(g, draw, "Tamaño de la mascota", "size", s.MascotSize, y);
                y = SwitchRow(g, draw, "Mascota en el escritorio", "desk", s.DesktopMascot, y);
                if (s.DesktopMascot)
                {
                    y = SwitchRow(g, draw, "Gestos del flujo (lo que hace la sesión)", "flowgest", s.FlowGestures, y);
                    y = SwitchRow(g, draw, "Bailar con música", "music", s.DanceToMusic, y);
                    y = SwitchRow(g, draw, "Gesto de los avisos", "noticegest", s.NoticeGestures, y);
                    y = SwitchRow(g, draw, "Saludar al aparecer", "greet", s.GreetOnAppear, y);
                    y = SwitchRow(g, draw, "Enojarse con muchos clics", "angry", s.AngryOnClicks, y);
                    y = SwitchRow(g, draw, "Dormir sin sesiones", "sleep", s.SleepWithoutSessions, y);
                    y = SwitchRow(g, draw, "Hablar (los avisos en una nubecita)", "speak", s.Speak, y);
                }
                break;
            case SettingsTab.Asistente:
                y = SettingsCaption(g, draw, "Chat", y);
                y = SwitchRow(g, draw, "Chat con la mascota", "chat", s.Chat, y);
                if (s.Chat)
                {
                    y = ChoiceRow(g, draw, "Modelo de la mascota", "mmodel", s.MascotModel, y);
                    y = ChoiceRow(g, draw, "Cuenta que responde", "account", s.AssistantAccount, y);
                    y = ChoiceRow(g, draw, "Carpeta de las tareas", "folder", s.TaskFolder, y);
                    y = SwitchRow(g, draw, "Analizar los archivos que le suelte", "drops", s.AnalyzeDrops, y);
                }
                y = SettingsCaption(g, draw, "Voz", y + S(8));
                y = SwitchRow(g, draw, "Micrófono", "mic", s.Mic, y);
                if (s.Mic)
                {
                    y = ChoiceRow(g, draw, "Tecla para hablar", "mickey", s.MicKey, y);
                    y = LinkRow(g, draw, s.VoiceStatus, s.VoiceAction, "voice", _t.Accent, y);
                    y = SwitchRow(g, draw, "Leer las respuestas en voz alta", "readaloud", s.ReadAloud, y);
                    if (s.ReadAloud)
                    {
                        y = ChoiceRow(g, draw, "Voz", "readvoice", s.ReadAloudVoice, y);
                        if (s.SpeechStatus.Length > 0) y = LinkRow(g, draw, s.SpeechStatus, s.SpeechAction, "speech", _t.Accent, y);
                    }
                }
                if (s.Chat || s.Island)
                {
                    y = SettingsCaption(g, draw, "Teclas", y + S(8));
                    y = ChoiceRow(g, draw, "Atajo del chat", "chatkey", s.ChatKey, y);
                    y = SwitchRow(g, draw, s.Mic ? "Tecla Copilot: chat y voz" : "Tecla Copilot: abre el chat", "copilot", s.CopilotKey, y);
                    y = LinkRow(g, draw, "Atajos de teclado", "Ver…", "shortcuts", _t.Accent, y);
                }
                break;
            case SettingsTab.Isla:
                y = SwitchRow(g, draw, "Isla en el borde de arriba", "island", s.Island, y);
                if (s.Island)
                {
                    y = ChoiceRow(g, draw, "Modelo de la isla", "imodel", s.IslandModel, y);
                    y = SwitchRow(g, draw, "Abrir al pasar el cursor", "hover", s.IslandHover, y);
                    y = SwitchRow(g, draw, "A la vista mientras algo trabaja", "busy", s.IslandWhileBusy, y);
                    y = LinkRow(g, draw, "Permisos y preguntas en la isla", s.Approvals, "approvals", _t.Accent, y);
                    y = SwitchRow(g, draw, "Resumen de la semana, los lunes", "recap", s.WeeklyRecap, y);
                    y = LinkRow(g, draw, "El de esta semana", "Ver ahora", "recapnow", _t.Accent, y);
                }
                break;
            default:
                y = LinkRow(g, draw, s.Accounts == 1 ? "1 cuenta" : $"{s.Accounts} cuentas", "Agregar cuenta…", "add", _t.Accent, y);
                y = SwitchRow(g, draw, "Iniciar con Windows", "startup", s.StartWithWindows, y);
                y = LinkRow(g, draw, "Avisos de permisos (hook)", "Configurar…", "setup", _t.Accent, y);
                y = LinkRow(g, draw, "Si algo no funciona", "Copiar diagnóstico", "diag", _t.Accent, y);
                y = LinkRow(g, draw, "Quitar hook e inicio con Windows", "Quitar…", "unsetup", _t.Danger, y);
                break;
        }
        return y;
    }

    int SettingsCaption(Graphics g, bool draw, string text, int y)
    {
        if (draw) Write(g, text.ToUpperInvariant(), _small, _t.Tertiary, new Rectangle(Pad, y, Inner, _small.Height));
        return y + _small.Height + S(6);
    }

    /// <summary>Un interruptor: la perilla se desliza y el color cambia al encenderlo o apagarlo.</summary>
    int SwitchRow(Graphics g, bool draw, string label, string key, bool on, int y)
    {
        var height = S(32);
        var row = new Rectangle(Pad - S(8), y, Inner + S(16), height);
        AddHit("set:" + key, row, HitKind.Setting);
        var k = SwitchPosition(key, on);
        if (draw)
        {
            if (_hoverKey == "set:" + key) Draw.Fill(g, _t.Hover, row, S(8));
            Write(g, label, _body, _t.Text, new Rectangle(Pad, y, Inner - S(50), height), TextFormatFlags.VerticalCenter);
            var track = new RectangleF(Pad + Inner - S(34), y + (height - S(18)) / 2f, S(34), S(18));
            Draw.Fill(g, Mix(_t.Track, _t.Accent, k), track, track.Height / 2);
            var knob = S(12);
            var kx = track.X + S(3) + (track.Width - S(6) - knob) * k;
            using var brush = new SolidBrush(Mix(_t.Secondary, Color.White, k));
            g.FillEllipse(brush, kx, track.Y + (track.Height - knob) / 2, knob, knob);
        }
        return y + height;
    }

    /// <summary>Una opción con lista; swatch pone un punto de ese color antes del valor (la paleta).</summary>
    int ChoiceRow(Graphics g, bool draw, string label, string key, string value, int y, Color? swatch = null)
    {
        var height = S(32);
        var row = new Rectangle(Pad - S(8), y, Inner + S(16), height);
        AddHit("set:" + key, row, HitKind.Setting);
        if (draw)
        {
            if (_hoverKey == "set:" + key) Draw.Fill(g, _t.Hover, row, S(8));
            var labelWidth = TextRenderer.MeasureText(g, label, _body, Size.Empty, Line).Width;
            Write(g, label, _body, _t.Text, new Rectangle(Pad, y, labelWidth, height), TextFormatFlags.VerticalCenter);
            var space = Inner - labelWidth - S(18) - S(14) - (swatch is null ? 0 : S(20));
            var valueWidth = Math.Max(0, Math.Min(space, TextRenderer.MeasureText(g, value, _body, Size.Empty, Line).Width));
            var valueX = Pad + Inner - S(18) - valueWidth;
            Write(g, value, _body, _t.Secondary, new Rectangle(valueX, y, valueWidth, height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (swatch is { } color)
            {
                var dot = S(12);
                Draw.Fill(g, color, new RectangleF(valueX - S(8) - dot, y + (height - dot) / 2f, dot, dot), dot / 2f);
            }
            Write(g, GlyphChevronDown, _glyphSmall, _t.Secondary, new Rectangle(Pad + Inner - S(14), y, S(14), height),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        return y + height;
    }

    int LinkRow(Graphics g, bool draw, string label, string link, string key, Color color, int y)
    {
        var height = S(32);
        var linkWidth = TextRenderer.MeasureText(g, link, _body, Size.Empty, Line).Width;
        var target = new Rectangle(Pad + Inner - linkWidth - S(8), y + S(2), linkWidth + S(16), height - S(4));
        AddHit("set:" + key, target, HitKind.Setting);
        if (draw)
        {
            Write(g, label, _body, _t.Secondary, new Rectangle(Pad, y, Inner - linkWidth - S(16), height), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (_hoverKey == "set:" + key) Draw.Fill(g, _t.Hover, target, S(6));
            Write(g, link, _body, color, new Rectangle(target.X + S(8), y, linkWidth, height), TextFormatFlags.VerticalCenter);
        }
        return y + height;
    }

    int LinksRow(Graphics g, bool draw, int y, params (string Text, string Key)[] links)
    {
        var height = S(32);
        var x = Pad - S(8);
        foreach (var (text, key) in links)
        {
            var width = TextRenderer.MeasureText(g, text, _small, Size.Empty, Line).Width + S(16);
            var target = new Rectangle(x, y + S(3), width, height - S(6));
            AddHit("set:" + key, target, HitKind.Setting);
            if (draw)
            {
                if (_hoverKey == "set:" + key) Draw.Fill(g, _t.Hover, target, S(6));
                Write(g, text, _small, _t.Accent, new Rectangle(target.X + S(8), y, width - S(16), height), TextFormatFlags.VerticalCenter);
            }
            x += width + S(4);
        }
        return y + height;
    }
}
