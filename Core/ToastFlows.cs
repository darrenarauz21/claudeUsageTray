using System.Drawing;
namespace ClaudeUsageTray;

/// <summary>
/// Los gestos del flujo en la mascota clásica, con un objeto al costado o encima (no tiene cara
/// aparte: lo que lleva no puede taparle el cuerpo). Con música, rebota al compás y suelta notas.
/// </summary>
static partial class ToastSprite
{
    static readonly Color Cover = Color.FromArgb(0x4F, 0x7F, 0xD0);
    static readonly Color CoverShade = Color.FromArgb(0x34, 0x58, 0x9A);
    static readonly Color Rule = Color.FromArgb(0xCF, 0xC8, 0xBE);
    static readonly Color Screen = Color.FromArgb(0x1E, 0x2A, 0x26);

    enum ArmAt { Down, Mid, Up }

    /// <summary>Como DrawBody, pero con cada brazo a su altura.</summary>
    static void DrawBody(Painter p, int ox, int oy, Color body, ArmAt left, ArmAt right)
    {
        static int Y(ArmAt a) => a switch { ArmAt.Up => 0, ArmAt.Mid => 3, _ => 5 };
        p.Px(body, ox + 3, oy + 2, 14, 7);
        p.Px(body, ox + 1, oy + Y(left), 2, 2);
        p.Px(body, ox + 17, oy + Y(right), 2, 2);
        foreach (var leg in new[] { 4, 6, 13, 15 })
            p.Px(body, ox + leg, oy + 9, 1, 2);
    }

