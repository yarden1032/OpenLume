using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public sealed record RenderedImage(byte[] Data, string MimeType, int Width, int Height);

public enum ImageExportFormat
{
    Jpeg,
    Png
}

public interface IImageRenderer
{
    Task<RenderedImage> RenderPreviewAsync(string sourcePath, EditRecipe edit, int maxDimension, CancellationToken cancellationToken = default);
    Task ExportAsync(string sourcePath, string destinationPath, EditRecipe edit, ImageExportFormat format, int quality = 92, CancellationToken cancellationToken = default);
    Task ExportJpegAsync(string sourcePath, string destinationPath, EditRecipe edit, int quality, CancellationToken cancellationToken = default) =>
        ExportAsync(sourcePath, destinationPath, edit, ImageExportFormat.Jpeg, quality, cancellationToken);
    Task ExportAsync(string sourcePath, string destinationPath, EditRecipe edit, ExportOptions options, CancellationToken cancellationToken = default)
    {
        options = options.Normalize();
        if (options.MaxDimension != 0)
            throw new NotSupportedException("This renderer only supports full-size export.");
        return ExportAsync(sourcePath, destinationPath, edit, options.Format, options.Quality, cancellationToken);
    }
}

