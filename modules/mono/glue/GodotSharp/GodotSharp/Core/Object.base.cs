using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Godot.Bridge;
using Godot.NativeInterop;

namespace Godot
{
    public partial class Object : IDisposable
    {
        // The point of this thread local static field is to allow the engine to create
        // managed instances using an existing native instance instead of creating a new one.
        // This way user derived classes don't need to define a constructor that takes the
        // native instance handle, which would be annoying.
        // Previously we were using a different trick for this. We were assigning the handle
        // field before calling the constructor. This is no longer viable now that we are
        // using SafeHandle (or at least the IDE warns about unreachable code).
        // Additionally, this new way will allow us to optimize the creation of new instances
        // as we will be able to use source generators instead of reflection. It also doesn't
        // come with the risk of  assuming undocumented compiler behavior (that fields without
        // a value assigned in code won't be assigned a default one by the constructor).
        [ThreadStatic] internal static IntPtr HandlePendingForNextInstance;

        private bool _disposed = false;
        private static readonly Type CachedType = typeof(Object);

        internal IntPtr NativePtr;
        private bool _memoryOwn;

        private WeakReference<Object> _weakReferenceToSelf;

        /// <summary>
        /// Constructs a new <see cref="Object"/>.
        /// </summary>
        public Object() : this(false)
        {
            unsafe
            {
                _ConstructAndInitialize(NativeCtor, NativeName, CachedType, refCounted: false);
            }
        }

        internal unsafe void _ConstructAndInitialize(
            delegate* unmanaged<IntPtr> nativeCtor,
            StringName nativeName,
            Type cachedType,
            bool refCounted
        )
        {
            var handlePending = HandlePendingForNextInstance;

            if (handlePending == IntPtr.Zero)
            {
                NativePtr = nativeCtor();

                InteropUtils.TieManagedToUnmanaged(this, NativePtr,
                    nativeName, refCounted, GetType(), cachedType);
            }
            else
            {
                NativePtr = handlePending;
                HandlePendingForNextInstance = IntPtr.Zero;
                InteropUtils.TieManagedToUnmanagedWithPreSetup(this, NativePtr);
            }

            _weakReferenceToSelf = DisposablesTracker.RegisterGodotObject(this);

            _InitializeGodotScriptInstanceInternals();
        }

        internal void _InitializeGodotScriptInstanceInternals()
        {
            // Performance is not critical here as this will be replaced with source generators.
            Type top = GetType();
            Type native = InternalGetClassNativeBase(top);

            while (top != null && top != native)
            {
                foreach (var eventSignal in top.GetEvents(
                                 BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                 BindingFlags.NonPublic | BindingFlags.Public)
                             .Where(ev => ev.GetCustomAttributes().OfType<SignalAttribute>().Any()))
                {
                    using var eventSignalName = new StringName(eventSignal.Name);
                    var eventSignalNameSelf = (godot_string_name)eventSignalName.NativeValue;
                    NativeFuncs.godotsharp_internal_object_connect_event_signal(NativePtr, eventSignalNameSelf);
                }

                top = top.BaseType;
            }
        }

        internal Object(bool memoryOwn)
        {
            _memoryOwn = memoryOwn;
        }

        /// <summary>
        /// The pointer to the native instance of this <see cref="Object"/>.
        /// </summary>
        public IntPtr NativeInstance => NativePtr;

        internal static IntPtr GetPtr(Object instance)
        {
            if (instance == null)
                return IntPtr.Zero;

            // We check if NativePtr is null because this may be called by the debugger.
            // If the debugger puts a breakpoint in one of the base constructors, before
            // NativePtr is assigned, that would result in UB or crashes when calling
            // native functions that receive the pointer, which can happen because the
            // debugger calls ToString() and tries to get the value of properties.
            if (instance._disposed || instance.NativePtr == IntPtr.Zero)
                throw new ObjectDisposedException(instance.GetType().FullName);

            return instance.NativePtr;
        }

        ~Object()
        {
            Dispose(false);
        }

