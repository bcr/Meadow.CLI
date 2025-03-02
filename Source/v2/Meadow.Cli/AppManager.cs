﻿using Meadow.Hcom;
using Meadow.Software;
using Microsoft.Extensions.Logging;

namespace Meadow.Cli;

public static class AppManager
{
    private static bool MatchingDllExists(string file)
    {
        var root = Path.GetFileNameWithoutExtension(file);
        return File.Exists($"{root}.dll");
    }

    private static bool IsPdb(string file)
    {
        return string.Compare(Path.GetExtension(file), ".pdb", true) == 0;
    }

    private static bool IsXmlDoc(string file)
    {
        if (string.Compare(Path.GetExtension(file), ".xml", true) == 0)
        {
            return MatchingDllExists(file);
        }
        return false;
    }

    public static async Task DeployApplication(
        IPackageManager packageManager,
        IMeadowConnection connection,
        string localBinaryDirectory,
        bool includePdbs,
        bool includeXmlDocs,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // TODO: add sub-folder support when HCOM supports it

        var localFiles = new Dictionary<string, uint>();

        // get a list of files to send
        var dependencies = packageManager.GetDependencies(new FileInfo(Path.Combine(localBinaryDirectory, "App.dll")));

        logger.LogInformation("Generating the list of files to deploy...");
        foreach (var file in dependencies)
        {
            // TODO: add any other filtering capability here

            if (!includePdbs && IsPdb(file)) continue;
            if (!includeXmlDocs && IsXmlDoc(file)) continue;

            // read the file data so we can generate a CRC
            using FileStream fs = File.Open(file, FileMode.Open);
            var len = (int)fs.Length;
            var bytes = new byte[len];

            await fs.ReadAsync(bytes, 0, len, cancellationToken);

            var crc = CrcTools.Crc32part(bytes, len, 0);

            localFiles.Add(file, crc);
        }

        if (localFiles.Count() == 0)
        {
            logger.LogInformation($"No new files to deploy");
        }

        // get a list of files on-device, with CRCs
        var deviceFiles = await connection.GetFileList(true, cancellationToken) ?? Array.Empty<MeadowFileInfo>();

        // get a list of files of the device files that are not in the list we intend to deploy
        var removeFiles = deviceFiles
            .Select(f => Path.GetFileName(f.Name))
            .Except(localFiles.Keys
                .Select(f => Path.GetFileName(f)));

        if (removeFiles.Count() == 0)
        {
            logger.LogInformation($"No files to delete");
        }

        // delete those files
        foreach (var file in removeFiles)
        {
            logger.LogInformation($"Deleting file '{file}'...");
            await connection.DeleteFile(file, cancellationToken);
        }

        // now send all files with differing CRCs
        foreach (var localFile in localFiles)
        {
            var existing = deviceFiles.FirstOrDefault(f => Path.GetFileName(f.Name) == Path.GetFileName(localFile.Key));

            if (existing != null && existing.Crc != null)
            {
                if (uint.Parse(existing.Crc.Substring(2), System.Globalization.NumberStyles.HexNumber) == localFile.Value)
                {
                    // exists and has a matching CRC, skip it
                    continue;
                }
            }

        send_file:

            if (connection != null && !await connection.WriteFile(localFile.Key, null, cancellationToken))
            {
                logger.LogWarning($"Error sending'{Path.GetFileName(localFile.Key)}'.  Retrying.");
                await Task.Delay(100);
                goto send_file;
            }
        }
    }
}
