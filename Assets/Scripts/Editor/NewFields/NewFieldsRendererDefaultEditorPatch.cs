using System;
using System.Collections.Generic;
using FastScriptReload.Editor.Compilation.CodeRewriting;
using FastScriptReload.Runtime;
using FastScriptReload.Scripts.Runtime;
using HarmonyLib;
using ImmersiveVRTools.Editor.Common.Utilities;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEditor;
using UnityEngine;

namespace FastScriptReload.Editor.NewFields
{
    [InitializeOnLoad]
    public class NewFieldsRendererDefaultEditorPatch
    {
        private static List<string> _cachedKeys = new List<string>();
        
        static NewFieldsRendererDefaultEditorPatch()
        {
            if (AppleSiliconDetour.IsRequired)
            {
                // Harmony patches can't be applied on Apple Silicon, so added fields can't be drawn in the inspector
                LoggerScoped.LogDebug($"Fast Script Reload: on Apple Silicon, added fields are shown in '{NewFieldsWindow.MenuPath}' instead of the inspector.");
                return;
            }

            var harmony = new Harmony(nameof(NewFieldsRendererDefaultEditorPatch));
        
            var renderAdditionalFieldsOnOptimizedGuiPostfix = AccessTools.Method(typeof(NewFieldsRendererDefaultEditorPatch), nameof(OnOptimizedInspectorGUI));
            var noCustomEditorOriginalRenderingMethdod = AccessTools.Method("UnityEditor.GenericInspector, UnityEditor.CoreModule:OnOptimizedInspectorGUI");
            harmony.Patch(noCustomEditorOriginalRenderingMethdod, postfix: new HarmonyMethod(renderAdditionalFieldsOnOptimizedGuiPostfix));
        
            var renderAdditionalFieldsDrawDefaultInspectorPostfix = AccessTools.Method(typeof(NewFieldsRendererDefaultEditorPatch), nameof(DrawDefaultInspector));
            var customEditorRenderingMethod = AccessTools.Method("UnityEditor.Editor, UnityEditor.CoreModule:DrawDefaultInspector");
            harmony.Patch(customEditorRenderingMethod, postfix: new HarmonyMethod(renderAdditionalFieldsDrawDefaultInspectorPostfix));

#if ODIN_INSPECTOR
            // Odin Inspector support
            var renderAdditionalFieldsDrawOdinInspectorPostfix = AccessTools.Method(typeof(NewFieldsRendererDefaultEditorPatch), nameof(DrawOdinInspector));
            var customOdinEditorRenderingMethod = AccessTools.Method("Sirenix.OdinInspector.Editor.OdinEditor, Sirenix.OdinInspector.Editor:DrawOdinInspector");
            harmony.Patch(customOdinEditorRenderingMethod, postfix: new HarmonyMethod(renderAdditionalFieldsDrawOdinInspectorPostfix));
#endif
        }
        
#if ODIN_INSPECTOR
        private static void DrawOdinInspector(UnityEditor.Editor __instance)
        {
            RenderNewlyAddedFields(__instance);
        }
#endif

        private static void OnOptimizedInspectorGUI(Rect contentRect, UnityEditor.Editor __instance)
        {
            RenderNewlyAddedFields(__instance);
        }
        
        private static void DrawDefaultInspector(UnityEditor.Editor __instance)
        {
            RenderNewlyAddedFields(__instance);
        }

        public static bool HasNewlyAddedFields(UnityEngine.Object target)
        {
            return target && TemporaryNewFieldValues.TryGetDynamicallyAddedFieldValues(target, out _);
        }

        private static void RenderNewlyAddedFields(UnityEditor.Editor __instance)
        {
            RenderNewlyAddedFields(__instance.target);
        }

