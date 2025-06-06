using RootMotion.FinalIK;
using System.Collections.Generic;
using System.Reflection.Emit;
using System.Reflection;
using System.IO;
using System;
using HarmonyLib;
using Unity.XR.OpenVR;
using UnityEngine;
using ValheimVRMod.Scripts;
using ValheimVRMod.Utilities;
using ValheimVRMod.VRCore;
using Valve.VR;

using static ValheimVRMod.Utilities.LogUtils;
using System.Threading;

namespace ValheimVRMod.Patches
{

    // STEAM ONLY
    // Ensure there are no problems parsing the environment variable. One user had some issue
    // where the environment variable had an invalid format, causing the steam app id to not load
    // properly. Normally without the mod installed, the game uses steam_appid.txt, but SteamVR
    // is setting the environment variable. This patch just does some additional error checking
    // instead and falls back to the steam_appid.txt file if there is a problem with the
    // environment variable.
    class SteamManager_LoadAppId_Patch
    {
        public static bool Prefix(ref uint __result)
        {
            string environmentVariable = Environment.GetEnvironmentVariable("SteamAppId");
            if (environmentVariable != null)
            {
                ZLog.Log(string.Concat("Using environment steamid ", environmentVariable));
                try
                {
                    __result = uint.Parse(environmentVariable);
                    return false;
                } catch
                {
                    LogError("Error parsing 'SteamAppId' environment variable. Using steam_appid.txt instead.");
                }
            }
            try
            {
                string str = File.ReadAllText("steam_appid.txt");
                ZLog.Log("Using steam_appid.txt");
                __result = uint.Parse(str);
            }
            catch
            {
                ZLog.LogWarning("Failed to find APPID");
                __result = (uint)0;
            }
            return false;
        }
    }

    // This patch just forces IsUsingSteamVRInput to always return true.
    // Without this, there seems to be some problem with how the DLL namespaces
    // are loaded when using certain other mods. Normally this method will result
    // in a call to the Assembly.GetTypes method, but for whatever reason, this
    // ends up throwing an exception and crashes the mod. The mod I know that causes
    // this conflict is ConfigurationManager, which is installed by default for
    // anyone using Vortex mod installer, but it likely will happen for others too.
    [HarmonyPatch(typeof(OpenVRHelpers), nameof(OpenVRHelpers.IsUsingSteamVRInput))]
    class OpenVRHelpers_IsUsingSteamVRInput_Patch
    {
        public static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }

    // This method is responsible for pausing the game when the
    // steam dashboard is shown as well as sets render scale
    // to half while the dashboard is showing. Both of these
    // break the game - pausing ends up freezing most of the
    // game functions and the render scale doesn't get restored
    // properly. So this patch skips the function. Dashboard still
    // is functional.
    [HarmonyPatch(typeof(SteamVR_Render), "OnInputFocus")]
    class SteamVR_Render_OnInputFocus_Patch
    {
        public static bool Prefix()
        {
            if (VHVRConfig.NonVrPlayer())
            {
                return true;
            }
            return false;
        }
    }

    // Disables camera culling for all materials currently loaded (trees, grass, etc.)
    [HarmonyPatch(typeof(Game), "Awake")]
    class Game_Awake_Patches
    {
        private static void Postfix()
        {
            if(!VHVRConfig.NonVrPlayer())
            {
                foreach(Material material in Resources.FindObjectsOfTypeAll<Material>())
                    material.SetInt("_CamCull", 0);
            }
        }
    }

    // This method updates FOV on a few cameras, but this
    // just throws an error while in VR and spams the logfile
    [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
    class GameCamera_UpdateCamera_Patch
    {

        private static MethodInfo fovMethod =
            AccessTools.Method(typeof(Camera), "set_fieldOfView");

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = new List<CodeInstruction>(instructions);
            if (VHVRConfig.NonVrPlayer())
            {
                return original;
            }
            for (int i = 0; i < original.Count; i++)
            {
                if (i + 4 < original.Count)
                {
                    // check if 4 instructions ahead is set_fieldOfView
                    var instruction = original[i + 4];
                    if (instruction.Calls(fovMethod))
                    {
                        // Replace these five instructions with NOPs
                        original[i].opcode = OpCodes.Nop;
                        original[i + 1].opcode = OpCodes.Nop;
                        original[i + 2].opcode = OpCodes.Nop;
                        original[i + 3].opcode = OpCodes.Nop;
                        original[i + 4].opcode = OpCodes.Nop;
                    }
                }
            }
            return original;
        }
    }

    // Removes calls to set the targetFrameRate of the game
    [HarmonyPatch(typeof(Game), nameof(Game.Update))]
    [HarmonyPatch(typeof(FejdStartup), nameof(FejdStartup.Update))]
    class Application_FrameRate_Patch
    {
        private static void Nop(int ignore) { }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (VHVRConfig.NonVrPlayer()) return instructions;

