# Uso de Claude

Una pequeña app que vive junto al reloj (en la bandeja de Windows o Linux, o en la barra de menús
de Mac) y le muestra, de un vistazo, cuánto le queda de Claude y qué están haciendo sus sesiones
de Claude Code.

- **Uso:** el porcentaje de su sesión de 5 horas y de la semana, por cuenta, y cuándo se reinicia.
- **Sesiones:** qué sesiones de Claude Code tiene abiertas y si están trabajando, esperando su
  respuesta o ya terminaron.
- **Actividad:** un calendario de los últimos 6 meses con cuánto usó Claude cada día.
- **Avisos:** un mensajito con la mascota de Claude cuando una sesión termina, le pregunta algo,
  necesita permiso o falla, y cuando está cerca del límite. Con sonido, que se puede cambiar.

Necesita tener **Claude Code** instalado y con la sesión iniciada: de ahí lee todo. No guarda
contraseñas ni el texto de sus conversaciones.

## Instalar

Descargue el archivo de su sistema desde la carpeta de paquetes y siga los pasos.

### Windows

1. Guarde `ClaudeUsageTray.exe` en una carpeta fija, por ejemplo `Documentos\Uso de Claude`.
2. Ábralo con doble clic.
3. Busque el ícono naranja junto al reloj. Si no aparece, está en la flechita **^**.

### macOS

1. Descomprima `UsoDeClaude-macos-arm64-<versión>.zip` (Mac con chip Apple: M1, M2, M3…) o
   `UsoDeClaude-macos-x64-<versión>.zip` (Mac con Intel).
2. Abra la app **Terminal**, escriba `sh ` (con un espacio), arrastre el archivo
   `preparar-mac.sh` a la ventana y pulse Enter.
3. La app queda en **Aplicaciones** y se abre sola: búsquela arriba a la derecha, en la barra de
   menús.

El paso 2 se hace una sola vez: la app no está firmada por Apple, y el script la autoriza solo
para su Mac. Si macOS pregunta si permite leer «Claude Code-credentials», elija **Permitir
siempre**.

### Linux

1. Descomprima el archivo: `tar -xzf UsoDeClaude-linux-x64-<versión>.tar.gz`
2. Entre en la carpeta y ejecute: `sh instalar.sh`
3. Busque el ícono en la bandeja. También queda en el menú de aplicaciones.

Si falta algo en su sistema, el instalador le dice qué y cómo instalarlo. En GNOME, el ícono de
la bandeja necesita la extensión «AppIndicator» (en Ubuntu ya viene activada).

## La primera vez

Al abrirla, la app le pregunta qué configurar. Puede aceptar todo con **Aplicar**:

- **Avisos de permisos:** para avisarle cuando Claude Code espere su permiso. Toca la
  configuración de Claude Code, pero antes guarda una copia.
- **Iniciar con el sistema:** para que se abra sola al encender el equipo.
- **Mostrar notificaciones.**

## Cómo se usa

- **Clic en el ícono:** abre la ventana. Arriba elige **Uso**, **Sesiones** o **Actividad**.
- **El número del ícono** es el porcentaje de su sesión de 5 horas.
- **Clic en un aviso:** abre la sesión o la cuenta de la que habla.
- **Menú del ícono** (clic derecho; en Mac, clic): actualizar, agregar otra cuenta, encender o
  apagar notificaciones y sonido, elegir el estilo de sonido, ver un aviso de ejemplo y salir.

## Si algo no funciona

- **No salen avisos:** use **Copiar diagnóstico** en el menú del ícono y compártalo con quien le
  pasó la app.
- **Dice «Sesión vencida»:** abra Claude Code con esa cuenta y se arregla sola.
- **Para quitarla:** use **Quitar configuración…** en el menú y después borre la app.

Los detalles técnicos (de dónde sale cada dato, cómo compilarla y empaquetarla) están en
[DETALLES.md](DETALLES.md).
