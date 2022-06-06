using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace PortalEnabler;

public class Patches
{
    public static long _logIntevervalSeconds { get; set; }
    public static ConfigEntry<string>? _portalPrefabName;
    public static ConfigEntry<string>? _onewayPortalTagPrefix;
    public static ConfigEntry<float>? _connectPortalCoroutineWait;


    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Awake))]
    public static class TeleportWorldPrefix
    {
        public static bool Prefix(TeleportWorld __instance)
        {
            __instance.m_nview = __instance.GetComponent<ZNetView>();

            if (__instance.m_nview.GetZDO() == null)
            {
                __instance.enabled = false;
                return true;
            }

            __instance.m_hadTarget = __instance.HaveTarget();

            if (!__instance.m_proximityRoot)
            {
                __instance.m_proximityRoot = __instance.transform;
            }

            if (__instance.m_target_found == null)
            {
                GameObject targetFoundObject = __instance.gameObject.transform.Find("_target_found").gameObject;

                targetFoundObject.SetActive(false);
                __instance.m_target_found = targetFoundObject.AddComponent<EffectFade>();
                targetFoundObject.SetActive(true);
            }

            __instance.m_nview.Register<string>("SetTag", new Action<long, string>(__instance.RPC_SetTag));
            __instance.InvokeRepeating("UpdatePortal", 0.5f, 0.5f);

            return false;
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.Start))]
    public static class GameStartPostfix
    {
        private static IEnumerator? coroutine;

        public static void Postfix(Game __instance)
        {
            if (!ZNet.instance.IsServer()) {
                return;
            }

            ZDOMan zdoMan = ZDOMan.instance;
            long logTimestamp;
            long lastLogTimestamp = 0;

            IEnumerator? ConnectPorts()
            {
                while (true)
                {
                    logTimestamp = DateTimeOffset.Now.ToUnixTimeSeconds();
                    bool shouldlog = (logTimestamp - lastLogTimestamp) > _logIntevervalSeconds;

                    __instance.m_tempPortalList.Clear();

                    int index = 0;
                    bool getPrefabsComplete = false;

                    if (_portalPrefabName != null)
                    {
                        HashSet<int> prefabHashCodes = new HashSet<int>
                        {
                            _portalPrefabName.Value.GetStableHashCode()
                        };

                        do
                        {
                            getPrefabsComplete = GetAllZdosMatchingPrefabHashcodes(zdoMan, prefabHashCodes,
                                __instance.m_tempPortalList, ref index);
                            yield return null;
                        } while (!getPrefabsComplete);
                    }

                    foreach (ZDO zdo in __instance.m_tempPortalList)
                    {
                        ZDOID targetZdoid = zdo.GetZDOID("target");
                        if (targetZdoid.IsNone())
                        {
                            continue;
                        }

                        string @tag = zdo.GetString("tag", string.Empty);
                        ZDO targetZdo = zdoMan.GetZDO(targetZdoid);
                        if (tag == String.Empty || targetZdo == null ||
                            (targetZdo.GetString("tag", String.Empty) != tag &&
                             !tag.StartsWith(_onewayPortalTagPrefix?.Value)))
                        {
                            zdo.SetOwner(zdoMan.GetMyID());
                            zdo.Set("target", ZDOID.None);
                            zdoMan.ForceSendZDO(zdo.m_uid);
                        }
                    }

                    foreach (ZDO zdo in __instance.m_tempPortalList)
                    {
                        string @tag = zdo.GetString("tag", string.Empty);

                        if (tag == string.Empty || !zdo.GetZDOID("target").IsNone())
                        {
                            continue;
                        }

                        // If tag starts with oneway-prefix, look for matching portal that has tag without the prefix. 
                        bool isOneWayPortal = tag.StartsWith(_onewayPortalTagPrefix?.Value);
                        if (_onewayPortalTagPrefix != null)
                        {
                            ZDO targetZdo = __instance.FindRandomUnconnectedPortal(
                                __instance.m_tempPortalList, zdo,
                                isOneWayPortal ? tag.Remove(0, _onewayPortalTagPrefix.Value.Length) : tag);

                            if (targetZdo != null)
                            {
                                zdo.SetOwner(zdoMan.GetMyID());
                                zdo.Set("target", targetZdo.m_uid);

                                // Only connect target if we are not a one-way portal.
                                targetZdo.SetOwner(zdoMan.GetMyID());
                                targetZdo.Set("target", isOneWayPortal ? ZDOID.None : zdo.m_uid);

                                zdoMan.ForceSendZDO(zdo.m_uid);
                                zdoMan.ForceSendZDO(targetZdo.m_uid);
                            }
                        }
                    }

                    if (_connectPortalCoroutineWait != null)
                        yield return new WaitForSeconds(_connectPortalCoroutineWait.Value);
                }
            }

            coroutine = ConnectPorts();

            __instance.StartCoroutine(coroutine);
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "Interact")]
    private class Interact_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> source = new List<CodeInstruction>(instructions);

            for (int index = 0; index < source.Count; ++index)
            {
                if (source[index].opcode == OpCodes.Ldc_I4_S)
                    source[index].operand = (object)(int)sbyte.MaxValue;
            }

            return source.AsEnumerable<CodeInstruction>();
        }
    }

    private static bool GetAllZdosMatchingPrefabHashcodes(
        ZDOMan zdoMan, HashSet<int> prefabHashcodes, List<ZDO> matchingZdos, ref int index)
    {
        if (index >= zdoMan.m_objectsBySector.Length)
        {
            foreach (var outsideZdos in zdoMan.m_objectsByOutsideSector.Values)
            {
                matchingZdos.AddRange(outsideZdos.Where(zdo =>
                    zdo.IsValid() && prefabHashcodes.Contains(zdo.GetPrefab())));
            }

            return true;
        }

        int counted = 0;

        while (index < zdoMan.m_objectsBySector.Length)
        {
            var sectorZdos = zdoMan.m_objectsBySector[index];

            if (sectorZdos != null)
            {
                var zdos = sectorZdos.Where(zdo => prefabHashcodes.Contains(zdo.GetPrefab()));
                matchingZdos.AddRange(zdos);
                counted += zdos.Count();
            }

            index++;

            if (counted > 500)
            {
                break;
            }
        }

        return false;
    }
}