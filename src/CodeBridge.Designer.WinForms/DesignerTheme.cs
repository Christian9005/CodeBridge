namespace CodeBridge.Designer.WinForms;

/// <summary>
/// The designer's colours and type. The palette is the dark scheme shared with the Blazor components, the MAUI sample and the
/// Visual Studio editor: graphite neutrals, blue for selection and values, green for live / connected, amber for caution,
/// red for faults. A state is never shown by colour alone.
/// </summary>
internal static class DesignerTheme
{
    public static readonly Color Workbench = Color.FromArgb(14, 18, 23);
    public static readonly Color Canvas = Color.FromArgb(11, 15, 20);
    public static readonly Color Surface = Color.FromArgb(23, 28, 35);
    public static readonly Color SurfaceRaised = Color.FromArgb(31, 37, 46);
    public static readonly Color SurfaceSunken = Color.FromArgb(17, 22, 28);
    public static readonly Color InputBackground = Color.FromArgb(17, 22, 28);
    public static readonly Color ButtonBackground = Color.FromArgb(31, 37, 46);
    public static readonly Color OutputBackground = Color.FromArgb(11, 15, 20);
    public static readonly Color SurfaceHover = Color.FromArgb(41, 49, 61);
    public static readonly Color SurfaceSelected = Color.FromArgb(24, 64, 112);
    public static readonly Color Border = Color.FromArgb(44, 52, 64);
    public static readonly Color BorderSubtle = Color.FromArgb(33, 40, 50);
    public static readonly Color GridMajor = Color.FromArgb(34, 41, 51);
    public static readonly Color GridMinor = Color.FromArgb(23, 29, 37);
    public static readonly Color Text = Color.FromArgb(231, 237, 244);
    public static readonly Color MutedText = Color.FromArgb(155, 167, 182);
    public static readonly Color DisabledText = Color.FromArgb(108, 119, 135);
    public static readonly Color PortText = Color.FromArgb(195, 204, 216);
    public static readonly Color Copper = Color.FromArgb(90, 169, 255);
    public static readonly Color AccentSoft = Color.FromArgb(143, 195, 255);
    public static readonly Color AccentStrong = Color.FromArgb(10, 95, 194);
    public static readonly Color Signal = Color.FromArgb(53, 201, 140);
    public static readonly Color Warning = Color.FromArgb(242, 177, 60);
    public static readonly Color Error = Color.FromArgb(255, 114, 133);
    public static readonly Color Magenta = Color.FromArgb(194, 86, 255);

    public static Font UiFont { get; } = new("Segoe UI", 9F, FontStyle.Regular);
    public static Font SmallFont { get; } = new("Segoe UI", 8.25F, FontStyle.Regular);
    public static Font TitleFont { get; } = new("Segoe UI Semibold", 9.5F, FontStyle.Regular);
    public static Font SectionFont { get; } = new("Segoe UI Semibold", 8F, FontStyle.Regular);
    public static Font MonoFont { get; } = new("Consolas", 9F, FontStyle.Regular);

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? subAppName, string? subIdList);

    /// <summary>
    /// Asks Windows for its dark rendering of the parts WinForms leaves to the system: scrollbars, tree and list views, grid headers,
    /// spinners and the frame of a combo box. Applies to every control now inside <paramref name="root"/> and to those added later.
    /// </summary>
    public static void ApplyDarkChrome(Control root)
    {
        static void Theme(Control control)
        {
            var name = control is ComboBox ? "DarkMode_CFD" : "DarkMode_Explorer";
            if (control.IsHandleCreated)
                _ = SetWindowTheme(control.Handle, name, null);
        }

        static void Visit(Control control)
        {
            if (control is TreeView or TextBoxBase or DataGridView or ListBox or ListView or NumericUpDown or ComboBox or ScrollableControl and not TableLayoutPanel)
            {
                control.HandleCreated += (_, _) => Theme(control);
                Theme(control);
            }

            foreach (Control child in control.Controls)
                Visit(child);

            control.ControlAdded += (_, e) =>
            {
                if (e.Control is not null)
                    Visit(e.Control);
            };
        }

        Visit(root);
    }

    /// <summary>Flat dark button; the primary one carries the accent, the rest stay neutral.</summary>
    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? AccentStrong : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(24, 112, 214) : SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(8, 78, 160) : SurfaceSelected;
        button.BackColor = primary ? AccentStrong : ButtonBackground;
        button.ForeColor = primary ? Color.White : Text;
        button.Cursor = Cursors.Hand;
    }

    /// <summary>
    /// A drop-down list that really is dark: the system draws a flat combo box with a white face whatever BackColor says,
    /// so the items (and the selected value in the box) are painted here.
    /// </summary>
    public static void StyleCombo(ComboBox combo)
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = InputBackground;
        combo.ForeColor = Border; // the flat style draws its frame in ForeColor; the text is painted below in Text
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.DrawItem -= DrawComboItem;
        combo.DrawItem += DrawComboItem;
    }

    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo)
            return;

        var inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
        var highlighted = inList && (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(highlighted ? SurfaceSelected : InputBackground))
            e.Graphics.FillRectangle(back, e.Bounds);

        if (e.Index < 0 || e.Index >= combo.Items.Count)
            return;

        var color = combo.Enabled ? Text : DisabledText;
        TextRenderer.DrawText(
            e.Graphics,
            combo.GetItemText(combo.Items[e.Index]),
            combo.Font,
            Rectangle.Inflate(e.Bounds, -3, 0),
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
