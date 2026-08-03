namespace NcfDesktopApp.GUI.Services;

internal static class WorkspaceLifetimePolicy
{
    public static bool ShouldShutdown(int activeWorkspaceCount, int pendingCloseOperations) =>
        activeWorkspaceCount <= 0 && pendingCloseOperations <= 0;
}
