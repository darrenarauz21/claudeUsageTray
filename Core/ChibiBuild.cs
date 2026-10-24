namespace ClaudeUsageTray;

/// <summary>
/// «Otras herramientas»: el chibi construye una casita de madera con el mazo, concentrado, y la
/// recorre: arrodillado de perfil pone la base y los postes, de pie levanta las paredes, de espaldas
/// pone el techo, del otro lado hace la puerta y la ventana, y de frente da el último golpe y la mira
/// contento. Entre una posición y otra da un saltito con polvo. Las vistas de perfil y de espaldas
/// existen solo para esto.
/// </summary>
static partial class ChibiSprite
{
    static class Build
    {
        // De perfil, mirando a la derecha (28 de ancho): los dos ojitos de cangrejo, uno detrás del otro.
        public static readonly string[] Profile =
        [
            "..............KKKK..........", // 0  ojo de cangrejo de adelante
            "......KKKK...KWWWWK.........", //    y el de atrás
            ".....KWWWWK..KWWKKK.........",
            ".....KWWKKK..KWWKKK.........",
            ".....KWWKKK..KWWWWK.........",
            ".....KWWWWK...KKKK..........", // 5
            "......KKKK....KOOK..........",
            "......KOOK....KOOK..........",
            "......KOOK....KOOK..........",
            ".....KKOOKKKKKKOOKKKKK......", //    capucha
            "...KKOOOOOOOOOOOOOOOOOKK....", // 10
            "..KOOOOOOOOOOOOOOOOOhhOOK...",
            ".KOOOOOOOOOOOOOOOOOOOOhOOK..",
            ".KoOOOOOOOOOOOOOOOOOOOOhOK..",
            "KooOOOOOOOOOKKKKKKKKKKKKOOK.",
            "KooOOOOOOOOKRRRRRRRRRRRRKOK.", // 15
            "KooOOOOOOOOKRRRyyRRRRRRRRKK.",
            "KooOOOOOOOOKRRyRRRRRRRRRRK..",
            "KooOOOOOOOoKRRRRrSSrRRrSSK..", //    flequillo
            "KooOOOOOOOoKRRrSSSKKKKSSK...",
            "KooOOOOOOOoKRRrSSSSaWaSSK...", // 20 ojo
            "KooOOOOOOOoKRRrSSSSaKaSSSK..",
            "KooOOOOOOOoKRRrSSSSaKaSSSK..",
            "KooOOOOOOOoKRRrSSSSAKASSSK..",
            "KooOOOOOOOoKRRrSSSSSAASSSK..",
            "KooOOOOOOOoKRRrSSbbbSSSSK...", // 25 rubor
            "KooOOOOOOOoKRRrSSSSSSSmK....", //    boca
            ".KoOOOOOOOoKRRrsSSSSSK......",
            "..KoOOOOOOoKKRrssssKK.......",
            "...KKOOOOOOOKRrKKK..........",
            "....KOOOOOOOOOOOOOK.........", // 30
            "...KoOOOOOOOOOOOOOWK........",
            "...KoOOOOOOOOOOOOOWK........",
            "...KoOOOOOOOOOOOOOWOK.......",
            "...KoOOOOOOOOOOOOOWOK.......",
            "...KoOOOOOOOOOOOOOOOK.......", // 35
            "...KoOOOOOOOOOOooooOK.......",
            "..KoOOOOOOOOOOoOOOOOOK......",
            "..KoOOOOOOOOOOoOOOOOOK......",
            ".KoOOOOOOOOOOOOOOOOOOOK.....",
            ".KooooooooooooooooooooK.....", // 40
            ".KKKKKKKKKKKKKKKKKKKKKK.....",
            ".......KSSSKSSSK............",
            ".......KSSSKSSSK............",
            ".......KsSSKSSSK............",
            ".......KSSSKSSSK............", // 45
            "......KKKKKKKKKKKK..........",
            "......KKKKK.KKKKKKK.........",
        ];

        // Arrodillado: cabeza y tronco cuatro filas más abajo, la rodilla de adelante arriba y la de atrás en el suelo.
        public static readonly string[] Kneel = BuildKneel();

        static string[] BuildKneel()
        {
            var g = Grid([.. Enumerable.Repeat(new string('.', 28), 4), .. Profile.Take(42)]);
            Stamp(g, 17, 43, [".KKKK.", "KSSSSK", "KSSSSK"]);
            return [.. g.Select(r => new string(r)), ".KKK..KSSSK......KSSSK......", "KKKKKKKKKKK.....KKKKKKK....."];
        }

