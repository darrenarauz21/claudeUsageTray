using Avalonia;
using Avalonia.Media;
using SD = System.Drawing;

namespace ClaudeUsageTray;

/// <summary>
/// Vista «Flujos» (igual que en Windows): un carril por sesión con sus pasos del turno; clic en un
/// carril abre el mapa de esa sesión. Mientras una sesión trabaja siempre hay algo animado.
/// </summary>
sealed partial class UsageView
{
    readonly Flows.Arrivals _arrivals = new();
    string? _flowSession;

    static Glyph KindGlyph(ToolKind kind) => kind switch
    {
        ToolKind.Read => Glyph.Read,
        ToolKind.Search => Glyph.Search,
        ToolKind.Edit => Glyph.Edit,
        ToolKind.Run => Glyph.Run,
        ToolKind.Agent => Glyph.Agent,
        ToolKind.Web => Glyph.Web,
        ToolKind.Ask => Glyph.Ask,
        _ => Glyph.Other,
    };

    static double EaseOut(double p) => 1 - (1 - p) * (1 - p);

    static double Wave(int periodMs) => Environment.TickCount64 % periodMs / (double)periodMs;

    /// <summary>Halo que se expande y se desvanece, en bucle.</summary>
    static void DrawHalo(DrawingContext dc, Rect box, SD.Color color, double spread)
    {
        var wave = Wave(1400);
        var grow = spread * wave;
        dc.DrawEllipse(Gfx.B(SD.Color.FromArgb((int)(110 * (1 - wave)), color)), null, box.Inflate(grow));
    }

    double ComposeFlows(DrawingContext? dc, double y)
    {
        if (_flowSession is { } id)
        {
            if (_sessions.FirstOrDefault(s => s.SessionId == id) is { } session) return ComposeFlowDetail(dc, session, y);
            _flowSession = null;
        }

        var lanes = _sessions.Where(s => s.State != SessionState.Idle).ToList();
        if (lanes.Count == 0)
        {
            Gfx.Write(dc, "No hay sesiones activas.", Gfx.Body, _t.Tertiary, new Rect(Pad, y, Inner, Gfx.Body.LineHeight));
            y += Gfx.Body.LineHeight + 10;
        }
        foreach (var session in lanes)
            y = ComposeLane(dc, session, y) + 8;

        const string hint = "Clic en una sesión: su mapa completo. Sale de los archivos de Claude Code; no gasta tokens.";
        var height = Gfx.MeasureWrapped(hint, Gfx.Small, Inner, 3);
        Gfx.Write(dc, hint, Gfx.Small, _t.Tertiary, new Rect(Pad, y + 2, Inner, height), maxLines: 3);
        return y + height + 14;
    }

    double ComposeLane(DrawingContext? dc, SessionInfo s, double y)
    {
        var key = "flow:" + s.SessionId;
        var compact = s.State == SessionState.Done;
        var top = y;
        var x0 = Pad + 4;
        var width = Inner - 8;
        var body = Gfx.Body.LineHeight;
        var small = Gfx.Small.LineHeight;

        if (dc is not null && _rowBounds.TryGetValue(key, out var previous))
        {
            if (!compact) Gfx.Fill(dc, _hoverKey == key ? _t.Hover : _t.Raised, previous, 10);
            else if (_hoverKey == key) Gfx.Fill(dc, _t.Hover, previous, 10);
            Gfx.Outline(dc, _t.Border, previous, 10);
        }

        y += 10;
        var tag = s.Account.Label;
        var tagWidth = Gfx.Measure(tag, Gfx.Small).Width;
        if (dc is not null)
        {
            DrawStateIcon(dc, s.State, new Rect(x0, y, 16, body));
            Gfx.Write(dc, s.DisplayTitle, Gfx.Body, compact ? _t.Secondary : _t.Text, new Rect(x0 + 22, y, width - 22 - tagWidth - 8, body));
            Gfx.Write(dc, tag, Gfx.Small, _t.Tertiary, new Rect(x0 + width - tagWidth, y, tagWidth + 1, body), center: true);
        }
        y += body + 2;

        var (status, color) = LaneStatus(s);
        Gfx.Write(dc, status, Gfx.Small, color, new Rect(x0 + 22, y, width - 22, small));
        y += small;

        if (!compact && (s.Flow.Count > 0 || Flows.Thinking(s)))
        {
            y += 12;
            y = ComposeLaneNodes(dc, s, x0, width, y);
        }

        var bounds = new Rect(Pad - 4, top, Inner + 8, y - top + 10);
        _rowBounds[key] = bounds;
        AddHit(key, bounds, HitKind.FlowSession, session: s);
        return bounds.Bottom;
    }

