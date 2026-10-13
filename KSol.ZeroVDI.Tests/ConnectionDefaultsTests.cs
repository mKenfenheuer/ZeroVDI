using System.Reflection;
using KSol.ZeroVDI.Models;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// Connection defaults are copied when access is granted and when the device policy is applied. Copying
/// them field by field silently dropped HiDPI when it was added, so every copy goes through Clone().
/// </summary>
public class ConnectionDefaultsTests
{
    [Fact]
    public void Clone_CopiesEveryOption()
    {
        var src = new ConnectionDefaults
        {
            Audio = false, Clipboard = false, Microphone = false, Camera = false,
            GfxMode = "progressive", HiDpi = true, PerformanceFlags = 0x7F,
        };
        var copy = src.Clone();

        Assert.NotSame(src, copy);
        // Compare every public property so a newly added option can't be forgotten again.
        foreach (var p in typeof(ConnectionDefaults).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.Equal(p.GetValue(src), p.GetValue(copy));
    }

    [Fact]
    public void Clone_IsIndependentOfTheSource()
    {
        var src = new ConnectionDefaults { HiDpi = true };
        var copy = src.Clone();
        copy.HiDpi = false;
        Assert.True(src.HiDpi);
    }
}
