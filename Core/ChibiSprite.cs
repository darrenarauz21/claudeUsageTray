using System.Drawing;
namespace ClaudeUsageTray;

/// <summary>
/// La opción «Mascota» de Ajustes, que junta la mascota y la animación al actualizar en una sola
/// lista: con la clásica o el chibi, esa mascota anima el ícono y sale en los avisos; con Asterisco,
/// Puntos o Ninguna, ese es el estilo al actualizar y los avisos llevan la clásica. Se guarda en
/// Animation y Mascot del settings.json.
/// </summary>
static class Mascots
{
    public const string Classic = "clasica", Chibi = "chibi";

    public static readonly (string Id, string Label)[] Choices =
        [(Classic, "Clásica"), (Chibi, "Chibi"), .. RefreshSprite.Styles.Where(s => s.Id != RefreshSprite.Mascot)];

    /// <summary>La mascota de los avisos (Classic o Chibi), como Theme.SelectedPalette: la leen al dibujarse.</summary>
    public static string Selected { get; set; } = Classic;

    public static bool IsChibi => Selected == Chibi;

    /// <summary>Lo elegido en la lista (Id de Choices).</summary>
    public static string ChoiceOf(AppSettings s) => s.Animation == RefreshSprite.Mascot ? s.Mascot : s.Animation;

    public static string Label(string choice) => Choices.FirstOrDefault(c => c.Id == choice).Label ?? Choices[0].Label;

    /// <summary>Guarda lo elegido en Animation y Mascot. Con lo ya elegido, deja los dos valores en orden.</summary>
    public static void Choose(AppSettings s, string choice)
    {
        if (!Choices.Any(c => c.Id == choice)) choice = Classic;
        (s.Animation, s.Mascot) = choice is Classic or Chibi ? (RefreshSprite.Mascot, choice) : (choice, Classic);
    }
}

/// <summary>Lo que hace el chibi: cada movimiento es una secuencia de cuadros que se repite.</summary>
enum ChibiMove { Idle, Dance, Wave, Cheer, Think, Dizzy, Sleep, Maracas, Pout, Sulk, Fury }

/// <summary>
/// El chibi de Claude: pelo naranja y hoodie de cangrejo (ojitos sobre antenas), en pixeles. El
/// cuerpo se escribe como su mitad izquierda, una letra por color, y se espeja. Cada cuadro parte
/// de ese dibujo y cambia ojos, boca, ojitos de cangrejo y brazos, mueve la cabeza o el cuerpo y
/// agrega efectos alrededor. Se dibuja en un lienzo de 40 × 52 casillas, con margen para brazos y
/// saltos; la cabeza sola, para el ícono de la bandeja, en 16 × 16. Sus colores son fijos.
/// </summary>
static class ChibiSprite
{
    public const int Columns = 40, Rows = 52;
    /// <summary>La cabeza para la animación al actualizar: grande (32 × 32, la del cuerpo) y chica (16 × 16, para íconos de 16 px).</summary>
    public const int HeadSize = 32, SmallHeadSize = 16;
    /// <summary>En un aviso el chibi asoma desde abajo: se ven unas ToastRows filas, con la fila HemRow (bajo el ruedo) en el borde.</summary>
    public const int ToastRows = 44, HemRow = 46;
    const int Ox = 4, Oy = 4; // dónde va el chibi dentro del lienzo

    static readonly Dictionary<char, Color> Palette = new()
    {
        ['K'] = Hex(0x2A1F1A), // contorno
        ['O'] = Hex(0xEE8A3C), // hoodie
        ['o'] = Hex(0xC9622A), // hoodie, sombra
        ['h'] = Hex(0xF8B374), // hoodie, brillo
        ['R'] = Hex(0xE2582C), // pelo
        ['r'] = Hex(0xB03C1E), // pelo, sombra
        ['y'] = Hex(0xF79555), // pelo, brillo
        ['S'] = Hex(0xF6CFB2), // piel
        ['s'] = Hex(0xE2A888), // piel, sombra
        ['b'] = Hex(0xF08E7E), // rubor
        ['W'] = Hex(0xFFFFFF),
        ['a'] = Hex(0xA8480F), // iris oscuro
        ['A'] = Hex(0xF5B830), // iris claro
        ['m'] = Hex(0x9A3B2A), // boca
        ['P'] = Hex(0x8B5CF6), // notas
        ['Y'] = Hex(0xF5C542), // destellos
        ['B'] = Hex(0x4AA8F0), // signo de pregunta y zetas
        ['M'] = Hex(0xE5484D), // maracas y 💢
        ['F'] = Hex(0xF4877A), // piel roja de furia
        ['f'] = Hex(0xDE5747), // piel roja de furia, sombra
        ['G'] = Hex(0xE4DFD8), // humo
        ['g'] = Hex(0x9E978E), // humo, borde
    };

