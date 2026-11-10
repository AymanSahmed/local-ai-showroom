using LocalAIShowroom.Bridge.Infrastructure;

namespace LocalAIShowroom.Bridge.Services;

/// <summary>One call that tells the UI what this PC can do. Each stack is probed independently.</summary>
public sealed class StatusService(
    WindowsAIService windowsAI,
    WindowsMLService windowsML,
    FoundryLocalService foundry,
    WinmlCliService winmlCli,
    AionService aion,
    OpenVinoSideloadService openVinoSideload,
    EgressGuard guard)
{
    public async Task<object> GetAsync(CancellationToken ct)
    {
        var windowsAITask = Safe(() => Task.FromResult(windowsAI.GetStatus()), TimeSpan.FromSeconds(10));
        var windowsMLTask = Safe(() => windowsML.GetStatusAsync(ct), TimeSpan.FromSeconds(30));
        var foundryTask = Safe(() => foundry.GetStatusAsync(ct), TimeSpan.FromSeconds(45));
        await Task.WhenAll(windowsAITask, windowsMLTask, foundryTask);

        return new
        {
            app = "Local AI Showroom",
            version = BuildInfo.Version,
            simulated = false,
            time = DateTimeOffset.Now,
            machine = MachineInfo.Get(),
            windowsAI = windowsAITask.Result,
            windowsML = windowsMLTask.Result,
            foundry = foundryTask.Result,
            aion = aion.GetStatus(),
            openvino = openVinoSideload.GetStatus(),
            winmlCli = winmlCli.GetStatus(),
            privacy = new
            {
                listensOn = "loopback only",
                localCalls = guard.Allowed,
                blockedCloudCalls = guard.Blocked,
                lastBlockedHost = guard.LastBlockedHost,
            },
        };
    }

    private static async Task<object> Safe(Func<Task<object>> probe, TimeSpan timeout)
    {
        try
        {
            var task = Task.Run(probe);
            var finished = await Task.WhenAny(task, Task.Delay(timeout));
            if (finished != task) return new { error = $"Timed out after {timeout.TotalSeconds:0} s." };
            return await task;
        }
        catch (Exception ex)
        {
            return new { error = Errors.Describe(ex) };
        }
    }
}
