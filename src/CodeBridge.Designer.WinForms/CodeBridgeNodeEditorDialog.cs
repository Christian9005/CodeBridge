using System.Globalization;
using System.Text.Json;
using CodeBridge.Flow;

namespace CodeBridge.Designer.WinForms;

internal sealed class CodeBridgeNodeEditorDialog : Form
{
    private readonly FlowBlockDefinition _definition;
    private readonly FlowNode _node;
    private readonly TabControl _tabs = new();
    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly Dictionary<string, Control> _editors = new(StringComparer.OrdinalIgnoreCase);

    public CodeBridgeNodeEditorDialog(FlowBlockDefinition definition, FlowNode node)
    {
        _definition = definition;
        _node = node;

        Text = $"{definition.DisplayName} Configuration";
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(430, 360);
        Size = new Size(520, 520);
        BackColor = DesignerTheme.Workbench;
        ForeColor = DesignerTheme.Text;
        Font = DesignerTheme.UiFont;

        var header = BuildHeader();
        _tabs.Dock = DockStyle.Fill;
        _tabs.Appearance = TabAppearance.Normal;
        _tabs.BackColor = DesignerTheme.Surface;

        AddPropertyPage("Basic", advanced: false);
        AddPropertyPage("Advanced", advanced: true);

        _okButton.Text = "OK";
        _okButton.DialogResult = DialogResult.OK;
        _okButton.Width = 92;
        ConfigureButton(_okButton);

        _cancelButton.Text = "Cancel";
        _cancelButton.DialogResult = DialogResult.Cancel;
        _cancelButton.Width = 92;
        ConfigureButton(_cancelButton);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = DesignerTheme.Surface
        };
        footer.Controls.Add(_okButton);
        footer.Controls.Add(_cancelButton);

