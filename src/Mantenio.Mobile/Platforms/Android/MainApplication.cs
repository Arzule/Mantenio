using Android.App;
using Android.Runtime;

namespace Mantenio.Mobile;

#if DEBUG
// Debug builds call the local API over plain HTTP (http://localhost:5013 via adb reverse).
[Application(UsesCleartextTraffic = true)]
#else
[Application]
#endif
public class MainApplication : MauiApplication
{
	public MainApplication(IntPtr handle, JniHandleOwnership ownership)
		: base(handle, ownership)
	{
	}

	public override void OnCreate()
	{
		base.OnCreate();
		PushSupport.Initialize(this);
	}

	protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
