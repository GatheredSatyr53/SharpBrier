using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;

namespace SharpBrier
{
    /// <summary>
    /// Builds instances of anonymous subclasses of <typeparamref name="TClass"/> at runtime,
    /// overriding virtual and abstract methods with delegates (an analogue of Java anonymous classes).
    /// <typeparamref name="TClass"/> may be a class or an interface; for an interface the anonymous type
    /// derives from <see cref="object"/> and implements the interface together with everything it inherits.
    /// </summary>
    public static class Anonym<TClass> where TClass : class
    {
        private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string BodiesFieldName = "<SharpBrier>__bodies";

        private static readonly ModuleBuilder Module = CreateModule();
        private static readonly ConcurrentDictionary<TypeShape, ConstructorInfo> GeneratedTypes = new();
        private static int _typeCounter;

        /// <summary>
        /// Creates an instance of an anonymous subclass of <typeparamref name="TClass"/>.
        /// </summary>
        /// <param name="constructorArguments">Arguments for the base class constructor.</param>
        /// <param name="buildAction">Registers the methods to override.</param>
        /// <param name="callerFilePath">Supplied by the compiler; used to name the generated type.</param>
        /// <param name="callerLineNumber">Supplied by the compiler; used to name the generated type.</param>
        /// <remarks>
        /// The generated type is cached by its shape (the base constructor and the set of overridden methods),
        /// so repeated calls, including calls in a loop, reuse one type while every instance keeps its own delegates.
        /// </remarks>
        public static TClass Build(
            object[] constructorArguments,
            Action<OverridingBuilder> buildAction,
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            ArgumentNullException.ThrowIfNull(constructorArguments);
            ArgumentNullException.ThrowIfNull(buildAction);

            Type baseType = typeof(TClass);
            if (baseType.IsSealed)
                throw new InvalidOperationException($"{baseType} is sealed and cannot be subclassed.");
            if (baseType.IsGenericTypeDefinition)
                throw new InvalidOperationException($"{baseType} is an open generic type.");
            if (baseType.IsInterface)
            {
                if (constructorArguments.Length > 0)
                    throw new ArgumentException(
                        $"{baseType} is an interface; constructor arguments must be empty.", nameof(constructorArguments));
                MethodInfo? staticAbstract = InterfaceHierarchy(baseType)
                    .SelectMany(i => i.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    .FirstOrDefault(m => m.IsAbstract);
                if (staticAbstract is not null)
                    throw new NotSupportedException(
                        $"{baseType} declares static abstract member {Describe(staticAbstract)}; static abstract members are not supported.");
            }

            OverridingBuilder builder = new();
            buildAction(builder);
            List<Override> overrides = builder.Overrides;

            HashSet<MethodInfo> overridden = new();
            foreach (Override @override in overrides)
            {
                if (!overridden.Add(@override.Target))
                    throw new InvalidOperationException($"Method {Describe(@override.Target)} is overridden more than once.");
            }

            ConstructorInfo baseConstructor = FindConstructor(baseType, constructorArguments);
            TypeShape shape = new(baseConstructor, overrides.Select(o => o.Shape).ToArray());

            ConstructorInfo constructor = GeneratedTypes.GetOrAdd(
                shape,
                static (s, site) => GenerateType(s, site.FilePath, site.LineNumber),
                (FilePath: callerFilePath, LineNumber: callerLineNumber));

            object?[] arguments = new object?[constructorArguments.Length + 1];
            arguments[0] = overrides.Select(o => o.Body).ToArray();
            constructorArguments.CopyTo(arguments, 1);

            return (TClass)constructor.Invoke(arguments);
        }

        public class OverridingBuilder
        {
            internal List<Override> Overrides { get; } = new();

            public OverridingBuilder NoReturn(string name, Type[] arguments, Action<TClass, object[]> body)
            {
                ArgumentNullException.ThrowIfNull(body);
                MethodInfo target = FindOverridableMethod(name, arguments);
                if (target.ReturnType != typeof(void))
                    throw new InvalidOperationException(
                        $"Method {Describe(target)} returns {target.ReturnType}; use {nameof(HasReturn)} instead.");
                Overrides.Add(new Override(new OverrideShape(target, body.GetType(), null), body));
                return this;
            }

            public OverridingBuilder HasReturn<TReturnValue>(string name, Type[] arguments, Func<TClass, object[], TReturnValue> body)
            {
                ArgumentNullException.ThrowIfNull(body);
                MethodInfo target = FindOverridableMethod(name, arguments);
                Type returnType = target.ReturnType;
                if (returnType == typeof(void))
                    throw new InvalidOperationException(
                        $"Method {Describe(target)} returns void; use {nameof(NoReturn)} instead.");
                if (!returnType.IsAssignableFrom(typeof(TReturnValue)) && typeof(TReturnValue) != typeof(object))
                    throw new InvalidOperationException(
                        $"Method {Describe(target)} returns {returnType}, which cannot be assigned from {typeof(TReturnValue)}.");
                Overrides.Add(new Override(new OverrideShape(target, body.GetType(), typeof(TReturnValue)), body));
                return this;
            }
        }

        /// <summary>A registered override: what to override, with which delegate type, and the delegate itself.</summary>
        internal sealed record Override(OverrideShape Shape, Delegate Body)
        {
            public MethodInfo Target => Shape.Target;
        }

        /// <summary>The part of an override that determines the generated IL (everything except the delegate instance).</summary>
        internal sealed record OverrideShape(MethodInfo Target, Type DelegateType, Type? DelegateReturnType);

        /// <summary>Cache key: everything that determines the generated type.</summary>
        internal sealed class TypeShape : IEquatable<TypeShape>
        {
            public ConstructorInfo BaseConstructor { get; }
            public OverrideShape[] Overrides { get; }

            public TypeShape(ConstructorInfo baseConstructor, OverrideShape[] overrides)
            {
                BaseConstructor = baseConstructor;
                Overrides = overrides;
            }

            public bool Equals(TypeShape? other) =>
                other is not null
                && BaseConstructor.Equals(other.BaseConstructor)
                && Overrides.AsSpan().SequenceEqual(other.Overrides);

            public override bool Equals(object? obj) => Equals(obj as TypeShape);

            public override int GetHashCode()
            {
                HashCode hash = new();
                hash.Add(BaseConstructor);
                foreach (OverrideShape @override in Overrides)
                    hash.Add(@override);
                return hash.ToHashCode();
            }
        }

        private static ConstructorInfo GenerateType(TypeShape shape, string callerFilePath, int callerLineNumber)
        {
            Type baseType = typeof(TClass);
            // A '.' in a type name would be read as a namespace separator, so the file extension is dropped.
            string site = callerFilePath.Length == 0 ? "" : $"{Path.GetFileNameWithoutExtension(callerFilePath)}:{callerLineNumber}";

            TypeBuilder typeBuilder = Module.DefineType(
                $"{baseType.Name}<Anonym>{site}#{Interlocked.Increment(ref _typeCounter)}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                baseType.IsInterface ? typeof(object) : baseType);

            if (baseType.IsInterface)
            {
                foreach (Type implemented in InterfaceHierarchy(baseType))
                    typeBuilder.AddInterfaceImplementation(implemented);
            }

            FieldBuilder bodiesField = typeBuilder.DefineField(
                BodiesFieldName, typeof(Delegate[]), FieldAttributes.Private | FieldAttributes.InitOnly);

            EmitConstructor(typeBuilder, bodiesField, shape.BaseConstructor);

            HashSet<MethodInfo> overridden = new();
            for (int i = 0; i < shape.Overrides.Length; i++)
            {
                overridden.Add(shape.Overrides[i].Target);
                EmitOverride(typeBuilder, bodiesField, i, shape.Overrides[i]);
            }

            foreach (MethodInfo abstractMethod in OverridableMethods(baseType))
            {
                if (abstractMethod.IsAbstract && !overridden.Contains(abstractMethod))
                    EmitAbstractStub(typeBuilder, abstractMethod);
            }

            Type createdType = typeBuilder.CreateType();
            return createdType.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Single();
        }

        private static ModuleBuilder CreateModule()
        {
            AssemblyName assemblyName = new($"SharpBrier.Anonym.{typeof(TClass).Name}.{Guid.NewGuid():N}");
            AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
            ModuleBuilder module = assembly.DefineDynamicModule("Main");

            // Let the generated assembly see internal members of the assembly that declares TClass,
            // so internal classes and protected internal members can be subclassed without InternalsVisibleTo.
            ConstructorInfo ignoresAccessChecksCtor = DefineIgnoresAccessChecksToAttribute(module);
            string? targetAssemblyName = typeof(TClass).Assembly.GetName().Name;
            if (targetAssemblyName is not null)
                assembly.SetCustomAttribute(new CustomAttributeBuilder(ignoresAccessChecksCtor, [targetAssemblyName]));

            return module;
        }

        /// <summary>
        /// Defines System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute inside the dynamic assembly.
        /// The runtime recognises the attribute by its full name and disables access checks against the named assembly.
        /// </summary>
        private static ConstructorInfo DefineIgnoresAccessChecksToAttribute(ModuleBuilder module)
        {
            TypeBuilder attributeType = module.DefineType(
                "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed,
                typeof(Attribute));

            FieldBuilder nameField = attributeType.DefineField("_assemblyName", typeof(string), FieldAttributes.Private);

            ConstructorBuilder ctor = attributeType.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                [typeof(string)]);
            ILGenerator il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Attribute).GetConstructor(InstanceMembers, Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, nameField);
            il.Emit(OpCodes.Ret);

            PropertyBuilder property = attributeType.DefineProperty("AssemblyName", PropertyAttributes.None, typeof(string), null);
            MethodBuilder getter = attributeType.DefineMethod(
                "get_AssemblyName",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                typeof(string), Type.EmptyTypes);
            ILGenerator getterIl = getter.GetILGenerator();
            getterIl.Emit(OpCodes.Ldarg_0);
            getterIl.Emit(OpCodes.Ldfld, nameField);
            getterIl.Emit(OpCodes.Ret);
            property.SetGetMethod(getter);

            CustomAttributeBuilder usage = new(
                typeof(AttributeUsageAttribute).GetConstructor([typeof(AttributeTargets)])!,
                [AttributeTargets.Assembly],
                [typeof(AttributeUsageAttribute).GetProperty(nameof(AttributeUsageAttribute.AllowMultiple))!],
                [true]);
            attributeType.SetCustomAttribute(usage);

            Type created = attributeType.CreateType();
            return created.GetConstructor([typeof(string)])!;
        }

