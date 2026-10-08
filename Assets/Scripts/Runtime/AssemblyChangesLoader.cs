#if UNITY_EDITOR || LiveScriptReload_Enabled

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ImmersiveVRTools.Runtime.Common;
using ImmersiveVRTools.Runtime.Common.Extensions;
using ImmersiveVrToolsCommon.Runtime.Logging;
using UnityEngine;
using Debug = UnityEngine.Debug;

using Memory = FastScriptReload.Runtime.Polyfills.Memory;

namespace FastScriptReload.Runtime
{
    [PreventHotReload]
#if UNITY_EDITOR
    [UnityEditor.InitializeOnLoad]
#endif
    public class AssemblyChangesLoader: IAssemblyChangesLoader
    {
        const BindingFlags ALL_BINDING_FLAGS = BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Static | BindingFlags.Instance |
                                               BindingFlags.FlattenHierarchy;
            
        const BindingFlags ALL_DECLARED_METHODS_BINDING_FLAGS = BindingFlags.Public | BindingFlags.NonPublic |
                                                                BindingFlags.Static | BindingFlags.Instance |
                                                                BindingFlags.DeclaredOnly; //only declared methods can be redirected, otherwise it'll result in hang
        
        public const string ClassnamePatchedPostfix = "__Patched_";
        public const string ON_HOT_RELOAD_METHOD_NAME = "OnScriptHotReload";
        public const string ON_HOT_RELOAD_NO_INSTANCE_STATIC_METHOD_NAME = "OnScriptHotReloadNoInstance";

        private static readonly List<Type> ExcludeMethodsDefinedOnTypes = new List<Type>
        {
            typeof(MonoBehaviour),
            typeof(Behaviour),
            typeof(UnityEngine.Object),
            typeof(Component),
            typeof(System.Object)
        }; //TODO: move out and possibly define a way to exclude all non-client created code? as this will crash editor
        
        private static AssemblyChangesLoader _instance;
        public static AssemblyChangesLoader Instance => _instance ?? (_instance = new AssemblyChangesLoader());

        private Dictionary<Type, Type> _existingTypeToRedirectedType = new Dictionary<Type, Type>();

