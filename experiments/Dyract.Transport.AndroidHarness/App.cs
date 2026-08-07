namespace Dyract.Transport.AndroidHarness;

public sealed class App : Application
{
    private readonly MainPage _mainPage;

    public App(MainPage mainPage)
    {
        _mainPage = mainPage ?? throw new ArgumentNullException(nameof(mainPage));
    }

    protected override Window CreateWindow(IActivationState? activationState)
        => new(new NavigationPage(_mainPage));
}