    // Mitad izquierda del cuerpo, 16 × 48.
    static readonly string[] Body =
    [
        "........KKKK....", // 0  ojo de cangrejo
        ".......KWWWWK...",
        ".......KWKKWK...",
        ".......KWKKWK...",
        ".......KWWWWK...",
        "........KKKK....", // 5
        "........KOOK....", //    antena
        ".....KKKKOOKKKKK", //    capucha
        "....KOOOOOOOOOOO",
        "...KOOhhOOOOOOOO",
        "..KOOhOOOOOOOOOO", // 10
        "..KOhOOOOOOOOOOO",
        ".KOOOOOOKKKKKKKK",
        ".KOOOOOKRRRRRRRR",
        ".KOOOOKRRRyyRRRR",
        "KOOOOKRRRyRRRRRR", // 15
        "KOOOoRRRRrRRRRRR",
        "KOOOoRRRrRRRrRRR",
        "KOOOoRRRrSSrRrSS", //    flequillo
        "KOOOoRRrKKKKKSSS",
        "KOOOoRRrKaWWaSSS", // 20 ojos
        "KOOOoRRrSaWKaSSS",
        "KOOOoRRrSaKKaSSS",
        "KOOOoRRrSAKKASSS",
        "KOOOoRRrSSAASSSS",
        "KOOOoRRrbbbSSSmS", // 25 rubor y boca
        "KOOOoRRRSSSSSSSm",
        ".KOOoRRRRSSSSSSS",
        "..KOoRrRoooKssss", //    la capucha se cierra
        "...KKRrROOOOOWOO", //    cordones
        "....KRrOOOOOOWOO", // 30 hombro, bajo la capucha
        "...KOOoOOOOOOWOO",
        "..KOOOKOOOOOOWOO", //    el brazo sale del tronco
        "..KOOOKoOOOOOWOO",
        ".KOOOKoOOOOOOWOO",
        ".KoooKoOOOOOOOOO", // 35 puño de la manga
        ".KSSSKoOOOOooooo", //    mano y bolsillo
        ".KSSKoOOOOoOOOOO",
        "..KKoOOOOoOOOOOO",
        "...KoOOOoOOOOOOO",
        "...Koooooooooooo", // 40 ruedo con vuelo
        "...KKKKKKKKKKKKK",
        "...........KSSSK", //    piernas
        "...........KSSSK",
        "...........KsSSK",
        "...........KSSSK", // 45
        "..........KKKKK.", //    zapatos
        "..........KKKKK.",
    ];

    // Mitad izquierda de la cabeza para la bandeja, 8 × 16.
    static readonly string[] Head =
    [
        "..KK....", // 0  ojo de cangrejo
        ".KWWK...",
        ".KWKK...",
        "..KOK...",
        "..KOKKKK", //    capucha
        ".KOhOOOO", // 5
        "KOhOOOOO",
        "KOOOKKKK",
        "KOOKRRRR",
        "KOoRRyRR",
        "KOoRrSRS", // 10 flequillo
        "KOoRSWKS", //    ojos
        "KOoRSKKS",
        "KOoRbSSs", //    rubor y boca
        ".KOoRSSS",
        "..KKKKKK", // 15
    ];

    // Torso sin brazo (mitad izquierda, filas 31 a 38), para dibujar encima el brazo en otra pose.
    static readonly string[] Armless =
    [
        "....KOoOOOOOOWOO",
        "....KOoOOOOOOWOO",
        "....KOoOOOOOOWOO",
        "....KOoOOOOOOWOO",
        "....KOoOOOOOOOOO",
        "....KOoOOOOooooo",
        "....KOoOOOoOOOOO",
        "...KoOOOOoOOOOOO",
    ];

    enum Arm { Down, Up, Wave, Out, Think, Angry }
    enum Eyes { Open, Closed, Happy, Sleep, Up, Dizzy, Squeeze, Annoyed }
    enum Mouth { Smile, Open, Small, Oh, Wavy, Cat, Pout }

    // Brazo izquierdo en cada pose: esquina (en casillas del chibi) y filas; el derecho se espeja.
    static readonly Dictionary<Arm, (int X, int Y, string[] Rows)> Arms = new()
    {
        [Arm.Up] = (-2, 23,
        [
            ".KKK....",
            "KSSSK...",
            "KSSSK...",
            ".KoooK..",
            "..KOOOK.",
            "..KOOOK.",
            "...KOOOK",
            "...KOOOK",
            "....KOOO",
            "....KOOO",
        ]),
        [Arm.Wave] = (-4, 22,
        [
            ".KKK......",
            "KSSSK.....",
            "KSSSK.....",
            ".KoooK....",
            "..KOOOK...",
            "...KOOOK..",
            "...KOOOK..",
            "....KOOOK.",
            ".....KOOOK",
            "......KOOO",
            "......KOOO",
        ]),
        // La manito en la boca (solo el brazo izquierdo): un puñito redondo junto a la boca, con los nudillos marcados.
        [Arm.Think] = (0, 25,
        [
            "..........KKKK..",
            ".........KSSSSK.",
            ".........KsSsSK.",
            "......KKKKSSSSK.",
            ".....KoooKKKKK..",
            "....KOOOK.......",
            "...KOOOK........",
            "...KOOK.........",
        ]),
        // Furia: recto hacia abajo y afuera (los dos brazos forman una Λ), con el puño apretado.
        [Arm.Angry] = (-4, 31,
        [
            "......KOOO",
            ".....KOOOK",
            "....KOOOK.",
            "...KOOOK..",
            "..KOOOK...",
            "..KoooK...",
            ".KSSSK....",
            ".KSsSK....",
            "..KKK.....",
        ]),
        [Arm.Out] = (-4, 30,
        [
            ".KKKKKKK.",
            "KSSKoOOOO",
            "KSSKoOOOO",
            ".KKKKKKKK",
        ]),
    };

    // Ojos de 4 × 6 desde la fila 19 («.» es piel); la espiral es de 5 de ancho.
    static readonly Dictionary<Eyes, string[]> EyeShapes = new()
    {
        [Eyes.Closed] = ["....", "....", "....", "....", "KKKK", "...."],
        [Eyes.Happy] = ["....", "....", ".KK.", "K..K", "....", "...."],
        [Eyes.Sleep] = ["....", "....", "....", "K..K", ".KK.", "...."],
        [Eyes.Up] = ["KKKK", "aKKa", "aWKa", "aaaa", "AAAA", ".AA."],
        [Eyes.Dizzy] = [".....", "KKKKK", "....K", ".KK.K", ".K..K", ".KKKK"],
        [Eyes.Squeeze] = ["....", "KK..", "..KK", "KK..", "....", "...."], // > (el derecho, espejado: <)
        [Eyes.Annoyed] = ["....", "....", "KKKK", "KKaa", "KKAA", ".AA."], // párpado a media altura, de reojo
    };

