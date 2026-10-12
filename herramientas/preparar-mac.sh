#!/bin/sh
# Prepara Uso de Claude en macOS: la copia a Aplicaciones, le da permiso de ejecución, la firma
# solo para este equipo (sin cuenta de Apple) y le quita la cuarentena. Se ejecuta una vez:
#     sh preparar-mac.sh
set -e
cd "$(dirname "$0")"

APP="Uso de Claude.app"
DEST="/Applications"
[ -w "$DEST" ] || DEST="$HOME/Applications"
mkdir -p "$DEST"

pkill -f "$APP/Contents/MacOS/UsoDeClaude" 2>/dev/null || true
rm -rf "$DEST/$APP"
cp -R "$APP" "$DEST/"
chmod +x "$DEST/$APP/Contents/MacOS/UsoDeClaude"
xattr -dr com.apple.quarantine "$DEST/$APP" 2>/dev/null || true
codesign --force --deep --sign - "$DEST/$APP"

echo "Uso de Claude quedó en $DEST. La abro…"
open "$DEST/$APP"
echo "Búsquela en la barra de menús, arriba a la derecha."