    static bool PaintFlow(Painter p, ChibiMove move, long t, Color body, bool music)
    {
        var oy = Oy + (music && t / ChibiSprite.Beat % 2 == 1 ? -1 : 0);
        var blink = t % 2800 > 2660;
        switch (move)
        {
            case ChibiMove.Read:
            {
                // El libro abierto a la derecha, en la mano: lo lee y cada tanto pasa la página.
                var at = t % 2400;
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Mid);
                DrawFace(p, Ox, oy, at > 2280 ? Eyes.Closed : at / 400 % 2 == 0 ? Eyes.Right : Eyes.Open, Mouth.Neutral);
                p.Px(Cover, 19, oy + 1, 7, 6);
                p.Px(CoverShade, 19, oy + 6, 7, 1);
                foreach (var x in new[] { 20, 23 })
                {
                    p.Px(White, x, oy + 2, 2, 4);
                    p.Px(Rule, x, oy + 3, 2, 1);
                    p.Px(Rule, x, oy + 5, 2, 1);
                }
                if (at is >= 1600 and < 2050) // la hoja que pasa: de la derecha, por arriba del lomo, a la izquierda
                {
                    var k = (int)((at - 1600) / 150);
                    if (k == 1) p.Px(White, 22, oy - 1, 1, 3);
                    else p.Px(White, k == 0 ? 23 : 20, oy + 1, 2, 1);
                }
                p.Px(body, Ox + 17, oy + 3, 2, 2); // la mano, delante del libro
                break;
            }
            case ChibiMove.Search:
            {
                // La lupa en alto: los ojos van de un lado a otro y, al encontrar, brilla.
                var k = (int)(t / 400 % 4);
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Up);
                DrawFace(p, Ox, oy, blink ? Eyes.Closed : k switch { 0 => Eyes.Right, 1 => Eyes.Up, 2 => Eyes.Left, _ => Eyes.Right }, Mouth.Open);
                // El aro justo sobre la mano: con música, al rebotar, todavía entra en el lienzo.
                p.Map(Steel, 20, oy - 5, " ### ", "#   #", "#   #", "#   #", " ### ");
                p.Px(White, 21, oy - 4);
                if (k == 1) Sparkle(p, Yellow, 24, oy - 5);
                break;
            }
            case ChibiMove.Edit:
            {
                // Una hoja a la derecha que se va llenando; el lápiz se mueve un poco al escribir.
                var k = (int)(t / 160 % 14);
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Mid);
                DrawFace(p, Ox, oy, k == 13 ? Eyes.Happy : Eyes.Right, k == 13 ? Mouth.Smile : Mouth.Neutral);
                p.Px(White, 20, oy + 4, 6, 7);
                for (var line = 0; line < 3; line++) p.Px(Rule, 21, oy + 5 + line * 2, 4, 1);
                for (var c = 0; c < Math.Min(k, 12); c++) p.Px(Cover, 21 + c % 4, oy + 5 + c / 4 * 2, 1, 1);
                var w = k < 12 ? k % 2 : 0;
                p.Px(Pink, 21 + w, oy + 1);
                p.Px(Yellow, 22 + w, oy + 2);
                p.Px(Yellow, 23 + w, oy + 3);
                p.Px(Ink, 24 + w, oy + 4);
                p.Px(body, Ox + 17, oy + 3, 2, 2);
                break;
            }
            case ChibiMove.Run:
            {
                // Una terminal flotando arriba a la derecha: teclea y van saliendo líneas.
                var k = (int)(t / 280 % 6);
                var tap = t / 140 % 2 == 0;
                DrawBody(p, Ox, oy, body, tap ? ArmAt.Mid : ArmAt.Down, tap ? ArmAt.Down : ArmAt.Mid);
                DrawFace(p, Ox, oy, blink ? Eyes.Closed : Eyes.Up, Mouth.Neutral);
                p.Px(Ink, 15, 0, 10, 7);
                p.Px(Screen, 16, 1, 8, 5);
                (bool Prompt, int Length)[] script = [(true, 4), (false, 5), (false, 3), (true, 3), (false, 6), (false, 2)];
                var first = Math.Max(0, k - 2);
                for (var i = first; i <= k; i++)
                {
                    var y = 1 + (i - first) * 2;
                    if (script[i].Prompt) { p.Px(Yellow, 17, y); p.Px(Teal, 19, y, script[i].Length, 1); }
                    else p.Px(Rule, 17, y, script[i].Length, 1);
                }
                if (k < 2 && t / 140 % 2 == 0) p.Px(White, 17, 3 + k * 2);
                break;
            }
            case ChibiMove.Web:
            {
                // El wifi sobre la cabeza: el punto y los arcos se encienden de a uno.
                var k = (int)(t / 300 % 4);
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Down);
                DrawFace(p, Ox, oy, blink ? Eyes.Closed : Eyes.Up, Mouth.Open);
                if (k < 3) p.Px(Blue, 11, 5, 2, 1);
                if (k is 1 or 2) p.Map(Blue, 9, 3, " #### ", "#    #");
                if (k == 2) p.Map(Blue, 7, 0, "  ######  ", " #      # ", "#        #");
                break;
            }
            case ChibiMove.Agent:
            {
                // Guía y observa a los ayudantes (ella misma en chiquito): señala un lado, el ayudante aparece
                // arriba con un «puf», salta mientras lo mira y se va con otro «puf»; después, del otro lado.
                var at = t % 2400;
                var right = at < 1200;
                var k = at % 1200;
                var x = right ? 19 : 2;
                DrawBody(p, Ox, oy, body, right ? ArmAt.Down : ArmAt.Up, right ? ArmAt.Up : ArmAt.Down);
                DrawFace(p, Ox, oy, blink ? Eyes.Closed : right ? Eyes.Right : Eyes.Left, Mouth.Neutral);
                if (k < 160 || k >= 1040) p.Map(Steel, x, 1, " ### ", "#####", " ### ");
                else Helper(p, body, x, k / 160 % 2 == 0 ? 2 : 1, k / 160 % 2 == 1);
                break;
            }
            case ChibiMove.Ponder:
            {
                // Una nube arriba a la derecha con tres puntos que saltan uno tras otro.
                var k = (int)(t / 220 % 3);
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Down);
                DrawFace(p, Ox, oy, blink ? Eyes.Closed : Eyes.Up, Mouth.Neutral);
                p.Map(Steel, 16, 0, " ## ### ", "########", "########", "########", " ###### ");
                p.Px(Steel, 17, 6);
                p.Px(Steel, 15, 7);
                for (var d = 0; d < 3; d++) p.Px(Ink, 18 + d * 2, k == d ? 1 : 2);
                break;
            }
            case ChibiMove.Tool:
            {
                // Martilla: levanta el martillo y golpea, y al golpe saltan chispitas (la llave del chibi no
                // se lee en tan pocos pixeles). Cabe en cinco filas sobre la mano, aun rebotando con música.
                var k = (int)(t / 240 % 4);
                DrawBody(p, Ox, oy, body, ArmAt.Down, ArmAt.Up);
                DrawFace(p, Ox, oy, k % 2 == 1 ? Eyes.Squint : Eyes.Open, Mouth.Neutral);
                if (k % 2 == 0)
                {
                    p.Map(Amber, 19, oy - 3, "# ", " #", " #");
                    p.Px(Steel, 17, oy - 5, 4, 2);
                }
                else
                {
                    p.Map(Amber, 21, oy - 2, " #", "# ");
                    p.Px(Steel, 23, oy - 4, 2, 3);
                    Sparkle(p, Yellow, 24, oy + 1);
                }
                break;
            }
            default:
                return false;
        }
        if (music)
        {
            // Una nota que sube por la esquina de arriba a la izquierda.
            var up = t / ChibiSprite.Beat % 2 == 1;
            p.Map(Purple, up ? 1 : 0, up ? 0 : 2, " ##", " # ", " # ", "## ");
        }
        return true;
    }

    /// <summary>Un ayudante: la mascota en chiquito (5 × 2), con las patitas alternadas al caminar.</summary>
    static void Helper(Painter p, Color body, int x, int y, bool step)
    {
        p.Px(body, x, y, 5, 2);
        p.Px(Ink, x + 1, y);
        p.Px(Ink, x + 3, y);
        foreach (var leg in step ? new[] { 0, 2, 4 } : new[] { 1, 3 })
            p.Px(body, x + leg, y + 2);
    }
}
