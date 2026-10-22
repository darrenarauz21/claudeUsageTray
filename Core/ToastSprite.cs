using System.Drawing;
namespace ClaudeUsageTray;

/// <summary>
/// La mascota de los avisos, en pixeles, siguiendo el paquete de ilustraciones: cuerpo de 14 × 7,
/// bracitos, cuatro patas, ojos y boca según la emoción, y un accesorio por caso. Se dibuja en un
/// lienzo de 26 × 18 casillas; todo se anima a partir del tiempo transcurrido.
/// </summary>
static class ToastSprite
{
    public const int Columns = 26, Rows = 18;
    const int Ox = 2, Oy = 6; // dónde va la mascota dentro del lienzo

    static readonly Color Ink = Color.FromArgb(0x2A, 0x1F, 0x1A);
    static readonly Color White = Color.FromArgb(0xFF, 0xFF, 0xFF);
    static readonly Color Green = Color.FromArgb(0x22, 0xA5, 0x59);
    static readonly Color Mint = Color.FromArgb(0xCF, 0xF5, 0xDA);
    static readonly Color Teal = Color.FromArgb(0x3D, 0xD6, 0xA3);
    static readonly Color TealDark = Color.FromArgb(0x1A, 0x6E, 0x58);
    static readonly Color Purple = Color.FromArgb(0x8B, 0x5C, 0xF6);
    static readonly Color Yellow = Color.FromArgb(0xF5, 0xC5, 0x42);
    static readonly Color Orange = Color.FromArgb(0xF0, 0x8A, 0x24);
    static readonly Color Red = Color.FromArgb(0xE5, 0x48, 0x4D);
    static readonly Color Pink = Color.FromArgb(0xE8, 0x50, 0x8E);
    static readonly Color Steel = Color.FromArgb(0xB8, 0xC7, 0xC0);
    static readonly Color Blue = Color.FromArgb(0x4A, 0xA8, 0xF0);
    static readonly Color Amber = Color.FromArgb(0xE0, 0xA8, 0x4B);

    enum Eyes { Open, Closed, Happy, Dizzy, Squint, Up, Left, Right }
    enum Mouth { Neutral, Smile, Frown, Open }

    sealed class Painter(PixelFill fill)
    {
        public void Px(Color color, int x, int y, int w = 1, int h = 1) => fill(color, x, y, w, h);

