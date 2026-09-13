using System;
using MediaBrowser.Controller.Library;

namespace Anaglyfin.VersionItems;

/// <summary>
/// The library events that mean "Anaglyfin's version items may be out of date now".
/// </summary>
/// <remarks>
/// A three-event view of <see cref="ILibraryManager.ItemAdded"/>, <c>ItemUpdated</c> and
/// <c>ItemRemoved</c>, so that whoever listens can be tested without a library manager - and so the
/// listener cannot reach any of the other hundred members of that interface by accident.
/// </remarks>
public interface ILibraryEventSource
{
    /// <summary>Raised after an item has been added to the library.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemAdded;

    /// <summary>Raised after an item has been written back to the library.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemUpdated;

    /// <summary>Raised after an item has been removed from the library.</summary>
    event EventHandler<ItemChangeEventArgs>? ItemRemoved;
}
