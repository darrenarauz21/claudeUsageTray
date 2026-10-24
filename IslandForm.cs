using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ClaudeUsageTray;

/// <summary>
/// La isla del borde de arriba de la pantalla. Oculta, una franja invisible en el centro del borde la
/// hace asomar compacta (cabeza de la mascota, estado y uso); con el cursor encima un momento, o con
/// un clic, se abre con sus pestañas: Inicio (las sesiones con sus pasos), Chat (con el modelo de sus
/// sesiones) y «+ Sesión». También recibe archivos (soltados encima o lanzados por la mascota) y los
/// analiza con Claude. Es una ventana con transparencia por pixel, siempre oscura, con el acento de la
/// paleta; no quita el foco salvo al escribir en su campo.
/// </summary>
sealed class IslandForm : Form
{
    public enum Shape { Hidden, Compact, Open }
    public enum Page { Home, Chat, Analysis, Permission, Question, Diff, Week }

    /// <summary>Cómo respondió la persona un pedido de permiso o una pregunta de Claude Code.</summary>
    public enum ApprovalChoice { Allow, Deny, Always, Answered }

    public sealed record ApprovalAnswer(ApprovalChoice Choice, IReadOnlyDictionary<string, IReadOnlyList<string>>? Picks = null);

    // ── Colores (siempre oscura) ────────────────────────────────────────────────────────────
    static readonly Color Ground = Hex(0x141413), Card = Hex(0x1F1E1D), Raised = Hex(0x262624), Tab = Hex(0x2E2E2C),
        Line = Hex(0x33322F), Edge = Hex(0x3D3D3A), Ink = Hex(0xF5F4EF), Soft = Hex(0xD6D4CC), Dim = Hex(0xA8A69E),
        Faint = Hex(0x8C8A83), Good = Hex(0x8FA46C), Warn = Hex(0xE0A84B), Bad = Hex(0xE0695C);

    // Medidas lógicas (a 96 ppp).
    const int CompactW = 300, CompactH = 36, MouthW = 336, MouthH = 44, OpenW = 560, ShadowMargin = 14;
    const int HotWidth = 320, HotHeight = 6, OpenDelay = 250, CloseDelay = 700, HideDelay = 1200;

    // ── Eventos ─────────────────────────────────────────────────────────────────────────────
    public event Action<SessionInfo>? SessionClicked;
    public event Action<SessionInfo>? TerminalClicked;
    public event Action? NewSessionClicked, SettingsClicked, ModelClicked, NewChatClicked;
    public event Action<string>? Asked;
    public event Action<string[]>? FilesDropped;
    /// <summary>«Abrir en VS Code» desde el diff de un archivo.</summary>
    public event Action<string>? OpenFileClicked;
    public event Action? CancelClicked;
    /// <summary>Una acción sobre el resultado de un análisis: ask, copy o session.</summary>
    public event Action<string>? ResultAction;
    /// <summary>Clic en el micrófono del campo: empezar a escuchar o terminar y enviar.</summary>
    public event Action? MicClicked;

