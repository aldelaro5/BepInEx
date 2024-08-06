using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Common;
using BepInEx.Unity.IL2CPP.Hook;
using BepInEx.Unity.IL2CPP.Logging;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Il2CppInterop.HarmonySupport;
using Il2CppInterop.Runtime.Startup;
using LibCpp2IL;
using Microsoft.Extensions.Logging;
using Mono.Cecil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using MSLoggerFactory = Microsoft.Extensions.Logging.LoggerFactory;

namespace BepInEx.Unity.IL2CPP;

internal static partial class Il2CppInteropManager
{
    static Il2CppInteropManager()
    {
        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
        InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
        LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
    }

    private static readonly ConfigEntry<bool> UpdateInteropAssemblies =
        ConfigFile.CoreConfig.Bind("IL2CPP",
                                   "UpdateInteropAssemblies",
                                   true,
                                   new StringBuilder()
                                       .AppendLine("Whether to run Il2CppInterop automatically to generate Il2Cpp support assemblies when they are outdated.")
                                       .AppendLine("If disabled assemblies in `BepInEx/interop` won't be updated between game or BepInEx updates!")
                                       .ToString());

    private static readonly ConfigEntry<string> UnityBaseLibrariesSource = ConfigFile.CoreConfig.Bind(
     "IL2CPP", "UnityBaseLibrariesSource",
     "https://unity.bepinex.dev/libraries/{VERSION}.zip",
     new StringBuilder()
         .AppendLine("URL to the ZIP of managed Unity base libraries.")
         .AppendLine("The base libraries are used by Il2CppInterop to generate interop assemblies.")
         .AppendLine("The URL can include {VERSION} template which will be replaced with the game's Unity engine version.")
         .ToString());

    private static readonly ConfigEntry<string> ConfigUnhollowerDeobfuscationRegex = ConfigFile.CoreConfig.Bind(
     "IL2CPP", "UnhollowerDeobfuscationRegex",
     string.Empty,
     new StringBuilder()
         .AppendLine("The RegEx string to pass to Il2CppAssemblyUnhollower for renaming obfuscated names.")
         .AppendLine("All types and members matching this RegEx will get a name based on their signature,")
         .AppendLine("resulting in names that persist after game updates.")
         .ToString());

    private static readonly ConfigEntry<bool> ScanMethodRefs = ConfigFile.CoreConfig.Bind(
     "IL2CPP", "ScanMethodRefs",
     Environment.Is64BitProcess,
     "If enabled, Il2CppInterop will use xref to find dead methods and generate CallerCount attributes.");

    private static readonly ConfigEntry<bool> DumpDummyAssemblies = ConfigFile.CoreConfig.Bind(
     "IL2CPP", "DumpDummyAssemblies",
     false,
     "If enabled, BepInEx will save dummy assemblies generated by an Cpp2IL dumper into BepInEx/dummy.");

    private static readonly ConfigEntry<string> IL2CPPInteropAssembliesPath = ConfigFile.CoreConfig.Bind(
     "IL2CPP", "IL2CPPInteropAssembliesPath",
     "{BepInEx}",
     new StringBuilder()
         .AppendLine("The path to the folder where IL2CPPInterop assemblies are stored.")
         .AppendLine("Supports the following placeholders:")
         .AppendLine("{BepInEx} - Path to the BepInEx folder.")
         .AppendLine("{ProcessName} - Name of the current process")
         .ToString());

    private static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource("InteropManager");
    private static Il2CppDetourFactory DetourFactory = new();
    private static string il2cppInteropBasePath;

    private static bool initialized;

    public static string GameAssemblyPath => Environment.GetEnvironmentVariable("BEPINEX_GAME_ASSEMBLY_PATH") ??
                                             Path.Combine(Paths.GameRootPath,
                                                          "GameAssembly." + 
                                                          (PlatformDetection.OS.Is(OSKind.OSX) ? "dylib" :
                                                          PlatformDetection.OS.Is(OSKind.Posix) ? "so" : "dll"));

    private static string HashPath => Path.Combine(IL2CPPInteropAssemblyPath, "assembly-hash.txt");

    private static string IL2CPPBasePath {
        get {
            if (il2cppInteropBasePath != null)
                return il2cppInteropBasePath;
            var path = Utility.GetCommandLineArgValue("--unhollowed-path") ?? IL2CPPInteropAssembliesPath.Value;
            il2cppInteropBasePath = path.Replace("{BepInEx}", Paths.BepInExRootPath)
                                     .Replace("{ProcessName}", Paths.ProcessName);
            return il2cppInteropBasePath;
        }
    }

    private static string UnityBaseLibsDirectory => Path.Combine(IL2CPPBasePath, "unity-libs");

