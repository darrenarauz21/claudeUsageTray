using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

/// <summary>
/// Vista «Flujos»: un carril por sesión con sus pasos del turno; clic en un carril abre el mapa de
/// esa sesión. Mientras una sesión trabaja siempre hay algo animado: el paso en curso o «Pensando…»,
/// y cada paso nuevo entra creciendo. Todo sale del .jsonl que ya se lee: no consulta la API.
/// </summary>
sealed partial class UsageForm
{
    readonly Flows.Arrivals _arrivals = new();
    string? _flowSession;
    Font? _tiny;
    Font Tiny => _tiny ??= Fonts.Make("Segoe UI Variable Text", 7.75f, "Segoe UI");

    static string KindGlyph(ToolKind kind) => kind switch
    {
        ToolKind.Read => "",
        ToolKind.Search => "",
        ToolKind.Edit => "",
        ToolKind.Run => "",
        ToolKind.Agent => "",
        ToolKind.Web => "",
        ToolKind.Ask => "",
        _ => "",
    };

    static double EaseOut(double p) => 1 - (1 - p) * (1 - p);

    /// <summary>Halo que se expande y se desvanece, en bucle.</summary>
    void DrawHalo(Graphics g, RectangleF box, Color color, float spread)
    {
        var wave = Environment.TickCount64 % 1400 / 1400.0;
        var grow = (float)(spread * wave);
        using var brush = new SolidBrush(Color.FromArgb((int)(110 * (1 - wave)), color));
        g.FillEllipse(brush, box.X - grow, box.Y - grow, box.Width + 2 * grow, box.Height + 2 * grow);
    }

    int ComposeFlows(Graphics g, bool draw, int y)
    {
        if (_flowSession is { } id)
        {
            if (_sessions.FirstOrDefault(s => s.SessionId == id) is { } session) return ComposeFlowDetail(g, draw, session, y);
            _flowSession = null;
        }

        var lanes = _sessions.Where(s => s.State != SessionState.Idle).ToList();
        if (lanes.Count == 0)
        {
            if (draw) Write(g, "No hay sesiones activas.", _body, _t.Tertiary, new Rectangle(Pad, y, Inner, _body.Height));
            y += _body.Height + S(10);
        }
        foreach (var session in lanes)
            y = ComposeLane(g, draw, session, y) + S(8);

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        const string hint = "Clic en una sesión: su mapa completo. Sale de los archivos de Claude Code; no gasta tokens.";
        var height = TextRenderer.MeasureText(g, hint, _small, new Size(Inner, 0), flags).Height;
        if (draw) TextRenderer.DrawText(g, hint, _small, new Rectangle(Pad, y + S(2), Inner, height), _t.Tertiary, flags);
        return y + height + S(14);
    }

    int ComposeLane(Graphics g, bool draw, SessionInfo s, int y)
    {
        var key = "flow:" + s.SessionId;
        var compact = s.State == SessionState.Done;
        var top = y;
        var x0 = Pad + S(4);
        var width = Inner - S(8);
        var bodyHeight = _body.Height;
        var smallHeight = _small.Height;

        if (draw && _rowBounds.TryGetValue(key, out var previous))
        {
            if (!compact) Draw.Fill(g, _hoverKey == key ? _t.Hover : _t.Raised, previous, S(10));
            else if (_hoverKey == key) Draw.Fill(g, _t.Hover, previous, S(10));
            Draw.Outline(g, _t.Border, previous, S(10));
        }

        y += S(10);
        var tag = s.Account.Label;
        var tagWidth = TextRenderer.MeasureText(g, tag, _small, Size.Empty, Line).Width;
        if (draw)
        {
            DrawStateIcon(g, s.State, new Rectangle(x0, y, S(16), bodyHeight));
            Write(g, s.DisplayTitle, _body, compact ? _t.Secondary : _t.Text,
                new Rectangle(x0 + S(22), y, width - S(22) - tagWidth - S(8), bodyHeight), TextFormatFlags.EndEllipsis);
            Write(g, tag, _small, _t.Tertiary, new Rectangle(x0 + width - tagWidth, y, tagWidth, bodyHeight), TextFormatFlags.VerticalCenter);
        }
        y += bodyHeight + S(2);

        var (status, color) = LaneStatus(s);
        if (draw) Write(g, status, _small, color, new Rectangle(x0 + S(22), y, width - S(22), smallHeight), TextFormatFlags.EndEllipsis);
        y += smallHeight;

        if (!compact && (s.Flow.Count > 0 || Flows.Thinking(s)))
        {
            y += S(12);
            y = ComposeLaneNodes(g, draw, s, x0, width, y);
        }

        var bounds = new Rectangle(Pad - S(4), top, Inner + S(8), y - top + S(10));
        _rowBounds[key] = bounds;
        AddHit(key, bounds, HitKind.FlowSession, session: s);
        return bounds.Bottom;
    }

