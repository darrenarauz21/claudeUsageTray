namespace ClaudeUsageTray;

/// <summary>
/// Los gestos del flujo: lo que hace la sesión (leer, buscar, editar, ejecutar, web, agentes, pensar
/// u otra herramienta), cada uno con su objeto en las manos. Con música, rebota al compás y suelta notas.
/// </summary>
static partial class ChibiSprite
{
    /// <summary>Un tiempo de la música, en ms.</summary>
    public const int Beat = 450;

    static class Art
    {
        public static readonly string[] Fist = [".KKK.", "KSSSK", "KSsSK", ".KKK."];

        // Libro abierto visto por detrás: el canto de las hojas arriba y la tapa con una estrellita.
        public static readonly string[] Book = Stamped(Sym(
            ".KKKKKKKK",
            "KWWWWWWWK",
            "KNNNNNNNK",
            "KNNNNNNNK",
            "KNNNNNNNK",
            "KNNNNNNNK",
            "KNNNNNNNK",
            "KnnnnnnnK",
            ".KKKKKKKK"), Spark, 12, 3);
        public static readonly string[] PageLift = ["....KK", "..KKWK", "KKWWWK"];
        public static readonly string[] PageUp = ["KKKK", "KWWK", "KWWK", "KWWK"];
        public static readonly string[] PageDrop = [.. PageLift.Select(Reverse)];

        // El ojo izquierdo apretado (>), para mirar con el otro por la lupa.
        public static readonly string[] Wink = ["SSSSS", "SKKSS", "SSSKK", "SKKSS", "SSSSS", "SSSSS"];
        public static readonly string[] Lens =
        [
            "..KKKKKK..",
            ".KEEEEEEK.",
            "KEE....EEK",
            "KE......EK",
            "KE......EK",
            "KE......EK",
            "KE......EK",
            "KEE....EEK",
            ".KEEEEEEK.",
            "..KKKKKK..",
        ];
        public static readonly string[] LensGlint = ["..", ".W", "W."];

        public static readonly string[] Notebook =
        [
            "KKKKKKKKKKKK",
            "KeWeWeWeWeWK",
            "KWWWWWWWWWWK",
            "KWLLLLLLLLWK",
            "KWWWWWWWWWWK",
            "KWLLLLLLLLWK",
            "KWWWWWWWWWWK",
            "KWLLLLLLLLWK",
            "KWWWWWWWWWWK",
            "KKKKKKKKKKKK",
        ];
        // Lápiz en diagonal, escribiendo: la goma arriba a la izquierda y la punta abajo a la derecha, en
        // (7, 7); el puño va en (3, 3) y deja ver la goma y la punta.
        public static readonly string[] Pencil =
        [
            ".KK.......",
            "KMMK......",
            ".KYYK.....",
            "..KYYK....",
            "...KYYK...",
            "....KYYK..",
            ".....KSSK.",
            "......KK..",
        ];

        // La laptop vista por detrás: la tapa con una calcomanía de terminal (>_) y el borde de la base.
        public static readonly string[] Lid =
        [
            "KKKKKKKKKKKKKKKK",
            "KEEEEEEEEEEEEEEK",
            "KEEEEEEEEEEEEEEK",
            "KEEEEKKKKKKEEEEK",
            "KEEEEKvVVVKEEEEK",
            "KEEEEKVvVVKEEEEK",
            "KEEEEKvVvvKEEEEK",
            "KEEEEKKKKKKEEEEK",
            "KeeeeeeeeeeeeeeK",
            "KKKKKKKKKKKKKKKK",
        ];
        public static readonly string[] LidBase = ["KeeeeeeeeeeeeeeeeK", ".KKKKKKKKKKKKKKKK."];

        // Audífonos sobre la capucha (filas 8 a 23 del chibi): el arco por dentro del borde y una almohadilla a cada lado.
        public static readonly string[] Headphones = BuildHeadphones();

        static string[] BuildHeadphones()
        {
            var g = Blank(32, 16);
            void Band(int row, int from, int to)
            {
                for (var c = from; c <= to; c++) g[row - 8][c] = g[row - 8][31 - c] = 'P';
            }
            Band(8, 5, 15); Band(9, 4, 4); Band(10, 3, 3); Band(11, 3, 3);
            Band(12, 2, 2); Band(13, 2, 2); Band(14, 2, 2); Band(15, 1, 1);
            string[] cup = [".KKK", "KPPK", "KPpK", "KPpK", "KPpK", "KPpK", "KPPK", ".KKK"];
            for (var r = 0; r < cup.Length; r++)
                for (var c = 0; c < 4; c++)
                    if (cup[r][c] != '.') g[8 + r][c] = g[8 + r][31 - c] = cup[r][c];
            return [.. g.Select(r => new string(r))];
        }

