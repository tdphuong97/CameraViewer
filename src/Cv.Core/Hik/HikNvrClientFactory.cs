using Cv.Core.Nvr;

namespace Cv.Core.Hik;

/// <summary>Creates ISAPI clients (Track A implements HikNvrClient behind this).</summary>
public sealed class HikNvrClientFactory : INvrClientFactory
{
    public INvrClient Create(NvrConfig config, string password) =>
        throw new NotImplementedException("Track A: HikNvrClient");
}
