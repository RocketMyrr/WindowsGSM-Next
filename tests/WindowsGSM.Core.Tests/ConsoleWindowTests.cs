using System.Diagnostics;
using WindowsGSM.Engine.Services;
using WindowsGSM.Functions;

namespace WindowsGSM.Core.Tests;

/// <summary>
/// A game's own console window. The window itself needs a desktop (checked by hand), but what the rest relies on
/// is checked here: plugins type their stop command into Process.MainWindowHandle, which WindowsGSM sets to the
/// game's window — through the runtime's private fields, so a new .NET that renames them must fail here, not quietly
/// in the field where graceful stops would turn into kills.
/// </summary>
public class ConsoleWindowTests
{
    [Fact]
    public void A_process_can_be_told_its_console_window()
    {
        Assert.True(ConsoleWindows.CanAdopt, "Process._mainWindowHandle/_haveMainWindow changed: update ConsoleWindows.Adopt.");

        using var p = Process.GetCurrentProcess();
        var window = new IntPtr(0x1234);
        ConsoleWindows.Adopt(p, window);
        Assert.Equal(window, p.MainWindowHandle);
    }

    [Fact]
    public void Nothing_is_adopted_without_a_window()
    {
        using var p = Process.GetCurrentProcess();
        IntPtr before = p.MainWindowHandle;
        ConsoleWindows.Adopt(p, IntPtr.Zero);
        Assert.Equal(before, p.MainWindowHandle);
    }

    [Fact]
    public void Games_whose_plugin_cant_capture_are_never_captured()
    {
        // As legacy did: the plugin's AllowsEmbedConsole, as shipped, says whether capturing can work at all.
        Assert.False(GameCatalog.CanCapture(new WindowsGSM.GameServer.RUST(null!)));
        Assert.False(GameCatalog.CanCapture(new WindowsGSM.GameServer.ARKSE(null!)));
        Assert.True(GameCatalog.CanCapture(new WindowsGSM.GameServer.MC(null!)));
        Assert.True(GameCatalog.CanCapture(new object())); // a plugin that doesn't say: allowed, as before
    }

    [Fact]
    public void Only_the_agent_turns_on_console_hosting()
    {
        // Tests (and anything else using the engine) start games the old way unless the agent enables it.
        Assert.False(ConsoleHost.Active);
        Assert.False(ConsoleHost.IsConsoleWindow(IntPtr.Zero));
        Assert.Equal(IntPtr.Zero, ConsoleHost.Confirm(IntPtr.Zero, Process.GetCurrentProcess()));
    }
}