        // Ojos de perfil (4 × 6 desde la columna 18, fila 19 de la cara) y bocas.
        public static readonly Dictionary<SideEyes, string[]> Eyes = new()
        {
            [SideEyes.Focus] = ["SSSS", "SSSS", "KKKK", "SaKa", "SAKA", "SSAA"], // concentrado: el párpado a media altura
            [SideEyes.Squeeze] = ["SSSS", "SKKS", "SSSK", "SKKS", "SSSS", "SSSS"], // >, al hacer fuerza
            [SideEyes.Happy] = ["SSSS", "SSSS", "SKKS", "KSSK", "SSSS", "SSSS"],
            [SideEyes.Closed] = ["SSSS", "SSSS", "SSSS", "SSSS", "KKKK", "SSSS"],
        };

        /// <summary>
        /// El mazo con el mango hacia el ángulo dado (en grados: 0 a la derecha, 90 arriba) y la cabeza
        /// perpendicular, con contorno. Grip es dónde va el puño, dentro de las filas.
        /// </summary>
        public static (string[] Rows, int GripX, int GripY) Mallet(double degrees)
        {
            const double handle = 6, length = 8, width = 4;
            var t = degrees * Math.PI / 180;
            double dx = Math.Cos(t), dy = -Math.Sin(t), cx = (handle + width / 2) * dx, cy = (handle + width / 2) * dy;
            var cells = new Dictionary<(int X, int Y), char>();
            for (var i = 0; i <= handle * 8; i++)
                cells[((int)Math.Round(i / 8.0 * dx), (int)Math.Round(i / 8.0 * dy))] = 'D';
            for (var x = -25; x <= 25; x++)
                for (var y = -25; y <= 25; y++)
                {
                    double a = (x - cx) * dx + (y - cy) * dy, b = -(x - cx) * dy + (y - cy) * dx;
                    if (Math.Abs(a) <= width / 2 - 0.25 && Math.Abs(b) <= length / 2 - 0.25)
                        cells[(x, y)] = a > width / 2 - 1.3 ? 'q' : 'D';
                }
            var all = new Dictionary<(int X, int Y), char>(cells);
            foreach (var (x, y) in cells.Keys)
                foreach (var n in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                    all.TryAdd(n, 'K');
            int x0 = all.Keys.Min(k => k.X), x1 = all.Keys.Max(k => k.X), y0 = all.Keys.Min(k => k.Y), y1 = all.Keys.Max(k => k.Y);
            string[] rows = [.. Enumerable.Range(y0, y1 - y0 + 1).Select(y =>
                new string([.. Enumerable.Range(x0, x1 - x0 + 1).Select(x => all.GetValueOrDefault((x, y), '.'))]))];
            return (rows, -x0, -y0);
        }

        // Brazo de perfil (el de adelante), hacia el puño que agarra el mazo.
        public static readonly string[] ArmStrike = ["..KOOK....", ".KOOOOK...", ".KOOOOOK..", "..KOOOOOK.", "...KooooK.", "....KKKK.."];

        // Una pila de tablas: lo que cae para empezar la casita siguiente; y la nube de polvo en que se va la terminada.
        public static readonly string[] Planks =
        [
            ".KKKKKKKKKK.",
            ".KDDDDDDDDK.",
            "KKKKKKKKKKKK",
            "KqqqqqqqqqqK",
            "KKKKKKKKKKKK",
        ];
        public static readonly string[] Cloud = [".gggg.", "gGGGGg", "gGGGGg", ".gggg."];

        // La casita (14 × 16) terminada; House(n) la arma por etapas.
        static readonly string[] Finished =
        [
            "......KK......",
            ".....KMMK.....",
            "....KMMMMK....",
            "...KMMMMMMK...",
            "..KMMMMMMMMK..",
            ".KMMMMMMMMMMK.",
            "KKKKKKKKKKKKKK",
            ".KDDDDDDDDDDK.",
            ".KqqqqqqqqqqK.",
            ".KDKKKKDKKKDK.",
            ".KDKBBKDKqKDK.",
            ".KqKBBKqKqKqK.",
            ".KDKKKKDKqYDK.",
            ".KqqqqqqKqKqK.",
            "KDDDDDDDDDDDDK",
            "KKKKKKKKKKKKKK",
        ];

        /// <summary>0 base, 1 postes, 2 media pared, 3 paredes, 4 techo, 5 puerta y ventana.</summary>
        public static string[] House(int stage)
        {
            var g = Blank(14, 16);
            void Put(IEnumerable<int> rows)
            {
                foreach (var y in rows)
                    for (var x = 0; x < 14; x++)
                        if (Finished[y][x] != '.') g[y][x] = Finished[y][x];
            }
            Put([14, 15]);
            if (stage >= 1)
                for (var y = 7; y < 14; y++) { g[y][1] = g[y][12] = 'K'; g[y][2] = g[y][11] = 'D'; }
            foreach (var y in stage == 2 ? Enumerable.Range(12, 2) : stage >= 3 ? Enumerable.Range(7, 7) : [])
                for (var x = 1; x < 13; x++) g[y][x] = x is 1 or 12 ? 'K' : y % 2 == 1 ? 'D' : 'q';
            if (stage >= 4) Put(Enumerable.Range(0, 7));
            if (stage >= 5) Put(Enumerable.Range(7, 7));
            return [.. g.Select(r => new string(r))];
        }
    }

