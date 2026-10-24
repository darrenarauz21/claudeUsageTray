# Uso de Claude: detalles técnicos

Lo que el [README](README.md) no cuenta: de dónde sale cada dato, dónde se guarda cada cosa, cómo
se compila y cuáles son los límites conocidos.

## Estructura

```text
Core/                    núcleo común, sin interfaz: uso, sesiones, actividad, avisos, sonidos,
                         hook, credenciales, inicio con el sistema y diagnóstico
*.cs + ClaudeUsageTray.csproj    app de Windows (WinForms)
ClaudeUsage.Avalonia/    app de Linux y macOS (Avalonia 11); también corre en Windows para probar
herramientas/            empaquetado (empaquetar.py), scripts de instalación y Info.plist de macOS
app.ico                  ícono de la mascota
```

Las dos apps compilan los mismos archivos de `Core/`; solo la interfaz es propia de cada una.

## Dónde se guarda cada cosa

| Qué | Windows | Linux y macOS |
|---|---|---|
| Configuración de la app | `%APPDATA%\ClaudeUsageTray\settings.json` | `~/.config/ClaudeUsageTray/settings.json` |
| Historial diario (actividad) | `…\ClaudeUsageTray\history.json` | `…/ClaudeUsageTray/history.json` |
| Eventos del hook | `…\ClaudeUsageTray\events.jsonl` | `…/ClaudeUsageTray/events.jsonl` |
| Sonidos | `…\ClaudeUsageTray\sonidos` | `…/ClaudeUsageTray/sonidos` |
| Modelo de voz (Whisper) | `…\ClaudeUsageTray\voz\ggml-small.bin` | — |
| Consultas del asistente (prompts de sistema) | `…\ClaudeUsageTray\asistente` | — |
| Credenciales de Claude Code (solo se leen) | `.credentials.json` de cada carpeta | Linux: igual; macOS: el Llavero |
| Iniciar con el sistema | registro, `HKCU\…\Run` | Linux: `~/.config/autostart/uso-de-claude.desktop`; macOS: `~/Library/LaunchAgents/local.usodeclaude.plist` |

En macOS, Claude Code guarda las credenciales en el Llavero: «Claude Code-credentials» para
`~/.claude` y, desde Claude Code 2.1.144, «Claude Code-credentials-» seguido de los 8 primeros
caracteres del SHA-256 de la ruta para cada `CLAUDE_CONFIG_DIR`. La app las lee con
`security find-generic-password`; macOS puede pedir permiso la primera vez.

## Uso

Un botón por cuenta y, para la elegida, la sesión (5 h), la semana y la semana por modelo, con su
barra y cuándo reinicia. Al pie, los tokens de hoy en este equipo. Al actualizar (a mano o cada
5 minutos), la animación de lo elegido en **Ajustes › Apariencia › Mascota** ocupa el ícono y la
hora del pie. La de siempre es la mascota clásica: abre los ojos, mira a los lados mientras llega la respuesta y hace `> <`
antes de mostrar el estado nuevo. Con la mascota chibi, lo mismo lo hace su cabeza, que termina con
`> <` y boca de gatito (`:3`).

## Apariencia

En **Ajustes**, con el mismo patrón que el estilo de sonido: una lista, la elegida marcada y una
muestra al elegir.

- **Color:** paletas fijas, cada una con su tono para el modo claro y el oscuro (el modo sigue al
  sistema): Claude (`#D97757`, la predeterminada), Océano, Bosque, Lavanda, Rosa y Grafito. Cambia
  el acento: barras, interruptores, enlaces, el número del ícono y la mascota del ícono, del pie y
  de los avisos. Fondos, textos y colores de estado (peligro, aviso, éxito) no cambian. Están en
  `Theme.Palettes` (`Core/Theme.cs`).
