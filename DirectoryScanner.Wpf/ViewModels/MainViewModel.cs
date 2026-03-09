using System.Collections.ObjectModel;
using System.Windows;
using DirectoryScanner.Core.Models;
using DirectoryScanner.Core.Services;
using DirectoryScanner.Wpf.Infra;
using Microsoft.Win32; 

namespace DirectoryScanner.Wpf.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly ScannerService _scannerService;
    private CancellationTokenSource? _cts;

    private bool _isScanning;
    private string _statusText = "Ready";
    private ObservableCollection<DirectoryItem> _items = new();

    public ObservableCollection<DirectoryItem> Items
    {
        get => _items;
        set { _items = value; OnPropertyChanged(); }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set { _isScanning = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    public RelayCommand SelectFolderCommand { get; }
    public RelayCommand CancelCommand { get; }

    public MainViewModel()
    {
        _scannerService = new ScannerService(maxThreads: 10);
        SelectFolderCommand = new RelayCommand(ExecuteSelectFolder, _ => !IsScanning);
        CancelCommand = new RelayCommand(ExecuteCancel, _ => IsScanning);
    }

    private async void ExecuteSelectFolder(object? obj)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true)
        {
            var path = dialog.FolderName;
            StatusText = $"Scanning: {path}...";
            IsScanning = true;
            Items.Clear();

            _cts = new CancellationTokenSource();

            try
            {
                var result = await Task.Run(() => _scannerService.ScanAsync(path, _cts.Token));

                Items.Add(result);
                StatusText = _cts.IsCancellationRequested ? "Scan Cancelled" : "Scan Completed";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}");
                StatusText = "Error";
            }
            finally
            {
                IsScanning = false;
                _cts.Dispose();
                _cts = null;
            }
        }
    }

    private void ExecuteCancel(object? obj)
    {
        _cts?.Cancel();
        StatusText = "Cancelling...";
    }
}