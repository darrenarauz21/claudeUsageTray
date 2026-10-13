using System.Drawing.Drawing2D;

namespace ClaudeUsageTray;

/// <summary>
/// Vista «Flujos»: un carril por sesión con sus pasos del turno, animado mientras trabaja; clic en
/// un carril abre el mapa de esa sesión. Todo sale del .jsonl que ya se lee: no consulta la API.
/// </summary>
sealed partial class UsageForm
{
    /// <summary>Pasos seguidos de la misma familia, juntos en un nodo («Leyendo ×3»).</summary>
    sealed record FlowGroup(ToolKind Kind, string Verb, string? Detail, int Count, bool Done, FlowStep Last);

    static readonly ToolKind[] MapKinds = [ToolKind.Read, ToolKind.Search, ToolKind.Edit, ToolKind.Run, ToolKind.Agent, ToolKind.Web];

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

    static string KindLabel(ToolKind kind) => kind switch
    {
        ToolKind.Read => "Leer",
        ToolKind.Search => "Buscar",
        ToolKind.Edit => "Editar",
        ToolKind.Run => "Ejecutar",
        ToolKind.Agent => "Agentes",
        ToolKind.Web => "Web",
        ToolKind.Ask => "Preguntar",
        _ => "Otras",
    };

    static List<FlowGroup> Groups(IReadOnlyList<FlowStep> flow)
    {
        var groups = new List<FlowGroup>();
        foreach (var step in flow)
        {
            if (groups.Count > 0 && groups[^1] is { } last && last.Kind == step.Kind && last.Verb == step.Verb && step.Kind != ToolKind.Agent)
                groups[^1] = last with { Detail = step.Detail, Count = last.Count + 1, Done = last.Done && step.Done, Last = step };
            else
                groups.Add(new FlowGroup(step.Kind, step.Verb, step.Detail, 1, step.Done, step));
        }
        return groups;
    }

    /// <summary>El paso que está en marcha: el último, si la sesión trabaja o espera y aún no tiene resultado.</summary>
    static bool IsCurrent(SessionInfo s, FlowStep step) =>
        s.State is SessionState.Working or SessionState.Waiting && !step.Done && s.Flow.Count > 0 && s.Flow[^1] == step;

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

        if (!compact && s.Flow.Count > 0)
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

