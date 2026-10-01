using OpenLume.Infrastructure.Presets;

namespace OpenLume.Tests.Presets;

public sealed class XmpPresetImporterTests
{
    [Theory]
    [InlineData("False")]
    [InlineData("0")]
    public void DisabledCropIgnoresBoundsAndStraightenButPreservesOrientation(string toggle)
    {
        var result = new XmpPresetImporter().Import($"""
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:CropLeft='.1' crs:CropTop='.2' crs:CropRight='.8' crs:CropBottom='.9'
              crs:CropAngle='12' crs:Orientation='7'><crs:HasCrop>{toggle}</crs:HasCrop></rdf:Description>
            """);
        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.UnsupportedParameters);
        Assert.Equal(0, result.Recipe.RotationDegrees);
        Assert.False(result.Recipe.Crop!.HasCrop);
        Assert.Equal(1, result.Recipe.Crop.QuarterTurns);
        Assert.True(result.Recipe.Crop.FlipVertical);
    }

    [Theory]
    [InlineData("True", 100)]
    [InlineData("False", 0)]
    [InlineData("1", 100)]
    [InlineData("0", 0)]
    [InlineData("-1", 100)]
    [InlineData("2", 100)]
    public void AutoLateralCaIsAToggle(string toggle, double expected)
    {
        var result = new XmpPresetImporter().Import($"""
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:AutoLateralCA='{toggle}'/>
            """);
        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.Equal(expected, result.Recipe.Optics!.ChromaticAberration);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("invalid")]
    public void InvalidAutoLateralCaDoesNotEnableCorrection(string toggle)
    {
        var result = new XmpPresetImporter().Import($"""
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:AutoLateralCA='{toggle}'/>
            """);
        Assert.True(result.Success);
        Assert.Single(result.Warnings);
        Assert.Equal(0, result.Recipe.Optics!.ChromaticAberration);
    }

    [Fact]
    public void MapsCameraRawFieldsRegardlessOfPrefix()
    {
        var x = "<x:xmpmeta xmlns:x='adobe:ns:meta/' xmlns:z='http://ns.adobe.com/camera-raw-settings/1.0/'><rdf:Description xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns/' z:PresetName='Film'><z:Exposure2012>1.5</z:Exposure2012><z:Contrast2012>-20</z:Contrast2012><z:Saturation>10</z:Saturation><z:Temperature>6000</z:Temperature><z:Tint>-4</z:Tint><z:Highlights2012>-35</z:Highlights2012><z:Shadows2012>28</z:Shadows2012><z:Whites2012>9</z:Whites2012><z:Blacks2012>-7</z:Blacks2012><z:Vibrance>18</z:Vibrance><z:PostCropVignetteAmount>-12</z:PostCropVignetteAmount><z:Texture>14</z:Texture><z:Clarity2012>8</z:Clarity2012><z:Dehaze>6</z:Dehaze><z:Sharpness>35</z:Sharpness><z:LuminanceSmoothing>22</z:LuminanceSmoothing><z:GrainAmount>18</z:GrainAmount></rdf:Description></x:xmpmeta>";
        var result = new XmpPresetImporter().Import(x);
        Assert.True(result.Success);
        Assert.Equal("Film", result.Name);
        Assert.Equal(1.5, result.Recipe.ExposureEv);
        Assert.Equal(-20, result.Recipe.Contrast);
        Assert.Equal(10, result.Recipe.Saturation);
        Assert.Equal(10, result.Recipe.Temperature);
        Assert.Equal(-35, result.Recipe.Highlights);
        Assert.Equal(28, result.Recipe.Shadows);
        Assert.Equal(9, result.Recipe.Whites);
        Assert.Equal(-7, result.Recipe.Blacks);
        Assert.Equal(18, result.Recipe.Vibrance);
        Assert.Equal(-12, result.Recipe.Vignette);
        Assert.Equal(14, result.Recipe.Texture);
        Assert.Equal(8, result.Recipe.Clarity);
        Assert.Equal(6, result.Recipe.Dehaze);
        Assert.Equal(35, result.Recipe.Sharpening);
        Assert.Equal(22, result.Recipe.NoiseReduction);
        Assert.Equal(18, result.Recipe.Grain);
        Assert.Empty(result.UnsupportedParameters);
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

    [Fact]
    public void MapsAllLightroomColorMixerComponents()
    {
        const string xmp = """
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:HueAdjustmentRed='-12' crs:SaturationAdjustmentRed='22' crs:LuminanceAdjustmentRed='8'
              crs:HueAdjustmentOrange='4' crs:SaturationAdjustmentYellow='14'
              crs:LuminanceAdjustmentGreen='-18' crs:HueAdjustmentAqua='9'
              crs:SaturationAdjustmentBlue='31' crs:LuminanceAdjustmentPurple='-7'
              crs:HueAdjustmentMagenta='16'/>
            """;

        var result = new XmpPresetImporter().Import(xmp);

        Assert.True(result.Success);
        Assert.Empty(result.UnsupportedParameters);
        Assert.Equal(-12, result.Recipe.ColorMixer!.Red!.Hue);
        Assert.Equal(22, result.Recipe.ColorMixer.Red.Saturation);
        Assert.Equal(8, result.Recipe.ColorMixer.Red.Luminance);
        Assert.Equal(4, result.Recipe.ColorMixer.Orange!.Hue);
        Assert.Equal(14, result.Recipe.ColorMixer.Yellow!.Saturation);
        Assert.Equal(-18, result.Recipe.ColorMixer.Green!.Luminance);
        Assert.Equal(9, result.Recipe.ColorMixer.Aqua!.Hue);
        Assert.Equal(31, result.Recipe.ColorMixer.Blue!.Saturation);
        Assert.Equal(-7, result.Recipe.ColorMixer.Purple!.Luminance);
        Assert.Equal(16, result.Recipe.ColorMixer.Magenta!.Hue);
    }

    [Fact]
    public void MapsLightroomParametricToneCurve()
    {
        const string xmp = """
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:ParametricHighlights='18' crs:ParametricLights='7'
              crs:ParametricDarks='-12' crs:ParametricShadows='-25'
              crs:ParametricShadowSplit='20' crs:ParametricMidtoneSplit='48'
              crs:ParametricHighlightSplit='78'/>
            """;

        var result = new XmpPresetImporter().Import(xmp);

        Assert.True(result.Success);
        Assert.Empty(result.UnsupportedParameters);
        Assert.Equal(18, result.Recipe.ToneCurve!.Highlights);
        Assert.Equal(7, result.Recipe.ToneCurve.Lights);
        Assert.Equal(-12, result.Recipe.ToneCurve.Darks);
        Assert.Equal(-25, result.Recipe.ToneCurve.Shadows);
        Assert.Equal(20, result.Recipe.ToneCurve.ShadowSplit);
        Assert.Equal(48, result.Recipe.ToneCurve.MidtoneSplit);
        Assert.Equal(78, result.Recipe.ToneCurve.HighlightSplit);
    }

    [Fact]
    public void MapsLightroomCropStraightenAndOrientation()
    {
        const string xmp = """
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:CropLeft='0.1' crs:CropTop='0.2' crs:CropRight='0.85' crs:CropBottom='0.9'
              crs:CropAngle='-2.5' crs:Orientation='6' crs:HasCrop='True'/>
            """;

        var result = new XmpPresetImporter().Import(xmp);

        Assert.True(result.Success);
        Assert.Empty(result.UnsupportedParameters);
        Assert.Equal(-2.5, result.Recipe.RotationDegrees);
        Assert.Equal(.1, result.Recipe.Crop!.X, 6);
        Assert.Equal(.2, result.Recipe.Crop.Y, 6);
        Assert.Equal(.75, result.Recipe.Crop.Width, 6);
        Assert.Equal(.7, result.Recipe.Crop.Height, 6);
        Assert.Equal(1, result.Recipe.Crop.QuarterTurns);
        Assert.False(result.Recipe.Crop.FlipHorizontal);
        Assert.False(result.Recipe.Crop.FlipVertical);
    }

    [Fact]
    public void MapsLightroomManualOpticsCorrections()
    {
        const string xmp = """
            <rdf:Description xmlns:rdf='x' xmlns:crs='http://ns.adobe.com/camera-raw-settings/1.0/'
              crs:LensManualDistortionAmount='-18' crs:AutoLateralCA='True'
              crs:DefringePurpleAmount='35' crs:VignetteAmount='22' crs:VignetteMidpoint='41'/>
            """;

        var result = new XmpPresetImporter().Import(xmp);

        Assert.True(result.Success);
        Assert.Empty(result.UnsupportedParameters);
        Assert.Equal(-18, result.Recipe.Optics!.Distortion);
        Assert.Equal(100, result.Recipe.Optics.ChromaticAberration);
        Assert.Equal(22, result.Recipe.Optics.LensVignette);
        Assert.Equal(41, result.Recipe.Optics.VignetteMidpoint);
    }
}
