using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhotoManager.Application.Catalog;
using PhotoManager.Application.Collections;
using PhotoManager.Application.Metadata;
using PhotoManager.Application.Microstock;
using PhotoManager.Application.Navigation;
using PhotoManager.Infrastructure.Configuration;
using PhotoManager.Infrastructure.Images;
using PhotoManager.Infrastructure.Files;
using PhotoManager.Infrastructure.Logging;
using PhotoManager.Persistence;

namespace PhotoManager.Wpf;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private ServiceProvider? _services;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstanceMutex = new Mutex(true, "PhotoManager.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        var paths = new ApplicationPaths();
        paths.EnsureDirectories();
        var collection = new ServiceCollection();
        collection.AddSingleton(paths);
        collection.AddSingleton<LocalApplicationConfiguration>();
        collection.AddSingleton<ICatalogRepository>(_ => new SqliteCatalogRepository(Path.Combine(paths.Data, "photomanager.db")));
        collection.AddSingleton<IOrganizationRepository>(services => (IOrganizationRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<ICollectionRepository>(services => (ICollectionRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<ICollectionService, CollectionService>();
        collection.AddSingleton<ICatalogService, CatalogService>();
        collection.AddSingleton<IOrganizationService, OrganizationService>();
        collection.AddSingleton<IFileOperationService, FileOperationService>();
        collection.AddSingleton<PhotoManager.Application.Transfer.ITransferRepository>(services => (PhotoManager.Application.Transfer.ITransferRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<PhotoManager.Application.Transfer.ITransferOrganizer, TransferOrganizer>();
        collection.AddSingleton<PhotoManager.Application.Transfer.ITransferSettings, TransferSettingsStore>();
        collection.AddSingleton<PhotoManager.Application.Library.ILibrarySettings, LibrarySettingsStore>();
        collection.AddSingleton(services => new PhotoManager.Wpf.Views.LibraryProServices(
            (PhotoManager.Application.Library.ILibraryRepository)services.GetRequiredService<ICatalogRepository>(),
            services.GetRequiredService<PhotoManager.Application.Library.ILibrarySettings>(),
            new PhotoManager.Application.Library.PhotoAnalysisService((PhotoManager.Application.Library.ILibraryRepository)services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<IThumbnailService>(), new PhotoManager.Infrastructure.Images.GrayImageReader()),
            new PhotoManager.Application.Ai.AiIndexService(new PhotoManager.Application.Ai.AiEngineRegistry(), (PhotoManager.Application.Ai.IAiRepository)services.GetRequiredService<ICatalogRepository>()),
            (PhotoManager.Application.Ai.IAiRepository)services.GetRequiredService<ICatalogRepository>(),
            new PhotoManager.Infrastructure.Ai.AiModelManager(Path.Combine(paths.Data, "Models")),
            new ExportService(),
            new PhotoManager.Infrastructure.Location.MapTileProvider(Path.Combine(paths.Cache, "Tiles")),
            new BackupVerifier()));
        collection.AddSingleton<IMetadataReader, PhotoManager.Infrastructure.Metadata.MetadataExtractorReader>();
        collection.AddSingleton<IMetadataWriter>(_ => new PhotoManager.Infrastructure.Metadata.CompositeMetadataWriter(new PhotoManager.Infrastructure.Metadata.JpegMetadataWriter(), new PhotoManager.Infrastructure.Metadata.SidecarXmpWriter()));
        collection.AddSingleton<IMetadataVersionRepository>(services => (IMetadataVersionRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<IMetadataEditService, MetadataEditService>();
        collection.AddSingleton<IMetadataPresetRepository>(services => (IMetadataPresetRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<IBatchMetadataService, BatchMetadataService>();
        collection.AddSingleton<IValidationProfileRepository>(services => (IValidationProfileRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<IMicrostockEvaluationService, MicrostockEvaluationService>();
        collection.AddSingleton<IUploadHistoryRepository>(services => (IUploadHistoryRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<IUploadHistoryService>(services => new UploadHistoryService(services.GetRequiredService<IUploadHistoryRepository>(), services.GetRequiredService<ICatalogRepository>()));
        collection.AddSingleton<IDuplicateRepository>(services => (IDuplicateRepository)services.GetRequiredService<ICatalogRepository>());
        collection.AddSingleton<IFileHashService, Sha256FileHashService>();
        collection.AddSingleton<IDuplicateDetectionService>(services => new DuplicateDetectionService(services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<IDuplicateRepository>(), services.GetRequiredService<IFileHashService>()));
        collection.AddSingleton<ThumbnailService>(_ => new ThumbnailService(Path.Combine(paths.Cache, "Thumbnails")));
        collection.AddSingleton<IThumbnailService>(services => services.GetRequiredService<ThumbnailService>());
        collection.AddSingleton<PhotoManager.Application.Transfer.IFileThumbnailService>(services => services.GetRequiredService<ThumbnailService>());
        collection.AddSingleton<IPhotoInfoReader>(services => services.GetRequiredService<ThumbnailService>());
        collection.AddSingleton<PhotoManager.Application.Location.IGpsReader, PhotoManager.Infrastructure.Location.GpsReader>();
        collection.AddSingleton<PhotoManager.Application.Location.IGeocoder>(_ => new PhotoManager.Infrastructure.Location.NominatimGeocoder(PhotoManager.Infrastructure.Location.NominatimGeocoder.CreateClient()));
        collection.AddSingleton<PhotoManager.Application.Location.ILocationService>(services =>
        {
            var settings = services.GetRequiredService<LocalApplicationConfiguration>();
            return new PhotoManager.Application.Location.LocationService(services.GetRequiredService<ICatalogRepository>(), services.GetRequiredService<PhotoManager.Application.Location.IGpsReader>(), services.GetRequiredService<PhotoManager.Application.Location.IGeocoder>(),
                () => settings.Get("geocoding.consent") == "true", granted => settings.Set("geocoding.consent", granted ? "true" : "false"));
        });
        collection.AddSingleton<INavigationService, NavigationService>();
        collection.AddSingleton<MainViewModel>();
        collection.AddSingleton<MainWindow>();
        collection.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddProvider(new FileLoggerProvider(paths.Logs));
        });
        _services = collection.BuildServiceProvider();
        Services = _services;
        DispatcherUnhandledException += (_, args) =>
        {
            var logger = _services.GetRequiredService<ILogger<App>>();
            logger.LogError(args.Exception, "Erro não tratado na interface.");
            MessageBox.Show("Ocorreu um erro inesperado. Consulte o log para mais detalhes.", "PhotoManager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        await _services.GetRequiredService<ICatalogRepository>().InitializeAsync();
        _services.GetRequiredService<MainWindow>().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
