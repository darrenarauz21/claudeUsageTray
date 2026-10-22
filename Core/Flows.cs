namespace ClaudeUsageTray;

/// <summary>Pasos seguidos de la misma familia, juntos en un nodo («Leyendo ×3»).</summary>
sealed record FlowGroup(ToolKind Kind, string Verb, string? Detail, int Count, bool Done, FlowStep Last);

/// <summary>Lo que la vista Flujos necesita saber de una sesión, igual en las dos apps.</summary>
static class Flows
{
    public static readonly ToolKind[] MapKinds = [ToolKind.Read, ToolKind.Search, ToolKind.Edit, ToolKind.Run, ToolKind.Agent, ToolKind.Web];

    /// <summary>Un paso nuevo se dibuja creciendo durante este tiempo.</summary>
    public const int EntranceMs = 450;

    public static List<FlowGroup> Groups(IReadOnlyList<FlowStep> flow)
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

    /// <summary>
    /// El último paso, mientras la sesión trabaja o espera: aunque ya haya terminado (leer o buscar
    /// duran milisegundos), es lo último que hizo y la vista lo sigue mostrando vivo.
    /// </summary>
    public static FlowStep? Active(SessionInfo s) =>
        s.State is SessionState.Working or SessionState.Waiting && s.Flow.Count > 0 ? s.Flow[^1] : null;

    /// <summary>Trabaja y el último paso ya volvió: el modelo está pensando el siguiente.</summary>
    public static bool Thinking(SessionInfo s) =>
        s.State == SessionState.Working && (s.Flow.Count == 0 || s.Flow[^1].Done);

    static double Bump(double t, double from, double to) =>
        t < from || t > to ? 0 : Math.Sin(Math.PI * (t - from) / (to - from));

    /// <summary>Latido de «Pensando»: dos golpes (el segundo más suave) y una pausa. De 0 a 1.</summary>
    public static double Heartbeat(long tick)
    {
        var t = tick % 1100 / 1100.0;
        return Math.Max(Bump(t, 0, 0.16), 0.7 * Bump(t, 0.22, 0.38));
    }

    /// <summary>Altura del salto de cada uno de los tres puntos, uno detrás del otro. De 0 a 1.</summary>
    public static double DotBounce(long tick, int index)
    {
        var t = (tick % 1000 / 1000.0 - index * 0.16 + 1) % 1;
        return Bump(t, 0, 0.4);
    }

    public static string KindLabel(ToolKind kind) => kind switch
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

    /// <summary>Recuerda cuándo apareció cada paso, para animar su entrada.</summary>
    public sealed class Arrivals
    {
        readonly Dictionary<string, long> _seen = new();
        bool _primed;

        /// <summary>Hasta cuándo hay entradas animándose (TickCount64).</summary>
        public long BusyUntil { get; private set; }

        /// <summary>Registra los pasos nuevos. La primera vez no anima: lo que ya estaba no "entra".</summary>
        public void Update(IEnumerable<SessionInfo> sessions)
        {
            var now = Environment.TickCount64;
            var current = new HashSet<string>();
            foreach (var step in sessions.SelectMany(s => s.Flow))
            {
                current.Add(step.Id);
                if (_seen.ContainsKey(step.Id)) continue;
                _seen[step.Id] = _primed ? now : 0;
                if (_primed) BusyUntil = now + EntranceMs;
            }
            foreach (var gone in _seen.Keys.Where(k => !current.Contains(k)).ToList()) _seen.Remove(gone);
            _primed = true;
        }

        /// <summary>De 0 (recién llegado) a 1 (ya entró).</summary>
        public double Progress(string id) =>
            _seen.TryGetValue(id, out var at) && at > 0 ? Math.Clamp((Environment.TickCount64 - at) / (double)EntranceMs, 0, 1) : 1;
    }
}