        /// <summary>
        /// Disposes of this <see cref="Object"/>.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes implementation of this <see cref="Object"/>.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (NativePtr != IntPtr.Zero)
            {
                IntPtr gcHandleToFree = NativeFuncs.godotsharp_internal_object_get_associated_gchandle(NativePtr);

                if (gcHandleToFree != IntPtr.Zero)
                {
                    object target = GCHandle.FromIntPtr(gcHandleToFree).Target;
                    // The GC handle may have been replaced in another thread. Release it only if
                    // it's associated to this managed instance, or if the target is no longer alive.
                    if (target != this && target != null)
                        gcHandleToFree = IntPtr.Zero;
                }

                if (_memoryOwn)
                {
                    NativeFuncs.godotsharp_internal_refcounted_disposed(NativePtr, gcHandleToFree,
                        (!disposing).ToGodotBool());
                }
                else
                {
                    NativeFuncs.godotsharp_internal_object_disposed(NativePtr, gcHandleToFree);
                }

                NativePtr = IntPtr.Zero;
            }

            DisposablesTracker.UnregisterGodotObject(this, _weakReferenceToSelf);
        }

        /// <summary>
        /// Converts this <see cref="Object"/> to a string.
        /// </summary>
        /// <returns>A string representation of this object.</returns>
        public override string ToString()
        {
            NativeFuncs.godotsharp_object_to_string(GetPtr(this), out godot_string str);
            using (str)
                return Marshaling.ConvertStringToManaged(str);
        }

        /// <summary>
        /// Returns a new <see cref="SignalAwaiter"/> awaiter configured to complete when the instance
        /// <paramref name="source"/> emits the signal specified by the <paramref name="signal"/> parameter.
        /// </summary>
        /// <param name="source">
        /// The instance the awaiter will be listening to.
        /// </param>
        /// <param name="signal">
        /// The signal the awaiter will be waiting for.
        /// </param>
        /// <example>
        /// This sample prints a message once every frame up to 100 times.
        /// <code>
        /// public override void _Ready()
        /// {
        ///     for (int i = 0; i &lt; 100; i++)
        ///     {
        ///         await ToSignal(GetTree(), "process_frame");
        ///         GD.Print($"Frame {i}");
        ///     }
        /// }
        /// </code>
        /// </example>
        /// <returns>
        /// A <see cref="SignalAwaiter"/> that completes when
        /// <paramref name="source"/> emits the <paramref name="signal"/>.
        /// </returns>
        public SignalAwaiter ToSignal(Object source, StringName signal)
        {
            return new SignalAwaiter(source, signal, this);
        }

        internal static Type InternalGetClassNativeBase(Type t)
        {
            do
            {
                var assemblyName = t.Assembly.GetName();

                if (assemblyName.Name == "GodotSharp")
                    return t;

                if (assemblyName.Name == "GodotSharpEditor")
                    return t;
            } while ((t = t.BaseType) != null);

            return null;
        }

        internal static bool InternalIsClassNativeBase(Type t)
        {
            // Check whether the type is declared in the GodotSharp or GodotSharpEditor assemblies
            var typeAssembly = t.Assembly;

            if (typeAssembly == CachedType.Assembly)
                return true;

            var typeAssemblyName = t.Assembly.GetName();
            return typeAssemblyName.Name == "GodotSharpEditor";
        }

        // ReSharper disable once VirtualMemberNeverOverridden.Global
        protected internal virtual bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
        {
            return false;
        }

        // ReSharper disable once VirtualMemberNeverOverridden.Global
        protected internal virtual bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
        {
            value = default;
            return false;
        }

