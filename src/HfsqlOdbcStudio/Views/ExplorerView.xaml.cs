using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HfsqlOdbcStudio.ViewModels;

namespace HfsqlOdbcStudio.Views;

public partial class ExplorerView : UserControl
{
    private ExplorerViewModel? _vm;

    public ExplorerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Tree.PreviewMouseRightButtonDown += OnPreviewRightButtonDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.HighlightRequested -= OnHighlightRequested;
        _vm = DataContext as ExplorerViewModel;
        if (_vm is not null) _vm.HighlightRequested += OnHighlightRequested;
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm?.OnSelectedNodeChanged(e.NewValue);

    private void OnHighlightRequested(object? sender, TableNodeViewModel node)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (Tree.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem item) item.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Selects the node under the mouse on right-click so the context menu acts on it.</summary>
    private static void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not TreeViewItem)
            source = source is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        if (source is TreeViewItem item) item.IsSelected = true;
    }
}