- **Mascota:** junta la mascota y la animación al actualizar en una sola lista (`Mascots`, en
  `Core/ChibiSprite.cs`). Clásica (de un color, el acento) o Chibi (ver abajo): esa mascota anima el
  ícono y sale en los avisos. Asterisco (el ✻ de Claude Code, que crece y se encoge), Puntos (una ola
  que termina en sonrisa) o Ninguna (el ícono sigue con el porcentaje y el pie dice «Actualizando…»):
  ese es el estilo al actualizar y los avisos llevan la clásica. Todos usan los mismos tiempos de
  `RefreshAnimator`; los dibujos están en `RefreshSprite` (`Core/Sprites.cs`). Al elegir uno, la app
  actualiza para mostrarlo.
- **Mascota en el escritorio:** la mascota (el chibi o la clásica) en su propia ventana; se enciende
  sola al elegir el chibi.
- **Hablar:** con la mascota en el escritorio, cuenta cada aviso en una nubecita, a su manera
  (`MascotLines`: de tú y con caritas), además del aviso de siempre.

Se guardan en `Palette`, `Animation` (el estilo al actualizar), `Mascot` (la de los avisos),
`DesktopMascot` y `Speak` del `settings.json` de la app.

## Chibi

Claude con pelo naranja y hoodie de cangrejo (ojitos sobre antenas), en pixeles y con colores
propios: no cambia con la paleta. Está en `ChibiSprite` (`Core/ChibiSprite.cs`): el cuerpo se
escribe como su mitad izquierda, una letra por color, y se espeja; cada cuadro cambia ojos, boca,
ojitos de cangrejo y brazos, mueve la cabeza o el cuerpo y agrega efectos. El lienzo es de 40 × 52
casillas. La cabeza, para la bandeja y el pie, es la del cuerpo (32 × 32); en íconos de menos de
32 px (la bandeja de Windows al 100 o 150 %) va una de 16 × 16, que se ve nítida.

| Movimiento | Cuándo |
|---|---|
| Quieta (parpadea y mira) | hay sesiones, ninguna trabaja |
| Bailar | alguna sesión trabaja |
| Pensar (manito en la boca) | alguna espera su respuesta; en los avisos de pregunta, permiso, reintento, uso alto y sesión vencida |
| Dormida | no hay sesiones; aviso de sesión cerrada |
| Festejar | terminó |
| Chachachá con maracas | se renovó un límite (sesión o semana) |
| Mareada | falló; uso crítico |
| Saludar | al aparecer y con un clic |
| Puchero | 3 a 5 clics seguidos |
| Brazos cruzados («¡hmph!») | 6 a 8 clics seguidos |
| Furia (roja, brazos en Λ, humo y 💢) | 9 clics seguidos o más; se le pasa a los 4 s sin clics |
| Se calma (suspira y mira de reojo a los lados, buscando quién fue) | al pasársele el puchero, los brazos cruzados o la furia; después vuelve a lo que corresponda |

Con un aviso, la mascota del escritorio hace el mismo gesto del aviso unos segundos y después vuelve
a lo que corresponda.

En los avisos asoma desde abajo (de la cabeza al ruedo). En el escritorio vive en una ventana sin
marco ni fondo, siempre encima, que no quita el foco: se arrastra para moverla, sin salirse de la
pantalla (la posición queda en `DesktopMascotX` y `DesktopMascotY`), y el clic derecho la abre o la
oculta. `ChibiDirector` decide qué hace según las sesiones, que la app sigue leyendo mientras la
mascota esté a la vista aunque las notificaciones estén apagadas. La mascota clásica también puede
estar en el escritorio, con los mismos movimientos armados con sus dibujos de los avisos
(`ToastSprite.PaintMove`). La nubecita de «Hablar» es de pixeles (`BubbleSprite`), va arriba de la
mascota (o abajo, si no cabe), la sigue al arrastrarla y se va a los 8 s; un clic abre lo mismo que
el aviso.

## Sesiones