    // Bocas de 4 × 2 desde la columna 14, fila 25.
    static readonly Dictionary<Mouth, string[]> MouthShapes = new()
    {
        [Mouth.Open] = ["mmmm", ".bb."],
        [Mouth.Small] = ["....", ".mm."],
        [Mouth.Oh] = [".mm.", ".mm."], // entreabierta, al pensar
        [Mouth.Cat] = ["m.mm.m", ".m..m."], // :3, de 6 de ancho desde la columna 13
        [Mouth.Pout] = [".mm.", "m..m"], // puchero
        [Mouth.Wavy] = ["m.m.", ".m.m"],
    };

    static readonly string[] Note = ["..PP", "..P.", "..P.", "PPP.", "PP.."];
    static readonly string[] Spark = [".Y.", "YWY", ".Y."];
    static readonly string[] Star = ["..Y..", "..Y..", "YYWYY", "..Y..", "..Y.."];
    static readonly string[] Question = [".BBB.", "B...B", "...B.", "..B..", ".....", "..B.."];
    static readonly string[] Z = ["BBBB", "..B.", ".B..", "BBBB"];
    static readonly string[] SmallZ = ["BBB", ".B.", "BBB"];
    static readonly string[] Maraca = [".KKK.", "KMMMK", "KYYYK", "KMMMK", ".KMK.", "..K..", "..K.."];
    static readonly string[] Steam = [".gg.", "gGGg", "gGGg", ".gg."];
    static readonly string[] SmallSteam = [".g.", "gGg", ".g."];
    static readonly string[] Anger = [".M.M.", "MM.MM", ".....", "MM.MM", ".M.M."]; // 💢

    // Brazos cruzados (mitad izquierda, se espeja): una banda sobre el pecho con los codos afuera.
    static readonly string[] Cross =
    [
        "...KKKKKKKKKKKKK",
        "..KOOOOOOOOOOOoK",
        "..KOOOOOOOOOOOoK",
        "..KooooooooooooK",
        "...KKKKKKKKKKKKK",
    ];
    const int CrossTop = 30;

    // Dónde va la maraca (esquina, en casillas del chibi) con la mano en cada pose: el mango toca la mano.
    static readonly Dictionary<(Arm Arm, bool Left), (int X, int Y)> MaracaAt = new()
    {
        [(Arm.Up, true)] = (-2, 16), [(Arm.Up, false)] = (29, 16),
        [(Arm.Out, true)] = (-4, 23), [(Arm.Out, false)] = (31, 23),
    };

    // Ojos de la cabeza grande mirando a un lado: el brillo arriba y la pupila corrida (los dos ojos igual).
    static readonly Dictionary<MascotFrame, string[]> LookEyes = new()
    {
        [MascotFrame.Left] = ["KKKK", "aWWa", "KKaa", "KKaa", "KKAA", ".AA."],
        [MascotFrame.Right] = ["KKKK", "aWWa", "aaKK", "aaKK", "AAKK", ".AA."],
    };

    // Ojos de la cabeza chica (2 × 2 en las columnas 5 y 9, fila 11) para cada cuadro de la animación al actualizar.
    static readonly Dictionary<MascotFrame, string[]> HeadEyes = new()
    {
        [MascotFrame.Closed] = ["..", "KK"],
        [MascotFrame.Open] = ["WK", "KK"],
        [MascotFrame.Left] = ["KW", "KW"],
        [MascotFrame.Right] = ["WK", "WK"],
        [MascotFrame.Happy] = [], // > < como la mascota clásica: se dibuja aparte
    };

    /// <summary>Un cuadro: qué cambia respecto del dibujo base. Crab es dónde miran los ojitos de cangrejo (null: cerrados).</summary>
    sealed record Pose
    {
        public Eyes Eyes { get; init; }
        public Mouth Mouth { get; init; }
        public (int X, int Y)? Crab { get; init; } = (1, 1);
        public int Head { get; init; }
        public Arm Left { get; init; }
        public Arm Right { get; init; }
        public int Dy { get; init; }
        public bool Breathe { get; init; }
        public (string[] Shape, int X, int Y)[] Fx { get; init; } = [];
        /// <summary>Maracas en las manos levantadas o abiertas; cada número es cuánto sube esa maraca al sacudirse.</summary>
        public (int Left, int Right)? Maracas { get; init; }
        /// <summary>Cachetes inflados (puchero).</summary>
        public bool Cheeks { get; init; }
        /// <summary>Brazos cruzados sobre el pecho (quita los dos brazos).</summary>
        public bool Folded { get; init; }
        /// <summary>La cara roja de furia.</summary>
        public bool Red { get; init; }
    }

    sealed record Frame(char[][] Grid, int Ms);

    static readonly Dictionary<ChibiMove, Frame[]> Moves = BuildMoves();
    static readonly Dictionary<MascotFrame, char[][]> Heads = HeadEyes.Keys.ToDictionary(f => f, BuildHead);
    static readonly Dictionary<MascotFrame, char[][]> SmallHeads = HeadEyes.Keys.ToDictionary(f => f, BuildSmallHead);

