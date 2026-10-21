using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using SD = System.Drawing;

namespace ClaudeUsageTray;

/// <summary>Selector de vistas con pin y vista «Ajustes» (igual que en Windows).</summary>
sealed partial class UsageView
{
    static readonly View[] Selectable = [View.Usage, View.Sessions, View.Activity, View.Flows];

    /// <summary>Lo que muestra «Ajustes»; lo manda TrayController cada vez que cambia algo.</summary>
    public sealed record SettingsState(bool Notifications, bool Sound, string SoundPack, string Palette, string Animation, bool StartWithSystem, int Accounts);

    readonly List<View> _pinned = [View.Usage, View.Sessions, View.Activity];
    bool _viewMenuOpen;
    /// <summary>Vista que se quiere fijar: el próximo pin de una fija la reemplaza.</summary>
    View? _replacing;
    View _beforeSettings = View.Usage;
    Rect _selectorBounds;
    SettingsState _settingsState = new(true, true, "", "", "", false, 0);
    string _soundPackId = "", _paletteId = "", _animationId = "";

    static string ViewLabel(View view) => view switch
    {
        View.Usage => "Uso",
        View.Sessions => "Sesiones",
        View.Activity => "Actividad",
        View.Flows => "Flujos",
        _ => "Ajustes",
    };

    /// <summary>Tres vistas distintas y elegibles; si no, las de siempre.</summary>
    public void SetPinned(IEnumerable<string> names)
    {
        var views = names.Select(n => Enum.TryParse<View>(n, out var v) ? v : (View?)null).ToList();
        var valid = views.Count == 3 && views.All(v => v is { } x && Selectable.Contains(x)) && views.Distinct().Count() == 3;
        _pinned.Clear();
        _pinned.AddRange(valid ? views.Select(v => v!.Value) : [View.Usage, View.Sessions, View.Activity]);
        Relayout();
    }

    /// <summary>Los Id marcan la opción elegida en cada lista.</summary>
    public void SetSettingsState(SettingsState state, string soundPackId, string paletteId, string animationId)
    {
        _settingsState = state;
        _soundPackId = soundPackId;
        _paletteId = paletteId;
        _animationId = animationId;
        if (_view == View.Settings) InvalidateVisual();
    }

    /// <summary>Cierra el selector si está abierto. Devuelve si había algo que cerrar (para Escape).</summary>
    public bool CloseMenus()
    {
        if (!_viewMenuOpen) return false;
        _viewMenuOpen = false;
        _replacing = null;
        Relayout();
        return true;
    }

    void OpenView(View view)
    {
        _view = view;
        _viewMenuOpen = false;
        _replacing = null;
        Relayout();
    }

    /// <summary>Siempre hay 3 fijas: el pin de una libre la prepara y el pin de una fija hace el cambio.</summary>
    void TogglePin(View view)
    {
        if (!_pinned.Contains(view))
            _replacing = _replacing == view ? null : view;
        else if (_replacing is { } incoming)
        {
            _pinned[_pinned.IndexOf(view)] = incoming;
            _replacing = null;
            PinnedChanged?.Invoke(_pinned.Select(v => v.ToString()).ToList());
        }
        Relayout();
    }

    // ── Selector de la cabecera ─────────────────────────────────────────────────────────────

    void ComposeViewSelector(DrawingContext? dc, double right, double top, double tabsRight)
    {
        const double height = 30, compact = 10 + 16 + 4 + 12 + 8;
        var extra = !_pinned.Contains(_view);
        var label = ViewLabel(_view);
        var labelWidth = Gfx.Measure(label, Gfx.Body).Width;
        var width = extra && right - (compact + 6 + labelWidth) >= tabsRight + 10 ? compact + 6 + labelWidth : compact;
        var showLabel = width > compact;
        _selectorBounds = new Rect(right - width, top, width, height);
        AddHit("viewmenu", _selectorBounds, HitKind.ViewMenu);
        if (dc is null) return;

        if (_viewMenuOpen) Gfx.Fill(dc, _t.Raised, _selectorBounds, 8);
        else if (_hoverKey == "viewmenu") Gfx.Fill(dc, _t.Hover, _selectorBounds, 8);
        Gfx.Outline(dc, extra || _viewMenuOpen ? _t.Accent : _t.Border, _selectorBounds, 8);

        var x = _selectorBounds.X + 10;
        Gfx.Pin(dc, extra ? _t.Accent : _t.Secondary, new Rect(x, top + (height - 16) / 2, 16, 16), filled: false);
        x += 16;
        if (showLabel)
        {
            Gfx.Write(dc, label, Gfx.Body, _t.Text, new Rect(x + 6, top, labelWidth + 2, height), center: true);
            x += 6 + labelWidth;
        }
        Gfx.Icon(dc, Glyph.ChevronDown, _t.Secondary, new Rect(x + 4, top, 12, height), 12);
    }

