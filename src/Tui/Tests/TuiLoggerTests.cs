using Microsoft.Extensions.Logging;
using Xunit;

namespace Tui.Tests;

public sealed class TuiLoggerTests
{
    [Fact]
    public void OperationFailed_WritesStructuredOperationAndException()
    {
        var logger = new CollectingLogger();
        var exception = new InvalidOperationException("disk is read-only");

        Services.TuiLogger.OperationFailed(logger, exception, "persist session messages");

        Assert.Equal(LogLevel.Warning, logger.Level);
        Assert.Same(exception, logger.Exception);
        Assert.Equal("persist session messages", logger.Properties["Operation"]);
    }

    private sealed class CollectingLogger : ILogger
    {
        public LogLevel Level { get; private set; }
        public Exception? Exception { get; private set; }
        public Dictionary<string, object?> Properties { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Level = logLevel;
            Exception = exception;
            if (state is not IEnumerable<KeyValuePair<string, object?>> properties)
                return;

            foreach (var property in properties)
                Properties[property.Key] = property.Value;
        }
    }
}
