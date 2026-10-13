using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
// Window tiene su propia propiedad Launcher: la clase del núcleo va con otro nombre.
using SessionLauncher = ClaudeUsageTray.Launcher;

namespace ClaudeUsageTray;

/// <summary>Elige cuenta, carpeta, modelo, esfuerzo y asesor de una sesión nueva de Claude Code.</summary>
sealed class NewSessionWindow : Window
{
    const string Default = "Predeterminado de la cuenta";
    const string NoAdvisor = "Ninguno";

    readonly IReadOnlyList<Account> _accounts;
    readonly ComboBox _account, _effort, _advisor;
    readonly AutoCompleteBox _folder, _model;
    readonly TextBlock _error;
    readonly TaskCompletionSource<LaunchOptions?> _done = new();

    /// <summary>Las opciones elegidas, o null si se canceló.</summary>
    public Task<LaunchOptions?> Result => _done.Task;

    public NewSessionWindow(IReadOnlyList<Account> accounts, LaunchOptions initial, IEnumerable<string> recentFolders, Theme theme)
    {
        _accounts = accounts;
        Title = "Nueva sesión de Claude Code";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Gfx.B(theme.Background);

        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 6, Width = 440 };
        panel.Children.Add(new TextBlock
        {
            Text = "Nueva sesión", FontSize = 22, FontFamily = Gfx.Title.Typeface.FontFamily, Foreground = Gfx.B(theme.Text),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Se abre en una terminal. Lo que elija vale solo para esa sesión.", FontSize = 12.5,
            Foreground = Gfx.B(theme.Secondary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
        void Row(string label, Control control)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = new TextBlock { Text = label, Foreground = Gfx.B(theme.Secondary), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
        }

        _account = new ComboBox
        {
            ItemsSource = accounts.Select(a => $"{a.Label} ({a.ConfigDir.Replace(Account.Home, "~")})").ToList(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, accounts.ToList().FindIndex(a => initial.ConfigDir is not null && Account.SamePath(a.ConfigDir, initial.ConfigDir))),
        };
        Row("Cuenta", _account);

        var folders = recentFolders.Where(f => f.Length > 0).Distinct().Take(10).ToList();
        _folder = new AutoCompleteBox
        {
            ItemsSource = folders, Text = initial.Folder ?? folders.FirstOrDefault() ?? Account.Home,
            FilterMode = AutoCompleteFilterMode.Contains, MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var browse = new Button { Content = "Examinar…" };
        browse.Click += async (_, _) =>
        {
            var start = await StorageProvider.TryGetFolderFromPathAsync(Directory.Exists(_folder.Text) ? _folder.Text! : Account.Home);
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Carpeta del proyecto", AllowMultiple = false, SuggestedStartLocation = start,
            });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) _folder.Text = path;
        };
        var folderRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 6 };
        Grid.SetColumn(browse, 1);
        folderRow.Children.Add(_folder);
        folderRow.Children.Add(browse);
        Row("Carpeta", folderRow);

        _model = new AutoCompleteBox
        {
            ItemsSource = SessionLauncher.Models, Text = initial.Model ?? "", Watermark = Default,
            FilterMode = AutoCompleteFilterMode.StartsWith, MinimumPrefixLength = 0, HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Row("Modelo", _model);

        var efforts = SessionLauncher.Efforts.Prepend(Default).ToList();
        _effort = new ComboBox { ItemsSource = efforts, SelectedIndex = Math.Max(0, efforts.IndexOf(initial.Effort ?? Default)), HorizontalAlignment = HorizontalAlignment.Stretch };
        Row("Esfuerzo", _effort);

        var advisors = SessionLauncher.Advisors.Prepend(NoAdvisor).Prepend(Default).ToList();
        var advisor = initial.Advisor == SessionLauncher.NoAdvisor ? NoAdvisor : initial.Advisor ?? Default;
        _advisor = new ComboBox { ItemsSource = advisors, SelectedIndex = Math.Max(0, advisors.IndexOf(advisor)), HorizontalAlignment = HorizontalAlignment.Stretch };
        Row("Asesor", _advisor);
        panel.Children.Add(grid);

        panel.Children.Add(new TextBlock
        {
            Text = "El modelo también acepta un ID completo, como claude-opus-5-5. El asesor predeterminado es el que fijó /advisor en esa cuenta.",
            FontSize = 12, Foreground = Gfx.B(theme.Tertiary), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        _error = new TextBlock { FontSize = 12.5, Foreground = Gfx.B(theme.Danger), TextWrapping = TextWrapping.Wrap, IsVisible = false };
        panel.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var open = new Button { Content = "Abrir", Background = Gfx.B(theme.Accent), Foreground = Brushes.White, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 90 };
        open.Click += (_, _) =>
        {
            if (Read() is { } options) { _done.TrySetResult(options); Close(); }
        };
        cancel.Click += (_, _) => { _done.TrySetResult(null); Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(open);
        panel.Children.Add(buttons);
        Content = panel;
        Closed += (_, _) => _done.TrySetResult(null);
    }

    /// <summary>Lee el formulario; si algo falta o no vale, lo dice debajo y devuelve null.</summary>
    LaunchOptions? Read()
    {
        LaunchOptions? Fail(string message)
        {
            _error.Text = message;
            _error.IsVisible = true;
            return null;
        }

        if (_account.SelectedIndex < 0 || _account.SelectedIndex >= _accounts.Count) return Fail("Elija una cuenta.");
        var folder = _folder.Text?.Trim() ?? "";
        if (!Directory.Exists(folder)) return Fail("Esa carpeta no existe.");
        var model = _model.Text?.Trim() ?? "";
        var modelValue = model.Length == 0 || model == Default ? null : model;
        if (!SessionLauncher.IsValid(modelValue)) return Fail("El modelo solo puede llevar letras, números, puntos, guiones y corchetes.");

        var effort = (string)_effort.SelectedItem!;
        var advisor = (string)_advisor.SelectedItem!;
        return new LaunchOptions
        {
            ConfigDir = _accounts[_account.SelectedIndex].ConfigDir,
            Folder = folder,
            Model = modelValue,
            Effort = effort == Default ? null : effort,
            Advisor = advisor == Default ? null : advisor == NoAdvisor ? SessionLauncher.NoAdvisor : advisor,
        };
    }
}