    enum SideEyes { Open, Focus, Squeeze, Happy, Closed }

    /// <summary>Un cuadro de perfil: con la casita a la derecha (o, con Left, todo espejado).</summary>
    sealed record SideShot
    {
        public bool Kneel { get; init; }
        public bool Left { get; init; }
        /// <summary>El ángulo del mango: 0 golpeando (el mango derecho y la cabeza de pie), 45 o 60 levantado.</summary>
        public double Angle { get; init; }
        /// <summary>En el aire, al saltar de una posición a otra.</summary>
        public int Hop { get; init; }
        public SideEyes Eyes { get; init; } = SideEyes.Focus;
        public int Stage { get; init; }
        /// <summary>Efectos, en casillas del lienzo (antes de espejar).</summary>
        public (string[] Shape, int X, int Y)[] Fx { get; init; } = [];
    }

    /// <summary>Dónde va la casita y dónde agarra el mazo de pie o arrodillado (de perfil, en casillas del lienzo).</summary>
    const int SideHouseX = 22, HouseY = 36, GripX = 21, StandGripY = 39, KneelGripY = 46;

    static char[][] ComposeSide(SideShot s)
    {
        var c = Blank(Columns, Rows);
        Stamp(c, SideHouseX, HouseY, Build.House(s.Stage));
        var g = Grid(s.Kneel ? Build.Kneel : Build.Profile);
        var face = s.Kneel ? 4 : 0;
        if (s.Eyes != SideEyes.Open) Stamp(g, 18, 19 + face, Build.Eyes[s.Eyes]);
        Stamp(g, 23, 26 + face, ["bK", "K."]); // la lengüita afuera, concentrado
        var top = Oy - s.Hop;
        Stamp(c, 0, top, g);
        Stamp(c, 7, top + face + 29 + (s.Kneel ? 2 : 0), Build.ArmStrike);
        var (gx, gy) = (GripX, (s.Kneel ? KneelGripY : StandGripY) - s.Hop - (s.Angle > 50 ? 2 : s.Angle > 0 ? 1 : 0));
        var (rows, px, py) = Build.Mallet(s.Angle);
        Stamp(c, 12, gy - 2, Art.Forearm(gx - 2 - 12 + 1)); // el antebrazo, del codo al puño: el mazo sube delante de la cara sin taparla
        Stamp(c, gx - px, gy - py, rows);
        Stamp(c, gx - 2, gy - 2, Art.Fist);
        foreach (var (shape, x, y) in s.Fx) Stamp(c, x, y, shape);
        if (s.Left)
            foreach (var row in c) Array.Reverse(row);
        return c;
    }

    /// <summary>De espaldas: la capucha entera por detrás con la costura al medio, los ojitos sin pupila y el buzo sin cordones ni bolsillo.</summary>
    static char[][] BackBody()
    {
        var g = Mirror(Body);
        for (var y = 2; y <= 3; y++)
            foreach (var x in new[] { 9, 10, 21, 22 }) g[y][x] = 'W';
        for (var y = 8; y < 29; y++)
        {
            var l = Array.IndexOf(g[y], 'K');
            var r = Array.LastIndexOf(g[y], 'K');
            for (var x = l + 1; x < r; x++) g[y][x] = y == 28 ? 'o' : 'O';
            if (y >= 15) g[y][l + 1] = g[y][r - 1] = 'o';
            if (y >= 9) g[y][15] = g[y][16] = 'o'; // costura
        }
        for (var y = 29; y < 41; y++)
            for (var x = 0; x < g[y].Length; x++)
                if (g[y][x] == 'W' || (y == 36 && x is >= 11 and <= 20)) g[y][x] = 'O';
        return g;
    }