    /// <summary>Pinta el cuadro del momento t (ms desde que empezó el movimiento), en casillas del lienzo de 40 × 52.</summary>
    public static void Paint(PixelFill fill, ChibiMove move, long t)
    {
        var frames = Moves[move];
        var total = frames.Sum(f => f.Ms);
        var at = t % total;
        foreach (var frame in frames)
        {
            if (at < frame.Ms) { Emit(fill, frame.Grid); return; }
            at -= frame.Ms;
        }
    }

    /// <summary>Número del cuadro que toca en el momento t: sirve para repintar solo cuando cambia.</summary>
    public static int FrameIndex(ChibiMove move, long t)
    {
        var frames = Moves[move];
        var at = t % frames.Sum(f => f.Ms);
        for (var i = 0; i < frames.Length; i++)
        {
            if (at < frames[i].Ms) return i;
            at -= frames[i].Ms;
        }
        return 0;
    }

    /// <summary>La cabeza sola en un cuadro de la animación al actualizar: la grande (32 × 32) o la chica (16 × 16).</summary>
    public static void PaintHead(PixelFill fill, MascotFrame frame, bool small = false) =>
        Emit(fill, (small ? SmallHeads : Heads)[frame]);

    /// <summary>Qué hace el chibi en cada aviso.</summary>
    public static ChibiMove ForNotice(NoticeKind kind) => kind switch
    {
        NoticeKind.Done => ChibiMove.Cheer,
        NoticeKind.UsageReset => ChibiMove.Maracas, // límite renovado: chachachá
        NoticeKind.Failed or NoticeKind.UsageCritical => ChibiMove.Dizzy,
        NoticeKind.Closed => ChibiMove.Sleep,
        _ => ChibiMove.Think, // pregunta, permiso, reintento, uso alto y sesión vencida: algo que mirar
    };

    // ── Cuadros ─────────────────────────────────────────────────────────────────────────────

