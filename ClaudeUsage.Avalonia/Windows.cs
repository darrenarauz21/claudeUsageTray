using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ClaudeUsageTray;

/// <summary>Dónde van las ventanas: en macOS arriba a la derecha (bajo la barra de menús); en los demás, abajo.</summary>
static class Placement
{
    public static bool FromTop => OperatingSystem.IsMacOS();

    public static Screen? PrimaryScreen(TopLevel top) => top.Screens?.Primary ?? top.Screens?.All.FirstOrDefault();
}

/// <summary>La ventana que se abre desde la bandeja, sin marco, junto al ícono.</summary>
sealed class PopupWindow : Window
{
    public UsageView View { get; } = new();
    public bool HoldOpen { get; set; }
    public DateTime LastHidden { get; private set; }

    readonly Border _frame;

    public PopupWindow()
    {
        Title = "Uso de Claude";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        _frame = new Border { BorderThickness = new Thickness(1), Child = View };
        Content = _frame;

        Deactivated += (_, _) => { if (!HoldOpen) HidePopup(); };
        // Escape cierra primero el selector de vistas; si no hay nada abierto, la ventana.
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !View.CloseMenus()) HidePopup(); };
        // Al cambiar de alto (otra vista, una sesión desplegada) se vuelve a pegar al borde.
        SizeChanged += (_, _) => { if (IsVisible) Reposition(); };
    }

    public void ApplyTheme(Theme theme)
    {
        View.Palette = theme;
        _frame.BorderBrush = Gfx.B(theme.Border);
        Background = Gfx.B(theme.Background);
    }

    public void ShowNear()
    {
        Show();
        Reposition();
        Activate();
        View.UpdateAnimation();
    }

    public void HidePopup()
    {
        if (!IsVisible) return;
        Hide();
        LastHidden = DateTime.Now;
        View.UpdateAnimation();
    }

    void Reposition()
    {
        if (Placement.PrimaryScreen(this) is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scale);
        var height = (int)Math.Ceiling(Bounds.Height * scale);
        var margin = (int)(12 * scale);
        Position = Placement.FromTop
            ? new PixelPoint(area.Right - width - margin, area.Y + (int)(6 * scale))
            : new PixelPoint(area.Right - width - margin, area.Bottom - height - margin);
    }
}

/// <summary>Un aviso: entra desde el costado derecho sin quitar el foco, con la mascota según el caso.</summary>
sealed class ToastWindow : Window
{
    public const double ToastWidth = 344, ToastHeight = 88;
    const int Lifetime = 6000, ExitMs = 200;

    public event Action<ToastWindow>? Clicked;
    public event Action<ToastWindow>? Dismissed;
    public Notice Notice { get; }

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly ToastView _view;
    long _last = Environment.TickCount64;
    long _remaining = Lifetime;
    long? _closingSince;
    PixelPoint _target;

    public ToastWindow(Notice notice, Theme theme)
    {
        Notice = notice;
        Title = "Uso de Claude";
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = ToastWidth;
        Height = ToastHeight;
        Background = Gfx.B(theme.Raised);
        _view = new ToastView(notice, theme);
        Content = new Border { BorderThickness = new Thickness(1), BorderBrush = Gfx.B(theme.Border), Child = _view };
        _view.Clicked += () => { Clicked?.Invoke(this); Dismiss(); };
        _view.CloseClicked += Dismiss;
        _timer.Tick += (_, _) => Tick();
    }

    // Antes de mostrarse, Avalonia aún no sabe la escala real: se usa la de la pantalla principal;
    // ya en pantalla, la de la ventana.
    double Scale => IsVisible ? RenderScaling : Placement.PrimaryScreen(this)?.Scaling ?? 1;
    public int PixelHeight => (int)Math.Ceiling(ToastHeight * Scale);
    int OffScreenX => (Placement.PrimaryScreen(this)?.WorkingArea.Right ?? 0) + 10;

    public void Present(PixelPoint target)
    {
        _target = target;
        Position = new PixelPoint(OffScreenX, target.Y);
        Show();
        _timer.Start();
    }

    public void MoveTo(PixelPoint target) => _target = target;

    public void Dismiss() => _closingSince ??= Environment.TickCount64;

