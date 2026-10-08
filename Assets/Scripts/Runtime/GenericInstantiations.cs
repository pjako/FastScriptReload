#if UNITY_EDITOR || LiveScriptReload_Enabled

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using ImmersiveVrToolsCommon.Runtime.Logging;

namespace FastScriptReload.Runtime
{
    /// <summary>
    /// Generic types and methods are compiled per instantiation (eg Box&lt;int&gt;, Find&lt;Enemy&gt;()), so each one in use has to be detoured.
    /// Mono can't list them, they're found by scanning the IL of project assemblies and hot reload assemblies for uses.
    /// </summary>
    public static class GenericInstantiations
    {
        //Assemblies compiled from the project (Assets and packages) end up there, others don't use project types
        private const string ProjectAssembliesFolder = "/Library/ScriptAssemblies/";
        private const BindingFlags AllDeclared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        //Code shared between reference type instantiations looks these up through the type it's called for (the original one),
        //they're only correct when the detoured code asks for the same things in the same order
        private static readonly HashSet<OpCode> TypeParameterLookupOpCodes = new HashSet<OpCode>
        {
            OpCodes.Ldtoken, OpCodes.Castclass, OpCodes.Isinst, OpCodes.Unbox, OpCodes.Unbox_Any, OpCodes.Newobj, OpCodes.Newarr,
            OpCodes.Initobj, OpCodes.Sizeof, OpCodes.Ldelema, OpCodes.Ldsfld, OpCodes.Stsfld, OpCodes.Ldsflda, OpCodes.Ldftn,
            OpCodes.Ldvirtftn, OpCodes.Mkrefany, OpCodes.Refanyval, OpCodes.Call, OpCodes.Callvirt
        };

        //Only these metadata tables can hold instantiations (MemberRef, TypeSpec, MethodSpec), resolving the others is wasted time
        private const int MemberRefTable = 0x0A;
        private const int TypeSpecTable = 0x1B;
        private const int MethodSpecTable = 0x2B;

        private static readonly object IndexLock = new object();
        private static readonly HashSet<Assembly> IndexedAssemblies = new HashSet<Assembly>();
        private static readonly List<Assembly> HotReloadAssembliesToIndex = new List<Assembly>();
        //Keyed by names without the patched postfix, so uses from hot reload assemblies count for the original type / method
        private static readonly Dictionary<string, Dictionary<string, Instantiation>> TypeInstantiations = new Dictionary<string, Dictionary<string, Instantiation>>();
        private static readonly Dictionary<string, Dictionary<string, Instantiation>> MethodInstantiations = new Dictionary<string, Dictionary<string, Instantiation>>();