    internal static string IL2CPPInteropAssemblyPath => Path.Combine(IL2CPPBasePath, "interop");

    private static ILoggerFactory LoggerFactory { get; } = MSLoggerFactory.Create(b =>
    {
        b.AddProvider(new BepInExLoggerProvider())
         .SetMinimumLevel(LogLevel.Trace); // Each BepInEx log listener has its own filtering
    });

    private static string ComputeHash()
    {
        using var md5 = MD5.Create();

        static void HashFile(ICryptoTransform hash, string file)
        {
            const int defaultCopyBufferSize = 81920;
            using var fs = File.OpenRead(file);
            var buffer = new byte[defaultCopyBufferSize];
            int read;
            while ((read = fs.Read(buffer)) > 0)
                hash.TransformBlock(buffer, 0, read, buffer, 0);
        }

        static void HashString(ICryptoTransform hash, string str)
        {
            var buffer = Encoding.UTF8.GetBytes(str);
            hash.TransformBlock(buffer, 0, buffer.Length, buffer, 0);
        }

        HashFile(md5, GameAssemblyPath);

        if (Directory.Exists(UnityBaseLibsDirectory))
            foreach (var file in Directory.EnumerateFiles(UnityBaseLibsDirectory, "*.dll",
                                                          SearchOption.TopDirectoryOnly))
            {
                HashString(md5, Path.GetFileName(file));
                HashFile(md5, file);
            }

        // Hash some common dependencies as they can affect output
        HashString(md5, typeof(InteropAssemblyGenerator).Assembly.GetName().Version.ToString());
        HashString(md5, typeof(Cpp2IlApi).Assembly.GetName().Version.ToString());

        md5.TransformFinalBlock(new byte[0], 0, 0);

        return Utility.ByteArrayToString(md5.Hash);
    }

    private static bool CheckIfGenerationRequired()
    {
        static bool NeedGenerationOrSkip()
        {
            if (!UpdateInteropAssemblies.Value)
            {
                var hash = ComputeHash();
                Logger.LogWarning($"Interop assemblies are possibly out of date. To disable this message, create file {HashPath} with the following contents: {hash}");
                return false;
            }

            return true;
        }

        if (!Directory.Exists(IL2CPPInteropAssemblyPath))
            return true;

        if (!File.Exists(HashPath))
            return NeedGenerationOrSkip();

        if (ComputeHash() != File.ReadAllText(HashPath) && NeedGenerationOrSkip())
        {
            Logger.LogInfo("Detected outdated interop assemblies, will regenerate them now");
            return true;
        }

        return false;
    }