    void Tick()
    {
        var now = Environment.TickCount64;
        var dt = now - _last;
        _last = now;

        if (_closingSince is { } since)
        {
            var k = Math.Min(1.0, (now - since) / (double)ExitMs);
            Position = new PixelPoint((int)(_target.X + (OffScreenX - _target.X) * k * k), Position.Y);
            if (k >= 1)
            {
                _timer.Stop();
                Dismissed?.Invoke(this);
                Close();
                return;
            }
        }
        else
        {
            var x = Position.X + (int)Math.Round((_target.X - Position.X) * Math.Min(1.0, dt / 260.0 * 3));
            var y = Position.Y + (int)Math.Round((_target.Y - Position.Y) * Math.Min(1.0, dt / 120.0));
            if (Math.Abs(_target.X - x) <= 1) x = _target.X;
            if (Math.Abs(_target.Y - y) <= 1) y = _target.Y;
            if (x != Position.X || y != Position.Y) Position = new PixelPoint(x, y);

            if (!Notice.Sticky && !_view.Hovered)
            {
                _remaining -= dt;
                if (_remaining <= 0) Dismiss();
            }
        }
        _view.Remaining = _closingSince is null && !Notice.Sticky ? Math.Clamp(_remaining / (double)Lifetime, 0, 1) : -1;
        _view.InvalidateVisual();
    }

    /// <summary>Lugar de cada aviso en la pila: abajo a la derecha (en macOS, arriba), el más nuevo junto al borde.</summary>
    public static PixelPoint Slot(TopLevel any, int index, int pixelHeight)
    {
        var screen = Placement.PrimaryScreen(any);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 1280, 800);
        var scale = screen?.Scaling ?? 1;
        var width = (int)Math.Ceiling(ToastWidth * scale);
        var margin = (int)(12 * scale);
        var gap = (int)(8 * scale);
        var offset = index * (pixelHeight + gap);
        return Placement.FromTop
            ? new PixelPoint(area.Right - width - margin, area.Y + margin + offset)
            : new PixelPoint(area.Right - width - margin, area.Bottom - margin - pixelHeight - offset);
    }
}

sealed class ToastView(Notice notice, Theme theme) : Control
{
    public event Action? Clicked;
    public event Action? CloseClicked;
    public bool Hovered { get; private set; }
    public double Remaining { get; set; } = 1;

    readonly long _born = Environment.TickCount64;
    bool _hoverClose;

    Rect CloseBounds => new(Bounds.Width - 30, 6, 24, 24);

    public override void Render(DrawingContext dc)
    {
        var t = theme;
        dc.FillRectangle(Gfx.B(t.Raised), new Rect(Bounds.Size));
        var art = new Rect(12, (Bounds.Height - 64) / 2, 78, 64);
        var born = Environment.TickCount64 - _born;
        if (Mascots.IsChibi)
        {
            // Asomado desde abajo, en todo el alto del aviso: de la cabeza al ruedo del hoodie.
            var column = new Rect(8, 0, 86, Bounds.Height);
            var chibiUnit = Math.Max(1, Math.Floor(column.Height / ChibiSprite.ToastRows));
            using (dc.PushClip(column))
                Gfx.Pixels(dc, column.X + (column.Width - ChibiSprite.Columns * chibiUnit) / 2, column.Bottom - ChibiSprite.HemRow * chibiUnit, chibiUnit,
                    fill => ChibiSprite.Paint(fill, ChibiSprite.ForNotice(notice.Kind), born));
        }
        else
        {
            const double unit = 3;
            Gfx.Pixels(dc, art.X + (art.Width - ToastSprite.Columns * unit) / 2, art.Y + (art.Height - ToastSprite.Rows * unit) / 2, unit,
                fill => ToastSprite.Paint(fill, notice.Kind, born, t.Accent));
        }

        var x = art.Right + 12;
        var width = Bounds.Width - x - 34;
        var titleColor = notice.Kind switch
        {
            NoticeKind.Failed or NoticeKind.UsageCritical or NoticeKind.Closed => t.Danger,
            NoticeKind.Question or NoticeKind.Permission or NoticeKind.UsageHigh or NoticeKind.Expired or NoticeKind.Retrying => t.Warn,
            _ => t.Success,
        };
        var y = 15.0;
        Gfx.Write(dc, notice.Title, Gfx.ToastTitle, titleColor, new Rect(x, y, width, Gfx.ToastTitle.LineHeight));
        y += Gfx.ToastTitle.LineHeight + 2;
        Gfx.Write(dc, notice.Subtitle, Gfx.ToastBody, t.Text, new Rect(x, y, width, Gfx.ToastBody.LineHeight));
        y += Gfx.ToastBody.LineHeight + 2;
        Gfx.Write(dc, notice.Detail, Gfx.ToastSmall, t.Tertiary, new Rect(x, y, width + 20, Gfx.ToastSmall.LineHeight * 2), maxLines: 2);

        if (Hovered)
        {
            if (_hoverClose) Gfx.Fill(dc, t.Hover, CloseBounds, 6);
            Gfx.Icon(dc, Glyph.Close, t.Secondary, CloseBounds, 11);
        }
        if (Remaining >= 0)
            dc.FillRectangle(Gfx.B(t.Accent), new Rect(0, Bounds.Height - 2, Bounds.Width * Remaining, 2));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Hovered = true;
        _hoverClose = CloseBounds.Contains(e.GetPosition(this));
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        Hovered = _hoverClose = false;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (CloseBounds.Contains(e.GetPosition(this))) CloseClicked?.Invoke();
        else Clicked?.Invoke();
    }
}