    (string Text, Color Color) LaneStatus(SessionInfo s)
    {
        var parts = new List<string>();
        if (Path.GetFileName(s.Cwd.TrimEnd('\\', '/')) is { Length: > 0 } folder) parts.Add(folder);
        switch (s.State)
        {
            case SessionState.Waiting:
                parts.Add(s.AwaitingAnswer ? "esperando su respuesta" : "necesita su atención");
                return (string.Join(" · ", parts), _t.Warn);
            case SessionState.Failed:
                parts.Add("falló: " + (s.ErrorText ?? "error de la API"));
                return (string.Join(" · ", parts), _t.Danger);
            case SessionState.Done:
                parts.Add("terminó " + Format.Ago(s.TurnEndedAt ?? s.StatusAt));
                parts.Add(s.Steps == 1 ? "1 paso" : $"{s.Steps} pasos");
                if (s.LastTurn is { } d) parts.Add(Format.Duration(d));
                return (string.Join(" · ", parts), _t.Tertiary);
            default:
                if (s.Model is { } model) parts.Add(Format.Model(model) + (s.Effort is { } e ? " " + e : ""));
                if (s.TurnStartedAt is { } start) parts.Add(Format.Duration(DateTimeOffset.Now - start));
                return (string.Join(" · ", parts), _t.Tertiary);
        }
    }

