using Android.App;
using Android.Runtime;
using Microsoft.Maui;

namespace Acme.Stockapp.Mobile;

// Android Application 引导类——经 MauiProgram.CreateMauiApp() 搭建 MAUI DI/服务（镜像 iOS 的 AppDelegate）。
// 缺它则 manifest 用默认 android.app.Application，MauiProgram 不被调用 → App/DI/路由全未初始化。
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership) : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
