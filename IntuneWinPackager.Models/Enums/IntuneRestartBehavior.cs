namespace IntuneWinPackager.Models.Enums;

public enum IntuneRestartBehavior
{
    DetermineBehaviorBasedOnReturnCodes = 0,
    AppInstallMayForceRestart = 1,
    NoSpecificAction = 2,
    IntuneWillForceRestart = 3
}
