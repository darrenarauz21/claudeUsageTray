using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ClaudeUsageTray;

/// <summary>
/// Los modelos que se pueden elegir para la mascota y para la isla: alias de Claude Code, que siempre
/// apuntan al último de cada familia. La isla puede usar además «el de sus sesiones»: sin --model,
/// Claude Code usa el predeterminado de la cuenta (comprobado: con Opus 5.5 en sus sesiones, respondió
/// Opus 5.5).
/// </summary>
static class AssistantModels
{
    public static readonly (string Id, string Label)[] Choices =
        [("haiku", "Haiku"), ("sonnet", "Sonnet"), ("opus", "Opus"), ("fable", "Fable")];

    /// <summary>Para la isla: primero «el de sus sesiones» (Id vacío).</summary>
    public static readonly (string Id, string Label)[] IslandChoices = [("", "El de sus sesiones"), .. Choices];

    public static string Label(string? id) =>
        string.IsNullOrEmpty(id) ? "El de sus sesiones" : Choices.FirstOrDefault(c => c.Id == id).Label ?? id;

}

/// <summary>
/// La tecla para hablar: una de estas, que se mantiene (nombres de System.Windows.Forms.Keys), o una tecla o
/// combinación grabada en Ajustes («Win+Shift+F23»), que también se toca una vez para hablar sin mantenerla.
/// </summary>
static class MicKeys
{
    public static readonly (string Id, string Label)[] Choices =
        [("RControlKey", "Ctrl derecho"), ("RMenu", "Alt derecho"), ("RShiftKey", "Mayús derecha")];

    /// <summary>Es una de las de mantener (si no, una grabada).</summary>
    public static bool IsHold(string id) => Choices.Any(c => c.Id == id);

    public static string Label(string id) =>
        Choices.FirstOrDefault(c => c.Id == id).Label ?? (id.Length > 0 ? KeyNames.Label(id) : Choices[0].Label);
}

/// <summary>Una combinación guardada como texto («Ctrl+Alt+Space», nombres de Keys), dicha como se ve en el teclado.</summary>
static class KeyNames
{
    public const string Copilot = "Win+Shift+F23";
    public const string DefaultChat = "Ctrl+Alt+Space";

    static readonly Dictionary<string, string> Spanish = new()
    {
        ["Shift"] = "Mayús", ["Space"] = "Espacio", ["Return"] = "Intro", ["Enter"] = "Intro", ["Escape"] = "Esc",
        ["Back"] = "Retroceso", ["Apps"] = "Menú", ["Prior"] = "RePág", ["PageUp"] = "RePág", ["Next"] = "AvPág",
        ["PageDown"] = "AvPág", ["Home"] = "Inicio", ["End"] = "Fin", ["Capital"] = "Bloq Mayús",
        ["Insert"] = "Insert", ["Delete"] = "Supr", ["Left"] = "←", ["Right"] = "→", ["Up"] = "↑", ["Down"] = "↓",
        ["Pause"] = "Pausa", ["Scroll"] = "Bloq Despl", ["Snapshot"] = "Impr Pant",
    };

    public static string Label(string combo)
    {
        if (combo == Copilot) return "Tecla Copilot";
        return string.Join("+", combo.Split('+').Select(part =>
            Spanish.TryGetValue(part, out var name) ? name
            : part.Length == 2 && part[0] == 'D' && char.IsDigit(part[1]) ? part[1..]
            : part.StartsWith("NumPad") ? "Num " + part[6..]
            : part));
    }
}

/// <summary>Quién pregunta: la mascota responde corto, en la nubecita; la isla, algo más largo.</summary>
enum AssistantVoice { Mascot, Island }

/// <summary>En qué va una respuesta: arrancó Claude Code, está leyendo el archivo o ya escribe.</summary>
enum AssistantStage { Opening, Reading, Writing }

/// <summary>Un turno de la conversación (lo que dijo la persona o lo que respondió).</summary>
sealed record ChatTurn(bool FromPerson, string Text);