        public void DynamicallyUpdateMethodsForCreatedAssembly(Assembly dynamicallyLoadedAssemblyWithUpdates, AssemblyChangesLoaderEditorOptionsNeededInBuild editorOptions)
        {
            try
            {
                var sw = new Stopwatch();
                sw.Start();

                foreach (var createdType in dynamicallyLoadedAssemblyWithUpdates.GetTypes()
                             .Where(t => (t.IsClass
                                         && !typeof(Delegate).IsAssignableFrom(t)) //don't redirect delegates
                                         // || (t.IsValueType && !t.IsPrimitive) //struct check, ensure works
                             )
                        )
                {
                    if (createdType.GetCustomAttribute<PreventHotReload>() != null)
                    {
                        //TODO: ideally type would be excluded from compilation not just from detour
                        LoggerScoped.Log($"Type: {createdType.Name} marked as {nameof(PreventHotReload)} - ignoring change.");
                        continue;
                    }
                    
                    var createdTypeNameWithoutPatchedPostfix = RemoveClassPostfix(createdType.FullName);
                    if (ProjectTypeCache.AllTypesInNonDynamicGeneratedAssemblies.TryGetValue(createdTypeNameWithoutPatchedPostfix, out var matchingTypeInExistingAssemblies))
                    {
                        _existingTypeToRedirectedType[matchingTypeInExistingAssemblies] = createdType;

                        if (IsCompilerGenerated(createdType) && !HaveSameInstanceFields(createdType, matchingTypeInExistingAssemblies))
                        {
                            //Closures and coroutine / async state machines still running would execute new code on their old field layout.
                            //They keep running old code instead, new ones are created from the new type anyway
                            LoggerScoped.LogDebug($"Fields of compiler generated type: '{matchingTypeInExistingAssemblies.FullName}' changed, existing instances keep running previous code.");
                            continue;
                        }

                        if (!editorOptions.IsDidFieldsOrPropertyCountChangedCheckDisabled
                            && !editorOptions.EnableExperimentalAddedFieldsSupport
                            && DidFieldsOrPropertyCountChanged(createdType,  matchingTypeInExistingAssemblies))
                        {
                            continue;
                        }

                        //Looked up by description, building descriptions is slow and was done for every existing method, for every changed one
                        var allDeclaredMethodsInExistingTypeByDescription = new Dictionary<string, MethodInfo>();
                        foreach (var existingMethod in matchingTypeInExistingAssemblies.GetMethods(ALL_DECLARED_METHODS_BINDING_FLAGS)
                                     .Where(m => !ExcludeMethodsDefinedOnTypes.Contains(m.DeclaringType)))
                        {
                            var existingMethodDescription = existingMethod.FullDescription();
                            if (!allDeclaredMethodsInExistingTypeByDescription.ContainsKey(existingMethodDescription))
                            {
                                allDeclaredMethodsInExistingTypeByDescription.Add(existingMethodDescription, existingMethod);
                            }
                        }

                        var methodsWithChangedLambdas = FindMethodsWithChangedLambdas(createdType, matchingTypeInExistingAssemblies);

                        foreach (var createdTypeMethodToUpdate in createdType.GetMethods(ALL_DECLARED_METHODS_BINDING_FLAGS)
                                     .Where(m => !ExcludeMethodsDefinedOnTypes.Contains(m.DeclaringType)))
                        {
                            if (TryGetLambdaContainingMethodName(createdTypeMethodToUpdate, out var lambdaContainingMethodName)
                                && methodsWithChangedLambdas.Contains(lambdaContainingMethodName))
                            {
                                //Lambdas are named by their position, after adding / removing one the same name can belong to a different lambda.
                                //Delegates created before the change keep running previous code
                                LoggerScoped.LogDebug($"Lambdas in method: '{lambdaContainingMethodName}' of type: '{matchingTypeInExistingAssemblies.FullName}' were added or removed, existing delegates keep running previous code.");
                                continue;
                            }

                            var createdTypeMethodToUpdateFullDescriptionWithoutPatchedClassPostfix = RemoveClassPostfix(createdTypeMethodToUpdate.FullDescription());
                            allDeclaredMethodsInExistingTypeByDescription.TryGetValue(createdTypeMethodToUpdateFullDescriptionWithoutPatchedClassPostfix, out var matchingMethodInExistingType);
                            if (matchingMethodInExistingType != null)
                            {
                                if (matchingMethodInExistingType.IsGenericMethod)
                                {
                                    LoggerScoped.LogWarning($"Method: '{matchingMethodInExistingType.FullDescription()}' is generic. Hot-Reload for generic methods is not supported yet, you won't see changes for that method.");
                                    continue;
                                }

                                if (matchingMethodInExistingType.DeclaringType != null && matchingMethodInExistingType.DeclaringType.IsGenericType)
                                {
                                    LoggerScoped.LogWarning($"Type for method: '{matchingMethodInExistingType.FullDescription()}' is generic. Hot-Reload for generic types is not supported yet, you won't see changes for that type.");
                                    continue;
                                }

                                LoggerScoped.LogDebug($"Trying to detour method, from: '{matchingMethodInExistingType.FullDescription()}' to: '{createdTypeMethodToUpdate.FullDescription()}'");
                                DetourCrashHandler.LogDetour(matchingMethodInExistingType.ResolveFullName());
                                if (AppleSiliconDetour.IsRequired)
                                {
                                    AppleSiliconDetour.DetourMethod(matchingMethodInExistingType, createdTypeMethodToUpdate);
                                }
                                else
                                {
                                    Memory.DetourMethod(matchingMethodInExistingType, createdTypeMethodToUpdate);
                                }
                            }
                            else if (createdTypeMethodToUpdate.Name != ON_HOT_RELOAD_METHOD_NAME) //Added OnScriptHotReload is called without a detour, see FindAndExecuteOnScriptHotReload
                            {
                                LoggerScoped.LogWarning($"Method: {createdTypeMethodToUpdate.FullDescription()} does not exist in initially compiled type: {matchingTypeInExistingAssemblies.FullName}. " +
                                                 $"Adding new methods at runtime is not fully supported. \r\n" +
                                                 $"It'll only work new method is only used by declaring class (eg private method)\r\n" +
                                                 $"Make sure to add method before initial compilation.");
                            }
                        }
                        
                        FindAndExecuteStaticOnScriptHotReloadNoInstance(createdType);
                        FindAndExecuteOnScriptHotReload(matchingTypeInExistingAssemblies, createdType);
                    }
                    else if (IsCompilerGenerated(createdType))
                    {
                        //Generated for code that only exists in the new version (eg a new lambda or 'dynamic' call), there's nothing to update
                        LoggerScoped.LogDebug($"New compiler generated type: '{createdType.FullName}'");
                    }
                    else
                    {
                        LoggerScoped.LogWarning($"FSR: Unable to find existing type for: '{createdType.FullName}', this is not an issue if you added new type. <color=orange>If it's an existing type please do a full domain-reload - one of optimisations is to cache existing types for later lookup on first call.</color>");
                        FindAndExecuteStaticOnScriptHotReloadNoInstance(createdType);
                        FindAndExecuteOnScriptHotReload(createdType, createdType);
                    }
                }
                
                LoggerScoped.Log($"Hot-reload completed (took {sw.ElapsedMilliseconds}ms)");
            }
            finally
            {
                DetourCrashHandler.ClearDetourLog();
            }
        }
        
