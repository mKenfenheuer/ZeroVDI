using KSol.ZeroVDI.Models;
using KSol.ZeroVDI.RDP;

namespace KSol.ZeroVDI.Tests;

/// <summary>
/// Identity has no "enabled" flag, so ZeroVDI expresses a disabled account as a far-future lockout.
/// These two predicates decide whether the admin UI offers "Unlock" or "Enable", and whether an
/// account counts towards the last-administrator guard — getting them the wrong way round would let
/// the last administrator be disabled.
/// </summary>
public class AdminAccountSafetyTests
{
    [Fact]
    public void AnUntouchedAccountIsNeitherDisabledNorLockedOut()
    {
        var user = new ApplicationUser();

        Assert.False(AdminAccountSafety.IsDisabled(user));
        Assert.False(AdminAccountSafety.IsLockedOut(user));
    }

    [Fact]
    public void ADisabledAccountIsNotReportedAsMerelyLockedOut()
    {
        var user = new ApplicationUser { LockoutEnd = AdminAccountSafety.DisabledUntil };

        Assert.True(AdminAccountSafety.IsDisabled(user));
        Assert.False(AdminAccountSafety.IsLockedOut(user));
    }

    [Fact]
    public void ABruteForceLockoutIsLockedOutButNotDisabled()
    {
        var user = new ApplicationUser { LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(5) };

        Assert.True(AdminAccountSafety.IsLockedOut(user));
        Assert.False(AdminAccountSafety.IsDisabled(user));
    }

    [Fact]
    public void AnExpiredLockoutIsBehindUs()
    {
        var user = new ApplicationUser { LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1) };

        Assert.False(AdminAccountSafety.IsLockedOut(user));
        Assert.False(AdminAccountSafety.IsDisabled(user));
    }
}