            var original = new List<CodeInstruction>(instructions);
            for (int i = 0; i < original.Count; i++)
            {
                if (original[i].Calls(AccessTools.Method(typeof(Application), "set_targetFrameRate", new[] { typeof(Int32) })))
                {
                    var changed = CodeInstruction.Call(typeof(Application_FrameRate_Patch), nameof(Nop), new[] { typeof(Int32) });
                    changed.labels = original[i].labels;
                    original[i] = changed;
                }
            }
            return original;
        }
    }

    // Prevents the character from spinning while paused
    [HarmonyPatch(typeof(Game), nameof(Game.UpdatePause))]
    class Prevent_Pause_Character_Spin_Patch
    {

        private static MethodInfo rotateMethod = AccessTools.Method(typeof(Transform), nameof(Transform.Rotate), new Type[] { typeof(Vector3), typeof(float) });
        private static MethodInfo setLookDirMethod = AccessTools.Method(typeof(Character), nameof(Player.SetLookDir));

        private static void FakeRotate(Transform t, Vector3 axis, float angle) {}

        private static void FakeSetLookDir(Character c, Vector3 v, float f) {}

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = new List<CodeInstruction>(instructions);
            if (VHVRConfig.NonVrPlayer())
            {
                return original;
            }
            var patched = new List<CodeInstruction>();
            for (int i = 0; i < original.Count; i++)
            {
                var instruction = original[i];
                if (i == 0)
                {
                    patched.Add(instruction);
                    continue;
                }
                if (instruction.Calls(rotateMethod) && original[i - 1].opcode == OpCodes.Mul)
                {
                    patched.Add(CodeInstruction.Call(typeof(Prevent_Pause_Character_Spin_Patch), nameof(Prevent_Pause_Character_Spin_Patch.FakeRotate)));
                }
                else if (instruction.Calls(setLookDirMethod))
                {
                    patched.Add(CodeInstruction.Call(typeof(Prevent_Pause_Character_Spin_Patch), nameof(Prevent_Pause_Character_Spin_Patch.FakeSetLookDir)));
                }
                else
                {
                    patched.Add(instruction);
                }
            }
            return patched;
        }
    }

    [HarmonyPatch(typeof(DistantFogEmitter), "PlaceOne")]
    class PatchFogEmitter
    {
        public static bool Prefix(DistantFogEmitter __instance)
        {
            if (VHVRConfig.NonVrPlayer() || !__instance)
            {
                return true;
            }

            Vector3 a;
            if (__instance.GetRandomPoint(__instance.transform.position, out a))
            {
                ParticleSystem.EmitParams emitParams = default(ParticleSystem.EmitParams);
                emitParams.position = a + Vector3.up * __instance.m_placeOffset;
                var num = UnityEngine.Random.Range(0, __instance.m_psystems.Length);
                __instance.m_psystems[num].Emit(emitParams, 1);
                var rend = __instance.m_psystems[num].GetComponent<ParticleSystemRenderer>();
                rend.allowRoll = false;
                rend.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            }
            return false;
        }
    }

    [HarmonyPatch(typeof(ParticleMist),nameof(ParticleMist.Awake))]
    class Patch_ParticleMist
    {
        public static void Postfix(ParticleMist __instance)
        {
            if (VHVRConfig.NonVrPlayer() || !__instance)
            {
                return;
            }
            var rend = __instance.m_ps.GetComponent<ParticleSystemRenderer>();
            rend.allowRoll = false;
            rend.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        }
    }


    // If the Overlay GUI is used and the Main Menu is brought up, when the game pauses, the GUI stops rendering
    // because the timeScale is set to 0. Not having a better workaround currently, I will force the game to not pause
    // and print a message to inform the user why that's happening.
    [HarmonyPatch(typeof(Game), nameof(Game.Pause))]
    class NoPauseWithOverlayGuiPatch
    {
        public static bool Prefix()
        {
            if (VHVRConfig.NonVrPlayer() || VHVRConfig.UseVrControls() || !VHVRConfig.GetUseOverlayGui())
            {
                return true;
            }
            LogUtils.LogInfo("Game Pause disabled - to enable Pausing set UseOverlayGui to false or UseVrControls to true. This is due to a conflict with pausing while the overlay is active.");
            Game.m_pause = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Character), nameof(Character.Start))]
    class CharacterSetLodGroupSize
    {
        public static void Postfix(Character __instance)
        {
            if (VHVRConfig.NonVrPlayer() || __instance.IsPlayer() || !__instance.m_lodGroup || VRPlayer.vrCam == null)
            {
                return;
            }

            __instance.gameObject.AddComponent<LodGroupUpdater>();
        }

        private class LodGroupUpdater : MonoBehaviour
        {
            private Character character;
            private bool isTamed;
            private float originalLodGroupSize;
            private float desiredLogGroupSize;

            void Awake()
            {
                character = GetComponent<Character>();
                isTamed = character.m_tamed;
                originalLodGroupSize = character.m_lodGroup.size;

                float cullingHeight = GetCullingHeight(character.m_lodGroup);
                Vector3 scale = character.m_lodGroup.transform.lossyScale;
                float dimension = Math.Min(Math.Min(scale.x, scale.y), scale.z);

                desiredLogGroupSize =
                    Mathf.Max(
                        originalLodGroupSize,
                        VRPlayer.vrCam.fieldOfView * Mathf.PI / 180 * cullingHeight * VHVRConfig.GetEnemyRenderDistanceValue() / dimension);
            }

            // Supposedly only update on Start, but somehow it doesnt work on some mobs (eg. fuling/goblin), so its using Update() for now
            void Update()
            {
                bool wasTamed = isTamed;
                isTamed = character.m_tamed;
                if (isTamed)
                {
                    if (!wasTamed)
                    {
                        character.m_lodGroup.size = originalLodGroupSize;
                    }
                    return;
                }

                if (character.m_lodGroup.size < desiredLogGroupSize)
                {
                    character.m_lodGroup.size = desiredLogGroupSize;
                }
            }

            private float GetCullingHeight(LODGroup lodGroup)
            {
                LOD[] lods = lodGroup.GetLODs();
                float cullingHeight = 1;
                foreach (LOD lOD in lods)
                {
                    if (lOD.screenRelativeTransitionHeight < cullingHeight)
                    {
                        cullingHeight = lOD.screenRelativeTransitionHeight;
                    }
                }
                return cullingHeight;
            }
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.Awake))]
    class PieceSetLodGroupSizePatch
    {
        public static void Postfix(Piece __instance)
        {
            if (!VHVRConfig.shouldModifyPieceLodGroup)
            {
                return;
            }

            LODGroup lodGroup = __instance.GetComponent<LODGroup>();

            if (lodGroup == null || lodGroup.lodCount < 2)
            {
                return;
            }

            var lods = lodGroup.GetLODs();

            if (lods[0].screenRelativeTransitionHeight <= lods[1].screenRelativeTransitionHeight)
            {
                return;
            }

            lods[0].screenRelativeTransitionHeight = lods[0].screenRelativeTransitionHeight * VHVRConfig.GetBuildingPieceDetailReductionFactor();

            lodGroup.SetLODs(lods);
        }
    }

    // These patches just remove a warning log that was very spammy under some circumstances
    // and resulted in poor performance. (Something to do with spiky fish...)
    [HarmonyPatch(typeof(Aoe), nameof(Aoe.OnCollisionEnter))]
    class AoeOnCollisionEnterPatch
    {
        public static bool Prefix(Aoe __instance, ref Collision collision)
        {
           
            if (!__instance.m_triggerEnterOnly)
            {
                return false;
            }
            if (!__instance.m_useTriggers)
            {
                return false;
            }
            if (__instance.m_nview != null && (!__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()))
            {
                return false;
            }
            __instance.OnHit(collision.collider, collision.collider.transform.position);
            return false;
        }
    }

    [HarmonyPatch(typeof(Aoe), nameof(Aoe.OnCollisionStay))]
    class AoeOnCollisionStayPatch
    {
        public static bool Prefix(Aoe __instance, ref Collision collision)
        {
            if (__instance.m_triggerEnterOnly)
            {
                return false;
            }
            if (!__instance.m_useTriggers)
            {
                return false;
            }
            if (__instance.m_nview != null && (!__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()))
            {
                return false;
            }
            __instance.OnHit(collision.collider, collision.collider.transform.position);
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    class PlayerOnDeathPatch
    {
        public static bool hasCharacterDied { get; private set; } = false;
        public static void Prefix(Player __instance)
        {
            if (__instance != Player.m_localPlayer)
            {
                VRPlayerSync sync = __instance.GetComponent<VRPlayerSync>();
                if (sync != null)
                {
                    sync.DestroyVrik();
                }
                return;
            }

            VRPlayer.DestroyVrik();
            StaticObjects.destroyQuickMenus();

            var followCamera = CameraUtils.getCamera(CameraUtils.FOLLOW_CAMERA);
            if (followCamera != null)
            {
                hasCharacterDied = true;
                // Disable the follow camera temporarily since it might interfere with the projection matrix of the main camera upon character death.
                followCamera.enabled = false;
                GameObject.Destroy(followCamera);
            }
        }
    }
}