| Estado | Señal | De dónde sale |
|---|---|---|
| Trabajando | asterisco animado y barra con destello | `status: busy` en `sessions/<pid>.json` |
| Esperando su respuesta | punto ámbar que late | una pregunta (`AskUserQuestion`) sin responder |
| Falló | cruz roja y el texto del error | un error de la API (`isApiErrorMessage`) después del último pedido |
| Terminó | marca verde, «hace 5 min · 2 min 25 s» | `status: idle` y el último turno hace menos de 30 min |
| Inactiva | punto gris, agrupadas y plegadas | `status: idle` desde hace más de 30 min |

Mientras trabaja, la sesión muestra lo que está haciendo, los pasos del turno y el tiempo. Con
lista de tareas (`TodoWrite`), la barra pasa a «3 de 5 tareas». Clic: despliega el último pedido,
la carpeta, el modelo y las interacciones. Clic derecho: nueva sesión en esa carpeta, abrir la
carpeta o copiar el ID. Solo se cuentan los procesos vivos: `procStart` (no documentado) se compara
con la hora de inicio del proceso en los formatos conocidos y, si el formato no se reconoce, basta
con que el proceso exista.

La línea «Modelo» sale de la última respuesta del `.jsonl`: `message.model`, `effort` y
`advisorModel` (no documentados). Un `/model` o `/effort` se ve recién en la respuesta siguiente.

## Vistas fijas y ajustes

La cabecera muestra siempre 3 vistas fijas (por defecto Uso, Sesiones y Actividad). El botón de la
tachuela abre la lista de todas: clic en el nombre la abre sin fijarla (el botón muestra su nombre
si cabe); el pin de una vista libre la prepara y el pin de una fija hace el cambio en ese lugar,
así nunca hay más ni menos de 3. Se guardan en `PinnedViews` del `settings.json` de la app. El
engranaje del pie abre **Ajustes**, en cinco pestañas: Avisos (notificaciones y sonido), Apariencia,
Asistente (chat, voz y teclas), Isla y Sistema (cuentas, inicio con el sistema, hook y diagnóstico);
la última abierta queda en `SettingsTab`. El menú del ícono queda en abrir, actualizar, nueva
sesión, ajustes y salir.

## Flujos

Un carril por sesión activa: «Pedido», «+N» con lo anterior y los últimos grupos de pasos del
turno (los seguidos de la misma herramienta se juntan: «Leyendo ×3»). Mientras la sesión trabaja
siempre hay algo animado: si el último paso sigue en curso, late; si ya volvió (leer o buscar duran
milisegundos), aparece al final «Pensando…» con el asterisco que gira. Un punto viaja hacia el
último nodo, cada paso nuevo entra creciendo y el ícono de actualizar gira mientras actualiza; en
ámbar si la sesión le espera. Debajo, el último subagente y su estado. Clic en un carril: el mapa
de esa sesión, con la cuenta, la sesión, cuántas veces usó cada familia de herramientas en el turno
(línea animada hacia la herramienta en uso, o de vuelta a la sesión mientras piensa), el subagente
y los últimos 5 pasos con su hora. Sale del mismo `.jsonl` que la vista Sesiones: cada `tool_use`
es un paso y su `tool_result` lo cierra; del subagente se toman `agentType` y `totalToolUseCount`
del resultado. La lógica común está en `Core/Flows.cs`.

## Nueva sesión

Desde el menú del ícono o el de una sesión. Abre una terminal en la carpeta elegida, fija
`CLAUDE_CONFIG_DIR` de la cuenta (o lo quita para `~/.claude`) y ejecuta
`claude --model … --effort … --advisor …`:

- **Windows:** Windows Terminal, o PowerShell si no está.
- **macOS:** Terminal, con `osascript` (`do script`), en el shell de inicio de la persona.
- **Linux:** la primera instalada entre gnome-terminal, konsole, xfce4-terminal, kitty, alacritty,
  wezterm, tilix, xterm y x-terminal-emulator, con `bash -lc` (para que `claude` esté en el PATH);
  al salir de Claude Code queda abierto el shell de la persona.