    static Dictionary<ChibiMove, Frame[]> BuildMoves()
    {
        var open = new Pose();
        Frame F(Pose pose, int ms) => new(Compose(pose), ms);

        var dance = new List<Frame>();
        foreach (var k in new[] { 0, 2 })
        {
            dance.Add(F(new() { Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (0, 1), Head = -1, Left = Arm.Up, Fx = [(Note, 30, 8 - k)] }, 160));
            dance.Add(F(new() { Eyes = Eyes.Happy, Crab = (1, 0), Left = Arm.Out, Right = Arm.Out, Dy = -1, Fx = [(Note, 31, 6 - k)] }, 160));
            dance.Add(F(new() { Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (2, 1), Head = 1, Right = Arm.Up, Fx = [(Note, -2, 8 - k)] }, 160));
            dance.Add(F(new() { Eyes = Eyes.Happy, Crab = (1, 0), Left = Arm.Out, Right = Arm.Out, Dy = -1, Fx = [(Note, -3, 6 - k)] }, 160));
        }
        // Cierra con un salto con los brazos arriba: el único momento con > <.
        dance.Add(F(new() { Eyes = Eyes.Squeeze, Mouth = Mouth.Open, Crab = (1, 0), Left = Arm.Up, Right = Arm.Up, Dy = -2, Fx = [(Star, -3, 6), (Star, 29, 6)] }, 240));
        dance.Add(F(new() { Eyes = Eyes.Happy, Left = Arm.Up, Right = Arm.Up, Breathe = true }, 160));

        var wave = open with { Head = 1, Right = Arm.Up };
        var waving = wave with { Right = Arm.Wave };
        var cheer = new Pose { Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (1, 0), Left = Arm.Up, Right = Arm.Up };
        var think = new Pose { Eyes = Eyes.Up, Mouth = Mouth.Oh, Crab = (1, 0), Left = Arm.Think };
        var cha = new Pose { Eyes = Eyes.Happy, Crab = (1, 0), Left = Arm.Up, Right = Arm.Up };
        var sleep = new Pose { Eyes = Eyes.Sleep, Mouth = Mouth.Small, Crab = null };
        var pout = new Pose { Eyes = Eyes.Annoyed, Mouth = Mouth.Pout, Head = -1, Cheeks = true, Crab = (0, 1) };
        var sulk = new Pose { Eyes = Eyes.Closed, Mouth = Mouth.Pout, Head = 1, Cheeks = true, Folded = true, Crab = (2, 1) };
        var fury = new Pose { Eyes = Eyes.Squeeze, Mouth = Mouth.Pout, Cheeks = true, Left = Arm.Angry, Right = Arm.Angry, Red = true, Crab = (1, 2) };
        (int X, int Y)[] circle = [(0, 0), (2, 0), (2, 2), (0, 2)];
        int[] wobble = [-1, 0, 1, 0];

        return new()
        {
            [ChibiMove.Idle] =
            [
                F(open, 1400), F(open with { Crab = (0, 1) }, 500), F(open, 250), F(open with { Crab = (2, 1) }, 500),
                F(open, 900), F(open with { Eyes = Eyes.Closed }, 110), F(open, 300), F(open with { Eyes = Eyes.Closed }, 110),
            ],
            [ChibiMove.Dance] = [.. dance],
            [ChibiMove.Wave] =
            [
                F(wave, 220), F(waving with { Mouth = Mouth.Open }, 220), F(wave, 220),
                F(waving with { Mouth = Mouth.Open }, 220), F(wave with { Eyes = Eyes.Closed }, 110), F(waving, 220),
            ],
            [ChibiMove.Cheer] =
            [
                F(cheer with { Fx = [(Spark, -1, 14), (Spark, 30, 14)] }, 110),
                F(cheer with { Dy = -2, Fx = [(Star, -3, 10), (Star, 29, 10)] }, 110),
                F(cheer with { Dy = -3, Fx = [(Spark, -2, 4), (Spark, 31, 4), (Spark, 15, -4)] }, 110),
                F(cheer with { Dy = -2, Fx = [(Star, -3, 1), (Star, 29, 1)] }, 110),
                F(cheer with { Mouth = Mouth.Smile, Breathe = true }, 110),
                F(new() { Eyes = Eyes.Happy, Left = Arm.Out, Right = Arm.Out }, 300),
            ],
            [ChibiMove.Think] =
            [
                F(think with { Fx = [(Question, 29, 1)] }, 400),
                F(think with { Fx = [(Question, 29, 0)] }, 400),
                F(think with { Fx = [(Question, 29, 1)] }, 400),
                F(think with { Eyes = Eyes.Closed, Fx = [(Question, 29, 0)] }, 120),
            ],
            [ChibiMove.Dizzy] = [.. Enumerable.Range(0, 8).Select(i => F(new()
            {
                Eyes = Eyes.Dizzy, Mouth = Mouth.Wavy, Crab = circle[i % 4], Head = wobble[i % 4], Fx = Orbit(i * Math.PI / 4),
            }, 120))],
            // Chachachá con maracas: lento, lento, rápido, rápido, lento.
            [ChibiMove.Maracas] =
            [
                F(cha with { Mouth = Mouth.Open, Crab = (0, 1), Head = -1, Right = Arm.Out, Maracas = (0, 1) }, 300),
                F(cha with { Mouth = Mouth.Open, Crab = (2, 1), Head = 1, Left = Arm.Out, Maracas = (1, 0) }, 300),
                F(cha with { Dy = -1, Maracas = (1, 1) }, 150),
                F(cha with { Maracas = (0, 0) }, 150),
                F(cha with { Eyes = Eyes.Squeeze, Mouth = Mouth.Open, Dy = -1, Maracas = (1, 1), Fx = [(Spark, -3, 12), (Spark, 34, 12)] }, 300),
            ],
            // De tanto molestarlo a clics: puchero, brazos cruzados y furia.
            [ChibiMove.Pout] =
            [
                F(pout, 450), F(pout with { Dy = -1 }, 150), F(pout with { Crab = (0, 2) }, 450), F(pout with { Eyes = Eyes.Closed }, 150),
            ],
            [ChibiMove.Sulk] =
            [
                F(sulk, 700), F(sulk with { Eyes = Eyes.Annoyed }, 500), F(sulk with { Dy = -1 }, 150), F(sulk, 500),
            ],
            [ChibiMove.Fury] =
            [
                F(fury with { Head = -1, Fx = [(Steam, 1, 6), (Steam, 27, 6), (Anger, 23, 11)] }, 90),
                F(fury with { Head = 1, Fx = [(Steam, 1, 5), (Steam, 27, 5), (Anger, 23, 11)] }, 90),
                F(fury with { Head = -1, Fx = [(Steam, 0, 3), (Steam, 28, 3), (SmallSteam, 2, 7), (SmallSteam, 27, 7)] }, 90),
                F(fury with { Head = 1, Fx = [(Steam, 0, 2), (Steam, 28, 2), (SmallSteam, 2, 7), (SmallSteam, 27, 7), (Anger, 23, 11)] }, 90),
                F(fury with { Head = -1, Fx = [(SmallSteam, 0, 0), (SmallSteam, 29, 0), (Steam, 1, 6), (Steam, 27, 6), (Anger, 23, 11)] }, 90),
                F(fury with { Head = 1, Fx = [(SmallSteam, 0, -1), (SmallSteam, 29, -1), (Steam, 1, 5), (Steam, 27, 5)] }, 90),
            ],
            [ChibiMove.Sleep] =
            [
                F(sleep with { Fx = [(SmallZ, 27, 6)] }, 450),
                F(sleep with { Breathe = true, Fx = [(SmallZ, 28, 4)] }, 450),
                F(sleep with { Fx = [(Z, 29, 0), (SmallZ, 27, 6)] }, 450),
                F(sleep with { Breathe = true, Fx = [(Z, 30, -2), (SmallZ, 28, 4)] }, 450),
                F(sleep with { Fx = [(Z, 31, -4)] }, 450),
                F(sleep with { Breathe = true }, 450),
            ],
        };
    }

    /// <summary>Tres destellos girando en una elipse sobre la cabeza.</summary>
    static (string[] Shape, int X, int Y)[] Orbit(double t) =>
    [
        .. Enumerable.Range(0, 3).Select(i =>
        {
            var a = t + i * 2 * Math.PI / 3;
            return (Spark, (int)Math.Round(14.5 + 14 * Math.Cos(a)), (int)Math.Round(-2 + 2 * Math.Sin(a)));
        }),
    ];

