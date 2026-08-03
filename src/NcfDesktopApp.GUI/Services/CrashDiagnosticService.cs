/*----------------------------------------------------------------
    Copyright (C) 2026 Senparc

    文件名：CrashDiagnosticService.cs
    文件功能描述：跨平台记录未处理托管异常，补充系统崩溃报告缺失的 .NET 调用栈

    创建标识：Senparc - 20260803

    修改标识：Senparc - 20260804
    修改描述：v0.6.0 增加桌面进程异常诊断记录

----------------------------------------------------------------*/

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace NcfDesktopApp.GUI.Services;

internal static class CrashDiagnosticService
{
    private static readonly object FileLock = new();
    private static int _registered;

    public static string LogPath => Path.Combine(NcfService.AppDataPath, "last-managed-exception.log");

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.UnhandledException += (_, args) =>
            Append("Avalonia UI dispatcher", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Append(
                args.IsTerminating ? "AppDomain terminating" : "AppDomain",
                args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
            Append("Unobserved task", args.Exception);
    }

    private static void Append(string source, Exception exception)
    {
        try
        {
            var entry = new StringBuilder()
                .AppendLine("------------------------------------------------------------")
                .Append("Time: ").AppendLine(DateTimeOffset.Now.ToString("O"))
                .Append("Source: ").AppendLine(source)
                .Append("OS: ").AppendLine(RuntimeInformation.OSDescription)
                .Append("Process architecture: ").AppendLine(RuntimeInformation.ProcessArchitecture.ToString())
                .AppendLine(exception.ToString())
                .ToString();

            lock (FileLock)
            {
                Directory.CreateDirectory(NcfService.AppDataPath);
                File.AppendAllText(LogPath, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // 异常处理路径必须保持无抛出，避免覆盖原始异常。
        }
    }
}
