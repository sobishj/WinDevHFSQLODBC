using System.ComponentModel;
using HfsqlOdbcStudio.ViewModels;

namespace HfsqlOdbcStudio.Views;

public partial class ConnectionWindow
{
    private readonly ConnectionViewModel _vm;
    private bool _syncingPassword;

    public ConnectionWindow(ConnectionViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // PasswordBox.Password is not bindable; keep it in sync manually.
        PasswordInput.Password = _vm.Password;
        PasswordInput.PasswordChanged += (_, _) =>
        {
            if (_syncingPassword) return;
            _syncingPassword = true;
            _vm.Password = PasswordInput.Password;
            _syncingPassword = false;
        };
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        _vm.CloseRequested += OnCloseRequested;
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.CloseRequested -= OnCloseRequested;
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.Password) || _syncingPassword) return;
        if (PasswordInput.Password == _vm.Password) return;
        _syncingPassword = true;
        PasswordInput.Password = _vm.Password;
        _syncingPassword = false;
    }

    private void OnCloseRequested(object? sender, bool success)
    {
        if (!IsVisible) return;
        DialogResult = success;
    }
}