        public Type GetRedirectedType(Type forExistingType)
        {
            return _existingTypeToRedirectedType[forExistingType];
        }

        //Closure classes, lambda caches and iterator / async state machines, their names start with '<' (eg '<>c__DisplayClass4_0', '<Fire>d__5')
        private static bool IsCompilerGenerated(Type type)
        {
            return type.Name.StartsWith("<");
        }

        private static bool HaveSameInstanceFields(Type createdType, Type existingType)
        {
            const BindingFlags instanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var createdTypeFields = createdType.GetFields(instanceFields).Select(f => RemoveClassPostfix(f.FieldType.FullName ?? f.FieldType.Name) + " " + f.Name);
            var existingTypeFields = existingType.GetFields(instanceFields).Select(f => (f.FieldType.FullName ?? f.FieldType.Name) + " " + f.Name);
            return createdTypeFields.SequenceEqual(existingTypeFields);
        }

        //Lambdas compile to methods named after the method containing them plus their position, eg '<Start>b__4_0'
        private static bool TryGetLambdaContainingMethodName(MethodInfo method, out string containingMethodName)
        {
            var lambdaMarkerIndex = method.Name.IndexOf(">b__", StringComparison.Ordinal);
            containingMethodName = method.Name.StartsWith("<") && lambdaMarkerIndex > 0 ? method.Name.Substring(1, lambdaMarkerIndex - 1) : null;
            return containingMethodName != null;
        }

        private static HashSet<string> FindMethodsWithChangedLambdas(Type createdType, Type existingType)
        {
            var createdTypeLambdas = GroupLambdasByContainingMethod(createdType, m => RemoveClassPostfix(m.FullDescription()));
            var existingTypeLambdas = GroupLambdasByContainingMethod(existingType, m => m.FullDescription());

            return new HashSet<string>(createdTypeLambdas.Keys.Concat(existingTypeLambdas.Keys)
                .Where(containingMethodName => !createdTypeLambdas.TryGetValue(containingMethodName, out var created)
                                               || !existingTypeLambdas.TryGetValue(containingMethodName, out var existing)
                                               || !created.SequenceEqual(existing)));
        }