    (string Text, SD.Color Color) LaneStatus(SessionInfo s)
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

    double ComposeLaneNodes(DrawingContext? dc, SessionInfo s, double x0, double width, double y)
    {
        var groups = Flows.Groups(s.Flow);
        var thinking = Flows.Thinking(s);
        var shown = groups.TakeLast(thinking ? 2 : 3).ToList();
        var hidden = groups.Count - shown.Count;
        var columns = 1 + (hidden > 0 ? 1 : 0) + shown.Count + (thinking ? 1 : 0);
        var column = width / Math.Max(columns, 4);
        const double circle = 26;
        var tiny = Gfx.Tiny.LineHeight;
        var cy = y + circle / 2;
        var centers = Enumerable.Range(0, columns).Select(i => x0 + i * column + column / 2).ToList();
        var active = Flows.Active(s);

        var arrival = new double[columns];
        Array.Fill(arrival, 1);
        var first = 1 + (hidden > 0 ? 1 : 0);
        for (var i = 0; i < shown.Count; i++) arrival[first + i] = EaseOut(_arrivals.Progress(shown[i].Last.Id));

        if (dc is not null)
        {
            for (var i = 1; i < columns; i++)
            {
                var from = centers[i - 1] + circle / 2 + 3;
                var to = centers[i] - circle / 2 - 3;
                var live = i == columns - 1 && s.State is SessionState.Working or SessionState.Waiting;
                dc.DrawLine(Gfx.P(live ? Gfx.Blend(_t.Border, _t.Accent, 0.5) : _t.Border, 2), new Point(from, cy), new Point(from + (to - from) * arrival[i], cy));
                if (live && s.State == SessionState.Working && to > from)
                    dc.DrawEllipse(Gfx.B(_t.Accent), null, new Point(from + 3 + (to - from - 6) * Wave(1100), cy), 3, 3);
            }
        }

        var index = 0;
        void Node(Glyph? glyph, string? chip, string label, string? detail, SD.Color fill, SD.Color iconColor, SD.Color? halo, bool outline, double grow = 1)
        {
            var cx = centers[index++];
            if (dc is null) return;
            var size = circle * (0.45 + 0.55 * grow);
            var box = new Rect(cx - size / 2, y + (circle - size) / 2, size, size);
            if (halo is { } h) DrawHalo(dc, box, h, 9);
            dc.DrawEllipse(Gfx.B(fill), outline ? Gfx.P(_t.Border) : null, box);
            if (grow > 0.7)
            {
                if (glyph is { } g) Gfx.Icon(dc, g, iconColor, box, 13);
                else if (chip is not null) Gfx.Write(dc, chip, Gfx.Small, iconColor, box, TextAlignment.Center, center: true);
            }
            var labelBox = new Rect(cx - column / 2, y + circle + 4, column, tiny);
            Gfx.Write(dc, label, Gfx.Tiny, Gfx.Blend(_t.Background, halo is null ? _t.Tertiary : _t.Text, grow), labelBox, TextAlignment.Center);
            if (detail is not null)
                Gfx.Write(dc, detail, Gfx.Tiny, Gfx.Blend(_t.Background, _t.Tertiary, grow), new Rect(labelBox.X, labelBox.Bottom, column, tiny), TextAlignment.Center);
        }

        Node(Glyph.Message, null, "Pedido", null, _t.Background, _t.Secondary, null, outline: true);
        if (hidden > 0) Node(null, "+" + hidden, "antes", null, _t.Background, _t.Secondary, null, outline: true);
        foreach (var group in shown)
        {
            var isLast = group == shown[^1];
            var running = isLast && active is not null && group.Last == active && !thinking;
            var waiting = isLast && s.State == SessionState.Waiting;
            var failed = isLast && s.State == SessionState.Failed;
            var accent = waiting ? _t.Warn : failed ? _t.Danger : _t.Accent;
            var lit = running || waiting || failed;
            var label = group.Count > 1 ? $"{group.Verb} ×{group.Count}" : group.Verb;
            Node(KindGlyph(group.Kind), null, label, lit || (isLast && thinking) ? group.Detail : null,
                lit ? accent : _t.Background, lit ? _t.Background : _t.Secondary,
                running || waiting ? accent : null, outline: !lit, grow: arrival[index]);
        }
        if (thinking)
        {
            var cx = centers[index++];
            if (dc is not null)
            {
                var box = new Rect(cx - circle / 2, y, circle, circle);
                DrawHalo(dc, box, _t.Accent, 8);
                dc.DrawEllipse(Gfx.B(_t.Background), Gfx.P(_t.Accent, 2), box);
                DrawSpinner(dc, box);
                Gfx.Write(dc, "Pensando…", Gfx.Tiny, _t.Text, new Rect(cx - column / 2, y + circle + 4, column, tiny), TextAlignment.Center);
            }
        }
        y += circle + 4 + 2 * tiny;

        if (s.Flow.LastOrDefault(f => f.Kind == ToolKind.Agent) is { } agent)
        {
            y += 6;
            var text = $"↳ {agent.AgentType ?? "subagente"} · " + (agent.Done
                ? agent.AgentSteps is { } n ? $"volvió · {n} pasos" : "volvió"
                : "trabajando…");
            Gfx.Write(dc, text, Gfx.Tiny, agent.Done ? _t.Tertiary : _t.Accent, new Rect(x0 + 4, y, width, tiny));
            y += tiny;
        }
        return y;
    }

