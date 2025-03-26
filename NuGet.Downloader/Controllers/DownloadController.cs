using Microsoft.AspNetCore.Mvc;
using NuGet.Downloader.Services;
using NuGet.Versioning;

namespace NuGet.Downloader.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DownloadController : ControllerBase
{
    private readonly NuGetService _nuGetService;
    private readonly ILogger<DownloadController> _logger;

    public DownloadController(NuGetService nuGetService, ILogger<DownloadController> logger)
    {
        _nuGetService = nuGetService;
        _logger = logger;
    }

    [HttpGet("{packageId}/{version}")]
    public async Task<IActionResult> DownloadPackage(string packageId, string version, [FromQuery] string? framework = null)
    {
        try
        {
            if (!NuGetVersion.TryParse(version, out var nugetVersion))
            {
                return BadRequest("Invalid version format");
            }

            if (!string.IsNullOrEmpty(framework))
            {
                _nuGetService.SetTargetFramework(framework);
            }

            var packageBytes = await _nuGetService.DownloadPackageAsync(packageId, nugetVersion);
            return File(packageBytes, "application/octet-stream", $"{packageId}.{version}.nupkg");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading package");
            return StatusCode(500, "Error downloading package");
        }
    }

    [HttpGet("{packageId}/{version}/with-dependencies")]
    public async Task<IActionResult> DownloadPackageWithDependencies(string packageId, string version, [FromQuery] string? framework = null)
    {
        try
        {
            if (!NuGetVersion.TryParse(version, out var nugetVersion))
            {
                return BadRequest("Invalid version format");
            }

            if (!string.IsNullOrEmpty(framework))
            {
                _nuGetService.SetTargetFramework(framework);
            }

            var packages = await _nuGetService.DownloadPackageWithDependenciesAsync(packageId, nugetVersion);
            
            using var memoryStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(memoryStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var package in packages)
                {
                    var entry = archive.CreateEntry(package.Key + ".nupkg");
                    using var entryStream = entry.Open();
                    await entryStream.WriteAsync(package.Value, 0, package.Value.Length);
                }
            }

            return File(memoryStream.ToArray(), "application/zip", $"{packageId}.{version}.with-dependencies.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading package with dependencies");
            return StatusCode(500, "Error downloading package with dependencies");
        }
    }
} 