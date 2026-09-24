using System;
using Anaglyfin.FFmpegWrapper;
using Xunit;

namespace Anaglyfin.Tests.FFmpegWrapper;

/// <summary>
/// A fact that needs the host to state a boot identifier of its own.
/// </summary>
/// <remarks>
/// A foreign-boot mark is meaningful only against a host that knows which boot is not foreign. A host
/// that states no identifier intentionally treats every mark's process id as local, so the behaviour
/// under test cannot be observed there; <see cref="TranscodeSlotStoreTests"/> states the fallback
/// directly against a named boot instead of depending on the host.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class RequiresHostBootIdFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequiresHostBootIdFactAttribute"/> class.
    /// </summary>
    public RequiresHostBootIdFactAttribute()
    {
        if (TranscodeSlotStore.CurrentBootId is null)
        {
            Skip = "The host states no boot identifier, so a foreign boot cannot be observed.";
        }
    }
}