    private static Assembly ResolveInteropAssemblies(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name);
        return Utility.TryResolveDllAssembly(assemblyName, IL2CPPInteropAssemblyPath, out var foundAssembly)
                   ? foundAssembly
                   : null;
    }

    public static void Initialize()
    {
        if (initialized)
            throw new InvalidOperationException("Already initialized");
        initialized = true;

        Environment.SetEnvironmentVariable("IL2CPP_INTEROP_DATABASES_LOCATION", IL2CPPInteropAssemblyPath);
        AppDomain.CurrentDomain.AssemblyResolve += ResolveInteropAssemblies;

        GenerateInteropAssemblies();
        DetourContext.SetGlobalContext(new DetourFactoryContext(DetourFactory));

        var interopLogger = LoggerFactory.CreateLogger("Il2CppInterop");
        var unityVersion = UnityInfo.Version;
        Il2CppInteropRuntime.Create(new RuntimeConfiguration
                            {
                                UnityVersion = new Version(unityVersion.Major, unityVersion.Minor, unityVersion.Build),
                                DetourProvider = new Il2CppInteropDetourProvider()
                            })
                            .AddLogger(interopLogger)
                            // .AddHarmonySupport()
                            .Start();
    }

    private static void GenerateInteropAssemblies()
    {
        if (!CheckIfGenerationRequired())
            return;

        try
        {
            Directory.CreateDirectory(IL2CPPInteropAssemblyPath);
            Directory.EnumerateFiles(IL2CPPInteropAssemblyPath, "*.dll").Do(File.Delete);

            AppDomain.CurrentDomain.AddCecilPlatformAssemblies(UnityBaseLibsDirectory);
            DownloadUnityAssemblies();
            var asmResolverAssemblies = RunCpp2Il();
            var cecilAssemblies = new AsmToCecilConverter(asmResolverAssemblies).ConvertAll();

            if (DumpDummyAssemblies.Value)
            {
                var dummyPath = Path.Combine(Paths.BepInExRootPath, "dummy");
                Directory.CreateDirectory(dummyPath);
                foreach (var assemblyDefinition in cecilAssemblies)
                    assemblyDefinition.Write(Path.Combine(dummyPath, $"{assemblyDefinition.Name.Name}.dll"));
            }

            RunIl2CppInteropGenerator(cecilAssemblies);

            File.WriteAllText(HashPath, ComputeHash());
        }
        catch (Exception e)
        {
            Logger.LogError($"Failed to generate Il2Cpp interop assemblies: {e}");
        }
    }

    private static void DownloadUnityAssemblies()
    {
        var unityVersion = UnityInfo.Version;
        var source =
            UnityBaseLibrariesSource.Value.Replace("{VERSION}",
                                                   $"{unityVersion.Major}.{unityVersion.Minor}.{unityVersion.Build}");

        if (!string.IsNullOrEmpty(source))
        {
            Logger.LogMessage("Downloading unity base libraries");

            Directory.CreateDirectory(UnityBaseLibsDirectory);
            Directory.EnumerateFiles(UnityBaseLibsDirectory, "*.dll").Do(File.Delete);

            using var httpClient = new HttpClient();
            using var zipStream = httpClient.GetStreamAsync(source).GetAwaiter().GetResult();
            using var zipArchive = new ZipArchive(zipStream, ZipArchiveMode.Read);

            Logger.LogMessage("Extracting downloaded unity base libraries");
            zipArchive.ExtractToDirectory(UnityBaseLibsDirectory);
        }
    }

    private static List<AsmResolver.DotNet.AssemblyDefinition> RunCpp2Il()
    {
        Logger.LogMessage("Running Cpp2IL to generate dummy assemblies");

        var metadataPath = Path.Combine(Paths.GameDataPath,
                                        "il2cpp_data",
                                        "Metadata",
                                        "global-metadata.dat");

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var cpp2IlLogger = BepInEx.Logging.Logger.CreateLogSource("Cpp2IL");

        Cpp2IL.Core.Logging.Logger.VerboseLog += (message, s) =>
            cpp2IlLogger.LogDebug($"[{s}] {message.Trim()}");
        Cpp2IL.Core.Logging.Logger.InfoLog += (message, s) =>
            cpp2IlLogger.LogInfo($"[{s}] {message.Trim()}");
        Cpp2IL.Core.Logging.Logger.WarningLog += (message, s) =>
            cpp2IlLogger.LogWarning($"[{s}] {message.Trim()}");
        Cpp2IL.Core.Logging.Logger.ErrorLog += (message, s) =>
            cpp2IlLogger.LogError($"[{s}] {message.Trim()}");

        var unityVersion = UnityInfo.Version;
        Cpp2IlApi.InitializeLibCpp2Il(GameAssemblyPath, metadataPath, unityVersion, false);

        List<Cpp2IlProcessingLayer> processingLayers = new() { new AttributeInjectorProcessingLayer(), };

        foreach (var cpp2IlProcessingLayer in processingLayers)
        {
            cpp2IlProcessingLayer.PreProcess(Cpp2IlApi.CurrentAppContext, processingLayers);
        }

        foreach (var cpp2IlProcessingLayer in processingLayers)
        {
            cpp2IlProcessingLayer.Process(Cpp2IlApi.CurrentAppContext);
        }

        var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);

        LibCpp2IlMain.Reset();
        Cpp2IlApi.CurrentAppContext = null;

        stopwatch.Stop();
        Logger.LogInfo($"Cpp2IL finished in {stopwatch.Elapsed}");

        return assemblies;
    }

    private static void RunIl2CppInteropGenerator(List<AssemblyDefinition> sourceAssemblies)
    {
        var opts = new GeneratorOptions
        {
            GameAssemblyPath = ScanMethodRefs.Value ? GameAssemblyPath : null,
            Source = sourceAssemblies,
            OutputDir = IL2CPPInteropAssemblyPath,
            UnityBaseLibsDir = Directory.Exists(UnityBaseLibsDirectory) ? UnityBaseLibsDirectory : null,
            ObfuscatedNamesRegex = !string.IsNullOrEmpty(ConfigUnhollowerDeobfuscationRegex.Value)
                                       ? new Regex(ConfigUnhollowerDeobfuscationRegex.Value)
                                       : null,
        };

        var renameMapLocation = Path.Combine(Paths.BepInExRootPath, "DeobfuscationMap.csv.gz");
        if (File.Exists(renameMapLocation))
        {
            Logger.LogInfo("Parsing deobfuscation rename mappings");
            opts.ReadRenameMap(renameMapLocation);
        }

        Logger.LogInfo("Generating interop assemblies");

        var logger = LoggerFactory.CreateLogger("Il2CppInteropGen");

        Il2CppInteropGenerator.Create(opts)
                              .AddLogger(logger)
                              .AddInteropAssemblyGenerator()
                              .Run();

        sourceAssemblies.Do(x => x.Dispose());
    }
}