        /// <summary>El antebrazo del lápiz: una manga horizontal desde el codo (columna 5) hasta el puño.</summary>
        public static string[] Forearm(int length) =>
            [new string('K', length), new string('O', length - 3) + "ooo", new string('O', length - 3) + "ooo", new string('K', length)];

        public static readonly string[] Globe =
        [
            ".....KKKK.....",
            "...KKBBBBKK...",
            "..KBBBBBBBBK..",
            ".KBBBBBBBBBBK.",
            ".KBBBBBBBBBBK.",
            "KBBBBBBBBBBBBK",
            "KBBBBBBBBBBBBK",
            "KBBBBBBBBBBBBK",
            "KBBBBBBBBBBBBK",
            ".KBBBBBBBBBBK.",
            ".KBBBBBBBBBBK.",
            "..KBBBBBBBBK..",
            "...KKBBBBKK...",
            ".....KKKK.....",
        ];
        // Los continentes, en una tira que da la vuelta al globo (28 de ancho).
        public static readonly string[] Land =
        [
            "............................",
            "......JJJ..........JJ.......",
            "....JJJJJJ.......JJJJJJ.....",
            "...JJJJJJJ......JJJJJJJJ....",
            "...JJJJJJ.......JJJJJJJ.....",
            "....JJJJ.........JJJJ.......",
            ".....JJ...........JJ....J...",
            "......JJ.....JJ........JJJ..",
            "......JJJ...JJJJ.......JJJJ.",
            ".......JJ...JJJJJ.......JJ..",
            "........J....JJJ............",
            ".............JJ.............",
            "............................",
            "............................",
        ];

        public static readonly string[] Cloud =
        [
            "..KKK.KKK..",
            ".KWWWKWWWK.",
            "KWWWWWWWWWK",
            "KWWWWWWWWWK",
            "KWWWWWWWWWK",
            ".KWWWWWWWK.",
            "..KKKKKKK..",
        ];
        public static readonly string[] Puff = [".K.", "KWK", ".K."];
        public static readonly string[] PuffBig = [".KK.", "KWWK", "KWWK", ".KK."];

        // Un ayudante: la mascota clásica en chiquito, con contorno.
        public static readonly string[] Helper =
        [
            ".KKKKKKK.",
            "KOOOOOOOK",
            "KOKOOOKOK",
            "KOOOOOOOK",
            ".KKKKKKK.",
            "..K.K.K..",
        ];
        public static readonly string[] HelperStep =
        [
            ".KKKKKKK.",
            "KOOOOOOOK",
            "KOKOOOKOK",
            "KOOOOOOOK",
            ".KKKKKKK.",
            ".K.K.K.K.",
        ];

        static string[] Sym(params string[] half) => [.. half.Select(r => r + Reverse(r))];

        static string[] Stamped(string[] rows, string[] shape, int x, int y)
        {
            var g = Grid(rows);
            Stamp(g, x, y, shape);
            return [.. g.Select(r => new string(r))];
        }
    }

    static void AddFlowShapes()
    {
        Palette['N'] = Hex(0x4F7FD0); // tapa del libro
        Palette['n'] = Hex(0x34589A); // tapa, sombra
        Palette['L'] = Hex(0xCFC8BE); // renglones
        Palette['E'] = Hex(0xB8C7C0); // metal
        Palette['e'] = Hex(0x7E8C86); // metal, sombra
        Palette['V'] = Hex(0x1E2A26); // pantalla
        Palette['v'] = Hex(0x3DD6A3); // texto de la terminal
        Palette['J'] = Hex(0x3FAE5A); // tierra del globo
        Palette['p'] = Hex(0x6B45C9); // audífonos, sombra
        Palette['D'] = Hex(0xB9773A); // madera del mazo
        Palette['q'] = Hex(0x8A5426); // madera, sombra ('d' es la caja por dentro)

        // Brazo hacia delante: la manga baja junto al cuerpo y el puño (en Props) toma lo que lleva.
        Arms[Arm.Front] = (1, 31,
        [
            "..KOOK....",
            ".KOOOOK...",
            ".KOOOOK...",
            "..KOOOOK..",
            "..KooooK..",
            "...KKKK...",
        ]);
        // Al teclear: como Front, con el puño de la manga una fila más arriba.
        Arms[Arm.Type] = (1, 31,
        [
            "..KOOK....",
            ".KOOOOK...",
            "..KOOOOK..",
            "..KooooK..",
            "...KKKK...",
        ]);
        // El brazo del lápiz: solo el de arriba, junto al cuerpo; el antebrazo va en Props, hasta el puño.
        Arms[Arm.Write] = (1, 31,
        [
            "..KOOK.",
            ".KOOOOK",
            ".KOOOOK",
            ".KOOOOK",
            ".KOOOOK",
            "..KKKK.",
        ]);

        EyeShapes[Eyes.Down] = ["....", "KKKK", "aWWa", "aKKa", "AKKA", ".AA."];
        EyeShapes[Eyes.DownLeft] = ["....", "KKKK", "WWaa", "KKaa", "KKAA", ".AA."];
        EyeShapes[Eyes.DownRight] = ["....", "KKKK", "aaWW", "aaKK", "AAKK", ".AA."];
        EyeShapes[Eyes.Glow] = ["....", "KKKK", "avva", "aKKa", "AKKA", ".AA."]; // mirando hacia abajo, con la pantalla reflejada
        MouthShapes[Mouth.Tongue] = ["mmmm", ".bb."]; // la lengüita afuera, concentrado
    }