/// <summary>Apila los avisos; como mucho cuatro a la vez.</summary>
sealed class ToastStack
{
    const int MaxVisible = 4;

    public event Action<Notice>? Clicked;
    public event Action? Shown;

    readonly List<ToastWindow> _toasts = new();

    public void Show(Notice notice, Theme theme)
    {
        var toast = new ToastWindow(notice, theme);
        toast.Clicked += t => Clicked?.Invoke(t.Notice);
        toast.Dismissed += t =>
        {
            _toasts.Remove(t);
            Layout();
        };
        _toasts.Insert(0, toast);
        foreach (var old in _toasts.Skip(MaxVisible)) old.Dismiss();
        toast.Present(ToastWindow.Slot(toast, 0, toast.PixelHeight));
        Layout();
        Shown?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var toast in _toasts.ToList()) toast.Close();
        _toasts.Clear();
    }

    void Layout()
    {
        for (var i = 0; i < _toasts.Count; i++)
            _toasts[i].MoveTo(ToastWindow.Slot(_toasts[i], i, _toasts[i].PixelHeight));
    }
}

/// <summary>Diálogos sencillos (aviso, confirmar, pedir un texto) con el tema de la app.</summary>
static class Dialogs
{
    public static Task Info(string text, Theme theme) => Show(text, theme, ["Aceptar"]);

    public static async Task<bool> Confirm(string text, Theme theme) => await Show(text, theme, ["Sí", "No"]) == 0;

    public static async Task<string?> Ask(string title, string label, string value, Theme theme)
    {
        var box = new TextBox { Text = value, Width = 280 };
        var result = await Show(label, theme, ["Aceptar", "Cancelar"], box, title);
        var text = box.Text?.Trim();
        return result == 0 && !string.IsNullOrEmpty(text) ? text : null;
    }

    static Task<int> Show(string text, Theme theme, string[] buttons, Control? extra = null, string title = "Uso de Claude")
    {
        var done = new TaskCompletionSource<int>();
        var window = new Window
        {
            Title = title, SizeToContent = SizeToContent.WidthAndHeight, CanResize = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Background = Gfx.B(theme.Background),
        };
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 14, MaxWidth = 460 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Gfx.B(theme.Text) });
        if (extra is not null) panel.Children.Add(extra);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button { Content = buttons[i], MinWidth = 84, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { button.Background = Gfx.B(theme.Accent); button.Foreground = Brushes.White; button.IsDefault = true; }
            else button.IsCancel = true;
            button.Click += (_, _) => { done.TrySetResult(index); window.Close(); };
            row.Children.Add(button);
        }
        panel.Children.Add(row);
        window.Content = panel;
        window.Closed += (_, _) => done.TrySetResult(-1);
        window.Show();
        window.Activate();
        return done.Task;
    }
}

/// <summary>Primera configuración: hook de permisos, iniciar con el sistema y notificaciones.</summary>
sealed class SetupWindow : Window
{
    sealed record HookRow(string Dir, HookInstaller.State State, CheckBox Box);

