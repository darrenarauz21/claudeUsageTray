using System.Runtime.InteropServices;

namespace ClaudeUsageTray;

/// <summary>
/// La caja que vuela de las manos de la mascota a la boca de la isla, en su propia ventanita sin fondo.
/// Da vueltas en el aire (cerrada y de costado, alternando) y pasa por detrás de la isla al entrar.
/// </summary>
sealed class BoxFlight : Form
{
    static readonly Color Key = Color.FromArgb(255, 0, 255);
    readonly int _unit;
    bool _tumbling;

    public BoxFlight(int unit)
    {
        _unit = unit;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        BackColor = Key;
        TransparencyKey = Key;
        ClientSize = new Size(ChibiSprite.BoxColumns * unit, ChibiSprite.BoxRows * unit);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20; // sin activar, de herramientas y transparente al mouse
            return cp;
        }
    }

    /// <summary>
    /// Vuela del punto from (las manos) al punto to (la boca de la isla) en ms milisegundos; las
    /// coordenadas son del centro de la caja. Termina escondida.
    /// </summary>
    public async Task FlyAsync(Point from, Point to, int ms, Form? behind, float scale)
    {
        Place(from);
        Show();
        if (behind is { IsHandleCreated: true })
            SetWindowPos(Handle, behind.Handle, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // debajo de la isla, sin activar
        var start = Environment.TickCount64;
        while (true)
        {
            var k = Math.Clamp((Environment.TickCount64 - start) / (double)Math.Max(1, ms), 0, 1);
            var at = ThrowPlan.Flight(from, to, k, scale);
            Place(Point.Round(at));
            var tumbling = (Environment.TickCount64 - start) / 90 % 2 == 1;
            if (tumbling != _tumbling)
            {
                _tumbling = tumbling;
                Invalidate();
            }
            if (k >= 1) break;
            await Task.Delay(15);
        }
        Hide();
    }

    void Place(Point center) => Location = new Point(center.X - Width / 2, center.Y - Height / 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Key);
        ToastArt.Pixels(e.Graphics, 0, 0, _unit,
            fill => ChibiSprite.PaintBox(fill, _tumbling ? ChibiSprite.BoxFrame.Tumbling : ChibiSprite.BoxFrame.Closed));
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, int flags);
}
