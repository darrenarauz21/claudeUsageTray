using System.Drawing;
namespace ClaudeUsageTray;

/// <summary>Pinta un rectángulo de casillas: cada app lo traduce a pixeles con sus herramientas.</summary>
delegate void PixelFill(Color color, int x, int y, int width, int height);

enum MascotFrame { Closed, Open, Left, Right, Happy }

/// <summary>Un cuadro de la animación al actualizar: la fase (la de la mascota) y, en los estilos que giran o saltan, el paso.</summary>
readonly record struct RefreshFrame(MascotFrame Phase, int Step = 0);

/// <summary>La mascota de Claude en pixeles: cuerpo, bracitos y cuatro patas, en una cuadrícula de 14 × 10.</summary>
static class MascotSprite
{
    public const int Columns = 14, Rows = 10;
    static readonly Color Eye = Color.FromArgb(0x1F, 0x1E, 0x1D);

    public static void Paint(PixelFill fill, MascotFrame frame, Color body)
    {
        fill(body, 2, 0, 10, 7);                      // cuerpo
        fill(body, 0, 3, 2, 2);                       // bracitos
        fill(body, 12, 3, 2, 2);
        foreach (var column in new[] { 3, 5, 8, 10 }) // patas
            fill(body, column, 7, 1, 3);

        switch (frame)
        {
            case MascotFrame.Closed:
                fill(Eye, 4, 3, 2, 1);
                fill(Eye, 8, 3, 2, 1);
                break;
            case MascotFrame.Open:
                fill(Eye, 4, 2, 1, 2);
                fill(Eye, 9, 2, 1, 2);
                break;
            case MascotFrame.Left:
                fill(Eye, 3, 2, 1, 2);
                fill(Eye, 8, 2, 1, 2);
                break;
            case MascotFrame.Right:
                fill(Eye, 5, 2, 1, 2);
                fill(Eye, 10, 2, 1, 2);
                break;
            case MascotFrame.Happy: // > <
                fill(Eye, 3, 2, 1, 1); fill(Eye, 4, 3, 1, 1); fill(Eye, 3, 4, 1, 1);
                fill(Eye, 10, 2, 1, 1); fill(Eye, 9, 3, 1, 1); fill(Eye, 10, 4, 1, 1);
                break;
        }
    }
}

/// <summary>
/// La animación al actualizar, con el estilo elegido en Ajustes (como el estilo de sonido). Todos se
/// dibujan en la cuadrícula de la mascota (14 × 10) con el acento de la paleta. «Ninguna» no anima:
/// el ícono sigue con el porcentaje y el pie dice «Actualizando…». Con la mascota chibi, el estilo
/// «Mascota» se dibuja con la cabeza del chibi, en su propia cuadrícula de 16 × 16.
/// </summary>
static class RefreshSprite
{
    public const string None = "ninguna";
    /// <summary>Estilos internos: «Mascota» con la mascota chibi, con su cabeza grande o chica. No aparecen en Ajustes.</summary>
    const string Chibi = "chibi", SmallChibi = "chibi-chico";

    public static readonly (string Id, string Label)[] Styles =
        [("mascota", "Mascota"), ("asterisco", "Asterisco"), ("puntos", "Puntos"), (None, "Ninguna")];

    public static string Label(string id) => Styles.FirstOrDefault(s => s.Id == id).Label ?? Styles[0].Label;

    /// <summary>El estilo que se dibuja: el elegido en Ajustes, con la mascota elegida si es «Mascota».</summary>
    public static string Resolve(string animation, string mascot) =>
        animation == "mascota" && mascot == Mascots.Chibi ? Chibi : animation;

    /// <summary>Tamaño de la cuadrícula de cada estilo, en casillas.</summary>
    public static int Columns(string style) => style switch
    {
        Chibi => ChibiSprite.HeadSize,
        SmallChibi => ChibiSprite.SmallHeadSize,
        _ => MascotSprite.Columns,
    };
    public static int Rows(string style) => style switch
    {
        Chibi => ChibiSprite.HeadSize,
        SmallChibi => ChibiSprite.SmallHeadSize,
        _ => MascotSprite.Rows,
    };

    /// <summary>Para un ícono de size pixeles: si la cabeza grande no entra, la chica, que se ve nítida.</summary>
    public static string ForIcon(string style, int size) => style == Chibi && size < ChibiSprite.HeadSize ? SmallChibi : style;

    /// <summary>Pasos de cada estilo en una vuelta de la espera (la mascota mira por fase, no por paso).</summary>
    public static int Steps(string style) => style switch { "asterisco" => 6, "puntos" => 4, _ => 1 };

    // El asterisco de Claude Code en cuatro tamaños, de un punto al destello; va y vuelve.
    static readonly string[][] Asterisk =
    [
        ["", "", "", "", "    ##", "    ##"],
        ["", "", "    ##", "    ##", "  ######", "  ######", "    ##", "    ##"],
        ["", " #  ##  #", "  # ## #", "   ####", " ########", " ########", "   ####", "  # ## #", " #  ##  #"],
        ["#   ##   #", " #  ##  #", "  # ## #", "   ####", "##########", "##########", "   ####", "  # ## #", " #  ##  #", "#   ##   #"],
    ];
    static readonly int[] AsteriskCycle = [1, 2, 3, 2, 1, 0];

