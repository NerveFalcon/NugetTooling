using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NuGet.Packaging.Core;
using NuGet.Common;
using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Frameworks;
using NuGet.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NuGet.Downloader.Services;

public class NuGetService
{
    private readonly SourceRepository _repository;
    private readonly ILogger<NuGetService> _logger;
    private readonly NuGetLogger _nugetLogger;
    private readonly string _packagesPath;
    private NuGetFramework _targetFramework;

    public NuGetService(ILogger<NuGetService> logger)
    {
        _logger = logger;
        var settings = Settings.LoadDefaultSettings(null);
        var sourceProvider = new PackageSourceProvider(settings);
        var sources = sourceProvider.LoadPackageSources();
        var source = sources.FirstOrDefault() ?? new PackageSource("https://api.nuget.org/v3/index.json");
        _repository = Repository.Factory.GetCoreV3(source);
        _packagesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NuGet.Downloader", "packages");
        _targetFramework = NuGetFramework.ParseFrameworkName("net9.0", DefaultFrameworkNameProvider.Instance);
        Directory.CreateDirectory(_packagesPath);
        _nugetLogger = new NuGetLogger(logger);
    }

    public void SetTargetFramework(string framework)
    {
        _targetFramework = NuGetFramework.ParseFolder(framework);
        _logger.LogInformation("Установлен целевой фреймворк: {Framework}", framework);
    }

    public async Task<IEnumerable<IPackageSearchMetadata>> SearchPackagesAsync(string searchTerm)
    {
        try
        {
            var searchResource = await _repository.GetResourceAsync<PackageSearchResource>();
            var searchResults = await searchResource.SearchAsync(
                searchTerm,
                new SearchFilter(includePrerelease: false),
                0, 20,
                _nugetLogger,
                CancellationToken.None);

            return searchResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching packages");
            throw;
        }
    }

    public async Task<IEnumerable<NuGetVersion>> GetPackageVersionsAsync(string packageId)
    {
        try
        {
            var metadataResource = await _repository.GetResourceAsync<PackageMetadataResource>();
            var sourceCacheContext = new SourceCacheContext();
            var metadata = await metadataResource.GetMetadataAsync(
                packageId,
                includePrerelease: false,
                includeUnlisted: false,
                sourceCacheContext,
                _nugetLogger,
                CancellationToken.None);

            return metadata.Select(m => m.Identity.Version).OrderByDescending(v => v);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting package versions");
            throw;
        }
    }

    public async Task<byte[]> DownloadPackageAsync(string packageId, NuGetVersion version)
    {
        try
        {
            var downloadResource = await _repository.GetResourceAsync<DownloadResource>();
            var downloadContext = new PackageDownloadContext(
                new SourceCacheContext(),
                Directory.GetCurrentDirectory(),
                true);

            var downloadResult = await downloadResource.GetDownloadResourceResultAsync(
                new PackageIdentity(packageId, version),
                downloadContext,
                string.Empty,
                _nugetLogger,
                CancellationToken.None);

            using var memoryStream = new MemoryStream();
            await downloadResult.PackageStream.CopyToAsync(memoryStream);
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading package");
            throw;
        }
    }