    static char[][] Compose(Pose pose)
    {
        var g = Mirror(Body);
        g[21][20] = 'W'; g[21][21] = 'K'; // el brillo del ojo derecho, del mismo lado que el del izquierdo
        SetEyes(g, pose.Eyes);
        SetMouth(g, pose.Mouth);
        SetCrab(g, pose.Crab);
        if (pose.Cheeks) PuffCheeks(g);
        ShiftHead(g, pose.Head);
        if (pose.Left != Arm.Down || pose.Folded) RemoveArm(g, left: true);
        if (pose.Right != Arm.Down || pose.Folded) RemoveArm(g, left: false);
        if (pose.Folded) FoldArms(g);
        if (pose.Red)
            for (var y = 14; y < 29; y++)
                for (var x = 5; x < 27; x++)
                    g[y][x] = g[y][x] switch { 'S' => 'F', 's' or 'b' => 'f', var ch => ch };
        if (pose.Breathe) Squash(g);

        var canvas = Blank(Columns, Rows);
        Stamp(canvas, Ox, Oy + pose.Dy, g);
        foreach (var (arm, left) in new[] { (pose.Left, true), (pose.Right, false) })
        {
            if (arm == Arm.Down) continue;
            var (x, y, rows) = Arms[arm];
            if (!left)
            {
                x = 31 - x - (rows[0].Length - 1);
                rows = [.. rows.Select(Reverse)];
            }
            Stamp(canvas, Ox + x, Oy + pose.Dy + y, rows);
        }
        if (pose.Maracas is { } shake)
            foreach (var (arm, left) in new[] { (pose.Left, true), (pose.Right, false) })
                if (MaracaAt.TryGetValue((arm, left), out var at))
                    Stamp(canvas, Ox + at.X, Oy + pose.Dy + at.Y - (left ? shake.Left : shake.Right), Maraca);
        foreach (var (shape, x, y) in pose.Fx) Stamp(canvas, Ox + x, Oy + y, shape);
        return canvas;
    }

    static void SetEyes(char[][] g, Eyes eyes)
    {
        if (eyes == Eyes.Open) return;
        foreach (var x0 in new[] { 8, 19 })
            for (var y = 19; y < 25; y++)
                for (var x = x0; x < x0 + 5; x++)
                    g[y][x] = 'S';
        var shape = EyeShapes[eyes];
        if (eyes == Eyes.Squeeze)
        {
            Stamp(g, 9, 19, shape);
            Stamp(g, 19, 19, [.. shape.Select(Reverse)]);
            return;
        }
        if (eyes == Eyes.Dizzy) // la espiral ocupa también la columna de las pestañas, espejada en el ojo derecho
        {
            Stamp(g, 8, 19, shape);
            Stamp(g, 19, 19, [.. shape.Select(Reverse)]);
            return;
        }
        Stamp(g, 9, 19, shape);
        Stamp(g, 19, 19, shape);
        if (eyes == Eyes.Up) // el rabillo de las pestañas
            for (var y = 19; y <= 20; y++)
                g[y][8] = g[y][23] = 'K';
    }

    /// <summary>Cachetes inflados: más rubor y la cara una casilla más ancha a cada lado.</summary>
    static void PuffCheeks(char[][] g)
    {
        Stamp(g, 7, 25, ["Sbbb", "Sbbb"]);
        Stamp(g, 21, 25, ["bbbS", "bbbS"]);
        for (var y = 25; y <= 26; y++) g[y][6] = g[y][25] = 'K';
    }

    /// <summary>
    /// Brazos cruzados a la altura del pecho: el de arriba (izquierdo) en naranja con brillo y el de
    /// abajo en sombra, separados por una diagonal; cada mano toma el brazo contrario.
    /// </summary>
    static void FoldArms(char[][] g)
    {
        Stamp(g, 0, CrossTop, [.. Cross.Select(row => row + Reverse(row))]);
        for (var y = CrossTop + 1; y < CrossTop + 4; y++)
        {
            var seam = 14 + (y - CrossTop - 1);
            for (var x = 3; x < 29; x++)
                if (g[y][x] is 'O' or 'o')
                    g[y][x] = x >= seam ? 'o' : y == CrossTop + 1 && x > 5 && x < seam - 1 ? 'h' : 'O';
            g[y][seam] = 'K';
        }
        Stamp(g, 2, CrossTop + 1, ["KSSKo", "KsSKo", ".KK.."]);
        Stamp(g, 25, CrossTop + 1, ["..KK.", "oKSSK", "oKSsK"]);
    }

    static void SetMouth(char[][] g, Mouth mouth)
    {
        if (mouth == Mouth.Smile) return;
        for (var y = 25; y <= 26; y++)
            for (var x = 14; x < 18; x++)
                g[y][x] = 'S';
        Stamp(g, 14, 25, MouthShapes[mouth]);
    }

    static void SetCrab(char[][] g, (int X, int Y)? look)
    {
        foreach (var x0 in new[] { 8, 20 })
        {
            if (look is not { } at)
            {
                Stamp(g, x0, 1, ["OOOO", "OOOO", "KKKK", "OOOO"]);
                continue;
            }
            Stamp(g, x0, 1, ["WWWW", "WWWW", "WWWW", "WWWW"]);
            Stamp(g, x0 + at.X, 1 + at.Y, ["KK", "KK"]);
        }
    }

    /// <summary>Mueve la cabeza (filas 0 a 29) a un lado; el cuerpo queda en su lugar.</summary>
    static void ShiftHead(char[][] g, int dx)
    {
        if (dx == 0) return;
        for (var y = 0; y < 30; y++)
        {
            var row = new char[g[y].Length];
            Array.Fill(row, '.');
            for (var x = 0; x < row.Length; x++)
                if (x - dx >= 0 && x - dx < row.Length) row[x] = g[y][x - dx];
            g[y] = row;
        }
    }

    static void RemoveArm(char[][] g, bool left)
    {
        for (var r = 0; r < Armless.Length; r++)
            for (var c = 0; c < 7; c++)
                g[31 + r][left ? c : 31 - c] = Armless[r][c];
    }

    /// <summary>Respira: todo lo que está sobre las piernas baja una casilla.</summary>
    static void Squash(char[][] g)
    {
        for (var y = 42; y > 0; y--) g[y] = (char[])g[y - 1].Clone();
        g[0] = new string('.', g[0].Length).ToCharArray();
    }

