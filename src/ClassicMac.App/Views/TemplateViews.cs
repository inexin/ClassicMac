using Avalonia;
using Avalonia.Data.Converters;

namespace ClassicMac.App.Views
{
    // The template form's view helpers (boards/template-form.md).
    internal static class TemplateViews
    {
        /// <summary>A template panel line's indent (DIPs) as a left margin.</summary>
        public static FuncValueConverter<int, Thickness> Indent { get; } = new(indent => new Thickness(indent, 0, 0, 0));
    }
}
