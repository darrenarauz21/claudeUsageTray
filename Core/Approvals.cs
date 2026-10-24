using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeUsageTray;

/// <summary>Una pregunta de Claude Code (AskUserQuestion) con sus opciones.</summary>
sealed record AskedQuestion(string Question, IReadOnlyList<(string Label, string Description)> Options, bool MultiSelect);

/// <summary>
/// Un pedido de permiso que llegó por el hook PermissionRequest: qué sesión, qué herramienta y sobre
/// qué (el comando, el archivo…). Si la herramienta es AskUserQuestion, trae las preguntas.
/// </summary>
sealed record ApprovalRequest(string SessionId, string Cwd, string Tool, string Target, string InputJson, IReadOnlyList<AskedQuestion>? Questions)
{
    public bool IsQuestion => Questions is { Count: > 0 };
}

/// <summary>
/// Permisos y preguntas de Claude Code respondidos desde la isla, como en coucou. El hook
/// PermissionRequest («--hook --permiso») le pasa el pedido a la app por un pipe con nombre (solo del
/// usuario) y espera su respuesta hasta 110 s. Si la app está cerrada, no contesta a tiempo o la
/// persona no responde, el hook no escribe nada y Claude Code pregunta en la terminal como siempre:
/// una sesión nunca queda trabada por la app. La respuesta es la documentada
/// (hookSpecificOutput.decision.behavior allow o deny; para las preguntas, allow con updatedInput y
/// sus respuestas: una etiqueta o, si se pueden elegir varias, la lista).
/// </summary>
static class Approvals
{
    /// <summary>Lo que el hook espera a la app; Claude Code le da 120 s (el timeout del hook).</summary>
    public const int DecisionSeconds = 110;

