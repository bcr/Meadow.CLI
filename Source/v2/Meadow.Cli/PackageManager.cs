﻿using GlobExpressions;
using Meadow.Cloud;
using Meadow.Software;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace Meadow.Cli;

public partial class PackageManager : IPackageManager
{
    public const string BuildOptionsFileName = "app.build.yaml";

    private FileManager _fileManager;

    public PackageManager(FileManager fileManager)
    {
        _fileManager = fileManager;
    }

    private bool CleanApplication(string projectFilePath, string configuration = "Release", CancellationToken? cancellationToken = null)
    {
        var proc = new Process();
        proc.StartInfo.FileName = "dotnet";
        proc.StartInfo.Arguments = $"clean {projectFilePath} -c {configuration}";

        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.ErrorDialog = false;
        proc.StartInfo.RedirectStandardError = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.UseShellExecute = false;

        var success = true;

        proc.ErrorDataReceived += (sendingProcess, errorLine) =>
        {
            // this gets called (with empty data) even on a successful build
            Debug.WriteLine(errorLine.Data);
        };
        proc.OutputDataReceived += (sendingProcess, dataLine) =>
        {
            // look for "Build FAILED"
            if (dataLine.Data != null)
            {
                Debug.WriteLine(dataLine.Data);
                if (dataLine.Data.Contains("Build FAILED", StringComparison.InvariantCultureIgnoreCase))
                {
                    Debug.WriteLine("Build failed");
                    success = false;
                }
            }
            // TODO: look for "X Warning(s)" and "X Error(s)"?
            // TODO: do we want to enable forwarding these messages for "verbose" output?
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        proc.WaitForExit();
        var exitCode = proc.ExitCode;
        proc.Close();

        return success;
    }

    public bool BuildApplication(string projectFilePath, string configuration = "Release", bool clean = true, ILogger? logger = null, CancellationToken? cancellationToken = null)
    {
        if (clean && !CleanApplication(projectFilePath, configuration, cancellationToken))
        {
            return false;
        }

        var proc = new Process();
        proc.StartInfo.FileName = "dotnet";
        proc.StartInfo.Arguments = $"build {projectFilePath} -c {configuration}";

        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.ErrorDialog = false;
        proc.StartInfo.RedirectStandardError = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.UseShellExecute = false;

        var success = true;

        proc.ErrorDataReceived += (sendingProcess, errorLine) =>
        {
            // this gets called (with empty data) even on a successful build
            Debug.WriteLine(errorLine.Data);
        };
        proc.OutputDataReceived += (sendingProcess, dataLine) =>
        {
            // look for "Build FAILED"
            if (dataLine.Data != null)
            {
                Debug.WriteLine(dataLine.Data);
                if (dataLine.Data.Contains("Build FAILED", StringComparison.InvariantCultureIgnoreCase))
                {
                    Debug.WriteLine("Build failed");
                    success = false;
                }
            }
            // TODO: look for "X Warning(s)" and "X Error(s)"?
            // TODO: do we want to enable forwarding these messages for "verbose" output?
        };

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();

        proc.WaitForExit();
        var exitCode = proc.ExitCode;
        proc.Close();

        return success;
    }

    public async Task TrimApplication(
        FileInfo applicationFilePath,
        bool includePdbs = false,
        IList<string>? noLink = null,
        ILogger? logger = null,
        CancellationToken? cancellationToken = null)
    {
        if (!applicationFilePath.Exists)
        {
            throw new FileNotFoundException($"{applicationFilePath} not found");
        }

        // does an app.build.yaml file exist?
        var buildOptionsFile = Path.Combine(
            applicationFilePath.DirectoryName ?? string.Empty,
            BuildOptionsFileName);

        if (File.Exists(buildOptionsFile))
        {
            logger?.LogInformation($"'{BuildOptionsFileName}' is present");
            var yaml = File.ReadAllText(buildOptionsFile);
            var deserializer = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build();
            var opts = deserializer.Deserialize<BuildOptions>(yaml);

            if (opts.Deploy?.NoLink != null && opts.Deploy?.NoLink.Count > 0)
            {
                noLink = opts.Deploy.NoLink;
            }
            if (opts.Deploy?.IncludePDBs != null)
            {
                includePdbs = opts.Deploy.IncludePDBs.Value;
            }
        }

        var dependencies = GetDependencies(applicationFilePath)
            .Where(x => x.Contains("App.") == false)
            .ToList();

        await TrimDependencies(
            applicationFilePath,
            dependencies,
            noLink,
            logger,
            includePdbs,
            verbose: false);
    }

    public const string PackageMetadataFileName = "info.json";

    public Task<string> AssemblePackage(
        string contentSourceFolder,
        string outputFolder,
        string osVersion,
        string filter = "*",
        bool overwrite = false,
        ILogger? logger = null,
        CancellationToken? cancellationToken = null)
    {
        var di = new DirectoryInfo(outputFolder);
        if (!di.Exists)
        {
            di.Create();
        }

        var mpakName = Path.Combine(outputFolder, $"{DateTime.UtcNow.ToString("yyyyMMddff")}.mpak");

        if (File.Exists(mpakName))
        {
            if (!overwrite)
            {
                throw new Exception($"Output file '{Path.GetFileName(mpakName)}' already exists.");
            }

            File.Delete(mpakName);
        }

        var appFiles = Glob.Files(contentSourceFolder, filter, GlobOptions.CaseInsensitive).ToArray();

        using var archive = ZipFile.Open(mpakName, ZipArchiveMode.Create);

        foreach (var fPath in appFiles)
        {
            CreateEntry(archive, Path.Combine(contentSourceFolder, fPath), Path.Combine("app", Path.GetFileName(fPath)));
        }

        // write a metadata file info.json in the mpak
        // TODO: we need to see what is necessary and meaningful here and pass it in via param (or the entire file via param?)
        PackageInfo info = new PackageInfo()
        {
            Version = "1.0",
            OsVersion = osVersion
        };

        var infoJson = JsonSerializer.Serialize(info);
        File.WriteAllText(PackageMetadataFileName, infoJson);
        CreateEntry(archive, PackageMetadataFileName, Path.GetFileName(PackageMetadataFileName));

        return Task.FromResult(mpakName);
    }

    private void CreateEntry(ZipArchive archive, string fromFile, string entryPath)
    {
        // Windows '\' Path separator character will be written to the zip which meadow os does not properly unpack
        //  See: https://github.com/dotnet/runtime/issues/41914
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            entryPath = entryPath.Replace('\\', '/');
        }

        archive.CreateEntryFromFile(fromFile, entryPath);
    }

    public static FileInfo[] GetAvailableBuiltConfigurations(string rootFolder, string appName = "App.dll")
    {
        if (!Directory.Exists(rootFolder)) throw new FileNotFoundException();

        // look for a 'bin' folder
        var path = Path.Combine(rootFolder, "bin");
        if (!Directory.Exists(path)) throw new FileNotFoundException($"No 'bin' directory found under {rootFolder}.  Have you compiled?");

        var files = new List<FileInfo>();
        FindApp(path, files);

        void FindApp(string directory, List<FileInfo> fileList)
        {
            foreach (var dir in Directory.GetDirectories(directory))
            {
                var shortname = System.IO.Path.GetFileName(dir);

                if (shortname == PackageManager.PostLinkDirectoryName || shortname == PackageManager.PreLinkDirectoryName)
                {
                    continue;
                }

                var file = Directory.GetFiles(dir).FirstOrDefault(f => string.Compare(Path.GetFileName(f), appName, true) == 0);
                if (file != null)
                {
                    fileList.Add(new FileInfo(file));
                }

                FindApp(dir, fileList);

            }
        }

        return files.ToArray();
    }
}
