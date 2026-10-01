using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(OpenLume.Tests.App.HeadlessTestApplication))]

namespace OpenLume.Tests.App;

public sealed class HeadlessTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { ShouldRenderOnUIThread = true });
}
