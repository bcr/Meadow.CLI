﻿using CliFx.Attributes;
using Meadow.Cli;
using Meadow.CLI.Core.Internals.Dfu;
using Meadow.LibUsb;
using Meadow.Software;
using Microsoft.Extensions.Logging;

namespace Meadow.CLI.Commands.DeviceManagement;

[Command("flash os", Description = "** Deprecated ** Use `firmware write` instead")]
public class FlashOsCommand : BaseDeviceCommand<FlashOsCommand>
{
    [CommandOption("osFile", 'o', Description = "Path to the Meadow OS binary")]
    public string? OSFile { get; init; }

    [CommandOption("runtimeFile", 'r', Description = "Path to the Meadow Runtime binary")]
    public string? RuntimeFile { get; init; }

    [CommandOption("skipDfu", 'd', Description = "Skip DFU flash")]
    public bool SkipOS { get; init; }

    [CommandOption("skipEsp", 'e', Description = "Skip ESP flash")]
    public bool SkipEsp { get; init; }

    [CommandOption("skipRuntime", 'k', Description = "Skip updating the runtime")]
    public bool SkipRuntime { get; init; }

    [CommandOption("dontPrompt", 'p', Description = "Don't show bulk erase prompt")]
    public bool DontPrompt { get; init; }

    [CommandOption("osVersion", 'v', Description = "Flash a specific downloaded OS version - x.x.x.x")]
    public string? Version { get; private set; }

    private FirmwareType[]? Files { get; set; } = default!;
    private bool UseDfu = true;

    private FileManager FileManager { get; }
    private ISettingsManager Settings { get; }

    private ILibUsbDevice? _libUsbDevice;

    public FlashOsCommand(ISettingsManager settingsManager, FileManager fileManager, MeadowConnectionManager connectionManager, ILoggerFactory loggerFactory)
        : base(connectionManager, loggerFactory)
    {
        Logger?.LogWarning($"Deprecated command.  Use `firmware write` instead");

        FileManager = fileManager;
        Settings = settingsManager;
    }

    protected override async ValueTask ExecuteCommand()
    {
        await base.ExecuteCommand();

        if (Connection != null)
        {
            var package = await GetSelectedPackage();

            var files = new List<FirmwareType>();
            if (!SkipOS) files.Add(FirmwareType.OS);
            if (!SkipEsp) files.Add(FirmwareType.ESP);
            if (!SkipRuntime) files.Add(FirmwareType.Runtime);
            Files = files.ToArray();

            if (Files == null && package != null)
            {
                Logger?.LogInformation($"Writing all firmware for version '{package.Version}'...");

                Files = new FirmwareType[]
                    {
                    FirmwareType.OS,
                    FirmwareType.Runtime,
                    FirmwareType.ESP
                    };
            }

            if (Files != null)
            {
                if (!Files.Contains(FirmwareType.OS) && UseDfu)
                {
                    Logger?.LogError($"DFU is only used for OS files.  Select an OS file or remove the DFU option");
                    return;
                }

                bool deviceSupportsOta = false; // TODO: get this based on device OS version

                if (package != null && package.OsWithoutBootloader == null
                    || !deviceSupportsOta
                    || UseDfu)
                {
                    UseDfu = true;
                }

                if (UseDfu && Files.Contains(FirmwareType.OS))
                {
                    // get a list of ports - it will not have our meadow in it (since it should be in DFU mode)
                    var initialPorts = await MeadowConnectionManager.GetSerialPorts();

                    // get the device's serial number via DFU - we'll need it to find the device after it resets
                    try
                    {
                        _libUsbDevice = GetLibUsbDeviceForCurrentEnvironment();
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError(ex.Message);
                        return;
                    }

                    var serial = _libUsbDevice.GetDeviceSerialNumber();

                    // no connection is required here - in fact one won't exist
                    // unless maybe we add a "DFUConnection"?

                    try
                    {
                        if (package != null && package.OSWithBootloader != null)
                        {
                            await WriteOsWithDfu(package.GetFullyQualifiedPath(package.OSWithBootloader), serial);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"Exception type: {ex.GetType().Name}");

                        // TODO: scope this to the right exception type for Win 10 access violation thing
                        // TODO: catch the Win10 DFU error here and change the global provider configuration to "classic"
                        Settings.SaveSetting(SettingsManager.PublicSettings.LibUsb, "classic");

                        Logger?.LogWarning("This machine requires an older version of libusb.  Not to worry, I'll make the change for you, but you will have to re-run this 'firmware write' command.");
                        return;
                    }

                    // now wait for a new serial port to appear
                    var ports = await MeadowConnectionManager.GetSerialPorts();
                    var retryCount = 0;

                    var newPort = ports.Except(initialPorts).FirstOrDefault();
                    while (newPort == null)
                    {
                        if (retryCount++ > 10)
                        {
                            throw new Exception("New meadow device not found");
                        }
                        await Task.Delay(500);
                        ports = await MeadowConnectionManager.GetSerialPorts();
                        newPort = ports.Except(initialPorts).FirstOrDefault();
                    }

                    // configure the route to that port for the user
                    Settings.SaveSetting(SettingsManager.PublicSettings.Route, newPort);

                    var cancellationToken = Console?.RegisterCancellationHandler();

                    if (Files.Any(f => f != FirmwareType.OS))
                    {
                        await Connection.WaitForMeadowAttach();

                        await WriteFiles();
                    }

                    if (Connection.Device != null)
                    {
                        var deviceInfo = await Connection.Device.GetDeviceInfo(cancellationToken);

                        if (deviceInfo != null)
                        {
                            Logger?.LogInformation($"Done.");
                            Logger?.LogInformation(deviceInfo.ToString());
                        }
                    }
                }
                else
                {
                    await WriteFiles();
                }
            }
        }
    }