        private static MethodInfo FindOverridableMethod(string name, Type[] arguments)
        {
            ArgumentException.ThrowIfNullOrEmpty(name);
            ArgumentNullException.ThrowIfNull(arguments);

            Type baseType = typeof(TClass);
            IEnumerable<Type> searchedTypes = baseType.IsInterface ? InterfaceHierarchy(baseType) : [baseType];

            // "IFoo.Bar" or "My.Namespace.IFoo.Bar" picks the method of one specific interface in the hierarchy.
            int lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                string typeName = name[..lastDot];
                name = name[(lastDot + 1)..];
                searchedTypes = searchedTypes.Where(t => t.Name == typeName || t.FullName == typeName).ToArray();
                if (!searchedTypes.Any())
                    throw new MissingMethodException($"{baseType} does not declare or inherit a type named {typeName}.");
            }

            MethodInfo? method = null;
            foreach (Type searched in searchedTypes)
            {
                method = searched.GetMethod(name, InstanceMembers, binder: null, arguments, modifiers: null);
                if (method is not null)
                    break;
            }
            if (method is null)
            {
                string signature = $"{name}({string.Join(", ", arguments.Select(t => t.Name))})";
                MethodInfo? genericCandidate = OverridableMethods(baseType)
                    .FirstOrDefault(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == arguments.Length);
                if (genericCandidate is not null)
                    throw new NotSupportedException($"Method {Describe(genericCandidate)} is generic; generic methods are not supported.");
                throw new MissingMethodException($"{baseType} has no instance method {signature}.");
            }
            if (!method.IsVirtual || method.IsFinal)
                throw new InvalidOperationException($"Method {Describe(method)} is not virtual or abstract and cannot be overridden.");
            if (method.IsPrivate)
                throw new InvalidOperationException($"Method {Describe(method)} is private and cannot be overridden.");
            if (method.IsGenericMethodDefinition)
                throw new NotSupportedException($"Method {Describe(method)} is generic; generic methods are not supported.");
            if (method.ReturnType.IsByRef || method.GetParameters().Any(p => p.ParameterType.IsByRef))
                throw new NotSupportedException($"Method {Describe(method)} has ref/out/in parameters or a ref return; these are not supported.");
            return method;
        }