        public static void RenderNewlyAddedFields(UnityEngine.Object target)
        {
            //TODO: perf optimize, this will be used for many types, perhaps keep which types changed and just pass type?
            if (target)
            {
                if (TemporaryNewFieldValues.TryGetDynamicallyAddedFieldValues(target, out var addedFieldValues))
                {
                    EditorGUILayout.Space(10);

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("[FSR] Dynamically Added Fields:");
                    GuiTooltipHelper.AddHelperTooltip("Fields were dynamically added for hot-reload, you can adjust their values and on full reload they'll disappear from this section and move back to main one.");
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.Space(5);

                    try
                    {
                        _cachedKeys.AddRange(addedFieldValues.Keys); //otherwise collection changed exception can happen

                        var newFieldNameToGetTypeFn = CreateNewFieldInitMethodRewriter.ResolveNewFieldsToTypeFn(
                            AssemblyChangesLoader.Instance.GetRedirectedType(target.GetType())
                        );
                        
                        if(newFieldNameToGetTypeFn.Count == 0)
                            return;
                        
                        foreach (var addedFieldValueKey in _cachedKeys)
                        {
                            var newFieldType = (Type)newFieldNameToGetTypeFn[addedFieldValueKey]();
                            var box = addedFieldValues[addedFieldValueKey];

                            //rendering types come from UnityEditor.EditorGUI.DefaultPropertyField - that should handle all cases that editor can render
                            if (newFieldType == typeof(int)) box.Value = EditorGUILayout.IntField(addedFieldValueKey, (int)box.Value);
                            else if (newFieldType == typeof(bool)) box.Value = EditorGUILayout.Toggle(addedFieldValueKey, (bool)box.Value);
                            else if (newFieldType == typeof(float)) box.Value = EditorGUILayout.FloatField(addedFieldValueKey, (float)box.Value);
                            else if (newFieldType == typeof(string)) box.Value = EditorGUILayout.TextField(addedFieldValueKey, (string)box.Value);
                            else if (newFieldType == typeof(Color)) box.Value = EditorGUILayout.ColorField(addedFieldValueKey, (Color)box.Value);
                            //TODO: SerializedPropertyType.LayerMask
                            else if (newFieldType == typeof(Enum)) box.Value = EditorGUILayout.EnumPopup(addedFieldValueKey, (Enum)box.Value);
                            else if (newFieldType == typeof(Vector2)) box.Value = EditorGUILayout.Vector2Field(addedFieldValueKey, (Vector2)box.Value);
                            else if (newFieldType == typeof(Vector3)) box.Value = EditorGUILayout.Vector3Field(addedFieldValueKey, (Vector3)box.Value);
                            else if (newFieldType == typeof(Vector4)) box.Value = EditorGUILayout.Vector4Field(addedFieldValueKey, (Vector4)box.Value);
                            else if (newFieldType == typeof(Rect)) box.Value = EditorGUILayout.RectField(addedFieldValueKey, (Rect)box.Value);
                            //TODO: SerializedPropertyType.ArraySize
                            //TODO: SerializedPropertyType.Character
                            // else if (existingValueType == typeof(char)) box.Value = EditorGUILayout.TextField(addedFieldValueKey, (char)box.Value);
                            else if (newFieldType == typeof(AnimationCurve)) box.Value = EditorGUILayout.CurveField(addedFieldValueKey, (AnimationCurve)box.Value);
                            else if (newFieldType == typeof(Bounds)) box.Value = EditorGUILayout.BoundsField(addedFieldValueKey, (Bounds)box.Value);
                            else if (newFieldType == typeof(Gradient)) box.Value = EditorGUILayout.GradientField(addedFieldValueKey, (Gradient)box.Value);
                            //TODO: SerializedPropertyType.FixedBufferSize
                            else if (newFieldType == typeof(Vector2Int)) box.Value = EditorGUILayout.Vector2IntField(addedFieldValueKey, (Vector2Int)box.Value);
                            else if (newFieldType == typeof(Vector3Int)) box.Value = EditorGUILayout.Vector3IntField(addedFieldValueKey, (Vector3Int)box.Value);
                            else if (newFieldType == typeof(RectInt)) box.Value = EditorGUILayout.RectIntField(addedFieldValueKey, (RectInt)box.Value);
                            else if (newFieldType == typeof(BoundsInt)) box.Value = EditorGUILayout.BoundsIntField(addedFieldValueKey, (BoundsInt)box.Value);
                            //TODO: SerializedPropertyType.Hash128
                            else if (newFieldType == typeof(Quaternion))
                            {
                                //Quaternions are rendered as euler angles in editor
                                box.Value = Quaternion.Euler(EditorGUILayout.Vector3Field(addedFieldValueKey, ((Quaternion)box.Value).eulerAngles));
                            }
                            else if (typeof(UnityEngine.Object).IsAssignableFrom(newFieldType))
                            {
                                box.Value = EditorGUILayout.ObjectField(new GUIContent(addedFieldValueKey), (UnityEngine.Object)box.Value, newFieldType, target);
                            }

                            else
                            {
                                EditorGUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField($"{newFieldType.Name} - Unable to render");
                                GuiTooltipHelper.AddHelperTooltip(
                                    $"Unable to handle added-field rendering for type: {newFieldType.Name}, it won't be rendered. Best workaround is to not add this type dynamically in current version.");
                                EditorGUILayout.EndHorizontal();
                            }
                        }
                    }
                    finally
                    {
                        _cachedKeys.Clear();
                    }
                }
            }
        }
    }
}