        internal void InternalRaiseEventSignal(in godot_string_name eventSignalName, NativeVariantPtrArgs args,
            int argc)
        {
            // Performance is not critical here as this will be replaced with source generators.

            using var stringName = StringName.CreateTakingOwnershipOfDisposableValue(
                NativeFuncs.godotsharp_string_name_new_copy(eventSignalName));
            string eventSignalNameStr = stringName.ToString();

            Type top = GetType();
            Type native = InternalGetClassNativeBase(top);

            while (top != null && top != native)
            {
                var foundEventSignals = top.GetEvents(
                        BindingFlags.DeclaredOnly | BindingFlags.Instance |
                        BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(ev => ev.GetCustomAttributes().OfType<SignalAttribute>().Any())
                    .Select(ev => ev.Name);

                var fields = top.GetFields(
                    BindingFlags.DeclaredOnly | BindingFlags.Instance |
                    BindingFlags.NonPublic | BindingFlags.Public);

                var eventSignalField = fields
                    .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType))
                    .Where(f => foundEventSignals.Contains(f.Name))
                    .FirstOrDefault(f => f.Name == eventSignalNameStr);

                if (eventSignalField != null)
                {
                    var @delegate = (Delegate)eventSignalField.GetValue(this);

                    if (@delegate == null)
                        continue;

                    var delegateType = eventSignalField.FieldType;

                    var invokeMethod = delegateType.GetMethod("Invoke");

                    if (invokeMethod == null)
                        throw new MissingMethodException(delegateType.FullName, "Invoke");

                    var parameterInfos = invokeMethod.GetParameters();
                    var paramsLength = parameterInfos.Length;

                    if (argc != paramsLength)
                    {
                        throw new InvalidOperationException(
                            $"The event delegate expects {paramsLength} arguments, but received {argc}.");
                    }

                    var managedArgs = new object[argc];

                    for (int i = 0; i < argc; i++)
                    {
                        managedArgs[i] = Marshaling.ConvertVariantToManagedObjectOfType(
                            args[i], parameterInfos[i].ParameterType);
                    }

                    invokeMethod.Invoke(@delegate, managedArgs);
                    return;
                }

                top = top.BaseType;
            }
        }

        internal static IntPtr ClassDB_get_method(StringName type, StringName method)
        {
            var typeSelf = (godot_string_name)type.NativeValue;
            var methodSelf = (godot_string_name)method.NativeValue;
            IntPtr methodBind = NativeFuncs.godotsharp_method_bind_get_method(typeSelf, methodSelf);

            if (methodBind == IntPtr.Zero)
                throw new NativeMethodBindNotFoundException(type + "." + method);

            return methodBind;
        }

        internal static unsafe delegate* unmanaged<IntPtr> ClassDB_get_constructor(StringName type)
        {
            // for some reason the '??' operator doesn't support 'delegate*'
            var typeSelf = (godot_string_name)type.NativeValue;
            var nativeConstructor = NativeFuncs.godotsharp_get_class_constructor(typeSelf);

            if (nativeConstructor == null)
                throw new NativeConstructorNotFoundException(type);

            return nativeConstructor;
        }

        protected internal virtual void SaveGodotObjectData(GodotSerializationInfo info)
        {
            // Temporary solution via reflection until we add a signals events source generator

            Type top = GetType();
            Type native = InternalGetClassNativeBase(top);

            while (top != null && top != native)
            {
                var foundEventSignals = top.GetEvents(
                        BindingFlags.DeclaredOnly | BindingFlags.Instance |
                        BindingFlags.NonPublic | BindingFlags.Public)
                    .Where(ev => ev.GetCustomAttributes().OfType<SignalAttribute>().Any())
                    .Select(ev => ev.Name);

                var fields = top.GetFields(
                    BindingFlags.DeclaredOnly | BindingFlags.Instance |
                    BindingFlags.NonPublic | BindingFlags.Public);

                foreach (var eventSignalField in fields
                             .Where(f => typeof(Delegate).IsAssignableFrom(f.FieldType))
                             .Where(f => foundEventSignals.Contains(f.Name)))
                {
                    var eventSignalDelegate = (Delegate)eventSignalField.GetValue(this);
                    info.AddSignalEventDelegate(eventSignalField.Name, eventSignalDelegate);
                }

                top = top.BaseType;
            }
        }

        // TODO: Should this be a constructor overload?
        protected internal virtual void RestoreGodotObjectData(GodotSerializationInfo info)
        {
            // Temporary solution via reflection until we add a signals events source generator

            void RestoreSignalEvent(StringName signalEventName)
            {
                Type top = GetType();
                Type native = InternalGetClassNativeBase(top);

                while (top != null && top != native)
                {
                    var foundEventSignal = top.GetEvent(signalEventName,
                        BindingFlags.DeclaredOnly | BindingFlags.Instance |
                        BindingFlags.NonPublic | BindingFlags.Public);

                    if (foundEventSignal != null &&
                        foundEventSignal.GetCustomAttributes().OfType<SignalAttribute>().Any())
                    {
                        var field = top.GetField(foundEventSignal.Name,
                            BindingFlags.DeclaredOnly | BindingFlags.Instance |
                            BindingFlags.NonPublic | BindingFlags.Public);

                        if (field != null && typeof(Delegate).IsAssignableFrom(field.FieldType))
                        {
                            var eventSignalDelegate = info.GetSignalEventDelegate(signalEventName);
                            field.SetValue(this, eventSignalDelegate);
                            return;
                        }
                    }

                    top = top.BaseType;
                }
            }

            foreach (var signalEventName in info.GetSignalEventsList())
            {
                RestoreSignalEvent(signalEventName);
            }
        }
    }
}