    private ILibUsbDevice GetLibUsbDeviceForCurrentEnvironment()
    {
        ILibUsbProvider provider;

        // TODO: read the settings manager to decide which provider to use (default to non-classic)
        var setting = Settings.GetAppSetting(SettingsManager.PublicSettings.LibUsb);
        if (setting == "classic")
        {
            provider = new ClassicLibUsbProvider();
        }
        else
        {
            provider = new LibUsbProvider();
        }

        var devices = provider.GetDevicesInBootloaderMode();

        switch (devices.Count)
        {
            case 0:
                throw new Exception("No device found in bootloader mode");
            case 1:
                return devices[0];
            default:
                throw new Exception("Multiple devices found in bootloader mode.  Disconnect all but one");
        }
    }

    private async Task<FirmwarePackage?> GetSelectedPackage()
    {
        await FileManager.Refresh();

        var collection = FileManager.Firmware["Meadow F7"];
        FirmwarePackage package;

        if (Version != null)
        {
            // make sure the requested version exists
            var existing = collection.FirstOrDefault(v => v.Version == Version);

            if (existing == null)
            {
                Logger?.LogError($"Requested version '{Version}' not found.");
                return null;
            }
            package = existing;
        }
        else
        {
            Version = collection.DefaultPackage?.Version ??
                throw new Exception("No default version set");

            package = collection.DefaultPackage;
        }

        return package;
    }

    private async ValueTask WriteFiles()
    {
        if (Connection != null)
        {
            var package = await GetSelectedPackage();

            if (Connection.Device != null
                && package != null)
            {
                var wasRuntimeEnabled = await Connection.Device.IsRuntimeEnabled(CancellationToken);

                if (wasRuntimeEnabled)
                {
                    Logger?.LogInformation("Disabling device runtime...");
                    await Connection.Device.RuntimeDisable();
                }

                Connection.FileWriteProgress += (s, e) =>
                {
                    var p = (e.completed / (double)e.total) * 100d;
                    Console?.Output.Write($"Writing {e.fileName}: {p:0}%     \r");
                };


                if (Files != null)
                {
                    if (Files.Contains(FirmwareType.OS))
                    {
                        if (UseDfu)
                        {
                            // this would have already happened before now (in ExecuteAsync) so ignore
                        }
                        else
                        {
                            Logger?.LogInformation($"{Environment.NewLine}Writing OS {package.Version}...");

                            throw new NotSupportedException("OtA writes for the OS are not yet supported");
                        }
                    }

                    if (Files.Contains(FirmwareType.Runtime))
                    {
                        Logger?.LogInformation($"{Environment.NewLine}Writing Runtime {package.Version}...");

                        // get the path to the runtime file
                        var runtime = package.Runtime;
                        if (string.IsNullOrEmpty(runtime))
                            runtime = string.Empty;
                        var rtpath = package.GetFullyQualifiedPath(runtime);

                    write_runtime:
                        if (!await Connection.Device.WriteRuntime(rtpath, CancellationToken))
                        {
                            Logger?.LogInformation($"Error writing runtime.  Retrying.");
                            goto write_runtime;
                        }
                    }


                    if (Files.Contains(FirmwareType.ESP))
                    {
                        Logger?.LogInformation($"{Environment.NewLine}Writing Coprocessor files...");

                        string[]? fileList;
                        if (package.CoprocApplication != null
                            && package.CoprocBootloader != null
                            && package.CoprocPartitionTable != null)
                        {
                            fileList = new string[]
                            {
                        package.GetFullyQualifiedPath(package.CoprocApplication),
                        package.GetFullyQualifiedPath(package.CoprocBootloader),
                        package.GetFullyQualifiedPath(package.CoprocPartitionTable),
                            };
                        }
                        else
                        {
                            fileList = Array.Empty<string>();
                        }

                        await Connection.Device.WriteCoprocessorFiles(fileList, CancellationToken);
                    }

                    Logger?.LogInformation($"{Environment.NewLine}");

                    if (wasRuntimeEnabled)
                    {
                        await Connection.Device.RuntimeEnable();
                    }
                }

                // TODO: if we're an F7 device, we need to reset
            }
        }
    }

    private async Task WriteOsWithDfu(string osFile, string serialNumber)
    {
        await DfuUtils.FlashFile(
            osFile,
            serialNumber,
            logger: Logger,
            format: DfuUtils.DfuFlashFormat.ConsoleOut);
    }
}