    /// <summary>Lo que lleva con las dos manos: el objeto y un puño en cada costado, abajo.</summary>
    static (string[] Shape, int X, int Y)[] Holding(string[] thing, int x, int y, int fistsAt, params (string[] Shape, int X, int Y)[] extra) =>
        [(thing, x, y), .. extra, (Art.Fist, x - 2, y + fistsAt), (Art.Fist, x + thing[0].Length - 3, y + fistsAt)];

    static void AddFlowMoves(Dictionary<ChibiMove, Frame[]> moves, Func<Pose, int, Frame> F)
    {
        // Leer: los ojos van y vienen por los renglones y cada tanto da vuelta la página.
        var read = new Pose { Eyes = Eyes.DownLeft, Mouth = Mouth.Small, Crab = (0, 2), Left = Arm.Front, Right = Arm.Front, Props = Holding(Art.Book, 7, 30, 4) };
        var readRight = read with { Eyes = Eyes.DownRight, Crab = (2, 2) };
        var page = read with { Eyes = Eyes.Down, Crab = (1, 2) };
        moves[ChibiMove.Read] =
        [
            F(read, 440), F(readRight, 440), F(read, 440), F(readRight, 440),
            F(page with { Props = Holding(Art.Book, 7, 30, 4, (Art.PageLift, 18, 28)) }, 120),
            F(page with { Props = Holding(Art.Book, 7, 30, 4, (Art.PageUp, 14, 27)) }, 120),
            F(page with { Props = Holding(Art.Book, 7, 30, 4, (Art.PageDrop, 8, 28)) }, 120),
            F(page, 320), F(page with { Eyes = Eyes.Closed }, 120), F(page, 200),
        ];

        // Buscar: mira por la lupa con un ojo, apretando el otro, y los ojitos de cangrejo buscan alrededor.
        (string[], int, int)[] Glint(int k) => [(Art.Wink, 8, 19), (Art.Lens, 16, 17), (Art.LensGlint, 18 + k, 18)];
        var search = new Pose { Eyes = Eyes.LookLeft, Mouth = Mouth.Oh, Crab = (0, 1), Right = Arm.Think, Face = Glint(0) };
        moves[ChibiMove.Search] =
        [
            F(search, 440), F(search with { Crab = (0, 0) }, 320),
            F(search with { Eyes = Eyes.LookRight, Crab = (2, 1), Face = Glint(1) }, 440), F(search with { Eyes = Eyes.LookRight, Crab = (2, 0), Face = Glint(1) }, 320),
            F(search with { Eyes = Eyes.Open, Crab = (1, 0) }, 320),
            F(search with { Eyes = Eyes.Open, Crab = (1, 1), Fx = [(Spark, 26, 13)] }, 240),
        ];

        // Editar: la libreta en la mano de la derecha y el lápiz en la otra, con la punta sobre la hoja;
        // la mano sigue lo que va escribiendo, renglón por renglón.
        const int bookX = 13, bookY = 32;
        var edit = new List<Frame>();
        var scribble = "NN.NNN.N";
        (string[], int, int)[] Writing(string[] page, int tipX, int tipY) =>
        [
            (page, bookX, bookY), (Art.Fist, bookX + 9, bookY + 4),
            (Art.Forearm(tipX - 4 - 5 + 1), 5, tipY - 4), (Art.Pencil, tipX - 7, tipY - 7), (Art.Fist, tipX - 4, tipY - 4),
        ];
        for (var line = 0; line < 3; line++)
            for (var k = 2; k <= scribble.Length; k += 2)
            {
                var sheet = Grid(Art.Notebook);
                for (var l = 0; l <= line; l++)
                    for (var c = 0; c < (l < line ? scribble.Length : k); c++)
                        if (scribble[c] == 'N') sheet[3 + l * 2][2 + c] = 'N';
                edit.Add(F(new Pose
                {
                    Eyes = k % 4 == 0 ? Eyes.DownRight : Eyes.Down, Mouth = Mouth.Small, Crab = (1, 2), Left = Arm.Write, Right = Arm.Front,
                    Props = Writing([.. sheet.Select(r => new string(r))], bookX + 1 + k, bookY + 3 + line * 2),
                }, 160));
            }
        var done = Grid(Art.Notebook);
        for (var l = 0; l < 3; l++)
            for (var c = 0; c < scribble.Length; c++)
                if (scribble[c] == 'N') done[3 + l * 2][2 + c] = 'N';
        edit.Add(F(new Pose { Eyes = Eyes.Happy, Mouth = Mouth.Open, Crab = (1, 0), Left = Arm.Write, Right = Arm.Front,
            Props = Writing([.. done.Select(r => new string(r))], bookX + 1 + scribble.Length, bookY + 7) }, 320));
        moves[ChibiMove.Edit] = [.. edit];

        // Ejecutar: detrás de la laptop (se ve la tapa, con su calcomanía >_), teclea a ráfagas con la
        // pantalla reflejada en los ojos, y entre ráfaga y ráfaga se queda mirando lo que salió.
        var lid = new (string[], int, int)[] { (Art.Lid, 8, 30), (Art.LidBase, 7, 40) };
        var typing = new Pose { Eyes = Eyes.Glow, Mouth = Mouth.Small, Crab = (1, 2), Left = Arm.Type, Right = Arm.Front, Props = lid };
        var typed = typing with { Left = Arm.Front, Right = Arm.Type };
        var watch = typing with { Left = Arm.Front, Right = Arm.Front };
        moves[ChibiMove.Run] =
        [
            F(typing, 120), F(typed, 120), F(typing, 120), F(typed, 120), F(typing, 120), F(typed, 120),
            F(watch, 440), F(watch with { Crab = (0, 2) }, 280),
            F(typing, 120), F(typed, 120), F(typing, 120), F(typed, 120),
            F(watch, 360), F(watch with { Eyes = Eyes.Closed }, 120), F(watch, 200),
        ];

        // Web: el globo gira entre las manos y los ojitos de cangrejo hacen de antenas.
        var web = new List<Frame>();
        for (var i = 0; i < 8; i++)
        {
            var globe = Grid(Art.Globe);
            for (var y = 0; y < globe.Length; y++)
                for (var x = 0; x < globe[y].Length; x++)
                    if (globe[y][x] == 'B' && Art.Land[y][(x + i * 2) % 28] == 'J') globe[y][x] = 'J';
            globe[3][3] = globe[2][4] = 'W'; // brillo
            (string[], int, int)[] waves = (i % 4) switch
            {
                0 => [(WaveSL, 4, 1), (WaveS, 26, 1)],
                1 => [(WaveSL, 4, 1), (WaveS, 26, 1), (WaveML, 1, 0), (WaveM, 28, 0)],
                2 => [(WaveML, 1, 0), (WaveM, 28, 0)],
                _ => [],
            };
            web.Add(F(new Pose
            {
                Eyes = Eyes.Up, Mouth = Mouth.Oh, Crab = (1, 0), Left = Arm.Front, Right = Arm.Front,
                Props = [(globe.Select(r => new string(r)).ToArray(), 9, 28), (Art.Fist, 7, 33), (Art.Fist, 20, 33)], Fx = waves,
            }, 200));
        }
        moves[ChibiMove.Web] = [.. web];

        // Agentes: guía y observa a los ayudantes (la mascota clásica en chiquito). Señala a un lado, el
        // ayudante aparece a sus pies con un «puf», da saltitos mientras ella lo mira con la mano en el
        // mentón y se va con otro «puf»; después, del otro lado. Todo queda dentro de la ventana.
        var agent = new List<Frame>();
        foreach (var right in new[] { true, false })
        {
            var hx = right ? 26 : -3; // dónde aparece el ayudante, en casillas del chibi
            var point = new Pose
            {
                Eyes = right ? Eyes.LookRight : Eyes.LookLeft, Mouth = Mouth.Small, Crab = right ? (2, 1) : (0, 1),
                Left = right ? Arm.Down : Arm.Out, Right = right ? Arm.Out : Arm.Down,
            };
            var observe = point with { Left = Arm.Think, Right = Arm.Down, Mouth = Mouth.Oh };
            (string[], int, int) Helper(int lift) => (lift > 0 ? Art.HelperStep : Art.Helper, hx, 42 - lift);
            (string[], int, int) puff = (Build.Cloud, hx + 1, 43);
            agent.AddRange([
                F(point with { Mouth = Mouth.Open, Fx = [puff] }, 160),
                F(point with { Fx = [(SmallSteam, hx + 3, 40), Helper(0)] }, 160),
                F(point with { Fx = [Helper(2)] }, 120),
                F(point with { Fx = [Helper(0)] }, 120),
                F(observe with { Fx = [Helper(2)] }, 120),
                F(observe with { Fx = [Helper(0)] }, 160),
                F(observe with { Fx = [Helper(2)] }, 120),
                F(observe with { Fx = [Helper(0)] }, 240),
                F(point with { Fx = [puff] }, 160),
                F(point with { Left = Arm.Down, Right = Arm.Down, Eyes = Eyes.Open, Crab = (1, 1) }, 160),
            ]);
        }
        moves[ChibiMove.Agent] = [.. agent];

        // Pensar (el modelo arma el siguiente paso): mira hacia la nube y los tres puntos saltan uno tras otro.
        var ponder = new List<Frame>();
        for (var i = 0; i < 6; i++)
        {
            var cloud = Grid(Art.Cloud);
            for (var d = 0; d < 3; d++)
                for (var y = 0; y < 2; y++)
                    cloud[(i % 3 == d ? 2 : 3) + y][2 + d * 3] = cloud[(i % 3 == d ? 2 : 3) + y][3 + d * 3] = 'a';
            ponder.Add(F(new Pose
            {
                Eyes = i == 5 ? Eyes.Closed : Eyes.Up, Mouth = Mouth.Small, Crab = (2, 0), Head = i < 3 ? 0 : 1,
                Fx = [(cloud.Select(r => new string(r)).ToArray(), 25, -4), (Art.PuffBig, 26, 4), (Art.Puff, 25, 8)],
            }, 240));
        }
        moves[ChibiMove.Ponder] = [.. ponder];

        // Otra herramienta: construye una casita con el mazo (ChibiBuild).
        AddBuildMove(moves, F);
    }