    // ── Estado ──────────────────────────────────────────────────────────────────────────────
    readonly System.Windows.Forms.Timer _poll = new() { Interval = 50 };
    readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    readonly IslandInput _input;
    readonly List<(Rectangle Bounds, string Key, object? Tag)> _hits = new();
    readonly Font _body, _bodyStrong, _small, _smallStrong, _glyph, _glyphTiny, _spinner, _mono;
    readonly float _scale;
    Color _accent = Hex(0xD97757);
    Shape _shape = Shape.Hidden;
    Page _page = Page.Home;
    Screen _screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];

    // Tamaño de la isla, animado: de (from) a (to) en _animMs desde _animAt. Y: 0 a la vista; negativo, escondida.
    float _w = CompactW, _h = CompactH, _y = -CompactH - ShadowMargin;
    float _fromW, _fromH, _fromY, _toW = CompactW, _toH = CompactH, _toY = -CompactH - ShadowMargin;
    long _animAt;
    int _animMs = 1;
    long _pageAt;           // cuándo cambió el contenido (entra apareciendo)
    long _overSince, _outSince, _hotSince;
    bool _pinned, _muted, _mouth, _clickOpened;
    long _swallowAt = -10_000;
    string? _hoverKey;

    // Datos
    IReadOnlyList<SessionInfo> _sessions = [];
    double? _sessionPercent, _weekPercent;
    string _model = "El de sus sesiones";
    string _account = "";
    IReadOnlyList<ChatTurn> _turns = [];
    string? _streaming;
    bool _asking;
    bool _chibi = true;

    // Análisis de un archivo
    string? _file;
    AssistantStage _stage;
    string _analysis = "";
    string? _analysisError;
    bool _analysisDone, _analysisSeen = true, _receiving;
    long _analysisAt, _analysisEnd;
    string _analysisModel = "";

    // Voz: escuchando (nivel, lo entendido y cómo terminar), pasando a texto o un aviso corto en el campo.
    bool _listening, _converting;
    float _level;
    string _heard = "", _voiceHint = "";
    string? _voiceNote;
    long _voiceNoteAt;

    int S(float v) => (int)Math.Round(v * _scale);

    public IslandForm()
    {
        _scale = DeviceDpi / 96f;
        _body = Fonts.Make("Segoe UI Variable Text", 9.75f, "Segoe UI");
        _bodyStrong = Fonts.Make("Segoe UI Variable Text Semibold", 9.75f, "Segoe UI Semibold");
        _small = Fonts.Make("Segoe UI Variable Text", 8.75f, "Segoe UI");
        _smallStrong = Fonts.Make("Segoe UI Variable Text Semibold", 8.75f, "Segoe UI Semibold");
        _glyph = Fonts.Make("Segoe Fluent Icons", 10f, "Segoe MDL2 Assets");
        _glyphTiny = Fonts.Make("Segoe Fluent Icons", 6.5f, "Segoe MDL2 Assets");
        _spinner = Fonts.Make("Segoe UI Symbol", 10.5f, "Segoe UI");
        _mono = Fonts.Make("Cascadia Mono", 8.5f, "Consolas");

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AllowDrop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

        _input = new IslandInput(this);
        _input.Sent += text => Asked?.Invoke(text);
        _input.Escaped += () => Collapse(force: true);

        _poll.Tick += (_, _) => Poll();
        _frame.Tick += (_, _) => Render();
        _poll.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Layered.ExLayered | 0x80 | 0x08000000; // capas, ventana de herramientas, sin activar
            return cp;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    // ── Lo que manda TrayApp ────────────────────────────────────────────────────────────────

    public void SetAccent(Color accent)
    {
        _accent = accent;
        Redraw();
    }

    public void SetMascot(bool chibi)
    {
        _chibi = chibi;
        Redraw();
    }

    public void SetSessions(IReadOnlyList<SessionInfo> sessions)
    {
        _sessions = sessions;
        Redraw(relayout: _shape == Shape.Open && _page == Page.Home);
    }

    public void SetUsage(double? session, double? week)
    {
        _sessionPercent = session;
        _weekPercent = week;
        Redraw();
    }

    /// <summary>El modelo de la isla, ya con nombre («Opus 5.5 · el de sus sesiones») y la cuenta que responde.</summary>
    public void SetModel(string model, string account)
    {
        _model = model;
        _account = account;
        Redraw();
    }

    /// <summary>La conversación (compartida con la mascota) y, si está llegando, la respuesta en curso.</summary>
    public void SetChat(IReadOnlyList<ChatTurn> turns, string? streaming, bool asking)
    {
        var now = Environment.TickCount64;
        if (turns.Count < _turns.Count || turns.Count == 0) // conversación nueva
        {
            _turnAt.Clear();
            _typedIndex = -1;
        }
        for (var i = _turns.Count; i < turns.Count; i++)
        {
            // La respuesta que se venía escribiendo pasa a la conversación sin volver a entrar: sigue escribiéndose.
            if (!turns[i].FromPerson && _streaming is not null && streaming is null)
            {
                _typedIndex = i;
                continue;
            }
            _turnAt[i] = now;
            if (!turns[i].FromPerson) { _typedIndex = i; _typedShown = 0; } // una respuesta sin partes (un error): igual se escribe
        }
        if (streaming is not null && _streaming is null) { _typedShown = 0; _thinkingAt = now; } // empieza otra respuesta
        _turns = turns;
        _streaming = streaming;
        _asking = asking;
        Redraw(relayout: _shape == Shape.Open && _page == Page.Chat);
    }

    // Animación del chat: cuándo entró cada mensaje (índice → TickCount64) y cuántas letras se ven de la
    // respuesta que se está escribiendo (la que llega por partes o, al terminar, la última).
    readonly Dictionary<int, long> _turnAt = new();
    int _typedIndex = -1;
    int _typedShown;
    long _thinkingAt;

    /// <summary>La respuesta que se está escribiendo todavía no se ve entera.</summary>
    bool Typing => _streaming is { Length: > 0 } s ? _typedShown < s.Length
        : _typedIndex >= 0 && _typedIndex < _turns.Count && _typedShown < _turns[_typedIndex].Text.Length;

    /// <summary>Avanza la escritura: unas 70 letras por segundo y, si se atrasa, más rápido.</summary>
    void AdvanceTyping()
    {
        var target = _streaming is { Length: > 0 } s ? s.Length
            : _typedIndex >= 0 && _typedIndex < _turns.Count ? _turns[_typedIndex].Text.Length : 0;
        if (_typedShown >= target) return;
        var behind = target - _typedShown;
        _typedShown = Math.Min(target, _typedShown + Math.Max(1, behind / 14));
        if (_shape == Shape.Open && _page == Page.Chat) Relayout(); // el globo crece con el texto
    }

    // ── Voz ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>El campo del chat lleva el botón del micrófono (Ajustes › Asistente › Micrófono).</summary>
    public bool MicEnabled
    {
        get => _micEnabled;
        set { _micEnabled = value; Redraw(); }
    }
    bool _micEnabled;

    public bool IsOpen => _shape == Shape.Open;

    /// <summary>Le están hablando: se abre en el chat, con el campo en rojo, el nivel y cómo se termina (hint).</summary>
    public void Listen(string hint, Screen? screen = null)
    {
        _listening = true;
        _converting = false;
        _heard = "";
        _voiceHint = hint;
        _level = 0;
        _voiceNote = null;
        ShowPage(Page.Chat, screen);
    }

    public void SetVoiceLevel(float level) => _level = Math.Max(_level, Math.Min(1, level * 2.2f));

    public void SetHeard(string text)
    {
        _heard = text;
        Redraw();
    }

    /// <summary>Soltó la tecla: el campo muestra lo entendido, con brillo, mientras se pasa todo a texto.</summary>
    public void ConvertingVoice()
    {
        _listening = false;
        _converting = true;
        Redraw();
    }

    /// <summary>Terminó de escuchar; note: un aviso corto en el campo (no se entendió, falta el modelo…).</summary>
    public void StopListening(string? note = null)
    {
        _listening = _converting = false;
        _voiceNote = note;
        _voiceNoteAt = Environment.TickCount64;
        Redraw();
    }

    bool VoiceNoteShown => _voiceNote is not null && Environment.TickCount64 - _voiceNoteAt < 4000;

    /// <summary>Mientras se le habla, o se pasa a texto, o hay un aviso, el campo lo dibuja la isla (sin el TextBox).</summary>
    bool VoiceInField => _listening || _converting || VoiceNoteShown;

    // ── Análisis de un archivo ──────────────────────────────────────────────────────────────

    /// <summary>La mascota viene caminando con un archivo: la isla asoma y espera.</summary>
    public void Receive(Screen screen)
    {
        _screen = screen;
        _receiving = true;
        if (_shape == Shape.Hidden) ShowShape(Shape.Compact);
        Redraw();
    }

    /// <summary>Abre la boca para recibir la caja (más grande y redonda).</summary>
    public void OpenMouth()
    {
        _mouth = true;
        if (_shape == Shape.Compact) Animate(MouthW, MouthH, 0, 200);
    }

    /// <summary>Dónde está la boca, en la pantalla: hacia ahí vuela la caja.</summary>
    public Point Mouth
    {
        get
        {
            var area = _screen.WorkingArea;
            return new Point(area.Left + area.Width / 2, area.Top + S(_mouth ? MouthH : CompactH) - S(4));
        }
    }

    public Screen Screen => _screen;

    /// <summary>Dónde queda la isla compacta (con la boca abierta) en esa pantalla, en pixeles: para planear el lanzamiento.</summary>
    public Rectangle MouthArea(Screen screen)
    {
        var area = screen.WorkingArea;
        return new Rectangle(area.Left + (area.Width - S(MouthW)) / 2, area.Top, S(MouthW), S(MouthH));
    }

    /// <summary>Llegó la caja: la isla rebota y se abre en «Analizando».</summary>
    public void Swallow() => _swallowAt = Environment.TickCount64;

    public void BeginAnalysis(string file)
    {
        _file = file;
        _stage = AssistantStage.Opening;
        _analysis = "";
        _analysisError = null;
        _analysisDone = false;
        _analysisSeen = false;
        _receiving = false;
        _mouth = false;
        _analysisAt = Environment.TickCount64;
        _analysisModel = _model;
        ShowPage(Page.Analysis);
    }

    public void SetAnalysisStage(AssistantStage stage)
    {
        _stage = stage;
        Redraw();
    }

    public void AppendAnalysis(string piece)
    {
        _analysis += piece;
        Redraw(relayout: _page == Page.Analysis);
    }

    public void FinishAnalysis(string text, string? error, string? model)
    {
        _analysis = text;
        _analysisError = error;
        _analysisDone = true;
        _analysisEnd = Environment.TickCount64;
        if (model is { Length: > 0 }) _analysisModel = model;
        Redraw(relayout: true);
    }

    public bool Analyzing => _file is not null && !_analysisDone;

    // ── Permisos y preguntas ────────────────────────────────────────────────────────────────

    ApprovalRequest? _approval;
    string _approvalTitle = "";
    TaskCompletionSource<ApprovalAnswer?>? _approvalTask;
    int _questionIndex;
    readonly Dictionary<string, List<string>> _picks = new();

    /// <summary>Hay un permiso o una pregunta esperando respuesta.</summary>
    public bool Pending => _approvalTask is not null;

    /// <summary>
    /// Muestra la tarjeta del pedido (sube sola, aunque la isla esté oculta) y espera la respuesta.
    /// Uno a la vez: si ya hay otro esperando, este va a la terminal (null).
    /// </summary>
    public Task<ApprovalAnswer?> AskApproval(ApprovalRequest request, string title, Screen? screen = null)
    {
        if (_approvalTask is not null) return Task.FromResult<ApprovalAnswer?>(null);
        _approval = request;
        _approvalTitle = title;
        _questionIndex = 0;
        _picks.Clear();
        _approvalTask = new TaskCompletionSource<ApprovalAnswer?>();
        ShowPage(request.IsQuestion ? Page.Question : Page.Permission, screen ?? Screen.FromPoint(Cursor.Position));
        return _approvalTask.Task;
    }

    /// <summary>Se venció el tiempo (o lo respondió en la terminal): la tarjeta se va sin respuesta.</summary>
    public void DropApproval() => ResolveApproval(null);

    void ResolveApproval(ApprovalAnswer? answer)
    {
        var task = _approvalTask;
        _approvalTask = null;
        _approval = null;
        if (_page is Page.Permission or Page.Question)
        {
            _clickOpened = false;
            ShowPage(Page.Home);
            _clickOpened = false;
        }
        task?.TrySetResult(answer);
    }

    /// <summary>La sesión del pedido, si está entre las que se leen (para su título y su terminal).</summary>
    SessionInfo? ApprovalSession => _approval is { } a ? _sessions.FirstOrDefault(s => s.SessionId == a.SessionId) : null;

    /// <summary>Abre la isla en una página (el chat, por ejemplo, desde «Ver en la isla»).</summary>
    public void ShowPage(Page page, Screen? screen = null)
    {
        if (screen is not null) _screen = screen;
        if (page == Page.Analysis) _analysisSeen = true;
        var changed = page != _page || _shape != Shape.Open;
        _page = page;
        _clickOpened = true;
        ShowShape(Shape.Open);
        if (changed) _pageAt = Environment.TickCount64;
        Relayout();
    }

    // ── Forma y animación ───────────────────────────────────────────────────────────────────

    void ShowShape(Shape shape)
    {
        if (shape == Shape.Open && _shape != Shape.Open) _pageAt = Environment.TickCount64;
        _shape = shape;
        if (!Visible && shape != Shape.Hidden) Show();
        switch (shape)
        {
            case Shape.Hidden:
                _input.Hide();
                Animate(_w, _h, -_h - ShadowMargin, 180);
                break;
            case Shape.Compact:
                _input.Hide();
                Animate(_mouth ? MouthW : CompactW, _mouth ? MouthH : CompactH, 0, _y < -1 ? 180 : 240);
                break;
            case Shape.Open:
                Animate(OpenW, MeasureOpen(), 0, 240);
                break;
        }
    }

    void Animate(float w, float h, float y, int ms)
    {
        (_fromW, _fromH, _fromY) = (_w, _h, _y);
        (_toW, _toH, _toY) = (w, h, y);
        _animAt = Environment.TickCount64;
        _animMs = AnimationsOn ? Math.Max(1, ms) : 1;
        _frame.Interval = 16;
        _frame.Start();
    }

    static bool AnimationsOn => SystemInformation.UIEffectsEnabled;

    double AnimProgress => Math.Clamp((Environment.TickCount64 - _animAt) / (double)_animMs, 0, 1);

    static double Ease(double p) => 1 - Math.Pow(1 - p, 3);

    void Step()
    {
        var k = (float)Ease(AnimProgress);
        _w = _fromW + (_toW - _fromW) * k;
        _h = _fromH + (_toH - _fromH) * k;
        _y = _fromY + (_toY - _fromY) * k;
        // El rebote al tragar la caja: crece un poco y vuelve.
        var swallow = (Environment.TickCount64 - _swallowAt) / 240.0;
        if (swallow is >= 0 and < 1)
        {
            var bump = (float)Math.Sin(Math.PI * swallow);
            _w += 12 * bump;
            _h += 9 * bump;
        }
    }

    /// <summary>Cambió el contenido de la página abierta: el alto se acomoda con suavidad.</summary>
    void Relayout()
    {
        if (_shape != Shape.Open) { Redraw(); return; }
        var height = MeasureOpen();
        if (Math.Abs(height - _toH) > 0.5f) Animate(OpenW, height, 0, 200);
        else Redraw();
    }

    void Redraw(bool relayout = false)
    {
        if (relayout) { Relayout(); return; }
        if (!Visible) return;
        if (!_frame.Enabled) { _frame.Interval = 16; _frame.Start(); }
    }

    /// <summary>¿Hay algo que se mueva en la isla (además del tamaño)? Si no, deja de dibujar.</summary>
    bool Animating =>
        AnimProgress < 1 || Environment.TickCount64 - _swallowAt < 260 || Environment.TickCount64 - _pageAt < 220
        || _shape == Shape.Open && (_sessions.Any(s => s.State is SessionState.Working or SessionState.Waiting) || _asking || Analyzing || Typing
            || _turnAt.Values.Any(t => Environment.TickCount64 - t < 260) || VoiceInField)
        || _shape == Shape.Compact; // en compacta, la cabeza parpadea

    // ── Cursor: asomar, abrir, cerrar ───────────────────────────────────────────────────────

    /// <summary>Lo que debería verse sin el cursor: compacta si algo trabaja (y está pedido), si analiza o recibe; si no, nada.</summary>
    Shape Resting
    {
        get
        {
            if (Analyzing || _receiving || Pending || (_file is not null && !_analysisSeen)) return Shape.Compact;
            if (WhileBusy && _sessions.Any(s => s.State is SessionState.Working or SessionState.Waiting)) return Shape.Compact;
            return Shape.Hidden;
        }
    }

    /// <summary>La isla queda compacta mientras algo trabaja (Ajustes › Isla).</summary>
    public bool WhileBusy { get; set; } = true;

    /// <summary>Se abre con el cursor encima (si no, con un clic).</summary>
    public bool OpenOnHover { get; set; } = true;

    void Poll()
    {
        var cursor = Cursor.Position;
        var now = Environment.TickCount64;
        var area = _screen.WorkingArea;

        // La franja sensible: en el centro del borde de arriba de cualquier pantalla.
        var hot = Screen.FromPoint(cursor) is var under
            && cursor.Y <= under.WorkingArea.Top + S(HotHeight)
            && Math.Abs(cursor.X - (under.WorkingArea.Left + under.WorkingArea.Width / 2)) <= S(HotWidth) / 2;
        var island = IslandScreenBounds();
        var over = Visible && _y > -2 && Rectangle.Inflate(island, S(8), S(8)).Contains(cursor);

        if (hot && _shape == Shape.Hidden)
        {
            if (_hotSince == 0) _hotSince = now;
            _screen = Screen.FromPoint(cursor);
            ShowShape(Shape.Compact);
        }
        else if (_shape == Shape.Hidden && Resting == Shape.Compact) ShowShape(Shape.Compact); // algo trabaja: queda a la vista
        if (!hot) _hotSince = 0;

        if (over)
        {
            _outSince = 0;
            if (_overSince == 0) _overSince = now;
            if (_shape == Shape.Compact && OpenOnHover && now - _overSince >= OpenDelay && !_mouth)
            {
                _clickOpened = false;
                ShowShape(Shape.Open);
                Relayout();
            }
        }
        else
        {
            _overSince = 0;
            if (_outSince == 0) _outSince = now;
            var away = now - _outSince;
            if (_shape == Shape.Open && away >= CloseDelay && CanFold && !(_clickOpened && away < CloseDelay * 3))
                Collapse(force: false);
            else if (_shape == Shape.Compact && Resting == Shape.Hidden && !_mouth && away >= HideDelay && !hot)
                ShowShape(Shape.Hidden);
        }

        if (_hoverKey is not null && !over) { _hoverKey = null; Redraw(); }
    }

    /// <summary>Se puede plegar: nada la retiene abierta (fijada, escribiendo, analizando o con el resultado recién llegado).</summary>
    bool CanFold =>
        !_pinned && !_input.Focused && _input.Value.Length == 0
        && !Analyzing && !(_page == Page.Analysis && Environment.TickCount64 - _analysisEnd < 5000) && !_asking
        && !_listening && !_converting
        && !(Pending && _page is Page.Permission or Page.Question) // una tarjeta no se pliega sola: se responde o se deja con ⌃
        && !(_page == Page.Week && Environment.TickCount64 - _pageAt < 15000); // el resumen de la semana se queda un rato

    void Collapse(bool force)
    {
        if (!force && !CanFold) return;
        _clickOpened = false;
        ShowShape(Resting == Shape.Hidden && !force ? Shape.Hidden : Shape.Compact);
        if (Resting == Shape.Hidden) _outSince = Environment.TickCount64; // ya compacta, se esconde después
    }

    Rectangle IslandScreenBounds()
    {
        var area = _screen.WorkingArea;
        var w = S(_w);
        var h = S(_h);
        return new Rectangle(area.Left + (area.Width - w) / 2, area.Top + S(_y), w, h);
    }

    // ── Dibujo ──────────────────────────────────────────────────────────────────────────────

    void Render()
    {
        Step();
        AdvanceTyping();
        _level *= 0.93f; // el nivel de la voz sube enseguida y baja de a poco
        if (_shape == Shape.Hidden && AnimProgress >= 1)
        {
            _frame.Stop();
            if (Visible) Hide();
            return;
        }
        if (!IsHandleCreated) CreateHandle();

        var w = S(_w);
        var h = S(_h);
        var m = S(ShadowMargin);
        using var bmp = Layered.Canvas(w + 2 * m, h + m);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var open = _shape == Shape.Open || (_toW >= OpenW - 1 && AnimProgress < 1);
            var radius = S(open ? 22 : 18) * Math.Min(1f, _h / 36f);
            DrawShadow(g, m, w, h, radius);
            using var shape = IslandPath(m, 0, w, h, radius);
            using (var fill = new SolidBrush(Ground)) g.FillPath(fill, shape);
            g.SetClip(shape);
            _hits.Clear();
            var bounds = new Rectangle(m, 0, w, h);

            // El contenido aparece cuando el tamaño ya casi llegó; al cambiar de página, la barra queda y lo de abajo se funde.
            var (barAlpha, bodyAlpha) = ContentAlpha(open);
            if (open)
            {
                Layer(g, bmp.Size, shape, barAlpha, lg => DrawOpen(lg, bounds, bar: true, body: false));
                Layer(g, bmp.Size, shape, bodyAlpha, lg => DrawOpen(lg, bounds, bar: false, body: true));
            }
            else Layer(g, bmp.Size, shape, barAlpha, lg => DrawCompact(lg, bounds));
        }
        var area = _screen.WorkingArea;
        var location = new Point(area.Left + (area.Width - w) / 2 - m, area.Top + S(_y));
        Layered.Show(Handle, bmp, location);
        PlaceInput();

        if (!Animating) _frame.Stop();
        else _frame.Interval = AnimProgress < 1 || Environment.TickCount64 - _pageAt < 220 || Environment.TickCount64 - _swallowAt < 260 ? 16 : _listening ? 33 : 50;
    }

    (float Bar, float Body) ContentAlpha(bool open)
    {
        var k = AnimProgress;
        var sizing = Math.Abs(_toW - _fromW) > 1 || Math.Abs(_toH - _fromH) > 30;
        var size = sizing ? (float)Math.Clamp((k - 0.45) / 0.55, 0, 1) : 1f;
        var page = (float)Math.Clamp((Environment.TickCount64 - _pageAt) / 180.0, 0, 1);
        return (size, open ? Math.Min(size, page) : size);
    }

    /// <summary>Dibuja algo con la transparencia pedida: directo si se ve entero; si no, en una capa aparte que se mezcla.</summary>
    static void Layer(Graphics g, Size size, GraphicsPath clip, float alpha, Action<Graphics> draw)
    {
        if (alpha <= 0.01f) return;
        if (alpha >= 0.99f) { draw(g); return; }
        using var layer = Layered.Canvas(size.Width, size.Height);
        using (var lg = Graphics.FromImage(layer))
        {
            lg.SmoothingMode = SmoothingMode.AntiAlias;
            lg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            lg.InterpolationMode = InterpolationMode.NearestNeighbor;
            lg.PixelOffsetMode = PixelOffsetMode.Half;
            lg.SetClip(clip);
            draw(lg);
        }
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(layer, new Rectangle(Point.Empty, size), 0, 0, size.Width, size.Height, GraphicsUnit.Pixel, attributes);
    }

    static GraphicsPath IslandPath(float x, float y, float w, float h, float r)
    {
        r = Math.Max(1, Math.Min(r, Math.Min(w / 2, h)));
        var path = new GraphicsPath();
        path.AddLine(x, y, x + w, y);
        path.AddLine(x + w, y, x + w, y + h - r);
        path.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        path.AddLine(x + w - r, y + h, x + r, y + h);
        path.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Una sombra suave bajo la isla: capas cada vez más anchas y más tenues.</summary>
    void DrawShadow(Graphics g, int m, int w, int h, float radius)
    {
        for (var i = m; i >= 1; i -= Math.Max(1, m / 7))
        {
            var a = (int)(26 * (1 - i / (float)(m + 1)));
            using var path = IslandPath(m - i, 0, w + 2 * i, h + i * 0.9f, radius + i);
            using var brush = new SolidBrush(Color.FromArgb(a, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    // ── Compacta ────────────────────────────────────────────────────────────────────────────

    void DrawCompact(Graphics g, Rectangle b)
    {
        var head = S(32);
        DrawHead(g, new Rectangle(b.X + S(6), b.Y + (b.Height - head) / 2, head, head), small: false);
        var x = b.X + S(6) + head + S(8);
        var right = b.Right - S(10);

        if (_sessionPercent is { } percent)
        {
            var label = $"{percent:0} %";
            var w = Measure(g, label, _smallStrong).Width + S(18);
            var pill = new RectangleF(right - w, b.Y + (b.Height - S(22)) / 2f, w, S(22));
            Fill(g, Color.FromArgb(46, _accent), pill, pill.Height / 2);
            Write(g, label, _smallStrong, Lighten(_accent), pill, StringAlignment.Center);
            right = (int)pill.X - S(8);
        }
        if (_sessions.Any(s => s.State == SessionState.Waiting))
        {
            var r = S(4);
            Pulse(g, new PointF(right - r, b.Y + b.Height / 2f), r, Warn);
            right -= 2 * r + S(8);
        }
        Write(g, CompactStatus(), _small, Soft, new RectangleF(x, b.Y, right - x, b.Height), StringAlignment.Near);
        Hit(b, "compact");
    }

    string CompactStatus()
    {
        if (_approval is { } pending) return pending.IsQuestion ? "Una pregunta le espera · clic para verla" : "Un permiso le espera · clic para verlo";
        if (_receiving) return "Recibiendo archivo…";
        if (Analyzing) return $"Analizando {Path.GetFileName(_file)}…";
        if (_file is not null && !_analysisSeen) return _analysisError is null ? "Análisis listo · clic para ver" : "No pude analizarlo · clic para ver";
        var working = _sessions.Count(s => s.State == SessionState.Working);
        var waiting = _sessions.Count(s => s.State == SessionState.Waiting);
        var parts = new List<string>();
        if (working > 0) parts.Add(working == 1 ? "1 trabajando" : $"{working} trabajando");
        if (waiting > 0) parts.Add(waiting == 1 ? "1 le espera" : $"{waiting} le esperan");
        if (parts.Count > 0) return string.Join(" · ", parts);
        var done = _sessions.Where(s => s.State == SessionState.Done).OrderByDescending(s => s.TurnEndedAt).FirstOrDefault();
        return done is not null ? $"«{Short(done.DisplayTitle, 28)}» terminó" : "Todo tranquilo";
    }

    /// <summary>La cabeza de la mascota: parpadea, mira a los lados mientras analiza y hace &gt; &lt; al terminar.</summary>
    void DrawHead(Graphics g, Rectangle box, bool small, bool looking = false, bool talking = false)
    {
        var tick = Environment.TickCount64;
        var frame = Analyzing || _receiving || looking ? (tick / 600 % 2 == 0 ? MascotFrame.Left : MascotFrame.Right)
            : talking ? (tick / 160 % 3 == 0 ? MascotFrame.Happy : MascotFrame.Open)
            : _file is not null && !_analysisSeen || tick - _analysisEnd < 3000 && _analysisDone ? MascotFrame.Happy
            : tick % 3600 > 3480 ? MascotFrame.Closed : MascotFrame.Open;
        if (_chibi)
        {
            var unit = Math.Max(1, box.Height / ChibiSprite.HeadSize);
            var size = ChibiSprite.HeadSize * unit;
            var x0 = box.X + (box.Width - size) / 2;
            var y0 = box.Y + (box.Height - size) / 2;
            ToastArt.Pixels(g, x0, y0, unit, fill => ChibiSprite.PaintHead(fill, frame));
        }
        else
        {
            var unit = Math.Max(1, box.Width / MascotSprite.Columns);
            var x0 = box.X + (box.Width - MascotSprite.Columns * unit) / 2;
            var y0 = box.Y + (box.Height - MascotSprite.Rows * unit) / 2;
            ToastArt.Pixels(g, x0, y0, unit, fill => MascotSprite.Paint(fill, frame, _accent));
        }
    }

    // ── Abierta ─────────────────────────────────────────────────────────────────────────────

    /// <summary>El alto de la isla abierta con la página actual (un recorrido sin dibujar).</summary>
    float MeasureOpen()
    {
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var hits = _hits.ToList();
        var bottom = ComposeOpen(g, new Rectangle(0, 0, S(OpenW), S(2000)), draw: false);
        _hits.Clear();
        _hits.AddRange(hits);
        return bottom / _scale;
    }

    /// <summary>La isla abierta: la barra de pestañas, la página o las dos.</summary>
    void DrawOpen(Graphics g, Rectangle b, bool bar, bool body)
    {
        var y = ComposeTopBar(g, b, draw: bar);
        if (!body) return;
        _ = _page switch
        {
            Page.Chat => ComposeChat(g, b, y, draw: true),
            Page.Analysis => ComposeAnalysis(g, b, y, draw: true),
            Page.Permission => ComposePermission(g, b, y, draw: true),
            Page.Question => ComposeQuestion(g, b, y, draw: true),
            Page.Diff => ComposeDiff(g, b, y, draw: true),
            Page.Week => ComposeWeek(g, b, y, draw: true),
            _ => ComposeHome(g, b, y, draw: true),
        };
    }

    /// <summary>Un recorrido sirve para medir el alto y para dibujar. Devuelve el borde de abajo.</summary>
    int ComposeOpen(Graphics g, Rectangle b, bool draw)
    {
        var y = ComposeTopBar(g, b, draw);
        y = _page switch
        {
            Page.Chat => ComposeChat(g, b, y, draw),
            Page.Analysis => ComposeAnalysis(g, b, y, draw),
            Page.Permission => ComposePermission(g, b, y, draw),
            Page.Question => ComposeQuestion(g, b, y, draw),
            Page.Diff => ComposeDiff(g, b, y, draw),
            Page.Week => ComposeWeek(g, b, y, draw),
            _ => ComposeHome(g, b, y, draw),
        };
        return y + S(14) - b.Y;
    }

    int ComposeTopBar(Graphics g, Rectangle b, bool draw)
    {
        var y = b.Y + S(8);
        var x = b.X + S(10);
        var height = S(28);
        foreach (var (key, label, page) in new (string, string, Page?)[] { ("tab:home", "Inicio", Page.Home), ("tab:chat", "Chat", Page.Chat), ("tab:new", "+ Sesión", null) })
        {
            var w = Measure(g, label, _small).Width + S(24);
            var rect = new Rectangle(x, y, (int)w, height);
            Hit(rect, key);
            if (draw)
            {
                var selected = page == _page;
                if (selected) Fill(g, Tab, rect, height / 2f);
                else if (_hoverKey == key) Fill(g, Color.FromArgb(0x24, 0x24, 0x22), rect, height / 2f);
                Write(g, label, _small, selected ? Ink : Dim, rect, StringAlignment.Center);
            }
            x = rect.Right + S(4);
        }

        var right = b.Right - S(10);
        foreach (var (key, glyph) in new[] { ("gear", ""), ("mute", _muted ? "" : ""), ("pin", _pinned ? "" : "") })
        {
            var rect = new Rectangle(right - S(30), y - S(1), S(30), S(30));
            Hit(rect, key);
            if (draw)
            {
                if (_hoverKey == key) Fill(g, Raised, rect, S(8));
                Write(g, glyph, _glyph, key == "pin" && _pinned ? _accent : Dim, rect, StringAlignment.Center);
            }
            right = rect.X;
        }
        right -= S(4);
        if (_weekPercent is { } week) right = UsagePill(g, $"Semana {week:0} %", week, right, y + (height - S(22)) / 2, draw, accent: false) - S(6);
        if (_sessionPercent is { } session) UsagePill(g, $"Sesión {session:0} %", session, right, y + (height - S(22)) / 2, draw, accent: true);
        return y + height + S(8);
    }

    int UsagePill(Graphics g, string label, double percent, int right, int y, bool draw, bool accent)
    {
        var w = Measure(g, label, accent ? _smallStrong : _small).Width + S(18);
        var rect = new RectangleF(right - w, y, w, S(22));
        if (draw)
        {
            var danger = percent >= 90;
            var color = danger ? Bad : _accent;
            Fill(g, accent || danger ? Color.FromArgb(46, color) : Raised, rect, rect.Height / 2);
            Write(g, label, accent ? _smallStrong : _small, accent || danger ? Lighten(color) : Soft, rect, StringAlignment.Center);
        }
        return (int)rect.X;
    }

    // ── Inicio: las sesiones ────────────────────────────────────────────────────────────────

    int ComposeHome(Graphics g, Rectangle b, int y, bool draw)
    {
        var list = _sessions.Where(s => s.State != SessionState.Idle).Take(4).ToList();
        var x = b.X + S(12);
        var width = b.Width - S(24);
        if (list.Count == 0)
        {
            if (draw)
            {
                Write(g, "No hay sesiones activas.", _body, Soft, new RectangleF(x + S(4), y + S(6), width, S(20)), StringAlignment.Near);
                Write(g, "Abra una con «+ Sesión», escríbale en el Chat o suelte un archivo aquí.", _small, Faint,
                    new RectangleF(x + S(4), y + S(28), width, S(18)), StringAlignment.Near);
            }
            return y + S(50);
        }
        foreach (var s in list) y = ComposeSession(g, s, x, y, width, draw) + S(6);
        return y - S(6);
    }

    int ComposeSession(Graphics g, SessionInfo s, int x, int y, int width, bool draw)
    {
        var top = y;
        var pad = S(10);
        var key = "sess:" + s.SessionId;
        var row = y + pad;
        var lineH = S(20);
        var textX = x + pad + S(24);
        var textW = width - pad * 2 - S(24);

        // Primera línea: estado, título, cuenta y modelo, y el botón de la terminal.
        var tag = s.Account.Label + (s.Model is { } model ? " · " + Format.Model(model) : "");
        var tagW = Measure(g, tag, _small).Width;
        var termW = Measure(g, "Terminal", _small).Width + S(18);
        var bodyY = row + lineH + S(4);
        var card = new Rectangle(x, top, width, ComposeSessionBody(g, s, textX, bodyY, textW, draw: false) + pad - top);
        if (draw)
        {
            var waiting = s.State == SessionState.Waiting;
            if (waiting)
            {
                Fill(g, Hex(0x24201A), card, S(12));
                Outline(g, Hex(0x5A4527), card, S(12));
            }
            else if (s.State == SessionState.Working) Fill(g, Card, card, S(12));
            else if (_hoverKey == key) Fill(g, Color.FromArgb(0x1A, 0x1A, 0x19), card, S(12));
            if (_hoverKey == key && s.State != SessionState.Done) Outline(g, Edge, card, S(12));

            StateIcon(g, s.State, new RectangleF(x + pad, row, S(16), lineH));
            var term = new Rectangle(x + width - pad - (int)termW, row - S(1), (int)termW, S(22));
            Write(g, s.DisplayTitle, _bodyStrong, Ink, new RectangleF(textX, row, textW - tagW - termW - S(16), lineH), StringAlignment.Near, ellipsis: true);
            Write(g, tag, _small, Dim, new RectangleF(term.X - S(8) - tagW, row, tagW + S(2), lineH), StringAlignment.Near);
            Outline(g, _hoverKey == "term:" + s.SessionId ? Soft : Edge, term, S(11));
            Write(g, "Terminal", _small, Soft, term, StringAlignment.Center);
            ComposeSessionBody(g, s, textX, bodyY, textW, draw: true);
        }
        Hit(card, key, s);
        Hit(new Rectangle(x + width - pad - (int)termW, row - S(1), (int)termW, S(22)), "term:" + s.SessionId, s);
        return card.Bottom;
    }

    /// <summary>Lo de abajo del título: los últimos pasos como fichas y el avance, o cómo terminó.</summary>
    int ComposeSessionBody(Graphics g, SessionInfo s, int x, int y, int width, bool draw)
    {
        var lineH = S(18);
        switch (s.State)
        {
            case SessionState.Working:
            {
                var groups = Flows.Groups(s.Flow).TakeLast(3).ToList();
                if (groups.Count > 0)
                {
                    var cx = x;
                    foreach (var group in groups)
                    {
                        var live = group == groups[^1] && !group.Done;
                        var edit = group.Kind == ToolKind.Edit && group.Count == 1 ? s.Edits.LastOrDefault(e => e.StepId == group.Last.Id) : null;
                        var label = edit is not null ? Path.GetFileName(edit.File)
                            : group.Count > 1 ? $"{group.Verb} ×{group.Count}" : group.Detail is { } d ? $"{group.Verb} {Short(d, 22)}" : group.Verb;
                        var plus = edit is null ? "" : $"+{edit.Added}";
                        var minus = edit is null || edit.Removed == 0 ? "" : $"−{edit.Removed}";
                        var counts = plus.Length == 0 ? 0 : Measure(g, plus + " " + minus, _small).Width + S(6);
                        var w = Measure(g, label, _small).Width + counts + S(live ? 26 : 16);
                        if (cx + w > x + width) break;
                        var chip = new RectangleF(cx, y, w, S(22));
                        if (edit is not null) Hit(Rectangle.Round(chip), "diff:" + edit.StepId, (s, edit));
                        if (draw)
                        {
                            var hover = edit is not null && _hoverKey == "diff:" + edit.StepId;
                            Fill(g, live ? Hex(0x2E2A24) : hover ? Tab : Raised, chip, S(6));
                            if (live) Pulse(g, new PointF(chip.X + S(10), chip.Y + chip.Height / 2), S(3), Warn);
                            var tx = chip.X + S(live ? 18 : 8);
                            var lw = Measure(g, label, _small).Width;
                            Write(g, label, _small, live ? Hex(0xF0C27A) : edit is not null ? Soft : Dim, new RectangleF(tx, chip.Y, lw + S(2), chip.Height), StringAlignment.Near);
                            if (plus.Length > 0)
                            {
                                var px = tx + lw + S(6);
                                Write(g, plus, _small, Good, new RectangleF(px, chip.Y, S(40), chip.Height), StringAlignment.Near);
                                if (minus.Length > 0)
                                    Write(g, minus, _small, Bad, new RectangleF(px + Measure(g, plus + " ", _small).Width, chip.Y, S(40), chip.Height), StringAlignment.Near);
                            }
                        }
                        cx += (int)w + S(6);
                    }
                    y += S(22) + S(6);
                }
                var progress = new List<string>();
                if (s.TodoTotal > 0) progress.Add($"{s.TodoDone} de {s.TodoTotal} tareas");
                progress.Add(s.Steps == 1 ? "1 paso" : $"{s.Steps} pasos");
                if (s.TurnStartedAt is { } start) progress.Add(Format.Duration(DateTimeOffset.Now - start));
                if (draw) Write(g, string.Join(" · ", progress), _small, Faint, new RectangleF(x, y, width, lineH), StringAlignment.Near);
                return y + lineH;
            }
            case SessionState.Waiting:
                if (draw) Write(g, s.AwaitingAnswer ? "Le está preguntando · clic para ver" : "Necesita su permiso · clic para ver", _small, Hex(0xF0C27A),
                    new RectangleF(x, y, width, lineH), StringAlignment.Near);
                return y + lineH;
            case SessionState.Failed:
                if (draw) Write(g, "Falló · " + (s.ErrorText ?? "error de la API"), _small, Bad, new RectangleF(x, y, width, lineH), StringAlignment.Near, ellipsis: true);
                return y + lineH;
            default:
                var done = "Terminó " + Format.Ago(s.TurnEndedAt ?? s.StatusAt) + (s.LastTurn is { } d2 ? $" · {Format.Duration(d2)}" : "");
                if (draw) Write(g, done, _small, Good, new RectangleF(x, y, width, lineH), StringAlignment.Near);
                if (s.LastAnswer is not { Length: > 0 } answer) return y + lineH;
                if (draw) Write(g, answer, _small, Dim, new RectangleF(x, y + lineH + S(2), width, lineH), StringAlignment.Near, ellipsis: true);
                return y + lineH * 2 + S(2);
        }
    }

    void StateIcon(Graphics g, SessionState state, RectangleF box)
    {
        switch (state)
        {
            case SessionState.Working:
                string[] frames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];
                Write(g, frames[(int)(Environment.TickCount64 / 110 % frames.Length)], _spinner, _accent, box, StringAlignment.Center);
                break;
            case SessionState.Waiting:
                Pulse(g, new PointF(box.X + box.Width / 2, box.Y + box.Height / 2), S(4), Warn);
                break;
            case SessionState.Failed:
                Write(g, "", _glyph, Bad, box, StringAlignment.Center);
                break;
            default:
                Write(g, "", _glyph, Good, box, StringAlignment.Center);
                break;
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────────────────────

    Rectangle _inputBounds;

    int ComposeChat(Graphics g, Rectangle b, int y, bool draw)
    {
        var x = b.X + S(12);
        var width = b.Width - S(24);
        y += S(2);

        var turns = _turns.TakeLast(6).ToList();
        if (turns.Count == 0 && _streaming is null)
        {
            if (draw) Write(g, "Pregúntele por su uso, sus sesiones o lo que quiera. También puede soltar un archivo aquí o pedirle una tarea.",
                _small, Faint, new RectangleF(x + S(4), y, width - S(8), S(36)), StringAlignment.Near, wrap: true);
            y += S(40);
        }
        else
        {
            // Que entren las últimas: como mucho unos 300 px de mensajes. Cada uno lleva su índice en la
            // conversación (para su animación de entrada) y la respuesta en curso se ve hasta donde va escrita.
            var first = _turns.Count - turns.Count;
            var items = turns.Select((t, n) => (t.FromPerson, Text: VisibleText(first + n, t.Text), Index: first + n, Thinking: false)).ToList();
            if (_streaming is not null)
                items.Add((false, _streaming.Length == 0 ? "" : _streaming[..Math.Min(_typedShown, _streaming.Length)], -1, _streaming.Length == 0 || _typedShown == 0));
            var heights = items.Select(i => i.Thinking ? S(36) : BubbleHeight(g, i.Text, i.FromPerson ? width - S(140) : width - S(80))).ToList();
            var budget = S(300);
            var start = items.Count;
            var used = 0;
            while (start > 0 && used + heights[start - 1] + S(8) <= budget) used += heights[--start] + S(8);
            start = Math.Min(start, items.Count - 1);
            for (var i = Math.Max(0, start); i < items.Count; i++)
            {
                var (person, text, index, thinking) = items[i];
                var max = person ? width - S(140) : width - S(80);
                var bubbleH = heights[i];
                // Entrada: sube unos pixeles mientras aparece (el mensaje enviado, desde el campo).
                var arrive = index >= 0 && _turnAt.TryGetValue(index, out var at) ? (float)Ease(Math.Clamp((Environment.TickCount64 - at) / 240.0, 0, 1)) : 1f;
                var lift = (1 - arrive) * S(person ? 14 : 8);
                var alpha = (int)(255 * arrive);
                if (thinking)
                {
                    // Pensando: el asterisco de Claude gira y el texto tiene un brillo que lo recorre.
                    var head = S(32);
                    var rect = new RectangleF(x + head + S(8), y, S(126), bubbleH);
                    if (draw)
                    {
                        DrawHead(g, new Rectangle(x, (int)rect.Bottom - head, head, head), small: false, looking: true);
                        Fill(g, Raised, rect, S(12));
                        string[] frames = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];
                        Write(g, frames[(int)((Environment.TickCount64 - _thinkingAt) / 110 % frames.Length)], _spinner, _accent,
                            new RectangleF(rect.X + S(10), rect.Y, S(18), rect.Height), StringAlignment.Center);
                        Shimmer(g, "Pensando…", _body, new RectangleF(rect.X + S(32), rect.Y, rect.Width - S(38), rect.Height));
                    }
                }
                else if (person)
                {
                    var size = MeasureWrapped(g, text, _body, max - S(22));
                    var bubbleW = size.Width + S(22);
                    var rect = new RectangleF(x + width - bubbleW, y + lift, bubbleW, bubbleH);
                    if (draw)
                    {
                        Fill(g, Color.FromArgb(alpha, Line), rect, S(12));
                        Write(g, text, _body, Color.FromArgb(alpha, Ink), RectangleF.Inflate(rect, -S(11), -S(7)), StringAlignment.Near, wrap: true);
                    }
                }
                else
                {
                    var size = MeasureWrapped(g, text, _body, max - S(22));
                    var bubbleW = size.Width + S(22);
                    var head = S(32);
                    var rect = new RectangleF(x + head + S(8), y + lift, bubbleW, bubbleH);
                    var writing = index < 0 || index == _typedIndex && Typing;
                    if (draw)
                    {
                        DrawHead(g, new Rectangle(x, (int)rect.Bottom - head, head, head), small: false, talking: writing);
                        Fill(g, Color.FromArgb(alpha, Raised), rect, S(12));
                        Write(g, text, _body, Color.FromArgb(alpha, Hex(0xE8E6DF)), RectangleF.Inflate(rect, -S(11), -S(7)), StringAlignment.Near, wrap: true);
                        if (writing)
                        {
                            // El cursor al final de lo escrito, parpadeando.
                            var inner = RectangleF.Inflate(rect, -S(11), -S(7));
                            var lineH = MeasureWrapped(g, "Ág", _body, S(400)).Height;
                            var lastW = LastLineWidth(g, text, (int)inner.Width);
                            if (Environment.TickCount64 / 450 % 2 == 0)
                            {
                                using var brush = new SolidBrush(_accent);
                                g.FillRectangle(brush, inner.X + Math.Min(inner.Width - S(6), lastW + S(2)), inner.Bottom - lineH + S(2), S(6), lineH - S(4));
                            }
                        }
                    }
                }
                y += bubbleH + S(8);
            }
        }

        // El modelo, la cuenta y «Nueva conversación».
        var chipLabel = _model;
        var chipW = Measure(g, chipLabel, _small).Width + S(30);
        var chip = new Rectangle(x, y, (int)chipW, S(24));
        Hit(chip, "model");
        if (draw)
        {
            Outline(g, _hoverKey == "model" ? Soft : Edge, chip, S(12));
            Write(g, chipLabel, _small, Soft, new RectangleF(chip.X + S(10), chip.Y, chipW - S(26), chip.Height), StringAlignment.Near);
            Chevron(g, new PointF(chip.Right - S(14), chip.Y + chip.Height / 2f), Soft);
            if (_account.Length > 0)
                Write(g, "con " + _account, _small, Faint, new RectangleF(chip.Right + S(8), chip.Y, S(160), chip.Height), StringAlignment.Near);
        }
        const string reset = "Nueva conversación";
        var resetW = Measure(g, reset, _small).Width + S(12);
        var resetRect = new Rectangle(x + width - (int)resetW, y, (int)resetW, S(24));
        Hit(resetRect, "newchat");
        if (draw) Write(g, reset, _small, _hoverKey == "newchat" ? Soft : Faint, resetRect, StringAlignment.Center);
        y += S(24) + S(8);

        // El campo (el texto lo escribe un TextBox encima), el micrófono y el botón de enviar.
        var field = new Rectangle(x, y, width, S(42));
        var send = new Rectangle(field.Right - S(36), field.Y + S(6), S(30), S(30));
        var mic = _micEnabled ? new Rectangle(send.X - S(34), send.Y, S(30), S(30)) : Rectangle.Empty;
        var textRight = _micEnabled ? mic.X : send.X;
        _inputBounds = new Rectangle(field.X + S(12), field.Y + S(11), textRight - field.X - S(20), S(20));
        Hit(send, "send");
        if (_micEnabled) Hit(mic, "mic");
        if (draw)
        {
            Fill(g, _listening ? Hex(0x241B1A) : Card, field, S(10));
            Outline(g, _listening ? Hex(0xD94B3F) : _input.Focused ? Color.FromArgb(160, _accent) : Edge, field, S(10));
            var text = new RectangleF(field.X + S(12), field.Y, textRight - field.X - S(20), field.Height);
            if (_listening) DrawListening(g, text);
            else if (_converting) Shimmer(g, _heard.Length > 0 ? Tail(g, _heard, _body, text.Width) : "Pasando a texto…", _body, text);
            else if (VoiceNoteShown) Write(g, _voiceNote!, _small, Warn, text, StringAlignment.Near, ellipsis: true);
            if (_micEnabled)
            {
                if (_listening) Fill(g, Hex(0xD94B3F), mic, S(8));
                else if (_hoverKey == "mic") Fill(g, Raised, mic, S(8));
                Write(g, "", _glyph, _listening ? Color.White : _hoverKey == "mic" ? Ink : Soft, mic, StringAlignment.Center);
            }
            Fill(g, _asking ? Raised : _accent, send, S(8));
            Write(g, _asking ? "" : "", _glyph, Color.White, send, StringAlignment.Center);
        }
        return field.Bottom;
    }

    /// <summary>Escuchando: el nivel de la voz en barritas, lo entendido hasta ahora y, a la derecha, cómo se termina.</summary>
    void DrawListening(Graphics g, RectangleF text)
    {
        var tick = Environment.TickCount64;
        var middle = text.Y + text.Height / 2;
        var periods = ChatBubble.BarPeriods;
        for (var i = 0; i < periods.Length; i++)
        {
            var wave = (float)(0.5 + 0.5 * Math.Sin(tick % periods[i] / (double)periods[i] * 2 * Math.PI + i));
            var h = Math.Max(S(4), S(20) * (0.35f + 0.65f * wave) * (0.2f + 0.8f * _level));
            Fill(g, Hex(0xE8695C), new RectangleF(text.X + i * S(6), middle - h / 2, S(3), h), S(1.5f));
        }
        var hintW = Measure(g, _voiceHint, _small).Width + S(4);
        var hint = new RectangleF(text.Right - hintW, text.Y, hintW, text.Height);
        Write(g, _voiceHint, _small, Hex(0xE8A39B), hint, StringAlignment.Far);
        var bars = periods.Length * S(6) + S(6);
        var said = new RectangleF(text.X + bars, text.Y, hint.X - text.X - bars - S(8), text.Height);
        Write(g, _heard.Length > 0 ? Tail(g, _heard, _body, said.Width) : "Escuchando…", _body, _heard.Length > 0 ? Ink : Faint, said, StringAlignment.Near);
    }

    /// <summary>El final de un texto que no cabe en una línea («…lo último que se dijo»).</summary>
    string Tail(Graphics g, string text, Font font, float width)
    {
        if (Measure(g, text, font).Width <= width) return text;
        while (text.Length > 1 && Measure(g, "…" + text, font).Width > width)
            text = text.IndexOf(' ') is var space and > 0 ? text[(space + 1)..] : text[1..];
        return "…" + text;
    }

    int BubbleHeight(Graphics g, string text, int max) => MeasureWrapped(g, text, _body, max - S(22)).Height + S(14);

    /// <summary>El texto de un mensaje tal como se ve: la respuesta que se está escribiendo, hasta donde va.</summary>
    string VisibleText(int index, string text) =>
        index == _typedIndex && _streaming is null ? text[..Math.Min(_typedShown, text.Length)] : text;

    // ── Análisis ────────────────────────────────────────────────────────────────────────────

    int ComposeAnalysis(Graphics g, Rectangle b, int y, bool draw)
    {
        var x = b.X + S(12);
        var width = b.Width - S(24);
        var card = new Rectangle(x, y + S(2), width, 0);
        var pad = S(14);
        var boxSize = S(54);
        var tx = card.X + pad + boxSize + S(14);
        var tw = card.Right - pad - tx;
        var ty = card.Y + pad;
        var name = Path.GetFileName(_file ?? "");

        // Título (con brillo mientras trabaja) y tiempo.
        var title = _analysisDone
            ? name
            : _stage switch
            {
                AssistantStage.Opening => $"Abriendo {name}",
                AssistantStage.Reading => $"Leyendo {name}",
                _ => "Escribiendo el resumen",
            };
        var elapsed = (_analysisDone ? _analysisEnd : Environment.TickCount64) - _analysisAt;
        var time = $"{elapsed / 60000}:{elapsed / 1000 % 60:00}";
        var timeW = Measure(g, time, _small).Width;
        var titleH = S(20);
        var row = ty;
        var stepsY = row + titleH + S(8);
        var textY = stepsY + S(16) + S(10);
        var text = _analysisError ?? (_analysis.Length > 0 ? _analysis : _analysisDone ? "" : "Claude está leyendo; el resumen aparece aquí mientras lo escribe.");
        var textH = Math.Max(S(38), MeasureWrapped(g, text, _body, tw).Height);
        var footY = textY + textH + S(10);
        var bottom = footY + S(_analysisDone ? 28 : 18) + pad;
        card.Height = bottom - card.Y;

        if (draw)
        {
            Fill(g, Card, card, S(14));
            // La caja: se abre una vez y la hoja sube y baja mientras lee.
            var frame = _analysisDone ? ChibiSprite.BoxFrame.Open
                : (Environment.TickCount64 - _analysisAt) switch
                {
                    < 500 => ChibiSprite.BoxFrame.Closed,
                    < 660 => ChibiSprite.BoxFrame.Opening,
                    var t => (t - 660) / 360 % 2 == 0 ? ChibiSprite.BoxFrame.Open : ChibiSprite.BoxFrame.PaperUp,
                };
            var unit = Math.Max(1, boxSize / ChibiSprite.BoxColumns);
            var bx = card.X + pad + (boxSize - ChibiSprite.BoxColumns * unit) / 2;
            ToastArt.Pixels(g, bx, ty, unit, fill => ChibiSprite.PaintBox(fill, frame));

            if (_analysisDone)
            {
                var nameW = Measure(g, title, _bodyStrong).Width;
                Write(g, title, _bodyStrong, Ink, new RectangleF(tx, row, Math.Min(nameW + S(4), tw - timeW - S(30)), titleH), StringAlignment.Near, ellipsis: true);
                var mark = new RectangleF(tx + Math.Min(nameW + S(6), tw - timeW - S(26)), row, S(16), titleH);
                Write(g, _analysisError is null ? "" : "", _glyph, _analysisError is null ? Good : Bad, mark, StringAlignment.Center);
            }
            else Shimmer(g, title, _bodyStrong, new RectangleF(tx, row, tw - timeW - S(10), titleH));
            Write(g, time, _small, Faint, new RectangleF(card.Right - pad - timeW, row, timeW + S(2), titleH), StringAlignment.Near);

            Stepper(g, tx, stepsY, tw);
            Write(g, text, _body, _analysisError is null ? Hex(0xE8E6DF) : Bad, new RectangleF(tx, textY, tw, textH), StringAlignment.Near, wrap: true);
            if (!_analysisDone && _stage == AssistantStage.Writing && Environment.TickCount64 / 450 % 2 == 0)
            {
                var end = MeasureWrapped(g, text, _body, tw);
                using var brush = new SolidBrush(_accent);
                g.FillRectangle(brush, tx + Math.Min(tw - S(8), LastLineWidth(g, text, tw) + S(2)), textY + end.Height - S(16), S(7), S(14));
            }
        }

        if (_analysisDone)
        {
            var ax = tx;
            foreach (var (key, label) in new[] { ("res:ask", "Preguntar algo más"), ("res:copy", "Copiar"), ("res:session", "Seguir en una sesión") })
            {
                var w = Measure(g, label, _small).Width + S(24);
                var rect = new Rectangle(ax, footY, (int)w, S(28));
                Hit(rect, key);
                if (draw)
                {
                    Outline(g, _hoverKey == key ? Soft : Edge, rect, S(14));
                    Write(g, label, _small, key == "res:ask" ? Lighten(_accent) : Soft, rect, StringAlignment.Center);
                }
                ax = rect.Right + S(8);
            }
        }
        else
        {
            var info = $"{_analysisModel} · solo puede leer la carpeta del archivo";
            const string cancel = "Cancelar · Esc";
            var cw = Measure(g, cancel, _small).Width + S(12);
            var cancelRect = new Rectangle(card.Right - pad - (int)cw, footY - S(2), (int)cw, S(22));
            Hit(cancelRect, "cancel");
            if (draw)
            {
                Write(g, info, _small, Faint, new RectangleF(tx, footY, tw - cw - S(10), S(18)), StringAlignment.Near, ellipsis: true);
                Write(g, cancel, _small, _hoverKey == "cancel" ? Soft : Dim, cancelRect, StringAlignment.Center);
            }
        }
        return card.Bottom;
    }

    // ── Atajos de teclado ───────────────────────────────────────────────────────────────────

    /// <summary>Abre el chat con el campo listo para escribir.</summary>
    public void FocusChat(Screen? screen = null)
    {
        ShowPage(Page.Chat, screen ?? Screen.FromPoint(Cursor.Position));
        _input.FocusText();
    }

    /// <summary>Va al permiso o a la pregunta que espera, si hay.</summary>
    public void ShowPending()
    {
        if (_approval is { } a) ShowPage(a.IsQuestion ? Page.Question : Page.Permission, Screen.FromPoint(Cursor.Position));
    }

    /// <summary>Abre la isla (en la página de antes) o la pliega.</summary>
    public void ToggleOpen()
    {
        if (_shape == Shape.Open) Collapse(force: true);
        else ShowPage(_page, Screen.FromPoint(Cursor.Position));
    }

    public void ToggleMute()
    {
        _muted = !_muted;
        Redraw();
    }

    // ── Resumen de la semana ────────────────────────────────────────────────────────────────

    WeekRecap? _week;

    public void ShowWeek(WeekRecap recap)
    {
        _week = recap;
        ShowPage(Page.Week, Screen.FromPoint(Cursor.Position));
    }

    int ComposeWeek(Graphics g, Rectangle b, int y, bool draw)
    {
        if (_week is not { } w) return y;
        var x = b.X + S(12);
        var width = b.Width - S(24);
        var pad = S(14);
        var top = y + S(2);
        var head = S(48);
        var tx = x + pad + head + S(14);
        var tw = x + width - pad - tx;
        var row = top + pad;
        var stats = new (string Value, string Label)[]
        {
            (Format.Tokens(w.Tokens), "tokens"),
            (w.Sessions.ToString(), w.Sessions == 1 ? "sesión" : "sesiones"),
            (w.Prompts.ToString(), w.Prompts == 1 ? "pedido" : "pedidos"),
            (w.ToolCalls.ToString(), "pasos"),
            (w.BusiestDay is { } d ? $"{d:dddd}" : "—", "día más activo"),
            (w.FavoriteModel ?? "—", "modelo favorito"),
        };
        var cellW = tw / 3;
        var gridY = row + S(28);
        var rowH = S(40);
        var buttonsY = gridY + 2 * rowH + S(4);
        var card = new Rectangle(x, top, width, buttonsY + S(28) + pad - top);
        if (draw)
        {
            Fill(g, Card, card, S(14));
            DrawHead(g, new Rectangle(x + pad, row, head, head), small: false, talking: false);
            Write(g, "Su semana con Claude", _bodyStrong, Ink, new RectangleF(tx, row, tw * 0.55f, S(22)), StringAlignment.Near);
            Write(g, w.Range, _small, Dim, new RectangleF(tx + tw * 0.55f, row, tw * 0.45f, S(22)), StringAlignment.Far, ellipsis: true);
            for (var i = 0; i < stats.Length; i++)
            {
                var cx = tx + i % 3 * cellW;
                var cy = gridY + i / 3 * rowH;
                Write(g, stats[i].Value, _bodyStrong, Ink, new RectangleF(cx, cy, cellW - S(8), S(20)), StringAlignment.Near, ellipsis: true);
                Write(g, stats[i].Label, _small, Dim, new RectangleF(cx, cy + S(19), cellW - S(8), S(18)), StringAlignment.Near);
            }
        }
        var bx = tx;
        foreach (var (key, label) in new[] { ("week:copy", "Copiar"), ("week:close", "Listo") })
        {
            var bw = Measure(g, label, _small).Width + S(26);
            var rect = new Rectangle(bx, buttonsY, (int)bw, S(28));
            Hit(rect, key);
            if (draw)
            {
                Outline(g, _hoverKey == key ? Soft : Edge, rect, S(14));
                Write(g, label, _small, key == "week:copy" ? Lighten(_accent) : Soft, rect, StringAlignment.Center);
            }
            bx = rect.Right + S(8);
        }
        return card.Bottom;
    }

    // ── El diff de un archivo ───────────────────────────────────────────────────────────────

    (SessionInfo Session, FileEdit Edit)? _diff;

    int ComposeDiff(Graphics g, Rectangle b, int y, bool draw)
    {
        if (_diff is not { } d) return y;
        var x = b.X + S(12);
        var width = b.Width - S(24);
        var row = y + S(2);
        var back = new Rectangle(x, row, S(28), S(28));
        Hit(back, "diff:back");
        var name = d.Edit.File.Replace(Account.Home, "~");
        const string open = "Abrir en VS Code ↗";
        var openW = Measure(g, open, _small).Width + S(24);
        var openRect = new Rectangle(x + width - (int)openW, row, (int)openW, S(28));
        Hit(openRect, "diff:open");
        if (draw)
        {
            Fill(g, _hoverKey == "diff:back" ? Tab : Raised, back, S(8));
            Write(g, "\uE72B", _glyph, Soft, back, StringAlignment.Center); // atrás
            var counts = $"+{d.Edit.Added}" + (d.Edit.Removed > 0 ? $"  −{d.Edit.Removed}" : "");
            var countsW = Measure(g, counts, _smallStrong).Width;
            var nameW = Math.Min(Measure(g, Path.GetFileName(name), _bodyStrong).Width, width - openW - countsW - S(60));
            Write(g, Path.GetFileName(name), _bodyStrong, Ink, new RectangleF(back.Right + S(10), row, nameW + S(2), S(28)), StringAlignment.Near, ellipsis: true);
            var cx = back.Right + S(10) + nameW + S(10);
            Write(g, $"+{d.Edit.Added}", _smallStrong, Good, new RectangleF(cx, row, S(50), S(28)), StringAlignment.Near);
            if (d.Edit.Removed > 0)
                Write(g, $"−{d.Edit.Removed}", _smallStrong, Bad, new RectangleF(cx + Measure(g, $"+{d.Edit.Added}  ", _smallStrong).Width, row, S(50), S(28)), StringAlignment.Near);
            Outline(g, _hoverKey == "diff:open" ? Soft : Edge, openRect, S(14));
            Write(g, open, _small, Soft, openRect, StringAlignment.Center);
        }

        // Las líneas: verdes las que suma, rojas las que quita; como mucho 18, el resto se cuenta.
        var top = row + S(36);
        var lineH = (int)Math.Ceiling(_mono.GetHeight(g)) + S(2);
        var lines = d.Edit.Lines.Take(18).ToList();
        var rest = d.Edit.Lines.Count - lines.Count;
        var box = new Rectangle(x, top, width, S(12) + lines.Count * lineH + (rest > 0 ? lineH : 0));
        if (draw)
        {
            Fill(g, Card, box, S(10));
            var ly = box.Y + S(6);
            foreach (var line in lines)
            {
                var sign = line.Length > 0 ? line[0] : ' ';
                var band = new RectangleF(box.X, ly, box.Width, lineH);
                if (sign == '+') using (var brush = new SolidBrush(Color.FromArgb(46, Good))) g.FillRectangle(brush, band);
                if (sign == '-') using (var brush = new SolidBrush(Color.FromArgb(42, Bad))) g.FillRectangle(brush, band);
                var color = sign == '+' ? Hex(0xC3D4A5) : sign == '-' ? Hex(0xF2A79E) : Dim;
                using var format = new StringFormat(Typographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                using var ink = new SolidBrush(color);
                g.DrawString(line, _mono, ink, new RectangleF(box.X + S(10), ly, box.Width - S(20), lineH), format);
                ly += lineH;
            }
            if (rest > 0) Write(g, $"… {rest} líneas más", _small, Faint, new RectangleF(box.X + S(10), ly, box.Width - S(20), lineH), StringAlignment.Near);
        }
        return box.Bottom;
    }

    // ── Tarjetas de permiso y de pregunta ───────────────────────────────────────────────────

    /// <summary>La cabeza con el «!» ámbar de algo que espera respuesta.</summary>
    void AlertHead(Graphics g, Rectangle box)
    {
        DrawHead(g, box, small: false, looking: true);
        var badge = new RectangleF(box.Right - S(13), box.Y - S(3), S(16), S(16));
        using (var brush = new SolidBrush(Warn)) g.FillEllipse(brush, badge);
        Write(g, "!", _smallStrong, Card, badge, StringAlignment.Center);
    }

    /// <summary>El botón ⌃ de la esquina: deja la tarjeta para después, sin responder.</summary>
    void FoldButton(Graphics g, int right, int y, bool draw)
    {
        var rect = new Rectangle(right - S(28), y - S(4), S(28), S(28));
        Hit(rect, "ap:fold");
        if (!draw) return;
        if (_hoverKey == "ap:fold") Fill(g, Raised, rect, S(8));
        Write(g, "\uE70E", _glyph, Dim, rect, StringAlignment.Center); // ChevronUp
    }

    int ComposePermission(Graphics g, Rectangle b, int y, bool draw)
    {
        if (_approval is not { } a) return y;
        var x = b.X + S(12);
        var width = b.Width - S(24);
        var pad = S(14);
        var head = S(48);
        var tx = x + pad + head + S(14);
        var tw = x + width - pad - tx;
        var top = y + S(2);
        var row = top + pad;

        var title = ApprovalSession?.DisplayTitle ?? _approvalTitle;
        var titleW = Math.Min(Measure(g, title, _bodyStrong).Width, tw * 0.55f);
        var what = $"necesita su permiso para {a.Tool}";
        var code = a.Target.ReplaceLineEndings(" ⏎ ");
        var codeH = Math.Min(MeasureWrapped(g, code, _mono, tw - S(20)).Height, S(52)) + S(14);
        var codeY = row + S(24);
        var buttonsY = codeY + codeH + S(10);
        var card = new Rectangle(x, top, width, buttonsY + S(32) + pad - top);
        if (draw)
        {
            Fill(g, Card, card, S(14));
            AlertHead(g, new Rectangle(x + pad, row, head, head));
            Write(g, title, _bodyStrong, Ink, new RectangleF(tx, row, titleW + S(2), S(20)), StringAlignment.Near, ellipsis: true);
            Write(g, what, _small, Dim, new RectangleF(tx + titleW + S(6), row, tw - titleW - S(40), S(20)), StringAlignment.Near, ellipsis: true);
            var box = new RectangleF(tx, codeY, tw, codeH);
            Fill(g, Raised, box, S(8));
            using var format = new StringFormat(Typographic) { Trimming = StringTrimming.EllipsisCharacter };
            using var brush = new SolidBrush(Ink);
            g.DrawString(code, _mono, brush, RectangleF.Inflate(box, -S(10), -S(7)), format);
        }
        FoldButton(g, x + width - pad + S(6), row, draw);

        var bx = tx;
        foreach (var (key, label, primary) in new[] { ("ap:deny", "Denegar", false), ("ap:allow", "Permitir", true), ("ap:always", "Siempre en esta sesión", false) })
        {
            var w = Measure(g, label, primary ? _smallStrong : _small).Width + S(26);
            var rect = new Rectangle(bx, buttonsY, (int)w, S(32));
            Hit(rect, key);
            if (draw)
            {
                var hover = _hoverKey == key;
                Fill(g, primary ? (hover ? Color.White : Ink) : (hover ? Tab : Line), rect, rect.Height / 2f);
                Write(g, label, primary ? _smallStrong : _small, primary ? Card : Ink, rect, StringAlignment.Center);
            }
            bx = rect.Right + S(8);
        }
        const string terminal = "Abrir terminal";
        var termW = Measure(g, terminal, _small).Width + S(12);
        var termRect = new Rectangle(x + width - pad - (int)termW, buttonsY, (int)termW, S(32));
        if (bx < termRect.X)
        {
            Hit(termRect, "ap:term");
            if (draw) Write(g, terminal, _small, _hoverKey == "ap:term" ? Soft : Dim, termRect, StringAlignment.Center);
        }
        return card.Bottom;
    }

    int ComposeQuestion(Graphics g, Rectangle b, int y, bool draw)
    {
        if (_approval?.Questions is not { } questions || _questionIndex >= questions.Count) return y;
        var q = questions[_questionIndex];
        var x = b.X + S(12);
        var width = b.Width - S(24);
        var pad = S(14);
        var top = y + S(2);
        var row = top + pad;
        var inner = width - pad * 2;

        var title = ApprovalSession?.DisplayTitle ?? _approvalTitle;
        var header = questions.Count > 1 ? $"{title} pregunta · {_questionIndex + 1} de {questions.Count}" : $"{title} pregunta";
        var qy = row + S(40);
        var qh = MeasureWrapped(g, q.Question, _bodyStrong, inner).Height;
        var gridY = qy + qh + S(10);
        var cols = 2;
        var cellW = (inner - S(8)) / cols;
        var cells = new List<(Rectangle Rect, string Label, string Description)>();
        var rowY = gridY;
        for (var i = 0; i < q.Options.Count; i += cols)
        {
            var heights = Enumerable.Range(i, Math.Min(cols, q.Options.Count - i)).Select(k =>
                S(18) + (q.Options[k].Description.Length > 0 ? Math.Min(MeasureWrapped(g, q.Options[k].Description, _small, cellW - S(22)).Height, S(34)) : 0) + S(16)).ToList();
            var h = heights.Max();
            for (var c = 0; c < heights.Count; c++)
                cells.Add((new Rectangle(x + pad + c * (cellW + S(8)), rowY, cellW, h), q.Options[i + c].Label, q.Options[i + c].Description));
            rowY += h + S(8);
        }
        var footY = rowY + S(2);
        var card = new Rectangle(x, top, width, footY + S(30) + pad - top);
        var chosen = _picks.TryGetValue(q.Question, out var list) ? list : [];

        if (draw)
        {
            Fill(g, Card, card, S(14));
            AlertHead(g, new Rectangle(x + pad, row, S(32), S(32)));
            Write(g, header, _small, Dim, new RectangleF(x + pad + S(42), row, inner - S(80), S(32)), StringAlignment.Near, ellipsis: true);
            Write(g, q.Question, _bodyStrong, Ink, new RectangleF(x + pad, qy, inner, qh + S(2)), StringAlignment.Near, wrap: true);
        }
        FoldButton(g, x + width - pad + S(6), row + S(6), draw);
        for (var i = 0; i < cells.Count; i++)
        {
            var (rect, label, description) = cells[i];
            var key = "q:opt:" + i;
            Hit(rect, key, label);
            if (!draw) continue;
            var selected = chosen.Contains(label);
            Fill(g, selected ? Color.FromArgb(30, _accent) : _hoverKey == key ? Raised : Card, rect, S(10));
            Outline(g, selected ? _accent : _hoverKey == key ? Soft : Edge, rect, S(10));
            Write(g, label, _smallStrong, Ink, new RectangleF(rect.X + S(11), rect.Y + S(8), rect.Width - S(22), S(18)), StringAlignment.Near, ellipsis: true);
            if (description.Length > 0)
                Write(g, description, _small, Dim, new RectangleF(rect.X + S(11), rect.Y + S(26), rect.Width - S(22), rect.Height - S(32)), StringAlignment.Near, wrap: true);
        }

        // Elegir varias: «Siguiente» o «Enviar» confirma; una sola: el clic ya responde.
        var fx = x + pad;
        if (q.MultiSelect)
        {
            var label = _questionIndex == questions.Count - 1 ? "Enviar" : "Siguiente";
            var w = Measure(g, label, _smallStrong).Width + S(28);
            var rect = new Rectangle(fx, footY, (int)w, S(30));
            if (chosen.Count > 0) Hit(rect, "q:next");
            if (draw)
            {
                Fill(g, chosen.Count > 0 ? (_hoverKey == "q:next" ? Color.White : Ink) : Line, rect, rect.Height / 2f);
                Write(g, label, _smallStrong, chosen.Count > 0 ? Card : Faint, rect, StringAlignment.Center);
            }
            fx = rect.Right + S(12);
        }
        const string terminal = "Responder en la terminal";
        var tw = Measure(g, terminal, _small).Width + S(12);
        var termRect = new Rectangle(fx, footY, (int)tw, S(30));
        Hit(termRect, "ap:terminal");
        if (draw) Write(g, terminal, _small, _hoverKey == "ap:terminal" ? Soft : Dim, termRect, StringAlignment.Center);
        return card.Bottom;
    }

    /// <summary>Elegir una opción: si se puede una sola, responde y pasa a la siguiente pregunta (o termina).</summary>
    void PickOption(string label)
    {
        if (_approval?.Questions is not { } questions || _questionIndex >= questions.Count) return;
        var q = questions[_questionIndex];
        if (!_picks.TryGetValue(q.Question, out var list)) _picks[q.Question] = list = new List<string>();
        if (q.MultiSelect)
        {
            if (!list.Remove(label)) list.Add(label);
            Redraw();
            return;
        }
        list.Clear();
        list.Add(label);
        Redraw();
        NextQuestion();
    }

    void NextQuestion()
    {
        if (_approval?.Questions is not { } questions) return;
        if (_questionIndex < questions.Count - 1)
        {
            _questionIndex++;
            _pageAt = Environment.TickCount64; // la pregunta siguiente entra apareciendo
            Relayout();
            return;
        }
        var picks = _picks.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.ToList());
        ResolveApproval(new ApprovalAnswer(ApprovalChoice.Answered, picks));
    }

    /// <summary>Los pasos reales del análisis: Abrir, Leer y Resumir (hecho, en curso o pendiente).</summary>
    void Stepper(Graphics g, int x, int y, int width)
    {
        string[] labels = ["Abrir", "Leer", "Resumir"];
        var current = _analysisDone ? 3 : (int)_stage;
        var cx = (float)x;
        var dot = S(16);
        for (var i = 0; i < labels.Length; i++)
        {
            var done = i < current;
            var active = i == current;
            var circle = new RectangleF(cx, y, dot, dot);
            if (done)
            {
                using (var brush = new SolidBrush(Good)) g.FillEllipse(brush, circle);
                Write(g, "", _glyphTiny, Card, circle, StringAlignment.Center);
            }
            else
            {
                var alpha = active ? (int)(140 + 115 * (0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 160.0))) : 255;
                using var pen = new Pen(active ? Color.FromArgb(alpha, _accent) : Hex(0x4A4946), S(2));
                g.DrawEllipse(pen, RectangleF.Inflate(circle, -S(1), -S(1)));
            }
            cx += dot + S(6);
            var lw = Measure(g, labels[i], _small).Width;
            Write(g, labels[i], _small, done ? Hex(0xC3D4A5) : active ? Ink : Faint, new RectangleF(cx, y - S(1), lw + S(2), dot + S(2)), StringAlignment.Near);
            cx += lw + S(8);
            if (i < labels.Length - 1)
            {
                var segment = Math.Max(S(18), (width - S(3 * 16 + 6 * 3 + 8 * 3) - labels.Sum(l => Measure(g, l, _small).Width)) / 2f);
                using var pen = new Pen(i < current - 0 && done ? Good : Line, S(2));
                g.DrawLine(pen, cx, y + dot / 2f, cx + segment - S(8), y + dot / 2f);
                cx += segment;
            }
        }
    }

    // ── Mouse y archivos ────────────────────────────────────────────────────────────────────

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitAt(e.Location);
        Cursor = hit is null ? Cursors.Default : Cursors.Hand;
        if (hit?.Key == _hoverKey) return;
        _hoverKey = hit?.Key;
        Redraw();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left || HitAt(e.Location) is not { } hit) return;
        switch (hit.Key)
        {
            case "compact":
                ShowPage(_approval is { } a ? (a.IsQuestion ? Page.Question : Page.Permission)
                    : _file is not null && !_analysisSeen ? Page.Analysis : _page);
                break;
            case "ap:allow": ResolveApproval(new ApprovalAnswer(ApprovalChoice.Allow)); break;
            case "ap:deny": ResolveApproval(new ApprovalAnswer(ApprovalChoice.Deny)); break;
            case "ap:always": ResolveApproval(new ApprovalAnswer(ApprovalChoice.Always)); break;
            case "ap:terminal": ResolveApproval(null); break;
            case "ap:fold":
                _clickOpened = false;
                ShowPage(Page.Home);
                Collapse(force: true);
                break;
            case "ap:term":
                if (ApprovalSession is { } session) TerminalClicked?.Invoke(session);
                break;
            case "q:next": NextQuestion(); break;
            case "diff:back": ShowPage(Page.Home); break;
            case "week:copy":
                if (_week is { } week) try { Clipboard.SetText(week.Text); } catch (Exception) { }
                break;
            case "week:close": ShowPage(Page.Home); break;
            case "diff:open":
                if (_diff is { } open) OpenFileClicked?.Invoke(open.Edit.File);
                break;
            case "tab:home": ShowPage(Page.Home); break;
            case "tab:chat": ShowPage(Page.Chat); _input.FocusText(); break;
            case "tab:new": NewSessionClicked?.Invoke(); break;
            case "gear": SettingsClicked?.Invoke(); break;
            case "mute": _muted = !_muted; Redraw(); break;
            case "pin": _pinned = !_pinned; Redraw(); break;
            case "model": ModelClicked?.Invoke(); break;
            case "mic": MicClicked?.Invoke(); break;
            case "newchat": NewChatClicked?.Invoke(); break;
            case "send":
                if (_asking) CancelClicked?.Invoke();
                else _input.Send();
                break;
            case "cancel": CancelClicked?.Invoke(); break;
            case "res:ask": ResultAction?.Invoke("ask"); break;
            case "res:copy": ResultAction?.Invoke("copy"); break;
            case "res:session": ResultAction?.Invoke("session"); break;
            default:
                if (hit.Key.StartsWith("q:opt:") && hit.Tag is string label) PickOption(label);
                else if (hit.Key.StartsWith("diff:") && hit.Tag is ValueTuple<SessionInfo, FileEdit> shown)
                {
                    _diff = (shown.Item1, shown.Item2);
                    ShowPage(Page.Diff);
                }
                else if (hit.Key.StartsWith("term:") && hit.Tag is SessionInfo t) TerminalClicked?.Invoke(t);
                else if (hit.Key.StartsWith("sess:") && hit.Tag is SessionInfo s) SessionClicked?.Invoke(s);
                break;
        }
    }

    /// <summary>Silenciada desde su botón (los avisos no suenan).</summary>
    public bool Muted => _muted;

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;
        e.Effect = DragDropEffects.Copy;
        if (_shape == Shape.Hidden) ShowShape(Shape.Compact);
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _screen = Screen.FromPoint(Cursor.Position);
            Swallow();
            FilesDropped?.Invoke(files);
        }
    }

    (Rectangle Bounds, string Key, object? Tag)? HitAt(Point p)
    {
        for (var i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Bounds.Contains(p)) return _hits[i];
        return null;
    }

    void Hit(Rectangle bounds, string key, object? tag = null) => _hits.Add((bounds, key, tag));

    // ── El campo de texto ───────────────────────────────────────────────────────────────────

    /// <summary>El campo va encima del dibujo, en coordenadas de pantalla; solo con el chat abierto y quieto.</summary>
    void PlaceInput()
    {
        var show = _shape == Shape.Open && _page == Page.Chat && AnimProgress >= 1 && _inputBounds.Width > 0 && !VoiceInField;
        if (!show)
        {
            if (_input.Visible) _input.Hide();
            return;
        }
        var area = _screen.WorkingArea;
        var origin = new Point(area.Left + (area.Width - S(_w)) / 2 - S(ShadowMargin), area.Top + S(_y));
        var bounds = new Rectangle(origin.X + _inputBounds.X, origin.Y + _inputBounds.Y, _inputBounds.Width, _inputBounds.Height);
        _input.Place(bounds, _asking ? "Respondiendo… (clic en el botón para cancelar)" : "Pregunte algo o suelte un archivo aquí…");
    }

    // ── Ayudas de dibujo ────────────────────────────────────────────────────────────────────

    static readonly StringFormat Typographic = StringFormat.GenericTypographic;

    SizeF Measure(Graphics g, string text, Font font) =>
        g.MeasureString(text.Length == 0 ? " " : text, font, PointF.Empty, Typographic);

    Size MeasureWrapped(Graphics g, string text, Font font, int width)
    {
        using var format = new StringFormat(Typographic) { Trimming = StringTrimming.Word };
        var size = g.MeasureString(text.Length == 0 ? " " : text, font, Math.Max(1, width), format);
        return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    /// <summary>Lo que mide la última línea de un texto envuelto (para poner el cursor al final).</summary>
    int LastLineWidth(Graphics g, string text, int width)
    {
        var words = text.Split(' ');
        var line = "";
        foreach (var word in words)
        {
            var test = line.Length == 0 ? word : line + " " + word;
            if (Measure(g, test, _body).Width > width && line.Length > 0) line = word;
            else line = test;
        }
        return (int)Measure(g, line, _body).Width;
    }

    void Write(Graphics g, string text, Font font, Color color, RectangleF bounds, StringAlignment align, bool ellipsis = false, bool wrap = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(Typographic)
        {
            Alignment = align,
            LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center,
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.Word,
            FormatFlags = (wrap ? 0 : StringFormatFlags.NoWrap) | StringFormatFlags.MeasureTrailingSpaces,
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    /// <summary>Texto con un brillo que lo recorre de izquierda a derecha (como el «pensando» de las apps de Claude).</summary>
    void Shimmer(Graphics g, string text, Font font, RectangleF bounds)
    {
        var width = Math.Max(S(60), Measure(g, text, font).Width);
        var phase = Environment.TickCount64 % 1800 / 1800f;
        var center = bounds.X - width * 0.3f + phase * width * 1.6f;
        using var brush = new LinearGradientBrush(new RectangleF(center - S(70), bounds.Y, S(140), bounds.Height), Dim, Dim, LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend { Colors = [Dim, Color.White, Dim], Positions = [0f, 0.5f, 1f] },
            WrapMode = WrapMode.TileFlipX,
        };
        using var format = new StringFormat(Typographic) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        // Fuera de la franja del brillo, el texto es tenue: se dibuja primero tenue y el brillo encima, recortado.
        using (var dim = new SolidBrush(Dim)) g.DrawString(text, font, dim, bounds, format);
        var state = g.Save();
        g.IntersectClip(new RectangleF(center - S(70), bounds.Y, S(140), bounds.Height));
        g.DrawString(text, font, brush, bounds, format);
        g.Restore(state);
    }

    static void Fill(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = Draw.Rounded(r, radius);
        g.FillPath(brush, path);
    }

    void Outline(Graphics g, Color color, RectangleF r, float radius)
    {
        using var pen = new Pen(color, Math.Max(1, S(1)));
        using var path = Draw.Rounded(RectangleF.Inflate(r, -0.5f, -0.5f), radius);
        g.DrawPath(pen, path);
    }

    void Pulse(Graphics g, PointF center, float r, Color color)
    {
        var wave = 0.5 + 0.5 * Math.Sin(Environment.TickCount64 % 1400 / 1400.0 * 2 * Math.PI);
        var halo = r + (float)(r * 0.8 * wave);
        using (var brush = new SolidBrush(Color.FromArgb((int)(70 * (1 - wave)) + 20, color)))
            g.FillEllipse(brush, center.X - halo, center.Y - halo, 2 * halo, 2 * halo);
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, center.X - r, center.Y - r, 2 * r, 2 * r);
    }

    void Chevron(Graphics g, PointF center, Color color)
    {
        using var pen = new Pen(color, Math.Max(1.2f, S(1.4f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var w = S(3);
        g.DrawLines(pen, new[] { new PointF(center.X - w, center.Y - w / 2f), new PointF(center.X, center.Y + w / 2f), new PointF(center.X + w, center.Y - w / 2f) });
    }

    static Color Lighten(Color c) => Color.FromArgb(Math.Min(255, c.R + 30), Math.Min(255, c.G + 40), Math.Min(255, c.B + 40));

    static string Short(string text, int max) => text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Dispose();
            _frame.Dispose();
            _input.Dispose();
            _body.Dispose(); _bodyStrong.Dispose(); _small.Dispose(); _smallStrong.Dispose();
            _glyph.Dispose(); _glyphTiny.Dispose(); _spinner.Dispose(); _mono.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// El campo de texto de la isla: una ventanita con un TextBox, encima del campo dibujado. Es la única
/// parte de la isla que toma el foco (para escribir). Enter envía y Esc pliega la isla.
/// </summary>
sealed class IslandInput : Form
{
    public event Action<string>? Sent;
    public event Action? Escaped;

    readonly CueTextBox _box;

    public IslandInput(Form owner)
    {
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0x1F, 0x1E, 0x1D);
        _box = new CueTextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = BackColor,
            ForeColor = Color.FromArgb(0xF5, 0xF4, 0xEF),
            CueColor = Color.FromArgb(0x8C, 0x8A, 0x83),
            Font = Fonts.Make("Segoe UI Variable Text", 9.75f, "Segoe UI"),
        };
        _box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Escaped?.Invoke(); }
        };
        Controls.Add(_box);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // ventana de herramientas
            return cp;
        }
    }

    public new bool Focused => _box.Focused || ContainsFocus;

    /// <summary>Lo escrito y no enviado.</summary>
    public string Value => _box.Text;

    bool _focusPending;

    public void Place(Rectangle bounds, string cue)
    {
        if (Bounds != bounds) Bounds = bounds;
        if (_box.Cue != cue) _box.Cue = cue;
        if (!Visible) Show(Owner);
        if (_focusPending)
        {
            _focusPending = false;
            Activate();
            _box.Focus();
        }
    }

    /// <summary>Toma el foco para escribir; si todavía no está a la vista (la isla se está abriendo), apenas aparezca.</summary>
    public void FocusText()
    {
        if (!Visible || !IsHandleCreated) { _focusPending = true; return; }
        Activate();
        _box.Focus();
    }

    public new void Hide()
    {
        _focusPending = false;
        base.Hide();
    }

    public void Send()
    {
        var text = _box.Text.Trim();
        if (text.Length == 0) return;
        _box.Clear();
        Sent?.Invoke(text);
    }
}
