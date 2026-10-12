using Microsoft.Win32;

namespace ClaudeUsageTray;

/// <summary>
/// Pregunta qué configurar antes de tocar nada: el hook de permisos en cada carpeta de Claude Code,
/// el inicio con Windows y las notificaciones.
/// </summary>
sealed class SetupForm : Form
{
    sealed record HookRow(string Dir, HookInstaller.State State, CheckBox Box);

    readonly List<HookRow> _rows = new();
    readonly CheckBox _startup;
    readonly CheckBox _notifications;

    public bool StartWithWindows => _startup.Checked;
    public bool NotificationsOn => _notifications.Checked;

    public SetupForm(AppSettings settings, string exePath, Theme theme, IEnumerable<string> configDirs)
    {
        Text = "Configurar Uso de Claude";
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(0, 0, LogicalToDeviceUnits(22), LogicalToDeviceUnits(22));
        BackColor = theme.Background;
        ForeColor = theme.Text;
        Font = Fonts.Make("Segoe UI Variable Text", 9.5f, "Segoe UI");
        var small = Fonts.Make("Segoe UI Variable Text", 8.75f, "Segoe UI");
        var width = LogicalToDeviceUnits(420);

        var pad = LogicalToDeviceUnits(22);
        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Location = new Point(pad, pad),
        };
        Label Heading(string text, Font font, Color color, int top = 0) => new()
        {
            Text = text, Font = font, ForeColor = color, AutoSize = true, MaximumSize = new Size(width, 0),
            Margin = new Padding(0, LogicalToDeviceUnits(top), 0, LogicalToDeviceUnits(4)),
        };
        CheckBox Option(string text, bool isChecked, bool enabled = true) => new()
        {
            Text = text, Checked = isChecked, Enabled = enabled, AutoSize = true, MaximumSize = new Size(width, 0),
            FlatStyle = FlatStyle.Standard, ForeColor = enabled ? theme.Text : theme.Tertiary, BackColor = theme.Background,
            Margin = new Padding(0, LogicalToDeviceUnits(4), 0, 0),
        };

        layout.Controls.Add(Heading("Configuración", Fonts.Make("Georgia", 14f, "Cambria"), theme.Text));
        layout.Controls.Add(Heading("Elija qué configurar. Nada cambia sin su confirmación.", small, theme.Secondary));

        layout.Controls.Add(Heading("Avisos de permisos", Font, theme.Text, top: 14));
        layout.Controls.Add(Heading("Agrega un hook al settings.json de Claude Code; antes se guarda un respaldo.", small, theme.Tertiary));
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
            layout.Controls.Add(box);
        }
        if (_rows.Count == 0)
            layout.Controls.Add(Heading("No se encontró ninguna carpeta de Claude Code.", small, theme.Tertiary));

        layout.Controls.Add(Heading("Programa", Font, theme.Text, top: 14));
        _startup = Option("Iniciar con Windows", StartupRegistration.IsEnabled || !settings.SetupDone);
        _notifications = Option("Mostrar notificaciones", settings.Notifications);
        layout.Controls.Add(_startup);
        layout.Controls.Add(_notifications);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width,
            Margin = new Padding(0, LogicalToDeviceUnits(20), 0, 0),
        };
        Button MakeButton(string text, DialogResult result, bool primary) => new()
        {
            Text = text, DialogResult = result, AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? theme.Accent : theme.Raised, ForeColor = primary ? Color.White : theme.Text,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(3), LogicalToDeviceUnits(10), LogicalToDeviceUnits(3)),
            FlatAppearance = { BorderColor = primary ? theme.Accent : theme.Border },
        };
        var apply = MakeButton("Aplicar", DialogResult.OK, primary: true);
        var later = MakeButton("Ahora no", DialogResult.Cancel, primary: false);
        buttons.Controls.AddRange(new Control[] { apply, later });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = apply;
        CancelButton = later;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.Apply(Handle, Theme.Current);
    }

    /// <summary>Instala o actualiza el hook donde se marcó. Devuelve una línea por carpeta.</summary>
    public List<string> ApplyHooks(string exePath)
    {
        var lines = new List<string>();
        foreach (var row in _rows.Where(r => r.Box.Enabled && r.Box.Checked))
        {
            var name = row.Dir.Replace(Account.Home, "~");
            try
            {
                var backup = HookInstaller.Install(row.Dir, exePath);
                var verb = row.State == HookInstaller.State.Outdated ? "hook actualizado" : "hook agregado";
                lines.Add(backup is null ? $"{name}: {verb}." : $"{name}: {verb} (respaldo: {Path.GetFileName(backup)}).");
            }
            catch (Exception ex)
            {
                lines.Add($"{name}: no se pudo cambiar ({ex.Message}).");
            }
        }
        return lines;
    }
}
