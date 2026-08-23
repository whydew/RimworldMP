using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;

namespace Multiplayer.Common
{
    /// <summary>
    /// Per-<see cref="Type"/> memoization of the reflection metadata used by <see cref="SyncSerialization"/>.
    ///
    /// Everything cached here is derived purely from a Type, which is immutable, so caching it changes nothing about
    /// the bytes produced on the wire — it only removes the repeated reflection work (array-allocating
    /// GetGenericArguments, MakeGenericType/MakeArrayType type construction, GetConstructors+LINQ, per-write
    /// field lookups, and Activator.CreateInstance) that previously ran on every read/write call.
    ///
    /// Thread-safe: the backing store is a ConcurrentDictionary and every cached value is idempotent, so a rare
    /// duplicate computation under contention is harmless.
    /// </summary>
    internal sealed class CachedType
    {
        public readonly Type type;
        public readonly bool isValueType;

        private Type[]? genericArgs;
        private Type? genericDef;
        private Type? elementType;
        private Type? arrayType;
        private Type? listType;
        private Type? hashSetType;
        private Type? enumUnderlying;
        private Type? nullableUnderlying;
        private ConstructorInfo? tupleCtor;
        private FieldInfo[]? tupleFields;

        private Func<object>? factory;
        private bool factoryBuilt;

        public CachedType(Type type)
        {
            this.type = type;
            isValueType = type.IsValueType;
        }

        /// Cached result of type.GetGenericArguments(). Callers must treat the returned array as read-only.
        public Type[] GenericArgs => genericArgs ??= type.GetGenericArguments();

        public Type GenericDef => genericDef ??= type.GetGenericTypeDefinition();

        public Type ElementType => elementType ??= type.GetElementType()!;

        /// this[] — i.e. an array whose element type is this type.
        public Type ArrayType => arrayType ??= type.MakeArrayType();

        /// List&lt;this&gt;
        public Type ListType => listType ??= typeof(List<>).MakeGenericType(type);

        /// HashSet&lt;this&gt;
        public Type HashSetType => hashSetType ??= typeof(HashSet<>).MakeGenericType(type);

        public Type EnumUnderlying => enumUnderlying ??= Enum.GetUnderlyingType(type);

        public Type NullableUnderlying => nullableUnderlying ??= Nullable.GetUnderlyingType(type)!;

        /// Matches the previous type.GetConstructors().First(): the first declared constructor (tuples have exactly one).
        public ConstructorInfo TupleCtor => tupleCtor ??= type.GetConstructors()[0];

        /// Public instance fields Item1..ItemN of a ValueTuple, in order.
        public FieldInfo[] TupleFields => tupleFields ??= BuildTupleFields();

        private FieldInfo[] BuildTupleFields()
        {
            var args = GenericArgs;
            var fields = new FieldInfo[args.Length];
            for (int i = 0; i < args.Length; i++)
                fields[i] = type.GetField("Item" + (i + 1), BindingFlags.Instance | BindingFlags.Public)!;
            return fields;
        }

        /// <summary>
        /// Parameterless construction. Uses a compiled delegate when the type has an accessible parameterless
        /// constructor (or is a value type); otherwise falls back to Activator.CreateInstance, which throws the exact
        /// same exception the previous code did — preserving behavior for types that genuinely can't be constructed.
        /// </summary>
        public object CreateInstance()
        {
            if (!factoryBuilt)
            {
                factory = BuildFactory();
                factoryBuilt = true;
            }

            return factory != null ? factory() : Activator.CreateInstance(type)!;
        }

        private Func<object>? BuildFactory()
        {
            try
            {
                if (isValueType)
                    return Expression.Lambda<Func<object>>(
                        Expression.Convert(Expression.New(type), typeof(object))).Compile();

                var ctor = type.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (ctor == null)
                    return null; // No public parameterless ctor: fall back to Activator (same exception as before).

                return Expression.Lambda<Func<object>>(
                    Expression.Convert(Expression.New(ctor), typeof(object))).Compile();
            }
            catch
            {
                // If codegen fails for any reason, fall back to Activator so behavior is never worse than before.
                return null;
            }
        }
    }

    internal static class SyncTypeCache
    {
        private static readonly ConcurrentDictionary<Type, CachedType> cache = new();

        public static CachedType Of(Type type) => cache.GetOrAdd(type, static t => new CachedType(t));
    }
}