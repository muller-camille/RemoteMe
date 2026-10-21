using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RemoteMe.Web.Configuration;
using RemoteMe.Web.Guacamole;

namespace RemoteMe.Web.Environments;

/// <summary>Compte Windows du pool. Le mot de passe ne sort jamais du serveur.</summary>
public sealed class PoolAccount
{
    public PoolAccount(string username, string password, string? domain)
    {
        Username = username;
        Password = password;
        Domain = string.IsNullOrWhiteSpace(domain) ? null : domain;
    }

    public string Username { get; }

    public string Password { get; }

    public string? Domain { get; }

    public string DisplayName => Domain is null ? Username : $"{Domain}\\{Username}";

    public override string ToString() => DisplayName;
}

public enum OpenFailure
{
    None,
    NoAccountConfigured,
    PoolExhausted,
    TooManyEnvironmentsForIp,
    RateLimited,
    TooManyConnections,
}

/// <summary>Résultat de <see cref="EnvironmentPool.OpenConnection"/>.</summary>
public readonly record struct OpenResult(ConnectionTicket? Ticket, OpenFailure Failure)
{
    public GuacStatus Status => Failure switch
    {
        OpenFailure.None => GuacStatus.Success,
        OpenFailure.NoAccountConfigured => GuacStatus.ServerError,
        OpenFailure.PoolExhausted => GuacStatus.ServerBusy,
        _ => GuacStatus.ClientTooMany,
    };
}

/// <summary>Vue publique d'un environnement, sans information sensible.</summary>
public sealed record EnvironmentSnapshot(
    string ShortId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int ActiveConnections);

public sealed record PoolStats(int Total, int Available, int Leased, int Cooling);

/// <summary>
/// Attribue à chaque visiteur anonyme un compte du pool, pour toute la durée de sa visite.
/// Un compte libéré passe en quarantaine (et éventuellement en réinitialisation) avant d'être réattribué,
/// afin qu'un nouveau visiteur ne retrouve jamais la session ou les fichiers du précédent.
/// </summary>
public sealed class EnvironmentPool
{
    private readonly object _gate = new();
    private readonly List<Slot> _slots;
    private readonly Dictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> _creationsByIp = new(StringComparer.Ordinal);
    private readonly EnvironmentOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<EnvironmentPool> _logger;
    private readonly bool _resetConfigured;

    public EnvironmentPool(IOptions<RemoteMeOptions> options, TimeProvider time, ILogger<EnvironmentPool> logger)
    {
        _options = options.Value.Environments;
        _time = time;
        _logger = logger;
        _resetConfigured = !string.IsNullOrWhiteSpace(_options.ResetCommand);

        // Au démarrage, des sessions laissées par un processus précédent (recyclage IIS) peuvent
        // encore exister : chaque compte commence donc en quarantaine, comme s'il venait d'être libéré.
        var now = _time.GetUtcNow();
        _slots = BuildAccounts(_options)
            .Select(account => new Slot(account)
            {
                State = SlotState.Cooling,
                AvailableAt = now.AddSeconds(_options.CooldownSeconds),
                ResetPending = _resetConfigured,
            })
            .ToList();

        if (_slots.Count == 0)
        {
            _logger.LogWarning("Aucun compte d'environnement configuré (RemoteMe:Environments).");
        }
        else
        {
            _logger.LogInformation("Pool d'environnements : {Count} compte(s).", _slots.Count);
        }
    }

    public bool ResetConfigured => _resetConfigured;

    /// <summary>
    /// Ouvre une connexion pour le visiteur, en lui attribuant un environnement s'il n'en a pas encore.
    /// </summary>
    public OpenResult OpenConnection(string visitorId, string? clientIp)
    {
        ArgumentException.ThrowIfNullOrEmpty(visitorId);
        var ip = clientIp ?? "?";

        lock (_gate)
        {
            var now = _time.GetUtcNow();

            if (!_leases.TryGetValue(visitorId, out var lease))
            {
                var failure = CheckCreationAllowed(ip, now);
                if (failure != OpenFailure.None)
                {
                    return new OpenResult(null, failure);
                }

                var slot = _slots
                    .Where(s => IsAssignable(s, now))
                    .OrderBy(s => s.AvailableAt)
                    .FirstOrDefault();
                if (slot is null)
                {
                    return new OpenResult(null, OpenFailure.PoolExhausted);
                }

                slot.State = SlotState.Leased;
                lease = new Lease(visitorId, ShortIdOf(visitorId), slot, ip, now);
                _leases.Add(visitorId, lease);
                RecordCreation(ip, now);
                _logger.LogInformation(
                    "Environnement {Env} attribué ({Account}, IP {Ip}).", lease.ShortId, slot.Account, ip);
            }

            if (lease.Connections.Count >= Math.Max(1, _options.MaxConnectionsPerEnvironment))
            {
                return new OpenResult(null, OpenFailure.TooManyConnections);
            }

            var ticket = new ConnectionTicket(this, lease);
            lease.Connections.Add(ticket);
            lease.LastActivity = now;
            return new OpenResult(ticket, OpenFailure.None);
        }
    }