    public static string PipeName => "UsoDeClaude-permisos-" + new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());

    // ── El hook ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Lo que corre Claude Code con «--hook --permiso»: pregunta a la app y escribe su respuesta, o nada.</summary>
    public static int RunHook()
    {
        try
        {
            var input = Console.In.ReadToEnd();
            var line = JsonNode.Parse(input)?.ToJsonString(); // en una sola línea
            if (line is null) return 0;
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(300); // la app cerrada: sigue la terminal
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            pipe.Write(bytes, 0, bytes.Length);
            pipe.Flush();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            var read = reader.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(DecisionSeconds)) || string.IsNullOrWhiteSpace(read.Result)) return 0;
            using var check = JsonDocument.Parse(read.Result); // solo JSON válido llega a Claude Code
            Console.Out.Write(read.Result);
            Console.Out.Flush();
        }
        catch (TimeoutException) { } // la app no está abierta (o no tiene la isla): decide la terminal
        catch (Exception ex) { ErrorLog.Write(ex); } // otro problema: queda anotado y decide la terminal
        return 0;
    }

    // ── Pedido y respuestas ─────────────────────────────────────────────────────────────────

    /// <summary>Lee el evento del hook. Null si no es un pedido de permiso.</summary>
    public static ApprovalRequest? Parse(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var r = doc.RootElement;
        if (Json.Str(r, "hook_event_name") is { } name && name != "PermissionRequest") return null;
        var tool = Json.Str(r, "tool_name") ?? "herramienta";
        var input = r.TryGetProperty("tool_input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var inputJson = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : "{}";
        return new ApprovalRequest(Json.Str(r, "session_id") ?? "", Json.Str(r, "cwd") ?? "", tool, TargetOf(tool, input), inputJson,
            tool == "AskUserQuestion" ? QuestionsOf(input) : null);
    }

    /// <summary>Sobre qué pide permiso: el comando, el archivo, la dirección o lo que traiga.</summary>
    static string TargetOf(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return tool;
        var target = Json.Str(input, "command") ?? Json.Str(input, "file_path") ?? Json.Str(input, "notebook_path")
            ?? Json.Str(input, "url") ?? Json.Str(input, "pattern") ?? Json.Str(input, "query") ?? Json.Str(input, "description");
        if (target is null)
        {
            target = input.GetRawText();
            if (target.Length > 300) target = target[..300] + "…";
        }
        return target;
    }

    static IReadOnlyList<AskedQuestion>? QuestionsOf(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("questions", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var questions = new List<AskedQuestion>();
        foreach (var q in list.EnumerateArray())
        {
            var text = Json.Str(q, "question");
            var options = q.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Select(x => (Json.Str(x, "label") ?? "", Json.Str(x, "description") ?? "")).Where(x => x.Item1.Length > 0).ToList()
                : [];
            // Una pregunta que no se puede mostrar entera (sin texto o con menos de dos opciones) queda para la terminal.
            if (string.IsNullOrWhiteSpace(text) || options.Count < 2) return null;
            questions.Add(new AskedQuestion(text, options, q.TryGetProperty("multiSelect", out var m) && m.ValueKind == JsonValueKind.True));
        }
        return questions.Count is > 0 and <= 4 ? questions : null;
    }

    public static string Allow() => Decision(new JsonObject { ["behavior"] = "allow" });

    public static string Deny() => Decision(new JsonObject { ["behavior"] = "deny", ["message"] = "Denegado desde Uso de Claude." });

    /// <summary>Las respuestas a las preguntas: por pregunta, una etiqueta o (si se pueden elegir varias) la lista.</summary>
    public static string Answer(ApprovalRequest request, IReadOnlyDictionary<string, IReadOnlyList<string>> picks)
    {
        var input = JsonNode.Parse(request.InputJson) as JsonObject ?? new JsonObject();
        var answers = new JsonObject();
        foreach (var q in request.Questions ?? [])
        {
            if (!picks.TryGetValue(q.Question, out var chosen) || chosen.Count == 0) continue;
            answers[q.Question] = q.MultiSelect ? new JsonArray([.. chosen.Select(c => (JsonNode)JsonValue.Create(c)!)]) : JsonValue.Create(chosen[0]);
        }
        input["answers"] = answers;
        return Decision(new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input });
    }

    static string Decision(JsonObject decision) => new JsonObject
    {
        ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
    }.ToJsonString();
}

/// <summary>
/// El lado de la app: escucha el pipe y, por cada pedido, pregunta a quien se suscribió (la isla) y le
/// devuelve al hook la respuesta, o nada si no hubo. Los pedidos llegan en el hilo de la interfaz.
/// </summary>
sealed class ApprovalServer : IDisposable
{
    readonly CancellationTokenSource _stop = new();
    readonly SynchronizationContext _ui;
    readonly Func<ApprovalRequest, Task<string?>> _ask;

    public ApprovalServer(Func<ApprovalRequest, Task<string?>> ask)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _ask = ask;
        _ = Task.Run(ListenAsync);
    }

    async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(Approvals.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleAsync(connected));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { await Task.Delay(1000); } // otro proceso con el mismo nombre, por ejemplo
            finally { pipe?.Dispose(); }
        }
    }

    async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_stop.Token);
                if (line is null || Approvals.Parse(line) is not { } request) return;
                var answered = new TaskCompletionSource<string?>();
                _ui.Post(async _ =>
                {
                    try { answered.TrySetResult(await _ask(request)); }
                    catch (Exception) { answered.TrySetResult(null); }
                }, null);
                var reply = await answered.Task.WaitAsync(TimeSpan.FromSeconds(Approvals.DecisionSeconds - 3), _stop.Token);
                if (reply is null) return;
                var bytes = Encoding.UTF8.GetBytes(reply + "\n");
                await pipe.WriteAsync(bytes, _stop.Token);
                await pipe.FlushAsync(_stop.Token);
            }
            catch (Exception) { } // sin respuesta: decide la terminal
        }
    }

    public void Dispose() => _stop.Cancel();
}
