#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeBridge.VisualStudio.Editor;

namespace CodeBridge.VisualStudio;

internal enum CodeBridgeSetupAction
{
    None,
    InstallPackage,
    RemovePackage,
    AddStarterForm
}

/// <summary>
/// The CodeBridge Setup tool window. WPF (not WinForms) so it reflows when the window is resized or docked narrow,
/// scales with the display DPI and follows the Visual Studio theme like the flow editor.
/// </summary>
internal sealed class CodeBridgeSetupControl : UserControl
{
    private static readonly Color Accent = Color.FromRgb(0, 122, 204);
    private readonly StackPanel _root = new StackPanel { Margin = new Thickness(18, 16, 18, 24) };

    public event EventHandler<CodeBridgeSetupAction>? ActionSelected;

    public CodeBridgeSetupControl()
    {
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 12;

        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodeBridge.VisualStudio;component/Editor/EditorTheme.xaml", UriKind.Relative) });
        foreach (var pair in new[]
        {
            ("VsWindowBackground", "#1e1e1e"), ("VsToolWindowBackground", "#252526"), ("VsToolWindowHeader", "#2d2d30"),
            ("VsToolWindowBorder", "#3f3f46"), ("VsToolWindowText", "#f1f1f1"), ("VsGrayText", "#999999"),
            ("VsInputBackground", "#333337"), ("VsInputBorder", "#434346"), ("VsHighlight", "#007acc")
        })
        {
            Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
        }

        VsTheme.Bind(Resources);
        SetResourceReference(BackgroundProperty, "VsToolWindowBackground");

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _root
        };
    }

    /// <summary>Rebuilds the page from the model. Fresh elements every time, so nothing is ever reused after disposal.</summary>
    public void UpdateModel(CodeBridgeSetupViewModel model)
    {
        _root.Children.Clear();

        _root.Children.Add(Text("CodeBridge Setup", 17, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 0, 0, 2)));
        _root.Children.Add(Text("Project setup, Toolbox package and the starter workflow for ESP32 and Arduino Uno.", 11.5, FontWeights.Normal, "VsGrayText", new Thickness(0, 0, 0, 14)));

        _root.Children.Add(StatusCard(model));
        _root.Children.Add(Heading("Toolbox components"));
        _root.Children.Add(ComponentList(model));

        _root.Children.Add(Heading("Recommended first run"));
        _root.Children.Add(Text(
            "Create a CodeBridge WinForms App (or add the starter form to a project) and run it. The form runs Flows\\Blink.cbflow on the board it finds on USB (or on the simulator) and offers to upload the firmware if the board needs it. Open the .cbflow, change it and save: the running app picks the change up.",
            12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 0, 0, 14)));

        _root.Children.Add(Buttons(model));
    }

    private FrameworkElement StatusCard(CodeBridgeSetupViewModel model)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        string installed;
        if (model.InstalledPackageVersion is null)
            installed = "Not installed in the selected project";
        else if (string.Equals(model.InstalledPackageVersion, model.PackageVersion, StringComparison.OrdinalIgnoreCase))
            installed = model.InstalledPackageVersion + "  (up to date)";
        else
            installed = model.InstalledPackageVersion + "  (the extension carries " + model.PackageVersion + ")";

        Row(grid, 0, "Project", model.ProjectName ?? "No active C# project selected");
        Row(grid, 1, "Package", $"{model.PackageId} {model.PackageVersion}");
        Row(grid, 2, "Installed", installed);
        Row(grid, 3, "Package feed", model.PackageFeedPath ?? "Not found");
        Row(grid, 4, "Toolbox", model.ToolboxCategory);

        var card = new Border { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 0, 6), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "VsToolWindowHeader");
        card.SetResourceReference(Border.BorderBrushProperty, "VsToolWindowBorder");
        return card;
    }

    private void Row(Grid grid, int row, string label, string value)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = Text(label, 12, FontWeights.Normal, "VsGrayText", new Thickness(0, 3, 8, 3));
        Grid.SetRow(name, row);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var content = Text(value, 12, FontWeights.Normal, "VsToolWindowText", new Thickness(0, 3, 0, 3));
        content.ToolTip = value;
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
    }

    private FrameworkElement ComponentList(CodeBridgeSetupViewModel model)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (model.Components.Length == 0)
            panel.Children.Add(Text("None found.", 12, FontWeights.Normal, "VsGrayText", new Thickness(0)));

        foreach (var component in model.Components)
            panel.Children.Add(Text("•  " + component, 12, FontWeights.Normal, "VsToolWindowText", new Thickness(4, 2, 0, 2)));

        return panel;
    }

    private FrameworkElement Buttons(CodeBridgeSetupViewModel model)
    {
        var panel = new WrapPanel();

        var install = Button("Install / Update", model.CanRunProjectActions, CodeBridgeSetupAction.InstallPackage, primary: true);
        var starter = Button("Add Starter Form", model.CanRunProjectActions, CodeBridgeSetupAction.AddStarterForm, primary: false);
        var remove = Button("Remove Package", model.CanRemovePackage, CodeBridgeSetupAction.RemovePackage, primary: false);
        panel.Children.Add(install);
        panel.Children.Add(starter);
        panel.Children.Add(remove);

        var tour = new Button { Content = "Take the CodeBridge Tour", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 0, 8, 8) };
        tour.Click += (_, _) => Tour.TourLauncher.TryOpen();
        panel.Children.Add(tour);
        return panel;
    }

    private Button Button(string text, bool enabled, CodeBridgeSetupAction action, bool primary)
    {
        var button = new Button
        {
            Content = text,
            IsEnabled = enabled,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 8)
        };

        if (primary)
        {
            button.Foreground = Brushes.White;
            button.Background = new SolidColorBrush(Accent);
            button.BorderBrush = new SolidColorBrush(Accent);
            button.FontWeight = FontWeights.SemiBold;
        }

        button.Click += (_, _) => ActionSelected?.Invoke(this, action);
        return button;
    }

    private FrameworkElement Heading(string text) =>
        Text(text, 13, FontWeights.SemiBold, "VsToolWindowText", new Thickness(0, 10, 0, 6));

    private static TextBlock Text(string text, double size, FontWeight weight, string brushKey, Thickness margin)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        block.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return block;
    }
}

internal sealed class CodeBridgeSetupViewModel
{
    public string? ProjectName { get; set; }

    public string? ProjectPath { get; set; }

    public string PackageId { get; set; } = string.Empty;

    public string PackageVersion { get; set; } = string.Empty;

    public string? PackageFeedPath { get; set; }

    public string? InstalledPackageVersion { get; set; }

    public string ToolboxCategory { get; set; } = string.Empty;

    public string[] Components { get; set; } = Array.Empty<string>();

    public bool CanRunProjectActions => !string.IsNullOrWhiteSpace(ProjectName) &&
        !string.IsNullOrWhiteSpace(PackageFeedPath);

    public bool CanRemovePackage => !string.IsNullOrWhiteSpace(ProjectName) &&
        !string.IsNullOrWhiteSpace(InstalledPackageVersion);
}
