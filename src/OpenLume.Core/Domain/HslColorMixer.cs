namespace OpenLume.Core.Domain;

public sealed record HslChannelAdjustment(
    double Hue = 0,
    double Saturation = 0,
    double Luminance = 0)
{
    public HslChannelAdjustment Normalize() => this with
    {
        Hue = Math.Clamp(Hue, -100, 100),
        Saturation = Math.Clamp(Saturation, -100, 100),
        Luminance = Math.Clamp(Luminance, -100, 100)
    };
}

public sealed record HslColorMixer(
    HslChannelAdjustment? Red = null,
    HslChannelAdjustment? Orange = null,
    HslChannelAdjustment? Yellow = null,
    HslChannelAdjustment? Green = null,
    HslChannelAdjustment? Aqua = null,
    HslChannelAdjustment? Blue = null,
    HslChannelAdjustment? Purple = null,
    HslChannelAdjustment? Magenta = null)
{
    public static HslColorMixer Neutral { get; } = new(
        new(), new(), new(), new(), new(), new(), new(), new());

    public HslColorMixer Normalize() => this with
    {
        Red = (Red ?? new()).Normalize(),
        Orange = (Orange ?? new()).Normalize(),
        Yellow = (Yellow ?? new()).Normalize(),
        Green = (Green ?? new()).Normalize(),
        Aqua = (Aqua ?? new()).Normalize(),
        Blue = (Blue ?? new()).Normalize(),
        Purple = (Purple ?? new()).Normalize(),
        Magenta = (Magenta ?? new()).Normalize()
    };
}