    public async Task<Dictionary<string, byte[]>> DownloadPackageWithDependenciesAsync(string packageId, NuGetVersion version)
    {
        try
        {
            var result = new Dictionary<string, byte[]>();
            var processedPackages = new HashSet<string>();
            var packagesToProcess = new Queue<(string Id, NuGetVersion Version)>();
            packagesToProcess.Enqueue((packageId, version));

            while (packagesToProcess.Count > 0)
            {
                var (currentId, currentVersion) = packagesToProcess.Dequeue();
                var packageKey = $"{currentId}.{currentVersion}";

                if (processedPackages.Contains(packageKey))
                    continue;

                processedPackages.Add(packageKey);

                // Скачиваем текущий пакет
                var packageBytes = await DownloadPackageAsync(currentId, currentVersion);
                result[packageKey] = packageBytes;

                // Получаем зависимости пакета
                var dependencies = await GetPackageDependenciesAsync(currentId, currentVersion);
                foreach (var dependency in dependencies)
                {
                    var dependencyKey = $"{dependency.Id}.{dependency.Version}";
                    if (!processedPackages.Contains(dependencyKey))
                    {
                        packagesToProcess.Enqueue((dependency.Id, dependency.Version));
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading package with dependencies");
            throw;
        }
    }

    private async Task<IEnumerable<PackageDependency>> GetPackageDependenciesAsync(string packageId, NuGetVersion version)
    {
        try
        {
            var metadataResource = await _repository.GetResourceAsync<PackageMetadataResource>();
            var sourceCacheContext = new SourceCacheContext();
            var metadata = await metadataResource.GetMetadataAsync(
                packageId,
                includePrerelease: false,
                includeUnlisted: false,
                sourceCacheContext,
                _nugetLogger,
                CancellationToken.None);

            var packageMetadata = metadata.FirstOrDefault(m => m.Identity.Version == version);
            if (packageMetadata == null)
                return [];

            // Получаем все наборы зависимостей для целевого фреймворка
            var dependencySets = packageMetadata.DependencySets
                .Where(ds => ds.TargetFramework == null || ds.TargetFramework.Framework == _targetFramework.Framework)
                .OrderByDescending(ds => ds.TargetFramework?.Version ?? new Version(0, 0, 0));

            if (!dependencySets.Any())
                return [];

            // Берем самый подходящий набор зависимостей
            var bestDependencySet = dependencySets.First();

            return bestDependencySet.Packages
                .Where(p => p.VersionRange != null)
                .Where(p => !IsRuntimeDependency(p.Id))
                .Select(p => new PackageDependency
                {
                    Id = p.Id,
                    Version = p.VersionRange.MinVersion
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting package dependencies");
            throw;
        }
    }

    private bool IsRuntimeDependency(string packageId)
    {
        // Исключаем runtime-зависимости
        return packageId.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase) ||
               packageId.Contains(".runtime.", StringComparison.OrdinalIgnoreCase) ||
               packageId.EndsWith(".runtime", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<PackageDetails> GetPackageDetailsAsync(string packageId, NuGetVersion version)
    {
        try
        {
            var metadataResource = await _repository.GetResourceAsync<PackageMetadataResource>();
            var sourceCacheContext = new SourceCacheContext();
            var metadata = await metadataResource.GetMetadataAsync(
                packageId,
                includePrerelease: false,
                includeUnlisted: false,
                sourceCacheContext,
                _nugetLogger,
                CancellationToken.None);

            var packageMetadata = metadata.FirstOrDefault(m => m.Identity.Version == version);
            if (packageMetadata == null)
                return new PackageDetails();

            // Получаем все доступные фреймворки
            var frameworks = packageMetadata.DependencySets
                .Where(ds => ds.TargetFramework != null)
                .Select(ds => ds.TargetFramework!.GetShortFolderName())
                .Distinct()
                .OrderBy(f => f);

            // Получаем зависимости для выбранного фреймворка
            var dependencies = await GetPackageDependenciesAsync(packageId, version);

            return new PackageDetails
            {
                AvailableFrameworks = frameworks,
                Dependencies = dependencies
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting package details");
            throw;
        }
    }
}

public class PackageDependency
{
    public string Id { get; set; } = "";
    public NuGetVersion Version { get; set; } = new NuGetVersion("1.0.0");
}

public class PackageDetails
{
    public IEnumerable<string> AvailableFrameworks { get; set; } = [];
    public IEnumerable<PackageDependency> Dependencies { get; set; } = [];
}

public class NuGetLogger : NuGet.Common.ILogger
{
    private readonly ILogger<NuGetService> _logger;

    public NuGetLogger(ILogger<NuGetService> logger)
    {
        _logger = logger;
    }

    public void LogDebug(string data) => _logger.LogDebug(data);
    public void LogVerbose(string data) => _logger.LogDebug(data);
    public void LogInformation(string data) => _logger.LogInformation(data);
    public void LogMinimal(string data) => _logger.LogInformation(data);
    public void LogWarning(string data) => _logger.LogWarning(data);
    public void LogError(string data) => _logger.LogError(data);
    public void LogInformationSummary(string data) => _logger.LogInformation(data);
    public void Log(NuGet.Common.LogLevel level, string data) => _logger.Log(ConvertLogLevel(level), data);
    public Task LogAsync(NuGet.Common.LogLevel level, string data) => Task.CompletedTask;
    public void Log(ILogMessage message) => _logger.Log(ConvertLogLevel(message.Level), message.Message);
    public Task LogAsync(ILogMessage message) => Task.CompletedTask;

    private static Microsoft.Extensions.Logging.LogLevel ConvertLogLevel(NuGet.Common.LogLevel level) => level switch
    {
        NuGet.Common.LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
        NuGet.Common.LogLevel.Verbose => Microsoft.Extensions.Logging.LogLevel.Debug,
        NuGet.Common.LogLevel.Information => Microsoft.Extensions.Logging.LogLevel.Information,
        NuGet.Common.LogLevel.Minimal => Microsoft.Extensions.Logging.LogLevel.Information,
        NuGet.Common.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
        NuGet.Common.LogLevel.Error => Microsoft.Extensions.Logging.LogLevel.Error,
        _ => Microsoft.Extensions.Logging.LogLevel.Information
    };
} 