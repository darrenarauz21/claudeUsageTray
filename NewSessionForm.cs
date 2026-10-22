namespace ClaudeUsageTray;

/// <summary>Elige cuenta, carpeta, modelo, esfuerzo y asesor de una sesión nueva de Claude Code.</summary>
sealed class NewSessionForm : Form
{
    const string Default = "Predeterminado de la cuenta";
    const string NoAdvisor = "Ninguno";

    readonly ComboBox _account, _folder, _model, _effort, _advisor;
    readonly IReadOnlyList<Account> _accounts;

    public LaunchOptions Result { get; private set; } = new();

    public NewSessionForm(IReadOnlyList<Account> accounts, LaunchOptions initial, IEnumerable<string> recentFolders, Theme theme)
    {
        _accounts = accounts;
        Text = "Nueva sesión de Claude Code";
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
        var fieldWidth = LogicalToDeviceUnits(300);
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
        ComboBox Combo(bool editable, IEnumerable<string> items, string? selected, int? comboWidth = null)
        {
            var combo = new ComboBox
            {
                DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList, Width = comboWidth ?? fieldWidth,
                FlatStyle = FlatStyle.Flat, BackColor = theme.Raised, ForeColor = theme.Text,
            };
            combo.Items.AddRange(items.Cast<object>().ToArray());
            if (editable) combo.Text = selected ?? "";
            else combo.SelectedItem = selected is not null && combo.Items.Contains(selected) ? selected : combo.Items[0];
            return combo;
        }
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0, LogicalToDeviceUnits(10), 0, 0) };
        void Row(string label, Control control)
        {
            grid.Controls.Add(new Label
            {
                Text = label, AutoSize = true, ForeColor = theme.Secondary, Anchor = AnchorStyles.Left,
                Margin = new Padding(0, LogicalToDeviceUnits(6), LogicalToDeviceUnits(12), LogicalToDeviceUnits(6)),
            });
            control.Margin = new Padding(0, LogicalToDeviceUnits(4), 0, LogicalToDeviceUnits(4));
            grid.Controls.Add(control);
        }

        layout.Controls.Add(Heading("Nueva sesión", Fonts.Make("Georgia", 14f, "Cambria"), theme.Text));
        layout.Controls.Add(Heading("Se abre en una terminal. Lo que elija vale solo para esa sesión.", small, theme.Secondary));

        var accountIndex = Math.Max(0, accounts.ToList().FindIndex(a => initial.ConfigDir is not null && Account.SamePath(a.ConfigDir, initial.ConfigDir)));
        _account = Combo(false, accounts.Select(a => $"{a.Label} ({a.ConfigDir.Replace(Account.Home, "~")})"), null);
        if (accounts.Count > 0) _account.SelectedIndex = accountIndex;
        Row("Cuenta", _account);

        var folders = recentFolders.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        _folder = Combo(true, folders, initial.Folder ?? folders.FirstOrDefault() ?? Account.Home, fieldWidth - LogicalToDeviceUnits(90));
        var browse = new Button
        {
            Text = "Examinar…", AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = theme.Raised, ForeColor = theme.Text,
            FlatAppearance = { BorderColor = theme.Border },
        };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Carpeta del proyecto", UseDescriptionForTitle = true,
                InitialDirectory = Directory.Exists(_folder.Text) ? _folder.Text : Account.Home,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        };
        var folderRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        _folder.Margin = new Padding(0, 0, LogicalToDeviceUnits(6), 0);
        folderRow.Controls.AddRange(new Control[] { _folder, browse });
        Row("Carpeta", folderRow);

        _model = Combo(true, Launcher.Models.Prepend(Default), initial.Model ?? Default);
        Row("Modelo", _model);
        _effort = Combo(false, Launcher.Efforts.Prepend(Default), initial.Effort);
        Row("Esfuerzo", _effort);
        var advisor = initial.Advisor == Launcher.NoAdvisor ? NoAdvisor : initial.Advisor;
        _advisor = Combo(false, Launcher.Advisors.Prepend(NoAdvisor).Prepend(Default), advisor);
        Row("Asesor", _advisor);
        layout.Controls.Add(grid);
        layout.Controls.Add(Heading("El modelo también acepta un ID completo, como claude-opus-5-5. El asesor predeterminado es el que fijó /advisor en esa cuenta.",
            small, theme.Tertiary, top: 8));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width,
            Margin = new Padding(0, LogicalToDeviceUnits(16), 0, 0),
        };
        Button MakeButton(string text, DialogResult result, bool primary) => new()
        {
            Text = text, DialogResult = result, AutoSize = true, FlatStyle = FlatStyle.Flat,
            BackColor = primary ? theme.Accent : theme.Raised, ForeColor = primary ? Color.White : theme.Text,
            Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(3), LogicalToDeviceUnits(10), LogicalToDeviceUnits(3)),
            FlatAppearance = { BorderColor = primary ? theme.Accent : theme.Border },
        };
        var open = MakeButton("Abrir", DialogResult.OK, primary: true);
        var cancel = MakeButton("Cancelar", DialogResult.Cancel, primary: false);
        buttons.Controls.AddRange(new Control[] { open, cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = open;
        CancelButton = cancel;

        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK) return;
            if (Read() is { } error)
            {
                MessageBox.Show(this, error, "Nueva sesión", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Dwm.Apply(Handle, Theme.Current);
    }

    /// <summary>Lee el formulario en Result. Devuelve el problema, o null si todo está bien.</summary>
    string? Read()
    {
        if (_account.SelectedIndex < 0 || _account.SelectedIndex >= _accounts.Count) return "Elija una cuenta.";
        var folder = _folder.Text.Trim();
        if (!Directory.Exists(folder)) return "Esa carpeta no existe.";
        var model = _model.Text.Trim();
        model = model.Length == 0 || model == Default ? null : model;
        if (!Launcher.IsValid(model)) return "El modelo solo puede llevar letras, números, puntos, guiones y corchetes.";

        var effort = (string)_effort.SelectedItem!;
        var advisor = (string)_advisor.SelectedItem!;
        Result = new LaunchOptions
        {
            ConfigDir = _accounts[_account.SelectedIndex].ConfigDir,
            Folder = folder,
            Model = model,
            Effort = effort == Default ? null : effort,
            Advisor = advisor == Default ? null : advisor == NoAdvisor ? Launcher.NoAdvisor : advisor,
        };
        return null;
    }
}