    /// <summary>
    /// «Pedido», «+N» con lo anterior y los últimos grupos, unidos por líneas. El último late (o
    /// «Pensando…» si ya volvió) y un punto viaja hacia él; cada paso nuevo entra creciendo.
    /// </summary>
    int ComposeLaneNodes(Graphics g, bool draw, SessionInfo s, int x0, int width, int y)
    {
        var groups = Flows.Groups(s.Flow);
        var thinking = Flows.Thinking(s);
        var shown = groups.TakeLast(thinking ? 2 : 3).ToList();
        var hidden = groups.Count - shown.Count;
        var columns = 1 + (hidden > 0 ? 1 : 0) + shown.Count + (thinking ? 1 : 0);
        var column = width / Math.Max(columns, 4);
        var circle = S(26);
        var tinyHeight = Tiny.Height;
        var cy = y + circle / 2;
        var centers = Enumerable.Range(0, columns).Select(i => x0 + i * column + column / 2).ToList();
        var active = Flows.Active(s);

        // Avance de entrada de cada columna (1 = ya está); la línea que llega a un nodo nuevo crece con él.
        var arrival = new double[columns];
        Array.Fill(arrival, 1);
        var first = 1 + (hidden > 0 ? 1 : 0);
        for (var i = 0; i < shown.Count; i++) arrival[first + i] = EaseOut(_arrivals.Progress(shown[i].Last.Id));

        if (draw)
        {
            for (var i = 1; i < columns; i++)
            {
                var from = centers[i - 1] + circle / 2 + S(3);
                var to = centers[i] - circle / 2 - S(3);
                var end = from + (to - from) * (float)arrival[i];
                var live = i == columns - 1 && s.State is SessionState.Working or SessionState.Waiting;
                using (var pen = new Pen(live ? Blend(_t.Border, _t.Accent, 0.5) : _t.Border, S(2)))
                    g.DrawLine(pen, from, cy, end, cy);
                if (live && s.State == SessionState.Working && to > from)
                {
                    var phase = Environment.TickCount64 % 1100 / 1100f;
                    var dot = S(6);
                    using var brush = new SolidBrush(_t.Accent);
                    g.FillEllipse(brush, from + (to - from - dot) * phase, cy - dot / 2f, dot, dot);
                }
            }
        }

        var index = 0;
        void Node(string glyph, string label, string? detail, Color fill, Color iconColor, Color? halo, bool outline, double grow = 1)
        {
            var cx = centers[index++];
            if (!draw) return;
            var size = (float)(circle * (0.45 + 0.55 * grow));
            var box = new RectangleF(cx - size / 2, y + (circle - size) / 2, size, size);
            if (halo is { } h) DrawHalo(g, box, h, S(9));
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, box);
            if (outline) using (var pen = new Pen(_t.Border)) g.DrawEllipse(pen, box);
            if (grow > 0.7)
                Write(g, glyph, glyph.StartsWith('+') ? _small : _glyphSmall, iconColor, Rectangle.Round(box),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var labelBox = new Rectangle(cx - column / 2, y + circle + S(4), column, tinyHeight);
            var labelColor = Blend(_t.Background, halo is null ? _t.Tertiary : _t.Text, grow);
            Write(g, label, Tiny, labelColor, labelBox, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (detail is not null)
                Write(g, detail, Tiny, Blend(_t.Background, _t.Tertiary, grow), new Rectangle(labelBox.X, labelBox.Bottom, column, tinyHeight),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        Node("", "Pedido", null, _t.Background, _t.Secondary, null, outline: true);
        if (hidden > 0) Node("+" + hidden, "antes", null, _t.Background, _t.Secondary, null, outline: true);
        foreach (var group in shown)
        {
            var isLast = group == shown[^1];
            var running = isLast && active is not null && group.Last == active && !thinking;
            var waiting = isLast && s.State == SessionState.Waiting;
            var failed = isLast && s.State == SessionState.Failed;
            var accent = waiting ? _t.Warn : failed ? _t.Danger : _t.Accent;
            var lit = running || waiting || failed;
            var label = group.Count > 1 ? $"{group.Verb} ×{group.Count}" : group.Verb;
            Node(KindGlyph(group.Kind), label, lit || (isLast && thinking) ? group.Detail : null,
                lit ? accent : _t.Background, lit ? _t.Background : _t.Secondary,
                running || waiting ? accent : null, outline: !lit, grow: arrival[index]);
        }
        if (thinking)
        {
            // El modelo está generando el siguiente paso: un nodo vacío con el asterisco que gira.
            var cx = centers[index++];
            if (draw)
            {
                var box = new RectangleF(cx - circle / 2f, y, circle, circle);
                DrawHalo(g, box, _t.Accent, S(8));
                using (var brush = new SolidBrush(_t.Background)) g.FillEllipse(brush, box);
                using (var pen = new Pen(_t.Accent, S(2))) g.DrawEllipse(pen, box);
                DrawSpinner(g, Rectangle.Round(box));
                Write(g, "Pensando…", Tiny, _t.Text, new Rectangle(cx - column / 2, y + circle + S(4), column, tinyHeight),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            }
        }
        y += circle + S(4) + 2 * tinyHeight;

        // Subagentes del turno: el último, con su estado.
        if (s.Flow.LastOrDefault(f => f.Kind == ToolKind.Agent) is { } agent)
        {
            y += S(6);
            var text = $"↳ {agent.AgentType ?? "subagente"} · " + (agent.Done
                ? agent.AgentSteps is { } n ? $"volvió · {n} pasos" : "volvió"
                : "trabajando…");
            if (draw) Write(g, text, Tiny, agent.Done ? _t.Tertiary : _t.Accent, new Rectangle(x0 + S(4), y, width, tinyHeight), TextFormatFlags.EndEllipsis);
            y += tinyHeight;
        }
        return y;
    }

    // ── Mapa de una sesión ──────────────────────────────────────────────────────────────────

    int ComposeFlowDetail(Graphics g, bool draw, SessionInfo s, int y)
    {
        var back = new Rectangle(Pad - S(4), y, S(28), S(28));
        AddHit("flow-back", back, HitKind.FlowBack);
        var bodyHeight = _body.Height;
        var smallHeight = _small.Height;
        if (draw)
        {
            Draw.Fill(g, _hoverKey == "flow-back" ? _t.Hover : _t.Raised, back, S(8));
            Write(g, GlyphBack, _glyphSmall, _t.Text, back, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            Write(g, s.DisplayTitle, _body, _t.Text, new Rectangle(back.Right + S(10), y - S(2), Inner - S(40), bodyHeight), TextFormatFlags.EndEllipsis);
            var sub = string.Join(" · ", new[] { Path.GetFileName(s.Cwd.TrimEnd('\\', '/')), Format.ModelLine(s) }.Where(p => !string.IsNullOrEmpty(p)));
            Write(g, sub, _small, _t.Tertiary, new Rectangle(back.Right + S(10), y + bodyHeight, Inner - S(40), smallHeight), TextFormatFlags.EndEllipsis);
        }
        y += Math.Max(S(28), bodyHeight + smallHeight) + S(14);

        // Cuenta → sesión → herramientas; el subagente debajo de la sesión.
        var active = Flows.Active(s);
        var thinking = Flows.Thinking(s);
        var agent = s.Flow.LastOrDefault(f => f.Kind == ToolKind.Agent);
        var mapHeight = agent is null ? S(206) : S(226);
        var top = y;
        var mid = top + S(90);
        var account = new Rectangle(Pad, mid - S(14), S(64), S(28));
        var session = new Rectangle(Pad + S(76), mid - S(28), S(96), S(56));
        var toolWidth = S(92);
        var toolHeight = S(26);
        var toolX = Pad + Inner - toolWidth;
        var step = (S(194) - toolHeight) / (Flows.MapKinds.Length - 1);
        var bend = (toolX - session.Right) / 2;
        var agentBox = new Rectangle(session.X, session.Bottom + S(38), session.Width, S(44));
        var working = s.State is SessionState.Working or SessionState.Waiting;

        if (draw)
        {
            using (var pen = new Pen(_t.Border, 1.5f * _scale)) g.DrawLine(pen, account.Right, mid, session.X, mid);
            for (var i = 0; i < Flows.MapKinds.Length; i++)
            {
                var kind = Flows.MapKinds[i];
                var ty = top + i * step + toolHeight / 2;
                var count = s.Flow.Count(f => f.Kind == kind);
                var lit = active?.Kind == kind;
                using var path = new GraphicsPath();
                path.AddBezier(session.Right, mid, session.Right + bend, mid, toolX - bend, ty, toolX, ty);
                using var pen = new Pen(lit ? _t.Accent : count > 0 ? Blend(_t.Border, _t.Secondary, 0.35) : _t.Border,
                    lit ? 2.5f * _scale : count > 0 ? 1.5f * _scale : 1);
                if (lit)
                {
                    pen.DashPattern = [2.5f, 2.5f];
                    // Hacia la herramienta mientras la usa; de vuelta a la sesión mientras piensa.
                    var phase = Environment.TickCount64 % 700 / 700f * 5f;
                    pen.DashOffset = thinking ? phase : -phase;
                }
                g.DrawPath(pen, path);
            }
            if (agent is not null)
                using (var pen = new Pen(Blend(_t.Border, _t.Secondary, 0.35), 1.5f * _scale) { DashPattern = [2f, 3f] })
                    g.DrawLine(pen, session.X + session.Width / 2, session.Bottom, session.X + session.Width / 2, agentBox.Y);

            Draw.Fill(g, _t.Raised, account, account.Height / 2);
            Draw.Outline(g, _t.Border, account, account.Height / 2);
            Write(g, s.Account.Label, _small, _t.Text, account, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var (status, color) = s.State switch
            {
                SessionState.Working => (thinking || active is null ? "Pensando…" : active.Verb, _t.Accent),
                SessionState.Waiting => ("Le espera a usted", _t.Warn),
                SessionState.Failed => ("Falló", _t.Danger),
                SessionState.Done => ("Terminó", _t.Success),
                _ => ("Inactiva", _t.Tertiary),
            };
            if (working)
            {
                var wave = Environment.TickCount64 % 1400 / 1400.0;
                var grow = (int)(S(6) * wave);
                Draw.Fill(g, Color.FromArgb((int)(70 * (1 - wave)), color), Rectangle.Inflate(session, grow, grow), S(10) + grow);
            }
            Draw.Fill(g, _t.Raised, session, S(10));
            Draw.Outline(g, working ? color : _t.Border, session, S(10));
            var inner = Rectangle.Inflate(session, -S(9), -S(6));
            Write(g, "Sesión", _small, _t.Text, new Rectangle(inner.X, inner.Y, inner.Width, smallHeight));
            Write(g, status, Tiny, color, new Rectangle(inner.X, inner.Y + smallHeight + S(1), inner.Width, Tiny.Height), TextFormatFlags.EndEllipsis);
            var steps = s.Flow.Count == 1 ? "1 paso" : $"{s.Flow.Count} pasos";
            Write(g, steps, Tiny, _t.Tertiary, new Rectangle(inner.X, inner.Y + smallHeight + Tiny.Height + S(2), inner.Width, Tiny.Height));

            for (var i = 0; i < Flows.MapKinds.Length; i++)
            {
                var kind = Flows.MapKinds[i];
                var box = new Rectangle(toolX, top + i * step, toolWidth, toolHeight);
                var count = s.Flow.Count(f => f.Kind == kind);
                var lit = active?.Kind == kind;
                if (lit)
                {
                    var wave = Environment.TickCount64 % 1400 / 1400.0;
                    var grow = (int)(S(6) * wave);
                    Draw.Fill(g, Color.FromArgb((int)(100 * (1 - wave)), _t.Accent), Rectangle.Inflate(box, grow, grow), S(8) + grow);
                    Draw.Fill(g, _t.Accent, box, S(8));
                }
                else Draw.Outline(g, count > 0 ? Blend(_t.Border, _t.Secondary, 0.35) : _t.Border, box, S(8));
                var textColor = lit ? _t.Background : count > 0 ? _t.Text : _t.Tertiary;
                Write(g, Flows.KindLabel(kind), _small, textColor, new Rectangle(box.X + S(9), box.Y, box.Width - S(36), box.Height), TextFormatFlags.VerticalCenter);
                Write(g, count.ToString(), _small, lit ? _t.Background : _t.Secondary, new Rectangle(box.Right - S(30), box.Y, S(21), box.Height),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }

            if (agent is not null)
            {
                Draw.Fill(g, _t.Background, agentBox, S(10));
                using (var pen = new Pen(Blend(_t.Border, _t.Secondary, 0.35)) { DashPattern = [3f, 2f] })
                using (var path = Draw.Rounded(agentBox, S(10))) g.DrawPath(pen, path);
                var a = Rectangle.Inflate(agentBox, -S(9), -S(5));
                Write(g, "Subagente " + (agent.AgentType ?? ""), _small, _t.Secondary, new Rectangle(a.X, a.Y, a.Width, smallHeight), TextFormatFlags.EndEllipsis);
                var state = agent.Done ? agent.AgentSteps is { } n ? $"volvió · {n} pasos" : "volvió" : "trabajando…";
                Write(g, state, Tiny, agent.Done ? _t.Tertiary : _t.Accent, new Rectangle(a.X, a.Y + smallHeight + S(2), a.Width, Tiny.Height));
            }
        }
        y = top + mapHeight;

        // Los últimos pasos, con su hora; el más nuevo entra desde la izquierda.
        if (draw) Write(g, "ÚLTIMOS PASOS DEL TURNO", _small, _t.Tertiary, new Rectangle(Pad, y, Inner, smallHeight));
        y += smallHeight + S(6);
        var last = s.Flow.TakeLast(5).ToList();
        if (last.Count == 0)
        {
            if (draw) Write(g, "Aún no hay pasos en este turno.", _small, _t.Tertiary, new Rectangle(Pad, y, Inner, smallHeight));
            y += smallHeight + S(4);
        }
        foreach (var f in last)
        {
            var isActive = working && f == active;
            if (draw)
            {
                var p = EaseOut(_arrivals.Progress(f.Id));
                var shift = (int)(S(14) * (1 - p));
                var time = f.At is { } at ? $"{at.LocalDateTime:HH:mm:ss}" : "";
                Write(g, time, _small, Blend(_t.Background, isActive ? _t.Accent : _t.Tertiary, p), new Rectangle(Pad, y, S(62), smallHeight));
                var text = f.Detail is null ? f.Verb : $"{f.Verb} · {f.Detail}";
                if (isActive) text += thinking ? "  · listo" : "  · en curso";
                Write(g, text, _small, Blend(_t.Background, isActive ? _t.Text : _t.Secondary, p),
                    new Rectangle(Pad + S(66) - shift, y, Inner - S(66), smallHeight), TextFormatFlags.EndEllipsis);
            }
            y += smallHeight + S(5);
        }
        return y + S(10);
    }
}
