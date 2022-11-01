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
public class ServerSyncFix : BaseUnityPlugin {
  const string GUID = "server_sync_fix";
  const string NAME = "Server Sync Fix";
  const string VERSION = "1.0";

#nullable disable
  public static ManualLogSource Log;
#nullable enable
  public void Awake() {
    Log = Logger;
    new Harmony(GUID).PatchAll();
  }

  [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Awake)), HarmonyPostfix]
  private static void DoPatch() {
    Harmony harmony = new(GUID);
    foreach (var info in Chainloader.PluginInfos.Values) {
      var assembly = info.Instance.GetType().Assembly;
      if (!IsOutdated(assembly)) continue;
      Log.LogWarning($"Mod {info.Metadata.GUID} has outdated server sync.");
      PreventSending(assembly, harmony);
      PreventReceiving(assembly, harmony);
      var syncType = ConfigSync(assembly);
      var syncs = (IEnumerable)Field(assembly, "configSyncs");
      foreach (var oldSync in syncs) {
        var newSync = CopySync(assembly, oldSync);
        CopyForceLock(assembly, oldSync, newSync);
        CopySettings(assembly, oldSync, newSync);
        CopyValues(assembly, oldSync, newSync);
        CopyLockConfig(assembly, oldSync, newSync);
        Log.LogWarning($"Updated server sync for {newSync.Name}.");
      }
      ClearConfigSyncs(assembly);
      ClearVersionChecks(assembly);
    }
  }
  private static bool IsOutdated(Assembly assembly) {
    var type = SubClass(assembly, "SendConfigsAfterLogin+BufferingSocket");
    if (type == null) return false;
    try {
      type.GetMethod("VersionMatch");
      return false;
    } catch { }
    return true;
  }
  private static void PreventSending(Assembly assembly, Harmony harmony) {
    try {
      var methods = ConfigSync(assembly).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance).Where(method => method.Name == "Broadcast");
      foreach (var method in methods) {
        var mPrefix = SymbolExtensions.GetMethodInfo((long target) => Cancel(ref target));
        harmony.Patch(method, new(mPrefix));
      }
      Log.LogDebug("Blocked sending.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Failed to block sending.");
    }
  }
  private static void Unpatch(Harmony harmony, Type type, Type classType, string name) {
    var prefix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Prefixes.FirstOrDefault(p => p.PatchMethod.DeclaringType == type);
    var postfix = PatchProcessor.GetPatchInfo(AccessTools.DeclaredMethod(classType, name)).Postfixes.FirstOrDefault(p => p.PatchMethod.DeclaringType == type);
    if (prefix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), prefix.PatchMethod);
    if (postfix != null) harmony.Unpatch(AccessTools.DeclaredMethod(classType, name), postfix.PatchMethod);
  }

  private static void PreventReceiving(Assembly assembly, Harmony harmony) {
    try {
      var type = SubClass(assembly, "RegisterRPCPatch");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.Awake));
      type = SubClass(assembly, "RegisterClientRPCPatch");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.OnNewConnection));
      type = SubClass(assembly, "SnatchCurrentlyHandlingRPC");
      Unpatch(harmony, type, typeof(ZRpc), nameof(ZRpc.HandlePackage));
      type = SubClass(assembly, "ShowConnectionError");
      Unpatch(harmony, type, typeof(FejdStartup), nameof(FejdStartup.ShowConnectError));
      type = SubClass(assembly, "ResetConfigsOnShutdown");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.Shutdown));
      type = SubClass(assembly, "SendConfigsAfterLogin");
      Unpatch(harmony, type, typeof(ZNet), nameof(ZNet.RPC_PeerInfo));
      type = SubClass(assembly, "PreventSavingServerInfo");
      Unpatch(harmony, type, typeof(ConfigEntryBase), nameof(ConfigEntryBase.GetSerializedValue));
      type = SubClass(assembly, "PreventConfigRereadChangingValues");
      Unpatch(harmony, type, typeof(ConfigEntryBase), nameof(ConfigEntryBase.SetSerializedValue));
      Log.LogDebug($"Unpatched server sync.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Failed to unpatch server sync.");
    }
  }
  private static bool Cancel(ref long target) {
    target = 0;
    return false;
  }
  private static ServerSync.ConfigSync CopySync(Assembly assembly, object oldSync) {
    var type = ConfigSync(assembly);
    var name = (string)Field(type, oldSync, "Name");
    var displayName = (string?)Field(type, oldSync, "DisplayName");
    var currentVersion = (string?)Field(type, oldSync, "CurrentVersion");
    var minimumRequiredVersion = (string?)Field(type, oldSync, "MinimumRequiredVersion");
    var modRequired = false;
    try {
      modRequired = (bool)Field(type, oldSync, "ModRequired");
    } catch { }
    return new ServerSync.ConfigSync(name)
    {
      CurrentVersion = currentVersion,
      DisplayName = displayName,
      MinimumRequiredVersion = minimumRequiredVersion,
      ModRequired = modRequired,
    };
  }
  private static void CopyForceLock(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync) {
    try {
      var forceConfigLocking = (bool?)Field(assembly, oldSync, "forceConfigLocking");
      if (forceConfigLocking.HasValue) newSync.IsLocked = forceConfigLocking.Value;
      Log.LogDebug($"Force config copied.");
    } catch {
      Log.LogDebug($"Force config doesn't exist.");
    }
  }
  private static void CopyLockConfig(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync) {
    try {
      var lockConfig = Field(assembly, oldSync, "lockedConfig");
      if (lockConfig != null) {
        var newType = ConfigSync(Assembly.GetExecutingAssembly());
        var oldSettingType = assembly.GetType("ServerSync.OwnConfigEntryBase");
        var configEntryBase = AccessTools.PropertyGetter(oldSettingType, "BaseConfig").Invoke(lockConfig, new object[0]);
        var itemType = ItemType(configEntryBase);
        Call(newType, itemType, "AddLockingConfigEntry", newSync, configEntryBase);
      }
      Log.LogDebug("Lock config copied.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Failed to copy lock config.");
    }
  }
  private static void CopySettings(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync) {
    try {
      var newType = ConfigSync(Assembly.GetExecutingAssembly());
      var oldSettingType = assembly.GetType("ServerSync.OwnConfigEntryBase");
      var settings = (IEnumerable)AccessTools.Field(ConfigSync(assembly), "allConfigs").GetValue(oldSync);
      foreach (var setting in settings) {
        var configEntryBase = AccessTools.PropertyGetter(oldSettingType, "BaseConfig").Invoke(setting, new object[0]);
        var itemType = ItemType(configEntryBase);
        Call(newType, itemType, "AddConfigEntry", newSync, configEntryBase);
      }
      Log.LogDebug("Settings copied.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Failed to copy settings.");
    }
  }
  private static void CopyValues(Assembly assembly, object oldSync, ServerSync.ConfigSync newSync) {
    try {
      var oldType = ConfigSync(assembly);
      var oldValueType = assembly.GetType("ServerSync.CustomSyncedValueBase");
      var values = (IEnumerable)Field(assembly, oldSync, "allCustomValues");
      foreach (var value in values) {
        var valueChanged = Event(oldValueType, value, "ValueChanged");
        var identifier = (string)Field(oldValueType, value, "Identifier");
        var boxedValue = (object?)Field(oldValueType, value, "boxedValue");
        var itemType = ItemType(value);
        var constructorType = typeof(ServerSync.CustomSyncedValue<>).MakeGenericType(itemType);
        var constructor = constructorType.GetConstructor(new Type[] { typeof(ServerSync.ConfigSync), typeof(string), itemType });
        var obj = (ServerSync.CustomSyncedValueBase)constructor.Invoke(new object[] { newSync, identifier, boxedValue! });
        if (valueChanged != null) {
          valueChanged.AddEventHandler(value, () => {
            var newValue = (object?)Field(oldValueType, value, "boxedValue");
            Log.LogDebug($"Old sync changed to {newValue ?? "null"} while current is {obj.BoxedValue ?? "null"}.");
            if (obj.BoxedValue != newValue)
              obj.BoxedValue = newValue;
          });
        }
        obj.ValueChanged += () => {
          var currentValue = (object?)Field(oldValueType, value, "boxedValue");
          Log.LogDebug($"New sync changed to {obj.BoxedValue ?? "null"} while old is {currentValue ?? "null"}.");
          if (currentValue != obj.BoxedValue)
            AccessTools.PropertySetter(oldValueType, "BoxedValue").Invoke(value, new object[] { obj.BoxedValue! });
        };
      }
      Log.LogDebug("Values copied.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Failed to copy values.");
    }
  }
  private static void ClearConfigSyncs(Assembly assembly) {
    try {
      var configSyncs = Field(ConfigSync(assembly), "configSyncs");
      Clear(configSyncs);
      Log.LogDebug("Config sync cleared.");
    } catch (Exception e) {
      Log.LogWarning(e.ToString());
      Log.LogDebug("Config syncs not found.");
    }
  }
  private static void ClearVersionChecks(Assembly assembly) {
    try {
      var versionChecks = Field(VersionCheck(assembly), "versionChecks");
      Clear(versionChecks);
      Log.LogDebug("Version check cleared.");
    } catch {
      Log.LogDebug("Version check not supported. Nothing to clear.");
    }
  }
  private static void Clear(object obj) => obj.GetType().GetMethod("Clear").Invoke(obj, new object[0]);
  private static Type SubClass(Assembly assembly, string name) => assembly.GetType($"ServerSync.ConfigSync+{name}");
  private static Type ConfigSync(Assembly assembly) => assembly.GetType("ServerSync.ConfigSync");
  private static Type VersionCheck(Assembly assembly) => assembly.GetType("ServerSync.VersionCheck");
  private static BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
  private static BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
  private static object Field(Assembly assembly, object sync, string name) => Field(ConfigSync(assembly), sync, name);
  private static object Field(Type type, object obj, string name) => type.GetField(name, Instance).GetValue(obj);
  private static EventInfo Event(Type type, object obj, string name) => type.GetEvent(name, Instance);
  private static object Field(Assembly assembly, string name) => Field(ConfigSync(assembly), name);
  private static object Field(Type type, string name) => type.GetField(name, Static).GetValue(null);
  private static Type ItemType(object obj) => obj.GetType().GetGenericArguments()[0];
  private static void Call(Type type, Type itemType, string name, object obj, object value) => type.GetMethod(name).MakeGenericMethod(itemType).Invoke(obj, new object[] { value });
}