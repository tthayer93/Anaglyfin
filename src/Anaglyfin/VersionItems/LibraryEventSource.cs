using System;
using MediaBrowser.Controller.Library;

namespace Anaglyfin.VersionItems;

/// <summary>
/// The library events, as the server raises them.
/// </summary>
/// <remarks>
/// A pass-through of the three events Anaglyfin listens to and nothing else: the server's own
/// <see cref="ILibraryManager"/> is the source, this only narrows what a listener may hold on to.
/// </remarks>
public sealed class LibraryEventSource : ILibraryEventSource
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryEventSource"/> class.
    /// </summary>
    /// <param name="libraryManager">The server's library manager, which raises the events.</param>
    /// <exception cref="ArgumentNullException"><paramref name="libraryManager"/> is <c>null</c>.</exception>
    public LibraryEventSource(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    }

    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemAdded
    {
        add => _libraryManager.ItemAdded += value;
        remove => _libraryManager.ItemAdded -= value;
    }

    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemUpdated
    {
        add => _libraryManager.ItemUpdated += value;
        remove => _libraryManager.ItemUpdated -= value;
    }

    /// <inheritdoc />
    public event EventHandler<ItemChangeEventArgs>? ItemRemoved
    {
        add => _libraryManager.ItemRemoved += value;
        remove => _libraryManager.ItemRemoved -= value;
    }
}
