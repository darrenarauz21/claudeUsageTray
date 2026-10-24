namespace ClaudeUsageTray;

/// <summary>
/// La nubecita del chat con la mascota, de pixeles como la de «Hablar» (BubbleSprite). Tiene cuatro
/// momentos: el campo para escribir (con el chip del modelo), el micrófono mientras se le habla, los
/// tres puntos mientras Claude piensa y la respuesta, que aparece de a poco. Si la respuesta es una
/// tarea, ofrece abrir la sesión. Va arriba de la mascota (o abajo, si no cabe) y la sigue si la
/// arrastran. Esc la cierra.
/// </summary>
sealed class ChatBubble : Form
{
    public enum Mode { Input, Thinking, Answer, Listening }

    static readonly Color Key = Color.FromArgb(255, 0, 255); // el fondo: Windows lo deja transparente
    static readonly Color Ink = Color.FromArgb(0x2A, 0x1F, 0x1A);
    static readonly Color Muted = Color.FromArgb(0x8F, 0x8D, 0x86);
    static readonly Color Chip = Color.FromArgb(0xD8, 0xD4, 0xCB);
    static readonly Color Accent = Color.FromArgb(0xC2, 0x5E, 0x3E);
    static readonly Color Mic = Color.FromArgb(0xD9, 0x4B, 0x3F);
    /// <summary>Lo que tarda en subir y bajar cada barrita del nivel (como en el diseño de la voz), en ms; también en la isla.</summary>
    public static readonly int[] BarPeriods = [550, 700, 620, 800, 500, 660];
    const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    const TextFormatFlags OneLine = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
    const int AnswerLifetime = 14000;

    /// <summary>Se envió lo escrito.</summary>
    public event Action<string>? Sent;
    /// <summary>Clic en el chip del modelo (se abre la lista).</summary>
    public event Action? ModelClicked;
    /// <summary>Clic en «Ver en la isla».</summary>
    public event Action? IslandClicked;
    /// <summary>La tarea: true abre la sesión en la carpeta propuesta; false elige otra carpeta.</summary>
    public event Action<bool>? TaskChosen;
    /// <summary>Se cerró (Esc, clic fuera o se acabó su tiempo).</summary>
    public event Action? Dismissed;
    /// <summary>La respuesta terminó de aparecer (la mascota deja de mover la boca).</summary>
    public event Action? DoneTalking;
    bool _wasTalking;

