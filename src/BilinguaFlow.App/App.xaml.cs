using System.IO;
using System.Windows;
using BilinguaFlow.App.ViewModels;
using BilinguaFlow.Audio;
using BilinguaFlow.Asr;
using BilinguaFlow.Core.Audio;
using BilinguaFlow.Infrastructure;
using BilinguaFlow.Core.Speech;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BilinguaFlow.App;

public partial class App : Application
{
    private readonly IHost _host = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.AddDebug())
        .ConfigureServices(services =>
        {
            services.AddSingleton<IAudioDeviceService, WasapiAudioDeviceService>();
            services.AddSingleton<IAudioCaptureSessionFactory, AudioCaptureSessionFactory>();
            services.AddSingleton<ISpeechRecognitionService, SenseVoiceSpeechRecognitionService>();
            services.AddSingleton<ITranscriptionSessionFactory, TranscriptionSessionFactory>();
            services.AddSingleton<ISystemClock, SystemClock>();
            services.AddSingleton<IRecordingPathFactory>(_ => new RecordingPathFactory(Path.Combine(AppContext.BaseDirectory, "recordings")));
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
        }).Build();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        await _host.StartAsync();
        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        await window.ViewModel.InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync(TimeSpan.FromSeconds(5));
        _host.Dispose();
        base.OnExit(e);
    }
}
