using OpenLume.Infrastructure.Presets;

namespace OpenLume.Tests.Presets;

public sealed class XmpPresetImporterTests
{
    [Fact]
    public void MapsCameraRawFieldsRegardlessOfPrefix()
    {
        var x = "<x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:z='http://ns.adobe.com/camera-raw-settings/1.0/'><rdf:Description xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns/' z:PresetName='Film'><z:Exposure2012>1.5</z:Exposure2012><z:Contrast2012>-20</z:Contrast2012><z:Saturation>10</z:Saturation><z:Temperature>6000</z:Temperature><z:Tint>-4</z:Tint></rdf:Description></x:xmpmeta>";
        var result = new XmpPresetImporter().Import(x);
        Assert.True(result.Success);
        Assert.Equal("Film", result.Name);
        Assert.Equal(1.5, result.Recipe.ExposureEv);
        Assert.Equal(-20, result.Recipe.Contrast);
        Assert.Equal(10, result.Recipe.Saturation);
        Assert.Equal(10, result.Recipe.Temperature);
        Assert.Contains(result.Warnings, warning => warning.Contains("temperature", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void ReportsUnknownFieldsAndMalformedInput()
    {
        var result = new XmpPresetImporter().Import("<rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/' crs:Foo='1' crs:Contrast='2'/>"); Assert.True(result.Success); Assert.Contains("Foo", result.UnsupportedParameters);
        var bad = new XmpPresetImporter().Import("<x"); Assert.False(bad.Success); Assert.NotEmpty(bad.Warnings);
    }
    [Fact]
    public void RejectsDtd()
    {
        var result = new XmpPresetImporter().Import("<!DOCTYPE x [<!ENTITY evil 'x'>]><x>&evil;</x>"); Assert.False(result.Success); Assert.NotEmpty(result.Warnings);
    }
}
