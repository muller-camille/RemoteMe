using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace RemoteMe.Tests;

/// <summary>Redirige les journaux de l'application vers la sortie du test.</summary>
internal sealed class XunitLoggerProvider : ILoggerProvider
{
    private readonly ITestOutputHelper _output;

    public XunitLoggerProvider(ITestOutputHelper output) => _output = output;

    public ILogger CreateLogger(string categoryName) => new XunitLogger(_output, categoryName);

    public void Dispose()
    {
    }

    private sealed class XunitLogger : ILogger
    {
        private readonly ITestOutputHelper _output;
        private readonly string _category;

        public XunitLogger(ITestOutputHelper output, string category)
        {
            _output = output;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try
            {
                _output.WriteLine($"[{logLevel}] {_category}: {formatter(state, exception)}{(exception is null ? "" : Environment.NewLine + exception)}");
            }
            catch (InvalidOperationException)
            {
                // Test déjà terminé.
            }
        }
    }
}
