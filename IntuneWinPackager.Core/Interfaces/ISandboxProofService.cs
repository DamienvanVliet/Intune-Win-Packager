using IntuneWinPackager.Models.Entities;

namespace IntuneWinPackager.Core.Interfaces;

public interface ISandboxProofService
{
    Task<SandboxProofSession> StartAsync(
        SandboxProofRequest request,
        CancellationToken cancellationToken = default);

    Task<SandboxProofDetectionResult> ReadResultAsync(
        string resultPath,
        CancellationToken cancellationToken = default);
}
