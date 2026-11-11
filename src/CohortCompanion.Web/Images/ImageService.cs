using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CohortCompanion.Web.Images;

public sealed class ImageOptions
{
    public const string SectionName = "Images";

    /// <summary>Blob service URL, for example <c>https://example.blob.core.windows.net</c>. Empty means local disk.</summary>
    public string? BlobServiceUri { get; set; }

    public string BlobContainer { get; set; } = "images";

    /// <summary>Folder used for local disk storage, relative to the content root.</summary>
    public string LocalPath { get; set; } = "App_Data/uploads";
}

public sealed record StoredImage(Stream Content, string ContentType);

public sealed record StoredImageInfo(string Name, DateTimeOffset SavedAt);

public interface IImageStore
{
    Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken);

    Task<StoredImage?> OpenAsync(string name, CancellationToken cancellationToken);

    /// <summary>Everything in the store, with when each file was saved.</summary>
    IAsyncEnumerable<StoredImageInfo> ListAsync(CancellationToken cancellationToken);

    /// <summary>Deletes an image. Does nothing if it's already gone.</summary>
    Task DeleteAsync(string name, CancellationToken cancellationToken);
}

public sealed class LocalImageStore(string folder) : IImageStore
{
    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await using var file = File.Create(Path.Combine(folder, name));
        await content.CopyToAsync(file, cancellationToken);
    }

    public Task<StoredImage?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, name);
        return Task.FromResult(File.Exists(path)
            ? new StoredImage(File.OpenRead(path), ImageService.ContentTypeFor(name))
            : null);
    }

    public IAsyncEnumerable<StoredImageInfo> ListAsync(CancellationToken cancellationToken)
    {
        var directory = new DirectoryInfo(folder);
        return (directory.Exists ? directory.EnumerateFiles() : [])
            .Select(file => new StoredImageInfo(file.Name, file.LastWriteTimeUtc))
            .ToAsyncEnumerable();
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken)
    {
        File.Delete(Path.Combine(folder, name));
        return Task.CompletedTask;
    }
}

/// <summary>Images in a private container, read with the app's managed identity and served from our own domain.</summary>
public sealed class BlobImageStore(ImageOptions options) : IImageStore
{
    private readonly BlobContainerClient container =
        new BlobServiceClient(new Uri(options.BlobServiceUri!), new DefaultAzureCredential()).GetBlobContainerClient(options.BlobContainer);

    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken cancellationToken) =>
        await container.GetBlobClient(name).UploadAsync(
            content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } },
            cancellationToken);

    public async Task<StoredImage?> OpenAsync(string name, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(name);
        if (!await blob.ExistsAsync(cancellationToken))
        {
            return null;
        }

        return new StoredImage(await blob.OpenReadAsync(cancellationToken: cancellationToken), ImageService.ContentTypeFor(name));
    }

    public async IAsyncEnumerable<StoredImageInfo> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            // A blob with no date is treated as brand new, so it's never deleted by mistake.
            yield return new StoredImageInfo(blob.Name, blob.Properties.CreatedOn ?? blob.Properties.LastModified ?? DateTimeOffset.MaxValue);
        }
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken) =>
        await container.GetBlobClient(name).DeleteIfExistsAsync(cancellationToken: cancellationToken);
}

public sealed record ImageUploadResult(string? Url, string? Error);

/// <summary>
/// Accepts uploads and hands back a URL on our own domain. The type comes from the file's first bytes,
/// never from its name or what the browser claims, and SVG isn't allowed because it can carry scripts.
/// </summary>
public sealed partial class ImageService(IImageStore store)
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public const string RoutePrefix = "media/";

    [GeneratedRegex(@"^[0-9a-f]{32}\.(png|jpg|gif|webp)$")]
    private static partial Regex StoredName();

    public static bool IsStoredName(string name) => StoredName().IsMatch(name);

    /// <summary>Keeps a URL only if it's one of ours, so student pages never load from a third party.</summary>
    public static string? OwnImageOrNull(string? url) =>
        url is not null && url.StartsWith(RoutePrefix, StringComparison.Ordinal) && IsStoredName(url[RoutePrefix.Length..]) ? url : null;

    public static string ContentTypeFor(string name) => Path.GetExtension(name) switch
    {
        ".png" => "image/png",
        ".jpg" => "image/jpeg",
        ".gif" => "image/gif",
        _ => "image/webp",
    };

    public async Task<ImageUploadResult> UploadAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return new ImageUploadResult(null, "That image is too big. The limit is 5 MB.");
            }

            buffer.Write(chunk, 0, read);
        }

        var extension = DetectExtension(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        if (extension is null)
        {
            return new ImageUploadResult(null, "That file isn't a PNG, JPEG, GIF, or WebP image.");
        }

        var name = $"{Guid.NewGuid():N}.{extension}";
        buffer.Position = 0;
        await store.SaveAsync(name, buffer, ContentTypeFor(name), cancellationToken);
        return new ImageUploadResult(RoutePrefix + name, null);
    }

    public static string? DetectExtension(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "png";
        }

        if (data.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return "jpg";
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return "gif";
        }

        return data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8) ? "webp" : null;
    }
}

public static class ImageEndpoint
{
    public static void MapImages(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/media/{name}", async (string name, IImageStore store, HttpContext context, CancellationToken cancellationToken) =>
        {
            if (!ImageService.IsStoredName(name) || await store.OpenAsync(name, cancellationToken) is not { } image)
            {
                return Results.NotFound();
            }

            // Names are random and never reused, so a browser can keep an image forever.
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(image.Content, image.ContentType);
        });
}