/// <summary>Una tarea que Claude propone abrir en una sesión de Claude Code. Folder: dónde abrirla (la del archivo soltado); null, la de las tareas.</summary>
sealed record TaskProposal(string Prompt, string? Folder = null);

/// <summary>Algo que la mascota hace en el equipo (cambiar el fondo, el volumen…): Kind es la acción y Value su valor, como los dio Claude.</summary>
sealed record AssistantAction(string Kind, string Value);

/// <summary>
/// Lo que se le pide al asistente. File: un archivo o una carpeta para analizar (si no, es una
/// pregunta). Model null: el de la cuenta. Context: el uso y las sesiones del momento, en texto.
/// </summary>
sealed record AssistantRequest
{
    public required string Prompt { get; init; }
    public AssistantVoice Voice { get; init; }
    public string? Model { get; init; }
    public string? ConfigDir { get; init; }
    public string Context { get; init; } = "";
    public IReadOnlyList<ChatTurn> History { get; init; } = [];
    public string? File { get; init; }
    /// <summary>Cómo se llama la mascota que responde (el chibi o la clásica), para el tono.</summary>
    public bool Chibi { get; init; } = true;
}

/// <summary>
/// La respuesta terminada. Text: lo que se muestra (sin la marca de tarea). Short: la primera línea
/// de un análisis, para la nubecita. Model: el modelo que respondió (para el chip de la isla). Action:
/// lo que pidió hacer en el equipo; lo ejecuta la app (SystemActions).
/// </summary>
sealed record AssistantResult(string Text, string? Short, string? Model, TaskProposal? Task, string? Error, AssistantAction? Action = null)
{
    public bool Failed => Error is not null;
}

