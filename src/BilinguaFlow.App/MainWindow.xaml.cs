using System.ComponentModel;
using System.Windows;
using BilinguaFlow.App.ViewModels;

namespace BilinguaFlow.App;

public partial class MainWindow : Window
{
    private bool _shutdownComplete;
    public MainViewModel ViewModel { get; }
    public MainWindow(MainViewModel viewModel) { InitializeComponent(); ViewModel = viewModel; DataContext = viewModel; }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        e.Cancel = true; IsEnabled = false; await ViewModel.DisposeAsync(); _shutdownComplete = true; Close();
    }
    protected override void OnInitialized(EventArgs e) { base.OnInitialized(e); Closing += OnClosing; }
}
