using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public sealed record RenderedImage(byte[] Data, string MimeType, int Width, int Height);

public interface IImageRenderer
{
    Task<RenderedImage> RenderPreviewAsync(string sourcePath, EditRecipe edit, int maxDimension, CancellationToken cancellationToken = default);
    Task ExportJpegAsync(string sourcePath, string destinationPath, EditRecipe edit, int quality, CancellationToken cancellationToken = default);
    Task ExportAsync(string sourcePath, string destinationPath, EditRecipe edit, ExportOptions options, CancellationToken cancellationToken = default)
    {
        options = options.Normalize();
        if (options.Format != ExportFormat.Jpeg || options.MaxDimension != 0)
            throw new NotSupportedException("This renderer only supports full-size JPEG export.");
        return ExportJpegAsync(sourcePath, destinationPath, edit, options.Quality, cancellationToken);
    }
}