    /// <summary>La lista de vistas bajo el selector. Devuelve su borde inferior.</summary>
    double ComposeViewMenu(DrawingContext? dc)
    {
        const double width = 250, rowHeight = 34;
        var x = Math.Min(_selectorBounds.Right, ViewWidth - 10) - width;
        var top = _selectorBounds.Bottom + 6;
        var small = Gfx.Small.LineHeight;
        var hint = _replacing is { } incoming
            ? $"Pulse el pin de la fija que cambia por {ViewLabel(incoming)}."
            : "Siempre hay 3 pestañas fijas. Para fijar otra, pulse su pin y luego el de la que cambia.";
        var hintHeight = Gfx.MeasureWrapped(hint, Gfx.Small, width - 24, 4);
        var height = 8 + small + 8 + Selectable.Length * rowHeight + 9 + hintHeight + 12;
        var panel = new Rect(x, top, width, height);
        AddHit("viewmenu-panel", panel, HitKind.MenuPanel);

        if (dc is not null)
        {
            Gfx.Fill(dc, _t.Raised, panel, 10);
            Gfx.Outline(dc, _t.Border, panel, 10);
            Gfx.Write(dc, "Clic: abrir · Pin: fijar como pestaña", Gfx.Small, _t.Tertiary, new Rect(x + 12, top + 8, width - 24, small));
        }

        var y = top + 8 + small + 8;
        foreach (var view in Selectable)
        {
            var row = new Rect(x + 6, y, width - 12, rowHeight);
            var pin = new Rect(row.Right - 32, row.Y + (rowHeight - 30) / 2, 30, 30);
            var rowKey = "vrow:" + view;
            var pinKey = "vpin:" + view;
            AddHit(rowKey, row, HitKind.ViewRow, view: view);
            AddHit(pinKey, pin, HitKind.ViewPin, view: view);
            if (dc is not null)
            {
                var pinned = _pinned.Contains(view);
                if (_replacing == view) Gfx.Fill(dc, Gfx.Blend(_t.Raised, _t.Warn, 0.18), row, 8);
                else if (_view == view) Gfx.Fill(dc, _t.Hover, row, 8);
                else if (_hoverKey == rowKey) Gfx.Fill(dc, Gfx.Blend(_t.Raised, _t.Hover, 0.6), row, 8);
                var text = ViewLabel(view);
                var textWidth = Gfx.Measure(text, Gfx.Body).Width;
                Gfx.Write(dc, text, Gfx.Body, _t.Text, new Rect(row.X + 10, row.Y, textWidth + 2, rowHeight), center: true);
                if (_view == view)
                    Gfx.Write(dc, "abierta", Gfx.Small, _t.Tertiary, new Rect(row.X + 10 + textWidth + 8, row.Y, 80, rowHeight), center: true);
                if (_hoverKey == pinKey) Gfx.Fill(dc, _t.Hover, pin, 8);
                var color = pinned ? (_replacing is null ? _t.Accent : _t.Warn) : _replacing == view ? _t.Warn : _t.Tertiary;
                Gfx.Pin(dc, color, new Rect(pin.X + 7, pin.Y + 7, 16, 16), filled: pinned);
            }
            y += rowHeight;
        }

        y += 4;
        if (dc is not null)
        {
            dc.DrawLine(Gfx.P(_t.Border), new Point(x + 10, y), new Point(x + width - 10, y));
            Gfx.Write(dc, hint, Gfx.Small, _replacing is null ? _t.Tertiary : _t.Warn, new Rect(x + 12, y + 5, width - 24, hintHeight), maxLines: 4);
        }
        return panel.Bottom;
    }