/// <summary>
/// El asistente de la mascota y de la isla: cada pregunta corre «claude -p» con la cuenta y el modelo
/// elegidos, en modo seguro (sin el CLAUDE.md de la persona, hooks ni MCP: comprobado, baja la entrada
/// de unos 7.400 a 1.000 tokens) y sin guardar la sesión. La pregunta va por la entrada estándar y el
/// prompt de sistema en un archivo, así que nada pasa por la línea de comandos. La respuesta llega por
/// partes (stream-json). El chat solo puede buscar en internet (WebSearch y WebFetch); el análisis de
/// un archivo solo puede leer su carpeta. Lo demás no lo hace Claude: pide una acción de una lista
/// cerrada ([ACCION], la ejecuta la app) o se lo encarga a una sesión de Claude Code ([TAREA]). Cada
/// consulta deja un sessions/&lt;pid&gt;.json mientras corre: SessionMonitor las ignora porque corren
/// en WorkDir.
/// </summary>
static class Assistant
{
    /// <summary>La carpeta donde corren las consultas (y donde quedan los prompts de sistema).</summary>
    public static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "asistente");

    /// <summary>Una sesión que corre en la carpeta del asistente: no es de la persona.</summary>
    public static bool IsOwn(string? cwd) =>
        !string.IsNullOrEmpty(cwd) && Account.SamePath(cwd, WorkDir);

    /// <summary>Lo que se le pide a Claude Code para marcar una tarea: la primera línea empieza así.</summary>
    const string TaskMark = "[TAREA]";

    /// <summary>Lo que se le pide para hacer algo en el equipo: la primera línea empieza así y sigue un JSON.</summary>
    const string ActionMark = "[ACCION]";

    static readonly string[] Marks = [TaskMark, ActionMark];

    /// <summary>Lo escrito hasta ahora puede ser el comienzo de una marca (o ya la trae completa).</summary>
    static bool MayBeMark(string start) => Marks.Any(m => start.Length <= m.Length
        ? m.StartsWith(start, StringComparison.OrdinalIgnoreCase)
        : start.StartsWith(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Pregunta (o analiza el archivo) y avisa por partes: onText con cada trozo de texto que llega y
    /// onStage cuando cambia de etapa. Termina con el resultado; si se cancela, el proceso se cierra.
    /// </summary>
    public static async Task<AssistantResult> AskAsync(AssistantRequest request, Action<string>? onText = null,
        Action<AssistantStage>? onStage = null, CancellationToken cancel = default)
    {
        string? claude = FindClaude();
        if (claude is null)
            return new AssistantResult("", null, null, null, "No encontré Claude Code. ¿Está instalado y en el PATH?");

        Directory.CreateDirectory(WorkDir);
        var systemFile = Path.Combine(WorkDir, request.Voice == AssistantVoice.Mascot ? "mascota.txt" : "isla.txt");
        await File.WriteAllTextAsync(systemFile, SystemPrompt(request), cancel);

        var start = new ProcessStartInfo
        {
            WorkingDirectory = WorkDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        var args = new List<string> { "-p", "--safe-mode", "--no-session-persistence", "--verbose",
            "--output-format", "stream-json", "--include-partial-messages", "--system-prompt-file", systemFile };
        if (request.File is { } file)
        {
            var folder = Directory.Exists(file) ? file : Path.GetDirectoryName(file) ?? file;
            args.AddRange(["--tools", "Read,Glob,Grep", "--allowedTools", "Read,Glob,Grep", "--add-dir", folder,
                "--permission-mode", "dontAsk", "--permission-prompts", "none"]);
        }
        else args.AddRange(["--tools", "WebSearch,WebFetch", "--allowedTools", "WebSearch,WebFetch",
            "--permission-mode", "dontAsk", "--permission-prompts", "none"]);
        if (!string.IsNullOrEmpty(request.Model)) args.AddRange(["--model", request.Model]);

        if (claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Instalado con npm: claude.cmd va por cmd.exe. Los argumentos son solo opciones y rutas, sin texto de la persona.
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/s");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(claude);
        }
        else start.FileName = claude;
        foreach (var arg in args) start.ArgumentList.Add(arg);

        // La cuenta: la carpeta por defecto no lleva CLAUDE_CONFIG_DIR. Y nada heredado de una sesión de
        // Claude Code (si la app se abrió desde una), para que no se crea anidada.
        if (request.ConfigDir is null || Account.SamePath(request.ConfigDir, Account.DefaultDir)) start.Environment.Remove("CLAUDE_CONFIG_DIR");
        else start.Environment["CLAUDE_CONFIG_DIR"] = request.ConfigDir;
        foreach (var name in new[] { "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SSE_PORT", "ANTHROPIC_API_KEY" })
            start.Environment.Remove(name);

        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (Exception ex) { return new AssistantResult("", null, null, null, $"No se pudo abrir Claude Code: {ex.Message}"); }
        onStage?.Invoke(AssistantStage.Opening);

        using var kill = cancel.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { }
        });

        await process.StandardInput.WriteAsync(UserMessage(request));
        process.StandardInput.Close();

        var stderr = process.StandardError.ReadToEndAsync();
        var text = new StringBuilder();
        string? model = null, final = null, error = null;
        var stage = AssistantStage.Opening;
        // La línea de la tarea o de la acción no se muestra mientras llega: se espera a ver si la respuesta empieza con una marca.
        var head = new StringBuilder();
        var shown = false;
        void Show(string piece)
        {
            if (shown) { onText?.Invoke(piece); return; }
            head.Append(piece);
            var start = head.ToString().TrimStart();
            if (start.Length == 0) return;
            if (!MayBeMark(start))
            {
                shown = true;
                onText?.Invoke(head.ToString());
                return;
            }
            var newline = start.IndexOf('\n');
            if (newline < 0) return;
            shown = true;
            if (newline + 1 < start.Length) onText?.Invoke(start[(newline + 1)..].TrimStart());
        }
        // Las negritas (**) no se ven en la nubecita ni en la isla: se quitan, aunque lleguen partidas en dos trozos.
        var star = false;
        string Plain(string piece)
        {
            piece = (star ? "*" + piece : piece).Replace("**", "");
            star = piece.EndsWith('*');
            return star ? piece[..^1] : piece;
        }
        void Stage(AssistantStage s)
        {
            if (s <= stage) return;
            stage = s;
            onStage?.Invoke(s);
        }

        while (await process.StandardOutput.ReadLineAsync(cancel) is { } line)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var r = doc.RootElement;
                switch (Json.Str(r, "type"))
                {
                    case "system" when Json.Str(r, "subtype") == "init":
                        model = Json.Str(r, "model") ?? model;
                        break;
                    case "stream_event" when r.TryGetProperty("event", out var e):
                        switch (Json.Str(e, "type"))
                        {
                            case "message_start" when e.TryGetProperty("message", out var m):
                                model = Json.Str(m, "model") ?? model;
                                break;
                            case "content_block_start" when e.TryGetProperty("content_block", out var block)
                                && Json.Str(block, "type") == "tool_use":
                                Stage(AssistantStage.Reading);
                                break;
                            case "content_block_delta" when e.TryGetProperty("delta", out var delta)
                                && Json.Str(delta, "type") == "text_delta" && Json.Str(delta, "text") is { } piece:
                                // En un análisis, el texto antes de leer («voy a leer…») no cuenta: se empieza de nuevo.
                                if (request.File is not null && stage < AssistantStage.Reading) break;
                                Stage(AssistantStage.Writing);
                                text.Append(piece);
                                Show(Plain(piece));
                                break;
                        }
                        break;
                    case "result":
                        final = Json.Str(r, "result");
                        if (r.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                            error = final ?? "Claude Code no pudo responder.";
                        if (r.TryGetProperty("modelUsage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                            model = usage.EnumerateObject().Select(p => p.Name).FirstOrDefault(n => !n.Contains("haiku")) ?? model;
                        break;
                }
            }
            catch (JsonException) { }
        }
        await process.WaitForExitAsync(CancellationToken.None);
        if (cancel.IsCancellationRequested) return new AssistantResult(text.ToString(), null, model, null, "Cancelado.");

        var answer = (final ?? text.ToString()).Replace("**", "").Trim();
        if (answer.Length == 0 && error is null)
        {
            var problem = (await stderr).Trim();
            error = problem.Length > 0 ? problem.Split('\n')[0].Trim() : "Claude Code no respondió.";
        }
        if (error is not null) return new AssistantResult("", null, model, null, Friendly(error));

        // ¿Es una tarea o una acción? La primera línea lo dice; lo demás es lo que se muestra.
        TaskProposal? task = null;
        AssistantAction? action = null;
        if (TakeMark(ref answer, TaskMark) is { Length: > 0 } prompt)
        {
            // El archivo soltado: la sesión se abre en su carpeta.
            var folder = request.File is { } dropped ? (Directory.Exists(dropped) ? dropped : Path.GetDirectoryName(dropped)) : null;
            task = new TaskProposal(prompt, folder);
        }
        else if (TakeMark(ref answer, ActionMark) is { } json)
            action = ParseAction(json);
        string? brief = null;
        if (request.File is not null)
        {
            var newline = answer.IndexOf('\n');
            brief = (newline < 0 ? answer : answer[..newline]).Trim();
        }
        return new AssistantResult(answer, brief, model, task, null, action);
    }

    /// <summary>Si la respuesta empieza con la marca, la quita junto con su línea y devuelve lo que seguía a la marca; si no, null.</summary>
    static string? TakeMark(ref string answer, string mark)
    {
        if (!answer.StartsWith(mark, StringComparison.OrdinalIgnoreCase)) return null;
        var newline = answer.IndexOf('\n');
        var rest = (newline < 0 ? answer[mark.Length..] : answer[mark.Length..newline]).Trim();
        answer = newline < 0 ? "" : answer[(newline + 1)..].Trim();
        return rest;
    }

    /// <summary>{"accion":"…","valor":"…"}; si no se entiende, una acción sin nombre (que la app dice no saber hacer).</summary>
    static AssistantAction ParseAction(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var kind = Json.Str(doc.RootElement, "accion") ?? "";
            var value = doc.RootElement.TryGetProperty("valor", out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()
                : "";
            return new AssistantAction(kind.Trim().ToLowerInvariant(), value.Trim());
        }
        catch (JsonException) { return new AssistantAction("", ""); }
    }

    /// <summary>Un error de Claude Code dicho en corto (los conocidos, en español).</summary>
    static string Friendly(string error)
    {
        if (error.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || error.Contains("usage limit", StringComparison.OrdinalIgnoreCase))
            return "Se alcanzó el límite de uso de esa cuenta.";
        if (error.Contains("login", StringComparison.OrdinalIgnoreCase) || error.Contains("authenticat", StringComparison.OrdinalIgnoreCase))
            return "Esa cuenta pide iniciar sesión en Claude Code.";
        return error.Length > 160 ? error[..160] + "…" : error;
    }

    static string SystemPrompt(AssistantRequest r)
    {
        var who = r.Chibi
            ? "una chica chibi de pelo naranja con un hoodie de cangrejo"
            : "la mascota clásica de Claude, una criaturita naranja de pixeles";
        var prompt = new StringBuilder();
        prompt.AppendLine($"Eres Claupet, la mascota de escritorio de Claude: {who}. Hablas en español, de tú, con calidez y alguna carita de texto como (^_^).");
        if (r.File is not null)
        {
            prompt.AppendLine("Te soltaron un archivo (o una carpeta) para que lo mires. Si puedes leerlo con tus herramientas (texto, código, PDF, imágenes), léelo y responde así:");
            prompt.AppendLine("primera línea: lo esencial en una frase, de 140 caracteres como máximo;");
            prompt.AppendLine("después, en un párrafo aparte, un resumen claro de 2 a 4 frases.");
            prompt.AppendLine("Sin títulos ni markdown.");
            prompt.AppendLine($"Si con tus herramientas no se puede abrir (Excel, Word, PowerPoint, comprimidos u otros binarios), no lo intentes ni digas que no puedes: encárgaselo a Claude Code. Empieza la respuesta con una línea exacta «{TaskMark} Abre «<la ruta completa del archivo>» y <lo que haga falta: resumirlo, decir qué contiene…>» y, en la línea siguiente, una frase corta para la persona.");
        }
        else if (r.Voice == AssistantVoice.Mascot)
            prompt.AppendLine("Respondes en una o dos frases cortas: como máximo 220 caracteres, sin listas, sin markdown, sin títulos.");
        else
            prompt.AppendLine("Respondes claro y breve: hasta 4 frases, o una lista corta con guiones si ayuda. En texto plano: sin títulos, negritas ni markdown.");
        prompt.AppendLine("Si no sabes algo, dilo; no inventes.");
        if (r.File is null)
        {
            prompt.AppendLine();
            prompt.AppendLine("Internet: puedes buscar con WebSearch y leer páginas con WebFetch. No anuncies que vas a buscar: busca y responde, sin enlaces ni lista de fuentes. Para el clima, usa WebFetch con https://wttr.in/<ciudad>?format=j1&lang=es (sin ciudad, https://wttr.in/?format=j1&lang=es, que usa la ubicación del equipo).");
            prompt.AppendLine();
            prompt.AppendLine($"Acciones en este equipo: si te piden una de esta lista, empieza la respuesta con una línea exacta «{ActionMark} {{\"accion\":\"…\",\"valor\":\"…\"}}» (el JSON en una sola línea) y, en la línea siguiente, una frase corta diciendo lo que hiciste. Una acción por respuesta.");
            prompt.AppendLine("- fondo: el fondo de pantalla. valor: la ruta de una imagen del equipo o la URL https directa de una imagen .jpg o .png.");
            prompt.AppendLine("- tema: «claro» u «oscuro» (el modo de Windows).");
            prompt.AppendLine("- color: el color de esta app: Claude, Océano, Bosque, Lavanda, Rosa o Grafito.");
            prompt.AppendLine("- volumen: un número de 0 a 100, «subir», «bajar», «silenciar» o «activar».");
            prompt.AppendLine("- musica: «reproducir», «pausar», «siguiente» o «anterior» (lo que suene en Windows).");
            prompt.AppendLine("- abrir: una URL (https, spotify: o ms-settings:) o el nombre de una app del menú Inicio. Para poner una canción o un artista, busca el video con WebSearch y abre su URL de YouTube; si piden Spotify, abre spotify:search:<lo que pidan>.");
            prompt.AppendLine();
            prompt.AppendLine($"Lo demás, encárgaselo a Claude Code: si te piden algo que no puedes hacer con tus herramientas ni con esas acciones (abrir o editar archivos, otros ajustes de Windows, instalar algo, programar, revisar un proyecto…), no lo intentes ni digas que no puedes. Empieza la respuesta con una línea exacta «{TaskMark} <la instrucción completa y clara para Claude Code, en español>» y, en la línea siguiente, una frase corta para la persona. La sesión se abrirá en la carpeta que elija la persona: no inventes rutas, pero si la persona nombró un archivo o una carpeta, ponlo tal cual. Si solo preguntan algo, responde normal.");
        }
        if (r.Context.Length > 0)
        {
            prompt.AppendLine();
            prompt.AppendLine("Lo que sabes del momento (úsalo solo si viene al caso; no lo recites):");
            prompt.AppendLine(r.Context);
        }
        return prompt.ToString();
    }

    static string UserMessage(AssistantRequest r)
    {
        if (r.File is { } file)
            return $"Mira este {(Directory.Exists(file) ? "carpeta" : "archivo")}: {file}" + (r.Prompt.Length > 0 ? $"\n\n{r.Prompt}" : "");
        if (r.History.Count == 0) return r.Prompt;
        var text = new StringBuilder("La conversación hasta ahora:\n");
        foreach (var turn in r.History.TakeLast(8))
            text.Append(turn.FromPerson ? "Persona: " : "Tú: ").AppendLine(turn.Text.ReplaceLineEndings(" "));
        text.AppendLine().Append("Persona: ").Append(r.Prompt);
        return text.ToString();
    }

    /// <summary>
    /// Dónde está claude: el instalador nativo lo deja en ~/.local/bin; con npm queda un claude.cmd en
    /// el PATH (Windows) o un claude en el PATH (Linux y macOS).
    /// </summary>
    public static string? FindClaude()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe", "claude.cmd" } : ["claude"];
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Prepend(Path.Combine(Account.Home, ".local", "bin"));
        if (!OperatingSystem.IsWindows()) dirs = dirs.Concat(["/opt/homebrew/bin", "/usr/local/bin"]);
        foreach (var dir in dirs)
            foreach (var name in names)
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch (Exception) { }
            }
        return null;
    }
}

/// <summary>
/// La conversación que comparten la nubecita y la isla: se olvida a los 10 minutos sin escribir o con
/// «Nueva conversación».
/// </summary>
sealed class ChatThread
{
    static readonly TimeSpan Forget = TimeSpan.FromMinutes(10);
    readonly List<ChatTurn> _turns = new();
    DateTime _last = DateTime.MinValue;

    public IReadOnlyList<ChatTurn> Turns
    {
        get
        {
            if (DateTime.Now - _last > Forget) _turns.Clear();
            return _turns;
        }
    }

    public void Add(ChatTurn turn)
    {
        if (DateTime.Now - _last > Forget) _turns.Clear();
        _turns.Add(turn);
        if (_turns.Count > 40) _turns.RemoveAt(0);
        _last = DateTime.Now;
    }

    public void Clear() => _turns.Clear();
}