    /// <summary>
    /// La construcción, en tandas de golpes rápidos: el mazo sube a 45° y baja varias veces, y el último
    /// golpe de la tanda (con estrella y polvo) hace aparecer la etapa siguiente. Al terminar, la casita
    /// se va en una nube de polvo y caen las tablas de la próxima. Built es el festejo cuando la
    /// herramienta termina de verdad: la casita queda lista y salta contento.
    /// </summary>
    static void AddBuildMove(Dictionary<ChibiMove, Frame[]> moves, Func<Pose, int, Frame> F)
    {
        // Las notas con música: en los cuadros de perfil, en la esquina libre (sobre la casita); en los demás, a la derecha.
        (int X, int Y)[] right = [(26, 3), (25, 0), (26, 3), (25, 0)], left = [(0, 3), (1, 0), (0, 3), (1, 0)];
        Frame S(SideShot s, int ms) => new(ComposeSide(s), ms) { Notes = s.Left ? left : right };
        Frame P(Pose pose, int ms) => F(pose, ms) with { Notes = right };
        // Polvo al caer de un salto: en casillas del lienzo (perfil) o del chibi (frente y espaldas).
        (string[], int, int)[] Dust(int y, params int[] xs) => [.. xs.Select(x => (SmallSteam, x, y))];

        // Una tanda de perfil: taps golpes; el último, el fuerte, pasa a la etapa next.
        IEnumerable<Frame> SideHits(SideShot at, int taps, int next)
        {
            var gy = at.Kneel ? KneelGripY : StandGripY;
            for (var i = 0; i < taps - 1; i++)
            {
                yield return S(at with { Angle = 45 }, 120);
                yield return S(at with { Fx = [(Spark, GripX + 7, gy + 3)] }, 120);
            }
            yield return S(at with { Angle = 60 }, 160);
            yield return S(at with { Stage = next, Eyes = SideEyes.Squeeze, Fx = [(Star, GripX + 9, gy - 3), (SmallSteam, GripX + 3, gy + 3)] }, 200);
            yield return S(at with { Stage = next }, 200);
        }

        // Una tanda de frente o de espaldas; (gx, gy) es el agarre, en casillas del lienzo.
        IEnumerable<Frame> PoseHits(Pose at, int gx, int gy, int taps, Func<Pose, bool, Pose> next)
        {
            (string[], int, int)[] Held(double angle)
            {
                var lift = angle > 50 ? 2 : angle > 0 ? 1 : 0;
                var (rows, px, py) = Build.Mallet(angle);
                return [(rows, gx - px - Ox - at.Dx, gy - lift - py - Oy), (Art.Fist, gx - 2 - Ox - at.Dx, gy - lift - 2 - Oy)];
            }
            for (var i = 0; i < taps - 1; i++)
            {
                yield return P(at with { Props = Held(45) }, 120);
                yield return P(at with { Props = Held(0), Fx = [(Spark, gx + 7 - Ox, gy + 3 - Oy)] }, 120);
            }
            yield return P(at with { Props = Held(60) }, 160);
            yield return P(next(at, true) with { Props = Held(0), Fx = [(Star, gx + 9 - Ox, gy - 3 - Oy), (SmallSteam, gx + 3 - Ox, gy + 3 - Oy)] }, 200);
            yield return P(next(at, false) with { Props = Held(0) }, 200);
        }
        (string[], int, int)[] House(int n) => [(Build.House(n), 26, HouseY)];

        var frames = new List<Frame>();

        // 1. Arrodillado, de perfil: la base y los postes.
        var kneel = new SideShot { Kneel = true };
        frames.AddRange([S(kneel with { Hop = 2, Fx = Dust(48, 3, 16) }, 160), S(kneel, 240)]);
        frames.AddRange(SideHits(kneel, 4, 1));

        // 2. De pie, de perfil: media pared y la pared entera.
        var stand = new SideShot { Stage = 1 };
        frames.AddRange([S(stand with { Hop = 2, Fx = Dust(48, 4, 15) }, 160), S(stand, 200)]);
        frames.AddRange(SideHits(stand, 3, 2));
        frames.AddRange([S(stand with { Stage = 2, Eyes = SideEyes.Closed }, 120), S(stand with { Stage = 2 }, 160)]);
        frames.AddRange(SideHits(stand with { Stage = 2 }, 4, 3));

        // 3. De espaldas, con la casita detrás a la derecha: el techo.
        var back = new Pose { Back = true, Dx = -4, Right = Arm.Front, Behind = House(3) };
        frames.Add(P(back with { Dy = -2, Right = Arm.Down, Fx = Dust(44, 4, 17) }, 160));
        frames.AddRange(PoseHits(back, 26, 39, 3, (p, _) => p with { Behind = House(4) }));

        // 4. Del otro lado, de perfil: la puerta y la ventana.
        var other = new SideShot { Left = true, Stage = 4 };
        frames.AddRange([S(other with { Hop = 2, Fx = Dust(48, 4, 15) }, 160), S(other, 200)]);
        frames.AddRange(SideHits(other, 4, 5));

        // 5. De frente, junto a la casita terminada: los últimos retoques, y la mira contento.
        var front = new Pose { Dx = -4, Eyes = Eyes.Down, Mouth = Mouth.Tongue, Crab = (2, 2), Right = Arm.Front, Behind = House(5) };
        frames.Add(P(front with { Dy = -2, Right = Arm.Down, Eyes = Eyes.Open, Fx = Dust(44, 4, 17) }, 160));
        frames.AddRange(PoseHits(front, 25, 38, 3, (p, strong) => p with { Eyes = strong ? Eyes.Squeeze : Eyes.Down }));
        var proud = front with { Right = Arm.Down, Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (1, 0) };
        frames.AddRange([
            P(proud with { Fx = [(Spark, 20, 30), (Spark, 34, 36)] }, 320),
            P(proud with { Fx = [(Spark, 26, 27), (Spark, 33, 42)] }, 320),
        ]);

        // Y a la siguiente: la casita se va en una nube de polvo y caen las tablas de la próxima.
        var look = front with { Right = Arm.Down, Eyes = Eyes.Open, Mouth = Mouth.Oh, Crab = (2, 2), Behind = [] };
        frames.AddRange([
            P(look with { Behind = House(5), Fx = [(Build.Cloud, 24, 36), (Build.Cloud, 30, 34), (Build.Cloud, 26, 42), (Build.Cloud, 31, 41)] }, 160),
            P(look with { Fx = [(SmallSteam, 25, 37), (SmallSteam, 32, 35), (SmallSteam, 27, 43), (SmallSteam, 33, 43)] }, 160),
            P(look, 120),
            P(look with { Eyes = Eyes.Up, Behind = [(Build.Planks, 27, 30)] }, 120),
            P(look with { Behind = [(Build.Planks, 27, 39)] }, 120),
            P(look with { Mouth = Mouth.Small, Behind = [(Build.Planks, 27, 47)], Fx = [(SmallSteam, 21, 44), (SmallSteam, 35, 44)] }, 160),
            P(look with { Mouth = Mouth.Small, Eyes = Eyes.Happy, Behind = [(Build.Planks, 27, 47)] }, 280),
        ]);
        moves[ChibiMove.Tool] = [.. frames];

        // Terminó de verdad (la herramienta volvió): la casita queda lista y festeja saltando.
        var cheer = new Pose { Dx = -4, Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (1, 0), Left = Arm.Up, Right = Arm.Up, Behind = House(5) };
        (string[], int, int)[] Shine(int k) => k % 2 == 0
            ? [(Star, 31, 26), (Spark, 20, 34), (Spark, 35, 40)]
            : [(Spark, 29, 28), (Star, 34, 37), (Spark, 22, 30)];
        moves[ChibiMove.Built] =
        [
            P(cheer with { Breathe = true, Fx = Shine(0) }, 120),
            P(cheer with { Dy = -3, Eyes = Eyes.Squeeze, Fx = Shine(1) }, 160),
            P(cheer with { Dy = -4, Fx = Shine(0) }, 120),
            P(cheer with { Dy = -2, Fx = Shine(1) }, 120),
            P(cheer with { Breathe = true, Fx = Shine(0) }, 120),
            P(cheer with { Dy = -3, Eyes = Eyes.Squeeze, Fx = Shine(1) }, 160),
            P(cheer with { Dy = -4, Fx = Shine(0) }, 120),
            P(cheer with { Dy = -2, Fx = Shine(1) }, 120),
            P(cheer with { Mouth = Mouth.Smile, Left = Arm.Out, Right = Arm.Out, Fx = Shine(0) }, 440),
        ];
    }
}
