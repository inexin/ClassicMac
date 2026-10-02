using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ClassicMac.App.ViewModels;
using ClassicMac.Core;

namespace ClassicMac.App.Views
{
    /// <summary>Labels for the diagnostics panel's controls.</summary>
    internal static class DiagnosticsLabels
    {
        public static FuncValueConverter<DiagnosticFilter, string> Filter { get; } = new(filter => filter switch
        {
            DiagnosticFilter.WarningsAndErrors => "Warnings and errors",
            DiagnosticFilter.Errors => "Errors",
            _ => "All",
        });

        /// <summary>The severity column's sort mark.</summary>
        public static FuncValueConverter<SeveritySort, string> Sort { get; } = new(sort => sort switch
        {
            SeveritySort.ErrorsFirst => "▾",
            SeveritySort.InfoFirst => "▴",
            _ => "",
        });
    }

    /// <summary>
    /// A severity's 14 px icon, so severity is never colour alone (design/boards/diagnostics.md): error a filled circle
    /// with ×, warning a filled triangle with !, info an outlined circle with i. The glyph inside a filled shape is cut
    /// out in CmPaneBackground (white in the light theme). Drawn from the theme's tokens, redrawn when it changes.
    /// </summary>
    internal sealed class SeverityIcon : Control
    {
        public static readonly StyledProperty<DiagnosticSeverity> SeverityProperty =
            AvaloniaProperty.Register<SeverityIcon, DiagnosticSeverity>(nameof(Severity));

        static SeverityIcon() => AffectsRender<SeverityIcon>(SeverityProperty);

        public SeverityIcon()
        {
            Width = Height = 14;
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        }

        public DiagnosticSeverity Severity
        {
            get => GetValue(SeverityProperty);
            set => SetValue(SeverityProperty, value);
        }

        private IBrush Token(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

        public override void Render(DrawingContext context)
        {
            var knockout = new Pen(Token("CmPaneBackground"), 1.6, lineCap: PenLineCap.Round);
            switch (Severity)
            {
                case DiagnosticSeverity.Error:
                    context.DrawEllipse(Token("CmError"), null, new Point(7, 7), 6.5, 6.5);
                    context.DrawLine(knockout, new Point(4.6, 4.6), new Point(9.4, 9.4));
                    context.DrawLine(knockout, new Point(9.4, 4.6), new Point(4.6, 9.4));
                    break;
                case DiagnosticSeverity.Warning:
                    var triangle = new StreamGeometry();
                    using (var g = triangle.Open())
                    {
                        g.BeginFigure(new Point(7, 0.5), true);
                        g.LineTo(new Point(13.6, 13));
                        g.LineTo(new Point(0.4, 13));
                        g.EndFigure(true);
                    }
                    context.DrawGeometry(Token("CmWarning"), null, triangle);
                    context.DrawLine(knockout, new Point(7, 5), new Point(7, 8.4));
                    context.DrawEllipse(knockout.Brush, null, new Point(7, 10.8), 0.9, 0.9);
                    break;
                default:
                    var info = Token("CmInfo");
                    context.DrawEllipse(null, new Pen(info, 1.3), new Point(7, 7), 6.2, 6.2);
                    context.DrawEllipse(info, null, new Point(7, 4.2), 0.9, 0.9);
                    context.DrawLine(new Pen(info, 1.6, lineCap: PenLineCap.Round), new Point(7, 6.4), new Point(7, 10.2));
                    break;
            }
        }
    }

    /// <summary>The main grid's diagnostics row follows the panel's height, and a dragged splitter sets it.</summary>
    internal static class DiagnosticsRow
    {
        public static void Bind(RowDefinition splitter, RowDefinition row, DiagnosticsPanel panel)
        {
            void Apply()
            {
                splitter.Height = new GridLength(panel.IsExpanded ? 5 : 0);
                row.Height = new GridLength(panel.PanelHeight);
            }
            Apply();
            panel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(DiagnosticsPanel.PanelHeight) or nameof(DiagnosticsPanel.IsExpanded)) Apply();
            };
            row.PropertyChanged += (_, e) =>
            {
                if (e.Property == RowDefinition.HeightProperty && row.Height.IsAbsolute && panel.IsExpanded) panel.PanelHeight = row.Height.Value;
            };
        }
    }
}
