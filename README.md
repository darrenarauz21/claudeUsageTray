# Uso de Claude (bandeja del sistema)

Ícono en la bandeja de Windows con el uso de las cuentas de Claude. Muestra el porcentaje de la
sesión actual (5 h) de la cuenta elegida. Al hacer clic se abre una ventana con tres vistas:

- **Uso**: un botón por cuenta y, para la elegida, la sesión (5 h), la semana y la semana por
  modelo, con su barra y cuándo reinicia.
- **Sesiones**: las sesiones de Claude Code abiertas en este equipo, de todas las cuentas.
- **Actividad**: mapa de calor de los últimos 6 meses, como el de `/stats` en Claude Code.
- Al pie, los tokens de hoy en este equipo, leídos de los registros `.jsonl` de Claude Code.

Al actualizar (a mano o cada 5 minutos), la mascota de Claude ocupa el ícono y la hora del pie: abre los ojos, mira a los lados mientras llega la respuesta y hace `> <` antes de mostrar el estado nuevo.

## Sesiones

| Estado | Señal | De dónde sale |
|---|---|---|
| Trabajando | asterisco animado y barra con destello | `status: busy` en `sessions/<pid>.json` |
| Esperando su respuesta | punto ámbar que late | una pregunta (`AskUserQuestion`) sin responder |
| Terminó | marca verde, «hace 5 min · 2 min 25 s» | `status: idle` y el último turno hace menos de 30 min |
| Inactiva | punto gris, agrupadas y plegadas | `status: idle` desde hace más de 30 min |

Mientras trabaja, la sesión muestra lo que está haciendo («Ejecutando · …», «Editando · archivo»),
los pasos del turno y el tiempo transcurrido. Si la sesión usa una lista de tareas (`TodoWrite`),
la barra pasa a «3 de 5 tareas». El título es el que Claude Code genera para la sesión.

Clic en una sesión: despliega el último pedido, la carpeta y las interacciones. Clic derecho:
abrir la carpeta del proyecto o copiar el ID de la sesión. Solo se cuentan los procesos vivos.
Las sesiones se leen cada 1,5 s mientras la ventana está abierta; cerrada, no se lee nada.

## Avisos

Un aviso entra por la derecha, abajo, con la mascota de Claude según el caso. Se encienden y se
apagan con la campana de la ventana o con **Notificaciones** en el menú del ícono. Un clic en
el aviso abre la ventana en esa sesión o en esa cuenta; **Ver un aviso de ejemplo** muestra el
siguiente de cada tipo con cada clic.

| Aviso | Cuándo | Mascota | Se va solo |
|---|---|---|---|
| Terminó | la sesión cierra su turno (`turn_duration`) | celebra con la burbuja verde | a los 6 s |
| Le está preguntando | queda una `AskUserQuestion` sin responder | piensa, con burbuja de diálogo | no |
| Necesita su permiso | Claude Code pide permiso para una herramienta (hook) | mira el candado | no |
| Falló | error de la API ya sin reintentos (`isApiErrorMessage`) | mareada, con el círculo rojo | no |
| Reintentando | van 3 reintentos por error de la API | mira a los lados, con flechas | a los 6 s |
| Se cerró mientras trabajaba | el proceso terminó con la sesión trabajando | duerme | no |
| Uso al 80 % / 95 % | la sesión de 5 h o la semana cruzan ese umbral | suda / entre llamas | a los 6 s |
| Límite reiniciado | un límite que estaba en 95 % o más vuelve a bajar | celebra | a los 6 s |
| Sesión vencida | el token de una cuenta venció | burbuja «!» | no |

Con el mouse encima, ningún aviso se va. Se apilan hasta cuatro. Al arrancar o al encender las
notificaciones no se avisa de lo que ya había pasado. Con las notificaciones encendidas, las
sesiones se leen cada 3 s aunque la ventana esté cerrada; apagadas, solo con la ventana abierta.

**Permisos.** Que una sesión espere su permiso no queda en el registro: lo cuenta el hook
`Notification` de Claude Code. Para recibirlo, la configuración de cada cuenta
(`settings.json` de su carpeta) necesita este hook, que guarda el evento en
`%APPDATA%\ClaudeUsageTray\events.jsonl` y termina:

```json
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "\"C:\\ruta\\a\\ClaudeUsageTray.exe\" --hook" } ] }
    ]
  }
}
```

### Sonido

Cada aviso suena según el caso: contento si terminó, como pregunta si le pregunta, dos golpecitos
si pide permiso y triste si falló. Hay dos estilos generados por la app, **Clásico** (una vocecita)
y **Risitas** (risitas cortas y agudas), que se eligen en **Estilo de sonido** del menú del ícono;
**Sonido** lo enciende y lo apaga. Si salen varios avisos juntos, suena uno.

Los sonidos están en `%APPDATA%\ClaudeUsageTray\sonidos` (**Abrir carpeta de sonidos**), con los
nombres `terminado`, `pregunta`, `permiso`, `fallo`, `uso` y `aviso`:

- `clasico\` y `risitas\`: los de la app. Si se borra uno, se vuelve a crear.
- `propios\`: un archivo con el mismo nombre (`.wav` o `.mp3`) suena en lugar del estilo elegido; si
  hay los dos, manda el más reciente.

## Actividad

Una casilla por día; el color sube con los tokens de ese día (entrada + escritura en caché +
salida; las lecturas de caché no cuentan). Los cuatro tonos se reparten por cuartiles de los
días con actividad. Al pasar el mouse por un día se ven sus tokens y respuestas. Debajo: tokens
del período, días activos, racha actual y más larga, día más activo y modelo favorito.

Sale de los `.jsonl` de Claude Code de este equipo (no incluye claude.ai ni otros equipos, y no
se separa por cuenta cuando el historial es compartido). Como Claude Code borra los registros
de más de 30 días, la app guarda un resumen por día en `%APPDATA%\ClaudeUsageTray\history.json`
(solo cifras, sin texto de las conversaciones) y lo actualiza en cada consulta, aunque la ventana
esté cerrada.

## Cuentas

Cada cuenta es una carpeta de configuración de Claude Code (la de `CLAUDE_CONFIG_DIR`). Se
detectan solas las carpetas `~/.claude*` que tienen `.credentials.json`. Para agregar otra,
use el botón **+**, o **Agregar cuenta…** en el menú del ícono. Con clic derecho sobre una
cuenta se puede renombrar, abrir la carpeta o quitarla de la lista.

La configuración vive en `%APPDATA%\ClaudeUsageTray\settings.json`.

## Compartir

Para otros equipos, la versión autocontenida trae su propio .NET y no pide instalar nada:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist-compartir
```

Se comparte solo `dist-compartir\ClaudeUsageTray.exe` (~70 MB). Conviene guardarlo en una carpeta
fija antes de abrirlo (por ejemplo `Documentos\Uso de Claude`), porque el hook apunta a esa ruta.

## Configuración

La primera vez que se abre, la app muestra **Configuración**: el hook de avisos de permisos en el
`settings.json` de cada carpeta de Claude Code, **Iniciar con Windows** y las notificaciones. Nada
cambia sin **Aplicar**. Antes de tocar un `settings.json` se guarda un respaldo junto a él
(`settings.json.respaldo-AAAAMMDD-HHMMSS`); se respeta el resto del archivo y los otros hooks, y
si el JSON tiene errores no se toca. Nada de esto pide permisos de administrador.

Si la app cambia de lugar, al abrirla detecta que el hook apunta a la ruta vieja y vuelve a
proponerlo. **Configurar…** abre el asistente cuando se quiera y **Quitar configuración…** saca
el hook y el inicio con Windows (para antes de borrar la app).

## Compilar y ejecutar

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
.\dist\ClaudeUsageTray.exe
```

Para que arranque con Windows, marque **Iniciar con Windows** en el menú del ícono. Eso escribe
en `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Opciones:

- `--show`: abre la ventana al arrancar.
- `--preview`: como `--show`, pero aunque ya haya otra instancia abierta (para probar cambios).
- `--sessions`: abre en la vista de sesiones.
- `--setup`: con `--preview`, muestra también el asistente de configuración.
- `--activity`: abre en la vista de actividad.
- `--demo`: muestra un aviso de cada tipo.
- `--hook`: modo hook de Claude Code; lee el evento por la entrada estándar y lo guarda.
- `--diag`: escribe el diagnóstico (lo mismo que **Copiar diagnóstico** en el menú).
- `--dump`: hace una consulta, escribe el resultado en la salida estándar y termina. Sirve para
  comprobar sin abrir la bandeja: `Start-Process .\dist\ClaudeUsageTray.exe --dump -Wait -NoNewWindow -RedirectStandardOutput out.txt`.

## Si en otro equipo no salen avisos

**Copiar diagnóstico**, en el menú del ícono, copia un resumen sin correos ni tokens: versiones de
la app, Windows, .NET y Claude Code; si las notificaciones están encendidas; y cada carpeta
`~/.claude*` con sus credenciales, sesiones registradas y vivas, y si tiene el hook. Lo habitual:

- No arranca: falta el **.NET 8 Desktop Runtime**.
- «credenciales no»: la app no ve la cuenta y no lee sus sesiones.
- «sin carpeta sessions»: esa versión de Claude Code no registra el estado de las sesiones.
- «sin hook»: no habrá avisos de permisos.

## Límites conocidos

- Los porcentajes salen de `https://api.anthropic.com/api/oauth/usage`, el mismo endpoint que
  usa `/usage` en Claude Code. **No está documentado**: si cambia, la cuenta muestra el error
  y se mantienen los últimos datos buenos.
- La app lee el token de `.credentials.json` y **no lo renueva**, para no pisar la sesión de
  Claude Code. Si una cuenta no se usa en horas, su token vence y la ventana pide abrir Claude
  Code con esa cuenta.
- Los tokens locales son los de este equipo, no los de la cuenta. Si dos carpetas comparten
  `projects` (por ejemplo, con un junction), se cuentan una sola vez y no se separan por cuenta.
- Se consulta cada 5 minutos (`RefreshMinutes` en `settings.json`) y al abrir la ventana si
  pasó más de 1 minuto.
- El estado de las sesiones sale de archivos internos de Claude Code, que no están documentados:
  una versión nueva puede cambiarlos.
- Windows 11 deja los íconos nuevos en el desbordamiento (^). Para tenerlo siempre a la vista:
  Configuración > Personalización > Barra de tareas > Otros íconos de la bandeja del sistema.