        /// <summary>Figura a partir de filas de texto: cada carácter distinto de espacio es una casilla.</summary>
        public void Map(Color color, int x, int y, params string[] rows)
        {
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] != ' ') Px(color, x + c, y + r);
        }
    }

    /// <summary>Pinta el cuadro del momento t (ms desde que apareció el aviso), en casillas del lienzo, con el cuerpo del color dado (el acento de la paleta).</summary>
    public static void Paint(PixelFill fill, NoticeKind kind, long t, Color body)
    {
        var p = new Painter(fill);
        // Al llegar: un salto; si falló, una sacudida.
        int dx = 0, dy = 0;
        if (kind == NoticeKind.Failed && t < 420) dx = t / 60 % 2 == 0 ? -1 : 1;
        else if (t < 450) dy = -(int)Math.Round(2 * Math.Sin(Math.PI * t / 450));

        var celebrating = kind is NoticeKind.Done or NoticeKind.UsageReset && t < 2800 && t / 350 % 2 == 0;
        DrawBody(p, Ox + dx, Oy + dy, celebrating, body);
        DrawFace(p, Ox + dx, Oy + dy, EyesFor(kind, t), MouthFor(kind));
        DrawProps(p, kind, t, dy);
    }

    /// <summary>
    /// La mascota clásica en el escritorio, con los movimientos del chibi (ChibiDirector): los que
    /// tienen aviso se toman de ahí (festejar, pensar, marearse, dormir, el límite renovado) y los demás
    /// se arman con las mismas piezas. t es el tiempo desde que empezó el movimiento.
    /// </summary>
    public static void PaintMove(PixelFill fill, ChibiMove move, long t, Color body)
    {
        var kind = move switch
        {
            ChibiMove.Cheer => NoticeKind.Done,
            ChibiMove.Maracas => NoticeKind.UsageReset,
            ChibiMove.Think => NoticeKind.Question,
            ChibiMove.Dizzy => NoticeKind.Failed,
            ChibiMove.Sleep => NoticeKind.Closed,
            _ => (NoticeKind?)null,
        };
        if (kind is { } k) { Paint(fill, k, t, body); return; }

        var p = new Painter(fill);
        var blink = t % 2800 > 2660;
        switch (move)
        {
            case ChibiMove.Dance:
            {
                // Saltitos al compás, brazos arriba y abajo y un destello que cambia de lado.
                var beat = (int)(t / 200 % 4);
                var dy = beat is 1 or 3 ? -1 : 0;
                DrawBody(p, Ox, Oy + dy, armsUp: beat < 2, body);
                DrawFace(p, Ox, Oy + dy, Eyes.Happy, Mouth.Smile);
                Sparkle(p, beat < 2 ? Yellow : Teal, beat < 2 ? 3 : 22, 3);
                break;
            }
            case ChibiMove.Wave:
                DrawBody(p, Ox, Oy, armsUp: t / 220 % 2 == 0, body);
                DrawFace(p, Ox, Oy, blink ? Eyes.Closed : Eyes.Open, Mouth.Smile);
                break;
            case ChibiMove.Pout: // de reojo, con la boca para abajo
                DrawBody(p, Ox, Oy, false, body);
                DrawFace(p, Ox, Oy, t / 600 % 2 == 0 ? Eyes.Left : Eyes.Squint, Mouth.Frown);
                break;
            case ChibiMove.Sulk: // «¡hmph!», con los ojos cerrados
                DrawBody(p, Ox, Oy, false, body);
                DrawFace(p, Ox, Oy, Eyes.Closed, Mouth.Frown);
                break;
            case ChibiMove.Fury:
            {
                // Roja, temblando y echando llamas.
                var dx = t / 90 % 2 == 0 ? -1 : 1;
                DrawBody(p, Ox + dx, Oy, false, Red);
                DrawFace(p, Ox + dx, Oy, Eyes.Squint, Mouth.Frown);
                Flames(p, t, small: false);
                break;
            }
            case ChibiMove.Calm: // como el chibi: suspira, mira a un lado y al otro y parpadea
                DrawBody(p, Ox, Oy, false, body);
                DrawFace(p, Ox, Oy, t switch
                {
                    < 900 => Eyes.Closed,
                    < 1150 => Eyes.Open,
                    < 1850 => Eyes.Left,
                    < 2550 => Eyes.Right,
                    < 2900 => Eyes.Left,
                    < 3250 => Eyes.Right,
                    >= 3500 and < 3610 => Eyes.Closed,
                    _ => Eyes.Open,
                }, Mouth.Neutral);
                break;
            default: // quieta: parpadea y mira a los lados
                DrawBody(p, Ox, Oy, false, body);
                DrawFace(p, Ox, Oy, blink ? Eyes.Closed : (t / 1500 % 4) switch { 1 => Eyes.Left, 3 => Eyes.Right, _ => Eyes.Open }, Mouth.Neutral);
                break;
        }
    }

    static Eyes EyesFor(NoticeKind kind, long t)
    {
        var blink = t % 2800 > 2660;
        return kind switch
        {
            NoticeKind.Done or NoticeKind.UsageReset => Eyes.Happy,
            NoticeKind.Question => blink ? Eyes.Closed : t / 900 % 2 == 0 ? Eyes.Up : Eyes.Open,
            NoticeKind.Permission => blink ? Eyes.Closed : Eyes.Right,
            NoticeKind.Failed => Eyes.Dizzy,
            NoticeKind.Retrying => t / 400 % 2 == 0 ? Eyes.Left : Eyes.Right,
            NoticeKind.Closed => Eyes.Closed,
            NoticeKind.UsageCritical => Eyes.Squint,
            _ => blink ? Eyes.Closed : Eyes.Open,
        };
    }

    static Mouth MouthFor(NoticeKind kind) => kind switch
    {
        NoticeKind.Done or NoticeKind.UsageReset or NoticeKind.UsageHigh => Mouth.Smile,
        NoticeKind.Failed or NoticeKind.UsageCritical or NoticeKind.Expired => Mouth.Frown,
        NoticeKind.Question => Mouth.Open,
        _ => Mouth.Neutral,
    };

    static void DrawBody(Painter p, int ox, int oy, bool armsUp, Color body)
    {
        p.Px(body, ox + 3, oy + 2, 14, 7);
        if (armsUp)
        {
            p.Px(body, ox + 1, oy, 2, 2);
            p.Px(body, ox + 17, oy, 2, 2);
        }
        else
        {
            p.Px(body, ox + 1, oy + 5, 2, 2);
            p.Px(body, ox + 17, oy + 5, 2, 2);
        }
        foreach (var leg in new[] { 4, 6, 13, 15 })
            p.Px(body, ox + leg, oy + 9, 1, 2);
    }

    static void DrawFace(Painter p, int ox, int oy, Eyes eyes, Mouth mouth)
    {
        void Eye(int x, int y, params string[] rows) => p.Map(Ink, ox + x, oy + y, rows);
        switch (eyes)
        {
            case Eyes.Open: p.Px(Ink, ox + 7, oy + 4, 1, 2); p.Px(Ink, ox + 12, oy + 4, 1, 2); break;
            case Eyes.Up: p.Px(Ink, ox + 8, oy + 3, 1, 2); p.Px(Ink, ox + 13, oy + 3, 1, 2); break;
            case Eyes.Left: p.Px(Ink, ox + 6, oy + 4, 1, 2); p.Px(Ink, ox + 11, oy + 4, 1, 2); break;
            case Eyes.Right: p.Px(Ink, ox + 8, oy + 4, 1, 2); p.Px(Ink, ox + 13, oy + 4, 1, 2); break;
            case Eyes.Closed: p.Px(Ink, ox + 6, oy + 5, 2, 1); p.Px(Ink, ox + 12, oy + 5, 2, 1); break;
            case Eyes.Happy: Eye(6, 4, " # ", "# #"); Eye(11, 4, " # ", "# #"); break;
            case Eyes.Dizzy: Eye(6, 3, "# #", " # ", "# #"); Eye(11, 3, "# #", " # ", "# #"); break;
            case Eyes.Squint: Eye(6, 3, "# ", " #", "# "); Eye(12, 3, " #", "# ", " #"); break;
        }

        switch (mouth)
        {
            case Mouth.Open:
                p.Px(White, ox + 9, oy + 6, 2, 2);
                break;
            case Mouth.Smile:
                p.Px(White, ox + 9, oy + 7, 2, 1);
                p.Px(Ink, ox + 8, oy + 6); p.Px(Ink, ox + 11, oy + 6);
                break;
            case Mouth.Frown:
                p.Px(White, ox + 9, oy + 7, 2, 1);
                p.Px(Ink, ox + 8, oy + 8); p.Px(Ink, ox + 11, oy + 8);
                break;
            default:
                p.Px(White, ox + 9, oy + 7, 2, 1);
                break;
        }
    }

    static void Sparkle(Painter p, Color color, int x, int y) => p.Map(color, x - 1, y - 1, " # ", "###", " # ");

    static void DrawProps(Painter p, NoticeKind kind, long t, int dy)
    {
        switch (kind)
        {
            case NoticeKind.Done:
            {
                // Burbuja verde con check sobre la cabeza y destellos que se alternan.
                var y = 1 + dy;
                p.Map(Green, 9, y, "  ##  ", " #### ", "######", "######", " #### ", "  ##  ");
                p.Map(Mint, 10, y + 2, "   #", "# # ", " #  ");
                if (t / 300 % 2 == 0) { Sparkle(p, Teal, 4, 4); Sparkle(p, Purple, 21, 3); Sparkle(p, Yellow, 23, 9); }
                else { Sparkle(p, Yellow, 5, 1); Sparkle(p, Teal, 19, 1); Sparkle(p, Purple, 22, 7); }
                break;
            }
            case NoticeKind.Question:
            {
                // Burbuja de diálogo con puntos que van apareciendo.
                p.Map(Teal, 18, 1, " ##### ", "#######", "#######", "#######", " ##### ");
                p.Px(Teal, 18, 6); p.Px(Teal, 17, 7);
                var dots = 1 + (int)(t / 350 % 3);
                for (var i = 0; i < dots; i++) p.Px(TealDark, 19 + i * 2, 3);
                break;
            }
            case NoticeKind.Permission:
            {
                // Candado que tiembla un poco.
                var y = t / 500 % 2 == 0 ? 0 : -1;
                p.Map(Steel, 21, 6 + y, " ### ", " # # ", " # # ");
                p.Px(Pink, 21, 9 + y, 5, 4);
                p.Px(White, 23, 10 + y, 1, 2);
                break;
            }
            case NoticeKind.Failed:
            {
                // Círculo de error y estrellitas mareadas girando sobre la cabeza.
                p.Map(Red, 19, 1, "  ##  ", " #### ", "######", "######", " #### ", "  ##  ");
                p.Map(White, 20, 2, "#  #", " ## ", " ## ", "#  #");
                (int X, int Y)[] orbit = [(6, 6), (11, 4), (16, 6), (11, 6)];
                var step = (int)(t / 160 % orbit.Length);
                Sparkle(p, Pink, orbit[step].X, orbit[step].Y);
                Sparkle(p, Orange, orbit[(step + 2) % orbit.Length].X, orbit[(step + 2) % orbit.Length].Y);
                break;
            }
            case NoticeKind.Retrying or NoticeKind.UsageReset:
            {
                // Flechas de sincronizar, como refresh-sync-pixel.
                var a = t / 260 % 2 == 0;
                p.Px(Green, 8, 3, 8, 1);
                p.Map(Green, a ? 15 : 7, 2, a ? "# " : " #", a ? "##" : "##", a ? "# " : " #");
                break;
            }
            case NoticeKind.Closed:
            {
                // Zetas que suben.
                var k = (int)(t / 450 % 2);
                p.Map(Purple, 14, 4 - k, "###", " # ", "###");
                p.Map(Teal, 18, 2 - k, "###", " # ", "###");
                p.Map(Yellow, 22, 0 + k, "##", "##");
                break;
            }
            case NoticeKind.UsageHigh:
            {
                // Gota de sudor que cae y llamitas que parpadean.
                p.Px(Blue, 20, 6 + (int)(t / 300 % 3), 1, 2);
                Flames(p, t, small: true);
                break;
            }
            case NoticeKind.UsageCritical:
                Flames(p, t, small: false);
                break;
            case NoticeKind.Expired:
            {
                // Burbuja ámbar con «!».
                if (t / 500 % 4 != 3)
                {
                    p.Map(Amber, 19, 0, " ##### ", "#######", "#######", "#######", "#######", " ##### ");
                    p.Px(White, 22, 1, 1, 2);
                    p.Px(White, 22, 4);
                }
                p.Px(Amber, 19, 6); p.Px(Amber, 18, 7);
                break;
            }
        }
    }

    static void Flames(Painter p, long t, bool small)
    {
        var a = t / 120 % 2 == 0;
        var outer = small ? Yellow : Orange;
        var inner = small ? Orange : Red;
        p.Map(outer, 0, 9, a ? " # " : "#  ", "## ", "###", "###", "## ");
        p.Map(outer, 23, 9, a ? " # " : "  #", " ##", "###", "###", " ##");
        p.Px(inner, 1, 12, 1, 2);
        p.Px(inner, 24, 12, 1, 2);
        if (small) return;
        p.Map(outer, 6, 5, a ? "#   #   #" : " #   # # ", "## ### ##");
        p.Map(Yellow, 9, 2, a ? " # " : "#  ", "###");
    }
}