        /// <summary>The interface itself followed by every interface it inherits.</summary>
        private static IEnumerable<Type> InterfaceHierarchy(Type interfaceType) =>
            interfaceType.GetInterfaces().Prepend(interfaceType);

        /// <summary>Instance methods that an anonymous subclass may override or must implement.</summary>
        private static IEnumerable<MethodInfo> OverridableMethods(Type baseType) =>
            baseType.IsInterface
                ? InterfaceHierarchy(baseType).SelectMany(i => i.GetMethods(InstanceMembers))
                : baseType.GetMethods(InstanceMembers);

        private static ConstructorInfo FindConstructor(Type baseType, object[] arguments)
        {
            if (baseType.IsInterface)
                return typeof(object).GetConstructor(Type.EmptyTypes)!;

            List<ConstructorInfo> candidates = baseType.GetConstructors(InstanceMembers)
                .Where(c => !c.IsPrivate && MatchesArguments(c.GetParameters(), arguments))
                .ToList();

            if (candidates.Count == 1)
                return candidates[0];

            string signature = string.Join(", ", arguments.Select(a => a?.GetType().Name ?? "null"));
            if (candidates.Count == 0)
                throw new MissingMethodException($"{baseType} has no accessible constructor matching ({signature}).");
            throw new AmbiguousMatchException($"{baseType} has several constructors matching ({signature}); the call is ambiguous.");
        }

