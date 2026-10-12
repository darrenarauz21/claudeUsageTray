"""Genera los paquetes de Uso de Claude para Windows, Linux y macOS en paquetes/.

Los nombres no llevan versión, para que los enlaces del README a la última Release
(releases/latest/download/<archivo>) sigan sirviendo en cada versión nueva:
UsoDeClaude-windows-x64.exe, UsoDeClaude-linux-x64.tar.gz, UsoDeClaude-macos-arm64.zip y
UsoDeClaude-macos-x64.zip.

    python herramientas/empaquetar.py            los tres sistemas
    python herramientas/empaquetar.py linux mac  solo los indicados

Solo usa la biblioteca estándar. Arma los .tar.gz y .zip a mano para conservar los permisos de
ejecución, que se pierden al comprimir desde Windows.
"""
import io
import os
import shutil
import struct
import subprocess
import sys
import tarfile
import zipfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "paquetes")
WORK = os.path.join(OUT, "_trabajo")
AVALONIA = os.path.join(ROOT, "ClaudeUsage.Avalonia", "ClaudeUsage.Avalonia.csproj")
WINDOWS = os.path.join(ROOT, "ClaudeUsageTray.csproj")
VERSION = "1.0.0"

# ── Íconos: la misma mascota que app.ico, en pixeles ─────────────────────────────────────────

BODY, INK, WHITE = (0xD9, 0x77, 0x57, 255), (0x2A, 0x1F, 0x1A, 255), (255, 255, 255, 255)
SMALL = (14, 10, [(BODY, 2, 0, 10, 7), (BODY, 0, 3, 2, 2), (BODY, 12, 3, 2, 2),
                  (BODY, 3, 7, 1, 3), (BODY, 5, 7, 1, 3), (BODY, 8, 7, 1, 3), (BODY, 10, 7, 1, 3),
                  (INK, 4, 2, 1, 2), (INK, 9, 2, 1, 2)])
LARGE = (20, 9, [(BODY, 3, 0, 14, 7), (BODY, 1, 3, 2, 2), (BODY, 17, 3, 2, 2),
                 (BODY, 4, 7, 1, 2), (BODY, 6, 7, 1, 2), (BODY, 13, 7, 1, 2), (BODY, 15, 7, 1, 2),
                 (INK, 7, 2, 1, 2), (INK, 12, 2, 1, 2), (WHITE, 9, 5, 2, 1)])