        Controls.Add(_tabs);
        Controls.Add(header);
        DesignerTheme.ApplyDarkChrome(this);
        Controls.Add(footer);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    public IReadOnlyDictionary<string, object?> ReadParameters()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in _definition.Properties)
        {
            if (!_editors.TryGetValue(property.Name, out var editor))
                continue;

            var rawValue = editor switch
            {
                ComboBox comboBox => comboBox.SelectedItem is PropertyOptionItem item
                    ? item.Option.Value
                    : comboBox.Text,
                CheckBox checkBox => checkBox.Checked,
                NumericUpDown numeric => property.ValueKind == FlowValueKind.Number
                    ? numeric.Value
                    : decimal.ToInt32(numeric.Value),
                TextBox textBox => textBox.Text,
                _ => null
            };

            values[property.Name] = CoerceValue(property, rawValue);
        }

        return values;
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 84,
            Padding = new Padding(18, 12, 18, 8),
            BackColor = DesignerTheme.Surface
        };

        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 26,
            Text = _definition.DisplayName,
            ForeColor = DesignerTheme.Text,
            Font = DesignerTheme.TitleFont
        };

        var description = new Label
        {
            Dock = DockStyle.Fill,
            Text = _definition.Description ?? _definition.Type,
            ForeColor = DesignerTheme.MutedText,
            Font = DesignerTheme.SmallFont
        };

        panel.Controls.Add(description);
        panel.Controls.Add(title);
        return panel;
    }

    private void AddPropertyPage(string title, bool advanced)
    {
        var properties = _definition.Properties
            .Where(property => property.IsAdvanced == advanced)
            .ToList();

        if (advanced && properties.Count == 0)
            return;

        var page = new TabPage(title)
        {
            BackColor = DesignerTheme.Surface,
            ForeColor = DesignerTheme.Text,
            Padding = new Padding(14)
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = DesignerTheme.Surface
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;
        foreach (var property in properties)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var label = new Label
            {
                Text = ToDisplayName(property.Name),
                Dock = DockStyle.Fill,
                ForeColor = DesignerTheme.MutedText,
                TextAlign = ContentAlignment.MiddleLeft
            };

            var editor = CreateEditor(property);
            _editors[property.Name] = editor;
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(editor, 1, row);
            row++;
        }

        if (properties.Count == 0)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.Controls.Add(new Label
            {
                Text = "No basic parameters.",
                Dock = DockStyle.Fill,
                ForeColor = DesignerTheme.MutedText,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            grid.SetColumnSpan(grid.Controls[0], 2);
        }

        page.Controls.Add(grid);
        _tabs.TabPages.Add(page);
    }

    private Control CreateEditor(FlowPropertyDefinition property)
    {
        var value = _node.Parameters.TryGetValue(property.Name, out var current)
            ? current
            : property.DefaultValue;

        if (property.Options is { Count: > 0 })
        {
            var combo = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 0, 3)
            };
            DesignerTheme.StyleCombo(combo);

            PropertyOptionItem? selected = null;
            foreach (var option in property.Options)
            {
                var item = new PropertyOptionItem(option);
                combo.Items.Add(item);
                if (ValuesEqual(option.Value, value))
                    selected = item;
            }

            combo.SelectedItem = selected ?? combo.Items.Cast<object>().FirstOrDefault();
            return combo;
        }

        if (property.ValueKind == FlowValueKind.Boolean)
        {
            return new CheckBox
            {
                Dock = DockStyle.Left,
                Width = 22,
                Checked = ConvertToBoolean(value),
                BackColor = DesignerTheme.Surface,
                ForeColor = DesignerTheme.Text,
                Margin = new Padding(0, 6, 0, 3)
            };
        }

        if (property.ValueKind is FlowValueKind.Integer or FlowValueKind.Number)
        {
            var numeric = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -1_000_000,
                Maximum = 1_000_000,
                DecimalPlaces = property.ValueKind == FlowValueKind.Number ? 2 : 0,
                BackColor = DesignerTheme.InputBackground,
                ForeColor = DesignerTheme.Text,
                BorderStyle = BorderStyle.FixedSingle,
                Margin = new Padding(0, 3, 0, 3)
            };
            numeric.Value = Math.Clamp(ConvertToDecimal(value), numeric.Minimum, numeric.Maximum);
            return numeric;
        }

        return new TextBox
        {
            Dock = DockStyle.Fill,
            Text = FormatValue(value),
            BackColor = DesignerTheme.InputBackground,
            ForeColor = DesignerTheme.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 3, 0, 3)
        };
    }

    private static object? CoerceValue(FlowPropertyDefinition property, object? value)
    {
        if (value is null)
            return property.DefaultValue;

        return property.ValueKind switch
        {
            FlowValueKind.Boolean => ConvertToBoolean(value),
            FlowValueKind.Integer => Convert.ToInt32(value, CultureInfo.InvariantCulture),
            FlowValueKind.Number => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static bool ValuesEqual(object? left, object? right) =>
        string.Equals(FormatValue(NormalizeValue(left)), FormatValue(NormalizeValue(right)), StringComparison.OrdinalIgnoreCase);

    private static bool ConvertToBoolean(object? value)
    {
        value = NormalizeValue(value);

        if (value is bool boolean)
            return boolean;

        if (value is string text)
        {
            if (bool.TryParse(text, out var parsed))
                return parsed;

            return text == "1";
        }

        return value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private static decimal ConvertToDecimal(object? value)
    {
        value = NormalizeValue(value);

        if (value is null)
            return 0;

        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private static string FormatValue(object? value)
    {
        var normalized = NormalizeValue(value);
        return normalized switch
        {
            null => "",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => normalized.ToString() ?? ""
        };
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is not JsonElement json)
            return value;

        return json.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when json.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number => json.GetDouble(),
            JsonValueKind.String => json.GetString(),
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            _ => json.GetRawText()
        };
    }

    private static string ToDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var chars = new List<char> { char.ToUpperInvariant(name[0]) };
        foreach (var character in name.Skip(1))
        {
            if (char.IsUpper(character))
                chars.Add(' ');
            chars.Add(character);
        }

        return new string(chars.ToArray());
    }

    private static void ConfigureButton(Button button)
    {
        DesignerTheme.StyleButton(button, primary: button.DialogResult == DialogResult.OK);
    }

    private sealed class PropertyOptionItem
    {
        public PropertyOptionItem(FlowPropertyOption option)
        {
            Option = option;
        }

        public FlowPropertyOption Option { get; }

        public override string ToString() => Option.Label;
    }
}