    /// <summary>
    /// La cabeza grande: las filas 0 a 29 del cuerpo (ojitos de cangrejo, capucha y cara), cerradas
    /// abajo. Al terminar hace > < con boca de gatito (:3).
    /// </summary>
    static char[][] BuildHead(MascotFrame frame)
    {
        var g = Mirror(Body);
        g[21][20] = 'W'; g[21][21] = 'K';
        switch (frame)
        {
            case MascotFrame.Closed:
                SetEyes(g, Eyes.Closed);
                break;
            case MascotFrame.Left or MascotFrame.Right:
                SetEyes(g, Eyes.Closed);
                foreach (var x0 in new[] { 9, 19 })
                    for (var r = 0; r < 6; r++)
                        for (var c = 0; c < 4; c++)
                            g[19 + r][x0 + c] = LookEyes[frame][r][c] == '.' ? 'S' : LookEyes[frame][r][c];
                for (var y = 19; y <= 20; y++) g[y][8] = g[y][23] = 'K'; // el rabillo de las pestañas
                break;
            case MascotFrame.Happy:
                SetEyes(g, Eyes.Squeeze);
                for (var y = 25; y <= 26; y++)
                    for (var x = 13; x < 19; x++)
                        g[y][x] = 'S';
                Stamp(g, 13, 25, MouthShapes[Mouth.Cat]);
                break;
        }
        var rows = g.Take(30).ToList();
        rows[29] = [.. rows[29].Select(ch => ch == 'W' ? 'O' : ch)]; // sin la punta de los cordones
        rows.Add("...KKKKKKKKKKKKKKKKKKKKKKKKKK...".ToCharArray()); // cierra abajo, bajo el cuello
        return [Blank(HeadSize, 1)[0], .. rows]; // 1 + 30 + 1 = 32 filas
    }

    static char[][] BuildSmallHead(MascotFrame frame)
    {
        var g = Mirror(Head);
        g[11][9] = 'W'; g[11][10] = 'K'; // brillo del mismo lado en los dos ojos
        foreach (var x in new[] { 5, 9 })
        {
            for (var y = 11; y <= 12; y++) g[y][x] = g[y][x + 1] = 'S';
            Stamp(g, x, 11, HeadEyes[frame]);
        }
        if (frame == MascotFrame.Happy)
        {
            g[10][5] = g[10][6] = g[10][9] = g[10][10] = 'S'; // el flequillo deja lugar a los ojos
            Stamp(g, 5, 10, ["K.", ".K", "K."]);
            Stamp(g, 9, 10, [".K", "K.", ".K"]);
            Stamp(g, 7, 13, ["mm"]);
        }
        return g;
    }

    // ── Rejillas ────────────────────────────────────────────────────────────────────────────

    static char[][] Mirror(string[] half) => [.. half.Select(row => (row + Reverse(row)).ToCharArray())];

    static string Reverse(string row) => new([.. row.Reverse()]);

    static char[][] Blank(int width, int height) => [.. Enumerable.Range(0, height).Select(_ => new string('.', width).ToCharArray())];

    /// <summary>Copia las filas sobre la rejilla desde (x0, y0); «.» no tapa lo de abajo y lo que cae fuera se pierde.</summary>
    static void Stamp(char[][] g, int x0, int y0, IReadOnlyList<string> rows)
    {
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Length; c++)
                Put(g, x0 + c, y0 + r, rows[r][c]);
    }

    static void Stamp(char[][] g, int x0, int y0, char[][] rows)
    {
        for (var r = 0; r < rows.Length; r++)
            for (var c = 0; c < rows[r].Length; c++)
                Put(g, x0 + c, y0 + r, rows[r][c]);
    }

    static void Put(char[][] g, int x, int y, char ch)
    {
        if (ch != '.' && y >= 0 && y < g.Length && x >= 0 && x < g[y].Length) g[y][x] = ch;
    }

    /// <summary>Manda la rejilla a dibujar, juntando en un solo rectángulo las casillas seguidas del mismo color.</summary>
    static void Emit(PixelFill fill, char[][] grid)
    {
        for (var y = 0; y < grid.Length; y++)
        {
            var row = grid[y];
            for (var x = 0; x < row.Length;)
            {
                var ch = row[x];
                var end = x + 1;
                while (end < row.Length && row[end] == ch) end++;
                if (ch != '.') fill(Palette[ch], x, y, end - x, 1);
                x = end;
            }
        }
    }

    static Color Hex(int rgb) => Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}

/// <summary>
/// Qué hace el chibi del escritorio: baila si alguna sesión trabaja, piensa si alguna espera su
/// respuesta, se queda quieto si hay sesiones paradas y duerme si no hay ninguna. Un gesto (saludar,
/// festejar, marearse) pasa por delante un rato. Cada movimiento nuevo empieza desde su primer cuadro.
/// </summary>
sealed class ChibiDirector
{
    ChibiMove _state = ChibiMove.Sleep;
    (ChibiMove Move, long Until)? _gesture;
    ChibiMove _current = ChibiMove.Sleep;
    long _since = Environment.TickCount64;

    public void SetSessions(IEnumerable<SessionInfo> sessions)
    {
        var list = sessions.ToList();
        _state = list.Any(s => s.State == SessionState.Working) ? ChibiMove.Dance
            : list.Any(s => s.State == SessionState.Waiting) ? ChibiMove.Think
            : list.Count > 0 ? ChibiMove.Idle
            : ChibiMove.Sleep;
    }

    int _pokes;
    long _lastPoke;

    /// <summary>Un gesto por unos segundos, encima de lo que esté haciendo.</summary>
    public void Gesture(ChibiMove move, int ms = 2500) => _gesture = (move, Environment.TickCount64 + ms);

