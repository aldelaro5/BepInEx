﻿using System;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Unity.Mono;

/// <summary>
///     The base plugin type that is used by the BepInEx plugin loader.
/// </summary>
public abstract class BaseUnityPlugin : MonoBehaviour
{
    /// <summary>
    ///     Create a new instance of a plugin and all of its tied in objects.
    /// </summary>
    /// <exception cref="InvalidOperationException">BepInPlugin attribute is missing.</exception>
    protected BaseUnityPlugin()
    {
        var metadata = MetadataHelper.GetMetadata(this);
        if (metadata == null)
            throw new InvalidOperationException("Can't create an instance of " + GetType().FullName +
                                                " because it inherits from BaseUnityPlugin and the BepInPlugin attribute is missing.");

        if (BaseChainloader<BaseUnityPlugin>.Instance.Plugins.TryGetValue(metadata.GUID, out var pluginInfo))
        {
            Info = pluginInfo;
            Info.Instance = this;
        }
        else
        {
            throw new InvalidOperationException($"The plugin information for {metadata.GUID} couldn't be found on the chainloader");
        }

        Logger = BepInEx.Logging.Logger.CreateLogSource(metadata.Name);

        Config = new ConfigFile(Utility.CombinePaths(Paths.ConfigPath, metadata.GUID + ".cfg"), false, metadata);
    }

    /// <summary>
    ///     Information about this plugin as it was loaded.
    /// </summary>
    public PluginInfo Info { get; }

    /// <summary>
    ///     Logger instance tied to this plugin.
    /// </summary>
    public ManualLogSource Logger { get; }

    /// <summary>
    ///     Default config file tied to this plugin. The config file will not be created until
    ///     any settings are added and changed, or <see cref="ConfigFile.Save" /> is called.
    /// </summary>
    public ConfigFile Config { get; }
}
