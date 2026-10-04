using System.Reflection;
using ClassicMac.App.ViewModels;

namespace ClassicMac.App.Tests;

// The main window's parts depend on the roles they use (IAppSelection, IAppServices, IAppView, IAppParts), not on the
// whole MainViewModel.
public class ViewModelDependencyTests
{
    [Fact]
    public void No_part_of_the_main_window_takes_the_whole_MainViewModel()
    {
        var takers = typeof(MainViewModel).Assembly.GetTypes()
            .Where(t => t != typeof(MainViewModel))
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(MainViewModel))))
            .Select(t => t.Name)
            .Order()
            .ToList();
        Assert.Empty(takers);
    }

    [Fact]
    public void MainViewModel_plays_every_role()
    {
        var model = new MainViewModel();
        Assert.IsAssignableFrom<IAppSelection>(model);
        Assert.IsAssignableFrom<IAppServices>(model);
        Assert.IsAssignableFrom<IAppView>(model);
        Assert.IsAssignableFrom<IAppParts>(model);
    }
}
