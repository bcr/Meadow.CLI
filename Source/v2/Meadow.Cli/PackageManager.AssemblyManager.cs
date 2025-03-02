﻿using Microsoft.Extensions.Logging;
using Mono.Cecil;
using Mono.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace Meadow.Cli;

public partial class PackageManager
{
    private const string IL_LINKER_DIR = "lib";
    public const string PostLinkDirectoryName = "postlink_bin";
    public const string PreLinkDirectoryName = "prelink_bin";
    public const string PackageOutputDirectoryName = "mpak";


    private readonly List<string> dependencyMap = new();

    private string? _meadowAssembliesPath;

    private string? MeadowAssembliesPath
    {
        get
        {
            if (_meadowAssembliesPath == null)
            {
                // for now we only support F7
                // TODO: add switch and support for other platforms
                var store = _fileManager.Firmware["Meadow F7"];
                if (store != null)
                {
                    store.Refresh();
                    if (store.DefaultPackage != null)
                    {
                        var defaultPackage = store.DefaultPackage;

                        if (defaultPackage.BclFolder != null)
                        {
                            _meadowAssembliesPath = defaultPackage.GetFullyQualifiedPath(defaultPackage.BclFolder);
                        }
                    }
                }
            }

            return _meadowAssembliesPath;
        }
    }