    static readonly Dictionary<(ChibiMove, int, int), char[][]> WithMusic = new();

    /// <summary>
    /// Como Paint, pero con música, sin dejar de hacer lo suyo. Al ejecutar se pone audífonos y mece la
    /// cabeza de lado a lado; en lo demás cabecea al compás (la cabeza baja en los tiempos impares) y
    /// suelta una nota que sube por una esquina de arriba y luego por la otra. Al pensar, la nube ocupa
    /// la esquina derecha: las notas salen solo por la izquierda.
    /// </summary>
    public static void Paint(PixelFill fill, ChibiMove move, long t, bool music)
    {
        if (!music) { Paint(fill, move, t); return; }
        var i = FrameIndex(move, t);
        var beat = (int)(t / Beat % 4);
        if (!WithMusic.TryGetValue((move, i, beat), out var grid))
        {
            var frame = Moves[move][i];
            if (frame.Pose is not { } pose) // un cuadro armado aparte (de perfil): solo la nota
            {
                grid = [.. frame.Grid.Select(r => (char[])r.Clone())];
                var (nx, ny) = frame.Notes![beat];
                Stamp(grid, Ox + nx, Oy + ny, Note);
                WithMusic[(move, i, beat)] = grid;
                Emit(fill, grid);
                return;
            }
            (int X, int Y)[] notes = frame.Notes ?? (move == ChibiMove.Ponder ? [(0, 3), (1, 0), (0, 3), (1, 0)] : [(0, 3), (1, 0), (27, 3), (26, 0)]);
            WithMusic[(move, i, beat)] = grid = move == ChibiMove.Run
                ? Compose(pose with { Head = beat switch { 0 => -1, 2 => 1, _ => 0 }, Face = [.. pose.Face, (Art.Headphones, 0, 8)] })
                : Compose(pose with { Nod = beat % 2 == 1, Fx = [.. pose.Fx, (Note, notes[beat].X, notes[beat].Y)] });
        }
        Emit(fill, grid);
    }

    /// <summary>Baja la cabeza (filas 0 a 28) una casilla: tapa la sombra del cuello.</summary>
    static void NodHead(char[][] g)
    {
        for (var y = 28; y > 0; y--) g[y] = (char[])g[y - 1].Clone();
        g[0] = new string('.', g[0].Length).ToCharArray();
    }
}
