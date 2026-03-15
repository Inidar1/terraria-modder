using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.Localization;
using Terraria.UI;
using TerrariaModder.Core.Logging;

namespace TerrariaModder.Core.UI
{
    internal static class NativeModsMenuPatches
    {
        private const string ModsLabel = "Mods";

        private static readonly MethodInfo DrawMenuMethod = typeof(Main).GetMethod("DrawMenu", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo IngameOptionsDrawMethod = typeof(IngameOptions).GetMethod("Draw", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo OpenAchievementsMethod = typeof(IngameFancyUI).GetMethod("OpenAchievements", BindingFlags.Public | BindingFlags.Static);
        private static readonly MethodInfo InjectTitleMenuEntryMethod = typeof(NativeModsMenuPatches).GetMethod(nameof(InjectTitleMenuEntry), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo InjectIngameMenuEntryMethod = typeof(NativeModsMenuPatches).GetMethod(nameof(InjectIngameMenuEntry), BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly FieldInfo LangMenuField = typeof(Lang).GetField("menu", BindingFlags.Public | BindingFlags.Static);
        private static readonly FieldInfo SelectedMenuField = typeof(Main).GetField("selectedMenu", BindingFlags.NonPublic | BindingFlags.Instance);

        private static Harmony _harmony;
        private static ILogger _log;
        private static bool _applied;

        public static void Initialize(ILogger log)
        {
            _log = log;
            _harmony = new Harmony("com.terrariamodder.native-menu");
        }

        public static void Apply()
        {
            if (_applied) return;

            try
            {
                if (DrawMenuMethod != null)
                {
                    _harmony.Patch(DrawMenuMethod, transpiler: new HarmonyMethod(typeof(NativeModsMenuPatches).GetMethod(nameof(DrawMenu_Transpiler), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                if (IngameOptionsDrawMethod != null)
                {
                    _harmony.Patch(IngameOptionsDrawMethod, transpiler: new HarmonyMethod(typeof(NativeModsMenuPatches).GetMethod(nameof(IngameOptionsDraw_Transpiler), BindingFlags.NonPublic | BindingFlags.Static)));
                }

                _applied = true;
            }
            catch (Exception ex)
            {
                _log?.Error($"[NativeMods] Failed to patch native menu entry points: {ex}");
            }
        }

        private static IEnumerable<CodeInstruction> DrawMenu_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            PatchTitleMenuItemCount(codes);
            int insertIndex = FindTitleMenuInsertionIndex(codes, out object menuItemsLocal, out object menuIndexLocal);
            if (insertIndex < 0)
            {
                _log?.Error("[NativeMods] Failed to locate title menu insertion point in Main.DrawMenu.");
                return codes;
            }

            codes.InsertRange(insertIndex, new[]
            {
                new CodeInstruction(OpCodes.Ldloc, menuItemsLocal),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldloca, menuIndexLocal),
                new CodeInstruction(OpCodes.Call, InjectTitleMenuEntryMethod)
            });

            return codes;
        }

        private static IEnumerable<CodeInstruction> IngameOptionsDraw_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int insertIndex = FindIngameMenuInsertionIndex(codes, out object menuIndexLocal, out object anchorLocal, out object offsetLocal, out object clickLocal);
            if (insertIndex < 0)
            {
                _log?.Error("[NativeMods] Failed to locate in-game options insertion point in IngameOptions.Draw.");
                return codes;
            }

            codes.InsertRange(insertIndex, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Ldloca, menuIndexLocal),
                new CodeInstruction(OpCodes.Ldloc, anchorLocal),
                new CodeInstruction(OpCodes.Ldloc, offsetLocal),
                new CodeInstruction(OpCodes.Ldloc, clickLocal),
                new CodeInstruction(OpCodes.Call, InjectIngameMenuEntryMethod)
            });

            return codes;
        }

        private static int FindTitleMenuInsertionIndex(List<CodeInstruction> codes, out object menuItemsLocal, out object menuIndexLocal)
        {
            menuItemsLocal = null;
            menuIndexLocal = null;

            for (int i = 0; i < codes.Count - 8; i++)
            {
                if (!IsLdloc(codes[i]) ||
                    !IsLdloc(codes[i + 1]) ||
                    !LoadsField(codes[i + 2], LangMenuField) ||
                    !IsLdcI4(codes[i + 3], 14) ||
                    codes[i + 4].opcode != OpCodes.Ldelem_Ref ||
                    codes[i + 5].opcode != OpCodes.Callvirt ||
                    codes[i + 6].opcode != OpCodes.Stelem_Ref)
                {
                    continue;
                }

                object candidateItemsLocal = codes[i].operand;
                object candidateIndexLocal = codes[i + 1].operand;

                for (int j = i - 1; j >= Math.Max(0, i - 6); j--)
                {
                    if (j < 3)
                        continue;

                    if (!IsStloc(codes[j]))
                        continue;
                    if (!IsLdloc(codes[j - 3]) ||
                        !IsLdcI4(codes[j - 2], 1) ||
                        codes[j - 1].opcode != OpCodes.Add)
                    {
                        continue;
                    }

                    if (!Equals(codes[j].operand, candidateIndexLocal))
                        continue;

                    menuItemsLocal = candidateItemsLocal;
                    menuIndexLocal = candidateIndexLocal;
                    return i;
                }
            }

            return -1;
        }

        private static void PatchTitleMenuItemCount(List<CodeInstruction> codes)
        {
            for (int i = 0; i < codes.Count - 5; i++)
            {
                if (!IsLdcI4(codes[i], 220) ||
                    !IsStloc(codes[i + 1]) ||
                    !IsLdcI4(codes[i + 2], 7) ||
                    !IsStloc(codes[i + 3]) ||
                    !IsLdcI4(codes[i + 4], 52))
                {
                    continue;
                }

                codes[i + 2] = new CodeInstruction(OpCodes.Ldc_I4_8);
                return;
            }

            _log?.Error("[NativeMods] Failed to patch title menu item count in Main.DrawMenu.");
        }

        private static int FindIngameMenuInsertionIndex(List<CodeInstruction> codes, out object menuIndexLocal, out object anchorLocal, out object offsetLocal, out object clickLocal)
        {
            menuIndexLocal = null;
            anchorLocal = null;
            offsetLocal = null;
            clickLocal = null;

            for (int i = 0; i < codes.Count - 16; i++)
            {
                if (!Calls(codes[i], OpenAchievementsMethod))
                    continue;

                if (i < 3 || !IsLdloc(codes[i - 3]))
                    continue;

                for (int j = i + 1; j < Math.Min(i + 12, codes.Count - 12); j++)
                {
                    if (!IsLdloc(codes[j]) ||
                        !IsLdcI4(codes[j + 1], 1) ||
                        codes[j + 2].opcode != OpCodes.Add ||
                        !IsStloc(codes[j + 3]) ||
                        codes[j + 4].opcode != OpCodes.Ldarg_1 ||
                        !LoadsField(codes[j + 5], LangMenuField) ||
                        !IsLdcI4(codes[j + 6], 118) ||
                        !IsLdloc(codes[j + 9]) ||
                        !IsLdloc(codes[j + 10]) ||
                        !IsLdloc(codes[j + 11]))
                    {
                        continue;
                    }

                    menuIndexLocal = codes[j + 3].operand;
                    anchorLocal = codes[j + 10].operand;
                    offsetLocal = codes[j + 11].operand;
                    clickLocal = codes[i - 3].operand;
                    return j + 4;
                }
            }

            return -1;
        }

        private static void InjectTitleMenuEntry(string[] menuItems, Main instance, ref int menuIndex)
        {
            menuItems[menuIndex] = ModsLabel;
            if (GetSelectedMenu(instance) == menuIndex)
            {
                NativeModsMenu.OpenFromTitle();
            }

            menuIndex++;
        }

        private static int GetSelectedMenu(Main instance)
        {
            if (instance == null || SelectedMenuField == null)
                return -1;

            return (int)SelectedMenuField.GetValue(instance);
        }

        private static void InjectIngameMenuEntry(SpriteBatch spriteBatch, ref int menuIndex, Vector2 anchor, Vector2 offset, bool activateClick)
        {
            if (IngameOptions.DrawLeftSide(spriteBatch, ModsLabel, menuIndex, anchor, offset, IngameOptions.leftScale, 0.7f, 0.8f, 0.01f))
            {
                IngameOptions.leftHover = menuIndex;
                if (activateClick)
                {
                    IngameOptions.Close();
                    NativeModsMenu.OpenIngame();
                }
            }

            menuIndex++;
        }

        private static bool Calls(CodeInstruction instruction, MethodInfo method)
        {
            return method != null && instruction.Calls(method);
        }

        private static bool LoadsField(CodeInstruction instruction, FieldInfo field)
        {
            if (field == null)
                return false;

            return (instruction.opcode == OpCodes.Ldsfld || instruction.opcode == OpCodes.Ldfld) && Equals(instruction.operand, field);
        }

        private static bool IsLdloc(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Ldloc ||
                   instruction.opcode == OpCodes.Ldloc_S ||
                   instruction.opcode == OpCodes.Ldloc_0 ||
                   instruction.opcode == OpCodes.Ldloc_1 ||
                   instruction.opcode == OpCodes.Ldloc_2 ||
                   instruction.opcode == OpCodes.Ldloc_3;
        }

        private static bool IsStloc(CodeInstruction instruction)
        {
            return instruction.opcode == OpCodes.Stloc ||
                   instruction.opcode == OpCodes.Stloc_S ||
                   instruction.opcode == OpCodes.Stloc_0 ||
                   instruction.opcode == OpCodes.Stloc_1 ||
                   instruction.opcode == OpCodes.Stloc_2 ||
                   instruction.opcode == OpCodes.Stloc_3;
        }

        private static bool IsLdcI4(CodeInstruction instruction, int value)
        {
            if (instruction.opcode == OpCodes.Ldc_I4)
                return Equals(instruction.operand, value);
            if (instruction.opcode == OpCodes.Ldc_I4_S)
                return Convert.ToInt32(instruction.operand) == value;

            switch (value)
            {
                case -1: return instruction.opcode == OpCodes.Ldc_I4_M1;
                case 0: return instruction.opcode == OpCodes.Ldc_I4_0;
                case 1: return instruction.opcode == OpCodes.Ldc_I4_1;
                case 2: return instruction.opcode == OpCodes.Ldc_I4_2;
                case 3: return instruction.opcode == OpCodes.Ldc_I4_3;
                case 4: return instruction.opcode == OpCodes.Ldc_I4_4;
                case 5: return instruction.opcode == OpCodes.Ldc_I4_5;
                case 6: return instruction.opcode == OpCodes.Ldc_I4_6;
                case 7: return instruction.opcode == OpCodes.Ldc_I4_7;
                case 8: return instruction.opcode == OpCodes.Ldc_I4_8;
                default: return false;
            }
        }
    }
}