    public async Task<IEnumerable<string>?> TrimDependencies(FileInfo file, List<string> dependencies, IList<string>? noLink, ILogger? logger, bool includePdbs, bool verbose = false, string? linkerOptions = null)
    {
        var directoryName = file.DirectoryName;
        if (!string.IsNullOrEmpty(directoryName))
        {
            var fileName = file.Name;
            var prelink_dir = Path.Combine(directoryName, PreLinkDirectoryName);
            var prelink_app = Path.Combine(prelink_dir, fileName);
            var prelink_os = Path.Combine(prelink_dir, "Meadow.dll");

            if (Directory.Exists(prelink_dir))
            {
                Directory.Delete(prelink_dir, recursive: true);
            }

            Directory.CreateDirectory(prelink_dir);
            File.Copy(file.FullName, prelink_app, overwrite: true);

            foreach (var dependency in dependencies)
            {
                File.Copy(dependency,
                            Path.Combine(prelink_dir, Path.GetFileName(Path.GetFileName(dependency))),
                            overwrite: true);

                if (includePdbs)
                {
                    var pdbFile = Path.ChangeExtension(dependency, "pdb");
                    if (File.Exists(pdbFile))
                    {
                        File.Copy(pdbFile,
                            Path.Combine(prelink_dir, Path.GetFileName(pdbFile)),
                            overwrite: true);
                    }
                }
            }

            var postlink_dir = Path.Combine(directoryName, PostLinkDirectoryName);
            if (Directory.Exists(postlink_dir))
            {
                Directory.Delete(postlink_dir, recursive: true);
            }
            Directory.CreateDirectory(postlink_dir);

            var base_path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(base_path))
            {
                var illinker_path = Path.Combine(base_path, IL_LINKER_DIR, "illink.dll");
                var descriptor_path = Path.Combine(base_path, IL_LINKER_DIR, "meadow_link.xml");

                if (!File.Exists(illinker_path))
                {
                    throw new FileNotFoundException("Cannot run trimming operation. illink.dll not found.");
                }

                if (linkerOptions != null)
                {
                    var fi = new FileInfo(linkerOptions);

                    if (fi.Exists)
                    {
                        logger?.LogInformation($"Using linker options from '{linkerOptions}'");
                    }
                    else
                    {
                        logger?.LogWarning($"Linker options file '{linkerOptions}' not found");
                    }
                }


                // add in any run-time no-link arguments
                var no_link_args = string.Empty;
                if (noLink != null)
                {
                    // no-link options want just the assembly name (i.e. no ".dll" extension)
                    no_link_args = string.Join(" ", noLink.Select(o => $"-p copy \"{o.Replace(".dll", string.Empty)}\""));
                }

                var monolinker_args = $"\"{illinker_path}\" -x \"{descriptor_path}\" {no_link_args}  --skip-unresolved --deterministic --keep-facades true --ignore-descriptors true -b true -c link -o \"{postlink_dir}\" -r \"{prelink_app}\" -a \"{prelink_os}\" -d \"{prelink_dir}\"";

                logger?.LogInformation("Trimming assemblies to reduce size (may take several seconds)...");
                if (!string.IsNullOrWhiteSpace(no_link_args))
                {
                    logger?.LogInformation($"no-link args:'{no_link_args}'");
                }

                using (var process = new Process())
                {
                    process.StartInfo.FileName = "dotnet";
                    process.StartInfo.Arguments = monolinker_args;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.Start();

                    // To avoid deadlocks, read the output stream first and then wait
                    string stdOutReaderResult;
                    using (StreamReader stdOutReader = process.StandardOutput)
                    {
                        stdOutReaderResult = await stdOutReader.ReadToEndAsync();
                        if (verbose)
                        {
                            Console.WriteLine("StandardOutput Contains: " + stdOutReaderResult);
                        }

                    }

                    string stdErrorReaderResult;
                    using (StreamReader stdErrorReader = process.StandardError)
                    {
                        stdErrorReaderResult = await stdErrorReader.ReadToEndAsync();
                        if (!string.IsNullOrEmpty(stdErrorReaderResult))
                        {
                            Console.WriteLine("StandardError Contains: " + stdErrorReaderResult);
                        }
                    }

                    process.WaitForExit(60000);
                    if (process.ExitCode != 0)
                    {
                        Debug.WriteLine($"Trimming failed - ILLinker execution error!\nProcess Info: {process.StartInfo.FileName} {process.StartInfo.Arguments} \nExit Code: {process.ExitCode}");
                        throw new Exception("Trimming failed");
                    }
                }

                return Directory.EnumerateFiles(postlink_dir);
            }
            else
            {
                throw new DirectoryNotFoundException("Trimming failed: base_path is invalid");
            }
        }
        else
        {
            throw new ArgumentException("Trimming failed: file.DirectoryName is invalid");
        }
    }

    public List<string> GetDependencies(FileInfo file)
    {
        dependencyMap.Clear();

        var directoryName = file.DirectoryName;
        if (!string.IsNullOrEmpty(directoryName))
        {
            var refs = GetAssemblyNameReferences(file.Name, directoryName);

            var dependencies = GetDependencies(refs, dependencyMap, directoryName);

            return dependencies;
        }
        else
        {
            return new();
        }
    }

    private (Collection<AssemblyNameReference>?, string?) GetAssemblyNameReferences(string fileName, string path)
    {
        static string? ResolvePath(string fileName, string path)
        {
            string attempted_path = Path.Combine(path, fileName);
            if (Path.GetExtension(fileName) != ".exe" &&
                Path.GetExtension(fileName) != ".dll")
            {
                attempted_path += ".dll";
            }
            return File.Exists(attempted_path) ? attempted_path : null;
        }

        //ToDo - is it ever correct to fall back to the root path without a version?
        if (!string.IsNullOrEmpty(MeadowAssembliesPath))
        {
            string? resolved_path = ResolvePath(fileName, MeadowAssembliesPath) ?? ResolvePath(fileName, path);

            if (resolved_path is null)
            {
                return (null, null);
            }

            Collection<AssemblyNameReference> references;

            using (var definition = Mono.Cecil.AssemblyDefinition.ReadAssembly(resolved_path))
            {
                references = definition.MainModule.AssemblyReferences;
            }
            return (references, resolved_path);
        }
        else
        {
            return (null, null);
        }
    }

    private List<string> GetDependencies((Collection<AssemblyNameReference>?, string?) references, List<string> dependencyMap, string folderPath)
    {
        if (references.Item2 == null || dependencyMap.Contains(references.Item2))
            return dependencyMap;

        dependencyMap.Add(references.Item2);

        if (references.Item1 != null)
        {
            foreach (var ar in references.Item1)
            {
                var namedRefs = GetAssemblyNameReferences(ar.Name, folderPath);

                if (namedRefs.Item1 == null)
                    continue;

                GetDependencies(namedRefs, dependencyMap, folderPath);
            }
        }

        return dependencyMap;
    }
}
