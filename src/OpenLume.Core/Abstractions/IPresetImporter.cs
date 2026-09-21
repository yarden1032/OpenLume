using OpenLume.Core.Domain;

namespace OpenLume.Core.Abstractions;

public interface IPresetImporter
{
    PresetImportResult Import(string xmp);
}
