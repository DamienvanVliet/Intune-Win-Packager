using IntuneWinPackager.Models.Process;

namespace IntuneWinPackager.Core.Interfaces;

public interface IProcessRunner
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, IProgress<ProcessOutputLine>? outputProgress = null, CancellationToken cancellationToken = default);
}