    readonly List<HookRow> _rows = new();
    readonly CheckBox _startup;
    readonly CheckBox _notifications;
    readonly TaskCompletionSource<bool> _done = new();

    public bool StartWithSystem => _startup.IsChecked == true;
    public bool NotificationsOn => _notifications.IsChecked == true;
    public Task<bool> Result => _done.Task;

    public SetupWindow(AppSettings settings, string exePath, Theme theme, IEnumerable<string> configDirs)
    {
        Title = "Configurar Uso de Claude";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Gfx.B(theme.Background);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 6, Width = 440 };
        TextBlock Label(string text, double size, IBrush color, double top = 0, FontWeight weight = FontWeight.Normal, FontFamily? family = null) => new()
        {
            Text = text, FontSize = size, Foreground = color, FontWeight = weight, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, top, 0, 2), FontFamily = family ?? FontFamily.Default,
        };
        CheckBox Option(string text, bool isChecked, bool enabled = true) => new()
        {
            Content = text, IsChecked = isChecked, IsEnabled = enabled, Foreground = Gfx.B(enabled ? theme.Text : theme.Tertiary),
        };

        panel.Children.Add(Label("Configuración", 22, Gfx.B(theme.Text), family: Gfx.Title.Typeface.FontFamily));
        panel.Children.Add(Label("Elija qué configurar. Nada cambia sin su confirmación.", 12.5, Gfx.B(theme.Secondary)));
        panel.Children.Add(Label("Avisos de permisos", 14, Gfx.B(theme.Text), 14, FontWeight.SemiBold));
        panel.Children.Add(Label("Agrega un hook al settings.json de Claude Code; antes se guarda un respaldo.", 12, Gfx.B(theme.Tertiary)));
        foreach (var dir in configDirs)
        {
            var state = HookInstaller.Status(dir, exePath);
            var name = dir.Replace(Account.Home, "~");
            var box = state switch
            {
                HookInstaller.State.Installed => Option($"{name} — ya está configurado", true, enabled: false),
                HookInstaller.State.Outdated => Option($"{name} — apunta a otra ruta: actualizarlo", true),
                HookInstaller.State.Unreadable => Option($"{name} — su settings.json tiene errores; no se toca", false, enabled: false),
                _ => Option($"{name} — agregar", true),
            };
            _rows.Add(new HookRow(dir, state, box));
            panel.Children.Add(box);
        }
        if (_rows.Count == 0) panel.Children.Add(Label("No se encontró ninguna carpeta de Claude Code.", 12, Gfx.B(theme.Tertiary)));

        panel.Children.Add(Label("Programa", 14, Gfx.B(theme.Text), 14, FontWeight.SemiBold));
        _startup = Option("Iniciar con el sistema", StartupRegistration.IsEnabled || !settings.SetupDone);
        _notifications = Option("Mostrar notificaciones", settings.Notifications);
        panel.Children.Add(_startup);
        panel.Children.Add(_notifications);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var apply = new Button { Content = "Aplicar", Background = Gfx.B(theme.Accent), Foreground = Brushes.White, IsDefault = true, MinWidth = 90 };
        var later = new Button { Content = "Ahora no", IsCancel = true, MinWidth = 90 };
        apply.Click += (_, _) => { _done.TrySetResult(true); Close(); };
        later.Click += (_, _) => { _done.TrySetResult(false); Close(); };
        row.Children.Add(later);
        row.Children.Add(apply);
        panel.Children.Add(row);
        Content = panel;
        Closed += (_, _) => _done.TrySetResult(false);
    }

    public List<string> ApplyHooks(string exePath)
    {
        var lines = new List<string>();
        foreach (var row in _rows.Where(r => r.Box.IsEnabled && r.Box.IsChecked == true))
        {
            var name = row.Dir.Replace(Account.Home, "~");
            try
            {
                var backup = HookInstaller.Install(row.Dir, exePath);
                var verb = row.State == HookInstaller.State.Outdated ? "hook actualizado" : "hook agregado";
                lines.Add(backup is null ? $"{name}: {verb}." : $"{name}: {verb} (respaldo: {Path.GetFileName(backup)}).");
            }
            catch (Exception ex) { lines.Add($"{name}: no se pudo cambiar ({ex.Message})."); }
        }
        return lines;
    }
}
