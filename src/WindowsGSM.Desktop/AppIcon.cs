namespace WindowsGSM.Desktop;

/// <summary>The WindowsGSM logo (WindowsGSM.ico, embedded) at the size a window or the tray wants.</summary>
internal static class AppIcon
{
    public static Icon Create(int size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("WindowsGSM.ico")
            ?? throw new InvalidOperationException("The app icon is missing from the build.");
        return new Icon(stream, size, size);
    }
}
