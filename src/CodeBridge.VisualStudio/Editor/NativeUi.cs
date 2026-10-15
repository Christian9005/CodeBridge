#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace CodeBridge.VisualStudio.Editor
{
    internal enum IconKind
    {
        Run,
        Stop,
        Undo,
        Redo,
        Refresh,
        Upload,
        Connect,
        Delete,
        Copy,
        Paste,
        Clear,
        Output,
        ZoomIn,
        ZoomOut,
        Fit,
        Arrange,
        Export,
        Wifi,
        Board,
        Monitor,
        Home,
        Mesh
    }

    /// <summary>
    /// Theme-aware icons (KnownMonikers through CrispImage) with a Segoe MDL2 glyph fallback when the editor runs
    /// outside Visual Studio (unit tests, designer). Control styling lives in EditorTheme.xaml.
    /// </summary>
    internal static class NativeUi
    {
        private static bool _hostAvailable = true;

        /// <summary>Gives a code-built window or control the editor's control styles and the Visual Studio theme brushes.</summary>
        public static void ApplyEditorTheme(FrameworkElement element)
        {
            element.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CodeBridge.VisualStudio;component/Editor/EditorTheme.xaml", UriKind.Relative) });
            foreach (var pair in new[]
            {
                ("VsWindowBackground", "#1e1e1e"), ("VsToolWindowBackground", "#252526"), ("VsToolWindowHeader", "#2d2d30"),
                ("VsToolWindowBorder", "#3f3f46"), ("VsToolWindowText", "#f1f1f1"), ("VsGrayText", "#999999"),
                ("VsInputBackground", "#333337"), ("VsInputBorder", "#434346"), ("VsHighlight", "#007acc")
            })
            {
                element.Resources[pair.Item1] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(pair.Item2));
            }

            VsTheme.Bind(element.Resources);
        }

        public static FrameworkElement CreateIcon(IconKind kind)
        {
            if (_hostAvailable)
            {
                try
                {
                    return CreateCrispIcon(kind);
                }
                catch (Exception ex) when (ex is IOException || ex is TypeLoadException || ex is InvalidOperationException)
                {
                    _hostAvailable = false;
                }
            }

            return new TextBlock
            {
                Text = Glyph(kind),
                FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI Symbol"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static FrameworkElement CreateCrispIcon(IconKind kind)
        {
            var glyphOnly = kind == IconKind.ZoomIn || kind == IconKind.ZoomOut || kind == IconKind.Fit || kind == IconKind.Arrange || kind == IconKind.Export || kind == IconKind.Wifi || kind == IconKind.Board || kind == IconKind.Monitor || kind == IconKind.Home || kind == IconKind.Mesh;
            if (glyphOnly)
            {
                return new TextBlock
                {
                    Text = Glyph(kind),
                    FontFamily = new FontFamily("Segoe MDL2 Assets, Segoe UI Symbol"),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var image = new CrispImage
            {
                Moniker = Moniker(kind),
                Width = 16,
                Height = 16
            };
            image.SetResourceReference(ImageThemingUtilities.ImageBackgroundColorProperty, EnvironmentColors.ToolWindowBackgroundColorKey);
            return image;
        }

        private static ImageMoniker Moniker(IconKind kind)
        {
            switch (kind)
            {
                case IconKind.Run: return KnownMonikers.Run;
                case IconKind.Stop: return KnownMonikers.Stop;
                case IconKind.Undo: return KnownMonikers.Undo;
                case IconKind.Redo: return KnownMonikers.Redo;
                case IconKind.Refresh: return KnownMonikers.Refresh;
                case IconKind.Upload: return KnownMonikers.Upload;
                case IconKind.Connect: return KnownMonikers.USB;
                case IconKind.Delete: return KnownMonikers.Cancel;
                case IconKind.Copy: return KnownMonikers.Copy;
                case IconKind.Paste: return KnownMonikers.Paste;
                case IconKind.Clear: return KnownMonikers.ClearWindowContent;
                case IconKind.Output: return KnownMonikers.Console;
                default: return KnownMonikers.QuestionMark;
            }
        }

        private static string Glyph(IconKind kind)
        {
            switch (kind)
            {
                case IconKind.Run: return "";
                case IconKind.Stop: return "";
                case IconKind.Undo: return "";
                case IconKind.Redo: return "";
                case IconKind.Refresh: return "";
                case IconKind.Upload: return "";
                case IconKind.Connect: return "";
                case IconKind.Delete: return "";
                case IconKind.Copy: return "";
                case IconKind.Paste: return "";
                case IconKind.Clear: return "";
                case IconKind.Output: return "";
                case IconKind.ZoomIn: return "";
                case IconKind.ZoomOut: return "";
                case IconKind.Fit: return "";
                case IconKind.Arrange: return "\uE8FD";
                case IconKind.Wifi: return "\uE701";
                case IconKind.Board: return "\uE950";
                case IconKind.Monitor: return "\uE756";
                case IconKind.Home: return "";
                case IconKind.Mesh: return "";
                case IconKind.Export: return "";
                default: return "";
            }
        }
    }
}
