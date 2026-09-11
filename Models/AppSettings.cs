using System;

namespace InstaDesktop.Models;

public enum CloseButtonBehavior { MinimizeToTray, ExitApplication }
public enum SidebarMode { Expanded, Compact }

public sealed class AppSettings
{
    public bool StartWithWindows { get; set; }
    public CloseButtonBehavior CloseButton { get; set; } = CloseButtonBehavior.MinimizeToTray;
    public bool HardwareAcceleration { get; set; } = true;
    public bool DeveloperTools { get; set; }
    public bool UiCustomization { get; set; }
    public bool BackgroundLowMemory { get; set; } = true;
    public bool AppNotifications { get; set; } = true;
    public bool AllowMicrophone { get; set; }
    public bool AllowCamera { get; set; }
    public SidebarMode SidebarMode { get; set; } = SidebarMode.Expanded;
    public bool CompactInstagramLayout { get; set; }
    public double Width { get; set; } = 1200;
    public double Height { get; set; } = 800;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }

    public AppSettings Copy() => (AppSettings)MemberwiseClone();

    public void Normalize()
    {
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 760, 16384) : 1200;
        Height = double.IsFinite(Height) ? Math.Clamp(Height, 600, 16384) : 800;
        if (Left is double left && !double.IsFinite(left)) Left = null;
        if (Top is double top && !double.IsFinite(top)) Top = null;
        if (!Enum.IsDefined(CloseButton)) CloseButton = CloseButtonBehavior.MinimizeToTray;
        if (!Enum.IsDefined(SidebarMode)) SidebarMode = SidebarMode.Expanded;
    }
}
