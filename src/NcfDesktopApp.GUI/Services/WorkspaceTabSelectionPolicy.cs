using System;

namespace NcfDesktopApp.GUI.Services;

/// <summary>决定关闭侧栏标签后应选中哪个剩余工作区。</summary>
public static class WorkspaceTabSelectionPolicy
{
    public static int GetNextSelectedIndex(int removedIndex, int remainingCount)
    {
        if (remainingCount <= 0)
        {
            return -1;
        }

        return Math.Clamp(removedIndex, 0, remainingCount - 1);
    }
}
