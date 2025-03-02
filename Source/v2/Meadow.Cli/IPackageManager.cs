﻿using Microsoft.Extensions.Logging;

namespace Meadow.Cli;

public interface IPackageManager
{
    List<string> GetDependencies(FileInfo file);

    bool BuildApplication(
        string projectFilePath,
        string configuration = "Release",
        bool clean = true,
        ILogger? logger = null,
        CancellationToken? cancellationToken = null);

    Task TrimApplication(
        FileInfo applicationFilePath,
        bool includePdbs = false,
        IList<string>? noLink = null,
        ILogger? logger = null,
        CancellationToken? cancellationToken = null);

    Task<string> AssemblePackage(
        string contentSourceFolder,
        string outputFolder,
        string osVersion,
        string filter = "*",
        bool overwrite = false,
        ILogger? logger = null,
        CancellationToken? cancellationToken = null);

}