    // ── Vista «Ajustes» ─────────────────────────────────────────────────────────────────────

    double ComposeSettingsHeader(DrawingContext? dc, double y)
    {
        var height = Gfx.Title.LineHeight;
        var close = new Rect(ViewWidth - Pad - 24, y + (height - 30) / 2, 30, 30);
        AddHit("settings-close", close, HitKind.CloseSettings);
        if (dc is not null)
        {
            Gfx.Write(dc, "Ajustes", Gfx.Title, _t.Text, new Rect(Pad, y, Inner - 40, height));
            if (_hoverKey == "settings-close") Gfx.Fill(dc, _t.Hover, close, 8);
            Gfx.Icon(dc, Glyph.Close, _t.Secondary, close, 13);
        }
        return y + height + 16;
    }

    double ComposeSettings(DrawingContext? dc, double y)
    {
        var s = _settingsState;
        y = SettingsCaption(dc, "Avisos", y);
        y = SwitchRow(dc, "Notificaciones", "notify", s.Notifications, y);
        y = SwitchRow(dc, "Sonido", "sound", s.Sound, y);
        y = ChoiceRow(dc, "Estilo de sonido", "pack", s.SoundPack, y);
        y = LinksRow(dc, y, ("Ver un aviso de ejemplo", "sample"), ("Abrir carpeta de sonidos", "sounds"));

        y = SettingsCaption(dc, "Apariencia", y + 10);
        y = ChoiceRow(dc, "Color", "palette", s.Palette, y, swatch: _t.Accent);
        y = ChoiceRow(dc, "Animación al actualizar", "anim", s.Animation, y);

        y = SettingsCaption(dc, "Cuentas", y + 10);
        y = LinkRow(dc, s.Accounts == 1 ? "1 cuenta" : $"{s.Accounts} cuentas", "Agregar cuenta…", "add", _t.Accent, y);

        y = SettingsCaption(dc, "Programa", y + 10);
        y = SwitchRow(dc, "Iniciar con el sistema", "startup", s.StartWithSystem, y);
        y = LinkRow(dc, "Avisos de permisos (hook)", "Configurar…", "setup", _t.Accent, y);
        y = LinkRow(dc, "Si algo no funciona", "Copiar diagnóstico", "diag", _t.Accent, y);
        y = LinkRow(dc, "Quitar hook e inicio con el sistema", "Quitar…", "unsetup", _t.Danger, y);
        return y + 14;
    }

    double SettingsCaption(DrawingContext? dc, string text, double y)
    {
        Gfx.Write(dc, text.ToUpperInvariant(), Gfx.Small, _t.Tertiary, new Rect(Pad, y, Inner, Gfx.Small.LineHeight));
        return y + Gfx.Small.LineHeight + 6;
    }

    double SwitchRow(DrawingContext? dc, string label, string key, bool on, double y)
    {
        const double height = 32;
        var row = new Rect(Pad - 8, y, Inner + 16, height);
        AddHit("set:" + key, row, HitKind.Setting);
        if (dc is not null)
        {
            if (_hoverKey == "set:" + key) Gfx.Fill(dc, _t.Hover, row, 8);
            Gfx.Write(dc, label, Gfx.Body, _t.Text, new Rect(Pad, y, Inner - 50, height), center: true);
            var track = new Rect(Pad + Inner - 34, y + (height - 18) / 2, 34, 18);
            Gfx.Fill(dc, on ? _t.Accent : _t.Track, track, 9);
            const double knob = 12;
            var kx = on ? track.Right - 3 - knob : track.X + 3;
            dc.DrawEllipse(on ? Brushes.White : Gfx.B(_t.Secondary), null, new Rect(kx, track.Y + (track.Height - knob) / 2, knob, knob));
        }
        return y + height;
    }

