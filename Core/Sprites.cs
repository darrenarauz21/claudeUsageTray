using System.Drawing;
namespace ClaudeUsageTray;

/// <summary>Pinta un rectángulo de casillas: cada app lo traduce a pixeles con sus herramientas.</summary>
delegate void PixelFill(Color color, int x, int y, int width, int height);

enum MascotFrame { Closed, Open, Left, Right, Happy }

/// <summary>La mascota de Claude en pixeles: cuerpo, bracitos y cuatro patas, en una cuadrícula de 14 × 10.</summary>
static class MascotSprite
{
    public const int Columns = 14, Rows = 10;
    static readonly Color Body = Color.FromArgb(0xD9, 0x77, 0x57);
    static readonly Color Eye = Color.FromArgb(0x1F, 0x1E, 0x1D);

    public static void Paint(PixelFill fill, MascotFrame frame)
    {
        fill(Body, 2, 0, 10, 7);                      // cuerpo
        fill(Body, 0, 3, 2, 2);                       // bracitos
        fill(Body, 12, 3, 2, 2);
        foreach (var column in new[] { 3, 5, 8, 10 }) // patas
            fill(Body, column, 7, 1, 3);

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
/// Abre los ojos, mira a los lados mientras dure la consulta y, al terminar, hace &gt; &lt; antes de
/// dar paso al estado nuevo. Mira al menos una vuelta completa aunque la consulta sea rápida. Los
/// eventos llegan en el hilo de la interfaz que llamó a Start.
/// </summary>
sealed class MascotAnimator : IDisposable
{
    const int OpenAt = 180, LookAt = 380, LookCycle = 700, HappyFor = 500, TickMs = 40;

    public event Action? FrameChanged;
    public event Action? Completed;

    readonly System.Threading.Timer _timer;
    SynchronizationContext? _ui;
    long _start;
    long? _happySince;
    bool _finish, _running, _tickQueued;

    public MascotFrame? Frame { get; private set; }
    public bool Running => _running;

    public MascotAnimator() => _timer = new System.Threading.Timer(_ => QueueTick());

    public void Start()
    {
        _ui ??= SynchronizationContext.Current;
        _finish = false;
        _happySince = null;
        if (_running) return;
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
        MascotFrame frame;

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
            frame = MascotFrame.Happy;
        }
        else if (t < OpenAt) frame = MascotFrame.Closed;
        else if (t < LookAt) frame = MascotFrame.Open;
        else
        {
            var inCycle = (t - LookAt) % LookCycle;
            frame = inCycle < 250 ? MascotFrame.Left : inCycle < 500 ? MascotFrame.Right : MascotFrame.Open;
            // Termina de mirar al centro y, si ya llegaron los datos, se pone contento.
            if (_finish && frame == MascotFrame.Open && t - LookAt >= LookCycle - 200)
            {
                _happySince = now;
                frame = MascotFrame.Happy;
            }
        }

        if (frame == Frame) return;
        Frame = frame;
        FrameChanged?.Invoke();
    }

    public void Dispose() => _timer.Dispose();
}