    public static void Paint(PixelFill fill, string style, RefreshFrame frame, Color color)
    {
        switch (style)
        {
            case "asterisco":
                var size = frame.Phase switch
                {
                    MascotFrame.Closed => 0,
                    MascotFrame.Happy => 3,
                    _ => AsteriskCycle[frame.Step % AsteriskCycle.Length],
                };
                Map(fill, color, 2, 0, Asterisk[size]);
                break;
            case "puntos":
                // Tres puntos: se encienden con uno, hacen la ola y terminan en sonrisa.
                int[] columns = [2, 6, 10];
                for (var i = 0; i < columns.Length; i++)
                {
                    var y = frame.Phase switch
                    {
                        MascotFrame.Closed when i != 1 => -1,
                        MascotFrame.Happy => i == 1 ? 5 : 3,
                        _ => frame.Step == i && frame.Phase != MascotFrame.Closed ? 2 : 4,
                    };
                    if (y >= 0) fill(color, columns[i], y, 2, 2);
                }
                break;
            case Chibi or SmallChibi:
                ChibiSprite.PaintHead(fill, frame.Phase, small: style == SmallChibi); // con sus colores, no con el acento
                break;
            default:
                MascotSprite.Paint(fill, frame.Phase, color);
                break;
        }
    }

    /// <summary>Figura a partir de filas de texto: cada carácter distinto de espacio es una casilla.</summary>
    static void Map(PixelFill fill, Color color, int x, int y, string[] rows)
    {
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                if (rows[r][c] != ' ') fill(color, x + c, y + r, 1, 1);
    }
}

/// <summary>
/// La animación al actualizar: abre (la mascota abre los ojos), espera mientras dure la consulta
/// (mira a los lados, gira o hace la ola) y, al terminar, festeja (&gt; &lt;) antes de dar paso al
/// estado nuevo. Da al menos una vuelta completa aunque la consulta sea rápida. Con el estilo
/// «ninguna» no corre y Finish termina enseguida. Los eventos llegan en el hilo de la interfaz que
/// llamó a Start.
/// </summary>
sealed class RefreshAnimator : IDisposable
{
    const int OpenAt = 180, LookAt = 380, LookCycle = 700, HappyFor = 500, TickMs = 40;

    public event Action? FrameChanged;
    public event Action? Completed;

    readonly System.Threading.Timer _timer;
    SynchronizationContext? _ui;
    long _start;
    long? _happySince;
    bool _finish, _running, _tickQueued;

    public RefreshFrame? Frame { get; private set; }
    public bool Running => _running;
    /// <summary>Estilo de RefreshSprite.Styles; se puede cambiar en cualquier momento.</summary>
    public string Style { get; set; } = RefreshSprite.Styles[0].Id;

    public RefreshAnimator() => _timer = new System.Threading.Timer(_ => QueueTick());

    public void Start()
    {
        _ui ??= SynchronizationContext.Current;
        _finish = false;
        _happySince = null;
        if (_running || Style == RefreshSprite.None) return;
        _running = true;
        _start = Environment.TickCount64;
        _timer.Change(TickMs, TickMs);
        Tick();
    }

    public void Finish()
    {
        if (!_running) { Completed?.Invoke(); return; }
        _finish = true;
    }

    void QueueTick()
    {
        if (_tickQueued) return;
        _tickQueued = true;
        if (_ui is null) Tick();
        else _ui.Post(_ => Tick(), null);
    }

    void Tick()
    {
        _tickQueued = false;
        if (!_running) return;
        var now = Environment.TickCount64;
        var t = now - _start;
        RefreshFrame frame;

        if (_happySince is { } since)
        {
            if (now - since >= HappyFor)
            {
                _running = false;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                Frame = null;
                FrameChanged?.Invoke();
                Completed?.Invoke();
                return;
            }
            frame = new(MascotFrame.Happy);
        }
        else if (t < OpenAt) frame = new(MascotFrame.Closed);
        else if (t < LookAt) frame = new(MascotFrame.Open);
        else
        {
            var inCycle = (t - LookAt) % LookCycle;
            var phase = inCycle < 250 ? MascotFrame.Left : inCycle < 500 ? MascotFrame.Right : MascotFrame.Open;
            frame = new(phase, (int)(inCycle * RefreshSprite.Steps(Style) / LookCycle));
            // Termina la vuelta en reposo (la mascota, mirando al centro) y, si ya llegaron los datos, festeja.
            if (_finish && phase == MascotFrame.Open && t - LookAt >= LookCycle - 200)
            {
                _happySince = now;
                frame = new(MascotFrame.Happy);
            }
        }

        if (frame == Frame) return;
        Frame = frame;
        FrameChanged?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
