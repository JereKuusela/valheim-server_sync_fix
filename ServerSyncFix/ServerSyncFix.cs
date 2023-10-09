using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace ServerSyncFix;
[HarmonyPatch]
[BepInPlugin(GUID, NAME, VERSION)]
public class ServerSyncFix : BaseUnityPlugin
{
  const string GUID = "server_sync_fix";
  const string NAME = "Server Sync Fix";
  const string VERSION = "1.3";

#nullable disable
  public static ManualLogSource Log;
#nullable enable
  public void Awake()
  {
    Log = Logger;
    new Harmony(GUID).PatchAll();
  }

  private static bool Patched = false;
  [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake)), HarmonyPrefix, HarmonyPriority(Priority.First)]
  private static void DoStuff()
  {
    if (Patched) return;
    Harmony harmony = new(GUID);
    foreach (var info in Chainloader.PluginInfos.Values)
    {
      var assembly = info.Instance.GetType().Assembly;
      if (!IsOutdated(assembly)) continue;
      DisablePatching(assembly, harmony);
      DisableBroadcast(assembly, harmony);
      UnpatchServerSync(assembly, harmony);
    }
  }
  [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake)), HarmonyPostfix, HarmonyPriority(Priority.Last)]
  private static void DoStuff2()
  {
    if (Patched) return;
    Patched = true;
    foreach (var info in Chainloader.PluginInfos.Values)
    {
      var assembly = info.Instance.GetType().Assembly;
      if (!IsOutdated(assembly)) continue;
      Log.LogWarning($"Mod {info.Metadata.GUID} has outdated server sync.");
      var syncs = (IEnumerable)Field(assembly, "configSyncs");
      foreach (var oldSync in syncs)
      {
        UpdateSync(assembly, oldSync);
      }
      ClearConfigSyncs(assembly);
      ClearVersionChecks(assembly);
    }
  }
  private static ServerSync.ConfigSync UpdateSync(Assembly assembly, object oldSync)
  {
    var newSync = CopySync(assembly, oldSync);
    CopyForceLock(assembly, oldSync, newSync);
    CopySettings(assembly, oldSync, newSync);
    CopyValues(assembly, oldSync, newSync);
    CopyLockConfig(assembly, oldSync, newSync);
    Log.LogWarning($"Updated server sync for {newSync.Name}.");
    return newSync;
  }
  private static bool IsOutdated(Assembly assembly)
  {
    var type = CustomSyncedValueBase(assembly);
    if (type == null) return false;
    try
    {
      var field = type.GetField("Priority");
      if (field != null) return false;
    }
    catch { }
    return true;
  }
  private static void DisableBroadcast(Assembly assembly, Harmony harmony)
  {
    try
    {
      var methods = ConfigSync(assembly).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Where(method => method.Name == "Broadcast");
      foreach (var method in methods)
      {
        var mPrefix = SymbolExtensions.GetMethodInfo((long target) => Cancel(ref target));
        harmony.Patch(method, new(mPrefix));
        //Log.LogInfo("Blocked sending.");
      }
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to block sending.");
    }
  }
  private static void DisablePatching(Assembly assembly, Harmony harmony)
  {
    try
    {
      var methods = VersionCheck(assembly).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(method => method.Name == "PatchServerSync");
      foreach (var method in methods)
      {
        var mPrefix = SymbolExtensions.GetMethodInfo(() => Cancel());
        harmony.Patch(method, new(mPrefix));
        //Log.LogInfo("Blocked patching.");
      }
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to block patching.");
    }
  }
  private static void Unpatch(Harmony harmony, Type type, Type classType, string name)
  {
    var prefix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Prefixes.FirstOrDefault(p => p.PatchMethod.DeclaringType == type);
    var postfix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Postfixes.FirstOrDefault(p => p.PatchMethod.DeclaringType == type);
    if (prefix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), prefix.PatchMethod);
    //if (prefix != null)Log.LogInfo("Unpatched prefix of " + name);
    if (postfix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), postfix.PatchMethod);
    //if (postfix != null) Log.LogInfo("Unpatched postfix of " + name);
  }
  private static void UnpatchAll(Harmony harmony, Assembly assembly, Type classType, string name)
  {
    var prefix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Prefixes.FirstOrDefault(p => p.PatchMethod.DeclaringType.Assembly == assembly);
    var postfix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Postfixes.FirstOrDefault(p => p.PatchMethod.DeclaringType.Assembly == assembly);
    if (prefix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), prefix.PatchMethod);
    //if (prefix != null) //Log.LogInfo("Unpatched all usages prefix of " + name);
    if (postfix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), postfix.PatchMethod);
    //if (postfix != null) //Log.LogInfo("Unpatched all usages postfix of " + name);
  }

  // Removes most behavior and should help with performance.
  private static void UnpatchServerSync(Assembly assembly, Harmony harmony)
  {
    try
    {
      // This patch would never work on older mods.
      UnpatchAll(harmony, assembly, typeof(FejdStartup), nameof(FejdStartup.ShowConnectError));
      var type = SubClass(assembly, "RegisterRPCPatch");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.Awake));
      type = SubClass(assembly, "RegisterClientRPCPatch");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.OnNewConnection));
      type = SubClass(assembly, "SnatchCurrentlyHandlingRPC");
      Unpatch(harmony, type, typeof(ZRpc), nameof(ZRpc.HandlePackage));
      type = SubClass(assembly, "ResetConfigsOnShutdown");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.Shutdown));
      type = SubClass(assembly, "SendConfigsAfterLogin");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.RPC_PeerInfo));
      type = SubClass(assembly, "PreventSavingServerInfo");
      Unpatch(harmony, type, typeof(ConfigEntryBase), nameof(ConfigEntryBase.GetSerializedValue));
      type = SubClass(assembly, "PreventConfigRereadChangingValues");
      Unpatch(harmony, type, typeof(ConfigEntryBase), nameof(ConfigEntryBase.SetSerializedValue));
      type = VersionCheck(assembly);
      Unpatch(harmony, type, typeof(FejdStartup), nameof(FejdStartup.ShowConnectError));
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.RPC_PeerInfo));
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.OnNewConnection));
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.Disconnect));
      //Log.LogInfo($"Unpatched server sync.");
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to unpatch server sync.");
    }
  }
  private static bool Cancel(ref long target)
  {
    //Log.LogInfo($"Canceled broadcast.");
    target = 0;
    return false;
  }
  private static bool Cancel() => false;
  private static ServerSync.ConfigSync CopySync(Assembly assembly, object oldSync)
  {
    var type = ConfigSync(assembly);
    var name = (string)Field(type, oldSync, "Name");
    var displayName = (string?)Field(type, oldSync, "DisplayName");
    var currentVersion = (string?)Field(type, oldSync, "CurrentVersion");
    var minimumRequiredVersion = (string?)Field(type, oldSync, "MinimumRequiredVersion");
    var modRequired = false;
    try
    {
      modRequired = (bool)Field(type, oldSync, "ModRequired");
    }
    catch { }
    return new ServerSync.ConfigSync(name)
    {
      CurrentVersion = currentVersion,
      DisplayName = displayName,
      MinimumRequiredVersion = minimumRequiredVersion,
      ModRequired = modRequired,
    };
  }
  private static void CopyForceLock(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync)
  {
    try
    {
      var forceConfigLocking = (bool?)Field(assembly, oldSync, "forceConfigLocking");
      if (forceConfigLocking.HasValue) newSync.IsLocked = forceConfigLocking.Value;
      //Log.LogInfo($"Force config copied.");
    }
    catch
    {
      //Log.LogInfo($"Force config doesn't exist.");
    }
  }
  private static void CopyLockConfig(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync)
  {
    try
    {
      var lockConfig = Field(assembly, oldSync, "lockedConfig");
      if (lockConfig != null)
      {
        var newType = ConfigSync(Assembly.GetExecutingAssembly());
        var oldSettingType = assembly.GetType("ServerSync.OwnConfigEntryBase");
        var configEntryBase = AccessTools.PropertyGetter(oldSettingType, "BaseConfig").Invoke(lockConfig, new object[0]);
        var itemType = ItemType(configEntryBase);
        Call(newType, itemType, "AddLockingConfigEntry", newSync, configEntryBase);
      }
      //Log.LogInfo("Lock config copied.");
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to copy lock config.");
    }
  }
  private static void CopySettings(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync)
  {
    try
    {
      var newType = ConfigSync(Assembly.GetExecutingAssembly());
      var oldSettingType = assembly.GetType("ServerSync.OwnConfigEntryBase");
      var settings = (IEnumerable)AccessTools.Field(ConfigSync(assembly), "allConfigs").GetValue(oldSync);
      foreach (var setting in settings)
      {
        var configEntryBase = AccessTools.PropertyGetter(oldSettingType, "BaseConfig").Invoke(setting, new object[0]);
        var itemType = ItemType(configEntryBase);
        Call(newType, itemType, "AddConfigEntry", newSync, configEntryBase);
      }
      //Log.LogInfo("Settings copied.");
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to copy settings.");
    }
  }
  private static void CopyValues(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync)
  {
    try
    {
      var oldType = ConfigSync(assembly);
      var oldValueType = assembly.GetType("ServerSync.CustomSyncedValueBase");
      var values = (IEnumerable)Field(assembly, oldSync, "allCustomValues");
      foreach (var value in values)
      {
        var valueChanged = Event(oldValueType, value, "ValueChanged");
        var identifier = (string)Field(oldValueType, value, "Identifier");
        var boxedValue = (object?)Field(oldValueType, value, "boxedValue");
        var itemType = ItemType(value);
        var constructorType = typeof(ServerSync.CustomSyncedValue<>).MakeGenericType(itemType);
        var constructor = constructorType.GetConstructor(new Type[] { typeof(ServerSync.ConfigSync), typeof(string), itemType });
        var obj = (ServerSync.CustomSyncedValueBase)constructor.Invoke(new object[] { newSync, identifier, boxedValue! });
        if (valueChanged != null)
        {
          valueChanged.AddEventHandler(value, () =>
          {
            var newValue = (object?)Field(oldValueType, value, "boxedValue");
            //Log.LogInfo($"Old sync changed to {newValue ?? "null"} while current is {obj.BoxedValue ?? "null"}.");
            if (obj.BoxedValue != newValue)
              obj.BoxedValue = newValue;
          });
        }
        obj.ValueChanged += () =>
        {
          var currentValue = (object?)Field(oldValueType, value, "boxedValue");
          //Log.LogInfo($"New sync changed to {obj.BoxedValue ?? "null"} while old is {currentValue ?? "null"}.");
          if (currentValue != obj.BoxedValue)
            AccessTools.PropertySetter(oldValueType, "BoxedValue").Invoke(value, new object[] { obj.BoxedValue! });
        };
      }
      //Log.LogInfo("Values copied.");
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Failed to copy values.");
    }
  }
  private static void ClearConfigSyncs(Assembly assembly)
  {
    try
    {
      var configSyncs = Field(ConfigSync(assembly), "configSyncs");
      Clear(configSyncs);
      //Log.LogInfo("Config sync cleared.");
    }
    catch (Exception e)
    {
      Log.LogWarning(e.ToString());
      //Log.LogInfo("Config syncs not found.");
    }
  }
  private static void ClearVersionChecks(Assembly assembly)
  {
    try
    {
      var versionChecks = Field(VersionCheck(assembly), "versionChecks");
      Clear(versionChecks);
      //Log.LogInfo("Version check cleared.");
    }
    catch
    {
      //Log.LogInfo("Version check not supported. Nothing to clear.");
    }
  }
  private static void Clear(object obj) => obj.GetType().GetMethod("Clear").Invoke(obj, new object[0]);
  private static Type SubClass(Assembly assembly, string name) => assembly.GetType($"ServerSync.ConfigSync+{name}");
  private static Type CustomSyncedValueBase(Assembly assembly) => assembly.GetType("ServerSync.CustomSyncedValueBase");
  private static Type ConfigSync(Assembly assembly) => assembly.GetType("ServerSync.ConfigSync");
  private static Type VersionCheck(Assembly assembly) => assembly.GetType("ServerSync.VersionCheck");
  private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
  private static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
  private static object Field(Assembly assembly, object sync, string name) => Field(ConfigSync(assembly), sync, name);
  private static object Field(Type type, object obj, string name) => type.GetField(name, Instance).GetValue(obj);
  private static EventInfo Event(Type type, object obj, string name) => type.GetEvent(name, Instance);
  private static object Field(Assembly assembly, string name) => Field(ConfigSync(assembly), name);
  private static object Field(Type type, string name) => type.GetField(name, Static).GetValue(null);
  private static Type ItemType(object obj) => obj.GetType().GetGenericArguments()[0];
  private static void Call(Type type, Type itemType, string name, object obj, object value) => type.GetMethod(name).MakeGenericMethod(itemType).Invoke(obj, [value]);
}