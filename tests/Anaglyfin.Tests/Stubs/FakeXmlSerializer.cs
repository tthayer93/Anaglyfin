using System;
using System.IO;
using MediaBrowser.Model.Serialization;

namespace Anaglyfin.Tests.Stubs;

/// <summary>
/// Minimal <see cref="IXmlSerializer"/> accepted by the plugin base constructor.
/// </summary>
/// <remarks>
/// The scaffold never reads or writes configuration, so every member fails fast:
/// a test that starts needing real serialization is a signal that the settings
/// model has landed and should replace this stub.
/// </remarks>
internal sealed class FakeXmlSerializer : IXmlSerializer
{
    public object DeserializeFromBytes(Type type, byte[] buffer)
        => throw new NotSupportedException("Configuration persistence is not implemented yet.");

    public object DeserializeFromFile(Type type, string file)
        => throw new NotSupportedException("Configuration persistence is not implemented yet.");

    public object DeserializeFromStream(Type type, Stream stream)
        => throw new NotSupportedException("Configuration persistence is not implemented yet.");

    public void SerializeToFile(object obj, string file)
        => throw new NotSupportedException("Configuration persistence is not implemented yet.");

    public void SerializeToStream(object obj, Stream stream)
        => throw new NotSupportedException("Configuration persistence is not implemented yet.");
}
