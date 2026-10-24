using System.Drawing;

namespace ClaudeUsageTray;

/// <summary>
/// Cómo lleva la mascota un archivo hasta la isla, según dónde esté (en pixeles de pantalla):
/// <list type="bullet">
/// <item>Abajo o a media altura: camina a su misma altura hasta quedar debajo de la isla, sin pasar de
/// 2,5 s (en pantallas muy anchas lanza desde donde llegue), y la lanza hacia arriba.</item>
/// <item>Ya debajo: no camina.</item>
/// <item>Arriba (los pies en el cuarto superior): se acerca hasta el costado de la isla y se la pasa de lado.</item>
/// <item>Encima de la isla: primero se aparta hacia el lado con más espacio.</item>
/// </list>
/// La caja vuela entre 0,4 y 0,8 s según la distancia y llega a la boca de la isla frenando.
/// </summary>
sealed record ThrowPlan(int WalkToX, int WalkMs, bool Left, int FlightMs)
{
    const int MaxWalkMs = 2500;

    /// <param name="mascot">La ventana de la mascota.</param>
    /// <param name="work">El área útil de su pantalla.</param>
    /// <param name="island">La isla compacta (o con la boca abierta) en esa pantalla.</param>
    /// <param name="unit">Pixeles por casilla de la mascota: camina más rápido cuanto más grande.</param>
    /// <param name="scale">Escala de la pantalla (ppp / 96), para medir la distancia del vuelo.</param>
    public static ThrowPlan Make(Rectangle mascot, Rectangle work, Rectangle island, int unit, float scale)
    {
        var speed = 150.0 * unit; // px/s: con la casilla de 3 px (normal a 100 %), 450 px/s
        var center = mascot.X + mascot.Width / 2;
        var islandCenter = island.X + island.Width / 2;
        var topBand = mascot.Bottom < work.Top + work.Height / 4;
        var gap = (int)(30 * scale);
        int target;
        if (topBand)
        {
            // Arriba: al costado de la isla (y si estaba encima, se aparta hacia el lado con más espacio).
            var right = center >= islandCenter
                ? island.Right + gap + mascot.Width <= work.Right
                : island.Left - gap - mascot.Width < work.Left;
            target = right ? island.Right + gap : island.Left - gap - mascot.Width;
            var overlaps = mascot.Right > island.Left - gap && mascot.Left < island.Right + gap;
            if (!overlaps && Math.Abs(mascot.X - target) < mascot.Width) target = mascot.X; // ya está al costado
        }
        else
        {
            target = islandCenter - mascot.Width / 2;
            var limit = speed * MaxWalkMs / 1000;
            var distance = target - mascot.X;
            if (Math.Abs(distance) < 30 * scale) target = mascot.X;
            else if (Math.Abs(distance) > limit) target = mascot.X + (int)(Math.Sign(distance) * limit);
        }
        target = Math.Clamp(target, work.Left, Math.Max(work.Left, work.Right - mascot.Width));
        var walk = (int)(Math.Abs(target - mascot.X) / speed * 1000);

        // El vuelo: desde las manos (a la altura del pecho) hasta la boca de la isla.
        var hands = new Point(target + mascot.Width / 2, mascot.Y + mascot.Height * 3 / 5);
        var mouth = new Point(islandCenter, island.Bottom);
        var distanceToMouth = Math.Sqrt(Math.Pow(mouth.X - hands.X, 2) + Math.Pow(mouth.Y - hands.Y, 2)) / scale;
        var flight = (int)Math.Clamp(250 + distanceToMouth * 0.6, 400, 800);
        return new ThrowPlan(target, walk, target < mascot.X, flight);
    }

    /// <summary>Dónde va la caja en el momento k (de 0 a 1) del vuelo: sube frenando y, si va casi recta, se curva un poco.</summary>
    public static PointF Flight(PointF from, PointF to, double k, float scale)
    {
        var x = from.X + (to.X - from.X) * k;
        var ease = 1 - Math.Pow(1 - k, 3);
        var y = from.Y + (to.Y - from.Y) * ease;
        if (Math.Abs(to.X - from.X) < 80 * scale) x += 40 * scale * Math.Sin(Math.PI * k);
        return new PointF((float)x, (float)y);
    }
}