    /// <summary>Una opción con lista; swatch pone un punto de ese color antes del valor (la paleta).</summary>
    double ChoiceRow(DrawingContext? dc, string label, string key, string value, double y, SD.Color? swatch = null)
    {
        const double height = 32;
        var row = new Rect(Pad - 8, y, Inner + 16, height);
        AddHit("set:" + key, row, HitKind.Setting);
        if (dc is not null)
        {
            if (_hoverKey == "set:" + key) Gfx.Fill(dc, _t.Hover, row, 8);
            Gfx.Write(dc, label, Gfx.Body, _t.Text, new Rect(Pad, y, Inner / 2, height), center: true);
            var valueWidth = Gfx.Measure(value, Gfx.Body).Width;
            var valueX = Pad + Inner - 18 - valueWidth;
            Gfx.Write(dc, value, Gfx.Body, _t.Secondary, new Rect(valueX, y, valueWidth + 2, height), center: true);
            if (swatch is { } color)
                dc.DrawEllipse(Gfx.B(color), null, new Rect(valueX - 8 - 12, y + (height - 12) / 2, 12, 12));
            Gfx.Icon(dc, Glyph.ChevronDown, _t.Secondary, new Rect(Pad + Inner - 14, y, 14, height), 12);
        }
        return y + height;
    }

    double LinkRow(DrawingContext? dc, string label, string link, string key, SD.Color color, double y)
    {
        const double height = 32;
        var linkWidth = Gfx.Measure(link, Gfx.Body).Width;
        var target = new Rect(Pad + Inner - linkWidth - 8, y + 2, linkWidth + 16, height - 4);
        AddHit("set:" + key, target, HitKind.Setting);
        if (dc is not null)
        {
            Gfx.Write(dc, label, Gfx.Body, _t.Secondary, new Rect(Pad, y, Inner - linkWidth - 16, height), center: true);
            if (_hoverKey == "set:" + key) Gfx.Fill(dc, _t.Hover, target, 6);
            Gfx.Write(dc, link, Gfx.Body, color, new Rect(target.X + 8, y, linkWidth + 2, height), center: true);
        }
        return y + height;
    }

    double LinksRow(DrawingContext? dc, double y, params (string Text, string Key)[] links)
    {
        const double height = 32;
        var x = Pad - 8;
        foreach (var (text, key) in links)
        {
            var width = Gfx.Measure(text, Gfx.Small).Width + 16;
            var target = new Rect(x, y + 3, width, height - 6);
            AddHit("set:" + key, target, HitKind.Setting);
            if (dc is not null)
            {
                if (_hoverKey == "set:" + key) Gfx.Fill(dc, _t.Hover, target, 6);
                Gfx.Write(dc, text, Gfx.Small, _t.Accent, new Rect(target.X + 8, y, width - 14, height), center: true);
            }
            x += width + 4;
        }
        return y + height;
    }

    /// <summary>La lista de la opción de Ajustes con esa clave (pack, palette, anim); elegir manda «clave:id».</summary>
    void ShowChoiceMenu(string key)
    {
        IEnumerable<(string Id, string Label)> choices = key switch
        {
            "palette" => ClaudeUsageTray.Theme.Palettes.Select(p => (p.Id, p.Label)),
            "anim" => RefreshSprite.Styles,
            _ => Sounds.Packs,
        };
        var selected = key switch { "palette" => _paletteId, "anim" => _animationId, _ => _soundPackId };
        var menu = new ContextMenu();
        foreach (var (id, label) in choices)
        {
            var item = new MenuItem { Header = (id == selected ? "✓  " : "     ") + label };
            if (key == "palette") // cada paleta con un punto de su color en el modo actual
            {
                var palette = ClaudeUsageTray.Theme.Palettes.First(p => p.Id == id);
                item.Icon = new Avalonia.Controls.Shapes.Ellipse { Width = 12, Height = 12, Fill = Gfx.B(_t.Dark ? palette.Dark : palette.Light) };
            }
            item.Click += (_, _) => SettingRequested?.Invoke(key + ":" + id);
            menu.Items.Add(item);
        }
        menu.Open(this);
    }
}
