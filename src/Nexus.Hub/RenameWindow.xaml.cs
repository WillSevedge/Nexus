using System.Windows;
using Nexus.Hub.ViewModels;

namespace Nexus.Hub;

/// <summary>Rename &amp; renumber with a live preview (see <see cref="RenameViewModel"/>).</summary>
public partial class RenameWindow : Window
{
    public RenameWindow(RenameViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Staged += () => DialogResult = true;
    }
}
