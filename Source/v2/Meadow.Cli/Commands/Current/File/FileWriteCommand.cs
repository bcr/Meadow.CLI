﻿using CliFx.Attributes;
using Microsoft.Extensions.Logging;

namespace Meadow.CLI.Commands.DeviceManagement;

[Command("file write", Description = "Writes one or more files to the device from the local file system")]
public class FileWriteCommand : BaseDeviceCommand<FileWriteCommand>
{
    [CommandOption(
        "files",
        'f',
        Description = "The file(s) to write to the Meadow Files System",
        IsRequired = true)]
    public IList<string> Files { get; init; } = Array.Empty<string>();

    [CommandOption(
        "targetFiles",
        't',
        Description = "The filename(s) to use on the Meadow File System")]
    public IList<string> TargetFileNames { get; init; } = Array.Empty<string>();

    public FileWriteCommand(MeadowConnectionManager connectionManager, ILoggerFactory loggerFactory)
        : base(connectionManager, loggerFactory)
    {
    }

    protected override async ValueTask ExecuteCommand()
    {
        await base.ExecuteCommand();

        if (Connection != null)
        {
            if (TargetFileNames.Any() && Files.Count != TargetFileNames.Count)
            {
                Logger?.LogError(
                    $"Number of files to write ({Files.Count}) does not match the number of target file names ({TargetFileNames.Count}).");

                return;
            }

            Connection.FileWriteProgress += (s, e) =>
            {
                var p = (e.completed / (double)e.total) * 100d;

                // Console instead of Logger due to line breaking for progress bar
                Console?.Output.Write($"Writing {e.fileName}: {p:0}%     \r");
            };

            Logger?.LogInformation($"Writing {Files.Count} file{(Files.Count > 1 ? "s" : "")} to device...");

            for (var i = 0; i < Files.Count; i++)
            {
                if (!File.Exists(Files[i]))
                {
                    Logger?.LogError($"Cannot find file '{Files[i]}'. Skippping");
                }
                else
                {
                    try
                    {
                        if (Connection.Device != null)
                        {
                            var targetFileName = GetTargetFileName(i);

                            Logger?.LogInformation($"Writing '{Files[i]}' as '{targetFileName}' to device");

                            await Connection.Device.WriteFile(Files[i], targetFileName, CancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"Error writing file: {ex.Message}");
                    }
                }
            }
        }
    }

    private string GetTargetFileName(int i)
    {
        if (TargetFileNames.Any()
         && TargetFileNames.Count >= i
         && string.IsNullOrWhiteSpace(TargetFileNames[i]) == false)
        {
            return TargetFileNames[i];
        }

        return new FileInfo(Files[i]).Name;
    }
}