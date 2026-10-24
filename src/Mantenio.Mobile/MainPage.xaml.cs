using Mantenio.Shared.Condos;

namespace Mantenio.Mobile;

public partial class MainPage : ContentPage
{
    public MainPage()
    {
        InitializeComponent();

        // Android 15+ draws edge to edge: the page keeps the web view inside the system bars
        // (SafeAreaEdges in XAML) and paints those strips with the app bar color so the
        // white status bar icons stay readable.
        BackgroundColor = Color.FromArgb(CondoProfile.Current.Theme.AppBar);
    }
}