        private static Dictionary<string, List<string>> GroupLambdasByContainingMethod(Type type, Func<MethodInfo, string> getDescription)
        {
            var lambdasByContainingMethod = new Dictionary<string, List<string>>();
            foreach (var method in type.GetMethods(ALL_DECLARED_METHODS_BINDING_FLAGS).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (TryGetLambdaContainingMethodName(method, out var containingMethodName))
                {
                    if (!lambdasByContainingMethod.TryGetValue(containingMethodName, out var lambdas))
                    {
                        lambdasByContainingMethod[containingMethodName] = lambdas = new List<string>();
                    }
                    lambdas.Add(getDescription(method));
                }
            }

            return lambdasByContainingMethod;
        }

        private static bool DidFieldsOrPropertyCountChanged(Type createdType, Type matchingTypeInExistingAssemblies)
        {
            var createdTypeFieldAndProperties = createdType.GetFields(ALL_BINDING_FLAGS).Concat(createdType.GetProperties(ALL_BINDING_FLAGS).Cast<MemberInfo>()).ToList();
            var matchingTypeFieldAndProperties = matchingTypeInExistingAssemblies.GetFields(ALL_BINDING_FLAGS).Concat(matchingTypeInExistingAssemblies.GetProperties(ALL_BINDING_FLAGS).Cast<MemberInfo>()).ToList();
            if (createdTypeFieldAndProperties.Count != matchingTypeFieldAndProperties.Count)
            {
                var addedMemberNames = createdTypeFieldAndProperties.Select(m => m.Name).Except(matchingTypeFieldAndProperties.Select(m => m.Name)).ToList();
                LoggerScoped.LogError($"It seems you've added/removed field to changed script. This is not supported and will result in undefined behaviour. Hot-reload will not be performed for type: {matchingTypeInExistingAssemblies.Name}" +
                               $"\r\n\r\nYou can skip the check and force reload anyway if needed, to do so go to: 'Window -> Fast Script Reload -> Start Screen -> Reload -> tick 'Disable added/removed fields check'" +
                               (addedMemberNames.Any() ? $"\r\nAdded: {string.Join(", ", addedMemberNames)}" : ""));
                LoggerScoped.Log(
                    $"<color=orange>There's an experimental feature that allows to add new fields (which are adjustable in editor), to enable please:</color>" +
                    $"\r\n - Open Settings 'Window -> Fast Script Reload -> Start Screen -> New Fields -> tick 'Enable experimental added field support'");
                return true;
            }

            return false;
        }