    /// <summary>
    /// Un clic encima: saluda. Si lo siguen molestando (clics con menos de 4 s entre uno y otro), hace
    /// puchero (3 a 5), cruza los brazos (6 a 8) y se pone rojo de furia (9 o más).
    /// </summary>
    public void Poke()
    {
        var now = Environment.TickCount64;
        if (now - _lastPoke > 4000) _pokes = 0;
        _lastPoke = now;
        _pokes++;
        var move = _pokes switch { <= 2 => ChibiMove.Wave, <= 5 => ChibiMove.Pout, <= 8 => ChibiMove.Sulk, _ => ChibiMove.Fury };
        Gesture(move, move == ChibiMove.Wave ? 2500 : 4000);
    }

    /// <summary>
    /// Al llegar un aviso hace el mismo gesto que el aviso (festeja, piensa, se marea, duerme, baila
    /// chachachá) por unos segundos y después vuelve a lo que corresponda.
    /// </summary>
    public void Notice(NoticeKind kind)
    {
        var move = ChibiSprite.ForNotice(kind);
        Gesture(move, move == ChibiMove.Maracas ? 3600 : 3000); // el chachachá, tres vueltas
    }

    /// <summary>El movimiento de ahora y cuánto lleva (ms), para ChibiSprite.Paint.</summary>
    public (ChibiMove Move, long T) Now()
    {
        var now = Environment.TickCount64;
        if (_gesture is { } g && now >= g.Until) _gesture = null;
        var move = _gesture?.Move ?? _state;
        if (move != _current)
        {
            _current = move;
            _since = now;
        }
        return (move, now - _since);
    }
}

/// <summary>
/// Lo que dice la mascota del escritorio en su nubecita (opción «Hablar»): el mismo aviso, contado
/// a su manera, de tú y con caritas. El chibi llama con «¡Oi!» cuando algo espera respuesta.
/// </summary>
static class MascotLines
{
    public static string For(Notice notice, bool chibi)
    {
        var s = Short(notice.Subtitle);
        var oi = chibi ? "¡Oi! " : "";
        var title = notice.Title.Length > 0 ? char.ToLower(notice.Title[0]) + notice.Title[1..] : "";
        return notice.Kind switch
        {
            NoticeKind.Done => $"¡Listo! «{s}» ya terminó \\(^o^)/",
            NoticeKind.Question => $"{oi}Psst… «{s}» quiere preguntarte algo (o_o)?",
            NoticeKind.Permission => $"{oi}¡Toc, toc! «{s}» necesita tu permiso para seguir.",
            NoticeKind.Failed => $"¡Uwaaa! «{s}» se tropezó… (T_T)",
            NoticeKind.Retrying => $"«{s}» lo intenta otra vez. ¡Tú puedes! (>_<)",
            NoticeKind.Closed => $"Ay… «{s}» se cerró a medio camino. (-_-) zzz",
            NoticeKind.UsageHigh => $"{oi}Ojo: {title} ({s}). ¡Con calma! (°o°;)",
            NoticeKind.UsageCritical => $"¡Casi sin energía! {notice.Title} ({s}). (×_×)",
            NoticeKind.UsageReset => $"¡Chachachá! Se renovó: {s}. ¡A darle! ♪",
            NoticeKind.Expired => $"Hmm… la cuenta «{s}» pide que abras Claude Code otra vez. (._.)",
            _ => notice.Title,
        };
    }

    static string Short(string text) => text.Length <= 42 ? text : text[..41].TrimEnd() + "…";
}

/// <summary>
/// La nubecita de diálogo en pixeles: contorno oscuro, relleno blanco, esquinas en escalón y una
/// colita que apunta a la mascota (abajo o arriba). Mide Width × Height casillas, colita incluida.
/// </summary>
static class BubbleSprite
{
    public const int Tail = 3; // alto de la colita, en casillas
    static readonly Color Ink = Color.FromArgb(0x2A, 0x1F, 0x1A);
    static readonly Color Paper = Color.FromArgb(0xFF, 0xFF, 0xFF);

    /// <summary>tailX: columna de la punta de la colita; tailDown: la colita sale por abajo (si no, por arriba).</summary>
    public static void Paint(PixelFill fill, int width, int height, int tailX, bool tailDown)
    {
        var top = tailDown ? 0 : Tail;
        var h = height - Tail; // alto del cuerpo de la nube
        // Cuerpo: relleno con esquinas recortadas en escalón y contorno de una casilla.
        fill(Ink, 2, top, width - 4, 1);
        fill(Ink, 2, top + h - 1, width - 4, 1);
        fill(Ink, 0, top + 2, 1, h - 4);
        fill(Ink, width - 1, top + 2, 1, h - 4);
        fill(Ink, 1, top + 1, 1, 1); fill(Ink, width - 2, top + 1, 1, 1);
        fill(Ink, 1, top + h - 2, 1, 1); fill(Ink, width - 2, top + h - 2, 1, 1);
        fill(Paper, 2, top + 1, width - 4, h - 2);
        fill(Paper, 1, top + 2, width - 2, h - 4);

        // Colita: un triángulo que se angosta hacia la mascota.
        tailX = Math.Clamp(tailX, 3, width - 4);
        for (var i = 0; i < Tail; i++)
        {
            var y = tailDown ? top + h - 1 + i : top - i;
            var half = Tail - 1 - i;
            fill(Paper, tailX - half, y, half * 2 + 1, 1);
            fill(Ink, tailX - half - 1, y, 1, 1);
            fill(Ink, tailX + half + 1, y, 1, 1);
        }
        fill(Ink, tailX, tailDown ? top + h - 1 + Tail : top - Tail, 1, 1);
    }
}