    public EnvironmentSnapshot? GetSnapshot(string? visitorId)
    {
        if (string.IsNullOrEmpty(visitorId))
        {
            return null;
        }

        lock (_gate)
        {
            return _leases.TryGetValue(visitorId, out var lease) ? Snapshot(lease) : null;
        }
    }

    /// <summary>Libère l'environnement du visiteur et ferme ses connexions.</summary>
    public bool Release(string? visitorId, GuacStatus reason)
    {
        if (string.IsNullOrEmpty(visitorId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_leases.TryGetValue(visitorId, out var lease))
            {
                return false;
            }

            ReleaseLocked(lease, reason, _time.GetUtcNow());
            return true;
        }
    }

    /// <summary>
    /// Expire les environnements inactifs ou trop anciens, puis renvoie les comptes à réinitialiser.
    /// Les comptes renvoyés sont marqués « en cours » jusqu'à l'appel de <see cref="CompleteReset"/>.
    /// </summary>
    public IReadOnlyList<PoolAccount> Sweep()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var idle = TimeSpan.FromMinutes(_options.IdleTimeoutMinutes);
            var lifetime = TimeSpan.FromMinutes(_options.MaxLifetimeMinutes);

            foreach (var lease in _leases.Values.ToList())
            {
                if (now - lease.CreatedAt >= lifetime)
                {
                    ReleaseLocked(lease, GuacStatus.SessionTimeout, now);
                }
                else if (lease.Connections.Count == 0 && now - lease.LastActivity >= idle)
                {
                    ReleaseLocked(lease, GuacStatus.SessionTimeout, now);
                }
            }

            foreach (var (ip, creations) in _creationsByIp.ToList())
            {
                PruneCreations(creations, now);
                if (creations.Count == 0)
                {
                    _creationsByIp.Remove(ip);
                }
            }

            var toReset = new List<PoolAccount>();
            foreach (var slot in _slots)
            {
                if (slot.State == SlotState.Cooling && slot.ResetPending && !slot.ResetRunning
                    && now >= slot.NextResetAttempt)
                {
                    slot.ResetRunning = true;
                    toReset.Add(slot.Account);
                }
            }