Esas opciones valen solo para esa sesión: no tocan el `settings.json` de la cuenta.
«Predeterminado» omite la opción; el asesor «Ninguno» se apaga con
`CLAUDE_CODE_DISABLE_ADVISOR_TOOL=1`, porque `--advisor` no acepta `off`. `--advisor` no aparece
en `claude --help`; se comprobó con Claude Code 2.1.296. Lo último elegido se guarda en
`settings.json` de la app (`Launch`). Los scripts están en `Core/Launcher.cs`.

## Asistente: chat, isla, voz y teclas (Windows)

Inspirado en [coucou](https://github.com/Louis-CFM/coucou), pero con Claude Code. Todo viene
apagado: `Chat`, `Island` y `Mic` en el `settings.json` de la app.

**Cómo responde.** Cada pregunta corre `claude -p` con la cuenta elegida (`AssistantAccount`; si no,
la de Uso) en `…\ClaudeUsageTray\asistente`, con `--safe-mode` (sin el CLAUDE.md de la persona,
hooks ni MCP: la entrada baja de unos 7.400 a 1.000 tokens), `--no-session-persistence` y
`--output-format stream-json`, para mostrar la respuesta mientras llega. La pregunta va por la
entrada estándar y el prompt de sistema en un archivo. El chat no tiene herramientas; el análisis de
un archivo solo puede leer su carpeta (`Read`, `Glob`, `Grep`). La mascota usa `MascotModel` (Haiku,
de entrada) y se le pide responder en 220 caracteres como mucho; la isla, `IslandModel` (vacío: el
modelo de sus sesiones). Si la respuesta empieza con `[TAREA]`, se ofrece abrir una sesión de Claude Code con ese
pedido en `TaskFolder`. Las consultas dejan un `sessions/<pid>.json` mientras corren; la vista
Sesiones las ignora por su carpeta. El código está en `Core/Assistant.cs`.

**La isla** (`IslandForm.cs`) es una ventana siempre oscura, con el acento de la paleta. Oculta, una
franja invisible de 320 × 6 px en el centro del borde de arriba la hace asomar compacta (300 × 36);
con el cursor encima (o un clic, según `IslandHover`) se abre con Inicio (las sesiones y sus pasos,
los cambios por archivo con su diff, ir a la terminal), Chat y «+ Sesión». Se pliega al irse el
cursor, salvo si está fijada, se está escribiendo, hay una pregunta en curso o acaba de llegar una
respuesta: esa queda a la vista de 5 a 15 s, según su largo. Con `IslandWhileBusy` queda compacta a
la vista mientras alguna sesión trabaja; con `WeeklyRecap`, los lunes muestra el resumen de la
semana.

**Permisos y preguntas en la isla** (opcional, en **Ajustes › Isla**). Agrega a Claude Code el hook
`PermissionRequest`, con un respaldo `settings.json.respaldo-<fecha>` del archivo anterior:

```json
{ "hooks": { "PermissionRequest": [ { "hooks": [ { "type": "command", "command": "\"<ruta de la app>\" --hook --permiso", "timeout": 120 } ] } ] } }
```

El hook le pasa el pedido a la app por un pipe con nombre (solo del usuario) y espera hasta 110 s.
La respuesta es la documentada: `hookSpecificOutput.decision.behavior` `allow` o `deny` y, para una
`AskUserQuestion`, `allow` con `updatedInput` y sus respuestas. Si la app está cerrada o nadie
responde a tiempo, el hook no escribe nada y Claude Code pregunta en la terminal como siempre
(`Core/Approvals.cs`).

**Un archivo soltado** sobre la mascota: lo atrapa en una caja, camina hacia la isla según dónde
esté, la lanza (`BoxFlight.cs`) y la isla muestra cómo lo abre, lo lee y lo resume, con «Preguntar
algo más», «Copiar» y «Seguir en una sesión». La mascota cuenta lo esencial en su nubecita y vuelve
a su lugar. Sin la isla, no camina: piensa con la caja en las manos. Se apaga con
`AnalyzeDrops`.

![La isla escuchando](docs/capturas/isla-escucha.png)

**Voz** (`Voice.cs`). El micrófono se graba a 16 kHz y se pasa a texto en el equipo con
[Whisper.net](https://github.com/sandrohanea/whisper.net) 1.9.1 y el modelo `ggml-small`
multilingüe (488 MB, de `huggingface.co/ggerganov/whisper.cpp`). Se descarga una vez, al encender el
micrófono; si se corta, se retoma, y solo se usa si su SHA-256 coincide. La ventana del codificador
se ajusta al largo de lo dicho (`WithAudioContextSize`) en vez de los 30 s de siempre: una frase de
3 s pasa a texto en menos de 1 s (antes, unos 5 s), con el mismo acierto (medido con 4 frases). Mientras
se habla, cada 0,7 s de audio nuevo se muestra lo entendido hasta ahí. El modelo cargado ocupa unos
850 MB de memoria y se suelta a los 3 minutos sin usarlo. Con «Leer las respuestas en voz alta», las
lee una voz de Windows en español.

| Para hablar | Qué pasa |
|---|---|
| Mantener la tecla (Ctrl derecho, `MicKey`) | escucha mientras está pulsada; si se pulsa otra tecla con ella, era un atajo y se cancela |
| Tocarla dos veces, o el micrófono de la isla | escucha sin mantenerla; envía con otro toque o al callar 1,8 s; si en 8 s no se oye nada, termina («no te entendí») |
| Esc | cancela |

**Teclas** (`Hotkeys.cs`). Con el chat o la isla encendidos:

| Tecla | Qué hace |
|---|---|
| Ctrl+Alt+Espacio (`ChatKey`) | abre el chat (en la isla, si está encendida; si no, en la nubecita) |
| Ctrl+Alt+A | va al permiso o la pregunta pendiente |
| Ctrl+Alt+T | trae la terminal de la sesión que trabaja o espera |
| Ctrl+Alt+N | abre o pliega la isla |
| Ctrl+Alt+S | silencia o vuelve a sonar |
| Tecla Copilot (`CopilotKey`) | con el micrófono: un toque escucha (envía al callar o con otro toque), mantenerla es como la tecla para hablar; sin micrófono, abre el chat |

Los Ctrl+Alt son atajos registrados (`RegisterHotKey`); si otra app ya tiene uno, la lista de
**Atajos de teclado** lo marca «en uso». La tecla Copilot llega a Windows como **Win+Mayús+F23**; la
app la toma con un gancho de teclado de bajo nivel (`KeyHook`) para que Copilot no se abra y, al
soltar Win, «toca» una tecla sin uso (0xE8, como AutoHotkey) para que no se abra Inicio. Solo
funciona con la app abierta: la opción de Windows para cambiar lo que abre esa tecla solo acepta
apps de la Store o empaquetadas en MSIX firmado. **Grabar…** (en **Ajustes › Asistente › Teclas**)
espera la tecla o la combinación con todas las teclas tomadas, para que Win o Copilot no hagan lo
suyo. Las que escriben solo valen con Ctrl, Alt o Win; solas valen F13 a F24, Pausa, Bloq Despl y
las de navegador y aplicaciones de los teclados multimedia, y para hablar también Ctrl, Alt o Mayús
derechos. Una tecla para
hablar grabada la toma la app como la Copilot: un toque escucha sin mantenerla.

![Ajustes › Asistente, con la sección Teclas](docs/capturas/ajustes-teclas.png)
![Grabar una tecla](docs/capturas/grabar-tecla.png)

## Avisos

| Aviso | Cuándo | Se va solo |
|---|---|---|
| Terminó | la sesión cierra su turno (`turn_duration`) | a los 6 s |
| Le está preguntando | queda una `AskUserQuestion` sin responder | no |
| Necesita su permiso | Claude Code pide permiso para una herramienta (hook) | no |
| Falló | error de la API ya sin reintentos | no |
| Reintentando | van 3 reintentos por error de la API | a los 6 s |
| Se cerró mientras trabajaba | el proceso terminó con la sesión trabajando | no |
| Uso al 80 % / 95 % | la sesión de 5 h o la semana cruzan ese umbral | a los 6 s |
| Límite reiniciado | un límite que estaba en 95 % o más vuelve a bajar | a los 6 s |
| Sesión vencida | el token de una cuenta venció | no |

Salen abajo a la derecha (en macOS, arriba, bajo la barra de menús), hasta cuatro a la vez. Con
el mouse encima no se van. Al arrancar no se avisa de lo que ya había pasado. Con las
notificaciones encendidas, las sesiones se leen cada 3 s aunque la ventana esté cerrada.

**Permisos.** Que una sesión espere permiso no queda en el registro: lo cuenta el hook
`Notification` de Claude Code, que el asistente agrega al `settings.json` de cada carpeta (con
respaldo) y que solo guarda el evento y termina:

```json
{ "hooks": { "Notification": [ { "hooks": [ { "type": "command", "command": "\"<ruta de la app>\" --hook" } ] } ] } }
```

## Sonido

Tres estilos generados por la app, **Clásico**, **Risitas** y **Chibi** (un «¡oi!», con la «o» que
se desliza a «i», y un «¡uwaaa!» si algo falla; voz femenina adulta sintetizada, como una voz de
lectura en japonés), con una variante por caso. Carpetas `clasico/`,
`risitas/` y `chibi/` (se regeneran si se borran o si cambia la versión del estilo) y `propios/`,
donde un `.wav` o `.mp3` con el mismo nombre (`terminado`, `pregunta`, `permiso`, `fallo`, `uso`,
`aviso`) manda sobre el estilo. Se reproducen con winmm en Windows, `afplay` en macOS y `paplay`,
`pw-play` o `aplay` en Linux.

## Actividad

Una casilla por día de los últimos 6 meses; el color sube con los tokens (entrada + escritura en
caché + salida). Sale de los `.jsonl` de Claude Code de este equipo. Como Claude Code borra los
registros de más de 30 días, la app guarda un resumen diario (solo cifras) en `history.json`.

## Compilar

Requiere el SDK de .NET 8.

```powershell
dotnet build ClaudeUsageTray.csproj -c Release           # Windows (WinForms)
dotnet build ClaudeUsage.Avalonia -c Release            # Linux y macOS (corre también en Windows)
python herramientas/empaquetar.py                       # los paquetes de los tres sistemas en paquetes/
```

`empaquetar.py` (solo biblioteca estándar) genera en `paquetes/` `UsoDeClaude-windows-x64.exe`,
`UsoDeClaude-linux-x64.tar.gz` y `UsoDeClaude-macos-{arm64,x64}.zip`, con los permisos de ejecución
y el ícono de cada sistema. Todos son autocontenidos: no piden instalar .NET. Los nombres no llevan
versión porque el README enlaza a `releases/latest/download/<archivo>`: en cada versión se crea una
Release en GitHub (etiqueta `vX.Y.Z`) y se adjuntan esos cuatro archivos con el mismo nombre.
En Linux la app lleva su propia ICU (no todas las distribuciones la traen). En macOS no se puede
firmar desde Windows: `preparar-mac.sh` la firma solo para ese equipo (`codesign --sign -`) y le
quita la cuarentena.

Opciones de línea de comandos (las dos apps):

- `--show`: abre la ventana al arrancar; `--preview`: igual, aunque haya otra instancia.
- `--sessions`, `--activity`: abre en esa vista. `--setup`: con `--preview`, muestra el asistente.
- `--demo`: un aviso de cada tipo.
- `--hook`: modo hook de Claude Code; lee el evento por la entrada estándar y lo guarda.
  `--hook --permiso`: el hook de permisos y preguntas de la isla (Windows).
- `--diag`: el diagnóstico (lo mismo que **Copiar diagnóstico**). `--dump`: una consulta completa.

Solo en Windows, para probar el asistente:

- `--settings`: abre en **Ajustes**, en la última pestaña usada.
- `--soltar <archivo>`: como si se soltara ese archivo sobre la mascota (la caja, la isla y el resumen).
- `--hablar <archivo.wav>`: la voz entera con ese audio (16 kHz, mono, 16 bits) en vez del
  micrófono, como si se mantuviera la tecla mientras suena. Necesita «Micrófono» encendido.

## Si no salen avisos

**Copiar diagnóstico**, en **Ajustes**, copia un resumen sin correos ni tokens: versiones,
si las notificaciones están encendidas y cada carpeta `~/.claude*` con sus credenciales, sesiones
registradas y vivas, y el hook. Lo habitual:

- «credenciales no»: la app no ve la cuenta y no lee sus sesiones.
- «sin carpeta sessions»: esa versión de Claude Code no registra el estado de las sesiones.
- «sin hook»: no habrá avisos de permisos.
- Solo Claude Desktop o claude.ai, sin Claude Code: la app no tiene de dónde leer.

## Límites conocidos

- Los porcentajes salen de `https://api.anthropic.com/api/oauth/usage`, el mismo endpoint que usa
  `/usage` en Claude Code. **No está documentado**: si cambia, la cuenta muestra el error y se
  mantienen los últimos datos buenos.
- La app **no renueva** el token, para no pisar la sesión de Claude Code: si una cuenta no se usa
  en horas, su token vence y la ventana pide abrir Claude Code con esa cuenta.
- El estado de las sesiones sale de archivos internos de Claude Code, que no están documentados.
- Linux: Avalonia 11 necesita `libICE`, `libSM` y `fontconfig` (vienen en los escritorios
  habituales). En GNOME, el ícono de la bandeja necesita la extensión AppIndicator.
- Windows 11 deja los íconos nuevos en el desbordamiento (^).
- El chat, la isla, la voz y las teclas son solo de Windows por ahora; Linux y macOS, en una etapa
  siguiente.
- Con ruido de fondo, Whisper a veces «oye» frases que nadie dijo; las conocidas («subtítulos
  realizados por…») se descartan.
- La tecla Copilot se probó simulándola (Win+Mayús+F23); en algunos equipos, como los Surface, puede
  comportarse distinto.

## Comprobar en macOS

Lo que no se pudo probar sin una Mac. Para quien la pruebe por primera vez:

1. `sh preparar-mac.sh` termina sin errores y la app aparece en la barra de menús (no en el Dock).
2. El número del ícono coincide con `/usage` de Claude Code. Si sale «Sin credenciales», revisar
   en **Acceso a Llaveros** que exista «Claude Code-credentials» (o «…-credentials-xxxxxxxx» si se
   usa `CLAUDE_CONFIG_DIR`) y responder **Permitir siempre** cuando macOS lo pregunte.
3. **Sesiones** muestra las sesiones abiertas de Claude Code y su estado.
4. **Ajustes › Ver un aviso de ejemplo** muestra el aviso arriba a la derecha y suena.
5. **Ajustes › Configurar…** agrega el hook; al pedir permiso una sesión nueva, sale «Necesita su permiso».
6. **Iniciar con el sistema** crea `~/Library/LaunchAgents/local.usodeclaude.plist`.
7. **Ajustes › Copiar diagnóstico**: pegar el resultado si algo falla.