    // ── Mapa de una sesión ──────────────────────────────────────────────────────────────────

    double ComposeFlowDetail(DrawingContext? dc, SessionInfo s, double y)
    {
        var back = new Rect(Pad - 4, y, 28, 28);
        AddHit("flow-back", back, HitKind.FlowBack);
        var body = Gfx.Body.LineHeight;
        var small = Gfx.Small.LineHeight;
        var tiny = Gfx.Tiny.LineHeight;
        if (dc is not null)
        {
            Gfx.Fill(dc, _hoverKey == "flow-back" ? _t.Hover : _t.Raised, back, 8);
            Gfx.Icon(dc, Glyph.Back, _t.Text, back, 14);
            Gfx.Write(dc, s.DisplayTitle, Gfx.Body, _t.Text, new Rect(back.Right + 10, y - 2, Inner - 40, body));
            var sub = string.Join(" · ", new[] { Path.GetFileName(s.Cwd.TrimEnd('\\', '/')), Format.ModelLine(s) }.Where(p => !string.IsNullOrEmpty(p)));
            Gfx.Write(dc, sub, Gfx.Small, _t.Tertiary, new Rect(back.Right + 10, y + body, Inner - 40, small));
        }
        y += Math.Max(28, body + small) + 14;

        var active = Flows.Active(s);
        var thinking = Flows.Thinking(s);
        var working = s.State is SessionState.Working or SessionState.Waiting;
        var agent = s.Flow.LastOrDefault(f => f.Kind == ToolKind.Agent);
        var mapHeight = agent is null ? 206.0 : 226.0;
        var top = y;
        var mid = top + 90;
        var account = new Rect(Pad, mid - 14, 64, 28);
        var session = new Rect(Pad + 76, mid - 28, 96, 56);
        const double toolWidth = 92, toolHeight = 26;
        var toolX = Pad + Inner - toolWidth;
        var step = (194 - toolHeight) / (Flows.MapKinds.Length - 1);
        var bend = (toolX - session.Right) / 2;
        var agentBox = new Rect(session.X, session.Bottom + 38, session.Width, 44);

        if (dc is not null)
        {
            dc.DrawLine(Gfx.P(_t.Border, 1.5), new Point(account.Right, mid), new Point(session.X, mid));
            for (var i = 0; i < Flows.MapKinds.Length; i++)
            {
                var kind = Flows.MapKinds[i];
                var ty = top + i * step + toolHeight / 2;
                var count = s.Flow.Count(f => f.Kind == kind);
                var lit = active?.Kind == kind;
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(new Point(session.Right, mid), false);
                    ctx.CubicBezierTo(new Point(session.Right + bend, mid), new Point(toolX - bend, ty), new Point(toolX, ty));
                    ctx.EndFigure(false);
                }
                IPen pen = lit
                    // Hacia la herramienta mientras la usa; de vuelta a la sesión mientras piensa.
                    ? new Pen(Gfx.B(_t.Accent), 2.5, new DashStyle([1, 1], (thinking ? 1 : -1) * Wave(700) * 2))
                    : Gfx.P(count > 0 ? Gfx.Blend(_t.Border, _t.Secondary, 0.35) : _t.Border, count > 0 ? 1.5 : 1);
                dc.DrawGeometry(null, pen, geometry);
            }
            if (agent is not null)
                dc.DrawLine(new Pen(Gfx.B(Gfx.Blend(_t.Border, _t.Secondary, 0.35)), 1.5, new DashStyle([1.3, 2], 0)),
                    new Point(session.Center.X, session.Bottom), new Point(session.Center.X, agentBox.Y));

            Gfx.Fill(dc, _t.Raised, account, 14);
            Gfx.Outline(dc, _t.Border, account, 14);
            Gfx.Write(dc, s.Account.Label, Gfx.Small, _t.Text, account, TextAlignment.Center, center: true);

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
                var wave = Wave(1400);
                Gfx.Fill(dc, SD.Color.FromArgb((int)(70 * (1 - wave)), color), session.Inflate(6 * wave), 10 + 6 * wave);
            }
            Gfx.Fill(dc, _t.Raised, session, 10);
            Gfx.Outline(dc, working ? color : _t.Border, session, 10);
            var inner = session.Deflate(new Thickness(9, 6));
            Gfx.Write(dc, "Sesión", Gfx.Small, _t.Text, new Rect(inner.X, inner.Y, inner.Width, small));
            Gfx.Write(dc, status, Gfx.Tiny, color, new Rect(inner.X, inner.Y + small + 1, inner.Width, tiny));
            Gfx.Write(dc, s.Flow.Count == 1 ? "1 paso" : $"{s.Flow.Count} pasos", Gfx.Tiny, _t.Tertiary, new Rect(inner.X, inner.Y + small + tiny + 2, inner.Width, tiny));

