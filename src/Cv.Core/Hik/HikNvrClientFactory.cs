using Cv.Core.Nvr;

namespace Cv.Core.Hik;

/// <summary>Creates ISAPI clients. Each call returns a new client; the caller disposes it.</summary>
public sealed class HikNvrClientFactory : INvrClientFactory
{
    public INvrClient Create(NvrConfig config, string password)
    {
        ArgumentNullException.ThrowIfNull(config);
        return new HikNvrClient(config, password ?? "");
    }
}