        private static void FindAndExecuteStaticOnScriptHotReloadNoInstance(Type createdType)
        {
            var onScriptHotReloadStaticFnForType = createdType.GetMethod(ON_HOT_RELOAD_NO_INSTANCE_STATIC_METHOD_NAME,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (onScriptHotReloadStaticFnForType != null)
            {
                UnityMainThreadDispatcher.Instance.Enqueue(() =>
                {
                    onScriptHotReloadStaticFnForType.Invoke(null, null);
                });
            }
        }

        private static void FindAndExecuteOnScriptHotReload(Type originalType, Type detourType)
        {
            var onScriptHotReloadFnForType = originalType.GetMethod(ON_HOT_RELOAD_METHOD_NAME, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (onScriptHotReloadFnForType != null)
            {
                ExecuteFnOnMainThread(originalType, instance => onScriptHotReloadFnForType.Invoke(instance, null));
            }
            else
            {
                //When OnScriptHotReload method is not present in original type reflection can not use method from new type (as instance types are not matching and will cause exception)
                var onScriptHotReloadFnForCreatedType = detourType.GetMethod(ON_HOT_RELOAD_METHOD_NAME, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (onScriptHotReloadFnForCreatedType != null)
                {
                    if (onScriptHotReloadFnForCreatedType.GetParameters().Length > 0)
                    {
                        LoggerScoped.LogWarning($"Added {ON_HOT_RELOAD_METHOD_NAME} on type: {originalType.Name} has parameters, it won't be called.");
                        return;
                    }

                    var callOnInstanceOfOriginalType = CreateCallOnInstanceOfOriginalType(onScriptHotReloadFnForCreatedType);
                    ExecuteFnOnMainThread(originalType, callOnInstanceOfOriginalType);
                }
            }
        }

        //Calls the new type's method with an instance of the original type as 'this', the same way detoured methods run.
        //Done in IL as reflection checks the instance type. Doesn't need a detour, so it also works where those aren't possible (Apple Silicon)
        private static Action<object> CreateCallOnInstanceOfOriginalType(MethodInfo instanceMethod)
        {
            var dynamicMethod = new DynamicMethod(instanceMethod.Name + "_CalledOnOriginalInstance", typeof(void), new[] { typeof(object) },
                typeof(AssemblyChangesLoader).Module, skipVisibility: true);
            var il = dynamicMethod.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, instanceMethod);
            if (instanceMethod.ReturnType != typeof(void))
            {
                il.Emit(OpCodes.Pop);
            }
            il.Emit(OpCodes.Ret);

            return (Action<object>)dynamicMethod.CreateDelegate(typeof(Action<object>));
        }

        private static void ExecuteFnOnMainThread(Type originalType, Action<object> onScriptHotReloadFn)
        {
            UnityMainThreadDispatcher.Instance.Enqueue(() =>
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(originalType))
                {
                    LoggerScoped.LogWarning($"Type: {originalType.Name} is not {nameof(MonoBehaviour)}, {ON_HOT_RELOAD_METHOD_NAME} method can't be executed. You can still use static version: {ON_HOT_RELOAD_NO_INSTANCE_STATIC_METHOD_NAME}");
                    return;
                }
                       //TODO: perf - could find them in different way?
#if UNITY_6000_0_OR_NEWER // added new FindObjectsByType
                foreach (var instanceOfType in UnityEngine.Object.FindObjectsByType(originalType, FindObjectsSortMode.None))
                {
                    onScriptHotReloadFn(instanceOfType);
                }
#elif UNITY_2021_1_OR_NEWER // keeping FindObjectOfType for older unity versions
                foreach (var instanceOfType in UnityEngine.Object.FindObjectsOfType(originalType))
                {
                    onScriptHotReloadFn(instanceOfType);
                }
#endif
            });
        }

        private static string RemoveClassPostfix(string fqdn)
        {
            return fqdn.Replace(ClassnamePatchedPostfix, string.Empty);
        }
    }
    
    
    [AttributeUsage(AttributeTargets.Assembly)]
    public class DynamicallyCreatedAssemblyAttribute : Attribute
    {
        public DynamicallyCreatedAssemblyAttribute()
        {
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class PreventHotReload : Attribute
    {
        
    }
    
    public interface IAssemblyChangesLoader
    {
        void DynamicallyUpdateMethodsForCreatedAssembly(Assembly dynamicallyLoadedAssemblyWithUpdates, AssemblyChangesLoaderEditorOptionsNeededInBuild editorOptions);
    }
    
    [Serializable]
    public class AssemblyChangesLoaderEditorOptionsNeededInBuild
    {
        public bool IsDidFieldsOrPropertyCountChangedCheckDisabled;
        public bool EnableExperimentalAddedFieldsSupport;

        public AssemblyChangesLoaderEditorOptionsNeededInBuild(bool isDidFieldsOrPropertyCountChangedCheckDisabled, bool enableExperimentalAddedFieldsSupport)
        {
            IsDidFieldsOrPropertyCountChangedCheckDisabled = isDidFieldsOrPropertyCountChangedCheckDisabled;
            EnableExperimentalAddedFieldsSupport = enableExperimentalAddedFieldsSupport;
        }
        
#pragma warning disable 0618
        [Obsolete("Needed for network serialization")]
#pragma warning restore 0618
        public AssemblyChangesLoaderEditorOptionsNeededInBuild()
        {
        }

        //WARN: make sure it has same params as ctor
        public void UpdateValues(bool isDidFieldsOrPropertyCountChangedCheckDisabled, bool enableExperimentalAddedFieldsSupport)
        {
            IsDidFieldsOrPropertyCountChangedCheckDisabled = isDidFieldsOrPropertyCountChangedCheckDisabled;
            EnableExperimentalAddedFieldsSupport = enableExperimentalAddedFieldsSupport;
        }
    }
}
#endif