            return toReset;
        }
    }

    /// <summary>Fin de réinitialisation d'un compte. En cas d'échec, une nouvelle tentative est planifiée.</summary>
    public void CompleteReset(PoolAccount account, bool success)
    {
        lock (_gate)
        {
            var slot = _slots.FirstOrDefault(s => ReferenceEquals(s.Account, account));
            if (slot is null)
            {
                return;
            }

            slot.ResetRunning = false;
            if (success)
            {
                slot.ResetPending = false;
                slot.ResetFailures = 0;
            }
            else
            {
                // Le compte reste hors rotation : mieux vaut un pool réduit qu'une session partagée.
                slot.ResetFailures++;
                var delay = TimeSpan.FromSeconds(Math.Min(600, 15 * Math.Pow(2, slot.ResetFailures - 1)));
                slot.NextResetAttempt = _time.GetUtcNow() + delay;
            }
        }
    }

    public PoolStats GetStats()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var available = _slots.Count(s => IsAssignable(s, now));
            var leased = _slots.Count(s => s.State == SlotState.Leased);
            return new PoolStats(_slots.Count, available, leased, _slots.Count - available - leased);
        }
    }

    /// <summary>Identifiant court et non réversible, affichable et journalisable.</summary>
    public static string ShortIdOf(string visitorId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(visitorId));
        return Convert.ToHexString(hash, 0, 3);
    }

    internal void CloseConnection(ConnectionTicket ticket)
    {
        lock (_gate)
        {
            if (ticket.Lease.Connections.Remove(ticket))
            {
                ticket.Lease.LastActivity = _time.GetUtcNow();
            }
        }
    }

    private static bool IsAssignable(Slot slot, DateTimeOffset now) =>
        slot.State == SlotState.Available
        || (slot.State == SlotState.Cooling && !slot.ResetPending && now >= slot.AvailableAt);

    private OpenFailure CheckCreationAllowed(string ip, DateTimeOffset now)
    {
        if (_slots.Count == 0)
        {
            return OpenFailure.NoAccountConfigured;
        }

        if (_options.MaxEnvironmentsPerIp > 0
            && _leases.Values.Count(l => l.ClientIp == ip) >= _options.MaxEnvironmentsPerIp)
        {
            return OpenFailure.TooManyEnvironmentsForIp;
        }

        if (_options.MaxNewEnvironmentsPerIpPerHour > 0 && _creationsByIp.TryGetValue(ip, out var creations))
        {
            PruneCreations(creations, now);
            if (creations.Count >= _options.MaxNewEnvironmentsPerIpPerHour)
            {
                return OpenFailure.RateLimited;
            }
        }

        return OpenFailure.None;
    }

    private void RecordCreation(string ip, DateTimeOffset now)
    {
        if (!_creationsByIp.TryGetValue(ip, out var creations))
        {
            creations = new Queue<DateTimeOffset>();
            _creationsByIp.Add(ip, creations);
        }

        creations.Enqueue(now);
    }

    private static void PruneCreations(Queue<DateTimeOffset> creations, DateTimeOffset now)
    {
        while (creations.Count > 0 && now - creations.Peek() >= TimeSpan.FromHours(1))
        {
            creations.Dequeue();
        }
    }

    private void ReleaseLocked(Lease lease, GuacStatus reason, DateTimeOffset now)
    {
        _leases.Remove(lease.VisitorId);
        foreach (var ticket in lease.Connections.ToList())
        {
            ticket.Abort(reason);
        }

        lease.Connections.Clear();

        var slot = lease.Slot;
        slot.State = SlotState.Cooling;
        slot.AvailableAt = now.AddSeconds(_options.CooldownSeconds);
        slot.ResetPending = _resetConfigured;
        slot.NextResetAttempt = now;

        _logger.LogInformation(
            "Environnement {Env} libéré ({Account}, motif {Reason}).", lease.ShortId, slot.Account, reason);
    }

    private EnvironmentSnapshot Snapshot(Lease lease)
    {
        var expires = lease.CreatedAt.AddMinutes(_options.MaxLifetimeMinutes);
        if (lease.Connections.Count == 0)
        {
            var idleExpiry = lease.LastActivity.AddMinutes(_options.IdleTimeoutMinutes);
            if (idleExpiry < expires)
            {
                expires = idleExpiry;
            }
        }

        return new EnvironmentSnapshot(lease.ShortId, lease.CreatedAt, expires, lease.Connections.Count);
    }

    private static IEnumerable<PoolAccount> BuildAccounts(EnvironmentOptions options)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in options.Accounts)
        {
            if (string.IsNullOrWhiteSpace(account.Username) || string.IsNullOrEmpty(account.Password))
            {
                continue;
            }

            var built = new PoolAccount(account.Username.Trim(), account.Password, account.Domain ?? options.Domain);
            if (seen.Add(built.DisplayName))
            {
                yield return built;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.UsernamePattern) && options.PatternCount > 0
            && !string.IsNullOrEmpty(options.SharedPassword))
        {
            for (var i = 0; i < options.PatternCount; i++)
            {
                var name = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture, options.UsernamePattern, options.PatternStart + i);
                var built = new PoolAccount(name, options.SharedPassword, options.Domain);
                if (seen.Add(built.DisplayName))
                {
                    yield return built;
                }
            }
        }
    }

    internal enum SlotState
    {
        Available,
        Leased,
        Cooling,
    }

    internal sealed class Slot
    {
        public Slot(PoolAccount account) => Account = account;

        public PoolAccount Account { get; }

        public SlotState State { get; set; }

        public DateTimeOffset AvailableAt { get; set; }

        public bool ResetPending { get; set; }

        public bool ResetRunning { get; set; }

        public int ResetFailures { get; set; }

        public DateTimeOffset NextResetAttempt { get; set; }
    }

    internal sealed class Lease
    {
        public Lease(string visitorId, string shortId, Slot slot, string clientIp, DateTimeOffset now)
        {
            VisitorId = visitorId;
            ShortId = shortId;
            Slot = slot;
            ClientIp = clientIp;
            CreatedAt = now;
            LastActivity = now;
        }

        public string VisitorId { get; }

        public string ShortId { get; }

        /// <summary>Identifiant aléatoire propre à cet environnement (jamais réutilisé).</summary>
        public string Key { get; } = Guid.NewGuid().ToString("N");

        public string ClientIp { get; }

        public DateTimeOffset CreatedAt { get; }

        public DateTimeOffset LastActivity { get; set; }

        public List<ConnectionTicket> Connections { get; } = new();

        public Slot Slot { get; }

        public PoolAccount Account => Slot.Account;
    }
}

/// <summary>
/// Une connexion ouverte (un onglet d'application). À libérer en fin de connexion ;
/// <see cref="Aborted"/> se déclenche si l'environnement est libéré entre-temps.
/// </summary>
public sealed class ConnectionTicket : IDisposable
{
    private readonly EnvironmentPool _pool;
    private readonly CancellationTokenSource _abort = new();
    private int _disposed;

    internal ConnectionTicket(EnvironmentPool pool, EnvironmentPool.Lease lease)
    {
        _pool = pool;
        Lease = lease;
    }

    public PoolAccount Account => Lease.Account;

    public string EnvironmentShortId => Lease.ShortId;

    public string EnvironmentKey => Lease.Key;

    public CancellationToken Aborted => _abort.Token;

    /// <summary>Motif de fermeture forcée, si l'environnement a été libéré pendant la connexion.</summary>
    public GuacStatus? AbortReason { get; private set; }

    internal EnvironmentPool.Lease Lease { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _pool.CloseConnection(this);
            _abort.Dispose();
        }
    }

    internal void Abort(GuacStatus reason)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        AbortReason = reason;
        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Connexion fermée au même instant : rien à interrompre.
        }
    }
}
