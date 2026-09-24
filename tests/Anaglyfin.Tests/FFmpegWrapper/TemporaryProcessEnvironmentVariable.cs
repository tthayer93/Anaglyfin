using System;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// A process-environment variable that is put back when the test is done.
/// </summary>
/// <remarks>
/// The hosted-service entry points resolve their configuration from the real process environment, so
/// a test that has to prove what startup does with one setting cannot avoid changing that setting for
/// the length of its own call. Restoring it is the test's debt: a leaked deployment setting would turn
/// one hermetic test into a different one for every other test in the run.
/// </remarks>
internal sealed class TemporaryProcessEnvironmentVariable : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TemporaryProcessEnvironmentVariable"/> class and
    /// installs the test value.
    /// </summary>
    /// <param name="name">The variable to set.</param>
    /// <param name="value">The value to install.</param>
    public TemporaryProcessEnvironmentVariable(string name, string? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);

        Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>Puts the variable back the way this test found it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Environment.SetEnvironmentVariable(_name, _previousValue);
        _disposed = true;
    }
}
