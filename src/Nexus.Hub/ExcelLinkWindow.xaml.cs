using System.Windows;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

public partial class ExcelLinkWindow : Window
{
    private readonly ExcelLinkViewModel _vm;

    public ExcelLinkWindow(ExcelLinkViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.TryAccept()) DialogResult = true;
    }
}
