using System.Windows;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

public partial class CompareWindow : Window
{
    private readonly CompareViewModel _vm;

    public CompareWindow(CompareViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (_vm.Apply()) DialogResult = true;
    }
}