    readonly Font _font = Fonts.Make("Segoe UI Variable Text", 9.5f, "Segoe UI");
    readonly Font _small = Fonts.Make("Segoe UI Variable Text", 8.25f, "Segoe UI");
    readonly Font _button = Fonts.Make("Segoe UI Variable Text Semibold", 8.25f, "Segoe UI Semibold");
    readonly Font _glyph = Fonts.Make("Segoe Fluent Icons", 8.25f, "Segoe MDL2 Assets");
    readonly CueTextBox _box;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 30 };
    readonly int _unit;
    Mode _mode = Mode.Input;
    string _model = "";
    string _target = "";      // la respuesta completa que se conoce hasta ahora
    int _visible;             // cuántas letras se ven: avanza de a poco hacia _target
    bool _streaming;
    bool _island;
    string? _taskPrompt, _taskFolder;
    bool _error;
    long _doneAt;
    bool _hover;
    int _cols, _rows, _tailX;
    bool _tailDown = true;
    Rectangle _mascot;
    Rectangle _chipBounds, _linkBounds, _openBounds, _otherBounds;
    string? _pressed;

    /// <summary>Mientras hay un menú abierto (el del modelo), perder el foco no la cierra.</summary>
    public bool HoldOpen { get; set; }

    /// <summary>Lo escrito y no enviado, para cuando se vuelva a abrir.</summary>
    public string Draft => _box.Text;

    public Mode Current => _mode;

    /// <summary>La respuesta todavía está apareciendo (para que la mascota mueva la boca).</summary>
    public bool Talking => _mode == Mode.Answer && (_streaming || _visible < _target.Length);

    int S(int v) => LogicalToDeviceUnits(v);
    int ContentWidth => _mode != Mode.Thinking ? S(224)
        : _question is null ? S(56)
        : Math.Clamp(TextRenderer.MeasureText(Quote(_question), _small, Size.Empty, OneLine).Width + S(4), S(56), S(224));

    static string Quote(string question) => $"«{question.ReplaceLineEndings(" ")}»";

    string? _question;
    long _questionAt;
    int Pad => _unit * 5;

    // Escuchando: el nivel que se ve (sube enseguida y baja de a poco), lo entendido y cómo terminar.
    float _level;
    string _heard = "", _hint = "";
    bool _converting;

    public ChatBubble()
    {
        _unit = Math.Max(1, (int)Math.Round(2 * DeviceDpi / 96f));
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Key;
        TransparencyKey = Key;
        KeyPreview = true;

        _box = new CueTextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            ForeColor = Ink,
            Font = _font,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.None,
            AcceptsReturn = false,
            Cue = "Pregúntame algo o pídeme una tarea…",
            CueColor = Muted,
        };
        _box.KeyDown += OnBoxKeyDown;
        _box.TextChanged += (_, _) => { if (_mode == Mode.Input) Relayout(); };
        Controls.Add(_box);

        _tick.Tick += (_, _) => Tick();
        Deactivate += (_, _) =>
        {
            if (_mode == Mode.Input && !HoldOpen) Close(dismissed: true);
        };
    }

    protected override bool ShowWithoutActivation => _mode != Mode.Input;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80; // WS_EX_TOOLWINDOW (encima: TopMost, ver MascotWindow)
            return cp;
        }
    }

    // ── Momentos ────────────────────────────────────────────────────────────────────────────

    /// <summary>El campo para escribir, junto a la mascota. Se queda con el foco para escribir enseguida.</summary>
    public void AskInput(Rectangle mascot, string model)
    {
        _mode = Mode.Input;
        _model = model;
        _target = "";
        _visible = 0;
        _taskPrompt = null;
        _error = false;
        _box.Visible = true;
        Relayout(mascot);
        if (!Visible) Show();
        Activate();
        _box.Focus();
        _box.SelectionStart = _box.TextLength;
        _tick.Start();
    }

    public void SetModel(string model)
    {
        _model = model;
        Invalidate();
    }

    /// <summary>Los tres puntos mientras llega la respuesta, bajo la pregunta que se le hizo (en gris).</summary>
    public void Think(Rectangle mascot, string? question = null)
    {
        _question = question;
        _questionAt = Environment.TickCount64;
        _mode = Mode.Thinking;
        _box.Visible = false;
        _target = "";
        _visible = 0;
        _streaming = true;
        Relayout(mascot);
        if (!Visible) Show();
        _tick.Start();
    }

    /// <summary>El micrófono abierto: el nivel de la voz, lo que va entendiendo y cómo se termina (hint).</summary>
    public void Listen(Rectangle mascot, string hint)
    {
        _mode = Mode.Listening;
        _box.Visible = false;
        _heard = "";
        _hint = hint;
        _converting = false;
        _level = 0;
        _taskPrompt = null;
        _error = false;
        Relayout(mascot);
        if (!Visible) Show();
        _tick.Start();
    }

    public void SetLevel(float level) => _level = Math.Max(_level, Math.Min(1, level * 2.2f));

    public void SetHeard(string text)
    {
        if (_mode != Mode.Listening) return;
        _heard = text;
        Relayout();
    }

    /// <summary>Soltó la tecla: queda lo entendido mientras se pasa todo a texto.</summary>
    public void Converting()
    {
        if (_mode != Mode.Listening) return;
        _converting = true;
        Invalidate();
    }

    /// <summary>Llega otro trozo de la respuesta: se agrega y aparece de a poco.</summary>
    public void Append(string piece)
    {
        if (_mode != Mode.Answer)
        {
            _mode = Mode.Answer;
            _box.Visible = false;
        }
        _target += piece;
        Relayout();
        _tick.Start();
    }

    /// <summary>
    /// La respuesta terminó: el texto final (puede corregir lo que llegó por partes), si ofrecer
    /// «Ver en la isla» y, si es una tarea, la tarea y el nombre de la carpeta donde se abriría.
    /// </summary>
    public void Finish(string text, bool island, string? taskPrompt = null, string? taskFolder = null, bool error = false)
    {
        _mode = Mode.Answer;
        _box.Visible = false;
        _streaming = false;
        _error = error;
        _island = island && !error;
        _taskPrompt = taskPrompt;
        _taskFolder = taskFolder;
        if (taskPrompt is not null)
            text = $"Eso es una tarea. ¿Abro Claude Code en «{taskFolder}» con esto?";
        if (!text.StartsWith(_target[..Math.Min(_visible, _target.Length)], StringComparison.Ordinal)) _visible = 0;
        _target = text;
        _doneAt = Environment.TickCount64;
        Relayout();
        if (!Visible) Show();
        _tick.Start();
    }

    /// <summary>Un mensaje corto de la mascota (al abrir una tarea, por ejemplo), con su tiempo para irse.</summary>
    public void Say(Rectangle mascot, string text)
    {
        _mascot = mascot;
        Finish(text, island: false);
    }

    void Tick()
    {
        var now = Environment.TickCount64;
        if (_mode == Mode.Answer && _visible < _target.Length)
        {
            // Unas 70 letras por segundo; si se atrasa mucho, se pone al día más rápido.
            var behind = _target.Length - _visible;
            _visible = Math.Min(_target.Length, _visible + Math.Max(2, behind / 12));
            Invalidate();
        }
        else if (_mode == Mode.Thinking) Invalidate();
        else if (_mode == Mode.Listening)
        {
            _level *= 0.9f;
            Invalidate();
        }
        var talking = Talking;
        if (_wasTalking && !talking) DoneTalking?.Invoke();
        _wasTalking = talking;

        var waiting = _taskPrompt is not null;
        if (_mode == Mode.Answer && !_streaming && _visible >= _target.Length && !waiting && !_hover
            && now - _doneAt > AnswerLifetime)
            Close(dismissed: true);
    }

    /// <summary>Se esconde (no se destruye: se vuelve a usar).</summary>
    public void Close(bool dismissed)
    {
        if (!Visible) return;
        _tick.Stop();
        Hide();
        if (dismissed) Dismissed?.Invoke();
    }

    // ── Medidas y lugar ─────────────────────────────────────────────────────────────────────

    int LineHeight => TextRenderer.MeasureText("Ág", _font, Size.Empty, OneLine).Height;

    void Relayout(Rectangle? mascot = null)
    {
        if (mascot is { } m) _mascot = m;
        var width = ContentWidth;
        int height;
        switch (_mode)
        {
            case Mode.Input:
                var text = _box.TextLength == 0 ? "W" : _box.Text + (_box.Text.EndsWith('\n') ? "W" : "");
                var lines = Math.Clamp(TextRenderer.MeasureText(text, _font, new Size(width, 0), Wrap).Height / LineHeight, 1, 5);
                var boxHeight = lines * LineHeight;
                _box.Bounds = new Rectangle(Pad, (_tailDown ? 0 : BubbleSprite.Tail * _unit) + Pad, width, boxHeight);
                height = boxHeight + S(8) + S(20);
                break;
            case Mode.Thinking:
                height = LineHeight + (_question is null ? 0 : _small.Height + S(4));
                break;
            case Mode.Listening:
                height = TextRenderer.MeasureText(HeardShown, _font, new Size(HeardWidth, 0), Wrap).Height + S(6) + _small.Height;
                break;
            default:
                var shown = _target.Length == 0 ? " " : _target;
                height = TextRenderer.MeasureText(shown, _font, new Size(width, 0), Wrap).Height;
                if (_taskPrompt is not null)
                    height += S(4) + TextRenderer.MeasureText($"«{_taskPrompt}»", _small, new Size(width, S(44)), Wrap | TextFormatFlags.EndEllipsis).Height
                        + S(10) + S(26);
                else if (_island) height += S(6) + _small.Height;
                break;
        }
        _cols = (width + Pad * 2) / _unit + 1;
        _rows = (height + Pad * 2) / _unit + 1 + BubbleSprite.Tail;
        ClientSize = new Size(_cols * _unit, _rows * _unit);
        Follow(_mascot);
    }

    /// <summary>Arriba de la mascota con la colita hacia su cabeza (o abajo, si arriba no cabe), sin salirse de la pantalla.</summary>
    public void Follow(Rectangle mascot)
    {
        _mascot = mascot;
        var area = Screen.FromRectangle(mascot).WorkingArea;
        var headX = mascot.X + mascot.Width / 2;
        var top = mascot.Y + mascot.Height / 20;
        var down = top - Height >= area.Top;
        if (down != _tailDown && _mode == Mode.Input)
        {
            _tailDown = down;
            _box.Top = (_tailDown ? 0 : BubbleSprite.Tail * _unit) + Pad;
        }
        _tailDown = down;
        var y = _tailDown ? top - Height : mascot.Bottom - mascot.Height / 20;
        var x = Math.Clamp(headX - Width * 2 / 3, area.Left, Math.Max(area.Left, area.Right - Width));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        _tailX = (headX - x) / _unit;
        Location = new Point(x, y);
        Invalidate();
    }

    /// <summary>El ancho del texto al escuchar: lo que queda junto al micrófono y las barritas.</summary>
    int HeardWidth => ContentWidth - S(58);

    /// <summary>Lo entendido, en tres líneas como mucho: si no cabe, el final (lo último que se dijo).</summary>
    string HeardShown
    {
        get
        {
            if (_heard.Length == 0) return "Te escucho…";
            var text = _heard;
            var max = LineHeight * 3;
            while (text.Length > 0 && TextRenderer.MeasureText("…" + text, _font, new Size(HeardWidth, 0), Wrap).Height > max)
                text = text.IndexOf(' ') is var space and > 0 ? text[(space + 1)..] : text[1..];
            return text.Length == _heard.Length ? text : "…" + text;
        }
    }

    // ── Dibujo ──────────────────────────────────────────────────────────────────────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Key);
        ToastArt.Pixels(g, 0, 0, _unit, fill => BubbleSprite.Paint(fill, _cols, _rows, _tailX, _tailDown));
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var x = Pad;
        var y = (_tailDown ? 0 : BubbleSprite.Tail * _unit) + Pad;
        var width = ContentWidth;
        _chipBounds = _linkBounds = _openBounds = _otherBounds = Rectangle.Empty;

        switch (_mode)
        {
            case Mode.Input:
                var row = _box.Bottom + S(8);
                var label = _model;
                var textWidth = TextRenderer.MeasureText(label, _small, Size.Empty, OneLine).Width;
                _chipBounds = new Rectangle(x, row, textWidth + S(26), S(18));
                using (var pen = new Pen(Chip, Math.Max(1, S(1))))
                using (var path = Draw.Rounded(_chipBounds, _chipBounds.Height / 2f))
                    g.DrawPath(pen, path);
                TextRenderer.DrawText(g, label, _small, new Rectangle(_chipBounds.X + S(8), row, textWidth, _chipBounds.Height), Ink,
                    OneLine | TextFormatFlags.VerticalCenter);
                var ax = _chipBounds.Right - S(13);
                var ay = row + _chipBounds.Height / 2 - S(1);
                using (var brush = new SolidBrush(Ink))
                    g.FillPolygon(brush, new PointF[] { new(ax, ay), new(ax + S(6), ay), new(ax + S(3), ay + S(3)) });
                const string hint = "Enter para enviar";
                var hintWidth = TextRenderer.MeasureText(hint, _small, Size.Empty, OneLine).Width;
                TextRenderer.DrawText(g, hint, _small, new Rectangle(x + width - hintWidth, row, hintWidth, _chipBounds.Height), Muted,
                    OneLine | TextFormatFlags.VerticalCenter);
                break;

            case Mode.Thinking:
                var tick = Environment.TickCount64;
                if (_question is { } question)
                {
                    // La pregunta queda arriba, en gris, apareciendo (de blanco a gris) mientras sube un poco.
                    var k = (float)Math.Clamp((tick - _questionAt) / 260.0, 0, 1);
                    var ease = 1 - (1 - k) * (1 - k) * (1 - k);
                    var color = Color.FromArgb((int)(255 + (Muted.R - 255) * ease), (int)(255 + (Muted.G - 255) * ease), (int)(255 + (Muted.B - 255) * ease));
                    TextRenderer.DrawText(g, Quote(question), _small, new Rectangle(x, y + (int)((1 - ease) * S(6)), width, _small.Height), color, Color.White,
                        OneLine | TextFormatFlags.EndEllipsis);
                    y += _small.Height + S(4);
                }
                // Tres puntos que saltan uno detrás del otro (como «Pensando» en Flujos).
                for (var i = 0; i < 3; i++)
                {
                    var bounce = (float)Flows.DotBounce(tick, i);
                    var r = S(3) + bounce * S(1);
                    var cx = x + S(8) + i * S(14);
                    var cy = y + LineHeight / 2f + S(2) - bounce * S(5);
                    using var brush = new SolidBrush(Color.FromArgb((int)(Muted.R + (Ink.R - Muted.R) * bounce), (int)(Muted.G + (Ink.G - Muted.G) * bounce), (int)(Muted.B + (Ink.B - Muted.B) * bounce)));
                    g.FillEllipse(brush, cx - r, cy - r, 2 * r, 2 * r);
                }
                break;

            case Mode.Listening:
                PaintListening(g, x, y);
                break;

            default:
                var shown = _target[..Math.Min(_visible, _target.Length)];
                var full = TextRenderer.MeasureText(_target.Length == 0 ? " " : _target, _font, new Size(width, 0), Wrap).Height;
                TextRenderer.DrawText(g, shown, _font, new Rectangle(x, y, width, full), _error ? Muted : Ink, Color.White, Wrap);
                var below = y + full;
                if (_taskPrompt is not null && _visible >= _target.Length)
                {
                    var quote = $"«{_taskPrompt}»";
                    var qh = TextRenderer.MeasureText(quote, _small, new Size(width, S(44)), Wrap | TextFormatFlags.EndEllipsis).Height;
                    TextRenderer.DrawText(g, quote, _small, new Rectangle(x, below + S(4), width, qh), Color.FromArgb(0x5E, 0x5D, 0x59), Color.White,
                        Wrap | TextFormatFlags.EndEllipsis);
                    var by = below + S(4) + qh + S(10);
                    _openBounds = PixelButton(g, "Abrir sesión", x, by, primary: true, pressed: _pressed == "open");
                    _otherBounds = PixelButton(g, "Otra carpeta…", _openBounds.Right + S(10), by, primary: false, pressed: _pressed == "other");
                }
                else if (_island && _visible >= _target.Length)
                {
                    const string link = "Ver en la isla";
                    var lw = TextRenderer.MeasureText(link, _small, Size.Empty, OneLine).Width;
                    _linkBounds = new Rectangle(x, below + S(6), lw, _small.Height);
                    TextRenderer.DrawText(g, link, _small, _linkBounds, Accent, Color.White, OneLine);
                }
                break;
        }
    }

    /// <summary>
    /// El micrófono rojo, las barritas del nivel (o tres puntos mientras pasa a texto), lo entendido y,
    /// debajo, cómo se termina.
    /// </summary>
    void PaintListening(Graphics g, int x, int y)
    {
        var tick = Environment.TickCount64;
        var mic = new Rectangle(x, y + (LineHeight - S(20)) / 2, S(20), S(20));
        using (var fill = new SolidBrush(Mic)) g.FillRectangle(fill, mic); // cuadrado, como los pixeles de la nubecita
        TextRenderer.DrawText(g, "", _glyph, mic, Color.White, Mic, OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        var barsX = mic.Right + S(6);
        var middle = mic.Y + mic.Height / 2f;
        using (var brush = new SolidBrush(Mic))
        {
            if (_converting)
                for (var i = 0; i < 3; i++)
                {
                    var bounce = (float)Flows.DotBounce(tick, i);
                    var r = S(2) + bounce * S(1);
                    var cx = barsX + S(4) + i * S(8);
                    g.FillEllipse(brush, cx - r, middle - r - bounce * S(3), 2 * r, 2 * r);
                }
            else
                for (var i = 0; i < BarPeriods.Length; i++)
                {
                    var wave = (float)(0.5 + 0.5 * Math.Sin(tick % BarPeriods[i] / (double)BarPeriods[i] * 2 * Math.PI + i));
                    var h = Math.Max(S(3), S(16) * (0.35f + 0.65f * wave) * (0.2f + 0.8f * _level));
                    g.FillRectangle(brush, barsX + i * S(4), middle - h / 2, S(2), h);
                }
        }

        var textX = x + S(58);
        var shown = HeardShown;
        var height = TextRenderer.MeasureText(shown, _font, new Size(HeardWidth, 0), Wrap).Height;
        TextRenderer.DrawText(g, shown, _font, new Rectangle(textX, y, HeardWidth, height), _heard.Length == 0 ? Muted : Ink, Color.White, Wrap);

        var hint = _converting ? "Pasando a texto…" : $"{_hint} · Esc cancela";
        TextRenderer.DrawText(g, hint, _small, new Rectangle(x, y + height + S(6), ContentWidth, _small.Height),
            _converting ? Muted : Mic, Color.White, OneLine | TextFormatFlags.EndEllipsis);
    }

    /// <summary>Un botón de pixeles: contorno oscuro y una sombra de una casilla que desaparece al pulsarlo.</summary>
    Rectangle PixelButton(Graphics g, string text, int x, int y, bool primary, bool pressed)
    {
        var tw = TextRenderer.MeasureText(text, _button, Size.Empty, OneLine).Width;
        var rect = new Rectangle(x, y + (pressed ? _unit : 0), tw + S(20), S(22));
        using (var fill = new SolidBrush(primary ? Accent : Color.White)) g.FillRectangle(fill, rect);
        using (var ink = new SolidBrush(Ink))
        {
            g.FillRectangle(ink, rect.X, rect.Y, rect.Width, _unit);
            g.FillRectangle(ink, rect.X, rect.Bottom - _unit, rect.Width, _unit);
            g.FillRectangle(ink, rect.X, rect.Y, _unit, rect.Height);
            g.FillRectangle(ink, rect.Right - _unit, rect.Y, _unit, rect.Height);
            if (!pressed) g.FillRectangle(ink, rect.X + _unit * 2, rect.Bottom, rect.Width, _unit);
        }
        TextRenderer.DrawText(g, text, _button, rect, primary ? Color.White : Ink, primary ? Accent : Color.White,
            OneLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return new Rectangle(x, y, rect.Width, rect.Height + _unit);
    }

    // ── Teclado y mouse ─────────────────────────────────────────────────────────────────────

    void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && !e.Shift)
        {
            e.SuppressKeyPress = true;
            var text = _box.Text.Trim();
            if (text.Length == 0) return;
            _box.Clear();
            Sent?.Invoke(text);
        }
        else if (e.KeyCode == Keys.Enter && e.Shift)
        {
            e.SuppressKeyPress = true;
            var at = _box.SelectionStart;
            _box.Text = _box.Text.Insert(at, Environment.NewLine);
            _box.SelectionStart = at + Environment.NewLine.Length;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close(dismissed: true);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _doneAt = Math.Max(_doneAt, Environment.TickCount64 - AnswerLifetime + 2500); // al irse el mouse, le quedan unos segundos
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hand = _chipBounds.Contains(e.Location) || _linkBounds.Contains(e.Location)
            || _openBounds.Contains(e.Location) || _otherBounds.Contains(e.Location);
        Cursor = hand ? Cursors.Hand : _mode == Mode.Input ? Cursors.IBeam : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = _openBounds.Contains(e.Location) ? "open" : _otherBounds.Contains(e.Location) ? "other" : null;
        if (_pressed is not null) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = null;
        if (e.Button != MouseButtons.Left) return;
        if (_chipBounds.Contains(e.Location)) { ModelClicked?.Invoke(); return; }
        if (_linkBounds.Contains(e.Location)) { Close(dismissed: false); IslandClicked?.Invoke(); return; }
        if (pressed == "open" && _openBounds.Contains(e.Location)) { _taskPrompt = null; TaskChosen?.Invoke(true); return; }
        if (pressed == "other" && _otherBounds.Contains(e.Location)) { TaskChosen?.Invoke(false); return; }
        if (_mode == Mode.Answer && _taskPrompt is null && !Talking) Close(dismissed: true);
        else Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tick.Dispose();
            _font.Dispose();
            _small.Dispose();
            _button.Dispose();
            _glyph.Dispose();
        }
        base.Dispose(disposing);
    }
}