        /// <summary>
        /// Scans Unity's default user code assemblies in advance, as that's where changed generic code usually is.
        /// Anything else (asmdefs, packages) is scanned when first needed, packages can take seconds (eg UniTask)
        /// </summary>
        public static void EnsureIndexed()
        {
            lock (IndexLock)
            {
                var sw = Stopwatch.StartNew();
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => IsProjectAssembly(a) && a.GetName().Name.StartsWith("Assembly-CSharp")))
                {
                    IndexAssembly(assembly);
                }
                IndexHotReloadAssemblies();
                LoggerScoped.LogDebug($"Indexed generic instantiations used in project assemblies, took: {sw.ElapsedMilliseconds}ms");
            }
        }

        //Only an assembly defining a generic type, or one referencing it, can use its instantiations
        private static void EnsureIndexedFor(Assembly definingAssembly)
        {
            var definingAssemblyName = definingAssembly.GetName().Name;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(IsProjectAssembly))
            {
                if (!IndexedAssemblies.Contains(assembly)
                    && (assembly == definingAssembly || assembly.GetReferencedAssemblies().Any(r => r.Name == definingAssemblyName)))
                {
                    IndexAssembly(assembly);
                }
            }
            IndexHotReloadAssemblies();
        }

        private static void IndexHotReloadAssemblies()
        {
            foreach (var hotReloadAssembly in HotReloadAssembliesToIndex)
            {
                IndexAssembly(hotReloadAssembly);
            }
            HotReloadAssembliesToIndex.Clear();
        }

        /// <summary>New code can use instantiations the project didn't use before</summary>
        public static void AddHotReloadAssembly(Assembly assembly)
        {
            lock (IndexLock)
            {
                HotReloadAssembliesToIndex.Add(assembly);
            }
        }

        public static List<Instantiation> GetUsedInstantiations(MethodInfo openMethod)
        {
            lock (IndexLock)
            {
                EnsureIndexedFor(openMethod.DeclaringType.Assembly);
                //Generic methods are indexed with their declaring type's arguments, other methods use all instantiations of their type
                var instantiations = openMethod.IsGenericMethodDefinition
                    ? MethodInstantiations.TryGetValue(MethodKey(openMethod), out var methodInstantiations) ? methodInstantiations : null
                    : TypeInstantiations.TryGetValue(TypeKey(openMethod.DeclaringType), out var typeInstantiations) ? typeInstantiations : null;
                return instantiations?.Values.ToList() ?? new List<Instantiation>();
            }
        }

        /// <summary>
        /// Whether the method needs its declaring type's generic parameters at runtime (eg typeof(T), new T(), casts to T, static members of Box&lt;T&gt;).
        /// When it does, detouring code shared between reference type instantiations isn't safe.
        /// </summary>
        public static bool UsesDeclaringTypeParametersAtRuntime(MethodBase openMethod)
        {
            var declaringType = openMethod.DeclaringType;
            if (declaringType == null || !declaringType.IsGenericTypeDefinition)
            {
                return false;
            }

            try
            {
                var body = openMethod.GetMethodBody();
                if (body == null)
                {
                    return false;
                }

                if (body.ExceptionHandlingClauses.Any(c => c.Flags == ExceptionHandlingClauseOptions.Clause && DependsOnTypeParameters(c.CatchType)))
                {
                    return true;
                }

                var typeArguments = declaringType.GetGenericArguments();
                var methodArguments = openMethod.IsGenericMethodDefinition ? openMethod.GetGenericArguments() : null;
                foreach (var instruction in IlInstructions.ReadTokens(body.GetILAsByteArray()))
                {
                    if (TypeParameterLookupOpCodes.Contains(instruction.OpCode)
                        && NeedsTypeParameterLookup(instruction.OpCode, openMethod.Module.ResolveMember(instruction.Token, typeArguments, methodArguments)))
                    {
                        return true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                //Can't tell, assume the worst
                LoggerScoped.LogDebug($"Unable to check how '{openMethod.Name}' uses generic parameters: {e.Message}");
                return true;
            }
        }

        private static bool NeedsTypeParameterLookup(OpCode opCode, MemberInfo member)
        {
            switch (member)
            {
                case Type type:
                    return DependsOnTypeParameters(type);
                case FieldInfo field:
                    //Instance fields are at the same offset for every reference type instantiation, static data is per instantiation
                    return field.IsStatic && DependsOnTypeParameters(field.DeclaringType);
                case MethodBase method:
                    var hasDependentMethodArguments = method.IsGenericMethod && method.GetGenericArguments().Any(DependsOnTypeParameters);
                    if (opCode == OpCodes.Call || opCode == OpCodes.Callvirt)
                    {
                        //Instance calls go to shared code through the instance, static and interface calls have to look up the instantiation
                        return hasDependentMethodArguments
                               || (DependsOnTypeParameters(method.DeclaringType) && (method.IsStatic || method.DeclaringType.IsInterface));
                    }
                    return hasDependentMethodArguments || DependsOnTypeParameters(method.DeclaringType);
                default:
                    return false;
            }
        }

        //Only type level parameters, method level ones are passed separately and were confirmed to be resolved correctly
        private static bool DependsOnTypeParameters(Type type)
        {
            if (type == null)
            {
                return false;
            }
            if (type.IsGenericParameter)
            {
                return type.DeclaringMethod == null;
            }
            if (type.HasElementType)
            {
                return DependsOnTypeParameters(type.GetElementType());
            }
            return type.IsGenericType && type.GetGenericArguments().Any(DependsOnTypeParameters);
        }

        private static bool IsProjectAssembly(Assembly assembly)
        {
            try
            {
                return !assembly.IsDynamic && assembly.Location.Replace('\\', '/').Contains(ProjectAssembliesFolder);
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private static void IndexAssembly(Assembly assembly)
        {
            if (!IndexedAssemblies.Add(assembly))
            {
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types.Where(t => t != null).ToArray();
            }

            foreach (var type in types)
            {
                foreach (var method in type.GetMethods(AllDeclared).Cast<MethodBase>().Concat(type.GetConstructors(AllDeclared)))
                {
                    IndexMethodBody(method);
                }
            }
        }

        private static void IndexMethodBody(MethodBase method)
        {
            byte[] il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch (Exception)
            {
                return;
            }
            if (il == null)
            {
                return;
            }

            var typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;
            foreach (var instruction in IlInstructions.ReadTokens(il))
            {
                var table = instruction.Token >> 24;
                if (table != MemberRefTable && table != TypeSpecTable && table != MethodSpecTable)
                {
                    continue;
                }

                try
                {
                    switch (method.Module.ResolveMember(instruction.Token, typeArguments, methodArguments))
                    {
                        case Type type:
                            AddType(type);
                            break;
                        case FieldInfo field:
                            AddType(field.DeclaringType);
                            AddType(field.FieldType);
                            break;
                        case MethodBase usedMethod:
                            AddMethod(usedMethod);
                            break;
                    }
                }
                catch (Exception)
                {
                    //Tokens that don't resolve (eg missing references) don't matter for finding instantiations
                }
            }
        }

        private static void AddType(Type type)
        {
            if (type == null)
            {
                return;
            }
            if (type.HasElementType)
            {
                AddType(type.GetElementType());
                return;
            }
            if (!type.IsGenericType)
            {
                return;
            }

            var typeArguments = type.GetGenericArguments();
            foreach (var typeArgument in typeArguments)
            {
                AddType(typeArgument);
            }
            if (!type.ContainsGenericParameters)
            {
                Add(TypeInstantiations, TypeKey(type.GetGenericTypeDefinition()), new Instantiation(typeArguments, Type.EmptyTypes));
            }
        }

        private static void AddMethod(MethodBase method)
        {
            AddType(method.DeclaringType);
            if (!(method is MethodInfo methodInfo) || !methodInfo.IsGenericMethod || methodInfo.IsGenericMethodDefinition)
            {
                return;
            }

            var methodArguments = methodInfo.GetGenericArguments();
            foreach (var methodArgument in methodArguments)
            {
                AddType(methodArgument);
            }
            if (methodInfo.ContainsGenericParameters)
            {
                return;
            }

            var definition = methodInfo.GetGenericMethodDefinition();
            var declaringType = methodInfo.DeclaringType;
            var typeArguments = Type.EmptyTypes;
            if (declaringType != null && declaringType.IsGenericType)
            {
                typeArguments = declaringType.GetGenericArguments();
                definition = (MethodInfo)MethodBase.GetMethodFromHandle(definition.MethodHandle, declaringType.GetGenericTypeDefinition().TypeHandle);
            }
            Add(MethodInstantiations, MethodKey(definition), new Instantiation(typeArguments, methodArguments));
        }

        private static void Add(Dictionary<string, Dictionary<string, Instantiation>> index, string key, Instantiation instantiation)
        {
            if (!index.TryGetValue(key, out var instantiations))
            {
                index[key] = instantiations = new Dictionary<string, Instantiation>();
            }
            instantiations[instantiation.ToString()] = instantiation;
        }

        private static string TypeKey(Type genericTypeDefinition)
        {
            return RemovePatchedPostfix(genericTypeDefinition.FullName);
        }

        private static string MethodKey(MethodBase genericMethodDefinition)
        {
            return RemovePatchedPostfix(genericMethodDefinition.FullDescription());
        }

        private static string RemovePatchedPostfix(string name)
        {
            return name.Replace(AssemblyChangesLoader.ClassnamePatchedPostfix, string.Empty);
        }

        public class Instantiation
        {
            public Type[] TypeArguments { get; }
            public Type[] MethodArguments { get; }

            //Mono compiles one shared version for reference type arguments, value type arguments get their own code
            public bool SharesCodeWithOtherTypeInstantiations => TypeArguments.Any(a => !a.IsValueType);

            public Instantiation(Type[] typeArguments, Type[] methodArguments)
            {
                TypeArguments = typeArguments;
                MethodArguments = methodArguments;
            }

            /// <summary>Closes an open method of a type with the same generic parameters (the original or patched one)</summary>
            public MethodBase Close(MethodInfo openMethod)
            {
                MethodBase method = openMethod;
                if (TypeArguments.Length > 0)
                {
                    var closedType = openMethod.DeclaringType.MakeGenericType(TypeArguments);
                    method = MethodBase.GetMethodFromHandle(openMethod.MethodHandle, closedType.TypeHandle);
                }
                if (MethodArguments.Length > 0)
                {
                    method = ((MethodInfo)method).MakeGenericMethod(MethodArguments);
                }
                return method;
            }

            public override string ToString()
            {
                var arguments = TypeArguments.Concat(MethodArguments).Select(a => a.FullName ?? a.Name);
                return $"<{string.Join(", ", arguments)}>";
            }
        }

        private static class IlInstructions
        {
            private static readonly Dictionary<short, OpCode> OpCodesByValue = CreateOpCodesByValue();

            public struct TokenInstruction
            {
                public OpCode OpCode;
                public int Token;
            }

            /// <summary>Instructions with a metadata token operand (types, methods, fields)</summary>
            public static IEnumerable<TokenInstruction> ReadTokens(byte[] il)
            {
                var position = 0;
                while (position < il.Length)
                {
                    short value = il[position++];
                    if (value == 0xFE && position < il.Length)
                    {
                        value = unchecked((short)(0xFE00 | il[position++]));
                    }
                    if (!OpCodesByValue.TryGetValue(value, out var opCode))
                    {
                        yield break;
                    }

                    switch (opCode.OperandType)
                    {
                        case OperandType.InlineField:
                        case OperandType.InlineMethod:
                        case OperandType.InlineTok:
                        case OperandType.InlineType:
                            yield return new TokenInstruction { OpCode = opCode, Token = BitConverter.ToInt32(il, position) };
                            position += 4;
                            break;
                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            position += 1;
                            break;
                        case OperandType.InlineVar:
                            position += 2;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            position += 8;
                            break;
                        case OperandType.InlineSwitch:
                            position += 4 + BitConverter.ToInt32(il, position) * 4;
                            break;
                        default:
                            position += 4;
                            break;
                    }
                }
            }

            private static Dictionary<short, OpCode> CreateOpCodesByValue()
            {
                var opCodesByValue = new Dictionary<short, OpCode>();
                foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    var opCode = (OpCode)field.GetValue(null);
                    opCodesByValue[opCode.Value] = opCode;
                }
                return opCodesByValue;
            }
        }
    }
}
#endif