        private static bool MatchesArguments(ParameterInfo[] parameters, object[] arguments)
        {
            if (parameters.Length != arguments.Length)
                return false;
            for (int i = 0; i < parameters.Length; i++)
            {
                Type parameterType = parameters[i].ParameterType;
                object? argument = arguments[i];
                if (argument is null)
                {
                    if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                        return false;
                }
                else if (!parameterType.IsInstanceOfType(argument))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Emits <c>.ctor(Delegate[] bodies, &lt;base parameters&gt;)</c>. The delegates are stored into the instance field
        /// before the base constructor runs, so virtual calls made from the base constructor already reach the overrides.
        /// (C# forbids touching fields before the base call, but IL allows it.)
        /// </summary>
        private static void EmitConstructor(TypeBuilder typeBuilder, FieldInfo bodiesField, ConstructorInfo baseConstructor)
        {
            Type[] baseParameterTypes = baseConstructor.GetParameters().Select(p => p.ParameterType).ToArray();
            Type[] parameterTypes = [typeof(Delegate[]), .. baseParameterTypes];
            ConstructorBuilder ctor = typeBuilder.DefineConstructor(
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                CallingConventions.Standard,
                parameterTypes);

            ILGenerator il = ctor.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, bodiesField);

            il.Emit(OpCodes.Ldarg_0);
            for (int i = 0; i < baseParameterTypes.Length; i++)
                EmitLoadArgument(il, i + 2);
            il.Emit(OpCodes.Call, baseConstructor);
            il.Emit(OpCodes.Ret);
        }

        private static MethodBuilder DefineOverridingMethod(TypeBuilder typeBuilder, MethodInfo baseMethod)
        {
            string name;
            MethodAttributes attributes;
            if (baseMethod.DeclaringType!.IsInterface)
            {
                // Explicit interface implementation: keeps same-named methods of different interfaces apart.
                name = $"{baseMethod.DeclaringType.FullName}.{baseMethod.Name}";
                attributes = MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final
                    | MethodAttributes.HideBySig | MethodAttributes.NewSlot;
            }
            else
            {
                name = baseMethod.Name;
                attributes = baseMethod.Attributes;
                attributes &= ~(MethodAttributes.Abstract | MethodAttributes.VtableLayoutMask);
                attributes |= MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.ReuseSlot;
            }

            Type[] parameterTypes = baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();
            MethodBuilder method = typeBuilder.DefineMethod(name, attributes, baseMethod.ReturnType, parameterTypes);
            typeBuilder.DefineMethodOverride(method, baseMethod);
            return method;
        }

        private static void EmitOverride(TypeBuilder typeBuilder, FieldInfo bodiesField, int index, OverrideShape @override)
        {
            MethodInfo baseMethod = @override.Target;
            MethodBuilder method = DefineOverridingMethod(typeBuilder, baseMethod);
            ILGenerator il = method.GetILGenerator();

            Type delegateType = @override.DelegateType;
            MethodInfo invoke = delegateType.GetMethod("Invoke")!;

            // ((DelegateType)this.__bodies[index]).Invoke(this, new object[] { arg1, arg2, ... })
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, bodiesField);
            EmitLoadInt32(il, index);
            il.Emit(OpCodes.Ldelem_Ref);
            il.Emit(OpCodes.Castclass, delegateType);
            il.Emit(OpCodes.Ldarg_0);

            ParameterInfo[] parameters = baseMethod.GetParameters();
            EmitLoadInt32(il, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                EmitLoadInt32(il, i);
                EmitLoadArgument(il, i + 1);
                Type parameterType = parameters[i].ParameterType;
                if (parameterType.IsValueType)
                    il.Emit(OpCodes.Box, parameterType);
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.Emit(OpCodes.Callvirt, invoke);

            Type returnType = baseMethod.ReturnType;
            if (returnType != typeof(void))
            {
                Type delegateReturnType = @override.DelegateReturnType!;
                if (delegateReturnType != returnType)
                {
                    // The delegate returns object (or a base type); convert to the method's declared return type.
                    if (returnType.IsValueType)
                        il.Emit(OpCodes.Unbox_Any, returnType);
                    else
                        il.Emit(OpCodes.Castclass, returnType);
                }
            }

            il.Emit(OpCodes.Ret);
        }

        private static void EmitAbstractStub(TypeBuilder typeBuilder, MethodInfo baseMethod)
        {
            MethodBuilder method = DefineOverridingMethod(typeBuilder, baseMethod);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldstr, $"Abstract method {Describe(baseMethod)} was not overridden in {nameof(Anonym<TClass>)}.{nameof(Build)}.");
            il.Emit(OpCodes.Newobj, typeof(NotImplementedException).GetConstructor([typeof(string)])!);
            il.Emit(OpCodes.Throw);
        }

