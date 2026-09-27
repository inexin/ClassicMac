using Avalonia.Data.Converters;
using Avalonia.Media;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Views
{
    // Simple path icons for the tree, one per node kind (no image assets).
    internal static class NodeIcons
    {
        private static readonly Geometry Disk = Geometry.Parse("M2,1 H12 L14,3 V15 H2 Z M4,1 V6 H11 V1 M4,9 H12 V14 H4 Z");
        private static readonly Geometry Folder = Geometry.Parse("M1,3 H6 L8,5 H15 V14 H1 Z");
        private static readonly Geometry File = Geometry.Parse("M3,1 H10 L13,4 V15 H3 Z M10,1 V4 H13");
        private static readonly Geometry Container = Geometry.Parse("M2,4 H14 V15 H2 Z M1,1 H15 V4 H1 Z M6,7 H10");
        private static readonly Geometry Type = Geometry.Parse("M1,2 H15 V6 H1 Z M1,8 H15 V14 H1 Z");
        private static readonly Geometry Resource = Geometry.Parse("M4,4 H12 V12 H4 Z");
        private static readonly Geometry Loading = Geometry.Parse("M2,8 A1,1 0 1 1 4,8 A1,1 0 1 1 2,8 M7,8 A1,1 0 1 1 9,8 A1,1 0 1 1 7,8 M12,8 A1,1 0 1 1 14,8 A1,1 0 1 1 12,8");

        public static FuncValueConverter<NodeKind, Geometry> Converter { get; } = new(kind => kind switch
        {
            NodeKind.Input => Disk,
            NodeKind.Container => Container,
            NodeKind.Folder => Folder,
            NodeKind.File => File,
            NodeKind.ResourceType => Type,
            NodeKind.Resource => Resource,
            _ => Loading,
        });
    }
}