def render(size):
    columns, rows, rects = LARGE if size >= 48 else SMALL
    unit = max(1, size // columns)
    x0, y0 = (size - columns * unit) // 2, (size - rows * unit) // 2
    pixels = [[(0, 0, 0, 0)] * size for _ in range(size)]
    for color, x, y, w, h in rects:
        for py in range(y0 + y * unit, y0 + (y + h) * unit):
            for px in range(x0 + x * unit, x0 + (x + w) * unit):
                pixels[py][px] = color
    return pixels


def png(size):
    pixels = render(size)
    raw = b"".join(b"\x00" + bytes(c for p in row for c in p) for row in pixels)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def icns():
    # Tipos de ícono de macOS con contenido PNG.
    entries = [(b"icp4", 16), (b"icp5", 32), (b"icp6", 64), (b"ic07", 128), (b"ic08", 256),
               (b"ic09", 512), (b"ic10", 1024), (b"ic11", 32), (b"ic12", 64), (b"ic13", 256), (b"ic14", 512)]
    body = b"".join(kind + struct.pack(">I", 8 + len(data)) + data for kind, data in ((k, png(s)) for k, s in entries))
    return b"icns" + struct.pack(">I", 8 + len(body)) + body


# ── Compilación ──────────────────────────────────────────────────────────────────────────────

def publish(project, rid, output, single_file):
    args = ["dotnet", "publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "-o", output, "-nologo", "-v", "q"]
    if single_file:
        args += ["-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true"]
    print(f"  compilando {rid}…")
    subprocess.run(args, check=True)
    for pdb in [f for f in os.listdir(output) if f.endswith(".pdb")]:
        os.remove(os.path.join(output, pdb))


def text(name):
    """Archivo de herramientas/ con saltos de línea de Unix (los scripts fallan con CR)."""
    with open(os.path.join(ROOT, "herramientas", name), encoding="utf-8", newline="") as h:
        return h.read().replace("\r\n", "\n")


def add_tar(tar, name, data, mode):
    info = tarfile.TarInfo(name)
    info.size, info.mode = len(data), mode
    tar.addfile(info, io.BytesIO(data))


def add_zip(archive, name, data, mode):
    info = zipfile.ZipInfo(name)
    info.create_system = 3  # Unix: así se conservan los permisos
    info.external_attr = (mode | 0o100000) << 16
    info.compress_type = zipfile.ZIP_DEFLATED
    archive.writestr(info, data)


def windows():
    staging = os.path.join(WORK, "win-x64")
    shutil.rmtree(staging, ignore_errors=True)
    publish(WINDOWS, "win-x64", staging, single_file=True)
    shutil.copyfile(os.path.join(staging, "ClaudeUsageTray.exe"), os.path.join(OUT, "UsoDeClaude-windows-x64.exe"))
    print("  listo: paquetes/UsoDeClaude-windows-x64.exe")


def linux():
    staging = os.path.join(WORK, "linux-x64")
    shutil.rmtree(staging, ignore_errors=True)
    publish(AVALONIA, "linux-x64", staging, single_file=True)
    name = "UsoDeClaude-linux-x64"
    path = os.path.join(OUT, name + ".tar.gz")
    with tarfile.open(path, "w:gz") as tar:
        with open(os.path.join(staging, "UsoDeClaude"), "rb") as h:
            add_tar(tar, f"{name}/UsoDeClaude", h.read(), 0o755)
        add_tar(tar, f"{name}/instalar.sh", text("instalar-linux.sh").encode(), 0o755)
        add_tar(tar, f"{name}/uso-de-claude.png", png(256), 0o644)
        add_tar(tar, f"{name}/LEEME.txt", text("LEEME-linux.txt").encode(), 0o644)
    print(f"  listo: paquetes/{name}.tar.gz")


def mac(rid):
    staging = os.path.join(WORK, rid)
    shutil.rmtree(staging, ignore_errors=True)
    # En macOS no es un solo archivo: así codesign firma cada biblioteca dentro del .app.
    publish(AVALONIA, rid, staging, single_file=False)
    app = "Uso de Claude.app"
    name = f"UsoDeClaude-macos-{rid.split('-')[1]}"
    plist = text("Info.plist").replace("{VERSION}", VERSION)
    path = os.path.join(OUT, name + ".zip")
    with zipfile.ZipFile(path, "w") as archive:
        for folder, _, files in os.walk(staging):
            for file in files:
                full = os.path.join(folder, file)
                rel = os.path.relpath(full, staging).replace("\\", "/")
                with open(full, "rb") as h:
                    data = h.read()
                executable = file in ("UsoDeClaude", "createdump") or data[:4] in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe")
                add_zip(archive, f"{name}/{app}/Contents/MacOS/{rel}", data, 0o755 if executable else 0o644)
        add_zip(archive, f"{name}/{app}/Contents/Info.plist", plist.encode(), 0o644)
        add_zip(archive, f"{name}/{app}/Contents/Resources/app.icns", icns(), 0o644)
        add_zip(archive, f"{name}/preparar-mac.sh", text("preparar-mac.sh").encode(), 0o755)
        add_zip(archive, f"{name}/LEEME.txt", text("LEEME-mac.txt").encode(), 0o644)
    print(f"  listo: paquetes/{name}.zip")


if __name__ == "__main__":
    wanted = set(sys.argv[1:]) or {"windows", "linux", "mac"}
    os.makedirs(OUT, exist_ok=True)
    if "windows" in wanted:
        print("Windows"); windows()
    if "linux" in wanted:
        print("Linux"); linux()
    if "mac" in wanted:
        print("macOS"); mac("osx-arm64"); mac("osx-x64")
    shutil.rmtree(WORK, ignore_errors=True)