        private static void EmitLoadArgument(ILGenerator il, int index)
        {
            switch (index)
            {
                case 0: il.Emit(OpCodes.Ldarg_0); break;
                case 1: il.Emit(OpCodes.Ldarg_1); break;
                case 2: il.Emit(OpCodes.Ldarg_2); break;
                case 3: il.Emit(OpCodes.Ldarg_3); break;
                default:
                    if (index <= byte.MaxValue) il.Emit(OpCodes.Ldarg_S, (byte)index);
                    else il.Emit(OpCodes.Ldarg, (short)index);
                    break;
            }
        }

        private static void EmitLoadInt32(ILGenerator il, int value)
        {
            switch (value)
            {
                case -1: il.Emit(OpCodes.Ldc_I4_M1); break;
                case 0: il.Emit(OpCodes.Ldc_I4_0); break;
                case 1: il.Emit(OpCodes.Ldc_I4_1); break;
                case 2: il.Emit(OpCodes.Ldc_I4_2); break;
                case 3: il.Emit(OpCodes.Ldc_I4_3); break;
                case 4: il.Emit(OpCodes.Ldc_I4_4); break;
                case 5: il.Emit(OpCodes.Ldc_I4_5); break;
                case 6: il.Emit(OpCodes.Ldc_I4_6); break;
                case 7: il.Emit(OpCodes.Ldc_I4_7); break;
                case 8: il.Emit(OpCodes.Ldc_I4_8); break;
                default:
                    if (value >= sbyte.MinValue && value <= sbyte.MaxValue) il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                    else il.Emit(OpCodes.Ldc_I4, value);
                    break;
            }
        }

        private static string Describe(MethodInfo method) =>
            $"{method.DeclaringType}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
    }
}
