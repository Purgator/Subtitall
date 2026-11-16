using Microsoft.Extensions.Logging.Abstractions;
using Subtitall.Api;
using Subtitall.Core;

namespace Subtitall.Tests;

/// <summary>An update check must always end — "checking" forever is a bug whatever the cause.</summary>
public class UpdateServiceTests
{
    private static UpdateService Create() => new(new EventBus(), NullLogger<UpdateService>.Instance);

    [Fact]
    public void A_check_the_updater_never_picked_up_ends_as_unsupported()
    {
        var updates = Create();
        var check = updates.BeginCheck();
        Assert.Equal("checking", updates.Status.State);

        updates.ExpireCheck(check);
        Assert.Equal("unsupported", updates.Status.State);
    }

    [Fact]
    public void A_check_the_updater_started_but_never_answered_ends_as_a_retryable_error()
    {
        var updates = Create();
        var check = updates.BeginCheck();
        updates.MarkUpdaterStarted(); // The "checking" event must not disarm the timeout.

        updates.ExpireCheck(check);
        Assert.Equal("error", updates.Status.State);
    }

    [Fact]
    public void The_timeout_leaves_an_answered_check_alone()
    {
        var updates = Create();
        var check = updates.BeginCheck();
        updates.CompleteCheck(check, UserAgent.Version);
        Assert.Equal("upToDate", updates.Status.State);

        updates.ExpireCheck(check);
        Assert.Equal("upToDate", updates.Status.State);
    }

    [Fact]
    public void The_timeout_of_an_old_check_does_not_end_a_newer_one()
    {
        var updates = Create();
        var first = updates.BeginCheck();
        updates.CompleteCheck(first, UserAgent.Version);
        updates.BeginCheck();

        updates.ExpireCheck(first);
        Assert.Equal("checking", updates.Status.State);
    }

    [Fact]
    public void Nothing_is_installed_before_an_update_is_downloaded()
    {
        var updates = Create();
        Assert.False(updates.BeginInstall());
        updates.BeginCheck();
        Assert.False(updates.BeginInstall());
        Assert.Equal("checking", updates.Status.State);
    }

    [Fact]
    public void A_returned_check_reporting_a_newer_version_is_left_to_the_download_events()
    {
        var updates = Create();
        var check = updates.BeginCheck();
        updates.CompleteCheck(check, "999.0.0");
        Assert.Equal("checking", updates.Status.State);
    }
}
