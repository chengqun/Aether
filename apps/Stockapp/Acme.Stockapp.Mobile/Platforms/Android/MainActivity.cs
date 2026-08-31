using Android.App;
using Android.Content.PM;
using Microsoft.Maui;

namespace Acme.Stockapp.Mobile;

// Android 入口 Activity——声明 MAIN/LAUNCHER intent-filter（经 MainLauncher=true 由 MAUI 目标生成），
// 缺它则 APK 无可启动图标（桌面看不到 App）。iOS 入口见 Platforms/iOS/AppDelegate.cs + Program.cs。
[Activity(Label = "Aether · 行情", Icon = "@mipmap/appicon", Theme = "@style/Maui.SplashTheme", MainLauncher = true, Exported = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
