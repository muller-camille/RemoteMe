using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Environments;
using RemoteMe.Web.Guacamole;

namespace RemoteMe.Tests;

public class EnvironmentPoolTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void Each_visitor_gets_a_distinct_account_and_keeps_it()
    {
        var pool = CreatePool(accounts: 3);

        using var a1 = Open(pool, "visitor-a");
        using var b = Open(pool, "visitor-b");
        using var a2 = Open(pool, "visitor-a");

        Assert.NotEqual(a1.Account.Username, b.Account.Username);
        Assert.Same(a1.Account, a2.Account);
        Assert.Equal(new PoolStats(3, 1, 2, 0), pool.GetStats());
    }

    [Fact]
    public void Accounts_start_in_cooldown_after_a_restart()
    {
        var pool = CreatePool(accounts: 1, cooldownSeconds: 60);

        Assert.Equal(OpenFailure.PoolExhausted, pool.OpenConnection("v", "1.1.1.1").Failure);

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(OpenFailure.None, pool.OpenConnection("v", "1.1.1.1").Failure);
    }

    [Fact]
    public void Pool_exhaustion_is_reported_as_server_busy()
    {
        var pool = CreatePool(accounts: 1);
        using var first = Open(pool, "a");

        var result = pool.OpenConnection("b", "2.2.2.2");

        Assert.Equal(OpenFailure.PoolExhausted, result.Failure);
        Assert.Equal(GuacStatus.ServerBusy, result.Status);
    }

    [Fact]
    public void Released_account_is_quarantined_before_reuse()
    {
        var pool = CreatePool(accounts: 1, cooldownSeconds: 30);
        _time.Advance(TimeSpan.FromSeconds(31));
        var ticket = Open(pool, "a");

        Assert.True(pool.Release("a", GuacStatus.SessionClosed));
        Assert.True(ticket.Aborted.IsCancellationRequested);
        Assert.Equal(GuacStatus.SessionClosed, ticket.AbortReason);
        ticket.Dispose();

        Assert.Equal(OpenFailure.PoolExhausted, pool.OpenConnection("b", "1.1.1.1").Failure);
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(OpenFailure.None, pool.OpenConnection("b", "1.1.1.1").Failure);
    }

    [Fact]
    public void Account_waits_for_a_successful_reset_before_reuse()
    {
        var pool = CreatePool(accounts: 1, resetCommand: "reset.cmd");

        // Au démarrage, la réinitialisation est demandée pour tous les comptes.
        var toReset = pool.Sweep();
        var account = Assert.Single(toReset);
        Assert.Empty(pool.Sweep());
        Assert.Equal(OpenFailure.PoolExhausted, pool.OpenConnection("a", "1.1.1.1").Failure);

        pool.CompleteReset(account, success: false);
        Assert.Equal(OpenFailure.PoolExhausted, pool.OpenConnection("a", "1.1.1.1").Failure);
        Assert.Empty(pool.Sweep());

        _time.Advance(TimeSpan.FromSeconds(16));
        Assert.Same(account, Assert.Single(pool.Sweep()));
        pool.CompleteReset(account, success: true);

        Assert.Equal(OpenFailure.None, pool.OpenConnection("a", "1.1.1.1").Failure);
    }

    [Fact]
    public void Idle_environment_expires_but_not_while_a_connection_is_open()
    {
        var pool = CreatePool(accounts: 1, idleMinutes: 10);
        var ticket = Open(pool, "a");

        _time.Advance(TimeSpan.FromMinutes(30));
        pool.Sweep();
        Assert.NotNull(pool.GetSnapshot("a"));

        ticket.Dispose();
        _time.Advance(TimeSpan.FromMinutes(9));
        pool.Sweep();
        Assert.NotNull(pool.GetSnapshot("a"));

        _time.Advance(TimeSpan.FromMinutes(2));
        pool.Sweep();
        Assert.Null(pool.GetSnapshot("a"));
    }

    [Fact]
    public void Max_lifetime_closes_open_connections()
    {
        var pool = CreatePool(accounts: 1, lifetimeMinutes: 60);
        using var ticket = Open(pool, "a");

        _time.Advance(TimeSpan.FromMinutes(61));
        pool.Sweep();

        Assert.Null(pool.GetSnapshot("a"));
        Assert.Equal(GuacStatus.SessionTimeout, ticket.AbortReason);
    }

    [Fact]
    public void Limits_per_ip_and_per_environment_are_enforced()
    {
        var pool = CreatePool(accounts: 5, configure: o =>
        {
            o.MaxEnvironmentsPerIp = 2;
            o.MaxConnectionsPerEnvironment = 2;
        });

        using var a1 = Open(pool, "a", "9.9.9.9");
        using var a2 = Open(pool, "a", "9.9.9.9");
        using var b = Open(pool, "b", "9.9.9.9");

        Assert.Equal(OpenFailure.TooManyConnections, pool.OpenConnection("a", "9.9.9.9").Failure);
        Assert.Equal(OpenFailure.TooManyEnvironmentsForIp, pool.OpenConnection("c", "9.9.9.9").Failure);
        Assert.Equal(GuacStatus.ClientTooMany, pool.OpenConnection("c", "9.9.9.9").Status);
        Assert.Equal(OpenFailure.None, pool.OpenConnection("c", "8.8.8.8").Failure);
    }

    [Fact]
    public void Environment_creation_is_rate_limited_per_ip()
    {
        var pool = CreatePool(accounts: 10, configure: o => o.MaxNewEnvironmentsPerIpPerHour = 2);

        Open(pool, "a").Dispose();
        pool.Release("a", GuacStatus.SessionClosed);
        Open(pool, "b").Dispose();
        pool.Release("b", GuacStatus.SessionClosed);

        Assert.Equal(OpenFailure.RateLimited, pool.OpenConnection("c", "1.1.1.1").Failure);
        _time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(OpenFailure.None, pool.OpenConnection("c", "1.1.1.1").Failure);
    }

    [Fact]
    public void Pattern_and_explicit_accounts_are_combined_without_duplicates()
    {
        var pool = CreatePool(accounts: 0, configure: o =>
        {
            o.UsernamePattern = "kiosk{0:00}";
            o.PatternStart = 1;
            o.PatternCount = 3;
            o.SharedPassword = "secret";
            o.Accounts.Add(new PoolAccountOptions { Username = "kiosk02", Password = "other" });
            o.Accounts.Add(new PoolAccountOptions { Username = "special", Password = "x" });
        });

        Assert.Equal(4, pool.GetStats().Total);
    }

    [Fact]
    public void Account_password_never_appears_in_its_string_form()
    {
        var account = new PoolAccount("kiosk01", "TopSecret!", "CONTOSO");
        Assert.DoesNotContain("TopSecret", account.ToString());
        Assert.Equal("CONTOSO\\kiosk01", account.ToString());
    }

    private static ConnectionTicket Open(EnvironmentPool pool, string visitor, string ip = "1.1.1.1")
    {
        var result = pool.OpenConnection(visitor, ip);
        Assert.Equal(OpenFailure.None, result.Failure);
        return result.Ticket!;
    }

    private EnvironmentPool CreatePool(
        int accounts,
        int cooldownSeconds = 0,
        int idleMinutes = 15,
        int lifetimeMinutes = 240,
        string? resetCommand = null,
        Action<EnvironmentOptions>? configure = null)
    {
        var options = new RemoteMeOptions();
        var env = options.Environments;
        env.CooldownSeconds = cooldownSeconds;
        env.IdleTimeoutMinutes = idleMinutes;
        env.MaxLifetimeMinutes = lifetimeMinutes;
        env.ResetCommand = resetCommand;
        env.MaxEnvironmentsPerIp = 0;
        env.MaxNewEnvironmentsPerIpPerHour = 0;
        for (var i = 1; i <= accounts; i++)
        {
            env.Accounts.Add(new PoolAccountOptions { Username = $"user{i}", Password = $"pw{i}" });
        }

        configure?.Invoke(env);
        return new EnvironmentPool(Options.Create(options), _time, NullLogger<EnvironmentPool>.Instance);
    }
}

internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
