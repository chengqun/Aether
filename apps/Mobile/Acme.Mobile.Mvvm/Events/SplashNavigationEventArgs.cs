using System;

namespace Acme.Mobile.Mvvm.Events;

public class SplashNavigationEventArgs : EventArgs
{
    public bool IsLoggedIn { get; set; }
}