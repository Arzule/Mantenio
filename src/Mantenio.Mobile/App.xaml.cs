using Mantenio.Shared.Condos;

namespace Mantenio.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState) =>
        new(new MainPage()) { Title = CondoProfile.Current.ShortName };
}