            for (var i = 0; i < Flows.MapKinds.Length; i++)
            {
                var kind = Flows.MapKinds[i];
                var box = new Rect(toolX, top + i * step, toolWidth, toolHeight);
                var count = s.Flow.Count(f => f.Kind == kind);
                var lit = active?.Kind == kind;
                if (lit)
                {
                    var wave = Wave(1400);
                    Gfx.Fill(dc, SD.Color.FromArgb((int)(100 * (1 - wave)), _t.Accent), box.Inflate(6 * wave), 8 + 6 * wave);
                    Gfx.Fill(dc, _t.Accent, box, 8);
                }
                else Gfx.Outline(dc, count > 0 ? Gfx.Blend(_t.Border, _t.Secondary, 0.35) : _t.Border, box, 8);
                Gfx.Write(dc, Flows.KindLabel(kind), Gfx.Small, lit ? _t.Background : count > 0 ? _t.Text : _t.Tertiary,
                    new Rect(box.X + 9, box.Y, box.Width - 36, box.Height), center: true);
                Gfx.Write(dc, count.ToString(), Gfx.Small, lit ? _t.Background : _t.Secondary,
                    new Rect(box.Right - 30, box.Y, 21, box.Height), TextAlignment.Right, center: true);
            }

            if (agent is not null)
            {
                Gfx.Fill(dc, _t.Background, agentBox, 10);
                dc.DrawRectangle(null, new Pen(Gfx.B(Gfx.Blend(_t.Border, _t.Secondary, 0.35)), 1, new DashStyle([3, 2], 0)), agentBox.Deflate(0.5), 10, 10);
                var a = agentBox.Deflate(new Thickness(9, 5));
                Gfx.Write(dc, "Subagente " + (agent.AgentType ?? ""), Gfx.Small, _t.Secondary, new Rect(a.X, a.Y, a.Width, small));
                var state = agent.Done ? agent.AgentSteps is { } n ? $"volvió · {n} pasos" : "volvió" : "trabajando…";
                Gfx.Write(dc, state, Gfx.Tiny, agent.Done ? _t.Tertiary : _t.Accent, new Rect(a.X, a.Y + small + 2, a.Width, tiny));
            }
        }
        y = top + mapHeight;

        Gfx.Write(dc, "ÚLTIMOS PASOS DEL TURNO", Gfx.Small, _t.Tertiary, new Rect(Pad, y, Inner, small));
        y += small + 6;
        var last = s.Flow.TakeLast(5).ToList();
        if (last.Count == 0)
        {
            Gfx.Write(dc, "Aún no hay pasos en este turno.", Gfx.Small, _t.Tertiary, new Rect(Pad, y, Inner, small));
            y += small + 4;
        }
        foreach (var f in last)
        {
            var isActive = working && f == active;
            if (dc is not null)
            {
                var p = EaseOut(_arrivals.Progress(f.Id));
                var time = f.At is { } at ? $"{at.LocalDateTime:HH:mm:ss}" : "";
                Gfx.Write(dc, time, Gfx.Small, Gfx.Blend(_t.Background, isActive ? _t.Accent : _t.Tertiary, p), new Rect(Pad, y, 62, small));
                var text = f.Detail is null ? f.Verb : $"{f.Verb} · {f.Detail}";
                if (isActive) text += thinking ? "  · listo" : "  · en curso";
                Gfx.Write(dc, text, Gfx.Small, Gfx.Blend(_t.Background, isActive ? _t.Text : _t.Secondary, p),
                    new Rect(Pad + 66 - 14 * (1 - p), y, Inner - 66, small));
            }
            y += small + 5;
        }
        return y + 10;
    }
}