    /// <summary>«Pedido», «+N» con lo anterior y los 3 últimos grupos, unidos por líneas; un punto viaja hacia el paso en curso.</summary>
    int ComposeLaneNodes(Graphics g, bool draw, SessionInfo s, int x0, int width, int y)
    {
        var groups = Groups(s.Flow);
        var shown = groups.TakeLast(3).ToList();
        var hidden = groups.Count - shown.Count;
        var columns = 1 + (hidden > 0 ? 1 : 0) + shown.Count;
        // Columnas iguales que llenan la tarjeta; los nodos quedan centrados en cada una.
        var column = width / Math.Max(columns, 4);
        var circle = S(26);
        var tinyHeight = Tiny.Height;

        var centers = new List<int>();
        for (var i = 0; i < columns; i++) centers.Add(x0 + i * column + column / 2);
        var cy = y + circle / 2;

        if (draw)
        {
            for (var i = 1; i < columns; i++)
            {
                var from = centers[i - 1] + circle / 2 + S(3);
                var to = centers[i] - circle / 2 - S(3);
                var intoCurrent = i == columns - 1 && shown.Count > 0 && IsCurrent(s, shown[^1].Last);
                using (var pen = new Pen(intoCurrent ? Blend(_t.Border, _t.Accent, 0.5) : _t.Border, S(2)))
                    g.DrawLine(pen, from, cy, to, cy);
                if (intoCurrent && s.State == SessionState.Working && to > from)
                {
                    var phase = Environment.TickCount64 % 1100 / 1100f;
                    var dot = S(6);
                    using var brush = new SolidBrush(_t.Accent);
                    g.FillEllipse(brush, from + (to - from - dot) * phase, cy - dot / 2f, dot, dot);
                }
            }
        }

        var index = 0;
        void Node(string glyph, string label, string? detail, Color fill, Color iconColor, Color? halo, bool outline)
        {
            var cx = centers[index++];
            var box = new Rectangle(cx - circle / 2, y, circle, circle);
            if (!draw) return;
            if (halo is { } h)
            {
                var wave = Environment.TickCount64 % 1400 / 1400.0;
                var grow = (float)(S(9) * wave);
                using var brush = new SolidBrush(Color.FromArgb((int)(110 * (1 - wave)), h));
                g.FillEllipse(brush, box.X - grow, box.Y - grow, box.Width + 2 * grow, box.Height + 2 * grow);
            }
            using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, box);
            if (outline) using (var pen = new Pen(_t.Border)) g.DrawEllipse(pen, box);
            Write(g, glyph, glyph.StartsWith('+') ? _small : _glyphSmall, iconColor, box, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var labelBox = new Rectangle(cx - column / 2, y + circle + S(4), column, tinyHeight);
            Write(g, label, Tiny, halo is null ? _t.Tertiary : _t.Text, labelBox, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            if (detail is not null)
                Write(g, detail, Tiny, _t.Tertiary, new Rectangle(labelBox.X, labelBox.Bottom, column, tinyHeight),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }

        Node("", "Pedido", null, _t.Background, _t.Secondary, null, outline: true);
        if (hidden > 0) Node("+" + hidden, "antes", null, _t.Background, _t.Secondary, null, outline: true);
        foreach (var group in shown)
        {
            var current = IsCurrent(s, group.Last);
            var waiting = current && (s.State == SessionState.Waiting || group.Kind == ToolKind.Ask);
            var failed = s.State == SessionState.Failed && group == shown[^1];
            var label = group.Count > 1 ? $"{group.Verb} ×{group.Count}" : group.Verb;
            var accent = waiting ? _t.Warn : failed ? _t.Danger : _t.Accent;
            Node(KindGlyph(group.Kind), label, current || failed ? group.Detail : null,
                current || failed ? accent : _t.Background, current || failed ? _t.Background : _t.Secondary,
                current ? accent : null, outline: !(current || failed));
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
        var current = s.Flow.Count > 0 && IsCurrent(s, s.Flow[^1]) ? s.Flow[^1] : null;
        var agent = s.Flow.LastOrDefault(f => f.Kind == ToolKind.Agent);
        var mapHeight = agent is null ? S(206) : S(226);
        var top = y;
        var mid = top + S(90);
        var account = new Rectangle(Pad, mid - S(14), S(64), S(28));
        var session = new Rectangle(Pad + S(76), mid - S(28), S(96), S(56));
        var toolWidth = S(92);
        var toolHeight = S(26);
        var toolX = Pad + Inner - toolWidth;
        var step = (S(194) - toolHeight) / (MapKinds.Length - 1);
        var bend = (toolX - session.Right) / 2;
        var agentBox = new Rectangle(session.X, session.Bottom + S(38), session.Width, S(44));

        if (draw)
        {
            using (var pen = new Pen(_t.Border, 1.5f * _scale)) g.DrawLine(pen, account.Right, mid, session.X, mid);
            for (var i = 0; i < MapKinds.Length; i++)
            {
                var kind = MapKinds[i];
                var ty = top + i * step + toolHeight / 2;
                var count = s.Flow.Count(f => f.Kind == kind);
                var active = current?.Kind == kind;
                using var path = new GraphicsPath();
                path.AddBezier(session.Right, mid, session.Right + bend, mid, toolX - bend, ty, toolX, ty);
                using var pen = new Pen(active ? _t.Accent : count > 0 ? Blend(_t.Border, _t.Secondary, 0.35) : _t.Border,
                    active ? 2.5f * _scale : count > 0 ? 1.5f * _scale : S(1));
                if (active)
                {
                    pen.DashPattern = [2.5f, 2.5f];
                    pen.DashOffset = -(Environment.TickCount64 % 700) / 700f * 5f;
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
                SessionState.Working => (current is null ? "Pensando…" : current.Verb, _t.Accent),
                SessionState.Waiting => ("Le espera a usted", _t.Warn),
                SessionState.Failed => ("Falló", _t.Danger),
                SessionState.Done => ("Terminó", _t.Success),
                _ => ("Inactiva", _t.Tertiary),
            };
            Draw.Fill(g, _t.Raised, session, S(10));
            Draw.Outline(g, s.State is SessionState.Working or SessionState.Waiting ? color : _t.Border, session, S(10));
            var inner = Rectangle.Inflate(session, -S(9), -S(6));
            Write(g, "Sesión", _small, _t.Text, new Rectangle(inner.X, inner.Y, inner.Width, smallHeight));
            Write(g, status, Tiny, color, new Rectangle(inner.X, inner.Y + smallHeight + S(1), inner.Width, Tiny.Height), TextFormatFlags.EndEllipsis);
            var steps = s.Flow.Count == 1 ? "1 paso" : $"{s.Flow.Count} pasos";
            Write(g, steps, Tiny, _t.Tertiary, new Rectangle(inner.X, inner.Y + smallHeight + Tiny.Height + S(2), inner.Width, Tiny.Height));

            for (var i = 0; i < MapKinds.Length; i++)
            {
                var kind = MapKinds[i];
                var box = new Rectangle(toolX, top + i * step, toolWidth, toolHeight);
                var count = s.Flow.Count(f => f.Kind == kind);
                var active = current?.Kind == kind;
                if (active)
                {
                    var wave = Environment.TickCount64 % 1400 / 1400.0;
                    var grow = (int)(S(6) * wave);
                    Draw.Fill(g, Color.FromArgb((int)(100 * (1 - wave)), _t.Accent), Rectangle.Inflate(box, grow, grow), S(8) + grow);
                    Draw.Fill(g, _t.Accent, box, S(8));
                }
                else Draw.Outline(g, count > 0 ? Blend(_t.Border, _t.Secondary, 0.35) : _t.Border, box, S(8));
                var textColor = active ? _t.Background : count > 0 ? _t.Text : _t.Tertiary;
                Write(g, KindLabel(kind), _small, textColor, new Rectangle(box.X + S(9), box.Y, box.Width - S(36), box.Height), TextFormatFlags.VerticalCenter);
                Write(g, count.ToString(), _small, active ? _t.Background : _t.Secondary, new Rectangle(box.Right - S(30), box.Y, S(21), box.Height),
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

        // Los últimos pasos, con su hora.
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
            var isCurrent = f == current;
            if (draw)
            {
                var time = f.At is { } at ? $"{at.LocalDateTime:HH:mm:ss}" : "";
                Write(g, time, _small, isCurrent ? _t.Accent : _t.Tertiary, new Rectangle(Pad, y, S(62), smallHeight));
                var text = f.Detail is null ? f.Verb : $"{f.Verb} · {f.Detail}";
                if (isCurrent) text += "  · en curso";
                Write(g, text, _small, isCurrent ? _t.Text : _t.Secondary, new Rectangle(Pad + S(66), y, Inner - S(66), smallHeight), TextFormatFlags.EndEllipsis);
            }
            y += smallHeight + S(5);
        }
        return y + S(10);
    }
}
